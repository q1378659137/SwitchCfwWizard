using System.Net;
using System.Net.Http;
using System.Text.Json;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.Services;

/// <summary>GitHub Releases 查询。</summary>
public sealed class GitHubReleaseService
{
    private const string ApiRoot = ReleaseAsset.ApiRoot;
    private const int MaxAttempts = 3;

    private readonly HttpClient _http;
    private readonly ILogSink _log;
    private readonly string? _token;

    /// <summary>
    /// 规范化之后的镜像站地址（见 <see cref="GitHubMirror"/>），<c>null</c> = 不启用 / 填的地址非法。
    ///
    /// ⚠️ 查询走镜像的方式与**下载**不一样，是有意的：查询**先直连官方，连不上才改走镜像**
    ///    （见 <see cref="CandidateUrls"/>）；下载则是「填了就直接走镜像」。
    ///
    /// 为什么查询不能直接换掉官方地址（2026-09-19 实测踩到）：镜像站的出口是**很多人共用**的一个 IP，
    /// 而未登录的 GitHub API 额度是**按 IP** 算的 60 次/小时。实测把查询改走镜像之后，第一次查询就
    /// 撞上 <c>403 API rate limit exceeded for 104.23.168.80</c> —— 也就是「填了镜像站反而一个组件都查不到」。
    /// 直连官方时那 60 次是用户自己的，够用；镜像只该在**官方根本打不通**时兜底。
    /// </summary>
    private readonly string? _mirror;

    public GitHubReleaseService(HttpClient http, ILogSink log, string? token, string? mirror = null)
    {
        _http = http;
        _log = log;
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _mirror = GitHubMirror.Normalize(mirror);
    }

    /// <summary>
    /// 这条查询的候选地址：**官方在前，镜像站在后**。<paramref name="officialUrl"/> 与镜像地址相同
    /// （没填镜像）时只返回一条，于是「没填镜像站」的行为与没有这个功能时逐字节相同。
    ///
    /// 两条的用法见 <see cref="GetReleasesAsync"/> 的通道循环：**只有官方「根本打不通」才会用第二条**，
    /// 官方给出 HTTP 回答（403 限流、404 没有这个仓库…）时不会换 —— 那是答案，不是网络问题，
    /// 换一条通道只会撞上镜像那份更挤的额度，还会把真正的原因盖掉。
    /// </summary>
    private IReadOnlyList<string> CandidateUrls(string officialUrl)
    {
        var mirrored = GitHubMirror.Apply(officialUrl, _mirror);

        return string.Equals(mirrored, officialUrl, StringComparison.OrdinalIgnoreCase)
            ? [officialUrl]
            : [officialUrl, mirrored];
    }

    /// <summary>按发布时间倒序取回最近若干个 Release（包含预览版）。带自动重试，官方连不上时改走镜像站。</summary>
    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(RepoSpec repo, int perPage, CancellationToken cancellationToken)
    {
        var urls = CandidateUrls(
            $"{ApiRoot}/repos/{repo.Owner}/{repo.Name}/releases?per_page={Math.Clamp(perPage, 1, 100)}");

        for (var channel = 0; channel < urls.Count; channel++)
        {
            var url = urls[channel];
            var lastChannel = channel == urls.Count - 1;

            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return await FetchReleasesAsync(url, repo, cancellationToken);
                }
                catch (Exception ex) when (attempt < MaxAttempts && NetworkErrors.IsTransient(ex, cancellationToken))
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s、4s
                    _log.Warn($"  {repo.DisplayName} 查询失败（{NetworkErrors.Describe(ex)}），{delay.TotalSeconds:0} 秒后重试"
                              + $"（第 {attempt}/{MaxAttempts} 次）…");
                    await Task.Delay(delay, cancellationToken);
                }
                catch (Exception ex) when (!lastChannel && NetworkErrors.IsTransient(ex, cancellationToken))
                {
                    // 官方那条把重试都用光了 —— 不是「GitHub 说不行」，是「根本打不通」→ 换镜像站。
                    _log.Warn($"  {repo.DisplayName} 官方地址连不上（{NetworkErrors.Describe(ex)}），"
                              + $"改用镜像站 {_mirror} 重试…");
                    break;
                }
            }
        }

        // 走不到：最后一个通道失败时上面那个 catch 不匹配，异常已经抛出去了。
        throw new InvalidOperationException($"{repo.DisplayName} 的查询通道全部失败。");
    }

    private async Task<IReadOnlyList<ReleaseInfo>> FetchReleasesAsync(
        string url, RepoSpec repo, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException(
                $"GitHub API 速率限制（HTTP {(int)response.StatusCode}）。请在「高级设置」中填写 GitHub Token 后重试。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"请求 {repo.DisplayName} 失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseReleases(json);
    }

    internal static IReadOnlyList<ReleaseInfo> ParseReleases(string json)
    {
        var result = new List<ReleaseInfo>();

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var tag = GetString(element, "tag_name") ?? string.Empty;
            var title = GetString(element, "name") ?? tag;
            var prerelease = element.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
            var htmlUrl = GetString(element, "html_url") ?? string.Empty;

            var published = DateTimeOffset.MinValue;
            var publishedRaw = GetString(element, "published_at") ?? GetString(element, "created_at");
            if (!string.IsNullOrEmpty(publishedRaw)
                && DateTimeOffset.TryParse(publishedRaw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                published = parsed;
            }

            var assets = new List<ReleaseAsset>();
            if (element.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    var name = GetString(asset, "name");
                    var downloadUrl = GetString(asset, "browser_download_url");
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(downloadUrl))
                    {
                        continue;
                    }

                    long size = 0;
                    if (asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize))
                    {
                        size = parsedSize;
                    }

                    // 资源 id：直链下载失败时靠它改走 API asset 端点。解析不到就留 0，
                    // 下载侧会因此只走直链（老 JSON / 手写样例里没有这个字段）。
                    long id = 0;
                    if (asset.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var parsedId))
                    {
                        id = parsedId;
                    }

                    assets.Add(new ReleaseAsset(name, downloadUrl, size, id));
                }
            }

            result.Add(new ReleaseInfo(tag, title, prerelease, published, htmlUrl, assets));
        }

        return result;
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// 列出仓库某个目录下的**文件**（<see cref="SlotSource.RepoDirectory"/> 的槽用）。
    ///
    /// 只留 <c>type == "file"</c>：用户对 theme-patches 的要求原文是「取该目录下**所有文件（不要文件夹）**」。
    /// 子目录（<c>type == "dir"</c>）与子模块（<c>"submodule"</c>）一律丢掉 —— 它们不是可下载的文件，
    /// 混进下载清单只会变成一堆 404。
    ///
    /// 结果**按名字排序**：GitHub 返回的顺序不保证稳定，而下载顺序直接决定日志顺序与进度条的分段顺序。
    /// 不排序的话，两次运行的日志会对不上，排查时无从比对。
    /// </summary>
    public async Task<IReadOnlyList<RepoDirectoryFile>> GetDirectoryFilesAsync(
        RepoSpec repo, string directory, CancellationToken cancellationToken)
    {
        var urls = CandidateUrls(ComponentCatalog.BuildRepoDirectoryApiUrl(repo, directory));

        for (var channel = 0; channel < urls.Count; channel++)
        {
            var url = urls[channel];
            var lastChannel = channel == urls.Count - 1;

            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return await FetchDirectoryFilesAsync(url, repo, directory, cancellationToken);
                }
                catch (Exception ex) when (attempt < MaxAttempts && NetworkErrors.IsTransient(ex, cancellationToken))
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    _log.Warn($"  {repo.DisplayName}/{directory} 列目录失败（{NetworkErrors.Describe(ex)}），"
                              + $"{delay.TotalSeconds:0} 秒后重试（第 {attempt}/{MaxAttempts} 次）…");
                    await Task.Delay(delay, cancellationToken);
                }
                catch (Exception ex) when (!lastChannel && NetworkErrors.IsTransient(ex, cancellationToken))
                {
                    // 与查询 Release 同一条规矩：只有**连接层**失败才换通道（403 是 GitHub 的回答，不是网络问题）。
                    _log.Warn($"  {repo.DisplayName}/{directory} 官方地址连不上（{NetworkErrors.Describe(ex)}），"
                              + $"改用镜像站 {_mirror} 重试…");
                    break;
                }
            }
        }

        // 走不到：最后一个通道失败时上面那个 catch 不匹配，异常已经抛出去了。
        throw new InvalidOperationException($"{repo.DisplayName}/{directory} 的列目录通道全部失败。");
    }

    private async Task<IReadOnlyList<RepoDirectoryFile>> FetchDirectoryFilesAsync(
        string url, RepoSpec repo, string directory, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException(
                $"GitHub API 速率限制（HTTP {(int)response.StatusCode}）。请在「高级设置」中填写 GitHub Token 后重试。");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"{repo.DisplayName} 里没有目录「{directory}」"
                + "（上游可能改了目录名，或该仓库的默认分支变了）。可在「高级设置 → 插件文件」里改目录。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"列出 {repo.DisplayName}/{directory} 失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseDirectoryFiles(json);
    }

    /// <summary>
    /// 解析 contents 端点的列目录响应。
    ///
    /// ⚠️ 路径指向的是**文件**而不是目录时，这个端点返回的是一个 JSON **对象**（不是数组）。
    /// 那种情况下返回空表，由调用方的「逐槽对账」点名报出来 —— 报错信息里会带上用户填的目录名，
    /// 比在这里抛一句「解析失败」有用得多。
    /// </summary>
    internal static IReadOnlyList<RepoDirectoryFile> ParseDirectoryFiles(string json)
    {
        var result = new List<RepoDirectoryFile>();

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (GetString(element, "type") != "file")
            {
                continue;
            }

            var name = GetString(element, "name");
            var path = GetString(element, "path");
            var downloadUrl = GetString(element, "download_url");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(downloadUrl))
            {
                continue;
            }

            long size = 0;
            if (element.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize))
            {
                size = parsedSize;
            }

            result.Add(new RepoDirectoryFile(name, path, size, downloadUrl));
        }

        return result.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
    }
}
