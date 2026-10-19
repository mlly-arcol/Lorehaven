using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Data.Repositories;
using NativeTavern.Helpers;
using NativeTavern.Models;
using NativeTavern.Providers;
using NativeTavern.Security;
using NativeTavern.Services;
using NativeTavern.ViewModels;
using Xunit;

namespace NativeTavern.Tests;

public sealed class LocalThinkingModeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsPageRestoresTranslatesAndSavesTheChoice(bool? enabled)
    {
        using var harness = new ChatViewModelHarness();
        var settings = new SettingsService(new SettingsRepository(harness.Factory), new PlainProtector());
        var stored = Settings("llamacpp", enabled);
        stored.AutoScanLocalModels = false;
        stored.LocalGpuLayers = 12;
        stored.LocalModelDirectory = harness.RootDirectory;
        stored.LlamaCppPath = Path.Combine(harness.RootDirectory, "custom llama-server.exe");
        await settings.SaveAsync(stored, null);
        var localization = new LocalizationService();
        using var localModels = new LocalModelService();
        var viewModel = new SettingsViewModel(settings, localization, null!, null!, localModels, null!,
            NullLogger<SettingsViewModel>.Instance);

        await viewModel.InitializeAsync();
        Assert.True(viewModel.IsLlamaCppProvider);
        Assert.Equal(12, viewModel.LocalGpuLayers);
        Assert.Equal(stored.LocalModelDirectory, viewModel.LocalModelDirectory);
        Assert.Equal(stored.LlamaCppPath, viewModel.LlamaCppPath);
        Assert.Equal(enabled, viewModel.SelectedThinkingMode!.Enabled);
        localization.SetLanguage(LocalizationService.English);
        Assert.Equal(enabled, viewModel.SelectedThinkingMode!.Enabled);
        Assert.Equal(["Model default", "Quick reply", "Deep thinking"], viewModel.ThinkingModes.Select(x => x.DisplayName));

        viewModel.SelectedThinkingMode = viewModel.ThinkingModes.Single(x => x.Enabled == false);
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(12, (await settings.LoadAsync()).LocalGpuLayers);
        Assert.Equal(stored.LocalModelDirectory, (await settings.LoadAsync()).LocalModelDirectory);
        Assert.Equal(stored.LlamaCppPath, (await settings.LoadAsync()).LlamaCppPath);
        Assert.False((await settings.LoadAsync()).LocalModelThinkingEnabled);

        viewModel.SelectedProvider = viewModel.ProviderProfiles.Single(x => x.Id == "openai");
        Assert.False(viewModel.IsLlamaCppProvider);
        viewModel.SelectedProvider = viewModel.ProviderProfiles.Single(x => x.Id == "llamacpp");
        Assert.Equal(false, viewModel.SelectedThinkingMode!.Enabled);
    }

    [Theory]
    [InlineData("llamacpp", null)]
    [InlineData("llamacpp", false)]
    [InlineData("llamacpp", true)]
    [InlineData("openai", false)]
    [InlineData("openai-compatible", true)]
    [InlineData("ollama", false)]
    [InlineData("lmstudio", true)]
    public async Task StreamingOnlyOverridesThinkingForExplicitLlamaCppChoices(string providerId, bool? enabled)
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAICompatibleProvider(client, null!, NullLogger<OpenAICompatibleProvider>.Instance);
        var settings = Settings(providerId, enabled);
        var request = new ChatCompletionRequest
        {
            Model = "preset-model",
            Messages = [new() { Role = "user", Content = "你好", ImageDataUrls = ["data:image/png;base64,AA=="] }],
            MaxTokens = 128
        };

        var chunks = new List<string>();
        await foreach (var chunk in provider.StreamAsync(request, settings, "", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(["你好"], chunks);
        using var json = JsonDocument.Parse(handler.Body!);
        var body = json.RootElement;
        Assert.Equal("preset-model", body.GetProperty("model").GetString());
        Assert.Equal(128, body.GetProperty("max_tokens").GetInt32());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal(2, body.GetProperty("messages")[0].GetProperty("content").GetArrayLength());
        if (providerId == "llamacpp" && enabled.HasValue)
            Assert.Equal(enabled.Value, body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        else
            Assert.False(body.TryGetProperty("chat_template_kwargs", out _));
        Assert.DoesNotContain("chat_template_kwargs", JsonSerializer.Serialize(request, JsonDefaults.Options));
    }

    [Fact]
    public async Task UtilityRequestsCanSkipLlamaThinkingWithoutSendingPrivateOptionsToCloudProviders()
    {
        foreach (var providerId in new[] { "llamacpp", "openai" })
        {
            using var handler = new RecordingHandler();
            using var client = new HttpClient(handler);
            var provider = new OpenAICompatibleProvider(client, null!, NullLogger<OpenAICompatibleProvider>.Instance);
            var request = new ChatCompletionRequest { Model = "model", DisableLocalThinking = true };
            await foreach (var _ in provider.StreamAsync(request, Settings(providerId, true), "", CancellationToken.None)) { }
            using var json = JsonDocument.Parse(handler.Body!);
            if (providerId == "llamacpp")
                Assert.False(json.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            else
                Assert.False(json.RootElement.TryGetProperty("chat_template_kwargs", out _));
            Assert.DoesNotContain("disableLocalThinking", handler.Body!);
        }
    }

    [Fact]
    public async Task ConnectionTestUsesTheUnsavedThinkingChoice()
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAICompatibleProvider(client, null!, NullLogger<OpenAICompatibleProvider>.Instance);

        Assert.True(await provider.TestConnectionAsync(Settings("llamacpp", false), "", CancellationToken.None));

        using var json = JsonDocument.Parse(handler.Body!);
        Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(json.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsPreserveAllThreeModes(bool? enabled)
    {
        var json = JsonSerializer.Serialize(Settings("llamacpp", enabled), JsonDefaults.Options);
        Assert.Equal(enabled, JsonSerializer.Deserialize<ProviderSettings>(json, JsonDefaults.Options)!.LocalModelThinkingEnabled);
        Assert.Null(JsonSerializer.Deserialize<ProviderSettings>("{\"providerId\":\"llamacpp\"}", JsonDefaults.Options)!.LocalModelThinkingEnabled);
    }

    private static ProviderSettings Settings(string providerId, bool? enabled) => new()
    {
        ProviderId = providerId,
        BaseUrl = "http://127.0.0.1:8080/v1",
        Model = "model",
        LocalModelThinkingEnabled = enabled
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"隐藏的思考\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedText) => protectedText;
    }
}
