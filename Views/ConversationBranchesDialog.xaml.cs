using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NativeTavern.Models;
using NativeTavern.Services;

namespace NativeTavern.Views;

public partial class ConversationBranchesDialog : Window
{
    public ConversationBranchesDialog(IReadOnlyList<ChatSession> sessions, long currentSessionId, bool english = false)
    {
        InitializeComponent();
        var roots = ConversationBranchTree.Build(sessions, currentSessionId);
        var presenters = new List<BranchItem>();
        var pending = new Queue<(ConversationBranchNode Node, BranchItem Item)>();
        foreach (var root in roots)
        {
            var item = new BranchItem(root, english);
            presenters.Add(item);
            pending.Enqueue((root, item));
        }
        while (pending.TryDequeue(out var next))
        {
            foreach (var child in next.Node.Children)
            {
                var item = new BranchItem(child, english);
                next.Item.Children.Add(item);
                pending.Enqueue((child, item));
            }
        }

        Title = Heading.Text = english ? "Story branches" : "剧情分支树";
        Hint.Text = english
            ? "Explore every route in this story. Select a branch and open it, or double-click to jump."
            : "查看同一故事的所有路线。选择分支后打开，也可以双击跳转。";
        CancelButton.Content = english ? "Close" : "关闭";
        OpenButton.Content = english ? "Open branch" : "打开分支";
        SelectionDetails.Text = english ? "Select a conversation to open." : "选择要打开的对话。";
        BranchTree.ItemsSource = presenters;
    }

    public ChatSession? SelectedSession => (BranchTree.SelectedItem as BranchItem)?.Session;
    public long? SelectedSessionId => SelectedSession?.Id;

    private void BranchTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        OpenButton.IsEnabled = e.NewValue is BranchItem;
        if (e.NewValue is BranchItem item) SelectionDetails.Text = $"{item.Title}\n{item.Details}";
    }

    private void Dialog_OnLoaded(object sender, RoutedEventArgs e)
    {
        BranchTree.Focus();
        BringSelectedIntoView(BranchTree);
    }

    private static void BringSelectedIntoView(ItemsControl tree)
    {
        var pending = new Stack<ItemsControl>();
        pending.Push(tree);
        while (pending.TryPop(out var parent))
        {
            parent.UpdateLayout();
            foreach (var item in parent.Items)
            {
                if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
                if (container.IsSelected)
                {
                    container.BringIntoView();
                    container.Focus();
                    return;
                }
                pending.Push(container);
            }
        }
    }

    private void Open_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is not null) DialogResult = true;
    }

    private void BranchTree_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Expanding a route should not also navigate to it.
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not TreeViewItem)
        {
            if (source is ToggleButton) return;
            source = source is FrameworkContentElement content
                ? content.Parent
                : VisualTreeHelper.GetParent(source);
        }
        if (source is not TreeViewItem || SelectedSession is null) return;
        e.Handled = true;
        DialogResult = true;
    }

    private void Dialog_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || SelectedSession is null) return;
        e.Handled = true;
        DialogResult = true;
    }

    private sealed class BranchItem
    {
        public BranchItem(ConversationBranchNode node, bool english)
        {
            Session = node.Session;
            IsCurrent = IsSelected = node.IsCurrent;
            Title = string.IsNullOrWhiteSpace(Session.Title)
                ? (english ? $"Conversation #{Session.Id}" : $"对话 #{Session.Id}")
                : Session.Title;
            CurrentLabel = english ? "Current" : "当前";
            var kind = Session.IsGroupChat ? (english ? "Group chat" : "群聊") : (english ? "Chat" : "对话");
            var origin = Session.BranchedFromMessageId is { } messageId
                ? (english ? $"Branch from message #{messageId}" : $"从消息 #{messageId} 分支")
                : Session.ParentSessionId.HasValue
                    ? (english ? "Branch point no longer available" : "分支起点消息已不存在")
                    : (english ? "Story beginning" : "故事起点");
            if (node.HasMissingParent)
                origin += english ? " · Parent conversation unavailable" : " · 原对话已不存在";
            else if (node.IsCycleRoot)
                origin += english ? " · Circular link shown once" : " · 循环关系仅展示一次";
            Details = $"{kind} · {origin} · {Session.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
            AccessibleLabel = IsCurrent ? $"{Title} · {CurrentLabel} · {Details}" : $"{Title} · {Details}";
        }

        public ChatSession Session { get; }
        public string Title { get; }
        public string Details { get; }
        public string CurrentLabel { get; }
        public string AccessibleLabel { get; }
        public bool IsCurrent { get; }
        public bool IsSelected { get; set; }
        public List<BranchItem> Children { get; } = [];
    }
}
