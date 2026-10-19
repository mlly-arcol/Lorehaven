using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NativeTavern.Models;

namespace NativeTavern.ViewModels;

public sealed record JournalBranchItem(long SessionId, string Title, bool IsCurrent);

public partial class ChatViewModel
{
    public ObservableCollection<JournalBranchItem> JournalBranches { get; } = [];
    [ObservableProperty] private string _journalDescription = string.Empty;
    private bool _journalInitialized;

    private void InitializeJournal()
    {
        if (_journalInitialized) return;
        _journalInitialized = true;
        memoryService.MemoriesChanged += OnJournalMemoriesChanged;
    }

    private void OnJournalMemoriesChanged(long characterId)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            if (!dispatcher.CheckAccess())
            {
                _ = dispatcher.BeginInvoke(new Action(() => OnJournalMemoriesChanged(characterId)));
                return;
            }
        }

        // Tests have no WPF Application; their isolated collections can refresh on the caller's context.
        if (SelectedMemoryCharacter?.Id == characterId)
            _ = ReloadSelectedMemorySafelyAsync(characterId);
    }

    private void RefreshJournalBranches()
    {
        JournalBranches.Clear();
        if (_session is null) return;
        var sessions = Sessions.GroupBy(session => session.Id).ToDictionary(group => group.Key, group => group.First());
        sessions[_session.Id] = _session;
        var route = new List<ChatSession>();
        var visited = new HashSet<long>();
        var current = _session;
        while (visited.Add(current.Id))
        {
            route.Add(current);
            if (current.ParentSessionId is not long parentId || !sessions.TryGetValue(parentId, out current)) break;
        }
        route.Reverse();
        foreach (var session in route.TakeLast(3))
            JournalBranches.Add(new(session.Id, session.Title, session.Id == _session.Id));
        foreach (var child in sessions.Values.Where(session => session.ParentSessionId == _session.Id && !visited.Contains(session.Id))
                     .OrderBy(session => session.CreatedAt).ThenBy(session => session.Id).Take(2))
            JournalBranches.Add(new(child.Id, child.Title, false));
    }

    [RelayCommand]
    private async Task OpenJournalBranchAsync(JournalBranchItem? branch)
    {
        if (branch is null || branch.IsCurrent || !CanModifyConversation) return;
        try { await OpenConversationBranchAsync(branch.SessionId); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Opening journal branch {SessionId} failed.", branch.SessionId);
            ErrorMessage = "打开剧情分支失败，请重试。";
        }
    }
}
