using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NativeTavern.Models;

namespace NativeTavern.ViewModels;

public partial class ChatViewModel
{
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _searchOnlyCurrentConversation;
    [ObservableProperty] private bool _searchPinnedOnly;
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoSearchResults))] private bool _hasSearchResults;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoSearchResults))] private bool _isSearching;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoSearchResults))] private bool _hasSearchError;
    private long _searchRequestId;
    public bool ShowNoSearchResults => !IsSearching && !HasSearchError && !HasSearchResults;

    public ObservableCollection<MessageSearchResult> SearchResults { get; } = [];

    /// <summary>Raised after a search result's conversation is loaded so the view can scroll to the hit.</summary>
    public event Action<long>? JumpToMessageRequested;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SearchMessagesAsync()
    {
        var query = SearchQuery.Trim();
        InvalidateSearch();
        var requestId = Volatile.Read(ref _searchRequestId);
        var sessionId = SearchOnlyCurrentConversation ? _session?.Id : null;
        if ((query.Length == 0 && !SearchPinnedOnly && !SearchOnlyCurrentConversation) ||
            (SearchOnlyCurrentConversation && sessionId is null))
        {
            IsSearchOpen = false;
            return;
        }

        IsSearching = true;
        IsSearchOpen = true;
        try
        {
            var results = await chatService.SearchMessagesAsync(query, 30, sessionId, SearchPinnedOnly);
            if (requestId != Volatile.Read(ref _searchRequestId)) return;
            foreach (var result in results) SearchResults.Add(result);
            HasSearchResults = SearchResults.Count > 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Conversation search failed.");
            if (requestId == Volatile.Read(ref _searchRequestId)) HasSearchError = true;
        }
        finally
        {
            if (requestId == Volatile.Read(ref _searchRequestId)) IsSearching = false;
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        InvalidateSearch();
    }

    private void InvalidateSearch()
    {
        Interlocked.Increment(ref _searchRequestId);
        SearchResults.Clear();
        HasSearchResults = false;
        IsSearchOpen = false;
        IsSearching = false;
        HasSearchError = false;
    }

    partial void OnSearchQueryChanged(string value) => InvalidateSearch();
    partial void OnSearchOnlyCurrentConversationChanged(bool value) => InvalidateSearch();
    partial void OnSearchPinnedOnlyChanged(bool value) => InvalidateSearch();

    [RelayCommand]
    private async Task OpenSearchResultAsync(MessageSearchResult? result)
    {
        if (result is null) return;
        var session = Sessions.FirstOrDefault(x => x.Id == result.ChatSessionId);
        if (session is null) return;
        if (_session?.Id != session.Id)
        {
            if (!CanModifyConversation) return;
            _suppressSessionSelection = true;
            try { SelectedSession = session; }
            finally { _suppressSessionSelection = false; }
            await LoadSessionSafelyAsync(session);
        }
        if (_session?.Id != session.Id || SelectedSession?.Id != session.Id) return;
        ClearSearch();
        if (Messages.FirstOrDefault(x => x.Model.Id == result.MessageId) is not { } target) return;
        ClearSpeakerFilterIfNeeded(target);
        JumpToMessageRequested?.Invoke(result.MessageId);
    }
}
