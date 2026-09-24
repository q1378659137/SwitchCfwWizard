namespace SwitchCfwWizard.Services;

/// <summary>
/// GitHub 镜像站（第三方反向代理）的地址改写：把 GitHub 的地址换成镜像站上的等价地址。
///
/// 用户 2026-09-19 的需求：在「高级设置」里填一个镜像站地址，**留空时一切照旧**走 GitHub 官方地址。
///
/// 三种改写形态（2026-09-19 用 <c>gh.shubiao.cc.cd</c> 实测过，逐条 curl 验证）：
/// <list type="bullet">
///   <item>
///     <c>github.com/&lt;owner&gt;/&lt;repo&gt;/…</c> → <c>&lt;镜像&gt;/&lt;owner&gt;/&lt;repo&gt;/…</c>
///     —— 主域名直接换掉，路径原样。**Release 资源大包也走这条**：实测 340 MB 的
///     <c>Firmware.23.0.0.zip</c> 回 200 + <c>application/octet-stream</c> + 正确的 <c>content-length</c>，
///     而且它的 302 目标是**镜像自己**的 <c>/proxy/release-assets.githubusercontent.com/…</c> ——
///     整条下载都由镜像承担，不是「镜像只给个跳转、字节还是从 GitHub 拉」。
///   </item>
///   <item>
///     <c>raw.githubusercontent.com/&lt;owner&gt;/&lt;repo&gt;/&lt;ref&gt;/&lt;path&gt;</c>
///     → <c>&lt;镜像&gt;/raw/&lt;owner&gt;/&lt;repo&gt;/&lt;ref&gt;/&lt;path&gt;</c> —— raw 要加一个 <c>raw/</c> 前缀。
///   </item>
///   <item>
///     其它主机（<c>api.github.com</c>、<c>codeload.github.com</c> …）
///     → <c>&lt;镜像&gt;/proxy/&lt;主机&gt;/&lt;路径&gt;</c> —— 通用代理前缀。
///     实测：<c>&lt;镜像&gt;/proxy/codeload.github.com/&lt;o&gt;/&lt;n&gt;/tar.gz/HEAD</c> 回 200 +
///     <c>application/x-gzip</c>；<c>&lt;镜像&gt;/proxy/api.github.com/repos/…</c> 回 **403 + application/json**
///     （那是 GitHub 的限流答复，说明请求真的代理到 GitHub 了，见下面那条「查询为什么先直连」）。
///   </item>
/// </list>
///
/// ⚠️ 「主域名换掉」这条规则**只对 github.com 成立**。曾经想当然地按同一个规则去拼
///    <c>&lt;镜像&gt;/api.github.com/repos/…</c>，实测回来的是 **404** —— 三种主机各有各的写法，
///    而这类错误在日志里只表现为「一堆 404」，很难一眼看出是拼错了。
///
/// ⚠️ **别用「换域名」以外的推导**。codeload 的整包地址其实还有一条等价路径
///    （<c>&lt;镜像&gt;/&lt;o&gt;/&lt;n&gt;/archive/&lt;ref&gt;.tar.gz</c>，实测也是 200），但那要解析并重排路径段 ——
///    多一处推导就多一处「猜错的形状」，而 <c>/proxy/&lt;主机&gt;/…</c> 是镜像站自己的通用规则、
///    路径原样带过去。所以这里只用通用规则。
///
/// ⚠️ 镜像站是**第三方**。启用它就意味着请求（以及配了 Token 时的 Token）交给对方转发 ——
///    这一点在界面的提示里写明、日志里也单独提醒一行，不藏着（见 <c>Settings.Mirror.Desc</c>
///    与 <c>Log.Session.MirrorToken</c>）。
///
/// ⚠️ 本类只管「把地址换成镜像站上的等价地址」，**不管什么时候该用它** —— 那是调用方的决定，
///    而且两边的决定**故意不一样**：下载是「填了就直接走镜像」，查询是「先官方、连不上才用镜像」
///    （理由见 <see cref="GitHubReleaseService"/>：镜像的出口 IP 是公用的，未登录的 API 额度
///    按 IP 算，直接换过去会「填了镜像反而一个组件都查不到」）。
/// </summary>
public static class GitHubMirror
{
    /// <summary>
    /// 「Cloudflare 搭建镜像站」的说明文档地址。
    ///
    /// 为什么把它放在这个类里而不是界面那边：它和镜像站是同一件事的两半 ——
    /// 这里管「填了地址怎么用」，这份文档管「地址从哪来」。
    /// 镜像站是整套加速能力里**唯一需要用户自己准备**的东西（下载源、插件地址都有内置默认值），
    /// 把入口放在填地址的地方，这个问题才不必靠用户自己去搜。
    /// </summary>
    public const string BuildGuideUrl = "https://docs.qq.com/doc/DTXJES21ZdFJkRkF0";

    /// <summary>镜像站能代到的上游站点：只有 GitHub 自家的域名才会被改写。</summary>
    private static bool IsGitHubHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("githubusercontent.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把用户填的镜像站地址规范化成 <c>https://主机[:端口][/子路径]</c>。
    ///
    /// 返回 <c>null</c> 表示「不启用镜像」，三种情况都归到这里：
    /// <list type="number">
    ///   <item>留空 —— 用户没在用镜像，一切照旧；</item>
    ///   <item>填的东西根本不成形（有空格、没主机名、不是 http/https）；</item>
    ///   <item>
    ///     看起来是**把仓库地址整段粘进来了**（路径超过 1 段，例如
    ///     <c>https://gh.example.com/CTCaer/hekate</c>）。这一条值得单独拦：它结构上完全合法，
    ///     不拦的话每条下载地址都会被再拼上一次 <c>/CTCaer/hekate</c> 而 404，
    ///     而用户手里那条地址**看着就是对的**。界面上另有红字说明「只填域名」。
    ///   </item>
    /// </list>
    ///
    /// 顺手补协议：用户填 <c>gh.example.com</c> 也认（补成 <c>https://</c>），
    /// 这是最常见的写法，为它报「格式不对」只会让人莫名其妙。
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        // 路径最多一层（<c>https://镜像/子路径</c> 这种少见但合法）；
        // 两层及以上按「粘了仓库地址」处理，见上面的说明。
        if (uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 1)
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>这条镜像站地址能不能用（空 = 没启用，也是「能用」，只是不生效）。</summary>
    public static bool IsAcceptable(string? raw) =>
        string.IsNullOrWhiteSpace(raw) || Normalize(raw) is not null;

    /// <summary>
    /// 把一条 GitHub 地址改写成镜像站上的等价地址。
    ///
    /// 下面几种情况**原样返回**（都不算错误，也不需要调用方判断）：
    /// <list type="bullet">
    ///   <item>没启用镜像（<paramref name="mirror"/> 为 null / 空白 / 非法）；</item>
    ///   <item>这条地址不是 GitHub 的（不碰别人的域名）；</item>
    ///   <item>这条地址已经在镜像站上了 —— 见下面的幂等说明。</item>
    /// </list>
    ///
    /// ⚠️ **幂等**不是可选的：同一个地址被改两次的话，第二次会把镜像主机名当成「上游主机」
    ///    塞进 <c>/proxy/</c> 里，得到 <c>&lt;镜像&gt;/proxy/&lt;镜像&gt;/…</c> —— 一条必定 404 的地址，
    ///    而且日志里看着很正常。所以主机名与镜像站相同就直接放行。
    /// </summary>
    public static string Apply(string url, string? mirror)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var root = Normalize(mirror);
        if (root is null)
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return url;
        }

        if (!IsGitHubHost(uri.Host))
        {
            return url;
        }

        if (Uri.TryCreate(root, UriKind.Absolute, out var rootUri)
            && string.Equals(uri.Host, rootUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var path = uri.AbsolutePath;

        var target = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? root + path
            : uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                ? root + "/raw" + path
                : root + "/proxy/" + uri.Host + path;

        // 查询串要带上：contents 端点的 <c>?ref=</c>、Release 列表的 <c>?per_page=</c> 都在这上面，
        // 丢了它们不会报错，只会安静地换一个结果（默认分支、默认每页 30 条）。
        return target + uri.Query;
    }
}
