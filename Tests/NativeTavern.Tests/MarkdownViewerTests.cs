using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using NativeTavern.Controls;
using Xunit;

namespace NativeTavern.Tests;

public sealed class MarkdownViewerTests
{
    [Fact]
    public void RendersCommonMarkdownBlocks()
    {
        RunSta(() =>
        {
            var viewer = new MarkdownViewer
            {
                Markdown = "# 标题\n\n- 第一项\n- 第二项\n\n`inline`\n\n```csharp\nvar n = 1;\n```"
            };

            viewer.RenderNow();

            Assert.IsType<Paragraph>(viewer.Document.Blocks.FirstBlock);
            Assert.Contains(viewer.Document.Blocks, block => block is System.Windows.Documents.List);
            Assert.Contains(viewer.Document.Blocks.OfType<Paragraph>(), paragraph =>
                paragraph.FontFamily.Source.Contains("Cascadia Mono", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void OnlyMakesHttpLinksNavigable()
    {
        RunSta(() =>
        {
            var viewer = new MarkdownViewer
            {
                Markdown = "[安全](https://example.com) [本地](file:///C:/secret.txt)"
            };

            viewer.RenderNow();

            var paragraph = Assert.IsType<Paragraph>(viewer.Document.Blocks.FirstBlock);
            var links = paragraph.Inlines.OfType<Hyperlink>().ToArray();
            Assert.Equal(2, links.Length);
            Assert.Equal("https", links[0].NavigateUri?.Scheme);
            Assert.Null(links[1].NavigateUri);
        });
    }

    [Fact]
    public void PreservesSingleLineBreaksAndShowsHtmlAsText()
    {
        RunSta(() =>
        {
            var viewer = new MarkdownViewer { Markdown = "第一行\n第二行 <b>原样显示</b>" };

            viewer.RenderNow();

            var paragraph = Assert.IsType<Paragraph>(viewer.Document.Blocks.FirstBlock);
            Assert.Contains(paragraph.Inlines, inline => inline is LineBreak);
            var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
            Assert.Contains("<b>", text);
            Assert.Contains("</b>", text);
        });
    }

    [Fact]
    public void RenderedMarkdownFollowsThemeResourceReplacement()
    {
        RunSta(() =>
        {
            var viewer = new MarkdownViewer
            {
                Markdown = "> Quote\n\n---\n\n`inline`\n\n```\ncode\n```\n\n[link](https://example.com)"
            };
            viewer.RenderNow();
            var document = viewer.Document;
            string[] resourceKeys =
            [
                "MarkdownQuoteBorderBrush", "MarkdownQuoteTextBrush", "MarkdownRuleBrush",
                "MarkdownCodeBrush", "MarkdownInlineCodeBrush", "MarkdownLinkBrush"
            ];
            var firstBrush = new SolidColorBrush(Colors.DarkSlateBlue);
            foreach (var key in resourceKeys) document.Resources[key] = firstBrush;

            var quote = Assert.IsType<Section>(document.Blocks.FirstBlock);
            var rule = Assert.IsType<Border>(document.Blocks.OfType<BlockUIContainer>().Single().Child);
            var inlineCode = document.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Run>())
                .Single(run => run.Text == "inline");
            var code = document.Blocks.OfType<Paragraph>()
                .Single(paragraph => paragraph.Inlines.OfType<Run>().Any(run => run.Text == "code"));
            var link = document.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Hyperlink>()).Single();

            Assert.Same(firstBrush, quote.BorderBrush);
            Assert.Same(firstBrush, quote.Foreground);
            Assert.Same(firstBrush, rule.Background);
            Assert.Same(firstBrush, code.Background);
            Assert.Same(firstBrush, inlineCode.Background);
            Assert.Same(firstBrush, link.Foreground);

            var replacementBrush = new SolidColorBrush(Colors.PaleGoldenrod);
            foreach (var key in resourceKeys) document.Resources[key] = replacementBrush;

            Assert.Same(replacementBrush, quote.BorderBrush);
            Assert.Same(replacementBrush, quote.Foreground);
            Assert.Same(replacementBrush, rule.Background);
            Assert.Same(replacementBrush, code.Background);
            Assert.Same(replacementBrush, inlineCode.Background);
            Assert.Same(replacementBrush, link.Foreground);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
