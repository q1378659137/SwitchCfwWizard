using System.Reflection;
using SwitchCfwWizard.Infrastructure;

namespace SwitchCfwWizard.Services;

/// <summary>
/// 把一份 <see cref="AppSettings"/> 摊平成「键 → 值」，并算出两次快照之间的差异。
///
/// 为什么需要它：用户要求「把所有操作写进日志文件，方便排查问题」，而**设置类**的操作
/// （勾组件、改配置项、改下载源、改引导项名……）本身不产生日志 —— 它们只是改了内存里的值。
/// 于是「用户到底改了什么」在日志里完全看不见，出了问题只能靠猜。
///
/// 做法是**不逐个 setter 手写日志**（那和手写保存一样，漏一处就静默少一条），
/// 而是拿两份快照做差：能改的东西必然出现在快照里，所以「有没有漏」不再取决于谁记得住。
/// 差异由自动落盘那一层触发 —— 它本来就在每次改动后结算一次，顺路把差异写进日志是免费的，
/// 而且天然**合并**了连续输入（一次节流窗口内的多次改动合成一行）。
///
/// 这里全是**纯函数**，没有 IO：可以对着固定输入断言输出，不必真去建一个 MainViewModel。
/// </summary>
public static class SettingsSnapshot
{
    /// <summary>
    /// 这些键的值**绝不写进日志**，只记「已设置 / 已清空」。
    ///
    /// <c>GitHubToken</c> 自不必说 —— 日志是要发给别人看的。
    /// <c>Proxy</c> 同样按机密处理：它常常长成 <c>http://用户名:密码@主机:端口</c>，
    /// 而「日志里带着一串能用的代理口令」这种事故，出事之后是收不回来的。
    ///
    /// 判据是**键名包含**而不是精确相等：将来多出 <c>GitHubTokenBackup</c> 之类也该被挡住，
    /// 而「漏挡一个」的后果远比「多挡一个」严重。
    /// </summary>
    private static readonly string[] SecretKeyMarkers = ["Token", "Proxy", "Password", "Secret"];

    /// <summary>把设置摊平成「键 → 值」。标量用属性名当键，字典用 <c>属性名.条目键</c>。</summary>
    public static IReadOnlyDictionary<string, string> Flatten(AppSettings settings)
    {
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in typeof(AppSettings)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            // 只处理「设置」本身的字段类型：标量 + 字符串→字符串的字典。
            // 别的类型（将来若加了集合/嵌套对象）会被**显式跳过**而不是硬转字符串 ——
            // 硬转会把一个对象打印成类型名，看着像日志、其实是噪音。
            if (IsScalar(property.PropertyType))
            {
                flat[property.Name] = FormatValue(property.GetValue(settings));
                continue;
            }

            if (property.GetValue(settings) is System.Collections.IDictionary map)
            {
                foreach (System.Collections.DictionaryEntry entry in map)
                {
                    flat[$"{property.Name}.{entry.Key}"] = FormatValue(entry.Value);
                }
            }
        }

        return flat;
    }

    /// <summary>
    /// 两次快照之间的差异（没有差异时返回空表）。
    ///
    /// 返回**结构化**的结果而不是拼好的字符串：键要拿去做「人话标签」（见调用方），
    /// 值要按机密与否分别处理 —— 拼成一个字符串再做二次解析，是把自己写死在一个格式上。
    ///
    /// 排序是**稳定**的（键名字典序）：同一组改动在日志里每次都是同一个顺序，
    /// 前后两次日志才好用肉眼或 diff 比。
    /// </summary>
    public static IReadOnlyList<SettingChange> Diff(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        var changes = new List<SettingChange>();

        foreach (var key in after.Keys.Concat(before.Keys).Distinct(StringComparer.Ordinal)
                     .OrderBy(k => k, StringComparer.Ordinal))
        {
            before.TryGetValue(key, out var oldValue);
            after.TryGetValue(key, out var newValue);

            // 两边都没有 = 不存在；一边缺 = 新增/删除；两边都在但相等 = 没变。
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                continue;
            }

            changes.Add(new SettingChange(
                key,
                oldValue ?? string.Empty,
                newValue ?? string.Empty,
                IsSecret(key)));
        }

        return changes;
    }

    /// <summary>这个键的值要不要脱敏（见 <see cref="SecretKeyMarkers"/>）。</summary>
    internal static bool IsSecret(string key)
    {
        foreach (var marker in SecretKeyMarkers)
        {
            if (key.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 机密值的展示形式：只说明「现在有没有」，不泄漏内容。给界面/日志直接显示用。
    /// </summary>
    public static string DescribeSecretValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "已清空" : "已设置（内容不记录）";

    /// <summary>一次设置改动。机密项的 <see cref="OldValue"/>/<see cref="NewValue"/> 也会是原文，
    /// 所以**显示前必须先看 <see cref="IsSecret"/>** —— 这一点由调用方的格式化函数兜住。</summary>
    public sealed record SettingChange(string Key, string OldValue, string NewValue, bool IsSecret);

    private static bool IsScalar(Type type) =>
        type == typeof(string) || type == typeof(bool) || type.IsEnum || type.IsPrimitive || type == typeof(decimal);

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "True" : "False",
        string text => text,
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
