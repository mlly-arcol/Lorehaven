using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Providers;
using NativeTavern.Security;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class ConversationTitleServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NativeTavernTitles-" + Guid.NewGuid().ToString("N"));
    private readonly DatabaseConnectionFactory _factory;
    private readonly ChatMessageRepository _messages;
    private readonly SettingsService _settings;
    private readonly TitleProvider _provider = new();
    private readonly ConversationTitleService _service;
    private ChatSession _session = null!;

    public ConversationTitleServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new DatabaseConnectionFactory(Path.Combine(_directory, "data.db"));
        _messages = new ChatMessageRepository(_factory);
        _settings = new SettingsService(new SettingsRepository(_factory), new TestProtector());
        _service = new ConversationTitleService(_messages, _settings, _provider);
    }

    public async Task InitializeAsync()
    {
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        await SaveSettingsAsync();
        _session = new ChatSession
        {
            Title = "Original title", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        await new ChatSessionRepository(_factory).CreateAsync(_session);
    }

    [Theory]
    [InlineData("zh-CN", "雾中山门", "Simplified Chinese")]
    [InlineData("en-US", "Trial at the Misty Gate", "English")]
    public async Task GeneratesAStructuredTitleInTheStoredLanguageWithoutSaving(
        string language, string title, string instructionLanguage)
    {
        await SaveSettingsAsync(language: language);
        await AddAsync(ChatRole.User, "We approached a mountain gate in the mist.");
        _provider.Chunks = ["{\"title\":\"", title, "\"}"];
        var originalUpdatedAt = _session.UpdatedAt;

        var result = await _service.GenerateAsync(_session);

        Assert.Equal(title, result);
        var request = Assert.Single(_provider.Requests);
        Assert.Equal("title-model", request.Model);
        Assert.Contains(instructionLanguage, request.Messages[0].Content);
        Assert.True(request.DisableLocalThinking);
        Assert.True(request.Stream);
        Assert.Equal("Original title", _session.Title);
        Assert.Equal(originalUpdatedAt, _session.UpdatedAt);
        var stored = await new ChatSessionRepository(_factory).GetAsync(_session.Id);
        Assert.Equal("Original title", stored!.Title);
        Assert.Equal(originalUpdatedAt, stored.UpdatedAt);
    }

    [Theory]
    [InlineData("```json\n{\"title\":\"雾中试炼\"}\n```")]
    [InlineData("  ```JSON\r\n{\"title\":\"雾中试炼\"}\r\n```  ")]
    [InlineData("```\n{\"title\":\"雾中试炼\"}\n```")]
    public async Task AcceptsAnOtherwiseCleanJsonCodeFence(string response)
    {
        await AddAsync(ChatRole.Assistant, "山门在雾中若隐若现。");
        _provider.Chunks = [response];

        Assert.Equal("雾中试炼", await _service.GenerateAsync(_session));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A plain title or reasoning")]
    [InlineData("<think>I should write a title.</think>{\"title\":\"Trial\"}")]
    [InlineData("{\"title\":\"<think>Trial</think>\"}")]
    [InlineData("{\"title\":\"第一行\\n第二行\"}")]
    [InlineData("{\"title\":\"\\nTrial\\n\"}")]
    [InlineData("{\"title\":\"First\\tSecond\"}")]
    [InlineData("{\"title\":\"First\\u2028Second\"}")]
    [InlineData("{\"title\":\"   \"}")]
    [InlineData("{\"title\":\"...\"}")]
    [InlineData("{\"title\":\"`Trial`\"}")]
    [InlineData("{\"title\":null}")]
    [InlineData("{\"title\":123}")]
    [InlineData("{\"title\":[\"Trial\"]}")]
    [InlineData("{\"title\":\"Trial\",\"title\":\"Other\"}")]
    [InlineData("{\"title\":\"Trial\",\"reasoning\":\"An explanation\"}")]
    [InlineData("{\"summary\":\"Trial\"}")]
    [InlineData("[\"Trial\"]")]
    [InlineData("{\"title\":\"Unclosed")]
    [InlineData("```text\n{\"title\":\"Trial\"}\n```")]
    public async Task RejectsEmptyInvalidAndReasoningOutput(string response)
    {
        await AddAsync(ChatRole.User, "Title this conversation.");
        _provider.Chunks = [response];

        Assert.Null(await _service.GenerateAsync(_session));
        Assert.Equal("Original title", _session.Title);
    }

    [Fact]
    public async Task RejectsAnOversizedTitleRatherThanSilentlyTruncatingIt()
    {
        await AddAsync(ChatRole.User, "A story.");
        _provider.Chunks = ["{\"title\":\"" + new string('字', ConversationTitleService.MaximumTitleCharacters + 1) + "\"}"];

        Assert.Null(await _service.GenerateAsync(_session));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DoesNotCallTheModelWithoutAConfiguredModel(string model)
    {
        await SaveSettingsAsync(model: model);
        await AddAsync(ChatRole.User, "A conversation.");

        Assert.Null(await _service.GenerateAsync(_session));
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task DoesNotCallTheModelWithAnInvalidProviderUrl()
    {
        await SaveSettingsAsync(baseUrl: "file:///not-a-provider");
        await AddAsync(ChatRole.User, "A conversation.");

        Assert.Null(await _service.GenerateAsync(_session));
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task EmptyWhitespaceAndSystemOnlyHistoryDoesNotCallTheModel()
    {
        Assert.Null(await _service.GenerateAsync(_session));
        await AddAsync(ChatRole.System, "Secret instructions are not conversation text.");
        await AddAsync(ChatRole.User, " \r\n ");
        await AddAsync(ChatRole.Assistant, "\t");

        Assert.Null(await _service.GenerateAsync(_session));
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task UsesOnlyRecentNonemptyConversationTextFromTheSelectedSession()
    {
        _session.AuthorNote = "AUTHOR_NOTE_SECRET";
        _session.Summary = "SUMMARY_SECRET";
        _session.Title = "EXISTING_TITLE_SECRET";
        await AddAsync(ChatRole.System, "SYSTEM_PROMPT_SECRET");
        for (var index = 0; index < 14; index++)
            await AddAsync(index % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"turn-{index:D2}-text");
        await AddAsync(ChatRole.Assistant, " \r\n ");
        var other = new ChatSession { Title = "Other", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        await new ChatSessionRepository(_factory).CreateAsync(other);
        await _messages.AddAsync(new ChatMessage
        {
            ChatSessionId = other.Id, Role = ChatRole.User, Content = "OTHER_SESSION_SECRET", CreatedAt = DateTimeOffset.UtcNow,
        });
        _provider.Chunks = ["{\"title\":\"A title\"}"];

        await _service.GenerateAsync(_session);

        var request = Assert.Single(_provider.Requests);
        Assert.Equal(new[] { "system", "user" }, request.Messages.Select(message => message.Role));
        Assert.All(request.Messages, message => Assert.Empty(message.ImageDataUrls));
        var transcript = request.Messages[1].Content;
        Assert.DoesNotContain("SECRET", string.Join('\n', request.Messages.Select(message => message.Content)));
        Assert.DoesNotContain("turn-00", transcript);
        Assert.DoesNotContain("turn-01", transcript);
        Assert.Contains("User: turn-02-text", transcript);
        Assert.Contains("Assistant: turn-13-text", transcript);
        Assert.Equal(ConversationTitleService.MaximumHistoryMessages, transcript.Split('\n').Length);
    }

    [Fact]
    public async Task BoundsTranscriptAndIndividualTurnsWhileKeepingTheNewestText()
    {
        for (var index = 0; index < 20; index++)
            await AddAsync(ChatRole.User, $"turn-{index:D2}:" + new string('字', 3000));
        _provider.Chunks = ["{\"title\":\"山门试炼\"}"];

        await _service.GenerateAsync(_session);

        var request = Assert.Single(_provider.Requests);
        var transcript = request.Messages[1].Content;
        Assert.InRange(transcript.Length, 1, ConversationTitleService.MaximumTranscriptCharacters);
        Assert.All(transcript.Split('\n'), line =>
            Assert.InRange(line.Length, 1, ConversationTitleService.MaximumMessageCharacters + "Assistant: ".Length));
        Assert.Contains("turn-19:", transcript);
        Assert.DoesNotContain("turn-00:", transcript);
        Assert.InRange(request.MaxTokens, 1, 200);
    }

    [Fact]
    public async Task ShrinksTranscriptToLeaveRoomForTheInstructionAndReplyInASmallContext()
    {
        await SaveSettingsAsync(contextLength: 512);
        await AddAsync(ChatRole.User, new string('旧', 3000));
        await AddAsync(ChatRole.Assistant, "最新消息：" + new string('新', 3000));
        _provider.Chunks = ["{\"title\":\"新消息\"}"];

        Assert.Equal("新消息", await _service.GenerateAsync(_session));

        var request = Assert.Single(_provider.Requests);
        Assert.Contains("最新消息", request.Messages[1].Content);
        Assert.True(TokenEstimator.Estimate(request.Messages) + request.MaxTokens <= 512);
        Assert.True(request.Messages[1].Content.Length < 512);
    }

    [Fact]
    public async Task DoesNotCallTheModelWhenTheContextCannotFitTheInstructionAndReply()
    {
        await SaveSettingsAsync(contextLength: 64);
        await AddAsync(ChatRole.User, "A story.");

        Assert.Null(await _service.GenerateAsync(_session));
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task OversizedStreamingOutputStopsBeforeReadingFurtherChunks()
    {
        await AddAsync(ChatRole.User, "A story.");
        _provider.Chunks = [new string('x', ConversationTitleService.MaximumResponseCharacters + 1), "never read"];

        Assert.Null(await _service.GenerateAsync(_session));

        Assert.Equal(1, _provider.YieldedChunks);
        Assert.True(_provider.Disposed);
        Assert.True(_provider.StreamToken.IsCancellationRequested);
    }

    [Fact]
    public async Task AccumulatedStreamingOutputIsBoundedToo()
    {
        await AddAsync(ChatRole.User, "A story.");
        _provider.Chunks = [new string('x', 1200), new string('x', 1200), "never read"];

        Assert.Null(await _service.GenerateAsync(_session));
        Assert.Equal(2, _provider.YieldedChunks);
        Assert.True(_provider.Disposed);
    }

    [Fact]
    public async Task EvenAStreamOfEmptyChunksCannotRunForever()
    {
        await AddAsync(ChatRole.User, "A story.");
        _provider.Chunks = Enumerable.Repeat(string.Empty, 10000).ToArray();

        Assert.Null(await _service.GenerateAsync(_session));
        Assert.InRange(_provider.YieldedChunks, 1, 9999);
        Assert.True(_provider.Disposed);
    }

    [Fact]
    public async Task PrecancelledRequestsPropagateCancellationWithoutCallingTheModel()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.GenerateAsync(_session, cancellation.Token));
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task CancellationDuringStreamingPropagatesAndDisposesTheProvider()
    {
        await AddAsync(ChatRole.User, "A story.");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.BeforeChunks = async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };
        using var cancellation = new CancellationTokenSource();
        var pending = _service.GenerateAsync(_session, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(_provider.Disposed);
        Assert.True(_provider.StreamToken.IsCancellationRequested);
    }

    private Task SaveSettingsAsync(string language = "zh-CN", string model = "title-model",
        string baseUrl = "http://127.0.0.1:8080/v1", int contextLength = 8192) =>
        _settings.SaveAsync(new ProviderSettings
        {
            Model = model, BaseUrl = baseUrl, LanguageCode = language, ContextLength = contextLength,
        }, null);

    private Task<long> AddAsync(ChatRole role, string content) => _messages.AddAsync(new ChatMessage
    {
        ChatSessionId = _session.Id, Role = role, Content = content, CreatedAt = DateTimeOffset.UtcNow,
    });

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private sealed class TestProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string encrypted) => encrypted;
    }

    private sealed class TitleProvider : ILLMProvider
    {
        public string Id => "title-test";
        public string DisplayName => "Title test";
        public string[] Chunks { get; set; } = [];
        public List<ChatCompletionRequest> Requests { get; } = [];
        public Func<CancellationToken, Task>? BeforeChunks { get; set; }
        public int YieldedChunks { get; private set; }
        public bool Disposed { get; private set; }
        public CancellationToken StreamToken { get; private set; }

        public async IAsyncEnumerable<string> StreamAsync(ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            StreamToken = cancellationToken;
            try
            {
                if (BeforeChunks is not null) await BeforeChunks(cancellationToken);
                foreach (var chunk in Chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    YieldedChunks++;
                    yield return chunk;
                }
            }
            finally { Disposed = true; }
        }

        public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([]);

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
