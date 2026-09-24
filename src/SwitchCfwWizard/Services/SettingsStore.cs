using System.IO;
using System.Text.Json;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;

namespace SwitchCfwWizard.Services;

/// <summary>持久化到 &lt;运行目录&gt;/settings.json 的应用设置。</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = LocalizationService.DefaultLanguageCode;

    /// <summary>
    /// 界面主题档位：<c>system</c> / <c>light</c> / <c>dark</c>（小写，编解码见 <see cref="ThemeModeCodec"/>）。
    ///
    /// ⚠️ 存**字符串**而不是枚举：存档是用户看得见、也可能手改的文本，编解码单独一层保证
    /// 「写了个错词」只会回落默认，而不是让程序起不来（同 <see cref="Mirror"/> 那一族的约定）。
    ///
    /// **老存档里没有这个字段**（null）⇒ 按「跟随系统」处理。也就是说：系统本来就是深色的老用户
    /// 升级上来，界面会第一次变暗 —— 这是**有意**的行为（跟随他已经表达过的系统偏好），
    /// 不是意外；想固定成浅色，在窗口右上角把「主题」切成「浅色」即可（会写回这个字段）。
    /// </summary>
    public string? Theme { get; set; }

    public string? GitHubToken { get; set; }

    public int TimeoutSeconds { get; set; } = 90;

    public string? Proxy { get; set; }

    /// <summary>
    /// GitHub 加速镜像站的地址（例如 <c>https://gh.example.com</c>）。留空 = 直连 GitHub 官方地址。
    ///
    /// ⚠️ 存的是**用户原话**（与 <see cref="Proxy"/> 同一约定）：规范化在**用的时候**做
    /// （<see cref="GitHubMirror.Normalize"/>），这样界面上显示的就是他自己填进去的那一串。
    ///
    /// 下载与查询的用法**故意不一样**：下载「填了就直接走镜像」，查询「先直连官方、连不上才用它」——
    /// 理由见 <see cref="GitHubMirror"/> 与 <see cref="GitHubReleaseService"/> 的注释。
    /// 改写规则也只写在 <see cref="GitHubMirror"/> 一处。
    /// </summary>
    public string? Mirror { get; set; }

    public bool PreferPrerelease { get; set; } = true;

    /// <summary>
    /// 是否把解压好的组件本体一并合并进 out。
    ///
    /// **默认开启**：不合并的话 out/ 里只有配置文件和 payload，拷进 SD 卡**根本起不来机**
    /// （缺 atmosphere/、bootloader/、switch/ 这些本体）。本工具的产出就是「一份能直接用的
    /// SD 卡内容」，让默认值产出一份不能用的东西说不过去。
    /// 已经存在 settings.json 的老用户不受影响——他们上次的显式选择会被保留。
    /// </summary>
    public bool IncludeComponentFilesInOutput { get; set; } = true;

    /// <summary>是否把 payload 文件（fusee.bin / hekate 引导 payload）一并放进 out 根目录。</summary>
    public bool IncludePayloads { get; set; } = true;

    /// <summary>
    /// 是否把 <c>boot.dat</c>（SX GEAR 引导文件）写到 out 根目录。**默认关闭**
    /// （用户 2026-09-21 明确要求：改成默认不勾选）。
    ///
    /// ⚠️ 它自 2026-09-21 起**不再内嵌在程序里**，改为下载（见下一条 <see cref="BootDatUrl"/>）——
    /// 所以「勾了」只代表「想要它」，拿不拿得到要看下载那一步。
    /// 老存档里没有这个字段 ⇒ 走这里的默认值（false）；已经勾过的存档照旧读回 true。
    /// </summary>
    public bool IncludeBootDat { get; set; }

    /// <summary>
    /// <c>boot.dat</c> 的下载地址。留空 = 用程序里的默认地址
    /// （<see cref="Services.BootDatSource.DefaultUrl"/>）。
    ///
    /// ⚠️ 默认值是一条 GitHub **网页**地址（<c>/blob/…</c>），真正取字节用的是它的 raw 形态 ——
    /// 这层翻译由 <see cref="Services.BootDatSource.Resolve"/> 完成，所以这里**存用户原话**即可
    /// （与 <see cref="Mirror"/> 同一约定：规范化在「用的时候」做，界面上显示的就是他自己填的那串）。
    /// 老存档没有这个字段 ⇒ null ⇒ 走默认；填了但解析不出 ⇒ **就当没填、照默认走**，不抛错。
    /// </summary>
    public string? BootDatUrl { get; set; }

    /// <summary>
    /// 「检查更新」查版本用的地址。留空 = 用默认地址（<see cref="Services.UpdateSource.DefaultUrl"/>，
    /// 一条 GitHub 的 releases 网页地址）。
    ///
    /// 同样存用户原话：真正查询走的是由它推出来的 <c>api.github.com/repos/…</c>
    /// （网页地址本身没有版本 JSON），推导见 <see cref="Services.UpdateSource.ResolveRepo"/>。
    /// </summary>
    public string? UpdateUrl { get; set; }

    /// <summary>组件勾选状态，键为 ComponentKind 名称。</summary>
    public Dictionary<string, bool> Components { get; set; } = new();

    public bool BootStock { get; set; } = true;

    public bool BootSysNand { get; set; } = true;

    public bool BootEmuNand { get; set; } = true;

    /// <summary>
    /// 三个引导项显示名。留空 = 用界面语言的默认名（本地化键 <c>Boot.Entry.*</c>）。
    /// 只影响 hekate 启动菜单里显示什么，不影响引导行为。
    /// </summary>
    public string BootStockTitle { get; set; } = string.Empty;

    public string BootSysNandTitle { get; set; } = string.Empty;

    public string BootEmuNandTitle { get; set; } = string.Empty;

    /// <summary>
    /// 三个引导项各自的启动图（用户本地图片的绝对路径）。留空 = 不设置。
    /// 生成时复制到 <c>bootloader/res/</c>，并写 <c>logopath=bootloader/res/&lt;文件名&gt;</c>。
    /// </summary>
    public string BootStockLogo { get; set; } = string.Empty;

    public string BootSysNandLogo { get; set; } = string.Empty;

    public string BootEmuNandLogo { get; set; } = string.Empty;

    /// <summary>
    /// 三个引导项各自的图标（用户本地图片的绝对路径）。留空 = 不设置。
    /// 生成时复制到 <c>bootloader/res/</c>，并写 <c>icon=bootloader/res/&lt;文件名&gt;</c>。
    /// 与 <see cref="BootStockLogo"/> 是同一族的引导项级「stylistic key」。
    /// </summary>
    public string BootStockIcon { get; set; } = string.Empty;

    public string BootSysNandIcon { get; set; } = string.Empty;

    public string BootEmuNandIcon { get; set; } = string.Empty;

    /// <summary>
    /// 用户改过的下载源：仓库默认地址（<c>owner/name</c>）→ 实际要用的地址。
    /// 没出现在这里的仓库一律用内置默认地址。**只管框架那四个组件**。
    /// </summary>
    public Dictionary<string, string> RepoOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 用户改过的插件文件槽地址：键 <c>&lt;组件&gt;/&lt;槽 Key&gt;</c> → 实际地址。
    /// 与 <see cref="AssetFileNames"/> 是同一批槽的两个维度，键完全一致。
    /// </summary>
    public Dictionary<string, string> AssetAddresses { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>用户改过的插件文件槽文件名：键同 <see cref="AssetAddresses"/>。</summary>
    public Dictionary<string, string> AssetFileNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 【旧字段，仅用于迁移】早期版本只有一个「屏蔽序列号」总开关，语义是 sysMMC 和 emuMMC 都屏蔽。
    /// 现已拆成 <see cref="BlankSerialSysmmc"/> / <see cref="BlankSerialEmummc"/> 两项。
    ///
    /// 这里刻意用可空类型：只有读到旧文件时才是非 null，迁移后立刻置回 null，
    /// 避免每次启动都用旧值覆盖用户后来分别设置的选项。
    /// </summary>
    public bool? BlankSerial { get; set; }

    /// <summary>
    /// 屏蔽真实破解系统（sysMMC）的序列号 → blank_prodinfo_sysmmc。
    ///
    /// **默认关闭**：屏蔽序列号会让 prodinfo 证书失效，必须靠 Sys-patch 在运行期补签名校验
    /// 才能正常启动，属于「有明确需求才开」的选项。老用户存档里的显式取值不受影响
    /// （旧格式的单个 <see cref="BlankSerial"/> 开关仍会被迁移展开）。
    /// </summary>
    public bool BlankSerialSysmmc { get; set; }

    /// <summary>
    /// 屏蔽虚拟破解系统（emuMMC）的序列号 → blank_prodinfo_emummc。**默认关闭**（同上）。
    /// </summary>
    public bool BlankSerialEmummc { get; set; }

    /// <summary>
    /// 旧格式的**单个** 90DNS 开关，**只作迁移标记**（新代码只读写下面两项）。
    /// 见 <see cref="Migrate"/>：展开成两项之后置回 <c>null</c>，保证只迁移一次。
    /// </summary>
    public bool? Use90Dns { get; set; }

    /// <summary>为真实破解系统（sysMMC）启用 90DNS。**默认关闭**。</summary>
    public bool Use90DnsSysmmc { get; set; }

    /// <summary>为虚拟破解系统（emuMMC）启用 90DNS。**默认关闭**。</summary>
    public bool Use90DnsEmummc { get; set; }

    public bool Ram8Gb { get; set; }

    /// <summary>
    /// 生成完成后自动把 out/ 打包成 zip。
    /// **默认关闭**：多数人是直接把 out/ 整个拷进 SD 卡，多打一个包反而多一步。
    /// 想打包随时可以点界面上的「打包为 zip」按钮。
    /// </summary>
    public bool AutoPackZip { get; set; }

    /// <summary>组件配置项取值，键为 "&lt;组件&gt;/&lt;选项 Key&gt;"。</summary>
    public Dictionary<string, string> OptionValues { get; set; } = new();
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public static string FilePath => Path.Combine(AppPaths.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                if (settings is not null)
                {
                    settings.Components ??= new Dictionary<string, bool>();
                    settings.OptionValues ??= new Dictionary<string, string>();
                    settings.RepoOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    settings.AssetAddresses ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    settings.AssetFileNames ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    settings.BootStockTitle ??= string.Empty;
                    settings.BootSysNandTitle ??= string.Empty;
                    settings.BootEmuNandTitle ??= string.Empty;
                    settings.BootStockLogo ??= string.Empty;
                    settings.BootSysNandLogo ??= string.Empty;
                    settings.BootEmuNandLogo ??= string.Empty;
                    settings.BootStockIcon ??= string.Empty;
                    settings.BootSysNandIcon ??= string.Empty;
                    settings.BootEmuNandIcon ??= string.Empty;
                    Migrate(settings);
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 设置文件损坏时回退到默认值
        }

        return new AppSettings();
    }

    public static bool Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, SerializerOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把旧版 <c>settings.json</c> 升到当前结构。
    ///
    /// 目前只有一处：旧的单个「屏蔽序列号」开关展开成 sysMMC / emuMMC 两项。
    /// **必须做**——否则升级后新字段默认 false，用户原本开着的屏蔽会被**静默关掉**，
    /// 而屏蔽序列号是隐私相关设置，不能悄悄失效。
    /// </summary>
    private static void Migrate(AppSettings settings)
    {
        // 两个迁移**各自独立**判断，不能提前 return —— 老存档完全可能只带其中一个旧字段
        // （例如从「90DNS 已经拆开、BlankSerial 早已迁移过」的版本升上来时，BlankSerial 是 null）。
        if (settings.BlankSerial is bool legacyBlank)
        {
            settings.BlankSerialSysmmc = legacyBlank;
            settings.BlankSerialEmummc = legacyBlank;

            // 置回 null：迁移只做一次。留着的话每次启动都会用旧值盖掉用户后来分开设的选择。
            settings.BlankSerial = null;
        }

        if (settings.Use90Dns is bool legacyDns)
        {
            // 旧版只有一个 90DNS 开关，它当时的产物是「按勾选的引导模式，给每个系统各写一份屏蔽表」。
            // 拆成两个开关之后，忠实还原就是**两个都置成旧值** —— 产物与旧版逐字节相同。
            settings.Use90DnsSysmmc = legacyDns;
            settings.Use90DnsEmummc = legacyDns;
            settings.Use90Dns = null;
        }
    }
}
