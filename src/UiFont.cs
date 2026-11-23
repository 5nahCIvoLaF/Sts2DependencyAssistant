using Godot;

namespace Sts2DependencyAssistant;

/// <summary>
/// 窗口与浮动按钮共用微软雅黑字体。
/// </summary>
internal static class UiFont
{
    public const int DefaultFontSize = 18;
    public const int TitleFontSize = 26;
    public const int RowTitleFontSize = 19;
    public const int HintFontSize = 15;

    // 状态颜色：红=严重缺失，黄=需处理，绿=正常
    public static readonly Color ColorMissing = new(0.95f, 0.35f, 0.35f);
    public static readonly Color ColorWarning = new(1.0f, 0.78f, 0.30f);
    public static readonly Color ColorOk = new(0.40f, 0.85f, 0.50f);
    public static readonly Color ColorMuted = new(0.65f, 0.68f, 0.72f);

    public static Theme CreateTheme(int fontSize = DefaultFontSize)
    {
        var font = new SystemFont
        {
            FontNames = new[]
            {
                "Microsoft YaHei",
                "Microsoft YaHei UI",
                "SimHei",
                "SimSun",
                "Noto Sans CJK SC",
                "sans-serif"
            },
            AllowSystemFallback = true
        };

        var theme = new Theme();
        theme.DefaultFont = font;
        theme.DefaultFontSize = fontSize;
        return theme;
    }
}
