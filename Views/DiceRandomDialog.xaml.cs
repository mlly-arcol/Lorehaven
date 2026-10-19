using System.Windows;
using System.Windows.Controls;
using NativeTavern.Models;
using NativeTavern.ViewModels;

namespace NativeTavern.Views;

public partial class DiceRandomDialog : Window
{
    private readonly ChatViewModel viewModel;
    private DiceRandomResult? current;

    public DiceRandomDialog(ChatViewModel viewModel, bool english)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DiceNotation.Text = "d20";
        DiceNotation.ToolTip = english ? "For example, d20 or 2d6+1" : "例如 d20、2d6+1";
        if (!english) return;
        Title = Heading.Text = "Dice and random tables";
        Hint.Text = "Results are saved in this conversation. The model sees a result only if you insert it into the draft and send it.";
        DiceLabel.Text = "Dice notation";
        TableLabel.Text = "Random table";
        RollButton.Content = "Roll";
        DrawButton.Content = "Draw";
        ResultHeading.Text = "Result";
        ResultText.Text = "No result yet.";
        HistoryHeading.Text = "Recent results";
        CloseButton.Content = "Close";
        InsertButton.Content = "Insert into draft";
    }

    private async void Dialog_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var catalog = await viewModel.GetRandomTableCatalogAsync();
            TablesBox.ItemsSource = catalog.Tables;
            TablesBox.SelectedIndex = catalog.Tables.Count == 0 ? -1 : 0;
            DrawButton.IsEnabled = catalog.Tables.Count > 0;
            if (catalog.Issues.Count > 0)
                StatusText.Text = string.Join("；", catalog.Issues.Select(x => x.Message));
            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void Roll_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowResult(await viewModel.RollDiceAsync(DiceNotation.Text));
            await RefreshHistoryAsync();
            StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void Draw_OnClick(object sender, RoutedEventArgs e)
    {
        if (TablesBox.SelectedItem is not RandomTableDefinition table) return;
        try
        {
            ShowResult(await viewModel.DrawRandomTableAsync(table.Id));
            await RefreshHistoryAsync();
            StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async Task RefreshHistoryAsync() => HistoryList.ItemsSource = await viewModel.GetRecentRandomResultsAsync();

    private void HistoryList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is DiceRandomResult result) ShowResult(result);
    }

    private void ShowResult(DiceRandomResult result)
    {
        current = result;
        ResultText.Text = $"{result.SourceLabel}：{result.ResultText}";
        DetailText.Text = result.DetailText;
        InsertButton.IsEnabled = true;
    }

    private void Insert_OnClick(object sender, RoutedEventArgs e)
    {
        if (current is null) return;
        viewModel.AddRandomResultToDraft(current);
        Close();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
