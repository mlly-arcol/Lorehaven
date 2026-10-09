using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace NativeTavern.Services;

/// <summary>
/// System-wide hotkeys registered with Win32 RegisterHotKey on the main window handle, so they keep
/// working while the window is unfocused or hidden in the tray. Two bindings are supported: a toggle
/// that shows or hides the window, and a boss key that always hides it.
/// </summary>
public sealed class GlobalHotkeyService(ILogger<GlobalHotkeyService> logger) : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int ToggleHotkeyId = 0x51A1;
    private const int BossHotkeyId = 0x51A2;

    private HwndSource? _source;
    private IntPtr _handle;
    private Action? _toggleWindow;
    private Action? _bossKey;

    /// <summary>Hooks the window message loop. Actions always run on the UI thread.</summary>
    public void Attach(Window window, Action toggleWindow, Action bossKey)
    {
        Detach();
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        _toggleWindow = toggleWindow;
        _bossKey = bossKey;
    }

    /// <summary>Re-registers both hotkeys from the current settings; invalid or occupied keys are skipped.</summary>
    public void Apply(bool enabled, string? toggleHotkey, string? bossHotkey)
    {
        UnregisterAll();
        if (!enabled || _handle == IntPtr.Zero) return;
        Register(ToggleHotkeyId, toggleHotkey);
        Register(BossHotkeyId, bossHotkey);
    }

    public void Detach()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
        _handle = IntPtr.Zero;
        _toggleWindow = null;
        _bossKey = null;
    }

    public void Dispose() => Detach();

    private void Register(int id, string? text)
    {
        if (!TryParse(text, out var modifiers, out var key)) return;
        if (!RegisterHotKey(_handle, id, modifiers | ModNoRepeat, key))
            logger.LogWarning("Global hotkey {Hotkey} could not be registered; another app may own it.", text);
    }

    private void UnregisterAll()
    {
        if (_handle == IntPtr.Zero) return;
        UnregisterHotKey(_handle, ToggleHotkeyId);
        UnregisterHotKey(_handle, BossHotkeyId);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        var id = wParam.ToInt32();
        if (id == ToggleHotkeyId) _toggleWindow?.Invoke();
        else if (id == BossHotkeyId) _bossKey?.Invoke();
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>Parses text like "Ctrl+Alt+T" into Win32 modifier and virtual-key flags.</summary>
    internal static bool TryParse(string? text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        var keyPart = 0;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= 0x0002; break;
                case "alt": modifiers |= 0x0001; break;
                case "shift": modifiers |= 0x0004; break;
                case "win": modifiers |= 0x0008; break;
                default:
                    if (keyPart > 0 || !Enum.TryParse<Key>(part, ignoreCase: true, out var parsed)) return false;
                    var virtualKey = KeyInterop.VirtualKeyFromKey(parsed);
                    if (virtualKey <= 0) return false;
                    key = (uint)virtualKey;
                    keyPart++;
                    break;
            }
        }
        // A lone key without modifiers would swallow that key across the whole system.
        return modifiers != 0 && key != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
