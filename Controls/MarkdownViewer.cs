using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using NativeTavern.Services;

namespace NativeTavern.Controls;

/// <summary>
/// Lightweight, selectable Markdown renderer for chat messages. Rendering is
/// debounced so token streaming does not rebuild the document for every chunk.
/// </summary>
public sealed class MarkdownViewer : FlowDocumentScrollViewer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(string.Empty, OnMarkdownChanged));

    private readonly DispatcherTimer _renderTimer;

    public MarkdownViewer()
    {
        IsToolBarVisible = false;
        IsSelectionEnabled = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        SetResourceReference(ForegroundProperty, "TextBrush");
        Loaded += (_, _) =>
        {
            ThemeService.ThemeChanged -= OnThemeChanged;
            ThemeService.ThemeChanged += OnThemeChanged;
            RenderNow();
        };
        Unloaded += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;

        _renderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(55)
        };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            RenderMarkdown();
        };
    }

    private void OnThemeChanged()
    {
        if (Dispatcher.CheckAccess()) RenderNow();
        else Dispatcher.InvokeAsync(RenderNow);
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        var viewer = (MarkdownViewer)dependencyObject;
        viewer._renderTimer.Stop();
        viewer._renderTimer.Start();
    }

    private void RenderMarkdown()
    {
        var document = CreateDocument();
        try
        {
            var markdownDocument = Markdig.Markdown.Parse(Markdown ?? string.Empty, Pipeline);
            foreach (var block in markdownDocument)
                AddBlock(document.Blocks, block);
        }
        catch
        {
            document.Blocks.Add(CreateParagraph(new Run(Markdown ?? string.Empty)));
        }

        if (document.Blocks.Count == 0)
            document.Blocks.Add(CreateParagraph(new Run(string.Empty)));

        Document = document;
    }

    internal void RenderNow()
    {
        _renderTimer.Stop();
        RenderMarkdown();
    }

    private FlowDocument CreateDocument() => new()
    {
        PagePadding = new Thickness(0),
        ColumnWidth = double.PositiveInfinity,
        FontFamily = FontFamily,
        FontSize = FontSize,
        Foreground = Foreground,
        LineHeight = 23
    };

    private static void AddBlock(BlockCollection target, Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var paragraph = CreateParagraph();
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.FontSize = heading.Level switch { 1 => 24, 2 => 21, 3 => 18, _ => 16 };
                paragraph.Margin = new Thickness(0, 7, 0, 6);
                AddInlines(paragraph.Inlines, heading.Inline);
                target.Add(paragraph);
                break;
            }
            case ParagraphBlock paragraphBlock:
            {
                var paragraph = CreateParagraph();
                AddInlines(paragraph.Inlines, paragraphBlock.Inline);
                target.Add(paragraph);
                break;
            }
            case FencedCodeBlock fencedCode:
                target.Add(CreateCodeBlock(fencedCode.Lines.ToString()));
                break;
            case CodeBlock code:
                target.Add(CreateCodeBlock(code.Lines.ToString()));
                break;
            case HtmlBlock html:
                target.Add(CreateParagraph(new Run(html.Lines.ToString())));
                break;
            case QuoteBlock quote:
            {
                var section = new Section
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 1, 0, 1),
                    Margin = new Thickness(0, 4, 0, 8)
                };
                section.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "MarkdownQuoteBorderBrush");
                section.SetResourceReference(TextElement.ForegroundProperty, "MarkdownQuoteTextBrush");
                foreach (var child in quote)
                    AddBlock(section.Blocks, child);
                target.Add(section);
                break;
            }
            case ListBlock listBlock:
                target.Add(CreateList(listBlock));
                break;
            case ThematicBreakBlock:
            {
                var rule = new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 8, 0, 10)
                };
                rule.SetResourceReference(Border.BackgroundProperty, "MarkdownRuleBrush");
                target.Add(new BlockUIContainer(rule));
                break;
            }
            case ContainerBlock container:
                foreach (var child in container)
                    AddBlock(target, child);
                break;
            case LeafBlock leaf when leaf.Inline is not null:
            {
                var paragraph = CreateParagraph();
                AddInlines(paragraph.Inlines, leaf.Inline);
                target.Add(paragraph);
                break;
            }
        }
    }

    private static System.Windows.Documents.List CreateList(ListBlock source)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = source.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(18, 2, 0, 8),
            Padding = new Thickness(10, 0, 0, 0)
        };

        foreach (var itemBlock in source.OfType<ListItemBlock>())
        {
            var item = new ListItem { Margin = new Thickness(0, 0, 0, 3) };
            foreach (var child in itemBlock)
                AddBlock(item.Blocks, child);
            list.ListItems.Add(item);
        }

        return list;
    }

    private static Paragraph CreateParagraph(params System.Windows.Documents.Inline[] inlines)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        paragraph.Inlines.AddRange(inlines);
        return paragraph;
    }

    private static Paragraph CreateCodeBlock(string code)
    {
        var paragraph = CreateParagraph(new Run(code.TrimEnd('\r', '\n')));
        paragraph.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        paragraph.FontSize = 13;
        paragraph.LineHeight = 20;
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "MarkdownCodeBrush");
        paragraph.Padding = new Thickness(12, 9, 12, 9);
        paragraph.Margin = new Thickness(0, 3, 0, 10);
        return paragraph;
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container)
    {
        for (var inline = container?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case LineBreakInline lineBreak:
                    target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                    break;
                case CodeInline code:
                {
                    var run = new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 13
                    };
                    run.SetResourceReference(TextElement.BackgroundProperty, "MarkdownInlineCodeBrush");
                    target.Add(run);
                    break;
                }
                case HtmlInline html:
                    target.Add(new Run(html.Tag));
                    break;
                case EmphasisInline emphasis:
                {
                    Span span;
                    if (emphasis.DelimiterChar == '~')
                        span = new Span { TextDecorations = TextDecorations.Strikethrough };
                    else
                        span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AddInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                }
                case LinkInline link when !link.IsImage:
                {
                    var hyperlink = new Hyperlink();
                    hyperlink.SetResourceReference(TextElement.ForegroundProperty, "MarkdownLinkBrush");
                    AddInlines(hyperlink.Inlines, link);
                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        hyperlink.NavigateUri = uri;
                        hyperlink.RequestNavigate += OpenLink;
                        hyperlink.ToolTip = uri.AbsoluteUri;
                    }
                    target.Add(hyperlink);
                    break;
                }
                case LinkInline image:
                {
                    var label = string.IsNullOrWhiteSpace(image.Title) ? "图片" : image.Title;
                    target.Add(new Run($"[{label}]") { FontStyle = FontStyles.Italic });
                    break;
                }
                case ContainerInline nested:
                    AddInlines(target, nested);
                    break;
                default:
                    target.Add(new Run(inline.ToString()));
                    break;
            }
        }
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme is not ("http" or "https")) return;
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
