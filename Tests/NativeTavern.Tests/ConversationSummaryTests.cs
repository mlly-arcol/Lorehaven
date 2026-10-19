using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

/// <summary>
/// The recap is what the model remembers of the first half of a long roleplay, so it has to grow without
/// re-reading everything, respect a hand written version, and never drop the newest covered turns.
/// </summary>
public class ConversationSummaryTests : IDisposable
{
    private readonly string _directory;
    private readonly ChatMessageRepository _messages;
    private readonly ChatSessionRepository _sessions;
    private readonly ConversationSummaryService _summaries;
    private readonly ChatSession _session;

    public ConversationSummaryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "NativeTavernRecap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var factory = new DatabaseConnectionFactory(Path.Combine(_directory, "data.db"));
        new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync().GetAwaiter().GetResult();
        _messages = new ChatMessageRepository(factory);
        _sessions = new ChatSessionRepository(factory);
        _summaries = new ConversationSummaryService(_messages, _sessions);
        var id = _sessions.CreateAsync(new ChatSession
        {
            Title = "Recap", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        }).GetAwaiter().GetResult();
        _session = _sessions.GetAsync(id).GetAwaiter().GetResult()!;
    }

    [Fact]
    public async Task DigestCoversOnlyTheEarlierTurns()
    {
        await AddTurnsAsync(1, 25);

        await _summaries.UpdateIfNeededAsync(_session);

        Assert.Equal(15, _session.SummaryCoveredCount);
        Assert.StartsWith(ConversationSummaryService.Header, _session.Summary, StringComparison.Ordinal);
        Assert.Equal(15, Lines(_session.Summary).Count);
        Assert.Contains("第 1 条", _session.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("第 25 条", _session.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortConversationsGetNoDigest()
    {
        await AddTurnsAsync(1, 12);
        await _summaries.UpdateIfNeededAsync(_session);

        Assert.Equal(0, _session.SummaryCoveredCount);
        Assert.Empty(_session.Summary);
    }

    [Fact]
    public async Task LaterUpdatesAppendOnlyTheNewTurns()
    {
        await AddTurnsAsync(1, 25);
        await _summaries.UpdateIfNeededAsync(_session);
        var firstLine = Lines(_session.Summary).First();

        await AddTurnsAsync(26, 5);
        await _summaries.UpdateIfNeededAsync(_session);

        Assert.Equal(20, _session.SummaryCoveredCount);
        Assert.Equal(20, Lines(_session.Summary).Count);
        Assert.Equal(firstLine, Lines(_session.Summary).First());
        Assert.Contains("第 20 条", _session.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageRangeReadsOnlyTheRequestedPositions()
    {
        await AddTurnsAsync(1, 30);
        Assert.Equal(30, await _messages.CountBySessionAsync(_session.Id));

        var slice = await _messages.GetBySessionRangeAsync(_session.Id, 17, 3);

        Assert.Equal(new[] { "第 18 条", "第 19 条", "第 20 条" },
            slice.Select(message => message.Content));
    }

    [Fact]
    public async Task CoverageSurvivesAReload()
    {
        await AddTurnsAsync(1, 25);
        await _summaries.UpdateIfNeededAsync(_session);

        var reloaded = await _sessions.GetAsync(_session.Id);

        Assert.Equal(15, reloaded!.SummaryCoveredCount);
        Assert.False(reloaded.SummaryIsManual);
        Assert.Equal(_session.Summary, reloaded.Summary);
    }

    [Fact]
    public async Task AHandWrittenRecapIsNeverOverwritten()
    {
        await _summaries.SaveManualAsync(_session, "我自己写的回顾：主角立了界碑。");
        await AddTurnsAsync(1, 30);

        await _summaries.UpdateIfNeededAsync(_session);

        Assert.Equal("我自己写的回顾：主角立了界碑。", _session.Summary);
        Assert.True(_session.SummaryIsManual);
        Assert.Equal(0, _session.SummaryCoveredCount);
    }

    [Fact]
    public async Task RegeneratingHandsControlBackToTheDigest()
    {
        await AddTurnsAsync(1, 25);
        await _summaries.SaveManualAsync(_session, "临时回顾");
        Assert.True(_session.SummaryIsManual);

        await _summaries.RebuildAsync(_session);

        Assert.False(_session.SummaryIsManual);
        Assert.Equal(15, _session.SummaryCoveredCount);
        Assert.Contains("第 1 条", _session.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrowingDigestKeepsTheNewestCoveredTurns()
    {
        await AddTurnsAsync(1, 40, verbose: true);

        await _summaries.UpdateIfNeededAsync(_session);

        var summary = _session.Summary;
        Assert.True(summary.Length <= ConversationSummaryService.Header.Length + ConversationSummaryService.MaxCharacters + 1);
        Assert.Contains("第 30 条", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("第 1 条\n", summary, StringComparison.Ordinal);
        Assert.True(Lines(summary).Count <= ConversationSummaryService.MaxLines);
    }

    [Fact]
    public async Task DeletedTurnsTriggerAFullRebuild()
    {
        await AddTurnsAsync(1, 25);
        await _summaries.UpdateIfNeededAsync(_session);
        _session.SummaryCoveredCount = 40;

        await _summaries.UpdateIfNeededAsync(_session);

        Assert.Equal(15, _session.SummaryCoveredCount);
        Assert.Equal(15, Lines(_session.Summary).Count);
    }

    [Fact]
    public async Task RemovingEnoughTurnsClearsAnOutdatedAutomaticDigest()
    {
        await AddTurnsAsync(1, 25);
        await _summaries.UpdateIfNeededAsync(_session);
        var history = await _messages.GetBySessionAsync(_session.Id);
        foreach (var message in history.Take(8)) await _messages.DeleteAsync(message.Id);

        await _summaries.UpdateIfNeededAsync(_session);

        Assert.Empty(_session.Summary);
        Assert.Equal(0, _session.SummaryCoveredCount);
        Assert.Equal(0, (await _sessions.GetAsync(_session.Id))!.SummaryCoveredCount);
    }

    [Fact]
    public async Task ClearingResetsEverything()
    {
        await AddTurnsAsync(1, 25);
        await _summaries.UpdateIfNeededAsync(_session);

        await _summaries.ClearAsync(_session);

        Assert.Empty(_session.Summary);
        Assert.Equal(0, _session.SummaryCoveredCount);
        Assert.False(_session.SummaryIsManual);
        Assert.Equal(0, (await _sessions.GetAsync(_session.Id))!.SummaryCoveredCount);
    }

    [Fact]
    public async Task OverLongManualRecapsAreRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _summaries.SaveManualAsync(_session, new string('长', ConversationSummaryService.ManualLimit + 1)));
    }

    private async Task AddTurnsAsync(int first, int count, bool verbose = false)
    {
        for (var index = first; index < first + count; index++)
        {
            await _messages.AddAsync(new ChatMessage
            {
                ChatSessionId = _session.Id,
                Role = index % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                Content = verbose ? $"第 {index} 条 " + new string('夜', 250) : $"第 {index} 条",
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(index)
            });
        }
    }

    private static IReadOnlyList<string> Lines(string summary) =>
        summary.Split('\n').Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
