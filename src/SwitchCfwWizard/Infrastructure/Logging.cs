namespace SwitchCfwWizard.Infrastructure;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss");
}

/// <summary>日志接收器：界面订阅它来显示运行日志。</summary>
public interface ILogSink
{
    void Log(LogLevel level, string message);
}

public static class LogSinkExtensions
{
    public static void Info(this ILogSink sink, string message) => sink.Log(LogLevel.Info, message);

    public static void Success(this ILogSink sink, string message) => sink.Log(LogLevel.Success, message);

    public static void Warn(this ILogSink sink, string message) => sink.Log(LogLevel.Warning, message);

    public static void Error(this ILogSink sink, string message) => sink.Log(LogLevel.Error, message);
}
