using System.IO;
using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.Services;

/// <summary>
/// 「检查更新」的地址解析与版本比较（纯函数，不联网）。
///
/// 两件事各自容易出错，所以都放在这里、都能被穷举测试：
///
/// 1. **从用户填的地址推出仓库**。用户给的默认值是 GitHub 的 <c>/releases</c> **网页地址** ——
///    那是个 HTML 列表页，没有任何版本 JSON；真正能查版本的只有 <c>api.github.com/repos/&lt;o&gt;/&lt;n&gt;/releases</c>。
///    所以这里从各种常见形态（仓库地址、<c>/releases</c>、<c>/releases/tag/x</c>、<c>/releases/latest</c>、
///    API 地址、裸 <c>owner/name</c>）里把 <c>owner/name</c> 取出来。
/// 2. **版本比较**。程序集版本是 <c>0.1.0.0</c> 四个分量，而 Release 的 tag 往往是 <c>0.1.0</c> 三个
///    —— 直接拿 <see cref="Version"/> 比会踩「分量数不同」的坑（<c>0.1.0 &lt; 0.1.0.0</c> 在
///    <c>Version.CompareTo</c> 里**成立**，于是「同一个版本」被判成「有新版本」，反复提示更新）。
///    所以统一补零到四个分量再比。
/// </summary>
public static class UpdateSource
{
    /// <summary>用户提供的默认地址（GitHub 的 releases 网页地址）。</summary>
    public const string DefaultUrl = "https://github.com/q1378659137/SwitchCfwWizard/releases";

    /// <summary>Release 里那个可执行资源的名字（与仓库里发布时用的名字一致）。</summary>
    public const string AssetName = "SwitchCfwWizard.exe";

    /// <summary>下载到本地时的文件名前缀，完整形态见 <see cref="BuildAssetFileName"/>。</summary>
    public const string DownloadedFilePrefix = "SwitchCfwWizard_";

    /// <summary>
    /// 下载落盘用的文件名：<c>SwitchCfwWizard_&lt;版本号&gt;.exe</c>。
    ///
    /// 为什么要带版本号（用户明确要求）：文件名与旧版一样时，新包会**覆盖**正在运行的那个 exe，
    /// 而 Windows 上那是做不到的（文件被占用）—— 结果是一个含义模糊的 IO 异常，
    /// 用户只会看到「更新失败」。带上版本号后新旧两份并存，覆盖不再发生。
    ///
    /// tag 里可能带文件名非法字符（例如 <c>v1.0/rc</c>），逐个替换成 <c>_</c>；
    /// 但**不做别的美化** —— 文件名里要能看到「获取到的那个版本号」本身。
    /// </summary>
    public static string BuildAssetFileName(string versionText)
    {
        var safe = new string(versionText.Trim()
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray());

        return safe.Length == 0
            ? DownloadedFilePrefix + "unknown.exe"
            : $"{DownloadedFilePrefix}{safe}.exe";
    }

    /// <summary>
    /// 界面判据：**空值算「没填」**（会用 <see cref="DefaultUrl"/>，不该标红），
    /// 只有「填了但推不出仓库」才算坏值。
    /// </summary>
    public static bool IsAcceptable(string? text)
        => string.IsNullOrWhiteSpace(text) || ResolveRepo(text) is not null;

    /// <summary>
    /// 「检查更新」真正要用的仓库 + 是不是**回落**到了默认地址。
    ///
    /// 与裸 <see cref="ResolveRepo"/> 的差别只有一条：填了但推不出仓库时**回落到默认地址**
    /// （用户 2026-09-22 要求「填了但格式不对会回落默认地址」），而不是让这次检查直接作废 ——
    /// 与「下载源」「插件文件槽」同一约定。
    ///
    /// ⚠️ 回落**必须说出来**（界面提示 + 日志）。静默回落比不做更坏：
    /// 用户会以为「我填的那条生效了」，于是永远查不到自己填错了。
    /// ⚠️ 留空**不算回落**（那是默认状态，本来就该用默认地址）—— 否则不改任何东西也会看到一句警告。
    /// </summary>
    public static (RepoSpec? Repo, bool FellBack) ResolveWithFallback(string? text)
    {
        var explicitRepo = ResolveRepo(text);
        if (explicitRepo is not null)
        {
            return (explicitRepo, FellBack: false);
        }

        // 走到这里只有一种情况：用户**填了**东西、但认不出仓库（留空时 ResolveRepo 已经给出默认）。
        // 默认地址也推不出来的话返回 null —— 调用方会走「地址用不了」那条兜底提示，不会崩。
        return (ResolveRepo(null), FellBack: true);
    }

    /// <summary>
    /// 从用户填的地址（留空 = <see cref="DefaultUrl"/>）推出仓库。推不出返回 <c>null</c> ——
    /// **不猜**：指向一个错的仓库只会让用户以为「已经是最新版」。
    /// </summary>
    public static RepoSpec? ResolveRepo(string? text)
    {
        var raw = string.IsNullOrWhiteSpace(text) ? DefaultUrl : text.Trim();

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            // 只认 GitHub 自家的主机。查询真正打的是 <c>api.github.com</c>，把别的站点
            // （GitLab、自建 Gitea、内部 Git）按同一套路径去查，只会得到「仓库不存在」这种
            // 看着像「地址填错了」的答案，而真实原因是「这套检查只支持 GitHub」。
            // 宁可明确说「这个地址用不了」，也不要给出一个会把人引偏的答案。
            var host = uri.Host;
            if (!host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && !host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)
                && !host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            // api.github.com/repos/<owner>/<name>/…（用户可能直接抄了 API 地址）
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                && segments.Count >= 1
                && segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase))
            {
                segments.RemoveAt(0);
            }

            return FromSegments(segments);
        }

        // 不是绝对 URL：按 owner/name（或 owner/name/releases）这种裸写法处理
        return FromSegments(raw.Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList());
    }

    private static RepoSpec? FromSegments(List<string> segments)
    {
        if (segments.Count >= 3 && segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase))
        {
            // releases 页 / releases/latest / releases/tag/vX / releases/download/… 全都指向同一个仓库，
            // 所以第 3 段之后有什么都不影响结论。
            segments = segments.Take(2).ToList();
        }

        // 只接受恰好两段：多出来的段说明这不是一个仓库地址（例如项目页下的某个子路径），
        // 硬取前两段会把路径当仓库名，指向一个 404 的仓库而看不出问题。
        if (segments.Count != 2)
        {
            return null;
        }

        var owner = segments[0];
        var name = segments[1];

        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return owner.Length > 0 && name.Length > 0 ? new RepoSpec(owner, name) : null;
    }

    /// <summary>
    /// 解析版本号字符串。容忍：<c>v</c> 前缀、前后空格、四段以内、以及版本号后面跟的
    /// 预览标记（<c>0.1.0-rc1</c> → <c>0.1.0</c>，只取数字段）。解析不出返回 <c>null</c>。
    /// </summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        // 只取开头的数字与点，遇到第一个非数字非点的字符就停：
        // "0.1.0-rc1" → "0.1.0"；"1.2 (build 5)" → "1.2"
        var end = 0;
        while (end < value.Length && (char.IsAsciiDigit(value[end]) || value[end] == '.'))
        {
            end++;
        }

        var digits = value[..end].Trim('.');
        if (digits.Length == 0)
        {
            return null;
        }

        var parts = digits.Split('.');
        var numbers = new int[4];

        for (var i = 0; i < parts.Length && i < 4; i++)
        {
            // 超过 int 范围（比如把日期当版本号且带了 10 位数字）时不要抛，按「解析不出」处理
            if (!int.TryParse(parts[i], out numbers[i]))
            {
                return null;
            }
        }

        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    /// <summary>
    /// <paramref name="candidate"/> 是否比 <paramref name="current"/> 新。
    ///
    /// 任一边解析不出就返回 <c>false</c> —— 宁可说「已是最新」，也不要因为看不懂一个版本号
    /// 就推着用户去装一个不知道是什么的东西。
    /// </summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        var a = ParseVersion(candidate);
        var b = ParseVersion(current);

        return a is not null && b is not null && a > b;
    }

    /// <summary>界面上显示用的版本号格式（与日志里的程序版本一致：三段）。</summary>
    public static string Format(Version? version)
        => version is null ? "?" : version.ToString(3);

    /// <summary>
    /// 这段字节像不像一个 Windows 可执行文件（PE 头 <c>MZ</c>）。
    ///
    /// 为什么要验：下回来的东西完全可能是**一个错误页**（HTTP 200 + 一段 HTML）——
    /// 代理、镜像站、企业网关都可能干这种事。那种情况下「下载成功」是假的，
    /// 而它会以新版 exe 的身份被启动，Windows 给出的提示只会是
    /// 「不是有效的 Win32 应用程序」—— 用户根本联系不到「网络中间那层有问题」。
    /// 先验两个字节，「下载了但内容不对」就能在这里被拦下。
    /// </summary>
    public static bool HasPortableExecutableHeader(ReadOnlySpan<byte> head)
        => head.Length >= 2 && head[0] == (byte)'M' && head[1] == 'Z';
}
