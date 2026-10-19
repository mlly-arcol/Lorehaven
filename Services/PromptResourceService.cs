using System.Text.Json;
using NativeTavern.Data.Repositories;
using NativeTavern.Helpers;
using NativeTavern.Models;

namespace NativeTavern.Services;

/// Moves personas, lorebooks, prompt presets and regex scripts in and out of the database as JSON.
public sealed class PromptResourceService(
    PromptRepository promptRepository,
    RegexScriptRepository scriptRepository,
    RegexScriptService regexScriptService)
{
    // The file-format identifier remains stable across the Lorehaven product rename.
    public const string PackFormat = "nativetavern_prompt_pack";
    public const int PackVersion = 1;
    private const long MaxImportFileSize = 20 * 1024 * 1024;
    private const int MaxResources = 5000;

    public sealed record PersonaResource(string Name, string Content);

    public sealed record LoreEntryResource(
        string Name,
        string Keywords,
        string SecondaryKeywords,
        string Content,
        int Priority,
        int Depth,
        bool IsEnabled,
        bool IsConstant,
        bool IsSelective);

    public sealed record LorebookResource(
        string Name,
        string Description,
        bool IsEnabled,
        IReadOnlyList<LoreEntryResource> Entries);

    public sealed record PresetResource(
        string Name,
        string SystemPrompt,
        string MainPrompt,
        string Model,
        double? Temperature,
        double? TopP,
        int? MaxTokens);

    public sealed record ScriptResource(
        string Name,
        string Pattern,
        string Replacement,
        string Target,
        string Mode,
        bool IsEnabled,
        int SortOrder);

    public sealed record PromptPack(
        string Format,
        int Version,
        DateTimeOffset ExportedAt,
        IReadOnlyList<PersonaResource> Personas,
        IReadOnlyList<LorebookResource> Lorebooks,
        IReadOnlyList<PresetResource> Presets,
        IReadOnlyList<ScriptResource> RegexScripts);

    public sealed record ImportReport(
        int Personas,
        int Lorebooks,
        int LoreEntries,
        int Presets,
        int Scripts,
        IReadOnlyList<string> Warnings)
    {
        public bool IsEmpty => Personas + Lorebooks + Presets + Scripts == 0;
    }

    public async Task<PromptPack> BuildPackAsync()
    {
        var lorebooks = new List<LorebookResource>();
        foreach (var lorebook in await promptRepository.GetLorebooksAsync())
        {
            var entries = await promptRepository.GetLoreEntriesAsync(lorebook.Id);
            lorebooks.Add(new LorebookResource(
                lorebook.Name,
                lorebook.Description,
                lorebook.IsEnabled,
                entries.Select(x => new LoreEntryResource(
                    x.Name, x.Keywords, x.SecondaryKeywords, x.Content,
                    x.Priority, x.Depth, x.IsEnabled, x.IsConstant, x.IsSelective)).ToArray()));
        }

        var scripts = await scriptRepository.GetAllAsync();
        return new PromptPack(
            PackFormat,
            PackVersion,
            DateTimeOffset.UtcNow,
            (await promptRepository.GetPersonasAsync())
                .Select(x => new PersonaResource(x.Name, x.Content)).ToArray(),
            lorebooks,
            (await promptRepository.GetPresetsAsync()).Select(x => new PresetResource(
                x.Name, x.SystemPrompt, x.MainPrompt, x.Model,
                x.Temperature, x.TopP, x.MaxTokens)).ToArray(),
            scripts.Select(x => new ScriptResource(
                x.Name, x.Pattern, x.Replacement, x.Target.ToString(), x.Mode.ToString(),
                x.IsEnabled, x.SortOrder)).ToArray());
    }

    public async Task ExportAllAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("资源包必须使用 .json 扩展名。");
        var json = JsonSerializer.Serialize(await BuildPackAsync(), JsonDefaults.Options);
        await DataExportService.WriteAllTextAtomicAsync(path, json, cancellationToken);
    }

    public async Task<ImportReport> ImportFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到资源文件。", path);
        if (new FileInfo(path).Length > MaxImportFileSize)
            throw new InvalidDataException("资源文件不能超过 20 MB。");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        return await ImportAsync(document.RootElement);
    }

    public async Task<ImportReport> ImportAsync(JsonElement root) => root.ValueKind switch
    {
        JsonValueKind.Object when ReadString(root, "format") == PackFormat => await ImportPackAsync(root),
        JsonValueKind.Object => await ImportForeignAsync(root),
        _ => throw new InvalidDataException("无法识别的资源文件格式。"),
    };

    private async Task<ImportReport> ImportPackAsync(JsonElement root)
    {
        var warnings = new List<string>();
        var personas = await AddPersonasAsync(GetArray(root, "personas"), warnings);
        var (lorebooks, entries) = await AddLorebooksAsync(GetArray(root, "lorebooks"), warnings);
        var presets = await AddPresetsAsync(GetArray(root, "presets"), warnings);
        var scripts = await AddScriptsAsync(GetArray(root, "regexScripts"), warnings);
        return new ImportReport(personas, lorebooks, entries, presets, scripts, warnings);
    }

    /// Accepts SillyTavern world-info books and the book embedded inside a character card.
    private async Task<ImportReport> ImportForeignAsync(JsonElement source)
    {
        // Character cards keep the book under "data", so unwrap it before looking for entries.
        var root = TryGetProperty(source, "data", out var cardData) && cardData.ValueKind == JsonValueKind.Object
            ? cardData
            : source;
        var warnings = new List<string>();
        var books = new List<(string Name, string Description, JsonElement Book)>();
        if (root.TryGetProperty("character_book", out var embedded) &&
            embedded.ValueKind == JsonValueKind.Object)
            books.Add((ReadString(root, "name"), ReadString(root, "description"), embedded));
        if (root.TryGetProperty("world_names", out var worldNames) &&
            worldNames.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in worldNames.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Object)
                    books.Add((property.Name, ReadString(property.Value, "description"), property.Value));
        }
        if (books.Count == 0 && LooksLikeBook(root))
            books.Add((ReadString(root, "name"), ReadString(root, "description"), root));
        if (books.Count == 0) throw new InvalidDataException("无法识别的资源文件格式。");

        var lorebooks = 0;
        var loreEntries = 0;
        foreach (var (name, description, book) in books)
        {
            var entries = ReadForeignEntries(book, warnings);
            if (entries.Count == 0) continue;
            lorebooks++;
            loreEntries += entries.Count;
            await SaveLorebookAsync(DefaultName(name, "Imported world info"), description, entries);
        }

        if (lorebooks == 0) throw new InvalidDataException("文件中没有可用的世界书条目。");
        return new ImportReport(0, lorebooks, loreEntries, 0, 0, warnings);
    }

    private static bool LooksLikeBook(JsonElement root) =>
        root.TryGetProperty("entries", out var entries) &&
        entries.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

    private static List<LoreEntryResource> ReadForeignEntries(JsonElement book, List<string> warnings)
    {
        var result = new List<LoreEntryResource>();
        if (!book.TryGetProperty("entries", out var entries)) return result;
        var index = 0;
        foreach (var element in Enumerate(entries))
        {
            if (++index > MaxResources)
            {
                warnings.Add("Too many entries in one book; the rest were skipped.");
                break;
            }

            if (element.ValueKind != JsonValueKind.Object) continue;
            var content = FirstString(element, "content", "text", "entry");
            if (string.IsNullOrWhiteSpace(content)) continue;
            var name = FirstString(element, "comment", "name", "title", "key");
            result.Add(new LoreEntryResource(
                DefaultName(name, $"Entry {result.Count + 1}"),
                JoinKeywords(element, "keys", "keyword"),
                JoinKeywords(element, "secondary_keys", "additional_keywords"),
                content.Trim(),
                ReadInt(element, 100, "priority", "insertion_order"),
                Math.Max(1, ReadInt(element, 4, "depth")),
                ReadBool(element, true, "enabled"),
                ReadBool(element, false, "constant"),
                ReadBool(element, false, "selective")));
        }
        return result;
    }

    private static string JoinKeywords(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
            if (value.ValueKind == JsonValueKind.Array)
                return string.Join(", ", value.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        return string.Empty;
    }

    private async Task<int> AddPersonasAsync(IReadOnlyList<JsonElement> items, List<string> warnings)
    {
        var taken = ExistingNames((await promptRepository.GetPersonasAsync()).Select(x => x.Name));
        var added = 0;
        foreach (var element in items)
        {
            var name = ReadString(element, "name");
            var content = ReadString(element, "content");            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
            {
                warnings.Add("A persona without a name or content was skipped.");
                continue;
            }
            await promptRepository.SavePersonaAsync(new Persona
            {
                Name = UniqueName(name.Trim(), taken),
                Content = content.Trim(),
            });
            added++;
        }
        return added;
    }

    private async Task<(int Lorebooks, int Entries)> AddLorebooksAsync(IReadOnlyList<JsonElement> items, List<string> warnings)
    {
        var taken = ExistingNames((await promptRepository.GetLorebooksAsync()).Select(x => x.Name));
        var books = 0;
        var entries = 0;
        foreach (var element in items)
        {
            var parsed = new List<LoreEntryResource>();
            foreach (var entry in GetArray(element, "entries"))
            {
                var content = ReadString(entry, "content");
                if (string.IsNullOrWhiteSpace(content))
                {
                    warnings.Add("A lore entry without content was skipped.");
                    continue;
                }
                parsed.Add(new LoreEntryResource(
                    DefaultName(ReadString(entry, "name"), "Unnamed entry"),
                    ReadString(entry, "keywords"),
                    ReadString(entry, "secondaryKeywords"),
                    content.Trim(),
                    ReadInt(entry, 100, "priority"),
                    Math.Max(1, ReadInt(entry, 4, "depth")),
                    ReadBool(entry, true, "isEnabled"),
                    ReadBool(entry, false, "isConstant"),
                    ReadBool(entry, false, "isSelective")));
            }
            var name = DefaultName(ReadString(element, "name"), "Imported world info");
            var bookName = UniqueName(name, taken);
            await SaveLorebookAsync(
                bookName, ReadString(element, "description"), parsed, ReadBool(element, true, "isEnabled"));
            books++;
            entries += parsed.Count;
        }
        return (books, entries);
    }

    private async Task SaveLorebookAsync(
        string name, string description, IReadOnlyList<LoreEntryResource> entries, bool isEnabled = true)
    {
        var book = new Lorebook { Name = name, Description = description.Trim(), IsEnabled = isEnabled };
        await promptRepository.SaveLorebookAsync(book);
        foreach (var entry in entries)
        {
            await promptRepository.SaveLoreEntryAsync(new LoreEntry
            {
                LorebookId = book.Id,
                Name = entry.Name,
                Keywords = entry.Keywords,
                SecondaryKeywords = entry.SecondaryKeywords,
                Content = entry.Content,
                Priority = entry.Priority,
                Depth = Math.Max(1, entry.Depth),
                IsEnabled = entry.IsEnabled,
                IsConstant = entry.IsConstant,
                IsSelective = entry.IsSelective,
            });
        }
    }

    private async Task<int> AddPresetsAsync(IReadOnlyList<JsonElement> items, List<string> warnings)
    {
        var taken = ExistingNames((await promptRepository.GetPresetsAsync()).Select(x => x.Name));
        var added = 0;
        foreach (var element in items)
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var name = ReadString(element, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                warnings.Add("A preset without a name was skipped.");
                continue;
            }
            await promptRepository.SavePresetAsync(new PromptPreset
            {
                Name = UniqueName(name.Trim(), taken),
                SystemPrompt = ReadString(element, "systemPrompt"),
                MainPrompt = ReadString(element, "mainPrompt"),
                Model = ReadString(element, "model"),
                Temperature = ReadNullableDouble(element, "temperature"),
                TopP = ReadNullableDouble(element, "topP"),
                MaxTokens = ReadNullableInt(element, "maxTokens"),
            });
            added++;
        }
        return added;
    }

    private async Task<int> AddScriptsAsync(IReadOnlyList<JsonElement> items, List<string> warnings)
    {
        var existing = await scriptRepository.GetAllAsync();
        var taken = ExistingNames(existing.Select(x => x.Name));
        var added = 0;
        var sortOrder = existing.Count;
        foreach (var element in items)
        {
            var name = ReadString(element, "name");
            var pattern = ReadString(element, "pattern");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(pattern))
            {
                warnings.Add("A script without a name or pattern was skipped.");
                continue;
            }
            var mode = Enum.TryParse<RegexScriptMode>(ReadString(element, "mode"), true, out var parsedMode)
                ? parsedMode : RegexScriptMode.Regex;
            var error = RegexScriptService.ValidatePattern(mode, pattern);
            if (error is not null)
            {
                warnings.Add($"Script “{name.Trim()}” was skipped: {error}");
                continue;
            }
            await regexScriptService.SaveAsync(new RegexScript
            {
                Name = UniqueName(name.Trim(), taken),
                Pattern = pattern,
                Replacement = ReadString(element, "replacement"),
                Target = Enum.TryParse<RegexScriptTarget>(ReadString(element, "target"), true, out var target)
                    ? target : RegexScriptTarget.AssistantOutput,
                Mode = mode,
                IsEnabled = ReadBool(element, true, "isEnabled"),
                SortOrder = ++sortOrder,
            });
            added++;
        }
        return added;
    }

    private static HashSet<string> ExistingNames(IEnumerable<string> names) =>
        new(names.Select(x => x.Trim().ToLowerInvariant()));

    private static string UniqueName(string name, HashSet<string> taken)
    {
        var candidate = name;
        var suffix = 2;
        while (!taken.Add(candidate.ToLowerInvariant()))
            candidate = $"{name} ({suffix++})";
        return candidate;
    }

    private static string DefaultName(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static IReadOnlyList<JsonElement> GetArray(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray().ToArray();
        }
        return [];
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static IEnumerable<JsonElement> Enumerate(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray().ToArray(),
        JsonValueKind.Object => element.EnumerateObject().Select(x => x.Value).ToArray(),
        _ => [],
    };

    private static string ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static string FirstString(JsonElement element, params string[] names) =>
        names.Select(name => ReadString(element, name))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static int ReadInt(JsonElement element, int fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            // Some exported books keep numeric fields as strings.
            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), out var parsed)) return parsed;
        }
        return fallback;
    }

    private static bool ReadBool(JsonElement element, bool fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value)) continue;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
                _ => fallback,
            };
        }
        return fallback;
    }

    private static double? ReadNullableDouble(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) ? number : null;

    private static int? ReadNullableInt(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) ? number : null;
}
