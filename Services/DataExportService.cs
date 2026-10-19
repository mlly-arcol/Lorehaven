using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NativeTavern.Helpers;
using NativeTavern.Models;

namespace NativeTavern.Services;

public static class DataExportService
{
    public static async Task ExportCharacterAsync(
        Character character,
        string path,
        CancellationToken cancellationToken = default)
    {
        var json = BuildCharacterCardJson(character);
        if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            await CharacterCardPng.WriteAsync(path, json, character.AvatarPath, character.Name, cancellationToken);
            return;
        }
        EnsureExtension(path, ".json");
        await WriteAllTextAtomicAsync(path, json, cancellationToken);
    }

    public static string BuildCharacterCardJson(Character character)
    {
        if (string.IsNullOrWhiteSpace(character.Name))
            throw new InvalidOperationException("角色名称不能为空。");

        var card = new
        {
            spec = "chara_card_v3",
            spec_version = "3.0",
            data = new
            {
                name = character.Name,
                description = character.Description,
                personality = character.Personality,
                scenario = character.Scenario,
                first_mes = character.FirstMessage,
                mes_example = character.ExampleMessages,
                creator = character.Creator,
                nickname = character.AliasList.Count > 0 ? character.AliasList[0] : string.Empty,
                alt_names = character.AliasList.ToArray(),
                tags = SplitTags(character.Tags),
                alternate_greetings = Array.Empty<string>(),
                extensions = new { }
            }
        };
        return JsonSerializer.Serialize(card, JsonDefaults.Options);
    }

    public static async Task ExportConversationAsync(
        ChatSession session,
        IEnumerable<ChatMessage> messages,
        IReadOnlyDictionary<long, string>? speakerNames,
        string path,
        CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var content = extension switch
        {
            ".md" or ".markdown" => BuildConversationMarkdown(session, messages, speakerNames),
            ".json" => BuildConversationJson(session, messages, speakerNames),
            ".html" or ".htm" => BuildConversationHtml(session, messages, speakerNames),
            _ => throw new InvalidDataException("聊天记录仅支持导出为 Markdown、HTML 或 JSON。")
        };
        await WriteAllTextAtomicAsync(path, content, cancellationToken);
    }

    public static string BuildConversationMarkdown(
        ChatSession session,
        IEnumerable<ChatMessage> messages,
        IReadOnlyDictionary<long, string>? speakerNames = null)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(session.Title.Trim());
        builder.AppendLine();
        builder.Append("> Lorehaven conversation exported ")
            .AppendLine(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        if (session.ParentSessionId is not null)
            builder.AppendLine("> This conversation was created from a branch.");

        foreach (var message in messages)
        {
            builder.AppendLine();
            builder.Append("## ").Append(GetSpeakerLabel(message, speakerNames))
                .Append(" · ").Append(message.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            if (message.IsPinned) builder.Append(" · ★ pinned");
            builder.AppendLine();
            builder.AppendLine(message.Content);
        }
        return builder.ToString();
    }

    public static string BuildConversationJson(
        ChatSession session,
        IEnumerable<ChatMessage> messages,
        IReadOnlyDictionary<long, string>? speakerNames = null)
    {
        var export = new
        {
            // Keep the schema identifier compatible with existing exported conversations.
            format = "nativetavern_conversation",
            version = 1,
            conversation = new
            {
                title = session.Title,
                is_group_chat = session.IsGroupChat,
                parent_session_id = session.ParentSessionId,
                branched_from_message_id = session.BranchedFromMessageId,
                created_at = session.CreatedAt,
                updated_at = session.UpdatedAt
            },
            messages = messages.Select(message => new
            {
                role = message.Role.ToString().ToLowerInvariant(),
                speaker = GetSpeakerLabel(message, speakerNames),
                content = message.Content,
                pinned = message.IsPinned ? true : (bool?)null,
                created_at = message.CreatedAt,
                updated_at = message.UpdatedAt
            }).ToArray()
        };
        return JsonSerializer.Serialize(export, JsonDefaults.Options);
    }

    /// <summary>
    /// A single readable file: no scripts, no external fonts, safe to mail or print as a story draft.
    /// </summary>
    public static string BuildConversationHtml(
        ChatSession session,
        IEnumerable<ChatMessage> messages,
        IReadOnlyDictionary<long, string>? speakerNames = null)
    {
        var title = session.Title.Trim();
        var turns = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var builder = new StringBuilder();
        builder.Append("<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head>\n")
            .Append("<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n")
            .Append("<title>").Append(EscapeHtml(title)).Append("</title>\n")
            .Append("<style>\n").Append(HtmlStyles).Append("\n</style>\n</head>\n<body>\n<main>\n");

        builder.Append("<h1>").Append(EscapeHtml(string.IsNullOrEmpty(title) ? "Lorehaven" : title)).Append("</h1>\n");
        builder.Append("<p class=\"meta\">Lorehaven · ")
            .Append(EscapeHtml(DateTime.Now.ToString("yyyy-MM-dd HH:mm")))
            .Append(" · ").Append(turns.Count).Append(" 条消息");
        if (session.ParentSessionId is not null) builder.Append(" · 派生自更早的对话");
        builder.Append("</p>\n");
        if (!string.IsNullOrWhiteSpace(session.Summary))
        {
            builder.Append("<details class=\"summary\"><summary>剧情回顾</summary>")
                .Append(RenderHtmlBody(session.Summary))
                .Append("</details>\n");
        }

        foreach (var message in turns)
        {
            var className = message.Role == ChatRole.User ? "user" : "assistant";
            builder.Append("<section class=\"").Append(className).Append(message.IsPinned ? " pinned" : string.Empty)
                .Append("\">\n<h2>")
                .Append(EscapeHtml(GetSpeakerLabel(message, speakerNames)));
            if (message.CreatedAt.Year > 1)
            {
                builder.Append("<time datetime=\"").Append(message.CreatedAt.ToString("O")).Append("\">")
                    .Append(EscapeHtml(message.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")))
                    .Append("</time>");
            }
            if (message.IsPinned) builder.Append("<span class=\"pin\" title=\"已收藏\">★</span>");
            builder.Append("</h2>\n").Append(RenderHtmlBody(message.Content)).Append("</section>\n");
        }

        builder.Append("</main>\n</body>\n</html>");
        return builder.ToString();
    }

    private static string RenderHtmlBody(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "<p class=\"empty\">（空）</p>\n";
        var builder = new StringBuilder();
        foreach (var paragraph in Regex.Split(text, @"\r?\n[ \t]*\r?\n+").Where(block => !string.IsNullOrWhiteSpace(block)))
        {
            var lines = Regex.Replace(paragraph.Trim(), @"\*+", "\u0000").Split('\n');
            builder.Append("<p>");
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0) builder.Append("<br>\n");
                builder.Append(InlineHtml(lines[index]));
            }
            builder.Append("</p>\n");
        }
        return builder.ToString();
    }

    /// <summary>Escapes every fragment, so an asterisk can never smuggle markup into the document.</summary>
    private static string InlineHtml(string line)
    {
        var parts = line.Split('\u0000');
        var markers = parts.Length - 1;
        var trailingLiteral = markers % 2 != 0;
        var builder = new StringBuilder();
        for (var index = 0; index < parts.Length; index++)
        {
            builder.Append(EscapeHtml(parts[index]));
            if (index >= markers) break;
            if (trailingLiteral && index == markers - 1) builder.Append('*');
            else builder.Append(index % 2 == 0 ? "<em>" : "</em>");
        }
        return builder.ToString();
    }

    private static string EscapeHtml(string value) => System.Net.WebUtility.HtmlEncode(value);

    private const string HtmlStyles = """
        :root { color-scheme: light dark; --ink: #1c1b19; --muted: #6d6a64; --line: #e2ded7; --soft: #f6f3ee; --panel: #fff; --user: #eef3f8; }
        @media (prefers-color-scheme: dark) { :root { --ink: #e8e6e1; --muted: #9a968d; --line: #33352f; --soft: #1b1d20; --panel: #24272b; --user: #2a333c; } }
        * { box-sizing: border-box; }
        body { margin: 0; padding: 40px 20px 72px; background: var(--soft); color: var(--ink);
               font: 16px/1.75 "Source Han Serif SC", "Noto Serif CJK SC", Georgia, "Times New Roman", serif; }
        main { max-width: 44rem; margin: 0 auto; }
        h1 { font-size: 28px; line-height: 1.35; margin: 0 0 6px; }
        .meta { color: var(--muted); font-size: 13px; margin: 0 0 28px; }
        .summary { background: var(--soft); border: 1px solid var(--line); border-radius: 10px; padding: 10px 16px; margin: 0 0 28px; font-size: 14px; }
        .summary summary { cursor: pointer; color: var(--muted); }
        section { background: var(--panel); border: 1px solid var(--line); border-radius: 12px; padding: 16px 20px 6px; margin: 0 0 14px; }
        section.user { background: var(--user); }
        section.pinned { border-color: #c8a24a; }
        h2 { font-size: 13px; font-weight: 600; letter-spacing: .02em; color: var(--muted); margin: 0 0 10px; }
        h2 time { font-weight: 400; margin-left: 8px; }
        .pin { color: #b8860b; margin-left: 6px; }
        p { margin: 0 0 14px; white-space: normal; overflow-wrap: anywhere; }
        em { color: var(--muted); font-style: italic; }
        .empty { color: var(--muted); }
        @media print { body { background: #fff; padding: 0; } section { break-inside: avoid; border-color: #ccc; } }
        """;

    public static string CreateSafeFileName(string value, string fallback)
    {        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    private static string GetSpeakerLabel(
        ChatMessage message,
        IReadOnlyDictionary<long, string>? speakerNames)
    {
        if (message.Role == ChatRole.User) return "User";
        return message.SpeakerCharacterId is long id &&
               speakerNames?.TryGetValue(id, out var name) == true &&
               !string.IsNullOrWhiteSpace(name)
            ? name
            : "Assistant";
    }

    private static string[] SplitTags(string tags) =>
        tags.Split([',', '，', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void EnsureExtension(string path, string expected)
    {
        if (!Path.GetExtension(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"文件必须使用 {expected} 扩展名。");
    }

    internal static async Task WriteAllTextAtomicAsync(
        string path,
        string content,
        CancellationToken cancellationToken) =>
        await WriteAtomicAsync(
            path,
            temporary => File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken),
            cancellationToken);

    internal static async Task WriteAllBytesAtomicAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken) =>
        await WriteAtomicAsync(
            path,
            temporary => File.WriteAllBytesAsync(temporary, content, cancellationToken),
            cancellationToken);

    private static async Task WriteAtomicAsync(
        string path,
        Func<string, Task> writeTemporary,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidDataException("导出路径无效。");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await writeTemporary(temporary);
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
