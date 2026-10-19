using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using NativeTavern.Models;
using NativeTavern.Services;

namespace NativeTavern.ViewModels;

public partial class ChatViewModel
{
    private int _sessionLoads;
    private long? _navigationComposerSessionId;
    private DraftStore.Draft? _navigationComposer;
    public bool IsSessionLoading => Volatile.Read(ref _sessionLoads) > 0;
    public bool CanModifyConversation => !IsGenerating && !IsGroupPerformanceRunning && !IsSessionLoading && !_isDeletingSession;

    private IDisposable BeginSessionNavigation()
    {
        if (Interlocked.Increment(ref _sessionLoads) == 1)
        {
            _navigationComposerSessionId = _session?.Id;
            _navigationComposer = DraftStore.Capture(InputText, PendingImagePaths);
            CancelDraftFlush();
        }
        RefreshConversationCommands();
        return new NavigationLease(() =>
        {
            if (Interlocked.Decrement(ref _sessionLoads) == 0)
            {
                _navigationComposer = null;
                _navigationComposerSessionId = null;
                ScheduleDraftFlush();
            }
            RefreshConversationCommands();
            if (_session is not null) _ = DrainQueueAsync(_session.Id);
        });
    }

    private sealed class NavigationLease(Action completed) : IDisposable
    {
        private Action? _completed = completed;
        public void Dispose() => Interlocked.Exchange(ref _completed, null)?.Invoke();
    }

    private async Task LoadSessionAsync(ChatSession session, bool requireSelectedSession = false)
    {
        using var navigation = BeginSessionNavigation();
        await LoadSessionCoreAsync(session, requireSelectedSession);
    }

    private void RefreshConversationCommands()
    {
        OnPropertyChanged(nameof(IsSessionLoading));
        OnPropertyChanged(nameof(CanModifyConversation));
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        NewChatCommand.NotifyCanExecuteChanged();
        BranchFromMessageCommand.NotifyCanExecuteChanged();
        OpenParentConversationCommand.NotifyCanExecuteChanged();
        ContinueLastReplyCommand.NotifyCanExecuteChanged();
        GenerateConversationTitleCommand.NotifyCanExecuteChanged();
        RefreshGroupPerformanceCommands();
    }
    public ObservableCollection<Character> MemoryCharacters { get; } = [];
    [ObservableProperty] private Character? _selectedMemoryCharacter;

    partial void OnSelectedMemoryCharacterChanged(Character? value)
    {
        CharacterMemories.Clear();
        HasMemories = false;
        MemoryStatus = string.Empty;
        // The journal rail shows this character's memories even while the editor is closed.
        if (value is not null) _ = ReloadSelectedMemorySafelyAsync(value.Id);
    }

    private async Task ReloadSelectedMemorySafelyAsync(long characterId)
    {
        try { await ReloadMemoriesAsync(characterId); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading character memories failed.");
            if (SelectedMemoryCharacter?.Id == characterId) MemoryStatus = "读取记忆失败，请重试。";
        }
    }

    public async Task ManageMemoriesAsync(
        Func<IReadOnlyList<Character>, CharacterMemoryService, long?, bool> showDialog)
    {
        if (MemoryCharacters.Count == 0) return;
        IsMemoryOpen = false;
        showDialog(MemoryCharacters.ToArray(), memoryService, SelectedMemoryCharacter?.Id);
        if (SelectedMemoryCharacter is { } character) await ReloadMemoriesAsync(character.Id);
    }

    public async Task<IReadOnlyList<ChatSession>> GetConversationBranchesAsync() =>
        await chatService.GetSessionsAsync();

    public async Task OpenConversationBranchAsync(long sessionId)
    {
        if (!CanModifyConversation) return;
        using var navigation = BeginSessionNavigation();
        var session = await chatService.GetSessionAsync(sessionId);
        if (session is null)
        {
            ErrorMessage = "该剧情分支已被删除。";
            return;
        }
        await RefreshSessionsAsync(sessionId);
        await LoadSessionAsync(session);
        ErrorMessage = null;
    }

    public async Task<bool> IsEnglishAsync() =>
        (await settingsService.LoadAsync()).LanguageCode == LocalizationService.English;
}
