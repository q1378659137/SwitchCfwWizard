using System.IO;
using System.Text;

namespace SwitchCfwWizard.Infrastructure;

/// <summary>
/// 把日志写进 <c>&lt;运行目录&gt;/logs/</c> 的当天文件 —— 用户遇到问题时把那个文件发过来就能排查。
///
/// 为什么需要它：界面上的运行日志一关窗就没了，而本工具最要命的一类故障恰恰是
/// 「这次跑出来的东西不对，但当时日志里写了什么已经无从查起」。文件日志是唯一能留下来的证据。
///
/// 几个刻意的取舍：
/// <list type="bullet">
///   <item>**一条一条追加、不长期占着句柄**：用 <see cref="File.AppendAllText(string, string, Encoding)"/>
///     而不是常开的 <c>StreamWriter</c>。日志条目本来就是「人读得过来」的量级（下载/解压/生成各若干条），
///     为此省下的那点开销换不来「文件被自己锁住」的风险 —— 而发布流水线恰恰要在程序退出后清理这个目录。</item>
///   <item>**写不进去就彻底停**：磁盘满 / 目录只读 / 被杀软拦住时，只在第一次记下原因并停用文件日志，
///     接着把原因回给界面日志。不做「每条日志都试一次」——那会把日志系统本身变成卡顿源。</item>
///   <item>**按天分文件 + 只留最近若干份**：不然长期使用会把磁盘吃满，而日志本身几乎只有最近几次有用。</item>
/// </list>
/// </summary>
public static class LogFile
{
    /// <summary>保留最近多少个日志文件（按文件名倒序，即按日期倒序）。</summary>
    private const int RetentionCount = 10;

    /// <summary>日志文件名前缀。清理旧文件时**只认这个前缀** —— 别把用户自己放进来的东西删了。</summary>
    private const string FileNamePrefix = "SwitchCfwWizard-";

    /// <summary>单个值在日志里的最大长度。超出截断并附省略号（Token / 超长路径 / 手输地址都可能很长）。</summary>
    internal const int MaxValueLength = 60;

    private static readonly object Gate = new();

    /// <summary>UTF-8 且**不带 BOM**：带 BOM 的话用户用记事本打开会看到一个乱码方块，也会让 grep 认不出首行。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static bool _disabled;
    private static bool _retentionDone;

    /// <summary>文件日志被停用的原因（正常时为 <c>null</c>）。</summary>
    public static string? DisabledReason { get; private set; }

    /// <summary>
    /// 当天日志文件的路径。**即使写不进去也照样算得出来** —— 界面上要把这个路径显示给用户，
    /// 否则「日志写到哪儿了」只能靠猜。
    /// </summary>
    public static string CurrentFilePath =>
        Path.Combine(AppPaths.LogRoot, $"{FileNamePrefix}{DateTimeOffset.Now:yyyy-MM-dd}.log");

    /// <summary>写一条带级别与时戳的日志（与界面日志同一份内容，只多了毫秒级时间戳）。</summary>
    public static void Write(LogLevel level, string message)
    {
        WriteRaw($"{DateTimeOffset.Now:HH:mm:ss.fff} [{LevelText(level)}] {message}");
    }

    /// <summary>
    /// 原样写一行（不加时戳与级别）。给会话头、分隔线这类「装饰」用 ——
    /// 它们只该出现在文件里，不该混进界面那份日志。
    /// </summary>
    public static void WriteRaw(string line)
    {
        lock (Gate)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                AppPaths.EnsureDirectory(AppPaths.LogRoot);
                File.AppendAllText(CurrentFilePath, line + Environment.NewLine, Utf8NoBom);

                // 清理旧文件只做一次：每次写都枚举一遍目录纯属浪费，而「同一天里多留几份」
                // 最多让磁盘多占几百 KB，无所谓。
                if (!_retentionDone)
                {
                    _retentionDone = true;
                    PruneOldFiles();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                           or System.Security.SecurityException or ArgumentException)
            {
                // 停用而不是每条都重试：见类注释。
                _disabled = true;
                DisabledReason = ex.Message;

                // 用 WriteRaw 自己再写一次会被上面的 _disabled 挡住，所以这里只能返回，
                // 由调用方（MainViewModel）看到 DisabledReason 后告诉用户。
            }
        }
    }

    /// <summary>
    /// 删掉超出 <see cref="RetentionCount"/> 的旧日志。**只按文件名前缀认自己的文件** ——
    /// 用户可能手动往这个目录里丢东西，误删比留旧日志糟得多。
    ///
    /// 全程 best-effort：删不掉（被占用 / 权限）就留着，不让日志把程序搞崩。
    ///
    /// <c>internal</c> 是**测试接缝**：正常路径下它整个进程只跑一次（见 <c>_retentionDone</c>），
    /// 于是「保留策略真的生效」这件事在集成测试里根本触发不了；不带接缝就只能靠读代码相信它。
    /// </summary>
    internal static void PruneOldFiles()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogRoot))
            {
                return;
            }

            var stale = Directory
                .EnumerateFiles(AppPaths.LogRoot, FileNamePrefix + "*.log")
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Skip(RetentionCount)
                .ToList();

            foreach (var path in stale)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 删不掉就留着，不影响写入
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 枚举目录失败同理：不值得为此停用日志
        }
    }

    /// <summary>把一段多行文本按行写进文件（会话头用）。</summary>
    public static void WriteBlock(params string[] lines)
    {
        foreach (var line in lines)
        {
            WriteRaw(line);
        }
    }

    /// <summary>
    /// 值的展示形式：过长就截断。
    ///
    /// ⚠️ 这里只负责「短」，**不负责脱敏** —— 谁都知道要脱敏的是哪几个键，
    /// 那个判断在 <see cref="Services.SettingsSnapshot"/> 里（见那里的注释），两件事分开才不至于
    /// 「哪天顺手改了截断长度，把脱敏一起改没了」。
    /// </summary>
    public static string Shorten(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(空)";
        }

        var flat = value.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= MaxValueLength ? flat : flat[..MaxValueLength] + "…";
    }

    internal static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Info => "信息",
        LogLevel.Success => "成功",
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        _ => level.ToString(),
    };
}
