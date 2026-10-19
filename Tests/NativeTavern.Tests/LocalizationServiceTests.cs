using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

/// <summary>
/// The translation table is a hand-maintained dictionary applied to literal XAML strings, so both a
/// duplicated key and a label that was never added fail silently in the running app.
/// </summary>
public sealed class LocalizationServiceTests
{
    /// <summary>Terms that stay English on purpose, plus glyph-only labels.</summary>
    private static readonly string[] TechnicalTerms =
    [
        "API Key", "Base URL", "Top P", "Ctrl+N", "Lorehaven", "Y", "TXT", "MD", "PDF"
    ];

    private static readonly Regex LiteralAttribute = new(
        @"(?<![A-Za-z])(?<attr>Content|Header|ToolTip|Text)=""(?<value>[A-Za-z][^""{}]*)""",
        RegexOptions.Compiled);

    [Fact]
    public void TranslationTableInitialisesWithoutDuplicateKeys()
    {
        var table = Table();
        Assert.NotEmpty(table);
        Assert.All(table, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));
    }

    [Fact]
    public void EveryLiteralMarkupStringCanBeTranslated()
    {
        var root = FindRepositoryRoot();
        if (root is null) return; // Single-file publish has no source tree alongside the binary.
        var table = Table();

        foreach (var file in Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories)
                     .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                                     && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            foreach (Match match in LiteralAttribute.Matches(File.ReadAllText(file)))
            {
                var value = System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value);
                if (value.Any(character => character > 127)) continue; // Already written in Chinese.
                if (TechnicalTerms.Contains(value)) continue;
                Assert.True(
                    table.ContainsKey(value),
                    $"{Path.GetFileName(file)} has {match.Groups["attr"].Value}=\"{value}\" with no EnglishToChinese entry.");
            }
        }
    }

    private static IReadOnlyDictionary<string, string> Table()
    {
        var field = typeof(LocalizationService)
            .GetField("EnglishToChinese", BindingFlags.NonPublic | BindingFlags.Static);
        return (IReadOnlyDictionary<string, string>)field!.GetValue(null)!;
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Services", "LocalizationService.cs"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }
}
