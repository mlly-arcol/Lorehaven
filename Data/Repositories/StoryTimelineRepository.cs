using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using NativeTavern.Models;

namespace NativeTavern.Data.Repositories;

public sealed class StoryTimelineRepository(DatabaseConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<StoryTimelineEvent>> GetBySessionAsync(long sessionId)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<EventRow>(
            "SELECT * FROM StoryTimelineEvents WHERE ChatSessionId=@sessionId " +
            "ORDER BY COALESCE(SourceMessageId, 9223372036854775807), Id", new { sessionId });
        return rows.Select(x => x.ToModel()).ToArray();
    }

    public async Task<IReadOnlyList<StoryTimelineSourceMessage>> GetNextSourceBatchAsync(long sessionId, int limit)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<SourceRow>(
            "SELECT Id, Role, Content FROM ChatMessages WHERE ChatSessionId=@sessionId " +
            "AND Id > COALESCE((SELECT LastMessageId FROM StoryTimelineScans WHERE ChatSessionId=@sessionId), 0) " +
            "AND Role IN ('User','Assistant') AND TRIM(Content)<>'' ORDER BY Id LIMIT @limit",
            new { sessionId, limit = Math.Clamp(limit, 1, 24) });
        return rows.Select(x => new StoryTimelineSourceMessage(x.Id, x.Role, x.Content)).ToArray();
    }

    /// <summary>
    /// Reads a bounded transcript without discarding the tail of a long message. A partial
    /// message stays ahead of the completed-message watermark until its final slice is saved.
    /// </summary>
    public async Task<StoryTimelineScanBatch> GetNextScanBatchAsync(
        long sessionId, int limit, int maxCharacters)
    {
        await using var connection = connectionFactory.CreateConnection();
        var cursor = await connection.QuerySingleOrDefaultAsync<ScanRow>(
            "SELECT LastMessageId,PartialMessageId,PartialOffset,PartialContentHash FROM StoryTimelineScans " +
            "WHERE ChatSessionId=@sessionId", new { sessionId }) ?? new ScanRow();
        var count = Math.Clamp(limit, 1, 24);
        var budget = Math.Clamp(maxCharacters, 256, 12_000);
        var rows = (await connection.QueryAsync<SourceRow>(
            "SELECT Id,Role,Content FROM ChatMessages WHERE ChatSessionId=@sessionId " +
            "AND Id>@lastMessageId AND Role IN ('User','Assistant') AND TRIM(Content)<>'' " +
            "ORDER BY Id LIMIT @take",
            new { sessionId, lastMessageId = cursor.LastMessageId, take = count + 1 })).ToArray();

        var chunks = new List<StoryTimelineScanChunk>();
        var completedId = cursor.LastMessageId;
        long? partialId = null;
        var partialOffset = 0;
        string? partialContentHash = null;
        foreach (var row in rows.Take(count))
        {
            if (budget < 600 && chunks.Count > 0) break;
            var contentHash = HashContent(row.Content);
            // Editing a partly scanned message invalidates its offset, including edits that
            // leave the new text longer than the old offset.
            var start = row.Id == cursor.PartialMessageId &&
                        string.Equals(contentHash, cursor.PartialContentHash, StringComparison.Ordinal)
                ? cursor.PartialOffset : 0;
            if (start < 0 || start >= row.Content.Length) start = 0;
            var length = Math.Min(row.Content.Length - start, budget);
            if (start + length < row.Content.Length &&
                char.IsHighSurrogate(row.Content[start + length - 1]) &&
                char.IsLowSurrogate(row.Content[start + length]))
                length--;
            var chunk = new StoryTimelineScanChunk(
                row.Id, row.Role, row.Content.Substring(start, length), start, row.Content.Length, contentHash);
            chunks.Add(chunk);
            budget -= length;
            if (chunk.IsComplete)
            {
                completedId = row.Id;
            }
            else
            {
                partialId = row.Id;
                partialOffset = chunk.EndOffset;
                partialContentHash = contentHash;
                break;
            }
        }

        return new StoryTimelineScanBatch(
            chunks, cursor.LastMessageId, cursor.PartialMessageId, cursor.PartialOffset,
            cursor.PartialContentHash, completedId, partialId, partialOffset, partialContentHash,
            partialId is not null || rows.Length > chunks.Count);
    }

    public async Task<StoryTimelineEvent> AddManualAsync(
        long sessionId, long? sourceMessageId, string title, string detail)
    {
        await using var connection = connectionFactory.CreateConnection();
        if (sourceMessageId is long id && !await SourceBelongsToSessionAsync(connection, sessionId, id))
            throw new InvalidDataException("关联的消息不属于当前对话。");
        var now = DateTimeOffset.UtcNow;
        var item = new StoryTimelineEvent
        {
            ChatSessionId = sessionId, SourceMessageId = sourceMessageId, Title = title,
            Detail = detail, CreatedAt = now, UpdatedAt = now
        };
        item.Id = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO StoryTimelineEvents(ChatSessionId,SourceMessageId,Title,Detail,IsAutoGenerated,CreatedAt,UpdatedAt) " +
            "VALUES(@ChatSessionId,@SourceMessageId,@Title,@Detail,0,@CreatedAt,@UpdatedAt); SELECT last_insert_rowid();",
            ToParameters(item));
        return item;
    }

    public async Task<bool> UpdateAsync(StoryTimelineEvent item)
    {
        await using var connection = connectionFactory.CreateConnection();
        if (item.SourceMessageId is long id && !await SourceBelongsToSessionAsync(connection, item.ChatSessionId, id))
            throw new InvalidDataException("关联的消息不属于当前对话。");
        item.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            var changed = await connection.ExecuteAsync(
                "UPDATE StoryTimelineEvents SET SourceMessageId=@SourceMessageId,Title=@Title,Detail=@Detail,UpdatedAt=@UpdatedAt " +
                "WHERE Id=@Id AND ChatSessionId=@ChatSessionId", ToParameters(item));
            return changed == 1;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && ex.SqliteExtendedErrorCode == 2067)
        {
            throw new InvalidDataException("同一来源消息下已存在同名剧情事件，请修改标题。", ex);
        }
    }

    public async Task<bool> DeleteAsync(long sessionId, long eventId)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteAsync(
            "DELETE FROM StoryTimelineEvents WHERE Id=@eventId AND ChatSessionId=@sessionId",
            new { sessionId, eventId }) == 1;
    }

    public async Task<int> SaveExtractionAsync(
        long sessionId, IReadOnlyList<StoryTimelineEvent> events, StoryTimelineScanBatch batch)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (!await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM ChatSessions WHERE Id=@sessionId)", new { sessionId }, transaction))
            throw new InvalidDataException("当前对话已不存在，无法保存剧情事件。");
        var cursor = await connection.QuerySingleOrDefaultAsync<ScanRow>(
            "SELECT LastMessageId,PartialMessageId,PartialOffset,PartialContentHash FROM StoryTimelineScans " +
            "WHERE ChatSessionId=@sessionId", new { sessionId }, transaction) ?? new ScanRow();
        if (cursor.LastMessageId != batch.ExpectedLastMessageId ||
            cursor.PartialMessageId != batch.ExpectedPartialMessageId ||
            cursor.PartialOffset != batch.ExpectedPartialOffset ||
            !string.Equals(cursor.PartialContentHash, batch.ExpectedPartialContentHash,
                StringComparison.Ordinal))
            throw new InvalidDataException("剧情时间线扫描位置已改变，请重试提取。");

        // Model calls run outside the transaction. Verify every scanned message, even when
        // the model returned no events, before committing results or advancing the cursor.
        var sources = (await connection.QueryAsync<SourceRow>(
            "SELECT Id,Role,Content FROM ChatMessages WHERE ChatSessionId=@sessionId AND Id IN @ids",
            new { sessionId, ids = batch.Chunks.Select(x => x.Id).ToArray() }, transaction))
            .ToDictionary(x => x.Id);
        if (batch.Chunks.Count == 0 || batch.Chunks.Any(chunk =>
                !sources.TryGetValue(chunk.Id, out var source) ||
                !string.Equals(source.Role, chunk.Role, StringComparison.Ordinal) ||
                !string.Equals(HashContent(source.Content), chunk.ContentHash, StringComparison.Ordinal)))
            throw new InvalidDataException("提取期间对话消息已改变，请重试提取。");
        var added = 0;
        foreach (var item in events)
        {
            if (item.ChatSessionId != sessionId || item.SourceMessageId is not long sourceId ||
                !sources.ContainsKey(sourceId))
                throw new InvalidDataException("剧情事件的来源消息无效。");
            added += await connection.ExecuteAsync(
                "INSERT OR IGNORE INTO StoryTimelineEvents(ChatSessionId,SourceMessageId,Title,Detail,IsAutoGenerated,CreatedAt,UpdatedAt) " +
                "SELECT @ChatSessionId,@SourceMessageId,@Title,@Detail,1,@CreatedAt,@UpdatedAt " +
                "WHERE EXISTS (SELECT 1 FROM ChatMessages WHERE Id=@SourceMessageId AND ChatSessionId=@ChatSessionId)",
                ToParameters(item), transaction);
        }
        await connection.ExecuteAsync(
            "INSERT INTO StoryTimelineScans(ChatSessionId,LastMessageId,PartialMessageId,PartialOffset,PartialContentHash) " +
            "VALUES(@sessionId,@lastCompletedMessageId,@partialMessageId,@partialOffset,@partialContentHash) " +
            "ON CONFLICT(ChatSessionId) DO UPDATE SET " +
            "LastMessageId=excluded.LastMessageId,PartialMessageId=excluded.PartialMessageId," +
            "PartialOffset=excluded.PartialOffset,PartialContentHash=excluded.PartialContentHash",
            new
            {
                sessionId, lastCompletedMessageId = batch.LastCompletedMessageId,
                partialMessageId = batch.PartialMessageId, partialOffset = batch.PartialOffset,
                partialContentHash = batch.PartialContentHash
            }, transaction);
        await transaction.CommitAsync();
        return added;
    }

    private static async Task<bool> SourceBelongsToSessionAsync(
        System.Data.IDbConnection connection, long sessionId, long messageId) =>
        await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ChatMessages WHERE Id=@messageId AND ChatSessionId=@sessionId",
            new { sessionId, messageId }) == 1;

    private static string HashContent(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static object ToParameters(StoryTimelineEvent item) => new
    {
        item.Id, item.ChatSessionId, item.SourceMessageId, item.Title, item.Detail,
        CreatedAt = item.CreatedAt.ToString("O"), UpdatedAt = item.UpdatedAt.ToString("O")
    };

    private sealed class SourceRow
    {
        public long Id { get; init; }
        public string Role { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
    }

    private sealed class ScanRow
    {
        public long LastMessageId { get; init; }
        public long? PartialMessageId { get; init; }
        public int PartialOffset { get; init; }
        public string? PartialContentHash { get; init; }
    }

    private sealed class EventRow
    {
        public long Id { get; init; }
        public long ChatSessionId { get; init; }
        public long? SourceMessageId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public int IsAutoGenerated { get; init; }
        public string CreatedAt { get; init; } = string.Empty;
        public string UpdatedAt { get; init; } = string.Empty;

        public StoryTimelineEvent ToModel() => new()
        {
            Id = Id, ChatSessionId = ChatSessionId, SourceMessageId = SourceMessageId,
            Title = Title, Detail = Detail, IsAutoGenerated = IsAutoGenerated != 0,
            CreatedAt = DateTimeOffset.Parse(CreatedAt), UpdatedAt = DateTimeOffset.Parse(UpdatedAt)
        };
    }
}
