using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace SwitchCfwWizard.Services;

/// <summary>
/// 把底层网络异常翻译成用户看得懂、并且能照着做的提示。
/// 原始异常文本（例如 “The proxy tunnel request to proxy 'http://127.0.0.1:53271/' failed with status code '502'”）
/// 对普通用户没有任何指导意义。
/// </summary>
public static class NetworkErrors
{
    /// <summary>从异常消息里兜底提取 HTTP 状态码（代理隧道失败时 StatusCode 会是 null）。</summary>
    private static readonly Regex StatusCodePattern =
        new(@"(?:status code '?|\bHTTP )(\d{3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>判断这个异常是否值得重试（网络抖动、网关错误、超时等）。</summary>
    public static bool IsTransient(Exception exception, CancellationToken cancellationToken)
    {
        // 用户主动取消不算可重试
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            TimeoutException => true,
            SocketException => true,
            IOException => true,
            TaskCanceledException => true,
            OperationCanceledException => true,
            HttpRequestException http => IsTransientStatus(http),
            _ => exception.InnerException is not null && IsTransient(exception.InnerException, cancellationToken),
        };
    }

    private static bool IsTransientStatus(HttpRequestException exception)
    {
        var status = ExtractStatusCode(exception);

        // 5xx 网关/服务端错误基本都是临时的
        if (status is >= 500 and <= 599)
        {
            return true;
        }

        // 408 请求超时值得等一下再试；取不到状态码（连接中断等）同样值得重试。
        // 429 限流不重试：限额按小时重置，等几秒没有意义，不如直接把提示给用户。
        return status is 408 or null;
    }

    /// <summary>生成一句可以直接展示给用户的说明。</summary>
    public static string Describe(Exception exception)
    {
        var inner = Unwrap(exception);

        if (ExtractStatusCode(inner) is { } status)
        {
            return status switch
            {
                502 or 503 or 504 =>
                    $"代理或网络返回 HTTP {status}（网关错误）。常见原因：代理不稳定、代理不支持大文件、或下载地址被拦截。"
                    + "可以在「高级设置」里换一个代理，或清空代理后直连重试。",
                403 or 429 =>
                    $"被 GitHub 限流（HTTP {status}）。请在「高级设置」里填写 GitHub Token 后重试。",
                404 => "下载地址返回 HTTP 404，资源可能已被上游删除，请稍后重试。",
                _ => $"服务器返回 HTTP {status}。",
            };
        }

        return inner switch
        {
            TimeoutException => "连接超时，网络或代理响应太慢。可以在「高级设置」里调大超时时间，或更换代理。",
            SocketException socket => socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused =>
                    "连接被拒绝。如果配置了代理，请确认代理程序正在运行、端口填写正确。",
                SocketError.HostNotFound or SocketError.NoData =>
                    "无法解析域名。请检查网络连接或 DNS 设置。",
                _ => $"网络连接异常（{socket.SocketErrorCode}）。",
            },
            TaskCanceledException or OperationCanceledException => "操作已取消。",
            HttpRequestException =>
                "网络请求失败，可能是连接中断、DNS 解析失败或代理异常。可以在「高级设置」里更换代理，"
                + $"或清空代理后直连重试。（原始信息：{inner.Message}）",
            _ => inner.Message,
        };
    }

    /// <summary>
    /// 优先用 <see cref="HttpRequestException.StatusCode"/>；
    /// 代理隧道失败等场景该属性为 null，只能从消息文本里取。
    /// </summary>
    private static int? ExtractStatusCode(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: { } status })
        {
            return (int)status;
        }

        var match = StatusCodePattern.Match(exception.Message);
        return match.Success && int.TryParse(match.Groups[1].Value, out var parsed) ? parsed : null;
    }

    /// <summary>剥掉包装层，取到最里层的真实异常。</summary>
    private static Exception Unwrap(Exception exception)
    {
        while (exception is { InnerException: not null } and not HttpRequestException
               && exception.InnerException is not HttpRequestException)
        {
            exception = exception.InnerException;
        }

        return exception;
    }
}
