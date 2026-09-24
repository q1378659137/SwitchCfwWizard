using SwitchCfwWizard.Infrastructure;

namespace SwitchCfwWizard.Models;

/// <summary>一个 GitHub 仓库。</summary>
public sealed record RepoSpec(string Owner, string Name)
{
    public string DisplayName => Owner + "/" + Name;

    public override string ToString() => DisplayName;

    /// <summary>
    /// 把用户填的 <c>owner/name</c> 解析成 <see cref="RepoSpec"/>。
    ///
    /// 容错处理用户手输的常见形态：整段 URL（<c>https://github.com/owner/name</c>）、
    /// 末尾带 <c>.git</c> 或斜杠、前后有空格。**故意不做「猜一个近似仓库」的纠正** ——
    /// 解析不出来就返回 null，让调用方明确拒绝，比默默指向一个错的仓库安全得多。
    /// </summary>
    public static RepoSpec? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();

        // 去掉协议与主机名，只留路径部分
        foreach (var prefix in new[] { "https://github.com/", "http://github.com/", "github.com/" })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..];
                break;
            }
        }

        value = value.Trim('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 只接受恰好两段；多出来的（例如 /releases 之类）一律拒绝，避免猜错
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return null;
        }

        return new RepoSpec(parts[0], parts[1]);
    }
}

/// <summary>Release 中的一个资源文件。</summary>
public sealed class ReleaseAsset
{
    /// <summary>GitHub REST API 的根地址。查询 Release 与走 asset 端点都用它，只留一处定义。</summary>
    public const string ApiRoot = "https://api.github.com";

    public ReleaseAsset(string name, string downloadUrl, long size, long id = 0)
    {
        Name = name;
        DownloadUrl = downloadUrl;
        Size = size;
        Id = id;
    }

    public string Name { get; }

    /// <summary>
    /// release 页面上的直链（<c>github.com/&lt;owner&gt;/&lt;repo&gt;/releases/download/…</c>）。
    /// 它会 302 跳到 <c>objects.githubusercontent.com</c>，这一步在部分代理下会被拦成 502，
    /// 因此失败后可以改走 <see cref="BuildApiAssetUrl"/>。
    /// </summary>
    public string DownloadUrl { get; }

    public long Size { get; }

    /// <summary>
    /// GitHub 给这个资源分配的数值 id（JSON 里的 <c>id</c>，**不是** <c>name</c>）。
    /// 用来走 API asset 端点。取不到时为 0 —— 老版本存档 / 手写的 JSON 里可能没有这个字段。
    /// </summary>
    public long Id { get; }

    /// <summary>是否能用 API asset 端点下载（拿不到 id 时只能用直链）。</summary>
    public bool HasApiId => Id > 0;

    public string SizeText => FormatSize(Size);

    /// <summary>
    /// API asset 端点。<b>必须配 <c>Accept: application/octet-stream</c></b>：
    /// 少了它 GitHub 会返回这个资源的 **JSON 元数据**（HTTP 200，内容是一段 JSON），
    /// 于是「下载成功」却把一段 JSON 当成组件包写进 SD 卡 —— 完全静默。
    /// </summary>
    public static string BuildApiAssetUrl(RepoSpec repo, long assetId)
        => $"{ApiRoot}/repos/{repo.Owner}/{repo.Name}/releases/assets/{assetId}";

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "?";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.##} {units[unit]}";
    }
}

/// <summary>一个 GitHub Release。</summary>
public sealed class ReleaseInfo
{
    public ReleaseInfo(
        string tag,
        string title,
        bool isPrerelease,
        DateTimeOffset publishedAt,
        string htmlUrl,
        IReadOnlyList<ReleaseAsset> assets)
    {
        Tag = tag;
        Title = title;
        IsPrerelease = isPrerelease;
        PublishedAt = publishedAt;
        HtmlUrl = htmlUrl;
        Assets = assets;
    }

    public string Tag { get; }

    public string Title { get; }

    public bool IsPrerelease { get; }

    public DateTimeOffset PublishedAt { get; }

    public string HtmlUrl { get; }

    public IReadOnlyList<ReleaseAsset> Assets { get; }

    /// <summary>“稳定版 / 预览版”的本地化文本。</summary>
    public string ChannelText => Localization.LocalizationService.Instance[
        IsPrerelease ? "Common.PreRelease" : "Common.Stable"];

    public ReleaseAsset? FindAsset(Func<ReleaseAsset, bool> predicate)
        => Assets.FirstOrDefault(predicate);

    public ReleaseAsset? FindAssetByName(string name)
        => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 在候选 Release 中挑选要使用的版本。
    /// 规则（对应需求「如果有预览版优先下载预览版」）：
    /// 当最新预览版的发布时间不早于最新稳定版时，优先采用预览版；否则采用最新稳定版。
    /// </summary>
    public static ReleaseInfo? SelectBest(IEnumerable<ReleaseInfo> releases, bool preferPrerelease)
    {
        var list = releases.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var newestStable = list.Where(r => !r.IsPrerelease)
            .OrderByDescending(r => r.PublishedAt)
            .FirstOrDefault();

        var newestPrerelease = list.Where(r => r.IsPrerelease)
            .OrderByDescending(r => r.PublishedAt)
            .FirstOrDefault();

        if (preferPrerelease && newestPrerelease is not null
            && (newestStable is null || newestPrerelease.PublishedAt >= newestStable.PublishedAt))
        {
            return newestPrerelease;
        }

        return newestStable ?? newestPrerelease;
    }
}
