using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NativeTavern.Services;
using NativeTavern.ViewModels;

namespace NativeTavern.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _viewModel;
    private ScrollViewer? _messageScrollViewer;
    private readonly DispatcherTimer _smoothScrollTimer;
    private readonly DispatcherTimer _autoScrollTimer;
    private readonly HashSet<ChatMessageViewModel> _subscribedMessages = [];
    private double _smoothScrollTarget;

    private void ChatLayout_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Use the available content area in WPF units, so this also works at high DPI.
        var compact = e.NewSize.Width < 760 || e.NewSize.Height < 600;
        var inset = compact ? 16 : 28;
        ConversationHeader.Padding = new Thickness(inset, compact ? 12 : 18, inset, 14);
        ComposerArea.Margin = new Thickness(inset, 8, inset, compact ? 10 : 20);
        ComposerInputBox.MaxHeight = Math.Clamp(e.NewSize.Height * 0.22, 72, 160);
    }

    private void ConversationMore_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    public ChatView()
    {
        InitializeComponent();
        _smoothScrollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _smoothScrollTimer.Tick += SmoothScrollTimerOnTick;
        _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _autoScrollTimer.Tick += AutoScrollTimerOnTick;
        DataContextChanged += (_, _) =>
        {
            DetachViewModel();
            if (IsLoaded) AttachViewModel();
        };
        Loaded += ChatView_OnLoaded;
        Unloaded += ChatView_OnUnloaded;
    }

    private void ChatView_OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel();
        if (_viewModel is not null)
        {
            _viewModel.JumpToMessageRequested -= OnJumpToMessageRequested;
            _viewModel.JumpToMessageRequested += OnJumpToMessageRequested;
        }
        _messageScrollViewer = FindVisualChild<ScrollViewer>(MessageList);
        if (_messageScrollViewer is null) return;

        _smoothScrollTarget = _messageScrollViewer.VerticalOffset;
        MessageList.PreviewMouseWheel -= MessageList_OnPreviewMouseWheel;
        MessageList.PreviewMouseWheel += MessageList_OnPreviewMouseWheel;
    }

    private void SearchBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ChatViewModel viewModel) return;
        e.Handled = true;
        _ = viewModel.SearchMessagesCommand.ExecuteAsync(null);
    }

    private void SearchPopup_OnClosed(object sender, EventArgs e)
    {
        if (DataContext is ChatViewModel viewModel) viewModel.IsSearchOpen = false;
    }

    private void PinnedPopup_OnClosed(object sender, EventArgs e)
    {
        if (DataContext is ChatViewModel viewModel) viewModel.IsPinnedListOpen = false;
    }

    private void StorySummaryPopup_OnClosed(object sender, EventArgs e)
    {
        if (DataContext is ChatViewModel viewModel) viewModel.IsStorySummaryOpen = false;
    }

    private void MemoryPopup_OnClosed(object sender, EventArgs e)
    {
        if (DataContext is ChatViewModel viewModel) viewModel.IsMemoryOpen = false;
    }

    private void OnJumpToMessageRequested(long messageId)
    {
        var target = _viewModel?.Messages.FirstOrDefault(x => x.Model.Id == messageId);
        if (target is null) return;
        MessageList.ScrollIntoView(target);
        MessageList.SelectedItem = target;
    }

    private void ChatView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _smoothScrollTimer.Stop();
        _autoScrollTimer.Stop();
        MessageList.PreviewMouseWheel -= MessageList_OnPreviewMouseWheel;
        DetachViewModel();
    }

    private void AttachViewModel()
    {
        DetachViewModel();
        _viewModel = DataContext as ChatViewModel;
        if (_viewModel is null) return;
        _viewModel.Messages.CollectionChanged += MessagesOnCollectionChanged;
        foreach (var message in _viewModel.Messages) SubscribeMessage(message);
    }

    private void DetachViewModel()
    {
        if (_viewModel is not null)
            _viewModel.Messages.CollectionChanged -= MessagesOnCollectionChanged;
        foreach (var message in _subscribedMessages)
            message.PropertyChanged -= MessageOnPropertyChanged;
        _subscribedMessages.Clear();
        _viewModel = null;
    }

    private void SubscribeMessage(ChatMessageViewModel message)
    {
        if (_subscribedMessages.Add(message))
            message.PropertyChanged += MessageOnPropertyChanged;
    }

    private void UnsubscribeMessage(ChatMessageViewModel message)
    {
        if (_subscribedMessages.Remove(message))
            message.PropertyChanged -= MessageOnPropertyChanged;
    }

    private void MessagesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var message in _subscribedMessages)
                message.PropertyChanged -= MessageOnPropertyChanged;
            _subscribedMessages.Clear();
            if (_viewModel is not null)
                foreach (var message in _viewModel.Messages) SubscribeMessage(message);
        }
        if (e.NewItems is not null)
            foreach (ChatMessageViewModel message in e.NewItems)
                SubscribeMessage(message);
        if (e.OldItems is not null)
            foreach (ChatMessageViewModel message in e.OldItems)
                UnsubscribeMessage(message);
        ScrollToLatest();
    }

    private void MessageOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChatMessageViewModel.Content) || !IsNearBottom()) return;

        // Streaming responses can update many times per second. Coalesce those updates so
        // layout and ScrollIntoView run at most once per rendered frame group.
        if (!_autoScrollTimer.IsEnabled) _autoScrollTimer.Start();
    }

    private void ScrollToLatest()
    {
        if (MessageList.Items.Count == 0) return;

        _smoothScrollTimer.Stop();
        MessageList.ScrollIntoView(MessageList.Items[^1]);
        Dispatcher.BeginInvoke(() =>
        {
            if (_messageScrollViewer is not null)
                _smoothScrollTarget = _messageScrollViewer.VerticalOffset;
        }, DispatcherPriority.Loaded);
    }

    private bool IsNearBottom()
    {
        if (_messageScrollViewer is null) return true;
        return _messageScrollViewer.ScrollableHeight - _messageScrollViewer.VerticalOffset < 120;
    }

    private void AutoScrollTimerOnTick(object? sender, EventArgs e)
    {
        _autoScrollTimer.Stop();
        if (IsNearBottom()) ScrollToLatest();
    }

    private void MessageList_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_messageScrollViewer is null || _messageScrollViewer.ScrollableHeight <= 0) return;

        var currentTarget = _smoothScrollTimer.IsEnabled
            ? _smoothScrollTarget
            : _messageScrollViewer.VerticalOffset;
        _smoothScrollTarget = Math.Clamp(
            currentTarget - e.Delta * 0.55,
            0,
            _messageScrollViewer.ScrollableHeight);

        if (!_smoothScrollTimer.IsEnabled) _smoothScrollTimer.Start();
        e.Handled = true;
    }

    private void SmoothScrollTimerOnTick(object? sender, EventArgs e)
    {
        if (_messageScrollViewer is null)
        {
            _smoothScrollTimer.Stop();
            return;
        }

        _smoothScrollTarget = Math.Clamp(_smoothScrollTarget, 0, _messageScrollViewer.ScrollableHeight);
        var distance = _smoothScrollTarget - _messageScrollViewer.VerticalOffset;
        if (Math.Abs(distance) < 0.5)
        {
            _messageScrollViewer.ScrollToVerticalOffset(_smoothScrollTarget);
            _smoothScrollTimer.Stop();
            return;
        }

        _messageScrollViewer.ScrollToVerticalOffset(
            _messageScrollViewer.VerticalOffset + distance * 0.24);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;

            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }

    private async void DeleteChat_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (ConfirmDeleteDialog.Show(this, "删除当前对话？", $"确定删除“{_viewModel.SessionTitle}”吗？",
                "该对话及其中的全部消息都会被永久删除。此操作无法撤销。", "删除对话"))
            await _viewModel.DeleteCurrentChatAsync();
    }

    private async void RenameChat_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.IsGenerating) return;
        var title = TextInputDialog.Show(this, "重命名对话", "对话标题", _viewModel.SessionTitle, 120);
        if (title is null) return;
        try { await _viewModel.RenameCurrentConversationAsync(title); }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "NativeTavern", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ExportChat_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.IsGenerating) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Conversation",
            FileName = DataExportService.CreateSafeFileName(_viewModel.SessionTitle, "conversation"),
            DefaultExt = ".html",
            AddExtension = true,
            Filter = "Reading copy HTML (*.html)|*.html|Markdown (*.md)|*.md|JSON (*.json)|*.json"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _viewModel.ExportCurrentConversationAsync(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show("导出失败：" + ex.Message, "NativeTavern", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void EditGroupChat_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not { IsGroupChat: true }) return;
        var characters = await _viewModel.GetAllCharactersAsync();
        var dialog = CharacterGroupDialog.ShowGroupChat(
            this,
            _viewModel.SessionTitle,
            characters,
            _viewModel.GroupMembers.Select(x => x.Id).ToList());
        if (dialog is not null)
            await _viewModel.UpdateGroupChatAsync(dialog.GroupName, dialog.SelectedCharacterIds);
    }

    private async void PromptSettings_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.IsGenerating) return;
        var dialog = ChatPromptSettingsDialog.Show(this, _viewModel);
        if (dialog is not null)
            await _viewModel.UpdatePromptContextAsync(
                dialog.SelectedPersona,
                dialog.SelectedLorebook,
                dialog.SelectedPreset,
                dialog.AuthorNote,
                dialog.GroupName);
    }

    private void NextSpeaker_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button button ||
            !_viewModel.NextSpeakerCommand.CanExecute(null)) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        AddSpeakerItem(menu, "自动选择", null);
        if (_viewModel.GroupMembers.Count > 0) menu.Items.Add(new Separator());
        foreach (var member in _viewModel.GroupMembers) AddSpeakerItem(menu, member.Name, member);
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void AddSpeakerItem(ContextMenu menu, string label, NativeTavern.Models.Character? character)
    {
        var item = new MenuItem { Header = label };
        item.Click += async (_, _) =>
        {
            if (_viewModel is not null) await _viewModel.GenerateNextSpeakerAsync(character);
        };
        menu.Items.Add(item);
    }

    private async void DeleteMessage_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { Tag: ChatMessageViewModel message }) return;
        if (ConfirmDeleteDialog.Show(this, "删除这条消息？", "确定从当前对话中删除这条消息吗？",
                "删除后消息上下文会随之更新。此操作无法撤销。", "删除消息"))
            await _viewModel.DeleteMessageAsync(message);
    }

    private void CopyMessage_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChatMessageViewModel message } && !string.IsNullOrEmpty(message.Content))
            Clipboard.SetText(message.Content);
    }

    private async void CharacterStatus_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { Tag: ChatMessageViewModel message } button) return;
        button.IsEnabled = false;
        try
        {
            var status = await _viewModel.GetCharacterStatusAsync(message);
            if (status is null)
            {
                MessageBox.Show("暂时没有可显示的角色状态。", "角色状态", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            CharacterStatusDialog.Show(this, status);
        }
        catch (Exception ex)
        {
            MessageBox.Show("读取角色状态失败：" + ex.Message, "角色状态", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { button.IsEnabled = true; }
    }

    private async void InputBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.Key == Key.Escape && _viewModel.StopCommand.CanExecute(null))
        {
            _viewModel.StopCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            await _viewModel.RegenerateLastAsync();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (_viewModel.SendCommand.CanExecute(null)) _viewModel.SendCommand.Execute(null);
        e.Handled = true;
    }

    private void AttachImage_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.gif", Multiselect = true };
        if (dialog.ShowDialog() == true) _viewModel?.AddImages(dialog.FileNames);
    }

    private void InputBox_OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _viewModel?.AddImages(files);
        e.Handled = true;
    }
}
