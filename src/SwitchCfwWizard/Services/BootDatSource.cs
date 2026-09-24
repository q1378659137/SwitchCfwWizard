using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.Services;

/// <summary>
/// <c>boot.dat</c> 的下载地址解析（纯函数，不联网）。
///
/// 背景：这个文件原先**内嵌在程序里**（<c>Resources/boot.dat</c> + <c>EmbeddedResource</c>），
/// 2026-09-21 用户要求取消内置、改为从仓库下载，并把开关从「输出内容」挪到「框架」。
///
/// ⚠️ **为什么需要这一层解析，而不是直接拿用户填的地址去下**：
/// 用户给的默认地址是 GitHub 的**网页地址**（<c>/blob/…</c>）—— 浏览器打开它是文件预览页，
/// 但**下下来的字节是 HTML**。直接照搬会写出一份 11 KB 的「boot.dat」，里面全是网页，
/// 而且**不会报任何错**（HTTP 200、有字节、也写出了文件）。
/// 所以这里把 <c>blob</c> 形态解析成真正取字节的 <c>raw</c> 形态。
/// 这不是「换掉用户给的地址」，而是**把同一条地址翻译成它对应的字节流形态**。
///
/// 幂等：已经解析过的地址再喂进来结果不变（<c>raw.githubusercontent.com</c> 与任意其它主机都原样放行）。
/// 解析不出就返回 <c>null</c>，**不猜** —— 猜错的代价是悄悄产出一个错的引导文件。
/// </summary>
public static class BootDatSource
{
    /// <summary>用户提供的默认下载地址（<c>blob</c> 网页形态，由本类翻译成 raw）。</summary>
    public const string DefaultUrl = "https://github.com/q1378659137/SwitchCfwWizard/blob/main/boot.dat";

    /// <summary>落到 <c>out/</c> 根目录时的文件名。</summary>
    public const string FileName = "boot.dat";

    /// <summary>
    /// contents 端点兜底通道的媒体类型：不带它，GitHub 会返回**这段文件的 JSON 元数据**
    /// （HTTP 200、内容是 JSON），于是「下载成功」却把 JSON 当引导文件写出去 —— 完全静默。
    /// </summary>
    public const string RawAccept = "application/vnd.github.raw";

    private const string RawHost = "raw.githubusercontent.com";
    private const string GitHubHost = "github.com";
    private const string ApiRoot = ReleaseAsset.ApiRoot;

    /// <summary>
    /// 界面判据：**空值算「没填」**（会自动用 <see cref="DefaultUrl"/>，不该标红），
    /// 只有「填了但解析不出可用地址」才算坏值。
    /// </summary>
    public static bool IsAcceptable(string? text)
        => string.IsNullOrWhiteSpace(text) || Resolve(text) is not null;

    /// <summary>
    /// 把用户填的地址（留空 = <see cref="DefaultUrl"/>）解析成一条可下载的地址。
    /// 返回 <c>null</c> 表示这条地址用不了 —— 调用方据此报警并跳过，而不是硬下。
    /// </summary>
    public static BootDatPlan? Resolve(string? text)
    {
        var raw = string.IsNullOrWhiteSpace(text) ? DefaultUrl : text.Trim();

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // 只认 http/https：其它协议（ftp/file/…）交给 HttpClient 只会得到一个含糊的异常
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (uri.Host.Equals(RawHost, StringComparison.OrdinalIgnoreCase))
        {
            // 已经是 raw 形态：原样用，但仍然能推出 API 兜底（形态是 /<owner>/<name>/<ref>/<path…>）
            return segments.Length >= 4
                ? new BootDatPlan(raw, BuildContentsFallback(segments, refIndex: 2))
                : new BootDatPlan(raw, null);
        }

        if (!uri.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            // 任意其它主机（含用户自建镜像、内部服务器）：照用户说的用，不给兜底
            // —— 我们无从知道那份资源在 API 上叫什么。
            return new BootDatPlan(raw, null);
        }

        // github.com/<owner>/<name>/{blob|raw}/<ref>/<path…>
        if (segments.Length >= 5
            && (segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase)
                || segments[2].Equals("raw", StringComparison.OrdinalIgnoreCase)))
        {
            var owner = segments[0];
            var name = segments[1];
            var gitRef = segments[3];
            var path = string.Join('/', segments.Skip(4));

            return new BootDatPlan(
                $"{Uri.UriSchemeHttps}://{RawHost}/{owner}/{name}/{gitRef}/{path}",
                $"{ApiRoot}/repos/{owner}/{name}/contents/{path}?ref={Uri.EscapeDataString(gitRef)}");
        }

        // 例如 github.com/<owner>/<name>/releases/… 、项目页、issues 页 —— 那些不是文件地址。
        // 交回去只会下到 HTML，所以明确拒绝（调用方报警），别让用户以为在下载。
        return null;
    }

    private static string BuildContentsFallback(string[] segments, int refIndex)
        => $"{ApiRoot}/repos/{segments[0]}/{segments[1]}/contents/"
           + $"{string.Join('/', segments.Skip(refIndex + 1))}?ref={Uri.EscapeDataString(segments[refIndex])}";

    /// <summary>API contents 兜底通道的容量上限：超过它 GitHub 不会返回正文（用它下大文件必然失败）。</summary>
    public const long ContentsApiMaxBytes = 1024 * 1024;
}

/// <summary>
/// 一条解析好的 boot.dat 下载方案。
///
/// <paramref name="FallbackUrl"/> 是**备用通道**：<c>raw.githubusercontent.com</c> 在部分网络下
/// 会被整段阻断（本沙箱就是：DNS 都解析不出来），而 <c>api.github.com</c> 常常仍然通。
/// 两条通道都交给 <see cref="DownloadService"/>（它会自动套用镜像站），主通道把重试次数用光后才换。
/// </summary>
public sealed record BootDatPlan(string DownloadUrl, string? FallbackUrl)
{
    /// <summary>备用通道（供 <see cref="DownloadService.DownloadAsync"/> 用）。没有则为 null。</summary>
    public DownloadFallback? Fallback => string.IsNullOrWhiteSpace(FallbackUrl)
        ? null
        : new DownloadFallback(FallbackUrl!, BootDatSource.RawAccept);
}
