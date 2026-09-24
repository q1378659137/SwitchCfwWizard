namespace SwitchCfwWizard.Models;

/// <summary>
/// 解压时怎么处理**包内路径**。默认（<see cref="All"/>）是「原样铺开」，与以前的行为逐字节一致。
///
/// 为什么需要它 —— 两个上游包的结构与用户的落点要求对不上：
/// <list type="bullet">
///   <item>
///     <c>ELY3M/sys-ftpd</c> 的 <c>release.zip</c> 里是 <c>out/atmosphere/…</c> 与 <c>out/config/…</c>，
///     多了一层 <c>out/</c>；用户要的是「只取 <c>atmosphere</c> 与 <c>config</c> 两个文件夹放 out 根」。
///     ⇒ <see cref="StripPrefix"/> + <see cref="KeepTopLevelDirectories"/>。
///   </item>
///   <item>
///     <c>xfangfang/wiliwili</c> 的包里除了 <c>wiliwili.nro</c> 还有一堆别的东西，
///     用户只要那一个文件。⇒ <see cref="KeepFileNames"/>。
///   </item>
/// </list>
///
/// ⚠️ **筛选绝不能静默筛空**。声明的 <see cref="StripPrefix"/> 一层都没命中（说明上游改了包结构、
/// 或者声明写错了），产物会是**一个空文件夹而日志一切正常** —— 正是本项目最在意的那类失败。
/// 所以 <see cref="ArchiveExtractor"/> 在「声明了剥前缀、却一个文件都没留下」时**直接报错**。
/// </summary>
public sealed record ExtractPlan
{
    /// <summary>原样铺开。所有字段为空时的默认值。</summary>
    public static readonly ExtractPlan All = new();

    /// <summary>
    /// 剥掉的前缀，按 <c>/</c> 分段比对（例：<c>"out"</c> 会把 <c>out/config/a.ini</c> 变成 <c>config/a.ini</c>）。
    /// 为空表示不剥。
    /// </summary>
    public string? StripPrefix { get; init; }

    /// <summary>
    /// 只保留这些**顶层目录**（剥完前缀之后再看）。为空表示不限。
    /// </summary>
    public IReadOnlyList<string> KeepTopLevelDirectories { get; init; } = [];

    /// <summary>
    /// 只保留这些**文件名**（任意层级，只比文件名不比路径）。为空表示不限。
    /// </summary>
    public IReadOnlyList<string> KeepFileNames { get; init; } = [];

    /// <summary>
    /// 两个白名单之间是**或**的关系：命中任一即保留。都为空表示全保留。
    /// 目前没有任何槽同时用两者，写清楚是为了让以后用的人不用猜。
    /// </summary>
    private bool HasKeepFilter => KeepTopLevelDirectories.Count > 0 || KeepFileNames.Count > 0;

    /// <summary>不做任何处理（用来判断「能不能沿用原来的目录项处理方式」）。</summary>
    public bool KeepsEverything => StripPrefix is null && !HasKeepFilter;

    /// <summary>
    /// 把包内条目路径映射成**落盘相对路径**（统一 <c>/</c> 分隔）；返回 <c>null</c> 表示丢弃该条目。
    ///
    /// 纯函数、不碰磁盘 —— 这样回归测试可以直接喂路径字符串验筛选规则，不必真造压缩包。
    /// 回归里那些样本路径是**实测的真实包内路径**，来源见 <c>tools/zip-listing.py</c>
    /// （HTTP Range 只取 ZIP 中央目录，几十 KB 拿全清单，适用任意仓库）与
    /// <c>tools/probe-background.py</c>（更早的 curl 版，**只探「后台」那 5 个**）。
    /// </summary>
    public string? Map(string entryFullName)
    {
        var path = entryFullName.Replace('\\', '/').TrimStart('/');
        if (path.Length == 0)
        {
            return null;
        }

        if (StripPrefix is { Length: > 0 } prefix)
        {
            var trimmed = prefix.Trim('/');
            if (path.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return null; // 前缀目录项本身
            }

            if (path.StartsWith(trimmed + "/", StringComparison.OrdinalIgnoreCase))
            {
                path = path[(trimmed.Length + 1)..];
            }
            else
            {
                return null; // 不在声明的前缀之下 ⇒ 不是我们要的内容
            }
        }

        if (path.Length == 0)
        {
            return null;
        }

        if (!HasKeepFilter)
        {
            return path;
        }

        var slash = path.LastIndexOf('/');
        var fileName = slash >= 0 ? path[(slash + 1)..] : path;
        var topLevel = slash >= 0 ? path[..path.IndexOf('/')] : string.Empty;

        if (KeepFileNames.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)))
        {
            return path;
        }

        // 顶层目录白名单只对「在某个目录里」的条目有意义；根目录下的散文件不匹配它。
        if (topLevel.Length > 0
            && KeepTopLevelDirectories.Any(d => string.Equals(d, topLevel, StringComparison.OrdinalIgnoreCase)))
        {
            return path;
        }

        return null;
    }
}
