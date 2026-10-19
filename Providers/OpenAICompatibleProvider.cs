using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NativeTavern.Helpers;
using NativeTavern.Models;
using NativeTavern.Services;

namespace NativeTavern.Providers;

public sealed class OpenAICompatibleProvider(
    HttpClient httpClient,
    SettingsService settingsService,
    ILogger<OpenAICompatibleProvider> logger) : ILLMProvider
{
    public string Id => "openai-compatible";
    public string DisplayName => "OpenAI Compatible";

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken)
    {
        var resolved = await settingsService.LoadResolvedAsync();
        return await GetModelsAsync(resolved.Settings, resolved.ApiKey, cancellationToken);
    }

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(
        ProviderSettings settings, string apiKey, CancellationToken cancellationToken)
    {
        if (!ProviderSettings.IsValidBaseUrl(settings.BaseUrl))
            throw new ProviderException("Base URL 格式无效。");
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.BaseUrl.TrimEnd('/') + "/models");
        AddHeaders(request, settings, apiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new ProviderException(MapStatus(response.StatusCode));
            return ParseModels(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ProviderException("获取模型列表超时。"); }
        catch (HttpRequestException ex)
        { throw new ProviderException("无法连接模型服务，请检查 Base URL 和网络连接。", ex); }
        catch (JsonException ex)
        { throw new ProviderException("模型服务返回了无法识别的模型列表。", ex); }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resolved = await settingsService.LoadResolvedAsync();
        await foreach (var chunk in StreamAsync(request, resolved.Settings, resolved.ApiKey, cancellationToken))
            yield return chunk;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatCompletionRequest request,
        ProviderSettings settings,
        string apiKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var message = CreateRequest(settings, apiKey, request);
        logger.LogInformation("Provider streaming request started for model {Model}", request.Model);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ProviderException("无法连接模型服务，请检查 Base URL 和网络连接。", ex);
        }

        using (response)
        {
            logger.LogInformation("Provider returned HTTP {StatusCode}", (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(MapStatus(response.StatusCode));

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) yield break;
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") yield break;
                string? content;
                try
                {
                    content = ParseStreamingContent(data);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "Ignored malformed SSE payload.");
                    continue;
                }
                if (!string.IsNullOrEmpty(content)) yield return content;
            }
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var resolved = await settingsService.LoadResolvedAsync();
        return await TestConnectionAsync(resolved.Settings, resolved.ApiKey, cancellationToken);
    }

    public async Task<bool> TestConnectionAsync(
        ProviderSettings settings, string apiKey, CancellationToken cancellationToken)
    {
        var payload = new ChatCompletionRequest
        {
            Model = settings.Model,
            Messages = [new ChatCompletionMessage { Role = "user", Content = "Hi" }],
            Temperature = settings.Temperature,
            TopP = settings.TopP,
            MaxTokens = 1,
            Stream = false
        };
        using var request = CreateRequest(settings, apiKey, payload);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await httpClient.SendAsync(request, timeout.Token);
            logger.LogInformation("Connection test returned HTTP {StatusCode}", (int)response.StatusCode);
            if (!response.IsSuccessStatusCode) throw new ProviderException(MapStatus(response.StatusCode));
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException("连接测试超时，请检查服务地址和网络。");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("无法连接模型服务，请检查 Base URL 和网络连接。", ex);
        }
    }

    private static HttpRequestMessage CreateRequest(
        ProviderSettings settings, string apiKey, ChatCompletionRequest payload)
    {
        if (!ProviderSettings.IsValidBaseUrl(settings.BaseUrl))
            throw new ProviderException("Base URL 格式无效。");
        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new ProviderException("请填写模型名称。");
        var url = settings.BaseUrl.TrimEnd('/') + "/chat/completions";
        var body = JsonSerializer.SerializeToNode(payload, JsonDefaults.Options)!;
        var thinkingEnabled = payload.DisableLocalThinking ? false : settings.LocalModelThinkingEnabled;
        if (settings.ProviderId == "llamacpp" && thinkingEnabled is bool enabled)
            body["chat_template_kwargs"] = JsonSerializer.SerializeToNode(new { enable_thinking = enabled });
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                body.ToJsonString(JsonDefaults.Options), Encoding.UTF8, "application/json")
        };
        AddHeaders(message, settings, apiKey);
        return message;
    }

    private static void AddHeaders(HttpRequestMessage message, ProviderSettings settings, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (settings.ProviderId == "gemini")
            message.Headers.TryAddWithoutValidation("x-goog-api-client", $"lorehaven/{AppVersion.Current.ToString(3)}");
        if (settings.ProviderId == "openrouter")
            message.Headers.TryAddWithoutValidation("X-OpenRouter-Title", "Lorehaven");
    }

    public static IReadOnlyList<ModelInfo> ParseModels(string jsonText)
    {
        using var json = JsonDocument.Parse(jsonText);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];
        return data.EnumerateArray().Select(item =>
        {
            var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? "" : "";
            var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            return new ModelInfo(id, name);
        }).Where(x => !string.IsNullOrWhiteSpace(x.Id)).OrderBy(x => x.Id).ToList();
    }

    public static string? ParseStreamingContent(string data)
    {
        using var json = JsonDocument.Parse(data);
        if (!json.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("delta", out var delta) ||
            !delta.TryGetProperty("content", out var content)) return null;
        return content.ValueKind == JsonValueKind.String ? content.GetString() : null;
    }

    private static string MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "API Key 无效或没有访问权限。",
        HttpStatusCode.NotFound => "接口或模型不存在，请检查 Base URL 和模型名称。",
        HttpStatusCode.TooManyRequests => "请求过于频繁，请稍后重试。",
        _ when (int)status >= 500 => "模型服务暂时不可用，请稍后重试。",
        _ => $"连接失败：HTTP {(int)status} {status}"
    };
}
