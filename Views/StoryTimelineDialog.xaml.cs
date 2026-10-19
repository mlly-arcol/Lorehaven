using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using NativeTavern.Models;
using NativeTavern.ViewModels;

namespace NativeTavern.Views;

public partial class StoryTimelineDialog : Window
{
    private readonly ChatViewModel viewModel;
    private readonly bool english;
    private readonly ObservableCollection<StoryTimelineEvent> events = [];
    private CancellationTokenSource? extractionCancellation;
    private StoryTimelineEvent? selected;

    public StoryTimelineDialog(ChatViewModel viewModel, bool english)
    {
        this.viewModel = viewModel;
        this.english = english;
        InitializeComponent();
        EventList.ItemsSource = events;
        if (english) Translate();
    }

    private void Translate()
    {
        Title = Heading.Text = "Story timeline";
        Hint.Text = "Record events yourself or ask the current model to extract new ones. Extraction may incur model charges; you can edit the results.";
        ExtractButton.Content = "Extract new events with AI";
        EditorHeading.Text = "Event details";
        TitleLabel.Text = "Title";
        DetailLabel.Text = "Details";
        AnchorBox.Content = "Link to the latest message for jumping back";
        NewButton.Content = "New";
        OpenButton.Content = "Go to message";
        DeleteButton.Content = "Delete";
        SaveButton.Content = "Save";
        CloseButton.Content = "Close";
    }

    private async void Dialog_OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        New_OnClick(this, new RoutedEventArgs());
    }

    private void Dialog_OnClosed(object? sender, EventArgs e)
    {
        extractionCancellation?.Cancel();
    }

    private async Task RefreshAsync(long? selectId = null)
    {
        var restoreId = selectId ?? selected?.Id;
        var values = await viewModel.GetStoryTimelineEventsAsync();
        events.Clear();
        foreach (var value in values) events.Add(value);
        EventList.SelectedItem = restoreId is long id ? events.FirstOrDefault(x => x.Id == id) : null;
        if (EventList.SelectedItem is null) ResetEditor();
    }

    private void EventList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventList.SelectedItem is not StoryTimelineEvent item)
        {
            ResetEditor();
            return;
        }
        selected = item;
        TitleBox.Text = item.Title;
        DetailBox.Text = item.Detail;
        AnchorBox.IsChecked = item.SourceMessageId is not null;
        AnchorBox.IsEnabled = false;
        OpenButton.IsEnabled = item.SourceMessageId is not null;
        DeleteButton.IsEnabled = true;
        EditorHeading.Text = english ? "Edit event" : "编辑事件";
    }

    private void New_OnClick(object sender, RoutedEventArgs e)
    {
        EventList.SelectedItem = null;
        ResetEditor();
        TitleBox.Focus();
    }

    private void ResetEditor()
    {
        selected = null;
        TitleBox.Text = string.Empty;
        DetailBox.Text = string.Empty;
        AnchorBox.IsEnabled = viewModel.Messages.Count > 0;
        AnchorBox.IsChecked = viewModel.Messages.Count > 0;
        OpenButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        EditorHeading.Text = english ? "New event" : "新建事件";
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            long id;
            if (selected is null)
            {
                var sourceId = AnchorBox.IsChecked == true ? viewModel.Messages.LastOrDefault()?.Model.Id : null;
                id = (await viewModel.AddStoryTimelineEventAsync(sourceId, TitleBox.Text, DetailBox.Text)).Id;
            }
            else
            {
                id = selected.Id;
                if (!await viewModel.UpdateStoryTimelineEventAsync(selected, TitleBox.Text, DetailBox.Text))
                    throw new InvalidOperationException(english ? "The event no longer exists." : "该事件已不存在。");
            }
            await RefreshAsync(id);
            StatusText.Text = english ? "Saved." : "已保存。";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        var answer = MessageBox.Show(this,
            english ? "Delete this event?" : "删除这条剧情事件？",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await viewModel.DeleteStoryTimelineEventAsync(selected.Id);
            await RefreshAsync();
            New_OnClick(this, new RoutedEventArgs());
            StatusText.Text = english ? "Deleted." : "已删除。";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void Open_OnClick(object sender, RoutedEventArgs e)
    {
        if (selected?.SourceMessageId is null) return;
        viewModel.JumpToStoryTimelineEvent(selected);
        Close();
    }

    private async void Extract_OnClick(object sender, RoutedEventArgs e)
    {
        if (extractionCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        extractionCancellation = cancellation;
        ExtractButton.IsEnabled = false;
        StatusText.Text = english ? "Extracting events…" : "正在提取剧情事件…";
        try
        {
            var result = await viewModel.ExtractStoryTimelineAsync(cancellation.Token);
            await RefreshAsync();
            StatusText.Text = result.ScannedMessages == 0 && !result.ScannedPartialMessage
                ? english ? "No new messages to scan." : "没有新的消息需要提取。"
                : english
                    ? $"Fully scanned {result.ScannedMessages} messages; added {result.AddedEvents} events." +
                      (result.ScannedPartialMessage
                          ? " A long message has more text; run again to continue."
                          : result.MayHaveMore ? " Run again to scan more." : string.Empty)
                    : $"已完整读取 {result.ScannedMessages} 条消息，新增 {result.AddedEvents} 条事件。" +
                      (result.ScannedPartialMessage
                          ? " 一条长消息仍有未读取内容，请再次提取。"
                          : result.MayHaveMore ? "还有消息时可再次提取。" : string.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            extractionCancellation = null;
            if (IsLoaded) ExtractButton.IsEnabled = true;
        }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
