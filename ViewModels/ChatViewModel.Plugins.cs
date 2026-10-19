using NativeTavern.Models;
using NativeTavern.Services;

namespace NativeTavern.ViewModels;

public partial class ChatViewModel
{
    public async Task<IReadOnlyList<PluginPromptResourceDescriptor>> GetPluginPromptResourcesAsync() =>
        pluginPromptResources is null ? [] : await pluginPromptResources.GetAvailableResourcesAsync();

    public async Task<IReadOnlyList<string>> GetSelectedPluginPromptResourceIdsAsync() =>
        _session is null || pluginResourceSelections is null
            ? []
            : await pluginResourceSelections.GetAsync(_session.Id);

    public async Task SaveSelectedPluginPromptResourcesAsync(
        IReadOnlyCollection<string> selectedIds, long? expectedSessionId = null)
    {
        if (_session is null || pluginPromptResources is null || pluginResourceSelections is null)
            throw new InvalidOperationException("当前对话不可用。");
        var sessionId = _session.Id;
        if (expectedSessionId is long expected && sessionId != expected)
            throw new InvalidOperationException("当前对话已切换，请重新打开插件资源选择。");
        var isGroupChat = _session.IsGroupChat;
        var available = await pluginPromptResources.GetAvailableResourcesAsync();
        var selectable = available.Where(x => x.IsEnabled && !x.AutoApply && x.IsApplicableTo(isGroupChat))
            .Select(x => x.QualifiedId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prior = await pluginResourceSelections.GetAsync(sessionId);
        // Disabled, automatic, and temporarily unavailable resources cannot be edited in this
        // dialog. Preserve their saved selections so a later enable or reinstall restores them.
        var keepUneditable = prior.Where(x => !selectable.Contains(x));
        var next = selectedIds.Where(selectable.Contains).Concat(keepUneditable)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_session?.Id != sessionId)
            throw new InvalidOperationException("当前对话已切换，请重新打开插件资源选择。");
        await pluginResourceSelections.SetAsync(sessionId, next);
    }

    public async Task<bool> IsDicePluginEnabledAsync() =>
        diceTableService is not null && await pluginService.IsCapabilityEnabledAsync(DiceTableService.Capability);

    public async Task<DiceTableCatalog> GetRandomTableCatalogAsync() =>
        diceTableService is null ? new DiceTableCatalog([], []) : await diceTableService.GetCatalogAsync();

    public async Task<DiceRandomResult> RollDiceAsync(string notation)
    {
        var sessionId = _session?.Id;
        if (sessionId is null || diceTableService is null || randomResultRepository is null ||
            !await IsDicePluginEnabledAsync())
            throw new InvalidOperationException("掷骰与随机表插件未启用。");
        var result = diceTableService.RollDice(notation);
        await randomResultRepository.SaveAsync(sessionId.Value, result);
        return result;
    }

    public async Task<DiceRandomResult> DrawRandomTableAsync(string qualifiedTableId)
    {
        var sessionId = _session?.Id;
        if (sessionId is null || diceTableService is null || randomResultRepository is null ||
            !await IsDicePluginEnabledAsync())
            throw new InvalidOperationException("掷骰与随机表插件未启用。");
        var result = await diceTableService.DrawFromTableAsync(qualifiedTableId);
        await randomResultRepository.SaveAsync(sessionId.Value, result);
        return result;
    }

    public async Task<IReadOnlyList<DiceRandomResult>> GetRecentRandomResultsAsync() =>
        _session is null || randomResultRepository is null
            ? []
            : await randomResultRepository.GetRecentAsync(_session.Id);

    /// <summary>Only this explicit action adds a local random result to the model-visible draft.</summary>
    public void AddRandomResultToDraft(DiceRandomResult result)
    {
        var line = $"[{result.SourceLabel}] {result.ResultText}";
        InputText = string.IsNullOrWhiteSpace(InputText) ? line : InputText.TrimEnd() + Environment.NewLine + line;
    }

    public async Task<bool> IsStoryTimelinePluginEnabledAsync() =>
        storyTimelineService is not null && await storyTimelineService.IsEnabledAsync();

    public async Task<IReadOnlyList<StoryTimelineEvent>> GetStoryTimelineEventsAsync() =>
        _session is null || storyTimelineService is null
            ? []
            : await storyTimelineService.GetEventsAsync(_session.Id);

    public async Task<StoryTimelineEvent> AddStoryTimelineEventAsync(
        long? sourceMessageId, string title, string detail)
    {
        if (_session is null || storyTimelineService is null)
            throw new InvalidOperationException("当前对话不可用。");
        return await storyTimelineService.AddManualAsync(_session.Id, sourceMessageId, title, detail);
    }

    public async Task<bool> UpdateStoryTimelineEventAsync(StoryTimelineEvent item, string title, string detail)
    {
        if (_session is null || storyTimelineService is null || item.ChatSessionId != _session.Id)
            throw new InvalidOperationException("当前对话不可用。");
        return await storyTimelineService.UpdateAsync(item, title, detail);
    }

    public async Task<bool> DeleteStoryTimelineEventAsync(long eventId)
    {
        if (_session is null || storyTimelineService is null)
            throw new InvalidOperationException("当前对话不可用。");
        return await storyTimelineService.DeleteAsync(_session.Id, eventId);
    }

    public async Task<StoryTimelineExtractionResult> ExtractStoryTimelineAsync(CancellationToken token = default)
    {
        if (_session is null || storyTimelineService is null)
            throw new InvalidOperationException("当前对话不可用。");
        return await storyTimelineService.ExtractNewAsync(_session.Id, token);
    }

    public void JumpToStoryTimelineEvent(StoryTimelineEvent item)
    {
        if (_session?.Id != item.ChatSessionId || item.SourceMessageId is not long messageId) return;
        if (Messages.FirstOrDefault(x => x.Model.Id == messageId) is not { } target) return;
        ClearSpeakerFilterIfNeeded(target);
        JumpToMessageRequested?.Invoke(messageId);
    }
}
