using Dapper;
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

/// <summary>
/// Conversations are the unit a writer organizes a story in, so pinning and grouping must survive a
/// reload without disturbing the "last touched" ordering the session list is sorted by.
/// </summary>
public class SessionOrganizationTests : IDisposable
{
    private readonly string _directory;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ChatSessionRepository _sessions;

    public SessionOrganizationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "NativeTavernSessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _factory = new DatabaseConnectionFactory(Path.Combine(_directory, "data.db"));
        new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync().GetAwaiter().GetResult();
        _sessions = new ChatSessionRepository(_factory);
    }

    [Fact]
    public async Task PinnedSessionsSortFirstWithoutMovingTheirTimestamp()
    {
        var older = await CreateAsync("旧故事", DateTimeOffset.UtcNow.AddDays(-2));
        var newer = await CreateAsync("新故事", DateTimeOffset.UtcNow);
        var stamp = older.UpdatedAt;

        await _sessions.SetPinnedAsync(older.Id, true);
        var ordered = await _sessions.GetAllAsync();

        Assert.Equal(newer.UpdatedAt, (await _sessions.GetAsync(newer.Id))!.UpdatedAt);
        Assert.Equal(new long[] { older.Id, newer.Id }, ordered.Select(x => x.Id).ToList());
        Assert.True(ordered[0].IsPinned);
        Assert.Equal(stamp, (await _sessions.GetAsync(older.Id))!.UpdatedAt);
    }

    [Fact]
    public async Task UnpinningRestoresThePlainOrdering()
    {
        var first = await CreateAsync("第一篇", DateTimeOffset.UtcNow.AddDays(-1));
        var second = await CreateAsync("第二篇", DateTimeOffset.UtcNow);
        await _sessions.SetPinnedAsync(first.Id, true);

        await _sessions.SetPinnedAsync(first.Id, false);

        Assert.Equal(new long[] { second.Id, first.Id }, (await _sessions.GetAllAsync()).Select(x => x.Id).ToList());
        Assert.False((await _sessions.GetAsync(first.Id))!.IsPinned);
    }

    [Fact]
    public async Task GroupsRoundTripAndOnlyRealNamesAreListed()
    {
        var first = await CreateAsync("山门外");
        var second = await CreateAsync("界碑下");
        var third = await CreateAsync("无组");
        await _sessions.SetGroupAsync(first.Id, "山门篇");
        await _sessions.SetGroupAsync(second.Id, "山门篇");
        await _sessions.SetGroupAsync(third.Id, "   ");

        var groups = await _sessions.GetGroupNamesAsync();

        Assert.Equal("山门篇", Assert.Single(groups));
        Assert.Equal("山门篇", (await _sessions.GetAsync(first.Id))!.GroupName);
        Assert.Equal(" · 山门篇", (await _sessions.GetAsync(first.Id))!.GroupLabel);
        Assert.Equal("   ", (await _sessions.GetAsync(third.Id))!.GroupName);
        Assert.Empty((await _sessions.GetAsync(third.Id))!.GroupLabel);
    }

    [Fact]
    public async Task GroupingLeavesTheConversationStampAlone()
    {
        var session = await CreateAsync("古墓");
        var stamp = session.UpdatedAt;

        await _sessions.SetGroupAsync(session.Id, "第一卷");

        Assert.Equal(stamp, (await _sessions.GetAsync(session.Id))!.UpdatedAt);
    }

    [Fact]
    public async Task OldDatabasesGainTheColumnsAndThePinnedIndex()
    {
        await using (var connection = _factory.CreateConnection())
        {
            await connection.ExecuteAsync("DROP INDEX IF EXISTS IX_ChatSessions_PinnedUpdated");
            await connection.ExecuteAsync("ALTER TABLE ChatSessions DROP COLUMN IsPinned");
            await connection.ExecuteAsync("ALTER TABLE ChatSessions DROP COLUMN GroupName");
        }

        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        await using (var connection = _factory.CreateConnection())
        {
            var columns = (await connection.QueryAsync<string>(
                "SELECT name FROM pragma_table_info('ChatSessions')")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("IsPinned", columns);
            Assert.Contains("GroupName", columns);
            Assert.NotNull(await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT name FROM sqlite_master WHERE type='index' AND name='IX_ChatSessions_PinnedUpdated'"));
        }
        var session = await CreateAsync("恢复后仍可置顶");
        await _sessions.SetPinnedAsync(session.Id, true);
        Assert.True((await _sessions.GetAllAsync()).Single().IsPinned);
    }

    [Fact]
    public async Task ServiceRejectsUnusablyLongGroupNames()
    {
        var service = await CreateChatServiceAsync();
        var session = await CreateAsync("长名");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetSessionGroupAsync(session, new string('卷', 41)));
        await service.SetSessionGroupAsync(session, "  第一卷  ");
        Assert.Equal("第一卷", session.GroupName);
    }

    private async Task<ChatSession> CreateAsync(string title, DateTimeOffset? updatedAt = null)
    {
        var stamp = updatedAt ?? DateTimeOffset.UtcNow;
        var session = new ChatSession { Title = title, CreatedAt = stamp, UpdatedAt = stamp };
        session.Id = await _sessions.CreateAsync(session);
        return session;
    }

    private async Task<ChatService> CreateChatServiceAsync()
    {
        var messages = new ChatMessageRepository(_factory);
        var prompts = new PromptRepository(_factory);
        var characters = new CharacterRepository(_factory);
        var knowledge = new KnowledgeService(new KnowledgeRepository(_factory), NullLogger<KnowledgeService>.Instance);
        var settings = new SettingsService(new SettingsRepository(_factory), new NoopProtector());
        await settings.SaveAsync(new ProviderSettings { BaseUrl = "http://127.0.0.1:8080/v1", Model = "m" }, null);
        return new ChatService(
            _sessions, messages, new MessageSwipeRepository(_factory), new ChatAttachmentRepository(_factory),
            new AttachmentService(new ChatAttachmentRepository(_factory), NullLogger<AttachmentService>.Instance),
            new ConversationSummaryService(messages, _sessions),
            new CharacterMemoryService(
                new CharacterMemoryRepository(_factory), messages, _sessions, characters,
                settings, new SilentProvider(), NullLogger<CharacterMemoryService>.Instance),
            characters,
            new PromptService(prompts, characters, _sessions, knowledge),
            new RegexScriptService(new RegexScriptRepository(_factory), NullLogger<RegexScriptService>.Instance),
            settings, new SilentProvider(), NullLogger<ChatService>.Instance);
    }

    private sealed class NoopProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedText) => protectedText;
    }

    private sealed class SilentProvider : ILLMProvider
    {
        public string Id => "test";
        public string DisplayName => "Test";
        public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<string> StreamAsync(
            ChatCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
