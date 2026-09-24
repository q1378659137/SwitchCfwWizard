using System.Diagnostics;
using System.IO;
using System.Net.Http;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.Services;

/// <summary>下载进度快照。</summary>
public sealed class DownloadProgress
{
    public DownloadProgress(long received, long? total, double bytesPerSecond)
    {
        Received = received;
        Total = total;
        BytesPerSecond = bytesPerSecond;
    }

    public long Received { get; }

    public long? Total { get; }

    public double BytesPerSecond { get; }

    public double Percent => Total is > 0
        ? Math.Clamp(Received * 100.0 / Total.Value, 0, 100)
        : 0;

    public string SpeedText => BytesPerSecond > 0
        ? ReleaseAsset.FormatSize((long)BytesPerSecond) + "/s"
        : "—";

    public string ProgressText => Total is > 0
        ? $"{ReleaseAsset.FormatSize(Received)} / {ReleaseAsset.FormatSize(Total.Value)}"
        : ReleaseAsset.FormatSize(Received);

    public TimeSpan? Remaining
    {
        get
        {
            if (Total is not > 0 || BytesPerSecond <= 1)
            {
                return null;
            }

            var seconds = (Total.Value - Received) / BytesPerSecond;
            return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
        }
    }

    public string RemainingText
    {
        get
        {
            var remaining = Remaining;
            if (remaining is null)
            {
                return string.Empty;
            }

            return remaining.Value.TotalHours >= 1
                ? $"{remaining.Value:hh\\:mm\\:ss}"
                : $"{remaining.Value:mm\\:ss}";
        }
    }
}

/// <summary>
/// 一次下载重试的通知（用于在界面上告诉用户正在重试）。
///
/// <paramref name="IsFallback"/> 为 true 表示这次已经在走备用地址；
/// <paramref name="IsChannelSwitch"/> 为 true 表示这一条不是「同一条地址再试一次」，
/// 而是「直链已经放弃，换一条通道」—— 界面必须把它说成「改用 API asset 端点」，
/// 说成「重试」的话用户会以为还在打同一个地址。
/// </summary>
public sealed record DownloadRetry(
    int Attempt,
    int MaxAttempts,
    string Reason,
    TimeSpan Delay,
    bool IsFallback = false,
    bool IsChannelSwitch = false);

/// <summary>
/// 直链重试耗尽后可以改走的备用地址。
///
/// 存在的理由：release 直链会 302 跳到 <c>objects.githubusercontent.com</c>，部分代理恰恰在这一跳上
/// 返回 502（实测直连 HTTP 000、代理 502），而 API asset 端点走的是 <c>api.github.com</c>，同一次会话里能通。
/// </summary>
/// <param name="Url">备用地址（见 <see cref="ReleaseAsset.BuildApiAssetUrl"/>）。</param>
/// <param name="Accept">
/// 该地址要求的 Accept 头。asset 端点必须是 <c>application/octet-stream</c>，
/// 少了它 GitHub 返回的是资源的 JSON 元数据（HTTP 200），会被当成组件包写进 SD 卡。
/// </param>
/// <param name="Authorization">
/// 可选的 Authorization 头（配了 GitHub Token 时带上，避开 API 限流）。
/// 不用担心它会漏到跳转目标上：asset 端点 302 到 S3 预签名 URL 时，.NET 会自动清掉 Authorization
/// （官方文档：<i>"The Authorization header is cleared on auto-redirects … No other headers are cleared."</i>），
/// 而 S3 对「URL 已带签名 + 请求头又带 Authorization」是直接 400 的。
/// </param>
public sealed record DownloadFallback(string Url, string Accept, string? Authorization = null);

/// <summary>流式下载，实时回报进度；带自动重试、停滞超时与备用地址。</summary>
public sealed class DownloadService
{
    private const int BufferSize = 81920;
    private const int DefaultMaxAttempts = 3;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>asset 端点的媒体类型，见 <see cref="DownloadFallback.Accept"/>。</summary>
    private const string OctetStream = "application/octet-stream";

    /// <summary>每条通道各自的尝试次数上限。回归测试会调小，避免白等退避。</summary>
    internal int MaxAttempts { get; set; } = DefaultMaxAttempts;

    /// <summary>第 n 次失败后等多久（默认 2s、4s…）。回归测试换成零等待。</summary>
    internal Func<int, TimeSpan> Backoff { get; set; } = attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    /// <summary>连续多久收不到数据就判定为卡死。只要一直在收数据就不会触发。</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    private readonly HttpClient _http;

    /// <summary>
    /// 规范化之后的镜像站地址（见 <see cref="GitHubMirror"/>），<c>null</c> = 不启用 / 填的地址非法。
    ///
    /// 在这一层统一改写：调用方手里有**五种** GitHub 地址（release 直链、API asset 端点、
    /// 仓库 raw 直链、contents 端点、整仓 tar.gz），逐个去改迟早漏一个 ——
    /// 而漏掉的那条会安安静静地继续走官方地址，只在「为什么这个文件还是很慢」里表现出来。
    /// 收在这一处，就只剩「所有下载都必须经过 DownloadService」这一条要维护。
    /// </summary>
    private readonly string? _mirror;

    /// <param name="mirror">
    /// 可选的 GitHub 镜像站地址。留空 = 一切照旧走官方地址（行为与没有这个参数时逐字节相同）。
    /// </param>
    public DownloadService(HttpClient http, string? mirror = null)
    {
        _http = http;
        _mirror = GitHubMirror.Normalize(mirror);
    }

    /// <param name="fallback">
    /// 可选的备用地址。直链把重试次数用光后才会改走它（见 <see cref="DownloadFallback"/>）。
    /// 传 null 或与直链相同则只走直链，行为与以前完全一致。
    /// </param>
    public async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        Action<DownloadRetry>? onRetry = null,
        DownloadFallback? fallback = null)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            AppPaths.EnsureDirectory(directory);
        }

        var tempPath = destinationPath + ".part";

        // 通道顺序：先直链（不限流、也便宜），直链把重试次数用光之后才换备用地址。
        // 刻意不「第一次失败就换」—— 直链偶发抖动很常见，为此去打 API 只会白白消耗配额，
        // 而且备用地址同样要走同一个代理。
        //
        // 镜像站在**这一步**改写：两条通道都要过一遍，否则会出「直链走镜像、备用通道偷偷回官方」
        // 这种半吊子状态 —— 而官方地址恰恰是镜像存在的理由（直连通不了才要镜像）。
        var directUrl = GitHubMirror.Apply(url, _mirror);
        var channels = new List<Channel> { new(directUrl, null, null, false) };

        if (fallback is not null && !string.IsNullOrWhiteSpace(fallback.Url))
        {
            var fallbackUrl = GitHubMirror.Apply(fallback.Url, _mirror);

            if (!string.Equals(fallbackUrl, directUrl, StringComparison.OrdinalIgnoreCase))
            {
                channels.Add(new Channel(fallbackUrl, fallback.Accept, fallback.Authorization, true));
            }
        }

        for (var index = 0; index < channels.Count; index++)
        {
            var channel = channels[index];
            var lastChannel = index == channels.Count - 1;

            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDelete(tempPath);

                try
                {
                    await DownloadOnceAsync(channel, tempPath, progress, cancellationToken);
                    File.Move(tempPath, destinationPath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < MaxAttempts && NetworkErrors.IsTransient(ex, cancellationToken))
                {
                    // 网络抖动 / 网关错误 / 卡死 → 退避后重试
                    TryDelete(tempPath);
                    var delay = Backoff(attempt); // 默认 2s、4s
                    onRetry?.Invoke(new DownloadRetry(
                        attempt, MaxAttempts, NetworkErrors.Describe(ex), delay, channel.IsFallback));
                    progress?.Report(new DownloadProgress(0, null, 0));
                    await Task.Delay(delay, cancellationToken);
                }
                catch (Exception ex) when (!lastChannel && NetworkErrors.IsTransient(ex, cancellationToken))
                {
                    // 本条通道的重试次数用光了，后面还有通道 → 换过去。
                    // 这一条必须专门报出去：否则日志里只会看到「重试 3 次后成功」，
                    // 用户无从知道实际走的是直链还是 API asset 端点。
                    TryDelete(tempPath);
                    onRetry?.Invoke(new DownloadRetry(
                        MaxAttempts,
                        MaxAttempts,
                        NetworkErrors.Describe(ex),
                        TimeSpan.Zero,
                        IsFallback: true,
                        IsChannelSwitch: true));
                    progress?.Report(new DownloadProgress(0, null, 0));
                    break;
                }
                catch
                {
                    TryDelete(tempPath);
                    throw;
                }
            }
        }
    }

    /// <summary>一条下载通道（直链，或备用地址）。</summary>
    private sealed record Channel(string Url, string? Accept, string? Authorization, bool IsFallback);

    private async Task DownloadOnceAsync(
        Channel channel,
        string tempPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, channel.Url);

        if (!string.IsNullOrWhiteSpace(channel.Accept))
        {
            // 必须**逐请求**指定，不能靠 HttpClient 的 DefaultRequestHeaders：
            // asset 端点若拿到 application/vnd.github+json 会返回资源的 JSON 元数据（HTTP 200！），
            // 于是「下载成功」却把一段 JSON 当成组件包写进 SD 卡。
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd(channel.Accept);
        }

        if (!string.IsNullOrWhiteSpace(channel.Authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", channel.Authorization);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // 兜底：万一还是拿回了 JSON（token 权限不足、端点行为变了…），宁可当失败，
        // 也不能把一段 JSON 当组件包写盘 —— 那种错误要等拷进机器才发现。
        //
        // 判据是「**任何**备用通道 + 拿到 JSON」而不是「Accept 恰好等于 octet-stream」：
        // 备用通道的 Accept 都是「要二进制内容」的意思（release 走 octet-stream，
        // 仓库文件树走 application/vnd.github.raw），任何一种拿回 JSON 都是同一个错误。
        // 写成等值比较的话，新增一种备用通道时会**静默漏过**这条兜底。
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (channel.Accept is not null
            && mediaType is not null
            && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"备用地址返回的是 {mediaType} 而不是二进制内容，已放弃这条通道（避免把 JSON 当成组件包写盘）。");
        }

        var total = response.Content.Headers.ContentLength;

        long received = 0;
        double speed = 0;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long lastBytes = 0;

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (true)
        {
            // 每读一次就重置计时，因此这只会捕捉“完全收不到数据”的卡死
            readCts.CancelAfter(StallTimeout);

            int read;
            try
            {
                read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), readCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"连续 {StallTimeout.TotalSeconds:0} 秒没有收到数据");
            }

            if (read <= 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;

            var elapsed = stopwatch.Elapsed;
            if (elapsed - lastReport >= ReportInterval)
            {
                var delta = elapsed - lastReport;
                if (delta.TotalSeconds > 0)
                {
                    speed = (received - lastBytes) / delta.TotalSeconds;
                }

                lastReport = elapsed;
                lastBytes = received;
                progress?.Report(new DownloadProgress(received, total, speed));
            }
        }

        await target.FlushAsync(cancellationToken);
        progress?.Report(new DownloadProgress(received, total ?? received, speed));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 忽略清理失败
        }
    }
}
