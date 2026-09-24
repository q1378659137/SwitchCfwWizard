using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.Services;

/// <summary>
/// 主题的应用层：把「档位」变成资源字典里**真实的颜色**。
///
/// ## 为什么是「换资源实例 + DynamicResource」
///
/// 最初的设想是「原地改那批共享 Brush 的 Color」—— 那样 <c>MainWindow.xaml</c> 里上百处
/// <c>{StaticResource}</c> 一个都不用动。**实测推翻了它**：Application 级资源字典里的 Freezable
/// 会被 WPF 自动冻结（<c>--selftest</c> 体检报出 22 个 Brush 全部 <c>IsFrozen=true</c>），
/// 改它的 <c>Color</c> 当场抛 <c>InvalidOperationException</c>。
///
/// 而 StaticResource 是**解析时拷贝引用**的：就算把字典里的对象换掉，已经建好的界面
/// 也还指着旧的那个 —— 症状是「切了主题，界面纹丝不动」，且不报错。
///
/// 所以走 WPF 的正路（两条腿都得有，缺一条就不生效）：
/// 1. 资源侧：<see cref="Apply"/> **换一个未冻结的新实例**放进字典（<c>host[key] = new SolidColorBrush(...)</c>）；
/// 2. 界面侧：颜色引用必须是 <c>{DynamicResource}</c>（<c>Themes/Styles.xaml</c> 与
///    <c>MainWindow.xaml</c> 里已全部改过来），它会订阅资源变更，于是自动重绘。
///
/// ⚠️ 这条不变式（**颜色键一律不得写回 StaticResource**）由回归里的源码契约钉住。
/// 谁哪天「顺手」把某个 <c>{DynamicResource}</c> 改回 <c>{StaticResource}</c>，
/// 那一处颜色就会在切主题时静止不动 —— 不报错、不崩溃，只是「看起来漏了一块」。
///
/// ## 测试接缝
///
/// 回归是**控制台程序**（<c>Application.Current</c> 为 null），所以资源宿主与「系统是否偏好深色」
/// 都是可注入的（<see cref="Host"/> / <see cref="SystemPrefersDark"/>），不然这两段逻辑
/// 在离线回归里根本进不去 —— 而它们恰好是最容易写错的两段。
/// </summary>
public static class ThemeService
{
    /// <summary>测试接缝：资源宿主。null ⇒ 用 <see cref="Application.Current"/> 的资源（真实运行就是这条）。</summary>
    public static ResourceDictionary? Host { get; set; }

    /// <summary>测试接缝：系统是否偏好深色。默认读注册表（见 <see cref="ReadSystemPrefersDark"/>）。</summary>
    public static Func<bool> SystemPrefersDark { get; set; } = ReadSystemPrefersDark;

    /// <summary>用户选的档位（可能是 <see cref="ThemeMode.System"/>）。</summary>
    public static ThemeMode Preference { get; private set; } = ThemeModeCodec.Default;

    /// <summary>实际渲染用的主题：**永远是 Light 或 Dark**（System 已经被解析掉了）。</summary>
    public static ThemeMode Applied { get; private set; } = ThemeMode.Light;

    private static readonly List<string> _missingKeys = new();
    private static int _resolvedKeys;

    private static bool _watching;

    /// <summary>
    /// 上一次 <see cref="Apply"/> 里「在资源字典里找不到」的键。**正常应恒为空** ——
    /// 非空意味着键名拼错、或 <c>Styles.xaml</c> 漏定义了某个键，而症状只是「那一处颜色不变」。
    /// </summary>
    public static IReadOnlyList<string> MissingKeysOnLastApply => _missingKeys;

    /// <summary>上一次 <see cref="Apply"/> 里真正处理好（找到并核对过颜色）的键数。正常应等于键总数。</summary>
    public static int ResolvedKeysOnLastApply => _resolvedKeys;

    /// <summary>
    /// 应用主题。幂等：同一个档位反复调，第二次起不改任何东西（颜色已经对了就不会再换实例）。
    ///
    /// ⚠️ 只在 UI 线程调（碰资源字典属于 UI 操作）。跟随系统那条路是唯一例外，
    /// 它在 <see cref="OnUserPreferenceChanged"/> 里被 marshal 回 UI 线程后才调进来。
    /// </summary>
    public static void Apply(ThemeMode preference)
    {
        Preference = preference;
        Applied = Resolve(preference);

        var palette = ThemePalette.For(Applied);
        var host = Host ?? Application.Current?.Resources;

        _missingKeys.Clear();
        _resolvedKeys = 0;

        if (host is null)
        {
            // 没界面（控制台回归没注入 Host、或极早期）。档位本身有效，只是这一刻还没有东西可上色。
            return;
        }

        foreach (var key in ThemePalette.Keys)
        {
            if (host[key] is not SolidColorBrush existing)
            {
                _missingKeys.Add(key);
                continue;
            }

            var color = (Color)ColorConverter.ConvertFromString(palette[key])!;

            // 已经是对的颜色就不动它：`Apply` 会被反复调用（每次自动落盘结算、
            // 每次系统主题变化），不判一下会平白造一堆 Brush 出来。
            if (existing.Color != color)
            {
                host[key] = new SolidColorBrush(color);
            }

            _resolvedKeys++;
        }
    }

    /// <summary>
    /// 把「用户的档位偏好」解析成「实际渲染的主题」（只会返回 Light / Dark）。
    ///
    /// 是 <c>public</c> 而不是私有：回归要能单独验这条分派（尤其 System 那一支），
    /// 不必真的去改一遍界面颜色。
    /// </summary>
    public static ThemeMode Resolve(ThemeMode preference) => preference switch
    {
        ThemeMode.Dark => ThemeMode.Dark,
        ThemeMode.Light => ThemeMode.Light,
        _ => SystemPrefersDark() ? ThemeMode.Dark : ThemeMode.Light,
    };

    /// <summary>按资源键取当前主题下的 Brush（转换器用）。取不到返回 null，由调用方决定回落什么。</summary>
    public static Brush? Resource(string key) => (Host ?? Application.Current?.Resources)?[key] as Brush;

    /// <summary>
    /// 读 Windows 的「应用模式」：<c>AppsUseLightTheme = 0</c> 表示深色。
    ///
    /// ⚠️ 刻意**不抛**任何异常：这是「好看一点」的功能，读不到就按浅色走（最保守的默认）。
    /// 让它把启动搞崩，得不偿失 —— 注册表项在某些精简系统 / 受策略限制的机器上会缺。
    /// </summary>
    public static bool ReadSystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 开始监听系统主题变化（只在正常带界面的启动路径上调，见 <c>App.StartUp</c>）。
    ///
    /// ⚠️ 无人值守路径（<c>--selftest</c> / <c>--run</c>）**刻意不订阅**：那两条路径下
    /// 监听没有任何用处，却会让进程挂着一个静态事件订阅 —— 回归里跑完不退出就是这个味道。
    /// </summary>
    public static void StartWatching()
    {
        if (_watching)
        {
            return;
        }

        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _watching = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            // 没有消息泵时订阅会失败。这一档退化成「只在启动时读一次」——而不是让程序起不来。
            _watching = false;
            LogFile.WriteRaw(
                $"{DateTimeOffset.Now:HH:mm:ss.fff} [警告] 主题：跟随系统的实时监听没能启动（{ex.GetType().Name}），"
                + "系统主题变化不会自动跟随；下次启动仍会重新读取。");
        }
    }

    /// <summary>停止监听（<c>App.OnExit</c> 调）。静态事件订阅不取消，进程退出后仍有人引用我们的类型。</summary>
    public static void StopWatching()
    {
        if (!_watching)
        {
            return;
        }

        try
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            // 退出路径上失败无所谓，不掩盖原始退出流程
        }

        _watching = false;
    }

    /// <summary>监听是否真在跑（回归断言用：无人值守路径必须没订阅）。</summary>
    public static bool IsWatching => _watching;

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (Preference != ThemeMode.System)
        {
            return;
        }

        // ⚠️ 这个事件在**非 UI 线程**上触发，而资源字典属于 UI 线程 ——
        //    直接改会抛「调用线程无法访问此对象」。必须 marshal 回去。
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply(Preference);
            return;
        }

        try
        {
            dispatcher.Invoke(() => Apply(Preference));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
        {
            // 应用正在关闭：放过这一次（不上色也不影响退出）
        }
    }
}
