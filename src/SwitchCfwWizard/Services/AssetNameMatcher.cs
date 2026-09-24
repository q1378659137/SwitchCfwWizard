using System.Text.RegularExpressions;
using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.Services;

/// <summary>
/// 命中的方式。
///
/// ⚠️ 分成两族，**别混**（2026-09-18 联网 e2e 才发现的区别）：
/// <list type="bullet">
/// <item><see cref="Exact"/> 与 <see cref="Wildcard"/> = **按声明命中** —— 用户给的就是这个名字/这个模式，
/// 我们照着取到了。用户清单里 <c>triplayer-x.x.x.zip</c> 这类**通配写法是用户的意图**（跟着版本走），
/// 不是「他没写清楚、我们猜了一个」。</item>
/// <item><see cref="Stem"/> 与 <see cref="SoleCandidate"/> = **替它猜** —— 声明里是确切的（或模式没中的）
/// 名字，我们降级找了一个「大概就是它」。这两个必须留痕。</item>
/// </list>
/// 判据见 <see cref="AssetMatch.IsGuess"/>。**把前者的提示语写成后者，就会建议用户去改掉他自己给的名字。**
/// </summary>
public enum AssetMatchKind
{
    /// <summary>与用户给的名字逐字相同。</summary>
    Exact,

    /// <summary>
    /// 用户给的名字带 <c>x</c> 版本占位（如 <c>sys-botbasexx.zip</c>），按通配匹配上的。
    /// **这是按声明命中，不是猜**（见枚举说明）—— 声明里本来就有占位符。
    /// </summary>
    Wildcard,

    /// <summary>按「去掉版本号后的前缀」匹配上的。**声明里没写模式**（或模式没中），所以这是猜。</summary>
    Stem,

    /// <summary>该 Release 里**只有一个**同扩展名的资源，就当它是了。这是猜。</summary>
    SoleCandidate,
}

/// <param name="Asset">实际选中的资源。</param>
/// <param name="Kind">怎么选中的。</param>
/// <param name="Requested">用户/默认声明的名字（原样带回来，用于日志里说明「你要的是这个、我用的是那个」）。</param>
public sealed record AssetMatch(ReleaseAsset Asset, AssetMatchKind Kind, string Requested)
{
    public bool IsExact => Kind == AssetMatchKind.Exact;

    /// <summary>
    /// 这是不是一次「**猜**」—— 即：声明里给的名字没能直接对上，我们降级选了一个。
    ///
    /// <see cref="AssetMatchKind.Exact"/>（逐字）与 <see cref="AssetMatchKind.Wildcard"/>（声明即通配）
    /// 都**不算猜**：前者如你所愿，后者也是照着你给的模式取的。
    /// 只有 <see cref="AssetMatchKind.Stem"/> 与 <see cref="AssetMatchKind.SoleCandidate"/> 才是。
    ///
    /// 用途：调用方据此决定日志级别与措辞 —— 猜要报 Warning 并建议用户改名；
    /// 按模式命中只需一句中性说明（**报 Warning 会让用户以为出了问题，甚至去改掉自己给的模式**）。
    /// </summary>
    public bool IsGuess => Kind.IsGuess();
}

/// <summary>
/// 一次命中该向用户交代到什么程度。**这是「命中方式」的唯一分类表**
/// （见 <see cref="AssetMatchKindExtensions.NoticeFor"/>）。
/// </summary>
public enum SlotMatchNotice
{
    /// <summary>逐字命中 —— 如你所愿，不必说什么。</summary>
    Silent,

    /// <summary>
    /// 按用户给的**模式**命中（<c>x</c>/<c>X</c>/<c>*</c> 是他亲手写的占位符）：一句**中性说明**即可。
    /// ⚠️ 不要报 Warning —— 那等于**建议他改掉自己刚给的名字**。
    /// </summary>
    PatternHit,

    /// <summary>
    /// 我们**降级挑了一个**（声明里没有模式，或模式没中）：必须 **Warning 并建议改名**，
    /// 否则用户永远不知道「自己看到的版本号从哪来」，也无从判断猜得对不对。
    /// </summary>
    Guess,
}

/// <summary>
/// 「命中方式 → 该怎么交代」—— **只有这一份定义**。
///
/// 为什么要把分类收拢成一张表：这个判断有**三个**消费点 ——
/// <see cref="AssetMatch.IsGuess"/>、<see cref="SlotMatch.IsGuess"/>，以及**调用方的日志级别分流**。
/// 各写一遍「Stem 或 SoleCandidate」迟早会漂开，而漂开的后果是
/// **日志级别不一致，没有任何断言会红**。
///
/// 这不是假想：2026-09-18 的联网 e2e 就真的漂过一次 —— 分流当时写成「非 <see cref="AssetMatchKind.Exact"/>
/// 就报 Warning」，于是 TriPlayer（用户清单给的就是通配 <c>triplayer-x.x.x.zip</c>）每次跑都留一条
/// 「没有精确匹配、已按模式选用……**请改成确切的文件名**」。**结论没变、退出码没变、没有任何断言变红。**
/// </summary>
public static class AssetMatchKindExtensions
{
    /// <summary>
    /// 这一级命中该怎么交代。**新增一级就必须在这里归类** —— 所以最后一条是 <c>throw</c> 而不是回退默认值：
    /// 回退会让「忘了归类」静默变成「按猜处理」或「不吭声」，两种都不对，且都不会有人发现。
    /// </summary>
    public static SlotMatchNotice NoticeFor(this AssetMatchKind kind) => kind switch
    {
        AssetMatchKind.Exact => SlotMatchNotice.Silent,
        AssetMatchKind.Wildcard => SlotMatchNotice.PatternHit,
        AssetMatchKind.Stem or AssetMatchKind.SoleCandidate => SlotMatchNotice.Guess,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            "新增了命中方式却没在 NoticeFor 里归类 —— 先想清楚它属于「按声明命中」还是「猜」"),
    };

    /// <summary>这一级是不是「猜」（语义见 <see cref="AssetMatch.IsGuess"/>）。</summary>
    public static bool IsGuess(this AssetMatchKind kind) => kind.NoticeFor() == SlotMatchNotice.Guess;
}

/// <summary>
/// 把「声明/用户填的文件名」落到 Release 里的某个资源上。
///
/// 为什么需要容错（用户 2026-09-18 选的策略）：用户给的清单里有几个名字与上游实际发布**对不上** ——
/// <c>ldn_mitm_FW_x.x.x.zip</c> 是带版本占位的写法、<c>release.zip</c> 与 <c>Awoo-Installer.zip</c>
/// 则是上游改了资源名（实际叫 <c>sys-ftpd-1.0.5.zip</c> / <c>NSAInstaller.zip</c>）。
/// 严格按名字只会让这几个插件静默地下不到东西。
///
/// ⚠️ **容错绝不能是静默的**。四级瀑布里前两级是「如你所愿」（① 逐字命中 / ② 照你给的**模式**命中），
/// 后两级才是**猜**（③ 去掉版本号比前缀 / ④ 唯一同扩展名候选），所以命中方式会随结果一起带出去
/// （<see cref="AssetMatch.Kind"/>；「算不算猜」的判据见 <see cref="AssetMatch.IsGuess"/>），
/// 由调用方写进日志。
/// 判据：「错误值会不会产生不同的可观察结果」—— 这里必须会。
/// </summary>
public static class AssetNameMatcher
{
    /// <summary>按四级瀑布找一个资源；一个都找不到时返回 null（调用方据此告警，不静默跳过）。</summary>
    public static AssetMatch? Find(ReleaseInfo release, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || release.Assets.Count == 0)
        {
            return null;
        }

        var wanted = pattern.Trim();

        // ① 逐字相同
        if (release.FindAssetByName(wanted) is { } exact)
        {
            return new AssetMatch(exact, AssetMatchKind.Exact, wanted);
        }

        // ② 通配：x / X / * 都当版本占位
        if (HasWildcard(wanted) && BuildWildcard(wanted) is { } regex)
        {
            if (Best(release.Assets, a => regex.IsMatch(a.Name), wanted) is { } hit)
            {
                return new AssetMatch(hit, AssetMatchKind.Wildcard, wanted);
            }
        }

        // ③ 前缀：去掉尾部版本号后比前缀（且扩展名要一致）
        var stem = Stem(wanted);
        if (stem.Length >= 3
            && Best(release.Assets, a => SameExtension(a.Name, wanted) && a.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase), wanted) is { } stemHit)
        {
            return new AssetMatch(stemHit, AssetMatchKind.Stem, wanted);
        }

        // ④ 唯一候选：该扩展名下只有一个资源
        var sameExtension = release.Assets.Where(a => SameExtension(a.Name, wanted)).ToList();
        if (sameExtension.Count == 1)
        {
            return new AssetMatch(sameExtension[0], AssetMatchKind.SoleCandidate, wanted);
        }

        return null;
    }

    private static bool HasWildcard(string pattern) =>
        pattern.Contains('x', StringComparison.OrdinalIgnoreCase) || pattern.Contains('*');

    /// <summary>
    /// 把带占位的名字变成正则：<c>x</c> / <c>X</c> / <c>*</c> 一律当作「至少一个字符」。
    ///
    /// 用 <c>.+</c> 而不是 <c>.*</c> 是有意的：名字里的 <c>x</c> 大多数时候**是名字的一部分**
    /// （<c>Explorer</c>、<c>nxdt</c>、<c>fix</c>、<c>KeyX</c>），只有当它真的替掉了某些字符时才算占位。
    /// 用 <c>.*</c> 的话 <c>Fizeau.zip</c> 这种不含 x 的名字无所谓，但 <c>NX-Shell.nro</c>
    /// 会退化成「N 开头、以 -Shell.nro 结尾」这种过宽的规则。
    /// </summary>
    private static Regex? BuildWildcard(string pattern)
    {
        var escaped = Regex.Escape(pattern);
        var body = Regex.Replace(escaped, "[xX]+|\\*", ".+");

        try
        {
            return new Regex("^" + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 「去掉版本号后的前缀」。从结尾开始剥掉数字、点、横杠、下划线和 x 占位，
    /// 剥到第一个「像正常字符」的位置为止。
    /// 例：<c>sys-botbasexx.zip</c> → <c>sys-botbase</c>；<c>MissionControl-x.x.x-x-x.zip</c> → <c>MissionControl</c>；
    /// <c>battery_desync_fix_vx.x.x.nro</c> → <c>battery_desync_fix_v</c>（<c>v</c> 停手，它不像版本号）。
    /// </summary>
    private static string Stem(string pattern)
    {
        var dot = pattern.LastIndexOf('.');
        var body = dot > 0 ? pattern[..dot] : pattern;

        var end = body.Length;
        while (end > 0 && IsVersionChar(body[end - 1]))
        {
            end--;
        }

        return body[..end];
    }

    private static bool IsVersionChar(char c) =>
        char.IsAsciiDigit(c) || c is '.' or '-' or '_' || c is 'x' or 'X';

    private static bool SameExtension(string assetName, string pattern)
    {
        var dot = pattern.LastIndexOf('.');
        if (dot <= 0 || dot == pattern.Length - 1)
        {
            return true; // 声明里没写扩展名，就不拿它当筛子
        }

        return assetName.EndsWith(pattern[dot..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 多个候选时选一个：**与声明名字的公共前缀最长**的优先（最像原意），
    /// 平手时名字短的优先。全序、可复现，不看资源在 API 里的返回顺序。
    /// </summary>
    private static ReleaseAsset? Best(
        IReadOnlyList<ReleaseAsset> assets,
        Func<ReleaseAsset, bool> predicate,
        string pattern)
    {
        return assets
            .Where(predicate)
            .OrderByDescending(a => CommonPrefixLength(a.Name, pattern))
            .ThenBy(a => a.Name.Length)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var max = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < max && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
        {
            i++;
        }

        return i;
    }
}
