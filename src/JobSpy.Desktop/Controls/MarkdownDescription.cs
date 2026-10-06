using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace JobSpy.Desktop.Controls;

public sealed class MarkdownDescription : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownDescription, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MarkdownProperty)
        {
            RenderMarkdown();
        }
    }

    private void RenderMarkdown()
    {
        var textBlock = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush.Parse("#52615A"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18
        };

        var document = Markdig.Markdown.Parse(Markdown ?? string.Empty);
        AppendBlocks(textBlock.Inlines!, document);
        Content = textBlock;
    }

    private static void AppendBlocks(InlineCollection target, IEnumerable<Block> blocks)
    {
        var hasBlock = false;
        foreach (var block in blocks)
        {
            if (hasBlock)
            {
                target.Add(new LineBreak());
                target.Add(new LineBreak());
            }

            switch (block)
            {
                case HeadingBlock heading:
                    var headingSpan = new Span
                    {
                        FontSize = heading.Level switch
                        {
                            1 => 18,
                            2 => 16,
                            _ => 14
                        },
                        FontWeight = FontWeight.Bold
                    };
                    AppendInlines(headingSpan.Inlines, heading.Inline);
                    target.Add(headingSpan);
                    break;
                case ParagraphBlock paragraph:
                    AppendInlines(target, paragraph.Inline);
                    break;
                case ListBlock list:
                    AppendList(target, list);
                    break;
                case QuoteBlock quote:
                    target.Add(new Run("| ") { Foreground = Brush.Parse("#718078") });
                    AppendBlocks(target, quote);
                    break;
                case FencedCodeBlock fencedCode:
                    target.Add(new Run(fencedCode.Lines.ToString())
                    {
                        FontFamily = new FontFamily("monospace"),
                        Foreground = Brush.Parse("#27634B")
                    });
                    break;
                case ThematicBreakBlock:
                    target.Add(new Run("--------------------------------"));
                    break;
            }

            hasBlock = true;
        }
    }

    private static void AppendList(InlineCollection target, ListBlock list)
    {
        var itemNumber = 1;
        foreach (var block in list)
        {
            if (block is not ListItemBlock item)
            {
                continue;
            }

            target.Add(new LineBreak());
            target.Add(new Run(list.IsOrdered ? $"{itemNumber}. " : "• "));
            AppendBlocks(target, item);
            itemNumber++;
        }
    }

    private static void AppendInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null)
        {
            return;
        }

        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = new FontFamily("monospace"),
                        Foreground = Brush.Parse("#27634B")
                    });
                    break;
                case EmphasisInline emphasis:
                    var emphasisSpan = new Span();
                    if (emphasis.DelimiterCount > 1)
                    {
                        emphasisSpan.FontWeight = FontWeight.Bold;
                    }
                    else
                    {
                        emphasisSpan.FontStyle = FontStyle.Italic;
                    }

                    AppendInlines(emphasisSpan.Inlines, emphasis);
                    target.Add(emphasisSpan);
                    break;
                case LinkInline link:
                    var linkSpan = new Span { Foreground = Brush.Parse("#397AA6") };
                    AppendInlines(linkSpan.Inlines, link);
                    target.Add(linkSpan);
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case ContainerInline nested:
                    AppendInlines(target, nested);
                    break;
            }
        }
    }
}