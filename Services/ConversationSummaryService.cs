using NativeTavern.Data.Repositories;
using NativeTavern.Models;

namespace NativeTavern.Services;

/// <summary>
/// Keeps a rolling recap of the earlier turns so a long roleplay still fits the context window.
/// Only the turns nobody has summarized yet are read on each update, and a recap the writer edited by
/// hand is never overwritten automatically.
/// </summary>
public sealed class ConversationSummaryService(ChatMessageRepository messages, ChatSessionRepository sessions)
{
    public const int MinimumMessages = 20;
    public const int RecentMessages = 10;
    public const string Header = "Automatic conversation summary (earlier context):";
    public const int MaxLines = 24;
    public const int MaxCharacters = 6000;
    public const int ManualLimit = 8000;
    private const int LineContentLimit = 260;

    public async Task UpdateIfNeededAsync(ChatSession session)
    {
        if (session.SummaryIsManual) return;
        var total = await messages.CountBySessionAsync(session.Id);
        if (total < MinimumMessages)
        {
            if (session.SummaryCoveredCount > 0 || session.Summary.StartsWith(Header, StringComparison.Ordinal))
                await SaveAsync(session, [], 0);
            return;
        }
        var coverable = total - RecentMessages;
        if (coverable < session.SummaryCoveredCount)
        {
            await RebuildAsync(session);
            return;
        }
        if (coverable == session.SummaryCoveredCount) return;

        var count = coverable - session.SummaryCoveredCount;
        var fresh = await messages.GetBySessionRangeAsync(session.Id, session.SummaryCoveredCount, count);
        if (fresh.Count != count)
        {
            await RebuildAsync(session);
            return;
        }
        await SaveAsync(session, Compact(ReadLines(session).Concat(fresh.Select(ToLine))), coverable);
    }

    /// <summary>Recomputes the recap from scratch, for example after a back-edited message.</summary>
    public async Task RebuildAsync(ChatSession session)
    {
        var history = await messages.GetBySessionAsync(session.Id);
        var coverable = Math.Max(0, history.Count - RecentMessages);
        await SaveAsync(session, Compact(history.Take(coverable).Select(ToLine)), coverable);
    }

    /// <summary>Stores the writer's own recap and pauses the automatic digest until the next rebuild.</summary>
    public async Task SaveManualAsync(ChatSession session, string summary)
    {
        summary = summary.Trim();
        if (summary.Length > ManualLimit)
            throw new InvalidOperationException($"剧情回顾不能超过 {ManualLimit} 个字符。");
        session.Summary = summary;
        session.SummaryIsManual = summary.Length > 0;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await sessions.UpdateAsync(session);
    }

    public async Task ClearAsync(ChatSession session)
    {
        session.Summary = string.Empty;
        session.SummaryCoveredCount = 0;
        session.SummaryIsManual = false;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await sessions.UpdateAsync(session);
    }

    private async Task SaveAsync(ChatSession session, IReadOnlyList<string> lines, int coveredCount)
    {
        session.Summary = lines.Count == 0 ? string.Empty : Header + "\n" + string.Join("\n", lines);
        session.SummaryCoveredCount = coveredCount;
        // Any automatic write hands control back to the digest, including right after a manual recap.
        session.SummaryIsManual = false;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await sessions.UpdateAsync(session);
    }

    private static IReadOnlyList<string> ReadLines(ChatSession session) =>
        session.Summary.StartsWith(Header, StringComparison.Ordinal)
            ? session.Summary.Split('\n').Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).ToList()
            : [];

    private static string ToLine(ChatMessage message) =>
        $"{message.Role}: {Trim(message.Content, LineContentLimit)}";

    /// <summary>Oldest lines are dropped first: the newest covered turns matter most.</summary>
    private static List<string> Compact(IEnumerable<string> lines)
    {
        var kept = new Queue<string>();
        var characters = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            kept.Enqueue(line);
            characters += line.Length + 1;
            while (kept.Count > MaxLines || (kept.Count > 1 && characters > MaxCharacters))
                characters -= kept.Dequeue().Length + 1;
        }
        var result = kept.ToList();
        if (result.Count == 1 && result[0].Length > MaxCharacters) result[0] = result[0][..MaxCharacters];
        return result;
    }

    private static string Trim(string content, int max) => content.Length <= max ? content : content[..max] + "…";
}
