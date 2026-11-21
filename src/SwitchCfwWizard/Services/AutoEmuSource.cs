using System.IO;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.Services;

/// <summary>
/// 「重建 emuMMC」所用组件包的来源解析（纯函数，不联网）。
///
/// 背景（用户 2026-11-21 要求）：这个包原先**内嵌在程序里**（<c>NsToolbox.AutoEmu.zip</c>），
/// 现在改为从 <see cref="DefaultUrl"/> 对应的仓库**下载最新 Release**；地址可在
/// 「高级设置」里改，并且受「优先下载预览版」「优先使用本地已下载的资源」两个开关影响。
///
/// 与 <see cref="UpdateSource"/> 的分工：那个类负责「检查更新」的地址，默认指向本程序仓库；
/// 这里负责 AutoEmu 组件包的地址，默认指向组件仓库。**解析规则是同一套**（见 <see cref="ResolveRepo"/>）。
/// </summary>
public static class AutoEmuSource
{
    /// <summary>用户提供的默认地址（GitHub 的 releases 网页地址）。</summary>
    public const string DefaultUrl = "https://github.com/q1378659137/AutoEmu/releases";

    /// <summary>Release 里那个组件包的文件名（与上游发布时用的名字一致）。</summary>
    public const string AssetName = "auto-emu.zip";

    /// <summary>
    /// `download/` 下给组件包专用的子目录名（2026-11-21 用户要求：下载完放到一个文件夹里）。
    ///
    /// 与各组件的 `ComponentDownloadRoot` 同一个约定：`download/<组件名>/`。
    /// 好处是 `download/` 根目录只剩"每个组件一个文件夹"，不会几十个 zip 混在一起。
    /// </summary>
    public const string DownloadFolderName = "AutoEmu";

    /// <summary>组件包的落地目录（<c>download/AutoEmu/</c>）。</summary>
    public static string DownloadFolder => AppPaths.ComponentDownloadRoot(DownloadFolderName);

    /// <summary>下载到本地时的文件名前缀，完整形态见 <see cref="BuildAssetFileName"/>。</summary>
    public const string DownloadedFilePrefix = "AutoEmu_";

    /// <summary>
    /// 下载落盘用的文件名：<c>AutoEmu_&lt;版本号&gt;.zip</c>。
    ///
    /// 为什么带版本号：文件名固定时，新旧两版会互相覆盖 —— 一旦某次下到一半的残骸
    /// 覆盖了能用的那份，「优先使用本地资源」还会拿它当宝贝（它只看大小）。
    /// 带上版本号后两份并存，`download/AutoEmu/` 里也能一眼看出当前用的是哪一版。
    /// tag 里的非法文件名字符逐个替换成 <c>_</c>，不做别的美化。
    /// </summary>
    public static string BuildAssetFileName(string versionText)
    {
        var safe = new string(versionText.Trim()
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray());

        return safe.Length == 0
            ? DownloadedFilePrefix + "unknown.zip"
            : $"{DownloadedFilePrefix}{safe}.zip";
    }

    /// <summary>
    /// 界面判据：**空值算「没填」**（会用 <see cref="DefaultUrl"/>，不该标红），
    /// 只有「填了但推不出仓库」才算坏值。
    /// </summary>
    public static bool IsAcceptable(string? text)
        => string.IsNullOrWhiteSpace(text) || ResolveRepo(text) is not null;

    /// <summary>
    /// 填了但推不出仓库时**回落到默认地址**（与「下载源」「检查更新」同一约定），
    /// 返回的 <c>FellBack</c> 让调用方把它说出来 —— 静默回落比不做更坏。
    /// </summary>
    public static (RepoSpec? Repo, bool FellBack) ResolveWithFallback(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (ResolveRepo(null), FellBack: false);
        }

        var explicitRepo = ResolveRepo(text);
        return explicitRepo is not null
            ? (explicitRepo, FellBack: false)
            : (ResolveRepo(null), FellBack: true);
    }

    /// <summary>
    /// 从用户填的地址（留空 = <see cref="DefaultUrl"/>）推出仓库。推不出返回 <c>null</c> ——
    /// **不猜**：指向一个错的仓库只会让用户以为「已经是最新版」。
    /// </summary>
    public static RepoSpec? ResolveRepo(string? text)
    {
        var raw = string.IsNullOrWhiteSpace(text) ? DefaultUrl : text.Trim();

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            // 允许直接写 owner/name
            return RepoSpec.TryParse(raw);
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        // 只认 GitHub 自家的主机：查询真正打的是 api.github.com，
        // 把别的站点按同一套路径去查只会得到「仓库不存在」这种看着像「地址填错了」的答案。
        var host = uri.Host;
        var isGitHub = host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                       || host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);
        var isApi = host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);

        if (!isGitHub && !isApi)
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (isApi)
        {
            // https://api.github.com/repos/<owner>/<name>/releases…
            return segments.Length >= 3 && segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase)
                ? new RepoSpec(segments[1], segments[2])
                : null;
        }

        // https://github.com/<owner>/<name>[/releases[/tag/x]]、/tree/main…
        return segments.Length >= 2 ? new RepoSpec(segments[0], segments[1]) : null;
    }
}
