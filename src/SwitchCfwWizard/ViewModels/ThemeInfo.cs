using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;
using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.ViewModels;

/// <summary>
/// 主题下拉框里的一项（与 <see cref="LanguageInfo"/> 同构，但多一层本地化）。
///
/// <see cref="DisplayName"/> 是**派生**的（每次读语言包），而不是构造时抓一次字符串 ——
/// 后者会让「切到英文之后下拉框里还写着『暗夜』」。派生属性的变更由
/// <see cref="RefreshLocalizedText"/> 显式广播（ComboBox 的 DisplayMemberPath 会订阅它）。
///
/// 之所以不采用「切语言时重建整个 Themes 集合」：重建会让 ComboBox 的 SelectedItem
/// 短暂变成 null 又回来，而 SelectedItem 的 setter 会去动真实主题 —— 为了刷新一个显示名
/// 去重放一次主题应用，代价与风险都不划算。
/// </summary>
public sealed class ThemeInfo : ObservableObject
{
    public ThemeInfo(ThemeMode mode)
    {
        Mode = mode;
        NameKey = ThemeModeCodec.NameKey(mode);
    }

    public ThemeMode Mode { get; }

    /// <summary>语言包里这一档的显示名键（三份包都必须有，回归里显式断言）。</summary>
    public string NameKey { get; }

    /// <summary>写进 settings.json 的那个词：<c>system</c> / <c>light</c> / <c>dark</c>。</summary>
    public string Code => ThemeModeCodec.ToStorage(Mode);

    public string DisplayName => LocalizationService.Instance[NameKey];

    internal void RefreshLocalizedText() => OnPropertyChanged(nameof(DisplayName));
}
