using Dapper;
using NativeTavern.Models;

namespace NativeTavern.Data.Repositories;

public sealed class ChatSessionRepository(DatabaseConnectionFactory connectionFactory)
{
    public async Task<long> CreateAsync(ChatSession session)
    {
        await using var connection = connectionFactory.CreateConnection();
        session.Id = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO ChatSessions(Title,CharacterId,IsGroupChat,ParentSessionId,BranchedFromMessageId,PersonaId,LorebookId,PromptPresetId,AuthorNote,Summary,SummaryCoveredCount,SummaryIsManual,MemoryCoveredCount,GroupName,IsPinned,CreatedAt,UpdatedAt) " +
            "VALUES(@Title,@CharacterId,@IsGroupChat,@ParentSessionId,@BranchedFromMessageId,@PersonaId,@LorebookId,@PromptPresetId,@AuthorNote,@Summary,@SummaryCoveredCount,@SummaryIsManual,@MemoryCoveredCount,@GroupName,@IsPinned,@CreatedAt,@UpdatedAt); SELECT last_insert_rowid();",
            ToParameters(session));
        return session.Id;
    }

    public async Task<ChatSession?> GetAsync(long id)
    {
        await using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<SessionRow>(
            "SELECT * FROM ChatSessions WHERE Id=@id", new { id });
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<ChatSession>> GetAllAsync()
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<SessionRow>(
            "SELECT * FROM ChatSessions ORDER BY IsPinned DESC, UpdatedAt DESC");
        return rows.Select(x => x.ToModel()).ToList();
    }

    public async Task<ChatSession?> FindEmptyOrdinaryAsync()
    {
        await using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<SessionRow>(
            "SELECT s.* FROM ChatSessions s " +
            "WHERE s.CharacterId IS NULL AND s.IsGroupChat=0 AND s.ParentSessionId IS NULL " +
            "AND NOT EXISTS (SELECT 1 FROM ChatMessages m WHERE m.ChatSessionId=s.Id) " +
            "ORDER BY s.UpdatedAt DESC, s.Id DESC LIMIT 1");
        return row?.ToModel();
    }

    public async Task UpdateAsync(ChatSession session)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE ChatSessions SET Title=@Title,CharacterId=@CharacterId,IsGroupChat=@IsGroupChat,ParentSessionId=@ParentSessionId,BranchedFromMessageId=@BranchedFromMessageId,PersonaId=@PersonaId,LorebookId=@LorebookId," +
            "PromptPresetId=@PromptPresetId,AuthorNote=@AuthorNote,Summary=@Summary,SummaryCoveredCount=@SummaryCoveredCount,SummaryIsManual=@SummaryIsManual,UpdatedAt=@UpdatedAt WHERE Id=@Id",
            ToParameters(session));
    }

    /// <summary>Only the flag moves, so pinning never rewrites the conversation's UpdatedAt stamp.</summary>
    public async Task SetPinnedAsync(long id, bool pinned)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE ChatSessions SET IsPinned=@pinned WHERE Id=@id", new { id, pinned = pinned ? 1 : 0 });
    }

    public async Task SetGroupAsync(long id, string groupName)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE ChatSessions SET GroupName=@groupName WHERE Id=@id", new { id, groupName });
    }

    /// <summary>Only the scan watermark moves, so memory extraction never rewrites UpdatedAt.</summary>
    public async Task SetMemoryCoveredAsync(long id, int coveredCount)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE ChatSessions SET MemoryCoveredCount=@coveredCount WHERE Id=@id", new { id, coveredCount });
    }

    public async Task<IReadOnlyList<string>> GetGroupNamesAsync()
    {
        await using var connection = connectionFactory.CreateConnection();
        var names = await connection.QueryAsync<string>(
            "SELECT DISTINCT TRIM(GroupName) FROM ChatSessions WHERE TRIM(GroupName)<>'' ORDER BY TRIM(GroupName)");
        return names.ToList();
    }

    public async Task DeleteAsync(long id)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync("DELETE FROM ChatSessions WHERE Id=@id", new { id });
    }

    public async Task<IReadOnlyList<long>> GetCharacterIdsAsync(long sessionId)
    {
        await using var connection = connectionFactory.CreateConnection();
        var ids = await connection.QueryAsync<long>(
            "SELECT CharacterId FROM ChatSessionCharacters WHERE ChatSessionId=@sessionId ORDER BY SortOrder,CharacterId",
            new { sessionId });
        return ids.ToList();
    }

    public async Task SetCharactersAsync(long sessionId, IReadOnlyCollection<long> characterIds)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM ChatSessionCharacters WHERE ChatSessionId=@sessionId",
            new { sessionId }, transaction);
        var order = 0;
        foreach (var characterId in characterIds.Distinct())
            await connection.ExecuteAsync(
                "INSERT INTO ChatSessionCharacters(ChatSessionId,CharacterId,SortOrder) VALUES(@sessionId,@characterId,@order)",
                new { sessionId, characterId, order = order++ }, transaction);
        await transaction.CommitAsync();
    }

    private static object ToParameters(ChatSession value) => new
    {
        value.Id, value.Title, value.CharacterId, IsGroupChat = value.IsGroupChat ? 1 : 0,
        value.ParentSessionId, value.BranchedFromMessageId, value.PersonaId, value.LorebookId,
        value.PromptPresetId, value.AuthorNote, value.Summary, value.SummaryCoveredCount,
        SummaryIsManual = value.SummaryIsManual ? 1 : 0, value.MemoryCoveredCount,
        value.GroupName, IsPinned = value.IsPinned ? 1 : 0,
        CreatedAt = value.CreatedAt.ToString("O"),
        UpdatedAt = value.UpdatedAt.ToString("O")
    };

    private sealed class SessionRow
    {
        public long Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public long? CharacterId { get; init; }
        public int IsGroupChat { get; init; }
        public long? ParentSessionId { get; init; }
        public long? BranchedFromMessageId { get; init; }
        public long? PersonaId { get; init; }
        public long? LorebookId { get; init; }
        public long? PromptPresetId { get; init; }
        public string AuthorNote { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public int SummaryCoveredCount { get; init; }
        public int SummaryIsManual { get; init; }
        public int MemoryCoveredCount { get; init; }
        public string GroupName { get; init; } = string.Empty;
        public int IsPinned { get; init; }
        public string CreatedAt { get; init; } = string.Empty;
        public string UpdatedAt { get; init; } = string.Empty;
        public ChatSession ToModel() => new()
        {
            Id = Id, Title = Title, CharacterId = CharacterId, IsGroupChat = IsGroupChat != 0,
            ParentSessionId = ParentSessionId, BranchedFromMessageId = BranchedFromMessageId, PersonaId = PersonaId,
            LorebookId = LorebookId, PromptPresetId = PromptPresetId, AuthorNote = AuthorNote, Summary = Summary,
            SummaryCoveredCount = SummaryCoveredCount, SummaryIsManual = SummaryIsManual != 0,
            MemoryCoveredCount = MemoryCoveredCount,
            GroupName = GroupName, IsPinned = IsPinned != 0,
            CreatedAt = DateTimeOffset.Parse(CreatedAt),
            UpdatedAt = DateTimeOffset.Parse(UpdatedAt)
        };
    }
}
