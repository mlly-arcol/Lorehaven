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
            "INSERT INTO CharacterMemories(CharacterId,Content,CreatedAt,UpdatedAt) " +
            "VALUES(@CharacterId,@Content,@CreatedAt,@UpdatedAt); SELECT last_insert_rowid();",
            new
            {
                memory.CharacterId, memory.Content,
                CreatedAt = memory.CreatedAt.ToString("O"),
                UpdatedAt = memory.UpdatedAt.ToString("O")
            });
        return memory;
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
        public string CreatedAt { get; init; } = string.Empty;
        public string UpdatedAt { get; init; } = string.Empty;

        public CharacterMemory ToModel() => new()
        {
            Id = Id,
            CharacterId = CharacterId,
            Content = Content,
            CreatedAt = DateTimeOffset.Parse(CreatedAt),
            UpdatedAt = DateTimeOffset.Parse(UpdatedAt)
        };
    }
}
