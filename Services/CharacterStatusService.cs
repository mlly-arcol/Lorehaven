using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NativeTavern.Data.Repositories;
using NativeTavern.Helpers;
using NativeTavern.Models;
using NativeTavern.Providers;

namespace NativeTavern.Services;

public sealed class CharacterStatusService(
    CharacterStatusRepository statusRepository,
    ChatMessageRepository messageRepository,
    CharacterRepository characterRepository,
    SettingsService settingsService,
    PluginService pluginService,
    ILLMProvider provider,
    ILogger<CharacterStatusService> logger)
{
    private const int HistoryLimit = 16;
    private const int AttributeLimit = 20;

    public Task<bool> IsEnabledAsync() =>
        pluginService.IsCapabilityEnabledAsync(PluginService.CharacterStatusCapability);

    public Task<CharacterStatusSnapshot?> GetAsync(long chatSessionId, long characterId) =>
        statusRepository.GetAsync(chatSessionId, characterId);

    public async Task<CharacterStatusSnapshot?> UpdateAsync(
        ChatSession session,
        long characterId,
        long? sourceMessageId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync()) return null;
        var character = await characterRepository.GetAsync(characterId);
        if (character is null) return null;
        var settings = await settingsService.LoadAsync();
        if (!settings.IsConfigured) return await statusRepository.GetAsync(session.Id, characterId);

        var current = await statusRepository.GetAsync(session.Id, characterId);
        var history = await messageRepository.GetRecentBySessionAsync(session.Id, HistoryLimit);
        var request = BuildRequest(character, history, current, settings);
        var response = new StringBuilder();
        try
        {
            await foreach (var chunk in provider.StreamAsync(request, cancellationToken))
                response.Append(chunk);
            var snapshot = ParseResponse(response.ToString(), session.Id, character, sourceMessageId);
            await statusRepository.UpsertAsync(snapshot);
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return current;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Character status update failed for character {CharacterId} in session {SessionId}.", characterId, session.Id);
            return current;
        }
    }

    private static ChatCompletionRequest BuildRequest(
        Character character,
        IReadOnlyList<ChatMessage> history,
        CharacterStatusSnapshot? current,
        ProviderSettings settings)
    {
        var transcript = string.Join("\n", history.Select(message =>
        {
            var speaker = message.Role == ChatRole.User ? "用户" :
                message.SpeakerCharacterId == character.Id || message.SpeakerCharacterId is null ? character.Name : "其他角色";
            var content = message.Content.Length > 1800 ? message.Content[..1800] + "…" : message.Content;
            return $"{speaker}: {content}";
        }));
        var previous = current is null
            ? "尚无状态，请依据角色设定和剧情创建最有用的一组属性。"
            : JsonSerializer.Serialize(new { current.Summary, current.Attributes }, JsonDefaults.Options);
        var characterProfile = string.Join("\n", new[] { character.Description, character.Personality, character.Scenario }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (characterProfile.Length > 5000) characterProfile = characterProfile[..5000] + "…";

        return new ChatCompletionRequest
        {
            Model = settings.Model,
            Temperature = Math.Min(settings.Temperature, 0.35),
            TopP = settings.TopP,
            MaxTokens = Math.Clamp(settings.MaxTokens, 300, 1000),
            Stream = true,
            Messages =
            [
                new ChatCompletionMessage
                {
                    Role = "system",
                    Content = "你是角色状态记录器。根据角色设定、已有状态和最新剧情，维护该角色当前状态。" +
                              "属性完全由剧情决定，不同角色可以有完全不同的属性；仅保留对当前角色有意义的内容。" +
                              "属性可以增删，值可以是数字、比例或短文本。不要把玩家或其他角色的状态混入。" +
                              "只输出严格 JSON，不要 Markdown：{\"summary\":\"一句话当前状态\",\"attributes\":[{\"name\":\"属性名\",\"value\":\"当前值\",\"description\":\"变化原因或简短说明\"}]}。" +
                              $"最多 {AttributeLimit} 项，每个字段务必简短。"
                },
                new ChatCompletionMessage
                {
                    Role = "user",
                    Content = $"角色：{character.Name}\n\n角色设定：\n{characterProfile}\n\n已有状态：\n{previous}\n\n最近对话：\n{transcript}"
                }
            ]
        };
    }

    internal static CharacterStatusSnapshot ParseResponse(
        string response,
        long chatSessionId,
        Character character,
        long? sourceMessageId)
    {
        var json = ExtractJson(response);
        var generated = JsonSerializer.Deserialize<GeneratedStatus>(json, JsonDefaults.Options)
                        ?? throw new InvalidDataException("AI 返回的角色状态为空。");
        var attributes = (generated.Attributes ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => new CharacterStatusAttribute
            {
                Name = Limit(x.Name.Trim(), 40),
                Value = Limit(x.Value.Trim(), 100),
                Description = Limit(x.Description?.Trim() ?? string.Empty, 240)
            })
            .DistinctBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(AttributeLimit)
            .ToList();
        if (attributes.Count == 0) throw new InvalidDataException("AI 没有返回有效的角色属性。");
        return new CharacterStatusSnapshot
        {
            ChatSessionId = chatSessionId,
            CharacterId = character.Id,
            CharacterName = character.Name,
            Summary = Limit(generated.Summary?.Trim() ?? string.Empty, 300),
            Attributes = attributes,
            SourceMessageId = sourceMessageId,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static string ExtractJson(string value)
    {
        var start = value.IndexOf('{');
        var end = value.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("AI 未返回 JSON 角色状态。");
        return value[start..(end + 1)];
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

    private sealed class GeneratedStatus
    {
        public string? Summary { get; set; }
        public IReadOnlyList<GeneratedAttribute>? Attributes { get; set; }
    }

    private sealed class GeneratedAttribute
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
