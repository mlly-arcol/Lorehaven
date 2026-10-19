using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Services;
using NativeTavern.Models;
using Xunit;

namespace NativeTavern.Tests;

/// <summary>Exercises message filters against the same SQLite schema used by the application.</summary>
public sealed class MessageSearchFilterTests : IDisposable
{
    private readonly string _directory;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ChatMessageRepository _messages;

    public MessageSearchFilterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "NativeTavernSearchFilters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _factory = new DatabaseConnectionFactory(Path.Combine(_directory, "data.db"));
        new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync().GetAwaiter().GetResult();
        _messages = new ChatMessageRepository(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [Fact]
    public async Task QuerySessionPinnedAndSpeakerFiltersMustAllMatch()
    {
        var mira = await NewCharacterAsync("Mira", "小米");
        var other = await NewCharacterAsync("Other");
        var selectedSession = await NewSessionAsync("Selected", mira);
        var otherSession = await NewSessionAsync("Other", mira);
        var expected = await AddAsync(selectedSession, "The compass points north", pinned: true);
        await AddAsync(selectedSession, "An unpinned compass");
        await AddAsync(selectedSession, "A different object", pinned: true);
        await AddAsync(selectedSession, "Another speaker's compass", pinned: true, speakerId: other);
        await AddAsync(selectedSession, "A user's compass", pinned: true, role: ChatRole.User);
        await AddAsync(otherSession, "A compass in another session", pinned: true);

        var results = await _messages.SearchAsync("@小米 COMPASS", sessionId: selectedSession, pinnedOnly: true);

        var result = Assert.Single(results);
        Assert.Equal(expected.Id, result.MessageId);
        Assert.Equal(selectedSession, result.ChatSessionId);
        Assert.Equal("Selected", result.SessionTitle);
        Assert.Equal("Mira", result.Speaker);
    }

    [Fact]
    public async Task EmptyQueryBrowsesOnlyWhenSessionOrPinnedFilterIsPresent()
    {
        var first = await NewSessionAsync("First");
        var second = await NewSessionAsync("Second");
        var firstPinned = await AddAsync(first, "Pinned here", pinned: true);
        var firstPlain = await AddAsync(first, "Latest here");
        var secondPinned = await AddAsync(second, "Pinned elsewhere", pinned: true);

        Assert.Empty(await _messages.SearchAsync("   "));
        Assert.Equal(new[] { firstPlain.Id, firstPinned.Id },
            (await _messages.SearchAsync("   ", sessionId: first)).Select(result => result.MessageId));
        Assert.Equal(new[] { secondPinned.Id, firstPinned.Id },
            (await _messages.SearchAsync("", pinnedOnly: true)).Select(result => result.MessageId));
        Assert.Equal(firstPinned.Id,
            Assert.Single(await _messages.SearchAsync("", sessionId: first, pinnedOnly: true)).MessageId);
        Assert.Empty(await _messages.SearchAsync("", sessionId: long.MaxValue));
    }

    [Fact]
    public async Task FilteredBrowseReturnsLatestMatchesAndAppliesLimitAfterFiltering()
    {
        var session = await NewSessionAsync("Recent");
        var other = await NewSessionAsync("Elsewhere");
        await AddAsync(session, "Old pin", pinned: true);
        var middle = await AddAsync(session, "Middle pin", pinned: true);
        await AddAsync(session, "Unpinned later message");
        var latest = await AddAsync(session, new string('字', 180), pinned: true);
        await AddAsync(other, "Latest pin in another session", pinned: true);

        var results = await _messages.SearchAsync("", limit: 2, sessionId: session, pinnedOnly: true);

        Assert.Equal(new[] { latest.Id, middle.Id }, results.Select(result => result.MessageId));
        Assert.Equal(new string('字', 120) + "…", results[0].Snippet);
        Assert.Empty(await _messages.SearchAsync("", limit: 0, sessionId: session));
        Assert.Empty(await _messages.SearchAsync("", limit: -1, pinnedOnly: true));
    }

    [Theory]
    [InlineData("%", "100% certain", "1000 certain")]
    [InlineData("_", "one_two", "oneXtwo")]
    [InlineData("\\", "folder\\file", "folderfile")]
    [InlineData("\\_%", "literal \\_% sequence", "literal \\Xanything sequence")]
    public async Task FilteredContentSearchTreatsWildcardsAndEscapeCharacterLiterally(
        string query, string content, string decoy)
    {
        var session = await NewSessionAsync("Literal content");
        var expected = await AddAsync(session, content, pinned: true);
        await AddAsync(session, decoy, pinned: true);
        await AddAsync(session, content);
        var otherSession = await NewSessionAsync("Other");
        await AddAsync(otherSession, content, pinned: true);

        var result = Assert.Single(await _messages.SearchAsync(query, sessionId: session, pinnedOnly: true));

        Assert.Equal(expected.Id, result.MessageId);
        Assert.Contains(query, result.Snippet);
    }

    [Theory]
    [InlineData("star%", "starAnything")]
    [InlineData("star_", "starX")]
    [InlineData("star\\", "star")]
    public async Task SpeakerAliasesTreatWildcardsAndEscapeCharacterLiterally(string alias, string decoyAlias)
    {
        var literal = await NewCharacterAsync("Literal", $"first, {alias}， last");
        var decoy = await NewCharacterAsync("Decoy", decoyAlias);
        var session = await NewSessionAsync("Aliases", literal);
        var expected = await AddAsync(session, "Literal speaker", pinned: true);
        await AddAsync(session, "Decoy speaker", pinned: true, speakerId: decoy);

        var result = Assert.Single(await _messages.SearchAsync("@" + alias, sessionId: session, pinnedOnly: true));

        Assert.Equal(expected.Id, result.MessageId);
        Assert.Equal("Literal", result.Speaker);
    }

    [Theory]
    [InlineData("天星")]
    [InlineData("小星")]
    [InlineData("north")]
    public async Task ExistingSpeakerSearchUsesSessionCharacterAndActualGroupSpeaker(string speaker)
    {
        var target = await NewCharacterAsync("天星", "小星， North");
        var other = await NewCharacterAsync("Other");
        var ordinary = await NewSessionAsync("Ordinary", target);
        var group = await NewSessionAsync("Group", other, isGroup: true);
        var ordinaryReply = await AddAsync(ordinary, "查看星图");
        await AddAsync(ordinary, "用户提到星图", role: ChatRole.User, speakerId: target);
        var groupReply = await AddAsync(group, "绘制星图", speakerId: target);
        await AddAsync(group, "其他角色的星图", speakerId: other);

        var results = await _messages.SearchAsync("星图 @" + speaker);

        Assert.Equal(new[] { groupReply.Id, ordinaryReply.Id }, results.Select(result => result.MessageId));
        Assert.All(results, result => Assert.Equal("天星", result.Speaker));
    }

    private Task<long> NewSessionAsync(string title, long? characterId = null, bool isGroup = false) =>
        new ChatSessionRepository(_factory).CreateAsync(new ChatSession
        {
            Title = title, CharacterId = characterId, IsGroupChat = isGroup,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

    private Task<long> NewCharacterAsync(string name, string aliases = "") =>
        new CharacterRepository(_factory).CreateAsync(new Character
        {
            Name = name, Aliases = aliases,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

    private async Task<ChatMessage> AddAsync(long sessionId, string content, bool pinned = false,
        ChatRole role = ChatRole.Assistant, long? speakerId = null)
    {
        var message = new ChatMessage
        {
            ChatSessionId = sessionId, Role = role, Content = content, IsPinned = pinned,
            SpeakerCharacterId = speakerId, CreatedAt = DateTimeOffset.UtcNow,
        };
        await _messages.AddAsync(message);
        return message;
    }
}
