using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.Services;

/// <summary>
/// 一次资源选取的结果。
///
/// 除了「下哪个文件」，还必须说清「下完放到 out/ 的哪儿」—— 上游各发布包的内部结构并不统一
/// （见 <see cref="OutputTarget"/> 的注释），落点跟着资源走是最不容易写错的位置：
/// 写 picker 的人一定知道自己在拿什么，而下游的合并逻辑不需要认识任何具体文件名。
/// </summary>
/// <param name="Asset">上游 Release 里的资源。</param>
/// <param name="SaveAs">落盘时改用的文件名。</param>
/// <param name="Targets">
/// 该产物在 <c>out/</c> 里的落点。<c>null</c> / 空表示「放 <c>out/</c> 根目录、沿用原文件名」——
/// 大多数组件包内部本来就是 SD 卡根目录的样子，整棵铺开即正确。
/// </param>
/// <param name="IsPayload">
/// 是否为「散装 payload」（<c>fusee.bin</c>、<c>hekate_ctcaer_*.bin</c>）。
/// payload 单独暂存到 <c>download/&lt;组件&gt;/payload/</c>，由「把 payload 放到对应目录」
/// 这个开关单独控制，与「把组件文件合并进 out」互不影响。
/// </param>
/// <summary>
/// 这个下载物来自哪个「文件槽」，以及它是不是**猜**出来的。
///
/// 手写 picker 的组件（框架那四个）没有槽，<see cref="AssetPick.Slot"/> 就是 null；
/// 槽驱动的组件一律带上，调用方据此**逐槽对账**：哪个槽没出东西要点名报出来，
/// 而不是笼统地说一句「未找到匹配的资源文件」—— 后者在一个组件有多个槽时等于没说。
/// </summary>
/// <param name="SlotKey">槽名（组件内唯一）。</param>
/// <param name="Kind">命中的方式。非 <see cref="AssetMatchKind.Exact"/> 时要在日志里说明用了哪个文件。</param>
/// <param name="RequestedPattern">声明/用户填的名字，原样带回来。</param>
public sealed record SlotMatch(string SlotKey, AssetMatchKind Kind, string RequestedPattern)
{
    /// <summary>
    /// 这次命中是不是「**猜**」（语义与判据见 <see cref="AssetMatch.IsGuess"/>）。
    ///
    /// ⚠️ 与「非逐字」**不是**一回事：声明里本来就带 <c>x</c> 占位（<c>triplayer-x.x.x.zip</c>）
    /// 时按模式取到的是**如你所愿**。把这一族当猜报 Warning，会教用户去改掉他自己给的模式。
    /// </summary>
    public bool IsGuess => Kind.IsGuess();
}

/// <summary>
/// 一个产物的来源是**仓库文件树**（而不是某个 Release）。
///
/// 存在的理由：<see cref="SlotSource.RepoFile"/> 的槽没有 Release 可查、没有 asset id 可用，
/// 下载阶段需要知道「哪个仓库的哪个路径」才能拼出备用地址 —— 这些信息只有在解析时才知道
/// （用户可能改过地址），所以随 <see cref="AssetPick"/> 一起带下去。
/// </summary>
public sealed record RepoFileRef(RepoSpec Repo, string Path);

/// <summary>
/// 仓库某个目录下的一个文件（<see cref="SlotSource.RepoDirectory"/> 的槽列目录列出来的）。
///
/// 与 <see cref="RepoFileRef"/> 的区别只是「多带了一个名字」：目录槽的文件名是**列目录列出来的**，
/// 不是声明的，所以下载阶段必须把它一路带下去（落点、日志、暂存都按它命名）。
/// </summary>
/// <param name="Name">文件名（不含目录）。</param>
/// <param name="Path">仓库内的完整路径（<c>systemPatches/xxx.ips</c>）—— 备用通道要用它。</param>
/// <param name="Size">字节数。contents 端点会报，所以目录槽的进度条是准的。</param>
/// <param name="DownloadUrl">contents 端点给的 raw 直链。</param>
public sealed record RepoDirectoryFile(string Name, string Path, long Size, string DownloadUrl);

public sealed record AssetPick(
    ReleaseAsset Asset,
    string? SaveAs = null,
    IReadOnlyList<OutputTarget>? Targets = null,
    bool IsPayload = false)
{
    private static readonly IReadOnlyList<OutputTarget> OutRoot = [new OutputTarget(string.Empty)];

    /// <summary>来源槽（手写 picker 的组件为 null）。</summary>
    public SlotMatch? Slot { get; init; }

    /// <summary>非空表示来自仓库文件树（见 <see cref="SlotSource.RepoFile"/>）。</summary>
    public RepoFileRef? RepoFile { get; init; }

    /// <summary>解压时怎么处理包内路径（见 <see cref="Models.ExtractPlan"/>）。</summary>
    public ExtractPlan Extract { get; init; } = ExtractPlan.All;

    /// <summary>保存到磁盘时使用的文件名。</summary>
    public string FileName => string.IsNullOrWhiteSpace(SaveAs) ? Asset.Name : SaveAs!;

    /// <summary>
    /// 是不是「下完要解压」的压缩包（而不是原样复制的散装文件）。
    ///
    /// 判据**委托给 <see cref="ArchiveExtractor.IsArchive"/>**，不在这里再写一遍扩展名 ——
    /// 两处各写一份的话，将来支持一个新格式（比如又冒出个 .rar 插件）会变成
    /// 「解压器认得、这里不认得」，产物就是**一个原封不动的压缩包躺在 out 里**，
    /// 而日志一切正常。usb-botbase 的 .7z 正是把这条判据从「只有 zip」扩开的起因。
    /// </summary>
    public bool IsArchive => ArchiveExtractor.IsArchive(FileName);

    /// <summary>
    /// 落点。未显式声明时默认 <c>out/</c> 根目录 + 原文件名。
    ///
    /// ⚠️ <see cref="OutputTarget.AssetNameToken"/>（<c>{asset}</c>）在这里被替换成
    /// **下载文件名去掉扩展名** —— 替换**必须**发生在这里，因为这里是落点的唯一出口：
    /// <see cref="OutputStager"/> 的压缩包分支读 <c>ResolvedTargets[0].Directory</c>、
    /// 散装分支读 <c>OutputTarget.Resolve()</c>，两处都从本属性取。
    /// 放到调用点去替换的话，就是「两条分支各写一份」，迟早分叉（离线固件正是压缩包分支那个）。
    ///
    /// 判据是「替换后才有意义」：没有占位符时原样返回同一批对象（不为每个 pick 白造一遍）。
    /// </summary>
    public IReadOnlyList<OutputTarget> ResolvedTargets
    {
        get
        {
            if (Targets is not { Count: > 0 })
            {
                return OutRoot;
            }

            if (!Targets.Any(target => target.HasAssetNameToken))
            {
                return Targets;
            }

            var stem = System.IO.Path.GetFileNameWithoutExtension(FileName);
            return Targets.Select(target => target.ResolveAssetName(stem)).ToList();
        }
    }
}

/// <summary>组件对某个仓库的取用规则。</summary>
public sealed class RepoRequest
{
    public required RepoSpec Repo { get; init; }

    /// <summary>根据 Release 内容与用户选项决定要下载哪些文件。</summary>
    public required Func<ReleaseInfo, WizardOptions, IReadOnlyList<AssetPick>> Picker { get; init; }

    /// <summary>
    /// 这个请求负责哪些「文件槽」。手写 picker 的组件为空；
    /// 由 <see cref="ComponentCatalog.RepoRequestsFor"/> 现算出来的请求会带上，
    /// 下载阶段据此**逐槽对账**（见 <see cref="SlotMatch"/>）。
    /// </summary>
    public IReadOnlyList<AssetSlot> Slots { get; init; } = [];

    /// <summary>
    /// 这个请求要不要参与「按界面语言换仓库」（目前只有 hekate 有语言镜像）。
    ///
    /// 默认 <c>true</c> = 现有全部请求的行为，只有一处设成 <c>false</c>：
    /// <c>hekate</c> 里负责 8G payload 的那个请求。原因是**同一个组件同时要两个仓库** ——
    /// 取汉化包那一路要镜像、取 <c>__ram8GB.bin</c> 那一路必须官方（镜像仓库只发 zip）。
    ///
    /// ⚠️ 关掉的**只是「按语言换镜像」这一条规则**，<b>不是</b>整个
    /// <see cref="ComponentCatalog.ResolveRepo"/> —— 用户在「下载源」里给官方那一行填的地址
    /// 照旧生效（那一路由 <see cref="ComponentCatalog.ResolveRepo"/> 的
    /// <c>allowLanguageMirror</c> 参数表达，别退化成「跳过 ResolveRepo」）。
    ///
    /// ⚠️ 别想用「另声明一个 <c>RepoSpec</c> 字段」来区分：<c>RepoSpec</c> 是 record，
    /// 而 <see cref="ComponentCatalog.ResolveRepo"/> 判的是 <c>declared == HekateRepo</c>（比**值**），
    /// 另起一个 <c>("CTCaer","hekate")</c> 照样会被一并重定向到镜像去。
    /// </summary>
    public bool AllowLanguageMirror { get; init; } = true;

    /// <summary>
    /// 这次运行要不要**真的去查**这个请求（<c>null</c> = 总是查）。
    ///
    /// 存在的理由：有些请求只在特定选项下才有意义（hekate 的 8G payload 只在「开了 8G
    /// **且**包走了汉化镜像」时才有活干）。让它的 picker 返回空表也能跑，但代价是三样东西
    /// 都会静默出现 —— 一次白花的 API 请求、一条「未找到匹配的资源文件」假告警、
    /// 以及一个把同一个版本号加两遍的 <c>VersionText</c>（<c>v6.5.3 + v6.5.3</c>）。
    /// 所以判据放在**查询之前**，由 <see cref="ComponentCatalog.RepoRequestsFor"/> 过滤掉。
    /// </summary>
    public Func<WizardOptions, bool>? Applies { get; init; }
}

/// <summary>一个组件的完整定义：来源仓库 + 可配置项。</summary>
public sealed class ComponentDefinition
{
    public required ComponentKind Kind { get; init; }

    /// <summary>
    /// 界面上归到哪个**分类文件夹**里（见 <see cref="ComponentCategory"/> 的注释）。
    ///
    /// 刻意做成 <c>required</c>：漏填的话编译器直接拦下。写成可空 + 兜底「归到框架」也能跑，
    /// 但那正是最坏的结果 —— 一个新增的插件悄悄混进「框架」组，而「框架」是用户最不该被搞混的一组。
    /// </summary>
    public required ComponentCategory Category { get; init; }

    /// <summary>
    /// 插件名**直接显示的文本**。非空时优先于语言键 <c>Component.&lt;Kind&gt;.Name</c>。
    ///
    /// 用户 2026-09-18 的通用约定：**作者 zdm65477730 的插件若没有语言文件，插件名就显示默认的英文名**。
    /// 那类插件在三份语言包里都该是同一个英文名，与其往三份包里各抄一遍（抄错一份就出现
    /// 「切到繁体变成另一个名字」），不如在这里写一次字面量。
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>同 <see cref="DisplayName"/>，但用于说明文字。</summary>
    public string? DisplayDescription { get; init; }

    public required IReadOnlyList<RepoRequest> Repos { get; init; }

    public required IReadOnlyList<OptionDefinition> Options { get; init; }

    /// <summary>
    /// 「可下载文件槽」—— 声明这个组件要下哪些文件、各自默认从哪来、叫什么名字、落到 out/ 的哪儿。
    ///
    /// 与 <see cref="Repos"/> 是**二选一**的两种写法，别混用：
    /// <list type="bullet">
    ///   <item>框架那四个组件的取用规则复杂（hekate 按语言/8G 分流、Ultrahand 有多个仓库和条件），
    ///   继续用手写的 <see cref="Repos"/> + picker。</item>
    ///   <item>其余插件都是「一个仓库、一个文件、落到某个目录」，用 <see cref="Slots"/> 声明，
    ///   由 <see cref="ComponentCatalog.RepoRequestsFor"/> 现算出等价的请求 ——
    ///   好处是**每个文件的地址和文件名都自动变成界面上可改的一项**，不必为每个插件手写一遍。</item>
    /// </list>
    /// 两者同时非空时 <see cref="Slots"/> 优先（见 <see cref="ComponentCatalog.RepoRequestsFor"/>）。
    /// </summary>
    public IReadOnlyList<AssetSlot> Slots { get; init; } = [];
}

/// <summary>组件目录：集中声明四个组件的来源与全部配置项。</summary>
public static class ComponentCatalog
{
    /// <summary>
    /// Homebrew Menu 的 title id（<c>010000000000100D</c>）。
    ///
    /// 它是 <c>override_config.ini</c> 里 <c>[hbl_config] program_id</c> 的**官方模板取值**，
    /// 也就是那个选项的默认值；同时还是「用户填的值不像 title id 时」的回落目标。
    /// 两处共用同一个字面量，别各写一份。
    /// </summary>
    public const string HblProgramIdDefault = "010000000000100D";

    /// <summary>同 <see cref="HblProgramIdDefault"/>，但作为 <c>[hbl_config] path</c> 的默认值。</summary>
    public const string HblPathDefault = "atmosphere/hbl.nsp";

    // ── 仓库地址 ────────────────────────────────────────────────
    public static readonly RepoSpec AtmosphereRepo = new("Atmosphere-NX", "Atmosphere");
    public static readonly RepoSpec HekateRepo = new("CTCaer", "hekate");
    public static readonly RepoSpec UltrahandRepo = new("ppkantorski", "Ultrahand-Overlay");
    public static readonly RepoSpec OvlSysmodulesRepo = new("ppkantorski", "ovl-sysmodules");
    public static readonly RepoSpec NxOvlloaderRepo = new("ppkantorski", "nx-ovlloader");
    public static readonly RepoSpec SysPatchRepo = new("impeeza", "sys-patch");

    /// <summary>
    /// <c>THZoria/NX_Firmware</c> —— 离线固件包（用户 2026-09-18 指定）。
    ///
    /// **实测（2026-09-18，92 个版本逐个数过）**：资源名一律是 <c>Firmware.&lt;版本&gt;.zip</c>
    /// —— **用点分隔，从来没有空格**（例如 <c>Firmware.23.0.0.zip</c>，340 MB）；
    /// 个别版本带后缀（<c>Firmware.19.0.1.Rebootless.zip</c>），挂在各自的 <c>r</c> tag 上，
    /// 与正式版不是同一个 Release。
    ///
    /// 默认文件名因此声明成**模式** <c>Firmware.x.x.x.zip</c>（<c>x</c> 是版本占位，
    /// 见 <see cref="AssetNameMatcher"/> 的第二级瀑布）—— 写死一个具体版本号的话，
    /// 上游一发新版默认值就作废，用户每次都得手改。
    /// </summary>
    public static readonly RepoSpec FirmwareRepo = new("THZoria", "NX_Firmware");

    // ── 「组件」类插件的仓库 ────────────────────────────────────
    //
    // ⚠️ 这批仓库**不出现在界面上的「下载源」那一栏**，它们的地址在各自插件的文件槽里改
    //    （见 AssetSlot）。回归里的 CheckRepoSources 会保证「声明的仓库」与
    //    「界面上改得动的仓库」两份清单仍然对得上 —— 加了仓库却没有任何入口改它，会当场报错。
    //
    // ⚠️ 选 zdm65477730 的 fork 而不是上游，是**用户指定**的：他的 fork 会跟上新固件、
    //    且资源名与用户清单一致（例如上游 sys-clk 叫 sys-clk-2.0.1-21fix.zip，他的叫 sys-clk.zip）。
    //    别「顺手改回上游」—— 那会让资源名匹配不上，用户得手工改一遍地址。
    public static readonly RepoSpec BreezeRepo = new("tomvita", "Breeze-Beta");
    public static readonly RepoSpec DbiRepo = new("rashevskyv", "DBIPatcher");
    public static readonly RepoSpec EdiZonOverlayRepo = new("zdm65477730", "EdiZon-Overlay");
    public static readonly RepoSpec GoldleafRepo = new("XorTroll", "Goldleaf");
    public static readonly RepoSpec NxdumptoolRepo = new("DarkMatterCore", "nxdumptool");
    public static readonly RepoSpec NxShellRepo = new("zdm65477730", "NX-Shell");
    public static readonly RepoSpec FtpdRepo = new("mtheall", "ftpd");
    public static readonly RepoSpec Haku33Repo = new("StarDustCFW", "Haku33");
    public static readonly RepoSpec JksvRepo = new("zdm65477730", "JKSV");
    public static readonly RepoSpec LunaAppRepo = new("Ixaruz", "Luna-App");
    public static readonly RepoSpec SimpleModAlchemistRepo = new("gtiersma", "Simple_Mod_Alchemist");
    public static readonly RepoSpec SysClkRepo = new("zdm65477730", "sys-clk");
    public static readonly RepoSpec SysDvrRepo = new("exelix11", "SysDVR");
    public static readonly RepoSpec LdnMitmRepo = new("Lusamine", "ldn_mitm");
    public static readonly RepoSpec SphairaRepo = new("NaGaa95", "sphaira");
    public static readonly RepoSpec FizeauRepo = new("zdm65477730", "Fizeau");
    public static readonly RepoSpec NxActivityLogRepo = new("zdm65477730", "NX-Activity-Log");
    public static readonly RepoSpec SwitchFirmwareDumperRepo = new("mrdude2478", "Switch-Firmware-Dumper");
    public static readonly RepoSpec BatteryDesyncFixRepo = new("CTCaer", "battery_desync_fix_nx");

    // ── 后台 ────────────────────────────────────────────────────────
    // ⚠️ sys-botbase 与 usb-botbase 装的是**同一个 title**（430000000000000B），
    //    只能二选一 —— 这不是「建议」，是实测出来的硬冲突（见 SysmoduleTitleId）。
    public static readonly RepoSpec SysBotbaseRepo = new("olliz0r", "sys-botbase");
    public static readonly RepoSpec UsbBotbaseRepo = new("Koi-3088", "usb-botbase");
    public static readonly RepoSpec MissionControlRepo = new("ndeadly", "MissionControl");
    public static readonly RepoSpec SysConRepo = new("o0Zz", "sys-con");
    public static readonly RepoSpec SysFtpdRepo = new("ELY3M", "sys-ftpd");

    // ── 主题 ────────────────────────────────────────────────────────
    public static readonly RepoSpec SwitchThemeInjectorRepo = new("exelix11", "SwitchThemeInjector");

    /// <summary>
    /// <c>exelix11/theme-patches</c> —— **不是一个 Release 仓库**，是一个「补丁文件目录」仓库。
    ///
    /// 用户要求：「另取 <c>exelix11/theme-patches</c> 的 <c>tree/master/systemPatches</c> 下
    /// **所有文件（不要文件夹）**」→ 落点 <c>out/themes/systemPatches</c>，与
    /// <c>NXThemesInstaller.nro</c> **同框绑定**（同一个组件、同一个勾选框）。
    ///
    /// 实测（2026-09-18）：默认分支 <c>master</c>，<c>systemPatches/</c> 下 **20 个扁平
    /// <c>.ips</c>**（19–24 字节），没有子目录 —— 所以走
    /// <see cref="SlotSource.RepoDirectory"/> 列目录逐个下，而不是下一个压缩包再筛。
    /// </summary>
    public static readonly RepoSpec ThemePatchesRepo = new("exelix11", "theme-patches");

    public static readonly RepoSpec AvatoolRepo = new("J-D-K", "Avatool");
    public static readonly RepoSpec SysTiconRepo = new("masagrator", "sys-ticon");

    // ── 底层 ────────────────────────────────────────────────────────
    // 两个散装 payload，落 out/bootloader/payloads —— 与 hekate 自带的 payloads/ 同一个目录。
    //
    // ⚠️ **刻意不标 IsPayload**。IsPayload 的语义是「散装下载的**引导** payload
    //    （fusee.bin / hekate_ctcaer_*.bin）」，它会被「把 payload 放到对应目录」这个
    //    开关**整个跳过**（OutputStager / MainViewModel 两处）。这两个是**普通组件**：
    //    用户勾了「Lockpick_RCM」就是想要这个文件，不该再有第二道隐藏闸门 ——
    //    否则勾了组件、没勾那个开关时会**静默什么都不产出**。
    public static readonly RepoSpec LockpickRcmRepo = new("zdm65477730", "Lockpick_RCMDecScots");
    public static readonly RepoSpec TegraExplorerRepo = new("zdm65477730", "TegraExplorer");

    // ── Ultrahand插件 ───────────────────────────────────────────────
    // Ultrahand 生态的 Tesla 覆盖层（<c>.ovl</c>）：装到 <c>switch/.overlays/</c> 后由 Ultrahand 呼出。
    //
    // 六个包的落点都是 **out 根**，因为包内路径已经以 SD 卡根为基准
    // （实测：emuiibo.zip 里是 <c>atmosphere/contents/…</c> + <c>switch/.overlays/…</c>，
    //  Zing/QuickNTP 是 <c>switch/.overlays/…</c> + <c>config/&lt;名字&gt;/…</c>，
    //  没有一层多余的包装目录）—— 整棵铺开即可，不需要 ExtractPlan。
    //
    // 上游实测（2026-09-18，逐字，来自各自 Release 的 assets）：
    //   zdm65477730/emuiibo                 v1.1.3 → emuiibo.zip        （628961 字节）
    //   zdm65477730/Zing                    v0.5.0 → Zing.zip           （412260 字节）
    //   zdm65477730/ReverseNX-RT            v2.2.1 → ReverseNX-RT.zip   （2359349 字节）
    //   zdm65477730/Status-Monitor-Overlay  v1.3.2 → StatusMonitor.zip  （421495 字节）
    //   zdm65477730/QuickNTP                1.6.0  → QuickNTP.zip       （395220 字节）
    //   TOM-BadEN/KeyX                      v1.5.6 → KeyX-CN.zip（991665 字节）/ KeyX-EN.zip（991658 字节）
    //
    // ⚠️ 上面这段「逐字清单」不是给人读的散文 —— **`tools/check-upstream-facts.py` 会把它解析出来**，
    //    联网逐个核对「资源名是否逐字存在 / 字节数是否一致 / 包内有没有多一层包装目录」。
    //    所以改这里就要跑一遍那个脚本；格式也别乱动（它认的是 `仓库  版本 → 文件名（N 字节）`）。
    //
    // ⚠️ **仓库名与文件名不一样的两处，是上游的命名，不要「顺手对齐」**：
    //    Status-Monitor-Overlay → StatusMonitor.zip（少了连字符与 Overlay）。
    //    「看起来该一致就改成一致」会让逐字命中失效、静默落进容错瀑布。
    //
    // ⚠️ emuiibo 的 Release 里还有一个 <c>emuiigen-jar-with-dependencies.jar</c>（13.7 MB 的 PC 端工具），
    //    与 SD 卡内容无关 —— 逐字命中 <c>emuiibo.zip</c> 时不受影响，但**它正是容错瀑布的诱饵**：
    //    若上游哪天把 zip 改名消失，「同扩展名唯一候选」那级会去看 .zip（jar 扩展名不同，不会被选中），
    //    所以这里没有「拿到 jar 当 SD 内容」的风险。
    public static readonly RepoSpec EmuiiboRepo = new("zdm65477730", "emuiibo");
    public static readonly RepoSpec ZingRepo = new("zdm65477730", "Zing");
    public static readonly RepoSpec ReverseNxRtRepo = new("zdm65477730", "ReverseNX-RT");
    public static readonly RepoSpec StatusMonitorOverlayRepo = new("zdm65477730", "Status-Monitor-Overlay");
    public static readonly RepoSpec QuickNtpRepo = new("zdm65477730", "QuickNTP");
    public static readonly RepoSpec KeyXRepo = new("TOM-BadEN", "KeyX");

    // ── 学习 ────────────────────────────────────────────────────────
    // 与 CFW 本身无关的附加软件：安装器、媒体播放、游戏串流。落点分两类 ——
    // 整包铺 out 根（安装器类，包内已是 SD 根），或单个 .nro 进 switch/<名字>/。
    //
    // 上游实测（2026-09-18，逐字，来自各自 Release 的 assets）：
    //   dezem/AtmoXL-Titel-Installer       1.7.3  → AtmoXL-Titel-Installer.zip（5540947 字节）
    //   Huntereb/Awoo-Installer            1.3.6  → Awoo-Installer.zip      （6307394 字节）
    //   SwitchScriptTW/linkalho            2.0.2  → linkalho.zip            （3184785 字节）
    //   impeeza/linkalho                   v2.0.2 → linkalho-v2.0.2.zip    （3156396 字节）
    //   meganukebmp/Switch_90DNS_tester    v1.1.0 → Switch_90DNS_tester.nro（255950 字节）
    //   xfangfang/wiliwili                 v1.6.0 → wiliwili-NintendoSwitch.zip（18815897 字节）
    //   XITRIX/Moonlight-Switch            v1.5.0 → Moonlight-Switch.nro   （17677907 字节）
    //   tallbl0nde/TriPlayer               v1.1.1 → triplayer-1.1.1.zip    （7092194 字节）
    //
    // ⚠️ 与 Ultrahand 那组一样，这段「逐字清单」由 `tools/check-upstream-facts.py` 解析并联网核对。
    //
    // ⚠️ **linkalho 是唯一「按界面语言换仓库」的槽**（用户 2026-09-18 明确要求）：
    //    中文界面走 <c>SwitchScriptTW/linkalho</c>（汉化镜像），其余走上游 <c>impeeza/linkalho</c>。
    //    两个仓库**都真实存在、都在发版**（实测各 1 个资源），所以这是**用户意图**不是容错 ——
    //    与 KeyX 那两个包同一性质（见 KeyXFileName）。两个仓库名也各不相同 ⇒ 文件名也不同，
    //    地址与文件名**必须一起按语言求值**。
    //
    // ⚠️ 注意上面「上游实测」那两行与下面的**声明**不是一回事，别把它们改齐：
    //    那两行记的是**上游此刻真实存在的资源名**（`linkalho.zip` / `linkalho-v2.0.2.zip`，
    //    带具体版本号），是核对脚本要拿去找的依据；而声明里 EN 侧写的是**用户给的模式**
    //    `linkalho-x.x.x.zip` —— 上游一发新版，具体版本号就过期，模式不会。
    //    **把声明「具体化」成实测到的版本号，等于每次上游发版都让逐字命中失效一次。**
    public static readonly RepoSpec AtmoXlRepo = new("dezem", "AtmoXL-Titel-Installer");
    public static readonly RepoSpec AwooRepo = new("Huntereb", "Awoo-Installer");
    public static readonly RepoSpec LinkalhoCnRepo = new("SwitchScriptTW", "linkalho");
    public static readonly RepoSpec LinkalhoEnRepo = new("impeeza", "linkalho");
    public static readonly RepoSpec Dns90TesterRepo = new("meganukebmp", "Switch_90DNS_tester");
    public static readonly RepoSpec WiliwiliRepo = new("xfangfang", "wiliwili");
    public static readonly RepoSpec MoonlightRepo = new("XITRIX", "Moonlight-Switch");
    public static readonly RepoSpec TriPlayerRepo = new("tallbl0nde", "TriPlayer");

    /// <summary>
    /// linkalho 按界面语言取哪个仓库：中文（简繁都算）走汉化镜像 <c>SwitchScriptTW/linkalho</c>，
    /// 其余走上游 <c>impeeza/linkalho</c>。
    ///
    /// ⚠️ 与 <see cref="LinkalhoFileName"/> **必须成对**：两个仓库的文件名不一样
    /// （CN 是逐字的 <c>linkalho.zip</c>，EN 是模式 <c>linkalho-x.x.x.zip</c>），只切其中一个会让
    /// 名字与仓库对不上、静默落进容错瀑布「猜」一个（能跑通，但日志里会留一条本不该有的记录）。
    /// 所以两处共用同一个判据函数，不各写一遍 <c>IsChinese</c>。
    /// </summary>
    public static RepoSpec LinkalhoRepo(string? language) =>
        IsChinese(language) ? LinkalhoCnRepo : LinkalhoEnRepo;

    /// <summary>
    /// linkalho 按界面语言取哪个包名（与 <see cref="LinkalhoRepo"/> 成对，见其说明）。
    ///
    /// EN 侧刻意保留**用户清单里的模式写法** <c>linkalho-x.x.x.zip</c>（而不是实测到的
    /// <c>linkalho-v2.0.2.zip</c>）：用户给的是「跟着版本走的模式」，写成具体版本号会让
    /// **每次上游发版都让逐字命中失效一次**、白留一条降级记录。模式与实测版本号不冲突 ——
    /// 上面「上游实测」那两行仍然记着具体版本，供 <c>tools/check-upstream-facts.py</c> 核对。
    /// </summary>
    public static string LinkalhoFileName(string? language) =>
        IsChinese(language) ? "linkalho.zip" : "linkalho-x.x.x.zip";

    /// <summary>
    /// hekate 的**中文本地化**镜像：<c>easyworld/hekate</c>。
    ///
    /// 它把 Nyx（hekate 的图形界面）汉化后重新打包，同一版本提供两个资源：
    /// <c>…_sc.zip</c>（简体）与 <c>…_tc.zip</c>（繁体）。包里结构与官方一致
    /// （<c>bootloader/**</c> + 根目录 <c>hekate_ctcaer_&lt;ver&gt;.bin</c>），所以整棵铺开即可。
    ///
    /// ⚠️ 它**只发 zip，没有独立的 <c>.bin</c> 资源**（官方那边是把 payload 单独挂一份的）。
    /// 所以走这个源时 picker 里的 <c>standardBin</c> / <c>ram8GbBin</c> 都会是 null ——
    /// 这不是 bug，payload 在 zip 里面（见 <c>ConfigGenerator</c> 的 payload.bin 兜底步骤）。
    /// </summary>
    public static readonly RepoSpec EasyWorldHekateRepo = new("easyworld", "hekate");

    /// <summary>
    /// 界面上「下载源」一栏要列出的仓库。
    ///
    /// <c>Key</c> 用默认地址（<c>owner/name</c>）—— 它同时是
    /// <see cref="Models.WizardOptions.RepoOverrides"/> 的键，**改动默认地址会让用户已存的自定义值失配**，
    /// 所以这里只增不改。<c>Name</c> 是插件名（三个语言下写法相同，不走多语言表）。
    ///
    /// hekate **占两行**（官方 + 中文镜像），因为「这次用哪个」原本是跟着界面语言自动切的、
    /// 界面上完全看不见。拆成两行后用户能明确指定，两行也都真的能生效 ——
    /// 见 <see cref="ResolveRepo"/>。
    /// </summary>
    public sealed record RepoSource(string Key, string Name, string? HintKey = null);

    public static readonly IReadOnlyList<RepoSource> RepoSources =
    [
        new(AtmosphereRepo.DisplayName, "Atmosphere"),
        new(HekateRepo.DisplayName, "Hekate", "Settings.Repo.Hekate.Hint"),
        new(EasyWorldHekateRepo.DisplayName, "Hekate (easyworld)", "Settings.Repo.Hekate.EasyWorld.Hint"),
        new(UltrahandRepo.DisplayName, "Ultrahand"),
        new(OvlSysmodulesRepo.DisplayName, "ovl-sysmodules"),
        new(NxOvlloaderRepo.DisplayName, "nx-ovlloader"),
        new(SysPatchRepo.DisplayName, "Sys-patch"),
    ];

    /// <summary>
    /// 界面上列出的每一行对应的「声明仓库」。行的 <c>Key</c> 就是默认地址，所以按显示名反查。
    ///
    /// 存在的意义是让护栏能**逐行**验证可达性（<c>CheckRepoSources</c>）：少写一个仓库、
    /// 或写了却没人读它的自定义值，都会在这里暴露出来。
    ///
    /// <c>Declared</c> 可空是刻意的：反查不到说明这一行是**孤儿**（写了名字却没有对应的仓库声明），
    /// 属于要报错的配置错误，不能悄悄丢掉。
    /// </summary>
    public static IReadOnlyList<(RepoSource Row, RepoSpec? Declared)> RepoRows =>
        RepoSources
            .Select(row => (Row: row, Declared: DeclaredRepos.FirstOrDefault(r => r.DisplayName == row.Key)))
            .ToList();

    /// <summary>
    /// 目录里声明过的全部仓库。**用反射从字段取**，不手抄清单 ——
    /// 手抄的清单自己会腐烂：新增一个仓库字段却忘了加进清单，护栏就看不见它，静默漏掉一个源。
    ///
    /// 写成属性而不是字段，是为了绕开静态字段的初始化顺序（反射读到还没赋值的字段会得到 null）。
    /// </summary>
    public static IReadOnlyList<RepoSpec> DeclaredRepos =>
        typeof(ComponentCatalog)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(RepoSpec))
            .Select(f => (RepoSpec)f.GetValue(null)!)
            .ToList();

    /// <summary>界面语言是不是中文（简体 / 繁体都算）。</summary>
    public static bool IsChinese(string? language) =>
        !string.IsNullOrWhiteSpace(language)
        && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// DBI 的翻译文件按界面语言取哪一份。
    ///
    /// <c>rashevskyv/DBIPatcher</c> 一个 Release 里挂了 20 多份 <c>translation_*.bin</c>，
    /// 而 DBI 只认与自己同目录的 <c>translation.bin</c> —— 所以「取哪份」和「叫什么」是两件事：
    /// 前者由本方法决定（跟随界面语言），后者固定（见那个槽的 <c>SaveAs</c>）。
    ///
    /// ⚠️ 繁体判在前面：<c>zh-Hant</c> 也以 <c>zh</c> 开头，顺序反了会一律落到简体
    /// （与 <c>FindLocalizedHekateZip</c> 同一个坑）。
    /// </summary>
    public static string DbiTranslationFileName(string? language) =>
        language is not null && language.Contains("Hant", StringComparison.OrdinalIgnoreCase)
            ? "translation_zhtw.bin"
            : IsChinese(language)
                ? "translation_zhcn.bin"
                : "translation_en.bin";

    /// <summary>
    /// KeyX 的覆盖层包按界面语言取哪一份：<c>KeyX-CN.zip</c> / <c>KeyX-EN.zip</c>。
    ///
    /// ⚠️ 与 DBI 那三份翻译文件**不是**同一类问题，别照着它的注释理解：
    /// 那两个包在 Release 里**同时存在、都真实可用**（实测 v1.5.6：CN 991665 字节 / EN 991658 字节，
    /// 只差 7 字节）。所以「取哪个」不是容错瀑布要解决的「名字对不上」，而是**用户意图** ——
    /// 判据只能是界面语言，不能靠猜。逐字命中永远成功，不会留痕到容错日志里。
    ///
    /// 中文（简繁都算）取 CN 包。**差在哪，逐条目量过**（2026-09-18，两包各 9 个条目）：
    /// 条目名集合完全相同，其中 8 个**逐字节相同** —— 含 <c>switch/.overlays/lang/KeyX/</c> 下的
    /// <c>de.json</c> / <c>en.json</c> / <c>ja.json</c> / <c>zh-tw.json</c>，两包一模一样；
    /// 唯一差异在 <c>switch/.overlays/ovl-KeyX.ovl</c> 里的 144 个字节 —— 那是覆盖层内嵌的显示名，
    /// CN 版是中文（<c>按键助手</c>），EN 版是 <c>KeyX</c>。
    /// 所以取哪个**只影响呼出菜单里那一行字**，与语言包无关；中文界面取 CN 只是为了那行字。
    ///
    /// ⚠️ 这条注释原先写的是「EN 包则只有英文那一份 / CN 包里的英文不如 EN 包完整」——**是编的**。
    /// 字节数是量过的，理由却是顺着包名推出来的，于是理由借了数字的可信度一起蒙混过关。
    /// **凡写「实测」，当场留下可复核的依据（数字 / 命令 / 对比方式），否则就写「未实测」。**
    /// </summary>
    public static string KeyXFileName(string? language) =>
        IsChinese(language) ? "KeyX-CN.zip" : "KeyX-EN.zip";

    /// <summary>
    /// 一个资源名 / 文件名是不是 8G 版 hekate payload。
    ///
    /// **唯一**的判据，三处共用：① picker 挑 payload；② 勾了 8G 却没拿到 8G payload 时告警
    /// （<see cref="IsRam8GbPayloadMissing"/>）；③ <c>ConfigGenerator</c> 挑「根目录 payload.bin 的来源」
    /// 与「8G 模式下要删掉的标准版」。
    ///
    /// 各写一遍字面量的话，上游改了命名规则就会**悄悄脱节**：一处认、一处不认，而两边都不报错 ——
    /// 8G 用户会拿到 4G 的 <c>payload.bin</c>，或者 8G 版 payload 被当成标准版删掉。
    /// 有护栏钉住「带引号的字面量在整个 src 里只能出现一次」（<c>CheckRam8GbPayloadWarning</c> ②）。
    /// </summary>
    public static bool IsRam8GbPayloadName(string fileName) =>
        fileName.Contains("ram8GB", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 一个资源名是不是 Ultrahand 的**语言包** zip。
    ///
    /// **唯一**的判据，两处共用：① <c>installLang</c> 勾了时挑语言包；
    /// ② <c>sdout.zip</c> 那条回退**必须排除它**。
    ///
    /// ② 不是洁癖：上游 Release 的 <c>assets</c> 是**按名字排序**返回的，而
    /// <c>lang.zip</c> 排在 <c>sdout.zip</c> **前面**（实测 v2.5.3 的顺序就是
    /// <c>lang.zip</c> → <c>ovlmenu.ovl</c> → <c>sdout.zip</c>）。所以「<c>sdout.zip</c>
    /// 找不到就随便拿一个 zip」拿到的是 <c>lang.zip</c> —— 14 个语言 json 被当成整套 SD 内容
    /// 铺到 <c>out/</c> 根目录，而 Ultrahand 本体一个文件都没有。
    ///
    /// 这个状态**真实可达**：Release 刚发布时资源是**逐个上传**的，那一刻 <c>sdout.zip</c>
    /// 可能还没传上去（GitHub 在资源传完前就已经把它标成 latest）。
    /// 而且它**不会被任何检查抓到**：语言包合并进来照样让「已合并 N 个组件文件」> 0，
    /// 逐组件对账也过 —— 用户拿到一张缺 Ultrahand 的 SD 卡，日志里一片祥和。
    /// </summary>
    public static bool IsUltrahandLangZipName(string fileName) =>
        fileName.Contains("lang", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 勾了 8G 运存，但这个 Release 里**没有** 8G 版 payload —— 调用方据此告警（用户 2026-09-17 决定）。
    ///
    /// 为什么需要它：picker 里 `ram8GbBin ?? standardBin` 那条回退是**静默**的，于是产物会呈现
    /// 「配置说 8G（`enable_mem_mode=1` + 引导项 `memmode=1`）、payload 却是 4G 版」——
    /// 文件看着完全合法，机器却按 4G 起。判据与 picker 共用 <see cref="IsRam8GbPayloadName"/>，
    /// 不会产生第二份真源。
    ///
    /// 只在「这个 Release 确实带 payload（有 <c>.bin</c>）」时才认为是异常 ——
    /// 一个 payload 都没有属于另一种情况，由别处的告警覆盖。
    /// </summary>
    public static bool IsRam8GbPayloadMissing(ReleaseInfo release, WizardOptions options)
    {
        if (!options.Ram8Gb)
        {
            return false;
        }

        // 只看 **hekate 自己的 payload**（`hekate_*.bin`）。不能只看 ".bin" 后缀：
        // Atmosphere 的 `fusee.bin` 也是 .bin，而它永远不会有 8G 版 —— 于是勾了 8G 的用户会在
        // **每个组件**上都看到一条「没有 __ram8GB.bin」的假告警（2026-09-18 实测到的误报，
        // 日志里 Atmosphere 那一行就是这么来的）。假告警多了，真告警就没人看了。
        var bins = release.Assets
            .Where(a => a.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                        && a.Name.StartsWith("hekate_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return bins.Count > 0 && !bins.Any(a => IsRam8GbPayloadName(a.Name));
    }

    /// <summary>
    /// 引导项显示名的**唯一**解析规则：用户改过就用用户的，否则用界面语言的默认名。
    ///
    /// 界面候选项（<c>MainViewModel.BootEntryName</c>）与生成的 <c>hekate_ipl.ini</c>
    /// （<c>ConfigGenerator</c>）都必须走这里。两处只要有一处漏掉自定义名，
    /// 「autoboot 指向第几个引导项」的跨层护栏就会失效 —— 界面显示 A、文件里是 B。
    /// </summary>
    public static string ResolveBootTitle(string? custom, string fallbackKey)
        => string.IsNullOrWhiteSpace(custom)
            ? Localization.LocalizationService.Instance[fallbackKey]
            : custom.Trim();

    /// <summary>
    /// 这次的 hekate **包**（<c>bootloader/**</c> + Nyx）要不要从中文本地化镜像取。
    ///
    /// 判据只有一条：**界面语言是中文**。用户 2026-09-18 明确要求 ——
    /// 界面是简体中文就该拿到中文 Nyx，**跟机器是不是 8G 运存无关**。
    ///
    /// ⚠️ 这里判的只是「包」这一半。8G 时**不能**靠它一个请求凑齐整套：
    /// 镜像仓库只发 <c>_sc.zip</c> / <c>_tc.zip</c>，里面**没有** <c>__ram8GB.bin</c>。
    /// 所以 8G 由 hekate 的**两个**请求拼起来 ——
    /// 这一个（<see cref="AllowLanguageMirror"/>=true）取汉化包，
    /// 另一个（<c>false</c>）从官方取 8G payload 与标准 payload 备用。
    ///
    /// ⚠️ 旧判据是 <c>IsChinese(...) &amp;&amp; !options.Ram8Gb</c>：8G 用户无论界面语言
    /// 拿到的都是**英文 Nyx**（日志里那一行还是 <c>CTCaer/hekate</c>）——
    /// 那正是用户报的「软件界面是简体中文，Hekate 却没从 easyworld/hekate 下载」。
    /// </summary>
    public static bool ShouldUseLocalizedHekate(WizardOptions options) =>
        IsChinese(options.Language);

    /// <summary>
    /// 求出某个仓库这次实际要用哪个地址。优先级：
    /// 用户改过的（<see cref="WizardOptions.RepoOverrides"/>） &gt; hekate 的中文本地化默认 &gt; 声明时的默认地址。
    ///
    /// 用户填的地址解析不出来时**回落到默认地址**，不抛异常 —— 界面上已经就地标红提示了，
    /// 这里再炸一次只会让「点开始」直接失败，反而更难排查。
    ///
    /// hekate 特殊：界面上它占**两行**（官方 / 中文镜像），两行都可能有用户填的地址。
    /// 规则是「**任一行填了自定义地址即生效**」，两行都填时以官方行为准 —— 顺序固定、不猜。
    /// 两行都没填才回落到「中文界面用镜像」的老规矩。
    ///
    /// ⚠️ 这个「两行都读」是本方法存在的**唯一**理由：只读官方那一行的话，
    /// 镜像行就成了「能改但不起作用」的死控件（改完界面上一切正常、下载时纹丝不动）。
    /// </summary>
    /// <param name="allowLanguageMirror">
    /// 要不要应用「按界面语言换仓库」这条规则。默认 <c>true</c>；只有 hekate 里
    /// 负责 8G payload 的那个请求传 <c>false</c>（见 <see cref="RepoRequest.AllowLanguageMirror"/>）。
    ///
    /// ⚠️ 关掉的**只是镜像那一条**：用户自己填的地址（上面第 ① 条规则）照旧生效 ——
    /// 这是把它做成参数而不是「调用方跳过整个方法」的原因。跳过方法的话，用户给官方行
    /// 填了个能用的镜像地址，8G 那一路却还是硬走 <c>CTCaer/hekate</c>，
    /// 而这在界面上完全看不出来。
    /// </param>
    public static RepoSpec ResolveRepo(RepoSpec declared, WizardOptions options, bool allowLanguageMirror = true)
    {
        if (TryOverride(declared.DisplayName, declared, options, out var custom))
        {
            return custom;
        }

        if (allowLanguageMirror && declared == HekateRepo)
        {
            // 官方行没填，但用户可能填的是镜像行 —— 那也算数（他明确要的就是那个地址）。
            //
            // ⚠️ 这一条也要受 allowLanguageMirror 管：镜像行就是个「语言镜像」入口，
            //    取 8G payload 的那一路要的是**官方**仓库，不该被它改道。
            if (TryOverride(EasyWorldHekateRepo.DisplayName, EasyWorldHekateRepo, options, out var mirror))
            {
                return mirror;
            }

            if (ShouldUseLocalizedHekate(options))
            {
                return EasyWorldHekateRepo;
            }
        }

        return declared;
    }

    /// <summary>
    /// 取某一行上用户填的自定义地址。没填、填了但解析不出来、或填的就是该行默认值时返回 <c>false</c>
    /// （后两种都按「没改」处理，与界面上的 <c>HasCustomValue</c> 判定保持一致）。
    /// </summary>
    private static bool TryOverride(string key, RepoSpec declared, WizardOptions options, out RepoSpec resolved)
    {
        resolved = declared;

        if (!options.RepoOverrides.TryGetValue(key, out var custom))
        {
            return false;
        }

        if (RepoSpec.TryParse(custom) is not { } parsed || parsed.Equals(declared))
        {
            return false;
        }

        resolved = parsed;
        return true;
    }

    // ── 文件槽（「下载地址 / 文件名」可改的那批插件）────────────────

    /// <summary>
    /// 一个槽在 <c>settings.json</c> 里的键：<c>&lt;组件&gt;/&lt;槽 Key&gt;</c>。
    ///
    /// 与 <see cref="Models.WizardOptions.Values"/> 的键同一套命名（那里也是「组件/键」），
    /// 所以「哪个组件的哪个项」在存档里一眼能看出来。规则只写在这一处，界面、存档、解析三边共用。
    /// </summary>
    public static string SlotSettingKey(ComponentDefinition definition, AssetSlot slot) =>
        definition.Kind + "/" + slot.Key;

    /// <summary>
    /// 这个槽这次实际用哪个地址。用户填过（且不是空白）就用用户的，否则用声明的默认值。
    ///
    /// ⚠️ 与 <see cref="ResolveRepo"/> 的 <c>TryOverride</c> **刻意不同**：那里「填了但解析不出来」
    /// 按「没改」处理，因为框架组件每一行都有默认值兜着、且界面上会标红。
    /// 这里同样回落，但**不在解析层报错** —— 解析不出来时由 <see cref="ResolveSlotRepo"/> 兜底，
    /// 界面上另有 <c>IsAddressInvalid</c> 就地标红。三层各司其职，别把报错挤到同一层。
    ///
    /// 默认值是按**界面语言**求值的（linkalho 中文界面走 <c>SwitchScriptTW</c> 那个镜像），
    /// 与 <see cref="ResolveSlotFileName"/> 同一套约定 —— 切语言时占位符跟着变，用户看得见。
    /// </summary>
    public static string ResolveSlotAddress(ComponentDefinition definition, AssetSlot slot, WizardOptions options) =>
        options.AssetAddresses.TryGetValue(SlotSettingKey(definition, slot), out var custom)
        && !string.IsNullOrWhiteSpace(custom)
            ? custom.Trim()
            : slot.Address(options.Language).DisplayName;

    /// <summary>
    /// 这个槽这次实际用哪个文件名。用户填过就用用户的，否则问槽要默认值。
    ///
    /// 默认值是按**界面语言**求值的（见 <see cref="AssetSlot.FileName"/>），
    /// 所以「跟随界面语言」这件事对用户是看得见的 —— 切语言时占位符会跟着变。
    /// </summary>
    public static string ResolveSlotFileName(ComponentDefinition definition, AssetSlot slot, WizardOptions options) =>
        options.AssetFileNames.TryGetValue(SlotSettingKey(definition, slot), out var custom)
        && !string.IsNullOrWhiteSpace(custom)
            ? custom.Trim()
            : slot.FileName(options.Language);

    /// <summary>
    /// 这个槽这次实际从哪个仓库取。
    ///
    /// 用户填的地址解析不出来时**回落到声明的默认地址**：界面上已经就地标红了，
    /// 这里再抛一次只会让「点开始」直接失败，反而更难排查（与 <see cref="ResolveRepo"/> 同一套理由）。
    /// 声明的默认地址本身也解析不出来才是真的配置错误 —— 那是护栏该拦的，不是运行期该忍的。
    /// </summary>
    public static RepoSpec ResolveSlotRepo(ComponentDefinition definition, AssetSlot slot, WizardOptions options)
    {
        var address = ResolveSlotAddress(definition, slot, options);

        // 声明值本身已经是 RepoSpec，所以这里不可能返回 null —— 唯一会解析失败的是用户手输的地址。
        return RepoSpec.TryParse(address) ?? slot.Address(options.Language);
    }

    /// <summary>声明里所有「从 Release 取」的槽。</summary>
    public static IReadOnlyList<AssetSlot> ReleaseSlots(ComponentDefinition definition) =>
        definition.Slots.Where(s => s.Source == SlotSource.Release).ToList();

    /// <summary>声明里所有「从仓库文件树取」的槽。</summary>
    public static IReadOnlyList<AssetSlot> RepoFileSlots(ComponentDefinition definition) =>
        definition.Slots.Where(s => s.Source == SlotSource.RepoFile).ToList();

    /// <summary>声明里所有「取仓库某个目录下全部文件」的槽。</summary>
    public static IReadOnlyList<AssetSlot> RepoDirectorySlots(ComponentDefinition definition) =>
        definition.Slots.Where(s => s.Source == SlotSource.RepoDirectory).ToList();

    /// <summary>
    /// 目录槽这次要从仓库里取哪个目录。用户改过就用用户的，否则用声明的默认值。
    ///
    /// 转发到 <see cref="ResolveSlotFileName"/> 而不是另写一遍读取逻辑：目录槽的第二个可改项
    /// 就是**目录路径**（见 <see cref="SlotSource.RepoDirectory"/>），存档键、用户覆盖、
    /// 默认值求值三件事与文件名槽完全同源。这里只是给调用点一个说人话的名字。
    /// </summary>
    public static string ResolveSlotDirectory(ComponentDefinition definition, AssetSlot slot, WizardOptions options) =>
        ResolveSlotFileName(definition, slot, options);

    /// <summary>
    /// 仓库文件树里一个文件的直链。用 <c>HEAD</c> 当 ref，避免把分支名写死
    /// （上游改 main/master 时不会静默 404）。
    /// </summary>
    public static string BuildRepoFileRawUrl(RepoSpec repo, string path) =>
        $"https://raw.githubusercontent.com/{repo.Owner}/{repo.Name}/HEAD/{path.TrimStart('/')}";

    /// <summary>
    /// 同上的备用地址：走 <c>api.github.com</c> 的 contents 端点。
    ///
    /// 存在的理由与 release 那条一模一样 —— <c>raw.githubusercontent.com</c> 在部分网络下不可达，
    /// 而 API 端点在同一次会话里往往能通（见 <see cref="Models.ReleaseAsset.DownloadUrl"/> 的注释）。
    /// ⚠️ 必须配 <c>Accept: application/vnd.github.raw</c>，否则返回的是**这段文件的 JSON 元数据**
    /// （HTTP 200），会被当成组件包写进 SD 卡。
    /// </summary>
    public static string BuildRepoFileApiUrl(RepoSpec repo, string path) =>
        $"{ReleaseAsset.ApiRoot}/repos/{repo.Owner}/{repo.Name}/contents/{path.TrimStart('/')}?ref=HEAD";

    /// <summary>contents 端点取原始内容所需的 Accept 头。</summary>
    public const string RawContentAccept = "application/vnd.github.raw";

    /// <summary>
    /// 列仓库目录用的 contents 端点地址。
    ///
    /// ⚠️ **不带 <c>?ref=</c>** —— 不带 ref 时 GitHub 用仓库的**默认分支**，
    /// 而带 <c>?ref=HEAD</c> 会 404（contents 端点的 ref 只认分支名 / 标签 / commit SHA，
    /// 不认 <c>HEAD</c> 这种符号引用）。用户清单里写的是 <c>tree/master/systemPatches</c>，
    /// 实测该仓库的默认分支就是 <c>master</c>，两者一致；走默认分支还能顺带免疫
    /// 「上游哪天把默认分支改名成 main」。
    /// </summary>
    public static string BuildRepoDirectoryApiUrl(RepoSpec repo, string directory) =>
        $"{ReleaseAsset.ApiRoot}/repos/{repo.Owner}/{repo.Name}/contents/{directory.Trim('/')}";

    /// <summary>
    /// 一个槽对应的下载物（0 个表示没找到匹配的资源，由调用方逐槽告警）。
    ///
    /// 命中的方式随结果一起塞进 <see cref="AssetPick.Slot"/> —— 「猜」必须留痕，
    /// 否则 <c>sys-ftpd</c> 那种「声明叫 release.zip、实际下了 sys-ftpd-1.0.5.zip」的事
    /// 用户永远不知道。
    /// </summary>
    public static IReadOnlyList<AssetPick> PickSlot(
        ComponentDefinition definition,
        AssetSlot slot,
        ReleaseInfo release,
        WizardOptions options)
    {
        var pattern = ResolveSlotFileName(definition, slot, options);
        var match = AssetNameMatcher.Find(release, pattern);
        if (match is null)
        {
            return [];
        }

        return
        [
            new AssetPick(match.Asset, SaveAs: slot.SaveAs, Targets: slot.Targets, IsPayload: slot.IsPayload)
            {
                Slot = new SlotMatch(slot.Key, match.Kind, pattern),
                Extract = slot.Extract,
            },
        ];
    }

    /// <summary>
    /// 把 <see cref="ComponentDefinition.Slots"/> 现算成下载阶段认识的那套请求。
    ///
    /// 手写 picker 的组件（<see cref="ComponentDefinition.Slots"/> 为空）原样返回
    /// <see cref="ComponentDefinition.Repos"/>，行为与以前逐字节相同。
    ///
    /// 槽驱动的组件按**实际仓库**分组：多个槽落在同一个仓库时只查一次 Release
    /// （用户把两个槽改成同一个地址是很自然的操作，不该因此查两遍）。
    ///
    /// ⚠️ <see cref="SlotSource.RepoFile"/> 的槽**不在**这里产出 —— 它们没有 Release 可查，
    /// 由 <see cref="PickRepoFileSlot"/> 单独处理（调用方要两条路都走）。
    /// </summary>
    public static IReadOnlyList<RepoRequest> RepoRequestsFor(ComponentDefinition definition, WizardOptions options)
    {
        if (definition.Slots.Count == 0)
        {
            // 手写 picker 的组件原样返回声明，只滤掉「这次运行不该查」的那些
            // （见 RepoRequest.Applies；没声明 Applies 的请求一律保留，行为与以前逐字节相同）。
            return definition.Repos
                .Where(request => request.Applies?.Invoke(options) ?? true)
                .ToList();
        }

        return ReleaseSlots(definition)
            .GroupBy(s => ResolveSlotRepo(definition, s, options))
            .Select(group => new RepoRequest
            {
                Repo = group.Key,
                Slots = group.ToList(),
                Picker = (release, opts) => group
                    .SelectMany(s => PickSlot(definition, s, release, opts))
                    .ToList(),
            })
            .ToList();
    }

    /// <summary>
    /// 一个「仓库文件树」槽对应的下载物。
    ///
    /// 与 <see cref="PickSlot"/> 的差别：不查 Release、不做名字容错（路径是用户填什么就是什么），
    /// 直接按「仓库 + 路径」拼出 raw 直链；备用通道是 contents 端点（见
    /// <see cref="BuildRepoFileApiUrl"/>），由下载阶段据 <see cref="AssetPick.RepoFile"/> 组装。
    ///
    /// <c>Size</c> 填 0：raw 端点不报长度，进度只能按「收到多少算多少」显示。
    /// 这不会让进度条乱跳 —— 下载服务在总长未知时按已收字节显示（见 <c>DownloadProgress.Percent</c>）。
    /// </summary>
    public static AssetPick PickRepoFileSlot(ComponentDefinition definition, AssetSlot slot, WizardOptions options)
    {
        var repo = ResolveSlotRepo(definition, slot, options);
        var fileName = ResolveSlotFileName(definition, slot, options);
        var path = string.IsNullOrWhiteSpace(slot.RepoPath) ? fileName : slot.RepoPath!;

        return new AssetPick(
            new ReleaseAsset(fileName, BuildRepoFileRawUrl(repo, path), size: 0),
            SaveAs: slot.SaveAs,
            Targets: slot.Targets,
            IsPayload: slot.IsPayload)
        {
            Slot = new SlotMatch(slot.Key, AssetMatchKind.Exact, fileName),
            RepoFile = new RepoFileRef(repo, path),
            Extract = slot.Extract,
        };
    }

    /// <summary>
    /// 一个「仓库目录」槽对应的**一批**下载物（列目录的结果已经在手上了）。
    ///
    /// 拆成「先列目录、再挑」两步，是为了让挑选这一步保持**纯函数** —— 列目录要联网，
    /// 而纯函数可以在回归里穷举（空目录、只有一个文件、混进子目录……），联网的那一步
    /// 只负责把 JSON 解析成 <see cref="RepoDirectoryFile"/>（那一步也另有解析用例）。
    ///
    /// 每个文件都带 <see cref="RepoFileRef"/>，于是它**白捡了仓库文件树那套备用通道**：
    /// raw 直链不通时自动改走 contents 端点（配 <c>application/vnd.github.raw</c>）。
    /// 这不是巧合 —— 目录里的文件本来就在仓库文件树里，两者是同一个东西的两种取法。
    ///
    /// <c>AssetMatchKind.Exact</c>：目录槽**没有「猜」这件事**，文件名是列目录列出来的，
    /// 逐字就是逐字。所以容错瀑布对它不适用，也不会出现「命中方式」需要留痕的情况。
    /// </summary>
    public static IReadOnlyList<AssetPick> PickRepoDirectorySlot(
        AssetSlot slot,
        RepoSpec repo,
        IReadOnlyList<RepoDirectoryFile> files)
    {
        return files
            .Select(file => new AssetPick(
                new ReleaseAsset(file.Name, file.DownloadUrl, file.Size),
                SaveAs: null,
                Targets: slot.Targets,
                IsPayload: slot.IsPayload)
            {
                Slot = new SlotMatch(slot.Key, AssetMatchKind.Exact, file.Name),
                RepoFile = new RepoFileRef(repo, file.Path),
                Extract = slot.Extract,
            })
            .ToList();
    }

    /// <summary>
    /// 仓库**整包**（tarball）地址：<c>codeload.github.com/&lt;owner&gt;/&lt;name&gt;/tar.gz/HEAD</c>。
    ///
    /// 为什么要它：目录槽的「列目录 + 逐个文件」在 <c>raw.githubusercontent.com</c> 不可达的网络里
    /// 会退化成**每个文件一次 API 调用** —— 实测那 20 个补丁就是 20 次，而未登录配额只有 60/小时，
    /// 第 17 个文件就撞上了 HTTP 403。整包一次取回只要 **1 次请求**，而且 codeload **不占 API 配额**。
    ///
    /// ⚠️ 用符号引用 <c>HEAD</c> 而不是分支名：实测 codeload 接受 <c>HEAD</c>（HTTP 200），
    /// 于是既**不用先查默认分支**（省掉一次 API 调用），也不怕上游把 <c>master</c> 改名成 <c>main</c>。
    ///
    /// ⚠️ 代价是会把**整个仓库快照**拉下来（含 <c>.github/</c> 之类），比「只取那个目录」大。
    /// 所以它只是**快路径**：拿不到就回落到原来的列目录 + 逐文件，行为与以前完全一样。
    /// </summary>
    public static string BuildRepoArchiveUrl(RepoSpec repo) =>
        $"https://codeload.github.com/{repo.Owner}/{repo.Name}/tar.gz/HEAD";

    /// <summary>整包下载落在 <c>download/&lt;组件&gt;/</c> 下的临时文件名（解包后即删）。</summary>
    public static string RepoArchiveFileName(RepoSpec repo) => repo.Name + "-HEAD.tar.gz";

    /// <summary>
    /// 从整包的条目名里挑出「<paramref name="directory"/> 下的**直接子文件**」，
    /// 返回「条目名 → 落盘裸文件名」。**纯函数** —— 归档 I/O 那层薄到不必测，规则这层才能穷举。
    ///
    /// 两步：
    /// <list type="number">
    ///   <item>
    ///     **剥掉第一层目录**。tarball 的根目录叫 <c>&lt;repo&gt;-HEAD/</c>，名字里带符号引用，
    ///     **无法在声明里预先写死** —— 这正是它用不了 <see cref="ExtractPlan.StripPrefix"/> 的原因
    ///     （那个字段是常量字符串）。根级的散条目（如 <c>pax_global_header</c>）没有第一层，直接丢掉。
    ///   </item>
    ///   <item>
    ///     只留**直接**躺在 <paramref name="directory"/> 下的文件，落成**裸文件名**（再深一层的不取）。
    ///   </item>
    /// </list>
    ///
    /// 落成裸名是关键：这样整包路径与「逐个文件下载」产出的文件名**完全一致**，
    /// 下游（暂存 / 落点 / 清单）一行都不用改。
    /// </summary>
    public static IReadOnlyList<(string Entry, string FileName)> SelectDirectoryEntries(
        IEnumerable<string> entryNames,
        string directory)
    {
        var prefix = directory.Trim('/');
        if (prefix.Length == 0)
        {
            return [];
        }

        var wanted = prefix + "/";
        var result = new List<(string Entry, string FileName)>();

        foreach (var raw in entryNames)
        {
            var name = raw.Replace('\\', '/').TrimStart('/');
            if (name.Length == 0)
            {
                continue;
            }

            var slash = name.IndexOf('/');
            if (slash < 0)
            {
                continue; // 根级散条目，没有第一层目录可剥
            }

            var rest = name[(slash + 1)..];
            if (!rest.StartsWith(wanted, StringComparison.Ordinal))
            {
                continue;
            }

            var file = rest[wanted.Length..];
            if (file.Length == 0 || file.Contains('/'))
            {
                continue; // 目录项本身，或者更深一层的
            }

            result.Add((name, file));
        }

        // 与列目录那条路径**同一个顺序**（按文件名序）—— 两条通道的日志与产物才对得上。
        return result.OrderBy(x => x.FileName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 把「整包里取出来的裸文件名」包成与列目录那条路径**同构**的 <see cref="RepoDirectoryFile"/>，
    /// 好让两边都走 <see cref="PickRepoDirectorySlot"/> 这一个入口。
    /// </summary>
    public static IReadOnlyList<RepoDirectoryFile> AsDirectoryFiles(
        RepoSpec repo,
        string directory,
        IReadOnlyList<(string FileName, long Size)> files)
    {
        var prefix = directory.Trim('/');
        return files
            .Select(file =>
            {
                var path = prefix + "/" + file.FileName;
                return new RepoDirectoryFile(file.FileName, path, file.Size, BuildRepoFileRawUrl(repo, path));
            })
            .ToList();
    }

    /// <summary>
    /// 一个槽的产物会落在 <c>out/</c> 的哪里（界面上当提示显示）。
    ///
    /// 压缩包与散装文件的语义不同：压缩包是**整包解到那个目录里**，散装文件是**复制成那个文件**。
    /// 界面上必须说清是「目录」还是「文件」，否则用户看到 <c>out/switch/DBI/</c> 会以为
    /// 会生成一个叫 DBI 的文件。
    /// </summary>
    public static string DescribeSlotPlacement(AssetSlot slot, string effectiveFileName)
    {
        // 目录槽是第三种语义：不是「解到某个目录里」，也不是「复制成某个文件」，
        // 而是「目录里的每个文件各自复制进去」。不加这一支的话会走到下面那条散装分支、
        // 报出「out/systemPatches」—— 用户会以为会生成一个叫 systemPatches 的文件。
        if (slot.Source == SlotSource.RepoDirectory)
        {
            // 同一套替换规则（目录槽目前没人用 {asset}，但显示与落点必须走同一条规则，
            // 否则将来谁用了它，界面显示的就是 out/xxx/{asset}/）。
            var dirStem = System.IO.Path.GetFileNameWithoutExtension(effectiveFileName);

            return slot.Targets.Count == 0
                ? "out/"
                : string.Join("、", slot.Targets
                    .Select(target => target.ResolveAssetName(dirStem))
                    .Select(target => string.IsNullOrEmpty(target.Directory)
                        ? "out/"
                        : "out/" + target.Directory.TrimEnd('/', '\\') + "/")
                    .Distinct(StringComparer.Ordinal));
        }

        if (slot.Targets.Count == 0)
        {
            return ArchiveExtractor.IsArchive(effectiveFileName) ? "out/" : "out/" + effectiveFileName;
        }

        var isArchive = ArchiveExtractor.IsArchive(effectiveFileName);

        // 落点里的 {asset} 也要在这里解出来，否则界面上会显示成 out/Firmware/{asset}/。
        // ⚠️ 界面上拿到的 effectiveFileName 是**默认模式**（`Firmware.x.x.x.zip`）而不是匹配到的资源名，
        //    所以显示成「out/Firmware/Firmware.x.x.x/」—— 这正是声明里那个模式，**不再另行编一个版本号**。
        //    （下载完成后的真实落点由 AssetPick.ResolvedTargets 决定，两者同一套替换规则。）
        var stem = System.IO.Path.GetFileNameWithoutExtension(effectiveFileName);

        return string.Join("、", slot.Targets
            .Select(target => target.ResolveAssetName(stem))
            .Select(target => isArchive
                ? (string.IsNullOrEmpty(target.Directory) ? "out/" : "out/" + target.Directory.TrimEnd('/', '\\') + "/")
                : "out/" + target.Resolve(effectiveFileName))
            .Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// 从 Release 里挑出与界面语言匹配的 hekate 本地化包。
    ///
    /// <c>easyworld/hekate</c> 的命名是 <c>hekate_ctcaer_&lt;ver&gt;_Nyx_&lt;nyxver&gt;_sc.zip</c>（简体）
    /// 与 <c>…_tc.zip</c>（繁体）。非中文界面、或该 Release 里没有本地化包（例如官方源）时返回 null，
    /// 由调用方回落到通用规则 —— 所以这一个 picker 同时服务两个源，不需要按仓库分支。
    /// </summary>
    private static ReleaseAsset? FindLocalizedHekateZip(ReleaseInfo release, string? language)
    {
        if (!IsChinese(language))
        {
            return null;
        }

        // 繁体判在前面：zh-Hant 也以 "zh" 开头，顺序反了会一律落到简体
        var suffix = language!.Contains("Hant", StringComparison.OrdinalIgnoreCase) ? "_tc" : "_sc";

        return release.FindAsset(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && a.Name.Contains("_Nyx_", StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith(suffix + ".zip", StringComparison.OrdinalIgnoreCase));
    }

    // ── 产物落点（相对 out/ 根目录）──────────────────────────────
    // 这些是**上游约定**，不是我们随便定的，改之前先看一眼对应项目的 README：
    //   · Overlay 插件：/switch/.overlays/  —— ovl-sysmodules 与 Ultrahand 都是这个目录
    //   · Ultrahand 语言包：/config/ultrahand/lang/
    //   · hekate 的 payload 菜单：/bootloader/payloads/
    //   · hekate 自身约定：根目录 payload.bin（RCM 注入用）+ bootloader/update.bin（自我更新用）
    /// <summary>Overlay 插件的落点：<c>/switch/.overlays/</c>。</summary>
    public static readonly IReadOnlyList<OutputTarget> OverlayTargets =
    [
        new OutputTarget("switch/.overlays"),
    ];

    /// <summary>Ultrahand 语言包的落点：<c>/config/ultrahand/lang/</c>。</summary>
    public static readonly IReadOnlyList<OutputTarget> UltrahandLangTargets =
    [
        new OutputTarget("config/ultrahand/lang"),
    ];

    /// <summary>Atmosphere <c>fusee.bin</c> 的落点：hekate 的 payload 菜单目录。</summary>
    public static readonly IReadOnlyList<OutputTarget> FuseePayloadTargets =
    [
        new OutputTarget("bootloader/payloads"),
    ];

    /// <summary>
    /// hekate payload 的落点：**同时**放根目录 <c>payload.bin</c> 与 <c>bootloader/update.bin</c>。
    /// 前者是 RCM 注入/链式加载直接用的文件名，后者是 hekate 自己认的更新文件名，缺一不可。
    /// </summary>
    public static readonly IReadOnlyList<OutputTarget> HekatePayloadTargets =
    [
        new OutputTarget(string.Empty, "payload.bin"),
        new OutputTarget("bootloader", "update.bin"),
    ];

    /// <summary>
    /// **非 8G** 模式下 hekate payload 的落点：比 <see cref="HekatePayloadTargets"/> 多一个
    /// 「根目录、保留原文件名」的落点。
    ///
    /// 用户 2026-09-18 的要求：非 8G 时根目录要**留着** <c>hekate_ctcaer_&lt;ver&gt;.bin</c>
    /// （手头随时有一份原始 payload），同时复制成 <c>payload.bin</c> 与 <c>bootloader/update.bin</c>。
    ///
    /// ⚠️ 以前根目录那份是**靠 hekate 的 zip 包碰巧带来的**（包里自带同名文件，整棵铺开就落在根目录）。
    /// 那不是保证 —— 用户一旦关掉「合并组件文件」（<c>IncludeComponentFilesInOutput</c>），
    /// 包就不解压，根目录的原名 payload **静默消失**，而日志里一片祥和。显式声明这个落点才叫保证。
    ///
    /// 8G 模式**不能**用它：那时根目录留标准版会让人分不清机器到底按哪个起
    /// （8G 版另有 <c>RemoveStandardHekatePayloadFromRoot</c> 把包带来的那份也删掉）。
    /// </summary>
    public static readonly IReadOnlyList<OutputTarget> HekatePayloadKeepNameTargets =
    [
        new OutputTarget(string.Empty),
        new OutputTarget(string.Empty, "payload.bin"),
        new OutputTarget("bootloader", "update.bin"),
    ];

    // ── Ultrahand 呼出组合键的数据 ──────────────────────────────
    // 注意：这些字段必须声明在 Definitions 之前。
    // C# 的静态字段按书写顺序初始化，而 Definitions 的初始化器会一路调用到
    // BuildKeyComboChoices()；若把它们写在后面，那时它们还是 null。
    /// <summary>
    /// Ultrahand 官方默认呼出组合键。
    /// 取自源码 source/utils.hpp 的 <c>defaultCombos[0]</c>。
    /// </summary>
    public const string DefaultKeyCombo = "L+DDOWN";

    /// <summary>
    /// 组合键里允许出现的按键令牌。
    /// 取自 Ultrahand 源码 source/utils.hpp 的 <c>symbolPlaceholders</c>，
    /// 外加 SL / SR（手柄导轨上的按键，官方预设里没用到但可以手写）。
    /// </summary>
    public static readonly IReadOnlyList<string> KeyComboTokens =
    [
        "A", "B", "X", "Y", "L", "R", "ZL", "ZR",
        "SL", "SR", "DUP", "DDOWN", "DLEFT", "DRIGHT",
        "LS", "RS", "MINUS", "PLUS",
    ];

    /// <summary>
    /// Ultrahand 官方 <c>defaultCombos</c> 里的全部 28 个预设组合键，顺序与源码一致。
    /// 直接拿来当候选项，避免自己编造不存在的组合。
    /// </summary>
    private static readonly string[] PresetKeyCombos =
    [
        // Primary（官方排在最前，也是默认值）
        "ZL+ZR+DDOWN", "ZL+ZR+DRIGHT", "ZL+ZR+DUP", "ZL+ZR+DLEFT",

        // 基础组合：L/R + 方向键
        "L+R+DDOWN", "L+R+DRIGHT", "L+R+DUP", "L+R+DLEFT", "L+DDOWN", "R+DDOWN",

        // 带 PLUS / MINUS 的组合
        "ZL+ZR+PLUS", "L+R+PLUS", "ZL+ZR+MINUS", "L+R+MINUS",
        "ZL+MINUS", "ZR+MINUS", "ZL+PLUS", "ZR+PLUS", "MINUS+PLUS",

        // 摇杆按下
        "LS+RS", "L+DDOWN+RS", "L+R+LS", "L+R+RS",

        // 不易冲突的组合
        "ZL+ZR+LS", "ZL+ZR+RS", "ZL+ZR+L", "ZL+ZR+R", "ZL+ZR+LS+RS",
    ];

    // ── Ultrahand default_lang 的数据 ───────────────────────────
    /// <summary>
    /// Ultrahand <c>default_lang</c> 的取值域 —— 与上游 <c>source/main.cpp</c> 里的
    /// <c>defaultLanguages</c> 列表**逐字对应**（顺序也照抄）：
    /// <code>
    /// {"en","es","fr","de","ja","ko","it","nl","pt","ru","uk","pl","zh-cn","zh-tw"}
    /// </code>
    /// 这 14 个正是 <c>lang.zip</c> 里 14 个 json 的文件名（<c>en.json</c> … <c>zh-tw.json</c>）。
    ///
    /// 上游取用它拼 <c>config/ultrahand/lang/&lt;code&gt;.json</c>；文件不存在时该语言在 Ultrahand
    /// 自己的设置里会被**跳过**（<c>if (defaultLangMode != "en" &amp;&amp; !isFile(langFile)) continue;</c>），
    /// 于是「配置说中文、卡上没有中文包」会静默退回内置英文。所以 <c>en</c> 是唯一不依赖语言包的值 ——
    /// 英文文案是编译进 <c>ovlmenu.ovl</c> 的，与上游 <c>ensureDefault(DEFAULT_LANG_STR, "en")</c> 一致。
    /// </summary>
    public static readonly IReadOnlyList<string> UltrahandLanguageCodes =
    [
        "en", "es", "fr", "de", "ja", "ko", "it", "nl",
        "pt", "ru", "uk", "pl", "zh-cn", "zh-tw",
    ];

    /// <summary>
    /// 语言代码 → 该语言**自己的写法**（endonym），用于下拉框直接显示。
    ///
    /// 刻意不走多语言表：语言选择器按惯例要「用这门语言自己写自己」—— 界面是英文的用户
    /// 也能一眼认出 简体中文 / 日本語 / Русский，翻成 "Chinese (Simplified)" 反而帮不上忙。
    /// 软件自己的语言下拉框（<c>LocalizationService</c>）取的也是语言包里的 <c>meta.name</c>，同一套做法。
    /// </summary>
    private static readonly Dictionary<string, string> UltrahandLanguageNames = new(StringComparer.Ordinal)
    {
        ["en"] = "English",
        ["es"] = "Español",
        ["fr"] = "Français",
        ["de"] = "Deutsch",
        ["ja"] = "日本語",
        ["ko"] = "한국어",
        ["it"] = "Italiano",
        ["nl"] = "Nederlands",
        ["pt"] = "Português",
        ["ru"] = "Русский",
        ["uk"] = "Українська",
        ["pl"] = "Polski",
        ["zh-cn"] = "简体中文",
        ["zh-tw"] = "繁體中文",
    };

    /// <summary>
    /// <c>default_lang</c> 的「跟随界面语言」哨兵值。
    ///
    /// 用哨兵而不是「把当前语言直接存成值」是**必须**的：界面语言随时可改，存成具体语言后
    /// 两者就会脱节 —— 用户把界面切到英文，产物里却还写着 <c>zh-cn</c>，而且没有任何东西会报错。
    /// 哨兵把「跟随」变成生成期的一次求值（见 <see cref="ResolveUltrahandDefaultLang"/>）。
    ///
    /// 它**绝不会**出现在 ini 里：写出前一定先解析成具体语言代码。
    /// </summary>
    public const string AutoLanguageValue = "auto";

    /// <summary>找不到对应关系时的兜底语言 —— 上游自己的默认值。</summary>
    public const string FallbackLanguageCode = "en";

    /// <summary>
    /// 把界面语言代码映射成 Ultrahand 的语言代码。
    ///
    /// 中文必须按**书写系统**分（<c>zh-Hans → zh-cn</c>、<c>zh-Hant → zh-tw</c>）：简繁是两套
    /// 互相独立的语言包，只看主语言子标签 <c>zh</c> 就猜，等于替用户换了语言。
    /// 其余按主语言子标签在取值域里精确匹配（<c>en-US → en</c>、<c>ja-JP → ja</c>）；
    /// 匹配不上的一律退回 <see cref="FallbackLanguageCode"/>，不抛异常 ——
    /// 界面语言是可以由用户往 <c>lang/</c> 里丢新包扩展的，认不出来不该让生成失败。
    /// </summary>
    public static string MapUiLanguageToUltrahand(string? uiLanguageCode)
    {
        var code = uiLanguageCode?.Trim() ?? string.Empty;

        if (IsChinese(code))
        {
            // 繁体：书写系统标记 Hant，或地区是台/港/澳。
            var traditional = code.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                              || code.Contains("TW", StringComparison.OrdinalIgnoreCase)
                              || code.Contains("HK", StringComparison.OrdinalIgnoreCase)
                              || code.Contains("MO", StringComparison.OrdinalIgnoreCase);

            return traditional ? "zh-tw" : "zh-cn";
        }

        var primary = code.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return UltrahandLanguageCodes.FirstOrDefault(
                   known => string.Equals(known, primary, StringComparison.OrdinalIgnoreCase))
               ?? FallbackLanguageCode;
    }

    /// <summary>
    /// <c>default_lang</c> 指向的语言文件在 <c>out/</c> 里的相对路径。
    ///
    /// 目录取自 <see cref="UltrahandLangTargets"/>（语言包实际落点），不另写一份字面量 ——
    /// 上游拼法是 libultrahand 的 <c>LANG_PATH = BASE_CONFIG_PATH + "lang/"</c>，
    /// 落点改了而这里没跟上的话，「语言包在不在」的判据就会指向一个永远不会存在的路径。
    /// </summary>
    public static string UltrahandLangFileFor(string languageCode) =>
        $"{UltrahandLangTargets[0].Directory}/{languageCode}.json";

    /// <summary>
    /// 某个字符串是不是 Ultrahand 认的语言代码（大小写不敏感；哨兵 <c>auto</c> **不算**）。
    ///
    /// 单独抽出来是给「存档里填了个坏值」这条告警用的：拿「解析结果与原值是否相同」当判据的话，
    /// 用户手写的大写 <c>ZH-CN</c> 会被解析成 <c>zh-cn</c>，于是被误报成非法值。
    /// </summary>
    public static bool IsKnownUltrahandLanguage(string? value)
    {
        var code = value?.Trim() ?? string.Empty;
        return code.Length > 0
               && UltrahandLanguageCodes.Any(known => string.Equals(known, code, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 把界面上 <c>default_lang</c> 的取值解析成**一定会写进 ini 的具体语言代码**。
    ///
    /// 三种输入、三种去向：
    /// <list type="bullet">
    /// <item>哨兵（<c>auto</c>）或空 → 跟随界面语言；</item>
    /// <item>合法代码 → 原样采用（用户显式覆盖，改界面语言也不会被改回来）；</item>
    /// <item>其它 → 视为存档损坏，回退到界面语言对应的代码。</item>
    /// </list>
    ///
    /// 返回值**保证**落在 <see cref="UltrahandLanguageCodes"/> 里 —— 哨兵与损坏值都不会流到 ini，
    /// 否则 Ultrahand 会拿着一个不存在的文件名去加载语言，然后静默用英文。
    /// </summary>
    public static string ResolveUltrahandDefaultLang(string? rawValue, string? uiLanguageCode)
    {
        var code = rawValue?.Trim() ?? string.Empty;

        if (code.Length == 0 || string.Equals(code, AutoLanguageValue, StringComparison.OrdinalIgnoreCase))
        {
            return MapUiLanguageToUltrahand(uiLanguageCode);
        }

        return UltrahandLanguageCodes.FirstOrDefault(
                   known => string.Equals(known, code, StringComparison.OrdinalIgnoreCase))
               ?? MapUiLanguageToUltrahand(uiLanguageCode);
    }

    /// <summary>
    /// <c>default_lang</c> 的候选语言列表：第一项是「跟随界面语言」，其余 14 个是 Ultrahand 认的代码。
    /// </summary>
    private static List<OptionChoice> BuildUltrahandLanguageChoices()
    {
        var choices = new List<OptionChoice>
        {
            new(AutoLanguageValue, "Opt.Ultrahand.DefaultLang.Auto"),
        };

        choices.AddRange(UltrahandLanguageCodes.Select(
            code => new OptionChoice(code, code, UltrahandLanguageNames[code])));

        return choices;
    }

    private static readonly Dictionary<ComponentKind, ComponentDefinition> Definitions = new()
    {
        [ComponentKind.Atmosphere] = new ComponentDefinition
        {
            Kind = ComponentKind.Atmosphere,
            Category = ComponentCategory.Framework,
            Repos =
            [
                new RepoRequest
                {
                    Repo = AtmosphereRepo,
                    Picker = (release, _) =>
                    {
                        var picks = new List<AssetPick>();

                        // 发布包：atmosphere-<ver>-<branch>-<hash>+hbl-<v>+hbmenu-<v>.zip
                        // 包内部就是 SD 卡根目录的样子（atmosphere/、switch/、hbmenu.nro），
                        // 不声明落点 = 整棵铺到 out 根目录，正是我们要的。
                        var zip = release.FindAsset(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                        if (zip is not null)
                        {
                            picks.Add(new AssetPick(zip));
                        }

                        var fusee = release.FindAssetByName("fusee.bin")
                                    ?? release.FindAsset(a => a.Name.EndsWith("fusee.bin", StringComparison.OrdinalIgnoreCase));
                        if (fusee is not null)
                        {
                            // fusee.bin 是**裸文件**，包里没有它，必须显式落到 hekate 的 payload 菜单目录
                            picks.Add(new AssetPick(
                                fusee,
                                Targets: FuseePayloadTargets,
                                IsPayload: true));
                        }

                        return picks;
                    },
                },
            ],
            Options = BuildAtmosphereOptions(),
        },

        [ComponentKind.Hekate] = new ComponentDefinition
        {
            Kind = ComponentKind.Hekate,
            Category = ComponentCategory.Framework,
            Repos =
            [
                new RepoRequest
                {
                    Repo = HekateRepo,
                    Picker = (release, options) =>
                    {
                        var picks = new List<AssetPick>();

                        // hekate_ctcaer_<ver>_Nyx_<ver>.zip（排除 joiner_scripts 等辅助包）
                        // 包内部同样是 SD 卡根目录的样子（bootloader/**），整棵铺开即可
                        //
                        // 中文界面下**先**试本地化包（easyworld 的 _sc / _tc）：
                        // 同一个 Release 里两个都在，必须按语言挑 —— 图省事「取第一个 _Nyx_ 包」
                        // 会让繁体用户静默拿到简体界面。
                        var zip = FindLocalizedHekateZip(release, options.Language)
                                  ?? release.FindAsset(a =>
                                      a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                      && a.Name.Contains("_Nyx_", StringComparison.OrdinalIgnoreCase))
                                  ?? release.FindAsset(a =>
                                      a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                      && !a.Name.Contains("joiner", StringComparison.OrdinalIgnoreCase));

                        if (zip is not null)
                        {
                            picks.Add(new AssetPick(zip));
                        }

                        var standardBin = release.FindAsset(a =>
                            a.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                            && !IsRam8GbPayloadName(a.Name));

                        var ram8GbBin = release.FindAsset(a =>
                            a.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                            && IsRam8GbPayloadName(a.Name));

                        // 勾选 8G 运存时优先取官方 __ram8GB.bin payload
                        var primary = options.Ram8Gb ? ram8GbBin ?? standardBin : standardBin;
                        if (primary is not null)
                        {
                            // 两个变体的落点相同（payload.bin + update.bin），所以**只要选中的是 8G 版**，
                            // 「界面选了 8G、机器却按 4G 起」就不会发生。
                            //
                            // ⚠️ 但 `ram8GbBin ?? standardBin` 这条回退**会**制造那种错位：发布里没有
                            // __ram8GB.bin 时，primary 悄悄变成标准版，而 exosphere.ini 的
                            // `enable_mem_mode=1` 与引导项的 `memmode=1` 照样写 —— 产物是「配置说 8G、
                            // payload 是 4G」。
                            //
                            // 这条回退**不再静默**（用户 2026-09-17 决定：告警，不报错）：下载阶段由
                            // `ComponentCatalog.IsRam8GbPayloadMissing` 判定，`MainViewModel` 在 picker
                            // 调用点打出告警。picker 自己的委托签名里没有 logger，所以判定与告警**必须
                            // 分在两处** —— 改这里时先看 `IsRam8GbPayloadMissing` 与它的用例。
                            // 非 8G：根目录也留一份原名的（见 HekatePayloadKeepNameTargets 的注释）；
                            // 8G：只落 payload.bin + update.bin —— 根目录留标准版会与 8G 版混淆。
                            picks.Add(new AssetPick(
                                primary,
                                Targets: options.Ram8Gb ? HekatePayloadTargets : HekatePayloadKeepNameTargets,
                                IsPayload: true));
                        }

                        // 8G 模式下同时保留标准 payload，便于随时切回去。
                        //
                        // 落点**不能**跟 primary 一样：两个文件写同一个目标，谁最后写谁赢，
                        // 结果就是 8G 模式实际拿到 4G payload（而且完全静默）。
                        // 放 bootloader/payloads/ 既不会盖掉正在用的 payload.bin，
                        // 又能在 hekate 的 payload 菜单里直接选中启动，比丢在根目录更有用。
                        if (options.Ram8Gb && standardBin is not null && !ReferenceEquals(standardBin, primary))
                        {
                            picks.Add(new AssetPick(
                                standardBin,
                                Targets: FuseePayloadTargets,
                                IsPayload: true));
                        }

                        return picks;
                    },
                },

                // ── 8G 运存的 payload：**固定**从官方取 ──────────────────────────
                //
                // 为什么需要第二个请求：中文界面下上面那个请求会被重定向到 easyworld，
                // 而镜像仓库只发 _sc.zip / _tc.zip，**一个 .bin 都没有** —— 于是 8G 用户
                // 拿到的是「中文 Nyx + 没有任何 payload」。以前靠「8G 就整个走官方」绕开，
                // 代价是 8G 用户永远拿不到中文 Nyx（用户 2026-09-18 报的正是这个）。
                //
                // ⚠️ 只在「包走了镜像」时才查：包本身走官方时（非中文界面），
                //    上面那个请求已经把 8G payload 取过了，这里再取一次就是**同一个文件下两遍**
                //    且两个 pick 抢同一个落点。
                //    判据放在 Applies 里（查询之前），不放在 picker 里 —— 后者会让这个请求
                //    照样查一次 Release，白花配额、留一条「未找到资源」假告警，还把同一个版本号
                //    记进 VersionText 两遍。**这个判断只准有一处**。
                new RepoRequest
                {
                    Repo = HekateRepo,
                    AllowLanguageMirror = false,
                    Applies = options => options.Ram8Gb && ShouldUseLocalizedHekate(options),
                    Picker = (release, options) =>
                    {
                        var picks = new List<AssetPick>();

                        var ram8GbBin = release.FindAsset(a =>
                            a.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                            && IsRam8GbPayloadName(a.Name));

                        if (ram8GbBin is not null)
                        {
                            // 落点与「包走官方」那条路**逐字一致**：payload.bin + bootloader/update.bin。
                            // 两边不一致的话，同一个「8G 运存」开关会因为界面语言不同产出不同布局。
                            picks.Add(new AssetPick(
                                ram8GbBin,
                                Targets: HekatePayloadTargets,
                                IsPayload: true));
                        }

                        // 8G 模式下留一份标准 payload 到 bootloader/payloads/，便于随时切回 4G。
                        // 与上面那条路同样保持一致 —— 镜像仓库的包里虽然也带了一份标准 payload，
                        // 但它落在 out/ 根目录，8G 时会被 ConfigGenerator 删掉（`RemoveStandardHekatePayloadFromRoot`），
                        // 不显式补一份的话「随时切回去」这个能力会随界面语言一起消失。
                        var standardBin = release.FindAsset(a =>
                            a.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                            && !IsRam8GbPayloadName(a.Name));

                        if (standardBin is not null)
                        {
                            picks.Add(new AssetPick(
                                standardBin,
                                Targets: FuseePayloadTargets,
                                IsPayload: true));
                        }

                        return picks;
                    },
                },
            ],
            Options = BuildHekateOptions(),
        },

        [ComponentKind.Ultrahand] = new ComponentDefinition
        {
            Kind = ComponentKind.Ultrahand,
            Category = ComponentCategory.Framework,
            Repos =
            [
                new RepoRequest
                {
                    Repo = UltrahandRepo,
                    Picker = (release, options) =>
                    {
                        var picks = new List<AssetPick>();
                        var manual = string.Equals(
                            options.Get(ComponentKind.Ultrahand, "installMode", "sdout"),
                            "manual",
                            StringComparison.OrdinalIgnoreCase);

                        if (manual)
                        {
                            var ovlMenu = release.FindAssetByName("ovlmenu.ovl")
                                          ?? release.FindAsset(a => a.Name.EndsWith(".ovl", StringComparison.OrdinalIgnoreCase));
                            if (ovlMenu is not null)
                            {
                                picks.Add(new AssetPick(ovlMenu, Targets: OverlayTargets));
                            }
                        }
                        else
                        {
                            // sdout.zip 内部就是 SD 卡根目录的样子（atmosphere/、config/、switch/），
                            // 而且 switch/.overlays/ovlmenu.ovl 已经在包里放好了 —— 整棵铺开即可，
                            // 不要额外套一层 sdout/。
                            //
                            // ⚠️ 回退**必须排除语言包**：上游资源按名字排序，`lang.zip` 排在
                            // `sdout.zip` 前面，所以「随便拿一个 zip」会拿到语言包，把 14 个 json
                            // 当成整套 SD 内容铺到 out 根目录。判据走 IsUltrahandLangZipName
                            // （与 installLang 那处共用同一份）。
                            var sdout = release.FindAssetByName("sdout.zip")
                                        ?? release.FindAsset(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                                                  && !IsUltrahandLangZipName(a.Name));
                            if (sdout is not null)
                            {
                                picks.Add(new AssetPick(sdout));
                            }
                        }

                        if (options.GetBool(ComponentKind.Ultrahand, "installLang"))
                        {
                            var lang = release.FindAssetByName("lang.zip")
                                       ?? release.FindAsset(a => IsUltrahandLangZipName(a.Name)
                                                                 && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                            if (lang is not null)
                            {
                                // lang.zip 里是 14 个裸 json（de.json / en.json / zh-cn.json …），
                                // 连一层目录都没有 —— 不显式指定落点就会全散在 out 根目录
                                picks.Add(new AssetPick(lang, Targets: UltrahandLangTargets));
                            }
                        }

                        return picks;
                    },
                },
                new RepoRequest
                {
                    Repo = OvlSysmodulesRepo,
                    Picker = (release, options) =>
                    {
                        if (!options.GetBool(ComponentKind.Ultrahand, "installSysmodules", true))
                        {
                            return [];
                        }

                        var ovl = release.FindAssetByName("ovlSysmodules.ovl")
                                  ?? release.FindAsset(a => a.Name.EndsWith(".ovl", StringComparison.OrdinalIgnoreCase));

                        // 裸 .ovl 文件：必须落到 /switch/.overlays/，否则 Ultrahand 的 Overlay 菜单里看不到
                        return ovl is null ? [] : [new AssetPick(ovl, Targets: OverlayTargets)];
                    },
                },
                new RepoRequest
                {
                    Repo = NxOvlloaderRepo,
                    Picker = (release, options) =>
                    {
                        if (!options.GetBool(ComponentKind.Ultrahand, "installOvlloader", true))
                        {
                            return [];
                        }

                        var zip = release.FindAssetByName("nx-ovlloader.zip")
                                  ?? release.FindAsset(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

                        // nx-ovlloader.zip 内部同样是 SD 卡根目录的样子（atmosphere/、switch/），
                        // 整棵铺开即可，不要额外套一层 nx-ovlloader/。
                        return zip is null ? [] : [new AssetPick(zip)];
                    },
                },
            ],
            Options = BuildUltrahandOptions(),
        },

        [ComponentKind.SysPatch] = new ComponentDefinition
        {
            Kind = ComponentKind.SysPatch,
            Category = ComponentCategory.Framework,
            Repos =
            [
                new RepoRequest
                {
                    Repo = SysPatchRepo,
                    Picker = (release, _) =>
                    {
                        var zip = release.FindAsset(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                        return zip is null ? [] : [new AssetPick(zip)];
                    },
                },
            ],
            Options = BuildSysPatchOptions(),
        },

        // ══ 固件（1 个）══════════════════════════════════════════════════
        //
        // 用户 2026-09-18 要求：从 THZoria/NX_Firmware 下 `Firmware x.x.x.zip`，
        // **解压后放在 out/Firmware/<版本>/ 里**（离线固件，拷进 SD 卡就能用 4ifir/Atmosphere 装机）。
        //
        // 走 Slots 而不是手写 picker：这个组件正是「一个仓库、一个文件」的形状，
        // 于是「下载地址」与「下载文件名」两个输入框**自动**出现在高级设置里（用户要求），
        // 不必为它单写一遍界面。
        [ComponentKind.Firmware] = new ComponentDefinition
        {
            Kind = ComponentKind.Firmware,
            Category = ComponentCategory.Firmware,
            // 名称与说明走语言键（Component.Firmware.Name / .Desc），不写死英文 ——
            // 「离线固件」是个中文用户一看就懂的概念，与 Lockpick_RCM 那类专有名词不同。
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Firmware",
                    Address = AssetSlot.Always(FirmwareRepo),

                    // ⚠️ 默认文件名是**模式**而不是具体版本号：上游发新版时，写死的名字会让默认值当场作废
                    //    （用户不改就下不到东西，而他还以为自己没改过任何东西）。`x` 是版本占位，
                    //    由 AssetNameMatcher 的第二级瀑布按通配匹配（实测命中 Firmware.23.0.0.zip）。
                    //    代价：日志里这条会标成「通配」而不是「逐字命中」—— 这是**如实**的，
                    //    用户想锁定某个版本就把名字改成那个具体版本号（那时就是逐字命中）。
                    FileName = AssetSlot.Always("Firmware.x.x.x.zip"),

                    // 包内是**平的**（实测：238 个 .nca / .cnmt.nca 全在压缩包根，没有顶层目录），
                    // 所以版本文件夹只能由「下载下来的那个文件名」造出来 —— 见 OutputTarget.AssetNameToken。
                    Targets = [new OutputTarget("Firmware/{asset}")],
                },
            ],
        },

        // ══ 组件（19 个）══════════════════════════════════════════════════
        //
        // 全部走 Slots 声明：地址与文件名自动成为界面上可改的两项。
        // 落点依据（用户 2026-09-18 的清单 + 上游包结构）：
        //   · .zip 包内部本来就是 SD 卡根目录的样子 → 整包铺到 out 根，不声明 Targets；
        //   · 裸 .nro 按 SD 卡惯例进 switch/<插件名>/（用户明确给出的 Goldleaf / DBI /
        //     NXThemesInstaller / wiliwili / Firmware-Dumper 五个落点全是这个形状）。
        //
        // ⚠️ 插件名一律用 DisplayName 字面量：它们都是英文专有名词，翻译成中文反而认不出来
        //    （用户 2026-09-18 的约定，见 ComponentDefinition.DisplayName 的注释）。
        //    说明文字走语言键 Component.<Kind>.Desc。

        [ComponentKind.Breeze] = new ComponentDefinition
        {
            Kind = ComponentKind.Breeze,
            Category = ComponentCategory.Component,
            DisplayName = "Breeze-Beta",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Breeze",
                    Address = AssetSlot.Always(BreezeRepo),
                    FileName = AssetSlot.Always("Breeze.zip"),
                },
            ],
        },

        [ComponentKind.Dbi] = new ComponentDefinition
        {
            Kind = ComponentKind.Dbi,
            Category = ComponentCategory.Component,
            DisplayName = "DBIPatcher",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "DbiNro",
                    Address = AssetSlot.Always(DbiRepo),
                    FileName = AssetSlot.Always("DBI.nro"),
                    Targets = [new OutputTarget("switch/DBI")],
                },
                new AssetSlot
                {
                    // DBI 只认与自己同目录的 translation.bin，所以下载名按语言取、
                    // 落盘名固定。改名这一步由 SaveAs 表达，不是 picker 里的特例。
                    Key = "DbiTranslation",
                    Address = AssetSlot.Always(DbiRepo),
                    FileName = DbiTranslationFileName,
                    SaveAs = "translation.bin",
                    Targets = [new OutputTarget("switch/DBI")],
                },
            ],
        },

        [ComponentKind.EdiZonOverlay] = new ComponentDefinition
        {
            Kind = ComponentKind.EdiZonOverlay,
            Category = ComponentCategory.Component,
            DisplayName = "EdiZon-Overlay",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "EdiZon",
                    Address = AssetSlot.Always(EdiZonOverlayRepo),
                    FileName = AssetSlot.Always("EdiZon.zip"),
                },
            ],
        },

        [ComponentKind.Goldleaf] = new ComponentDefinition
        {
            Kind = ComponentKind.Goldleaf,
            Category = ComponentCategory.Component,
            DisplayName = "Goldleaf",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Goldleaf",
                    Address = AssetSlot.Always(GoldleafRepo),
                    FileName = AssetSlot.Always("Goldleaf.nro"),
                    Targets = [new OutputTarget("switch/Goldleaf")],
                },
            ],
        },

        [ComponentKind.Nxdumptool] = new ComponentDefinition
        {
            Kind = ComponentKind.Nxdumptool,
            Category = ComponentCategory.Component,
            DisplayName = "nxdumptool",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Nxdumptool",
                    Address = AssetSlot.Always(NxdumptoolRepo),
                    FileName = AssetSlot.Always("nxdt_rw_poc.nro"),
                    Targets = [new OutputTarget("switch/nxdt_rw_poc")],
                },
            ],
        },

        [ComponentKind.NxShell] = new ComponentDefinition
        {
            Kind = ComponentKind.NxShell,
            Category = ComponentCategory.Component,
            DisplayName = "NX-Shell",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "NxShell",
                    Address = AssetSlot.Always(NxShellRepo),
                    FileName = AssetSlot.Always("NX-Shell.nro"),
                    Targets = [new OutputTarget("switch/NX-Shell")],
                },
            ],
        },

        [ComponentKind.Ftpd] = new ComponentDefinition
        {
            Kind = ComponentKind.Ftpd,
            Category = ComponentCategory.Component,
            DisplayName = "ftpd",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Ftpd",
                    Address = AssetSlot.Always(FtpdRepo),
                    FileName = AssetSlot.Always("ftpd.nro"),
                    Targets = [new OutputTarget("switch/ftpd")],
                },
            ],
        },

        [ComponentKind.Haku33] = new ComponentDefinition
        {
            Kind = ComponentKind.Haku33,
            Category = ComponentCategory.Component,
            DisplayName = "Haku33",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Haku33",
                    Address = AssetSlot.Always(Haku33Repo),
                    FileName = AssetSlot.Always("Haku33.nro"),
                    Targets = [new OutputTarget("switch/Haku33")],
                },
            ],
        },

        [ComponentKind.Jksv] = new ComponentDefinition
        {
            Kind = ComponentKind.Jksv,
            Category = ComponentCategory.Component,
            DisplayName = "JKSV",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Jksv",
                    Address = AssetSlot.Always(JksvRepo),
                    FileName = AssetSlot.Always("JKSV.nro"),
                    Targets = [new OutputTarget("switch/JKSV")],
                },
            ],
        },

        [ComponentKind.LunaApp] = new ComponentDefinition
        {
            Kind = ComponentKind.LunaApp,
            Category = ComponentCategory.Component,
            DisplayName = "Luna-App",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Luna",
                    Address = AssetSlot.Always(LunaAppRepo),
                    FileName = AssetSlot.Always("luna.zip"),
                },
                new AssetSlot
                {
                    // enctemplate.zip 从来没发布过，一直躺在仓库根目录 —— 走仓库文件树取。
                    Key = "LunaEnctemplate",
                    Address = AssetSlot.Always(LunaAppRepo),
                    FileName = AssetSlot.Always("enctemplate.zip"),
                    Source = SlotSource.RepoFile,
                    Targets = [new OutputTarget("config/luna/enctemplate")],
                },
            ],
        },

        [ComponentKind.SimpleModAlchemist] = new ComponentDefinition
        {
            Kind = ComponentKind.SimpleModAlchemist,
            Category = ComponentCategory.Component,
            DisplayName = "Simple Mod Alchemist",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "SimpleModAlchemist",
                    Address = AssetSlot.Always(SimpleModAlchemistRepo),
                    FileName = AssetSlot.Always("simple-mod-alchemist_x_x_x.zip"),
                },
            ],
        },

        [ComponentKind.SysClk] = new ComponentDefinition
        {
            Kind = ComponentKind.SysClk,
            Category = ComponentCategory.Component,
            DisplayName = "sys-clk",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "SysClk",
                    Address = AssetSlot.Always(SysClkRepo),
                    FileName = AssetSlot.Always("sys-clk.zip"),
                },
            ],
        },

        [ComponentKind.SysDvr] = new ComponentDefinition
        {
            Kind = ComponentKind.SysDvr,
            Category = ComponentCategory.Component,
            DisplayName = "SysDVR",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "SysDvr",
                    Address = AssetSlot.Always(SysDvrRepo),
                    FileName = AssetSlot.Always("SysDVR.zip"),
                },
            ],
        },

        [ComponentKind.LdnMitm] = new ComponentDefinition
        {
            Kind = ComponentKind.LdnMitm,
            Category = ComponentCategory.Component,
            DisplayName = "ldn_mitm",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "LdnMitm",
                    Address = AssetSlot.Always(LdnMitmRepo),
                    FileName = AssetSlot.Always("ldn_mitm_FW_x.x.x.zip"),
                },
            ],
        },

        [ComponentKind.Sphaira] = new ComponentDefinition
        {
            Kind = ComponentKind.Sphaira,
            Category = ComponentCategory.Component,
            DisplayName = "sphaira",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Sphaira",
                    Address = AssetSlot.Always(SphairaRepo),
                    FileName = AssetSlot.Always("sphaira.zip"),
                },
            ],
        },

        [ComponentKind.Fizeau] = new ComponentDefinition
        {
            Kind = ComponentKind.Fizeau,
            Category = ComponentCategory.Component,
            DisplayName = "Fizeau",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Fizeau",
                    Address = AssetSlot.Always(FizeauRepo),
                    FileName = AssetSlot.Always("Fizeau.zip"),
                },
            ],
        },

        [ComponentKind.NxActivityLog] = new ComponentDefinition
        {
            Kind = ComponentKind.NxActivityLog,
            Category = ComponentCategory.Component,
            DisplayName = "NX-Activity-Log",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "NxActivityLog",
                    Address = AssetSlot.Always(NxActivityLogRepo),
                    FileName = AssetSlot.Always("NX-Activity-Log.nro"),
                    Targets = [new OutputTarget("switch/NX-Activity-Log")],
                },
            ],
        },

        [ComponentKind.SwitchFirmwareDumper] = new ComponentDefinition
        {
            Kind = ComponentKind.SwitchFirmwareDumper,
            Category = ComponentCategory.Component,
            DisplayName = "Switch-Firmware-Dumper",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "FirmwareDumper",
                    Address = AssetSlot.Always(SwitchFirmwareDumperRepo),
                    FileName = AssetSlot.Always("Firmware-Dumper.zip"),
                    Targets = [new OutputTarget("switch/Firmware-Dumper")],
                },
            ],
        },

        [ComponentKind.BatteryDesyncFix] = new ComponentDefinition
        {
            Kind = ComponentKind.BatteryDesyncFix,
            Category = ComponentCategory.Component,
            DisplayName = "battery_desync_fix_nx",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "BatteryDesyncFix",
                    Address = AssetSlot.Always(BatteryDesyncFixRepo),
                    FileName = AssetSlot.Always("battery_desync_fix_vx.x.x.nro"),
                    Targets = [new OutputTarget("switch/battery_desync_fix_nx")],
                },
            ],
        },

        // ── 后台（5 个）─────────────────────────────────────────────
        // 全部落 out 根：这几个包的内部结构本来就是「SD 卡根目录的样子」
        // （atmosphere/…、config/…），实测确认，见 tools/probe-background.py。
        //
        // 上游资源名实测（2026-09-18）：
        //   olliz0r/sys-botbase  v2.5   → sys-botbase25.zip（用户写的 sys-botbasexx.zip 走通配）
        //   Koi-3088/usb-botbase v2.5   → atmosphere.7z（逐字）
        //   ndeadly/MissionControl v0.15.2 → MissionControl-0.15.2-master-d3941d43.zip（用户写的带 x 占位，走通配）
        //   o0Zz/sys-con         1.7.0  → sys-con-1.7.0.zip（用户写的带 x 占位，走通配）
        //   ELY3M/sys-ftpd       9      → release.zip（逐字；包内多一层 out/）

        [ComponentKind.SysBotbase] = new ComponentDefinition
        {
            Kind = ComponentKind.SysBotbase,
            Category = ComponentCategory.Background,
            DisplayName = "sys-botbase",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "SysBotbase",
                    Address = AssetSlot.Always(SysBotbaseRepo),
                    FileName = AssetSlot.Always("sys-botbasexx.zip"),
                    // 与 usb-botbase 同一个 title，二者互斥（见 AssetSlot.SysmoduleTitleId）。
                    SysmoduleTitleId = "430000000000000B",
                },
            ],
        },

        [ComponentKind.UsbBotbase] = new ComponentDefinition
        {
            Kind = ComponentKind.UsbBotbase,
            Category = ComponentCategory.Background,
            DisplayName = "usb-botbase",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "UsbBotbase",
                    Address = AssetSlot.Always(UsbBotbaseRepo),
                    FileName = AssetSlot.Always("atmosphere.7z"),
                    SysmoduleTitleId = "430000000000000B",
                },
            ],
        },

        [ComponentKind.MissionControl] = new ComponentDefinition
        {
            Kind = ComponentKind.MissionControl,
            Category = ComponentCategory.Background,
            DisplayName = "MissionControl",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "MissionControl",
                    Address = AssetSlot.Always(MissionControlRepo),
                    FileName = AssetSlot.Always("MissionControl-x.x.x-x-x.zip"),
                    SysmoduleTitleId = "010000000000bd00",
                },
            ],
        },

        [ComponentKind.SysCon] = new ComponentDefinition
        {
            Kind = ComponentKind.SysCon,
            Category = ComponentCategory.Background,
            DisplayName = "sys-con",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "SysCon",
                    Address = AssetSlot.Always(SysConRepo),
                    FileName = AssetSlot.Always("sys-con-x.x.x.zip"),
                    SysmoduleTitleId = "690000000000000D",
                },
            ],
        },

        [ComponentKind.SysFtpd] = new ComponentDefinition
        {
            Kind = ComponentKind.SysFtpd,
            Category = ComponentCategory.Background,
            DisplayName = "sys-ftpd",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "SysFtpd",
                    Address = AssetSlot.Always(SysFtpdRepo),
                    FileName = AssetSlot.Always("release.zip"),
                    // release.zip 里是 out/atmosphere/… 与 out/config/…：剥掉 out/ 一层，
                    // 再按用户要求「只取 atmosphere 与 config 两个文件夹」。
                    Extract = new ExtractPlan
                    {
                        StripPrefix = "out",
                        KeepTopLevelDirectories = ["atmosphere", "config"],
                    },
                    SysmoduleTitleId = "420000000000000E",
                },
            ],
        },

        // 上游实测（2026-09-18）：
        //   exelix11/SwitchThemeInjector nxt-3.0.1 → NXThemesInstaller.nro（逐字；另有 nxtheme-editor-web-selfhost.zip 等，不取）
        //   exelix11/theme-patches       master     → systemPatches/ 下 20 个扁平 .ips（列目录逐个下）
        //   J-D-K/Avatool                2.1.0      → Avatool.nro（逐字）
        //   masagrator/sys-ticon         1.0.8      → sys-ticon.zip（逐字；⚠️ 同一 Release 里还有个 sys-ticon-log.zip）
        [ComponentKind.NxThemesInstaller] = new ComponentDefinition
        {
            Kind = ComponentKind.NxThemesInstaller,
            Category = ComponentCategory.Theme,
            DisplayName = "NXThemes Installer",
            Repos = [],
            Options = [],
            // 用户要求「两个同框绑定」：安装器本体与那 20 个补丁在**同一个组件**里，
            // 勾一次两个都下。分成两个组件会让「装了安装器却没有补丁」成为可能，
            // 而补丁正是安装器的工作对象。
            Slots =
            [
                new AssetSlot
                {
                    Key = "NxThemesInstaller",
                    Address = AssetSlot.Always(SwitchThemeInjectorRepo),
                    FileName = AssetSlot.Always("NXThemesInstaller.nro"),
                    Targets = [new OutputTarget("switch/NXThemesInstaller")],
                },
                new AssetSlot
                {
                    Key = "ThemePatches",
                    Address = AssetSlot.Always(ThemePatchesRepo),
                    // 目录槽：这个「文件名」框装的是**仓库内的目录路径**（见 SlotSource.RepoDirectory）。
                    FileName = AssetSlot.Always("systemPatches"),
                    Source = SlotSource.RepoDirectory,
                    Targets = [new OutputTarget("themes/systemPatches")],
                },
            ],
        },

        [ComponentKind.Avatool] = new ComponentDefinition
        {
            Kind = ComponentKind.Avatool,
            Category = ComponentCategory.Theme,
            DisplayName = "Avatool",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Avatool",
                    Address = AssetSlot.Always(AvatoolRepo),
                    FileName = AssetSlot.Always("Avatool.nro"),
                    Targets = [new OutputTarget("switch/Avatool")],
                },
            ],
        },

        [ComponentKind.SysTicon] = new ComponentDefinition
        {
            Kind = ComponentKind.SysTicon,
            Category = ComponentCategory.Theme,
            DisplayName = "sys-ticon",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "SysTicon",
                    Address = AssetSlot.Always(SysTiconRepo),
                    FileName = AssetSlot.Always("sys-ticon.zip"),
                    // 实测（2026-09-18，下 1.0.8 的 sys-ticon.zip 看包内结构）：
                    //   atmosphere/contents/00FF747765616BFF/{exefs.nsp, toolbox.json, flags/boot2.flag}
                    // 注意这不是猜的 —— 一开始我按别的 sysmodule 的样式随手写了一个值，
                    // 实测才发现是 00FF747765616BFF（twili 的通用 title）。
                    SysmoduleTitleId = "00FF747765616BFF",
                },
            ],
        },

        // ── 底层（2 个）─────────────────────────────────────────────
        // 两个散装 payload，都落 out/bootloader/payloads —— 与 hekate 自带的 payloads/
        // 是同一个目录，所以它们是**普通文件**（不标 IsPayload，理由见 RepoSpec 声明处）。
        //
        // 上游实测（2026-09-18）：
        //   zdm65477730/Lockpick_RCMDecScots v2.0.0 → Lockpick_RCM.bin（114357 字节，逐字）
        //   zdm65477730/TegraExplorer        v4.2.0 → TegraExplorer.bin（153401 字节，逐字）
        // 两个 Release 都**只有一个资源**，不存在「同名变体」的挑选问题。
        [ComponentKind.LockpickRcm] = new ComponentDefinition
        {
            Kind = ComponentKind.LockpickRcm,
            Category = ComponentCategory.LowLevel,
            DisplayName = "Lockpick_RCM",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "LockpickRcm",
                    Address = AssetSlot.Always(LockpickRcmRepo),
                    FileName = AssetSlot.Always("Lockpick_RCM.bin"),
                    Targets = [new OutputTarget("bootloader/payloads")],
                },
            ],
        },

        [ComponentKind.TegraExplorer] = new ComponentDefinition
        {
            Kind = ComponentKind.TegraExplorer,
            Category = ComponentCategory.LowLevel,
            DisplayName = "TegraExplorer",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "TegraExplorer",
                    Address = AssetSlot.Always(TegraExplorerRepo),
                    FileName = AssetSlot.Always("TegraExplorer.bin"),
                    Targets = [new OutputTarget("bootloader/payloads")],
                },
            ],
        },

        // ── Ultrahand插件（6 个）────────────────────────────────────
        // 六个 Tesla 覆盖层（.ovl），落点都是 **out 根** —— 包内路径已经以 SD 卡根为基准
        // （实测：emuiibo.zip 是 atmosphere/contents/… + switch/.overlays/…；
        //  Zing / QuickNTP 是 switch/.overlays/… + config/<名字>/…），整棵铺开即可。
        //
        // ⚠️ 这一组里有**四个**包内含 sysmodule（实测：逐包扫 <c>atmosphere/contents/&lt;16 位&gt;/</c>）：
        //    emuiibo 的 <c>0100000000000352</c>、KeyX 的 <c>0100000000251020</c> 与
        //    <c>4100000002025924</c>、ReverseNX-RT 的 <c>0000000000534C56</c>
        //    （= ASCII 的 "SLV"，即 SaltySD —— ReverseNX-RT 依赖它）。但**都不声明 SysmoduleTitleId**：
        //    ① KeyX 装**两个** title，而这个字段是单值的，写一个就是半个真话；
        //    ② 按现有约定，只有「冲突可预期」的才声明（组件类里的 SysClk / SysDvr / LdnMitm
        //       同样是 sysmodule，同样没声明）。
        //    实测这四组 title 与已声明的五个（430000000000000B ×2 / 010000000000bd00 /
        //    690000000000000D / 420000000000000E / 00FF747765616BFF）**没有交集**，
        //    所以不声明不会漏掉任何真实冲突。要收紧这条约定，得先把那个字段改成多值。
        [ComponentKind.Emuiibo] = new ComponentDefinition
        {
            Kind = ComponentKind.Emuiibo,
            Category = ComponentCategory.UltrahandPlugin,
            DisplayName = "emuiibo",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Emuiibo",
                    Address = AssetSlot.Always(EmuiiboRepo),
                    FileName = AssetSlot.Always("emuiibo.zip"),
                },
            ],
        },

        [ComponentKind.Zing] = new ComponentDefinition
        {
            Kind = ComponentKind.Zing,
            Category = ComponentCategory.UltrahandPlugin,
            DisplayName = "Zing",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Zing",
                    Address = AssetSlot.Always(ZingRepo),
                    FileName = AssetSlot.Always("Zing.zip"),
                },
            ],
        },

        [ComponentKind.ReverseNxRt] = new ComponentDefinition
        {
            Kind = ComponentKind.ReverseNxRt,
            Category = ComponentCategory.UltrahandPlugin,
            DisplayName = "ReverseNX-RT",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "ReverseNxRt",
                    Address = AssetSlot.Always(ReverseNxRtRepo),
                    FileName = AssetSlot.Always("ReverseNX-RT.zip"),
                },
            ],
        },

        [ComponentKind.StatusMonitorOverlay] = new ComponentDefinition
        {
            Kind = ComponentKind.StatusMonitorOverlay,
            Category = ComponentCategory.UltrahandPlugin,
            DisplayName = "Status-Monitor-Overlay",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    // ⚠️ 仓库叫 Status-Monitor-Overlay，包叫 StatusMonitor.zip —— 上游的命名，别改。
                    Key = "StatusMonitor",
                    Address = AssetSlot.Always(StatusMonitorOverlayRepo),
                    FileName = AssetSlot.Always("StatusMonitor.zip"),
                },
            ],
        },

        [ComponentKind.QuickNtp] = new ComponentDefinition
        {
            Kind = ComponentKind.QuickNtp,
            Category = ComponentCategory.UltrahandPlugin,
            DisplayName = "QuickNTP",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "QuickNtp",
                    Address = AssetSlot.Always(QuickNtpRepo),
                    FileName = AssetSlot.Always("QuickNTP.zip"),
                },
            ],
        },

        [ComponentKind.KeyX] = new ComponentDefinition
        {
            Kind = ComponentKind.KeyX,
            Category = ComponentCategory.UltrahandPlugin,
            DisplayName = "KeyX",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "KeyX",
                    Address = AssetSlot.Always(KeyXRepo),
                    // 上游两个包都真实存在（只差 7 字节），取哪个是**用户意图**，
                    // 判据只能是界面语言 —— 见 KeyXFileName 的注释。
                    FileName = KeyXFileName,
                },
            ],
        },

        // ── 学习（7 个）──────────────────────────────────────────────
        // 与 CFW 无关的附加软件。落点分两类，判据是**包内结构**（2026-09-18 逐包实测，
        // 用 HTTP Range 只取 ZIP 中央目录拿到的完整条目清单）：
        //
        //   AtmoXL    switch/AtmoXL-Titel-Installer/AtmoXL-Titel-Installer.nro  → out 根
        //   Awoo      switch/Awoo-Installer/Awoo-Installer.nro                  → out 根
        //   linkalho  switch/linkalho/linkalho.nro（CN/EN 两个仓库结构相同）    → out 根
        //   90DNS     散装 .nro（不是压缩包）                                   → switch/Switch_90DNS_tester
        //   wiliwili  wiliwili/{README.md, wiliwili.nro, 安装必读.txt}          → switch/wiliwili（只取 .nro）
        //   Moonlight 散装 .nro（不是压缩包）                                   → switch/Moonlight-Switch
        //   TriPlayer atmosphere/contents/4200000000000FFF/… + switch/…         → out 根
        //
        // ⚠️ TriPlayer 的包里**含一个 sysmodule**（title <c>4200000000000FFF</c>），与已声明的五个
        //    （<c>420000000000000E</c> 是 sys-ftpd，只差末两位，**不是同一个**）无交集 ⇒ 按现有约定不声明。
        //    这条交集结论由 `tools/check-upstream-facts.py` 的 ④ 联网复核。
        [ComponentKind.AtmoXL] = new ComponentDefinition
        {
            Kind = ComponentKind.AtmoXL,
            Category = ComponentCategory.Learning,
            DisplayName = "AtmoXL-Titel-Installer",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "AtmoXL",
                    Address = AssetSlot.Always(AtmoXlRepo),
                    // 实测（Range 取中央目录）：包内是 switch/AtmoXL-Titel-Installer/… ⇒ out 根正确。
                    FileName = AssetSlot.Always("AtmoXL-Titel-Installer.zip"),
                },
            ],
        },

        [ComponentKind.Awoo] = new ComponentDefinition
        {
            Kind = ComponentKind.Awoo,
            Category = ComponentCategory.Learning,
            DisplayName = "Awoo-Installer",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Awoo",
                    Address = AssetSlot.Always(AwooRepo),
                    FileName = AssetSlot.Always("Awoo-Installer.zip"),
                },
            ],
        },

        [ComponentKind.Linkalho] = new ComponentDefinition
        {
            Kind = ComponentKind.Linkalho,
            Category = ComponentCategory.Learning,
            DisplayName = "linkalho",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Linkalho",
                    // ⚠️ 地址与文件名**都**按界面语言求值，且共用同一个判据（见 LinkalhoRepo 的说明）。
                    Address = LinkalhoRepo,
                    FileName = LinkalhoFileName,
                },
            ],
        },

        [ComponentKind.Dns90Tester] = new ComponentDefinition
        {
            Kind = ComponentKind.Dns90Tester,
            Category = ComponentCategory.Learning,
            DisplayName = "Switch_90DNS_tester",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Dns90Tester",
                    Address = AssetSlot.Always(Dns90TesterRepo),
                    FileName = AssetSlot.Always("Switch_90DNS_tester.nro"),
                    // 上游只发散装 .nro（不打包）⇒ 直接落 homebrew 目录。
                    Targets = [new OutputTarget("switch/Switch_90DNS_tester")],
                },
            ],
        },

        [ComponentKind.Wiliwili] = new ComponentDefinition
        {
            Kind = ComponentKind.Wiliwili,
            Category = ComponentCategory.Learning,
            DisplayName = "wiliwili",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Wiliwili",
                    Address = AssetSlot.Always(WiliwiliRepo),
                    // ⚠️ 这个 Release 有 **17 个资源**（Linux/Windows/macOS/PS4/PSV 各平台），
                    //    只有 wiliwili-NintendoSwitch.zip 是我们的 —— 逐字命中，不靠猜。
                    FileName = AssetSlot.Always("wiliwili-NintendoSwitch.zip"),
                    // ⚠️ 实测（Range 取中央目录）：包内是 **wiliwili/wiliwili.nro**（多一层子目录），
                    //    所以必须 StripPrefix 剥掉它，否则会落成 out/switch/wiliwili/wiliwili/wiliwili.nro。
                    //    KeepFileNames 比的是**文件名**（剥前缀之后），不是包内路径 —— 与
                    //    KeepTopLevelDirectories 同一套写法（见 ExtractPlan.Map）。
                    Extract = new ExtractPlan
                    {
                        StripPrefix = "wiliwili",
                        KeepFileNames = ["wiliwili.nro"],
                    },
                    Targets = [new OutputTarget("switch/wiliwili")],
                },
            ],
        },

        [ComponentKind.Moonlight] = new ComponentDefinition
        {
            Kind = ComponentKind.Moonlight,
            Category = ComponentCategory.Learning,
            DisplayName = "Moonlight-Switch",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "Moonlight",
                    Address = AssetSlot.Always(MoonlightRepo),
                    // ⚠️ 上游 Release 里还有 Debug.elf / 两个 Windows zip / 一个 apk，
                    //    以及旧版的近名变体 Moonlight-Switch-hos21.nro（v1.3.4 起才有）——
                    //    逐字命中 Moonlight-Switch.nro 时必须只中那一个。
                    FileName = AssetSlot.Always("Moonlight-Switch.nro"),
                    Targets = [new OutputTarget("switch/Moonlight-Switch")],
                },
            ],
        },

        [ComponentKind.TriPlayer] = new ComponentDefinition
        {
            Kind = ComponentKind.TriPlayer,
            Category = ComponentCategory.Learning,
            DisplayName = "TriPlayer",
            Repos = [],
            Options = [],
            Slots =
            [
                new AssetSlot
                {
                    Key = "TriPlayer",
                    Address = AssetSlot.Always(TriPlayerRepo),
                    // 上游文件名带版本号（triplayer-1.1.1.zip），所以走**通配**逐槽声明，
                    // 而不是把版本号写死 —— 版本一升，写死的那串就永远命中不了。
                    FileName = AssetSlot.Always("triplayer-x.x.x.zip"),
                },
            ],
        },
    };

    public static ComponentDefinition Get(ComponentKind kind) => Definitions[kind];

    public static IReadOnlyCollection<ComponentDefinition> All => Definitions.Values;

    /// <summary>
    /// 一组「装同一个 sysmodule title」的组件 —— 它们必然互相覆盖，只能留一个。
    /// </summary>
    /// <param name="TitleId">冲突的 title（<c>atmosphere/contents/&lt;这个&gt;</c>）。</param>
    /// <param name="Kinds">撞在一起的全部组件（≥ 2 个）。</param>
    public sealed record TitleConflict(string TitleId, IReadOnlyList<ComponentKind> Kinds);

    /// <summary>
    /// 当前选择里有没有「装同一个 title」的组件。
    ///
    /// 为什么做成**穷举规则**而不是写死「sys-botbase 与 usb-botbase 互斥」：
    /// 判据来自各槽自己声明的 <see cref="AssetSlot.SysmoduleTitleId"/>（实测得来），
    /// 所以将来再加一个撞车的 sysmodule，不需要改这里任何一行代码就会自动被抓出来。
    /// 写死一对名字的话，第三对撞车时会**静默产出**一张「后合并的那个赢」的卡 ——
    /// 两个都勾着、界面也没报错，用户完全看不出来。
    ///
    /// 名单从 <see cref="All"/> 现取（不是手抄），与界面上显示的组件集合同源。
    /// </summary>
    public static IReadOnlyList<TitleConflict> FindTitleConflicts(WizardOptions options) =>
        FindTitleConflicts(All.Where(definition => options.IsSelected(definition.Kind)).Select(definition => definition.Kind));

    /// <summary>
    /// 同上，但直接收「当前被勾选的那些组件」。
    ///
    /// 单独留一个入口是为了让界面侧也能用**同一套判据**：界面在每次勾选变化时手上有的是
    /// <c>ComponentViewModel</c> 列表，构造一个 <see cref="WizardOptions"/> 只为问这一个问题
    /// 既绕又容易与生成端走岔。判据只有这一份实现，两边都调它。
    /// </summary>
    public static IReadOnlyList<TitleConflict> FindTitleConflicts(IEnumerable<ComponentKind> selectedKinds)
    {
        var wanted = selectedKinds.ToHashSet();

        return All
            .Where(definition => wanted.Contains(definition.Kind))
            .SelectMany(definition => definition.Slots
                .Where(slot => !string.IsNullOrWhiteSpace(slot.SysmoduleTitleId))
                .Select(slot => (definition.Kind, Title: slot.SysmoduleTitleId!)))
            .GroupBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TitleConflict(
                group.Key,
                group.Select(entry => entry.Kind).Distinct().ToList()))
            .Where(conflict => conflict.Kinds.Count > 1)
            .ToList();
    }

    /// <summary>
    /// 界面上的分类**顺序** —— 直接取枚举的声明序，不另写清单。
    ///
    /// 之所以敢依赖声明序：顺序是有意义的（框架在最前），而写死一份数组迟早会和枚举脱节 ——
    /// 新增一个分类却忘了加进数组，那一组就**整组不显示**，且没有任何东西会报错。
    /// 顺序本身由 <see cref="ComponentCategory"/> 的注释负责说明，改枚举位置即改界面顺序。
    /// </summary>
    public static IReadOnlyList<ComponentCategory> Categories => Enum.GetValues<ComponentCategory>();

    /// <summary>分类标题的语言键。命名规则只有这一处，界面与护栏共用。</summary>
    public static string CategoryTitleKey(ComponentCategory category) => "Category." + category;

    /// <summary>
    /// 一个组件在界面上显示的名字。
    ///
    /// 有 <see cref="ComponentDefinition.DisplayName"/> 就直接用它（那类插件三语同名，
    /// 见该属性的注释）；否则走语言键。**两处共用这一个入口** —— 界面标题与
    /// 「分类里有没有这个组件」的护栏都从这里取，免得一处显示字面量、一处显示语言键。
    /// </summary>
    public static string ResolveComponentTitle(ComponentDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.DisplayName)
            ? Localization.LocalizationService.Instance["Component." + definition.Kind + ".Name"]
            : definition.DisplayName!;

    /// <summary>同 <see cref="ResolveComponentTitle"/>，用于说明文字。</summary>
    public static string ResolveComponentDescription(ComponentDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.DisplayDescription)
            ? Localization.LocalizationService.Instance["Component." + definition.Kind + ".Desc"]
            : definition.DisplayDescription!;

    // ── Atmosphere ──────────────────────────────────────────────
    private static List<OptionDefinition> BuildAtmosphereOptions() =>
    [
        // exosphere.ini
        new()
        {
            Key = "debugmode_user", TargetFile = "exosphere.ini", Section = "exosphere",
            GroupKey = "Group.Exosphere",
            LabelKey = "Opt.Atmosphere.debugmode_user", DescriptionKey = "Opt.Atmosphere.debugmode_user.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "disable_user_exception_handlers", TargetFile = "exosphere.ini", Section = "exosphere",
            GroupKey = "Group.Exosphere",
            LabelKey = "Opt.Atmosphere.disable_user_exception_handlers",
            DescriptionKey = "Opt.Atmosphere.disable_user_exception_handlers.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "enable_user_pmu_access", TargetFile = "exosphere.ini", Section = "exosphere",
            GroupKey = "Group.Exosphere",
            LabelKey = "Opt.Atmosphere.enable_user_pmu_access",
            DescriptionKey = "Opt.Atmosphere.enable_user_pmu_access.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "allow_writing_to_cal_sysmmc", TargetFile = "exosphere.ini", Section = "exosphere",
            GroupKey = "Group.Exosphere",
            LabelKey = "Opt.Atmosphere.allow_writing_to_cal_sysmmc",
            DescriptionKey = "Opt.Atmosphere.allow_writing_to_cal_sysmmc.Desc",
            DefaultValue = "0",
        },

        // system_settings.ini
        new()
        {
            Key = "upload_enabled", TargetFile = "atmosphere/config/system_settings.ini", Section = "eupld",
            GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.upload_enabled", DescriptionKey = "Opt.Atmosphere.upload_enabled.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "usb30_force_enabled", TargetFile = "atmosphere/config/system_settings.ini", Section = "usb",
            GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.usb30_force_enabled",
            DescriptionKey = "Opt.Atmosphere.usb30_force_enabled.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "ease_nro_restriction", TargetFile = "atmosphere/config/system_settings.ini", Section = "ro",
            GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.ease_nro_restriction",
            DescriptionKey = "Opt.Atmosphere.ease_nro_restriction.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "power_menu_reboot_function", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "atmosphere", GroupKey = "Group.SystemSettings", Editor = OptionEditor.ComboBox,
            IniValuePrefix = "str",
            LabelKey = "Opt.Atmosphere.power_menu_reboot_function",
            DescriptionKey = "Opt.Atmosphere.power_menu_reboot_function.Desc",
            DefaultValue = "payload",
            Choices =
            [
                new OptionChoice("normal", "Opt.Value.Normal"),
                new OptionChoice("payload", "Opt.Value.Payload"),
                new OptionChoice("rcm", "Opt.Value.Rcm"),
            ],
        },
        new()
        {
            Key = "dmnt_cheats_enabled_by_default", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "atmosphere", GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.dmnt_cheats_enabled_by_default",
            DescriptionKey = "Opt.Atmosphere.dmnt_cheats_enabled_by_default.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "dmnt_always_save_cheat_toggles", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "atmosphere", GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.dmnt_always_save_cheat_toggles",
            DescriptionKey = "Opt.Atmosphere.dmnt_always_save_cheat_toggles.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "enable_dns_mitm", TargetFile = "atmosphere/config/system_settings.ini", Section = "atmosphere",
            GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.enable_dns_mitm", DescriptionKey = "Opt.Atmosphere.enable_dns_mitm.Desc",
            // 默认**关**（用户 2026-09-17 决定，推翻了「默认值 = 你那 8 份 ini」的既有约定里这一项）。
            // 理由：它与「两个 90DNS 都不勾 ⇒ mitm 关」的联动规则必须**起点一致** ——
            // 否则界面一打开就是「90DNS 全关、mitm 却开着」，规则与默认值互相矛盾。
            // 勾任一 90DNS 会自动打开它（见 MainViewModel.ApplyDnsMitmCoupling）。
            // ⚠️ 改这里要同步改 CheckUserConfigBaseline 的期望值，并有 Check90DnsSwitches 钉着起点一致性。
            DefaultValue = "0",
        },
        new()
        {
            Key = "add_defaults_to_dns_hosts", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "atmosphere", GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.add_defaults_to_dns_hosts",
            DescriptionKey = "Opt.Atmosphere.add_defaults_to_dns_hosts.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "enable_log_manager", TargetFile = "atmosphere/config/system_settings.ini", Section = "atmosphere",
            GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.enable_log_manager",
            DescriptionKey = "Opt.Atmosphere.enable_log_manager.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "enable_htc", TargetFile = "atmosphere/config/system_settings.ini", Section = "atmosphere",
            GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.enable_htc", DescriptionKey = "Opt.Atmosphere.enable_htc.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "enable_external_bluetooth_db", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "atmosphere", GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.enable_external_bluetooth_db",
            DescriptionKey = "Opt.Atmosphere.enable_external_bluetooth_db.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "enable_am_debug_mode", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "atmosphere", GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.enable_am_debug_mode",
            DescriptionKey = "Opt.Atmosphere.enable_am_debug_mode.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "fatal_auto_reboot_interval", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "atmosphere", GroupKey = "Group.SystemSettings", Editor = OptionEditor.Number,
            IniValuePrefix = "u64", Minimum = 0, Maximum = 60000, Step = 100,
            LabelKey = "Opt.Atmosphere.fatal_auto_reboot_interval",
            DescriptionKey = "Opt.Atmosphere.fatal_auto_reboot_interval.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "applet_heap_size", TargetFile = "atmosphere/config/system_settings.ini", Section = "hbloader",
            GroupKey = "Group.SystemSettings", Editor = OptionEditor.Number, IniValuePrefix = "u64",
            Minimum = 0, Maximum = 4294967296, Step = 1048576,
            LabelKey = "Opt.Atmosphere.applet_heap_size",
            DescriptionKey = "Opt.Atmosphere.applet_heap_size.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "applet_heap_reservation_size", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "hbloader", GroupKey = "Group.SystemSettings", Editor = OptionEditor.TextBox,
            IniValuePrefix = "u64",
            LabelKey = "Opt.Atmosphere.applet_heap_reservation_size",
            DescriptionKey = "Opt.Atmosphere.applet_heap_reservation_size.Desc",
            DefaultValue = "0x8600000",
        },

        // 以下 7 项来自用户实际使用的 system_settings.ini，默认值即用户当前取值
        new()
        {
            Key = "enable_sd_card_logging", TargetFile = "atmosphere/config/system_settings.ini", Section = "lm",
            GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.enable_sd_card_logging",
            DescriptionKey = "Opt.Atmosphere.enable_sd_card_logging.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "sd_card_log_output_directory", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "lm", GroupKey = "Group.SystemSettings", Editor = OptionEditor.TextBox,
            IniValuePrefix = "str",
            LabelKey = "Opt.Atmosphere.sd_card_log_output_directory",
            DescriptionKey = "Opt.Atmosphere.sd_card_log_output_directory.Desc",
            DefaultValue = "atmosphere/binlogs",
        },
        new()
        {
            Key = "enable_hbl_bis_write", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "atmosphere", GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.enable_hbl_bis_write",
            DescriptionKey = "Opt.Atmosphere.enable_hbl_bis_write.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "enable_hbl_cal_read", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "atmosphere", GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.enable_hbl_cal_read",
            DescriptionKey = "Opt.Atmosphere.enable_hbl_cal_read.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "fsmitm_redirect_saves_to_sd", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "atmosphere", GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.fsmitm_redirect_saves_to_sd",
            DescriptionKey = "Opt.Atmosphere.fsmitm_redirect_saves_to_sd.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "enable_deprecated_hid_mitm", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "atmosphere", GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.enable_deprecated_hid_mitm",
            DescriptionKey = "Opt.Atmosphere.enable_deprecated_hid_mitm.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "enable_dns_mitm_debug_log", TargetFile = "atmosphere/config/system_settings.ini",
            Section = "atmosphere", GroupKey = "Group.SystemSettings", IniValuePrefix = "u8",
            LabelKey = "Opt.Atmosphere.enable_dns_mitm_debug_log",
            DescriptionKey = "Opt.Atmosphere.enable_dns_mitm_debug_log.Desc",
            DefaultValue = "0",
        },

        // stratosphere.ini
        new()
        {
            Key = "nogc", TargetFile = "atmosphere/config/stratosphere.ini", Section = "stratosphere",
            GroupKey = "Group.Stratosphere", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Atmosphere.nogc", DescriptionKey = "Opt.Atmosphere.nogc.Desc",
            DefaultValue = "auto",
            Choices =
            [
                new OptionChoice("auto", "Opt.Value.Auto"),
                new OptionChoice("1", "Opt.Value.On"),
                new OptionChoice("0", "Opt.Value.Off"),
            ],
        },

        // override_config.ini
        new()
        {
            Key = "override_key", TargetFile = "atmosphere/config/override_config.ini", Section = "default_config",
            GroupKey = "Group.OverrideConfig", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Atmosphere.override_key", DescriptionKey = "Opt.Atmosphere.override_key.Desc",
            // 用户实际用的是 "!L"：按住 L 时**不**进入 Homebrew Menu（! 前缀即反转语义）。
            DefaultValue = "!L",
            Choices = BuildOverrideKeyChoices(),
        },
        new()
        {
            Key = "cheat_enable_key", TargetFile = "atmosphere/config/override_config.ini",
            Section = "default_config", GroupKey = "Group.OverrideConfig", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Atmosphere.cheat_enable_key",
            DescriptionKey = "Opt.Atmosphere.cheat_enable_key.Desc",
            DefaultValue = "!L",
            Choices = BuildOverrideKeyChoices(),
        },

        // override_config.ini → [hbl_config]：Homebrew Menu 的启动方式。
        //
        // ⚠️ 这五个的 Key 都带 `hbl_` 前缀，因为**存储键必须在本组件内唯一**，而
        // `[default_config]` 已经占了 `override_key`。写进 ini 时的键名是另一回事，
        // 由 ConfigGenerator 的调用点给出（`overrides.Set("hbl_config", "override_key", …)`）。
        // 两边的对应关系由 CheckHblConfigOptionsAreWired 从调用点扫出来核对，不手抄。
        new()
        {
            Key = "hbl_program_id", TargetFile = "atmosphere/config/override_config.ini",
            Section = "hbl_config", GroupKey = "Group.OverrideConfig", Editor = OptionEditor.TextBox,
            LabelKey = "Opt.Atmosphere.hbl_program_id",
            DescriptionKey = "Opt.Atmosphere.hbl_program_id.Desc",
            DefaultValue = HblProgramIdDefault,
        },
        new()
        {
            Key = "hbl_override_any_app", TargetFile = "atmosphere/config/override_config.ini",
            Section = "hbl_config", GroupKey = "Group.OverrideConfig", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Atmosphere.hbl_override_any_app",
            DescriptionKey = "Opt.Atmosphere.hbl_override_any_app.Desc",
            // 刻意用下拉而不是勾选框：这个键的取值语法是**字面量 true/false**，
            // 而勾选框统一产出 "1"/"0"。写 "1" 能不能被 Atmosphere 认下来没有把握，
            // 照抄官方模板的 true 才是零风险的做法。
            DefaultValue = "true",
            Choices = [new OptionChoice("true", "true"), new OptionChoice("false", "false")],
        },
        new()
        {
            Key = "hbl_path", TargetFile = "atmosphere/config/override_config.ini",
            Section = "hbl_config", GroupKey = "Group.OverrideConfig", Editor = OptionEditor.TextBox,
            LabelKey = "Opt.Atmosphere.hbl_path",
            DescriptionKey = "Opt.Atmosphere.hbl_path.Desc",
            DefaultValue = HblPathDefault,
        },
        new()
        {
            Key = "hbl_override_key", TargetFile = "atmosphere/config/override_config.ini",
            Section = "hbl_config", GroupKey = "Group.OverrideConfig", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Atmosphere.hbl_override_key",
            DescriptionKey = "Opt.Atmosphere.hbl_override_key.Desc",
            DefaultValue = "!R",
            Choices = BuildOverrideKeyChoices(),
        },
        new()
        {
            Key = "hbl_override_any_app_key", TargetFile = "atmosphere/config/override_config.ini",
            Section = "hbl_config", GroupKey = "Group.OverrideConfig", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Atmosphere.hbl_override_any_app_key",
            DescriptionKey = "Opt.Atmosphere.hbl_override_any_app_key.Desc",
            DefaultValue = "R",
            Choices = BuildOverrideKeyChoices(),
        },
    ];

    /// <summary>
    /// override_config.ini 里 <c>override_key</c> / <c>cheat_enable_key</c> 的候选项。
    ///
    /// Atmosphere 的语义：写 <c>R</c> 表示「按住 R 时启用该行为」，写 <c>!R</c> 表示
    /// 「按住 R 时**不**启用」。两种写法都是合法的，所以候选项成对给出 ——
    /// 只给不带 <c>!</c> 的那一半会让用户没法表达自己实际在用的配置。
    /// </summary>
    private static List<OptionChoice> BuildOverrideKeyChoices() =>
    [
        new OptionChoice("!L", "!L"),
        new OptionChoice("!R", "!R"),
        new OptionChoice("!X", "!X"),
        new OptionChoice("!Y", "!Y"),
        new OptionChoice("L", "L"),
        new OptionChoice("R", "R"),
        new OptionChoice("X", "X"),
        new OptionChoice("Y", "Y"),
        new OptionChoice("ZL+ZR", "ZL+ZR"),
        new OptionChoice("!ZL+ZR", "!ZL+ZR"),
        new OptionChoice("DUP", "D-Pad ↑"),
        new OptionChoice("!DUP", "!D-Pad ↑"),
    ];

    // ── Hekate ──────────────────────────────────────────────────
    private static List<OptionDefinition> BuildHekateOptions() =>
    [
        new()
        {
            Key = "autoboot", TargetFile = "bootloader/hekate_ipl.ini", Section = "config",
            GroupKey = "Group.HekateConfig", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Hekate.autoboot", DescriptionKey = "Opt.Hekate.autoboot.Desc",
            DefaultValue = "0",
            // 候选项由 MainViewModel.RefreshAutobootChoices() 按当前勾选的引导项动态生成，
            // 这里的 "0" 只是占位。必须标 DynamicChoices，否则存档里的 "1"/"2"/"3"
            // 会在 OptionViewModel 构造时被这条占位列表自愈成 "0"。
            DynamicChoices = true,
            Choices = [new OptionChoice("0", "Opt.Value.Off")],
        },
        new()
        {
            Key = "autoboot_list", TargetFile = "bootloader/hekate_ipl.ini", Section = "config",
            GroupKey = "Group.HekateConfig",
            LabelKey = "Opt.Hekate.autoboot_list", DescriptionKey = "Opt.Hekate.autoboot_list.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "bootwait", TargetFile = "bootloader/hekate_ipl.ini", Section = "config",
            GroupKey = "Group.HekateConfig", Editor = OptionEditor.Number, Minimum = 0, Maximum = 20,
            LabelKey = "Opt.Hekate.bootwait", DescriptionKey = "Opt.Hekate.bootwait.Desc",
            DefaultValue = "2",
        },
        new()
        {
            Key = "autohosoff", TargetFile = "bootloader/hekate_ipl.ini", Section = "config",
            GroupKey = "Group.HekateConfig", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Hekate.autohosoff", DescriptionKey = "Opt.Hekate.autohosoff.Desc",
            DefaultValue = "2",
            Choices =
            [
                new OptionChoice("0", "Opt.Hekate.HosOff.Disable"),
                new OptionChoice("1", "Opt.Hekate.HosOff.LogoThenOff"),
                new OptionChoice("2", "Opt.Hekate.HosOff.Immediate"),
            ],
        },
        new()
        {
            Key = "autonogc", TargetFile = "bootloader/hekate_ipl.ini", Section = "config",
            GroupKey = "Group.HekateConfig",
            LabelKey = "Opt.Hekate.autonogc", DescriptionKey = "Opt.Hekate.autonogc.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "updater2p", TargetFile = "bootloader/hekate_ipl.ini", Section = "config",
            GroupKey = "Group.HekateConfig",
            LabelKey = "Opt.Hekate.updater2p", DescriptionKey = "Opt.Hekate.updater2p.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "backlight", TargetFile = "bootloader/hekate_ipl.ini", Section = "config",
            GroupKey = "Group.HekateConfig", Editor = OptionEditor.Number, Minimum = 0, Maximum = 255,
            LabelKey = "Opt.Hekate.backlight", DescriptionKey = "Opt.Hekate.backlight.Desc",
            DefaultValue = "100",
        },
        new()
        {
            Key = "noticker", TargetFile = "bootloader/hekate_ipl.ini", Section = "config",
            GroupKey = "Group.HekateConfig",
            LabelKey = "Opt.Hekate.noticker", DescriptionKey = "Opt.Hekate.noticker.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "bootprotect", TargetFile = "bootloader/hekate_ipl.ini", Section = "config",
            GroupKey = "Group.HekateConfig",
            LabelKey = "Opt.Hekate.bootprotect", DescriptionKey = "Opt.Hekate.bootprotect.Desc",
            DefaultValue = "0",
        },

        // 引导项附加参数
        new()
        {
            Key = "kip1_atmosphere_kips", GroupKey = "Group.HekateBoot",
            LabelKey = "Opt.Hekate.kip1_atmosphere_kips",
            DescriptionKey = "Opt.Hekate.kip1_atmosphere_kips.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "kip1patch_nosigchk", GroupKey = "Group.HekateBoot",
            LabelKey = "Opt.Hekate.kip1patch_nosigchk",
            DescriptionKey = "Opt.Hekate.kip1patch_nosigchk.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "usb3force", GroupKey = "Group.HekateBoot",
            LabelKey = "Opt.Hekate.usb3force", DescriptionKey = "Opt.Hekate.usb3force.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "nouserexceptions", GroupKey = "Group.HekateBoot",
            LabelKey = "Opt.Hekate.nouserexceptions",
            DescriptionKey = "Opt.Hekate.nouserexceptions.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "userpmu", GroupKey = "Group.HekateBoot",
            LabelKey = "Opt.Hekate.userpmu", DescriptionKey = "Opt.Hekate.userpmu.Desc",
            DefaultValue = "0",
        },

        // nyx.ini
        new()
        {
            Key = "themebg", TargetFile = "bootloader/nyx.ini", Section = "config",
            GroupKey = "Group.Nyx", Editor = OptionEditor.TextBox,
            LabelKey = "Opt.Hekate.themebg", DescriptionKey = "Opt.Hekate.themebg.Desc",
            DefaultValue = "2d2d2d",
        },
        new()
        {
            Key = "themecolor", TargetFile = "bootloader/nyx.ini", Section = "config",
            GroupKey = "Group.Nyx", Editor = OptionEditor.Number, Minimum = 0, Maximum = 255,
            LabelKey = "Opt.Hekate.themecolor", DescriptionKey = "Opt.Hekate.themecolor.Desc",
            DefaultValue = "167",
        },
        new()
        {
            Key = "entries5col", TargetFile = "bootloader/nyx.ini", Section = "config",
            GroupKey = "Group.Nyx",
            LabelKey = "Opt.Hekate.entries5col", DescriptionKey = "Opt.Hekate.entries5col.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "timeoffset", TargetFile = "bootloader/nyx.ini", Section = "config",
            GroupKey = "Group.Nyx", Editor = OptionEditor.TextBox,
            LabelKey = "Opt.Hekate.timeoffset", DescriptionKey = "Opt.Hekate.timeoffset.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "timedst", TargetFile = "bootloader/nyx.ini", Section = "config",
            GroupKey = "Group.Nyx",
            LabelKey = "Opt.Hekate.timedst", DescriptionKey = "Opt.Hekate.timedst.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "homescreen", TargetFile = "bootloader/nyx.ini", Section = "config",
            GroupKey = "Group.Nyx", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Hekate.homescreen", DescriptionKey = "Opt.Hekate.homescreen.Desc",
            DefaultValue = "0",
            Choices =
            [
                new OptionChoice("0", "Opt.Hekate.Home.Home"),
                new OptionChoice("1", "Opt.Hekate.Home.AllConfigs"),
                new OptionChoice("2", "Opt.Hekate.Home.Launch"),
                new OptionChoice("3", "Opt.Hekate.Home.MoreConfigs"),
            ],
        },
        new()
        {
            Key = "verification", TargetFile = "bootloader/nyx.ini", Section = "config",
            GroupKey = "Group.Nyx", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Hekate.verification", DescriptionKey = "Opt.Hekate.verification.Desc",
            DefaultValue = "1",
            Choices =
            [
                new OptionChoice("0", "Opt.Hekate.Verify.None"),
                new OptionChoice("1", "Opt.Hekate.Verify.Sparse"),
                new OptionChoice("2", "Opt.Hekate.Verify.Full"),
            ],
        },
        new()
        {
            Key = "umsemmcrw", TargetFile = "bootloader/nyx.ini", Section = "config",
            GroupKey = "Group.Nyx",
            LabelKey = "Opt.Hekate.umsemmcrw", DescriptionKey = "Opt.Hekate.umsemmcrw.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "jcdisable", TargetFile = "bootloader/nyx.ini", Section = "config",
            GroupKey = "Group.Nyx",
            LabelKey = "Opt.Hekate.jcdisable", DescriptionKey = "Opt.Hekate.jcdisable.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "jcforceright", TargetFile = "bootloader/nyx.ini", Section = "config",
            GroupKey = "Group.Nyx",
            LabelKey = "Opt.Hekate.jcforceright", DescriptionKey = "Opt.Hekate.jcforceright.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "bpmpclock", TargetFile = "bootloader/nyx.ini", Section = "config",
            GroupKey = "Group.Nyx", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Hekate.bpmpclock", DescriptionKey = "Opt.Hekate.bpmpclock.Desc",
            DefaultValue = "1",
            Choices =
            [
                new OptionChoice("0", "0 · Auto"),
                new OptionChoice("1", "1 · 589 MHz"),
                new OptionChoice("2", "2 · 576 MHz"),
                new OptionChoice("3", "3 · 563 MHz"),
                new OptionChoice("4", "4 · 544 MHz"),
                new OptionChoice("5", "5 · 408 MHz"),
            ],
        },
    ];

    // ── Ultrahand ───────────────────────────────────────────────
    // 组合键相关的常量与预设表声明在文件上方（必须在 Definitions 之前初始化）。
    private static List<OptionChoice> BuildKeyComboChoices()
        => PresetKeyCombos.Select(combo => new OptionChoice(combo, combo, combo)).ToList();

    /// <summary>
    /// 校验并规范化组合键：2–4 个按键、令牌必须合法、不允许重复。
    /// 不合法时返回 <c>null</c>，由调用方决定回退策略。
    /// </summary>
    public static string? NormalizeKeyCombo(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var tokens = raw
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToUpperInvariant())
            .ToList();

        if (tokens.Count is < 2 or > 4)
        {
            return null;
        }

        if (tokens.Any(token => !KeyComboTokens.Contains(token, StringComparer.Ordinal)))
        {
            return null;
        }

        if (tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Count)
        {
            return null;
        }

        return string.Join("+", tokens);
    }

    private static List<OptionDefinition> BuildUltrahandOptions() =>
    [
        new()
        {
            Key = "key_combo", TargetFile = "config/ultrahand/config.ini", Section = "ultrahand",
            GroupKey = "Group.Ultrahand.Config", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Ultrahand.KeyCombo", DescriptionKey = "Opt.Ultrahand.KeyCombo.Desc",
            DefaultValue = DefaultKeyCombo,
            Choices = BuildKeyComboChoices(),
        },
        new()
        {
            // Ultrahand 的界面语言。键名来自 libultrahand 的 DEFAULT_LANG_STR = "default_lang"，
            // 段名来自 ULTRAHAND_PROJECT_NAME = "ultrahand"（与 key_combo 同一份文件、同一个段）。
            //
            // 默认值是**哨兵**而不是某个具体语言：界面语言一改，产物就该跟着改，存成具体语言
            // 两者会脱节且无人报错。生成时经 ResolveUltrahandDefaultLang 解析成具体代码。
            Key = "default_lang", TargetFile = "config/ultrahand/config.ini", Section = "ultrahand",
            GroupKey = "Group.Ultrahand.Config", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Ultrahand.DefaultLang", DescriptionKey = "Opt.Ultrahand.DefaultLang.Desc",
            DefaultValue = AutoLanguageValue,
            Choices = BuildUltrahandLanguageChoices(),
        },
        new()
        {
            // Ultrahand 源码里这个键只在 [memory] 段被**读取**（source/main.cpp）：
            //   parseValueFromIniSection(ULTRAHAND_CONFIG_INI_PATH, MEMORY_STR, "custom_overlay_memory_MB")
            // 校验规则写死为「纯数字 && >8 && 偶数」，所以 10/12/14/16 合法，4/6/8 会被忽略。
            // 注意它的语义**不是**直接设定堆大小，而是给「Overlay 内存」滑条追加第 4 档
            // （原本固定 4/6/8 MB），用户仍需在 Ultrahand 里手动选中该档。
            Key = "custom_overlay_memory_MB", TargetFile = "config/ultrahand/config.ini", Section = "memory",
            GroupKey = "Group.Ultrahand.Config", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Ultrahand.CustomMemory", DescriptionKey = "Opt.Ultrahand.CustomMemory.Desc",
            DefaultValue = string.Empty,
            Choices =
            [
                new OptionChoice(string.Empty, "Opt.Ultrahand.CustomMemory.None"),
                new OptionChoice("10", "10 MB"),
                new OptionChoice("12", "12 MB"),
                new OptionChoice("14", "14 MB"),
                new OptionChoice("16", "16 MB"),
            ],
        },
        new()
        {
            Key = "installMode", GroupKey = "Group.Ultrahand", Editor = OptionEditor.ComboBox,
            LabelKey = "Opt.Ultrahand.InstallMode", DescriptionKey = "Opt.Ultrahand.InstallMode.Desc",
            DefaultValue = "sdout",
            Choices =
            [
                new OptionChoice("sdout", "Opt.Ultrahand.InstallMode.SdOut"),
                new OptionChoice("manual", "Opt.Ultrahand.InstallMode.Manual"),
            ],
        },
        new()
        {
            Key = "installOvlloader", GroupKey = "Group.Ultrahand",
            LabelKey = "Opt.Ultrahand.InstallOvlloader", DescriptionKey = "Opt.Ultrahand.InstallOvlloader.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "installSysmodules", GroupKey = "Group.Ultrahand",
            LabelKey = "Opt.Ultrahand.InstallSysmodules",
            DescriptionKey = "Opt.Ultrahand.InstallSysmodules.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "installLang", GroupKey = "Group.Ultrahand",
            LabelKey = "Opt.Ultrahand.InstallLang", DescriptionKey = "Opt.Ultrahand.InstallLang.Desc",
            DefaultValue = "0",
        },

        // ovl-sysmodules overlay 自己的配置文件（config/ovl-sysmodules/config.ini）
        new()
        {
            Key = "powerControlEnabled", TargetFile = "config/ovl-sysmodules/config.ini",
            Section = "ovl-sysmodules", GroupKey = "Group.OvlSysmodules",
            LabelKey = "Opt.OvlSysmodules.powerControlEnabled",
            DescriptionKey = "Opt.OvlSysmodules.powerControlEnabled.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "wifiControlEnabled", TargetFile = "config/ovl-sysmodules/config.ini",
            Section = "ovl-sysmodules", GroupKey = "Group.OvlSysmodules",
            LabelKey = "Opt.OvlSysmodules.wifiControlEnabled",
            DescriptionKey = "Opt.OvlSysmodules.wifiControlEnabled.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "sysmodulesControlEnabled", TargetFile = "config/ovl-sysmodules/config.ini",
            Section = "ovl-sysmodules", GroupKey = "Group.OvlSysmodules",
            LabelKey = "Opt.OvlSysmodules.sysmodulesControlEnabled",
            DescriptionKey = "Opt.OvlSysmodules.sysmodulesControlEnabled.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "bootFileControlEnabled", TargetFile = "config/ovl-sysmodules/config.ini",
            Section = "ovl-sysmodules", GroupKey = "Group.OvlSysmodules",
            LabelKey = "Opt.OvlSysmodules.bootFileControlEnabled",
            DescriptionKey = "Opt.OvlSysmodules.bootFileControlEnabled.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "hekateRestartControlEnabled", TargetFile = "config/ovl-sysmodules/config.ini",
            Section = "ovl-sysmodules", GroupKey = "Group.OvlSysmodules",
            LabelKey = "Opt.OvlSysmodules.hekateRestartControlEnabled",
            DescriptionKey = "Opt.OvlSysmodules.hekateRestartControlEnabled.Desc",
            DefaultValue = "0",
        },
        new()
        {
            Key = "consoleRegionControlEnabled", TargetFile = "config/ovl-sysmodules/config.ini",
            Section = "ovl-sysmodules", GroupKey = "Group.OvlSysmodules",
            LabelKey = "Opt.OvlSysmodules.consoleRegionControlEnabled",
            DescriptionKey = "Opt.OvlSysmodules.consoleRegionControlEnabled.Desc",
            DefaultValue = "0",
        },
    ];

    // ── Sys-patch ───────────────────────────────────────────────
    private static List<OptionDefinition> BuildSysPatchOptions() =>
    [
        new()
        {
            Key = "patch_sysmmc", TargetFile = "config/sys-patch/config.ini", Section = "options",
            GroupKey = "Group.SysPatch",
            LabelKey = "Opt.SysPatch.patch_sysmmc", DescriptionKey = "Opt.SysPatch.patch_sysmmc.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "patch_emummc", TargetFile = "config/sys-patch/config.ini", Section = "options",
            GroupKey = "Group.SysPatch",
            LabelKey = "Opt.SysPatch.patch_emummc", DescriptionKey = "Opt.SysPatch.patch_emummc.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "enable_logging", TargetFile = "config/sys-patch/config.ini", Section = "options",
            GroupKey = "Group.SysPatch",
            LabelKey = "Opt.SysPatch.enable_logging", DescriptionKey = "Opt.SysPatch.enable_logging.Desc",
            DefaultValue = "1",
        },
        new()
        {
            Key = "version_skip", TargetFile = "config/sys-patch/config.ini", Section = "options",
            GroupKey = "Group.SysPatch",
            LabelKey = "Opt.SysPatch.version_skip", DescriptionKey = "Opt.SysPatch.version_skip.Desc",
            DefaultValue = "1",
        },
    ];
}
