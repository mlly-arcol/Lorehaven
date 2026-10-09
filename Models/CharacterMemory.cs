namespace NativeTavern.Models;

/// <summary>
/// A single long-term fact a character remembers, distilled from past conversations.
/// Memories belong to the character, not to one session, so they survive across chats.
/// </summary>
public sealed class CharacterMemory
{
    public long Id { get; set; }
    public long CharacterId { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
