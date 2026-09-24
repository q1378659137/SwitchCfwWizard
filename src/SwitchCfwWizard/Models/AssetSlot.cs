namespace SwitchCfwWizard.Models;

/// <summary>文件槽的来源类型。</summary>
public enum SlotSource
{
    /// <summary>仓库最新 Release 里的一个资源文件（绝大多数插件都是这种）。</summary>
    Release,

    /// <summary>
    /// 仓库**文件树**里的一个文件（走 raw 下载，不经过 Release）。
    /// 典型是 Luna 的 <c>enctemplate.zip</c> —— 它躺在仓库根目录，从来没有发布过。
    /// </summary>
    RepoFile,

    /// <summary>
    /// 仓库**目录**下的所有文件（不含子目录）。一个槽 → 一批下载物。
    /// 典型是 <c>exelix11/theme-patches</c> 的 <c>systemPatches/</c>：20 个扁平的 <c>.ips</c> 补丁，
    /// 用户要求「取该目录下所有文件，不要文件夹」。
    ///
    /// ⚠️ 这种槽的**第二个可改项**（<see cref="AssetSlot.FileName"/>）代表**仓库内的目录路径**
    /// 而不是文件名 —— 一个目录没法用一个文件名描述，而「取哪个目录」正是与「用哪个仓库」并列的
    /// 那半个可改项。界面上那一列的表头因此写成「文件名 / 目录」（<c>Settings.Slots.FileNameLabel</c>）。
    /// </summary>
    RepoDirectory,
}

/// <summary>
/// 一个「可下载文件槽」：高级设置里的一行，也是「下载地址 / 文件名」这两个可改项的载体。
///
/// 存在的理由（用户 2026-09-18 的要求）：每个插件的**下载地址**与**下载文件名**都要能在界面上改，
/// 有多个文件的就出多个输入框；用户不改就用声明的默认值。
///
/// ⚠️ 默认文件名写成 <c>Func&lt;string?, string&gt;</c> 而不是 <c>string</c>，是因为**有语言相关的槽**：
/// DBI 的翻译文件要按界面语言取 <c>translation_zhcn.bin</c> / <c>translation_zhtw.bin</c> /
/// <c>translation_en.bin</c>。参数是界面语言代码（<c>zh-Hans</c> / <c>zh-Hant</c> / <c>en-US</c>），
/// 不是 <see cref="WizardOptions"/> —— 界面上要显示这个默认值当占位符，而那时并没有 WizardOptions。
/// 两边都从同一个语言代码求值，不会各说各话。
/// 定值用 <see cref="Always"/>。
/// </summary>
public sealed class AssetSlot
{
    /// <summary>组件内唯一的槽名。settings.json 里的键是 <c>&lt;组件&gt;/&lt;Key&gt;</c>。</summary>
    public required string Key { get; init; }

    /// <summary>
    /// 默认下载地址。界面上当占位符显示。
    ///
    /// ⚠️ 与 <see cref="FileName"/> 一样声明成 <c>Func&lt;string?, RepoSpec&gt;</c> 而不是 <c>RepoSpec</c>：
    /// **有「按界面语言换仓库」的槽** —— linkalho 的官方中文镜像在 <c>SwitchScriptTW/linkalho</c>，
    /// 上游英文版在 <c>impeeza/linkalho</c>（用户 2026-09-18 明确要求中文界面走前者）。
    /// 参数与 <see cref="FileName"/> 是**同一个**界面语言代码，两边不会各说各话。
    /// 定值用 <see cref="Always(RepoSpec)"/>。
    ///
    /// 声明成 <see cref="RepoSpec"/>（而不是 <c>string</c>）的那条理由仍然成立：
    /// ① 写错仓库名在**编译期**就过不去；② <c>ComponentCatalog.DeclaredRepos</c> 是反射扫
    /// <c>RepoSpec</c> 静态字段得来的，用强类型声明能让「声明的仓库」与「界面上改得动的仓库」
    /// 这两份清单自动对得上（见回归里的 <c>CheckRepoSources</c>）。
    /// </summary>
    public required Func<string?, RepoSpec> Address { get; init; }

    /// <summary>
    /// 默认文件名。界面上当占位符显示；下载时先按它精确匹配，匹配不到再走容错。
    ///
    /// ⚠️ <see cref="SlotSource.RepoDirectory"/> 的槽里，它代表**仓库内的目录路径**
    /// （见该枚举成员的说明）—— 载体共用，但界面上的列头写的是「文件名 / 目录」。
    /// </summary>
    public required Func<string?, string> FileName { get; init; }

    /// <summary>
    /// 落盘时改用的名字。为空表示沿用来源文件名。
    /// 只有「上游名字与 SD 卡上要求的名字不同」时才需要 —— 目前只有 DBI 的翻译文件
    /// （<c>translation_zhcn.bin</c> 必须叫 <c>translation.bin</c> 才被 DBI 认）。
    /// </summary>
    public string? SaveAs { get; init; }

    /// <summary>
    /// 该产物在 <c>out/</c> 里的落点。语义与 <see cref="AssetPick.Targets"/> 完全一致
    /// （压缩包只看第一个落点的目录）。
    /// </summary>
    public IReadOnlyList<OutputTarget> Targets { get; init; } = [];

    public SlotSource Source { get; init; } = SlotSource.Release;

    /// <summary>
    /// <see cref="SlotSource.RepoFile"/> 专用：文件在仓库里的路径。
    /// 为空表示就用文件名本身（文件在仓库根目录）。
    /// </summary>
    public string? RepoPath { get; init; }

    /// <summary>是否为散装 payload（单独暂存、单独开关控制）。</summary>
    public bool IsPayload { get; init; }

    /// <summary>
    /// 解压时怎么处理包内路径。默认 <see cref="ExtractPlan.All"/>（原样铺开）。
    /// 只有「上游包结构与用户的落点要求对不上」时才需要 —— 目前是 sys-ftpd（剥 <c>out/</c> 前缀 + 只取两个目录）
    /// 与 wiliwili（只取一个文件）。
    /// </summary>
    public ExtractPlan Extract { get; init; } = ExtractPlan.All;

    /// <summary>
    /// 这个槽装的是哪个 **sysmodule title**（<c>atmosphere/contents/&lt;16 位十六进制&gt;</c> 的那个 ID）。
    ///
    /// ⚠️ 为空表示的是「**未声明、不参与 title 冲突检测**」，**不是**「这个包不是 sysmodule」。
    /// 两者的区别是真实存在的：组件类里的 `SysClk` / `SysDvr` / `LdnMitm` 都是 sysmodule 却没声明；
    /// 「Ultrahand插件」里的 emuiibo（`0100000000000352`）同理，而 KeyX 装了**两个** title
    /// （`0100000000251020` + `4100000002025924`），单值字段根本表达不了 —— 写一个就是半个真话。
    ///
    /// 现有约定：**只声明「冲突可预期」的那些**（botbase 那一对 + 用了通用 title 的 sys-ticon）。
    /// ⏳ 若要把约定收紧成「所有 sysmodule 都必须声明」，得先把这个字段改成**多值**，
    /// 那时 <see cref="ComponentCatalog.FindTitleConflicts"/>、护栏 ⑨、那张实测对照表要一起动。
    ///
    /// 存在的理由：<c>sys-botbase</c> 与 <c>usb-botbase</c> 装的是**同一个 title**（<c>430000000000000B</c>），
    /// 两个都勾就必然互相覆盖 —— 用户要求「提示冲突、只能二选一」。
    /// 把 title 写进声明、而不是在代码里写死「这两个名字互斥」，是为了让规则**可穷举**：
    /// 判据变成「任意两个被勾选的槽声明了同一个 title 即冲突」，将来再加一个撞车的 sysmodule
    /// 不需要改任何代码就会自动被抓出来（见 <c>CheckAssetSlots</c> 的冲突用例）。
    /// </summary>
    public string? SysmoduleTitleId { get; init; }

    /// <summary>定值默认文件名的简写，让绝大多数槽的声明保持一行。</summary>
    public static Func<string?, string> Always(string fileName) => _ => fileName;

    /// <summary>定值默认地址的简写 —— 绝大多数槽与界面语言无关，只有 linkalho 不是。</summary>
    public static Func<string?, RepoSpec> Always(RepoSpec repo) => _ => repo;
}
