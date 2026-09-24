using System.Text.RegularExpressions;
using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.Services;

/// <summary>
/// 两套主题的调色板：**资源键 → ARGB 色值**。
///
/// 设计要点（它决定了整个暗夜模式怎么实现）：
///
/// - 这里的键**就是** <c>Themes/Styles.xaml</c> 里的资源键名。运行时只做一件事：把资源字典里
///   这些 Brush **实例**的 <c>Color</c> 换成新主题的值。
/// - 于是 <c>MainWindow.xaml</c> 里那 160 多处 <c>{StaticResource XxxBrush}</c> **一个都不用改** ——
///   它们持有的是同一批 Brush 实例（StaticResource 只是把引用拷过去），改实例的颜色，引用方自动重绘。
///   反过来（把 StaticResource 全换成 DynamicResource、再整套换字典）要动 160 多处，漏一处就是
///   「切了主题那块不变」，而且这种漏**不报错**。
/// - 也正因如此，**两套字典的键集必须完全一致**：少一个键 = 切换后那个颜色不变 = 静默的局部失配。
///   回归 <c>CheckThemePaletteIsComplete</c> 把这条钉住 —— 它正是「清单式接线」最容易漏的那类。
/// - 这里**不出现任何 WPF 类型**（色值是字符串）：这样回归能纯离线地穷举两套表，
///   也方便和 XAML 里的字面量对照。
/// </summary>
public static class ThemePalette
{
    // ── 表面 / 容器 ──────────────────────────────────────────────
    public const string AppBackground = "AppBackgroundBrush";
    public const string CardBackground = "CardBackgroundBrush";
    public const string CardBorder = "CardBorderBrush";
    public const string Divider = "DividerBrush";
    public const string InnerPanelBackground = "InnerPanelBackgroundBrush";

    // ── 文本 ────────────────────────────────────────────────────
    public const string TextPrimary = "TextPrimaryBrush";
    public const string TextSecondary = "TextSecondaryBrush";
    public const string TextTertiary = "TextTertiaryBrush";
    public const string LogInfo = "LogInfoBrush";
    public const string ExpanderHeader = "ExpanderHeaderBrush";

    // ── 强调 / 状态 ──────────────────────────────────────────────
    public const string Accent = "AccentBrush";
    public const string AccentHover = "AccentHoverBrush";
    public const string AccentSoft = "AccentSoftBrush";
    public const string Success = "SuccessBrush";
    public const string Warning = "WarningBrush";
    public const string Danger = "DangerBrush";
    public const string WarnBadgeBackground = "WarnBadgeBackgroundBrush";

    // ── 控件 ────────────────────────────────────────────────────
    public const string ControlBackground = "ControlBackgroundBrush";
    public const string ControlHover = "ControlHoverBrush";
    public const string DisabledButton = "DisabledButtonBrush";
    public const string PrimaryButtonText = "PrimaryButtonTextBrush";
    public const string InputBorder = "InputBorderBrush";
    public const string ProgressTrack = "ProgressTrackBrush";
    public const string ScrollTrack = "ScrollTrackBrush";
    public const string ScrollThumb = "ScrollThumbBrush";

    /// <summary>全部键，**有序**（顺序只为让诊断输出稳定、便于人读，不影响行为）。</summary>
    public static IReadOnlyList<string> Keys { get; } = new[]
    {
        AppBackground,
        CardBackground,
        CardBorder,
        Divider,
        InnerPanelBackground,
        TextPrimary,
        TextSecondary,
        TextTertiary,
        LogInfo,
        ExpanderHeader,
        Accent,
        AccentHover,
        AccentSoft,
        Success,
        Warning,
        Danger,
        WarnBadgeBackground,
        ControlBackground,
        ControlHover,
        DisabledButton,
        PrimaryButtonText,
        InputBorder,
        ProgressTrack,
        ScrollTrack,
        ScrollThumb,
    };

    /// <summary>浅色（保持暗夜模式出现之前的原值，逐字不变 —— 升级不该无声改配色）。</summary>
    public static IReadOnlyDictionary<string, string> Light { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AppBackground] = "#FFF4F6F9",
        [CardBackground] = "#FFFFFFFF",
        [CardBorder] = "#FFE4E7ED",
        [Divider] = "#FFEEF0F4",
        [InnerPanelBackground] = "#FFFAFBFC",
        [TextPrimary] = "#FF1D2129",
        [TextSecondary] = "#FF6B7280",
        [TextTertiary] = "#FF9AA1AC",
        [LogInfo] = "#FF4E5969",
        [ExpanderHeader] = "#FF6B7280",
        [Accent] = "#FF2B6CF6",
        [AccentHover] = "#FF1D5BE0",
        [AccentSoft] = "#FFEAF1FE",
        [Success] = "#FF0E8A5F",
        [Warning] = "#FFB8720A",
        [Danger] = "#FFC0392B",
        [WarnBadgeBackground] = "#FFFDF3E3",
        [ControlBackground] = "#FFFFFFFF",
        [ControlHover] = "#FFF7F9FC",
        [DisabledButton] = "#FFC7CDD8",
        [PrimaryButtonText] = "#FFFFFFFF",
        [InputBorder] = "#FFD6DAE2",
        [ProgressTrack] = "#FFEDF0F5",
        [ScrollTrack] = "#FFF0F2F7",
        [ScrollThumb] = "#FFC3C9D4",
    };

    /// <summary>
    /// 暗夜（近纯黑）。
    ///
    /// 两个刻意的选择，都不是随手挑的：
    /// - **底色近纯黑但主文本不是纯白**（<c>#EDEEF1</c>）：纯黑底 + 纯白字在暗环境里对比过硬，
    ///   长时间盯会很累；降一档亮度反而更耐看。
    /// - **强调色/状态色整体提亮**（<c>#2B6CF6</c> → <c>#5B93FF</c>，绿/红同理）：
    ///   浅色底上够用的那几个色，挪到近黑底上会「发闷」到看不清，必须比浅色主题更亮。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Dark { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AppBackground] = "#FF0B0B0D",
        [CardBackground] = "#FF151619",
        [CardBorder] = "#FF26282E",
        [Divider] = "#FF1F2126",
        [InnerPanelBackground] = "#FF111214",
        [TextPrimary] = "#FFEDEEF1",
        [TextSecondary] = "#FFA0A6B0",
        [TextTertiary] = "#FF6E7480",
        [LogInfo] = "#FFB0B6C2",
        [ExpanderHeader] = "#FFEDEEF1",
        [Accent] = "#FF5B93FF",
        [AccentHover] = "#FF7BA8FF",
        [AccentSoft] = "#FF1A2740",
        [Success] = "#FF3DD68C",
        [Warning] = "#FFE0A94A",
        [Danger] = "#FFF87171",
        [WarnBadgeBackground] = "#FF2E2412",
        [ControlBackground] = "#FF151619",
        [ControlHover] = "#FF1E2025",
        [DisabledButton] = "#FF35383F",
        [PrimaryButtonText] = "#FFFFFFFF",
        [InputBorder] = "#FF3A3D45",
        [ProgressTrack] = "#FF26282E",
        [ScrollTrack] = "#FF15161A",
        [ScrollThumb] = "#FF3B3E46",
    };

    private static readonly Regex ColorPattern = new("^#[0-9A-Fa-f]{8}$", RegexOptions.Compiled);

    /// <summary>
    /// 取某套调色板。⚠️ 只接受 <see cref="ThemeMode.Light"/> / <see cref="ThemeMode.Dark"/> ——
    /// <see cref="ThemeMode.System"/> 是「偏好」而不是「实际渲染成什么」，必须先经
    /// <see cref="ThemeService.Resolve"/> 解析。传 System 进来直接抛：
    /// 让它当场炸掉，好过静默地永远给浅色（那种症状是「选了跟随系统却纹丝不动」，很难查）。
    /// </summary>
    public static IReadOnlyDictionary<string, string> For(ThemeMode effective) => effective switch
    {
        ThemeMode.Light => Light,
        ThemeMode.Dark => Dark,
        _ => throw new ArgumentOutOfRangeException(
            nameof(effective), effective, "System 必须先经 ThemeService.Resolve 解析成 Light/Dark 再取调色板。"),
    };

    /// <summary>色值格式是否是 <c>#AARRGGBB</c>（回归用它穷举两套表，防手写错）。</summary>
    public static bool IsWellFormedColor(string? hex) => hex is not null && ColorPattern.IsMatch(hex);
}
