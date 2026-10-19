using Dapper;
using NativeTavern.Models;

namespace NativeTavern.Data.Repositories;

public sealed class ChatMessageRepository(DatabaseConnectionFactory connectionFactory)
{
    public async Task<long> AddAsync(ChatMessage message)
    {
        await using var connection = connectionFactory.CreateConnection();
        message.Id = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO ChatMessages(ChatSessionId,Role,SpeakerCharacterId,Content,CreatedAt,UpdatedAt,CurrentSwipeIndex,IsPinned) " +
            "VALUES(@ChatSessionId,@Role,@SpeakerCharacterId,@Content,@CreatedAt,@UpdatedAt,@CurrentSwipeIndex,@IsPinned); SELECT last_insert_rowid();",
            ToParameters(message));
        return message.Id;
    }

    public async Task UpdateAsync(ChatMessage message)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE ChatMessages SET SpeakerCharacterId=@SpeakerCharacterId,Content=@Content,UpdatedAt=@UpdatedAt," +
            "CurrentSwipeIndex=@CurrentSwipeIndex WHERE Id=@Id",
            ToParameters(message));
    }

    public async Task<IReadOnlyList<ChatMessage>> GetBySessionAsync(long chatSessionId)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<MessageRow>(
            "SELECT * FROM ChatMessages WHERE ChatSessionId=@chatSessionId ORDER BY Id",
            new { chatSessionId });
        return rows.Select(x => x.ToModel()).ToList();
    }

    public async Task<int> CountBySessionAsync(long chatSessionId)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ChatMessages WHERE ChatSessionId=@chatSessionId",
            new { chatSessionId });
    }

    /// <summary>Reads a positional slice for the rolling summary without materializing the whole chat.</summary>
    public async Task<IReadOnlyList<ChatMessage>> GetBySessionRangeAsync(
        long chatSessionId, int offset, int count)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count <= 0) return [];
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<MessageRow>(
            "SELECT * FROM ChatMessages WHERE ChatSessionId=@chatSessionId ORDER BY Id " +
            "LIMIT @count OFFSET @offset",
            new { chatSessionId, offset, count });
        return rows.Select(x => x.ToModel()).ToList();
    }

    /// <summary>Returns the newest messages in conversation order without loading older history.</summary>
    public async Task<IReadOnlyList<ChatMessage>> GetRecentBySessionAsync(long chatSessionId, int limit)
    {
        if (limit <= 0) return [];
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<MessageRow>(
            "SELECT * FROM ChatMessages WHERE ChatSessionId=@chatSessionId ORDER BY Id DESC LIMIT @limit",
            new { chatSessionId, limit });
        return rows.Reverse().Select(x => x.ToModel()).ToList();
    }

    public async Task DeleteBySessionAsync(long chatSessionId)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "DELETE FROM ChatMessages WHERE ChatSessionId=@chatSessionId", new { chatSessionId });
    }

    public async Task DeleteAsync(long id)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync("DELETE FROM ChatMessages WHERE Id=@id", new { id });
    }

    public async Task SetPinnedAsync(long id, bool pinned)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync("UPDATE ChatMessages SET IsPinned=@pinned WHERE Id=@id",
            new { id, pinned = pinned ? 1 : 0 });
    }

    /// <summary>
    /// Case-insensitive substring search, optionally limited to one conversation or pinned messages.
    /// Empty queries browse the latest messages only when a scope or filter is supplied. Wildcards in the
    /// query are escaped so a literal percent or underscore does not widen the match. A leading
    /// "@名字" token narrows the search to what that character said, by name or by alias.
    /// </summary>
    public async Task<IReadOnlyList<MessageSearchResult>> SearchAsync(
        string query, int limit = 50, long? sessionId = null, bool pinnedOnly = false)
    {
        var (term, speaker) = SplitSpeakerFilter(query);
        if (term.Length == 0 && speaker.Length == 0 && sessionId is null && !pinnedOnly) return [];
        if (limit <= 0) return [];
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<SearchRow>(
            "SELECT m.Id AS MessageId, m.ChatSessionId, s.Title AS SessionTitle, m.Role, m.Content, m.CreatedAt, " +
            "COALESCE((SELECT x.Name FROM Characters x WHERE x.Id = " + SpeakerIdExpression + "), '') AS Speaker " +
            "FROM ChatMessages m JOIN ChatSessions s ON s.Id = m.ChatSessionId " +
            @"WHERE (@term='' OR m.Content LIKE @pattern ESCAPE '\') " +
            "AND (@sessionId IS NULL OR m.ChatSessionId=@sessionId) " +
            "AND (@pinnedOnly=0 OR m.IsPinned=1) " +
            @"AND (@speaker='' OR EXISTS (SELECT 1 FROM Characters x WHERE x.Id = " + SpeakerIdExpression + " " +
            @"AND (x.Name = @speaker OR (',' || replace(replace(x.Aliases, '，', ','), ' ', '') || ',') LIKE @speakerPattern ESCAPE '\'))) " +
            "ORDER BY m.Id DESC LIMIT @limit",
            new
            {
                term, speaker, pattern = $"%{EscapeLike(term)}%",
                speakerPattern = $"%,{EscapeLike(speaker)},%", limit, sessionId,
                pinnedOnly = pinnedOnly ? 1 : 0,
            });
        return rows.Select(x => x.ToResult(term)).ToList();
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    /// <summary>The character that actually spoke: user turns have none, group turns carry their own.</summary>
    private const string SpeakerIdExpression =
        "(CASE WHEN m.Role='User' THEN NULL ELSE COALESCE(m.SpeakerCharacterId, s.CharacterId) END)";

    /// <summary>Splits a leading "@名字" speaker filter out of a search query.</summary>
    internal static (string Term, string Speaker) SplitSpeakerFilter(string query)
    {
        var parts = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var speaker = string.Empty;
        for (var index = 0; index < parts.Count; index++)
        {
            if (!parts[index].StartsWith('@') || parts[index].Length < 2) continue;
            speaker = parts[index][1..].Trim();
            parts.RemoveAt(index);
            break;
        }
        return (string.Join(' ', parts), speaker);
    }

    private static object ToParameters(ChatMessage value) => new
    {
        value.Id, value.ChatSessionId, value.SpeakerCharacterId, Role = value.Role.ToString(), value.Content,
        CreatedAt = value.CreatedAt.ToString("O"),
        UpdatedAt = value.UpdatedAt?.ToString("O"), value.CurrentSwipeIndex, IsPinned = value.IsPinned ? 1 : 0
    };

    private sealed class SearchRow
    {
        public long MessageId { get; init; }
        public long ChatSessionId { get; init; }
        public string SessionTitle { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
        public string CreatedAt { get; init; } = string.Empty;
        public string Speaker { get; init; } = string.Empty;

        public MessageSearchResult ToResult(string term) => new()
        {
            MessageId = MessageId,
            ChatSessionId = ChatSessionId,
            SessionTitle = SessionTitle,
            Role = Enum.Parse<ChatRole>(Role, true),
            CreatedAt = DateTimeOffset.Parse(CreatedAt),
            Speaker = Speaker,
            Snippet = BuildSnippet(Content, term),
        };

        private static string BuildSnippet(string content, string term)
        {
            const int context = 48;
            var flat = content.ReplaceLineEndings(" ").Trim();
            var index = term.Length == 0 ? -1 : flat.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return flat.Length <= 120 ? flat : flat[..120] + "…";
            var start = Math.Max(0, index - context);
            var end = Math.Min(flat.Length, index + term.Length + context);
            return (start > 0 ? "…" : string.Empty) + flat[start..end] + (end < flat.Length ? "…" : string.Empty);
        }
    }

    private sealed class MessageRow
    {
        public long Id { get; init; }
        public long ChatSessionId { get; init; }
        public string Role { get; init; } = string.Empty;
        public long? SpeakerCharacterId { get; init; }
        public string Content { get; init; } = string.Empty;
        public string CreatedAt { get; init; } = string.Empty;
        public string? UpdatedAt { get; init; }
        public int CurrentSwipeIndex { get; init; }
        public int IsPinned { get; init; }
        public ChatMessage ToModel() => new()
        {
            Id = Id, ChatSessionId = ChatSessionId,
            Role = Enum.Parse<ChatRole>(Role, true), SpeakerCharacterId = SpeakerCharacterId, Content = Content,
            CreatedAt = DateTimeOffset.Parse(CreatedAt),
            UpdatedAt = string.IsNullOrEmpty(UpdatedAt) ? null : DateTimeOffset.Parse(UpdatedAt),
            CurrentSwipeIndex = CurrentSwipeIndex,
            IsPinned = IsPinned != 0
        };
    }
}
