using System.IO;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.Services;

/// <summary>一个待暂存的下载物：磁盘上的来源文件 + 它的落点声明。</summary>
public sealed record StagingItem(string SourcePath, AssetPick Pick);

/// <summary>暂存结果里的一条记录。</summary>
/// <param name="Pick">来源下载物。</param>
/// <param name="RelativePath">
/// 相对暂存根目录的落点（统一用 <c>/</c> 分隔）。
/// 压缩包记的是**目标目录**（内容已按包内结构铺开），散装文件记的是**目标文件**。
/// </param>
/// <param name="FileCount">写出的文件数。压缩包是真实解压文件数，散装文件恒为 1。</param>
/// <param name="IsPayload">是否进了 payload 暂存区（而不是组件暂存区）。</param>
public sealed record StagedAsset(AssetPick Pick, string RelativePath, int FileCount, bool IsPayload);

/// <summary>暂存进度：正在处理哪个下载物、解压到第几个条目。</summary>
public sealed record StagingProgress(AssetPick Pick, int Current, int Total);

/// <summary>
/// 暂存：把下载好的文件摆成「相对 <c>out/</c> 根目录」的样子。
///
/// 为什么要有这一步：<c>out/</c> 由 <see cref="ConfigGenerator"/> 统一负责，
/// 它需要一份「和 SD 卡根目录同构」的目录整棵合并过去。中间隔一层暂存目录，
/// 两边就都不用知道对方的细节 —— 下载阶段不认识 ini，生成阶段也不认识 zip。
///
/// 落点完全由 <see cref="AssetPick.Targets"/> 声明（见 <see cref="ComponentCatalog"/>），
/// 这里**不认识任何具体文件名**。以前不是这样的：以前是「一个组件有多个 zip 就各解到
/// 以文件名命名的子目录」，于是 <c>out/</c> 里凭空多出 <c>sdout/</c>、
/// <c>nx-ovlloader/</c> 一层，而裸文件（<c>.ovl</c> / <c>.bin</c>）则被一律平铺到
/// <c>out/</c> 根目录 —— 六个文件错位问题都出自这里。
///
/// 之所以独立成服务而不是留在 <c>MainViewModel</c> 里：真正的下载流程要联网，离线回归测试
/// 够不着，而「文件错位」恰恰只在这一步发生。抽出来之后测试可以直接喂假的下载物。
/// </summary>
public static class OutputStager
{
    /// <summary>
    /// 暂存一个组件的全部下载物。
    ///
    /// 整组一起做（而不是一个文件一个文件地调）是有意的：**开头的清空暂存区必须和
    /// 后面的暂存绑在同一个入口上**。分开的话，「忘了清空」这个错就落进了
    /// 「测试够不着 MainViewModel」的盲区里 —— 而它恰恰会让旧版本遗留的
    /// <c>unpacked/sdout/</c> 继续被合并进 <c>out/</c>，升级后老毛病照旧。
    /// </summary>
    /// <param name="items">该组件的全部下载物。</param>
    /// <param name="unpackedRoot">组件暂存根目录（<see cref="AppPaths.ComponentUnpackedRoot"/>）。</param>
    /// <param name="payloadRoot">payload 暂存根目录（<see cref="AppPaths.ComponentPayloadRoot"/>）。</param>
    /// <param name="includePayloads">是否暂存 payload。关掉时 payload 下载物原样留在下载目录里备用。</param>
    /// <param name="progress">解压进度（仅压缩包会回调）。</param>
    public static async Task<IReadOnlyList<StagedAsset>> StageComponentAsync(
        IReadOnlyList<StagingItem> items,
        string unpackedRoot,
        string payloadRoot,
        bool includePayloads,
        IProgress<StagingProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 下载全部成功之后才会走到这里，所以可以放心清空：
        // 暂存区是可再生的构建产物，不清的话上一轮的东西会跟着混进 out/。
        ResetStaging(unpackedRoot, payloadRoot);

        var staged = new List<StagedAsset>(items.Count);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.Pick.IsPayload && !includePayloads)
            {
                continue;
            }

            var root = item.Pick.IsPayload ? payloadRoot : unpackedRoot;

            if (item.Pick.IsArchive)
            {
                // 压缩包只认第一个落点，作为「包内容铺到哪」的目录。
                // 声明多个落点说明这条目录写错了 —— 一个包不可能同时铺到两个地方，
                // 静默取第一个只会让错误藏起来，所以直接报出来。
                if (item.Pick.ResolvedTargets.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"压缩包 {item.Pick.FileName} 只能声明一个落点，实际声明了 {item.Pick.ResolvedTargets.Count} 个。");
                }

                var directory = item.Pick.ResolvedTargets[0].Directory;
                var target = Combine(root, directory);
                var pick = item.Pick;

                var zipProgress = progress is null
                    ? null
                    : new Progress<ExtractProgress>(
                        p => progress.Report(new StagingProgress(pick, p.Current, p.Total)));

                var count = await Task.Run(
                    () => ArchiveExtractor.Extract(
                        item.SourcePath, target, zipProgress, cancellationToken, item.Pick.Extract),
                    cancellationToken);

                staged.Add(new StagedAsset(item.Pick, Normalize(directory), count, item.Pick.IsPayload));
                continue;
            }

            // 散装文件（.ovl / .bin）：按声明的落点逐个复制。
            // 必须支持「一个来源 → 多个落点」：hekate 的 payload 就要同时放到 payload.bin 和
            // bootloader/update.bin 两处。
            foreach (var placement in item.Pick.ResolvedTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relative = placement.Resolve(item.Pick.FileName);
                var target = Combine(root, relative);

                AppPaths.EnsureDirectory(Path.GetDirectoryName(target)!);
                File.Copy(item.SourcePath, target, overwrite: true);

                staged.Add(new StagedAsset(item.Pick, Normalize(relative), 1, item.Pick.IsPayload));
            }
        }

        return staged;
    }

    /// <summary>
    /// 清空一个组件的暂存区。
    ///
    /// 旧版本把 <c>sdout.zip</c> 解到 <c>unpacked/sdout/</c>、<c>nx-ovlloader.zip</c> 解到
    /// <c>unpacked/nx-ovlloader/</c>，新版直接解到 <c>unpacked/</c>。不清空的话，那两棵旧目录树
    /// 会**继续**被合并进 <c>out/</c> —— 用户升级之后看到的还是「多了不该有的文件夹」。
    /// </summary>
    private static void ResetStaging(string unpackedRoot, string payloadRoot)
    {
        DeleteDirectory(unpackedRoot);
        DeleteDirectory(payloadRoot);
    }

    private static string Combine(string root, string relative)
        => string.IsNullOrEmpty(relative)
            ? root
            : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Normalize(string relative) => relative.Replace('\\', '/');

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉不该让整轮生成失败：后面的合并是覆盖式的，最多留下一点陈旧文件
        }
    }
}
