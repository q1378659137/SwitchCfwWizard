namespace SwitchCfwWizard.Models;

/// <summary>
/// 向导支持的组件。
///
/// 前四个是「框架」—— 取用规则复杂，走手写的 <c>Repos</c> + picker。
/// 其余是「插件」—— 一律「一个仓库、若干文件、落到某个目录」，走
/// <c>ComponentDefinition.Slots</c> 声明，地址与文件名自动变成界面上可改的项。
///
/// ⚠️ 枚举名会直接拼进两个地方，**改名等于改存档**：
/// <list type="bullet">
///   <item><c>settings.json</c> 的 <c>Components</c> / <c>OptionValues</c> / <c>AssetAddresses</c> / <c>AssetFileNames</c> 的键；</item>
///   <item>多语言键 <c>Component.&lt;Kind&gt;.Name</c> / <c>.Desc</c>。</item>
/// </list>
/// </summary>
public enum ComponentKind
{
    // ── 框架 ────────────────────────────────────────────────────
    Atmosphere,
    Hekate,
    Ultrahand,
    SysPatch,

    // ── 固件 ────────────────────────────────────────────────────
    // 离线固件包（THZoria/NX_Firmware）：机器要跑的那个**系统本体**。
    // 单开一组而不是并进「框架」—— 框架是「把系统跑起来的东西」，它是「系统本身」。
    Firmware,

    // ── 组件 ────────────────────────────────────────────────────
    Breeze,
    Dbi,
    EdiZonOverlay,
    Goldleaf,
    Nxdumptool,
    NxShell,
    Ftpd,
    Haku33,
    Jksv,
    LunaApp,
    SimpleModAlchemist,
    SysClk,
    SysDvr,
    LdnMitm,
    Sphaira,
    Fizeau,
    NxActivityLog,
    SwitchFirmwareDumper,
    BatteryDesyncFix,

    // ── 后台 ────────────────────────────────────────────────────
    SysBotbase,
    UsbBotbase,
    MissionControl,
    SysCon,
    SysFtpd,

    // ── 主题 ────────────────────────────────────────────────────
    NxThemesInstaller,
    Avatool,
    SysTicon,

    // ── 底层 ────────────────────────────────────────────────────
    LockpickRcm,
    TegraExplorer,

    // ── Ultrahand插件 ───────────────────────────────────────────
    // Ultrahand 生态的 Tesla 覆盖层（.ovl）：装到 switch/.overlays/ 后由 Ultrahand 呼出。
    Emuiibo,
    Zing,
    ReverseNxRt,
    StatusMonitorOverlay,
    QuickNtp,
    KeyX,

    // ── 学习 ────────────────────────────────────────────────────
    // 与 CFW 本身无关的附加软件：安装器、媒体播放、游戏串流。
    AtmoXL,
    Awoo,
    Linkalho,
    Dns90Tester,
    Wiliwili,
    Moonlight,
    TriPlayer,
}
