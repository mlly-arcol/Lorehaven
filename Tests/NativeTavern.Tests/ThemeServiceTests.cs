using System.IO;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

[Collection("Theme settings")]
public class ThemeServiceTests
{
    [Fact]
    public void PaletteKeysAreUniqueAndColorsParse()
    {
        foreach (var style in new[] { ThemeService.HarborStyle, ThemeService.StarlitStyle })
        {
            var palette = ThemeService.GetPalette(style);
            var keys = palette.Select(entry => entry.ResourceKey).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(ThemeService.Palette.Select(entry => entry.ResourceKey), keys);
            foreach (var entry in palette)
            {
                Assert.EndsWith("Brush", entry.ResourceKey);
                Assert.IsType<System.Windows.Media.Color>(ThemeService.ParseColor(entry.Light));
                Assert.IsType<System.Windows.Media.Color>(ThemeService.ParseColor(entry.Dark));
            }
        }
    }

    [Theory]
    [InlineData("harbor", false)]
    [InlineData("harbor", true)]
    [InlineData("starlit", false)]
    [InlineData("starlit", true)]
    public void BothStylesKeepReadableBodyAndButtonText(string style, bool dark)
    {
        var colors = ThemeService.GetPalette(style).ToDictionary(entry => entry.ResourceKey,
            entry => ThemeService.ParseColor(dark ? entry.Dark : entry.Light));
        Assert.True(Contrast(colors["TextBrush"], colors["PanelBrush"]) >= 4.5);
        Assert.True(Contrast(colors["TextBrush"], colors["WindowBrush"]) >= 4.5);
        Assert.True(Contrast(colors["OnAccentBrush"], colors["AccentBrush"]) >= 4.5);
        Assert.True(Contrast(colors["TextBrush"], colors["MarkdownCodeBrush"]) >= 4.5);
        Assert.True(Contrast(colors["MarkdownLinkBrush"], colors["PanelBrush"]) >= 4.5);
    }

    [Theory]
    [InlineData(AppThemeMode.Light, true, false)]
    [InlineData(AppThemeMode.Dark, false, true)]
    [InlineData(AppThemeMode.System, true, true)]
    [InlineData(AppThemeMode.System, false, false)]
    public void ChangingAppearancePreservesTheSelectedStyle(AppThemeMode mode, bool systemDark, bool expectedDark)
    {
        var originalMode = ThemeService.Mode;
        var originalStyle = ThemeService.StyleCode;
        var originalDark = ThemeService.IsDark;
        try
        {
            foreach (var style in new[] { ThemeService.HarborStyle, ThemeService.StarlitStyle })
            {
                ThemeService.Apply(AppThemeMode.Light, style);
                ThemeService.Apply(mode, systemDark);
                Assert.Equal(style, ThemeService.StyleCode);
                Assert.Equal(mode, ThemeService.Mode);
                Assert.Equal(expectedDark, ThemeService.IsDark);
            }
        }
        finally
        {
            ThemeService.Apply(originalMode, originalStyle, originalDark);
        }
    }

    [Fact]
    public void ArtworkTracksStyleAndDarkModeWithoutChangingHarborLightPresentation()
    {
        var harbor = ThemeService.GetArtwork("harbor", false);
        var starlit = ThemeService.GetArtwork("starlit", false);
        Assert.Equal("HarborHeaderArtwork", harbor.HeaderResourceKey);
        Assert.Equal("StarlitHeaderArtwork", starlit.HeaderResourceKey);
        Assert.Equal("HarborSidebarArtwork", harbor.SidebarResourceKey);
        Assert.Equal("StarlitNotesArtwork", starlit.NotesResourceKey);
        Assert.Equal("StarlitPaperTexture", starlit.TextureResourceKey);
        Assert.Equal(.78, harbor.Opacities["ThemeHeaderOpacity"]);
        Assert.Equal(.3, harbor.Opacities["ThemeSidebarOpacity"]);
        Assert.Equal(.8, harbor.Opacities["ThemeNotesOpacity"]);
        foreach (var style in new[] { "harbor", "starlit" })
        {
            var light = ThemeService.GetArtwork(style, false);
            var dark = ThemeService.GetArtwork(style, true);
            Assert.Equal(light.Opacities.Keys, dark.Opacities.Keys);
            foreach (var (key, opacity) in light.Opacities)
            {
                Assert.InRange(opacity, 0, 1);
                Assert.InRange(dark.Opacities[key], 0, opacity);
            }
        }
    }

    [Theory]
    [InlineData(" HARBOR ", "harbor")]
    [InlineData("starlit", "starlit")]
    [InlineData("unknown", "starlit")]
    [InlineData(null, "starlit")]
    public void StyleCodesNormalizeToSupportedResources(string? code, string expected)
    {
        Assert.Equal(expected, ThemeService.NormalizeStyle(code));
        Assert.Same(ThemeService.GetPalette(expected), ThemeService.GetPalette(code));
    }

    private static double Contrast(System.Windows.Media.Color first, System.Windows.Media.Color second)
    {
        static double Channel(byte value)
        {
            var channel = value / 255d;
            return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
        static double Luminance(System.Windows.Media.Color color) =>
            .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    [Fact]
    public void EveryPaletteKeyIsDeclaredInAppResources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? appXaml = null;
        while (directory is not null && appXaml is null)
        {
            var candidate = Path.Combine(directory.FullName, "App.xaml");
            if (File.Exists(candidate)) appXaml = candidate;
            directory = directory.Parent;
        }
        if (appXaml is null) return; // Single-file publish has no source tree alongside the binary.

        var markup = File.ReadAllText(appXaml);
        foreach (var entry in ThemeService.Palette)
            Assert.Contains($"x:Key=\"{entry.ResourceKey}\"", markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppThemeMode.Dark, false, true)]
    [InlineData(AppThemeMode.Dark, true, true)]
    [InlineData(AppThemeMode.Light, true, false)]
    [InlineData(AppThemeMode.Light, false, false)]
    [InlineData(AppThemeMode.System, true, true)]
    [InlineData(AppThemeMode.System, false, false)]
    public void ResolveIsDarkFollowsModeAndSystemPreference(AppThemeMode mode, bool systemDark, bool expected) =>
        Assert.Equal(expected, ThemeService.ResolveIsDark(mode, systemDark));

    [Theory]
    [InlineData("dark", AppThemeMode.Dark)]
    [InlineData("Dark", AppThemeMode.Dark)]
    [InlineData(" light ", AppThemeMode.Light)]
    [InlineData("light", AppThemeMode.Light)]
    [InlineData("system", AppThemeMode.System)]
    [InlineData(null, AppThemeMode.System)]
    [InlineData("", AppThemeMode.System)]
    [InlineData("nonsense", AppThemeMode.System)]
    public void ModeCodesRoundTrip(string? code, AppThemeMode expected)
    {
        var parsed = ThemeService.ParseMode(code);
        Assert.Equal(expected, parsed);
        var canonical = code?.Trim().ToLowerInvariant();
        if (canonical is "light" or "dark" or "system") Assert.Equal(canonical, ThemeService.ToCode(parsed));
    }
}
