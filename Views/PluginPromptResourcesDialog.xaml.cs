using System.Collections.ObjectModel;
using System.Windows;
using NativeTavern.Models;

namespace NativeTavern.Views;

public partial class PluginPromptResourcesDialog : Window
{
    public ObservableCollection<ResourceChoice> Choices { get; } = [];
    public IReadOnlyList<string> SelectedIds => Choices.Where(x => x.IsSelected && !x.AutoApply)
        .Select(x => x.QualifiedId).ToArray();

    private PluginPromptResourcesDialog(
        IReadOnlyList<PluginPromptResourceDescriptor> resources,
        IReadOnlyCollection<string> selectedIds,
        bool isGroupChat,
        bool english)
    {
        InitializeComponent();
        var selected = new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resources.OrderBy(x => x.PluginId).ThenBy(x => x.Kind).ThenBy(x => x.Name))
            Choices.Add(new ResourceChoice(resource, selected.Contains(resource.QualifiedId), isGroupChat, english));
        DataContext = this;
        EmptyHint.Visibility = Choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!english) return;
        Title = Heading.Text = "Plugin prompt resources";
        Hint.Text = "Selections apply only to this conversation. Disabling a plugin keeps your choices for later. " +
                    "At most 12 sections or 12,000 characters apply per request; later sections are omitted in application order.";
        EmptyHint.Text = "No resources available. Install a prompt resource pack in Plugins.";
        CancelButton.Content = "Cancel";
        SaveButton.Content = "Apply";
    }

    public static IReadOnlyList<string>? Show(
        Window owner,
        IReadOnlyList<PluginPromptResourceDescriptor> resources,
        IReadOnlyCollection<string> selectedIds,
        bool isGroupChat,
        bool english)
    {
        var dialog = new PluginPromptResourcesDialog(resources, selectedIds, isGroupChat, english) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.SelectedIds : null;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    public sealed class ResourceChoice
    {
        private readonly PluginPromptResourceDescriptor descriptor;
        public ResourceChoice(PluginPromptResourceDescriptor descriptor, bool selected, bool isGroupChat, bool english)
        {
            this.descriptor = descriptor;
            IsSelected = selected || descriptor.AutoApply;
            var kind = descriptor.Kind switch
            {
                "persona" => english ? "Persona" : "人设",
                "lorebook" => english ? "Lorebook" : "世界书",
                "preset" => english ? "Preset" : "提示词预设",
                _ => english ? "Text rule" : "文本规则"
            };
            Label = $"{descriptor.Name} · {kind} · {descriptor.PluginId}";
            var scopeMismatch = !descriptor.IsApplicableTo(isGroupChat);
            if (scopeMismatch)
                StateLabel = descriptor.Scope == "group"
                    ? english ? "Group chats only; inactive in this conversation" : "仅适用于群聊；当前对话不生效"
                    : english ? "Single chats only; inactive in this conversation" : "仅适用于单人对话；当前对话不生效";
            else if (!descriptor.IsEnabled)
                StateLabel = english ? "Plugin disabled" : "插件已停用";
            else
                StateLabel = descriptor.AutoApply
                    ? english ? "Applies automatically while enabled" : "启用后自动生效"
                    : string.Empty;
            CanSelect = descriptor.IsEnabled && !descriptor.AutoApply && !scopeMismatch;
        }
        public string QualifiedId => descriptor.QualifiedId;
        public string Label { get; }
        public string Preview => descriptor.Preview;
        public string StateLabel { get; }
        public bool CanSelect { get; }
        public bool AutoApply => descriptor.AutoApply;
        public bool IsSelected { get; set; }
    }
}
