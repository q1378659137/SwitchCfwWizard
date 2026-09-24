using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Services;

namespace SwitchCfwWizard.Converters;

/// <summary>bool → Visibility；ConverterParameter="Invert" 时反转。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>bool 取反。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>非空字符串 → Visible。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = !string.IsNullOrWhiteSpace(value as string);
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 日志级别 → 前景色。
///
/// ⚠️ 颜色**从主题资源里取**，不再是自己 <c>static readonly</c> 造几个 <c>Freeze()</c> 过的 Brush ——
/// 冻结的 Brush 改不了色，暗夜模式下日志会继续用浅色主题的深灰/深绿，在近黑底上几乎看不见。
/// 取到的是资源字典里那份**共享实例**，所以主题一换，颜色跟着变。
/// </summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    // 资源取不到时的回落（没有 Application 的场景）：只是不让它抛，不走主题。
    private static readonly SolidColorBrush FallbackInfo = Create("#4E5969");
    private static readonly SolidColorBrush FallbackSuccess = Create("#0E8A5F");
    private static readonly SolidColorBrush FallbackWarning = Create("#B8720A");
    private static readonly SolidColorBrush FallbackError = Create("#C0392B");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var (key, fallback) = value switch
        {
            LogLevel.Success => (ThemePalette.Success, FallbackSuccess),
            LogLevel.Warning => (ThemePalette.Warning, FallbackWarning),
            LogLevel.Error => (ThemePalette.Danger, FallbackError),
            _ => (ThemePalette.LogInfo, FallbackInfo),
        };

        return ThemeService.Resource(key) ?? fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Create(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>进度值 → 是否处于“已完成”状态（用于着色）。颜色同样来自主题资源，理由见上。</summary>
public sealed class ProgressToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush FallbackAccent = Create("#2B6CF6");
    private static readonly SolidColorBrush FallbackDone = Create("#0E8A5F");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = value is double d ? d : 0;

        return progress >= 99.999
            ? ThemeService.Resource(ThemePalette.Success) ?? FallbackDone
            : ThemeService.Resource(ThemePalette.Accent) ?? FallbackAccent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Create(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
