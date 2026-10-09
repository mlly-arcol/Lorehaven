using NativeTavern.Data.Repositories;
using NativeTavern.Models;

namespace NativeTavern.Services;

public sealed class PromptService(
    PromptRepository repository,
    CharacterRepository characterRepository,
    ChatSessionRepository sessionRepository,
    KnowledgeService knowledgeService,
    CharacterMemoryRepository? memoryRepository = null)
{
    private const int MinimumKeptMessages = 2;
    private const int DefaultReplyTokens = 1024;
    private const int RequestOverheadTokens = 192;
    private const int MessageOverheadTokens = 8;

    public async Task<PromptBuildResult> BuildAsync(
        ChatSession session,
        IEnumerable<ChatMessage> history,
        ProviderSettings settings,
        long? speakingCharacterId = null)
    {
        var historyList = history.ToList();
        var sections = new List<string>();
        PromptPreset? preset = null;
        if (session.PromptPresetId is long presetId)
            preset = (await repository.GetPresetsAsync()).FirstOrDefault(x => x.Id == presetId);
        AddSection(sections, "System Prompt", preset?.SystemPrompt);
        AddSection(sections, "Main Prompt", preset?.MainPrompt);

        IReadOnlyList<Character> groupMembers = [];
        if (session.IsGroupChat)
        {
            groupMembers = await characterRepository.GetByIdsAsync(
                await sessionRepository.GetCharacterIdsAsync(session.Id));
            var speaker = groupMembers.FirstOrDefault(x => x.Id == speakingCharacterId);
            AddSection(
                sections,
                "Group Chat Rules",
                "Participants: " + string.Join(", ", groupMembers.Select(x => x.Name)) + ".\n" +
                (speaker is null
                    ? "This is a multi-character conversation. Keep every message attributed to its speaker."
                    : $"Reply only as {speaker.Name}. Do not write dialogue or actions for other participants. " +
                      "Stay in character and respond naturally to the latest conversation."));
            if (settings.IncludeCharacterContext)
                foreach (var member in groupMembers)
                    AddSection(sections, "Character Profile: " + member.Name, BuildGroupCharacterContext(member));
        }
        else if (settings.IncludeCharacterContext && session.CharacterId is long characterId &&
                 await characterRepository.GetAsync(characterId) is { } character)
        {
            AddSection(sections, "Character", BuildCharacterContext(character));
        }

        if (settings.CharacterMemoryEnabled && memoryRepository is not null)
        {
            if (session.IsGroupChat)
            {
                foreach (var member in groupMembers)
                    AddSection(sections, "Character Memory: " + member.Name,
                        RenderMemories(await memoryRepository.GetByCharacterAsync(member.Id)));
            }
            else if (session.CharacterId is long memoryCharacterId &&
                     await characterRepository.GetAsync(memoryCharacterId) is { } memoryCharacter)
            {
                AddSection(sections, "Character Memory: " + memoryCharacter.Name,
                    RenderMemories(await memoryRepository.GetByCharacterAsync(memoryCharacterId)));
            }
        }

        if (session.PersonaId is long personaId &&
            (await repository.GetPersonasAsync()).FirstOrDefault(x => x.Id == personaId) is { } persona)
            AddSection(sections, "Persona", persona.Content);

        var activatedNames = new List<string>();
        if (session.LorebookId is long lorebookId &&
            (await repository.GetLorebooksAsync()).FirstOrDefault(x => x.Id == lorebookId) is { IsEnabled: true })
        {
            var activated = ActivateLoreEntries(await repository.GetLoreEntriesAsync(lorebookId), historyList);
            foreach (var entry in activated)
            {
                AddSection(sections, "World Info: " + entry.Name, entry.Content);
                activatedNames.Add(entry.Name);
            }
        }

        if (settings.IncludeKnowledgeContext)
        {
            var latestUserText = historyList.LastOrDefault(x => x.Role == ChatRole.User)?.Content ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(latestUserText))
            {
                var knowledge = await knowledgeService.SearchAsync(latestUserText);
                if (knowledge.Count > 0) AddSection(sections, "Knowledge Base", string.Join("\n\n---\n\n", knowledge));
            }
        }
        if (!string.IsNullOrWhiteSpace(session.Summary)) AddSection(sections, "Conversation Summary", session.Summary);

        var conversation = string.IsNullOrWhiteSpace(session.Summary) ? historyList : historyList.TakeLast(12).ToList();
        var speakerNames = groupMembers.ToDictionary(x => x.Id, x => x.Name);
        var trimmedMessages = TrimHistoryToBudget(
            conversation,
            settings,
            preset?.MaxTokens ?? settings.MaxTokens,
            TokenEstimator.EstimateText(string.Join("\n\n", sections)));
        var messages = ChatService.BuildMessages(conversation, speakerNames).ToList();
        if (sections.Count > 0)
            messages.Insert(0, new ChatCompletionMessage { Role = "system", Content = string.Join("\n\n", sections) });
        if (!string.IsNullOrWhiteSpace(session.AuthorNote))
        {
            var position = Math.Max(messages.Count - 1, sections.Count > 0 ? 1 : 0);
            messages.Insert(position, new ChatCompletionMessage
            {
                Role = "system",
                Content = "Author Note:\n" + session.AuthorNote.Trim()
            });
        }

        return new PromptBuildResult
        {
            Messages = messages,
            Model = string.IsNullOrWhiteSpace(preset?.Model) ? settings.Model : preset.Model.Trim(),
            Temperature = preset?.Temperature ?? settings.Temperature,
            TopP = preset?.TopP ?? settings.TopP,
            MaxTokens = preset?.MaxTokens ?? settings.MaxTokens,
            ActivatedLoreEntries = activatedNames,
            TrimmedMessages = trimmedMessages,
            EstimatedTokens = TokenEstimator.Estimate(messages)
        };
    }

    /// <summary>
    /// Oldest turns are dropped so a long roleplay does not bounce off the model's context window.
    /// The newest turns always survive, even when the budget cannot cover them.
    /// </summary>
    internal static int TrimHistoryToBudget(
        List<ChatMessage> history,
        ProviderSettings settings,
        int replyTokens,
        int fixedTokens)
    {
        if (!settings.TrimHistoryToContext || settings.ContextLength <= 0 || history.Count <= MinimumKeptMessages)
            return 0;
        var available = settings.ContextLength
                        - (replyTokens > 0 ? replyTokens : DefaultReplyTokens)
                        - RequestOverheadTokens
                        - fixedTokens;
        var used = history.Sum(EstimateMessageTokens);
        var dropped = 0;
        while (used > available && history.Count - dropped > MinimumKeptMessages)
        {
            used -= EstimateMessageTokens(history[dropped]);
            dropped++;
        }
        if (dropped > 0) history.RemoveRange(0, dropped);
        return dropped;
    }

    private static int EstimateMessageTokens(ChatMessage message) =>
        TokenEstimator.EstimateText(message.Content) + MessageOverheadTokens;

    public static IReadOnlyList<LoreEntry> ActivateLoreEntries(
        IEnumerable<LoreEntry> entries,
        IReadOnlyList<ChatMessage> history)
    {
        return entries.Where(entry =>
        {
            if (!entry.IsEnabled) return false;
            var depth = Math.Max(1, entry.Depth);
            var text = string.Join("\n", history.TakeLast(depth).Select(x => x.Content));
            if (!entry.IsConstant && !ContainsAny(text, SplitKeywords(entry.Keywords))) return false;
            return !entry.IsSelective || ContainsAny(text, SplitKeywords(entry.SecondaryKeywords));
        }).OrderByDescending(x => x.Priority).ThenBy(x => x.Id).ToList();
    }

    private static IEnumerable<string> SplitKeywords(string value) =>
        value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ContainsAny(string text, IEnumerable<string> keywords) =>
        keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static void AddSection(ICollection<string> sections, string heading, string? content)
    {
        if (!string.IsNullOrWhiteSpace(content)) sections.Add(heading + ":\n" + content.Trim());
    }

    private static string RenderMemories(IReadOnlyList<CharacterMemory> memories) =>
        memories.Count == 0 ? string.Empty : string.Join("\n", memories.Select(x => "- " + x.Content));

    private static string BuildCharacterContext(Character character)
    {
        var sections = new[]
        {
            $"You are {character.Name}. Stay in character throughout the conversation.",
            string.IsNullOrWhiteSpace(character.Description) ? null : "Description:\n" + character.Description,
            string.IsNullOrWhiteSpace(character.Personality) ? null : "Personality:\n" + character.Personality,
            string.IsNullOrWhiteSpace(character.Scenario) ? null : "Scenario:\n" + character.Scenario,
            string.IsNullOrWhiteSpace(character.ExampleMessages) ? null : "Example dialogue:\n" + character.ExampleMessages
        };
        return RenderCharacterText(string.Join("\n\n", sections.Where(x => x is not null)), character.Name);
    }

    private static string BuildGroupCharacterContext(Character character)
    {
        var sections = new[]
        {
            "Name: " + character.Name,
            string.IsNullOrWhiteSpace(character.Description) ? null : "Description:\n" + character.Description,
            string.IsNullOrWhiteSpace(character.Personality) ? null : "Personality:\n" + character.Personality,
            string.IsNullOrWhiteSpace(character.Scenario) ? null : "Scenario:\n" + character.Scenario,
            string.IsNullOrWhiteSpace(character.ExampleMessages) ? null : "Example dialogue:\n" + character.ExampleMessages
        };
        return RenderCharacterText(
            string.Join("\n\n", sections.Where(x => x is not null)), character.Name);
    }

    private static string RenderCharacterText(string text, string characterName) =>
        text.Replace("{{char}}", characterName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{user}}", "User", StringComparison.OrdinalIgnoreCase);
}
