using Dapper;
using NativeTavern.Models;

namespace NativeTavern.Data.Repositories;

public sealed class ChatAttachmentRepository(DatabaseConnectionFactory connectionFactory)
{
    public async Task AddAsync(ChatAttachment item)
    {
        await using var connection = connectionFactory.CreateConnection();
        item.Id = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO ChatAttachments(ChatMessageId,FileName,FilePath,MimeType,SizeBytes,CreatedAt) VALUES(@ChatMessageId,@FileName,@FilePath,@MimeType,@SizeBytes,@CreatedAt); SELECT last_insert_rowid();",
            new { item.ChatMessageId, item.FileName, item.FilePath, item.MimeType, item.SizeBytes, CreatedAt=item.CreatedAt.ToString("O") });
    }

    public async Task<IReadOnlyList<ChatAttachment>> GetByMessageAsync(long chatMessageId)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<Row>("SELECT * FROM ChatAttachments WHERE ChatMessageId=@chatMessageId ORDER BY Id", new { chatMessageId });
        return rows.Select(x => new ChatAttachment { Id=x.Id, ChatMessageId=x.ChatMessageId, FileName=x.FileName, FilePath=x.FilePath, MimeType=x.MimeType, SizeBytes=x.SizeBytes, CreatedAt=DateTimeOffset.Parse(x.CreatedAt) }).ToList();
    }

    /// <summary>Reads only the attachments referenced by a prompt in one query per SQLite parameter batch.</summary>
    public async Task<IReadOnlyList<ChatAttachment>> GetByMessageIdsAsync(IEnumerable<long> messageIds)
    {
        var ids = messageIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        const int batchSize = 500;
        var attachments = new List<ChatAttachment>();
        await using var connection = connectionFactory.CreateConnection();
        foreach (var batch in ids.Chunk(batchSize))
        {
            var rows = await connection.QueryAsync<Row>(
                "SELECT * FROM ChatAttachments WHERE ChatMessageId IN @batch ORDER BY Id", new { batch });
            attachments.AddRange(rows.Select(x => new ChatAttachment
            {
                Id = x.Id, ChatMessageId = x.ChatMessageId, FileName = x.FileName,
                FilePath = x.FilePath, MimeType = x.MimeType, SizeBytes = x.SizeBytes,
                CreatedAt = DateTimeOffset.Parse(x.CreatedAt)
            }));
        }
        return attachments.OrderBy(x => x.Id).ToList();
    }

    public async Task<IReadOnlyList<ChatAttachment>> GetBySessionAsync(long chatSessionId)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<Row>(
            "SELECT a.* FROM ChatAttachments a INNER JOIN ChatMessages m ON m.Id=a.ChatMessageId " +
            "WHERE m.ChatSessionId=@chatSessionId ORDER BY a.Id",
            new { chatSessionId });
        return rows.Select(x => new ChatAttachment { Id=x.Id, ChatMessageId=x.ChatMessageId, FileName=x.FileName, FilePath=x.FilePath, MimeType=x.MimeType, SizeBytes=x.SizeBytes, CreatedAt=DateTimeOffset.Parse(x.CreatedAt) }).ToList();
    }

    private sealed class Row { public long Id { get; init; } public long ChatMessageId { get; init; } public string FileName { get; init; } = ""; public string FilePath { get; init; } = ""; public string MimeType { get; init; } = ""; public long SizeBytes { get; init; } public string CreatedAt { get; init; } = ""; }
}
