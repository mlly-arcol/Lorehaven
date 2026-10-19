using System.Runtime.CompilerServices;
using NativeTavern.Models;
using NativeTavern.Services;

namespace NativeTavern.Providers;

public sealed class ProviderRouter(
    SettingsService settingsService,
    OpenAICompatibleProvider compatibleProvider,
    ClaudeProvider claudeProvider) : ILLMProvider
{
    public string Id => "provider-router";
    public string DisplayName => "Lorehaven Provider Router";

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken)
    {
        var resolved = await settingsService.LoadResolvedAsync();
        return await GetModelsAsync(resolved.Settings, resolved.ApiKey, cancellationToken);
    }

    public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(
        ProviderSettings settings, string apiKey, CancellationToken cancellationToken) =>
        settings.ProviderId == "claude"
            ? claudeProvider.GetModelsAsync(settings, apiKey, cancellationToken)
            : compatibleProvider.GetModelsAsync(settings, apiKey, cancellationToken);

    public async IAsyncEnumerable<string> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = await settingsService.LoadAsync();
        var provider = settings.ProviderId == "claude" ? (ILLMProvider)claudeProvider : compatibleProvider;
        await foreach (var chunk in provider.StreamAsync(request, cancellationToken)) yield return chunk;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var resolved = await settingsService.LoadResolvedAsync();
        return await TestConnectionAsync(resolved.Settings, resolved.ApiKey, cancellationToken);
    }

    public Task<bool> TestConnectionAsync(
        ProviderSettings settings, string apiKey, CancellationToken cancellationToken) =>
        settings.ProviderId == "claude"
            ? TestClaudeAsync(settings, apiKey, cancellationToken)
            : compatibleProvider.TestConnectionAsync(settings, apiKey, cancellationToken);

    private async Task<bool> TestClaudeAsync(
        ProviderSettings settings, string apiKey, CancellationToken cancellationToken)
    {
        _ = await claudeProvider.GetModelsAsync(settings, apiKey, cancellationToken);
        return true;
    }
}
