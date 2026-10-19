using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace NativeTavern.ViewModels;

/// <summary>Replaces a loaded conversation with one collection change notification.</summary>
public sealed class MessageCollection : ObservableCollection<ChatMessageViewModel>
{
    public void ReplaceAll(IReadOnlyList<ChatMessageViewModel> messages)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var message in messages) Items.Add(message);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
