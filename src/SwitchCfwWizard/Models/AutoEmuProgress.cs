namespace SwitchCfwWizard.Models;

/// <summary>组件包准备到哪一步了。**用枚举而不是本地化字符串**表示阶段 —— 界面要按阶段决定
/// 「这两条进度条哪一条在动」，拿显示文案去比较会在换语言或改文案时静默失效。</summary>
public enum AutoEmuStage
{
    /// <summary>正在查 Release 列表。</summary>
    Query,

    /// <summary>正在下载 <c>auto-emu.zip</c>。</summary>
    Download,

    /// <summary>正在解压并逐文件校验。</summary>
    Extract,
}

/// <summary>
/// 「重建 emuMMC」组件包的下载/解压进度。
///
/// 单独一个类型（而不是复用 <see cref="DownloadProgress"/>）是因为这里有两段完全不同的进度：
/// **下载**（有百分比、有速度）与**解压**（只有「第几个文件 / 共几个」）。
/// 界面上是两条独立的进度条，混用一个类型只会让「这条进度条现在表示什么」变得不清楚。
/// </summary>
/// <param name="Kind">阶段标识（见 <see cref="AutoEmuStage"/>），界面用它选进度条。</param>
/// <param name="Stage">阶段名（已本地化），直接显示在进度条上方。</param>
/// <param name="Percent">0～100；<c>null</c> = 这一阶段没有确定进度（转圈）。</param>
/// <param name="Detail">附加信息（大小、速度、当前文件）。</param>
public sealed record AutoEmuProgress(AutoEmuStage Kind, string Stage, double? Percent, string? Detail = null);
