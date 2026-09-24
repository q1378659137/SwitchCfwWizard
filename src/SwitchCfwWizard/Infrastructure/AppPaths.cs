using System.IO;

namespace SwitchCfwWizard.Infrastructure;

/// <summary>
/// 统一的路径规划。所有路径都基于“软件运行目录”（exe 所在目录）。
/// </summary>
public static class AppPaths
{
    /// <summary>软件运行目录。</summary>
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>下载根目录：&lt;运行目录&gt;/download</summary>
    public static string DownloadRoot { get; } = Path.Combine(BaseDirectory, "download");

    /// <summary>输出根目录：&lt;运行目录&gt;/out</summary>
    public static string OutputRoot { get; } = Path.Combine(BaseDirectory, "out");

    /// <summary>外部语言包目录：&lt;运行目录&gt;/lang（可选，用于覆盖或扩展内置语言）</summary>
    public static string LanguageRoot { get; } = Path.Combine(BaseDirectory, "lang");

    /// <summary>日志目录：&lt;运行目录&gt;/logs</summary>
    public static string LogRoot { get; } = Path.Combine(BaseDirectory, "logs");

    /// <summary>
    /// boot.dat 的下载目录：&lt;运行目录&gt;/download/boot
    ///
    /// 单独给它一个子目录（而不是直接丢在 download 根下），与各组件的
    /// <c>download/&lt;组件&gt;/</c> 保持同一种形状 —— 一眼能看出这个文件是谁下的。
    /// 注意它**只是下载缓存**：真正要用的那份会被复制到 <c>out/</c> 根目录（见 ConfigGenerator）。
    /// </summary>
    public static string BootDatRoot { get; } = Path.Combine(DownloadRoot, "boot");

    public static string ComponentDownloadRoot(string folderName)
        => Path.Combine(DownloadRoot, folderName);

    /// <summary>解压目录：&lt;运行目录&gt;/download/&lt;组件&gt;/unpacked</summary>
    public static string ComponentUnpackedRoot(string folderName)
        => Path.Combine(ComponentDownloadRoot(folderName), "unpacked");

    /// <summary>
    /// payload 暂存目录：&lt;运行目录&gt;/download/&lt;组件&gt;/payload
    ///
    /// 与 <see cref="ComponentUnpackedRoot"/> 分开，是因为两者受不同开关控制：
    /// 组件本体跟「把组件文件合并进 out」走，payload 跟「把 payload 放到对应目录」走。
    /// 放同一个目录里就没法在合并阶段区分了。
    /// </summary>
    public static string ComponentPayloadRoot(string folderName)
        => Path.Combine(ComponentDownloadRoot(folderName), "payload");

    public static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }
}
