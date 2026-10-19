using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NativeTavern.Helpers;
using NativeTavern.Models;

namespace NativeTavern.Services;

/// <summary>
/// Loads bounded, declarative prompt resources from installed plugins. It never writes to the
/// user's prompt tables, so disabling or removing a plugin has no effect on user-owned resources.
/// </summary>
public sealed class PluginPromptResourceService(
    PluginService pluginService,
    ILogger<PluginPromptResourceService> logger)
{
    public const string Capability = "prompt-resources-v1";
    public const string DataFileName = "prompt-resources.json";
    private const int MaxFileBytes = 1024 * 1024;
    private const int MaxResources = 64;
    private const int MaxLoreEntries = 256;
    private const int MaxSections = 12;
    private const int MaxSectionCharacters = 12_000;
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Validates a staged package before PluginService makes it visible. Existing packages without
    /// this capability remain valid and require no resource file.
    /// </summary>
    public static async Task ValidatePackageDirectoryAsync(PluginManifest manifest, string directory)
    {
        if (!HasCapability(manifest)) return;
        _ = await ReadValidatedPackAsync(directory);
    }

    /// <summary>Lists resources from installed packs, including disabled packs for selection UI.</summary>
    public async Task<IReadOnlyList<PluginPromptResourceDescriptor>> GetAvailableResourcesAsync()
    {
        var result = new List<PluginPromptResourceDescriptor>();
        foreach (var plugin in await GetPromptPluginsAsync())
        {
            var pack = await TryReadPackAsync(plugin);
            if (pack is null) continue;
            foreach (var persona in pack.Personas)
                result.Add(Describe(plugin, persona.Id, "persona", persona.Name, persona.Content,
                    persona.AutoApply, persona.Scope));
            foreach (var book in pack.Lorebooks)
                result.Add(Describe(plugin, book.Id, "lorebook", book.Name,
                    book.Entries.FirstOrDefault()?.Content ?? string.Empty, book.AutoApply, book.Scope));
            foreach (var preset in pack.Presets)
                result.Add(Describe(plugin, preset.Id, "preset", preset.Name,
                    string.IsNullOrWhiteSpace(preset.SystemPrompt) ? preset.MainPrompt : preset.SystemPrompt,
                    preset.AutoApply, preset.Scope));
            foreach (var rule in pack.TextRules)
                result.Add(Describe(plugin, rule.Id, "text-rule", rule.Name, rule.Content,
                    rule.AutoApply, rule.Scope));
        }
        return result;
    }

    /// <summary>
    /// Returns selected resources and explicitly automatic resources from enabled plugins only.
    /// The caller may pass persisted qualified IDs, such as "example.pack/storyteller".
    /// </summary>
    public async Task<IReadOnlyList<PluginPromptSection>> GetActiveSectionsAsync(
        ChatSession session,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyCollection<string>? selectedResourceIds = null)
    {
        var selected = new HashSet<string>(selectedResourceIds ?? [], StringComparer.OrdinalIgnoreCase);
        var sections = new List<PluginPromptSection>();
        var characters = 0;

        void Add(string pluginId, string resourceId, string heading, string content)
        {
            if (sections.Count >= MaxSections || string.IsNullOrWhiteSpace(content)) return;
            var trimmed = content.Trim();
            if (characters + trimmed.Length > MaxSectionCharacters) return;
            sections.Add(new PluginPromptSection(pluginId, resourceId, heading, trimmed));
            characters += trimmed.Length;
        }

        foreach (var plugin in await GetPromptPluginsAsync())
        {
            if (!plugin.IsEnabled || plugin.ValidationError is not null) continue;
            var pack = await TryReadPackAsync(plugin);
            if (pack is null) continue;
            bool Active(string id, bool autoApply, string scope) =>
                ScopeMatches(scope, session) && (autoApply || selected.Contains(Qualify(plugin.Id, id)));

            foreach (var persona in pack.Personas)
                if (Active(persona.Id, persona.AutoApply, persona.Scope))
                    Add(plugin.Id, persona.Id, $"Plugin Persona ({plugin.Name}): {persona.Name}", persona.Content);

            foreach (var preset in pack.Presets)
            {
                if (!Active(preset.Id, preset.AutoApply, preset.Scope)) continue;
                Add(plugin.Id, preset.Id, $"Plugin Preset ({plugin.Name}): {preset.Name} - System Prompt", preset.SystemPrompt);
                Add(plugin.Id, preset.Id, $"Plugin Preset ({plugin.Name}): {preset.Name} - Main Prompt", preset.MainPrompt);
            }

            foreach (var book in pack.Lorebooks)
            {
                if (!Active(book.Id, book.AutoApply, book.Scope)) continue;
                foreach (var entry in book.Entries
                             .Where(entry => IsLoreEntryActive(entry, history))
                             .OrderByDescending(entry => entry.Priority)
                             .ThenBy(entry => entry.Id, StringComparer.Ordinal))
                    Add(plugin.Id, book.Id, $"Plugin World Info ({plugin.Name} / {book.Name}): {entry.Name}", entry.Content);
            }

            foreach (var rule in pack.TextRules)
                if (Active(rule.Id, rule.AutoApply, rule.Scope))
                    Add(plugin.Id, rule.Id, $"Plugin Text Rule ({plugin.Name}): {rule.Name}", rule.Content);

            if (sections.Count >= MaxSections) break;
        }
        return sections;
    }

    private static PluginPromptResourceDescriptor Describe(
        InstalledPlugin plugin, string resourceId, string kind, string name, string content,
        bool autoApply, string scope)
    {
        var preview = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (preview.Length > 160) preview = preview[..160] + "…";
        return new PluginPromptResourceDescriptor(
            Qualify(plugin.Id, resourceId), plugin.Id, resourceId, kind, name, preview,
            autoApply, plugin.IsEnabled && plugin.ValidationError is null, scope);
    }

    private static string Qualify(string pluginId, string resourceId) => pluginId + "/" + resourceId;

    private async Task<IReadOnlyList<InstalledPlugin>> GetPromptPluginsAsync() =>
        (await pluginService.GetInstalledAsync(validateContents: true))
            .Where(plugin => HasCapability(plugin.Manifest))
            .OrderBy(plugin => plugin.Id, StringComparer.Ordinal)
            .ToList();

    private async Task<PluginPromptResourcePack?> TryReadPackAsync(InstalledPlugin plugin)
    {
        try { return await ReadValidatedPackAsync(plugin.DirectoryPath); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Skipping invalid prompt resources in plugin {PluginId}", plugin.Id);
            return null;
        }
    }

    private static async Task<PluginPromptResourcePack> ReadValidatedPackAsync(string directory)
    {
        var path = Path.Combine(directory, DataFileName);
        if (!File.Exists(path)) throw new InvalidDataException($"插件缺少 {DataFileName}。");
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("插件资源文件不能是符号链接。");
        if (info.Length > MaxFileBytes) throw new InvalidDataException("插件资源文件不能超过 1 MB。");
        if (info.Length == 0) throw new InvalidDataException("插件资源文件为空。");

        try
        {
            await using var stream = File.OpenRead(path);
            var pack = await JsonSerializer.DeserializeAsync<PluginPromptResourcePack>(stream, JsonDefaults.Options)
                       ?? throw new InvalidDataException("插件资源文件为空。");
            Validate(pack);
            return pack;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("插件资源 JSON 格式无效。", ex);
        }
    }

    private static void Validate(PluginPromptResourcePack pack)
    {
        if (pack.FormatVersion != 1) throw new InvalidDataException("不支持此提示词资源包版本。");
        if (pack.Personas is null || pack.Lorebooks is null || pack.Presets is null || pack.TextRules is null)
            throw new InvalidDataException("插件资源列表不能为 null。");
        if (pack.Personas.Count + pack.Lorebooks.Count + pack.Presets.Count + pack.TextRules.Count > MaxResources)
            throw new InvalidDataException("插件资源数量过多。");
        if (pack.Lorebooks.Sum(book => book?.Entries?.Count ?? 0) > MaxLoreEntries)
            throw new InvalidDataException("插件世界书条目过多。");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var persona in pack.Personas)
        {
            if (persona is null) throw new InvalidDataException("Persona 资源无效。");
            ValidateIdentity(persona.Id, persona.Name, persona.Scope, ids);
            ValidateText(persona.Content, "Persona 内容", required: true);
        }
        foreach (var book in pack.Lorebooks)
        {
            if (book is null) throw new InvalidDataException("世界书资源无效。");
            ValidateIdentity(book.Id, book.Name, book.Scope, ids);
            if (book.Entries is null) throw new InvalidDataException("世界书条目列表不能为 null。");
            var entryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in book.Entries)
            {
                if (entry is null) throw new InvalidDataException("世界书条目无效。");
                ValidateId(entry.Id);
                if (!entryIds.Add(entry.Id)) throw new InvalidDataException("世界书条目 ID 重复。");
                ValidateName(entry.Name);
                ValidateText(entry.Content, "世界书内容", required: true);
                ValidateText(entry.Keywords, "世界书关键词", maxLength: 500);
                ValidateText(entry.SecondaryKeywords, "世界书次级关键词", maxLength: 500);
                if (entry.Depth is < 1 or > 20 || entry.Priority is < -1000 or > 1000)
                    throw new InvalidDataException("世界书条目的深度或优先级超出范围。");
                if (!entry.IsConstant && string.IsNullOrWhiteSpace(entry.Keywords))
                    throw new InvalidDataException("非常驻世界书条目需要关键词。");
                if (entry.IsSelective && string.IsNullOrWhiteSpace(entry.SecondaryKeywords))
                    throw new InvalidDataException("选择性世界书条目需要次级关键词。");
            }
        }
        foreach (var preset in pack.Presets)
        {
            if (preset is null) throw new InvalidDataException("预设资源无效。");
            ValidateIdentity(preset.Id, preset.Name, preset.Scope, ids);
            ValidateText(preset.SystemPrompt, "预设系统提示词");
            ValidateText(preset.MainPrompt, "预设主要提示词");
            if (string.IsNullOrWhiteSpace(preset.SystemPrompt) && string.IsNullOrWhiteSpace(preset.MainPrompt))
                throw new InvalidDataException("预设至少需要一段提示词。");
        }
        foreach (var rule in pack.TextRules)
        {
            if (rule is null) throw new InvalidDataException("文本规则资源无效。");
            ValidateIdentity(rule.Id, rule.Name, rule.Scope, ids);
            ValidateText(rule.Content, "文本规则内容", required: true);
        }
    }

    private static void ValidateIdentity(string id, string name, string scope, HashSet<string> ids)
    {
        ValidateId(id);
        if (!ids.Add(id)) throw new InvalidDataException("插件资源 ID 重复。");
        ValidateName(name);
        if (scope is not ("all" or "single" or "group"))
            throw new InvalidDataException("插件资源 scope 只能为 all、single 或 group。");
    }

    private static void ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id))
            throw new InvalidDataException("插件资源 ID 格式无效。");
    }

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Contains('\n') || name.Contains('\r'))
            throw new InvalidDataException("插件资源名称不能为空、包含换行或超过 80 个字符。");
    }

    private static void ValidateText(string? content, string label, bool required = false, int maxLength = 4000)
    {
        if (content is null || content.Length > maxLength || (required && string.IsNullOrWhiteSpace(content)))
            throw new InvalidDataException($"{label}为空或超过 {maxLength} 个字符。");
    }

    private static bool ScopeMatches(string scope, ChatSession session) => scope switch
    {
        "single" => !session.IsGroupChat,
        "group" => session.IsGroupChat,
        _ => true
    };

    private static bool HasCapability(PluginManifest manifest) =>
        manifest.Capabilities?.Contains(Capability, StringComparer.OrdinalIgnoreCase) == true;

    private static bool IsLoreEntryActive(PluginPackLoreEntry entry, IReadOnlyList<ChatMessage> history)
    {
        if (!entry.IsEnabled) return false;
        var text = string.Join("\n", history.TakeLast(entry.Depth).Select(message => message.Content));
        if (!entry.IsConstant && !ContainsAny(text, entry.Keywords)) return false;
        return !entry.IsSelective || ContainsAny(text, entry.SecondaryKeywords);
    }

    private static bool ContainsAny(string text, string keywords) =>
        keywords.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}
