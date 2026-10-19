using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class SessionTitleUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NativeTavernTitles-" + Guid.NewGuid().ToString("N"));
    private readonly DatabaseConnectionFactory _factory;
    private readonly ChatSessionRepository _sessions;
    private static readonly DateTimeOffset OriginalStamp = new(2026, 10, 10, 8, 30, 0, TimeSpan.FromHours(8));

    public SessionTitleUpdateTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new DatabaseConnectionFactory(Path.Combine(_directory, "data.db"));
        new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync().GetAwaiter().GetResult();
        _sessions = new ChatSessionRepository(_factory);
    }

    [Fact]
    public async Task ConditionalRenameSucceedsWhenTheStoredTitleStillMatches()
    {
        var session = await CreateSessionAsync("原来的标题");
        var updatedAt = OriginalStamp.AddMinutes(5);

        var changed = await _sessions.SetTitleAsync(session.Id, "雨夜重逢", updatedAt, session.Title);

        Assert.True(changed);
        var stored = Assert.IsType<ChatSession>(await _sessions.GetAsync(session.Id));
        Assert.Equal("雨夜重逢", stored.Title);
        Assert.Equal(updatedAt, stored.UpdatedAt);
        AssertUnchangedMetadata(session, stored);
    }

    [Theory]
    [InlineData("Draft", "draft")]
    [InlineData("Draft", "")]
    [InlineData("Draft ", "Draft")]
    [InlineData("草稿", "其他标题")]
    [InlineData("caf\u00e9", "cafe\u0301")]
    public async Task ATitleMismatchPreservesTheStoredTitleAndTimestamp(string storedTitle, string expectedTitle)
    {
        var session = await CreateSessionAsync(storedTitle);

        var changed = await _sessions.SetTitleAsync(session.Id, "迟到的自动标题", OriginalStamp.AddHours(1), expectedTitle);

        Assert.False(changed);
        var stored = Assert.IsType<ChatSession>(await _sessions.GetAsync(session.Id));
        Assert.Equal(storedTitle, stored.Title);
        Assert.Equal(OriginalStamp, stored.UpdatedAt);
        AssertUnchangedMetadata(session, stored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADeletedSessionCannotBeRenamed(bool conditional)
    {
        var session = await CreateSessionAsync("将被删除");
        await _sessions.DeleteAsync(session.Id);

        var changed = await _sessions.SetTitleAsync(session.Id, "迟到的标题", OriginalStamp.AddHours(1),
            conditional ? session.Title : null);

        Assert.False(changed);
        Assert.Null(await _sessions.GetAsync(session.Id));
    }

    [Fact]
    public async Task AnUnconditionalRenameChangesOnlyTheTitleAndTimestamp()
    {
        var session = await CreateSessionAsync("手动重命名前");
        var updatedAt = OriginalStamp.AddDays(1);

        var changed = await _sessions.SetTitleAsync(session.Id, "手动重命名后", updatedAt);

        Assert.True(changed);
        var stored = Assert.IsType<ChatSession>(await _sessions.GetAsync(session.Id));
        Assert.Equal("手动重命名后", stored.Title);
        Assert.Equal(updatedAt, stored.UpdatedAt);
        AssertUnchangedMetadata(session, stored);
    }

    [Fact]
    public async Task MetadataEditedWhileATitleIsBeingPreparedSurvivesItsConditionalRename()
    {
        var session = await CreateSessionAsync("准备生成标题");
        var captured = Assert.IsType<ChatSession>(await _sessions.GetAsync(session.Id));
        var otherWriter = new ChatSessionRepository(_factory);
        var edited = Assert.IsType<ChatSession>(await otherWriter.GetAsync(session.Id));
        edited.CharacterId = 80;
        edited.IsGroupChat = false;
        edited.ParentSessionId = null;
        edited.BranchedFromMessageId = null;
        edited.PersonaId = 81;
        edited.LorebookId = 82;
        edited.PromptPresetId = 83;
        edited.AuthorNote = "生成标题期间编辑的上下文";
        edited.Summary = "生成标题期间编辑的摘要";
        edited.SummaryCoveredCount = 17;
        edited.SummaryIsManual = false;
        edited.UpdatedAt = OriginalStamp.AddMinutes(1);
        await otherWriter.UpdateAsync(edited);
        await otherWriter.SetGroupAsync(session.Id, "后来移动的分组");
        await otherWriter.SetPinnedAsync(session.Id, false);
        await otherWriter.SetMemoryCoveredAsync(session.Id, 19);
        var beforeRename = Assert.IsType<ChatSession>(await otherWriter.GetAsync(session.Id));
        var renamedAt = OriginalStamp.AddMinutes(2);

        var changed = await _sessions.SetTitleAsync(captured.Id, "新自动标题", renamedAt, captured.Title);

        Assert.True(changed);
        var stored = Assert.IsType<ChatSession>(await _sessions.GetAsync(session.Id));
        Assert.Equal("新自动标题", stored.Title);
        Assert.Equal(renamedAt, stored.UpdatedAt);
        AssertUnchangedMetadata(beforeRename, stored);
    }

    [Fact]
    public async Task ANewerRenamePreventsAStaleConditionalTitleFromBeingSaved()
    {
        var session = await CreateSessionAsync("最初的标题");
        var manuallyRenamedAt = OriginalStamp.AddMinutes(1);
        Assert.True(await _sessions.SetTitleAsync(session.Id, "用户的新标题", manuallyRenamedAt));

        var changed = await _sessions.SetTitleAsync(session.Id, "较早生成的标题", OriginalStamp.AddMinutes(2), session.Title);

        Assert.False(changed);
        var stored = Assert.IsType<ChatSession>(await _sessions.GetAsync(session.Id));
        Assert.Equal("用户的新标题", stored.Title);
        Assert.Equal(manuallyRenamedAt, stored.UpdatedAt);
    }

    private async Task<ChatSession> CreateSessionAsync(string title)
    {
        var parent = new ChatSession { Title = "父对话", CreatedAt = OriginalStamp, UpdatedAt = OriginalStamp };
        await _sessions.CreateAsync(parent);
        var branchedFrom = await new ChatMessageRepository(_factory).AddAsync(new ChatMessage
        {
            ChatSessionId = parent.Id, Role = ChatRole.User, Content = "分支起点", CreatedAt = OriginalStamp
        });
        var session = new ChatSession
        {
            Title = title, CharacterId = 12, IsGroupChat = true, ParentSessionId = parent.Id,
            BranchedFromMessageId = branchedFrom, PersonaId = 13, LorebookId = 14, PromptPresetId = 15,
            AuthorNote = "原上下文", Summary = "原摘要", SummaryCoveredCount = 3, SummaryIsManual = true,
            MemoryCoveredCount = 4, GroupName = "原分组", IsPinned = true,
            CreatedAt = OriginalStamp.AddDays(-1), UpdatedAt = OriginalStamp
        };
        await _sessions.CreateAsync(session);
        return session;
    }

    private static void AssertUnchangedMetadata(ChatSession expected, ChatSession actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.CharacterId, actual.CharacterId);
        Assert.Equal(expected.IsGroupChat, actual.IsGroupChat);
        Assert.Equal(expected.ParentSessionId, actual.ParentSessionId);
        Assert.Equal(expected.BranchedFromMessageId, actual.BranchedFromMessageId);
        Assert.Equal(expected.PersonaId, actual.PersonaId);
        Assert.Equal(expected.LorebookId, actual.LorebookId);
        Assert.Equal(expected.PromptPresetId, actual.PromptPresetId);
        Assert.Equal(expected.AuthorNote, actual.AuthorNote);
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(expected.SummaryCoveredCount, actual.SummaryCoveredCount);
        Assert.Equal(expected.SummaryIsManual, actual.SummaryIsManual);
        Assert.Equal(expected.MemoryCoveredCount, actual.MemoryCoveredCount);
        Assert.Equal(expected.GroupName, actual.GroupName);
        Assert.Equal(expected.IsPinned, actual.IsPinned);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
