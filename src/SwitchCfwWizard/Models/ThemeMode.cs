namespace SwitchCfwWizard.Models;

/// <summary>界面主题档位 —— 存档里 <c>Theme</c> 字段的取值域。</summary>
public enum ThemeMode
{
    /// <summary>浅色。固定，不理会系统设置。</summary>
    Light,

    /// <summary>暗夜。固定，不理会系统设置。</summary>
    Dark,

    /// <summary>跟随系统：读 Windows 的「应用模式」，并在系统切换时实时跟随。</summary>
    System,
}

/// <summary>
/// <see cref="ThemeMode"/> ↔ 存档字符串 的编解码。
///
/// 为什么单独一层、而不是直接 <c>Enum.Parse</c>：存档是**用户看得见、也可能手改**的文本，
/// 而枚举名是编译期的东西 —— 直接绑上去等于把「json 里写了个错词」变成**启动崩溃**。
/// 本项目对坏输入的一贯姿势是「当没填、回落默认」（同 <see cref="Services.GitHubMirror.Normalize"/>），这里照做。
/// </summary>
public static class ThemeModeCodec
{
    /// <summary>
    /// 老存档里没有这个字段时用的档位：**跟随系统**。
    ///
    /// 挑它而不是「浅色」的理由：用户系统本来就是深色的话，跟随它等于**尊重他已经表达过的偏好**，
    /// 而不是我们替他决定。⚠️ 反过来的代价也要说清 —— 系统是深色的老用户升级上来，
    /// 界面会第一次变暗。这是**有意**的行为（不是 bug），README 里写明了怎么改回固定浅色。
    /// </summary>
    public const ThemeMode Default = ThemeMode.System;

    /// <summary>下拉框里的顺序：跟随系统放第一个（它也是默认）。</summary>
    public static IReadOnlyList<ThemeMode> All { get; } =
        new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark };

    /// <summary>存档 → 档位。null / 空 / 认不出的词一律回落 <see cref="Default"/>，**不抛**。</summary>
    public static ThemeMode Parse(string? stored) => stored?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        "system" => ThemeMode.System,
        _ => Default,
    };

    /// <summary>
    /// 档位 → 存档。刻意写小写、字符串字面量**不依赖枚举名**：
    /// 以后枚举改名时，用户手里的存档不该跟着变（那会让「跟进系统」变成「回落默认」）。
    /// </summary>
    public static string ToStorage(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "light",
        ThemeMode.Dark => "dark",
        _ => "system",
    };

    /// <summary>语言包里「档位显示名」的键。三份包都必须有（回归里显式断言）。</summary>
    public static string NameKey(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "App.Theme.Light",
        ThemeMode.Dark => "App.Theme.Dark",
        _ => "App.Theme.System",
    };
}
