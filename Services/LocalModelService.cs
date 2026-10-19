using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using NativeTavern.Models;

namespace NativeTavern.Services;

public sealed partial class LocalModelService : ObservableObject, IDisposable
{
    public static string DefaultModelDirectory => Path.Combine(AppContext.BaseDirectory, "LocalModels");
    public const string LegacyModelDirectory = @"E:\BaiduNetdiskDownload\SillyTavern\models";
    public const string DefaultModelFileName = "Qwen3-8B-Q4_K_M.gguf";
    public static string DefaultLlamaCppPath => Path.Combine(AppContext.BaseDirectory, "llama.cpp", "llama-server.exe");
    private readonly Uri _modelsEndpoint;
    private readonly int _port;
    private readonly HttpClient _client = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    private Process? _ownedProcess;
    private readonly object _processLock = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private CancellationTokenSource? _loadingCancellation;
    private LocalModelDiagnostics? _diagnostics;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _runningModelName = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagnostics))]
    private string _lastDiagnostics = string.Empty;
    public bool HasDiagnostics => !string.IsNullOrWhiteSpace(LastDiagnostics);

    public LocalModelService() : this(8080) { }

    internal LocalModelService(int port)
    {
        _port = port;
        _modelsEndpoint = new Uri($"http://127.0.0.1:{port}/v1/models");
    }

    public IReadOnlyList<LocalModelFile> Scan(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => new LocalModelFile(file.Name, file.FullName, file.Length))
            .ToList();
    }

    public Task StartAsync(
        string llamaCppPath,
        LocalModelFile model,
        int contextLength,
        CancellationToken cancellationToken) =>
        StartAsync(llamaCppPath, model, contextLength, 35, cancellationToken);

    public static ProcessStartInfo CreateStartInfo(string llamaCppPath, LocalModelFile model, int contextLength, int gpuLayers) =>
        CreateStartInfo(llamaCppPath, model, contextLength, gpuLayers, 8080);

    internal static ProcessStartInfo CreateStartInfo(string llamaCppPath, LocalModelFile model, int contextLength, int gpuLayers, int port)
    {
        if (gpuLayers is < 0 or > 999)
            throw new ArgumentOutOfRangeException(nameof(gpuLayers), "GPU layers must be between 0 and 999.");
        if (contextLength is < 512 or > 262144)
            throw new ArgumentOutOfRangeException(nameof(contextLength), "Context length must be between 512 and 262144.");
        if (!File.Exists(llamaCppPath))
            throw new FileNotFoundException("llama.cpp server executable was not found.", llamaCppPath);
        if (!File.Exists(model.FilePath))
            throw new FileNotFoundException("GGUF model file was not found.", model.FilePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = llamaCppPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[]
                 {
                     "--model", model.FilePath, "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture),
                     "--ctx-size", contextLength.ToString(CultureInfo.InvariantCulture),
                     "--n-gpu-layers", gpuLayers.ToString(CultureInfo.InvariantCulture), "--jinja",
                     // Existing provider settings use this stable model identifier.
                     "--alias", "NativeTavern-Local"
                 })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    public async Task StartAsync(string llamaCppPath, LocalModelFile model, int contextLength,
        int gpuLayers, CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            ProcessStartInfo startInfo;
            try { startInfo = CreateStartInfo(llamaCppPath, model, contextLength, gpuLayers, _port); }
            catch (Exception ex)
            {
                lock (_processLock) _diagnostics = null;
                LastDiagnostics = ex.Message;
                throw;
            }
            Stop();
            var diagnostics = new LocalModelDiagnostics();
            lock (_processLock) _diagnostics = diagnostics;
            LastDiagnostics = string.Empty;
            // A healthy server on this port may belong to another application.
            var listener = new TcpListener(IPAddress.Loopback, _port);
            try { listener.Start(); }
            catch (SocketException ex)
            {
                var message = $"Port {_port} is already in use. Connect to the existing service or stop it before starting a model.";
                diagnostics.Append(message);
                LastDiagnostics = message;
                throw new InvalidOperationException(message, ex);
            }
            finally { listener.Stop(); }

            using var loadingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_processLock) _loadingCancellation = loadingCancellation;
            var token = loadingCancellation.Token;
            Process? process = null;
            Task output = Task.CompletedTask;
            Task error = Task.CompletedTask;
            try
            {
                token.ThrowIfCancellationRequested();
                lock (_processLock)
                {
                    token.ThrowIfCancellationRequested();
                    process = Process.Start(startInfo) ?? throw new InvalidOperationException("llama.cpp server could not be started.");
                    _ownedProcess = process;
                    RunningModelName = model.Name;
                    IsRunning = true;
                    IsLoading = true;
                    output = ReadDiagnosticsAsync(process.StandardOutput, diagnostics);
                    error = ReadDiagnosticsAsync(process.StandardError, diagnostics);
                }
                var deadline = DateTime.UtcNow.AddMinutes(2);
                while (DateTime.UtcNow < deadline)
                {
                    token.ThrowIfCancellationRequested();
                    if (process.HasExited)
                        throw new InvalidOperationException($"llama.cpp server exited with code {process.ExitCode}.");
                    try
                    {
                        using var response = await _client.GetAsync(_modelsEndpoint, token);
                        token.ThrowIfCancellationRequested();
                        if (response.IsSuccessStatusCode && !process.HasExited)
                        {
                            LastDiagnostics = diagnostics.Snapshot();
                            _ = ObserveExitAsync(process, diagnostics, output, error);
                            return;
                        }
                    }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
                    await Task.Delay(1000, token);
                }
                throw new TimeoutException("llama.cpp server did not become ready within two minutes.");
            }
            catch (Exception ex)
            {
                Stop();
                await Task.WhenAll(output, error);
                diagnostics.Append("\n" + ex.Message);
                LastDiagnostics = diagnostics.Snapshot();
                process?.Dispose();
                throw;
            }
            finally
            {
                lock (_processLock) _loadingCancellation = null;
                IsLoading = false;
            }
        }
        finally { _startGate.Release(); }
    }

    private static async Task ReadDiagnosticsAsync(StreamReader reader, LocalModelDiagnostics diagnostics)
    {
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                diagnostics.Append(new string(buffer, 0, count));
        }
        catch (IOException ex) { diagnostics.Append("\n" + ex.Message); }
    }

    private async Task ObserveExitAsync(Process process, LocalModelDiagnostics diagnostics, Task output, Task error)
    {
        try
        {
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            lock (_processLock)
            {
                if (ReferenceEquals(_diagnostics, diagnostics)) LastDiagnostics = diagnostics.Snapshot();
                if (ReferenceEquals(_ownedProcess, process))
                {
                    _ownedProcess = null;
                    IsRunning = false;
                    IsLoading = false;
                    RunningModelName = string.Empty;
                }
            }
        }
        finally { lock (_processLock) process.Dispose(); }
    }

    public void Stop()
    {
        lock (_processLock)
        {
            _loadingCancellation?.Cancel();
            var process = _ownedProcess;
            if (process is null) return;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000)) throw new TimeoutException("The local model service did not stop within five seconds.");
            }
            _ownedProcess = null;
            IsRunning = false;
            IsLoading = false;
            RunningModelName = string.Empty;
            if (_diagnostics is not null) LastDiagnostics = _diagnostics.Snapshot();
        }
    }

    public void Dispose()
    {
        try { Stop(); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
        _client.Dispose();
    }
}
