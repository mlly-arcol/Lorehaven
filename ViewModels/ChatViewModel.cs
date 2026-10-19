using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NativeTavern.Models;
using NativeTavern.Data.Repositories;
using NativeTavern.Providers;
using NativeTavern.Services;

namespace NativeTavern.ViewModels;

public partial class ChatViewModel(
    ChatService chatService,
    PromptRepository promptRepository,
    ComposerDraftRepository draftRepository,
    SettingsService settingsService,
    RegexScriptService regexScripts,
    CharacterStatusService characterStatusService,
    ReplySuggestionService replySuggestionService,
    PluginService pluginService,
    TrayService trayService,
    CharacterMemoryService memoryService,
    ConversationTitleService titleService,
    ILogger<ChatViewModel> logger,
    PluginPromptResourceService? pluginPromptResources = null,
    PluginResourceSelectionRepository? pluginResourceSelections = null,
    DiceTableService? diceTableService = null,
    PluginRandomResultRepository? randomResultRepository = null,
    StoryTimelineService? storyTimelineService = null) : ObservableObject
{
    public MessageCollection Messages { get; } = new();
    public ObservableCollection<ChatSession> Sessions { get; } = [];
    public ObservableCollection<string> SessionGroups { get; } = [];
    public ObservableCollection<Persona> Personas { get; } = [];
    public ObservableCollection<Lorebook> Lorebooks { get; } = [];
    public ObservableCollection<PromptPreset> Presets { get; } = [];
    public ObservableCollection<string> PendingImagePaths { get; } = [];
    public ObservableCollection<Character> GroupMembers { get; } = [];
    public ObservableCollection<string> ReplySuggestions { get; } = [];
    public ObservableCollection<SendQueue.Item> QueuedSends { get; } = [];

    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _sessionTitle = "New Chat";
    [ObservableProperty] private bool _hasProviderConfiguration;
    [ObservableProperty] private ChatSession? _selectedSession;
    [ObservableProperty] private Persona? _selectedPersona;
    [ObservableProperty] private Lorebook? _selectedLorebook;
    [ObservableProperty] private PromptPreset? _selectedPreset;
    [ObservableProperty] private string _authorNote = string.Empty;
    [ObservableProperty] private int _estimatedPromptTokens;
    [ObservableProperty] private string _currentAssistantName = "Lorehaven";
    [ObservableProperty] private string _currentAssistantAvatarPath = string.Empty;
    [ObservableProperty] private bool _isGroupChat;
    [ObservableProperty] private bool _isBranch;
    [ObservableProperty] private bool _isCharacterStatusPluginEnabled;
    [ObservableProperty] private bool _hasReplySuggestions;
    [ObservableProperty] private bool _isGeneratingSuggestions;
    [ObservableProperty] private bool _hasQueuedSends;

    private ChatSession? _session;
    private readonly DraftStore _drafts = new();
    private readonly SendQueue _queue = new();
    private bool _draftHooksWired;
    private long _draftFlushId;
    private CancellationTokenSource? _generationCancellation;
    private TaskCompletionSource? _generationCompletion;
    private CancellationTokenSource? _suggestionCancellation;
    private CancellationTokenSource? _titleCancellation;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(GenerateConversationTitleCommand)), NotifyCanExecuteChangedFor(nameof(CancelConversationTitleCommand))]
    private bool _isGeneratingTitle;
    private bool _suppressSessionSelection;
    private long _sessionLoadRequestId;
    private long _suggestionRequestId;
    private bool _isDeletingSession;

    public event Action? ConfigureRequested;
    public event Action? PromptInspectorRequested;

    public ChatSession? CurrentSession => _session;

    public async Task InitializeAsync()
    {
        InitializeJournal();
        pluginService.PluginsChanged += OnPluginsChanged;
        if (!_draftHooksWired)
        {
            _draftHooksWired = true;
            Messages.CollectionChanged += (_, _) => GenerateConversationTitleCommand.NotifyCanExecuteChanged();
            PendingImagePaths.CollectionChanged += (_, _) => ScheduleDraftFlush();
        }
        foreach (var draft in await draftRepository.GetAllAsync())
            _drafts.Remember(draft.ChatSessionId, DraftStore.Capture(draft.Text, draft.Images));
        var sessions = await chatService.GetSessionsAsync();
        var session = sessions.FirstOrDefault() ?? await chatService.CreateSessionAsync();
        await RefreshPromptOptionsAsync();
        await RefreshSessionsAsync(session.Id);
        await LoadSessionAsync(session);
        await RefreshConfigurationAsync();
        await RefreshCharacterStatusPluginAsync();
    }

    public async Task RefreshCharacterStatusPluginAsync() =>
        IsCharacterStatusPluginEnabled = await characterStatusService.IsEnabledAsync();

    public async Task<CharacterStatusSnapshot?> GetCharacterStatusAsync(ChatMessageViewModel message)
    {
        if (_session is null || !IsCurrentMessage(message) || !message.IsAssistant) return null;
        var session = _session;
        var characterId = message.CharacterId;
        if (characterId is not long id) return null;
        var snapshot = await characterStatusService.GetAsync(session.Id, id);
        if (!IsCurrentMessage(message)) return null;
        return snapshot ?? await characterStatusService.UpdateAsync(session, id, message.Model.Id);
    }

    public async Task RefreshConfigurationAsync() =>
        HasProviderConfiguration = (await settingsService.LoadAsync()).IsConfigured;

    public async Task RefreshPromptOptionsAsync()
    {
        var personaId = SelectedPersona?.Id ?? _session?.PersonaId;
        var lorebookId = SelectedLorebook?.Id ?? _session?.LorebookId;
        var presetId = SelectedPreset?.Id ?? _session?.PromptPresetId;
        Personas.Clear();
        foreach (var item in await promptRepository.GetPersonasAsync()) Personas.Add(item);
        Lorebooks.Clear();
        foreach (var item in await promptRepository.GetLorebooksAsync()) Lorebooks.Add(item);
        Presets.Clear();
        foreach (var item in await promptRepository.GetPresetsAsync()) Presets.Add(item);
        SelectedPersona = Personas.FirstOrDefault(x => x.Id == personaId);
        SelectedLorebook = Lorebooks.FirstOrDefault(x => x.Id == lorebookId);
        SelectedPreset = Presets.FirstOrDefault(x => x.Id == presetId);
        await RefreshSessionGroupsAsync();
    }

    public async Task RefreshSessionGroupsAsync()
    {
        SessionGroups.Clear();
        foreach (var name in await chatService.GetSessionGroupsAsync()) SessionGroups.Add(name);
    }

    [RelayCommand]
    private async Task ApplyPromptContextAsync()
    {
        if (_session is null || !CanModifyConversation) return;
        var session = _session;
        var groupName = SessionGroup;
        await chatService.UpdateSessionPromptAsync(
            session, SelectedPersona?.Id, SelectedLorebook?.Id, SelectedPreset?.Id, AuthorNote.Trim());
        try
        {
            await chatService.SetSessionGroupAsync(session, groupName);
            if (_session?.Id != session.Id || SelectedSession?.Id != session.Id) return;
            SessionGroup = session.GroupName;
            ErrorMessage = null;
        }
        catch (InvalidOperationException ex)
        {
            if (_session?.Id != session.Id || SelectedSession?.Id != session.Id) return;
            SessionGroup = session.GroupName;
            ErrorMessage = ex.Message;
        }
        await RefreshSessionsAsync(session.Id, onlyForCurrentSessionId: session.Id);
        await RefreshSessionGroupsAsync();
        if (_session?.Id == session.Id) await RefreshTokenEstimateAsync();
    }

    [RelayCommand]
    private async Task ClearPromptContextAsync()
    {
        SelectedPersona = null;
        SelectedLorebook = null;
        SelectedPreset = null;
        AuthorNote = string.Empty;
        await ApplyPromptContextAsync();
    }

    public async Task UpdatePromptContextAsync(
        Persona? persona,
        Lorebook? lorebook,
        PromptPreset? preset,
        string authorNote,
        string groupName)
    {
        SelectedPersona = persona;
        SelectedLorebook = lorebook;
        SelectedPreset = preset;
        AuthorNote = authorNote;
        SessionGroup = groupName;
        await ApplyPromptContextAsync();
    }

    [RelayCommand]
    private async Task ToggleSessionPinAsync()
    {
        if (_session is null) return;
        await chatService.SetSessionPinnedAsync(_session, !_session.IsPinned);
        IsCurrentSessionPinned = _session.IsPinned;
        await RefreshSessionsAsync(_session.Id);
    }

    public async Task StartCharacterChatAsync(Character character)
    {
        if (!CanModifyConversation) return;
        using var navigation = BeginSessionNavigation();
        var session = await chatService.CreateSessionAsync(character);
        await RefreshSessionsAsync(session.Id);
        await LoadSessionAsync(session);
        ErrorMessage = null;
    }

    public async Task StartGroupChatAsync(string groupName)
    {
        if (!CanModifyConversation) return;
        using var navigation = BeginSessionNavigation();
        try
        {
            var session = await chatService.CreateGroupSessionAsync(groupName);
            await RefreshSessionsAsync(session.Id);
            await LoadSessionAsync(session);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating group chat failed.");
            ErrorMessage = ex.Message;
        }
    }

    public Task<IReadOnlyList<Character>> GetAllCharactersAsync() => chatService.GetAllCharactersAsync();

    public async Task ExportCurrentConversationAsync(string path)
    {
        if (_session is null || IsGenerating) return;
        var speakerNames = GroupMembers.ToDictionary(x => x.Id, x => x.Name);
        if (_session.CharacterId is long characterId && !speakerNames.ContainsKey(characterId))
            speakerNames[characterId] = CurrentAssistantName;
        await DataExportService.ExportConversationAsync(
            _session,
            Messages.Select(x => x.Model),
            speakerNames,
            path);
    }

    public async Task RenameCurrentConversationAsync(string title)
    {
        if (_session is null || IsGenerating) return;
        _titleCancellation?.Cancel();
        await chatService.UpdateSessionTitleAsync(_session, title);
        SessionTitle = _session.Title;
        await RefreshSessionsAsync(_session.Id);
    }

    private bool CanGenerateConversationTitle() => _session is not null && CanModifyConversation &&
        !IsGeneratingTitle && HasProviderConfiguration && Messages.Any(x => !string.IsNullOrWhiteSpace(x.Content));

    [RelayCommand(CanExecute = nameof(CanGenerateConversationTitle))]
    private async Task GenerateConversationTitleAsync()
    {
        if (!CanGenerateConversationTitle()) return;
        var session = _session!;
        var originalTitle = session.Title;
        using var cancellation = new CancellationTokenSource();
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        requestCancellation.CancelAfter(TimeSpan.FromSeconds(45));
        _titleCancellation = cancellation;
        IsGeneratingTitle = true;
        ErrorMessage = null;
        var english = false;
        try
        {
            english = (await settingsService.LoadAsync()).LanguageCode == LocalizationService.English;
            requestCancellation.Token.ThrowIfCancellationRequested();
            var title = await titleService.GenerateAsync(session, requestCancellation.Token);
            requestCancellation.Token.ThrowIfCancellationRequested();
            if (_session?.Id != session.Id || SelectedSession?.Id != session.Id || IsGenerating) return;
            if (title is null)
            {
                ErrorMessage = english ? "No valid title was generated. Please try again." : "未能生成有效标题，请重试。";
                return;
            }
            if (!await chatService.TryUpdateSessionTitleAsync(session, originalTitle, title)) return;
            if (_session?.Id == session.Id) SessionTitle = session.Title;
            await RefreshSessionsAsync(_session?.Id ?? session.Id);
        }
        catch (OperationCanceledException)
        {
            if (_session?.Id == session.Id && !cancellation.IsCancellationRequested)
                ErrorMessage = english ? "Title generation timed out. Please try again." : "生成标题超时，请重试。";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Generating a conversation title failed.");
            if (_session?.Id == session.Id) ErrorMessage = english
                ? "Could not generate a title. Check the model connection and try again."
                : "生成标题失败，请检查模型连接后重试。";
        }
        finally
        {
            if (ReferenceEquals(_titleCancellation, cancellation)) _titleCancellation = null;
            IsGeneratingTitle = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelConversationTitle))]
    private void CancelConversationTitle() => _titleCancellation?.Cancel();
    private bool CanCancelConversationTitle() => IsGeneratingTitle;

    [RelayCommand(CanExecute = nameof(CanBranchFromMessage))]
    private async Task BranchFromMessageAsync(ChatMessageViewModel? message)
    {
        if (_session is null || !IsCurrentMessage(message) || !CanModifyConversation) return;
        using var navigation = BeginSessionNavigation();
        try
        {
            var branch = await chatService.BranchSessionAsync(_session, message.Model.Id);
            await RefreshSessionsAsync(branch.Id);
            await LoadSessionAsync(branch);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating conversation branch failed.");
            ErrorMessage = "创建聊天分支失败：" + ex.Message;
        }
    }

    private bool CanBranchFromMessage(ChatMessageViewModel? message) =>
        IsCurrentMessage(message) && CanModifyConversation;

    [RelayCommand(CanExecute = nameof(CanOpenParentConversation))]
    private async Task OpenParentConversationAsync()
    {
        if (_session?.ParentSessionId is not long parentId || !CanModifyConversation) return;
        using var navigation = BeginSessionNavigation();
        var parent = await chatService.GetSessionAsync(parentId);
        if (parent is null)
        {
            ErrorMessage = "源对话已被删除。";
            return;
        }
        await LoadSessionAsync(parent);
        ErrorMessage = null;
    }

    private bool CanOpenParentConversation() =>
        _session?.ParentSessionId is not null && CanModifyConversation;

    public async Task UpdateGroupChatAsync(string title, IReadOnlyCollection<long> characterIds)
    {
        if (_session is null || !_session.IsGroupChat || !CanModifyConversation) return;
        using var navigation = BeginSessionNavigation();
        StopGroupPerformance();
        try
        {
            await chatService.UpdateGroupSessionAsync(_session, title, characterIds);
            await RefreshSessionsAsync(_session.Id);
            await LoadSessionAsync(_session);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Updating group chat failed.");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (_session is null || !CanSend()) return;
        var sessionId = _session.Id;
        // An interjection takes priority over the next performance turn.
        if (IsGroupPerformanceActive) PauseGroupPerformance();
        ErrorMessage = null;
        ClearReplySuggestions();
        var input = InputText;
        var images = PendingImagePaths.ToArray();
        InputText = string.Empty;
        PendingImagePaths.Clear();
        CancelDraftFlush();
        await FlushDraftAsync(sessionId, DraftStore.Draft.Empty);
        if (_isDeletingSession) return;
        // A reply is still being written, so hold the message until the conversation is free again.
        if (IsGenerating || _session?.Id != sessionId || IsSessionLoading)
        {
            _queue.Enqueue(sessionId, new SendQueue.Item(input, images));
            RefreshQueuedSends();
            return;
        }
        await SendNowAsync(sessionId, input, images);
    }

    private async Task SendNowAsync(long sessionId, string input, IReadOnlyList<string> images)
    {
        if (_session is null || _session.Id != sessionId || _isDeletingSession || IsSessionLoading) return;
        using var cancellation = BeginGeneration();
        ChatMessageViewModel? assistantViewModel = null;
        try
        {
            await chatService.SendAsync(
                _session, input, images,
                async (user, assistant) =>
                {
                    var attachments = await chatService.GetAttachmentsAsync(user.Id);
                    await OnUiAsync(() =>
                    {
                        Messages.Add(new ChatMessageViewModel(user, attachments: attachments));
                        var speaker = GroupMembers.FirstOrDefault(x => x.Id == assistant.SpeakerCharacterId);
                        assistantViewModel = new ChatMessageViewModel(
                            assistant,
                            assistantName: speaker?.Name ?? CurrentAssistantName,
                            assistantAvatarPath: speaker?.AvatarPath ?? CurrentAssistantAvatarPath,
                            characterId: assistant.SpeakerCharacterId ?? _session.CharacterId);
                        assistantViewModel.IsStreaming = true;
                        Messages.Add(assistantViewModel);
                        SessionTitle = _session.Title;
                    });
                },
                async (_, chunk) =>
                {
                    await OnUiAsync(() =>
                    {
                        if (assistantViewModel is not null) assistantViewModel.Content += chunk;
                    });
                }, cancellation.Token);
            if (assistantViewModel is not null && !string.IsNullOrEmpty(assistantViewModel.Content))
            {
                assistantViewModel.SetSwipeState(0, 1, assistantViewModel.Content);
                trayService.Notify("Lorehaven", "助手回复已完成。");
                await RefreshReplySuggestionsAsync(cancellation.Token);
                await UpdateCharacterStatusAsync(assistantViewModel.Model, cancellation.Token);
            }
            SessionTitle = _session.Title;
            await RefreshSessionsAsync(_session.Id);
            await RefreshTokenEstimateAsync();
        }
        catch (ProviderException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat generation failed.");
            ErrorMessage = "生成失败，请查看日志后重试。";
        }
        finally
        {
            if (assistantViewModel is not null) assistantViewModel.IsStreaming = false;
            try
            {
                if (assistantViewModel is not null && !string.IsNullOrEmpty(assistantViewModel.Content))
                {
                    var count = await chatService.GetSwipeCountAsync(assistantViewModel.Model);
                    assistantViewModel.SetSwipeState(
                        assistantViewModel.Model.CurrentSwipeIndex, count, assistantViewModel.Content);
                }
                else if (assistantViewModel is not null)
                {
                    Messages.Remove(assistantViewModel);
                }
            }
            finally
            {
                EndGeneration(cancellation);
                RefreshQueuedSends();
                _ = DrainQueueAsync(sessionId);
            }
        }
    }

    /// Streaming callbacks arrive on background tasks and WPF only accepts collection changes from the
    /// UI thread. With no Application - in tests or at design time - running the update inline is the
    /// same thing, and it keeps this view model reachable without a window.
    private static Task OnUiAsync(Action update) =>
        Application.Current is { } app ? app.Dispatcher.InvokeAsync(update).Task : Task.Run(update);

    /// Sends the next queued message once the conversation is idle again, one at a time so each reply
    /// can stream before the following message is posted.
    private async Task DrainQueueAsync(long sessionId)
    {
        if (_session?.Id != sessionId || IsGenerating || _isDeletingSession || IsGroupPerformanceRunning || IsSessionLoading) return;
        if (_queue.Dequeue(sessionId) is not SendQueue.Item item) return;
        RefreshQueuedSends();
        await SendNowAsync(sessionId, item.Text, item.Images);
    }

    private void RefreshQueuedSends()
    {
        QueuedSends.Clear();
        if (_session is not null)
            foreach (var item in _queue.For(_session.Id)) QueuedSends.Add(item);
        HasQueuedSends = QueuedSends.Count > 0;
    }

    [RelayCommand]
    private void CancelQueuedSend(SendQueue.Item? item)
    {
        if (_session is null || item is null) return;
        _queue.Remove(_session.Id, item);
        RefreshQueuedSends();
    }

    [RelayCommand]
    private void ClearQueuedSends()
    {
        if (_session is null) return;
        _queue.Clear(_session.Id);
        RefreshQueuedSends();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        StopGroupPerformance();
        _generationCancellation?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanGenerateNextSpeaker))]
    private Task NextSpeakerAsync() => GenerateNextSpeakerAsync();

    public async Task GenerateNextSpeakerAsync(Character? requestedSpeaker = null)
    {
        if (_session is null || !CanGenerateNextSpeaker()) return;
        if (IsGroupPerformanceActive) PauseGroupPerformance();
        await GenerateNextSpeakerCoreAsync(requestedSpeaker);
        if (_session is not null) await DrainQueueAsync(_session.Id);
    }

    private async Task<bool> GenerateNextSpeakerCoreAsync(Character? requestedSpeaker, bool performance = false)
    {
        if (_session is null || IsGenerating || IsSessionLoading || _isDeletingSession) return false;
        var session = _session;
        ErrorMessage = null;
        ClearReplySuggestions();
        using var cancellation = BeginGeneration();
        ChatMessageViewModel? assistantViewModel = null;
        try
        {
            await chatService.GenerateNextSpeakerAsync(
                session,
                requestedSpeaker?.Id,
                async assistant =>
                {
                    var speaker = GroupMembers.FirstOrDefault(x => x.Id == assistant.SpeakerCharacterId);
                    await OnUiAsync(() =>
                    {
                        assistantViewModel = new ChatMessageViewModel(
                            assistant,
                            assistantName: speaker?.Name,
                            assistantAvatarPath: speaker?.AvatarPath,
                            characterId: assistant.SpeakerCharacterId);
                        assistantViewModel.IsStreaming = true;
                        Messages.Add(assistantViewModel);
                    });
                },
                async (_, chunk) => await OnUiAsync(() =>
                {
                    if (assistantViewModel is not null) assistantViewModel.Content += chunk;
                }),
                cancellation.Token);
            if (assistantViewModel is not null && !string.IsNullOrEmpty(assistantViewModel.Content))
            {
                assistantViewModel.SetSwipeState(0, 1, assistantViewModel.Content);
                if (!performance) await RefreshReplySuggestionsAsync(cancellation.Token);
                await UpdateCharacterStatusAsync(assistantViewModel.Model, cancellation.Token);
            }
            await RefreshSessionsAsync(session.Id);
            await RefreshTokenEstimateAsync();
            if (cancellation.IsCancellationRequested) return false;
            if (assistantViewModel is not null && !string.IsNullOrWhiteSpace(assistantViewModel.Content)) return true;
            ErrorMessage = "模型未返回内容，演出已暂停。";
            return false;
        }
        catch (ProviderException ex) { ErrorMessage = ex.Message; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Group chat next speaker generation failed.");
            ErrorMessage = "群聊生成失败，请查看日志后重试。";
        }
        finally
        {
            if (assistantViewModel is not null) assistantViewModel.IsStreaming = false;
            if (assistantViewModel is not null && string.IsNullOrEmpty(assistantViewModel.Content))
                Messages.Remove(assistantViewModel);
            EndGeneration(cancellation);
        }
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanCreateChat))]
    private async Task NewChatAsync()
    {
        if (!CanModifyConversation) return;
        using var navigation = BeginSessionNavigation();
        // Reuse the current empty conversation, preserving any unsent draft.
        // Otherwise reuse an empty ordinary session from the saved conversation list.
        if (_session is { CharacterId: null, IsGroupChat: false, ParentSessionId: null }
            && Messages.Count == 0)
            return;

        var session = await chatService.GetOrCreateEmptySessionAsync();
        await RefreshSessionsAsync(session.Id);
        await LoadSessionAsync(session);
        ErrorMessage = null;
    }

    public async Task DeleteCurrentChatAsync()
    {
        if (_session is null || _isDeletingSession) return;
        _isDeletingSession = true;
        RefreshConversationCommands();
        try
        {
            StopGroupPerformance();
            if (IsGenerating)
            {
                var completion = _generationCompletion?.Task;
                _generationCancellation?.Cancel();
                if (completion is not null) await completion;
            }

            var deletedId = _session?.Id;
            if (deletedId is null) return;
            CancelDraftFlush();
            _drafts.Forget(deletedId.Value);
            _queue.Clear(deletedId.Value);
            Interlocked.Increment(ref _sessionLoadRequestId);
            ClearReplySuggestions();
            await chatService.DeleteSessionAsync(deletedId.Value);
            if (_session?.Id == deletedId.Value) _session = null;

            var sessions = await chatService.GetSessionsAsync();
            var next = sessions.FirstOrDefault() ?? await chatService.CreateSessionAsync();
            await RefreshSessionsAsync(next.Id);
            await LoadSessionAsync(next);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting the current conversation failed.");
            ErrorMessage = "删除对话失败：" + ex.Message;
            if (_session is null)
            {
                var recovery = (await chatService.GetSessionsAsync()).FirstOrDefault()
                               ?? await chatService.CreateSessionAsync();
                await RefreshSessionsAsync(recovery.Id);
                await LoadSessionAsync(recovery);
            }
        }
        finally
        {
            _isDeletingSession = false;
            RefreshConversationCommands();
        }
    }

    [RelayCommand]
    private void BeginEdit(ChatMessageViewModel? message)
    {
        if (CanModifyConversation && IsCurrentMessage(message)) message!.BeginEdit();
    }

    [RelayCommand]
    private void CancelEdit(ChatMessageViewModel? message) => message?.CancelEdit();

    [RelayCommand]
    private async Task SaveEditAsync(ChatMessageViewModel? message)
    {
        if (!IsCurrentMessage(message) || !CanModifyConversation) return;
        var session = _session!;
        if (string.IsNullOrWhiteSpace(message.EditText))
        {
            ErrorMessage = "消息内容不能为空。";
            return;
        }
        message.FinishEdit();
        await chatService.UpdateMessageAsync(message.Model);
        await RefreshAutomaticSummaryAfterMessageChangeAsync(session);
        if (!IsCurrentMessage(message)) return;
        if (message.IsAssistant)
        {
            message.SetSwipeState(0, 1, message.Content);
            await RefreshReplySuggestionsAsync();
            if (!IsCurrentMessage(message)) return;
            await UpdateCharacterStatusAsync(message.Model);
        }
        ErrorMessage = null;
    }

    public async Task DeleteMessageAsync(ChatMessageViewModel message)
    {
        if (!CanModifyConversation || !IsCurrentMessage(message)) return;
        var session = _session!;
        ClearReplySuggestions();
        await chatService.DeleteMessageAsync(message.Model.Id);
        await RefreshAutomaticSummaryAfterMessageChangeAsync(session);
        if (!IsCurrentMessage(message)) return;
        Messages.Remove(message);
        RefreshPinnedMessages();
        ErrorMessage = null;
    }

    [ObservableProperty] private bool _hasPinnedMessages;
    [ObservableProperty] private bool _isPinnedListOpen;
    [ObservableProperty] private bool _isStorySummaryOpen;
    [ObservableProperty] private bool _hasStorySummary;
    [ObservableProperty] private bool _isStorySummaryManual;
    [ObservableProperty] private int _storySummaryCoveredCount;
    public ObservableCollection<SpeakerFilterOption> SpeakerFilters { get; } = [];
    [ObservableProperty] private SpeakerFilterOption? _selectedSpeakerFilter;
    [ObservableProperty] private bool _hasSpeakerFilters;
    [ObservableProperty] private bool _isCurrentSessionPinned;
    [ObservableProperty] private string _sessionGroup = string.Empty;
    [ObservableProperty] private string _storySummary = string.Empty;
    [ObservableProperty] private string _storySummaryStatus = string.Empty;

    public ObservableCollection<ChatMessageViewModel> PinnedMessages { get; } = [];

    [ObservableProperty] private bool _isStatsOpen;
    [ObservableProperty] private IReadOnlyList<StatLine> _statLines = [];
    [ObservableProperty] private bool _hasSpeakerShares;
    [ObservableProperty] private bool _hasRepeatedPhrases;
    public ObservableCollection<SpeakerShare> SpeakerShares { get; } = [];
    public ObservableCollection<RepeatedPhrase> RepeatedPhrases { get; } = [];
    private long _phraseScanId;

    [RelayCommand]
    private void OpenStats()
    {
        var written = Messages.Where(x => !x.IsStreaming).ToList();
        StatLines = ConversationStatistics.BuildLines(
            written, written.FirstOrDefault()?.Model.CreatedAt, written.LastOrDefault()?.Model.CreatedAt);
        SpeakerShares.Clear();
        foreach (var share in ConversationStatistics.BuildSpeakerShares(written)) SpeakerShares.Add(share);
        HasSpeakerShares = SpeakerShares.Count > 1;
        IsStatsOpen = true;
        // Scanning every reply for pet phrases is the slow part of this panel, so the numbers appear
        // first and the phrases follow once the scan finishes off the UI thread.
        _ = FindRepeatedPhrasesAsync(written.Where(x => x.IsAssistant).Select(x => x.Content).ToArray());
    }

    private async Task FindRepeatedPhrasesAsync(string[] replies)
    {
        var requestId = Interlocked.Increment(ref _phraseScanId);
        RepeatedPhrases.Clear();
        HasRepeatedPhrases = false;
        var found = await Task.Run(() => RepeatedPhraseFinder.Find(replies));
        if (requestId != Volatile.Read(ref _phraseScanId)) return;
        await OnUiAsync(() =>
        {
            foreach (var phrase in found) RepeatedPhrases.Add(phrase);
            HasRepeatedPhrases = RepeatedPhrases.Count > 0;
        });
    }

    /// Turning a tic into a rewrite rule is the point of spotting it, so one click removes the phrase
    /// from everything the model writes from now on.
    [RelayCommand]
    private async Task CreateRewriteRuleAsync(RepeatedPhrase? phrase)
    {
        if (phrase is null) return;
        var existing = await regexScripts.GetScriptsAsync();
        if (existing.Any(x => x.Target == RegexScriptTarget.AssistantOutput && x.Pattern == phrase.Phrase))
        {
            ErrorMessage = $"已经有规则在处理「{phrase.Phrase}」了，可到提示词工作室的文案脚本里修改它。";
            return;
        }
        var now = DateTimeOffset.UtcNow;
        await regexScripts.SaveAsync(new RegexScript
        {
            Name = $"去掉「{phrase.Phrase}」",
            Pattern = phrase.Phrase,
            Replacement = string.Empty,
            Target = RegexScriptTarget.AssistantOutput,
            Mode = RegexScriptMode.Literal,
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task TogglePinAsync(ChatMessageViewModel? message)
    {
        if (!IsCurrentMessage(message) || !CanModifyConversation) return;
        message.IsPinned = !message.IsPinned;
        await chatService.SetMessagePinnedAsync(message.Model, message.IsPinned);
        if (!IsCurrentMessage(message)) return;
        RefreshPinnedMessages();
        ErrorMessage = null;
    }

    [RelayCommand]
    private void OpenPinnedList()
    {
        RefreshPinnedMessages();
        IsPinnedListOpen = HasPinnedMessages;
    }

    [RelayCommand]
    private void JumpToPinned(ChatMessageViewModel? message)
    {
        if (message is null) return;
        IsPinnedListOpen = false;
        ClearSpeakerFilterIfNeeded(message);
        JumpToMessageRequested?.Invoke(message.Model.Id);
    }

    partial void OnSelectedSpeakerFilterChanged(SpeakerFilterOption? value)
    {
        MessagesView.Filter = message => message is not ChatMessageViewModel item
                                         || value?.CharacterId is not long id
                                         || SpeakerKeyOf(item) == id;
        MessagesView.Refresh();
        HasSpeakerFilters = SpeakerFilters.Count > 1;
        foreach (var option in SpeakerFilters) option.IsSelected = ReferenceEquals(option, value) || option.CharacterId == value?.CharacterId;
    }

    [RelayCommand]
    private void SelectSpeakerFilter(SpeakerFilterOption? option) =>
        SelectedSpeakerFilter = option is null || option.CharacterId is null ? null : option;

    /// <summary>A message's speaker: group turns carry their own, single chats fall back to the session character.</summary>
    private long? SpeakerKeyOf(ChatMessageViewModel message) =>
        message.IsAssistant ? message.CharacterId ?? _session?.CharacterId : null;

    private ICollectionView MessagesView => CollectionViewSource.GetDefaultView(Messages);

    /// <summary>A jump should land on its message, so a filter that hides it is lifted first.</summary>
    private void ClearSpeakerFilterIfNeeded(ChatMessageViewModel message)
    {
        if (SelectedSpeakerFilter?.CharacterId is long id && SpeakerKeyOf(message) != id) SelectedSpeakerFilter = null;
    }

    private void RebuildSpeakerFilters()
    {
        var speakers = Messages
            .Select(SpeakerKeyOf)
            .Where(id => id is not null)
            .Distinct()
            .ToDictionary(id => id!.Value, id => Messages.First(x => SpeakerKeyOf(x) == id).RoleLabel);
        HasSpeakerFilters = speakers.Count > 1;
        if (speakers.Count == SpeakerFilters.Count - 1 &&
            SpeakerFilters.Skip(1).All(option => speakers.ContainsKey(option.CharacterId!.Value))) return;
        var selected = SelectedSpeakerFilter?.CharacterId;
        SpeakerFilters.Clear();
        SpeakerFilters.Add(new SpeakerFilterOption(null, "全部") { IsSelected = selected is null });
        foreach (var speaker in speakers)
            SpeakerFilters.Add(new SpeakerFilterOption(speaker.Key, speaker.Value) { IsSelected = selected == speaker.Key });
        if (selected is not null && !speakers.ContainsKey(selected.Value)) SelectedSpeakerFilter = null;
    }

    private void RefreshPinnedMessages()
    {
        PinnedMessages.Clear();
        RebuildSpeakerFilters();
        foreach (var message in Messages.Where(x => x.IsPinned)) PinnedMessages.Add(message);
        HasPinnedMessages = PinnedMessages.Count > 0;
        if (!HasPinnedMessages) IsPinnedListOpen = false;
    }

    [RelayCommand]
    private void OpenStorySummary()
    {
        if (_session is null) return;
        StorySummary = _session.Summary;
        StorySummaryStatus = string.Empty;
        RefreshStorySummaryState();
        IsStorySummaryOpen = true;
    }

    [RelayCommand]
    private async Task SaveStorySummaryAsync()
    {
        if (_session is null || !CanModifyConversation) return;
        var session = _session;
        try
        {
            await chatService.SaveStorySummaryAsync(session, StorySummary);
            if (!ApplyStorySummaryToCurrentSession(session)) return;
            StorySummaryStatus = HasStorySummary
                ? "已保存，自动摘要暂停；点“重新生成”可恢复。"
                : "已清空剧情回顾。";
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            if (_session?.Id == session.Id) StorySummaryStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RebuildStorySummaryAsync()
    {
        if (_session is null || !CanModifyConversation) return;
        var session = _session;
        await chatService.RebuildStorySummaryAsync(session);
        if (!ApplyStorySummaryToCurrentSession(session)) return;
        StorySummaryStatus = HasStorySummary
            ? $"已根据最早的 {StorySummaryCoveredCount} 条消息重新整理。"
            : "消息还不够多，暂时没有可整理的早期剧情。";
    }

    [RelayCommand]
    private async Task ClearStorySummaryAsync()
    {
        if (_session is null || !CanModifyConversation) return;
        var session = _session;
        await chatService.ClearStorySummaryAsync(session);
        if (!ApplyStorySummaryToCurrentSession(session)) return;
        StorySummaryStatus = "已清空，后续回复会重新自动生成。";
    }

    private void RefreshStorySummaryState()
    {
        if (_session is null) return;
        HasStorySummary = !string.IsNullOrWhiteSpace(_session.Summary);
        IsStorySummaryManual = _session.SummaryIsManual;
        StorySummaryCoveredCount = _session.SummaryCoveredCount;
        OnPropertyChanged(nameof(StorySummaryCoverage));
    }

    public string StorySummaryCoverage => StorySummaryCoveredCount == 0
        ? "还没有整理过早期剧情，模型目前只看到最近的对话。"
        : IsStorySummaryManual
            ? $"手动编辑中，自动整理已暂停（已覆盖最早 {StorySummaryCoveredCount} 条消息）"
            : $"自动整理，已覆盖最早 {StorySummaryCoveredCount} 条消息";

    public ObservableCollection<CharacterMemory> CharacterMemories { get; } = [];
    [ObservableProperty] private bool _isMemoryOpen;
    [ObservableProperty] private bool _hasMemoryTarget;
    [ObservableProperty] private bool _isExtractingMemory;
    [ObservableProperty] private bool _hasMemories;
    [ObservableProperty] private string _memoryStatus = string.Empty;
    [ObservableProperty] private string _newMemoryText = string.Empty;
    private long _memoryReadRequestId;
    public bool CanExtractMemory => !IsExtractingMemory;
    partial void OnIsExtractingMemoryChanged(bool value) => OnPropertyChanged(nameof(CanExtractMemory));

    [RelayCommand]
    private async Task OpenMemoryAsync()
    {
        if (SelectedMemoryCharacter?.Id is not long characterId) return;
        IsMemoryOpen = true;
        await ReloadMemoriesAsync(characterId);
        MemoryStatus = string.Empty;
    }

    [RelayCommand]
    private async Task AddMemoryAsync()
    {
        if (SelectedMemoryCharacter?.Id is not long characterId) return;
        try
        {
            await memoryService.AddManualAsync(characterId, NewMemoryText);
            NewMemoryText = string.Empty;
            await ReloadMemoriesAsync(characterId);
            MemoryStatus = "已添加，下次请求起会随对话一起发送。";
        }
        catch (InvalidOperationException ex)
        {
            MemoryStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteMemoryAsync(CharacterMemory? memory)
    {
        if (memory is null) return;
        await memoryService.DeleteAsync(memory.Id);
        CharacterMemories.Remove(memory);
        HasMemories = CharacterMemories.Count > 0;
    }

    [RelayCommand]
    private async Task ExtractMemoryNowAsync()
    {
        if (_session is null || SelectedMemoryCharacter?.Id is not long characterId || IsExtractingMemory) return;
        var session = _session;
        IsExtractingMemory = true;
        MemoryStatus = "正在从对话中提炼记忆…";
        try
        {
            var before = CharacterMemories.Count;
            await memoryService.ExtractIfNeededAsync(session, force: true);
            if (_session?.Id != session.Id || SelectedMemoryCharacter?.Id != characterId) return;
            await ReloadMemoriesAsync(characterId);
            var added = CharacterMemories.Count - before;
            MemoryStatus = added > 0 ? $"新增 {added} 条记忆。" : "没有提炼出新的记忆。";
        }
        finally { IsExtractingMemory = false; }
    }

    private async Task ReloadMemoriesAsync(long characterId)
    {
        var requestId = Interlocked.Increment(ref _memoryReadRequestId);
        var memories = await memoryService.GetAsync(characterId);
        if (SelectedMemoryCharacter?.Id != characterId || requestId != Volatile.Read(ref _memoryReadRequestId)) return;
        CharacterMemories.Clear();
        foreach (var memory in memories) CharacterMemories.Add(memory);
        HasMemories = CharacterMemories.Count > 0;
    }

    [RelayCommand]
    private async Task SwipeLeftAsync(ChatMessageViewModel? message)
    {
        if (!IsCurrentMessage(message) || !CanModifyConversation || !message!.CanSwipeLeft) return;
        await SelectSwipeAsync(message, message.Model.CurrentSwipeIndex - 1);
    }

    [RelayCommand]
    private async Task SwipeRightAsync(ChatMessageViewModel? message)
    {
        if (!IsCurrentMessage(message) || !CanModifyConversation || !message!.IsAssistant) return;
        if (message.Model.CurrentSwipeIndex + 1 < message.SwipeCount)
            await SelectSwipeAsync(message, message.Model.CurrentSwipeIndex + 1);
        else
            await GenerateAlternativeCoreAsync(message);
    }

    [RelayCommand]
    private Task RegenerateAsync(ChatMessageViewModel? message) =>
        message is null ? Task.CompletedTask : GenerateAlternativeCoreAsync(message);

    public Task RegenerateLastAsync()
    {
        var last = Messages.LastOrDefault(x => x.IsAssistant);
        return last is null ? Task.CompletedTask : GenerateAlternativeCoreAsync(last);
    }

    [RelayCommand(CanExecute = nameof(CanContinueLastReply))]
    private Task ContinueLastReplyAsync(ChatMessageViewModel? message) =>
        message is null ? Task.CompletedTask : ContinueReplyAsync(message);

    private bool CanContinueLastReply() => CanModifyConversation && HasProviderConfiguration && _session is not null;

    private async Task ContinueReplyAsync(ChatMessageViewModel message)
    {
        if (_session is null || !IsCurrentMessage(message) || !CanModifyConversation || !message.IsAssistant) return;
        if (!ReferenceEquals(message, Messages.LastOrDefault()))
        {
            ErrorMessage = "只能续写最后一条助手回复。";
            return;
        }
        if (string.IsNullOrWhiteSpace(message.Content))
        {
            ErrorMessage = "这条回复还没有内容，请先重新生成。";
            return;
        }

        ErrorMessage = null;
        using var cancellation = BeginGeneration();
        message.IsStreaming = true;
        try
        {
            var result = await chatService.ContinueAssistantAsync(
                _session, message.Model,
                async content => await OnUiAsync(() => message.Content = content),
                cancellation.Token);
            message.SetSwipeState(result.Message.CurrentSwipeIndex, result.SwipeCount, result.Message.Content);
            await RefreshReplySuggestionsAsync(cancellation.Token);
            await UpdateCharacterStatusAsync(message.Model, cancellation.Token);
            await RefreshSessionsAsync(_session.Id);
        }
        catch (ProviderException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Continuing the reply failed.");
            ErrorMessage = "续写失败，请查看日志后重试。";
        }
        finally
        {
            message.IsStreaming = false;
            try
            {
                var count = await chatService.GetSwipeCountAsync(message.Model);
                message.SetSwipeState(message.Model.CurrentSwipeIndex, count, message.Content);
            }
            finally { EndGeneration(cancellation); }
        }
    }

    [RelayCommand]
    private void OpenSettings() => ConfigureRequested?.Invoke();

    [RelayCommand]
    private void OpenPromptInspector() => PromptInspectorRequested?.Invoke();

    public void AddImages(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(AttachmentService.IsSupportedImage).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!PendingImagePaths.Contains(path, StringComparer.OrdinalIgnoreCase)) PendingImagePaths.Add(path);
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemovePendingImage(string? path)
    {
        if (path is not null) PendingImagePaths.Remove(path);
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectReplySuggestion(string? suggestion)
    {
        if (!string.IsNullOrWhiteSpace(suggestion)) InputText = suggestion;
    }

    private async Task RefreshReplySuggestionsAsync(CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null) return;
        _suggestionCancellation?.Cancel();
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _suggestionCancellation = linkedCancellation;
        var requestId = Interlocked.Increment(ref _suggestionRequestId);
        ReplySuggestions.Clear();
        HasReplySuggestions = false;
        IsGeneratingSuggestions = true;
        try
        {
            var suggestions = await replySuggestionService.GenerateAsync(session, linkedCancellation.Token);
            if (requestId != Volatile.Read(ref _suggestionRequestId) || _session?.Id != session.Id) return;
            ReplySuggestions.Clear();
            foreach (var suggestion in suggestions) ReplySuggestions.Add(suggestion);
            HasReplySuggestions = ReplySuggestions.Count > 0;
        }
        finally
        {
            if (ReferenceEquals(_suggestionCancellation, linkedCancellation))
            {
                _suggestionCancellation = null;
            }
            linkedCancellation.Dispose();
            if (requestId == Volatile.Read(ref _suggestionRequestId)) IsGeneratingSuggestions = false;
        }
    }

    private void ClearReplySuggestions()
    {
        _suggestionCancellation?.Cancel();
        _suggestionCancellation = null;
        Interlocked.Increment(ref _suggestionRequestId);
        ReplySuggestions.Clear();
        HasReplySuggestions = false;
        IsGeneratingSuggestions = false;
    }

    public Task<PromptBuildResult?> GetPromptPreviewAsync() =>
        _session is null ? Task.FromResult<PromptBuildResult?>(null) : GetPreviewAsync(_session);

    private async Task<PromptBuildResult?> GetPreviewAsync(ChatSession session) => await chatService.GetPromptPreviewAsync(session);

    private async Task SelectSwipeAsync(ChatMessageViewModel message, int index)
    {
        if (!IsCurrentMessage(message) || !CanModifyConversation) return;
        var session = _session!;
        try
        {
            var result = await chatService.SelectSwipeAsync(message.Model, index);
            await RefreshAutomaticSummaryAfterMessageChangeAsync(session);
            if (!IsCurrentMessage(message)) return;
            message.SetSwipeState(result.Message.CurrentSwipeIndex, result.SwipeCount, result.Message.Content);
            await RefreshReplySuggestionsAsync();
            if (!IsCurrentMessage(message)) return;
            await UpdateCharacterStatusAsync(message.Model);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Selecting message swipe failed.");
            if (IsCurrentMessage(message)) ErrorMessage = "切换候选回复失败。";
        }
    }

    private async Task GenerateAlternativeCoreAsync(ChatMessageViewModel message)
    {
        if (_session is null || !IsCurrentMessage(message) || !CanModifyConversation || !message.IsAssistant) return;
        if (!ReferenceEquals(message, Messages.LastOrDefault()))
        {
            ErrorMessage = "只能为聊天末尾的助手消息生成新候选。";
            return;
        }
        if (!HasProviderConfiguration)
        {
            ErrorMessage = "尚未配置模型 Provider，请先前往 Settings。";
            return;
        }

        ErrorMessage = null;
        using var cancellation = BeginGeneration();
        message.IsStreaming = true;
        try
        {
            var result = await chatService.GenerateAlternativeAsync(
                _session, message.Model,
                async content => await OnUiAsync(() => message.Content = content),
                cancellation.Token);
            message.SetSwipeState(result.Message.CurrentSwipeIndex, result.SwipeCount, result.Message.Content);
            await RefreshReplySuggestionsAsync(cancellation.Token);
            await UpdateCharacterStatusAsync(message.Model, cancellation.Token);
            await RefreshSessionsAsync(_session.Id);
        }
        catch (ProviderException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Alternative generation failed.");
            ErrorMessage = "重新生成失败，请查看日志后重试。";
        }
        finally
        {
            message.IsStreaming = false;
            try
            {
                var count = await chatService.GetSwipeCountAsync(message.Model);
                message.SetSwipeState(message.Model.CurrentSwipeIndex, count, message.Content);
            }
            finally { EndGeneration(cancellation); }
        }
    }

    internal async Task LoadSessionCoreAsync(ChatSession session, bool requireSelectedSession = false)
    {
        if (_session?.Id != session.Id) StopGroupPerformance();
        _titleCancellation?.Cancel();
        var requestId = Interlocked.Increment(ref _sessionLoadRequestId);
        var composerAtLoadStart = _navigationComposer is not null && _navigationComposerSessionId == _session?.Id
            ? _navigationComposer : DraftStore.Capture(InputText, PendingImagePaths);
        _drafts.OnLoadStarted(_session?.Id, composerAtLoadStart);
        if (_session?.Id is long outgoing && outgoing != session.Id)
        {
            CancelDraftFlush();
            await FlushDraftAsync(outgoing, composerAtLoadStart);
        }
        var members = session.IsGroupChat
            ? await chatService.GetGroupMembersAsync(session.Id)
            : [];
        var character = session.CharacterId is long characterId
            ? await chatService.GetCharacterAsync(characterId) : null;
        var assistantName = character?.Name ?? "Lorehaven";
        var assistantAvatarPath = character?.AvatarPath ?? string.Empty;
        var bundle = await chatService.GetSessionBundleAsync(session.Id);
        var messageViewModels = new List<ChatMessageViewModel>();
        foreach (var message in bundle.Messages)
        {
            var speaker = members.FirstOrDefault(x => x.Id == message.SpeakerCharacterId);
            messageViewModels.Add(new ChatMessageViewModel(
                message,
                bundle.SwipeCountFor(message.Id),
                bundle.AttachmentsFor(message.Id),
                speaker?.Name ?? assistantName,
                speaker?.AvatarPath ?? assistantAvatarPath,
                message.SpeakerCharacterId ?? session.CharacterId));
        }

        var estimatedTokens = await GetTokenEstimateAsync(session, bundle.Messages);
        if (requestId != Volatile.Read(ref _sessionLoadRequestId) ||
            (requireSelectedSession && SelectedSession?.Id != session.Id)) return;

        _session = session;
        InvalidateSearch();
        ClearReplySuggestions();
        IsGroupChat = session.IsGroupChat;
        IsBranch = session.ParentSessionId is not null;
        GroupMembers.Clear();
        foreach (var member in members) GroupMembers.Add(member);
        CurrentAssistantName = assistantName;
        CurrentAssistantAvatarPath = assistantAvatarPath;
        SessionTitle = session.Title;
        JournalDescription = !string.IsNullOrWhiteSpace(session.AuthorNote) ? session.AuthorNote
            : character?.Scenario ?? members.FirstOrDefault()?.Scenario ?? string.Empty;
        SetSelectedSession(session.Id);
        SelectedPersona = Personas.FirstOrDefault(x => x.Id == session.PersonaId);
        SelectedLorebook = Lorebooks.FirstOrDefault(x => x.Id == session.LorebookId);
        SelectedPreset = Presets.FirstOrDefault(x => x.Id == session.PromptPresetId);
        AuthorNote = session.AuthorNote;
        StorySummary = session.Summary;
        IsCurrentSessionPinned = session.IsPinned;
        SessionGroup = session.GroupName;
        StorySummaryStatus = string.Empty;
        IsStorySummaryOpen = false;
        IsStatsOpen = false;
        IsMemoryOpen = false;
        MemoryStatus = string.Empty;
        CharacterMemories.Clear();
        HasMemories = false;
        MemoryCharacters.Clear();
        if (session.IsGroupChat)
            foreach (var member in members) MemoryCharacters.Add(member);
        else if (character is not null) MemoryCharacters.Add(character);
        SelectedMemoryCharacter = MemoryCharacters.FirstOrDefault();
        HasMemoryTarget = MemoryCharacters.Count > 0;
        var draft = _drafts.OnLoadFinished(
            session.Id, composerAtLoadStart, DraftStore.Capture(InputText, PendingImagePaths));
        InputText = draft.Text;
        PendingImagePaths.Clear();
        // Attachments are files on disk, and a draft can outlive them.
        foreach (var image in draft.Images.Where(File.Exists)) PendingImagePaths.Add(image);
        RefreshStorySummaryState();
        Messages.ReplaceAll(messageViewModels);
        RefreshPinnedMessages();
        EstimatedPromptTokens = estimatedTokens;
        NextSpeakerCommand.NotifyCanExecuteChanged();
        BranchFromMessageCommand.NotifyCanExecuteChanged();
        OpenParentConversationCommand.NotifyCanExecuteChanged();
        GenerateConversationTitleCommand.NotifyCanExecuteChanged();
        RefreshQueuedSends();
        RefreshJournalBranches();
        if (Messages.LastOrDefault()?.IsAssistant == true)
            _ = RefreshReplySuggestionsAsync();
        // Messages queued earlier go out now that this conversation is on screen and idle.
        _ = DrainQueueAsync(session.Id);
    }

    private async Task RefreshSessionsAsync(long selectedId, long? onlyForCurrentSessionId = null)
    {
        var sessions = await chatService.GetSessionsAsync();
        if (onlyForCurrentSessionId is long required &&
            (_session?.Id != required || SelectedSession?.Id != required)) return;
        _suppressSessionSelection = true;
        Sessions.Clear();
        foreach (var session in sessions) Sessions.Add(session);
        SelectedSession = Sessions.FirstOrDefault(x => x.Id == selectedId);
        _suppressSessionSelection = false;
        RefreshJournalBranches();
    }

    private void SetSelectedSession(long id)
    {
        _suppressSessionSelection = true;
        SelectedSession = Sessions.FirstOrDefault(x => x.Id == id);
        _suppressSessionSelection = false;
    }

    private CancellationTokenSource BeginGeneration()
    {
        IsGenerating = true;
        _generationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _generationCancellation = new CancellationTokenSource();
        return _generationCancellation;
    }

    private void EndGeneration(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_generationCancellation, cancellation)) _generationCancellation = null;
        IsGenerating = false;
        _generationCompletion?.TrySetResult();
        _generationCompletion = null;
    }

    /// Sending during generation is allowed on purpose: the message joins the queue instead of being
    /// swallowed by a disabled button.
    private bool CanSend() => HasProviderConfiguration && !IsSessionLoading && !_isDeletingSession &&
                              (!string.IsNullOrWhiteSpace(InputText) || PendingImagePaths.Count > 0);
    private bool CanStop() => IsGenerating || IsGroupPerformanceRunning;
    private bool CanCreateChat() => CanModifyConversation;
    private bool CanGenerateNextSpeaker() => CanModifyConversation && IsGroupChat &&
                                             HasProviderConfiguration && GroupMembers.Count >= 2;

    partial void OnSelectedSessionChanged(ChatSession? value)
    {
        if (_suppressSessionSelection || value is null || value.Id == _session?.Id) return;
        _titleCancellation?.Cancel();
        if (IsGenerating || _isDeletingSession)
        {
            if (_session is not null) SetSelectedSession(_session.Id);
            return;
        }
        StopGroupPerformance();
        _ = LoadSessionSafelyAsync(value);
    }

    private async Task LoadSessionSafelyAsync(ChatSession session)
    {
        using var navigation = BeginSessionNavigation();
        try
        {
            var stored = await chatService.GetSessionAsync(session.Id);
            if (stored is null)
            {
                if (SelectedSession?.Id == session.Id && _session is not null)
                    SetSelectedSession(_session.Id);
                return;
            }
            if (SelectedSession?.Id != session.Id) return;
            await LoadSessionAsync(stored, requireSelectedSession: true);
            if (SelectedSession?.Id == session.Id) ErrorMessage = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading chat session failed.");
            if (SelectedSession?.Id == session.Id) ErrorMessage = "加载聊天记录失败。";
        }
    }

    private async Task RefreshTokenEstimateAsync()
    {
        var session = _session;
        var tokens = session is null ? 0 : await GetTokenEstimateAsync(session);
        if (_session?.Id == session?.Id) EstimatedPromptTokens = tokens;
    }

    private async Task RefreshAutomaticSummaryAfterMessageChangeAsync(ChatSession session)
    {
        if (session.SummaryIsManual || (session.SummaryCoveredCount <= 0 &&
            !session.Summary.StartsWith(ConversationSummaryService.Header, StringComparison.Ordinal))) return;
        // Navigation may have reloaded this session, or the writer may have saved a manual recap.
        var stored = await chatService.GetSessionAsync(session.Id);
        if (stored is null) return;
        if (!stored.SummaryIsManual && (stored.SummaryCoveredCount > 0 ||
            stored.Summary.StartsWith(ConversationSummaryService.Header, StringComparison.Ordinal)))
            await chatService.RebuildStorySummaryAsync(stored);
        session.Summary = stored.Summary;
        session.SummaryCoveredCount = stored.SummaryCoveredCount;
        session.SummaryIsManual = stored.SummaryIsManual;
        ApplyStorySummaryToCurrentSession(session);
    }

    private bool ApplyStorySummaryToCurrentSession(ChatSession session)
    {
        if (_session?.Id != session.Id || SelectedSession?.Id != session.Id) return false;
        // Returning to the same conversation can replace its model while the rebuild is pending.
        _session.Summary = session.Summary;
        _session.SummaryCoveredCount = session.SummaryCoveredCount;
        _session.SummaryIsManual = session.SummaryIsManual;
        StorySummary = session.Summary;
        RefreshStorySummaryState();
        return true;
    }

    // Context menus can still hold a bubble after navigation or deletion has replaced the list.
    private bool IsCurrentMessage([NotNullWhen(true)] ChatMessageViewModel? message) => message is not null &&
        _session?.Id == message.Model.ChatSessionId && Messages.Contains(message);

    private async Task UpdateCharacterStatusAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (_session is null || message.ChatSessionId != _session.Id || !IsCharacterStatusPluginEnabled) return;
        var characterId = message.SpeakerCharacterId ?? _session.CharacterId;
        if (characterId is long id)
            await characterStatusService.UpdateAsync(_session, id, message.Id, cancellationToken);
    }

    private void OnPluginsChanged()
    {
        if (Application.Current is null) return;
        _ = Application.Current.Dispatcher.InvokeAsync(RefreshCharacterStatusPluginAsync);
    }

    private async Task<int> GetTokenEstimateAsync(
        ChatSession session, IReadOnlyList<ChatMessage>? loadedMessages = null)
    {
        try { return (await chatService.GetPromptPreviewAsync(session, loadedMessages)).EstimatedTokens; }
        catch { return 0; }
    }

    partial void OnInputTextChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
        ScheduleDraftFlush();
    }

    /// Drafts reach the database when typing pauses rather than on every keystroke.
    private void ScheduleDraftFlush()
    {
        // While navigation is pending the box belongs to the incoming conversation. Persist it once
        // that conversation owns the composer, keeping the outgoing draft at its original snapshot.
        if (IsSessionLoading || _session?.Id is not long sessionId) return;
        var flushId = Interlocked.Increment(ref _draftFlushId);
        var draft = DraftStore.Capture(InputText, PendingImagePaths);
        _ = FlushDraftWhenIdleAsync(flushId, sessionId, draft);
    }

    /// Drops a flush that is still waiting, so nothing resurrects a draft the composer no longer owns.
    private void CancelDraftFlush() => Interlocked.Increment(ref _draftFlushId);

    private async Task FlushDraftWhenIdleAsync(long flushId, long sessionId, DraftStore.Draft draft)
    {
        await Task.Delay(800);
        if (flushId != Volatile.Read(ref _draftFlushId)) return;
        await FlushDraftAsync(sessionId, draft);
    }

    private async Task FlushDraftAsync(long sessionId, DraftStore.Draft draft)
    {
        _drafts.Remember(sessionId, draft);
        try { await draftRepository.SaveAsync(sessionId, draft.Text, draft.Images); }
        catch (Exception ex) { logger.LogError(ex, "Saving an unsent draft failed."); }
    }
    partial void OnIsGeneratingChanged(bool value)
    {
        if (value) _titleCancellation?.Cancel();
        OnPropertyChanged(nameof(CanModifyConversation));
        ContinueLastReplyCommand.NotifyCanExecuteChanged();
        GenerateConversationTitleCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        NewChatCommand.NotifyCanExecuteChanged();
        NextSpeakerCommand.NotifyCanExecuteChanged();
        BranchFromMessageCommand.NotifyCanExecuteChanged();
        OpenParentConversationCommand.NotifyCanExecuteChanged();
        RefreshGroupPerformanceCommands();
    }
    partial void OnHasProviderConfigurationChanged(bool value)
    {
        GenerateConversationTitleCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        NextSpeakerCommand.NotifyCanExecuteChanged();
        RefreshGroupPerformanceCommands();
    }
    partial void OnIsGroupChatChanged(bool value) => RefreshGroupPerformanceCommands();
}
