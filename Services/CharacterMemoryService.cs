using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Providers;

namespace NativeTavern.Services;

/// <summary>
/// Distills long-term memories from the chat history and keeps them per character, so a character
/// remembers the user and the story across conversations. Extraction runs after a reply, costs one
/// extra model call every few turns, and is off unless enabled in Settings. The rolling recap stays
/// complementary: the recap covers what happened recently, memories keep what stays true.
/// </summary>
public sealed class CharacterMemoryService(
    CharacterMemoryRepository memoryRepository,
    ChatMessageRepository messageRepository,
    ChatSessionRepository sessionRepository,
    CharacterRepository characterRepository,
    SettingsService settingsService,
    ILLMProvider provider,
    ILogger<CharacterMemoryService> logger)
{
    /// <summary>New messages between two automatic extractions.</summary>
    public const int ExtractionInterval = 8;
    public const int MaxMemoriesPerCharacter = 40;
    public const int MemoryLengthLimit = 200;
    private const int ExtractionBatchLimit = 12;
    private const int MessageContentLimit = 1200;
    private const int MaxNewPerExtraction = 5;

    public Task<IReadOnlyList<CharacterMemory>> GetAsync(long characterId) =>
        memoryRepository.GetByCharacterAsync(characterId);

    public async Task<CharacterMemory> AddManualAsync(long characterId, string content)
    {
        content = content.Trim();
        if (content.Length == 0) throw new InvalidOperationException("记忆内容不能为空。");
        if (content.Length > MemoryLengthLimit)
            throw new InvalidOperationException($"单条记忆不能超过 {MemoryLengthLimit} 个字符。");
        var existing = await memoryRepository.GetByCharacterAsync(characterId);
        if (existing.Any(x => string.Equals(x.Content, content, StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException("这条记忆已经存在。");
        var now = DateTimeOffset.UtcNow;
        return await memoryRepository.AddAsync(new CharacterMemory
        {
            CharacterId = characterId, Content = content, CreatedAt = now, UpdatedAt = now
        });
    }

    public Task DeleteAsync(long id) => memoryRepository.DeleteAsync(id);

    /// <summary>Never throws: safe to fire and forget after a reply finishes.</summary>
    public async Task ExtractIfNeededAsync(
        ChatSession session, bool force = false, CancellationToken cancellationToken = default)
    {
        try
        {
            await ExtractCoreAsync(session, force, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Character memory extraction failed for session {SessionId}.", session.Id);
        }
    }

    private async Task ExtractCoreAsync(ChatSession session, bool force, CancellationToken cancellationToken)
    {
        // Group chats stay out of scope: attributing memories to the right speaker needs more design.
        if (session.IsGroupChat || session.CharacterId is not long characterId) return;
        var settings = await settingsService.LoadAsync();
        if (!settings.CharacterMemoryEnabled || !settings.IsConfigured) return;

        var history = await messageRepository.GetBySessionAsync(session.Id);
        if (!force && history.Count - session.MemoryCoveredCount < ExtractionInterval) return;
        var start = Math.Clamp(session.MemoryCoveredCount, 0, Math.Max(0, history.Count - 1));
        var fresh = history.Skip(start).TakeLast(ExtractionBatchLimit).ToList();

        var character = await characterRepository.GetAsync(characterId);
        if (character is null) return;
        var existing = (await memoryRepository.GetByCharacterAsync(characterId)).ToList();
        var known = existing.Select(x => x.Content).ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        var added = 0;
        if (fresh.Count > 0)
        {
            var extracted = await RequestMemoriesAsync(character, fresh, existing, settings, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            foreach (var content in extracted)
            {
                if (existing.Count >= MaxMemoriesPerCharacter) break;
                if (!known.Add(content)) continue;
                await memoryRepository.AddAsync(new CharacterMemory
                {
                    CharacterId = characterId, Content = content, CreatedAt = now, UpdatedAt = now
                });
                existing.Add(new CharacterMemory
                {
                    CharacterId = characterId, Content = content, CreatedAt = now, UpdatedAt = now
                });
                added++;
            }
        }

        // The watermark always moves, even when nothing new was worth remembering.
        session.MemoryCoveredCount = history.Count;
        await sessionRepository.SetMemoryCoveredAsync(session.Id, history.Count);
        if (added > 0)
            logger.LogInformation("Stored {Count} new memories for character {CharacterId}.", added, characterId);
    }

    private async Task<IReadOnlyList<string>> RequestMemoriesAsync(
        Character character,
        IReadOnlyList<ChatMessage> fresh,
        IReadOnlyList<CharacterMemory> existing,
        ProviderSettings settings,
        CancellationToken cancellationToken)
    {
        var transcript = string.Join("\n", fresh.Select(message =>
        {
            var content = message.Content.Length > MessageContentLimit
                ? message.Content[..MessageContentLimit] + "…" : message.Content;
            var speaker = message.Role == ChatRole.User ? "用户" :
                message.SpeakerCharacterId is long id && id != character.Id ? "其他角色" : character.Name;
            return $"{speaker}: {content}";
        }));
        var existingText = existing.Count == 0
            ? "（暂无）"
            : string.Join("\n", existing.Select(x => "- " + x.Content));

        var request = new ChatCompletionRequest
        {
            Model = settings.Model,
            Temperature = Math.Min(settings.Temperature, 0.3),
            TopP = settings.TopP,
            MaxTokens = Math.Clamp(settings.MaxTokens, 200, 600),
            Stream = true,
            Messages =
            [
                new ChatCompletionMessage
                {
                    Role = "system",
                    Content = "你是角色记忆整理器。阅读角色扮演对话的新片段，提炼值得长期记住的要点：" +
                              "关于用户的稳定事实（身份、喜好、约定），以及对后续剧情有持续影响的事件、关系或设定变化。" +
                              "忽略一次性细节、寒暄和场景描写。不要重复已有记忆；若新信息与已有记忆冲突，输出更新后的表述。" +
                              $"每条一句、不超过 80 字，最多 {MaxNewPerExtraction} 条；没有值得记住的内容时输出空数组。" +
                              "只输出严格 JSON，不要 Markdown：{\"memories\":[\"…\"]}"
                },
                new ChatCompletionMessage
                {
                    Role = "user",
                    Content = $"已有记忆：\n{existingText}\n\n新对话片段：\n{transcript}"
                }
            ]
        };

        var response = new StringBuilder();
        await foreach (var chunk in provider.StreamAsync(request, cancellationToken)) response.Append(chunk);
        return ParseResponse(response.ToString());
    }

    internal static IReadOnlyList<string> ParseResponse(string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start) return [];
        try
        {
            using var json = JsonDocument.Parse(response[start..(end + 1)]);
            if (!json.RootElement.TryGetProperty("memories", out var memories) || memories.ValueKind != JsonValueKind.Array)
                return [];
            return memories.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => (x.GetString() ?? string.Empty).Trim().TrimStart('-', '•', ' '))
                .Where(x => x.Length > 0)
                .Select(x => x.Length <= MemoryLengthLimit ? x : x[..MemoryLengthLimit])
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxNewPerExtraction)
                .ToList();
        }
        catch (JsonException) { return []; }
    }
}
