using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using NativeTavern.Models;
using NativeTavern.ViewModels;

namespace NativeTavern.Views;

public partial class GroupPerformanceDialog : Window
{
    private readonly ObservableCollection<Character> speakers;
    private readonly bool english;

    public GroupPerformanceDialog(IReadOnlyList<Character> members, bool english = false)
    {
        ArgumentNullException.ThrowIfNull(members);
        this.english = english;
        speakers = new ObservableCollection<Character>(members);
        InitializeComponent();
        Title = english ? "Group performance" : "群聊自动演出";
        ExplanationText.Text = english
            ? "Each round lets every member speak once in the order below. You can pause the performance or send a message to interject."
            : "每轮让所有成员按下方顺序各发言一次。演出时可以暂停，也可以发送消息插话。";
        RoundsLabel.Content = english ? "Rounds" : "演出轮数";
        OrderLabel.Content = english ? "Speaking order" : "发言顺序";
        MoveUpButton.Content = english ? "Move up" : "上移";
        MoveDownButton.Content = english ? "Move down" : "下移";
        CancelButton.Content = english ? "Cancel" : "取消";
        StartButton.Content = english ? "Start" : "开始演出";
        AutomationProperties.SetName(RoundsBox, english ? "Number of rounds" : "演出轮数");
        AutomationProperties.SetName(SpeakerList, english ? "Speaking order" : "角色发言顺序");
        AutomationProperties.SetName(MoveUpButton, english ? "Move selected speaker up" : "将选中角色上移");
        AutomationProperties.SetName(MoveDownButton, english ? "Move selected speaker down" : "将选中角色下移");
        RoundsBox.ItemsSource = Enumerable.Range(1, 20);
        RoundsBox.SelectedItem = 3;
        SpeakerList.ItemsSource = speakers;
        if (speakers.Count > 0) SpeakerList.SelectedIndex = 0;
        Loaded += (_, _) => RoundsBox.Focus();
    }

    public int Rounds => RoundsBox.SelectedItem is int rounds ? rounds : 1;
    public IReadOnlyList<long> SpeakerOrder => speakers.Select(character => character.Id).ToArray();

    private void SpeakerList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMoveButtons();

    private void UpdateMoveButtons()
    {
        if (MoveUpButton is null || MoveDownButton is null) return;
        var index = SpeakerList.SelectedIndex;
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < speakers.Count - 1;
    }

    private void MoveUp_OnClick(object sender, RoutedEventArgs e) => MoveSelectedSpeaker(-1);
    private void MoveDown_OnClick(object sender, RoutedEventArgs e) => MoveSelectedSpeaker(1);

    private void MoveSelectedSpeaker(int offset)
    {
        var index = SpeakerList.SelectedIndex;
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= speakers.Count) return;
        var selected = speakers[index];
        speakers.Move(index, destination);
        SpeakerList.SelectedItem = selected;
        SpeakerList.ScrollIntoView(selected);
        SpeakerList.Focus();
        UpdateMoveButtons();
    }

    private void Start_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = new GroupPerformancePlan(Rounds, SpeakerOrder);
            DialogResult = true;
        }
        catch (ArgumentException)
        {
            ValidationMessage.Text = english
                ? "Select 1–20 rounds and ensure every speaker is a valid, distinct character."
                : "请选择 1～20 轮，并确保所有发言成员都是有效且不重复的角色。";
        }
    }
}
