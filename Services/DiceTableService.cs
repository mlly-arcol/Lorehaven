using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NativeTavern.Helpers;
using NativeTavern.Models;

namespace NativeTavern.Services;

/// <summary>
/// Handles the declaration-only dice and weighted table capability. Plugin files
/// are treated as data; this service does not run plugin code or call a model.
/// </summary>
public sealed partial class DiceTableService
{
    public const string Capability = "dice-random-tables-v1";
    public const string TablesFileName = "random-tables.json";

    private const long MaxTablesFileBytes = 256 * 1024;
    private const int MaxTablesPerPlugin = 20;
    private const int MaxEntriesPerTable = 100;
    private const int MaxEntryWeight = 10_000;
    private const int MaxTotalWeight = 1_000_000;
    private const int MaxDiceCount = 100;
    private const int MaxDiceSides = 1_000;
    private const int MaxDiceModifier = 100_000;
    private static readonly JsonSerializerOptions TableJsonOptions = new(JsonDefaults.Options)
    {
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly PluginService plugins;
    private readonly Random random;
    private readonly Lock randomGate = new();

    public DiceTableService(PluginService plugins, Random? random = null)
    {
        this.plugins = plugins;
        this.random = random ?? Random.Shared;
    }

    public static async Task ValidatePackageDirectoryAsync(
        PluginManifest manifest, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Capabilities?.Contains(Capability, StringComparer.OrdinalIgnoreCase) != true) return;
        _ = await ReadTablesAsync(manifest, directory, cancellationToken);
    }

    public async Task<DiceTableCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var tables = new List<RandomTableDefinition>();
        var issues = new List<DiceTableValidationIssue>();
        foreach (var plugin in await plugins.GetInstalledAsync(validateContents: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!plugin.IsEnabled ||
                !plugin.Manifest.Capabilities.Contains(Capability, StringComparer.OrdinalIgnoreCase))
                continue;

            if (plugin.ValidationError is not null)
            {
                issues.Add(new DiceTableValidationIssue(plugin.Id, plugin.ValidationError));
                continue;
            }

            try
            {
                tables.AddRange(await ReadTablesAsync(plugin.Manifest, plugin.DirectoryPath, cancellationToken));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
            {
                issues.Add(new DiceTableValidationIssue(plugin.Id, ex.Message));
            }
        }
        return new DiceTableCatalog(tables, issues);
    }

    public async Task<IReadOnlyList<RandomTableDefinition>> GetTablesAsync(
        CancellationToken cancellationToken = default) =>
        (await GetCatalogAsync(cancellationToken)).Tables;

    public DiceRandomResult RollDice(string notation)
    {
        if (string.IsNullOrWhiteSpace(notation))
            throw new ArgumentException("请输入骰子表达式，例如 2d6+1。", nameof(notation));

        var input = notation.Trim();
        var match = DicePattern().Match(input);
        if (!match.Success)
            throw new ArgumentException("骰子表达式应为 NdS±M，例如 d20 或 2d6+1。", nameof(notation));

        var count = match.Groups["count"].Success
            ? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture)
            : 1;
        var sides = int.Parse(match.Groups["sides"].Value, CultureInfo.InvariantCulture);
        var modifier = match.Groups["modifier"].Success
            ? int.Parse(match.Groups["modifier"].Value, CultureInfo.InvariantCulture)
            : 0;
        if (count is < 1 or > MaxDiceCount || sides is < 1 or > MaxDiceSides ||
            modifier is < -MaxDiceModifier or > MaxDiceModifier)
            throw new ArgumentOutOfRangeException(nameof(notation),
                "一次最多掷 100 个骰子，每个骰子最多 1000 面，修正值不能超过 ±100000。");

        var values = new int[count];
        lock (randomGate)
        {
            for (var i = 0; i < values.Length; i++)
                values[i] = random.Next(1, sides + 1);
        }
        var total = values.Sum() + modifier;
        var canonical = $"{count}d{sides}" + (modifier switch
        {
            > 0 => $"+{modifier}",
            < 0 => modifier.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty
        });
        var rolls = string.Join(" + ", values.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        var detail = modifier switch
        {
            > 0 => $"{rolls} + {modifier} = {total}",
            < 0 => $"{rolls} - {-modifier} = {total}",
            _ => $"{rolls} = {total}"
        };
        return new DiceRandomResult
        {
            Kind = DiceRandomKind.Dice,
            SourceLabel = canonical,
            ResultText = total.ToString(CultureInfo.InvariantCulture),
            DetailText = detail
        };
    }

    public async Task<DiceRandomResult> DrawFromTableAsync(
        string qualifiedTableId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedTableId);
        var table = (await GetTablesAsync(cancellationToken))
            .FirstOrDefault(x => string.Equals(x.Id, qualifiedTableId, StringComparison.Ordinal));
        if (table is null)
            throw new KeyNotFoundException("找不到已启用的随机表。");

        var totalWeight = table.Entries.Sum(x => x.Weight);
        int selected;
        lock (randomGate) selected = random.Next(totalWeight);
        foreach (var entry in table.Entries)
        {
            selected -= entry.Weight;
            if (selected >= 0) continue;
            return new DiceRandomResult
            {
                Kind = DiceRandomKind.Table,
                PluginId = table.PluginId,
                SourceLabel = table.Name,
                ResultText = entry.Text,
                DetailText = $"{table.Name}：{entry.Text}"
            };
        }
        throw new InvalidOperationException("随机表权重计算失败。");
    }

    private static async Task<IReadOnlyList<RandomTableDefinition>> ReadTablesAsync(
        PluginManifest manifest, string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, TablesFileName);
        if (!File.Exists(path)) throw new InvalidDataException($"插件缺少 {TablesFileName}。");
        var file = new FileInfo(path);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("随机表文件不能是链接。");
        if (file.Length > MaxTablesFileBytes)
            throw new InvalidDataException("随机表文件不能超过 256 KB。");

        RandomTableFile? data;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            data = await JsonSerializer.DeserializeAsync<RandomTableFile>(
                stream, TableJsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("随机表 JSON 格式无效。", ex);
        }
        if (data?.FormatVersion != 1)
            throw new InvalidDataException("不支持此随机表格式版本。");
        if (data.Tables is null || data.Tables.Count is < 1 or > MaxTablesPerPlugin)
            throw new InvalidDataException("插件须包含 1–20 张随机表。");

        var tables = new List<RandomTableDefinition>(data.Tables.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in data.Tables)
        {
            if (table is null || string.IsNullOrWhiteSpace(table.Id) || !TableIdPattern().IsMatch(table.Id))
                throw new InvalidDataException("随机表 ID 无效。");
            if (!ids.Add(table.Id)) throw new InvalidDataException("随机表 ID 重复。");
            if (string.IsNullOrWhiteSpace(table.Name) || table.Name.Length > 80)
                throw new InvalidDataException("随机表名称不能为空且不能超过 80 个字符。");
            if (table.Description?.Length > 500)
                throw new InvalidDataException("随机表说明不能超过 500 个字符。");
            if (table.Entries is null || table.Entries.Count is < 1 or > MaxEntriesPerTable)
                throw new InvalidDataException("每张随机表须包含 1–100 个结果。");

            var entries = new List<RandomTableEntry>(table.Entries.Count);
            var totalWeight = 0;
            foreach (var entry in table.Entries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Text) || entry.Text.Length > 500)
                    throw new InvalidDataException("随机表结果不能为空且不能超过 500 个字符。");
                if (entry.Weight is < 1 or > MaxEntryWeight)
                    throw new InvalidDataException("随机表权重须在 1–10000 之间。");
                totalWeight += entry.Weight;
                if (totalWeight > MaxTotalWeight)
                    throw new InvalidDataException("随机表总权重不能超过 1000000。");
                entries.Add(new RandomTableEntry(entry.Text.Trim(), entry.Weight));
            }
            tables.Add(new RandomTableDefinition(
                $"{manifest.Id}/{table.Id}", manifest.Id, table.Id,
                table.Name.Trim(), table.Description?.Trim() ?? string.Empty, entries));
        }
        return tables;
    }

    [GeneratedRegex(@"^(?<count>[1-9][0-9]{0,2})?[dD](?<sides>[1-9][0-9]{0,3})(?<modifier>[+-][0-9]{1,6})?$", RegexOptions.CultureInvariant)]
    private static partial Regex DicePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TableIdPattern();

    private sealed class RandomTableFile
    {
        public RandomTableFile() { }
        public int FormatVersion { get; set; }
        public List<RandomTableData?>? Tables { get; set; }
    }

    private sealed class RandomTableData
    {
        public RandomTableData() { }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<RandomTableEntryData?>? Entries { get; set; }
    }

    private sealed class RandomTableEntryData
    {
        public RandomTableEntryData() { }
        public string? Text { get; set; }
        public int Weight { get; set; }
    }
}
