using System.Collections.Generic;
using SwitchCfwWizard.Localization;

namespace SwitchCfwWizard.Services;

/// <summary>
/// 把「带占位符的提示文案」填好。
///
/// 抽成纯函数是为了能测：用户 2026-11-22 的要求是「冲突提示必须点名是哪个插件」，
/// 而这类文案最容易出的错是**静默**的 —— 某个语言包漏写 <c>{0}</c>，界面上就永远
/// 显示「与另一个已勾选的组件冲突」，翻译看着没问题，实际正是用户抱怨的那句话。
/// </summary>
public static class HintText
{
    /// <summary>
    /// <paramref name="key"/> 为空 → 空串；<paramref name="titles"/> 为空 → 原样返回文案
    /// （这条很重要：不填参数时不走 <see cref="string.Format(string, object?)"/>，
    /// 否则文案里若出现真正的花括号会直接抛 <see cref="FormatException"/>）。
    /// </summary>
    public static string Compose(string? key, IReadOnlyList<string> titles)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var template = LocalizationService.Instance[key];
        if (titles.Count == 0)
        {
            return template;
        }

        var separator = LocalizationService.Instance["Common.ListSeparator"];
        return string.Format(template, string.Join(separator, titles));
    }
}
