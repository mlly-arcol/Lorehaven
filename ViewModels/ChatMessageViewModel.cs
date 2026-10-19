using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using NativeTavern.Models;

namespace NativeTavern.ViewModels;

public partial class ChatMessageViewModel : ObservableObject
{
    private const string DefaultAvatarPath = "/Lorehaven;component/Assets/NativeTavern.png";
    private readonly string _assistantName;
    private readonly string _assistantAvatarPath;

    public ChatMessageViewModel(
        ChatMessage model,
        int swipeCount = 0,
        IEnumerable<ChatAttachment>? attachments = null,
        string? assistantName = null,
        string? assistantAvatarPath = null,
        long? characterId = null)
    {
        Model = model;
        _content = model.Content;
        _editText = model.Content;
        _swipeCount = swipeCount;
        _assistantName = string.IsNullOrWhiteSpace(assistantName) ? "Lorehaven" : assistantName;
        _assistantAvatarPath = !string.IsNullOrWhiteSpace(assistantAvatarPath) && File.Exists(assistantAvatarPath)
            ? assistantAvatarPath : DefaultAvatarPath;
        CharacterId = characterId;
        Attachments = new ObservableCollection<ChatAttachment>(attachments ?? []);
    }

    public ChatMessage Model { get; }
    public ChatRole Role => Model.Role;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public string RoleLabel => Role == ChatRole.User ? "You" : _assistantName;
    public string AssistantAvatarPath => IsAssistant ? _assistantAvatarPath : string.Empty;
    public bool HasAssistantAvatar => IsAssistant;
    public bool HasCustomAssistantAvatar => IsAssistant && _assistantAvatarPath != DefaultAvatarPath;
    public string DisplayTime => Model.CreatedAt.ToLocalTime().ToString("HH:mm");
    public long? CharacterId { get; }
    public bool IsPinned
    {
        get => Model.IsPinned;
        set
        {
            if (Model.IsPinned == value) return;
            Model.IsPinned = value;
            OnPropertyChanged(nameof(IsPinned));
            OnPropertyChanged(nameof(PinnedGlyph));
        }
    }
    public string PinnedGlyph => Model.IsPinned ? "★" : "☆";
    // Without this a popup entry is announced as the view-model type name.
    public string EntryLabel
    {
        get
        {
            var text = Content.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 60) text = text[..60] + "…";
            return $"{RoleLabel}: {text}";
        }
    }

    public bool HasCharacterStatusTarget => IsAssistant && CharacterId is not null;
    public bool IsWaitingForResponse => IsAssistant && IsStreaming && string.IsNullOrEmpty(Content);
    public string SwipeDisplay => IsAssistant && SwipeCount > 0
        ? $"{Model.CurrentSwipeIndex + 1} / {SwipeCount}" : string.Empty;
    public bool CanSwipeLeft => IsAssistant && Model.CurrentSwipeIndex > 0;
    public ObservableCollection<ChatAttachment> Attachments { get; }
    public bool HasAttachments => Attachments.Count > 0;

    [ObservableProperty] private string _content;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText;
    [ObservableProperty] private int _swipeCount;
    [ObservableProperty] private bool _isStreaming;

    public void BeginEdit() { EditText = Content; IsEditing = true; }
    public void CancelEdit() { EditText = Content; IsEditing = false; }
    public void FinishEdit()
    {
        Content = EditText.Trim();
        Model.Content = Content;
        IsEditing = false;
    }

    public void SetSwipeState(int index, int count, string content)
    {
        Model.CurrentSwipeIndex = index;
        Model.Content = content;
        Content = content;
        SwipeCount = count;
        OnPropertyChanged(nameof(SwipeDisplay));
        OnPropertyChanged(nameof(CanSwipeLeft));
    }

    partial void OnContentChanged(string value)
    {
        Model.Content = value;
        OnPropertyChanged(nameof(IsWaitingForResponse));
        OnPropertyChanged(nameof(EntryLabel));
    }
    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(IsWaitingForResponse));
    partial void OnSwipeCountChanged(int value)
    {
        OnPropertyChanged(nameof(SwipeDisplay));
        OnPropertyChanged(nameof(CanSwipeLeft));
    }
}
