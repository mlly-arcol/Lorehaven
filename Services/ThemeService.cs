using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace NativeTavern.Services;

public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Restyles the application with semantic palette resources. DynamicResource consumers
/// also update when WPF has frozen a brush and the resource must be replaced.
/// </summary>
public static class ThemeService
{
    public sealed record ThemeColor(string ResourceKey, string Light, string Dark);
    public sealed record ThemeArtwork(
        string HeaderResourceKey,
        string SidebarResourceKey,
        string NotesResourceKey,
        string TextureResourceKey,
        IReadOnlyDictionary<string, double> Opacities);

    public const string HarborStyle = "harbor";
    public const string StarlitStyle = "starlit";

    // Keep the original palette available to callers and existing saved installations.
    public static readonly IReadOnlyList<ThemeColor> Palette =
    [
        new("WindowBrush", "#F5F1E8", "#18221F"),
        new("SidebarBrush", "#F0EADD", "#1D2B26"),
        new("PanelBrush", "#FFFCF6", "#23332D"),
        new("CardBrush", "#F3EEE4", "#2B3D35"),
        new("HoverBrush", "#E7E4D5", "#365045"),
        new("AccentBrush", "#244D47", "#A8C8AD"),
        new("AccentHoverBrush", "#356659", "#BED7BB"),
        new("TextBrush", "#242E2A", "#F2EDDF"),
        new("MutedTextBrush", "#626E63", "#B6C1B5"),
        new("SubtleTextBrush", "#71776D", "#A6B3A4"),
        new("BorderBrush", "#DED2BC", "#47584B"),
        new("DangerBrush", "#A74532", "#E39E88"),
        new("OnAccentBrush", "#FFF8E9", "#14251D"),
        new("MaskBrush", "#32000000", "#66000000"),
        new("StrongBorderBrush", "#A9A08A", "#7E9381"),
        new("NeutralBgBrush", "#F0EBE1", "#2B3932"),
        new("NeutralBorderBrush", "#DAD0BD", "#48594B"),
        new("SuccessTextBrush", "#3B7456", "#A4CCA9"),
        new("DangerTextBrush", "#A43E2D", "#EDAF99"),
        new("DangerSolidBrush", "#AD4935", "#A04C39"),
        new("DangerStrongTextBrush", "#863C2B", "#F3C1AC"),
        new("DangerSoftBgBrush", "#F9E8DE", "#423129"),
        new("DangerSoftBorderBrush", "#E9C4AE", "#775442"),
        new("WarningTextBrush", "#8A642D", "#DFC392"),
        new("WarningSoftBgBrush", "#F6EDD8", "#3D3727"),
        new("WarningSoftBorderBrush", "#E3D0A5", "#6C6143"),
        new("AccentTintBgBrush", "#E3E5D6", "#334A3D"),
        new("AccentTintBorderBrush", "#CBD2BB", "#5B725D"),
        new("AccentTintTextBrush", "#345D4D", "#C6DDC1"),
        new("AccentDeepTextBrush", "#264C3F", "#E2EADB"),
        new("TealBrush", "#3B6D5D", "#A4C9B0"),
        new("TealTintBgBrush", "#E8EDDF", "#2C4439"),
        new("TealTintBorderBrush", "#C9D6BF", "#506D58"),
        new("TealTintTextBrush", "#476957", "#C0D8BF"),
        new("ClayBrush", "#B85D36", "#E7AA7B"),
        new("ClayTintBgBrush", "#F9E5D7", "#49372D"),
        new("ClayTintBorderBrush", "#E9C5A9", "#79604A"),
        new("ClayTintTextBrush", "#9A4D2F", "#F0C6A1"),
        new("PaperEdgeBrush", "#E5D9C4", "#3C4C40"),
        new("MarkdownQuoteBorderBrush", "#B4B8BF", "#7E9381"),
        new("MarkdownQuoteTextBrush", "#555960", "#C4D0C3"),
        new("MarkdownRuleBrush", "#DADCE0", "#47584B"),
        new("MarkdownCodeBrush", "#F4F5F7", "#18251F"),
        new("MarkdownInlineCodeBrush", "#EEEFF2", "#334A3D"),
        new("MarkdownLinkBrush", "#0000FF", "#AFC8F5"),
    ];

    public static readonly IReadOnlyList<ThemeColor> StarlitPalette =
    [
        new("WindowBrush", "#F5F1EC", "#1D1A27"),
        new("SidebarBrush", "#EDE7E3", "#24202F"),
        new("PanelBrush", "#FCF9F3", "#2B2638"),
        new("CardBrush", "#F0EAE5", "#332D40"),
        new("HoverBrush", "#E5DCE8", "#463C55"),
        new("AccentBrush", "#5D4B79", "#DCC39A"),
        new("AccentHoverBrush", "#75618D", "#EAD3AE"),
        new("TextBrush", "#302B3B", "#F0EAF4"),
        new("MutedTextBrush", "#6F6479", "#C5BACF"),
        new("SubtleTextBrush", "#807389", "#B2A6BE"),
        new("BorderBrush", "#DCD1DF", "#51455F"),
        new("DangerBrush", "#A7444D", "#E7A1AB"),
        new("OnAccentBrush", "#FFF9EF", "#2C2335"),
        new("MaskBrush", "#3220192B", "#7820192B"),
        new("StrongBorderBrush", "#AEA0B8", "#9380A6"),
        new("NeutralBgBrush", "#EDE7E8", "#342C40"),
        new("NeutralBorderBrush", "#D6CBDC", "#574962"),
        new("SuccessTextBrush", "#426D5F", "#A9CDB8"),
        new("DangerTextBrush", "#A5404E", "#EDABB6"),
        new("DangerSolidBrush", "#AC4B58", "#A25165"),
        new("DangerStrongTextBrush", "#8D3547", "#F3C0CA"),
        new("DangerSoftBgBrush", "#F7E6E8", "#49303F"),
        new("DangerSoftBorderBrush", "#E5BFC9", "#7B4E65"),
        new("WarningTextBrush", "#8A642D", "#E4C995"),
        new("WarningSoftBgBrush", "#F7EDD9", "#443A2D"),
        new("WarningSoftBorderBrush", "#E3CCA0", "#756349"),
        new("AccentTintBgBrush", "#EAE1EF", "#41354F"),
        new("AccentTintBorderBrush", "#D3C3DF", "#786189"),
        new("AccentTintTextBrush", "#624D7D", "#DECCE9"),
        new("AccentDeepTextBrush", "#49355F", "#EDE0F4"),
        new("TealBrush", "#526F75", "#B0CCD0"),
        new("TealTintBgBrush", "#E6ECEC", "#2D414B"),
        new("TealTintBorderBrush", "#C5D6D8", "#52717D"),
        new("TealTintTextBrush", "#506A75", "#C4DBE0"),
        new("ClayBrush", "#A77439", "#E4BF83"),
        new("ClayTintBgBrush", "#F5EBD9", "#46392F"),
        new("ClayTintBorderBrush", "#E4CCA4", "#81684B"),
        new("ClayTintTextBrush", "#8E632F", "#E9CD9E"),
        new("PaperEdgeBrush", "#E2D7D9", "#44394F"),
        new("MarkdownQuoteBorderBrush", "#B4A1C4", "#9D87B2"),
        new("MarkdownQuoteTextBrush", "#6B5D79", "#D4C7DF"),
        new("MarkdownRuleBrush", "#DCD1DF", "#51455F"),
        new("MarkdownCodeBrush", "#EEE9F1", "#211B2D"),
        new("MarkdownInlineCodeBrush", "#E8DEEE", "#463651"),
        new("MarkdownLinkBrush", "#65447E", "#E4C995"),
    ];

    private const int DarkModeAttribute = 20;

    static ThemeService()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window) ApplyTitleBar(window);
            }));
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (Mode == AppThemeMode.System) Apply(AppThemeMode.System);
        };
    }

    public static AppThemeMode Mode { get; private set; } = AppThemeMode.System;
    public static string StyleCode { get; private set; } = StarlitStyle;
    public static bool IsDark { get; private set; }
    public static event Action? ThemeChanged;

    public static void Apply(AppThemeMode mode, bool? systemDark = null) =>
        Apply(mode, StyleCode, systemDark);

    public static void Apply(AppThemeMode mode, string? styleCode, bool? systemDark = null)
    {
        if (Application.Current is { } currentApp && !currentApp.Dispatcher.CheckAccess())
        {
            currentApp.Dispatcher.Invoke(() => Apply(mode, styleCode, systemDark));
            return;
        }

        Mode = mode;
        StyleCode = NormalizeStyle(styleCode);
        IsDark = ResolveIsDark(mode, systemDark ?? IsSystemDark());
        if (Application.Current is not { } app) return;

        foreach (var entry in GetPalette(StyleCode))
        {
            var color = ParseColor(IsDark ? entry.Dark : entry.Light);
            switch (app.Resources[entry.ResourceKey])
            {
                case SolidColorBrush brush when !brush.IsFrozen:
                    brush.Color = color;
                    break;
                default:
                    app.Resources[entry.ResourceKey] = new SolidColorBrush(color);
                    break;
            }
        }

        var artwork = GetArtwork(StyleCode, IsDark);
        ApplyArtwork(app, "ThemeHeaderArtwork", artwork.HeaderResourceKey);
        ApplyArtwork(app, "ThemeSidebarArtwork", artwork.SidebarResourceKey);
        ApplyArtwork(app, "ThemeNotesArtwork", artwork.NotesResourceKey);
        ApplyArtwork(app, "ThemePaperTexture", artwork.TextureResourceKey);
        foreach (var (resourceKey, opacity) in artwork.Opacities)
            app.Resources[resourceKey] = opacity;
        app.Resources["ComposerShadowColor"] = ParseColor(StyleCode == HarborStyle ? "#244D47" : "#5D4B79");

        foreach (Window window in app.Windows) ApplyTitleBar(window);
        ThemeChanged?.Invoke();
    }

    public static void Apply(string? modeCode) => Apply(ParseMode(modeCode));

    public static void Apply(string? modeCode, string? styleCode) => Apply(ParseMode(modeCode), styleCode);

    public static string NormalizeStyle(string? styleCode) =>
        styleCode?.Trim().ToLowerInvariant() == HarborStyle ? HarborStyle : StarlitStyle;

    public static IReadOnlyList<ThemeColor> GetPalette(string? styleCode) =>
        NormalizeStyle(styleCode) == HarborStyle ? Palette : StarlitPalette;

    public static ThemeArtwork GetArtwork(string? styleCode, bool isDark)
    {
        var isHarbor = NormalizeStyle(styleCode) == HarborStyle;
        var prefix = isHarbor ? "Harbor" : "Starlit";
        return new ThemeArtwork(
            prefix + "HeaderArtwork", prefix + "SidebarArtwork", prefix + "NotesArtwork", prefix + "PaperTexture",
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["ThemeHeaderOpacity"] = isHarbor ? isDark ? .18 : .78 : isDark ? .28 : .46,
                ["ThemeSidebarOpacity"] = isHarbor ? .3 : isDark ? .19 : .34,
                ["ThemeNotesOpacity"] = isHarbor ? isDark ? .16 : .8 : isDark ? .19 : .32,
                ["ThemeTextureOpacity"] = isDark ? .08 : .12,
                ["ThemeSidebarTextureOpacity"] = !isHarbor && isDark ? .08 : .16,
                ["ThemeWelcomeTextureOpacity"] = !isHarbor && isDark ? .09 : .18,
                ["ThemeChatTextureOpacity"] = !isHarbor && isDark ? .04 : .07,
                ["ThemeWelcomeHeaderOpacity"] = !isHarbor && isDark ? .72 : 1d,
            });
    }

    private static void ApplyArtwork(Application app, string targetKey, string sourceKey)
    {
        if (app.TryFindResource(sourceKey) is { } source) app.Resources[targetKey] = source;
    }

    public static AppThemeMode ParseMode(string? modeCode) =>
        modeCode?.Trim().ToLowerInvariant() switch
        {
            "light" => AppThemeMode.Light,
            "dark" => AppThemeMode.Dark,
            _ => AppThemeMode.System,
        };

    public static string ToCode(AppThemeMode mode) => mode.ToString().ToLowerInvariant();

    public static bool ResolveIsDark(AppThemeMode mode, bool systemDark) => mode switch
    {
        AppThemeMode.Dark => true,
        AppThemeMode.Light => false,
        _ => systemDark,
    };

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static Color ParseColor(string hex)
    {
        var value = hex.StartsWith("#", StringComparison.Ordinal) ? hex[1..] : hex;
        return value.Length switch
        {
            6 => Color.FromArgb(0xFF,
                Convert.ToByte(value[0..2], 16), Convert.ToByte(value[2..4], 16), Convert.ToByte(value[4..6], 16)),
            8 => Color.FromArgb(
                Convert.ToByte(value[0..2], 16), Convert.ToByte(value[2..4], 16),
                Convert.ToByte(value[4..6], 16), Convert.ToByte(value[6..8], 16)),
            _ => throw new FormatException($"'{hex}' is not a 6 or 8 digit hex color."),
        };
    }

    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = IsDark ? 1 : 0;
        try
        {
            _ = DwmSetWindowAttribute(handle, DarkModeAttribute, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Non-Windows hosts keep the default title bar.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
