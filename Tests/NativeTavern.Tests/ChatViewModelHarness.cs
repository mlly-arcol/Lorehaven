using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Providers;
using NativeTavern.Security;
using NativeTavern.Services;
using NativeTavern.ViewModels;

namespace NativeTavern.Tests;

/// A whole chat view model on a throwaway database, so behaviour that only appears while a reply is
/// streaming - drafts, the send queue - can be tested without a window.
internal sealed class ChatViewModelHarness : IDisposable
{
    public ChatViewModelHarness()
        : this(Path.Combine(Path.GetTempPath(), "NativeTavernChat-" + Guid.NewGuid().ToString("N")))
    {
    }

    /// A second harness on the same folder is what restarting the app looks like: same database, a
    /// brand new view model.
    public ChatViewModelHarness(string root)
    {
        RootDirectory = root;
        var pluginDirectory = Path.Combine(root, "plugins");
        Directory.CreateDirectory(pluginDirectory);
        Factory = new DatabaseConnectionFactory(Path.Combine(root, "data.db"));
        new DatabaseInitializer(Factory, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync().GetAwaiter().GetResult();
        Sessions = new ChatSessionRepository(Factory);
        Drafts = new ComposerDraftRepository(Factory);
        var messages = new ChatMessageRepository(Factory);
        var characters = new CharacterRepository(Factory);
        var settings = new SettingsService(new SettingsRepository(Factory), new NoopProtector());
        settings.SaveAsync(new ProviderSettings { BaseUrl = "http://127.0.0.1:8080/v1", Model = "m" }, null)
            .GetAwaiter().GetResult();
        var pluginService = new PluginService(pluginDirectory, NullLogger<PluginService>.Instance);
        var regexScripts = new RegexScriptService(
            new RegexScriptRepository(Factory), NullLogger<RegexScriptService>.Instance);
        Provider = new GatedProvider();
        var memoryService = new CharacterMemoryService(
            new CharacterMemoryRepository(Factory), messages, Sessions, characters,
            settings, Provider, NullLogger<CharacterMemoryService>.Instance);
        var chatService = new ChatService(
            Sessions, messages, new MessageSwipeRepository(Factory), new ChatAttachmentRepository(Factory),
            new AttachmentService(new ChatAttachmentRepository(Factory), NullLogger<AttachmentService>.Instance),
            new ConversationSummaryService(messages, Sessions), memoryService, characters,
            new PromptService(new PromptRepository(Factory), characters, Sessions,
                new KnowledgeService(new KnowledgeRepository(Factory), NullLogger<KnowledgeService>.Instance)),
            regexScripts,
            settings, Provider, NullLogger<ChatService>.Instance);
        Service = chatService;
        Scripts = regexScripts;
        Memory = memoryService;
        ViewModel = new ChatViewModel(
            chatService, new PromptRepository(Factory), Drafts, settings, regexScripts,
            new CharacterStatusService(new CharacterStatusRepository(Factory), messages, characters,
                settings, pluginService, Provider, NullLogger<CharacterStatusService>.Instance),
            new ReplySuggestionService(messages, settings, Provider, NullLogger<ReplySuggestionService>.Instance),
            pluginService, new TrayService(), memoryService, NullLogger<ChatViewModel>.Instance);
    }

    public string RootDirectory { get; }
    public ChatService Service { get; }
    public RegexScriptService Scripts { get; }
    public DatabaseConnectionFactory Factory { get; }
    public ChatSessionRepository Sessions { get; }
    public ComposerDraftRepository Drafts { get; }
    public ChatViewModel ViewModel { get; }
    public GatedProvider Provider { get; }
    public CharacterMemoryService Memory { get; }

    public async Task<ChatSession> CreateSessionAsync(string title, DateTimeOffset? stamp = null)
    {
        var when = stamp ?? DateTimeOffset.UtcNow;
        var session = new ChatSession { Title = title, CreatedAt = when, UpdatedAt = when };
        session.Id = await Sessions.CreateAsync(session);
        return session;
    }

    /// Streams reach the view model through the thread pool, so tests wait for a state instead of
    /// sleeping for a fixed guess.
    public static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(40);
        }
        return false;
    }

    public static async Task<bool> WaitForAsync(Func<Task<bool>> condition, int timeoutMilliseconds = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(40);
        }
        return false;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(RootDirectory, true); } catch (IOException) { }
    }
}

/// Streams canned replies, and holds the first one open so a test can act while a reply is in flight.
/// Replies are served in order; the last script repeats for any further request.
internal sealed class GatedProvider : ILLMProvider
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ChatCompletionRequest> _requests = [];
    private int _calls;

    public List<string[]> Scripts { get; } = [["夜色", "沉下来。"]];

    /// Suggestion and status requests interleave with reply requests, so a test that cares which reply
    /// comes back routes by request content instead of by call order.
    public Func<ChatCompletionRequest, string[]>? Router { get; set; }

    public void Release() => _release.TrySetResult();

    public int Started => Volatile.Read(ref _calls);

    public IReadOnlyList<ChatCompletionRequest> SnapshotRequests()
    {
        lock (_requests) { return _requests.ToArray(); }
    }

    public string Id => "gated";
    public string DisplayName => "Gated";

    public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ModelInfo>>([]);

    public async IAsyncEnumerable<string> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _calls) - 1;
        lock (_requests) { _requests.Add(request); }
        var chunks = Router?.Invoke(request) ?? Scripts[Math.Min(index, Scripts.Count - 1)];
        await _release.Task.ConfigureAwait(false);
        foreach (var chunk in chunks) yield return chunk;
    }

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}

internal sealed class NoopProtector : ISecretProtector
{
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string protectedText) => protectedText;
}
