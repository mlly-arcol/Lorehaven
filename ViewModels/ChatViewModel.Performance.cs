using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace NativeTavern.ViewModels;

public partial class ChatViewModel
{
    [ObservableProperty] private bool _isGroupPerformanceActive;
    [ObservableProperty] private bool _isGroupPerformanceRunning;
    [ObservableProperty] private bool _isGroupPerformancePaused;
    [ObservableProperty] private string _groupPerformanceStatus = string.Empty;
    private GroupPerformancePlan? _performancePlan;
    private long _performanceSessionId;
    private long _performanceVersion;
    private bool _performancePauseRequested;
    private bool _performanceEnglish;

    public bool CanConfigureGroupPerformance => CanModifyConversation && !IsGroupPerformanceActive &&
        IsGroupChat && HasProviderConfiguration && GroupMembers.Count >= 2 && !HasQueuedSends;

    public async Task StartGroupPerformanceAsync(int rounds, IReadOnlyList<long> speakerOrder)
    {
        if (!CanConfigureGroupPerformance || _session is null) return;
        var sessionId = _session.Id;
        // Every member gets exactly one turn per round, in a stable snapshot of the chosen order.
        var plan = new GroupPerformancePlan(rounds, speakerOrder);
        if (!plan.SpeakerOrder.ToHashSet().SetEquals(GroupMembers.Select(x => x.Id)))
            throw new InvalidOperationException("发言顺序必须包含当前群聊的每位角色，且不能重复。");
        _performanceEnglish = await IsEnglishAsync();
        if (!CanConfigureGroupPerformance || _session?.Id != sessionId ||
            !plan.SpeakerOrder.ToHashSet().SetEquals(GroupMembers.Select(x => x.Id))) return;
        _performancePlan = plan;
        _performanceSessionId = _session.Id;
        _performancePauseRequested = false;
        Interlocked.Increment(ref _performanceVersion);
        IsGroupPerformanceActive = true;
        IsGroupPerformancePaused = false;
        await RunGroupPerformanceAsync();
    }

    [RelayCommand(CanExecute = nameof(CanPauseGroupPerformance))]
    private void PauseGroupPerformance()
    {
        if (!IsGroupPerformanceRunning) return;
        _performancePauseRequested = true;
        GroupPerformanceStatus = _performanceEnglish
            ? "Pausing after the current reply. You can send a message to interject."
            : "当前回复结束后暂停，可发送消息插话。";
        RefreshGroupPerformanceCommands();
    }

    private bool CanPauseGroupPerformance() => IsGroupPerformanceRunning && !_performancePauseRequested;

    [RelayCommand(CanExecute = nameof(CanResumeGroupPerformance))]
    private async Task ResumeGroupPerformanceAsync()
    {
        if (!CanResumeGroupPerformance()) return;
        _performancePauseRequested = false;
        IsGroupPerformancePaused = false;
        await RunGroupPerformanceAsync();
    }

    private bool CanResumeGroupPerformance() => IsGroupPerformanceActive && IsGroupPerformancePaused &&
        !IsGenerating && !IsSessionLoading && !HasQueuedSends && HasProviderConfiguration && !_isDeletingSession &&
        _session?.Id == _performanceSessionId;

    [RelayCommand(CanExecute = nameof(CanStopGroupPerformance))]
    private void StopGroupPerformance()
    {
        var wasRunning = IsGroupPerformanceRunning;
        Interlocked.Increment(ref _performanceVersion);
        _performancePlan = null;
        _performancePauseRequested = false;
        IsGroupPerformanceRunning = false;
        IsGroupPerformancePaused = false;
        IsGroupPerformanceActive = false;
        GroupPerformanceStatus = string.Empty;
        if (wasRunning) _generationCancellation?.Cancel();
    }

    private bool CanStopGroupPerformance() => IsGroupPerformanceActive;

    private async Task RunGroupPerformanceAsync()
    {
        var plan = _performancePlan;
        if (plan is null) return;
        var version = Volatile.Read(ref _performanceVersion);
        var sessionId = _performanceSessionId;
        IsGroupPerformanceRunning = true;
        RefreshGroupPerformanceCommands();
        try
        {
            while (!plan.IsComplete && !_performancePauseRequested &&
                   version == Volatile.Read(ref _performanceVersion) && _session?.Id == sessionId)
            {
                var speaker = GroupMembers.FirstOrDefault(x => x.Id == plan.NextSpeakerId);
                if (speaker is null)
                {
                    ErrorMessage = "群聊成员已变化，请重新设置演出。";
                    break;
                }
                GroupPerformanceStatus = _performanceEnglish
                    ? $"Round {plan.CurrentRound}/{plan.Rounds} · {speaker.Name} · {plan.CompletedTurns}/{plan.TotalTurns} turns completed"
                    : $"第 {plan.CurrentRound}/{plan.Rounds} 轮 · {speaker.Name} · 已完成 {plan.CompletedTurns}/{plan.TotalTurns} 次发言";
                var succeeded = await GenerateNextSpeakerCoreAsync(speaker, performance: true);
                if (version != Volatile.Read(ref _performanceVersion)) return;
                if (!succeeded) break;
                plan.Advance();
                if (HasQueuedSends) _performancePauseRequested = true;
                if (!plan.IsComplete && !_performancePauseRequested) await Task.Delay(200);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Group performance failed.");
            ErrorMessage = "群聊演出已暂停，请检查连接后继续。";
        }
        finally
        {
            if (version == Volatile.Read(ref _performanceVersion))
            {
                IsGroupPerformanceRunning = false;
                IsGroupPerformancePaused = !plan.IsComplete;
                IsGroupPerformanceActive = !plan.IsComplete;
                GroupPerformanceStatus = plan.IsComplete
                    ? (_performanceEnglish ? $"Performance complete · {plan.Rounds} rounds, {plan.TotalTurns} turns"
                        : $"演出完成 · {plan.Rounds} 轮，共 {plan.TotalTurns} 次发言")
                    : (_performanceEnglish ? $"Paused · {plan.CompletedTurns}/{plan.TotalTurns} turns completed. Interject or resume."
                        : $"已暂停 · 已完成 {plan.CompletedTurns}/{plan.TotalTurns} 次发言，可插话后继续。");
                RefreshGroupPerformanceCommands();
                // Interjections queued during a turn go out before the next performance turn.
            }
            if (_session?.Id == sessionId && !IsGroupPerformanceRunning) await DrainQueueAsync(sessionId);
        }
    }

    private void RefreshGroupPerformanceCommands()
    {
        OnPropertyChanged(nameof(CanConfigureGroupPerformance));
        PauseGroupPerformanceCommand.NotifyCanExecuteChanged();
        ResumeGroupPerformanceCommand.NotifyCanExecuteChanged();
        StopGroupPerformanceCommand.NotifyCanExecuteChanged();
        NextSpeakerCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsGroupPerformanceActiveChanged(bool value) => RefreshGroupPerformanceCommands();
    partial void OnIsGroupPerformanceRunningChanged(bool value) => RefreshConversationCommands();
    partial void OnIsGroupPerformancePausedChanged(bool value) => RefreshGroupPerformanceCommands();
    partial void OnHasQueuedSendsChanged(bool value) => RefreshGroupPerformanceCommands();
}
