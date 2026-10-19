using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using NativeTavern.Models;
using NativeTavern.Services;

namespace NativeTavern.Views;

public partial class CharacterMemoryDialog : Window
{
    private readonly CharacterMemoryService _service;
    private readonly bool _english;
    private readonly ObservableCollection<CharacterMemory> _memories = [];
    private bool _ready;
    private bool _busy;
    private bool _changed;

    private CharacterMemoryDialog(Window? owner, IReadOnlyList<Character> characters,
        CharacterMemoryService service, long? initialCharacterId, bool english = false)
    {
        _english = english;
        InitializeComponent();
        Owner = owner;
        _service = service;
        ApplyLanguage();
        CharacterPicker.ItemsSource = characters;
        CharacterPicker.SelectedItem = characters.FirstOrDefault(x => x.Id == initialCharacterId) ?? characters.FirstOrDefault();
        MemoryList.ItemsSource = _memories;
    }

    public static bool Show(DependencyObject source, IReadOnlyList<Character> characters,
        CharacterMemoryService service, long? initialCharacterId = null, bool english = false)
    {
        var dialog = new CharacterMemoryDialog(source as Window ?? Window.GetWindow(source), characters, service, initialCharacterId, english);
        dialog.ShowDialog();
        return dialog._changed;
    }

    public string LockedMemoryTooltip => Text("已锁定，自动整理不会清除", "Locked; automatic extraction will keep this fact");

    private string Text(string chinese, string english) => _english ? english : chinese;

    private void ApplyLanguage()
    {
        Title = HeadingText.Text = Text("角色记忆", "Character memories");
        IntroText.Text = Text("记忆跟随角色跨对话保存。锁定的重要事实不会被自动整理替换或清除。",
            "Memories follow each character across chats. Locked facts are kept during automatic extraction.");
        CharacterLabel.Text = Text("记忆所属角色", "Character");
        EditorLabel.Text = Text("新增记忆", "New memory");
        LockedBox.Content = Text("锁定重要记忆", "Lock this important memory");
        LockedBox.ToolTip = Text("自动整理不会清除锁定的记忆，您仍可手动编辑或删除。",
            "Automatic extraction keeps locked memories. You can still edit or delete them manually.");
        DoneButton.Content = Text("完成", "Done");
        NewButton.Content = Text("新增", "New");
        SaveButton.Content = Text("保存记忆", "Save memory");
        DeleteButton.Content = Text("删除", "Delete");
        AutomationProperties.SetName(CharacterPicker, Text("选择记忆所属角色", "Choose the character whose memories to edit"));
        AutomationProperties.SetName(MemoryList, Text("角色记忆列表", "Character memories"));
        AutomationProperties.SetName(ContentBox, Text("记忆内容，最多 200 字", "Memory content, up to 200 characters"));
    }

    private string DescribeError(Exception exception)
    {
        if (!_english) return exception.Message;
        return exception.Message switch
        {
            "记忆内容不能为空。" => "Memory content cannot be empty.",
            "这条记忆已经存在。" => "This memory already exists.",
            "这条记忆已被删除，请刷新后重试。" => "This memory was deleted. Refresh and try again.",
            var message when message == $"单条记忆不能超过 {CharacterMemoryService.MemoryLengthLimit} 个字符。" =>
                $"Each memory can contain up to {CharacterMemoryService.MemoryLengthLimit} characters.",
            var message when message == $"每个角色最多保存 {CharacterMemoryService.MaxMemoriesPerCharacter} 条记忆，请先删除不再需要的记忆。" =>
                $"A character can have up to {CharacterMemoryService.MaxMemoriesPerCharacter} memories. Delete an unneeded memory first.",
            _ => exception.Message
        };
    }

    private async void Dialog_OnLoaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        await ReloadSafeAsync();
    }

    private void Dialog_OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        StatusText.Text = Text("正在保存或读取记忆，请稍候。", "Please wait while memories are being saved or loaded.");
    }

    private async void Character_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) await ReloadSafeAsync();
    }

    private void Memory_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MemoryList.SelectedItem is CharacterMemory memory)
        {
            ContentBox.Text = memory.Content;
            LockedBox.IsChecked = memory.IsLocked;
            EditorLabel.Text = Text("编辑记忆", "Edit memory");
            DeleteButton.IsEnabled = !_busy;
        }
        else ResetEditor();
    }

    private void ResetEditor()
    {
        ContentBox.Clear();
        LockedBox.IsChecked = true;
        EditorLabel.Text = Text("新增记忆", "New memory");
        DeleteButton.IsEnabled = false;
    }

    private async Task ReloadSafeAsync(long? selectedMemoryId = null)
    {
        if (_busy) return;
        SetBusy(true);
        try { await ReloadAsync(selectedMemoryId); }
        catch (Exception ex) { StatusText.Text = Text("读取记忆失败：", "Could not load memories: ") + DescribeError(ex); }
        finally { SetBusy(false); }
    }

    private async Task ReloadAsync(long? selectedMemoryId = null)
    {
        _memories.Clear();
        if (CharacterPicker.SelectedItem is not Character character) return;
        var memories = await _service.GetAsync(character.Id);
        foreach (var memory in memories.OrderByDescending(x => x.IsLocked).ThenBy(x => x.Id)) _memories.Add(memory);
        CountText.Text = Text(
            $"{memories.Count} / {CharacterMemoryService.MaxMemoriesPerCharacter} 条 · 每条最多 {CharacterMemoryService.MemoryLengthLimit} 字",
            $"{memories.Count} / {CharacterMemoryService.MaxMemoriesPerCharacter} memories · {CharacterMemoryService.MemoryLengthLimit} characters each");
        Title = character.Name + Text(" · 角色记忆", " · Character memories");
        StatusText.Text = memories.Count == 0
            ? Text("暂无记忆，可以手动添加。", "No memories yet. Add one manually.")
            : Text("选中记忆即可编辑内容或更改锁定状态。", "Select a memory to edit its content or change its lock.");
        MemoryList.SelectedItem = _memories.FirstOrDefault(x => x.Id == selectedMemoryId);
        if (MemoryList.SelectedItem is null) ResetEditor();
    }

    private void New_OnClick(object sender, RoutedEventArgs e)
    {
        MemoryList.SelectedItem = null;
        ResetEditor();
        ContentBox.Focus();
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || CharacterPicker.SelectedItem is not Character character) return;
        SetBusy(true);
        try
        {
            CharacterMemory memory;
            if (MemoryList.SelectedItem is CharacterMemory selected)
            {
                memory = await _service.UpdateManualAsync(character.Id, selected.Id, ContentBox.Text, LockedBox.IsChecked == true);
            }
            else memory = await _service.AddManualAsync(character.Id, ContentBox.Text, LockedBox.IsChecked == true);
            _changed = true;
            await ReloadAsync(memory.Id);
            StatusText.Text = Text("记忆已保存。", "Memory saved.");
        }
        catch (Exception ex) { StatusText.Text = DescribeError(ex); }
        finally { SetBusy(false); }
    }

    private async void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || MemoryList.SelectedItem is not CharacterMemory memory) return;
        if (!ConfirmDeleteDialog.Show(this, Text("删除角色记忆", "Delete character memory"),
                Text("确认删除这条记忆？", "Delete this memory?"), memory.Content, Text("确认删除", "Delete"))) return;
        SetBusy(true);
        try
        {
            await _service.DeleteAsync(memory.Id);
            _changed = true;
            await ReloadAsync();
            StatusText.Text = Text("记忆已删除。", "Memory deleted.");
        }
        catch (Exception ex) { StatusText.Text = Text("删除记忆失败：", "Could not delete memory: ") + DescribeError(ex); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CharacterPicker.IsEnabled = !busy;
        MemoryList.IsEnabled = !busy;
        ContentBox.IsEnabled = !busy;
        LockedBox.IsEnabled = !busy;
        ActionButtons.IsEnabled = !busy;
        DoneButton.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy && MemoryList.SelectedItem is CharacterMemory;
    }
}
