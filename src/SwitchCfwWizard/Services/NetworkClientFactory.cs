using System.Net;
using System.Net.Http;

namespace SwitchCfwWizard.Services;

/// <summary>创建全应用共用的 HttpClient（统一 UA / 代理 / 解压）。</summary>
public static class NetworkClientFactory
{
    public const string UserAgent = "SwitchCfwWizard/0.1 (+https://github.com/)";

    /// <summary>
    /// 创建一个 HttpClient。
    /// timeout 传 <see cref="Timeout.InfiniteTimeSpan"/> 表示由调用方自己控制单次请求超时
    /// （下载走这条路，因为大文件耗时不可预估，改由 DownloadService 的停滞超时兜底）。
    /// </summary>
    public static HttpClient Create(string? proxy, TimeSpan timeout, bool gitHubApiHeaders = false)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy.Trim());
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler)
        {
            Timeout = timeout,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        if (gitHubApiHeaders)
        {
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        return client;
    }
}
