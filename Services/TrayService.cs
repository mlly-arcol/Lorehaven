using System.Drawing;
using System.Windows.Forms;

namespace NativeTavern.Services;

public sealed class TrayService : IDisposable
{
    private NotifyIcon? _icon;
    private Icon? _ownedIcon;

    public void Initialize(Action showWindow)
    {
        if (_icon is not null) return;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Lorehaven", null, (_, _) => showWindow());
        menu.Items.Add("Exit", null, (_, _) => System.Windows.Application.Current.Shutdown());
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            _ownedIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        _icon = new NotifyIcon
        {
            Text = "Lorehaven",
            Icon = _ownedIcon ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => showWindow();
    }
    public void Notify(string title, string message) => _icon?.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
        _ownedIcon?.Dispose();
        _ownedIcon = null;
    }
}
