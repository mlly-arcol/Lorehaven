namespace NativeTavern.Models;

public sealed class ChatSession
{
    public long Id { get; set; }
    public string Title { get; set; } = "New Chat";
    public long? CharacterId { get; set; }
    public bool IsGroupChat { get; set; }
    public long? ParentSessionId { get; set; }
    public long? BranchedFromMessageId { get; set; }
    public long? PersonaId { get; set; }
    public long? LorebookId { get; set; }
    public long? PromptPresetId { get; set; }
    public string AuthorNote { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    /// <summary>How many of the oldest turns the summary already covers; the digest only grows for new ones.</summary>
    public int SummaryCoveredCount { get; set; }
    /// <summary>Once the writer edits the recap by hand, the automatic digest stops overwriting it.</summary>
    public bool SummaryIsManual { get; set; }
    /// <summary>How many messages the character-memory extraction has already scanned.</summary>
    public int MemoryCoveredCount { get; set; }
    /// <summary>Optional label used to keep related conversations together in the picker.</summary>
    public string GroupName { get; set; } = string.Empty;
    /// <summary>Pinned conversations sort to the top of the picker and keep their own column.</summary>
    public bool IsPinned { get; set; }
    /// <summary>Shown after the title in the conversation picker; empty groups stay invisible.</summary>
    public string GroupLabel => string.IsNullOrWhiteSpace(GroupName) ? string.Empty : $" · {GroupName}";
    /// <summary>Screen-reader label for the picker; without it the item is announced as this type's name.</summary>
    public string PickerLabel => string.IsNullOrWhiteSpace(GroupName) ? Title : $"{Title} · {GroupName}";
    /// <summary>Picker text. The conversation combo box renders its items as plain strings, so markers live here.</summary>
    public string PickerText => IsPinned ? $"📌 {PickerLabel}" : PickerLabel;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
