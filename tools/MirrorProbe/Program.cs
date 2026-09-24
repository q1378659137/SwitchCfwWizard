// 镜像站连通性与**形态**验证（联网工具，不进回归）。
//
// 为什么需要一个独立的联网工具：回归里的镜像用例全是**离线**的（纯函数 + 桩 HttpMessageHandler），
// 它们能证明「请求打在了哪个 URI 上」，但证明不了「HttpClient 真的跟着镜像的 302 走、
// 把字节流出来落成文件」。而用户配镜像的全部价值就在后半句上。
//
// 它还回答了另一个问题：**你填的那个镜像站到底支不支持这几种形态**。
// 镜像站各不相同（有的只代理 release 资源、有的连 raw 都不代理），
// 所以「同一个规则套到另一个镜像上还行不行」只能实测。新增插件时如果发现某条地址没走镜像，
// 先跑一次这个工具，就知道是该改配置还是该改规则。
//
// 用法（在 tools/MirrorProbe 目录下）：
//   dotnet run -c Release                    # 用内置样例镜像
//   dotnet run -c Release -- https://你的镜像  # 换一个镜像（`--` 不能省，否则参数会被 dotnet 自己吃掉）
//
// 退出码 0 = 镜像侧全通过（直连对照组不通属**正常**，见下）。

using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Models;
using SwitchCfwWizard.Services;

var mirror = args.Length > 0 ? args[0] : "https://gh.shubiao.cc.cd";
var work = Path.Combine(AppContext.BaseDirectory, "probe-out");

if (Directory.Exists(work))
{
    Directory.Delete(work, recursive: true);
}

Directory.CreateDirectory(work);

var failures = 0;
var repo = new RepoSpec("ppkantorski", "ovl-sysmodules");

Console.WriteLine($"镜像站：{GitHubMirror.Normalize(mirror) ?? "(解析不出可用地址 —— 按官方地址走)"}");
Console.WriteLine($"工作目录：{work}");
Console.WriteLine();

// ── 镜像侧：这两条必须通 ────────────────────────────────────────
async Task ThroughMirrorAsync(string title, string url, string fileName, Func<string, bool> checkText,
    Func<string, bool>? checkFile = null)
{
    var effective = GitHubMirror.Apply(url, mirror);
    var path = Path.Combine(work, fileName);
    var watch = Stopwatch.StartNew();

    Console.WriteLine("── " + title);
    Console.WriteLine("   GitHub 直链 : " + url);
    Console.WriteLine("   实际会请求  : " + effective);

    using var http = NetworkClientFactory.Create(proxy: null, timeout: Timeout.InfiniteTimeSpan);
    var service = new DownloadService(http, mirror);

    if (GitHubMirror.Normalize(mirror) is null)
    {
        Console.WriteLine("   => **镜像地址不可用**，程序会按官方地址走（这条工具也验不了镜像）");
        failures++;
        Console.WriteLine();
        return;
    }

    try
    {
        var received = 0L;
        await service.DownloadAsync(
            url,   // 传**原始直链**：改写是 DownloadService 的职责（这一点本身也是被测对象）
            path,
            new Progress<DownloadProgress>(p => received = p.Received),
            CancellationToken.None);

        var bytes = new FileInfo(path).Length;
        var head = File.ReadAllText(path)[..Math.Min(60, (int)bytes)];
        var ok = checkText(head) && (checkFile?.Invoke(path) ?? true);

        Console.WriteLine($"   => {bytes} 字节 / {watch.Elapsed.TotalSeconds:0.0}s（进度回调收到 {received} 字节）");
        Console.WriteLine("   判定        : " + (ok ? "通过" : "**不通过**"));
        if (!ok)
        {
            failures++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   => 失败：{ex.GetType().Name}: {ex.Message}");
        failures++;
    }

    Console.WriteLine();
}

// ① raw 文件：镜像的 /raw/ 形态
await ThroughMirrorAsync(
    "① raw 文件（<镜像>/raw/…）",
    ComponentCatalog.BuildRepoFileRawUrl(repo, "README.md"),
    "readme.md",
    text => text.Contains("ovlSysmodule", StringComparison.Ordinal));

// ② 仓库整包：codeload 走镜像的通用代理前缀 /proxy/<主机>/…
//    额外把归档**真解开看条目** —— 光验 gzip 魔数不够，
//    一个「恰好也是 gzip 的错误页」能骗过它。
await ThroughMirrorAsync(
    "② 仓库整包（codeload → <镜像>/proxy/codeload.github.com/<owner>/<name>/tar.gz/<ref>）",
    ComponentCatalog.BuildRepoArchiveUrl(repo),
    "repo.tar.gz",
    _ => true,
    path =>
    {
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);

            var entries = new List<string>();
            while (tar.GetNextEntry() is { } entry)
            {
                entries.Add(entry.Name);
            }

            var hasReadme = entries.Any(n => n.EndsWith("/README.md", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"   归档        : {entries.Count} 个条目、根目录 = {entries.FirstOrDefault()?.Split('/')[0]}");

            if (entries.Count == 0 || !hasReadme)
            {
                Console.WriteLine("   **解出来的不是这个仓库的快照** —— 这条改写形态可能已经失效");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   **归档解不开**：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    });

// ── 对照组：直连。**不计失败** ──────────────────────────────────
// 直连不通恰恰是「需要镜像」的证据；直连能通说明镜像的收益只是快慢。
// 两种情况都正常，所以这里只陈述事实，不判成败。
Console.WriteLine("── ③ 对照组：同一条 raw 地址**不走镜像**");
try
{
    using var http = NetworkClientFactory.Create(proxy: null, timeout: TimeSpan.FromSeconds(20));
    var url = ComponentCatalog.BuildRepoFileRawUrl(repo, "README.md");
    var body = await http.GetStringAsync(url);

    Console.WriteLine($"   => 直连可通（{body.Length} 字符）。所以你这边的网络本来就能访问 GitHub，"
                      + "镜像的收益只在快慢上。");
}
catch (Exception ex)
{
    Console.WriteLine($"   => 直连不通（{ex.GetType().Name}）。这正是镜像要解决的问题 —— 上面两条通过，"
                      + "说明这些下载在你这台机器上真的走得通了。");
}

// ── ④ / ⑤ 两个新功能：**真从网络把字节取回来**（2026-09-21 加） ──────────
// 前面几条验的是镜像站；这两条验的是新功能自己的**两条官方通道**：
//   boot.dat   主通道 raw.githubusercontent.com → 兜底 api.github.com 的 contents 端点
//   新版 exe   主通道 release 直链（会 302 到 objects.githubusercontent.com）→ 兜底 API asset 端点
//
// 为什么必须放在这个工具里而不是回归里：回归是**离线**的，它能断言「会请求哪条地址」，
// 断言不了「真的把字节取回来了」。而这两件事的差距，恰恰就是「用户点了没反应」和「能用」的差距。
//
// ⚠️ 这一节**刻意不带镜像**：要验的是官方那两条通道。在一个 raw 域名被整段阻断的网络里,
//    它同时也是兜底通道的**实测证据** —— 主通道挂了、兜底把文件取回来了。
Console.WriteLine("── ④ boot.dat：用程序自己的地址解析 + 下载器取回来（不带镜像）");

var bootDatPlan = BootDatSource.Resolve(null);

if (bootDatPlan is null)
{
    Console.WriteLine("   **默认地址解析不出可用地址** —— 这条功能等于坏了（生成时会跳过 boot.dat）");
    failures++;
}
else
{
    Console.WriteLine("   解析出的主地址: " + bootDatPlan.DownloadUrl);
    Console.WriteLine("   解析出的兜底  : " + (bootDatPlan.FallbackUrl ?? "(无)"));

    using var bootDatHttp = NetworkClientFactory.Create(proxy: null, timeout: TimeSpan.FromSeconds(90));
    var bootDatPath = Path.Combine(work, "boot.dat");

    try
    {
        await new DownloadService(bootDatHttp).DownloadAsync(
            bootDatPlan.DownloadUrl,
            bootDatPath,
            progress: null,
            CancellationToken.None,
            onRetry: retry => Console.WriteLine(
                $"   [通道] 第 {retry.Attempt}/{retry.MaxAttempts} 次：{retry.Reason}"
                + (retry.IsChannelSwitch ? "（改用兜底通道）" : string.Empty)),
            fallback: bootDatPlan.Fallback);

        var bytes = File.ReadAllBytes(bootDatPath);
        var magic = Encoding.ASCII.GetString(bytes, 0, Math.Min(6, bytes.Length));
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Console.WriteLine($"   => {bytes.Length} 字节 / sha256 {sha[..16]}… / 开头「{magic}」");

        var ok = bytes.Length == 11520 && magic == "SXGEAR";
        Console.WriteLine("   判定: " + (ok ? "通过（11520 字节、SXGEAR 开头）" : "**不通过**（应是 11520 字节且以 SXGEAR 开头）"));
        if (!ok)
        {
            failures++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   => 失败：{ex.GetType().Name}: {ex.Message}");
        failures++;
    }
}

Console.WriteLine();
Console.WriteLine("── ⑤ 新版 exe：查 release → 下载（不带镜像）");

var updateRepo = UpdateSource.ResolveRepo(null);
var appVersion = typeof(GitHubMirror).Assembly.GetName().Version;

if (updateRepo is null)
{
    Console.WriteLine("   **默认更新地址推不出仓库** —— 「检查更新」会直接报地址不可用");
    failures++;
}
else
{
    using var updateHttp = NetworkClientFactory.Create(proxy: null, timeout: TimeSpan.FromSeconds(90));

    try
    {
        var releases = await new GitHubReleaseService(updateHttp, new ProbeSink(), token: null)
            .GetReleasesAsync(updateRepo, 20, CancellationToken.None);
        var latest = ReleaseInfo.SelectBest(releases, preferPrerelease: false);

        Console.WriteLine($"   仓库 {updateRepo.DisplayName}：{releases.Count} 条发布；"
                          + $"最新稳定版 = {latest?.Tag ?? "(无)"}；当前程序版本 = {UpdateSource.Format(appVersion)}");

        var asset = latest?.FindAssetByName(UpdateSource.AssetName);
        if (asset is null)
        {
            Console.WriteLine($"   **最新发布里没有 {UpdateSource.AssetName}** —— 点「检查更新」会走到「无法自动更新」那条分支");
            failures++;
        }
        else
        {
            Console.WriteLine("   是否有新版  : " + (UpdateSource.IsNewer(latest!.Tag, appVersion?.ToString()) ? "有" : "没有（点一下会说「已是最新版」）"));

            var fileName = UpdateSource.BuildAssetFileName(latest.Tag);
            var target = Path.Combine(work, fileName);
            var fallback = asset.HasApiId
                ? new DownloadFallback(ReleaseAsset.BuildApiAssetUrl(updateRepo, asset.Id), "application/octet-stream")
                : null;

            Console.WriteLine($"   落盘名: {fileName}（API 报的大小 {asset.SizeText}）");

            await new DownloadService(updateHttp).DownloadAsync(
                asset.DownloadUrl,
                target,
                progress: null,
                CancellationToken.None,
                onRetry: retry => Console.WriteLine(
                    $"   [通道] 第 {retry.Attempt}/{retry.MaxAttempts} 次：{retry.Reason}"
                    + (retry.IsChannelSwitch ? "（改用兜底通道）" : string.Empty)),
                fallback: fallback);

            var head = new byte[2];
            using (var stream = File.OpenRead(target))
            {
                stream.ReadExactly(head);
            }

            var size = new FileInfo(target).Length;
            var ok = UpdateSource.HasPortableExecutableHeader(head) && size == asset.Size;

            Console.WriteLine($"   => {size} 字节 / 头 {head[0]:X2} {head[1]:X2}");
            Console.WriteLine("   判定: " + (ok ? "通过（PE 头正确、与 API 报的大小一致）" : "**不通过**"));
            if (!ok)
            {
                failures++;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   => 失败：{ex.GetType().Name}: {ex.Message}");
        failures++;
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "MIRROR PROBE: OK（镜像侧两条形态都通过）"
    : $"MIRROR PROBE: {failures} 条不通过 —— 这个镜像站可能不支持对应形态");

return failures == 0 ? 0 : 1;

/// <summary>
/// 查询 Release 时用的日志出口：只把警告留下来（查询失败会走 <c>Warn</c>），
/// 正常的信息行不刷屏。放在文件末尾 —— 顶层语句必须排在类型声明之前。
/// </summary>
internal sealed class ProbeSink : ILogSink
{
    public void Log(LogLevel level, string message)
    {
        if (level is LogLevel.Warning or LogLevel.Error)
        {
            Console.WriteLine($"   [{level}] {message}");
        }
    }
}
