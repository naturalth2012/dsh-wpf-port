using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Dsh.App; // MarkdownRenderer / MarkdownBlock / InlineRun
using Localization = Dsh.App.Services.Localization;

namespace Dsh.Viewer;

/// <summary>
/// Renders one <see cref="SessionFold.Row"/> as a self-contained rich card for the offline
/// Surface view: a role chip, an optional collapsed reasoning block, Markdown-rendered body
/// (via the shared <see cref="MarkdownRenderer"/> in Dsh.App — no Dsh.Wpf dependency), or a
/// tool-call card for tool rows. Uses fixed readable colors so the standalone viewer needs no
/// app theme; see os/03 L3.
/// </summary>
public sealed class MessageRowControl : ContentControl
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    // Role colours for the chip + left accent.
    private static readonly Brush UserAccent = Brush("#2E7CF6");
    private static readonly Brush AssistantAccent = Brush("#10A37F");
    private static readonly Brush ToolAccent = Brush("#B7791F");
    private static readonly Brush ErrorAccent = Brush("#C5221F");
    private static readonly Brush ReasoningBrush = Brush("#5F6368");
    private static readonly Brush CodeBg = Brush("#F1F3F4");
    private static readonly Brush TextPrimary = Brush("#202124");
    private static readonly Brush Muted = Brush("#5F6368");

    public static readonly DependencyProperty RowProperty =
        DependencyProperty.Register(nameof(Row), typeof(SessionFold.Row), typeof(MessageRowControl),
            new PropertyMetadata(null, (d, _) => ((MessageRowControl)d).Rebuild()));

    public SessionFold.Row? Row
    {
        get => (SessionFold.Row?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public MessageRowControl()
    {
        Content = null;
    }

    private void Rebuild()
    {
        Content = Row is null ? null : Build(Row);
    }

    private static Brush Brush(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return new SolidColorBrush(c);
    }

    private UIElement Build(SessionFold.Row row)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };

        // turn separators / group rows: a slim centred divider line.
        if (row.Role == "turn")
        {
            panel.Children.Add(BuildTurnDivider(row.Text));
            return panel;
        }

        // Header: role chip + (for user) maybe a label; left accent border via a Border wrapper.
        var accent = row.Role switch
        {
            "user" => UserAccent,
            "assistant" => AssistantAccent,
            "tool" => ToolAccent,
            "error" => ErrorAccent,
            _ => Muted,
        };

        var header = BuildHeader(row);
        panel.Children.Add(header);

        // Reasoning (collapsed).
        if (!string.IsNullOrWhiteSpace(row.Reasoning))
        {
            panel.Children.Add(BuildReasoningExpander(row.Reasoning!));
        }

        // Body: tool rows render a tool card; user/assistant rows render Markdown text.
        if (row.Role == "tool" && row.Tool is not null)
        {
            panel.Children.Add(BuildToolCard(row.Tool));
        }
        else if (!string.IsNullOrWhiteSpace(row.Text))
        {
            panel.Children.Add(BuildMarkdownBody(row.Text));
        }

        // Wrap everything in a left-accent Border (role coloured) for a card feel.
        return new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 1, 0, 1),
            Background = row.Role == "tool"
                ? new SolidColorBrush(Color.FromArgb(0x0C, 0xB7, 0x79, 0x1F))
                : Brushes.Transparent,
            Child = panel,
        };
    }

    private UIElement BuildTurnDivider(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = Muted,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 6),
        };
        return tb;
    }

    private UIElement BuildHeader(SessionFold.Row row)
    {
        string label = row.Role switch
        {
            "user" => Localization.Get("Viewer.Role.User"),
            "assistant" => Localization.Get("Viewer.Role.Assistant"),
            "tool" => Localization.Get("Viewer.Role.Tool"),
            "error" => Localization.Get("Viewer.Role.Error"),
            "turn" => Localization.Get("Viewer.Role.Turn"),
            _ => row.Role,
        };
        var chipBg = row.Role switch
        {
            "user" => Brush("#E8F0FE"),
            "assistant" => Brush("#E6F4EA"),
            "tool" => Brush("#FEF7E0"),
            "error" => Brush("#FCE8E6"),
            _ => Brush("#F1F3F4"),
        };
        var chip = new Border
        {
            Background = chipBg,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 2),
            Child = new TextBlock { Text = label, FontSize = 10, Foreground = Muted, FontWeight = FontWeights.SemiBold },
        };

        var row2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 2),
        };
        row2.Children.Add(chip);
        if (row.Role == "tool" && row.Tool is { } t && !string.IsNullOrWhiteSpace(t.Name))
        {
            var name = new TextBlock
            {
                Text = "  " + t.Name,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row2.Children.Add(name);
        }
        return row2;
    }

    private UIElement BuildReasoningExpander(string reasoning)
    {
        var body = new TextBlock
        {
            Text = reasoning,
            FontSize = 12,
            Foreground = ReasoningBrush,
            FontStyle = FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap,
        };
        var expander = new Expander
        {
            Header = new TextBlock { Text = Localization.Get("Viewer.Reasoning"), FontSize = 11, Foreground = Muted, FontWeight = FontWeights.SemiBold },
            IsExpanded = false,
            Margin = new Thickness(0, 2, 0, 2),
        };
        expander.Content = new Border
        {
            Background = Brush("#F8F9FA"),
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(4),
            Child = body,
        };
        return expander;
    }

    /// <summary>Render Markdown text via the shared Markdig-based renderer in Dsh.App.</summary>
    private UIElement BuildMarkdownBody(string text)
    {
        var stack = new StackPanel();
        foreach (var block in MarkdownRenderer.Render(text))
        {
            var el = BuildBlock(block);
            if (el != null) stack.Children.Add(el);
        }
        // If Render returned nothing (blank), show the raw text so nothing is lost.
        if (stack.Children.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        }
        return stack;
    }

    private UIElement? BuildBlock(MarkdownBlock b) => b.Kind switch
    {
        MarkdownBlockKind.Paragraph => MakeRichText(b.Runs, 13, TextPrimary),
        MarkdownBlockKind.Heading => MakeRichText(b.Runs, 15, TextPrimary, FontWeights.SemiBold),
        MarkdownBlockKind.ListItem => MakeListRow(b),
        MarkdownBlockKind.Quote => MakeQuote(b.Runs),
        MarkdownBlockKind.Code => MakeCodeBlock(b),
        MarkdownBlockKind.Table => MakeTextFallback(b),
        MarkdownBlockKind.ThematicBreak => new Border { Height = 1, Background = Brush("#DADCE0"), Margin = new Thickness(0, 4, 0, 4) },
        _ => MakeRichText(b.Runs, 13, TextPrimary),
    };

    private UIElement MakeListRow(MarkdownBlock b)
    {
        var content = MakeRichText(b.Runs, 13, TextPrimary);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness((b.Level ?? 0) * 16, 1, 0, 1) };
        panel.Children.Add(new TextBlock { Text = b.ListMarker + " ", FontSize = 13, Foreground = TextPrimary });
        panel.Children.Add(content);
        return panel;
    }

    private UIElement MakeQuote(IReadOnlyList<InlineRun>? runs)
    {
        var inner = MakeRichText(runs, 12, Muted);
        return new Border
        {
            Background = Brush("#F8F9FA"),
            BorderBrush = Brush("#DADCE0"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(0, 4, 4, 0),
            Margin = new Thickness(0, 2, 0, 2),
            Child = inner,
        };
    }

    private UIElement MakeCodeBlock(MarkdownBlock b)
    {
        var body = new TextBlock
        {
            Text = b.Text ?? "",
            FontFamily = Mono,
            FontSize = 12,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.NoWrap,
        };
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 400,
            Content = body,
        };
        var header = new TextBlock
        {
            Text = b.Language ?? "code",
            FontFamily = Mono,
            FontSize = 11,
            Foreground = Muted,
            Margin = new Thickness(8, 3, 0, 0),
        };
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(scroller);
        return new Border
        {
            Background = CodeBg,
            BorderBrush = Brush("#DADCE0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 2, 0, 2),
            Child = stack,
        };
    }

    private UIElement MakeTextFallback(MarkdownBlock b)
    {
        // Tables and anything unsupported render as plain text rather than being dropped.
        return new TextBlock { Text = b.Text ?? "", TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = TextPrimary };
    }

    private TextBlock MakeRichText(IReadOnlyList<InlineRun>? runs, double size, Brush color, FontWeight? weight = null)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, Foreground = color };
        if (weight.HasValue) tb.FontWeight = weight.Value;
        if (runs != null)
        {
            foreach (var run in runs) tb.Inlines.Add(BuildInline(run));
        }
        return tb;
    }

    private Inline BuildInline(InlineRun run) => run.Kind switch
    {
        InlineKind.Bold => new Bold(new Run(run.Text)),
        InlineKind.Italic => new Italic(new Run(run.Text)),
        InlineKind.InlineCode => new Run(run.Text)
        {
            FontFamily = Mono,
            Background = Brush("#E8EAED"),
            Foreground = TextPrimary,
        },
        InlineKind.Link => MakeHyperlink(run.Text, run.Url),
        InlineKind.File => MakeHyperlink(run.Text, run.Url),
        _ => new Run(run.Text),
    };

    private Hyperlink MakeHyperlink(string text, string? url)
    {
        var link = new Hyperlink(new Run(text)) { Foreground = UserAccent };
        if (!string.IsNullOrWhiteSpace(url))
        {
            try { link.NavigateUri = new Uri(url, UriKind.RelativeOrAbsolute); }
            catch { link.NavigateUri = null; }
        }
        link.RequestNavigate += (_, e) =>
        {
            e.Handled = true;
            if (!string.IsNullOrWhiteSpace(url))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { /* ignore */ }
            }
        };
        return link;
    }

    /// <summary>A simple tool-call card: name + status + arguments + output.</summary>
    private UIElement BuildToolCard(SessionFold.ToolCallNode tool)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        title.Children.Add(new TextBlock { Text = $"🔧 {tool.Name}", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ToolAccent });
        var statusColor = tool.Status == "error" ? ErrorAccent : Muted;
        title.Children.Add(new TextBlock
        {
            Text = $"  [{tool.Status}]",
            FontSize = 11,
            Foreground = statusColor,
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(title);

        if (!string.IsNullOrWhiteSpace(tool.Arguments))
        {
            stack.Children.Add(MakeMonoBlock(Localization.Get("Viewer.Tool.Args"), tool.Arguments));
        }

        if (!string.IsNullOrWhiteSpace(tool.Output))
        {
            stack.Children.Add(MakeMonoBlock(
                tool.Status == "error" ? Localization.Get("Viewer.Tool.ResultError") : Localization.Get("Viewer.Tool.Result"),
                tool.Output));
        }

        if (tool.Children is { Count: > 0 })
        {
            var sub = new StackPanel { Margin = new Thickness(12, 2, 0, 0) };
            foreach (var child in tool.Children) sub.Children.Add(BuildSubTool(child));
            stack.Children.Add(sub);
        }
        return stack;
    }

    private UIElement BuildSubTool(SessionFold.ToolCallNode tool)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };
        stack.Children.Add(new TextBlock
        {
            Text = $"↳ {tool.Name}  [{tool.Status}]",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ToolAccent,
        });
        if (!string.IsNullOrWhiteSpace(tool.Arguments)) stack.Children.Add(MakeMonoBlock(null, tool.Arguments));
        if (!string.IsNullOrWhiteSpace(tool.Output)) stack.Children.Add(MakeMonoBlock(null, tool.Output));
        return stack;
    }

    private UIElement MakeMonoBlock(string? label, string content)
    {
        // Prevent absurdly long tool output from freezing the layout: cap the visible height.
        const int maxLen = 4096;
        string shown = content.Length <= maxLen ? content : content[..maxLen] + "\n" + Localization.Get("Viewer.Truncated");
        var body = new TextBlock
        {
            Text = shown,
            FontFamily = Mono,
            FontSize = 11,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };
        if (!string.IsNullOrWhiteSpace(label))
        {
            panel.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = Muted, FontWeight = FontWeights.SemiBold });
        }
        panel.Children.Add(body);
        return panel;
    }
}
