namespace SwitchCfwWizard.Models;

/// <summary>配置项在界面上的编辑控件类型。</summary>
public enum OptionEditor
{
    /// <summary>勾选框，值域 "0" / "1"。</summary>
    CheckBox,

    /// <summary>下拉选择，取值来自 Choices。</summary>
    ComboBox,

    /// <summary>文本框。</summary>
    TextBox,

    /// <summary>数字输入框。</summary>
    Number,
}

/// <summary>下拉框中的一个选项。</summary>
/// <param name="Value">写入配置的实际取值。</param>
/// <param name="LabelKey">显示名称的多语言 key。</param>
/// <param name="DisplayText">
/// 直接显示的文本。不为空时优先于 <paramref name="LabelKey"/>——
/// 用于「组合键」这类本身就不是自然语言、不需要翻译的候选项。
/// </param>
public sealed record OptionChoice(string Value, string LabelKey, string? DisplayText = null);

/// <summary>
/// 一个配置项的定义（纯数据，与界面无关）。
/// 组件目录（ComponentCatalog）以声明式方式列出全部可配置项。
/// </summary>
public sealed class OptionDefinition
{
    /// <summary>在所属组件内唯一的键名，通常等于 ini 的键名。</summary>
    public required string Key { get; init; }

    /// <summary>
    /// 该选项最终写入的文件（相对 out 目录），例如 "exosphere.ini"、"bootloader/hekate_ipl.ini"。
    /// 为空表示该选项不写入配置文件（例如 Ultrahand 的安装范围选项）。
    /// </summary>
    public string? TargetFile { get; init; }

    /// <summary>所属 ini 段名（写入文件时使用）。</summary>
    public string? Section { get; init; }

    /// <summary>选项分组标题的多语言 key。</summary>
    public required string GroupKey { get; init; }

    /// <summary>显示名称的多语言 key。</summary>
    public required string LabelKey { get; init; }

    /// <summary>说明文字的多语言 key。</summary>
    public string? DescriptionKey { get; init; }

    public OptionEditor Editor { get; init; } = OptionEditor.CheckBox;

    /// <summary>默认值。勾选框使用 "0" / "1"。</summary>
    public string DefaultValue { get; init; } = "0";

    public IReadOnlyList<OptionChoice>? Choices { get; init; }

    /// <summary>
    /// 候选项在运行期动态生成（例如 hekate 的 <c>autoboot</c>，它的候选项取决于当前勾了哪几个引导项）。
    ///
    /// 这类选项的静态 <see cref="Choices"/> 只是**占位**，并不完整，所以
    /// <see cref="ViewModels.OptionViewModel"/> 构造时**不能**拿它做「值不在候选项里就回退到第一项」的自愈 ——
    /// 否则存档里的合法值（如 "3"）会因为占位列表里只有 "0" 而被当场改成 "0"，
    /// 而真正的候选项要等 <c>MainViewModel.RefreshAutobootChoices()</c> 才建出来，那时值已经被改掉了。
    /// 标记为 true 后，构造时原样保留值，交由重建候选项的那一步去校验与自愈。
    /// </summary>
    public bool DynamicChoices { get; init; }

    /// <summary>当同组件内另一个选项等于指定值时才显示（为空表示始终显示）。</summary>
    public string? VisibleWhenKey { get; init; }

    public string? VisibleWhenValue { get; init; }

    public double Minimum { get; init; }

    public double Maximum { get; init; } = 100;

    /// <summary>数字输入框的步进。</summary>
    public double Step { get; init; } = 1;

    /// <summary>写入 ini 时使用的类型前缀（Atmosphere 的 system_settings.ini 需要 u8!/u64!/str!）。</summary>
    public string? IniValuePrefix { get; init; }
}
