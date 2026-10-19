using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NativeTavern.Models;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class LocalModelLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NativeTavern-model-" + Guid.NewGuid().ToString("N"));
    private static string Server => Path.Combine(AppContext.BaseDirectory, "ModelStub", "NativeTavern.TestModelServer.exe");

    public LocalModelLifecycleTests() => Directory.CreateDirectory(_directory);

    private LocalModelFile Model(string mode)
    {
        var path = Path.Combine(_directory, "model with spaces.gguf");
        File.WriteAllText(path, mode);
        return new LocalModelFile(Path.GetFileName(path), path, new FileInfo(path).Length);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(999)]
    public void LaunchPreservesPathsAndRequestedGpuLayers(int layers)
    {
        var model = Model("ready");
        var info = LocalModelService.CreateStartInfo(Server, model, 4096, layers);
        var args = info.ArgumentList.ToList();
        Assert.Equal(model.FilePath, args[args.IndexOf("--model") + 1]);
        Assert.Equal(layers.ToString(), args[args.IndexOf("--n-gpu-layers") + 1]);
        Assert.Equal("4096", args[args.IndexOf("--ctx-size") + 1]);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
    }

    [Theory]
    [InlineData(-1, 8192)]
    [InlineData(1000, 8192)]
    [InlineData(35, 511)]
    [InlineData(35, 262145)]
    public void InvalidParametersFailBeforeStartingAProcess(int layers, int context)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalModelService.CreateStartInfo(Server, Model("ready"), context, layers));
    }

    [Fact]
    public async Task StartAndStopTrackTheOwnedServiceAndReleaseItsPort()
    {
        var port = FreePort();
        using var service = new LocalModelService(port);
        var model = Model("ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await service.StartAsync(Server, model, 8192, 0, timeout.Token);
        using var process = Process.GetProcessById(int.Parse(File.ReadAllText(model.FilePath + ".pid")));
        Assert.True(service.IsRunning);
        Assert.False(service.IsLoading);
        Assert.Equal(model.Name, service.RunningModelName);

        service.Stop();
        await process.WaitForExitAsync(timeout.Token);
        Assert.False(service.IsRunning);
        Assert.Equal(string.Empty, service.RunningModelName);
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); }
        finally { listener.Stop(); }
        service.Stop();
    }

    [Fact]
    public async Task StartingAnotherModelStopsThePreviousProcessBeforeReusingThePort()
    {
        using var service = new LocalModelService(FreePort());
        var model = Model("ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await service.StartAsync(Server, model, 8192, 0, timeout.Token);
        using var first = Process.GetProcessById(int.Parse(File.ReadAllText(model.FilePath + ".pid")));
        await service.StartAsync(Server, model, 8192, 12, timeout.Token);
        Assert.True(first.HasExited);
        Assert.True(service.IsRunning);
        Assert.NotEqual(first.Id, int.Parse(File.ReadAllText(model.FilePath + ".pid")));
    }

    [Fact]
    public async Task StopDuringLoadingCancelsStartupAndAllowsAnotherStart()
    {
        using var service = new LocalModelService(FreePort());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var loading = service.StartAsync(Server, Model("loading"), 8192, 0, timeout.Token);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => service.IsLoading));
        service.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.False(service.IsRunning);
        Assert.False(service.IsLoading);
        await service.StartAsync(Server, Model("ready"), 8192, 0, timeout.Token);
        Assert.True(service.IsRunning);
    }

    [Fact]
    public async Task AnUnexpectedExitClearsTheRunningState()
    {
        using var service = new LocalModelService(FreePort());
        var model = Model("ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await service.StartAsync(Server, model, 8192, 0, timeout.Token);
        using var process = Process.GetProcessById(int.Parse(File.ReadAllText(model.FilePath + ".pid")));
        process.Kill();
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => !service.IsRunning));
        Assert.Equal(string.Empty, service.RunningModelName);
    }

    [Fact]
    public async Task AStartupCrashDoesNotLeaveStaleState()
    {
        using var service = new LocalModelService(FreePort());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(Server, Model("crash"), 8192, 0, timeout.Token));
        Assert.Contains("code 7", error.Message);
        Assert.Contains("GGUF_MAGIC_INVALID", service.LastDiagnostics);
        Assert.True(service.HasDiagnostics);
        Assert.False(service.IsRunning);
        Assert.False(service.IsLoading);
        service.Stop();
    }

    [Fact]
    public async Task AnOccupiedPortIsNotMistakenForASuccessfulStart()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var service = new LocalModelService(((IPEndPoint)listener.LocalEndpoint).Port);
        var model = Model("ready");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(Server, model, 8192, 0, CancellationToken.None));
            Assert.Contains("already in use", error.Message);
            Assert.Contains("already in use", service.LastDiagnostics);
            Assert.False(service.IsRunning);
            Assert.False(File.Exists(model.FilePath + ".pid"));
            service.Stop();
            Assert.True(listener.Server.IsBound);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task LargeServiceLogsAreDrainedAndBoundedAndReplacedOnRestart()
    {
        using var service = new LocalModelService(FreePort());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await service.StartAsync(Server, Model("verbose"), 8192, 0, timeout.Token);
        Assert.InRange(service.LastDiagnostics.Length, 1, LocalModelDiagnostics.Capacity);
        Assert.Contains("MODEL_LOG_END", service.LastDiagnostics);
        await service.StartAsync(Server, Model("ready"), 8192, 0, timeout.Token);
        Assert.DoesNotContain("MODEL_LOG_END", service.LastDiagnostics);
    }

    [Fact]
    public async Task MissingExecutableReplacesOldDiagnosticsWithoutStartingAnything()
    {
        using var service = new LocalModelService(FreePort());
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.StartAsync(Path.Combine(_directory, "missing.exe"), Model("ready"), 8192, 0, CancellationToken.None));
        Assert.True(service.HasDiagnostics);
        Assert.Contains("executable", service.LastDiagnostics);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task AnInvalidConcurrentStartDoesNotReplaceTheActiveStartDiagnostics()
    {
        using var service = new LocalModelService(FreePort());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var model = Model("loading");
        var loading = service.StartAsync(Server, model, 8192, 0, timeout.Token);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => service.IsLoading));
        var invalid = service.StartAsync(Path.Combine(_directory, "missing.exe"), model, 8192, 0, timeout.Token);
        try
        {
            Assert.False(invalid.IsCompleted);
            Assert.Empty(service.LastDiagnostics);
            Assert.True(service.IsLoading);
        }
        finally
        {
            service.Stop();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
            await Assert.ThrowsAsync<FileNotFoundException>(() => invalid);
        }
        Assert.Contains("executable", service.LastDiagnostics);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task AnInvalidStartDoesNotStopAnAlreadyRunningModel()
    {
        using var service = new LocalModelService(FreePort());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var model = Model("ready");
        await service.StartAsync(Server, model, 8192, 0, timeout.Token);
        using var process = Process.GetProcessById(int.Parse(File.ReadAllText(model.FilePath + ".pid")));

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.StartAsync(
            Path.Combine(_directory, "missing.exe"), model, 8192, 0, timeout.Token));

        Assert.False(process.HasExited);
        Assert.True(service.IsRunning);
        Assert.Equal(model.Name, service.RunningModelName);
        Assert.Contains("executable", service.LastDiagnostics);
    }

    [Fact]
    public async Task AFailedProcessLaunchStillAllowsStop()
    {
        using var service = new LocalModelService(FreePort());
        var model = Model("ready");
        await Assert.ThrowsAnyAsync<Exception>(() => service.StartAsync(model.FilePath, model, 8192, 0, CancellationToken.None));
        service.Stop();
        Assert.False(service.IsRunning);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
