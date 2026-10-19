using Dapper;

namespace NativeTavern.Data.Repositories;

public sealed class PluginResourceSelectionRepository(DatabaseConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<string>> GetAsync(long sessionId)
    {
        await using var connection = connectionFactory.CreateConnection();
        var values = await connection.QueryAsync<string>(
            "SELECT QualifiedResourceId FROM PluginResourceSelections WHERE ChatSessionId=@sessionId " +
            "ORDER BY QualifiedResourceId", new { sessionId });
        return values.ToArray();
    }

    public async Task SetAsync(long sessionId, IReadOnlyCollection<string> qualifiedResourceIds)
    {
        var ids = qualifiedResourceIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length > 64 || ids.Any(x => x.Length is < 3 or > 160 || !x.Contains('/')))
            throw new InvalidDataException("插件资源选择无效。");
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM PluginResourceSelections WHERE ChatSessionId=@sessionId",
            new { sessionId }, transaction);
        foreach (var id in ids)
            await connection.ExecuteAsync(
                "INSERT INTO PluginResourceSelections(ChatSessionId,QualifiedResourceId) VALUES(@sessionId,@id)",
                new { sessionId, id }, transaction);
        await transaction.CommitAsync();
    }
}
