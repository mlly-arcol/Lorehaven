namespace NativeTavern.Models;

/// <summary>Data-only resources supplied by a plugin, kept separate from user-owned prompt records.</summary>
public sealed class PluginPromptResourcePack
{
    public int FormatVersion { get; set; }
    public IReadOnlyList<PluginPackPersona> Personas { get; set; } = [];
    public IReadOnlyList<PluginPackLorebook> Lorebooks { get; set; } = [];
    public IReadOnlyList<PluginPackPreset> Presets { get; set; } = [];
    public IReadOnlyList<PluginPackTextRule> TextRules { get; set; } = [];
}

public sealed class PluginPackPersona
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Scope { get; set; } = "all";
    public bool AutoApply { get; set; }
}

public sealed class PluginPackLorebook
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Scope { get; set; } = "all";
    public bool AutoApply { get; set; }
    public IReadOnlyList<PluginPackLoreEntry> Entries { get; set; } = [];
}

public sealed class PluginPackLoreEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Keywords { get; set; } = string.Empty;
    public string SecondaryKeywords { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Priority { get; set; } = 100;
    public int Depth { get; set; } = 4;
    public bool IsEnabled { get; set; } = true;
    public bool IsConstant { get; set; }
    public bool IsSelective { get; set; }
}

/// <summary>Prompt text only; model settings in a pack never override the user's provider settings.</summary>
public sealed class PluginPackPreset
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string MainPrompt { get; set; } = string.Empty;
    public string Scope { get; set; } = "all";
    public bool AutoApply { get; set; }
}

/// <summary>A natural-language instruction, not an executable regex or script.</summary>
public sealed class PluginPackTextRule
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Scope { get; set; } = "all";
    public bool AutoApply { get; set; }
}

public sealed record PluginPromptResourceDescriptor(
    string QualifiedId,
    string PluginId,
    string ResourceId,
    string Kind,
    string Name,
    string Preview,
    bool AutoApply,
    bool IsEnabled,
    string Scope)
{
    public bool IsApplicableTo(bool isGroupChat) => Scope switch
    {
        "single" => !isGroupChat,
        "group" => isGroupChat,
        _ => true
    };
}

public sealed record PluginPromptSection(
    string PluginId,
    string ResourceId,
    string Heading,
    string Content);
