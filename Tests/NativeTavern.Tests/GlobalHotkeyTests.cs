using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class GlobalHotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+T", 0x0002u | 0x0001u)]
    [InlineData("ctrl+alt+t", 0x0002u | 0x0001u)]
    [InlineData("Ctrl + Alt + T", 0x0002u | 0x0001u)]
    [InlineData("Control+Shift+F9", 0x0002u | 0x0004u)]
    [InlineData("Win+Alt+B", 0x0008u | 0x0001u)]
    public void TryParseReadsModifiersInAnyOrder(string text, uint expectedModifiers)
    {
        Assert.True(GlobalHotkeyService.TryParse(text, out var modifiers, out var key));
        Assert.Equal(expectedModifiers, modifiers);
        Assert.True(key > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("T")]                 // no modifier: would swallow the key system-wide
    [InlineData("Ctrl")]               // modifier only, no key
    [InlineData("Ctrl+Alt")]           // two modifiers, still no key
    [InlineData("Ctrl+Alt+T+B")]       // two keys
    [InlineData("Ctrl+NotAKey")]
    [InlineData("Ctrl++")]
    public void TryParseRejectsUnsafeOrInvalidText(string? text)
    {
        Assert.False(GlobalHotkeyService.TryParse(text, out _, out _));
    }

    [Fact]
    public void TryParseMapsLettersToVirtualKeys()
    {
        Assert.True(GlobalHotkeyService.TryParse("Ctrl+Alt+T", out _, out var key));
        Assert.Equal((uint)'T', key);
    }
}
