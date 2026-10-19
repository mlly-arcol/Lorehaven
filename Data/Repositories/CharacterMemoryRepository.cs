using Dapper;
using NativeTavern.Models;

namespace NativeTavern.Data.Repositories;

public sealed class CharacterMemoryRepository(DatabaseConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<CharacterMemory>> GetByCharacterAsync(long characterId)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<Row>(
            "SELECT * FROM CharacterMemories WHERE CharacterId=@characterId ORDER BY Id", new { characterId });
        return rows.Select(x => x.ToModel()).ToList();
    }

    public async Task<CharacterMemory> AddAsync(CharacterMemory memory)
    {
        await using var connection = connectionFactory.CreateConnection();
        memory.Id = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO CharacterMemories(CharacterId,Content,IsLocked,CreatedAt,UpdatedAt) " +
            "VALUES(@CharacterId,@Content,@IsLocked,@CreatedAt,@UpdatedAt); SELECT last_insert_rowid();",
            new
            {
                memory.CharacterId, memory.Content, memory.IsLocked,
                CreatedAt = memory.CreatedAt.ToString("O"),
                UpdatedAt = memory.UpdatedAt.ToString("O")
            });
        return memory;
    }

    /// <summary>The capacity and duplicate checks share the write transaction with the manual save.</summary>
    public async Task<CharacterMemory> AddManualWithinCapacityAsync(CharacterMemory memory, int capacity)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var contents = (await connection.QueryAsync<string>(
            "SELECT Content FROM CharacterMemories WHERE CharacterId=@CharacterId",
            new { memory.CharacterId }, transaction)).ToList();
        if (contents.Any(x => string.Equals(x, memory.Content, StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException("这条记忆已经存在。");
        if (contents.Count >= capacity)
            throw new InvalidOperationException($"每个角色最多保存 {capacity} 条记忆，请先删除不再需要的记忆。");

        memory.Id = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO CharacterMemories(CharacterId,Content,IsLocked,CreatedAt,UpdatedAt) " +
            "VALUES(@CharacterId,@Content,@IsLocked,@CreatedAt,@UpdatedAt); SELECT last_insert_rowid();",
            new
            {
                memory.CharacterId, memory.Content, memory.IsLocked,
                CreatedAt = memory.CreatedAt.ToString("O"),
                UpdatedAt = memory.UpdatedAt.ToString("O")
            }, transaction);
        await transaction.CommitAsync();
        return memory;
    }

    public async Task<CharacterMemory?> GetAsync(long characterId, long id)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QuerySingleOrDefaultAsync<Row>(
            "SELECT * FROM CharacterMemories WHERE Id=@id AND CharacterId=@characterId",
            new { characterId, id }))?.ToModel();
    }

    public async Task<CharacterMemory?> GetByIdAsync(long id)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QuerySingleOrDefaultAsync<Row>(
            "SELECT * FROM CharacterMemories WHERE Id=@id", new { id }))?.ToModel();
    }

    public async Task<bool> UpdateAsync(long characterId, long id, string content, bool? isLocked = null)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteAsync(
            "UPDATE CharacterMemories SET Content=@content,IsLocked=COALESCE(@isLocked,IsLocked),UpdatedAt=@updatedAt " +
            "WHERE Id=@id AND CharacterId=@characterId",
            new { characterId, id, content, isLocked, updatedAt = DateTimeOffset.UtcNow.ToString("O") }) > 0;
    }

    public async Task<bool> SetLockedAsync(long characterId, long id, bool isLocked)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteAsync(
            "UPDATE CharacterMemories SET IsLocked=@isLocked,UpdatedAt=@updatedAt " +
            "WHERE Id=@id AND CharacterId=@characterId",
            new { characterId, id, isLocked, updatedAt = DateTimeOffset.UtcNow.ToString("O") }) > 0;
    }

    /// <summary>Capacity changes and insertion share a transaction, so a concurrently locked fact stays pinned.</summary>
    public async Task<bool> AddAutomaticAsync(
        long characterId, string content, int capacity, IReadOnlyList<CharacterMemory>? evictionCandidates = null)
    {
        if (capacity <= 0) return false;
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var rows = (await connection.QueryAsync<Row>(
            "SELECT * FROM CharacterMemories WHERE CharacterId=@characterId ORDER BY UpdatedAt,Id",
            new { characterId }, transaction)).ToList();
        if (rows.Any(x => string.Equals(x.Content, content, StringComparison.CurrentCultureIgnoreCase)))
            return false;

        var removeCount = Math.Max(0, rows.Count - capacity + 1);
        var removable = rows.Where(x => !x.IsLocked && (evictionCandidates is null || evictionCandidates.Any(
                candidate => candidate.Id == x.Id && candidate.Content == x.Content &&
                             candidate.UpdatedAt == DateTimeOffset.Parse(x.UpdatedAt))))
            .Take(removeCount).ToList();
        if (removable.Count < removeCount) return false;
        foreach (var row in removable)
            await connection.ExecuteAsync("DELETE FROM CharacterMemories WHERE Id=@Id AND IsLocked=0",
                new { row.Id }, transaction);

        var now = DateTimeOffset.UtcNow.ToString("O");
        await connection.ExecuteAsync(
            "INSERT INTO CharacterMemories(CharacterId,Content,IsLocked,CreatedAt,UpdatedAt) " +
            "VALUES(@characterId,@content,0,@now,@now)", new { characterId, content, now }, transaction);
        await transaction.CommitAsync();
        return true;
    }

    public async Task DeleteAsync(long id)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync("DELETE FROM CharacterMemories WHERE Id=@id", new { id });
    }

    private sealed class Row
    {
        public long Id { get; init; }
        public long CharacterId { get; init; }
        public string Content { get; init; } = string.Empty;
        public bool IsLocked { get; init; }
        public string CreatedAt { get; init; } = string.Empty;
        public string UpdatedAt { get; init; } = string.Empty;

        public CharacterMemory ToModel() => new()
        {
            Id = Id,
            CharacterId = CharacterId,
            Content = Content,
            IsLocked = IsLocked,
            CreatedAt = DateTimeOffset.Parse(CreatedAt),
            UpdatedAt = DateTimeOffset.Parse(UpdatedAt)
        };
    }
}
