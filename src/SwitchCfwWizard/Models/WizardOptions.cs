namespace SwitchCfwWizard.Models;

/// <summary>
/// 一次向导运行所需的全部输入。
/// </summary>
public sealed class WizardOptions
{
    // ── 组件勾选 ────────────────────────────────────────────────
    //
    // ⚠️ **每个 ComponentKind 都要有同名的一个 bool**，`IsSelected` 就是按「属性名 = 枚举名」这个
    //    对应关系分派的（见下面的 SelectionAccessors）。漏一个的后果完全隐形：
    //    ConfigGenerator 用它过滤（跳过那个组件、不产出任何文件），**OutputValidator 也用它过滤**
    //    （于是连「逐组件对账」都看不见），结果是「少一个组件却全绿」。
    //    回归里的 CheckComponentKindExhaustiveness 逐个枚举成员钉住这件事。
    public bool Atmosphere { get; set; }

    public bool Hekate { get; set; }

    public bool Ultrahand { get; set; }

    public bool SysPatch { get; set; }

    // ── 固件 ────────────────────────────────────────────────────
    // 离线固件包（THZoria/NX_Firmware）—— 它不是「框架」，是**系统本体**，界面上单开一组。
    public bool Firmware { get; set; }

    // ── 「组件」类插件（19 个）──────────────────────────────────
    public bool Breeze { get; set; }

    public bool Dbi { get; set; }

    public bool EdiZonOverlay { get; set; }

    public bool Goldleaf { get; set; }

    public bool Nxdumptool { get; set; }

    public bool NxShell { get; set; }

    public bool Ftpd { get; set; }

    public bool Haku33 { get; set; }

    public bool Jksv { get; set; }

    public bool LunaApp { get; set; }

    public bool SimpleModAlchemist { get; set; }

    public bool SysClk { get; set; }

    public bool SysDvr { get; set; }

    public bool LdnMitm { get; set; }

    public bool Sphaira { get; set; }

    public bool Fizeau { get; set; }

    public bool NxActivityLog { get; set; }

    public bool SwitchFirmwareDumper { get; set; }

    public bool BatteryDesyncFix { get; set; }

    // ── 「后台」类插件（5 个）────────────────────────────────────
    public bool SysBotbase { get; set; }

    public bool UsbBotbase { get; set; }

    public bool MissionControl { get; set; }

    public bool SysCon { get; set; }

    public bool SysFtpd { get; set; }

    // ── 「主题」类插件（3 个）────────────────────────────────────
    public bool NxThemesInstaller { get; set; }

    public bool Avatool { get; set; }

    public bool SysTicon { get; set; }

    // ── 「底层」类插件（2 个）────────────────────────────────────
    public bool LockpickRcm { get; set; }

    public bool TegraExplorer { get; set; }

    // ── 「Ultrahand插件」类（6 个）───────────────────────────────
    public bool Emuiibo { get; set; }

    public bool Zing { get; set; }

    public bool ReverseNxRt { get; set; }

    public bool StatusMonitorOverlay { get; set; }

    public bool QuickNtp { get; set; }

    public bool KeyX { get; set; }

    // ── 「学习」类（7 个）────────────────────────────────────────
    public bool AtmoXL { get; set; }

    public bool Awoo { get; set; }

    public bool Linkalho { get; set; }

    public bool Dns90Tester { get; set; }

    public bool Wiliwili { get; set; }

    public bool Moonlight { get; set; }

    public bool TriPlayer { get; set; }

    /// <summary>8G 运存支持（同时影响 Hekate payload 选择与 exosphere/引导项配置）。</summary>
    public bool Ram8Gb { get; set; }

    // ── 引导进入系统 ────────────────────────────────────────────
    public bool BootStock { get; set; } = true;

    public bool BootSysNand { get; set; } = true;

    public bool BootEmuNand { get; set; } = true;

    /// <summary>
    /// 三个引导项在 <c>hekate_ipl.ini</c> 里显示的名字。留空表示用界面语言的默认名
    /// （<c>Boot.Entry.Stock</c> 等本地化键）。用户想叫什么就叫什么 —— 这串字只影响
    /// hekate 启动菜单里显示什么，不影响引导行为。
    /// </summary>
    public string BootStockTitle { get; set; } = string.Empty;

    public string BootSysNandTitle { get; set; } = string.Empty;

    public string BootEmuNandTitle { get; set; } = string.Empty;

    /// <summary>
    /// 三个引导项各自的启动图（用户在本地选的图片文件，绝对路径）。留空表示不设置。
    /// 生成时会复制到 <c>bootloader/res/</c>，并在引导项里写 <c>logopath=bootloader/res/&lt;文件名&gt;</c>。
    /// </summary>
    public string BootStockLogo { get; set; } = string.Empty;

    public string BootSysNandLogo { get; set; } = string.Empty;

    public string BootEmuNandLogo { get; set; } = string.Empty;

    /// <summary>
    /// 三个引导项各自的图标（用户在本地选的图片文件，绝对路径）。留空表示不设置。
    /// 生成时会复制到 <c>bootloader/res/</c>，并在引导项里写 <c>icon=bootloader/res/&lt;文件名&gt;</c>。
    ///
    /// 与 <see cref="BootStockLogo"/> 是同一族的「stylistic key」（上游模板原文：
    /// "like logopath= key which is for bootlogo and icon= key for Nyx icon"）：
    /// <c>logopath</c> 决定**进这一项时**刷的启动图，<c>icon</c> 决定这一项在 **Nyx 菜单里**显示的小图标。
    /// </summary>
    public string BootStockIcon { get; set; } = string.Empty;

    public string BootSysNandIcon { get; set; } = string.Empty;

    public string BootEmuNandIcon { get; set; } = string.Empty;

    // ── 条件选项 ────────────────────────────────────────────────
    /// <summary>
    /// 屏蔽真实破解系统（sysMMC）的序列号 → <c>exosphere.ini</c> 的 <c>blank_prodinfo_sysmmc</c>。
    /// 与 emuMMC 那项**互相独立**，用户想屏蔽哪个就屏蔽哪个。
    ///
    /// **默认关闭**：屏蔽序列号会让 prodinfo 证书失效，必须靠 Sys-patch 才能正常启动，
    /// 属于「有明确需求才开」的选项，不适合替用户默认打开。
    /// 老用户存档里的显式取值不受影响（见 <see cref="Services.SettingsStore"/> 的迁移逻辑）。
    /// </summary>
    public bool BlankSerialSysmmc { get; set; }

    /// <summary>
    /// 屏蔽虚拟破解系统（emuMMC）的序列号 → <c>exosphere.ini</c> 的 <c>blank_prodinfo_emummc</c>。
    /// **默认关闭**（同上）。
    /// </summary>
    public bool BlankSerialEmummc { get; set; }

    /// <summary>
    /// 为**真实破解系统（sysMMC）**启用 90DNS → 写 <c>atmosphere/hosts/default.txt</c> + <c>sysmmc.txt</c>。
    ///
    /// 与 emuMMC 那项**互相独立**（用户想给哪个系统挡遥测就勾哪个），与屏蔽序列号同一套
    /// 「跟着对应引导模式」的规矩：只在勾了真实破解系统时才有意义，也只有那时才写文件。
    ///
    /// 90DNS 靠 <c>atmosphere/hosts</c> 里的屏蔽表工作，而这份屏蔽表要生效必须打开
    /// <c>system_settings.ini</c> 的 <c>enable_dns_mitm</c> —— 两者的联动见
    /// <c>MainViewModel</c> 的 <c>ApplyDnsMitmCoupling()</c>。
    ///
    /// **默认关闭**（同 <see cref="BlankSerialSysmmc"/>：属「有明确需求才开」的选项）。
    /// </summary>
    public bool Use90DnsSysmmc { get; set; }

    /// <summary>
    /// 为**虚拟破解系统（emuMMC）**启用 90DNS → 写 <c>atmosphere/hosts/default.txt</c> + <c>emummc.txt</c>。
    /// **默认关闭**（同上）。
    /// </summary>
    public bool Use90DnsEmummc { get; set; }

    // ── 全局行为 ────────────────────────────────────────────────
    public bool PreferPrerelease { get; set; } = true;

    /// <summary>
    /// 当前界面语言代码（<c>zh-Hans</c> / <c>zh-Hant</c> / <c>en-US</c>）。
    /// 用来决定 hekate 从哪个仓库取：中文走 <c>easyworld/hekate</c> 的本地化包，其余走官方。
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// 用户改过的下载源：仓库默认地址（<c>owner/name</c>）→ 实际要用的地址。
    /// 没出现在这里的仓库一律用默认地址。
    ///
    /// ⚠️ 这里只管**框架那四个组件**（界面上「下载源」那一栏）。
    /// 插件类的地址在 <see cref="AssetAddresses"/> 里，两者是两套入口、别混。
    /// </summary>
    public Dictionary<string, string> RepoOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 用户改过的**文件槽地址**：键 <c>&lt;组件&gt;/&lt;槽 Key&gt;</c> → 实际地址。
    /// 没出现在这里的一律用声明的默认地址。见 <see cref="Services.ComponentCatalog.ResolveSlotAddress"/>。
    /// </summary>
    public Dictionary<string, string> AssetAddresses { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 用户改过的**文件槽文件名**：键同上 → 实际要下的文件名。
    /// 没出现在这里的一律用声明的默认值（可能按界面语言求值）。
    /// 见 <see cref="Services.ComponentCatalog.ResolveSlotFileName"/>。
    /// </summary>
    public Dictionary<string, string> AssetFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 是否把解压后的组件文件一并合并进 out 目录。
    /// 默认开启——不合并的话 out 里只有配置和 payload，拷进 SD 卡开不了机。
    /// 与 <see cref="Services.AppSettings.IncludeComponentFilesInOutput"/> 保持一致。
    /// </summary>
    public bool IncludeComponentFilesInOutput { get; set; } = true;

    /// <summary>
    /// 是否把 payload 文件（Atmosphere 的 <c>fusee.bin</c>、Hekate 的 <c>hekate_ctcaer_*.bin</c>）
    /// 一并放到 out 根目录。这些文件是散装下载的，不在解压目录里，所以要单独处理。
    /// </summary>
    public bool IncludePayloads { get; set; } = true;

    /// <summary>
    /// 是否把 <c>boot.dat</c>（SX GEAR 引导文件）写到 <c>out/</c> 根目录。
    ///
    /// ⚠️ 自 2026-09-21 起这个文件**不再内嵌在程序里**，改为按高级设置里的地址下载：
    /// 「勾了」只代表「想要它」，真正决定 out 里有没有的，是下面的 <see cref="BootDatPath"/>。
    /// </summary>
    public bool IncludeBootDat { get; set; }

    /// <summary>
    /// 已下载到本地的 <c>boot.dat</c> 路径；由下载阶段填进来，**没下到就是 null**。
    ///
    /// 为什么传「路径」而不是「字节」：配置生成是**同步且不联网**的，也不该知道 HTTP 的存在。
    /// 下载发生在 <c>MainViewModel</c> 那一层（那里有 <see cref="DownloadService"/>，
    /// 于是自动吃到镜像站与备用通道），生成只负责「把这个文件摆到 out 根目录」。
    /// </summary>
    public string? BootDatPath { get; set; }

    /// <summary>组件级配置项取值：组件 → 选项 Key → 字符串值。</summary>
    public Dictionary<ComponentKind, Dictionary<string, string>> Values { get; } = new();

    /// <summary>
    /// 这个组件这次勾了没有。
    ///
    /// **按名字反射**读同名 bool 属性，而不是写一条 23 臂的 switch。
    ///
    /// 理由：那种 switch 是**第三份手抄清单**（枚举一份、bool 属性一份、switch 一份），
    /// 加组件时漏改任何一处，运行时都只表现为静默的「没勾」—— 而这一处偏偏最要命
    /// （生成器与校验器**都**用它过滤，所以两边会一起跳过那个组件，「少一个组件却全绿」）。
    /// 反射之后只剩「属性名 = 枚举名」这一条约定，而它由回归里的 CheckComponentKindExhaustiveness
    /// **逐个枚举成员**钉住（那份清单从枚举本身生成，不手抄，不会腐烂）。
    ///
    /// 查不到名字时返回 false（而不是抛异常）：那是「新增枚举成员却忘了加属性」的形态，
    /// 该由回归报出来，不该让用户点「开始」时炸掉。反过来说 —— 只要回归是绿的，
    /// 这里就不可能查不到。
    /// </summary>
    public bool IsSelected(ComponentKind kind) =>
        SelectionProperties.TryGetValue(kind, out var property) && (bool)property.GetValue(this)!;

    /// <summary>
    /// 把某个组件的勾选状态写进对应的同名 bool 属性。找不到同名属性时返回 false（回归会报出来）。
    ///
    /// 与 <see cref="IsSelected"/> 共用同一张表，所以「读」和「写」不可能各认一套名字 ——
    /// 分成两处写的话，读用 `Dbi`、写用 `DBI` 这种大小写错位会表现成「勾了没反应」。
    /// </summary>
    public bool TrySetSelected(ComponentKind kind, bool selected)
    {
        if (!SelectionProperties.TryGetValue(kind, out var property))
        {
            return false;
        }

        property.SetValue(this, selected);
        return true;
    }

    /// <summary>
    /// 枚举成员 → 同名 bool 属性。**从 <see cref="WizardOptions"/> 自己的属性反射生成**，
    /// 不手抄清单（§1 教训 4：手抄的清单自己会腐烂）。
    ///
    /// 按名字做内连接：只有「恰好与某个 ComponentKind 同名」的 bool 属性才会进表，
    /// 所以 <c>Ram8Gb</c> / <c>BootStock</c> 这些普通开关不会被误当成组件勾选位。
    /// </summary>
    private static readonly Dictionary<ComponentKind, System.Reflection.PropertyInfo> SelectionProperties =
        typeof(WizardOptions)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(bool) && p.CanRead && p.CanWrite)
            .Join(
                Enum.GetValues<ComponentKind>(),
                property => property.Name,
                kind => kind.ToString(),
                (property, kind) => (Kind: kind, Property: property))
            .ToDictionary(pair => pair.Kind, pair => pair.Property);

    /// <summary>
    /// 有没有勾任何一个组件（界面据此决定「开始」按钮能不能按）。
    ///
    /// 同样是遍历**枚举**再问 <see cref="IsSelected"/>，不写 OR 链：OR 链是又一份手抄清单，
    /// 漏掉的那个组件会让界面显示「请至少勾选一个组件」而拒绝开始 —— 用户明明勾了。
    /// </summary>
    public bool HasAnyComponentSelected => Enum.GetValues<ComponentKind>().Any(IsSelected);

    public bool HasAnyBootEntry => BootStock || BootSysNand || BootEmuNand;

    /// <summary>是否需要真实破解系统相关配置（sysMMC）。</summary>
    public bool NeedsSysNandConfig => BootSysNand;

    /// <summary>是否需要虚拟破解系统相关配置（emuMMC）。</summary>
    public bool NeedsEmuNandConfig => BootEmuNand;

    public string Get(ComponentKind kind, string key, string fallback = "")
    {
        if (Values.TryGetValue(kind, out var map) && map.TryGetValue(key, out var value))
        {
            return value;
        }

        return fallback;
    }

    public bool GetBool(ComponentKind kind, string key, bool fallback = false)
    {
        var raw = Get(kind, key, fallback ? "1" : "0");
        return raw is "1" or "true" or "True" or "on" or "On";
    }

    public int GetInt(ComponentKind kind, string key, int fallback = 0)
        => int.TryParse(Get(kind, key, fallback.ToString()), out var value) ? value : fallback;

    public void Set(ComponentKind kind, string key, string value)
    {
        if (!Values.TryGetValue(kind, out var map))
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            Values[kind] = map;
        }

        map[key] = value;
    }
}
