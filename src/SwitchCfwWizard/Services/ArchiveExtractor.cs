using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.Services;

public sealed record ExtractProgress(int Current, int Total, string EntryName);

/// <summary>
/// 压缩包解压（带进度、防目录穿越、覆盖已有文件）。
///
/// 支持两种格式，**按扩展名分派**：
/// <list type="bullet">
///   <item><c>.zip</c> —— 走 .NET 8 自带的 <see cref="ZipFile"/>。</item>
///   <item><c>.7z</c> —— 走 SharpCompress。自带的 System.IO.Compression 只认 zip，
///   而 usb-botbase 只发 7z（见 SwitchCfwWizard.csproj 里那段注释）。</item>
/// </list>
///
/// 为什么 zip 不也统一换成 SharpCompress：那会让「唯一一个第三方依赖」的代价平白放大 ——
/// 一个包能解 7z 就够了，zip 用自带库既快又少一层出错面。
///
/// ⚠️ 新增格式时**必须同时**改 <see cref="IsArchive"/>（它决定下载物走解压还是走散装复制），
/// 只加一个分支会出现「下载了、也识别成压缩包、但没人解压」的静默缺口。
/// </summary>
public static class ArchiveExtractor
{
    /// <summary>本类能解开的扩展名。判据只此一份，<see cref="IsArchive"/> 与解压分派共用。</summary>
    private static readonly string[] ArchiveExtensions = [".zip", ".7z"];

    /// <summary>这个文件是不是「需要解压的压缩包」（而不是要原样复制的散装文件）。</summary>
    public static bool IsArchive(string path) =>
        ArchiveExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 解压 <paramref name="archivePath"/> 到 <paramref name="destinationDirectory"/>。
    /// 返回**实际写出的文件数**（不含目录项）——压缩包里通常还有一批目录条目，
    /// 拿条目总数当「文件数」报给用户会偏大。
    /// </summary>
    /// <param name="plan">
    /// 包内路径怎么处理（剥前缀 / 白名单）。传 <c>null</c> 等同 <see cref="ExtractPlan.All"/>，原样铺开。
    /// </param>
    /// <exception cref="IOException">
    /// 声明了筛选、却**一个文件都没留下**。这是有意的硬失败：上游改了包结构（或声明写错）时，
    /// 静默留一个空文件夹是**看不见**的，而报错看得见。
    /// </exception>
    public static int Extract(
        string archivePath,
        string destinationDirectory,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        ExtractPlan? plan = null)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        var effective = plan ?? ExtractPlan.All;

        var fileCount = archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
            ? ExtractSevenZip(archivePath, destinationRoot, progress, cancellationToken, effective)
            : ExtractZip(archivePath, destinationRoot, progress, cancellationToken, effective);

        if (fileCount == 0 && !effective.KeepsEverything)
        {
            throw new IOException(
                $"按声明的解压规则处理 {Path.GetFileName(archivePath)} 后一个文件都没留下 —— " +
                "多半是上游改了包内结构，或者解压规则写错了（见 ExtractPlan）。");
        }

        return fileCount;
    }

    private static int ExtractZip(
        string zipPath,
        string destinationRoot,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        ExtractPlan plan)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries;
        var total = entries.Count;
        var index = 0;
        var fileCount = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            if (string.IsNullOrEmpty(entry.Name))
            {
                // 目录项。有筛选时不建空目录：需要的父目录由 EnsureParent 按需创建，
                // 否则剥掉 out/ 之后会留下一堆指向不存在内容的空文件夹。
                if (plan.KeepsEverything)
                {
                    Directory.CreateDirectory(ResolveSafePath(destinationRoot, entry.FullName));
                }

                continue;
            }

            if (plan.Map(entry.FullName) is not { } relative)
            {
                continue;
            }

            var targetPath = ResolveSafePath(destinationRoot, relative);
            EnsureParent(targetPath);
            entry.ExtractToFile(targetPath, overwrite: true);
            fileCount++;
            progress?.Report(new ExtractProgress(index, total, entry.FullName));
        }

        return fileCount;
    }

    private static int ExtractSevenZip(
        string archivePath,
        string destinationRoot,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        ExtractPlan plan)
    {
        // ⚠️ 0.50 起入口叫 OpenArchive 而不是 Open（旧版的 SevenZipArchive.Open / ArchiveFactory.Open
        //    在这个版本里都不存在），升级 SharpCompress 时先看这里。
        using var archive = SevenZipArchive.OpenArchive(archivePath, new ReaderOptions());

        // 先物化成列表：条目总数要用来报进度，而 7z 的 Entries 是流式的，
        // 边遍历边数会让「第几个 / 共几个」的分母一直是错的。
        // 目录项不算文件（与 zip 那边的口径一致）。
        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
        var total = entries.Count;
        var index = 0;
        var fileCount = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            var key = entry.Key ?? string.Empty;
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (plan.Map(key) is not { } relative)
            {
                continue;
            }

            var targetPath = ResolveSafePath(destinationRoot, relative);
            EnsureParent(targetPath);

            // ExtractFullPath=false + 传完整目标路径：路径已经由 ResolveSafePath 校验过了，
            // 再让 SharpCompress 自己拼一次等于把校验绕过去。
            entry.WriteToFile(targetPath, new ExtractionOptions { Overwrite = true, ExtractFullPath = false });

            fileCount++;
            progress?.Report(new ExtractProgress(index, total, key));
        }

        return fileCount;
    }

    /// <summary>
    /// 把目录内容递归合并到目标目录（同名覆盖）。
    /// 返回**相对目标目录的路径**列表，调用方据此把合并结果记进清单。
    /// </summary>
    public static IReadOnlyList<string> MergeDirectory(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var merged = new List<string>();
        if (!Directory.Exists(sourceDirectory))
        {
            return merged;
        }

        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceDirectory, file);
            var target = Path.Combine(destinationDirectory, relative);

            EnsureParent(target);
            File.Copy(file, target, overwrite: true);
            merged.Add(relative);
        }

        return merged;
    }

    /// <summary>
    /// 从 GitHub 的 **tar.gz 整包**里，只取出 <paramref name="directory"/> 下的**直接子文件**，
    /// 落成**裸文件名**写进 <paramref name="destinationDirectory"/>。
    ///
    /// 用 .NET 内置的 <c>System.Formats.Tar</c>，**不引入新依赖** —— 而且回归测试能用**同一个库**
    /// 造出真实的 tar.gz fixture（不必手搓二进制，也不必往仓库里塞二进制样本）。
    ///
    /// 筛选规则本身在 <see cref="ComponentCatalog.SelectDirectoryEntries"/>（纯函数、可穷举）；
    /// 这里只负责「按那份映射把内容写出去」。所以先读一遍条目名算映射、再读一遍取内容 ——
    /// 两遍都在本地文件上，代价可以忽略，换来的是规则**只有一处**。
    /// </summary>
    /// <returns>写出的文件（裸文件名 + 字节数），按文件名排序。</returns>
    /// <exception cref="IOException">
    /// 目录下一个文件都没取到 —— 与 <see cref="Extract"/> 同一条规矩：**筛空必须报错**，
    /// 静默留下一个空目录在日志里看不见。
    /// </exception>
    public static IReadOnlyList<(string FileName, long Size)> ExtractDirectoryFromTarGz(
        string archivePath,
        string directory,
        string destinationDirectory)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        var selected = ComponentCatalog.SelectDirectoryEntries(ReadTarGzEntryNames(archivePath), directory);
        if (selected.Count == 0)
        {
            throw new IOException(
                $"整包里没有 {directory}/ 下的文件 —— 多半是上游把目录改名了，" +
                "或者「文件名 / 目录」那一栏填错了（可以在「高级设置」里改）。");
        }

        var wanted = selected.ToDictionary(x => x.Entry, x => x.FileName, StringComparer.Ordinal);
        var written = new List<(string FileName, long Size)>();

        using (var file = File.OpenRead(archivePath))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is TarEntryType.Directory || entry.DataStream is null)
                {
                    continue;
                }

                if (!wanted.TryGetValue(entry.Name, out var bareName))
                {
                    continue;
                }

                var target = ResolveSafePath(destinationRoot, bareName);
                EnsureParent(target);

                using (var output = File.Create(target))
                {
                    entry.DataStream.CopyTo(output);
                }

                written.Add((bareName, new FileInfo(target).Length));
            }
        }

        if (written.Count == 0)
        {
            throw new IOException(
                $"整包条目名与 {directory}/ 对上了，但一个文件都没写出来（{Path.GetFileName(archivePath)}）—— " +
                "上游的打包方式可能变了。");
        }

        written.Sort((a, b) => string.CompareOrdinal(a.FileName, b.FileName));
        return written;
    }

    /// <summary>只读一遍条目名（目录项不算），交给纯规则函数去挑。</summary>
    private static IReadOnlyList<string> ReadTarGzEntryNames(string archivePath)
    {
        var names = new List<string>();

        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not TarEntryType.Directory)
            {
                names.Add(entry.Name);
            }
        }

        return names;
    }

    private static void EnsureParent(string targetPath)
    {
        var parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static string ResolveSafePath(string root, string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"压缩包内存在非法路径：{relativePath}");
        }

        return combined;
    }
}
