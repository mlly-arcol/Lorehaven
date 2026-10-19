using System.Text.Json.Serialization;

namespace NativeTavern.Models;

public enum DiceRandomKind
{
    Dice,
    Table
}

public sealed record RandomTableEntry(string Text, int Weight);

public sealed record RandomTableDefinition(
    string Id,
    string PluginId,
    string LocalId,
    string Name,
    string Description,
    IReadOnlyList<RandomTableEntry> Entries);

public sealed record DiceTableValidationIssue(string PluginId, string Message);

public sealed record DiceTableCatalog(
    IReadOnlyList<RandomTableDefinition> Tables,
    IReadOnlyList<DiceTableValidationIssue> Issues);

/// <summary>
/// A local random outcome. The chat must explicitly set IncludeInModelContext before
/// ModelContextText can be used in a model request.
/// </summary>
public sealed record DiceRandomResult
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DiceRandomKind Kind { get; init; }
    public string? PluginId { get; init; }
    public string SourceLabel { get; init; } = string.Empty;
    public string ResultText { get; init; } = string.Empty;
    public string DetailText { get; init; } = string.Empty;
    public bool IncludeInModelContext { get; init; }

    [JsonIgnore]
    public string? ModelContextText => IncludeInModelContext
        ? $"{SourceLabel}：{ResultText}"
        : null;
}
