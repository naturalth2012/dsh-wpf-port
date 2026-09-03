using Dsh.App;
using Xunit;

namespace Dsh.App.Tests;

/// <summary>
/// 自绘重构（A1，消除"新增 kind 静默空白"）：校验 MarkdownRenderer 产出的所有块 kind
/// 都落在 <see cref="MarkdownBlockKind"/> 已枚举的 7 种集合内（控件据此 switch 渲染）。
/// 新增 kind 会让此测试失败，提示同步控件与枚举。
/// </summary>
public class MarkdownKindCoverageTests
{
    private static readonly MarkdownBlockKind[] AllKinds =
    [
        MarkdownBlockKind.Paragraph, MarkdownBlockKind.Heading, MarkdownBlockKind.ListItem,
        MarkdownBlockKind.Quote, MarkdownBlockKind.Code, MarkdownBlockKind.Table,
        MarkdownBlockKind.ThematicBreak,
    ];

    [Fact]
    public void Renderer_emits_only_handled_kinds()
    {
        var markdown =
            "# Title\n\n" +
            "Plain paragraph with **bold** and `code`.\n\n" +
            "- item one\n- item two\n\n" +
            "1. ordered one\n2. ordered two\n\n" +
            "> a quote\n\n" +
            "```csharp\nvar x = 1;\n```\n\n" +
            "| a | b |\n|---|---|\n| 1 | 2 |\n\n" +
            "---\n\n" +
            "`src/foo.cs` and [link](https://example.com)\n";

        var blocks = MarkdownRenderer.Render(markdown);

        Assert.NotEmpty(blocks);
        foreach (var b in blocks)
        {
            Assert.Contains(b.Kind, AllKinds);
        }
    }

    [Fact]
    public void Comprehensive_doc_covers_all_kinds()
    {
        var markdown =
            "# H\n\npara\n\n- list\n\n> quote\n\n```\ncode\n```\n\n| a |\n|---|\n| 1 |\n\n---";

        var kinds = MarkdownRenderer.Render(markdown).Select(b => b.Kind).ToHashSet();
        Assert.True(kinds.SetEquals(AllKinds),
            $"期望恰好覆盖全部 kind，实际缺/多: {string.Join(",", kinds)}");
    }
}
