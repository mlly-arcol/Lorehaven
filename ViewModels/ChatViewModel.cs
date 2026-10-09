using System.Collections.ObjectModel;
using System.ComponentModel;
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
    ILogger<ChatViewModel> logger) : ObservableObject
{
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
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
    [ObservableProperty] private string _currentAssistantName = "NativeTavern";
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
    private bool _suppressSessionSelection;
    private long _sessionLoadRequestId;
    private long _suggestionRequestId;
    private bool _isDeletingSession;

    public event Action? ConfigureRequested;
    public event Action? PromptInspectorRequested;

    public ChatSession? CurrentSession => _session;

    public async Task InitializeAsync()
    {
        pluginService.PluginsChanged += OnPluginsChanged;
        if (!_draftHooksWired)
        {
            _draftHooksWired = true;
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
        if (_session is null || !message.IsAssistant) return null;
        var characterId = message.CharacterId;
        if (characterId is not long id) return null;
        return await characterStatusService.GetAsync(_session.Id, id)
               ?? await characterStatusService.UpdateAsync(_session, id, message.Model.Id);
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
        if (_session is null || IsGenerating) return;
        await chatService.UpdateSessionPromptAsync(
            _session, SelectedPersona?.Id, SelectedLorebook?.Id, SelectedPreset?.Id, AuthorNote.Trim());
        try
        {
            await chatService.SetSessionGroupAsync(_session, SessionGroup);
            SessionGroup = _session.GroupName;
            ErrorMessage = null;
        }
        catch (InvalidOperationException ex)
        {
            SessionGroup = _session.GroupName;
            ErrorMessage = ex.Message;
        }
        await RefreshSessionsAsync(_session.Id);
        await RefreshSessionGroupsAsync();
        await RefreshTokenEstimateAsync();
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
        if (IsGenerating) return;
        var session = await chatService.CreateSessionAsync(character);
        await RefreshSessionsAsync(session.Id);
        await LoadSessionAsync(session);
        ErrorMessage = null;
    }

    public async Task StartGroupChatAsync(string groupName)
    {
        if (IsGenerating) return;
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
        await chatService.UpdateSessionTitleAsync(_session, title);
        SessionTitle = _session.Title;
        await RefreshSessionsAsync(_session.Id);
    }

    [RelayCommand(CanExecute = nameof(CanBranchFromMessage))]
    private async Task BranchFromMessageAsync(ChatMessageViewModel? message)
    {
        if (_session is null || message is null || IsGenerating) return;
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
        _session is not null && message is not null && !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanOpenParentConversation))]
    private async Task OpenParentConversationAsync()
    {
        if (_session?.ParentSessionId is not long parentId || IsGenerating) return;
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
        _session?.ParentSessionId is not null && !IsGenerating;

    public async Task UpdateGroupChatAsync(string title, IReadOnlyCollection<long> characterIds)
    {
        if (_session is null || !_session.IsGroupChat || IsGenerating) return;
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
        ErrorMessage = null;
        ClearReplySuggestions();
        var input = InputText;
        var images = PendingImagePaths.ToArray();
        InputText = string.Empty;
        PendingImagePaths.Clear();
        CancelDraftFlush();
        await FlushDraftAsync(_session.Id, DraftStore.Draft.Empty);
        var sessionId = _session.Id;
        // A reply is still being written, so hold the message until the conversation is free again.
        if (IsGenerating)
        {
            _queue.Enqueue(sessionId, new SendQueue.Item(input, images));
            RefreshQueuedSends();
            return;
        }
        await SendNowAsync(sessionId, input, images);
    }

    private async Task SendNowAsync(long sessionId, string input, IReadOnlyList<string> images)
    {
        if (_session is null || _session.Id != sessionId) return;
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
                trayService.Notify("NativeTavern", "助手回复已完成。");
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
        if (_session?.Id != sessionId || IsGenerating || _isDeletingSession) return;
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
    private void Stop() => _generationCancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanGenerateNextSpeaker))]
    private Task NextSpeakerAsync() => GenerateNextSpeakerAsync();

    public async Task GenerateNextSpeakerAsync(Character? requestedSpeaker = null)
    {
        if (_session is null || !CanGenerateNextSpeaker()) return;
        ErrorMessage = null;
        ClearReplySuggestions();
        using var cancellation = BeginGeneration();
        ChatMessageViewModel? assistantViewModel = null;
        try
        {
            await chatService.GenerateNextSpeakerAsync(
                _session,
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
                await RefreshReplySuggestionsAsync(cancellation.Token);
                await UpdateCharacterStatusAsync(assistantViewModel.Model, cancellation.Token);
            }
            await RefreshSessionsAsync(_session.Id);
            await RefreshTokenEstimateAsync();
        }
        catch (ProviderException ex) { ErrorMessage = ex.Message; }
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
    }

    [RelayCommand(CanExecute = nameof(CanCreateChat))]
    private async Task NewChatAsync()
    {
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
        try
        {
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
        finally { _isDeletingSession = false; }
    }

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private bool _hasSearchResults;
    [ObservableProperty] private bool _isSearching;

    public ObservableCollection<MessageSearchResult> SearchResults { get; } = [];

    /// <summary>Raised after a search result's conversation is loaded so the view can scroll to the hit.</summary>
    public event Action<long>? JumpToMessageRequested;

    [RelayCommand]
    private async Task SearchMessagesAsync()
    {
        var query = SearchQuery.Trim();
        SearchResults.Clear();
        HasSearchResults = false;
        if (query.Length < 2)
        {
            IsSearchOpen = false;
            return;
        }

        IsSearching = true;
        IsSearchOpen = true;
        try
        {
            foreach (var result in await chatService.SearchMessagesAsync(query, 30)) SearchResults.Add(result);
            HasSearchResults = SearchResults.Count > 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Conversation search failed.");
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        SearchResults.Clear();
        HasSearchResults = false;
        IsSearchOpen = false;
    }

    [RelayCommand]
    private async Task OpenSearchResultAsync(MessageSearchResult? result)
    {
        if (result is null) return;
        var session = Sessions.FirstOrDefault(x => x.Id == result.ChatSessionId);
        if (session is null) return;
        if (!ReferenceEquals(session, SelectedSession)) SelectedSession = session;
        // Selecting a session loads it asynchronously; wait for that to settle before scrolling.
        for (var attempt = 0; attempt < 80 && !ReferenceEquals(_session, session); attempt++)
            await Task.Delay(25);
        ClearSearch();
        if (Messages.FirstOrDefault(x => x.Model.Id == result.MessageId) is { } target) ClearSpeakerFilterIfNeeded(target);
        JumpToMessageRequested?.Invoke(result.MessageId);
    }

    [RelayCommand]
    private void BeginEdit(ChatMessageViewModel? message)
    {
        if (!IsGenerating) message?.BeginEdit();
    }

    [RelayCommand]
    private void CancelEdit(ChatMessageViewModel? message) => message?.CancelEdit();

    [RelayCommand]
    private async Task SaveEditAsync(ChatMessageViewModel? message)
    {
        if (message is null || IsGenerating) return;
        if (string.IsNullOrWhiteSpace(message.EditText))
        {
            ErrorMessage = "消息内容不能为空。";
            return;
        }
        message.FinishEdit();
        await chatService.UpdateMessageAsync(message.Model);
        if (message.IsAssistant)
        {
            message.SetSwipeState(0, 1, message.Content);
            await RefreshReplySuggestionsAsync();
            await UpdateCharacterStatusAsync(message.Model);
        }
        ErrorMessage = null;
    }

    public async Task DeleteMessageAsync(ChatMessageViewModel message)
    {
        if (IsGenerating) return;
        await chatService.DeleteMessageAsync(message.Model.Id);
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
        if (message is null || IsGenerating) return;
        message.IsPinned = !message.IsPinned;
        await chatService.SetMessagePinnedAsync(message.Model, message.IsPinned);
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
        if (_session is null) return;
        try
        {
            await chatService.SaveStorySummaryAsync(_session, StorySummary);
            RefreshStorySummaryState();
            StorySummaryStatus = HasStorySummary
                ? "已保存，自动摘要暂停；点“重新生成”可恢复。"
                : "已清空剧情回顾。";
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            StorySummaryStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RebuildStorySummaryAsync()
    {
        if (_session is null || IsGenerating) return;
        await chatService.RebuildStorySummaryAsync(_session);
        StorySummary = _session.Summary;
        RefreshStorySummaryState();
        StorySummaryStatus = HasStorySummary
            ? $"已根据最早的 {StorySummaryCoveredCount} 条消息重新整理。"
            : "消息还不够多，暂时没有可整理的早期剧情。";
    }

    [RelayCommand]
    private async Task ClearStorySummaryAsync()
    {
        if (_session is null) return;
        await chatService.ClearStorySummaryAsync(_session);
        StorySummary = string.Empty;
        RefreshStorySummaryState();
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
    public bool CanExtractMemory => !IsExtractingMemory;
    partial void OnIsExtractingMemoryChanged(bool value) => OnPropertyChanged(nameof(CanExtractMemory));

    [RelayCommand]
    private async Task OpenMemoryAsync()
    {
        if (_session?.CharacterId is not long characterId) return;
        await ReloadMemoriesAsync(characterId);
        MemoryStatus = string.Empty;
        IsMemoryOpen = true;
    }

    [RelayCommand]
    private async Task AddMemoryAsync()
    {
        if (_session?.CharacterId is not long characterId) return;
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
        if (_session?.CharacterId is not long characterId || IsExtractingMemory) return;
        IsExtractingMemory = true;
        MemoryStatus = "正在从对话中提炼记忆…";
        try
        {
            var before = CharacterMemories.Count;
            await memoryService.ExtractIfNeededAsync(_session, force: true);
            await ReloadMemoriesAsync(characterId);
            var added = CharacterMemories.Count - before;
            MemoryStatus = added > 0 ? $"新增 {added} 条记忆。" : "没有提炼出新的记忆。";
        }
        finally { IsExtractingMemory = false; }
    }

    private async Task ReloadMemoriesAsync(long characterId)
    {
        CharacterMemories.Clear();
        foreach (var memory in await memoryService.GetAsync(characterId)) CharacterMemories.Add(memory);
        HasMemories = CharacterMemories.Count > 0;
    }

    [RelayCommand]
    private async Task SwipeLeftAsync(ChatMessageViewModel? message)
    {
        if (message is null || IsGenerating || !message.CanSwipeLeft) return;
        await SelectSwipeAsync(message, message.Model.CurrentSwipeIndex - 1);
    }

    [RelayCommand]
    private async Task SwipeRightAsync(ChatMessageViewModel? message)
    {
        if (message is null || IsGenerating || !message.IsAssistant) return;
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

    private bool CanContinueLastReply() => !IsGenerating && HasProviderConfiguration && _session is not null;

    private async Task ContinueReplyAsync(ChatMessageViewModel message)
    {
        if (_session is null || IsGenerating || !message.IsAssistant) return;
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
        try
        {
            var result = await chatService.SelectSwipeAsync(message.Model, index);
            message.SetSwipeState(result.Message.CurrentSwipeIndex, result.SwipeCount, result.Message.Content);
            await RefreshReplySuggestionsAsync();
            await UpdateCharacterStatusAsync(message.Model);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Selecting message swipe failed.");
            ErrorMessage = "切换候选回复失败。";
        }
    }

    private async Task GenerateAlternativeCoreAsync(ChatMessageViewModel message)
    {
        if (_session is null || IsGenerating || !message.IsAssistant) return;
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

    private async Task LoadSessionAsync(ChatSession session)
    {
        var requestId = Interlocked.Increment(ref _sessionLoadRequestId);
        var composerAtLoadStart = DraftStore.Capture(InputText, PendingImagePaths);
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
        var assistantName = character?.Name ?? "NativeTavern";
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

        var estimatedTokens = await GetTokenEstimateAsync(session);
        if (requestId != Volatile.Read(ref _sessionLoadRequestId)) return;

        _session = session;
        ClearReplySuggestions();
        IsGroupChat = session.IsGroupChat;
        IsBranch = session.ParentSessionId is not null;
        GroupMembers.Clear();
        foreach (var member in members) GroupMembers.Add(member);
        CurrentAssistantName = assistantName;
        CurrentAssistantAvatarPath = assistantAvatarPath;
        SessionTitle = session.Title;
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
        HasMemoryTarget = session is { IsGroupChat: false, CharacterId: not null };
        var draft = _drafts.OnLoadFinished(
            session.Id, composerAtLoadStart, DraftStore.Capture(InputText, PendingImagePaths));
        InputText = draft.Text;
        PendingImagePaths.Clear();
        // Attachments are files on disk, and a draft can outlive them.
        foreach (var image in draft.Images.Where(File.Exists)) PendingImagePaths.Add(image);
        RefreshStorySummaryState();
        Messages.Clear();
        foreach (var message in messageViewModels) Messages.Add(message);
        RefreshPinnedMessages();
        EstimatedPromptTokens = estimatedTokens;
        NextSpeakerCommand.NotifyCanExecuteChanged();
        BranchFromMessageCommand.NotifyCanExecuteChanged();
        OpenParentConversationCommand.NotifyCanExecuteChanged();
        RefreshQueuedSends();
        if (Messages.LastOrDefault()?.IsAssistant == true)
            _ = RefreshReplySuggestionsAsync();
        // Messages queued earlier go out now that this conversation is on screen and idle.
        _ = DrainQueueAsync(session.Id);
    }

    private async Task RefreshSessionsAsync(long selectedId)
    {
        var sessions = await chatService.GetSessionsAsync();
        _suppressSessionSelection = true;
        Sessions.Clear();
        foreach (var session in sessions) Sessions.Add(session);
        SelectedSession = Sessions.FirstOrDefault(x => x.Id == selectedId);
        _suppressSessionSelection = false;
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
    private bool CanSend() => HasProviderConfiguration &&
                              (!string.IsNullOrWhiteSpace(InputText) || PendingImagePaths.Count > 0);
    private bool CanStop() => IsGenerating;
    private bool CanCreateChat() => !IsGenerating;
    private bool CanGenerateNextSpeaker() => !IsGenerating && IsGroupChat &&
                                             HasProviderConfiguration && GroupMembers.Count >= 2;

    partial void OnSelectedSessionChanged(ChatSession? value)
    {
        if (_suppressSessionSelection || value is null || value.Id == _session?.Id) return;
        if (IsGenerating)
        {
            if (_session is not null) SetSelectedSession(_session.Id);
            return;
        }
        _ = LoadSessionSafelyAsync(value);
    }

    private async Task LoadSessionSafelyAsync(ChatSession session)
    {
        try
        {
            var stored = await chatService.GetSessionAsync(session.Id);
            if (stored is null)
            {
                if (_session is not null) SetSelectedSession(_session.Id);
                return;
            }
            if (SelectedSession?.Id != session.Id) return;
            await LoadSessionAsync(stored);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading chat session failed.");
            ErrorMessage = "加载聊天记录失败。";
        }
    }

    private async Task RefreshTokenEstimateAsync()
    {
        EstimatedPromptTokens = _session is null ? 0 : await GetTokenEstimateAsync(_session);
    }

    private async Task UpdateCharacterStatusAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (_session is null || !IsCharacterStatusPluginEnabled) return;
        var characterId = message.SpeakerCharacterId ?? _session.CharacterId;
        if (characterId is long id)
            await characterStatusService.UpdateAsync(_session, id, message.Id, cancellationToken);
    }

    private void OnPluginsChanged()
    {
        if (Application.Current is null) return;
        _ = Application.Current.Dispatcher.InvokeAsync(RefreshCharacterStatusPluginAsync);
    }

    private async Task<int> GetTokenEstimateAsync(ChatSession session)
    {
        try { return (await chatService.GetPromptPreviewAsync(session)).EstimatedTokens; }
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
        if (_session?.Id is not long sessionId) return;
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
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        NewChatCommand.NotifyCanExecuteChanged();
        NextSpeakerCommand.NotifyCanExecuteChanged();
        BranchFromMessageCommand.NotifyCanExecuteChanged();
        OpenParentConversationCommand.NotifyCanExecuteChanged();
    }
    partial void OnHasProviderConfigurationChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        NextSpeakerCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsGroupChatChanged(bool value) => NextSpeakerCommand.NotifyCanExecuteChanged();
}
