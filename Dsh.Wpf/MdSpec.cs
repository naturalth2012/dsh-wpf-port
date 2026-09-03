using System.Windows;

namespace Dsh.Wpf;

/// <summary>
/// M1/M4/自绘重构：Markdown 排版常量（字号/间距/圆角/字体）。自绘控件统一从此取值，
/// 保证所有 Markdown 块（正文/标题/列表/引用/代码/表格/分隔线）排版节奏一致。
/// </summary>
public static class MdSpec
{
    // ---- 字号 ----
    public const double Body = 13;          // 正文
    public const double Small = 12;         // 引用 / 代码 / 表格
    public const double Code = 12;          // 代码正文
    public const double CodeHeader = 10;    // 代码语言标签 / 复制按钮
    public const double Heading1 = 20;
    public const double Heading2 = 17;
    public const double Heading3 = 15;
    public const double Heading4 = 14;
    public const double Heading5 = 13;
    public const double Heading6 = 13;

    // ---- 间距 ----
    public const double BlockGap = 5;       // 块之间垂直间距
    public const double LineHeight = 1.4;   // 正文行距系数

    // ---- 代码块 ----
    public const double CodeMaxHeight = 360;
    public const double CodeCorner = 4;

    // ---- 气泡 ----
    public static readonly Thickness BubblePadding = new(12, 8, 12, 8);
    public static readonly CornerRadius BubbleRadius = new(0, 12, 12, 12);
    public static readonly Thickness BubbleAccentBar = new(3, 0, 0, 0);

    /// <summary>映射 Markdown 标题级别（1-6）到字号。</summary>
    public static double HeadingSize(int level) => level switch
    {
        <= 1 => Heading1,
        2 => Heading2,
        3 => Heading3,
        4 => Heading4,
        5 => Heading5,
        _ => Heading6,
    };
}
