using Dapper;
using NativeTavern.Models;

namespace NativeTavern.Data.Repositories;

public sealed class PluginRandomResultRepository(DatabaseConnectionFactory connectionFactory)
{
    public async Task SaveAsync(long sessionId, DiceRandomResult result)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "INSERT INTO PluginRandomResults(Id,ChatSessionId,PluginId,Kind,SourceLabel,ResultText,DetailText,CreatedAt) " +
            "VALUES(@Id,@sessionId,@PluginId,@Kind,@SourceLabel,@ResultText,@DetailText,@CreatedAt)",
            new
            {
                result.Id, sessionId, result.PluginId, Kind = result.Kind.ToString(),
                result.SourceLabel, result.ResultText, result.DetailText,
                CreatedAt = result.CreatedAtUtc.ToString("O")
            });
    }

    public async Task<IReadOnlyList<DiceRandomResult>> GetRecentAsync(long sessionId, int limit = 30)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<ResultRow>(
            "SELECT * FROM PluginRandomResults WHERE ChatSessionId=@sessionId ORDER BY CreatedAt DESC, rowid DESC LIMIT @limit",
            new { sessionId, limit = Math.Clamp(limit, 1, 100) });
        return rows.Select(x => new DiceRandomResult
        {
            Id = x.Id,
            PluginId = x.PluginId,
            Kind = Enum.Parse<DiceRandomKind>(x.Kind, true),
            SourceLabel = x.SourceLabel,
            ResultText = x.ResultText,
            DetailText = x.DetailText,
            CreatedAtUtc = DateTimeOffset.Parse(x.CreatedAt)
        }).ToArray();
    }

    private sealed class ResultRow
    {
        public string Id { get; init; } = string.Empty;
        public string? PluginId { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string SourceLabel { get; init; } = string.Empty;
        public string ResultText { get; init; } = string.Empty;
        public string DetailText { get; init; } = string.Empty;
        public string CreatedAt { get; init; } = string.Empty;
    }
}
