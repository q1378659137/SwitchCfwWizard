using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;
using SwitchCfwWizard.Models;
using SwitchCfwWizard.Services;
using SwitchCfwWizard.ViewModels;

namespace ConfigSmokeTest;

/// <summary>
/// 配置生成回归测试：不联网，直接用 WizardOptions 驱动 ConfigGenerator，
/// 对生成出来的 ini / hosts 文件做断言。
///
/// 用法（在 tools/ConfigSmokeTest 目录下）：
///   dotnet run
/// 退出码 0 表示全部通过。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    /// <summary>
    /// 三个引导项在 <c>hekate_ipl.ini</c> 里的**默认显示名** —— 即语言包
    /// <c>Boot.Entry.Stock</c> / <c>Boot.Entry.SysNand</c> / <c>Boot.Entry.EmuNand</c> 的取值。
    ///
    /// 独立成常量是因为它们散落在十几处断言里当 section 名用：改默认名时只改这一处，
    /// 漏改的地方会以「找不到 section」的形式报错，而不是静默通过。
    /// </summary>
    private const string StockTitle = "zbxt";

    private const string SysNandTitle = "zspjxt";

    private const string EmuNandTitle = "xnpjxt";

    /// <summary>
    /// 界面语言的**全部**取值（语言包目录里的那三份）。
    ///
    /// 存在的理由：槽的默认地址与默认文件名都是 <c>Func&lt;string?, …&gt;</c> ——
    /// 求值结果**跟着界面语言变**（linkalho 中文走汉化仓库、其余走上游；KeyX / DBI 同理）。
    /// 所以「这个槽声明的仓库/文件名是什么」没有单一答案，必须**在全部语言下求值**才算穷举。
    /// 只取一个语言去比对，会漏掉另一半声明（linkalho 的 CN 仓库就完全不会被检查到）。
    /// </summary>
    private static readonly string[] UiLanguageCodes = ["zh-Hans", "zh-Hant", "en-US"];

    /// <summary>完整场景的输入与结果，供打包 / 校验用例复用。</summary>
    private static WizardOptions? _fullOptions;
    private static ConfigGenerationResult? _fullResult;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 回归**自己**必须是幂等的：跑完 settings.json 要与开跑前逐字节相同。
        // 这是兜底护栏 —— 某个用例忘了还原自己改过的存档时（本轮真发生过：一个用例把
        // BootSysNand 翻了个面又落盘，于是另一条毫不相干的断言在红/绿之间来回跳，
        // 而两次运行的代码一模一样），下面那条断言会**直接点名这件事**，
        // 而不是等人从「同一份代码两次结果不同」里反推。
        var settingsPath = SettingsStore.FilePath;
        var settingsBefore = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        _settingsExistedAtStart = settingsBefore is not null;

        CheckLocalization();
        CheckDefaults();
        CheckFreshUiState();
        CheckOptionDefinitionInvariants();
        CheckIniValuePrefixesAreRecognized();
        CheckSysPatchForcing();
        CheckBlankSerialIndependence();
        CheckSettingsMigration();
        CheckMigrationThroughViewModel();
        CheckViewModelToOptionsMapping();
        CheckEverySettingIsWiredIntoOptions();
        CheckSettingsRoundTrip();
        // 设置项的落盘/读回接线：手写赋值块漏一处，症状只在「下次启动」才现形
        CheckEverySettingIsPersisted();
        // 自动落盘：界面上每一次改动都要写进 settings.json（用户 2026-09-18）
        CheckAutoSaveWiring();
        CheckAutoSaveBehaviour();
        // 操作日志：点了哪个按钮、输了什么文字都落进 logs/ 的日志文件（用户 2026-09-18）
        CheckOperationLogging();
        CheckOptionValuesPersistence();
        CheckComponentSelectionPersistence();
        CheckAutobootRemapOnBootEntryChange();
        CheckAutobootIndexMatchesGeneratedEntries();
        CheckNetworkErrors();
        CheckGitHubMirror();
        CheckMirrorSettingPersistence();
        CheckBootDatSource();
        CheckBootDatIsNotEmbedded();
        CheckUpdateSource();
        CheckUpdateUrlFallsBack();
        CheckUpdateDoesNotPersist();
        CheckNewUrlSettingsPersistence();
        // 2026-09-22：下载百分比接上 + boot.dat 卡片那行文字的空状态
        CheckUpdateProgressIsWired();
        CheckBootDatStatusText();
        CheckDownloadFallback();
        // 用户配置基线：锁死「向导默认值 == 用户实际在用的 ini」，默认值被改回上游值会立刻报警。
        // 放在这里是因为它要生成一整套文件，后面每个用例都会自己重新生成，不会互相干扰。
        CheckUserConfigBaseline();
        // 这几个用例都自己重新生成，彼此独立
        CheckUltrahandConfig();
        // Ultrahand default_lang：跟随界面语言 + 用户可覆盖 + 语言包缺失要告警
        CheckUltrahandDefaultLang();
        CheckMinimalScenario();
        CheckKip1Toggle();
        CheckStratosphereNogc();
        CheckIniSectionsMatchDeclarations();
        CheckFullScenario();
        CheckEveryDeclaredTargetFileIsWritten();
        CheckEveryDeclaredOptionIsConsumed();
        CheckOptionAccessorKeysAreDeclared();
        CheckPackaging();
        CheckArchivePruning();
        CheckBootDat();
        CheckValidation();
        CheckZipManifest();
        CheckZipExtraction();
        CheckMergeIntoOut();
        CheckEverySelectedComponentMergesIntoOut();

        // 2026-09-16 七项功能改动对应的护栏。
        // 放在 CheckFullScenario 之后：CheckPayloadBinFallback 结束时要用 _fullOptions 把 out/ 还原。
        CheckBootEntryTitles();
        CheckBootLogo();
        // 引导项图标（icon）：与启动图共用一套复制/去重逻辑，重点测共用状态才有的问题
        CheckBootIcon();
        CheckLocalizedHekateSource();
        // 8G 运存拿不到 8G payload 时必须告警（用户 2026-09-17 决定 A）
        CheckRam8GbPayloadWarning();
        // Ultrahand 的语言包绝不能被当成整包 SD 内容（上游资源按名排序，lang.zip 在前）
        CheckUltrahandPickerAssets();
        CheckRepoSources();
        // 文件槽机制（19 个插件类组件共用）：地址/文件名可改 + 容错匹配 + 落点与用户清单一致
        CheckAssetSlots();
        // 组件枚举与目录定义的一一对应（新增组件时忘了加目录定义 = 组件在界面上不存在）
        CheckComponentKindCoverage();
        CheckComponentKindExhaustiveness();
        // 2026-09-18 加「分类文件夹」：分组是 Components → ComponentGroups 的投影，投影写错会静默丢卡片
        CheckComponentCategoryCoverage();
        CheckPayloadBinFallback();
        // 2026-09-17 四项需求对应的护栏：90DNS 拆两个开关 + 与 enable_dns_mitm 双向绑定；
        // override_config.ini 的 [hbl_config] 五个键变可配置。两条都会自己重生成 out/ 再还原。
        Check90DnsSwitches();
        CheckHblConfigOptionsAreWired();
        CheckHblConfigFallbacks();
        // 首次运行的勾选契约：界面不勾 / 命令行自动全选（含 App.xaml.cs 的源码契约核对）
        CheckFirstRunSelectionContract();
        // 放最后一条断言类：它把源码里引用的语言键全扫一遍，缺任何一个都会报出来
        CheckLocalizationKeyCoverage();
        CheckNoDeadLanguageKeys();
        // 同一族但更严的一条：直接比三份语言包的**原始键集**，不经会回退的索引器
        CheckLanguagePackKeyParity();
        // XAML 绑定名写错不会编译报错、也绑不到任何东西 —— 只有扫源码才抓得住
        CheckXamlBindingsResolve();

        CheckThemePaletteIsComplete();
        CheckThemePaletteMatchesStyles();
        CheckThemeColorsAreDynamic();
        CheckThemeModeCodec();
        CheckThemeNamesAreLocalized();
        CheckThemeApply();
        CheckThemeSettingPersistence();

        // 离线固件：{asset} 占位符必须过一遍**真实的暂存**（只验属性算得对，改回读裸 Targets 照样全绿）
        CheckFirmwareStaging();

        // 高级设置里槽那一行的「地址 / 文件名」要能落盘再读回（那两项存在字典里，既有的持久化用例没覆盖）
        CheckAssetSlotRowPersistence();

        // 放最后：这个用例会在 download 下造假的一整套下载目录（含 payload），跑完要清理干净
        CheckOutputStaging();

        // 全部断言跑完后再把「完整场景」生成一遍：这样留在 out/ 里的就是内容最全的一套，
        // 方便人工翻看，也是仓库里 samples/ 的来源。
        // 刻意放在最后而不是把 CheckFullScenario() 挪到最后 —— 后者会让 CheckPackaging() /
        // CheckBootDat() 在 _fullOptions 还没赋值时就去读它（实测直接 NullReferenceException）。
        if (_fullOptions is not null)
        {
            Generate(_fullOptions);
        }

        // 幂等护栏（见 Main 开头那段注释）：用例可以改 settings.json，但必须还原。
        // 判据是**逐字节**，不是「关键字段还对」—— 半还原（例如只把值改回去、却顺手把键序改了）
        // 同样是「测试给用户留了痕迹」，而它恰恰最难靠肉眼发现。
        var settingsAfter = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        Assert(string.Equals(settingsBefore, settingsAfter, StringComparison.Ordinal),
            "回归跑完，settings.json 应与开跑前逐字节相同"
            + "（用例改过的存档必须自己还原，否则结果取决于上一次谁跑过 —— 本轮就是这么红绿交替的）"
            + (_settingsLeakSection is null ? "" : $"；它最早在进入「{_settingsLeakSection}」这一节之前就已经存在了"));

        Console.WriteLine();
        Console.WriteLine($"通过 {_passed} 项断言，失败 {Failures.Count} 项。");
        if (Failures.Count > 0)
        {
            foreach (var failure in Failures)
            {
                Console.WriteLine("  ✗ " + failure);
            }

            return 1;
        }

        Console.WriteLine("SMOKE: ALL OK");
        return 0;
    }

    // ── 多语言 ──────────────────────────────────────────────────
    private static void CheckLocalization()
    {
        Section("多语言资源");

        var loc = LocalizationService.Instance;

        Assert(loc.Languages.Count == 3, $"内置语言应为 3 种，实际 {loc.Languages.Count} 种");
        Assert(loc.Languages[0].Code == "zh-Hans", "默认语言（列表首位）应为 zh-Hans");
        Assert(loc["App.Title"] == "Switch CFW 配置向导", "zh-Hans 的 App.Title 应已翻译");
        Assert(loc["Opt.Hekate.jcforceright"] == "强制右手柄作鼠标", "jcforceright 文案已补齐");

        loc.SetLanguage("en-US");
        Assert(loc["App.Title"] == "Switch CFW Wizard", "en-US 的 App.Title 应已翻译");
        loc.SetLanguage("zh-Hant");
        Assert(loc["App.Title"] == "Switch CFW 設定精靈", "zh-Hant 的 App.Title 应已翻译");
        loc.SetLanguage("zh-Hans");
    }

    // ── 默认值 ──────────────────────────────────────────────────
    private static void CheckDefaults()
    {
        Section("默认值：组件本体合并应为开启");

        // 不合并的话 out/ 里只有配置文件和 payload，拷进 SD 卡**根本开不了机**。
        // 本工具的产出就是「一份能直接拷进 SD 卡的完整内容」，默认值产出一份不能用的东西
        // 说不过去，所以这里把「默认开启」这个决定锁死，防止被无意改回去。
        Assert(new AppSettings().IncludeComponentFilesInOutput,
            "全新安装（无 settings.json）时「把组件文件合并进 out」应默认开启");
        Assert(new WizardOptions().IncludeComponentFilesInOutput,
            "WizardOptions 的默认值也应是开启，与 AppSettings 保持一致");

        // 其余默认值一并钉住
        Assert(new AppSettings().IncludePayloads, "payload 应默认放进 out 根目录");
        // 用户要求：打包是「想用才勾」的收尾动作，默认不勾，免得每次生成都多出一份 zip。
        Assert(!new AppSettings().AutoPackZip, "生成后应默认**不**自动打包 zip（用户要求默认不勾）");
        Assert(new AppSettings().PreferPrerelease, "应默认优先预览版");
        Assert(new AppSettings().Language == LocalizationService.DefaultLanguageCode,
            "默认语言应为简体中文");
        Assert(new AppSettings().BootStock && new AppSettings().BootSysNand && new AppSettings().BootEmuNand,
            "三种引导模式应默认全勾");
    }

    // ── 全新安装时的界面状态 ────────────────────────────────────
    /// <summary>
    /// 用户现在的需求是「首次运行默认不勾选任何组件，但保留组件的默认配置选项」。
    /// 此前 <see cref="CheckDefaults"/> 测的是 <c>AppSettings</c> / <c>WizardOptions</c> 的
    /// **对象默认值**，<c>CheckUserConfigBaseline</c> 测的是**生成出来的 ini 内容** ——
    /// 两者都绕开了「界面刚打开时那些勾到底勾没勾、选项值又是什么」。
    ///
    /// 这条用例补的就是这个缺口：删掉 <c>settings.json</c> 后**真的构造一个 `MainViewModel`**，
    /// 逐项核对用户打开软件时看到的状态。两个方向都要钉住：
    /// ① 组件**一个都不勾**（让用户自己挑要装什么）；
    /// ② 各组件内部的选项**仍等于目录默认值** —— 「不勾组件」不等于「选项没有默认值」，
    ///    这两件事很容易在重构时被一起做掉（第 ⑥ 段就是专门守这个的）。
    ///
    /// 它同时是「默认值 ↔ 界面」之间的护栏：以后谁改了某个默认值、却忘了界面读的是另一个来源，
    /// 这里会立刻报警，而不是等用户发现「软件打开还是没按我的配置勾好」。
    /// </summary>
    private static void CheckFreshUiState()
    {
        Section("全新安装：界面打开时的默认勾选状态");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var vm = new MainViewModel();

            // ① 所有组件：默认**全不勾**。
            //    用户要求首次运行不替用户预设，由用户自己挑要装什么。
            //    先钉住前置条件（组件清单非空），否则「一个都没勾」可能是集合为空导致的恒真。
            Assert(vm.Components.Count > 0, $"前置条件：应有组件清单，实际 {vm.Components.Count} 个");

            var selectedByDefault = vm.Components
                .Where(c => c.IsSelected)
                .Select(c => c.Kind)
                .ToList();
            Assert(selectedByDefault.Count == 0,
                $"首次运行应一个组件都不勾（由用户自己挑）；实际勾了：{string.Join("、", selectedByDefault)}");

            // ② 三个引导模式：默认全勾
            Assert(vm.BootStock && vm.BootSysNand && vm.BootEmuNand, "三种引导模式应默认全勾");
            Assert(vm.ShowSysmmcBlank && vm.ShowEmummcBlank, "三个引导全勾时两个屏蔽项都应可见");

            // ③ 屏蔽序列号：用户要求**默认不勾**（想屏蔽的人自己勾，别替他做决定）。
            Assert(!vm.BlankSerialSysmmc, "屏蔽真实破解序列号应默认**不**勾");
            Assert(!vm.BlankSerialEmummc, "屏蔽虚拟破解序列号应默认**不**勾");

            // ④ 连带：没勾屏蔽项 → Sys-patch 不该被强制锁定。
            //    组件本身也默认不勾（首次运行不预设），所以是「未勾 + 可自由勾」。
            var sysPatch = vm.Components.First(c => c.Kind == ComponentKind.SysPatch);
            Assert(!vm.SysPatchForced, "没勾屏蔽序列号时不应强制 Sys-patch");
            Assert(!sysPatch.IsSelected, "首次运行不勾任何组件，Sys-patch 也应不勾");
            Assert(sysPatch.IsSelectable, "没被强制时 Sys-patch 必须可自由勾选");
            Assert(!sysPatch.HasLockHint, "没锁定就不该显示锁定原因");

            // ⑤ 其余开关的默认值
            Assert(!vm.Use90DnsSysmmc, "真实破解系统的 90DNS 应默认关闭");
            Assert(!vm.Use90DnsEmummc, "虚拟破解系统的 90DNS 应默认关闭");
            Assert(!vm.Ram8Gb, "8G 运存应默认关闭（用户 exosphere.ini 里 enable_mem_mode=0）");
            Assert(vm.IncludeComponentFilesInOutput && vm.IncludePayloads,
                "合并组件 / 摆放 payload 都应默认开启");

            // boot.dat 自 2026-09-21 起**默认不勾选**（用户明确要求）。它同时是
            // 「默认值改过」这件事的第二处判据：第一处在 CheckBootDat 里查的是两个模型对象，
            // 这里查的是**界面上那个视图模型** —— 三处默认值漏改任何一处都会红。
            Assert(!vm.IncludeBootDat, "boot.dat 应默认**不**勾选（用户 2026-09-21 要求）");
            Assert(vm.PreferPrerelease, "应默认优先预览版");
            Assert(!vm.CanStart, "一个组件都没勾时「开始」按钮应不可用（勾上任意一个才可用）");
            // 按钮灰着就点不动，RunAsync 里那句 Common.NoSelection 永远弹不出来 ——
            // 所以界面必须自己把原因说出来，否则用户只能对着灰按钮猜。
            Assert(vm.ShowNoSelectionHint, "一个组件都没勾时应显示「请至少勾选一个组件」的提示");

            // ⑤a 2026-09-16 新增的界面默认值
            Assert(!vm.AutoPackZip, "「生成后自动打包为 zip」应默认不勾");
            Assert(vm.BootStockTitle.Length == 0 && vm.BootSysNandTitle.Length == 0 && vm.BootEmuNandTitle.Length == 0,
                "三个引导项显示名应默认为空（空 = 用界面语言的默认名）");
            Assert(vm.BootStockLogo.Length == 0 && vm.BootSysNandLogo.Length == 0 && vm.BootEmuNandLogo.Length == 0,
                "三个启动图应默认为空（空 = 这一项不设启动图）");
            Assert(!vm.HasBootStockLogo && !vm.HasBootSysNandLogo && !vm.HasBootEmuNandLogo,
                "没选启动图时「清除」按钮不该出现");
            Assert(vm.BootStockIcon.Length == 0 && vm.BootSysNandIcon.Length == 0 && vm.BootEmuNandIcon.Length == 0,
                "三个图标应默认为空（空 = 这一项不设图标）");
            Assert(!vm.HasBootStockIcon && !vm.HasBootSysNandIcon && !vm.HasBootEmuNandIcon,
                "没选图标时「清除」按钮不该出现");
            Assert(vm.RepoSources.Count == ComponentCatalog.RepoSources.Count,
                $"界面上的下载源清单应与目录声明一致（{ComponentCatalog.RepoSources.Count} 项）");
            Assert(vm.RepoSources.All(s => s.Value.Length == 0),
                "下载源应默认全为空（空 = 用内置默认地址）");
            Assert(!vm.HasTokenStatus && !vm.IsVerifyingToken, "刚打开时不该显示 Token 验证结果");
            Assert(!vm.ShowAdvancedSettings, "高级设置应默认收起");

            // ⑤b 这里曾经有一条断言：「界面路径不该出现『已默认勾选全部组件』那条日志」。
            //     它已被删除 —— 因为它是**恒真的**：`Log.ComponentsDefaultSelected` 在整个 src/ 里
            //     一次都没出现（`EnsureComponentsSelected()` 只设勾选、不打日志），所以那条断言
            //     永远不可能失败，却让人以为「界面不默认全选」是靠它守着的。
            //     真正的护栏有两处：上面 ⑤a 的**状态**断言（全新界面一个组件都不勾），以及
            //     `CheckFirstRunSelectionContract()` 里的**源码契约**（`EnsureComponentsSelected()`
            //     的调用点只能落在命令行路径 App.xaml.cs 的 RunHeadless 里）。
            //     教训：拿「某条日志没出现」当护栏之前，先确认那条日志**真的会被打出来** ——
            //     否则守的是一个不可能发生的事件。

            // ⑥ 全部选项：界面显示的值必须等于目录里的默认值。
            //    逐个比对而不是抽查 —— 以后新增选项会自动被这条覆盖，不用手工补测试。
            var mismatched = new List<string>();
            foreach (var definition in ComponentCatalog.All)
            {
                var component = vm.Components.First(c => c.Kind == definition.Kind);
                foreach (var optionDefinition in definition.Options)
                {
                    var option = component.FindOption(optionDefinition.Key);
                    if (option is null)
                    {
                        mismatched.Add($"{definition.Kind}/{optionDefinition.Key}：界面上没有这个选项");
                        continue;
                    }

                    if (!string.Equals(option.Value, optionDefinition.DefaultValue, StringComparison.Ordinal))
                    {
                        mismatched.Add(
                            $"{definition.Kind}/{optionDefinition.Key}：界面「{option.Value}」≠ 默认「{optionDefinition.DefaultValue}」");
                    }
                }
            }

            Assert(mismatched.Count == 0,
                "界面上全部选项的取值应与目录默认值逐项一致"
                + (mismatched.Count == 0 ? "" : "，实际有 " + mismatched.Count + " 项不一致：" + string.Join("；", mismatched)));

            // ⑦ 用户配置里几个关键取值单独点名 —— 读日志时一眼能看出对齐了没有
            var atmosphere = vm.Components.First(c => c.Kind == ComponentKind.Atmosphere);
            var hekate = vm.Components.First(c => c.Kind == ComponentKind.Hekate);
            var ultrahand = vm.Components.First(c => c.Kind == ComponentKind.Ultrahand);

            Assert(atmosphere.FindOption("override_key")?.Value == "!L", "override_key 应默认 !L");
            Assert(atmosphere.FindOption("cheat_enable_key")?.Value == "!L", "cheat_enable_key 应默认 !L");
            Assert(atmosphere.FindOption("usb30_force_enabled")?.Value == "1", "usb30_force_enabled 应默认 1");
            Assert(atmosphere.FindOption("enable_sd_card_logging")?.Value == "0", "enable_sd_card_logging 应默认 0");
            Assert(atmosphere.FindOption("sd_card_log_output_directory")?.Value == "atmosphere/binlogs",
                "sd_card_log_output_directory 应默认 atmosphere/binlogs");
            Assert(hekate.FindOption("autoboot")?.Value == "0", "autoboot 应默认 0");
            Assert(hekate.FindOption("autoboot_list")?.Value == "0", "autoboot_list 应默认 0");
            Assert(hekate.FindOption("bootwait")?.Value == "2", "bootwait 应默认 2");
            Assert(hekate.FindOption("updater2p")?.Value == "1", "updater2p 应默认 1");
            Assert(ultrahand.FindOption("key_combo")?.Value == "L+DDOWN", "key_combo 应默认 L+DDOWN");
            Assert(ultrahand.FindOption("powerControlEnabled")?.Value == "1", "powerControlEnabled 应默认 1");
            Assert(ultrahand.FindOption("consoleRegionControlEnabled")?.Value == "0",
                "consoleRegionControlEnabled 应默认 0");
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    // ── 屏蔽序列号 ↔ Sys-patch 联动 ─────────────────────────────
    private static void CheckSysPatchForcing()
    {
        Section("屏蔽序列号 ↔ Sys-patch 联动");

        var vm = new MainViewModel();
        var sysPatch = vm.Components.First(c => c.Kind == ComponentKind.SysPatch);

        // 起点：真实破解 + 虚拟破解都勾上，屏蔽都不勾
        vm.BootSysNand = true;
        vm.BootEmuNand = true;
        vm.BlankSerialSysmmc = false;
        vm.BlankSerialEmummc = false;
        sysPatch.IsSelected = false;

        Assert(vm.ShowSysmmcBlank && vm.ShowEmummcBlank, "两个引导都勾时，两个屏蔽项都应可见");
        Assert(!vm.SysPatchForced, "什么都不屏蔽时不应强制");
        Assert(sysPatch.IsSelectable, "不强制时复选框应可点");

        // 关键回归：「虚拟破解系统」本身**不**触发强制
        Assert(vm.BootEmuNand, "前置条件：虚拟破解勾着");
        Assert(!vm.SysPatchForced, "只勾「虚拟破解系统」不应强制 Sys-patch");

        // ── 需求 1：两个屏蔽项互相独立 ──────────────────────────
        vm.BlankSerialSysmmc = true;
        Assert(vm.SysPatchForced, "只屏蔽 sysMMC 也应强制 Sys-patch");
        Assert(!vm.BlankSerialEmummc, "屏蔽 sysMMC 不应顺手把 emuMMC 也勾上");

        vm.BlankSerialSysmmc = false;
        vm.BlankSerialEmummc = true;
        Assert(vm.SysPatchForced, "只屏蔽 emuMMC 也应强制 Sys-patch");
        Assert(!vm.BlankSerialSysmmc, "屏蔽 emuMMC 不应顺手把 sysMMC 也勾上");

        // ── 需求 2：任一屏蔽项 → 强制勾选并锁定 ─────────────────
        vm.BlankSerialSysmmc = true;
        vm.BlankSerialEmummc = true;
        Assert(vm.SysPatchForced, "两个都屏蔽时更应强制");
        Assert(sysPatch.IsSelected, "强制时应自动把 Sys-patch 勾上");
        Assert(!sysPatch.IsSelectable, "强制时复选框应被锁定");
        Assert(sysPatch.HasLockHint, "锁定时必须给出原因，不能只把框变灰");
        Assert(sysPatch.LockHintKey == "Component.SysPatch.ForcedHint", "锁定原因应指向对应多语言键");
        Assert(!string.IsNullOrWhiteSpace(sysPatch.LockHint), "锁定原因应能取到本地化文案");

        // 两个屏蔽项都取消 → 解锁，但保留用户的勾选（不替用户反选）
        vm.BlankSerialSysmmc = false;
        vm.BlankSerialEmummc = false;
        Assert(!vm.SysPatchForced, "两项都取消后不应再强制");
        Assert(sysPatch.IsSelectable, "取消强制后复选框应恢复可点");
        Assert(!sysPatch.HasLockHint, "解锁后不应再显示锁定原因");
        Assert(sysPatch.IsSelected, "解锁后应保留原有的勾选状态，不替用户反选");

        // ── 需求 3：手动勾 Sys-patch → 两个屏蔽项默认勾上，但可自行取消 ──
        sysPatch.IsSelected = false;
        Assert(!vm.BlankSerialSysmmc && !vm.BlankSerialEmummc, "前置条件：屏蔽项都还没勾");

        sysPatch.IsSelected = true;   // 模拟用户手动勾选
        Assert(vm.BlankSerialSysmmc, "手动勾上 Sys-patch 后，sysMMC 屏蔽应默认勾上");
        Assert(vm.BlankSerialEmummc, "手动勾上 Sys-patch 后，emuMMC 屏蔽应默认勾上");

        // 默认只是「默认」——用户必须能取消（这是需求 3 的后半句）
        vm.BlankSerialSysmmc = false;
        Assert(!vm.BlankSerialSysmmc, "用户应能取消默认勾上的 sysMMC 屏蔽");
        Assert(vm.SysPatchForced, "还剩 emuMMC 屏蔽勾着，Sys-patch 仍应处于强制状态");

        vm.BlankSerialEmummc = false;
        Assert(!vm.SysPatchForced, "两项都取消后应解除强制");
        Assert(sysPatch.IsSelectable, "解除强制后复选框应恢复可点");

        // ── 连锁回归：程序自动勾 Sys-patch 时，不能反过来把另一个屏蔽项也勾上 ──
        // 少了重入保护就会「勾一个屏蔽 = 两个都勾」，直接废掉需求 1。
        sysPatch.IsSelected = false;
        vm.BlankSerialSysmmc = false;
        vm.BlankSerialEmummc = false;
        vm.BlankSerialSysmmc = true;   // 只勾 sysMMC 这一个
        Assert(!vm.BlankSerialEmummc, "自动勾上 Sys-patch 不应连锁把 emuMMC 屏蔽也勾上");
        Assert(sysPatch.IsSelected, "前置条件：Sys-patch 已被自动勾上");

        // ── 边界：屏蔽项的复选框被隐藏时，不能把 Sys-patch 锁死 ──
        // 两个屏蔽项各自跟着对应引导模式显示。引导模式取消后复选框会消失，
        // 若此时仍强制，用户就找不到能解锁的那个勾了。
        vm.BootEmuNand = false;
        Assert(!vm.ShowEmummcBlank, "取消虚拟破解后，emuMMC 屏蔽项应隐藏");
        Assert(!vm.BlankSerialEmummc, "前置条件：emuMMC 屏蔽本来就没勾");
        Assert(vm.SysPatchForced, "sysMMC 屏蔽仍勾着且真实破解可见，应仍然强制");

        vm.BootSysNand = false;
        Assert(!vm.ShowSysmmcBlank && !vm.ShowEmummcBlank, "两个引导都取消后，两个屏蔽项都应隐藏");
        Assert(!vm.SysPatchForced, "屏蔽项全部隐藏时不应再锁死 Sys-patch");
        Assert(sysPatch.IsSelectable, "此时复选框应恢复可点，用户才有办法改");
        Assert(!sysPatch.HasLockHint, "此时不应再显示锁定原因");

        // 重新勾回一个引导 → 屏蔽项重新可见、锁定恢复
        vm.BootSysNand = true;
        Assert(vm.ShowSysmmcBlank, "重新勾上真实破解后 sysMMC 屏蔽项应重新可见");
        Assert(vm.SysPatchForced, "重新可见后应恢复强制");
        Assert(!sysPatch.IsSelectable, "恢复强制后应重新锁定");
        Assert(sysPatch.HasLockHint, "恢复强制后应重新显示锁定原因");

        // 收尾：恢复成不强制，避免影响后续用例
        vm.BlankSerialSysmmc = false;
        Assert(!vm.SysPatchForced, "收尾：取消屏蔽后不应强制");
    }

    // ── 屏蔽序列号：两个引导系统互相独立 ────────────────────────
    private static void CheckBlankSerialIndependence()
    {
        Section("屏蔽序列号：sysMMC / emuMMC 写进 ini 时互相独立");

        var options = new WizardOptions
        {
            Atmosphere = true,
            Hekate = true,
            Ultrahand = false,
            SysPatch = false,
            BootStock = false,
            BootSysNand = true,
            BootEmuNand = true,
            BlankSerialSysmmc = false,
            BlankSerialEmummc = false,
            Use90DnsSysmmc = false,
            Use90DnsEmummc = false,
            Ram8Gb = false,
            // 只验证 exosphere.ini 的内容，显式关掉合并
            IncludeComponentFilesInOutput = false,
        };
        ApplyCatalogDefaults(options);

        // 都不屏蔽
        Generate(options);
        Assert(Read("exosphere.ini").Contains("blank_prodinfo_sysmmc=0"), "都不屏蔽时 sysmmc 应为 0");
        Assert(Read("exosphere.ini").Contains("blank_prodinfo_emummc=0"), "都不屏蔽时 emummc 应为 0");

        // 只屏蔽 sysMMC —— 关键：不能顺手把 emuMMC 也写成 1
        options.BlankSerialSysmmc = true;
        Generate(options);
        Assert(Read("exosphere.ini").Contains("blank_prodinfo_sysmmc=1"), "只屏蔽 sysMMC 时 sysmmc 应为 1");
        Assert(Read("exosphere.ini").Contains("blank_prodinfo_emummc=0"), "只屏蔽 sysMMC 时 emummc 应保持 0");

        // 只屏蔽 emuMMC —— 反向同理
        options.BlankSerialSysmmc = false;
        options.BlankSerialEmummc = true;
        Generate(options);
        Assert(Read("exosphere.ini").Contains("blank_prodinfo_sysmmc=0"), "只屏蔽 emuMMC 时 sysmmc 应保持 0");
        Assert(Read("exosphere.ini").Contains("blank_prodinfo_emummc=1"), "只屏蔽 emuMMC 时 emummc 应为 1");

        // 两个都屏蔽
        options.BlankSerialSysmmc = true;
        Generate(options);
        Assert(Read("exosphere.ini").Contains("blank_prodinfo_sysmmc=1"), "都屏蔽时 sysmmc 应为 1");
        Assert(Read("exosphere.ini").Contains("blank_prodinfo_emummc=1"), "都屏蔽时 emummc 应为 1");

        // 对应的引导模式没勾时，屏蔽不该写进 ini——否则用户会以为已经屏蔽了，
        // 实际上那个引导项根本不存在，屏蔽无从生效。
        options.BootSysNand = false;
        Generate(options);
        Assert(Read("exosphere.ini").Contains("blank_prodinfo_sysmmc=0"),
            "没勾「真实破解系统」时，sysMMC 屏蔽不应写入");
        Assert(Read("exosphere.ini").Contains("blank_prodinfo_emummc=1"),
            "emuMMC 那项不受影响，应仍为 1");
    }

    // ── settings.json 迁移 ──────────────────────────────────────
    private static void CheckSettingsMigration()
    {
        Section("settings.json 迁移：旧的单个屏蔽序列号 → 两个独立开关");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            // 旧版本只有一个 BlankSerial 总开关，语义是「两个都屏蔽」
            File.WriteAllText(path, """{ "BlankSerial": true }""");
            var migrated = SettingsStore.Load();
            Assert(migrated.BlankSerialSysmmc, "旧配置 BlankSerial=true 应迁移到 sysMMC");
            Assert(migrated.BlankSerialEmummc, "旧配置 BlankSerial=true 应迁移到 emuMMC");
            Assert(migrated.BlankSerial is null, "迁移后应清空旧字段，否则每次启动都会用旧值盖掉新选择");

            File.WriteAllText(path, """{ "BlankSerial": false }""");
            var off = SettingsStore.Load();
            Assert(!off.BlankSerialSysmmc && !off.BlankSerialEmummc,
                "旧配置 BlankSerial=false 应迁移成两项都关");

            // 新格式不应被迁移逻辑动到
            File.WriteAllText(path, """{ "BlankSerialSysmmc": true, "BlankSerialEmummc": false }""");
            var modern = SettingsStore.Load();
            Assert(modern.BlankSerialSysmmc, "新格式的 sysMMC 取值应原样保留");
            Assert(!modern.BlankSerialEmummc, "新格式的 emuMMC 取值应原样保留");

            // 全新安装（没有 settings.json）→ 两项都**关**。
            // 用户明确要求：屏蔽序列号是「有需要才勾」的动作，默认不替他做决定。
            File.Delete(path);
            var fresh = SettingsStore.Load();
            Assert(!fresh.BlankSerialSysmmc && !fresh.BlankSerialEmummc,
                "全新安装时两个屏蔽项都应默认关闭（用户要求默认不勾）");
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    // ── 迁移结果真的传到了界面层吗 ──────────────────────────────
    //
    // CheckSettingsMigration 只验到 SettingsStore.Load() 为止。但用户看到的是界面：
    // 如果 MainViewModel 的构造函数漏读了新字段（或读错字段），迁移再正确也没用 ——
    // 界面照样显示「两项都没勾」，而配置里其实写了 blank_prodinfo_*=1，静默不一致。
    // 这里走完整链路：磁盘上的旧 settings.json → Load() → new MainViewModel() → 联动结果。
    private static void CheckMigrationThroughViewModel()
    {
        Section("迁移链路：旧 settings.json → Load() → MainViewModel → 联动");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            // 旧版本：单个总开关开着 + 两个破解引导都勾着
            File.WriteAllText(path, """
                { "BlankSerial": true, "BootStock": true, "BootSysNand": true, "BootEmuNand": true }
                """);

            var vm = new MainViewModel();
            Assert(vm.BlankSerialSysmmc, "旧配置应让界面上的 sysMMC 屏蔽显示为已勾选");
            Assert(vm.BlankSerialEmummc, "旧配置应让界面上的 emuMMC 屏蔽显示为已勾选");

            // 两个屏蔽项都开着 → 应触发强制勾选（这是用户需求②在迁移路径上的表现）
            Assert(vm.SysPatchForced, "迁移后两项屏蔽都开着，应触发 Sys-patch 强制勾选");
            var sysPatch = vm.Components.First(c => c.Kind == ComponentKind.SysPatch);
            Assert(sysPatch.IsSelected, "强制勾选应真的落到 Sys-patch 上");
            Assert(!sysPatch.IsSelectable, "强制勾选时 Sys-patch 应处于锁定状态");
            Assert(sysPatch.HasLockHint, "锁定时应给出原因提示，别让用户对着灰掉的框猜");

            // 旧配置是关的 → 界面两项都不该勾，也不该强制
            File.WriteAllText(path, """
                { "BlankSerial": false, "BootStock": true, "BootSysNand": true, "BootEmuNand": true }
                """);

            var vmOff = new MainViewModel();
            Assert(!vmOff.BlankSerialSysmmc && !vmOff.BlankSerialEmummc,
                "旧配置为关时，界面两项都不应勾选");
            Assert(!vmOff.SysPatchForced, "旧配置为关时不应强制 Sys-patch");
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    // ── 界面状态 → WizardOptions → 生成的 ini ────────────────────
    //
    // BuildWizardOptions 是 internal 的（见 MainViewModel 上的说明），唯一调用方 RunAsync 必须联网，
    // 离线测试原本够不着。但它恰恰是最容易出「复制粘贴错位」的一步：把 emuMMC 那行写成 sysMMC 的值，
    // 界面上看不出任何异常，生成的 exosphere.ini 却是错的 —— 用户以为只屏蔽了一个，实际两个都被抹了。
    // 所以用 InternalsVisibleTo 开放 internal 后，在这里把「装配」这一步单独钉死。
    private static void CheckViewModelToOptionsMapping()
    {
        Section("界面状态 → WizardOptions → exosphere.ini（装配链路）");

        var vm = new MainViewModel();
        vm.BootStock = true;
        vm.BootSysNand = true;
        vm.BootEmuNand = true;
        vm.Components.First(c => c.Kind == ComponentKind.Atmosphere).IsSelected = true;
        vm.Components.First(c => c.Kind == ComponentKind.Hekate).IsSelected = false;
        vm.Components.First(c => c.Kind == ComponentKind.Ultrahand).IsSelected = false;
        vm.Components.First(c => c.Kind == ComponentKind.SysPatch).IsSelected = false;
        vm.IncludeComponentFilesInOutput = false;

        // 不对称取值：两个屏蔽项刻意一开一关。
        // 若装配时把两项写成同一个来源，这里就会暴露 —— 对称取值（true/true 或 false/false）测不出来。
        vm.BlankSerialSysmmc = true;
        vm.BlankSerialEmummc = false;

        var options = vm.BuildWizardOptions();
        Assert(options.BlankSerialSysmmc, "装配后 sysMMC 屏蔽应为 true");
        Assert(!options.BlankSerialEmummc, "装配后 emuMMC 屏蔽应为 false，不能串成 sysMMC 的值");
        Assert(options.BootSysNand && options.BootEmuNand, "引导项状态应一并装配过去");
        Assert(options.Atmosphere && !options.Hekate, "组件勾选状态应一并装配过去");

        Generate(options);
        var exosphere = Read("exosphere.ini");
        Assert(exosphere.Contains("blank_prodinfo_sysmmc=1"), "界面勾了 sysMMC 屏蔽，ini 就应写 1");
        Assert(exosphere.Contains("blank_prodinfo_emummc=0"), "界面没勾 emuMMC 屏蔽，ini 就应写 0");

        // 反向：只屏蔽 emuMMC。两次结果必须不同，否则说明装配时两项被绑在了一起。
        vm.BlankSerialSysmmc = false;
        vm.BlankSerialEmummc = true;

        var reversed = vm.BuildWizardOptions();
        Assert(!reversed.BlankSerialSysmmc && reversed.BlankSerialEmummc, "装配应能反映反向取值");

        Generate(reversed);
        exosphere = Read("exosphere.ini");
        Assert(exosphere.Contains("blank_prodinfo_sysmmc=0"), "反向：ini 的 sysMMC 应为 0");
        Assert(exosphere.Contains("blank_prodinfo_emummc=1"), "反向：ini 的 emuMMC 应为 1");
    }

    // ── 界面设置 → WizardOptions 的「接线」完整性 ──────────────────
    /// <summary>
    /// 界面上的每一项设置都必须真的接进 <see cref="WizardOptions"/>。
    ///
    /// 漏接的症状很静默：用户在界面上改了开关，生成的 ini 却纹丝不动。
    /// <see cref="CheckViewModelToOptionsMapping"/> 只点名了「两个屏蔽项不能串」这一处，
    /// 将来新增设置项时忘了在 <c>BuildWizardOptions()</c> 里接线，**没有任何用例会红**。
    /// 这条把它变成穷举。
    ///
    /// 做法（自维护 —— 将来新增设置项会被自动覆盖，不用手工补断言）：
    /// ① 把界面上每个「WizardOptions 里有同名属性」的开关翻成**与默认值相反**的值；
    /// ② 装配后逐项断言它确实翻过来了 —— 没接线的会停在默认值，立刻暴露；
    /// ③ 反向再扫一遍 WizardOptions：还有哪个标量属性**没有任何界面设置接上去**。
    /// </summary>
    private static void CheckEverySettingIsWiredIntoOptions()
    {
        Section("界面设置 → WizardOptions：每一项都必须真的接上");

        // 这些 WizardOptions 属性**故意**不来自「同名界面开关」。列在这里，免得被误判成漏接。
        //
        // ⚠️ 「组件勾选」那一族（Atmosphere / Hekate / … 共 23 个）**从枚举现取**，不手抄：
        //    它们的来源是组件卡片的勾选状态（`BuildWizardOptions` 里 `TrySetSelected` 逐个写），
        //    本来就不该有同名界面开关。手抄的话，每加一个组件都要往这里补一行 ——
        //    漏了会被下面那条 orphan 断言报成「新增属性忘了接线」，而它其实接得好好的。
        var wiredElsewhere = new HashSet<string>(StringComparer.Ordinal)
        {
            "Language",   // 来自当前界面语言；界面上的语言项叫 SelectedLanguage
            // boot.dat 的那一份「从哪来」：它不是设置，而是**下载阶段填进来的路径**
            // （界面上的设置是 BootDatUrl「从哪下」与 IncludeBootDat「要不要」，见 MainViewModel
            //  的 PrepareBootDatAsync）。为它硬造一个界面开关只会多一个没人改的空框。
            "BootDatPath",
        };

        foreach (var kind in Enum.GetValues<ComponentKind>())
        {
            wiredElsewhere.Add(kind.ToString());
        }

        var defaults = new WizardOptions();
        var optionProperties = typeof(WizardOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && IsScalar(p.PropertyType))
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        // 例外清单自己也会腐坏（属性改名后清单里留个死名字，照样「通过」），先钉住它。
        var staleExceptions = wiredElsewhere.Where(name => !optionProperties.ContainsKey(name)).ToList();
        Assert(staleExceptions.Count == 0,
            "例外清单里的名字都应是 WizardOptions 上真实存在的属性"
            + (staleExceptions.Count == 0 ? "" : "，实际已不存在：" + string.Join("、", staleExceptions)));

        var vm = new MainViewModel();
        var flipped = new List<string>();

        foreach (var property in typeof(MainViewModel)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.CanWrite && IsScalar(p.PropertyType)))
        {
            if (!optionProperties.TryGetValue(property.Name, out var optionProperty)
                || wiredElsewhere.Contains(property.Name))
            {
                continue;   // 界面上有、WizardOptions 里没有对应项（如 AutoPackZip 属收尾动作，不进配置）
            }

            property.SetValue(vm, Opposite(defaults, optionProperty));
            flipped.Add(property.Name);
        }

        // 前置条件：真的翻动了一批，否则下面两条断言可能是恒真的空转。
        Assert(flipped.Count > 0, $"前置条件：至少要能翻动一项界面设置，实际 {flipped.Count} 项");

        var options = vm.BuildWizardOptions();

        var stuck = flipped
            .Where(name => !Equals(optionProperties[name].GetValue(options),
                                   Opposite(defaults, optionProperties[name])))
            .ToList();
        Assert(stuck.Count == 0,
            "界面上翻过的每一项都应体现在 WizardOptions 上（没接线的会停在默认值）"
            + (stuck.Count == 0 ? "" : "，实际没接上：" + string.Join("、", stuck)));

        var orphan = optionProperties.Keys
            .Where(name => !flipped.Contains(name) && !wiredElsewhere.Contains(name))
            .ToList();
        Assert(orphan.Count == 0,
            "WizardOptions 的每个标量属性都应有界面设置接上去（新增属性忘了接，这里会报警）"
            + (orphan.Count == 0 ? "" : "，实际没有来源：" + string.Join("、", orphan)));
    }

    /// <summary>只处理标量：集合类属性（RepoOverrides / Values）另有装配路径，不参与本用例。</summary>
    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive || underlying == typeof(string) || underlying.IsEnum;
    }

    /// <summary>取「与默认值相反」的值：bool 取反，字符串取非空占位。用来判断开关到底有没有接上。</summary>
    private static object Opposite(WizardOptions defaults, PropertyInfo property)
    {
        return property.GetValue(defaults) switch
        {
            bool flag => !flag,
            string text => text.Length == 0 ? "__wired__" : string.Empty,
            var other => other!,
        };
    }

    /// <summary>
    /// 「改一处设置 → 立刻落盘」，取代原先那句 <c>SaveSettingsCommand.Execute(null)</c>。
    ///
    /// 为什么不直接调 <c>FlushSettings()</c>：自动落盘的判据是「**快照真的变了**」
    /// （见 <c>SaveSettingsQuietly</c>），不变就不写盘 —— 而用例里「改完之后发现和磁盘上一模一样」
    /// 完全可能（磁盘上本来就存着那个值，尤其是上一条用例刚写过）。那种时候 FlushSettings
    /// 一声不响地什么都不做，用例随即红在一句与它无关的断言上，而真正的原因是「这次压根没有改动」。
    /// 所以这里**先制造一次必定不同的改动**再落盘：与磁盘状态无关、与用例顺序无关，永远会写。
    ///
    /// 原先靠「点保存必定写盘」的地方，现在都得这么显式地说出「确实有东西要存」——
    /// 按钮没了之后，「无条件写盘」这件事本身就不该再被当成理所当然。
    /// </summary>
    private static void TouchAndFlush(MainViewModel vm)
    {
        vm.PreferPrerelease = !vm.PreferPrerelease;
        vm.FlushSettings();
    }

    // ── 保存方向：界面状态 → settings.json → 重新加载 ─────────────
    //
    // SaveSettings 的赋值错位和 BuildWizardOptions 是同一类风险，但这里更阴险：
    // 它写的是**磁盘上的文件**，错了会跨进程留下来。
    //
    // 最要命的一种：保存时把旧的 BlankSerial 又写回成 true。那样下次启动 Migrate 会**再跑一次**，
    // 拿旧值把两个屏蔽项一起盖成 true —— 用户「只屏蔽一个」的选择被悄悄改回「两个都屏蔽」，
    // 而界面上完全看不出发生过什么。
    //
    // 这里走「改设置 → 自动落盘」这条公开路径（不再有保存按钮，也不必开测试接缝），做一次完整往返。
    private static void CheckSettingsRoundTrip()
    {
        Section("保存链路：界面状态 → settings.json → 重新加载");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            var vm = new MainViewModel();
            vm.BootSysNand = true;
            vm.BootEmuNand = true;

            // 不对称取值：保存方向同样要不对称才测得出来
            vm.BlankSerialSysmmc = true;
            vm.BlankSerialEmummc = false;

            TouchAndFlush(vm);
            Assert(File.Exists(path), "改过设置之后应真的落盘（自动落盘，不再需要按任何按钮）");

            var saved = File.ReadAllText(path);
            Assert(!saved.Contains("\"BlankSerial\": true"),
                "旧字段不该被写回成 true，否则下次启动会二次迁移、盖掉用户的分项选择");

            var reloaded = SettingsStore.Load();
            Assert(reloaded.BlankSerialSysmmc, "往返后 sysMMC 屏蔽应保持 true");
            Assert(!reloaded.BlankSerialEmummc, "往返后 emuMMC 屏蔽应保持 false，不能被串成 true");
            Assert(reloaded.BlankSerial is null, "往返后旧字段应仍为 null，迁移不应复活");

            // 反向，确认上面的断言不是恒真
            vm.BlankSerialSysmmc = false;
            vm.BlankSerialEmummc = true;
            TouchAndFlush(vm);

            var reversed = SettingsStore.Load();
            Assert(!reversed.BlankSerialSysmmc && reversed.BlankSerialEmummc,
                "反向保存也应能正确往返");
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    // ── 设置项的持久化接线 ────────────────────────────────────────
    //
    // `SaveSettings()` 的赋值块与构造函数的读取块都是**手写**的。新增一个设置项时漏掉任一处，
    // 症状都很隐蔽、而且只在「下次启动」才现形：
    //   - 漏了 `SaveSettings()`  → 用户改完关掉程序，下次启动悄悄回到旧值；
    //   - 漏了构造函数读回        → `settings.json` 里明明存着，界面却显示默认值。
    // 两种都不报错、不写日志。靠人眼复查 30 行赋值块迟早会漏（第八节第 41 条）。
    //
    // 判据是「这个字段名有没有在这两段代码里出现过」。名字清单**从 AppSettings 反射生成**，
    // 不手抄 —— 手抄的清单自己会腐烂（§1 教训 4）。
    private static void CheckEverySettingIsPersisted()
    {
        Section("界面设置 → settings.json：每一项都必须真的落盘、也必须真的读回");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到源码目录，无法核对设置项的持久化接线");
            return;
        }

        // 只服务于旧存档迁移的两个字段：新代码只读不写，迁移完就置回 null。
        var migrationOnly = new HashSet<string>(StringComparer.Ordinal) { "BlankSerial", "Use90Dns" };

        var settingsProperties = typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && IsScalar(p.PropertyType))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        // 例外清单自己也会腐坏（字段改名后清单里留个死名字，照样「通过」），先钉住它。
        var staleExceptions = migrationOnly.Where(name => !settingsProperties.Contains(name)).ToList();
        Assert(staleExceptions.Count == 0,
            "例外清单里的名字都应是 AppSettings 上真实存在的字段"
            + (staleExceptions.Count == 0 ? "" : "，实际已不存在：" + string.Join("、", staleExceptions)));

        var lines = File.ReadAllLines(Path.Combine(sourceRoot, "ViewModels", "MainViewModel.cs"));
        var constructorBody = BodyOf(lines, "public MainViewModel()");
        var saveBody = BodyOf(lines, "private void SaveSettings()");
        var materializeBody = BodyOf(lines, "private void MaterializeInto(AppSettings target)");

        // 前置条件：三段都真的取到了。取不到的话下面全是恒真的空转断言（§1 教训 5）。
        Assert(constructorBody.Count > 50
               && constructorBody.Any(l => l.Contains("StartCommand = Command(new(", StringComparison.Ordinal)),
            $"前置条件：应取到 MainViewModel 的构造函数体（实际 {constructorBody.Count} 行）");
        Assert(saveBody.Count > 0
               && saveBody.Any(l => l.Contains("MaterializeInto(_settings)", StringComparison.Ordinal)),
            $"前置条件：SaveSettings 应把「界面 → 存档」整段交给 MaterializeInto（实际 {saveBody.Count} 行）");
        Assert(materializeBody.Count > 30
               && materializeBody.Any(l => l.Contains("target.Components =", StringComparison.Ordinal)),
            $"前置条件：应取到 MaterializeInto 的方法体（实际 {materializeBody.Count} 行）");

        // ⚠️ 反向：映射**只许有这一处**。SaveSettings 里若又冒出一段自己的 `_settings.X = `，
        //    两条路（点保存 / 自动落盘）就会分叉，而症状是「日志说的改动」与「文件里写的东西」对不上 ——
        //    两句话都出自同一个程序，用户不知道该信哪一份。判据是「有没有第二份」，不是「哪份更对」。
        var duplicated = saveBody.Where(l => Regex.IsMatch(l, @"_settings\.[A-Za-z_]\w*\s*=")).ToList();
        Assert(duplicated.Count == 0,
            "SaveSettings 里不该再有自己手写的字段赋值 —— 「界面 → 存档」的映射只许留在 MaterializeInto 一处"
            + (duplicated.Count == 0 ? "" : "，实际还有：" + string.Join("；", duplicated.Select(l => l.Trim()))));

        // ⚠️ 写入侧要看 **MaterializeInto** 而不是 SaveSettings：映射整段搬到了前者
        //    （它同时被构造函数用来取差异基线 —— 见那条方法的注释）。
        //    上面那条前置条件保证 SaveSettings 确实调它，两段合起来才等价于原来的断言。
        var written = MentionedSettingsFields(materializeBody, "target");
        var read = MentionedSettingsFields(constructorBody);

        var notWritten = settingsProperties.Except(written).Except(migrationOnly)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert(notWritten.Count == 0,
            "AppSettings 的每个标量字段都应在 MaterializeInto() 里被写入，否则用户改完一重启就丢"
            + (notWritten.Count == 0 ? "" : "，实际没写：" + string.Join("、", notWritten)));

        var notRead = settingsProperties.Except(read).Except(migrationOnly)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert(notRead.Count == 0,
            "AppSettings 的每个标量字段都应在 MainViewModel 构造函数里被读回，否则文件里存着、界面却显示默认值"
            + (notRead.Count == 0 ? "" : "，实际没读：" + string.Join("、", notRead)));
    }

    // ── 自动落盘接线（用户 2026-09-18：打开软件后的每一次操作都要写进 settings.json，
    //    下次打开时读回来）────────────────────────────────────────────────────
    //
    // 这条链路的坏法**全是静默的**：定时器没接上、跳过表把某个设置也吞了、
    // 关窗前那一次没补写、自动落盘与「保存设置」按钮各写一套快照……
    // 而 `DispatcherTimer` 只在真正的消息泵里才会 Tick —— 控制台回归里**永不触发**，
    // 所以行为测试够不着它，只能钉「声明与源码契约」（§1 教训 4：行为测试够不着 UI 启动路径）。
    private static void CheckAutoSaveWiring()
    {
        Section("自动落盘：界面上每一次操作都要写进 settings.json");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到源码目录，无法核对自动落盘的接线");
            return;
        }

        var lines = File.ReadAllLines(Path.Combine(sourceRoot, "ViewModels", "MainViewModel.cs"));
        var text = string.Join('\n', lines);

        // ① 定时器必须在**字段声明处**就建出来（而不是构造函数末尾的产物）。
        //    构造函数中途就会发出变更通知（UpdateForcedSelections 改 IsSelected，
        //    而组件变更通知是更早订阅的），那时若定时器还是 null 就当场空引用崩溃 ——
        //    本轮就是这么崩的，回归当场抓到过（NullReferenceException at ScheduleAutoSave）。
        Assert(Regex.IsMatch(text, @"private readonly DispatcherTimer _autoSaveTimer = new\("),
            "自动落盘的定时器必须在字段声明处建出来（挪到构造函数末尾会出现「通知先到、定时器还没建」的时序窗口）");

        // ①b 「上膛」必须排在构造函数末尾。早了的话，构造函数中途那些赋值
        //     （读存档、按存档做联动归一化）会被当成用户操作写回存档 ——
        //     磁盘上看着没变，但「启动即写」会让排查的人无法用文件时间戳判断值的来源。
        var ctorBody = BodyOf(lines, "public MainViewModel()");
        var hookIndex = ctorBody.FindIndex(l => l.Contains("HookAutoSaveSources()", StringComparison.Ordinal));
        var armIndex = ctorBody.FindIndex(l => l.Contains("_autoSaveArmed = true", StringComparison.Ordinal));
        Assert(hookIndex >= 0 && armIndex > hookIndex,
            $"「上膛」(_autoSaveArmed = true) 必须排在构造函数最后、HookAutoSaveSources() 之后"
            + $"（实际 HookAutoSaveSources 在第 {hookIndex} 行、上膛在第 {armIndex} 行）");

        // ② 落盘的**唯一**实现是 SaveSettings()，自动落盘必须调它、不许自己再写一套赋值块。
        //    （用户 2026-09-19 取消了「保存设置」按钮，于是自动落盘成了唯一调用者 ——
        //    这更该钉住：没有第二条路可以对账了，落盘写错就彻底没人发现。）
        var quietBody = BodyOf(lines, "private void SaveSettingsQuietly()");
        Assert(quietBody.Count > 0 && quietBody.Any(l => l.Contains("SaveSettings()", StringComparison.Ordinal)),
            "自动落盘必须调 SaveSettings()（落盘的唯一实现），不能自己再写一套赋值块");

        // ②b 「保存设置」按钮**不许回来**（用户 2026-09-19 明确要求取消）。
        //     它回来本身不算「坏」，但会让用户重新面对「我改了没点它，到底存没存」——
        //     而这正是当初取消它的理由。命令在源码里只经 Command() 建，所以属性还在就等于按钮还在。
        Assert(typeof(MainViewModel).GetProperty("SaveSettingsCommand") is null,
            "「保存设置」按钮已按要求取消：MainViewModel 上不该再有 SaveSettingsCommand");
        Assert(!File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml"))
                .Contains("SaveSettingsCommand", StringComparison.Ordinal),
            "MainWindow.xaml 里不该再绑到 SaveSettingsCommand —— WPF 的绑定失败是**静默**的："
            + "界面照常显示，点下去什么也不发生");

        // ③ 三类子视图模型的变更都要接上。少接一类 = 那一类改完不落盘，
        //    而界面上一切正常，下次启动才发现被打回原形。
        var hookBody = BodyOf(lines, "private void HookAutoSaveSources()");
        Assert(hookBody.Count > 0, "前置条件：应取到 HookAutoSaveSources 的方法体");
        Assert(hookBody.Any(l => l.Contains("option.ValueChanged", StringComparison.Ordinal)),
            "配置项（OptionViewModel）的取值变化必须接进自动落盘");
        Assert(hookBody.Any(l => l.Contains("RepoSources", StringComparison.Ordinal)),
            "「下载源」的改动必须接进自动落盘");
        Assert(hookBody.Any(l => l.Contains("AssetSlotGroups", StringComparison.Ordinal)),
            "「插件文件」槽的改动必须接进自动落盘");
        Assert(BodyOf(lines, "private void OnComponentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)")
                   .Any(l => l.Contains("ScheduleAutoSave()", StringComparison.Ordinal)),
            "组件勾选的变化必须接进自动落盘（勾了哪些组件是设置，下次打开要一模一样）");

        // ④ 跳过表：只许跳过**运行时**属性，且不许腐烂。
        var transient = MainViewModel.TransientPropertyNames;
        Assert(transient.Count > 0, "前置条件：跳过表不该是空的，否则这条用例退化成空跑");

        var stale = transient.Where(name => typeof(MainViewModel).GetProperty(name) is null)
            .OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert(stale.Count == 0,
            "跳过表里的名字都应是 MainViewModel 上真实存在的属性（改名后忘了改这里，跳过表自己就烂了）"
            + (stale.Count == 0 ? "" : "，实际已不存在：" + string.Join("、", stale)));

        var settingsNames = typeof(AppSettings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        var swallowed = transient.Where(settingsNames.Contains)
            .OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert(swallowed.Count == 0,
            "跳过表里**不许**出现 settings.json 的字段名 —— 那等于这个设置永远不会自动落盘，"
            + "而界面上完全看不出来（它只是个「性能跳过表」，不是白名单；要跳过就得先确认它不是设置）"
            + (swallowed.Count == 0 ? "" : "，实际混进了：" + string.Join("、", swallowed)));

        // ⑤ 关窗前必须补写一次。自动落盘有 400ms 的节流窗口，
        //    而「改完顺手就关窗」完全可能落在窗口里 —— 那一次改动会凭空消失。
        var windowLines = File.ReadAllLines(Path.Combine(sourceRoot, "MainWindow.xaml.cs"));
        var windowText = string.Join('\n', windowLines);
        Assert(windowText.Contains("Closing += OnClosing;", StringComparison.Ordinal),
            "MainWindow 必须订阅 Closing，否则关窗时的补写根本没机会跑");
        Assert(BodyOf(windowLines, "private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)")
                   .Any(l => l.Contains("FlushSettings()", StringComparison.Ordinal)),
            "关窗时必须调用 FlushSettings()（把节流窗口里那一次改动补写出去）");

        CheckPropertySettersDoNotWriteSettings(lines);
    }

    /// <summary>
    /// 不变式：**属性 setter 一律不写存档、也不碰 <c>_settings</c>**。
    ///
    /// 为什么值得单独立一条：界面上的每一处改动都会走某个 setter，而 setter 里写盘看着「更即时」、
    /// 也**能跑**，于是它极容易被再写一遍 —— 三条后果全是静默的：
    /// <list type="number">
    ///   <item>**启动即写**：构造函数末尾会把存档里的值读回成属性（例如语言），
    ///     那次赋值照样触发 setter ⇒ 打开软件就写一次 <c>settings.json</c>，
    ///     文件时间戳从此不能说明「值是谁写的」；</item>
    ///   <item>**写出没映射过的快照**：<c>_settings.Components</c> / <c>OptionValues</c> 要等到第一次
    ///     <see cref="MainViewModel.SaveSettings"/> 才填满，所以写入的是一份 <c>"Components": {}</c> 的存档 ——
    ///     而空 Components 在别处**有语义**（「用户从没勾过 ⇒ 命令行默认全选」）；</item>
    ///   <item>**两处都写 ⇒ 迟早分叉**（第 ② 条那个「同一份快照」的翻版）。</item>
    /// </list>
    ///
    /// 这条不变式是靠幂等护栏抓出来的：测试跑完 <c>settings.json</c> 平白多出来一个文件，
    /// 而护栏只能报「多出来了」，看不出是谁写的（为此还加了按小节定位的探针）。
    /// **判据：能即时生效的是「界面」（<c>LocalizationService.SetLanguage</c> 这类），
    /// 落盘一律交给统一落盘层。**
    /// </summary>
    private static void CheckPropertySettersDoNotWriteSettings(IReadOnlyList<string> lines)
    {
        var propertyBodies = new List<(string Name, List<string> Body)>();

        for (var i = 0; i < lines.Count; i++)
        {
            // 本文件里属性的写法是固定的：「4 空格缩进 + 声明行」紧跟一行「4 空格 + {」。
            // 对不上的（表达式体属性等）直接跳过 —— 下面那条前置条件负责钉住「扫到的数量不是 0」。
            if (!lines[i].StartsWith("    ", StringComparison.Ordinal)
                || !lines[i].EndsWith("{", StringComparison.Ordinal)
                || lines[i].Trim().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var declaration = i > 0 ? lines[i - 1] : string.Empty;
            if (!declaration.StartsWith("    public ", StringComparison.Ordinal)
                || declaration.Contains('(')
                || declaration.Contains('='))
            {
                continue;
            }

            var name = declaration.Trim().Split(' ')[^1];
            var body = new List<string>();
            for (var j = i; j < lines.Count; j++)
            {
                body.Add(lines[j]);
                if (j > i && lines[j] == "    }")
                {
                    break;
                }
            }

            propertyBodies.Add((name, body));
        }

        Assert(propertyBodies.Count >= 20,
            $"前置条件：应扫到 20 个以上属性才谈得上「属性都不写存档」（实际 {propertyBodies.Count} 个，"
            + "扫描格式可能已经不匹配本文件的写法了）");

        // ⚠️ 先剔除注释行。这条不是洁癖：本文件里**解释「为什么 setter 不写存档」的那段注释
        //    自己就写着 `_settings.Language`**，不剔除的话这条断言会红在一句正确的注释上
        //    —— 这类扫描必须剔除注释行，是第 75 条记过的原话，本轮又踩了一次。
        var offenders = propertyBodies
            .Where(p => p.Body
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Any(l => l.Contains("SettingsStore.Save", StringComparison.Ordinal)
                          || l.Contains("_settings.", StringComparison.Ordinal)))
            .Select(p => p.Name)
            .ToList();

        Assert(offenders.Count == 0,
            "属性 setter 里不许写存档、也不许碰 _settings —— 那会造成「启动即写」+ 写出没映射过的快照"
            + "（界面能即时生效的只管界面，落盘交给 MaterializeInto + 自动落盘那一层）"
            + (offenders.Count == 0 ? "" : "，实际越界的属性：" + string.Join("、", offenders)));
    }

    /// <summary>
    /// 自动落盘的**行为**验证：改一个设置、什么都不点，等过节流窗口后 settings.json 里就该有新值。
    ///
    /// 为什么这条能跑、而别处说「行为测试够不着」：<c>DispatcherTimer</c> 只在**消息泵**里才 Tick，
    /// 控制台测试默认没有泵 —— 所以这里显式推一个 <see cref="DispatcherFrame"/> 把那一小段时间泵过去。
    /// 少了它，「自动落盘」就只剩源码契约，没有一条真跑过的断言。
    /// </summary>
    private static void CheckAutoSaveBehaviour()
    {
        Section("自动落盘（行为）：改完不点保存，等一会儿也该落进 settings.json");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            var vm = new MainViewModel();

            // 取值必须与磁盘上**不同**，否则「文件里本来就是它」会让断言变成恒真。
            var before = SettingsStore.Load().BootSysNand;
            var target = !before;

            vm.BootSysNand = target;

            // 立刻读一次：节流窗口还没过，此时**不该**已经落盘。
            // 反过来说，这条同时钉住了「不是每敲一个键就写一次文件」。
            Assert(SettingsStore.Load().BootSysNand == before,
                "刚改完、节流窗口还没过时不该已经落盘（否则自动落盘就成了每次按键都写一次文件）");

            PumpDispatcher(TimeSpan.FromMilliseconds(MainViewModel.AutoSaveDelayMs + 500));

            var after = SettingsStore.Load().BootSysNand;
            Assert(after == target,
                $"改完设置后等过节流窗口，settings.json 里就该是新值（期望 {target}，实际 {after}）");

            // 反向再走一遍：不然「碰巧是目标值」和「真的落盘了」分不开
            vm.BootSysNand = before;
            PumpDispatcher(TimeSpan.FromMilliseconds(MainViewModel.AutoSaveDelayMs + 500));
            Assert(SettingsStore.Load().BootSysNand == before,
                "反向改动同样要落盘 —— 否则「自动落盘」只对某些取值生效");
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    // ── 操作日志（用户 2026-09-18：点了哪个按钮、输了什么文字，都要能查）──────
    //
    // 这条链路的证据**只能在文件里**：界面日志一关窗就没了，而它要回答的恰恰是
    // 「上次跑的时候我点了什么」。所以这里不开接缝、不加抽象，直接读那个文件。
    /// <summary>
    /// 这一段的断言会**真的改动 `settings.json`**（勾选框、Token、引导项开关都要经 SaveSettings 落盘），
    /// 所以整段外面套一层备份 / 还原。
    ///
    /// ⚠️ 不还原的两个后果，都在实测里撞到过：
    /// <list type="number">
    ///   <item>那枚**假 Token** 会留在存档里 —— 一个专门用来证明「机密不落日志」的字符串，
    ///     自己先落进了一个 plainly 可读的文件；</item>
    ///   <item>后面 <c>CheckComponentSelectionPersistence</c> 的「屏蔽序列号 → 强制勾 Sys-patch」
    ///     依赖存档里的 <c>BootSysNand</c>，而本段那句 <c>vm.BootSysNand = !vm.BootSysNand</c>
    ///     把它**永久翻了个面** ⇒ 那条断言在「红 / 绿」之间来回跳，而两次运行的代码一模一样
    ///     （实测：同一份代码连跑三次，红、绿、红）。</item>
    /// </list>
    /// 判据是「**测试自己必须是幂等的**」：一个用例的结果取决于上一次谁跑过，它就不再是断言，
    /// 而是一枚硬币。Main 末尾那条「settings.json 与开跑前逐字节相同」是这条的兜底护栏。
    /// </summary>
    private static void CheckOperationLogging()
    {
        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            CheckOperationLoggingCore();
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    private static void CheckOperationLoggingCore()
    {
        Section("操作日志：按钮点击与设置改动都要落进 logs/ 的日志文件");

        // 日志必须写在运行目录**里面**。写在别处的话，发布流水线的「残留运行痕迹」检查
        // （build.sh / refresh-dist.py 都盯 <exe>/logs）就看不见它，日志目录有可能被打进发布包。
        var root = AppPaths.LogRoot;
        Assert(root.StartsWith(AppPaths.BaseDirectory, StringComparison.OrdinalIgnoreCase),
            $"日志目录应在运行目录之内（发布流水线靠这一点拦截运行痕迹），实际：{root}");
        Assert(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) == "logs",
            "日志目录应叫 logs —— 那是流水线里被明确列为「不该进发布包」的名字");

        var logPath = LogFile.CurrentFilePath;
        var lengthBefore = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
        Assert(LogFile.DisabledReason is null,
            $"前置条件：日志文件应可写，否则下面的断言全是空跑（原因：{LogFile.DisabledReason}）");

        var vm = new MainViewModel();

        // ① 点一次按钮 → 日志里要有「操作 · 点击「高级设置」」。
        //    原先点的是「保存设置」，而它已按要求取消（用户 2026-09-19）。
        //    换成「高级设置」还有个额外好处：它是个**只动界面、不动任何设置项**的按钮，
        //    于是这条同时证明「点按钮留痕」与「有没有东西要落盘」是两件独立的事。
        vm.ToggleAdvancedCommand.Execute(null);

        // ② 改一个设置（等价于用户点了一下勾选框）→ 日志里要有「操作 · 设置变更」
        vm.BootSysNand = !vm.BootSysNand;
        vm.FlushSettings();

        var appended = ReadFrom(logPath, lengthBefore);
        Assert(appended.Contains("操作 · 点击「高级设置」", StringComparison.Ordinal),
            "点击按钮必须留痕：日志里应出现「操作 · 点击「高级设置」」"
            + $"；实际新增内容：{Flatten(appended)}");
        Assert(appended.Contains("操作 · 设置变更：", StringComparison.Ordinal),
            "设置改动必须留痕：日志里应出现「操作 · 设置变更：…」" + $"；实际新增内容：{Flatten(appended)}");
        Assert(appended.Contains("BootSysNand", StringComparison.Ordinal)
               && appended.Contains("→", StringComparison.Ordinal),
            "设置变更那一行要指明**改的是哪一项**以及旧值→新值" + $"；实际新增内容：{Flatten(appended)}");

        // ③ 机密绝不进日志。
        //    这条是硬约束：日志是**要发给别人看的**，而服务端密码、GitHub Token 一旦写进去，
        //    收回来是不可能的。所以注入一个一眼能认出来的假 Token，断言它**一次都不出现**。
        lengthBefore = new FileInfo(logPath).Length;
        const string secret = "ghp_THIS_MUST_NEVER_APPEAR_0123456789";
        vm.GitHubToken = secret;
        vm.FlushSettings();

        var secretSection = ReadFrom(logPath, lengthBefore);
        Assert(!secretSection.Contains(secret, StringComparison.Ordinal),
            "GitHub Token 绝不能被写进日志（日志是要发给别人排查问题的）"
            + $"；实际新增内容：{Flatten(secretSection)}");
        Assert(secretSection.Contains("GitHubToken", StringComparison.Ordinal)
               && secretSection.Contains("已设置", StringComparison.Ordinal),
            "但「改过 Token」这件事要留下，且只记「设了没有」不记内容"
            + $"；实际新增内容：{Flatten(secretSection)}");

        // 反向：把 Token 清掉也要留痕，且同样不出现内容
        lengthBefore = new FileInfo(logPath).Length;
        vm.GitHubToken = string.Empty;
        vm.FlushSettings();
        var cleared = ReadFrom(logPath, lengthBefore);
        Assert(cleared.Contains("GitHubToken", StringComparison.Ordinal)
               && cleared.Contains("已清空", StringComparison.Ordinal),
            "清空 Token 也要留痕（只记「已清空」）" + $"；实际新增内容：{Flatten(cleared)}");

        // ⑤ 非法地址的告警**不能跟着「保存设置」按钮一起消失**。
        //    它原先挂在那条路上（用户按一次 = 报一次，天然不会重复）；按钮取消后它挪进了自动落盘，
        //    而自动落盘随时可能结算一次 ⇒ 必须自带去重，否则边打字边刷屏。
        //    这条同时钉住两件事：**还报**（不然用户填错了没任何痕迹）和**只报一次**。
        lengthBefore = new FileInfo(logPath).Length;
        var repoRow = vm.RepoSources[0];

        // ⚠️ 造非法值要挑**真的解析不出来**的：`owner/name` 的判据是「恰好两段」，
        //    所以「这不是一个 owner/name」这种带一个斜杠的字符串**是合法的**（实测踩过 ——
        //    它当场让这条断言红了，而红的原因是测试数据不对，不是程序不对）。
        repoRow.Value = "没有斜杠";
        vm.FlushSettings();
        vm.FlushSettings();   // 再结算一次：同一个非法值不许再报一遍

        var invalidSection = ReadFrom(logPath, lengthBefore);
        var invalidLines = invalidSection.Split('\n')
            .Count(l => l.Contains("不是合法的 owner/name", StringComparison.Ordinal));
        Assert(invalidLines == 1,
            "填了不合法的下载源地址要报**一次**：取消「保存设置」按钮之后这条告警挪到了自动落盘里，"
            + "而自动落盘会反复结算 —— 不去重就会边打字边刷屏，真正要看的那条被淹掉"
            + $"（实际报了 {invalidLines} 次）" + $"；实际新增内容：{Flatten(invalidSection)}");

        // 反向：改成一个**另一个**非法值 → 要能再报一次（判据是「这个值报过没有」，
        // 不是「报过没有」—— 后者会让用户以为改完就好了）
        lengthBefore = new FileInfo(logPath).Length;
        repoRow.Value = "也没有斜杠";
        vm.FlushSettings();
        Assert(ReadFrom(logPath, lengthBefore).Contains("不是合法的 owner/name", StringComparison.Ordinal),
            "换成另一个非法值时应当再报一次");

        // 收尾：把这一行还原，免得把非法地址带进后面的用例（外面那层备份/还原是最后一道，
        // 但不该让它替我们兜住这种本可以避免的事）
        repoRow.Value = string.Empty;
        vm.FlushSettings();

        CheckSettingsDiff();
        CheckOperationLoggingWiring();
        CheckLogRetention();

        // ⚠️ 收尾：把这一段那个 VM 里**还挂着的自动落盘定时器**卸掉（FlushSettings 先停表再落盘，
        //    于是这次写在窗口内发生，随后被外层还原）。
        //
        //    不这么做的话，它会在这段外面、几百毫秒之后（下一个用例跑到一半时）真的写一次文件——
        //    那时备份/还原早已结束，于是「用例改过的存档」又冒出来一份。实测症状是 Main 末尾那条
        //    幂等护栏报「跑完与开跑前不一样」，而**看不出是谁写的**。
        //    判据：**延迟写也是写**，只要它能逃出备份窗口，「用例自己还原」这句话就不成立。
        vm.FlushSettings();
    }

    /// <summary>读日志文件里「从 <paramref name="offset"/> 字节之后」的内容。文件不存在时返回空串。</summary>
    private static string ReadFrom(string path, long offset)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>把多行文本压成一行，方便塞进断言消息（日志里带换行会把汇总行冲散）。</summary>
    private static string Flatten(string text) =>
        text.Replace("\r", " ").Replace("\n", " ｜ ").Trim();

    /// <summary>
    /// 差异函数是**纯函数**，所以直接喂固定输入断言输出 —— 不必真去建一个 MainViewModel。
    /// 覆盖：新增 / 删除 / 改动 / 没变 / 机密。
    /// </summary>
    private static void CheckSettingsDiff()
    {
        const string secretKey = "GitHubToken";

        var before = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BootSysNand"] = "True",
            ["BootStockTitle"] = string.Empty,
            ["Components.Hekate"] = "False",
            ["OptionValues.Ultrahand/autoboot"] = "0",
            [secretKey] = string.Empty,
        };
        var after = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BootSysNand"] = "False",
            ["BootStockTitle"] = "zbxt",
            ["Components.Hekate"] = "True",
            // autoboot 原样 ⇒ 不该报
            ["OptionValues.Ultrahand/autoboot"] = "0",
            [secretKey] = "ghp_secret",
            ["AutoPackZip"] = "True",   // 新增项
        };

        var changes = SettingsSnapshot.Diff(before, after);
        var keys = changes.Select(c => c.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert(keys.SequenceEqual(
                new[] { "AutoPackZip", "BootStockTitle", "BootSysNand", "Components.Hekate", secretKey },
                StringComparer.Ordinal),
            "差异应恰好是「改动的 + 新增的」，原样不变的项不该出现；实际：" + string.Join("、", keys));

        var title = changes.First(c => c.Key == "BootStockTitle");
        Assert(title.OldValue.Length == 0 && title.NewValue == "zbxt",
            "改了值的项要带上「旧 → 新」两个值（这就是「用户输了什么文字」那条证据）"
            + $"；实际：'{title.OldValue}' → '{title.NewValue}'");

        Assert(changes.First(c => c.Key == secretKey).IsSecret,
            "机密键必须被标记出来 —— 标记是「显示层不许打原文」的唯一判据");
        Assert(!changes.First(c => c.Key == "BootSysNand").IsSecret,
            "普通设置项不该被误标成机密（误标会让日志失去「改成什么了」这个最重要的信息）");

        Assert(SettingsSnapshot.Diff(before, before).Count == 0,
            "两份一模一样的快照之间不该有任何差异（否则每次落盘都会白报一行）");

        // 键名包含式判定：将来冒出 GitHubTokenBackup 之类的也必须被挡住。
        // 「漏挡一个」的后果远比「多挡一个」严重。
        Assert(SettingsSnapshot.IsSecret("GitHubTokenBackup") && SettingsSnapshot.IsSecret("Proxy")
               && SettingsSnapshot.IsSecret("SomePasswordField"),
            "机密判定要按**键名包含**而不是精确相等：漏挡一个等于把口令写进要发出去的日志里");
    }

    /// <summary>
    /// 两条接线都要在**源码**上钉住 —— 它们的坏法都是静默的，而行为断言够不着
    /// （按钮有 25 个、设置项 76 个，靠人眼复查「都加了没有」迟早会漏）。
    /// </summary>
    private static void CheckOperationLoggingWiring()
    {
        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到源码目录，无法核对操作日志的接线");
            return;
        }

        var viewModelPath = Path.Combine(sourceRoot, "ViewModels", "MainViewModel.cs");
        var lines = File.ReadAllLines(viewModelPath);
        var text = string.Join('\n', lines);

        // ① 界面日志 → 文件日志只有一处桥（Append）。日志有几十个出口，逐个加一行必漏。
        var appendBody = BodyOf(lines, "private void Append(LogLevel level, string message)");
        Assert(appendBody.Any(l => l.Contains("LogFile.Write(level, message)", StringComparison.Ordinal)),
            "界面日志必须统一由 Append 桥接到日志文件 —— 逐个调用点手写迟早会漏，"
            + "而漏掉的那条恰恰可能是用户要拿来定位问题的那条");

        // ② 按钮：除 Command() 自己以外，不许再出现 new RelayCommand(
        //    （新加按钮时绕不过去，否则点击就不会进日志）。
        var commandBody = BodyOf(lines, "private RelayCommand Command(CommandLabel label, Action execute, Func<bool>? canExecute = null)");
        Assert(commandBody.Count > 0, "前置条件：应取到 Command() 的方法体");

        // ⚠️ 先剔除注释行：解释「为什么不这么写」的注释里必然出现 `new RelayCommand(`，
        //    不剔的话这条断言会红在一句正确的注释上（第 75 条那条教训的原话：
        //    「写这类源码扫描时先剔除注释行，否则解释『为什么不这么写』的注释会把断言弄红」）。
        var rawConstructions = lines
            .Select((line, index) => (line, index))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                        && x.line.Contains("new RelayCommand(", StringComparison.Ordinal)
                        && !commandBody.Contains(x.line))
            .ToList();
        Assert(rawConstructions.Count == 0,
            "所有按钮都必须经 Command() 建（否则点击不进日志文件，而漏记是静默的）"
            + (rawConstructions.Count == 0
                ? ""
                : "，实际还有：" + string.Join("、", rawConstructions.Select(x => $"第 {x.index + 1} 行"))));

        // ③ 每个按钮的标签键都必须在三份语言包里取得到文案。
        //    标签是「点了哪个按钮」的唯一证据，取不到就会往日志里写一串键名。
        var labels = Regex.Matches(text, @"Command\(new\(([^)]*)\)")
            .Select(m => Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"")
                .Select(k => k.Groups[1].Value)
                .ToList())
            .ToList();

        Assert(labels.Count >= 20,
            $"应能从源码里收集到每个按钮的标签（实际只找到 {labels.Count} 个，正则可能失效了）");

        var unknown = new List<string>();
        foreach (var code in new[] { "zh-Hans", "zh-Hant", "en-US" })
        {
            LocalizationService.Instance.SetLanguage(code);

            foreach (var key in labels.SelectMany(l => l))
            {
                var resolved = LocalizationService.Instance[key];
                if (string.IsNullOrWhiteSpace(resolved) || resolved == key)
                {
                    unknown.Add($"{code}/{key}");
                }
            }
        }

        LocalizationService.Instance.SetLanguage("zh-Hans");
        Assert(unknown.Count == 0,
            "每个按钮的标签键三份语言包都要有文案（取不到的话日志里写的是键名）"
            + (unknown.Count == 0 ? "" : "，缺：" + string.Join("、", unknown)));

        // ④ XAML 里那个按钮的文案键，必须就是标签的**最后一个**键 ——
        //    这条把「日志里说的按钮」与「用户看到的按钮」物理对齐，光验「键存在」抓不到抄错。
        var xaml = File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml"));
        var buttonKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match tag in Regex.Matches(xaml, @"<Button\b[^>]*?/?>", RegexOptions.Singleline))
        {
            var command = Regex.Match(tag.Value, @"Command=""\{Binding ([A-Za-z]+)\}""");
            var content = Regex.Match(tag.Value, @"Content=""\{loc:Loc ([A-Za-z0-9_.]+)\}""");
            if (command.Success && content.Success)
            {
                buttonKeys[command.Groups[1].Value] = content.Groups[1].Value;
            }
        }

        Assert(buttonKeys.Count >= 20,
            $"应能从 XAML 里收集到每个按钮的文案键（实际只找到 {buttonKeys.Count} 个，正则可能失效了）");

        var mismatched = new List<string>();
        foreach (Match m in Regex.Matches(text, @"(\w+Command)\s*=\s*Command\(new\(([^)]*)\)"))
        {
            var property = m.Groups[1].Value;
            var keys = Regex.Matches(m.Groups[2].Value, "\"([^\"]+)\"")
                .Select(k => k.Groups[1].Value)
                .ToList();

            if (keys.Count == 0 || !buttonKeys.TryGetValue(property, out var contentKey))
            {
                continue;
            }

            if (!string.Equals(keys[^1], contentKey, StringComparison.Ordinal))
            {
                mismatched.Add($"{property}: 日志标的是 {keys[^1]}、按钮上是 {contentKey}");
            }
        }

        Assert(mismatched.Count == 0,
            "日志里那个按钮名，最后一个键必须是按钮上真正显示的那句话（否则日志说的是另一个按钮）"
            + (mismatched.Count == 0 ? "" : "，对不上：" + string.Join("；", mismatched)));
    }

    /// <summary>
    /// 保留策略：只留最近若干份，且**只删自己的文件**。
    ///
    /// 正常路径下这一整套一整个进程只跑一次，集成测试根本触发不到，所以这里直接调那个接缝。
    /// </summary>
    private static void CheckLogRetention()
    {
        AppPaths.EnsureDirectory(AppPaths.LogRoot);

        // 造 12 份「很旧」的日志（2001 年）+ 1 份不属于自己的文件。
        // 日期故意排在今天之前，这样今天那份（前几条断言刚写过）不会因为被挤掉而丢失证据。
        for (var i = 1; i <= 12; i++)
        {
            File.WriteAllText(
                Path.Combine(AppPaths.LogRoot, $"SwitchCfwWizard-2001-01-{i:00}.log"),
                "old");
        }

        var foreign = Path.Combine(AppPaths.LogRoot, "keep-me.txt");
        File.WriteAllText(foreign, "用户自己放进来的东西");

        LogFile.PruneOldFiles();

        var remaining = Directory.EnumerateFiles(AppPaths.LogRoot, "SwitchCfwWizard-*.log").ToList();
        Assert(remaining.Count <= 10,
            $"日志只该保留最近 10 份，实际 {remaining.Count} 份（不清理的话长期使用会把磁盘吃满）");
        Assert(File.Exists(LogFile.CurrentFilePath),
            "当天那份必须留下 —— 清理策略不能把「正在写的这份」也删掉");
        Assert(File.Exists(foreign),
            "清理只许动自己前缀的文件：用户手动放进 logs/ 的东西一律不许碰");

        // 收拾现场（用 File.Delete 而不是 shell 的 rm：后者在本机沙箱里对 *.log 会被拦下）
        foreach (var path in remaining)
        {
            if (!string.Equals(path, LogFile.CurrentFilePath, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(path);
            }
        }

        TryDeleteFile(foreign);
    }

    /// <summary>把消息泵推 <paramref name="duration"/> 那么久，让 DispatcherTimer 有机会 Tick。</summary>
    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(duration, DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);

        try
        {
            stop.Start();
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            stop.Stop();
        }
    }

    /// <summary>
    /// 取某个类成员的函数体行：从声明行起，到第一个「恰好 4 空格缩进的 <c>}</c>」为止。
    /// C# 里类成员一律缩进 4 空格、成员内部一律 ≥8 空格，所以那个收尾大括号是唯一的。
    /// </summary>
    private static List<string> BodyOf(IReadOnlyList<string> lines, string declaration)
    {
        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("    ", StringComparison.Ordinal)
                && lines[i].Trim() == declaration)
            {
                start = i;
                break;
            }
        }

        var body = new List<string>();
        if (start < 0)
        {
            return body;
        }

        for (var i = start; i < lines.Count; i++)
        {
            // 整行注释不算数：注释里提到某个字段名，不代表代码真的碰过它。
            // （行尾注释仍会被计入 —— 这是个已知的宽松处，宁可漏报也不误报。）
            if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                body.Add(lines[i]);
            }

            if (i > start && lines[i] == "    }")
            {
                break;
            }
        }

        return body;
    }

    /// <summary>扫出这些行里出现过的 <c>_settings.&lt;字段名&gt;</c>。</summary>
    private static HashSet<string> MentionedSettingsFields(IEnumerable<string> bodyLines, string receiver = "_settings")
    {
        // 接收者要能换：「界面 → 存档」的唯一搬运点在 MaterializeInto 里，那里的形参叫 target
        // （见那条方法的注释），同一份断言要能同时看这两处，否则换个形参名就等于放弃了检查。
        var pattern = Regex.Escape(receiver) + @"\.([A-Za-z_]\w*)";

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in bodyLines)
        {
            foreach (Match match in Regex.Matches(line, pattern))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return names;
    }

    // ── 组件配置项的持久化往返 ──────────────────────────────────
    //
    // SaveSettings 用 "组件名/选项Key" 作键写进 OptionValues，构造函数里的 ExtractSavedValues
    // 再用 "组件名/" 前缀读回来。这一对读写格式必须严格一致 —— 一旦分隔符或前缀写法不同，
    // **所有组件配置项都会静默丢失**：用户设好的呼出组合键、自定义内存档位、kip1 开关
    // 每次启动都回到默认值，而且不报任何错。
    //
    // 这里把**每个组件的每个选项**都设成可辨识的值再往返，而不是只挑几个已知 key ——
    // 后者只能覆盖到想得到的那几条路径，漏掉的恰恰是最容易出问题的。
    private static void CheckOptionValuesPersistence()
    {
        Section("组件配置项持久化：界面 → settings.json → 重新加载");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            var vm = new MainViewModel();

            // 组件勾选状态显式清零：这条用例测的是「选项值原样往返」，而**载入时会做归一化**的
            // 联动（default_lang 解析出非 en ⇒ 自动补勾「安装语言包」）只在组件勾着时才动作。
            // 不显式清掉的话，这条断言会取决于「磁盘上恰好有没有一份勾了 Ultrahand 的 settings.json」——
            // 那不是往返失败，是另一条功能；它有自己的用例（CheckLangPackCoupling）。
            foreach (var component in vm.Components)
            {
                component.IsSelected = false;
            }

            var before = SnapshotOptionValues(vm);
            Assert(before.Count > 0, $"前置条件：应至少有一个可配置选项，实际 {before.Count}");

            // 给每个组件的每个选项都设成可辨识的值
            foreach (var component in vm.Components)
            {
                foreach (var option in component.OptionGroups.SelectMany(g => g.Options))
                {
                    option.Value = RoundTripValue(component.Kind, option);
                }
            }

            // 记下 VM **实际接受**的值：下拉选项遇到非法值会自愈成第一个候选，
            // 那是 OptionViewModel 的设计行为，不是持久化的问题，所以比对要用接受后的值。
            var expected = SnapshotOptionValues(vm);
            Assert(expected.Any(p => before.TryGetValue(p.Key, out var b) && b != p.Value),
                "前置条件：至少要真的改动过一部分选项，否则这条测试是空的");

            TouchAndFlush(vm);

            var reloaded = new MainViewModel();
            var actual = SnapshotOptionValues(reloaded);

            // 反向护栏：动态候选项虽然不再被占位列表自愈，但**真正非法**的值仍必须被重建那一步纠正，
            // 否则「保留值」就变成了「什么垃圾都留着」。
            var badPath = SettingsStore.FilePath;
            var originalJson = File.ReadAllText(badPath);
            var badJson = originalJson.Replace(
                $"\"Hekate/autoboot\": \"{expected["Hekate/autoboot"]}\"",
                "\"Hekate/autoboot\": \"99\"");
            Assert(badJson != originalJson, "前置条件：应成功把 autoboot 改成越界值，否则这条护栏是空的");

            File.WriteAllText(badPath, badJson);
            var healed = new MainViewModel()
                .Components.First(c => c.Kind == ComponentKind.Hekate)
                .GetOptionValue("autoboot");
            Assert(healed == "0", $"越界的 autoboot 值应被纠正回 0，实际「{healed}」");

            Assert(actual.Count == expected.Count,
                $"选项数量应一致：保存前 {expected.Count} 个，重载后 {actual.Count} 个");

            var lost = expected
                .Where(pair => !actual.TryGetValue(pair.Key, out var value) || value != pair.Value)
                .Select(pair => $"{pair.Key}（存的是「{pair.Value}」，读回「{(actual.TryGetValue(pair.Key, out var v) ? v : "<缺失>")}」）")
                .ToList();

            Assert(lost.Count == 0,
                $"全部 {expected.Count} 个组件配置项都应原样往返；丢失或串值的：{string.Join("、", lost)}");
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    // ── 组件勾选状态的持久化往返 ────────────────────────────────
    //
    // 组件勾选和 OptionValues 存在同一个 settings.json 里，但走的是两条独立链路：
    //   保存：_settings.Components = Components.ToDictionary(c => c.Kind.ToString(), c => c.IsSelected)
    //   加载：vm.IsSelected = _settings.Components.TryGetValue(kind.ToString(), out var s) && s
    // 两边都用 ComponentKind 的**名字**当键。一旦写法不一致（一边 ToString()、一边用枚举数值，
    // 或改了枚举成员名），所有勾选会静默复位成「一个都没勾」——用户每次开程序都要重新勾一遍，
    // 而且不报任何错。
    //
    // 还有一个更容易漏的点：保存时必须把**未勾选的项也写进去**（显式写 false）。
    // 只写 true 的话，「用户主动取消全部勾选」和「从来没勾过」在文件里长得一模一样，
    // 而这两者的正确行为是**相反**的：前者要尊重（保持全不勾），后者要默认全选
    // （见 EnsureComponentsSelected）。混淆的后果是用户明确表达的「我什么都不要」被程序改回去。
    private static void CheckComponentSelectionPersistence()
    {
        Section("组件勾选状态：界面 → settings.json → 重新加载");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            var total = ComponentCatalog.All.Count;
            Assert(total > 1, $"前置条件：组件数应大于 1，否则「只勾一部分」测不出来，实际 {total}");

            // ── 不对称勾选：只勾两个，其余显式取消 ──
            var keep = new[] { ComponentKind.Atmosphere, ComponentKind.Hekate };
            var vm = new MainViewModel();
            foreach (var component in vm.Components)
            {
                component.IsSelected = keep.Contains(component.Kind);
            }

            TouchAndFlush(vm);
            Assert(File.Exists(path), "选好组件之后应真的落盘");

            var raw = File.ReadAllText(path);
            Assert(raw.Contains("\"Components\""), "settings.json 里应有 Components 段");

            // 未勾选的项必须以 false 显式落盘，不能省略
            Assert(raw.Contains("\"Ultrahand\": false"),
                "未勾选的组件应以 false 显式落盘；省略的话「主动取消全部」与「从没勾过」无法区分");

            var reloaded = new MainViewModel();
            Assert(reloaded.Components.Count == total,
                $"重载后组件数应仍为 {total}，实际 {reloaded.Components.Count}");

            var mismatched = reloaded.Components
                .Where(c => c.IsSelected != keep.Contains(c.Kind))
                .Select(c => $"{c.Kind} 应为 {keep.Contains(c.Kind)} 实际 {c.IsSelected}")
                .ToList();
            Assert(mismatched.Count == 0,
                $"重载后的勾选状态应与保存前逐个一致；不一致的：{string.Join("、", mismatched)}");

            var unexpectedlySelected = reloaded.Components
                .Where(c => !keep.Contains(c.Kind) && c.IsSelected)
                .Select(c => c.Kind.ToString())
                .ToList();
            Assert(unexpectedlySelected.Count == 0,
                $"未勾选的组件重载后不应变成已勾选；出问题的：{string.Join("、", unexpectedlySelected)}");

            // ── 反向：只勾一个，确认上面的断言不是恒真 ──
            foreach (var component in vm.Components)
            {
                component.IsSelected = component.Kind == ComponentKind.SysPatch;
            }

            TouchAndFlush(vm);

            var reversedSelected = new MainViewModel().Components
                .Where(c => c.IsSelected)
                .Select(c => c.Kind)
                .ToList();
            Assert(reversedSelected.Count == 1 && reversedSelected[0] == ComponentKind.SysPatch,
                $"反向保存后应只剩 SysPatch 勾选，实际：{string.Join("、", reversedSelected)}");

            // ── CanStart（「开始」按钮的可用性）必须跟着勾选状态立刻变 ──
            foreach (var component in vm.Components)
            {
                component.IsSelected = false;
            }

            Assert(!vm.CanStart, "一个组件都没勾时「开始」应不可用");

            vm.Components.First(c => c.Kind == ComponentKind.Hekate).IsSelected = true;
            Assert(vm.CanStart, "勾上任意一个组件后「开始」应立即可用（不能等到重建视图模型才生效）");

            vm.Components.First(c => c.Kind == ComponentKind.Hekate).IsSelected = false;
            Assert(!vm.CanStart, "取消最后一个勾选后「开始」应立刻变回不可用");

            // ── EnsureComponentsSelected 必须尊重用户的显式选择 ──
            //
            // 注意构造函数末尾会调 UpdateForcedSelections()，所以「上次开着屏蔽序列号」的用户
            // 重启后 Sys-patch 会被**强制勾上**（需求②在重启后同样生效）。这会影响
            // 「重载后到底有几个组件是勾着的」，所以下面把两种状态分开断言。
            //
            // ⚠️ 先把两个前提**自己摆好**，不沿用磁盘上那份 settings.json 的取值。
            //    下面 B 段的判据是 `SysPatchForced = (屏蔽sysmmc && BootSysNand) || (屏蔽emuMMC && BootEmuNand)`
            //    —— 它含**引导项**。沿用环境状态的话，「红还是绿」就取决于上一个用例
            //    （或者上一次运行）留下的值：实测同一份代码连跑三次，红、绿、红，
            //    而报错信息只说「实际：Ultrahand」，完全看不出是引导项在作祟。
            //    判据：**用例要能自己建立前提**，否则它就不是断言，是一枚硬币。
            vm.BootStock = true;
            vm.BootSysNand = true;
            vm.BootEmuNand = true;

            // A. 屏蔽序列号全关 → 勾选状态应原样保持
            vm.BlankSerialSysmmc = false;
            vm.BlankSerialEmummc = false;
            foreach (var component in vm.Components)
            {
                component.IsSelected = false;
            }

            vm.Components.First(c => c.Kind == ComponentKind.Ultrahand).IsSelected = true;
            TouchAndFlush(vm);

            var respected = new MainViewModel();
            Assert(!respected.EnsureComponentsSelected(),
                "已经显式勾过组件时，EnsureComponentsSelected 不该做默认全选");

            var respectedSelected = respected.Components
                .Where(c => c.IsSelected)
                .Select(c => c.Kind)
                .ToList();
            Assert(respectedSelected.Count == 1 && respectedSelected[0] == ComponentKind.Ultrahand,
                $"屏蔽序列号全关时，重载后应只勾着 Ultrahand；实际：{string.Join("、", respectedSelected)}");

            // B. 屏蔽序列号开着 → 重启后 Sys-patch 应被需求②强制勾上，且 EnsureComponentsSelected 仍不改动
            vm.BlankSerialSysmmc = true;
            TouchAndFlush(vm);

            var forced = new MainViewModel();
            var forcedSelected = forced.Components
                .Where(c => c.IsSelected)
                .Select(c => c.Kind)
                .ToList();
            Assert(forcedSelected.Contains(ComponentKind.SysPatch),
                $"上次开着屏蔽序列号时，重启后 Sys-patch 应被强制勾上；实际：{string.Join("、", forcedSelected)}");

            Assert(!forced.EnsureComponentsSelected(),
                "屏蔽项触发强制勾选时，EnsureComponentsSelected 同样不该做默认全选");

            var forcedAfter = forced.Components
                .Where(c => c.IsSelected)
                .Select(c => c.Kind)
                .ToList();
            Assert(forcedAfter.Count == forcedSelected.Count && !forcedSelected.Except(forcedAfter).Any(),
                $"EnsureComponentsSelected 不应改动任何勾选；调用前 [{string.Join("、", forcedSelected)}] 调用后 [{string.Join("、", forcedAfter)}]");

            // C. 判据是「文件里有没有 Components 记录」，而不是「有没有哪个是 true」，
            //    更不是「实时状态」。这里刻意造一个「没有 Components 记录、但屏蔽序列号开着」的
            //    settings.json：此时实时状态里 Sys-patch 已被构造函数强制勾上，
            //    若实现改读实时状态（或改看「有没有哪个是 true」之外的东西），
            //    就会认为「用户已经选过了」而只留下 Sys-patch 一个勾。
            //    正确行为是全部组件都勾上。
            //
            //    注意：默认全选**只走命令行 --run**，界面路径（构造函数）不做这件事。
            //    所以这里必须先断言「界面路径确实没勾」，再显式调 EnsureComponentsSelected()
            //    验证命令行路径 —— 两件事都要钉住，缺一个就会漏掉一半的回归面。
            SettingsStore.Save(new AppSettings { BlankSerialSysmmc = true, BlankSerialEmummc = false });

            var neverChosen = new MainViewModel();
            var unexpected = neverChosen.Components
                .Where(c => c.IsSelected && c.Kind != ComponentKind.SysPatch)
                .Select(c => c.Kind)
                .ToList();
            Assert(unexpected.Count == 0,
                $"界面路径不该默认勾选任何组件（Sys-patch 是被屏蔽序列号强制勾上的，不算）；实际多勾了：{string.Join("、", unexpected)}");

            Assert(neverChosen.EnsureComponentsSelected(),
                "文件里没有组件勾选记录时，EnsureComponentsSelected（命令行 --run 用）应做默认全选");

            var neverChosenSelected = neverChosen.Components
                .Where(c => c.IsSelected)
                .Select(c => c.Kind)
                .ToList();
            Assert(neverChosenSelected.Count == total,
                $"默认全选后应勾上全部 {total} 个组件；实际只勾了：{string.Join("、", neverChosenSelected)}");
            Assert(neverChosen.CanStart, "默认全选后「开始」应可用");

            //    顺带钉住重入保护：默认全选是**程序**行为，不能触发「用户手动勾 Sys-patch →
            //    默认勾上屏蔽序列号」的软联动。这里 emuMMC 屏蔽原本是关的，跑完必须还是关的 ——
            //    否则用户从没勾过的屏蔽项被程序悄悄打开，exosphere.ini 里会多出
            //    blank_prodinfo_emummc=1，而且还会把 Sys-patch 一起锁死。
            Assert(!neverChosen.BlankSerialEmummc,
                "默认全选不应顺手把没勾过的屏蔽序列号勾上（那是用户手动勾 Sys-patch 才该触发的软默认）");

            // D. 「用户主动取消全部勾选」重启后必须**保持全不勾**。
            //    界面默认已经改成全不勾了，这条断言仍然要留着 —— 它防的是「默认全选」被重新引回
            //    界面路径（改回去的话，用户明确表达的「我什么都不要」会在下次启动时被悄悄改回全选）。
            //
            //    两种「全不勾」在**文件**里形态不同，别混：主动取消会留下显式 false 记录，
            //    从没勾过是空记录。差别只在命令行 --run —— 后者会替他勾满（C 段），前者不会。
            var allOff = new MainViewModel();
            allOff.BlankSerialSysmmc = false;
            allOff.BlankSerialEmummc = false;
            foreach (var component in allOff.Components)
            {
                component.IsSelected = false;
            }

            TouchAndFlush(allOff);

            var reloadedAllOff = new MainViewModel();
            var stillOff = reloadedAllOff.Components
                .Where(c => c.IsSelected)
                .Select(c => c.Kind.ToString())
                .ToList();
            Assert(stillOff.Count == 0,
                $"用户主动取消全部勾选后重启不应被重新勾上；实际勾上了：{string.Join("、", stillOff)}");
            Assert(!reloadedAllOff.CanStart, "全不勾时「开始」应不可用");
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    /// <summary>把界面上所有组件选项的当前值抓成 "组件名/选项Key" → 值。</summary>
    private static Dictionary<string, string> SnapshotOptionValues(MainViewModel vm)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in vm.Components)
        {
            foreach (var option in component.OptionGroups.SelectMany(g => g.Options))
            {
                result[$"{component.Kind}/{option.Key}"] = option.Value;
            }
        }

        return result;
    }

    /// <summary>
    /// 挑一个用于往返测试的值。有候选值的（下拉）必须用**合法**候选，
    /// 否则会被自愈逻辑覆盖掉；特意取**最后一个**候选而不是第一个 ——
    /// 第一个往往就是默认值，拿默认值往返，即使持久化完全失效也看不出差别。
    /// </summary>
    private static string RoundTripValue(ComponentKind kind, OptionViewModel option)
        => option.Choices.Count > 0
            ? option.Choices[^1].Value
            : $"rt-{kind}-{option.Key}";

    // ── 改动引导项后 autoboot 指向哪个系统 ──────────────────────
    //
    // autoboot 的候选项编号是**位置相关**的：0=关，之后按「正版 / 真实破解 / 虚拟破解」
    // 里**当前勾着的**顺序依次编号。所以取消勾选靠前的一项，后面所有项的编号都会整体前移。
    //
    // 若重建候选项时只认数字、不认「指向哪个系统」，就会出两种错：
    //   ① 旧编号越界 → 被重置成「关闭」，用户设的自动引导项凭空消失；
    //   ② 旧编号仍合法但含义变了 → 用户要「真实破解」，实际自动进入「虚拟破解」，**静默换系统**。
    private static void CheckAutobootRemapOnBootEntryChange()
    {
        Section("改动引导项后 autoboot 应仍指向同一个系统，而不是同一个数字");

        var vm = new MainViewModel();
        vm.BootStock = true;
        vm.BootSysNand = true;
        vm.BootEmuNand = true;

        var autoboot = vm.Components.First(c => c.Kind == ComponentKind.Hekate).FindOption("autoboot");
        Assert(autoboot is not null, "前置条件：Hekate 应有 autoboot 选项");
        Assert(vm.BootStock && vm.BootSysNand && vm.BootEmuNand, "前置条件：三个引导项都勾着");

        // 全勾时：0=关、1=正版、2=真实破解、3=虚拟破解
        autoboot!.Value = "2";

        // 取消「正版系统」→ 编号整体前移：0=关、1=真实破解、2=虚拟破解
        vm.BootStock = false;

        Assert(autoboot.Value != "0",
            "取消正版系统不应把 autoboot 重置成「关闭」，用户设的自动引导项不该凭空消失");
        Assert(autoboot.Value == "1",
            $"取消正版系统后应仍指向「真实破解系统」（编号前移到 1），实际「{autoboot.Value}」");

        // 目标系统**本身**被取消勾选 → 这次只能回落到「关闭」（没有可指的系统了）
        vm.BootSysNand = false;
        Assert(autoboot.Value == "0",
            "被指向的系统自己被取消勾选时，autoboot 才应回落到「关闭」");

        // 再勾回来、重选、再加回正版：多次切换后编号仍要准确
        vm.BootSysNand = true;
        autoboot.Value = "1";          // 0=关、1=真实破解（正版还没勾）
        vm.BootStock = true;           // 正版回来 → 真实破解前移到 2
        Assert(autoboot.Value == "2",
            $"反复勾选后应仍准确指向真实破解（编号为 2），实际「{autoboot.Value}」");

        // 全部取消 → 只剩「关闭」一项，值必须收敛到 0，不能留一个越界编号
        vm.BootStock = false;
        vm.BootSysNand = false;
        vm.BootEmuNand = false;
        Assert(autoboot.Value == "0", "三个引导项全不勾时 autoboot 只能是「关闭」");
    }

    // ── autoboot 的编号在界面与 ini 里是否指向同一个系统 ─────────
    //
    // 上面两条测的都是「界面内部」的一致性。但用户最终拿去用的是 hekate_ipl.ini ——
    // autoboot 的数字是 hekate 按**文件里引导项的顺序**解释的，而界面候选项的顺序是
    // MainViewModel 自己排的（正版 → 真实破解 → 虚拟破解），ConfigGenerator 又是另一处
    // 独立排的。三处只要有一处顺序不一致，界面显示「虚拟破解」而机器实际进「正版」，
    // 且两边各自看都没问题 —— 这是最典型的跨层错位，只能拿**最终产物**来对。
    private static void CheckAutobootIndexMatchesGeneratedEntries()
    {
        Section("autoboot 编号：界面候选项 vs 生成的 hekate_ipl.ini 应指向同一个系统");

        var vm = new MainViewModel();
        vm.BootStock = true;
        vm.BootSysNand = true;
        vm.BootEmuNand = true;
        vm.Components.First(c => c.Kind == ComponentKind.Atmosphere).IsSelected = true;
        vm.Components.First(c => c.Kind == ComponentKind.Hekate).IsSelected = true;
        vm.Components.First(c => c.Kind == ComponentKind.Ultrahand).IsSelected = false;
        vm.Components.First(c => c.Kind == ComponentKind.SysPatch).IsSelected = false;
        vm.IncludeComponentFilesInOutput = false;

        var autobootOption = vm.Components.First(c => c.Kind == ComponentKind.Hekate).FindOption("autoboot");
        Assert(autobootOption is not null, "前置条件：Hekate 应有 autoboot 选项");
        var autoboot = autobootOption!;

        // 逐个编号核对：界面里该编号的候选项名字，必须等于 ini 里第 N 个引导项的名字
        foreach (var value in new[] { "1", "2", "3" })
        {
            autoboot.Value = value;
            Generate(vm.BuildWizardOptions());

            var titles = BootEntryTitles(Read("bootloader/hekate_ipl.ini"));
            var index = int.Parse(value, CultureInfo.InvariantCulture);
            Assert(index <= titles.Count,
                $"前置条件：编号 {value} 应有对应引导项，实际文件里只有 {titles.Count} 个");

            var choiceLabel = autoboot.Choices.First(c => c.Value == value).Label;
            Assert(titles[index - 1] == choiceLabel,
                $"编号 {value}：界面「{choiceLabel}」应等于 ini 第 {index} 项「{titles[index - 1]}」");
        }

        // 取消「正版系统」后顺序整体前移，两边的对应关系必须**一起**前移
        vm.BootStock = false;
        foreach (var value in new[] { "1", "2" })
        {
            autoboot.Value = value;
            Generate(vm.BuildWizardOptions());

            var titles = BootEntryTitles(Read("bootloader/hekate_ipl.ini"));
            var index = int.Parse(value, CultureInfo.InvariantCulture);
            Assert(index <= titles.Count, $"前置条件：取消正版后编号 {value} 仍应有对应引导项");

            var choiceLabel = autoboot.Choices.First(c => c.Value == value).Label;
            Assert(titles[index - 1] == choiceLabel,
                $"取消正版后编号 {value}：界面「{choiceLabel}」应等于 ini 第 {index} 项「{titles[index - 1]}」");
        }
    }

    /// <summary>
    /// 按顺序取出 hekate_ipl.ini 里的引导项标题。
    /// 排除 <c>[config]</c>（hekate 的设置段，不是引导项）与 <c>{------ xxx ------}</c>（caption，只是分隔标题）。
    /// </summary>
    private static List<string> BootEntryTitles(string ini)
    {
        var titles = new List<string>();
        foreach (var raw in ini.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('{'))
            {
                continue;
            }

            if (line.Length > 2 && line.StartsWith('[') && line.EndsWith(']'))
            {
                var title = line[1..^1];
                if (!string.Equals(title, "config", StringComparison.OrdinalIgnoreCase))
                {
                    titles.Add(title);
                }
            }
        }

        return titles;
    }

    // ── 选项定义自身的内部一致性 ────────────────────────────────
    //
    // 这一条不测行为，只测**目录数据的自洽**。之所以值得单独测，是因为
    // OptionViewModel 构造函数会对「值不在候选项里」做自愈（回退到第一项），
    // 于是定义里写错的两处会互相掩护：默认值不在候选项里 → 界面显示的是第一项，
    // 而不是定义里声明的默认值，**两边看都没报错**。
    // autoboot 那类 bug 就是从这附近长出来的，所以把这条不变量钉死。
    private static void CheckOptionDefinitionInvariants()
    {
        Section("选项定义自洽性（默认值必须在候选项里、下拉框必须有候选项）");

        var violations = new List<string>();
        var checkedCount = 0;

        foreach (var definition in ComponentCatalog.All)
        {
            foreach (var option in definition.Options)
            {
                checkedCount++;
                var choices = option.Choices ?? [];
                var label = $"{definition.Kind}/{option.Key}";

                // 默认值必须是候选项之一，否则构造时会被自愈成第一项，
                // 界面显示的「默认值」和定义里写的对不上。
                if (choices.Count > 0
                    && !string.IsNullOrEmpty(option.DefaultValue)
                    && !choices.Any(c => string.Equals(c.Value, option.DefaultValue, StringComparison.Ordinal)))
                {
                    violations.Add($"{label} 的默认值「{option.DefaultValue}」不在候选项里");
                }

                // 下拉框必须有候选项；动态生成的除外，它们的候选项由 MainViewModel 在运行期填。
                if (option.Editor == OptionEditor.ComboBox && choices.Count == 0 && !option.DynamicChoices)
                {
                    violations.Add($"{label} 是下拉框却没有任何候选项");
                }
            }
        }

        Assert(checkedCount > 0, $"前置条件：应至少检查到一个选项，实际 {checkedCount}");
        Assert(violations.Count == 0,
            $"全部 {checkedCount} 个选项的定义都应自洽；有问题的：{string.Join("；", violations)}");
    }
    // 「值格式前缀」这一侧的护栏。IniValuePrefix 是 string?，与 TargetFile / Section 同族，但它有**两种**漏法，
    // 而且两种都会以「裸值」告终 —— 裸值正是另外 48 个选项的**正常**产物，所以产物看着完全合理：
    //   · 漏写 → `upload_enabled=1`（而不是 `upload_enabled=u8!0x1`），固件按类型解析时读不出来；
    //   · 拼错（"u18" / "u8 " / "i32"）→ 落进 FormatTypedValue 的 `_ => value` 分支，
    //     产物与「本来就不需要前缀」**逐字节相同** —— 与 nogc 那条同族：错误被合法分支吸收。
    // 现有 22 条 `Contains("键=u8!…")` 断言只点名了**当前的** 22 个选项；第 23 个漏写前缀时，没有任何断言会红
    // （「字面量断言的覆盖只等于它点名的键」）。下面两条不变量都是**从声明集合推出来的**，不手抄清单。
    private static void CheckIniValuePrefixesAreRecognized()
    {
        Section("配置值前缀：声明的前缀必须是 FormatTypedValue 认得的，且同一文件内统一");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到源码目录，无法核对配置值前缀");
            return;
        }

        var generatorPath = Path.Combine(sourceRoot, "Services", "ConfigGenerator.cs");
        var recognized = FindRecognizedValuePrefixes(File.ReadAllText(generatorPath));

        // 前置条件：源码扫描本身要能工作。扫不到 = 下面「全部认得」恒真。
        Assert(recognized.Count >= 5,
            $"前置条件：应从 FormatTypedValue 的 switch 分支里扫出至少 5 个已知前缀（当前 {recognized.Count} 个），否则下面的比对恒真");

        var all = ComponentCatalog.All.SelectMany(d => d.Options).ToList();
        var recognizedList = string.Join("/", recognized.OrderBy(x => x, StringComparer.Ordinal));

        // 不变量 1：拼错的前缀必须报错。它会被 `_ => value` 静默吃掉。
        var unknown = all
            .Where(o => !string.IsNullOrEmpty(o.IniValuePrefix))
            .Where(o => !recognized.Contains(o.IniValuePrefix!))
            .Select(o => $"{o.Key} 声明的前缀「{o.IniValuePrefix}」不在 {recognizedList} 里")
            .ToList();

        Assert(unknown.Count == 0,
            "声明的前缀必须是 FormatTypedValue 认得的 —— 拼错的前缀会落进 `_ => value`，值以裸值写出，"
            + "与「本来就不需要前缀」逐字节相同，任何行为断言都看不见：" + string.Join("；", unknown));

        // 不变量 2：同一目标文件内，前缀声明必须**统一**。
        // 一个文件的值语法是**文件的属性**，不是单个选项的：要么全带类型前缀（system_settings.ini 的 u8!/u64!/str!），
        // 要么全是裸值（hekate_ipl.ini / nyx.ini / exosphere.ini …）。
        // 混着就说明「新加的那个选项漏写了前缀」—— 而 48/70 个选项本来就该是裸值，所以漏写看起来毫无异常。
        var mixed = new List<string>();
        var filesWithPrefix = 0;
        var declaredTotal = 0;

        foreach (var group in all
                     .Where(o => !string.IsNullOrEmpty(o.TargetFile))
                     .GroupBy(o => o.TargetFile!, StringComparer.OrdinalIgnoreCase))
        {
            var total = group.Count();
            var withPrefix = group.Count(o => !string.IsNullOrEmpty(o.IniValuePrefix));
            declaredTotal += total;
            if (withPrefix > 0)
            {
                filesWithPrefix++;
            }

            if (withPrefix != 0 && withPrefix != total)
            {
                var offenders = group
                    .Where(o => string.IsNullOrEmpty(o.IniValuePrefix))
                    .Select(o => o.Key);
                mixed.Add($"{group.Key} 里 {withPrefix}/{total} 个声明了前缀，缺前缀的：{string.Join("、", offenders)}");
            }
        }

        Assert(declaredTotal > 0, "前置条件：应有选项声明写进某个配置文件，否则下面的比对恒真");
        Assert(filesWithPrefix > 0,
            "前置条件：应至少有一个文件用类型前缀，否则「同一文件内统一」这条恒真（当前一个都没有）");
        Assert(mixed.Count == 0,
            "同一配置文件里的选项应统一带 / 不带值前缀（一个文件的值语法是文件的属性，不是单个选项的）—— "
            + "缺前缀的选项会以裸值写出，固件按类型解析时读不出来：" + string.Join("；", mixed));
    }

    /// <summary>
    /// 从 <c>ConfigGenerator.FormatTypedValue</c> 的 switch 分支里扫出「认得的」值前缀集合。
    /// 只取 <c>=&gt;</c> **左边**的字面量 —— 右边的 "u8!0x1" / "true" 之类是产物，不是前缀。
    /// **刻意不手抄清单** —— 新增一个分支会自动被带上；扫不到会在上面那条前置条件上炸，不会静默漏过。
    /// </summary>
    private static HashSet<string> FindRecognizedValuePrefixes(string generatorSource)
    {
        var lines = generatorSource.Replace("\r\n", "\n").Split('\n');
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        var inSwitch = false;

        foreach (var line in lines)
        {
            if (!inSwitch)
            {
                if (line.Contains("prefix switch", StringComparison.Ordinal))
                {
                    inSwitch = true;
                }

                continue;
            }

            if (line.TrimStart().StartsWith("};", StringComparison.Ordinal))
            {
                break;
            }

            var arrow = line.IndexOf("=>", StringComparison.Ordinal);
            if (arrow < 0)
            {
                continue;
            }

            foreach (Match match in Regex.Matches(line[..arrow], "\"([^\"]*)\""))
            {
                if (match.Groups[1].Value.Length > 0)
                {
                    prefixes.Add(match.Groups[1].Value);
                }
            }
        }

        return prefixes;
    }


    // ── 网络错误翻译 ────────────────────────────────────────────
    private static void CheckNetworkErrors()
    {
        Section("网络错误翻译");

        // 代理隧道失败：HttpRequestException.StatusCode 为 null，只能从消息里取状态码
        var proxyFailure = new HttpRequestException(
            "The proxy tunnel request to proxy 'http://127.0.0.1:53271/' failed with status code '502'.");
        var described = NetworkErrors.Describe(proxyFailure);

        Assert(described.Contains("502"), "应能从异常文本里提取出 502");
        Assert(described.Contains("代理"), "502 的提示应与代理有关");
        Assert(!described.Contains("proxy tunnel request"), "不应把原始英文异常直接丢给用户");
        Assert(NetworkErrors.IsTransient(proxyFailure, CancellationToken.None), "502 应判定为可重试");

        var notFound = new HttpRequestException("Not Found", null, System.Net.HttpStatusCode.NotFound);
        Assert(NetworkErrors.Describe(notFound).Contains("404"), "404 应给出对应提示");
        Assert(!NetworkErrors.IsTransient(notFound, CancellationToken.None), "404 不应重试");

        var limited = new HttpRequestException("rate limited", null, System.Net.HttpStatusCode.TooManyRequests);
        Assert(NetworkErrors.Describe(limited).Contains("Token"), "429 应提示去填 GitHub Token");
        Assert(!NetworkErrors.IsTransient(limited, CancellationToken.None), "429 不应重试");

        Assert(NetworkErrors.Describe(new TimeoutException()).Contains("超时"), "TimeoutException 应提示超时");
        Assert(NetworkErrors.IsTransient(new TimeoutException(), CancellationToken.None), "超时应可重试");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert(!NetworkErrors.IsTransient(new OperationCanceledException(), cancelled.Token),
            "用户主动取消不应重试");
    }

    // ── GitHub 镜像站（用户 2026-09-19：高级设置里可配，未填时按原 GitHub 地址）──
    //
    // 这条链有个别的功能都没有的性质：**跑回归的人几乎一定没填镜像站**。
    // 它的「有效路径」在测试环境里天然盖不到 —— 只能靠三层：
    // ① 纯函数穷举（规范化 + 三种改写形态 + 幂等）；② 桩 handler 看**实际请求的 URI**；
    // ③ 查设置的真源（源码契约：两处业务出口都得过同一个门，而诊断出口刻意不过）。
    // 少了任何一条，「填了镜像站却没走镜像」都能一路绿到用户那里。
    private static void CheckGitHubMirror()
    {
        Section("GitHub 镜像站：三种地址改写 + 服务真的改走镜像");

        const string mirror = "https://gh.example.com";
        const string releaseUrl = "https://github.com/CTCaer/hekate/releases/download/v6.5.3/hekate.zip";
        const string rawUrl = "https://raw.githubusercontent.com/CTCaer/hekate/HEAD/res/f.bin";
        const string apiUrl = "https://api.github.com/repos/CTCaer/hekate/releases?per_page=10";
        const string codeloadUrl = "https://codeload.github.com/CTCaer/hekate/tar.gz/HEAD";
        var assetUrl = ReleaseAsset.BuildApiAssetUrl(new RepoSpec("CTCaer", "hekate"), 987654321);

        // ── ① 规范化：什么样的取值算「填了镜像站」 ──────────────────
        Assert(GitHubMirror.Normalize(null) is null
               && GitHubMirror.Normalize(string.Empty) is null
               && GitHubMirror.Normalize("   ") is null,
            "留空 = 不启用镜像（这是默认值，老存档升上来没有这个字段也走这条）");

        Assert(GitHubMirror.Normalize("gh.example.com") == "https://gh.example.com",
            "不写协议时应补成 https —— 这是最常见的写法，为它报「格式不对」只会让人莫名其妙");

        Assert(GitHubMirror.Normalize("https://gh.example.com/") == "https://gh.example.com",
            "结尾的斜杠要吃掉（不然拼出来会变成「//」，有些反向代理不认这种路径）");

        Assert(GitHubMirror.Normalize("http://gh.example.com:8080") == "http://gh.example.com:8080",
            "带端口 / 非 https 的镜像站要能认（自建镜像常常不是 443）");

        Assert(GitHubMirror.Normalize("https://") is null
               && GitHubMirror.Normalize("not a host") is null
               && GitHubMirror.Normalize("ftp://gh.example.com") is null,
            "没有主机名 / 带空格 / 非 http(s) 的一律当没填，不许拼出一条垃圾地址去打");

        Assert(GitHubMirror.Normalize("https://gh.example.com/CTCaer/hekate") is null,
            "把「owner/repo」整段粘进镜像站框的必须拦下来 —— 它结构上完全合法，"
            + "不拦的话每条地址都会被再拼一次仓库路径而 404，而用户手里那条地址看着就是对的");

        // ── ② 改写：留空走原路，填了按三种主机各自改写 ──────────────
        Assert(GitHubMirror.Apply(releaseUrl, null) == releaseUrl
               && GitHubMirror.Apply(apiUrl, string.Empty) == apiUrl
               && GitHubMirror.Apply(rawUrl, "   ") == rawUrl,
            "没填 / 填空白时每条地址都必须原样返回 —— 「留空 = 一切照旧」是这个功能的地基");

        Assert(GitHubMirror.Apply(releaseUrl, mirror)
               == "https://gh.example.com/CTCaer/hekate/releases/download/v6.5.3/hekate.zip",
            "github.com：主域名直接换掉，路径原样");

        Assert(GitHubMirror.Apply(rawUrl, mirror) == "https://gh.example.com/raw/CTCaer/hekate/HEAD/res/f.bin",
            "raw.githubusercontent.com：要加 raw/ 前缀（与 github.com 不是同一条规则，混用会 404）");

        Assert(GitHubMirror.Apply(apiUrl, mirror)
               == "https://gh.example.com/proxy/api.github.com/repos/CTCaer/hekate/releases?per_page=10",
            "api.github.com：走 /proxy/<主机>/ 前缀，且查询串（?per_page=）一个字节都不能丢。"
            + "⚠️ 曾经按「主域名换掉」那样拼成 <镜像>/api.github.com/… —— 实测那是 404，"
            + "而这类错误在日志里只表现为「一堆 404」");

        Assert(GitHubMirror.Apply(codeloadUrl, mirror)
               == "https://gh.example.com/proxy/codeload.github.com/CTCaer/hekate/tar.gz/HEAD",
            "codeload.github.com（目录槽的整仓快路径）同样在镜像范围内");

        Assert(GitHubMirror.Apply("https://example.com/a/b", mirror) == "https://example.com/a/b",
            "不是 GitHub 的地址一律不碰（不许把无关域名也塞进第三方镜像）");

        Assert(GitHubMirror.Apply(releaseUrl.Replace("https://github.com", mirror, StringComparison.Ordinal), mirror)
               == releaseUrl.Replace("https://github.com", mirror, StringComparison.Ordinal),
            "已经在镜像站上的地址不许再改一次（再改一次会得到 <镜像>/proxy/<镜像>/…，一条必定 404 的地址）");

        Assert(GitHubMirror.Apply("not-a-url", mirror) == "not-a-url"
               && GitHubMirror.Apply("ftp://github.com/x", mirror) == "ftp://github.com/x",
            "不成形的地址原样返回，不许在这里抛异常把整条流程带走");

        // ── ③ 下载通道：请求真的打到镜像地址上 ──────────────────────
        var stamp = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), "scw-mirror-" + stamp + ".bin");
        var stub = new StubHandler(directStatus: 200, fallbackStatus: 200, fallbackBody: "FALLBACK-BYTES");

        try
        {
            using var http = new HttpClient(stub) { Timeout = Timeout.InfiniteTimeSpan };

            // ③a 不填镜像：请求打到的就是原地址（「留空 = 一切照旧」的行为版证据）
            var plain = new DownloadService(http) { MaxAttempts = 1, Backoff = _ => TimeSpan.Zero };
            plain.DownloadAsync(releaseUrl, path + ".plain", null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(stub.LastDirectUrl == releaseUrl,
                $"没填镜像站时请求应打到原地址，实际「{stub.LastDirectUrl ?? "(空)"}」");

            // ③b 填了镜像：直链那一次请求必须打到镜像地址
            stub.Reset(directStatus: 200, fallbackStatus: 200, fallbackBody: "FALLBACK-BYTES");
            var mirrored = new DownloadService(http, mirror) { MaxAttempts = 1, Backoff = _ => TimeSpan.Zero };
            mirrored.DownloadAsync(releaseUrl, path + ".mirrored", null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(stub.LastDirectUrl
                   == "https://gh.example.com/CTCaer/hekate/releases/download/v6.5.3/hekate.zip",
                $"填了镜像站后请求必须打到镜像地址，实际「{stub.LastDirectUrl ?? "(空)"}」");

            // ③c 备用通道也要改走镜像。少了这一条会出「直链走镜像、备用通道偷偷回官方」的半吊子状态，
            //     而官方地址恰恰是「直连通不了才要镜像」的那一个。
            stub.Reset(directStatus: 502, fallbackStatus: 200, fallbackBody: "FALLBACK-BYTES");
            var withFallback = new DownloadService(http, mirror) { MaxAttempts = 1, Backoff = _ => TimeSpan.Zero };
            withFallback.DownloadAsync(
                releaseUrl, path + ".fallback", null, CancellationToken.None, null,
                new DownloadFallback(assetUrl, "application/octet-stream")).GetAwaiter().GetResult();
            Assert(stub.LastFallbackUrl
                   == "https://gh.example.com/proxy/api.github.com/repos/CTCaer/hekate/releases/assets/987654321",
                $"备用通道（API asset 端点）也必须走镜像，实际「{stub.LastFallbackUrl ?? "(空)"}」");
            Assert(File.ReadAllText(path + ".fallback") == "FALLBACK-BYTES",
                "改写通道不该影响落盘内容");

            // ③d 镜像站填错时不许「半启用」：整条流程退回官方地址
            stub.Reset(directStatus: 200, fallbackStatus: 200, fallbackBody: "FALLBACK-BYTES");
            var invalid = new DownloadService(http, "https://gh.example.com/CTCaer/hekate")
            {
                MaxAttempts = 1,
                Backoff = _ => TimeSpan.Zero,
            };
            invalid.DownloadAsync(releaseUrl, path + ".invalid", null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(stub.LastDirectUrl == releaseUrl,
                $"镜像站填错时应整条退回官方地址，实际「{stub.LastDirectUrl ?? "(空)"}」");
        }
        finally
        {
            foreach (var leftover in new[]
                     {
                         path + ".plain", path + ".mirrored", path + ".fallback", path + ".invalid",
                         path + ".plain.part", path + ".mirrored.part",
                         path + ".fallback.part", path + ".invalid.part",
                     })
            {
                if (File.Exists(leftover))
                {
                    File.Delete(leftover);
                }
            }
        }

        // ── ④ 查询通道：官方先试，连不上才改走镜像 ──────────────────
        //
        // ⚠️ 这一条是**实测踩出来的**（2026-09-19）。原先查询也按「填了就换掉」处理，
        //    结果第一次联网 e2e 就撞上镜像站的共用出口被 GitHub 限流：
        //    `403 API rate limit exceeded for 104.23.168.80` —— 也就是「填了镜像站反而一个组件都查不到」。
        //    未登录的 60 次/小时是**按 IP** 算的，直连官方时那是用户自己的额度；镜像站那份是所有人共用的。
        //    所以查询必须「官方优先、镜像兜底」，而**下载**仍然是「填了就直接走镜像」（那是用户填它的目的）。
        var apiStub = new ApiChannelStubHandler();
        using (var apiHttp = new HttpClient(apiStub) { Timeout = Timeout.InfiniteTimeSpan })
        {
            var repo = new RepoSpec("CTCaer", "hekate");

            var releases = new GitHubReleaseService(apiHttp, new SilentSink(), null, mirror)
                .GetReleasesAsync(repo, 10, CancellationToken.None).GetAwaiter().GetResult();

            Assert(releases.Count == 1 && releases[0].Tag == "v9.9.9",
                "官方连不上时应能从镜像站查到版本（这是镜像站对查询的全部价值）");
            Assert(apiStub.Requests.Count > 0
                   && apiStub.Requests[0].StartsWith("https://api.github.com/", StringComparison.Ordinal),
                $"查询的第一条请求必须是官方地址，实际「{(apiStub.Requests.Count > 0 ? apiStub.Requests[0] : "(没有请求)")}」");
            Assert(apiStub.Requests[^1]
                   == "https://gh.example.com/proxy/api.github.com/repos/CTCaer/hekate/releases?per_page=10",
                $"官方连不上之后应改走镜像站，实际「{apiStub.Requests[^1]}」");

            // 没填镜像站：官方失败就照常失败，一条请求都不许打到别处去
            apiStub.Requests.Clear();
            var noMirrorThrew = false;
            try
            {
                new GitHubReleaseService(apiHttp, new SilentSink(), null, null)
                    .GetReleasesAsync(repo, 10, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (HttpRequestException)
            {
                noMirrorThrew = true;
            }

            Assert(noMirrorThrew, "没填镜像站时官方查询失败应照常抛异常（不能静默吞掉）");
            Assert(apiStub.Requests.All(u => u.StartsWith("https://api.github.com/", StringComparison.Ordinal)),
                "没填镜像站时一条请求都不该打到别处（「留空 = 一切照旧」）");
        }

        // ── ⑤ 源码契约：两处业务出口都过同一个门，诊断出口刻意不过 ────
        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到源码目录，无法核对镜像站的接线");
            return;
        }

        var downloadSource = File.ReadAllText(Path.Combine(sourceRoot, "Services", "DownloadService.cs"));
        var releaseSource = File.ReadAllText(Path.Combine(sourceRoot, "Services", "GitHubReleaseService.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(sourceRoot, "ViewModels", "MainViewModel.cs"));

        Assert(downloadSource.Contains("GitHubMirror.Apply(", StringComparison.Ordinal),
            "DownloadService 必须过 GitHubMirror.Apply —— 五种下载地址若交给各调用方自己改写，迟早漏一个，"
            + "而漏掉的那条会安静地继续走官方地址");

        Assert(releaseSource.Contains("GitHubMirror.Apply(", StringComparison.Ordinal),
            "GitHubReleaseService 必须过 GitHubMirror.Apply（版本查询与列目录都在这里，不留出口就查不到）");

        Assert(viewModelSource.Contains("new GitHubReleaseService(_http, this, GitHubToken, Mirror)", StringComparison.Ordinal)
               && viewModelSource.Contains("new DownloadService(_downloadHttp, Mirror)", StringComparison.Ordinal),
            "MainViewModel 建这两个服务时必须把镜像站传进去 —— 传漏了界面上填了也不生效，而三处都编译得过");

        var signatureIndex = viewModelSource.IndexOf("private string NetworkSignature", StringComparison.Ordinal);
        var signatureWindow = signatureIndex < 0
            ? string.Empty
            : viewModelSource[signatureIndex..Math.Min(viewModelSource.Length, signatureIndex + 300)];

        Assert(signatureIndex >= 0 && signatureWindow.Contains("Mirror", StringComparison.Ordinal),
            "镜像站必须进 NetworkSignature —— 不进的话「填完镜像站得重启程序才生效」，"
            + "而用户填完就在界面上点了开始");

        // 反向契约：「验证 Token」**刻意不走镜像站**（理由见 VerifyTokenAsync 的注释）——
        // 额度是按 IP 算的，走镜像拿到的是镜像那个公用出口的剩余额度，用户会以为自己的 Token 坏了。
        // 这条断言防的是「顺手也给它套上镜像」那种好意改动。
        var verifyBody = BodyOf(
            File.ReadAllLines(Path.Combine(sourceRoot, "ViewModels", "MainViewModel.cs")),
            "private async Task VerifyTokenAsync()");

        Assert(verifyBody.Count > 10, $"前置条件：应取到 VerifyTokenAsync 的方法体（实际 {verifyBody.Count} 行）");
        Assert(!verifyBody.Any(l => l.Contains("GitHubMirror", StringComparison.Ordinal)),
            "「验证 Token」不许走镜像站 —— 它回答的是「**我**还剩多少额度」；"
            + "走镜像会显示镜像那个公用出口的额度，用户会以为自己的 Token 出了问题");
    }

    /// <summary>
    /// 镜像站地址要能存下来、下次打开读回来（用户要求：操作写入配置文件，再次打开软件读取该配置）。
    ///
    /// ⚠️ 这条**不能**只断言「界面上还显示着」—— 界面上显示的是当前那个对象里的值，它当然还在。
    /// 必须**重开一个 MainViewModel**，让值真的过一遍 settings.json。
    /// </summary>
    // ── boot.dat：地址解析（纯函数）与「取消内置」的源码契约 ─────────
    /// <summary>
    /// boot.dat 的地址解析（用户 2026-09-21 要求「取消内置、改为下载」）。
    ///
    /// 这里最要紧的一条：用户给的默认地址是 GitHub 的 <c>blob</c> **网页** ——
    /// 直接下会拿到一整页 HTML（HTTP 200、有字节、也写出了文件，**完全静默**），
    /// 于是 out 里躺着一个 11 KB 的假 boot.dat。所以必须解析成 raw 形态。
    /// </summary>
    private static void CheckBootDatSource()
    {
        Section("boot.dat：下载地址解析（blob → raw）与兜底通道");

        var plan = BootDatSource.Resolve(null);
        Assert(plan is not null, "默认地址（留空时用它）应当解析得出可用地址");
        if (plan is null)
        {
            return;
        }

        const string ExpectedRaw =
            "https://raw.githubusercontent.com/q1378659137/SwitchCfwWizard/main/boot.dat";

        Assert(plan.DownloadUrl == ExpectedRaw,
            $"默认地址应解析成 raw 形态（blob 是网页，下下来是 HTML），实际「{plan.DownloadUrl}」");
        Assert(plan.FallbackUrl is not null && plan.FallbackUrl.Contains("/contents/boot.dat"),
            "GitHub 文件地址应当同时给出 contents 端点兜底（raw 域名在部分网络下会被整段阻断）");
        Assert(BootDatSource.Resolve(null)!.Fallback?.Accept == BootDatSource.RawAccept,
            "兜底通道必须带 contents 端点要求的 Accept —— 不带它返回的是 JSON 元数据（HTTP 200），"
            + "会被当成引导文件写出去");

        Assert(BootDatSource.IsAcceptable(null) && BootDatSource.IsAcceptable("   "),
            "空值是「没填」：会走默认地址，界面不该对默认状态报红");

        // 三种可识别的形态都要落到同一条 raw 地址上
        foreach (var (input, label) in new[]
                 {
                     ("https://github.com/q1378659137/SwitchCfwWizard/blob/main/boot.dat", "blob 网页形态"),
                     ("https://github.com/q1378659137/SwitchCfwWizard/raw/main/boot.dat", "github.com 的 raw 形态"),
                     ("https://raw.githubusercontent.com/q1378659137/SwitchCfwWizard/main/boot.dat", "raw 主机的原始形态"),
                 })
        {
            var resolved = BootDatSource.Resolve(input);
            Assert(resolved?.DownloadUrl == ExpectedRaw,
                $"{label} 应落到同一条 raw 地址，实际「{resolved?.DownloadUrl ?? "(null)"}」");
        }

        // 幂等：把解析结果再喂一遍不许变形（不然「已经在 raw 上」的地址会被再拼一次）
        var once = BootDatSource.Resolve(BootDatSource.DefaultUrl)!.DownloadUrl;
        Assert(BootDatSource.Resolve(once)?.DownloadUrl == once, "解析必须幂等");

        // 子目录里的文件：路径要原样保留（含兜底地址里的那份）
        var nested = BootDatSource.Resolve("https://github.com/o/n/blob/dev/a/b/boot.dat");
        Assert(nested?.DownloadUrl == "https://raw.githubusercontent.com/o/n/dev/a/b/boot.dat",
            $"子路径要原样保留，实际「{nested?.DownloadUrl ?? "(null)"}」");
        Assert(nested?.FallbackUrl?.Contains("contents/a/b/boot.dat") == true,
            "兜底地址里的路径同样要保留子目录");

        // 非 GitHub 主机：照用户说的用，但不给兜底（我们无从知道那份资源在 API 上叫什么）
        var plain = BootDatSource.Resolve("https://example.com/files/boot.dat");
        Assert(plain?.DownloadUrl == "https://example.com/files/boot.dat" && plain.FallbackUrl is null,
            "非 GitHub 主机应原样使用、且不给兜底通道");

        // 解析不出来的必须**明确拒绝**，而不是硬下 —— 下到 HTML 是静默的坏结果
        foreach (var bad in new[] { "不是地址", "ftp://github.com/o/n/blob/main/boot.dat", "github.com/o/n/releases" })
        {
            Assert(BootDatSource.Resolve(bad) is null, $"「{bad}」应当解析不出来（返回 null，不猜）");
            Assert(!BootDatSource.IsAcceptable(bad), $"「{bad}」应当被判为坏值（界面红字提示）");
        }
    }

    /// <summary>
    /// 「取消内置」这件事的源码契约。用户要的是**不再嵌在程序里**，而这件事
    /// 光看行为验不出来：内嵌着一份而代码没读它，行为上完全一样 —— 直到有人
    /// 「顺手」把它当成兜底，于是「下载失败就跳过」的语义又悄悄变了。
    /// </summary>
    private static void CheckBootDatIsNotEmbedded()
    {
        Section("boot.dat：不许再内嵌（取消内置的源码契约）");

        var root = FindSourceRoot();
        if (root is null)
        {
            Assert(false, "前置条件：应能找到 src/SwitchCfwWizard 目录");
            return;
        }

        var csprojPath = Path.Combine(root, "SwitchCfwWizard.csproj");
        Assert(File.Exists(csprojPath), "前置条件：csproj 应存在");
        if (File.Exists(csprojPath))
        {
            var csproj = File.ReadAllText(csprojPath);
            Assert(!csproj.Contains("Resources\\boot.dat"),
                "csproj 里不该再有 boot.dat 的 EmbeddedResource 条目（它会让「下载失败就跳过」多出一份静默兜底）");
        }

        Assert(!File.Exists(Path.Combine(root, "Resources", "boot.dat")),
            "Resources/boot.dat 应当已经删掉 —— 改为下载之后，留着的只是一份会和线上版本漂移的死副本");
        Assert(!File.Exists(Path.Combine(root, "Infrastructure", "EmbeddedAssets.cs")),
            "EmbeddedAssets（只用来读那个嵌入资源）应当已经删掉");

        var generatorPath = Path.Combine(root, "Services", "ConfigGenerator.cs");
        var generator = File.ReadAllText(generatorPath);
        Assert(!generator.Contains("EmbeddedAssets"),
            "ConfigGenerator 不该再引用 EmbeddedAssets —— 它现在只能把**下载好的那份**复制到 out 根目录");
        Assert(generator.Contains("options.BootDatPath"),
            "ConfigGenerator 应当从 WizardOptions.BootDatPath 取源文件（下载与生成之间的唯一接口）");

        // 下载那一步必须走 DownloadService：那是「自动吃到镜像站与备用通道」的唯一保证
        var viewModelLines = File.ReadAllLines(Path.Combine(root, "ViewModels", "MainViewModel.cs"));
        var body = BodyOf(viewModelLines, "private async Task<string?> PrepareBootDatAsync(WizardOptions options, CancellationToken token)");
        Assert(body.Count > 15, $"前置条件：应取到 PrepareBootDatAsync 的方法体（实际 {body.Count} 行）");

        Assert(body.Any(l => l.Contains("_download!.DownloadAsync(")),
            "boot.dat 必须走 DownloadService 下载 —— 否则镜像站与备用通道都吃不到（各写一遍 HttpClient 就是那种漏）");
        Assert(body.Any(l => l.Contains("plan.Fallback")),
            "要把兜底通道交给下载器：raw 主机在部分网络下整段不可达，而 api.github.com 常常仍然通");
        Assert(body.Any(l => l.Contains("BootDatSource.Resolve(")),
            "地址要先经 BootDatSource 解析（blob → raw），不能拿用户填的网页地址直接下");
        Assert(body.Any(l => l.Contains("AppPaths.BootDatRoot")),
            "下载落点要用 AppPaths.BootDatRoot（download/boot/）—— 直接丢在 download 根下就违背了"
            + "「下载下来的文件放入 boot 文件夹下」这条要求");
        Assert(body.Any(l => l.Contains("BootDatProgress")),
            "下载时要回报进度（用户要求 boot.dat 也有进度条）：必须给 DownloadAsync 传 progress 并更新 BootDatProgress");
    }

    // ── 检查更新：地址解析 + 版本比较（纯函数） ──────────────────────
    /// <summary>
    /// 「检查更新」的地址解析与版本比较（用户 2026-09-21）。
    ///
    /// 两个最容易错的地方都在这里钉住：① 默认地址是 releases 的**网页**，得推出仓库才能查 API；
    /// ② 版本比较要补零到四段 —— 程序集版本是 <c>0.1.0.0</c>、tag 常常是 <c>0.1.0</c>，
    /// 直接 <c>Version.CompareTo</c> 会认为 <c>0.1.0 &lt; 0.1.0.0</c>，
    /// 于是「同一个版本」被判成「有新版本」，每次点都提示更新。
    /// </summary>
    private static void CheckUpdateSource()
    {
        Section("检查更新：地址解析、版本比较、落盘文件名");

        var repo = UpdateSource.ResolveRepo(null);
        Assert(repo?.DisplayName == "q1378659137/SwitchCfwWizard",
            $"默认地址（releases 网页）应能推出仓库，实际「{repo?.DisplayName ?? "(null)"}」");

        foreach (var (input, label) in new[]
                 {
                     ("https://github.com/o/n/releases", "releases 列表页"),
                     ("https://github.com/o/n/releases/", "带尾斜杠的 releases 页"),
                     ("https://github.com/o/n/releases/latest", "releases/latest"),
                     ("https://github.com/o/n/releases/tag/v1.2.3", "某个 tag 的页"),
                     ("https://github.com/o/n/releases/download/1.2.3/x.exe", "某个资源的直链"),
                     ("https://github.com/o/n", "仓库地址"),
                     ("https://github.com/o/n/", "带尾斜杠的仓库地址"),
                     ("https://github.com/o/n.git", "带 .git"),
                     ("https://api.github.com/repos/o/n/releases", "API 地址"),
                     ("o/n", "裸 owner/name"),
                 })
        {
            var parsed = UpdateSource.ResolveRepo(input);
            Assert(parsed?.DisplayName == "o/n",
                $"{label} 都应推出 o/n，实际「{parsed?.DisplayName ?? "(null)"}」");
        }

        // 推不出来的必须明确拒绝。⚠️ 「别的主机」那一条尤其重要：查询真正打的是 api.github.com，
        // 把 GitLab 地址当成 GitHub 仓库去查，得到的是「仓库不存在」—— 一个会把人引偏的答案。
        foreach (var bad in new[]
                 {
                     "不是地址",
                     "ftp://github.com/o/n",
                     "https://gitlab.com/o/n",
                     "https://github.com/o",
                     "https://github.com/o/n/issues/3",
                 })
        {
            Assert(UpdateSource.ResolveRepo(bad) is null, $"「{bad}」应当推不出仓库（返回 null，不猜）");
            Assert(!UpdateSource.IsAcceptable(bad), $"「{bad}」应当被判为坏值（界面红字提示）");
        }

        Assert(UpdateSource.IsAcceptable("") && UpdateSource.IsAcceptable(null),
            "空值 = 用默认地址，界面不该对默认状态报红");

        // 版本比较
        Assert(!UpdateSource.IsNewer("0.1.0", "0.1.0.0"),
            "0.1.0 与 0.1.0.0 是同一个版本 —— 分量数不同也要相等（不补零就会一直提示「有新版本」）");
        Assert(!UpdateSource.IsNewer("0.1.0", "0.1.0"), "相同版本不算新");
        Assert(UpdateSource.IsNewer("0.2.0", "0.1.0"), "0.2.0 比 0.1.0 新");
        Assert(UpdateSource.IsNewer("v0.2.0", "0.1.0"), "带 v 前缀也要能比");
        Assert(!UpdateSource.IsNewer("0.1.0-rc1", "0.1.0"), "0.1.0-rc1 取数字段后就是 0.1.0，不算新");
        Assert(UpdateSource.IsNewer("1.0", "0.9.9"), "段数不同也要能比（补零）");
        Assert(!UpdateSource.IsNewer("看着不像版本号", "0.1.0")
               && !UpdateSource.IsNewer("0.2.0", "看不懂的当前版本"),
            "任一边解析不出就返回 false —— 宁可说「已是最新」，也不要推着用户去装一个看不懂的东西");
        Assert(UpdateSource.ParseVersion("1.2.3.4.5") == new Version(1, 2, 3, 4),
            "超过四段的取前四段（Version 本身只支持四段）");
        Assert(UpdateSource.ParseVersion(null) is null && UpdateSource.ParseVersion("abc") is null,
            "解析不出的版本号返回 null，不抛");

        // 落盘文件名：必须带版本号 —— 与旧版同名会去覆盖**正在运行**的那个 exe，Windows 上必然失败
        Assert(UpdateSource.BuildAssetFileName("0.1.0") == "SwitchCfwWizard_0.1.0.exe",
            $"文件名应当是 SwitchCfwWizard_<版本>.exe，实际「{UpdateSource.BuildAssetFileName("0.1.0")}」");
        Assert(!UpdateSource.BuildAssetFileName("v1/0").Contains('/'),
            "版本号里的非法文件名字符要替换掉（否则写盘直接失败）");

        // 下回来的字节像不像 exe（错误页也可能 200 + 一堆字节）
        Assert(UpdateSource.HasPortableExecutableHeader("MZ"u8), "MZ 开头应判为可执行文件");
        Assert(!UpdateSource.HasPortableExecutableHeader("<html"u8), "错误页不该被当成 exe");
        Assert(!UpdateSource.HasPortableExecutableHeader(new byte[] { (byte)'M' }),
            "只有一个字节时不该判真（越界读取是另一类问题）");
    }



    /// <summary>
    /// 「填了但格式不对会回落默认地址」（用户 2026-09-22 的原话）。
    ///
    /// 这条以前埋在 <c>CheckUpdateAsync</c> 里：填坏了直接 <c>return</c>，整次检查作废
    ///（界面上只留一句「地址用不了」）。现在抽成纯函数 <c>UpdateSource.ResolveWithFallback</c>，
    /// 于是这条行为第一次可以被**离线穷举** —— 而不是靠「源码里我写了回落」这句话。
    /// </summary>
    private static void CheckUpdateUrlFallsBack()
    {
        Section("检查更新的地址：填坏了要回落到默认地址");

        var defaultRepo = UpdateSource.ResolveRepo(null);
        Assert(defaultRepo is not null, "前置条件：默认地址应当能推出仓库");

        // 留空 = 默认状态，**不算回落**（否则什么都没改也会看到一句警告）
        var blank = UpdateSource.ResolveWithFallback(null);
        Assert(Equals(blank.Repo, defaultRepo) && !blank.FellBack,
            "留空应当用默认地址、且不标记「回落」—— 那是默认状态，不是用户填错了");
        Assert(!UpdateSource.ResolveWithFallback("   ").FellBack,
            "只敲了空格也算「没填」，同样不该报回落");

        // 填对了：用填的那条，不报回落
        foreach (var (input, label) in new[]
                 {
                     ("https://github.com/me/fork", "完整链接"),
                     ("me/fork", "裸 owner/name"),
                     ("https://github.com/me/fork/releases", "releases 页"),
                 })
        {
            var hit = UpdateSource.ResolveWithFallback(input);
            Assert(hit.Repo?.Owner == "me" && hit.Repo?.Name == "fork" && !hit.FellBack,
                $"{label}「{input}」应当被采用且不报回落，实际 "
                + $"{hit.Repo?.DisplayName ?? "(null)"} / FellBack={hit.FellBack}");
        }

        // 填坏了：回落默认 + 标记回落（而不是让这次检查作废）
        foreach (var bad in new[] { "https://gitlab.com/o/n", "乱写一通", "github.com/a/b/c", "ftp://github.com/o/n" })
        {
            var fell = UpdateSource.ResolveWithFallback(bad);
            Assert(Equals(fell.Repo, defaultRepo) && fell.FellBack,
                $"「{bad}」应当回落到默认地址并标记 FellBack，实际 "
                + $"{fell.Repo?.DisplayName ?? "(null)"} / FellBack={fell.FellBack}");
        }

        // 反向：有回落**不代表**坏值就不报红了 —— 用户得知道自己填的那条没生效
        Assert(!UpdateSource.IsAcceptable("https://gitlab.com/o/n"),
            "坏值仍要被界面判为非法（红字提示），别因为有了回落就悄悄不吭声");

        // 源码契约：CheckUpdateAsync 必须走「带回落」的入口
        //（直接调 ResolveRepo 的话，「填坏了」又会退化成「这次不检查了」）
        var root = FindSourceRoot();
        if (root is null)
        {
            Assert(false, "前置条件：应能找到 src/SwitchCfwWizard 目录");
            return;
        }

        var body = BodyOf(
            File.ReadAllLines(Path.Combine(root, "ViewModels", "MainViewModel.cs")),
            "private async Task CheckUpdateAsync()");

        Assert(body.Any(l => l.Contains("ResolveWithFallback(")),
            "CheckUpdateAsync 必须用 ResolveWithFallback —— 否则「填坏了」又会变成「这次不检查了」");
    }

    /// <summary>
    /// 「检查更新」下载新版时要把进度喂给界面（用户 2026-09-22 要求「在版本更新的后方增加下载百分比」）。
    ///
    /// 只能靠源码契约钉住：这条路径要联网，离线回归跑不动它。
    /// 判据特意写成「**不许**再传 progress: null」，而不是「必须调用某个方法」——
    /// 传 null 时那个百分比就是**永远一片空白**，而且不报错、不崩溃，
    /// 正是这类改动最容易悄悄退化回去的样子。
    /// </summary>
    private static void CheckUpdateProgressIsWired()
    {
        Section("检查更新：下载百分比要真的接上（不许再传 progress: null）");

        var root = FindSourceRoot();
        if (root is null)
        {
            Assert(false, "前置条件：应能找到 src/SwitchCfwWizard 目录");
            return;
        }

        var lines = File.ReadAllLines(Path.Combine(root, "ViewModels", "MainViewModel.cs"));
        var body = BodyOf(lines, "private async Task CheckUpdateAsync()");
        Assert(body.Count > 30, $"前置条件：应取到 CheckUpdateAsync 的方法体（实际 {body.Count} 行）");

        var nullProgress = body.Where(l => l.Contains("progress: null")).Select(l => l.Trim()).ToList();
        Assert(nullProgress.Count == 0,
            "下载新版时必须把进度喂给界面（传 progress: null 时按钮旁边那个百分比永远是空白）"
            + (nullProgress.Count == 0 ? "" : "，违规：" + string.Join(" / ", nullProgress)));

        Assert(body.Any(l => l.Contains("UpdateProgress = p.Percent")),
            "进度回调应当写进 UpdateProgress —— 界面上那个百分比绑的就是它");

        // 反向再钉一遍界面那一侧：绑错时 WPF 是**静默**的（元素在、永远不显示）
        var xaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        Assert(xaml.Contains("UpdateProgressText"),
            "标题栏要有那个百分比文本（绑 UpdateProgressText）");
        Assert(xaml.Contains("Binding IsUpdateDownloading"),
            "百分比只该在下载那段时间显示（可见性绑 IsUpdateDownloading）");
    }

    /// <summary>
    /// boot.dat 卡片上那行文字**永不为空**（2026-09-22：那一行改为始终显示、进度条也始终在）。
    ///
    /// 控件在卡片上一直占着一行；空状态若返回空串，用户看到的就是「一条 0% 的进度条 + 一行空白」，
    /// 看上去像卡片坏了。这里把两种空状态都钉住：没勾 / 勾了但还没开始生成。
    /// </summary>
    private static void CheckBootDatStatusText()
    {
        Section("boot.dat 卡片文字：空状态也要有内容（那一行现在是始终显示的）");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            var loc = LocalizationService.Instance;
            var vm = new MainViewModel();

            // 先把两条文案取出来：避免在内插字符串的洞里再写 loc["…"]（那种嵌套引号容易踩解析器）。
            var notSelected = loc["Common.NotSelected"];
            var ready = loc["Common.Ready"];

            vm.IncludeBootDat = false;
            Assert(vm.BootDatStatusText == notSelected,
                $"没勾选时应显示「{notSelected}」，实际「{vm.BootDatStatusText}」");

            vm.IncludeBootDat = true;
            Assert(vm.BootDatStatusText == ready,
                $"勾了但还没开始生成时应显示「{ready}」，实际「{vm.BootDatStatusText}」");

            // 真的有下载状态时，必须显示状态**本身**，不能被上面那两条兜底文案盖住。
            // 这一支只能这样测：`BootDatStatus` 的 setter 是私有的（只由下载过程写），
            // 而离线回归跑不了那次下载 —— 所以用反射触发它。
            // ⚠️ 这一条不能省：三元表达式一旦写反，界面就会在**下完之后**仍然显示「就绪」，
            //    而只看空状态的那两条断言照样全绿（正好是「测了个寂寞」的样子）。
            var statusSetter = typeof(MainViewModel)
                .GetProperty("BootDatStatus")?.GetSetMethod(nonPublic: true);

            if (statusSetter is null)
            {
                Assert(false, "前置条件：BootDatStatus 应当有一个（私有的）setter");
            }
            else
            {
                statusSetter.Invoke(vm, new object[] { "正在下载到 42%" });
                Assert(vm.BootDatStatusText == "正在下载到 42%",
                    $"有下载状态时应显示状态本身，实际「{vm.BootDatStatusText}」");
            }

            // 勾选状态一变，卡片上那行文字必须**通知**出去。
            // 这条防的是「属性算得对、界面还是旧的」：`BootDatStatusText` 是派生属性，
            // 它只在 setter 里被 `OnPropertyChanged` 带出去 —— 漏了那一行，上面那几条断言
            // 照样全绿（它们读的是属性本身），而用户勾上之后卡片上仍然写着「未勾选」。
            // 用户这一轮要的正是「勾选后显示就绪」，所以「会通知」是要求的一半。
            var notified = new List<string>();
            vm.PropertyChanged += (_, args) => notified.Add(args.PropertyName ?? string.Empty);

            vm.IncludeBootDat = false;   // 上面已经置成 true 了，先复位才有「变化」
            notified.Clear();
            vm.IncludeBootDat = true;

            Assert(notified.Contains(nameof(MainViewModel.BootDatStatusText)),
                "切换勾选状态时应当通知 BootDatStatusText —— 不通知的话属性值对了、卡片上那行字还是旧的"
                + $"（实际通知了：{string.Join("/", notified.Distinct().ToArray())}）");

            // 反向：两种空状态的文案必须**不同**。若它们恰好相同，上面两条断言其实只测了一条
            // ——「跟着勾选状态走」这件事就成了假的，而两条断言照样全绿。
            Assert(notSelected != ready,
                "两种空状态的文案不该相同 —— 否则上面两条断言等于只测了一条");
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    /// <summary>
    /// 「检查更新无需写入配置文件」（用户 2026-09-21 的原话）。
    ///
    /// 这条只能靠源码契约钉住：跑一遍命令在离线回归里做不到（它要联网），
    /// 而「顺手在里面存一下状态」是最容易发生、也最没有必要的改动。
    /// </summary>
    private static void CheckUpdateDoesNotPersist()
    {
        Section("检查更新：不写配置文件（用户明确要求）");

        var root = FindSourceRoot();
        if (root is null)
        {
            Assert(false, "前置条件：应能找到 src/SwitchCfwWizard 目录");
            return;
        }

        var lines = File.ReadAllLines(Path.Combine(root, "ViewModels", "MainViewModel.cs"));
        var body = BodyOf(lines, "private async Task CheckUpdateAsync()");
        Assert(body.Count > 30, $"前置条件：应取到 CheckUpdateAsync 的方法体（实际 {body.Count} 行）");

        var writes = body
            .Where(l => l.Contains("MaterializeInto(")
                        || l.Contains("SettingsStore.Save")
                        || l.Contains("_settings."))
            .Select(l => l.Trim())
            .ToList();

        Assert(writes.Count == 0,
            "「检查更新」不许写配置文件（查到新版、下载、启动，都不该动 settings.json）"
            + (writes.Count == 0 ? "" : "，违规：" + string.Join(" / ", writes)));

        // 反向：这个按钮本身要能留痕（与本项目其它按钮同一条约定）
        Assert(lines.Any(l => l.Contains("CheckUpdateCommand = Command(new(")),
            "「检查更新」必须经 Command(...) 建 —— 否则点了不会进日志文件");
    }

    /// <summary>
    /// 两个新地址写进 <c>settings.json</c> 再读回来（用户要求「等待再次打开软件读取该配置文件」）。
    /// 「界面上还显示着」不算证据：那是内存里那个对象的值，它当然还在。
    /// </summary>
    private static void CheckNewUrlSettingsPersistence()
    {
        Section("boot.dat 下载地址 / 检查更新地址：写进 settings.json → 重开读回来");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            const string bootDat = "https://raw.githubusercontent.com/me/fork/main/boot.dat";
            const string update = "https://github.com/me/fork/releases";

            var vm = new MainViewModel();
            vm.BootDatUrl = bootDat;
            vm.UpdateUrl = update;
            TouchAndFlush(vm);

            var saved = SettingsStore.Load();
            Assert(saved.BootDatUrl == bootDat && saved.UpdateUrl == update,
                "两个新地址都应原样写进 settings.json（存原话，规范化在用的时候做）"
                + $"；实际「{saved.BootDatUrl ?? "(null)"}」/「{saved.UpdateUrl ?? "(null)"}」");

            var reloaded = new MainViewModel();
            Assert(reloaded.BootDatUrl == bootDat && reloaded.UpdateUrl == update,
                "重开之后两个地址都应原样读回来"
                + $"；实际「{reloaded.BootDatUrl}」/「{reloaded.UpdateUrl}」");
            Assert(!reloaded.BootDatUrlIsInvalid && !reloaded.UpdateUrlIsInvalid,
                "合法地址不该被判为非法");

            // 清空 → 重开应为空（空 = 用默认地址），且不报红
            reloaded.BootDatUrl = string.Empty;
            reloaded.UpdateUrl = string.Empty;
            TouchAndFlush(reloaded);

            var cleared = new MainViewModel();
            Assert(cleared.BootDatUrl.Length == 0 && cleared.UpdateUrl.Length == 0,
                "清空之后重开应该是空的（空 = 用内置默认地址）");
            Assert(!cleared.BootDatUrlIsInvalid && !cleared.UpdateUrlIsInvalid,
                "空值算「没填」而不是「填错了」—— 界面不该对默认状态报红");

            // 填坏 → 界面报红，但**照样存下来**（与镜像站、下载源同一约定：免得用户白填一遍）
            cleared.BootDatUrl = "ftp://github.com/o/n/blob/main/boot.dat";
            cleared.UpdateUrl = "https://gitlab.com/o/n";
            Assert(cleared.BootDatUrlIsInvalid && cleared.UpdateUrlIsInvalid,
                "解析不出的地址应当被判为非法（界面红字提示）");
            TouchAndFlush(cleared);

            var badSaved = new MainViewModel();
            Assert(badSaved.BootDatUrl.Contains("ftp://") && badSaved.UpdateUrl.Contains("gitlab.com"),
                "填坏的地址也应照样存下来：下次打开还在，用户才看得到自己填过什么、好去改");
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    private static void CheckMirrorSettingPersistence()
    {
        Section("GitHub 镜像站：写进 settings.json → 重新打开读回来");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            var vm = new MainViewModel();
            vm.Mirror = "https://gh.example.com/";
            TouchAndFlush(vm);

            var saved = SettingsStore.Load();
            Assert(saved.Mirror == "https://gh.example.com/",
                "镜像站地址应原样写进 settings.json（用户填什么就存什么，规范化在用的时候做）"
                + $"；实际「{saved.Mirror ?? "(null)"}」");

            var reloaded = new MainViewModel();
            Assert(reloaded.Mirror == "https://gh.example.com/",
                $"重开之后镜像站地址应原样读回来，实际「{reloaded.Mirror}」");
            Assert(!reloaded.MirrorIsInvalid, "合法（哪怕带尾斜杠）的镜像站不该被判为非法");

            // 反向：清空 → 重开应是空（空 = 走官方地址），且不报红
            reloaded.Mirror = string.Empty;
            TouchAndFlush(reloaded);

            var cleared = new MainViewModel();
            Assert(cleared.Mirror.Length == 0, "清空镜像站后重开应该是空的");
            Assert(!cleared.MirrorIsInvalid, "空值算「没配」而不是「配错了」—— 界面不该对默认状态报红");

            // 填坏 → 界面报红，但**照样存下来**（免得用户白填一遍，与下载源那一栏同一约定）
            cleared.Mirror = "https://gh.example.com/CTCaer/hekate";
            Assert(cleared.MirrorIsInvalid, "把仓库路径整段粘进来时应判为非法（界面红字提示）");
            TouchAndFlush(cleared);

            Assert(new MainViewModel().Mirror == "https://gh.example.com/CTCaer/hekate",
                "填坏的地址也应照样存下来：下次打开还在，用户才看得到自己填过什么、好去改");
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    /// <summary>
    /// 桩：把 <c>api.github.com</c> 一律当成「连不上」（**连接层**失败，没有 HTTP 状态码），
    /// 其余地址返回一段合法的 Release JSON。
    ///
    /// 判别「连接层失败」与「GitHub 给出的回答（403 限流之类）」正是查询通道切换的全部判据：
    /// 前者该换镜像站，后者不该 —— 换过去只会撞上镜像那份更挤的额度，还会把真正的原因盖掉。
    /// </summary>
    private sealed class ApiChannelStubHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            Requests.Add(url);

            // ⚠️ 判据必须是**主机名**，不能用「URL 里含 api.github.com」：镜像地址长成
            //    `<镜像>/proxy/api.github.com/…`，用子串判断会把镜像那条也一并判失败，
            //    于是「官方连不上 → 改用镜像」这条路在桩里永远走不通（第一次就踩了这个坑）。
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                // 没有 StatusCode 的 HttpRequestException 就是「根本打不通」那一族
                // （DNS 解析不了 / 连接被拒 / 代理挡了），NetworkErrors.IsTransient 认为它值得重试。
                return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("No such host is known. (api.github.com:443)"));
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"tag_name\":\"v9.9.9\",\"name\":\"x\",\"prerelease\":false,"
                    + "\"published_at\":\"2024-01-01T00:00:00Z\",\"html_url\":\"https://example.invalid\","
                    + "\"assets\":[]}]",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    // ── 下载的备用通道（直链失败 → GitHub API asset 端点） ────────
    /// <summary>
    /// 用桩 <see cref="HttpMessageHandler"/> 离线验证备用通道。**不联网**，所以在任何环境下都能跑。
    ///
    /// 为什么值得专门测：这条路径只在「直链坏了」的时候才走到，而那种环境（代理拦 302 那一跳）
    /// 恰恰是最不容易复现的。它又有两个静默失败点 ——
    /// ① 少了 <c>Accept: application/octet-stream</c> 时 GitHub 返回的是资源的 **JSON 元数据**（HTTP 200），
    ///    程序会以为下载成功，把一段 JSON 当成组件包写进 SD 卡；
    /// ② 换通道时如果不报日志，用户只会看到「重试 3 次后成功」，根本不知道走的哪条路。
    /// </summary>
    private static void CheckDownloadFallback()
    {
        Section("下载备用通道（直链失败 → API asset 端点）");

        const string directUrl = "https://github.com/CTCaer/hekate/releases/download/v6.5.3/hekate.zip";
        var assetUrl = ReleaseAsset.BuildApiAssetUrl(new RepoSpec("CTCaer", "hekate"), 123456);
        var stamp = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), "scw-fallback-" + stamp + ".bin");

        var stub = new StubHandler(directStatus: 502, fallbackStatus: 200, fallbackBody: "FALLBACK-BYTES");
        var notifications = new List<DownloadRetry>();

        try
        {
            using var http = new HttpClient(stub) { Timeout = Timeout.InfiniteTimeSpan };
            var service = new DownloadService(http) { MaxAttempts = 2, Backoff = _ => TimeSpan.Zero };

            // ① 直链一直 502，备用地址能通 —— 落盘的内容必须来自备用地址
            service.DownloadAsync(
                directUrl, path, null, CancellationToken.None, notifications.Add,
                new DownloadFallback(assetUrl, "application/octet-stream")).GetAwaiter().GetResult();

            Assert(File.Exists(path), "备用通道成功时目标文件应存在");
            Assert(File.ReadAllText(path) == "FALLBACK-BYTES", "目标文件的内容应来自备用地址");
            Assert(!File.Exists(path + ".part"), "下载完成后不应留下 .part 临时文件");
            Assert(stub.DirectRequests == 2, $"直链应先用满 2 次机会才换通道，实际 {stub.DirectRequests} 次");
            Assert(stub.FallbackRequests == 1, $"备用地址应只请求 1 次，实际 {stub.FallbackRequests} 次");

            // ② 换通道必须专门报一次，且要标成「已经在走备用地址」
            var switches = notifications.Where(n => n.IsChannelSwitch).ToList();
            Assert(switches.Count == 1, $"换通道应恰好通知 1 次，实际 {switches.Count} 次");
            Assert(switches.Count == 1 && switches[0].IsFallback, "换通道的通知应标明这次已在走备用地址");
            Assert(notifications.Count(n => !n.IsChannelSwitch && !n.IsFallback) == 1,
                "直链阶段应报 1 次普通重试（第 1 次失败之后）");
            Assert(notifications.Count(n => !n.IsChannelSwitch && n.IsFallback) == 0,
                "备用地址一次就成功时不应再报备用地址的重试");

            // ③ 最关键的一条：asset 端点必须带 Accept: application/octet-stream。
            //    少了它 GitHub 返回 JSON 元数据（HTTP 200），程序会把 JSON 当组件包写盘。
            Assert(stub.LastFallbackAccept == "application/octet-stream",
                $"asset 端点必须发 Accept: application/octet-stream，实际「{stub.LastFallbackAccept ?? "(空)"}」");
            Assert(string.IsNullOrEmpty(stub.LastDirectAccept),
                $"直链不应带 Accept 头（下载用的 client 没有默认 Accept），实际「{stub.LastDirectAccept ?? "(空)"}」");

            // ④ client 上挂着 GitHub API 的默认 Accept 时（查询 Release 的那个 client 就是这样），
            //    逐请求指定的 octet-stream 也必须生效。这条同时钉住「同名默认头与请求头谁说了算」
            //    这个 .NET 行为假设 —— 假设错了，asset 端点会返回 JSON 元数据。
            stub.Reset(directStatus: 502, fallbackStatus: 200, fallbackBody: "FALLBACK-BYTES");
            using (var apiHttp = new HttpClient(stub) { Timeout = Timeout.InfiniteTimeSpan })
            {
                apiHttp.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                var apiService = new DownloadService(apiHttp) { MaxAttempts = 1, Backoff = _ => TimeSpan.Zero };
                apiService.DownloadAsync(
                    "https://example.invalid/default-accept.zip", path + ".defaultaccept", null,
                    CancellationToken.None, null,
                    new DownloadFallback(assetUrl, "application/octet-stream")).GetAwaiter().GetResult();

                Assert(stub.LastFallbackAccept == "application/octet-stream",
                    $"client 带默认 Accept 时 asset 端点仍须只发 octet-stream，实际「{stub.LastFallbackAccept ?? "(空)"}」");
            }

            // ⑤ 拿不到 asset id 时不给备用地址 → 行为与以前完全一致，异常照常抛出来
            stub.Reset(directStatus: 502, fallbackStatus: 200, fallbackBody: "X");
            var noFallbackThrew = false;
            try
            {
                service.DownloadAsync(directUrl, path + ".nofallback", null, CancellationToken.None, null, null)
                    .GetAwaiter().GetResult();
            }
            catch (HttpRequestException)
            {
                noFallbackThrew = true;
            }

            Assert(noFallbackThrew, "没有备用地址时直链失败应照常抛异常（不能静默吞掉）");
            Assert(!File.Exists(path + ".nofallback"), "失败时不应产出目标文件");
            Assert(!File.Exists(path + ".nofallback.part"), "失败时不应留下 .part 临时文件");

            // ⑥ 备用地址与直链相同时不额外多跑一轮（否则同一个坏地址要等两倍退避）
            stub.Reset(directStatus: 502, fallbackStatus: 200, fallbackBody: "X");
            var sameUrlThrew = false;
            try
            {
                service.DownloadAsync(
                    directUrl, path + ".same", null, CancellationToken.None, null,
                    new DownloadFallback(directUrl, "application/octet-stream")).GetAwaiter().GetResult();
            }
            catch (HttpRequestException)
            {
                sameUrlThrew = true;
            }

            Assert(sameUrlThrew && stub.DirectRequests == 2,
                $"备用地址与直链相同时应只走直链那 2 次，实际 {stub.DirectRequests} 次");

            // ⑦ 非暂时性错误（404）不该换通道 —— 换了也一样 404，只是白等退避
            stub.Reset(directStatus: 404, fallbackStatus: 200, fallbackBody: "X");
            var notFoundThrew = false;
            try
            {
                service.DownloadAsync(
                    directUrl, path + ".404", null, CancellationToken.None, null,
                    new DownloadFallback(assetUrl, "application/octet-stream")).GetAwaiter().GetResult();
            }
            catch (HttpRequestException)
            {
                notFoundThrew = true;
            }

            Assert(notFoundThrew, "404 应直接失败，不该被当成可重试");
            Assert(stub.DirectRequests == 1, $"404 应立即失败而不是重试，实际 {stub.DirectRequests} 次");
            Assert(stub.FallbackRequests == 0, $"404 时不应改走备用地址，实际走了 {stub.FallbackRequests} 次");

            // ⑧ 兜底：备用地址若返回 JSON（token 权限不足、端点行为变了…），必须当失败，
            //    绝不能把 JSON 当组件包写盘 —— 那种错误要等拷进机器才发现
            stub.Reset(directStatus: 502, fallbackStatus: 200, fallbackBody: "{\"name\":\"hekate.zip\"}",
                fallbackContentType: "application/json");
            var jsonThrew = false;
            try
            {
                service.DownloadAsync(
                    directUrl, path + ".json", null, CancellationToken.None, null,
                    new DownloadFallback(assetUrl, "application/octet-stream")).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException)
            {
                jsonThrew = true;
            }

            Assert(jsonThrew, "备用地址返回 JSON 时应报错，而不是把它当成组件包写盘");
            Assert(!File.Exists(path + ".json"), "返回 JSON 时不应产出目标文件");

            // ⑨ 配了 Token 时 Authorization 要真的发出去（asset 端点计入 API 配额），没配就一个头都不发。
            //    这条是「Token 会不会漏到 S3 上」那个问题的前半段：.NET 在 302 跳转时会清掉 Authorization
            //    （官方文档明说 "The Authorization header is cleared on auto-redirects"），后半段没法离线测，
            //    只能靠文档 + 这一条合起来保证。
            stub.Reset(directStatus: 502, fallbackStatus: 200, fallbackBody: "X");
            using (var authHttp = new HttpClient(stub) { Timeout = Timeout.InfiniteTimeSpan })
            {
                var authService = new DownloadService(authHttp) { MaxAttempts = 1, Backoff = _ => TimeSpan.Zero };
                authService.DownloadAsync(
                    "https://example.invalid/auth.zip", path + ".auth", null, CancellationToken.None, null,
                    new DownloadFallback(assetUrl, "application/octet-stream", "Bearer ghp_test"))
                    .GetAwaiter().GetResult();

                Assert(stub.LastFallbackAuthorization == "Bearer ghp_test",
                    $"备用地址应带上 Authorization，实际「{stub.LastFallbackAuthorization ?? "(空)"}」");
                Assert(stub.LastDirectAuthorization is null,
                    $"直链不该带 Authorization，实际「{stub.LastDirectAuthorization ?? "(空)"}」");
            }

            // ⑩ MainViewModel 拼备用地址的三条分支：带 Token / Token 只有空白 / 拿不到 asset id。
            //    「Bearer 」前缀漏了会被 GitHub 判成 401；「没有 id 却硬拼 URL」会去打一个必然 404 的地址 ——
            //    两种都是静默失败，所以钉住。
            var savedSettings = File.Exists(SettingsStore.FilePath) ? File.ReadAllText(SettingsStore.FilePath) : null;
            try
            {
                var vm = new MainViewModel();
                var repo = new RepoSpec("CTCaer", "hekate");
                var withId = new ReleaseAsset("hekate.zip", "https://example.invalid/hekate.zip", 1, 987);

                vm.GitHubToken = "ghp_test";
                var authorized = vm.BuildDownloadFallback(repo, withId);
                Assert(authorized is not null && authorized.Authorization == "Bearer ghp_test",
                    "配了 Token 时应带「Bearer 」前缀");
                Assert(authorized is not null && authorized.Accept == "application/octet-stream",
                    "备用地址必须要求 application/octet-stream（少了会拿回 JSON 元数据）");
                Assert(authorized is not null && authorized.Url.EndsWith("/releases/assets/987", StringComparison.Ordinal),
                    "备用地址应指向该资源的 asset 端点");

                vm.GitHubToken = "   ";
                var anonymous = vm.BuildDownloadFallback(repo, withId);
                Assert(anonymous is not null && anonymous.Authorization is null,
                    "Token 只有空白时不该发 Authorization");

                Assert(vm.BuildDownloadFallback(repo, new ReleaseAsset("x", "https://example.invalid/x", 1, 0)) is null,
                    "拿不到 asset id 时应返回 null（退回只走直链），而不是硬拼一个必然 404 的地址");
            }
            finally
            {
                if (savedSettings is null)
                {
                    if (File.Exists(SettingsStore.FilePath))
                    {
                        File.Delete(SettingsStore.FilePath);
                    }
                }
                else
                {
                    File.WriteAllText(SettingsStore.FilePath, savedSettings);
                }
            }
        }
        finally
        {
            foreach (var leftover in new[]
                     {
                         path, path + ".part", path + ".defaultaccept", path + ".nofallback",
                         path + ".nofallback.part", path + ".same", path + ".404", path + ".json",
                         path + ".auth",
                     })
            {
                if (File.Exists(leftover))
                {
                    File.Delete(leftover);
                }
            }
        }

        // ⑪ id 解析：直链失败时靠它拼出 asset 端点，解析不到就只能干瞪眼
        const string releasesJson = "[{\"tag_name\":\"v6.5.3\",\"name\":\"hekate\",\"prerelease\":false,"
            + "\"published_at\":\"2024-01-01T00:00:00Z\",\"html_url\":\"https://example.invalid\","
            + "\"assets\":[{\"id\":987654321,\"name\":\"hekate.zip\","
            + "\"browser_download_url\":\"https://example.invalid/hekate.zip\",\"size\":123},"
            + "{\"name\":\"no-id.bin\",\"browser_download_url\":\"https://example.invalid/no-id.bin\",\"size\":1}]}]";

        var parsed = GitHubReleaseService.ParseReleases(releasesJson);
        Assert(parsed.Count == 1 && parsed[0].Assets.Count == 2, "应解析出 1 个 Release、2 个资源");
        Assert(parsed[0].Assets[0].Id == 987654321, "资源的 id 应被解析出来");
        Assert(parsed[0].Assets[0].HasApiId, "有 id 的资源应能用 asset 端点");
        Assert(!parsed[0].Assets[1].HasApiId,
            "缺 id 的资源应退回「只走直链」，而不是拿着 0 去拼一个必然 404 的地址");
        Assert(ReleaseAsset.BuildApiAssetUrl(new RepoSpec("CTCaer", "hekate"), 987654321)
               == "https://api.github.com/repos/CTCaer/hekate/releases/assets/987654321",
            "asset 端点的 URL 形状必须与 GitHub 文档一致");
    }

    /// <summary>
    /// 桩 <see cref="HttpMessageHandler"/>：按 URL 里有没有 <c>/releases/assets/</c> 区分「直链」与
    /// 「asset 端点」（与真实端点的形状一致），并记录每次请求实际带出去的 Accept 与 Authorization 头。
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private int _directStatus;
        private int _fallbackStatus;
        private string _fallbackBody = string.Empty;
        private string? _fallbackContentType;

        public StubHandler(int directStatus, int fallbackStatus, string fallbackBody,
            string? fallbackContentType = null)
            => Reset(directStatus, fallbackStatus, fallbackBody, fallbackContentType);

        public int DirectRequests { get; private set; }

        public int FallbackRequests { get; private set; }

        public string? LastDirectAccept { get; private set; }

        public string? LastFallbackAccept { get; private set; }

        public string? LastDirectAuthorization { get; private set; }

        public string? LastFallbackAuthorization { get; private set; }

        /// <summary>
        /// 主通道**实际请求**的地址。镜像那条链只有靠它才验得了 ——
        /// Accept / Authorization 都是「请求头对不对」，而「到底打在哪个主机上」只能看 URI。
        /// </summary>
        public string? LastDirectUrl { get; private set; }

        /// <summary>备用通道实际请求的地址（用来证明镜像**没有**把备用通道一起改写掉）。</summary>
        public string? LastFallbackUrl { get; private set; }

        public void Reset(int directStatus, int fallbackStatus, string fallbackBody,
            string? fallbackContentType = null)
        {
            _directStatus = directStatus;
            _fallbackStatus = fallbackStatus;
            _fallbackBody = fallbackBody;
            _fallbackContentType = fallbackContentType;
            DirectRequests = 0;
            FallbackRequests = 0;
            LastDirectAccept = null;
            LastFallbackAccept = null;
            LastDirectAuthorization = null;
            LastFallbackAuthorization = null;
            LastDirectUrl = null;
            LastFallbackUrl = null;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var isFallback = url.Contains("/releases/assets/", StringComparison.Ordinal);

            // 请求头里现在有什么，就记什么 —— HttpClient 已经把默认头并进来了，
            // 所以这里看到的正是真正发出去的那一组。
            var accept = request.Headers.Accept.Count == 0
                ? null
                : string.Join(",", request.Headers.Accept.Select(a => a.MediaType));
            var authorization = request.Headers.Authorization?.ToString();

            if (isFallback)
            {
                FallbackRequests++;
                LastFallbackAccept = accept;
                LastFallbackAuthorization = authorization;
                LastFallbackUrl = url;

                if (_fallbackStatus is < 200 or >= 300)
                {
                    return Task.FromResult(Fail(_fallbackStatus));
                }

                var content = new ByteArrayContent(Encoding.UTF8.GetBytes(_fallbackBody));
                if (_fallbackContentType is not null)
                {
                    content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue(_fallbackContentType);
                }

                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = content,
                });
            }

            DirectRequests++;
            LastDirectAccept = accept;
            LastDirectAuthorization = authorization;
            LastDirectUrl = url;

            return Task.FromResult(_directStatus is >= 200 and < 300
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("DIRECT-BYTES")),
                }
                : Fail(_directStatus));
        }

        private static HttpResponseMessage Fail(int status)
            => new((System.Net.HttpStatusCode)status) { Content = new StringContent(string.Empty) };
    }

    // ── kip1 开关 ───────────────────────────────────────────────
    private static void CheckKip1Toggle()
    {
        Section("kip1=atmosphere/kips/* 开关");

        var options = new WizardOptions
        {
            Atmosphere = true,
            Hekate = true,
            BootStock = false,
            BootSysNand = true,
            BootEmuNand = false,
            // 只验证 ini 内容与文件数，显式关掉合并
            IncludeComponentFilesInOutput = false,
            IncludePayloads = false,
            // 这几个场景只关心配置生成，显式关掉 boot.dat —— 否则新增开关会让
            // 「应写入 N 个文件」这类计数断言跟着漂移，测的就不是原本的意图了。
            IncludeBootDat = false,
        };

        ApplyCatalogDefaults(options);

        // 关闭时不应出现
        var off = Generate(options);
        Assert(off.WrittenFiles.Count == 6, $"应写入 6 个文件，实际 {off.WrittenFiles.Count} 个");
        Assert(!Read("bootloader/hekate_ipl.ini").Contains("kip1=atmosphere/kips/*"),
            "开关关闭时不应写入 kip1=atmosphere/kips/*");

        // 开启时出现在 pkg3 之后
        options.Set(ComponentKind.Hekate, "kip1_atmosphere_kips", "1");
        Generate(options);

        var hekate = Read("bootloader/hekate_ipl.ini");
        Assert(hekate.Contains("kip1=atmosphere/kips/*"), "开关开启时应写入 kip1=atmosphere/kips/*");

        var entry = ReadSection(hekate, SysNandTitle);
        var pkg3Index = entry.IndexOf("pkg3=", StringComparison.Ordinal);
        var kip1Index = entry.IndexOf("kip1=atmosphere/kips/*", StringComparison.Ordinal);
        Assert(pkg3Index >= 0 && kip1Index > pkg3Index, "kip1 必须排在 pkg3 之后（hekate 按顺序解析）");
    }

    // stratosphere.ini 的 nogc 写入分支此前**从未被任何用例走到过**：
    //   · 目录默认值是 "auto" → 只写一条说明注释、不写键值行（用户那份 stratosphere.ini 也是这样）
    //   · 只有 "0"/"1" 才写 nogc=…
    // 这恰好也是「Key 拼错」能完全隐形的原因：拼错后 Option() 回退空串，同样落进「只写注释」分支，
    // 产物**逐字节相同**，任何断言都看不出差别。所以这里把三个分支都钉住，让 nogc 变成可观察的。
    private static void CheckStratosphereNogc()
    {
        Section("stratosphere.ini：nogc 的三个取值分支（auto 不写行 / 0、1 写行）");

        var options = new WizardOptions
        {
            Atmosphere = true,
            Hekate = false,
            Ultrahand = false,
            SysPatch = false,
            BootStock = false,
            BootSysNand = false,
            BootEmuNand = false,
            // 只验 ini 内容，显式关掉会改文件数/落点的开关（理由同 CheckKip1Toggle）
            IncludeComponentFilesInOutput = false,
            IncludePayloads = false,
            IncludeBootDat = false,
        };

        ApplyCatalogDefaults(options);

        // ① 默认 auto：不写 nogc 键值行，只留一条说明注释
        Generate(options);
        var auto = Read("atmosphere/config/stratosphere.ini");
        Assert(auto.Contains("[stratosphere]"), "stratosphere.ini 应含 [stratosphere] 段");
        Assert(!auto.Contains("nogc="), "nogc=auto 时不应写出 nogc 键值行（保持自动判断）");

        // ② 强制启用
        options.Set(ComponentKind.Atmosphere, "nogc", "1");
        Generate(options);
        Assert(Read("atmosphere/config/stratosphere.ini").Contains("nogc=1"),
            "nogc=1 时应写出 nogc=1（强制启用，始终禁用游戏卡读取器）");

        // ③ 强制禁用
        options.Set(ComponentKind.Atmosphere, "nogc", "0");
        Generate(options);
        Assert(Read("atmosphere/config/stratosphere.ini").Contains("nogc=0"),
            "nogc=0 时应写出 nogc=0（强制禁用）");
    }

    // 「段写错 / 忘写段」这一侧的护栏。
    // system_settings.ini 是**按模块分段**的（[eupld] / [usb] / [ro] / [atmosphere] / [hbloader] / [lm]），
    // Atmosphere 按段把这些键分发给对应模块 —— 键落在错误的段里，固件根本不读它，**等于没配**。
    // 而写入器是泛化的：settings.Set(option.Section ?? "atmosphere", option.Key, …)
    //   · 兜底 "atmosphere" 对 22 个选项里的 15 个恰好正确 → 这个错误在多数情况下看不出来；
    //   · Section 是 string?，「忘了写」与「本来就不需要」在类型上无法区分。
    // 现有断言全是整文件 Contains("key=value")，**段根本不参与断言**
    // （[usb] / [eupld] / [ro] / [hbloader] / [lm] 在测试里一次都没出现）→ 把键挪到别的段，所有断言照样全绿。
    private static void CheckIniSectionsMatchDeclarations()
    {
        Section("配置文件段：键必须落在它声明的段里（防「段写错 / 忘写段」被固件静默忽略）");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到源码目录，无法核对配置文件段");
            return;
        }

        var generatorPath = Path.Combine(sourceRoot, "Services", "ConfigGenerator.cs");
        var sectionDrivenFiles = FindSectionDrivenTargetFiles(File.ReadAllText(generatorPath));
        Assert(sectionDrivenFiles.Count > 0,
            "前置条件：应从 ConfigGenerator 里找出用 `option.Section ?? …` 泛化写出的目标文件，否则下面的比对恒真");

        var options = new WizardOptions
        {
            Atmosphere = true,
            Hekate = true,
            Ultrahand = true,
            SysPatch = true,
            BootStock = true,
            BootSysNand = true,
            BootEmuNand = true,
            IncludeComponentFilesInOutput = false,
            IncludePayloads = false,
            IncludeBootDat = false,
        };

        ApplyCatalogDefaults(options);
        Generate(options);

        var declaredTotal = 0;
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var wrong = new List<string>();

        foreach (var file in sectionDrivenFiles)
        {
            var declared = ComponentCatalog.All
                .SelectMany(d => d.Options)
                .Where(o => string.Equals(o.TargetFile, file, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // 注意：Assert 成功时也会打印 message，所以 message 必须是「中性陈述」，不能只在失败时读得通。
            Assert(declared.Count > 0,
                $"前置条件：{file} 应有选项声明（当前 {declared.Count} 个），否则下面的比对恒真");

            var actual = ParseIniKeySections(Read(file));
            foreach (var option in declared)
            {
                declaredTotal++;
                if (!string.IsNullOrEmpty(option.Section))
                {
                    sections.Add(option.Section);
                }

                if (!actual.TryGetValue(option.Key, out var section))
                {
                    missing.Add($"{file} 里没有 {option.Key}");
                    continue;
                }

                if (!string.Equals(section, option.Section, StringComparison.OrdinalIgnoreCase))
                {
                    wrong.Add($"{option.Key} 声明 [{(string.IsNullOrEmpty(option.Section) ? "未声明" : option.Section)}]"
                              + $"，实际落在 [{section}]（{file}）");
                }
            }
        }

        Assert(declaredTotal > 0, "前置条件：泛化写出的文件应有选项，否则下面的比对恒真");
        Assert(sections.Count >= 2,
            $"前置条件：这些文件应跨多个段（当前 {sections.Count} 个）—— 只有一个段时「段写错」本来就不可能发生，本用例也就失去意义");
        Assert(missing.Count == 0,
            "声明写进配置文件的选项，其键必须真的出现在该文件里：" + string.Join("；", missing));
        Assert(wrong.Count == 0,
            "键必须落在它声明的段里（段写错 = 固件按段分发时读不到，等于没配）：" + string.Join("；", wrong));
    }

    /// <summary>
    /// 从 <c>ConfigGenerator.cs</c> 里找出「用 <c>option.Section</c> 泛化写出」的目标文件。
    /// 做法：每个 `option.Section ??` 出现处**向上**找最近的、以 <c>.ini</c> 结尾且含 <c>/</c> 的字符串字面量。
    /// **刻意不手抄文件清单** —— 新增第三个泛化循环会自动被带上（认错了会在上面那条前置条件上炸，不会静默漏过）。
    /// </summary>
    private static List<string> FindSectionDrivenTargetFiles(string generatorSource)
    {
        var lines = generatorSource.Replace("\r\n", "\n").Split('\n');
        var files = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("option.Section ??", StringComparison.Ordinal))
            {
                continue;
            }

            for (var j = i; j >= 0 && j > i - 40; j--)
            {
                var match = Regex.Match(lines[j], "\"([^\"]+\\.ini)\"", RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                var candidate = match.Groups[1].Value;
                if (candidate.Contains('/') && !files.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    files.Add(candidate);
                }

                break;
            }
        }

        return files;
    }

    /// <summary>把 ini 文本解析成「键 → 所在段」。段外的键记为 <c>(无段)</c>。</summary>
    private static Dictionary<string, string> ParseIniKeySections(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var current = "(无段)";

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1];
                continue;
            }

            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            var index = line.IndexOf('=');
            if (index > 0)
            {
                result[line[..index].Trim()] = current;
            }
        }

        return result;
    }

    // ── 完整场景 ────────────────────────────────────────────────
    private static void CheckFullScenario()
    {
        Section("完整场景：Atmosphere + Hekate + Ultrahand + Sys-patch，三引导 + 屏蔽序列号 + 90DNS + 8G");

        var options = new WizardOptions
        {
            Atmosphere = true,
            Hekate = true,
            Ultrahand = true,
            SysPatch = true,
            Ram8Gb = true,
            BootStock = true,
            BootSysNand = true,
            BootEmuNand = true,
            BlankSerialSysmmc = true,
            BlankSerialEmummc = true,
            Use90DnsSysmmc = true,
            Use90DnsEmummc = true,
            PreferPrerelease = true,
            // 本场景只验证「配置文件生成得对不对」，合并组件本体有专门的 CheckMergeIntoOut。
            // 这里显式关掉，避免依赖 WizardOptions 的默认值——默认值会变，测试的意图不该跟着变。
            IncludeComponentFilesInOutput = false,
            // payload 同理显式关掉：断言的是配置文件条数，不该被暂存目录里的 payload 影响
            IncludePayloads = false,
            // 这几个场景只关心配置生成，显式关掉 boot.dat —— 否则新增开关会让
            // 「应写入 N 个文件」这类计数断言跟着漂移，测的就不是原本的意图了。
            IncludeBootDat = false,
            // 界面语言也要显式给。不给的话走 WizardOptions.Language 的空串默认值，
            // 而空串在真实程序里**永远不会出现**（BuildWizardOptions 一律从 LocalizationService 取），
            // 于是 Ultrahand 的 default_lang 会被解析成 en —— 样本展示的是一个跑不出来的输入。
            // 这里取程序自己的默认界面语言，样本于是就是「打开软件、默认设置」会得到的东西。
            Language = "zh-Hans",
        };

        ApplyCatalogDefaults(options);
        options.Set(ComponentKind.Atmosphere, "nogc", "-1");
        options.Set(ComponentKind.Hekate, "autoboot", "3");

        var result = Generate(options);

        // 后面的打包/校验用例都要复用这一套结果，别重复生成把 out/ 弄乱
        _fullOptions = options;
        _fullResult = result;

        // 12 = 原有 11 份 + config/ovl-sysmodules/config.ini（用户配置基线新增的一份 overlay 配置）
        Assert(result.WrittenFiles.Count == 12, $"应写入 12 个文件，实际 {result.WrittenFiles.Count} 个");

        // exosphere.ini
        var exosphere = Read("exosphere.ini");
        Assert(exosphere.Contains("enable_mem_mode=1"), "8G 运存应写入 enable_mem_mode=1");
        Assert(exosphere.Contains("blank_prodinfo_sysmmc=1"), "屏蔽序列号应写入 blank_prodinfo_sysmmc=1");
        Assert(exosphere.Contains("blank_prodinfo_emummc=1"), "屏蔽序列号应写入 blank_prodinfo_emummc=1");
        Assert(exosphere.Contains("allow_writing_to_cal_sysmmc=0"), "默认不应允许写入 PRODINFO");

        // 90DNS
        Assert(LineCount("atmosphere/hosts/default.txt") == 2, "default.txt 应为 2 行");
        Assert(LineCount("atmosphere/hosts/sysmmc.txt") == 40, "sysmmc.txt 应为 40 行");
        Assert(LineCount("atmosphere/hosts/emummc.txt") == 40, "emummc.txt 应为 40 行");
        Assert(Read("atmosphere/hosts/sysmmc.txt").Contains("95.216.149.205 *90dns.test"), "90DNS 内容完整");

        // hekate_ipl.ini
        var hekate = Read("bootloader/hekate_ipl.ini");
        Assert(hekate.Contains("[config]"), "hekate_ipl.ini 含 [config] 段");
        Assert(hekate.Contains("autoboot=3"), "autoboot 应指向第 3 个引导项");
        Assert(hekate.Contains("{------ Stock / OFW ------}"), "含 caption 标题行");
        Assert(SectionCount(hekate, StockTitle) == 1, "含正版系统引导项");
        Assert(SectionCount(hekate, SysNandTitle) == 1, "含真实破解系统引导项");
        Assert(SectionCount(hekate, EmuNandTitle) == 1, "含虚拟破解系统引导项");

        var stockEntry = ReadSection(hekate, StockTitle);
        Assert(stockEntry.Contains("stock=1"), "正版系统引导项应有 stock=1");
        Assert(stockEntry.Contains("emummc_force_disable=1"), "正版系统引导项应有 emummc_force_disable=1");
        Assert(!stockEntry.Contains("memmode=1"), "正版系统引导项不应带 memmode");

        var sysEntry = ReadSection(hekate, SysNandTitle);
        Assert(sysEntry.Contains("emummc_force_disable=1"), "真实破解系统引导项应有 emummc_force_disable=1");
        Assert(sysEntry.Contains("memmode=1"), "8G 运存时真实破解系统引导项应有 memmode=1");
        Assert(sysEntry.Contains("id=zspjxt"), "真实破解系统引导项应有 id=zspjxt");

        var emuEntry = ReadSection(hekate, EmuNandTitle);
        Assert(emuEntry.Contains("emummcforce=1"), "虚拟破解系统引导项应有 emummcforce=1");
        Assert(emuEntry.Contains("memmode=1"), "8G 运存时虚拟破解系统引导项应有 memmode=1");
        // 用户原值是 xunipjxt（8 字符），超出 hekate 的 7 字符上限，所以生成 7 字符版本
        Assert(emuEntry.Contains("id=xunipjx"), "虚拟破解系统引导项应有 id=xunipjx");

        Assert(!hekate.Contains("kip1patch=nosigchk"), "默认不应写入过时的 kip1patch=nosigchk");

        // nyx.ini
        var nyx = Read("bootloader/nyx.ini");
        Assert(nyx.Contains("jcdisable=0"), "nyx.ini 含 jcdisable");
        Assert(nyx.Contains("jcforceright=0"), "nyx.ini 含 jcforceright");
        Assert(nyx.Contains("bpmpclock=1"), "nyx.ini 含 bpmpclock");

        // sys-patch
        var sysPatch = Read("config/sys-patch/config.ini");
        Assert(sysPatch.Contains("patch_sysmmc=1"), "勾选真实破解系统时 patch_sysmmc 应为 1");
        Assert(sysPatch.Contains("patch_emummc=1"), "勾选虚拟破解系统时 patch_emummc 应为 1");

        // system_settings.ini 的值格式
        var settings = Read("atmosphere/config/system_settings.ini");
        Assert(settings.Contains("upload_enabled=u8!0x0"), "u8 类型值格式正确");
        Assert(settings.Contains("fatal_auto_reboot_interval=u64!0"), "u64 类型值格式正确");
        Assert(settings.Contains("power_menu_reboot_function=str!payload"), "str 类型值格式正确");
    }

    // ── 用户配置基线 ────────────────────────────────────────────
    /// <summary>
    /// 把用户实际在用的 8 份 ini 里的取值，逐项断言成「向导默认值生成出来的结果」。
    ///
    /// 这组断言的作用是**锁死默认值**：用户要求「软件打开后默认就按我的配置勾好」，
    /// 所以任何一处默认值被改回上游默认（或被人手滑改掉），这里都会立刻报警，
    /// 而不是等用户发现「生成出来的 ini 又变回原样了」。
    ///
    /// 注意：不显式赋值 <see cref="WizardOptions.BlankSerialSysmmc"/> /
    /// <see cref="WizardOptions.BlankSerialEmummc"/>，因为要断言的正是「默认就勾上了」。
    /// </summary>
    private static void CheckUserConfigBaseline()
    {
        Section("用户配置基线：向导默认值 == 用户实际使用的 ini");

        var options = new WizardOptions
        {
            Atmosphere = true,
            Hekate = true,
            Ultrahand = true,
            SysPatch = true,
            // 用户 exosphere.ini 里 enable_mem_mode=0，即未开 8G 运存
            Ram8Gb = false,
            BootStock = true,
            BootSysNand = true,
            BootEmuNand = true,
            // 屏蔽序列号的**默认值已经改成不勾**（用户 2026-09-16 要求），但用户实际那份
            // exosphere.ini 里两项都是 1 —— 这一节要验的是「打开屏蔽时生成的 ini 对不对」，
            // 所以这里显式打开，别依赖默认值。「默认不勾」另有 CheckDefaults / CheckFreshUiState 兜着。
            BlankSerialSysmmc = true,
            BlankSerialEmummc = true,
            IncludeComponentFilesInOutput = false,
            IncludePayloads = false,
            IncludeBootDat = false,
            // 程序默认的界面语言。必须显式给 —— 空串是 WizardOptions 的字段默认值，
            // 真实程序里到不了（见 CheckFullScenario 里的同一条说明）。
            Language = "zh-Hans",
        };

        ApplyCatalogDefaults(options);
        Generate(options);

        // ① atmosphere/config/override_config.ini
        var overrides = Read("atmosphere/config/override_config.ini");
        Assert(overrides.Contains("[hbl_config]"), "override_config.ini 应含 [hbl_config] 段");
        Assert(overrides.Contains("program_id=010000000000100D"), "hbl_config 的 program_id 应为 Homebrew Menu 的 title id");
        Assert(overrides.Contains("override_any_app=true"), "hbl_config 应含 override_any_app=true");
        Assert(overrides.Contains("path=atmosphere/hbl.nsp"), "hbl_config 的 path 应为 atmosphere/hbl.nsp");

        var hblCfg = ReadSection(overrides, "hbl_config");
        Assert(hblCfg.Contains("override_key=!R"), "hbl_config 的 override_key 应为 !R");
        Assert(hblCfg.Contains("override_any_app_key=R"), "hbl_config 的 override_any_app_key 应为 R");

        var defaultCfg = ReadSection(overrides, "default_config");
        Assert(defaultCfg.Contains("override_key=!L"), "default_config 的 override_key 默认应为 !L");
        Assert(defaultCfg.Contains("cheat_enable_key=!L"), "default_config 的 cheat_enable_key 默认应为 !L");

        // ② atmosphere/config/system_settings.ini
        var settings = Read("atmosphere/config/system_settings.ini");
        Assert(settings.Contains("usb30_force_enabled=u8!0x1"), "usb30_force_enabled 默认应为 u8!0x1");
        Assert(settings.Contains("upload_enabled=u8!0x0"), "upload_enabled 默认应为 u8!0x0");
        Assert(settings.Contains("ease_nro_restriction=u8!0x0"), "ease_nro_restriction 默认应为 u8!0x0");
        Assert(settings.Contains("enable_sd_card_logging=u8!0x0"), "enable_sd_card_logging 默认应为 u8!0x0");
        Assert(settings.Contains("sd_card_log_output_directory=str!atmosphere/binlogs"),
            "sd_card_log_output_directory 默认应为 str!atmosphere/binlogs");
        Assert(settings.Contains("fatal_auto_reboot_interval=u64!0"), "fatal_auto_reboot_interval 默认应为 u64!0");
        Assert(settings.Contains("power_menu_reboot_function=str!payload"), "power_menu_reboot_function 默认应为 str!payload");
        Assert(settings.Contains("dmnt_cheats_enabled_by_default=u8!0x0"), "dmnt_cheats_enabled_by_default 默认应为 u8!0x0");
        Assert(settings.Contains("dmnt_always_save_cheat_toggles=u8!0x1"), "dmnt_always_save_cheat_toggles 默认应为 u8!0x1");
        Assert(settings.Contains("enable_hbl_bis_write=u8!0x0"), "enable_hbl_bis_write 默认应为 u8!0x0");
        Assert(settings.Contains("enable_hbl_cal_read=u8!0x0"), "enable_hbl_cal_read 默认应为 u8!0x0");
        Assert(settings.Contains("fsmitm_redirect_saves_to_sd=u8!0x0"), "fsmitm_redirect_saves_to_sd 默认应为 u8!0x0");
        Assert(settings.Contains("enable_deprecated_hid_mitm=u8!0x0"), "enable_deprecated_hid_mitm 默认应为 u8!0x0");
        Assert(settings.Contains("enable_am_debug_mode=u8!0x0"), "enable_am_debug_mode 默认应为 u8!0x0");
        // ⚠️ 本节的**唯一一处刻意例外**（用户 2026-09-17 决定 B）：`enable_dns_mitm` 的默认值是
        // **关**，而不是用户那份 system_settings.ini 里的 1。
        // 理由是它与「两个 90DNS 都不勾 ⇒ mitm 关」的联动规则必须**起点一致**：
        // 默认值留 1 的话，界面一打开就是「90DNS 全关、mitm 却开着」——
        // 规则说「都不勾就关」，起点说「开着」，产物照起点写，两边自相矛盾。
        // 所以这条断言**不是**「基线被改坏了」，是基线里被明确推翻的一项；
        // 别按本节注释里「任何一处默认值被改掉都会立刻报警」的口径把它改回 u8!0x1。
        // 配套护栏：Check90DnsSwitches 里「刚打开界面时 mitm 应为关」那条钉着起点一致性。
        Assert(settings.Contains("enable_dns_mitm=u8!0x0"),
            "enable_dns_mitm 默认应为 u8!0x0（用户 2026-09-17 决定 B：与「两个 90DNS 都不勾 ⇒ mitm 关」起点一致）");
        Assert(settings.Contains("add_defaults_to_dns_hosts=u8!0x1"), "add_defaults_to_dns_hosts 默认应为 u8!0x1");
        Assert(settings.Contains("enable_dns_mitm_debug_log=u8!0x0"), "enable_dns_mitm_debug_log 默认应为 u8!0x0");
        Assert(settings.Contains("enable_htc=u8!0x0"), "enable_htc 默认应为 u8!0x0");
        Assert(settings.Contains("enable_log_manager=u8!0x0"), "enable_log_manager 默认应为 u8!0x0");
        Assert(settings.Contains("enable_external_bluetooth_db=u8!0x1"), "enable_external_bluetooth_db 默认应为 u8!0x1");
        Assert(settings.Contains("applet_heap_size=u64!0"), "applet_heap_size 默认应为 u64!0");
        Assert(settings.Contains("applet_heap_reservation_size=u64!0x8600000"),
            "applet_heap_reservation_size 默认应为 u64!0x8600000");

        // ③ bootloader/hekate_ipl.ini 的 [config] 段
        var hekate = Read("bootloader/hekate_ipl.ini");
        var config = ReadSection(hekate, "config");
        Assert(config.Contains("autoboot=0"), "autoboot 默认应为 0（不自动引导）");
        Assert(config.Contains("autoboot_list=0"), "autoboot_list 默认应为 0（从 hekate_ipl.ini 读取）");
        Assert(config.Contains("bootwait=2"), "bootwait 默认应为 2");
        Assert(config.Contains("autohosoff=2"), "autohosoff 默认应为 2");
        Assert(config.Contains("autonogc=0"), "autonogc 默认应为 0");
        Assert(config.Contains("updater2p=1"), "updater2p 默认应为 1");
        Assert(config.Contains("backlight=100"), "backlight 默认应为 100");

        // ④ 三个引导项：pkg3 / id / cal0blank
        var stock = ReadSection(hekate, StockTitle);
        Assert(stock.Contains("pkg3=atmosphere/package3"), "正版系统应使用 pkg3（fss0 已被 hekate 标注弃用）");
        Assert(stock.Contains("stock=1"), "正版系统应有 stock=1");
        Assert(stock.Contains("emummc_force_disable=1"), "正版系统应有 emummc_force_disable=1");
        Assert(stock.Contains("id=zsxt"), "正版系统应有 id=zsxt");
        Assert(!stock.Contains("cal0blank=1"), "正版系统不应写 cal0blank");

        var sysEntry = ReadSection(hekate, SysNandTitle);
        Assert(sysEntry.Contains("pkg3=atmosphere/package3"), "真实破解系统应使用 pkg3");
        Assert(sysEntry.Contains("emummc_force_disable=1"), "真实破解系统应有 emummc_force_disable=1");
        Assert(sysEntry.Contains("id=zspjxt"), "真实破解系统应有 id=zspjxt");
        Assert(sysEntry.Contains("cal0blank=1"), "开了 sysMMC 屏蔽时真实破解系统应写 cal0blank=1（与 exosphere 保持一致）");

        var emuEntry = ReadSection(hekate, EmuNandTitle);
        Assert(emuEntry.Contains("pkg3=atmosphere/package3"), "虚拟破解系统应使用 pkg3");
        Assert(emuEntry.Contains("emummcforce=1"), "虚拟破解系统应有 emummcforce=1");
        Assert(emuEntry.Contains("id=xunipjx"), "虚拟破解系统应有 id=xunipjx（用户原值 8 字符，超出 hekate 上限）");
        Assert(emuEntry.Contains("cal0blank=1"), "开了 emuMMC 屏蔽时虚拟破解系统应写 cal0blank=1");

        // ⑤ config/ovl-sysmodules/config.ini
        var ovl = Read("config/ovl-sysmodules/config.ini");
        Assert(ovl.Contains("[ovl-sysmodules]"), "ovl-sysmodules 配置应含 [ovl-sysmodules] 段");
        Assert(ovl.Contains("powerControlEnabled=1"), "powerControlEnabled 默认应为 1");
        Assert(ovl.Contains("wifiControlEnabled=1"), "wifiControlEnabled 默认应为 1");
        Assert(ovl.Contains("sysmodulesControlEnabled=1"), "sysmodulesControlEnabled 默认应为 1");
        Assert(ovl.Contains("bootFileControlEnabled=0"), "bootFileControlEnabled 默认应为 0");
        Assert(ovl.Contains("hekateRestartControlEnabled=0"), "hekateRestartControlEnabled 默认应为 0");
        Assert(ovl.Contains("consoleRegionControlEnabled=0"), "consoleRegionControlEnabled 默认应为 0");

        // ⑥ config/ultrahand/config.ini
        Assert(Read("config/ultrahand/config.ini").Contains("key_combo=L+DDOWN"),
            "Ultrahand 组合键默认应为用户配置的 L+DDOWN");

        // 用户那份 config.ini 里**没有** default_lang（这是本轮新增的键），所以这里锁的是
        // 「打开软件、默认设置下会写出什么」：界面语言 zh-Hans + 这一项默认「跟随界面语言」
        // ⇒ default_lang=zh-cn。语言包本身不在 samples 里（它属于组件文件，与 package3 同理）。
        Assert(Read("config/ultrahand/config.ini").Contains("default_lang=zh-cn"),
            "Ultrahand 界面语言默认应跟随界面语言（zh-Hans → zh-cn）");

        // ⑦ exosphere.ini
        var exosphere = Read("exosphere.ini");
        Assert(exosphere.Contains("debugmode=1"), "debugmode 应为 1（硬编码，与用户配置一致）");
        Assert(exosphere.Contains("debugmode_user=0"), "debugmode_user 默认应为 0");
        Assert(exosphere.Contains("disable_user_exception_handlers=0"), "disable_user_exception_handlers 默认应为 0");
        Assert(exosphere.Contains("enable_user_pmu_access=0"), "enable_user_pmu_access 默认应为 0");
        Assert(exosphere.Contains("enable_mem_mode=0"), "未勾选 8G 时 enable_mem_mode 应为 0");
        Assert(exosphere.Contains("blank_prodinfo_sysmmc=1"), "开了 sysMMC 屏蔽 → blank_prodinfo_sysmmc=1");
        Assert(exosphere.Contains("blank_prodinfo_emummc=1"), "开了 emuMMC 屏蔽 → blank_prodinfo_emummc=1");
        Assert(exosphere.Contains("allow_writing_to_cal_sysmmc=0"), "allow_writing_to_cal_sysmmc 默认应为 0");
        Assert(exosphere.Contains("log_port=0"), "log_port 应为 0（硬编码）");
        Assert(exosphere.Contains("log_baud_rate=115200"), "log_baud_rate 应为 115200（硬编码）");
        Assert(exosphere.Contains("log_inverted=0"), "log_inverted 应为 0（硬编码）");

        // ⑧ 反向：两个屏蔽项都**不勾**时，生成出来的 ini 必须真的写 0。
        //    只验「勾上写 1」是不够的 —— 取消勾选后如果文件里还留着上一次的 1，
        //    用户会以为关掉了、实际序列号照样被屏蔽。
        var offOptions = CloneFlags(options);
        offOptions.BlankSerialSysmmc = false;
        offOptions.BlankSerialEmummc = false;
        ApplyCatalogDefaults(offOptions);
        Generate(offOptions);

        var offExosphere = Read("exosphere.ini");
        Assert(offExosphere.Contains("blank_prodinfo_sysmmc=0"), "关掉 sysMMC 屏蔽 → blank_prodinfo_sysmmc 应写 0");
        Assert(offExosphere.Contains("blank_prodinfo_emummc=0"), "关掉 emuMMC 屏蔽 → blank_prodinfo_emummc 应写 0");

        var offHekate = Read("bootloader/hekate_ipl.ini");
        Assert(!ReadSection(offHekate, "真实破解系统").Contains("cal0blank=1"),
            "关掉 sysMMC 屏蔽后真实破解系统不该再写 cal0blank=1");
        Assert(!ReadSection(offHekate, "虚拟破解系统").Contains("cal0blank=1"),
            "关掉 emuMMC 屏蔽后虚拟破解系统不该再写 cal0blank=1");
    }

    // ── 配置项落点：声明了就必须真的写出来 ────────────────────────
    /// <summary>
    /// 目录里每个选项都能声明自己的落点（<see cref="OptionDefinition.TargetFile"/>），生成器**按落点遍历**去写。
    /// 所以「声明了一个新落点、却没写对应的写出循环」的后果是：选项在界面上能改、也存进了 `settings.json`，
    /// 生成结果里却**一行都没有** —— 完全静默。
    ///
    /// 现有的「应写入 12 个文件」只是**计数**断言：少写一个文件数量不变，挡不住它。这条把它变成穷举 ——
    /// 声明的落点集合必须被产出的配置文件集合**完全覆盖**，反向也要成立（产出里不该有没人声明的 ini）。
    ///
    /// 复用 <see cref="CheckFullScenario"/> 的产出（<c>_fullResult</c>），不重新生成、不碰 `out/`。
    /// </summary>
    private static void CheckEveryDeclaredTargetFileIsWritten()
    {
        Section("配置项落点：目录里声明的每个 TargetFile 都必须真的被写出来");

        // TargetFile 为 null 是「本就不写进配置文件」的**显式**标记（见 OptionDefinition 的注释），不算漏。
        var declared = ComponentCatalog.All
            .SelectMany(d => d.Options)
            .Where(o => !string.IsNullOrEmpty(o.TargetFile))
            .Select(o => o.TargetFile!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert(declared.Count > 0, $"前置条件：目录里应声明了至少一个 TargetFile，实际 {declared.Count} 个");
        Assert(_fullResult is not null, "前置条件：完整场景应已生成（本用例复用它的产出，不重新生成）");

        // 清单里的路径分隔符与 TargetFile 未必一致（清单历史上用反斜杠），一律归一化后再比。
        static string Normalize(string path) => path.Replace('\\', '/');
        var written = new HashSet<string>(
            _fullResult!.ConfigFiles.Select(Normalize), StringComparer.OrdinalIgnoreCase);

        var missing = declared.Where(f => !written.Contains(f)).ToList();
        Assert(missing.Count == 0,
            "目录里声明的每个 TargetFile 都应真的被写出来（声明了却没写 = 界面上能改、生成结果里没有）"
            + (missing.Count == 0 ? "" : $"；实际没写出的 {missing.Count} 个：" + string.Join("、", missing)));

        // 反向：产出的每份 .ini 都该有选项声明它 —— 否则是写了个没人声明的文件（多半是改名时漏改了一边）。
        var orphan = written
            .Where(f => f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            .Where(f => !declared.Contains(f, StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert(orphan.Count == 0,
            "产出的每份 .ini 都应有目录选项声明它"
            + (orphan.Count == 0 ? "" : "；实际没有来源的：" + string.Join("、", orphan)));
    }

    // 「没写 TargetFile」这一侧的反向护栏。
    // TargetFile 为 null 既是「本就不写进配置文件」的显式标记，也正是「忘了写」的默认值 —— 两者在类型上
    // 无法区分，而 CheckEveryDeclaredTargetFileIsWritten 把 null 全部排除，于是「新增选项时忘了写
    // TargetFile」会静默通过：界面上多一个能改的开关，改了什么也不发生。
    // 判据：TargetFile 为空的选项，其 Key 必须在本项目源码里**至少出现两次** —— 一次是声明，另一次是真正
    // 的消费点（引导项专用代码、picker lambda 里读它……）。
    // 注意：不能按「文件」排除 ComponentCatalog.cs —— 它自己就含有消费点（picker lambda），排除掉会误报。
    private static void CheckEveryDeclaredOptionIsConsumed()
    {
        Section("配置项：没写 TargetFile 的选项必须真的被消费（防「忘了写 TargetFile」）");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到源码目录，无法核对选项 Key 是否被消费");
            return;
        }

        // 只留代码行：注释里出现同一个字符串不算消费，留着会把「只有声明」误判成「已被消费」。
        var codeLines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".xaml"))
            {
                continue;
            }

            var relative = Path.GetRelativePath(sourceRoot, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            codeLines.AddRange(File.ReadAllLines(file)
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal)
                            && !line.StartsWith("*", StringComparison.Ordinal)));
        }

        var allOptions = ComponentCatalog.All.SelectMany(d => d.Options).ToList();
        Assert(allOptions.Count > 0,
            $"前置条件：目录里应声明了选项（当前 {allOptions.Count} 个），否则下面的比对恒真");

        var withoutTarget = allOptions.Where(o => string.IsNullOrEmpty(o.TargetFile)).ToList();
        var onlyDeclared = new List<string>();
        foreach (var option in withoutTarget)
        {
            var literal = "\"" + option.Key + "\"";
            var occurrences = codeLines.Count(line => line.Contains(literal, StringComparison.Ordinal));
            if (occurrences < 2)
            {
                onlyDeclared.Add($"{option.Key}（仅 {occurrences} 处）");
            }
        }

        Assert(onlyDeclared.Count == 0,
            $"没写 TargetFile 的 {withoutTarget.Count} 个选项都必须另有消费点（只有声明没有消费 = 界面上能改、"
            + "改了什么也不发生；若确实要写进文件，请补 TargetFile）"
            + (onlyDeclared.Count == 0 ? "" : "；实际只有声明的：" + string.Join("；", onlyDeclared)));
    }

    // 「Option() 的 Key 拼错」这一侧的护栏。
    // ConfigGenerator 里用 Option(options, definition, "key") 取选项值，实现是：
    //     definition.Options.FirstOrDefault(o => o.Key == key)
    // 找不到就回退 optionDefinition?.DefaultValue ?? string.Empty —— 也就是说 **Key 拼错不报错，
    // 只会静默写出一行 `key=`（空值）**：配置看着生成了，其实那一项是空的。
    // 10 个调用点全是字面量，这类拼写错误此前没有任何东西在守。
    // 判据：每个调用点的 Key 必须声明在「该调用点所在作用域里 definition 指向的那个组件」的目录里。
    // 声明侧直接查 ComponentCatalog（真源），不是手抄清单。
    private static void CheckOptionAccessorKeysAreDeclared()
    {
        Section("配置项取值：Option() 的 Key 必须在其所属组件的目录里声明（防拼错静默写成空值）");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到源码目录，无法核对 Option() 的 Key 是否已声明");
            return;
        }

        const string pattern = "Option(options, definition, \"";
        var callSites = new List<string>();
        var mismatched = new List<string>();
        var unresolved = new List<string>();

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var start = lines[i].IndexOf(pattern, StringComparison.Ordinal);
                if (start < 0)
                {
                    continue;
                }

                var rest = lines[i][(start + pattern.Length)..];
                var end = rest.IndexOf('"');
                if (end <= 0)
                {
                    continue;
                }

                var key = rest[..end];
                var where = $"{Path.GetFileName(file)}:{i + 1} {key}";
                callSites.Add(where);

                // 向上找最近的 `var definition = ComponentCatalog.Get(ComponentKind.X);` —— 它就是
                // 这一行的 Key 必须落在哪个组件的目录里。
                string? owner = null;
                for (var j = i; j >= 0; j--)
                {
                    var match = Regex.Match(lines[j],
                        @"var\s+definition\s*=\s*ComponentCatalog\.Get\(ComponentKind\.(\w+)\)");
                    if (match.Success)
                    {
                        owner = match.Groups[1].Value;
                        break;
                    }
                }

                if (owner is null)
                {
                    unresolved.Add($"{where}（向上找不到 definition = ComponentCatalog.Get(...)）");
                    continue;
                }

                if (!Enum.TryParse<ComponentKind>(owner, out var kind))
                {
                    unresolved.Add($"{where}（无法识别的组件名 {owner}）");
                    continue;
                }

                if (!ComponentCatalog.Get(kind).Options.Any(o => o.Key == key))
                {
                    mismatched.Add($"{where}（{owner} 的目录里没有这个 Key）");
                }
            }
        }

        Assert(callSites.Count > 0,
            "前置条件：源码里应能找到 Option(options, definition, \"…\") 调用点，否则下面的比对恒真");

        Assert(unresolved.Count == 0,
            "每个 Option() 调用点都必须能解析出所属组件（向上找最近的 definition = ComponentCatalog.Get(...)）："
            + string.Join("；", unresolved));

        Assert(mismatched.Count == 0,
            $"Option() 的 {callSites.Count} 个调用点，其 Key 都必须声明在所属组件的目录里"
            + "（否则 Option() 回退成空串，静默写出一行空值）："
            + string.Join("；", mismatched));
    }

    // ── 最小场景 ────────────────────────────────────────────────
    private static void CheckMinimalScenario()
    {
        Section("最小场景：仅 Atmosphere，无引导项、无 90DNS");

        var options = new WizardOptions
        {
            Atmosphere = true,
            Hekate = false,
            Ultrahand = false,
            SysPatch = false,
            BootStock = false,
            BootSysNand = false,
            BootEmuNand = false,
            BlankSerialSysmmc = false,
            BlankSerialEmummc = false,
            Use90DnsSysmmc = false,
            Use90DnsEmummc = false,
            Ram8Gb = false,
            // 只验证配置文件，显式关掉合并（否则断言的文件数会被合并进来的组件本体影响）
            IncludeComponentFilesInOutput = false,
            IncludePayloads = false,
            // 这几个场景只关心配置生成，显式关掉 boot.dat —— 否则新增开关会让
            // 「应写入 N 个文件」这类计数断言跟着漂移，测的就不是原本的意图了。
            IncludeBootDat = false,
        };

        ApplyCatalogDefaults(options);
        var result = Generate(options);

        Assert(result.WrittenFiles.Count == 4, $"应写入 4 个文件，实际 {result.WrittenFiles.Count} 个");
        Assert(!File.Exists(Path.Combine(AppPaths.OutputRoot, "atmosphere", "hosts", "sysmmc.txt")),
            "未勾选 90DNS 时不应生成 hosts 文件");

        var exosphere = Read("exosphere.ini");
        Assert(exosphere.Contains("enable_mem_mode=0"), "未勾选 8G 时 enable_mem_mode 应为 0");
        Assert(exosphere.Contains("blank_prodinfo_sysmmc=0"), "未勾选屏蔽序列号时应为 0");
        Assert(exosphere.Contains("blank_prodinfo_emummc=0"), "未勾选屏蔽序列号时应为 0");
    }

    // ── 打包成 zip ──────────────────────────────────────────────
    private static void CheckPackaging()
    {
        Section("打包 out 目录为 zip");

        // 空目录 / 不存在的目录都不应被当成“有内容”
        Assert(!OutputPackager.HasContent(Path.Combine(AppPaths.BaseDirectory, "no-such-dir")),
            "不存在的目录 HasContent 应为 false");
        Assert(OutputPackager.HasContent(AppPaths.OutputRoot),
            "完整场景跑完后 out 目录应被判定为有内容");

        var zipPath = Path.Combine(AppPaths.BaseDirectory, "pack-test.zip");
        var produced = OutputPackager.Create(AppPaths.OutputRoot, zipPath);
        Assert(produced == zipPath, "应返回调用方指定的压缩包路径");
        Assert(File.Exists(zipPath), "压缩包应真的被创建出来");

        // 读完就立刻关闭：否则下面重复打包时会因为文件被占用而删不掉
        var entries = new List<string>();
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            entries.AddRange(archive.Entries.Select(e => e.FullName.Replace('\\', '/')));
        }

        Assert(entries.Contains("exosphere.ini"), "压缩包根目录应直接包含 exosphere.ini");
        Assert(entries.Contains("bootloader/hekate_ipl.ini"), "应包含 bootloader/hekate_ipl.ini");
        Assert(entries.Contains("atmosphere/config/system_settings.ini"), "应包含 atmosphere/config/system_settings.ini");
        Assert(entries.Contains("atmosphere/hosts/sysmmc.txt"), "应包含 90DNS 的 atmosphere/hosts/sysmmc.txt");
        Assert(!entries.Any(e => e.StartsWith("out/", StringComparison.Ordinal)),
            "压缩包内不应多套一层 out/ 目录，否则解压后无法直接拖进 SD 卡根目录");

        // 目标文件已存在时必须能覆盖，而不是抛 IOException
        var again = OutputPackager.Create(AppPaths.OutputRoot, zipPath);
        Assert(again == zipPath && File.Exists(zipPath), "重复打包应覆盖旧文件而不是报错");

        File.Delete(zipPath);
    }

    // ── 旧压缩包清理 ────────────────────────────────────────────
    private static void CheckArchivePruning()
    {
        Section("反复运行不残留旧压缩包");

        var directory = Path.Combine(AppPaths.BaseDirectory, "archive-prune-test");
        TryDeleteDirectory(directory);
        AppPaths.EnsureDirectory(directory);

        // 造出「早先几次运行留下的压缩包」，外加几个不该被碰的文件
        var stale1 = Path.Combine(directory, "SwitchCFW-20260101-010101.zip");
        var stale2 = Path.Combine(directory, "SwitchCFW-20260102-020202.zip");
        var userRenamed = Path.Combine(directory, "我的配置备份.zip");
        var lookalike = Path.Combine(directory, "SwitchCFW-notatimestamp.zip");
        var foreign = Path.Combine(directory, "other-tool.zip");

        foreach (var path in new[] { stale1, stale2, userRenamed, lookalike, foreign })
        {
            File.WriteAllText(path, "x");
        }

        var current = Path.Combine(directory, "SwitchCFW-20260916-120000.zip");
        OutputPackager.Create(AppPaths.OutputRoot, current);

        Assert(File.Exists(current), "本次生成的压缩包应存在");
        Assert(!File.Exists(stale1), "早先运行留下的压缩包应被清掉（SwitchCFW-20260101-010101.zip）");
        Assert(!File.Exists(stale2), "早先运行留下的压缩包应被清掉（SwitchCFW-20260102-020202.zip）");
        Assert(File.Exists(userRenamed), "用户自己改名保存的压缩包不该被删");
        Assert(File.Exists(lookalike), "形状不符的 SwitchCFW-notatimestamp.zip 不该被删");
        Assert(File.Exists(foreign), "其它工具生成的压缩包不该被删");

        var remaining = Directory.EnumerateFiles(directory, "SwitchCFW-*.zip")
            .Select(Path.GetFileName)
            .OrderBy(name => name)
            .ToList();
        Assert(remaining.Count == 2
               && remaining[0] == "SwitchCFW-20260916-120000.zip"
               && remaining[1] == "SwitchCFW-notatimestamp.zip",
            $"清理后 SwitchCFW-*.zip 应只剩本次生成的这一个（外加形状不符的那份），实际：{string.Join("、", remaining)}");

        TryDeleteDirectory(directory);
    }

    // ── boot.dat（下载来的） ─────────────────────────────────────
    /// <summary>
    /// boot.dat（SX GEAR 引导文件）**不再内置**，改为下载后写到 out 根目录
    /// （用户 2026-09-21 要求：取消内置、从仓库下载、开关从「输出内容」移到「框架」）。
    ///
    /// 这条链路每一环都可能「看起来对、其实没生效」：下载的字节被文本读写损坏、
    /// 写进去了却没进清单、取消勾选后上一轮那份没被带走、下不到却留下一个空文件 ——
    /// 所以逐环断言。
    ///
    /// ⚠️ **本用例刻意不联网**：联网的东西交给 <c>tools/MirrorProbe</c>（手工跑）。
    ///    回归在不该有网络的地方红，是最没价值的一种红。所以这里自己造一份「已下载」的文件，
    ///    只验「生成阶段会不会把它逐字节搬对」；「真能从网上取回来」那一半由探针验。
    /// </summary>
    private static void CheckBootDat()
    {
        Section("下载来的 boot.dat 写到 out 根目录");

        // 造一份「已下载」的 boot.dat。内容刻意是**二进制**（含 0x00 / 0x0A / 0x1A / 0x80 / 0xFF）：
        // 只要链路上有一环走了文本读写（ReadAllText / WriteAllText / 按行处理），字节就会被改坏 ——
        // 而那种损坏在 SD 卡上表现为「开不了机」，日志里却一切正常。
        // 落点必须是 download/boot/（用户要求「下载下来的文件放入 boot 文件夹下」）。
        // 这里刻意用 AppPaths.BootDatRoot 而不是自己拼字符串：产品代码换了目录时，
        // 这条用例要么跟着走、要么在上面那条路径断言上直接红 —— 而不是「两边都改了、测试还说通过」。
        Assert(AppPaths.BootDatRoot == Path.Combine(AppPaths.DownloadRoot, "boot"),
            $"boot.dat 的下载目录应当是 <运行目录>/download/boot，实际「{AppPaths.BootDatRoot}」");

        var downloaded = Path.Combine(AppPaths.BootDatRoot, BootDatSource.FileName);

        // ⚠️ 夹具要自己把前提摆好：产品代码那条路是 DownloadService 建目录，而这里直接写文件。
        // 只建 download/ 不建 download/boot/ 的话，写文件会抛 DirectoryNotFoundException，
        // 整条回归**未处理崩溃**（不是一条红断言 —— 那种失败最难读）。
        AppPaths.EnsureDirectory(AppPaths.BootDatRoot);

        var payload = new byte[11520];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = i switch
            {
                0 => 0x00,
                1 => 0x0A,
                2 => 0x1A,
                3 => 0x80,
                _ => (byte)(i % 251),
            };
        }

        payload[^1] = 0xFF;
        File.WriteAllBytes(downloaded, payload);

        // 前置条件：造出来的东西字节数是对的（否则下面「逐字节一致」可能掩盖写入被截断的问题）
        Assert(File.ReadAllBytes(downloaded).Length == 11520, "前置条件：造出来的下载结果应是 11520 字节");

        // 1) 默认值：两边都**不勾选**（用户 2026-09-21 明确要求「boot.dat 修改为默认不勾选」）。
        //    「取消内置」与「默认关掉」是两件事：前者说的是它从哪来，后者说的是它要不要被摆进去。
        Assert(!new AppSettings().IncludeBootDat, "AppSettings.IncludeBootDat 应默认**不**勾选");
        Assert(!new WizardOptions().IncludeBootDat, "WizardOptions.IncludeBootDat 应默认**不**勾选");

        var bootDatPath = Path.Combine(AppPaths.OutputRoot, "boot.dat");

        // 让「完整场景」也带上它，与真实运行时一致（否则最后一轮还原出来的 out 里会少一个文件）
        _fullOptions!.BootDatPath = downloaded;

        // 2) 勾上 → 把**下载好的那份**逐字节搬到 out 根目录，进清单但不进 ConfigFiles
        var on = CloneFlags(_fullOptions!);
        on.IncludeBootDat = true;
        var onResult = Generate(on);

        Assert(File.Exists(bootDatPath), "勾选后 out/boot.dat 应存在");
        Assert(File.ReadAllBytes(bootDatPath).SequenceEqual(payload),
            "写出的 boot.dat 应与下载到的逐字节一致（走文本写出会被 CRLF / 编码归一化损坏）");
        Assert(onResult.WrittenFiles.Contains("boot.dat"), "boot.dat 应写进清单，否则打包校验会漏掉它");
        Assert(!onResult.ConfigFiles.Contains("boot.dat"),
            "boot.dat 不是 ini/hosts，不该算进「配置文件齐全，共 N 个」");

        // 3) 没勾 → 不该出现；上一轮那份要被清单清理带走
        var off = CloneFlags(_fullOptions!);
        off.IncludeBootDat = false;
        var offResult = Generate(off);

        Assert(!File.Exists(bootDatPath), "取消勾选后 out/boot.dat 不该存在（上一轮那份应被清单清理带走）");
        Assert(!offResult.WrittenFiles.Contains("boot.dat"), "取消勾选后清单里不该有 boot.dat");

        // 4) 勾了但**没下到**（BootDatPath 为空）→ 跳过，且**不许**留一个空文件。
        //    这一条是本节最关键的：写一个 0 字节的 boot.dat 比不写更坏 ——
        //    它看起来「有」，而 SD 卡启动时会失败，用户还找不到是哪个文件的问题。
        var missing = CloneFlags(_fullOptions!);
        missing.IncludeBootDat = true;
        missing.BootDatPath = null;
        Generate(missing);

        Assert(!File.Exists(bootDatPath),
            "勾了但没下到时，不许在 out 里留一个空的 boot.dat（那会把「少了文件」变成「文件是坏的」）");

        // 5) 源文件路径存在但文件不在（下载失败留下的正是这种状态）→ 同样跳过，不抛异常中断整轮
        missing.BootDatPath = Path.Combine(AppPaths.DownloadRoot, "not-there-boot.dat");
        Generate(missing);
        Assert(!File.Exists(bootDatPath), "源文件不存在时应跳过，而不是抛异常把整轮生成带崩");

        // 把 out/ 还原成完整场景：后面的压缩包清单比对要拿 _fullResult 的清单去核对
        Generate(_fullOptions!);

        // ⚠️ 造出来的那份「下载结果」**刻意留着**，不删：Main 最后还会用 _fullOptions 生成一遍
        //    （那是留在 out/ 里供人翻看、也是 samples/ 的来源），删了就会少一个文件、
        //    并在日志里多一条「没有可用的 boot.dat」。而 download/ 本来就是运行期目录。
    }

    // ── Ultrahand 呼出组合键 ────────────────────────────────────
    private static void CheckUltrahandConfig()
    {
        Section("Ultrahand config/ultrahand/config.ini");

        var options = new WizardOptions
        {
            Atmosphere = false,
            Hekate = false,
            Ultrahand = true,
            SysPatch = false,
            // 只验证 config.ini 内容，显式关掉合并
            IncludeComponentFilesInOutput = false,
            IncludePayloads = false,
            // 这几个场景只关心配置生成，显式关掉 boot.dat —— 否则新增开关会让
            // 「应写入 N 个文件」这类计数断言跟着漂移，测的就不是原本的意图了。
            IncludeBootDat = false,
        };

        ApplyCatalogDefaults(options);
        Generate(options);

        var ini = Read("config/ultrahand/config.ini");
        Assert(ini.Contains("[ultrahand]"), "应写入 [ultrahand] 段（官方 section 名）");
        Assert(ini.Contains("key_combo=L+DDOWN"), "默认组合键应为用户配置的 L+DDOWN");

        // 换一个合法组合
        options.Set(ComponentKind.Ultrahand, "key_combo", "L+DDOWN+RS");
        Generate(options);
        Assert(Read("config/ultrahand/config.ini").Contains("key_combo=L+DDOWN+RS"), "自定义组合键应被写入");

        // 小写 + 空格也应被规范化
        options.Set(ComponentKind.Ultrahand, "key_combo", " zl + zr + dup ");
        Generate(options);
        Assert(Read("config/ultrahand/config.ini").Contains("key_combo=ZL+ZR+DUP"), "应规范化为大写且去掉空格");

        // 非法组合键必须回退，而不是把坏值写进 SD 卡
        options.Set(ComponentKind.Ultrahand, "key_combo", "ZL+BOGUS+DUP");
        Generate(options);
        Assert(Read("config/ultrahand/config.ini").Contains("key_combo=L+DDOWN"),
            "含非法按键时应回退为默认组合键");

        options.Set(ComponentKind.Ultrahand, "key_combo", "ZL");
        Generate(options);
        Assert(Read("config/ultrahand/config.ini").Contains("key_combo=L+DDOWN"),
            "只给 1 个按键时应回退为默认组合键");

        options.Set(ComponentKind.Ultrahand, "key_combo", "ZL+ZR+DUP+DDOWN+DLEFT");
        Generate(options);
        Assert(Read("config/ultrahand/config.ini").Contains("key_combo=L+DDOWN"),
            "超过 4 个按键时应回退为默认组合键");

        options.Set(ComponentKind.Ultrahand, "key_combo", "ZL+ZL+DUP");
        Generate(options);
        Assert(Read("config/ultrahand/config.ini").Contains("key_combo=L+DDOWN"),
            "按键重复时应回退为默认组合键");

        // NormalizeKeyCombo 的直接单元断言
        Assert(ComponentCatalog.NormalizeKeyCombo("MINUS+PLUS") == "MINUS+PLUS", "MINUS+PLUS 应合法");
        Assert(ComponentCatalog.NormalizeKeyCombo("ls+rs") == "LS+RS", "小写应被规范化");
        Assert(ComponentCatalog.NormalizeKeyCombo("SL+SR") == "SL+SR", "SL+SR 应合法");
        Assert(ComponentCatalog.NormalizeKeyCombo("") is null, "空值应判为非法");
        Assert(ComponentCatalog.NormalizeKeyCombo("HOME+A") is null, "HOME 不在按键令牌表里，应判为非法");
        Assert(ComponentCatalog.NormalizeKeyCombo("A") is null, "单个按键应判为非法");

        // 默认值本身必须能通过校验，且必须是官方预设之一
        Assert(ComponentCatalog.NormalizeKeyCombo(ComponentCatalog.DefaultKeyCombo) == ComponentCatalog.DefaultKeyCombo,
            "默认组合键自身必须合法");

        var comboOption = ComponentCatalog.Get(ComponentKind.Ultrahand).Options
            .First(o => o.Key == "key_combo");
        Assert(comboOption.Choices is { Count: 28 }, $"候选项应为官方 28 个预设，实际 {comboOption.Choices?.Count}");
        Assert(comboOption.Choices!.All(c => ComponentCatalog.NormalizeKeyCombo(c.Value) is not null),
            "每个候选项都必须能通过校验");
        Assert(comboOption.Choices!.All(c => c.DisplayText is not null),
            "候选项应直接显示组合键文本，不查多语言表");

        // ── [memory] 段：custom_overlay_memory_MB ──────────────────
        // 上游 source/main.cpp 的规则是「纯数字 && >8 && 偶数」，不满足就整条忽略。
        options.Set(ComponentKind.Ultrahand, "key_combo", ComponentCatalog.DefaultKeyCombo);

        // 默认（空值）不应产生 [memory] 段——否则会凭空给用户加一档内存选项
        Generate(options);
        Assert(!Read("config/ultrahand/config.ini").Contains("[memory]"),
            "未选择自定义内存时不应写出 [memory] 段");

        // 合法值：10 / 12 / 14 / 16
        foreach (var mb in new[] { "10", "12", "14", "16" })
        {
            options.Set(ComponentKind.Ultrahand, "custom_overlay_memory_MB", mb);
            Generate(options);
            var text = Read("config/ultrahand/config.ini");
            Assert(text.Contains("[memory]"), $"{mb} MB 应写出 [memory] 段");
            Assert(text.Contains($"custom_overlay_memory_MB={mb}"), $"{mb} MB 应原样写入");
        }

        // 非法值必须整段跳过，而不是写进去一个 Ultrahand 根本不认的值
        foreach (var bad in new[] { "8", "9", "7", "0", "-12", "12.0", "12 MB", "abc", "1e2", " " })
        {
            options.Set(ComponentKind.Ultrahand, "custom_overlay_memory_MB", bad);
            Generate(options);
            Assert(!Read("config/ultrahand/config.ini").Contains("[memory]"),
                $"「{bad}」不合法（需 >8 的偶数），不应写出 [memory] 段");
        }

        // 目录里的候选项必须都是合法的（空值除外），且不设重复
        var memoryOption = ComponentCatalog.Get(ComponentKind.Ultrahand).Options
            .First(o => o.Key == "custom_overlay_memory_MB");
        Assert(memoryOption.Section == "memory", "段名必须是官方常量 MEMORY_STR = \"memory\"");
        Assert(memoryOption.TargetFile == "config/ultrahand/config.ini", "应写入 Ultrahand 的 config.ini");
        Assert(memoryOption.DefaultValue.Length == 0, "默认值应为空串（表示不设置）");
        Assert(memoryOption.Choices is { Count: 5 }, $"候选项应为「不设置 + 4 档」，实际 {memoryOption.Choices?.Count}");

        var memoryValues = memoryOption.Choices!
            .Where(c => c.Value.Length > 0)
            .Select(c => int.Parse(c.Value))
            .ToList();
        Assert(memoryValues.All(v => v > 8 && v % 2 == 0), "所有非空候选项都必须满足 >8 且为偶数");
        Assert(memoryValues.Distinct().Count() == memoryValues.Count, "候选项不应重复");
        Assert(memoryOption.Choices!.First().Value.Length == 0, "第一项应为「不设置」");
    }

    // ── Ultrahand default_lang ───────────────────────────────────
    /// <summary>
    /// Ultrahand <c>default_lang</c>：跟随界面语言、用户可覆盖、语言包缺失要告警。
    ///
    /// 上游依据（<c>source/main.cpp</c> 与 libultrahand）：
    /// <list type="bullet">
    /// <item>键名 <c>DEFAULT_LANG_STR = "default_lang"</c>，段名 <c>ULTRAHAND_PROJECT_NAME = "ultrahand"</c>；</item>
    /// <item>取值域写死在 <c>defaultLanguages</c> 里（14 个），正是 <c>lang.zip</c> 里 14 个 json 的文件名；</item>
    /// <item>上游拿它拼 <c>config/ultrahand/lang/&lt;代码&gt;.json</c>，文件不存在时该语言在它自己的设置里
    ///       会被**跳过**，于是「配置说中文、卡上没有中文包」会静默退回编译进去的英文。</item>
    /// </list>
    ///
    /// 最后一条是本用例的重点：向导侧不做兜底，而是让产物层面的不一致变成一条明确告警。
    /// </summary>
    private static void CheckUltrahandDefaultLang()
    {
        Section("Ultrahand default_lang：跟随界面语言 + 用户可覆盖 + 语言包缺失要告警");

        var definition = ComponentCatalog.Get(ComponentKind.Ultrahand);
        var option = definition.Options.FirstOrDefault(o => o.Key == "default_lang");

        Assert(option is not null, "Ultrahand 目录里应声明 default_lang 选项");
        if (option is null)
        {
            return;
        }

        // ① 落点契约
        Assert(option.TargetFile == "config/ultrahand/config.ini",
            $"default_lang 应写入 config/ultrahand/config.ini（实际 {option.TargetFile}）");
        Assert(option.Section == "ultrahand",
            $"段名必须是上游常量 ULTRAHAND_PROJECT_NAME = \"ultrahand\"（实际 {option.Section}）");
        Assert(option.Editor == OptionEditor.ComboBox, "default_lang 应是下拉框");
        Assert(option.DefaultValue == ComponentCatalog.AutoLanguageValue,
            "默认值应是「跟随界面语言」哨兵，而不是某个具体语言（否则改界面语言后两者会脱节）");

        // ② 取值域：**集合**比对，不是计数 —— 「有 15 项」说明不了「就是那 15 项」。
        //    真源是 ComponentCatalog.UltrahandLanguageCodes（与上游 defaultLanguages 逐字对应）。
        var choices = option.Choices ?? [];
        var expected = new HashSet<string>(ComponentCatalog.UltrahandLanguageCodes, StringComparer.Ordinal)
        {
            ComponentCatalog.AutoLanguageValue,
        };
        var actual = choices.Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

        Assert(actual.SetEquals(expected),
            "候选项应恰好是「哨兵 + 14 个上游语言代码」"
            + $"（缺：{string.Join("、", expected.Except(actual))}；多：{string.Join("、", actual.Except(expected))}）");
        Assert(ComponentCatalog.UltrahandLanguageCodes.Count == 14,
            $"上游 defaultLanguages 共 14 项，实际 {ComponentCatalog.UltrahandLanguageCodes.Count} 项");

        // 语言名按惯例用**该语言自己的写法**，不走多语言表（与软件自己的语言下拉框同一套做法）
        Assert(choices.All(c => c.DisplayText is not null || c.Value == ComponentCatalog.AutoLanguageValue),
            "语言候选项应直接显示语言自称（DisplayText）；只有「跟随界面语言」走多语言表");

        // ③ 界面语言 → 语言代码：把界面语言**枚举**一遍逐个断言。写死那三个的话，
        //    将来往 lang/ 里加一份界面语言包就漏了。
        Assert(LocalizationService.Instance.Languages.Count > 0, "前置条件：应有界面语言清单");
        foreach (var language in LocalizationService.Instance.Languages)
        {
            var mapped = ComponentCatalog.MapUiLanguageToUltrahand(language.Code);
            Assert(ComponentCatalog.UltrahandLanguageCodes.Contains(mapped, StringComparer.Ordinal),
                $"界面语言 {language.Code} 应映射到上游认得的代码（实际 {mapped}）");
        }

        // 映射本身也要点名：中文必须按书写系统分 —— 简繁是两套互相独立的语言包
        Assert(ComponentCatalog.MapUiLanguageToUltrahand("zh-Hans") == "zh-cn", "zh-Hans 应映射到 zh-cn");
        Assert(ComponentCatalog.MapUiLanguageToUltrahand("zh-Hant") == "zh-tw", "zh-Hant 应映射到 zh-tw");
        Assert(ComponentCatalog.MapUiLanguageToUltrahand("en-US") == "en", "en-US 应映射到 en");
        Assert(ComponentCatalog.MapUiLanguageToUltrahand("ja-JP") == "ja",
            "其余语言按主语言子标签匹配（ja-JP → ja）");
        Assert(ComponentCatalog.MapUiLanguageToUltrahand("kl") == ComponentCatalog.FallbackLanguageCode,
            "认不出的界面语言应退回 en（上游自己的默认值）");
        Assert(ComponentCatalog.MapUiLanguageToUltrahand(null) == ComponentCatalog.FallbackLanguageCode,
            "空界面语言应退回 en");

        var options = new WizardOptions
        {
            Ultrahand = true,
            IncludeComponentFilesInOutput = false,
            IncludePayloads = false,
            IncludeBootDat = false,
            Language = "zh-Hans",
        };

        ApplyCatalogDefaults(options);

        // ④ 生成侧：哨兵 → 跟随界面语言
        foreach (var (ui, expectedCode) in new[] { ("zh-Hans", "zh-cn"), ("zh-Hant", "zh-tw"), ("en-US", "en") })
        {
            var scenario = CloneFlags(options);
            scenario.Language = ui;
            scenario.Set(ComponentKind.Ultrahand, "default_lang", ComponentCatalog.AutoLanguageValue);
            Generate(scenario);

            var text = Read("config/ultrahand/config.ini");
            Assert(ReadIniValue(text, "default_lang") == expectedCode,
                $"界面语言 {ui} + 「跟随界面语言」应写出 default_lang={expectedCode}"
                + $"（实际 {Describe(ReadIniValue(text, "default_lang") ?? string.Empty)}）");
            Assert(!text.Contains(ComponentCatalog.AutoLanguageValue, StringComparison.Ordinal),
                "哨兵值绝不能出现在 ini 里 —— Ultrahand 会拿它当文件名去找语言包");
        }

        // ⑤ 用户显式覆盖：选了 ja 之后，换界面语言也不该被改回（否则「允许用户切换」形同虚设）
        foreach (var ui in new[] { "zh-Hans", "zh-Hant", "en-US" })
        {
            var scenario = CloneFlags(options);
            scenario.Language = ui;
            scenario.Set(ComponentKind.Ultrahand, "default_lang", "ja");
            Generate(scenario);

            Assert(ReadIniValue(Read("config/ultrahand/config.ini"), "default_lang") == "ja",
                $"显式选了 ja，界面语言为 {ui} 时也应保持 ja");
        }

        // 大小写不敏感：手写 ZH-CN 应被规范化，而不是被判成非法值
        var upperCase = CloneFlags(options);
        upperCase.Set(ComponentKind.Ultrahand, "default_lang", "ZH-CN");
        Generate(upperCase);
        Assert(ReadIniValue(Read("config/ultrahand/config.ini"), "default_lang") == "zh-cn",
            "大写 ZH-CN 应被规范化成 zh-cn");

        // ⑥ 非法值：回退到界面语言对应的代码，且坏值绝不落进 ini
        foreach (var bad in new[] { "klingon", "zh", "en_US", "ja-JP", "简体中文", "auto1", "EN-US" })
        {
            var scenario = CloneFlags(options);
            scenario.Language = "zh-Hant";
            scenario.Set(ComponentKind.Ultrahand, "default_lang", bad);
            Generate(scenario);

            var value = ReadIniValue(Read("config/ultrahand/config.ini"), "default_lang");
            Assert(value == "zh-tw", $"「{bad}」不是合法语言代码，应回退为界面语言对应的 zh-tw（实际 {value}）");
        }

        Assert(ComponentCatalog.IsKnownUltrahandLanguage("zh-tw"), "zh-tw 应被认作合法代码");
        Assert(!ComponentCatalog.IsKnownUltrahandLanguage(ComponentCatalog.AutoLanguageValue),
            "哨兵不是语言代码 —— 把它当合法值的话，「存档里是 auto」会被误判成用户显式选了某个语言");
        Assert(!ComponentCatalog.IsKnownUltrahandLanguage("zh"), "zh 不在上游取值域里，应判为非法");

        // ⑦ 校验器：default_lang 指向的语言包必须在产物里真的存在（上游缺文件就静默退回英文）。
        //    两个方向都钉住 —— 只断言「缺文件要告警」的话，一个「永远告警」的实现照样能过。
        var warnScenario = CloneFlags(options);
        warnScenario.Language = "zh-Hans";
        warnScenario.Set(ComponentKind.Ultrahand, "default_lang", ComponentCatalog.AutoLanguageValue);
        var warnResult = Generate(warnScenario);

        var warnItem = OutputValidator.Validate(warnScenario, AppPaths.OutputRoot, warnResult)
            .Items.FirstOrDefault(i => i.Key == "Check.Warn.DefaultLangNoLangFile");
        Assert(warnItem is { Level: ValidationLevel.Warning },
            "default_lang=zh-cn 而 out 里没有 config/ultrahand/lang/zh-cn.json 时，应给出告警");
        Assert(warnItem?.Detail == "zh-cn", $"告警应指明是哪个语言（实际 {warnItem?.Detail}）");

        // 对照实验：把语言文件放进去 → 同一条告警必须消失
        var langDir = Path.Combine(AppPaths.OutputRoot, "config", "ultrahand", "lang");
        var langFile = Path.Combine(langDir, "zh-cn.json");
        Directory.CreateDirectory(langDir);
        File.WriteAllText(langFile, "{}");
        try
        {
            Assert(!OutputValidator.Validate(warnScenario, AppPaths.OutputRoot, warnResult)
                    .Items.Any(i => i.Key == "Check.Warn.DefaultLangNoLangFile"),
                "语言文件在位时不该再报这条告警（否则它就是个恒真断言）");
        }
        finally
        {
            File.Delete(langFile);
            if (Directory.Exists(langDir) && Directory.GetFileSystemEntries(langDir).Length == 0)
            {
                Directory.Delete(langDir);
            }
        }

        // en 不依赖语言包（英文文案编译在 ovlmenu.ovl 里），没有文件也不该告警
        var enScenario = CloneFlags(options);
        enScenario.Language = "en-US";
        enScenario.Set(ComponentKind.Ultrahand, "default_lang", ComponentCatalog.AutoLanguageValue);
        var enResult = Generate(enScenario);
        Assert(!OutputValidator.Validate(enScenario, AppPaths.OutputRoot, enResult)
                .Items.Any(i => i.Key == "Check.Warn.DefaultLangNoLangFile"),
            "en 不依赖语言包，不该报这条告警");

        // 没勾 Ultrahand 时这一项根本不生成，同样不该报
        var offScenario = CloneFlags(options);
        offScenario.Ultrahand = false;
        offScenario.Language = "zh-Hans";
        var offResult = Generate(offScenario);
        Assert(!OutputValidator.Validate(offScenario, AppPaths.OutputRoot, offResult)
                .Items.Any(i => i.Key == "Check.Warn.DefaultLangNoLangFile"),
            "没勾 Ultrahand 时不该报这条告警");

        // ⑧ 界面软联动
        CheckLangPackCoupling();
    }

    /// <summary>
    /// 界面软联动：<c>default_lang</c> 解析结果非 en ⇒ 程序自动勾上「安装语言包」。
    ///
    /// 为什么必须有它：语言包默认**不装**，而「跟随界面语言」在中文界面下解析出来就是 zh-cn，
    /// 于是默认配置会生成一张「配置说中文、卡上没有中文包」的卡 —— 正好是这条联动要消灭的状态。
    ///
    /// ⚠️ 必须带**对照实验**：「非 en 时勾上了」这句话，一个「无脑勾上」的实现也能满足。
    ///    所以下面四条要一起看：中文时勾上 / 英文时不勾（对照）/ 显式选 en 时不勾 / 用户取消后不被改回。
    /// </summary>
    private static void CheckLangPackCoupling()
    {
        Section("default_lang ↔ 安装语言包：软联动（自动勾上，用户可取消）");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            LocalizationService.Instance.SetLanguage("zh-Hans");

            var vm = new MainViewModel();
            var ultrahand = vm.Components.First(c => c.Kind == ComponentKind.Ultrahand);
            var installLang = ultrahand.FindOption(MainViewModel.InstallLangOptionKey);
            var defaultLang = ultrahand.FindOption(MainViewModel.DefaultLangOptionKey);

            Assert(installLang is not null && defaultLang is not null,
                "前置条件：Ultrahand 上应有 default_lang 与 installLang 两个选项，否则下面的断言恒真");
            if (installLang is null || defaultLang is null)
            {
                return;
            }

            Assert(installLang.Value == "0",
                "前置条件：全新安装、且一个组件都没勾时，语言包应停在默认的「不装」"
                + "（用户还没决定装不装 Ultrahand，不该替他勾安装项）");

            // 勾上 Ultrahand → default_lang 默认「跟随界面语言」→ 中文界面解析出 zh-cn → 补勾语言包
            ultrahand.IsSelected = true;

            Assert(defaultLang.Value == ComponentCatalog.AutoLanguageValue,
                "前置条件：default_lang 应停在「跟随界面语言」上（下面几条才说明得了问题）");
            Assert(installLang.Value == "1",
                "中文界面下勾上 Ultrahand，应自动补勾「安装语言包」（否则 default_lang=zh-cn 是空转）");

            // 用户可自行取消 —— 软联动，不锁定
            installLang.Value = "0";
            Assert(installLang.Value == "0", "用户应能自行取消「安装语言包」（软联动，不是硬约束）");

            // 切到英文界面：default_lang 仍是「跟随」，但解析结果变成 en（不依赖语言包）→ 不该勾回来
            vm.SelectedLanguage = vm.Languages.First(l => l.Code == "en-US");
            Assert(installLang.Value == "0",
                "切到英文后 default_lang 解析成 en，不该把用户取消掉的勾重新勾上");

            // 对照实验：中文界面 + 用户显式选 en → 同样不该勾
            vm.SelectedLanguage = vm.Languages.First(l => l.Code == "zh-Hans");
            defaultLang.Value = "en";
            Assert(installLang.Value == "0",
                "显式把 default_lang 选成 en 时不依赖语言包，不该自动勾上（对照实验）");

            // 改回「跟随界面语言」，界面是中文 → 应再次补勾（证明钩子真的挂在选项值变化上）
            defaultLang.Value = ComponentCatalog.AutoLanguageValue;
            Assert(installLang.Value == "1",
                "改回「跟随界面语言」且界面是中文时，应再次补勾语言包");

            // 对照实验：全新安装 + 英文界面 → 解析成 en → 不勾
            SettingsStore.Save(new AppSettings { Language = "en-US" });
            LocalizationService.Instance.SetLanguage("en-US");

            var enVm = new MainViewModel();
            var enUltrahand = enVm.Components.First(c => c.Kind == ComponentKind.Ultrahand);
            enUltrahand.IsSelected = true;

            Assert(enUltrahand.FindOption(MainViewModel.InstallLangOptionKey)?.Value == "0",
                "英文界面下 default_lang 解析成 en，不该自动勾上语言包（对照实验）");

            // 载入归一化：存档里就是「Ultrahand 已勾 + default_lang 跟随 + 中文界面 + 语言包没勾」
            // （例如用户升级上来），构造函数也必须把它补上，而不是等用户碰一下选项才生效。
            SettingsStore.Save(new AppSettings
            {
                Language = "zh-Hans",
                Components = new Dictionary<string, bool>(StringComparer.Ordinal)
                {
                    [nameof(ComponentKind.Ultrahand)] = true,
                },
            });
            LocalizationService.Instance.SetLanguage("zh-Hans");

            var loaded = new MainViewModel();
            Assert(loaded.Components.First(c => c.Kind == ComponentKind.Ultrahand)
                    .FindOption(MainViewModel.InstallLangOptionKey)?.Value == "1",
                "存档里 Ultrahand 已勾 + 中文界面时，载入后应把语言包补上（载入归一化）");
        }
        finally
        {
            LocalizationService.Instance.SetLanguage("zh-Hans");

            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    // ── 生成结果完整性校验 ──────────────────────────────────────
    private static void CheckValidation()
    {
        Section("生成结果完整性校验");

        var options = _fullOptions!;
        var result = _fullResult!;

        var report = OutputValidator.Validate(options, AppPaths.OutputRoot, result);

        Assert(report.ErrorCount == 0, $"完整场景不应有校验错误，实际 {report.ErrorCount} 项");
        Assert(report.Items.Any(i => i.Key == "Check.Ok.ConfigFiles" && i.Level == ValidationLevel.Ok),
            "应报告「配置文件齐全」");
        Assert(report.Items.Any(i => i.Key == "Check.Ok.Hosts" && i.Level == ValidationLevel.Ok),
            "勾选了 90DNS，应报告 hosts 文件齐全");
        Assert(report.Items.Any(i => i.Key == "Check.Warn.MergeOff" && i.Level == ValidationLevel.Warning),
            "勾了组件但没开合并时，应提醒「只拷 out 不够用」");
        Assert(!report.Items.Any(i => i.Key == "Check.Error.Package3"),
            "没开合并时不应报 package3 缺失（那是另一个场景的问题）");

        // 开了合并、但 download 下没有解压目录 → 必须报错，而不是静默通过
        options.IncludeComponentFilesInOutput = true;
        var mergeOnReport = OutputValidator.Validate(options, AppPaths.OutputRoot, result);
        options.IncludeComponentFilesInOutput = false;

        Assert(mergeOnReport.Items.Any(i => i.Key == "Check.Error.MergedNothing" && i.Level == ValidationLevel.Error),
            "开了合并却一个文件都没合并进来时应报错");
        Assert(!mergeOnReport.IsHealthy, "上述情况不应被判定为健康");

        // 清单里记录了、磁盘上却没有的文件必须被抓出来
        var broken = new ConfigGenerationResult();
        broken.WrittenFiles.Add("exosphere.ini");
        broken.WrittenFiles.Add("并不存在的文件.ini");

        var brokenReport = OutputValidator.Validate(options, AppPaths.OutputRoot, broken);
        var missingItem = brokenReport.Items.FirstOrDefault(i => i.Key == "Check.Error.MissingFile");
        Assert(missingItem is not null && missingItem.Level == ValidationLevel.Error,
            "清单里的文件不存在时应报错");
        Assert(missingItem!.Detail.Contains("并不存在的文件.ini"), "错误信息应指出具体是哪个文件");
        Assert(!missingItem.Detail.Contains("exosphere.ini"), "真实存在的文件不应被误报");

        // Hekate 勾了却没勾引导项
        var noEntry = CloneFlags(options);
        noEntry.BootStock = false;
        noEntry.BootSysNand = false;
        noEntry.BootEmuNand = false;
        Assert(OutputValidator.Validate(noEntry, AppPaths.OutputRoot, result)
                .Items.Any(i => i.Key == "Check.Warn.NoBootEntry"),
            "勾了 Hekate 却没有引导项时应给出提醒");
    }

    // ── 压缩包清单比对 ──────────────────────────────────────────
    private static void CheckZipManifest()
    {
        Section("压缩包与清单比对");

        var result = _fullResult!;
        var zipPath = Path.Combine(AppPaths.BaseDirectory, "verify-test.zip");

        OutputPackager.Create(AppPaths.OutputRoot, zipPath);

        var missing = OutputValidator.FindMissingInZip(zipPath, result.WrittenFiles);
        Assert(missing.Count == 0,
            $"压缩包应包含清单中的全部 {result.WrittenFiles.Count} 个文件，缺少：{string.Join("、", missing)}");

        var probe = OutputValidator.FindMissingInZip(zipPath, new[] { "exosphere.ini", "并不存在的文件.ini" });
        Assert(probe.Count == 1 && probe[0] == "并不存在的文件.ini",
            $"应只报出真正缺失的那个文件，实际：{string.Join("、", probe)}");

        File.Delete(zipPath);
    }

    // ── 产物落点（六个「文件错位」问题的护栏）──────────────────
    /// <summary>
    /// 逐条盯住上游的目录契约：包内结构、裸文件落点、payload 双落点、以及
    /// 「out/ 里不该多出 sdout/ / nx-ovlloader/ 这一层」。
    ///
    /// 关键手法：**落点声明取自真实的 picker**（喂假 ReleaseInfo），而不是在测试里手写。
    /// 手写的话，catalog 里把落点写错测试照样过 —— 那正是这次要防的错。
    /// </summary>
    private static void CheckOutputStaging()
    {
        Section("产物落点：按上游目录契约摆放，不多套一层、不乱平铺");

        // 1) OutputTarget 的路径拼接
        Assert(new OutputTarget(string.Empty).Resolve("a.bin") == "a.bin",
            "落点目录为空时应就是原文件名");
        Assert(new OutputTarget("bootloader/payloads").Resolve("fusee.bin") == "bootloader/payloads/fusee.bin",
            "目录 + 原文件名应拼成相对路径");
        Assert(new OutputTarget(string.Empty, "payload.bin").Resolve("hekate_ctcaer_6.2.1.bin") == "payload.bin",
            "改名后应落在 out 根目录");
        Assert(new OutputTarget("bootloader", "update.bin").Resolve("hekate_ctcaer_6.2.1.bin") == "bootloader/update.bin",
            "改名与子目录应同时生效");
        Assert(new OutputTarget("switch/.overlays/").Resolve("x.ovl") == "switch/.overlays/x.ovl",
            "目录带尾斜杠时不应拼出双斜杠");

        // 2) 压缩包声明多个落点属于目录写错 —— 静默取第一个只会让错误藏起来
        var twoTargetZip = new AssetPick(
            new ReleaseAsset("bad.zip", "https://example.invalid/bad.zip", 1),
            Targets:
            [
                new OutputTarget("a"),
                new OutputTarget("b"),
            ]);

        var rejected = false;
        try
        {
            OutputStager.StageComponentAsync(
                    [new StagingItem(DownloadPath(ComponentKind.Ultrahand, "sdout.zip"), twoTargetZip)],
                    Path.Combine(AppPaths.BaseDirectory, "staging-probe"),
                    Path.Combine(AppPaths.BaseDirectory, "staging-probe-payload"),
                    includePayloads: true,
                    progress: null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Assert(rejected, "压缩包声明多个落点时应直接报错，而不是静默取第一个");

        // 3) 造一份「和真实发布包同构」的假下载目录，用真实 picker 取落点后暂存 + 生成
        //
        // 先把 out/ 整个清掉：本用例要断言「out/ 里**没有**某个目录」，
        // 而清单驱动的清理只认上一次的清单（上一轮要是崩在中途，产物就没被记进清单）。
        // 留着旧产物会让断言测到上一轮的残留，而不是本轮的行为。
        TryDeleteDirectory(AppPaths.OutputRoot);

        BuildFakeDownloadTree();

        // 前置条件：旧版本留下的 unpacked/sdout/、unpacked/nx-ovlloader/ 确实种下去了。
        // 少了这一条，下面那两条「out 里不该有 xxx/」就可能是空跑。
        var staleUnpacked = AppPaths.ComponentUnpackedRoot(ConfigGenerator.FolderName(ComponentKind.Ultrahand));
        Assert(Directory.Exists(Path.Combine(staleUnpacked, "sdout")),
            "前置条件：应已种下旧版本留下的 unpacked/sdout/");
        Assert(Directory.Exists(Path.Combine(staleUnpacked, "nx-ovlloader")),
            "前置条件：应已种下旧版本留下的 unpacked/nx-ovlloader/");

        var options = new WizardOptions
        {
            Atmosphere = true,
            Hekate = true,
            Ultrahand = true,
            SysPatch = true,
            Ram8Gb = true,
            BootStock = true,
            BootSysNand = true,
            BootEmuNand = true,
            Use90DnsSysmmc = false,
            Use90DnsEmummc = false,
            IncludeComponentFilesInOutput = true,
            IncludePayloads = true,
        };
        ApplyCatalogDefaults(options);
        options.Set(ComponentKind.Ultrahand, "installMode", "sdout");
        options.Set(ComponentKind.Ultrahand, "installLang", "1");
        options.Set(ComponentKind.Ultrahand, "installOvlloader", "1");
        options.Set(ComponentKind.Ultrahand, "installSysmodules", "1");

        StageAllComponents(options);
        var result = Generate(options);
        var outRoot = AppPaths.OutputRoot;

        // 第 1 条：sdout.zip 的内容就是 SD 卡根目录结构，不该再套一层 sdout/。
        //         这一条同时也是「升级路径」的护栏：旧版本解出来的 unpacked/sdout/ 必须被清掉，
        //         否则它会被当成普通组件目录合并进 out/，老毛病照旧。
        Assert(!Directory.Exists(Path.Combine(outRoot, "sdout")),
            "out/ 里不应出现 sdout/ 这一层（含旧版本遗留在暂存区里的那棵）");
        Assert(File.Exists(Path.Combine(outRoot, "config", "ultrahand", "lang", "en.json")),
            "sdout.zip 里的 config/ultrahand/lang/en.json 应落到 out/config/ultrahand/lang/");
        Assert(File.Exists(Path.Combine(outRoot, "switch", ".overlays", "ovlmenu.ovl")),
            "sdout.zip 里的 ovlmenu.ovl 应落在 out/switch/.overlays/");
        Assert(File.Exists(Path.Combine(outRoot, "atmosphere", "contents", "420000000007E51A", "exefs.nsp")),
            "sdout.zip 里的 atmosphere/contents/… 应落在 out/atmosphere/contents/…");

        // 第 2 条：nx-ovlloader.zip 同理
        Assert(!Directory.Exists(Path.Combine(outRoot, "nx-ovlloader")),
            "out/ 里不应出现 nx-ovlloader/ 这一层");
        Assert(File.Exists(Path.Combine(outRoot, "switch", "Ultrahand-Reload", "Ultrahand-Reload.nro")),
            "nx-ovlloader.zip 里的 switch/Ultrahand-Reload/ 应落在 out/switch/Ultrahand-Reload/");

        // 第 3 条：ovlSysmodules.ovl → /switch/.overlays/
        Assert(File.Exists(Path.Combine(outRoot, "switch", ".overlays", "ovlSysmodules.ovl")),
            "ovlSysmodules.ovl 应落在 out/switch/.overlays/");
        Assert(!File.Exists(Path.Combine(outRoot, "ovlSysmodules.ovl")),
            "ovlSysmodules.ovl 不该平铺在 out 根目录");

        // 第 4 条：lang.zip 里是裸 json，必须落到 /config/ultrahand/lang/
        Assert(File.Exists(Path.Combine(outRoot, "config", "ultrahand", "lang", "zh-cn.json")),
            "lang.zip 里的 zh-cn.json 应落在 out/config/ultrahand/lang/");
        Assert(!File.Exists(Path.Combine(outRoot, "zh-cn.json")),
            "lang.zip 里的裸 json 不该散落在 out 根目录");

        // 第 5 条：fusee.bin → /bootloader/payloads/
        Assert(File.Exists(Path.Combine(outRoot, "bootloader", "payloads", "fusee.bin")),
            "fusee.bin 应落在 out/bootloader/payloads/");
        Assert(!File.Exists(Path.Combine(outRoot, "fusee.bin")),
            "fusee.bin 不该平铺在 out 根目录");

        // 第 6 条：hekate payload 同时放根目录 payload.bin 与 bootloader/update.bin，
        //         且必须是我们**选中的那个变体**（8G 模式下是 __ram8GB.bin）。
        //         注意 bootloader/update.bin 组件包里本来也有一份（标准版），
        //         所以这条断言同时盯住了「payload 合并必须排在组件合并之后」。
        //
        // 用 ReadOrEmpty：文件整个缺失时也要给出可读的失败信息，而不是抛 FileNotFound
        // 把后面的断言全带崩（那样只能看到一条崩溃，看不出到底错在哪几处）。
        var payloadBin = ReadOrEmpty("payload.bin");
        var updateBin = ReadOrEmpty("bootloader/update.bin");
        var spareHekate = ReadOrEmpty("bootloader/payloads/hekate_ctcaer_6.2.1.bin");

        Assert(payloadBin == "hekate-8g", $"根目录 payload.bin 应是 8G 版 payload，实际：{Describe(payloadBin)}");
        Assert(updateBin == "hekate-8g",
            $"bootloader/update.bin 应被 8G 版 payload 覆盖，而不是留着组件包里的标准版，实际：{Describe(updateBin)}");
        Assert(spareHekate == "hekate-4g",
            $"8G 模式下额外保留的标准版 payload 应放在 bootloader/payloads/，不能盖掉 payload.bin，实际：{Describe(spareHekate)}");
        Assert(!File.Exists(Path.Combine(outRoot, "hekate_ctcaer_6.2.1__ram8GB.bin")),
            "8G 版 payload 不该再以原名平铺在 out 根目录（它已经就是 payload.bin）");

        // 8G 模式下，组件包**自带**的那份标准版 payload 落在 out 根目录，
        // 名字与正在使用的 8G 版只差一个后缀，肉眼几乎分不出 —— 必须删掉。
        // 标准版本身仍保留在 bootloader/payloads/（见上面的 spareHekate 断言）。
        Assert(!File.Exists(Path.Combine(outRoot, "hekate_ctcaer_6.2.1.bin")),
            "8G 模式下 out 根目录不该留下标准版 payload（组件包自带的那份要删掉）");
        Assert(!result.WrittenFiles.Any(f => string.Equals(f, "hekate_ctcaer_6.2.1.bin", StringComparison.OrdinalIgnoreCase)),
            "被删掉的标准版 payload 不该继续留在清单里，否则打包校验会报「清单中的文件不在包内」");
        Assert(result.WrittenFiles.Contains(Path.Combine("bootloader", "payloads", "hekate_ctcaer_6.2.1.bin")),
            "8G 模式下保留的那份标准版 payload 仍应留在清单里");

        // 清单里记的必须是落点后的路径，否则打包完整性校验会漏掉它们
        Assert(result.PayloadFiles.Contains("bootloader/payloads/fusee.bin"), "fusee.bin 的清单路径应是落点路径");
        Assert(result.PayloadFiles.Contains("payload.bin"), "payload.bin 应写进清单");
        Assert(result.PayloadFiles.Contains("bootloader/update.bin"), "bootloader/update.bin 应写进清单");
        Assert(result.WrittenFiles.Contains(Path.Combine("switch", ".overlays", "ovlSysmodules.ovl")),
            "ovlSysmodules.ovl 的落点应写进清单");

        // 我们生成的配置不能被组件包里的同名文件冲掉（合并排在配置生成之前）
        Assert(Read("config/ultrahand/config.ini").Contains("[ultrahand]"),
            "Ultrahand 配置应是我们生成的那份");

        // 校验器应按「文件到底在不在该在的地方」通过
        var report = OutputValidator.Validate(options, outRoot, result);
        Assert(report.ErrorCount == 0, $"落点场景不应有校验错误，实际 {report.ErrorCount} 项");
        Assert(report.Items.Any(i => i.Key == "Check.Ok.Payloads" && i.Level == ValidationLevel.Ok),
            "校验器应确认 payload 都落在正确位置");

        // 4) 关掉 payload 开关：payload 不该出现，组件本体照旧
        var noPayload = CloneFlags(options);
        ApplyCatalogDefaults(noPayload);
        noPayload.IncludePayloads = false;

        StageAllComponents(noPayload);
        var noPayloadResult = Generate(noPayload);

        Assert(noPayloadResult.PayloadFiles.Count == 0, "关掉开关后不应放置任何 payload");
        Assert(!File.Exists(Path.Combine(outRoot, "payload.bin")), "关掉开关后 out 根目录不应有 payload.bin");
        Assert(!File.Exists(Path.Combine(outRoot, "bootloader", "payloads", "fusee.bin")),
            "关掉开关后不应有 bootloader/payloads/fusee.bin");
        Assert(ReadOrEmpty("bootloader/update.bin") == "hekate-4g",
            "关掉开关后 update.bin 应回落成组件包里的那份");
        Assert(File.Exists(Path.Combine(outRoot, "atmosphere", "package3")),
            "payload 开关不应影响组件本体合并");
        Assert(!File.Exists(Path.Combine(outRoot, "hekate_ctcaer_6.2.1.bin")),
            "只要开着 8G 运存，根目录就不该留标准版 payload（与 payload 摆放开关无关）");

        // 5) 4G 模式：两个落点都应换成标准版 payload
        var ram4Gb = CloneFlags(options);
        ApplyCatalogDefaults(ram4Gb);
        ram4Gb.Ram8Gb = false;

        StageAllComponents(ram4Gb);
        Generate(ram4Gb);

        Assert(ReadOrEmpty("payload.bin") == "hekate-4g", "4G 模式下 payload.bin 应是标准版 payload");
        Assert(ReadOrEmpty("bootloader/update.bin") == "hekate-4g", "4G 模式下 update.bin 应是标准版 payload");
        Assert(!File.Exists(Path.Combine(outRoot, "bootloader", "payloads", "hekate_ctcaer_6.2.1.bin")),
            "4G 模式下不该再保留一份重复的标准版 payload");
        Assert(File.Exists(Path.Combine(outRoot, "hekate_ctcaer_6.2.1.bin")),
            "4G 模式下组件包自带的标准版 payload 应留在 out 根目录（它就是要用的那一份）");

        // 6) 手工安装模式：ovlmenu.ovl 也应落到 /switch/.overlays/
        var manual = CloneFlags(options);
        ApplyCatalogDefaults(manual);
        manual.Set(ComponentKind.Ultrahand, "installMode", "manual");

        StageAllComponents(manual);
        Generate(manual);

        Assert(File.Exists(Path.Combine(outRoot, "switch", ".overlays", "ovlmenu.ovl")),
            "手工安装模式下 ovlmenu.ovl 应落在 out/switch/.overlays/");
        Assert(!File.Exists(Path.Combine(outRoot, "ovlmenu.ovl")),
            "手工安装模式下 ovlmenu.ovl 不该平铺在 out 根目录");

        // 7) 清单清理要把「被清空」的目录一并收走。
        //
        // 这是升级路径上的真实场景：旧版本把 sdout.zip 铺到 out/sdout/，新版铺到 out/ 根。
        // 清理只删文件的话，out/sdout/ 会作为一个空文件夹留下来 —— 用户看到的依然是
        // 「多了不该有的文件夹」，等于没修好。
        var staleRoot = AppPaths.ComponentUnpackedRoot(ConfigGenerator.FolderName(ComponentKind.SysPatch));
        WriteText(Path.Combine(staleRoot, "sdout", "switch", "Ultrahand-Reload", "Ultrahand-Reload.nro"), "stale");

        Generate(options);
        Assert(Directory.Exists(Path.Combine(outRoot, "sdout")),
            "前置条件：这一轮应把 sdout/ 合并进 out，用来模拟旧版本留下的目录");

        TryDeleteDirectory(Path.Combine(staleRoot, "sdout"));
        Generate(options);

        Assert(!Directory.Exists(Path.Combine(outRoot, "sdout")),
            "上一轮合并进来的空目录应被清理掉，而不是留下一个空文件夹");

        // 清理：删掉造的假下载目录，并把 out/ 还原成最完整的完整场景
        CleanComponentDownloads();
        Generate(_fullOptions!);

        Assert(!File.Exists(Path.Combine(outRoot, "payload.bin")),
            "清理后 out/ 应还原成不含 payload 的完整场景");
        Assert(File.Exists(Path.Combine(outRoot, "bootloader", "hekate_ipl.ini")),
            "清理后 out/ 应重新包含完整场景的引导配置");
    }

    /// <summary>
    /// 造一份「和真实发布包同构」的假下载目录。结构照抄实测的 dist/download/：
    /// 各组件包内部一律是 SD 卡根目录的样子，只有 lang.zip 是裸 json
    /// —— 这正是它必须显式声明落点的原因。
    /// </summary>
    private static void BuildFakeDownloadTree()
    {
        CleanComponentDownloads();

        // Atmosphere：zip 内含 atmosphere/、switch/、hbmenu.nro；另有一个散装 fusee.bin
        CreateZip(
            DownloadPath(ComponentKind.Atmosphere, "atmosphere-1.7.0-master.zip"),
            ("atmosphere/package3", "pkg3"),
            ("atmosphere/contents/0100000000000000/exefs.nsp", "nsp"),
            ("switch/daybreak.nro", "nro"),
            ("hbmenu.nro", "nro"));
        WriteText(DownloadPath(ComponentKind.Atmosphere, "fusee.bin"), "fusee");

        // Hekate：zip 内含 bootloader/**（其中自带一份标准版 update.bin），另有散装 payload。
        // zip 里那份同名文件是「payload 合并必须排在组件合并之后」这条顺序约定的试金石。
        CreateZip(
            DownloadPath(ComponentKind.Hekate, "hekate_ctcaer_6.2.1_Nyx_1.6.0.zip"),
            ("bootloader/sys/nyx.bin", "nyx"),
            ("bootloader/update.bin", "hekate-4g"),
            // 真实 Nyx 包在根目录**也**带着一份标准版 payload，整棵铺开就会落到 out 根目录。
            // 8G 模式下它是必须被清掉的那一份（见下面的断言）。
            ("hekate_ctcaer_6.2.1.bin", "hekate-4g"));
        WriteText(DownloadPath(ComponentKind.Hekate, "hekate_ctcaer_6.2.1.bin"), "hekate-4g");
        WriteText(DownloadPath(ComponentKind.Hekate, "hekate_ctcaer_6.2.1__ram8GB.bin"), "hekate-8g");

        // Ultrahand：sdout.zip
        CreateZip(
            DownloadPath(ComponentKind.Ultrahand, "sdout.zip"),
            ("atmosphere/contents/420000000007E51A/exefs.nsp", "ovlloader"),
            ("atmosphere/contents/420000000007E51A/flags/boot2.flag", string.Empty),
            ("config/ultrahand/lang/en.json", "{}"),
            ("config/ultrahand/sounds/enter.wav", "wav"),
            ("switch/.overlays/ovlmenu.ovl", "ovlmenu"),
            ("switch/Ultrahand-Reload/Ultrahand-Reload.nro", "reload"));

        // nx-ovlloader.zip：真实发布包里和 sdout.zip 内容重复，照抄这个事实
        CreateZip(
            DownloadPath(ComponentKind.Ultrahand, "nx-ovlloader.zip"),
            ("atmosphere/contents/420000000007E51A/exefs.nsp", "ovlloader"),
            ("switch/Ultrahand-Reload/Ultrahand-Reload.nro", "reload"));

        // lang.zip：**裸 json**，连一层目录都没有
        CreateZip(
            DownloadPath(ComponentKind.Ultrahand, "lang.zip"),
            ("en.json", "{}"),
            ("zh-cn.json", "{}"));

        WriteText(DownloadPath(ComponentKind.Ultrahand, "ovlSysmodules.ovl"), "ovlsysmodules");

        // 手工安装模式取的是裸 ovlmenu.ovl（与 sdout.zip 里的那份是同一个东西）
        WriteText(DownloadPath(ComponentKind.Ultrahand, "ovlmenu.ovl"), "ovlmenu");

        // Sys-patch
        CreateZip(
            DownloadPath(ComponentKind.SysPatch, "sys-patch-v1.6.2.3.zip"),
            ("atmosphere/contents/420000000000000B/exefs.nsp", "syspatch"),
            ("switch/.overlays/sys-patch-overlay.ovl", "ovl"));

        // 种下「旧版本留下的暂存结果」：老版本把 sdout.zip / nx-ovlloader.zip 各解到以文件名
        // 命名的子目录里。新版必须靠清空暂存区把这两棵树清掉，否则它们会继续被合并进 out/ ——
        // 用户升级之后看到的还是老毛病（out/ 里多出 sdout/、nx-ovlloader/）。
        var ultrahandUnpacked = AppPaths.ComponentUnpackedRoot(ConfigGenerator.FolderName(ComponentKind.Ultrahand));
        WriteText(Path.Combine(ultrahandUnpacked, "sdout", "config", "ultrahand", "lang", "en.json"), "stale");
        WriteText(Path.Combine(ultrahandUnpacked, "nx-ovlloader", "switch", "stale.nro"), "stale");
    }

    /// <summary>
    /// 用**真实的 picker** 走一遍「下载 → 暂存」。
    /// 手写落点会漏掉 catalog 写错的情况，所以这里一路走真货：假 ReleaseInfo 喂给 picker，
    /// 拿到的 AssetPick 直接交给 OutputStager。
    /// </summary>
    private static void StageAllComponents(WizardOptions options)
    {
        var releases = new Dictionary<string, ReleaseInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [ComponentCatalog.AtmosphereRepo.DisplayName] = FakeRelease("atmosphere-1.7.0-master.zip", "fusee.bin"),
            [ComponentCatalog.HekateRepo.DisplayName] = FakeRelease(
                "hekate_ctcaer_6.2.1_Nyx_1.6.0.zip",
                "hekate_ctcaer_6.2.1.bin",
                "hekate_ctcaer_6.2.1__ram8GB.bin"),
            [ComponentCatalog.UltrahandRepo.DisplayName] = FakeRelease("sdout.zip", "ovlmenu.ovl", "lang.zip"),
            [ComponentCatalog.OvlSysmodulesRepo.DisplayName] = FakeRelease("ovlSysmodules.ovl"),
            [ComponentCatalog.NxOvlloaderRepo.DisplayName] = FakeRelease("nx-ovlloader.zip"),
            [ComponentCatalog.SysPatchRepo.DisplayName] = FakeRelease("sys-patch-v1.6.2.3.zip"),
        };

        // 清单来源必须是**真源本身**（枚举），不能手抄 —— 手抄的清单在新增第 5 个组件时会
        // 静默跳过它，于是「每个组件都能被暂存/生成」这件事看起来一直成立（§1 教训 4）。
        // 本文件另外三处（CheckComponentKindCoverage / 选项往返 / 落点契约）都已是这个写法。
        foreach (var kind in Enum.GetValues<ComponentKind>())
        {
            var folder = ConfigGenerator.FolderName(kind);
            var downloadRoot = AppPaths.ComponentDownloadRoot(folder);
            var unpackedRoot = AppPaths.ComponentUnpackedRoot(folder);
            var payloadRoot = AppPaths.ComponentPayloadRoot(folder);

            var missing = new List<string>();
            var stagingItems = new List<StagingItem>();

            // ⚠️ 这里只能遍历**声明的手写请求**并按 Applies 过滤，不能改用 RepoRequestsFor：
            //    后者会把槽驱动的组件（另外 40 个插件）也展开成请求，而这个用例只在假下载目录里
            //    种了框架那四个组件的文件 —— 一展开就会拿 40 个不存在的仓库名去查字典，直接崩。
            //    （顺带说明：对槽驱动的组件，下面这个循环本来就是**空转**的，它们的覆盖在
            //      CheckAssetSlots 那一族用例里。）
            foreach (var request in ComponentCatalog.Get(kind).Repos
                         .Where(request => request.Applies?.Invoke(options) ?? true))
            {
                foreach (var pick in request.Picker(releases[request.Repo.DisplayName], options))
                {
                    var source = Path.Combine(downloadRoot, pick.FileName);
                    if (!File.Exists(source))
                    {
                        missing.Add(pick.FileName);
                        continue;
                    }

                    stagingItems.Add(new StagingItem(source, pick));
                }
            }

            Assert(missing.Count == 0,
                $"picker 选出来的文件都该在假下载目录里（{folder} 缺少：{string.Join("、", missing)}）");

            // 走产品代码里同一个入口：它内部会先清空暂存区，所以「旧版本遗留的
            // unpacked/sdout/ 有没有被清掉」也一并被这一步覆盖到
            OutputStager.StageComponentAsync(
                    stagingItems,
                    unpackedRoot,
                    payloadRoot,
                    options.IncludePayloads,
                    progress: null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
        }
    }

    private static ReleaseInfo FakeRelease(params string[] assetNames)
        => new(
            "v-test",
            "test",
            isPrerelease: false,
            publishedAt: DateTimeOffset.UnixEpoch,
            htmlUrl: "https://example.invalid",
            assetNames
                .Select(name => new ReleaseAsset(name, "https://example.invalid/" + name, 1))
                .ToList());

    private static string DownloadPath(ComponentKind kind, string fileName)
        => Path.Combine(AppPaths.ComponentDownloadRoot(ConfigGenerator.FolderName(kind)), fileName);

    /// <summary>
    /// 离线固件：**版本目录由下载文件名造出来**这件事，走一次真实的暂存。
    ///
    /// 为什么单独立一个用例：<c>{asset}</c> 的替换点是 <c>AssetPick.ResolvedTargets</c>，
    /// 而真正读它的是 <c>OutputStager</c> 的**压缩包分支**（<c>ResolvedTargets[0].Directory</c>）。
    /// 只断言「属性算出来是 Firmware/Firmware.23.0.0」的话，谁把 OutputStager 改回去读裸的
    /// <c>Targets</c>，回归照样全绿 —— 那条路只有**真跑一次暂存**才会露出来。
    ///
    /// ⚠️ 包内条目用的是**实测的真实条目名**（<c>tools/zip-listing.py</c> 读 23.0.0 的中央目录得到：
    /// 238 个条目全在压缩包根，形如 <c>&lt;32 位十六进制&gt;.nca</c> / <c>.cnmt.nca</c>），
    /// 不是编的假名字 —— 用假名字的话，「落点对不对」这条断言就没有意义了。
    /// </summary>
    private static void CheckFirmwareStaging()
    {
        Section("离线固件：包内是平的 ⇒ 版本目录由下载文件名造出来");

        var work = Path.Combine(AppPaths.BaseDirectory, "firmware-staging-probe");
        TryDeleteDirectory(work);
        Directory.CreateDirectory(work);

        var zipPath = Path.Combine(work, "Firmware.23.0.0.zip");
        CreateZip(zipPath,
            ("00e7f53ae3ee4d6fd915b1ae26fbd1be.cnmt.nca", "cnmt"),
            ("02d227be9dd2682bc52551bd10cbe992.nca", "nca-1"));

        var firmware = ComponentCatalog.All.First(d => d.Kind == ComponentKind.Firmware);
        var slot = firmware.Slots[0];

        // 走产品代码同一条路：先用真实资源名匹配出 pick，再拿它去暂存
        var picks = ComponentCatalog.PickSlot(
            firmware, slot, FakeRelease("Firmware.23.0.0.zip"), new WizardOptions { Language = "zh-Hans" });
        Assert(picks.Count == 1, $"前置条件：应恰好选中 1 个资源，实际 {picks.Count}");

        var unpacked = Path.Combine(work, "unpacked");
        var payload = Path.Combine(work, "payload");

        var staged = OutputStager.StageComponentAsync(
                [new StagingItem(zipPath, picks[0])],
                unpacked,
                payload,
                includePayloads: false,
                progress: null,
                CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert(staged.Count == 1, $"前置条件：应暂存 1 条记录，实际 {staged.Count}");
        Assert(staged[0].RelativePath == "Firmware/Firmware.23.0.0",
            "压缩包的暂存落点应是 Firmware/Firmware.23.0.0（落点里的 {asset} 换成下载文件名去扩展名）"
            + $"；实际 {staged[0].RelativePath}");

        var flat = Directory.EnumerateFiles(unpacked, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(unpacked, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert(flat.SequenceEqual(
                new[]
                {
                    "Firmware/Firmware.23.0.0/00e7f53ae3ee4d6fd915b1ae26fbd1be.cnmt.nca",
                    "Firmware/Firmware.23.0.0/02d227be9dd2682bc52551bd10cbe992.nca",
                },
                StringComparer.Ordinal),
            "两个 .nca 都应解到 Firmware/Firmware.23.0.0/ 下（包内是平的 ⇒ 不多套一层、也不丢文件）"
            + "；实际：" + string.Join("、", flat));

        Assert(!Directory.EnumerateDirectories(unpacked, "*", SearchOption.AllDirectories)
                .Any(dir => Path.GetFileName(dir).Contains("{asset}", StringComparison.Ordinal)),
            "占位符必须被替换掉 —— 出现字面量的 {asset} 目录，说明某个分支读的是没解析过的 Targets");

        // 用户在高级设置里改了文件名 ⇒ **目录跟着改**。
        // 这是 `{asset}` 那条设计的承诺（也是用户要求「地址与文件名可改」的直接后果）：
        // 不跟着改的话会产出「文件叫 Firmware.20.0.0.zip、目录叫 Firmware.23.0.0」这种
        // 要对着两个名字猜的东西。这里走的是 settings 里那个键（`<组件>/<槽 Key>` = Firmware/Firmware）。
        var renamed = new WizardOptions { Language = "zh-Hans" };
        renamed.AssetFileNames["Firmware/Firmware"] = "Firmware.20.0.0.zip";

        var renamedPicks = ComponentCatalog.PickSlot(
            firmware, slot, FakeRelease("Firmware.20.0.0.zip"), renamed);

        Assert(renamedPicks.Count == 1
               && renamedPicks[0].ResolvedTargets[0].Directory == "Firmware/Firmware.20.0.0",
            "用户改过下载文件名时，版本目录要跟着改成 Firmware/Firmware.20.0.0"
            + "；实际：" + string.Join("、", renamedPicks.SelectMany(p => p.ResolvedTargets).Select(t => t.Directory)));

        TryDeleteDirectory(work);
    }

    /// <summary>
    /// 高级设置里**槽那一行**的「地址 / 文件名」必须真的过一遍 <c>settings.json</c>。
    ///
    /// 为什么单独立这条：既有的持久化用例覆盖的是**标量**字段（`CheckEverySettingIsPersisted`
    /// 只挑 `IsScalar` 的属性）与 `OptionValues`，而槽的这两项存在 `AssetAddresses` /
    /// `AssetFileNames` 两个**字典**里 —— 一个用例都没走过。用户对「离线固件」的明确要求是
    /// 「地址与文件名可改 + 操作写入配置文件 + 下次打开读回来」，那正是这条路径。
    ///
    /// 受害人选「离线固件」那一行：它就是本轮为这个需求新增的那一行，顺带把它钉住。
    /// 机制是**所有槽共用**的，所以这条红了意味着全部 40 多个插件的地址都存不下来。
    ///
    /// ⚠️ 它真的会写 <c>settings.json</c>，所以外面套备份/还原，收尾先 <c>FlushSettings()</c>
    /// 把挂着的自动落盘卸掉（**延迟写也是写** —— 见第 83 条）。
    /// </summary>
    private static void CheckAssetSlotRowPersistence()
    {
        Section("高级设置：槽那一行的地址与文件名要能落盘、再读回来");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            var vm = new MainViewModel();
            var row = vm.AssetSlotGroups
                .First(group => group.Definition.Kind == ComponentKind.Firmware)
                .Slots.Single();

            Assert(row.Address.Length == 0 && row.FileName.Length == 0,
                "前置条件：用户没改过时，这两项应是**空串**（界面显示灰色占位文字），"
                + $"而不是把默认值填进去；实际 Address='{row.Address}'、FileName='{row.FileName}'");

            row.Address = "myfork/NX_Firmware";
            row.FileName = "Firmware 20.0.0.zip";
            TouchAndFlush(vm);

            var saved = SettingsStore.Load();
            Assert(saved.AssetAddresses.TryGetValue("Firmware/Firmware", out var savedAddress)
                   && savedAddress == "myfork/NX_Firmware",
                "改过的地址必须写进 settings.json 的 AssetAddresses（键 = <组件>/<槽 Key>）"
                + $"；实际 {(saved.AssetAddresses.TryGetValue("Firmware/Firmware", out var a) ? a : "(没有)")}");
            Assert(saved.AssetFileNames.TryGetValue("Firmware/Firmware", out var savedName)
                   && savedName == "Firmware 20.0.0.zip",
                "改过的文件名必须写进 settings.json 的 AssetFileNames"
                + $"；实际 {(saved.AssetFileNames.TryGetValue("Firmware/Firmware", out var n) ? n : "(没有)")}");

            // 重开一次（模拟下次启动）：这两项要回到界面上，并且真的生效到下载时用的那一对
            var reloaded = new MainViewModel();
            var reloadedRow = reloaded.AssetSlotGroups
                .First(group => group.Definition.Kind == ComponentKind.Firmware)
                .Slots.Single();

            Assert(reloadedRow.Address == "myfork/NX_Firmware"
                   && reloadedRow.FileName == "Firmware 20.0.0.zip",
                "重开软件后，那一行应原样显示用户填的地址与文件名"
                + $"；实际 Address='{reloadedRow.Address}'、FileName='{reloadedRow.FileName}'");

            var options = reloaded.BuildWizardOptions();
            Assert(ComponentCatalog.ResolveSlotAddress(
                       ComponentCatalog.Get(ComponentKind.Firmware), ComponentCatalog.Get(ComponentKind.Firmware).Slots[0], options)
                   == "myfork/NX_Firmware",
                "用户填的地址要真的被解析层用上（只是显示出来不算数）");

            reloaded.FlushSettings();
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    private static void CleanComponentDownloads()
    {
        foreach (var kind in Enum.GetValues<ComponentKind>())
        {
            TryDeleteDirectory(AppPaths.ComponentDownloadRoot(ConfigGenerator.FolderName(kind)));
        }
    }

    private static void CreateZip(string path, params (string Name, string Content)[] entries)
    {
        AppPaths.EnsureDirectory(Path.GetDirectoryName(path)!);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            AddEntry(archive, name, content);
        }
    }

    // ── zip 解压 ────────────────────────────────────────────────
    private static void CheckZipExtraction()
    {
        Section("zip 解压");

        var work = Path.Combine(AppPaths.BaseDirectory, "extract-test");
        TryDeleteDirectory(work);
        Directory.CreateDirectory(work);

        var zipPath = Path.Combine(work, "sample.zip");

        // 造一个「既有目录条目、又有嵌套文件」的压缩包，模拟真实的 Atmosphere/Hekate 包
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "atmosphere/", null);                          // 目录项
            AddEntry(archive, "atmosphere/contents/", null);                 // 目录项
            AddEntry(archive, "atmosphere/config/", null);                   // 目录项
            AddEntry(archive, "atmosphere/package3", "pkg3");
            AddEntry(archive, "atmosphere/config/system_settings.ini", "[atmosphere]");
            AddEntry(archive, "exosphere.ini", "blank_prodinfo_sysmmc=0");
        }

        var target = Path.Combine(work, "unpacked");
        var count = ArchiveExtractor.Extract(zipPath, target, null, CancellationToken.None);

        // 压缩包共 6 个条目（3 目录 + 3 文件）。日志里报的必须是真实文件数 3，
        // 而不是条目总数 6 —— 之前就报成了 6，用户看到的数字会偏大。
        Assert(count == 3, $"解压应返回真实文件数 3（不含 3 个目录项），实际 {count}");
        Assert(File.Exists(Path.Combine(target, "atmosphere", "package3")), "嵌套文件应被解出");
        Assert(File.Exists(Path.Combine(target, "atmosphere", "config", "system_settings.ini")),
            "深层嵌套文件应被解出");
        Assert(File.Exists(Path.Combine(target, "exosphere.ini")), "根目录文件应被解出");

        var onDisk = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Count();
        Assert(onDisk == 3, $"磁盘上应恰好有 3 个文件，实际 {onDisk}");

        // 压缩包里的 ../ 路径穿越必须被拦下
        var evilZip = Path.Combine(work, "evil.zip");
        using (var archive = ZipFile.Open(evilZip, ZipArchiveMode.Create))
        {
            AddEntry(archive, "../escaped.txt", "nope");
        }

        var blocked = false;
        try
        {
            ArchiveExtractor.Extract(evilZip, Path.Combine(work, "evil-out"), null, CancellationToken.None);
        }
        catch (IOException)
        {
            blocked = true;
        }

        Assert(blocked, "压缩包内的 ../ 路径穿越应被拒绝");
        Assert(!File.Exists(Path.Combine(work, "escaped.txt")), "穿越出来的文件不应真的落地");

        // MergeDirectory 返回相对路径列表（合并进 out 时用它记清单）
        var merged = Path.Combine(work, "merged");
        var mergedFiles = ArchiveExtractor.MergeDirectory(target, merged, CancellationToken.None);
        Assert(mergedFiles.Count == 3, $"MergeDirectory 应返回 3 条相对路径，实际 {mergedFiles.Count}");
        Assert(File.Exists(Path.Combine(merged, "atmosphere", "package3")), "合并后应保留目录结构");
        Assert(mergedFiles.Contains(Path.Combine("atmosphere", "package3")), "返回的应是相对路径");
        Assert(ArchiveExtractor.MergeDirectory(Path.Combine(work, "no-such-dir"), merged, CancellationToken.None).Count == 0,
            "源目录不存在时应返回空列表而不是抛异常");

        TryDeleteDirectory(work);
    }

    // ── 把解压好的组件文件合并进 out ─────────────────────────────
    private static void CheckMergeIntoOut()
    {
        Section("把解压好的组件文件合并进 out");

        var atmosphereUnpacked = AppPaths.ComponentUnpackedRoot(ConfigGenerator.FolderName(ComponentKind.Atmosphere));
        var hekateUnpacked = AppPaths.ComponentUnpackedRoot(ConfigGenerator.FolderName(ComponentKind.Hekate));

        TryDeleteDirectory(atmosphereUnpacked);
        TryDeleteDirectory(hekateUnpacked);

        // 造假的解压结果。故意放两个「和生成的配置同名」的文件——这是最危险的一类碰撞：
        // Ultrahand 的 sdout.zip 里就有 config/ultrahand/config.ini，hekate 早期版本的包里
        // 也带过 bootloader/hekate_ipl.ini。一旦被组件包覆盖，用户勾的引导项就白勾了。
        WriteText(Path.Combine(atmosphereUnpacked, "atmosphere", "package3"), "pkg3");
        WriteText(Path.Combine(atmosphereUnpacked, "hbmenu.nro"), "nro");
        WriteText(Path.Combine(atmosphereUnpacked, "atmosphere", "config", "stratosphere.ini"),
            "; 组件包里的示例，绝不该覆盖我们生成的配置");

        WriteText(Path.Combine(hekateUnpacked, "bootloader", "sys", "nyx.bin"), "nyx");
        WriteText(Path.Combine(hekateUnpacked, "bootloader", "hekate_ipl.ini"),
            "; 组件包里的示例引导项，绝不该覆盖用户勾的引导项");

        var options = new WizardOptions
        {
            Atmosphere = true,
            Hekate = true,
            Ultrahand = false,
            SysPatch = false,
            BootStock = true,
            BootSysNand = true,
            BootEmuNand = true,
            IncludeComponentFilesInOutput = true,
            IncludePayloads = false,
            // 这几个场景只关心配置生成，显式关掉 boot.dat —— 否则新增开关会让
            // 「应写入 N 个文件」这类计数断言跟着漂移，测的就不是原本的意图了。
            IncludeBootDat = false,
        };
        ApplyCatalogDefaults(options);

        var result = Generate(options);

        // 1) 合并进来的组件本体必须进清单。漏记的后果：
        //    · 下次换组件/换版本时旧文件永远清不掉，会被一起拷进 SD 卡
        //    · 打包后的完整性校验只覆盖配置文件，给出虚假的「全部在包内」
        Assert(result.MergedFileCount == 5, $"应合并 5 个组件文件，实际 {result.MergedFileCount}");
        Assert(result.WrittenFiles.Contains(Path.Combine("atmosphere", "package3")),
            "合并进来的 atmosphere/package3 应写进清单");
        Assert(result.WrittenFiles.Contains(Path.Combine("hbmenu.nro")),
            "合并进来的 hbmenu.nro 应写进清单");
        Assert(result.WrittenFiles.Contains(Path.Combine("bootloader", "sys", "nyx.bin")),
            "合并进来的 bootloader/sys/nyx.bin 应写进清单");

        // 与组件包同名的配置文件只应记一次（合并和配置生成都会碰到它）
        var colliding = Path.Combine("atmosphere", "config", "stratosphere.ini");
        Assert(result.WrittenFiles.Count(p => string.Equals(p, colliding, StringComparison.OrdinalIgnoreCase)) == 1,
            "与组件包同名的配置文件在清单里只应出现一次");
        Assert(result.WrittenFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() == result.WrittenFiles.Count,
            "清单里不应有重复路径");
        // 5 个合并 + 6 个配置 - 2 个同名碰撞（stratosphere.ini、hekate_ipl.ini）= 9
        Assert(result.WrittenFiles.Count == 9,
            $"清单应为 9 条（5 合并 + 6 配置 - 2 碰撞），实际 {result.WrittenFiles.Count}");

        // 2) ConfigFiles 只装配置文件，不含组件本体
        Assert(result.ConfigFiles.Count == 6, $"应写出 6 个配置文件，实际 {result.ConfigFiles.Count}");
        Assert(!result.ConfigFiles.Contains(Path.Combine("atmosphere", "package3")),
            "ConfigFiles 不应包含组件本体");

        // 3) 生成的配置必须压过组件包里的同名文件（合并排在配置生成之前）
        var hekateIni = Read(Path.Combine("bootloader", "hekate_ipl.ini"));
        Assert(!hekateIni.Contains("组件包里的示例引导项"), "组件包里的示例引导项不应覆盖用户勾的引导项");
        Assert(hekateIni.Contains($"[{SysNandTitle}]"), "用户勾的引导项应保留");

        var stratosphere = Read(colliding);
        Assert(!stratosphere.Contains("组件包里的示例"), "组件包里的示例配置不应覆盖我们生成的配置");
        Assert(stratosphere.Contains("[stratosphere]"), "我们生成的 stratosphere.ini 应保留");

        // 4) 不碰撞的组件本体应正常落地
        Assert(File.Exists(Path.Combine(AppPaths.OutputRoot, "hbmenu.nro")), "组件本体应真的落在 out 里");
        Assert(File.Exists(Path.Combine(AppPaths.OutputRoot, "atmosphere", "package3")),
            "atmosphere/package3 应真的落在 out 里");

        // 5) 打包校验现在能覆盖合并进来的文件
        var zipPath = Path.Combine(AppPaths.BaseDirectory, "merge-test.zip");
        OutputPackager.Create(AppPaths.OutputRoot, zipPath);
        var missing = OutputValidator.FindMissingInZip(zipPath, result.WrittenFiles);
        Assert(missing.Count == 0,
            $"压缩包应含清单里全部 {result.WrittenFiles.Count} 个文件，缺少：{string.Join("、", missing)}");
        File.Delete(zipPath);

        // 6) 校验器应把合并结果和 package3 都报出来
        var report = OutputValidator.Validate(options, AppPaths.OutputRoot, result);
        Assert(report.ErrorCount == 0, $"合并场景不应有校验错误，实际 {report.ErrorCount} 项");
        Assert(report.Items.Any(i => i.Key == "Check.Ok.Merged" && i.Level == ValidationLevel.Ok),
            "应报告已合并组件文件");
        Assert(report.Items.Any(i => i.Key == "Check.Ok.Package3" && i.Level == ValidationLevel.Ok),
            "应报告 package3 已就位");

        // 7) 关键：关掉合并后，上一轮合并进来的文件必须被清单驱动的清理删掉
        var off = CloneFlags(options);
        ApplyCatalogDefaults(off);
        off.IncludeComponentFilesInOutput = false;

        var offResult = Generate(off);
        Assert(offResult.MergedFileCount == 0, "关掉合并后不应再合并");
        Assert(!File.Exists(Path.Combine(AppPaths.OutputRoot, "hbmenu.nro")),
            "关掉合并后，上一轮合并进来的组件本体应被清单清理掉");
        Assert(!File.Exists(Path.Combine(AppPaths.OutputRoot, "atmosphere", "package3")),
            "关掉合并后，上一轮合并进来的 package3 应被清单清理掉");

        // 清理造的假解压目录，并把 out/ 还原成完整场景
        TryDeleteDirectory(atmosphereUnpacked);
        TryDeleteDirectory(hekateUnpacked);
        Generate(_fullOptions!);
    }

    /// <summary>
    /// 逐组件对账：勾了 N 个组件，out/ 里就必须有这 N 个组件**各自**的文件。
    ///
    /// 缺口形态（本用例存在的理由）：<c>MergeComponentFiles</c> 按 <c>ComponentCatalog.All</c>
    /// 逐个组件合并，但某个组件的解压目录不在时是**静默跳过**的；而运行期校验器原先只判断
    /// 「合并总数 &gt; 0」。四个组件里缺一个，另外三个合进来几十个文件，总数照样 &gt; 0 ——
    /// 报告写着「已合并 N 个组件文件」，用户拿到的是缺件产物。
    ///
    /// 名单从 <c>ComponentCatalog.All</c> 取（不手抄）：以后加第 5 个组件，这里自动覆盖。
    /// 在此之前，现有用例只造了 Atmosphere / Hekate 两个组件的解压目录，
    /// Ultrahand 与 Sys-patch 的合并路径**从来没被走到过**。
    /// </summary>
    private static void CheckEverySelectedComponentMergesIntoOut()
    {
        Section("每个被勾选的组件都必须真的合并进 out（按组件对账，不看总数）");

        var all = ComponentCatalog.All.ToList();

        // 前置条件：目录为空时下面的循环恒真；只有 1 个组件时「缺一个」与「全缺」分不开
        Assert(all.Count >= 2, $"前置条件：组件目录应至少 2 项，实际 {all.Count}");

        // 每个组件造一个**唯一文件名**的标记文件，这样每个组件都在 out/ 里留下可指认的痕迹。
        // 不能都叫同一个名字：四个组件写同一个目标路径，最后只剩一份，就分不出是谁了。
        foreach (var definition in all)
        {
            var unpacked = AppPaths.ComponentUnpackedRoot(ConfigGenerator.FolderName(definition.Kind));
            TryDeleteDirectory(unpacked);
            WriteText(Path.Combine(unpacked, $"probe-{definition.Kind}.txt"), definition.Kind.ToString());
        }

        var options = new WizardOptions
        {
            BootStock = true,
            BootSysNand = true,
            BootEmuNand = true,
            IncludeComponentFilesInOutput = true,
            IncludePayloads = false,
            IncludeBootDat = false,
        };

        // 勾选**全部**组件 —— 清单从枚举现取，不手抄。
        // 手写那四个的话，新增的组件永远不会被这条护栏覆盖，而它恰恰是「逐组件对账」唯一的守护者。
        foreach (var kind in Enum.GetValues<ComponentKind>())
        {
            options.TrySetSelected(kind, true);
        }

        ApplyCatalogDefaults(options);

        var result = Generate(options);

        // 1) 每个组件都必须有 > 0 个文件进来
        var zero = all
            .Where(d => result.MergedFilesByComponent.GetValueOrDefault(d.Kind) == 0)
            .Select(d => ConfigGenerator.FolderName(d.Kind))
            .ToList();
        Assert(zero.Count == 0, $"每个被勾选的组件都应合并进至少 1 个文件，这些没有：{string.Join("、", zero)}");

        // 2) 两套账必须对得上：总数 == 各组件之和
        var sum = result.MergedFilesByComponent.Values.Sum();
        Assert(result.MergedFileCount == sum, $"总数（{result.MergedFileCount}）应等于各组件之和（{sum}）");

        // 3) 每个组件的标记文件都要进清单（合并进来的文件必须逐个 AddProducedFile）
        foreach (var definition in all)
        {
            var marker = $"probe-{definition.Kind}.txt";
            Assert(result.WrittenFiles.Contains(marker),
                $"{ConfigGenerator.FolderName(definition.Kind)} 的合并文件应进清单：{marker}");
        }

        // 4) 决定性对照：只删掉**一个**组件的解压目录，其余照旧。
        //    期望：旧的总数检查仍然通过（它看不见缺的那一个），新的按组件对账点名缺的那个。
        //    这条对照是本护栏「非冗余」的证明 —— 没有它，无法排除新检查只是旧检查的复述。
        //
        //    刻意挑一个**非 Atmosphere** 的组件：Atmosphere 缺了会连带触发
        //    Check.Error.Package3，那是另一条检查，会把这条对照搅浑。
        var victim = all.FirstOrDefault(d => d.Kind != ComponentKind.Atmosphere) ?? all[0];
        var spared = all.First(d => d.Kind != victim.Kind);
        TryDeleteDirectory(AppPaths.ComponentUnpackedRoot(ConfigGenerator.FolderName(victim.Kind)));

        var partial = Generate(options);
        var report = OutputValidator.Validate(options, AppPaths.OutputRoot, partial);

        Assert(partial.MergedFileCount > 0,
            "前置条件：删掉一个组件后其余组件仍应合并出文件，总数 > 0（否则这个对照没有意义）");
        Assert(report.Items.Any(i => i.Key == "Check.Ok.Merged" && i.Level == ValidationLevel.Ok),
            "旧的总数检查应**仍然通过** —— 它看不见缺的那一个组件（这正是本护栏存在的理由）");
        Assert(report.Items.Any(i => i.Key == "Check.Warn.ComponentNotMerged"
                && i.Level == ValidationLevel.Warning
                && i.Detail.Contains(ConfigGenerator.FolderName(victim.Kind))),
            $"按组件对账应点名缺件组件 {ConfigGenerator.FolderName(victim.Kind)}");
        Assert(!report.Items.Any(i => i.Key == "Check.Warn.ComponentNotMerged"
                && i.Detail.Contains(ConfigGenerator.FolderName(spared.Kind))),
            "没被删的组件不应被点名（否则这条检查会退化成「凡有组件就报警」）");
    }

    // ── 本轮新增功能（2026-09-16）────────────────────────────────
    //
    // 用户提了七项：① 引导项显示名可改 ② AutoPackZip 默认不勾 ③ GitHub Token 一键化
    // ④ 屏蔽序列号默认不勾 ⑤ hekate 启动图 logopath ⑥ 中文界面走 easyworld/hekate
    // ⑦ 各插件下载源可改。
    //
    // ②④ 改的是默认值，已经并进 CheckDefaults / CheckFreshUiState / CheckUserConfigBaseline；
    // 这里覆盖其余五项，外加一条「语言键覆盖率」的总护栏（见最后一个方法）。

    /// <summary>① 引导项显示名可改。</summary>
    private static void CheckBootEntryTitles()
    {
        Section("引导项显示名：留空用默认名，填了就用用户的名字");

        var loc = LocalizationService.Instance;
        loc.SetLanguage("zh-Hans");

        // 解析规则只有一处（ComponentCatalog.ResolveBootTitle），界面候选项与生成的 ini 共用。
        // 两处只要有一处漏掉自定义名，就会出现「界面显示 A、文件里是 B」——
        // 而 autoboot 的跨层护栏正是拿名字对齐的，漏了它那条护栏就形同虚设。
        Assert(ComponentCatalog.ResolveBootTitle(null, "Boot.Entry.Stock") == loc["Boot.Entry.Stock"],
            "显示名留空时应回落到界面语言的默认名");
        Assert(ComponentCatalog.ResolveBootTitle("   ", "Boot.Entry.Stock") == loc["Boot.Entry.Stock"],
            "显示名只有空白时也应回落到默认名");
        Assert(ComponentCatalog.ResolveBootTitle("  我的正版  ", "Boot.Entry.Stock") == "我的正版",
            "自定义显示名应去掉首尾空白（否则 ini 里会多出看不见的空格）");

        // ⚠️ 2026-09-18 用户要求：三个默认显示名**固定**为 zbxt / zspjxt / xnpjxt，与界面语言无关。
        //    所以这里断言的是「**三份包取值一致**且等于约定值」，而**不是**「跟着语言走」——
        //    旧写法（en-US 必须是 "Stock (SYSNAND)"）钉的正是「跟着语言走」，与用户的新要求相反；
        //    只把它改成 `ResolveBootTitle(null, K) == loc[K]` 更糟：那是**恒真**的橡皮图章
        //    （解析规则本来就是回落到 loc[K]），改错了也不会红。
        //
        //    为什么「三份包必须一致」是要紧的：段名同时进 hekate_ipl.ini 与 autoboot 候选项，
        //    而 autoboot 的跨层护栏是**拿名字对齐**的。三份包不一致 ⇒ 换个界面语言，
        //    同一张卡生成出不同段名，`autoboot=` 指向的项就对不上了。
        var sourceRootForTitles = FindSourceRoot();
        Assert(sourceRootForTitles is not null, "应能找到 src/SwitchCfwWizard 源码目录，用来核对三份语言包的默认显示名");
        if (sourceRootForTitles is not null)
        {
            var packs = new[] { "zh-Hans", "zh-Hant", "en-US" };
            var entryKeys = new[] { "Boot.Entry.Stock", "Boot.Entry.SysNand", "Boot.Entry.EmuNand" };
            var expectedTitles = new[] { StockTitle, SysNandTitle, EmuNandTitle };

            for (var i = 0; i < entryKeys.Length; i++)
            {
                var key = entryKeys[i];
                var values = packs
                    .Select(code => (Code: code, Value: ReadPackString(sourceRootForTitles, code, key)))
                    .ToList();

                Assert(values.All(v => v.Value == expectedTitles[i]),
                    $"{key} 在三份语言包里都应是「{expectedTitles[i]}」（与界面语言无关，否则换语言就换段名），实际："
                    + string.Join(" / ", values.Select(v => $"{v.Code}={v.Value ?? "<缺失>"}")));
            }

            loc.SetLanguage("en-US");
            Assert(ComponentCatalog.ResolveBootTitle(null, "Boot.Entry.Stock") == StockTitle,
                $"英文界面下留空的显示名也应落到「{StockTitle}」（这是用户点名的固定默认值）");
            loc.SetLanguage("zh-Hans");
        }

        loc.SetLanguage("zh-Hans");

        // 生成出来：段名就是用户填的名字，段内容一字不改
        var options = new WizardOptions
        {
            Atmosphere = true,
            Hekate = true,
            Ultrahand = true,
            SysPatch = true,
            BootStock = true,
            BootSysNand = true,
            BootEmuNand = true,
            BootStockTitle = "我的正版",
            BootSysNandTitle = "我的真实破解",
            BootEmuNandTitle = "我的虚拟破解",
            IncludeComponentFilesInOutput = false,
            IncludePayloads = false,
            IncludeBootDat = false,
        };
        ApplyCatalogDefaults(options);
        Generate(options);

        var hekate = ReadOrEmpty("bootloader/hekate_ipl.ini");
        foreach (var (title, marker) in new[]
                 {
                     ("我的正版", "stock=1"),
                     ("我的真实破解", "emummc_force_disable=1"),
                     ("我的虚拟破解", "emummcforce=1"),
                 })
        {
            Assert(SectionCount(hekate, title) == 1,
                $"hekate_ipl.ini 里应有且只有一个自定义段 [{title}]（实际：{Describe(hekate)}）");
            Assert(ReadSection(hekate, title).Contains(marker), $"段 [{title}] 的内容应原样保留（{marker}）");
        }

        Assert(!hekate.Contains("[" + loc["Boot.Entry.Stock"] + "]"),
            "改名之后不该同时留下默认名的段，否则 hekate 菜单里会多出两个引导项");

        // 界面候选项必须用同一个名字 —— 这是「界面 ↔ 文件」一致性的另一半
        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            File.WriteAllText(path, """
                { "BootStock": true, "BootSysNand": true, "BootEmuNand": true,
                  "BootStockTitle": "我的正版", "BootSysNandTitle": "我的真实破解", "BootEmuNandTitle": "我的虚拟破解" }
                """);

            var vm = new MainViewModel();
            Assert(vm.BootStockTitle == "我的正版", "界面应读回存档里的自定义显示名");

            var autoboot = vm.Components.First(c => c.Kind == ComponentKind.Hekate).FindOption("autoboot");
            Assert(autoboot is not null, "Hekate 应有 autoboot 选项");
            var labels = autoboot!.Choices.Select(c => c.Label).ToList();
            Assert(labels.Contains("我的正版") && labels.Contains("我的真实破解") && labels.Contains("我的虚拟破解"),
                $"autoboot 候选项应显示自定义名，实际：{string.Join(" / ", labels)}");
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    /// <summary>⑤ hekate 启动图（logopath）。</summary>
    private static void CheckBootLogo()
    {
        Section("hekate 启动图：复制进 bootloader/res/ 并写 logopath");

        var temp = Path.Combine(Path.GetTempPath(), "cfw-logo-" + Guid.NewGuid().ToString("N"));
        var temp2 = Path.Combine(Path.GetTempPath(), "cfw-logo2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(temp2);

        var loc = LocalizationService.Instance;
        loc.SetLanguage("zh-Hans");

        try
        {
            var stockLogo = Path.Combine(temp, "stock.bmp");
            var sysLogo = Path.Combine(temp, "sys.bmp");
            WriteText(stockLogo, "BMP-STOCK");
            WriteText(sysLogo, "BMP-SYS");

            var options = new WizardOptions
            {
                Atmosphere = true,
                Hekate = true,
                Ultrahand = false,
                SysPatch = false,
                BootStock = true,
                BootSysNand = true,
                BootEmuNand = true,
                BootStockLogo = stockLogo,
                BootSysNandLogo = sysLogo,
                // 故意指向一个不存在的文件：一张图缺失不该让整个生成流程失败
                BootEmuNandLogo = Path.Combine(temp, "no-such-logo.bmp"),
                IncludeComponentFilesInOutput = false,
                IncludePayloads = false,
                IncludeBootDat = false,
            };
            ApplyCatalogDefaults(options);
            var result = Generate(options);

            // ① 图真的被复制进 out/bootloader/res/
            var resDir = Path.Combine(AppPaths.OutputRoot, "bootloader", "res");
            Assert(File.Exists(Path.Combine(resDir, "stock.bmp")), "选中的启动图应被复制到 out/bootloader/res/");
            Assert(File.Exists(Path.Combine(resDir, "sys.bmp")), "第二张启动图也应被复制过去");
            Assert(ReadFileOrEmpty(Path.Combine(resDir, "stock.bmp")) == "BMP-STOCK", "复制过去的应是原图内容");

            // ② logopath 必须是相对 SD 卡根目录的路径。
            //    hekate 按 SD 卡根目录解释这个值，写本机绝对路径在机器上必然找不到图。
            var hekate = ReadOrEmpty("bootloader/hekate_ipl.ini");
            var stockSection = ReadSection(hekate, loc["Boot.Entry.Stock"]);
            Assert(stockSection.Contains("logopath=bootloader/res/stock.bmp"),
                $"正版项应写相对路径 logopath=bootloader/res/stock.bmp（实际：{Describe(stockSection)}）");
            Assert(!stockSection.Contains(temp), "logopath 绝不能写成本机绝对路径");

            var sysSection = ReadSection(hekate, loc["Boot.Entry.SysNand"]);
            Assert(sysSection.Contains("logopath=bootloader/res/sys.bmp"), "真实破解项应指向自己那张图");
            Assert(sysSection.Contains("pkg3=atmosphere/package3"), "加了启动图不该挤掉这一项的其它配置");

            // ③ 文件不存在 → 不写 logopath，但这一项的其它配置照常
            var emuSection = ReadSection(hekate, loc["Boot.Entry.EmuNand"]);
            Assert(!emuSection.Contains("logopath="), "启动图文件不存在时不该写 logopath（写了机器上就是一张空图）");
            Assert(emuSection.Contains("emummcforce=1"), "一张图缺失不该影响这一项的其它配置");

            // ④ 启动图必须进产物清单 —— 清单是清理与打包校验的唯一依据，漏记会被当成垃圾删掉
            Assert(result.WrittenFiles.Any(f => f.Replace('/', '\\')
                    .Equals(Path.Combine("bootloader", "res", "stock.bmp"), StringComparison.OrdinalIgnoreCase)),
                $"启动图应进产物清单，实际清单：{string.Join("、", result.WrittenFiles)}");

            // ⑤ 撞名：两个引导项选**不同目录下的同名图** → 后来者加该引导项的 id 前缀。
            //    不加的话后复制的会盖掉先复制的，两个引导项静默变成同一张图。
            WriteText(Path.Combine(temp, "same.bmp"), "SAME-FROM-A");
            WriteText(Path.Combine(temp2, "same.bmp"), "SAME-FROM-B");

            var clash = CloneFlags(options);
            clash.BootStockLogo = Path.Combine(temp, "same.bmp");
            clash.BootSysNandLogo = Path.Combine(temp2, "same.bmp");
            clash.BootEmuNandLogo = string.Empty;
            ApplyCatalogDefaults(clash);
            var clashResult = Generate(clash);

            var clashHekate = ReadOrEmpty("bootloader/hekate_ipl.ini");
            Assert(File.Exists(Path.Combine(resDir, "same.bmp")), "先复制的同名图应保持原名");
            Assert(File.Exists(Path.Combine(resDir, "zspjxt_same.bmp")),
                "后复制的同名图应加引导项 id 前缀，否则会静默盖掉前一张");
            Assert(ReadFileOrEmpty(Path.Combine(resDir, "same.bmp")) == "SAME-FROM-A", "前一张的内容不该被覆盖");
            Assert(ReadFileOrEmpty(Path.Combine(resDir, "zspjxt_same.bmp")) == "SAME-FROM-B", "后一张应是自己的内容");
            Assert(ReadSection(clashHekate, loc["Boot.Entry.Stock"]).Contains("logopath=bootloader/res/same.bmp"),
                "正版项应指向未加前缀的那张");
            Assert(ReadSection(clashHekate, loc["Boot.Entry.SysNand"]).Contains("logopath=bootloader/res/zspjxt_same.bmp"),
                "真实破解项应指向加了自己 id 前缀的那张");
            Assert(clashResult.WrittenFiles.Count(f => f.Contains("same.bmp", StringComparison.OrdinalIgnoreCase)) == 2,
                "两张撞名的图都应进清单");

            // ⑥ 两个引导项选**同一个文件** → 共用一张，不该多复制一份带前缀的
            var shared = CloneFlags(options);
            shared.BootStockLogo = Path.Combine(temp, "same.bmp");
            shared.BootSysNandLogo = Path.Combine(temp, "same.bmp");
            shared.BootEmuNandLogo = string.Empty;
            ApplyCatalogDefaults(shared);
            Generate(shared);

            var sharedHekate = ReadOrEmpty("bootloader/hekate_ipl.ini");
            Assert(!File.Exists(Path.Combine(resDir, "zspjxt_same.bmp")),
                "两项选了同一个文件时不该再复制一份加前缀的（那是同一张图）");
            Assert(ReadSection(sharedHekate, loc["Boot.Entry.Stock"]).Contains("logopath=bootloader/res/same.bmp")
                   && ReadSection(sharedHekate, loc["Boot.Entry.SysNand"]).Contains("logopath=bootloader/res/same.bmp"),
                "两项选了同一个文件时应指向同一张图");
        }
        finally
        {
            TryDeleteDirectory(temp);
            TryDeleteDirectory(temp2);
        }
    }

    /// <summary>
    /// 引导项图标（Nyx 菜单里的小图标，<c>icon=</c>）。与启动图（<c>logopath=</c>）是同一族，
    /// 共用 <c>AppendBootImage</c> 那套复制/去重/登记逻辑 —— 所以这里重点测**两者共用一套状态**
    /// 才可能出现的问题：跨键撞名、同一文件被两个键共用。
    /// </summary>
    private static void CheckBootIcon()
    {
        Section("hekate 引导项图标：复制进 bootloader/res/ 并写 icon");

        var temp = Path.Combine(Path.GetTempPath(), "cfw-icon-" + Guid.NewGuid().ToString("N"));
        var temp2 = Path.Combine(Path.GetTempPath(), "cfw-icon2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(temp2);

        var loc = LocalizationService.Instance;
        loc.SetLanguage("zh-Hans");

        try
        {
            var stockIcon = Path.Combine(temp, "stock-icon.bmp");
            var sysIcon = Path.Combine(temp, "sys-icon.bmp");
            WriteText(stockIcon, "ICON-STOCK");
            WriteText(sysIcon, "ICON-SYS");

            var options = new WizardOptions
            {
                Atmosphere = true,
                Hekate = true,
                Ultrahand = false,
                SysPatch = false,
                BootStock = true,
                BootSysNand = true,
                BootEmuNand = true,
                BootStockIcon = stockIcon,
                BootSysNandIcon = sysIcon,
                // 故意指向一个不存在的文件：一张图缺失不该让整个生成流程失败
                BootEmuNandIcon = Path.Combine(temp, "no-such-icon.bmp"),
                IncludeComponentFilesInOutput = false,
                IncludePayloads = false,
                IncludeBootDat = false,
            };
            ApplyCatalogDefaults(options);
            var result = Generate(options);

            var resDir = Path.Combine(AppPaths.OutputRoot, "bootloader", "res");

            // ① 图真的被复制进 out/bootloader/res/
            Assert(File.Exists(Path.Combine(resDir, "stock-icon.bmp")), "选中的图标应被复制到 out/bootloader/res/");
            Assert(ReadFileOrEmpty(Path.Combine(resDir, "stock-icon.bmp")) == "ICON-STOCK", "复制过去的应是原图内容");

            // ② icon= 必须是相对 SD 卡根目录的路径（和 logopath 同一个理由）
            var hekate = ReadOrEmpty("bootloader/hekate_ipl.ini");
            var stockSection = ReadSection(hekate, loc["Boot.Entry.Stock"]);
            Assert(stockSection.Contains("icon=bootloader/res/stock-icon.bmp"),
                $"正版项应写相对路径 icon=bootloader/res/stock-icon.bmp（实际：{Describe(stockSection)}）");
            Assert(!stockSection.Contains(temp), "icon 绝不能写成本机绝对路径");

            var sysSection = ReadSection(hekate, loc["Boot.Entry.SysNand"]);
            Assert(sysSection.Contains("icon=bootloader/res/sys-icon.bmp"), "真实破解项应指向自己那个图标");
            Assert(sysSection.Contains("pkg3=atmosphere/package3"), "加了图标不该挤掉这一项的其它配置");

            // ③ 文件不存在 → 不写 icon=，但这一项的其它配置照常
            var emuSection = ReadSection(hekate, loc["Boot.Entry.EmuNand"]);
            Assert(!emuSection.Contains("icon="), "图标文件不存在时不该写 icon（写了 Nyx 只会回退，等于白写）");
            Assert(emuSection.Contains("emummcforce=1"), "一个图标缺失不该影响这一项的其它配置");

            // ④ 图标必须进产物清单 —— 清单是清理与打包校验的唯一依据，漏记会被当成垃圾删掉
            Assert(result.WrittenFiles.Any(f => f.Replace('/', '\\')
                    .Equals(Path.Combine("bootloader", "res", "stock-icon.bmp"), StringComparison.OrdinalIgnoreCase)),
                $"图标应进产物清单，实际清单：{string.Join("、", result.WrittenFiles)}");

            // ⑤ 同一个引导项同时设了启动图和图标 → 两行都要在，互不挤掉
            WriteText(Path.Combine(temp, "both-logo.bmp"), "BOTH-LOGO");
            WriteText(Path.Combine(temp, "both-icon.bmp"), "BOTH-ICON");

            var both = CloneFlags(options);
            both.BootStockLogo = Path.Combine(temp, "both-logo.bmp");
            both.BootStockIcon = Path.Combine(temp, "both-icon.bmp");
            both.BootSysNandLogo = string.Empty;
            both.BootSysNandIcon = string.Empty;
            both.BootEmuNandIcon = string.Empty;
            ApplyCatalogDefaults(both);
            Generate(both);

            var bothSection = ReadSection(ReadOrEmpty("bootloader/hekate_ipl.ini"), loc["Boot.Entry.Stock"]);
            Assert(bothSection.Contains("logopath=bootloader/res/both-logo.bmp")
                   && bothSection.Contains("icon=bootloader/res/both-icon.bmp"),
                $"同一项应同时写出 logopath 与 icon（实际：{Describe(bothSection)}）");

            // ⑥ 跨键撞名：A 项的**启动图**与 B 项的**图标**同名不同目录。
            //    两者落在同一个 bootloader/res/ 目录、共用同一张去重表 —— 不给后来者加前缀的话，
            //    后复制的那张会盖掉先复制的，A 的启动图静默变成 B 的图标。
            WriteText(Path.Combine(temp, "clash.bmp"), "CLASH-AS-LOGO");
            WriteText(Path.Combine(temp2, "clash.bmp"), "CLASH-AS-ICON");

            var cross = CloneFlags(options);
            cross.BootStockLogo = Path.Combine(temp, "clash.bmp");
            cross.BootStockIcon = string.Empty;
            cross.BootSysNandLogo = string.Empty;
            cross.BootSysNandIcon = Path.Combine(temp2, "clash.bmp");
            cross.BootEmuNandIcon = string.Empty;
            ApplyCatalogDefaults(cross);
            var crossResult = Generate(cross);

            Assert(File.Exists(Path.Combine(resDir, "clash.bmp")), "先复制的应保持原名");
            Assert(File.Exists(Path.Combine(resDir, "zspjxt_clash.bmp")),
                "跨键撞名时后来者也要加引导项 id 前缀，否则 A 的启动图会被 B 的图标盖掉");
            Assert(ReadFileOrEmpty(Path.Combine(resDir, "clash.bmp")) == "CLASH-AS-LOGO", "前一张的内容不该被覆盖");
            Assert(ReadFileOrEmpty(Path.Combine(resDir, "zspjxt_clash.bmp")) == "CLASH-AS-ICON", "后一张应是自己的内容");

            var crossHekate = ReadOrEmpty("bootloader/hekate_ipl.ini");
            Assert(ReadSection(crossHekate, loc["Boot.Entry.Stock"]).Contains("logopath=bootloader/res/clash.bmp"),
                "正版项的启动图应指向未加前缀的那张");
            Assert(ReadSection(crossHekate, loc["Boot.Entry.SysNand"]).Contains("icon=bootloader/res/zspjxt_clash.bmp"),
                "真实破解项的图标应指向加了自己 id 前缀的那张");
            Assert(crossResult.WrittenFiles.Count(f => f.Contains("clash.bmp", StringComparison.OrdinalIgnoreCase)) == 2,
                "两张撞名的图都应进清单");

            // ⑦ 同一项的启动图与图标指向**同一个文件** → 共用一张，不该多复制一份带前缀的
            WriteText(Path.Combine(temp, "shared.bmp"), "SHARED-ONE-FILE");

            var shared = CloneFlags(options);
            shared.BootStockLogo = Path.Combine(temp, "shared.bmp");
            shared.BootStockIcon = Path.Combine(temp, "shared.bmp");
            shared.BootSysNandLogo = string.Empty;
            shared.BootSysNandIcon = string.Empty;
            shared.BootEmuNandIcon = string.Empty;
            ApplyCatalogDefaults(shared);
            Generate(shared);

            Assert(!File.Exists(Path.Combine(resDir, "zsxt_shared.bmp")),
                "同一项把同一个文件同时用作启动图与图标时，不该再复制一份加前缀的（那是同一张图）");

            var sharedSection = ReadSection(ReadOrEmpty("bootloader/hekate_ipl.ini"), loc["Boot.Entry.Stock"]);
            Assert(sharedSection.Contains("logopath=bootloader/res/shared.bmp")
                   && sharedSection.Contains("icon=bootloader/res/shared.bmp"),
                $"两个键应指向同一张图（实际：{Describe(sharedSection)}）");
        }
        finally
        {
            TryDeleteDirectory(temp);
            TryDeleteDirectory(temp2);
        }
    }

    /// <summary>⑥ 中文界面走 easyworld/hekate 的本地化包。</summary>
    private static void CheckLocalizedHekateSource()
    {
        Section("中文本地化 hekate 源（easyworld/hekate）");

        // ① 语言判定
        Assert(ComponentCatalog.IsChinese("zh-Hans") && ComponentCatalog.IsChinese("zh-Hant"),
            "简体与繁体都应算中文");
        Assert(!ComponentCatalog.IsChinese("en-US"), "英文界面不该算中文");
        Assert(!ComponentCatalog.IsChinese(null) && !ComponentCatalog.IsChinese("")
               && !ComponentCatalog.IsChinese("   "), "没设置语言时不该算中文");

        // ② 判据**只有**「界面语言是不是中文」——
        //    8G 运存不再把整个组件打回官方（用户 2026-09-18 的原话：
        //    界面是简体中文就该拿到中文 Nyx，跟运存是不是 8G 无关）。
        //    镜像仓库里没有 __ram8GB.bin 这件事，由**另一个固定走官方的请求**补上，
        //    见下面 ③b —— 不是靠「整个组件退回官方」绕开。
        Assert(ComponentCatalog.ShouldUseLocalizedHekate(new WizardOptions { Language = "zh-Hans", Ram8Gb = false }),
            "简体中文应走本地化源");
        Assert(ComponentCatalog.ShouldUseLocalizedHekate(new WizardOptions { Language = "zh-Hant", Ram8Gb = false }),
            "繁体中文应走本地化源");
        Assert(ComponentCatalog.ShouldUseLocalizedHekate(new WizardOptions { Language = "zh-Hans", Ram8Gb = true }),
            "简体中文 + 8G 也该走本地化源 —— 8G 只影响 payload 从哪儿取，没有理由让 Nyx 退回英文");
        Assert(ComponentCatalog.ShouldUseLocalizedHekate(new WizardOptions { Language = "zh-Hant", Ram8Gb = true }),
            "繁体中文 + 8G 也该走本地化源（理由同上）");
        Assert(!ComponentCatalog.ShouldUseLocalizedHekate(new WizardOptions { Language = "en-US", Ram8Gb = false }),
            "英文界面不该走本地化源");
        Assert(!ComponentCatalog.ShouldUseLocalizedHekate(new WizardOptions { Language = "en-US", Ram8Gb = true }),
            "英文界面 + 8G 也不该走本地化源");

        // ③ ResolveRepo 的优先级：用户自定义 > 本地化默认 > 声明默认。
        //    ⚠️ 这里的判据里**不许**再出现 Ram8Gb —— 它出现过一次，就是那个 bug 复活的信号。
        Assert(ComponentCatalog.ResolveRepo(ComponentCatalog.HekateRepo, new WizardOptions { Language = "zh-Hans" })
               == ComponentCatalog.EasyWorldHekateRepo, "中文界面下 hekate 应解析到本地化仓库");
        Assert(ComponentCatalog.ResolveRepo(ComponentCatalog.HekateRepo, new WizardOptions { Language = "en-US" })
               == ComponentCatalog.HekateRepo, "英文界面下 hekate 应仍是官方仓库");
        Assert(ComponentCatalog.ResolveRepo(ComponentCatalog.HekateRepo,
                   new WizardOptions { Language = "zh-Hans", Ram8Gb = true }) == ComponentCatalog.EasyWorldHekateRepo,
            "中文界面 + 8G 时 hekate 包仍应解析到本地化仓库（payload 另有请求负责）");
        Assert(ComponentCatalog.ResolveRepo(ComponentCatalog.AtmosphereRepo, new WizardOptions { Language = "zh-Hans" })
               == ComponentCatalog.AtmosphereRepo, "本地化规则只该影响 hekate，不能牵连别的组件");

        var custom = new WizardOptions { Language = "zh-Hans" };
        custom.RepoOverrides[ComponentCatalog.HekateRepo.DisplayName] = "myfork/hekate";
        Assert(ComponentCatalog.ResolveRepo(ComponentCatalog.HekateRepo, custom).DisplayName == "myfork/hekate",
            "用户自己填的地址优先级最高，要盖过本地化默认");

        // ③b 8G 的「包走镜像 + payload 走官方」是**两个请求**拼出来的（用户 2026-09-18）。
        //
        //     为什么单独立护栏：这条拼法有两个静默坏法 ——
        //     ① 第二个请求忘了钉住官方 ⇒ ResolveRepo 见到 declared == HekateRepo 就把它一起
        //        重定向到镜像去，镜像里没有 .bin，**8G 用户一个 payload 都拿不到**；
        //     ② 第二个请求没被 Applies 挡住 ⇒ 包本来就来自官方时（非中文界面）同一批文件
        //        下两遍、两个 pick 抢同一个落点，日志里还会多一条假的「未找到匹配的资源文件」。
        var hekateRepos = ComponentCatalog.Get(ComponentKind.Hekate).Repos;
        Assert(hekateRepos.Count == 2,
            $"hekate 应由两个请求组成（汉化包 + 固定官方的 8G payload），实际 {hekateRepos.Count} 个");
        Assert(hekateRepos[0].AllowLanguageMirror,
            "取包的那个请求必须参与语言镜像，否则中文界面拿不到汉化包");
        Assert(!hekateRepos[1].AllowLanguageMirror,
            "取 8G payload 的那个请求必须钉住官方地址 —— 否则它会被一起重定向到没有 .bin 的镜像仓库");

        var zh8Options = new WizardOptions { Language = "zh-Hans", Ram8Gb = true };
        var zh4Options = new WizardOptions { Language = "zh-Hans", Ram8Gb = false };
        var en8Options = new WizardOptions { Language = "en-US", Ram8Gb = true };

        Assert(ComponentCatalog.RepoRequestsFor(ComponentCatalog.Get(ComponentKind.Hekate), zh8Options).Count == 2,
            "中文界面 + 8G：两个请求都要查（一个拿汉化包、一个拿官方 payload）");
        Assert(ComponentCatalog.RepoRequestsFor(ComponentCatalog.Get(ComponentKind.Hekate), zh4Options).Count == 1,
            "非 8G：payload 由取包那个请求顺带取走，第二个请求不该被查（白花一次 API 配额）");
        Assert(ComponentCatalog.RepoRequestsFor(ComponentCatalog.Get(ComponentKind.Hekate), en8Options).Count == 1,
            "非中文界面 + 8G：包本身就来自官方，第二个请求不该被查（否则同一批文件下两遍）");

        // ④ 按语言后缀挑包。zh-Hant 也以 "zh" 开头，判反了繁体用户会**静默**拿到简体界面。
        var picker = ComponentCatalog.Get(ComponentKind.Hekate).Repos[0].Picker;
        var localized = new ReleaseInfo("v6.5.3", "hekate", false, DateTimeOffset.UnixEpoch, string.Empty,
        [
            new ReleaseAsset("hekate_ctcaer_6.5.3_Nyx_1.9.3_sc.zip", "u1", 1),
            new ReleaseAsset("hekate_ctcaer_6.5.3_Nyx_1.9.3_tc.zip", "u2", 2),
        ]);

        var sc = picker(localized, new WizardOptions { Language = "zh-Hans" });
        Assert(sc.Any(p => p.FileName.EndsWith("_sc.zip", StringComparison.OrdinalIgnoreCase)),
            $"简体界面应挑 _sc 包，实际：{string.Join("、", sc.Select(p => p.FileName))}");
        Assert(!sc.Any(p => p.FileName.EndsWith("_tc.zip", StringComparison.OrdinalIgnoreCase)),
            "简体界面不该同时挑上繁体包");

        var tc = picker(localized, new WizardOptions { Language = "zh-Hant" });
        Assert(tc.Any(p => p.FileName.EndsWith("_tc.zip", StringComparison.OrdinalIgnoreCase)),
            $"繁体界面应挑 _tc 包，实际：{string.Join("、", tc.Select(p => p.FileName))}");
        Assert(!tc.Any(p => p.FileName.EndsWith("_sc.zip", StringComparison.OrdinalIgnoreCase)),
            "繁体界面不该挑到简体包（zh-Hant 也以 zh 开头，判反了会静默给简体）");

        // ⑤ 官方源（只有 .bin、没有本地化包）时行为与以前完全一致
        var official = new ReleaseInfo("v6.5.3", "hekate", false, DateTimeOffset.UnixEpoch, string.Empty,
        [
            new ReleaseAsset("hekate_ctcaer_6.5.3_Nyx_1.9.3.zip", "u1", 1),
            new ReleaseAsset("hekate_ctcaer_6.5.3.bin", "u2", 2),
            new ReleaseAsset("hekate_ctcaer_6.5.3_ram8GB.bin", "u3", 3),
        ]);

        var zhOfficial = picker(official, new WizardOptions { Language = "zh-Hans" });
        Assert(zhOfficial.Any(p => p.FileName == "hekate_ctcaer_6.5.3_Nyx_1.9.3.zip"),
            "该 Release 没有本地化包时应回落到通用 zip 规则（同一个 picker 服务两个源）");
        Assert(zhOfficial.Any(p => p.IsPayload && p.FileName == "hekate_ctcaer_6.5.3.bin"),
            "非 8G 时应取标准版 payload");

        var zh8 = picker(official, new WizardOptions { Language = "zh-Hans", Ram8Gb = true });
        Assert(zh8.Any(p => p.IsPayload && p.FileName == "hekate_ctcaer_6.5.3_ram8GB.bin"),
            "8G 时应取官方 __ram8GB.bin payload");

        // ⑤b payload 落点契约（用户 2026-09-18 明确要求）：
        //   非 8G → 根目录**保留原文件名** + 复制成 payload.bin + bootloader/update.bin（三份）
        //   8G   → 8G 版只落 payload.bin + bootloader/update.bin（根目录不留，免得与标准版混淆）；
        //          标准版让位到 bootloader/payloads/
        // 关键点：以前「根目录那份」只是 hekate 的 zip 包**碰巧**带来的（包里自带同名文件）。
        // 那不是保证 —— 关掉「合并组件文件」后包就不解压，根目录的原名 payload 静默消失。
        // 所以必须由 picker **显式声明**这个落点。
        var std4G = zhOfficial.First(p => p.IsPayload && p.FileName == "hekate_ctcaer_6.5.3.bin");
        Assert(std4G.ResolvedTargets.Any(t => t.Directory.Length == 0 && string.IsNullOrEmpty(t.FileName)),
            "非 8G 时根目录应保留原名的那一份 payload（用户要求「保留在根目录」）");
        Assert(std4G.ResolvedTargets.Any(t => t.FileName == "payload.bin"),
            "非 8G 时应同时复制成 out/payload.bin");
        Assert(std4G.ResolvedTargets.Any(t => t.Directory == "bootloader" && t.FileName == "update.bin"),
            "非 8G 时应同时复制成 out/bootloader/update.bin");

        var ram8 = zh8.First(p => p.IsPayload && p.FileName == "hekate_ctcaer_6.5.3_ram8GB.bin");
        Assert(ram8.ResolvedTargets.Count == 2
               && ram8.ResolvedTargets.Any(t => t.FileName == "payload.bin")
               && ram8.ResolvedTargets.Any(t => t.Directory == "bootloader" && t.FileName == "update.bin"),
            "8G 版 payload 应只落 out/payload.bin 与 out/bootloader/update.bin 两处"
            + "（根目录留标准版会让人分不清按哪个起）；实际："
            + string.Join("、", ram8.ResolvedTargets.Select(t => t.Resolve("x"))));
        Assert(zh8.Any(p => p.IsPayload && p.FileName == "hekate_ctcaer_6.5.3.bin"
                            && p.ResolvedTargets.Any(t => t.Directory == "bootloader/payloads")),
            "8G 时标准版 payload 应让位到 bootloader/payloads/（可在 hekate 的 payload 菜单里选中）");

        // ⑤c **语言只该改变包的来源，不该改变 payload 的落点**（用户 2026-09-18）。
        //
        //     中文 + 8G 走的是「镜像的汉化包 + 官方补的 8G payload」两段拼装，
        //     非中文 + 8G 走的是「官方一次拿齐」。两条路的 payload（含落点、含「额外保留的标准版」）
        //     必须**逐项相同** —— 否则同一个「8G 运存」开关会因为界面语言不同产出不同布局，
        //     而那种差异在日志里完全看不出来。
        var patchPicker = ComponentCatalog.Get(ComponentKind.Hekate).Repos[1].Picker;

        // 镜像仓库的真实形态：只有 _sc / _tc 两个 zip，一个 .bin 都没有。
        var mirrorRelease = new ReleaseInfo("v6.5.3", "hekate", false, DateTimeOffset.UnixEpoch, string.Empty,
        [
            new ReleaseAsset("hekate_ctcaer_6.5.3_Nyx_1.9.3_sc.zip", "u1", 1),
            new ReleaseAsset("hekate_ctcaer_6.5.3_Nyx_1.9.3_tc.zip", "u2", 2),
        ]);
        Assert(!mirrorRelease.Assets.Any(a => a.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)),
            "前置条件：汉化镜像仓库里不该有 .bin，否则这条用例覆盖不到「8G payload 必须回官方取」");

        static List<string> PayloadTargets(IEnumerable<AssetPick> picks) => picks
            .Where(p => p.IsPayload)
            .SelectMany(p => p.ResolvedTargets.Select(t => t.Resolve(p.FileName)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var mirrored = PayloadTargets(
            picker(mirrorRelease, zh8Options).Concat(patchPicker(official, zh8Options)));
        var straightOfficial = PayloadTargets(picker(official, en8Options));

        Assert(mirrored.SequenceEqual(straightOfficial, StringComparer.Ordinal),
            "中文界面 + 8G（镜像包 + 官方 payload）与非中文界面 + 8G（官方一次拿齐）的 payload 落点必须逐项相同；"
            + $"实际镜像那路：{string.Join("、", mirrored)}；官方那路：{string.Join("、", straightOfficial)}");

        // ⑥ 8G 回退（2026-09-17 审计发现；用户同日决定**方案 A：告警，不报错**）。
        //    勾了 8G、但这个 Release 里没有 __ram8GB.bin 时，picker 会退回标准版 —— 而
        //    `exosphere.ini` 照样写 `enable_mem_mode=1`、引导项照样写 `memmode=1`，
        //    于是产物呈现「配置说 8G、payload 是 4G」。
        //
        //    这条分支在真实下载里很难触发（官方 hekate 一直带 __ram8GB.bin），所以此前**从未被走到**；
        //    而 picker 的委托签名 `Func<ReleaseInfo, WizardOptions, IReadOnlyList<AssetPick>>` 里
        //    **没有 logger**，就地告警也不可行 —— 所以「判定」留在 ComponentCatalog 的纯函数
        //    `IsRam8GbPayloadMissing` 里，「告警」放在 MainViewModel 的 picker 调用点（有日志出口）。
        //    这里钉住**回退行为本身**（它仍然回退，只是不再静默）；判定与告警的护栏见
        //    CheckRam8GbPayloadWarning()。
        var no8G = new ReleaseInfo("v6.5.3", "hekate", false, DateTimeOffset.UnixEpoch, string.Empty,
        [
            new ReleaseAsset("hekate_ctcaer_6.5.3_Nyx_1.9.3.zip", "u1", 1),
            new ReleaseAsset("hekate_ctcaer_6.5.3.bin", "u2", 2),
        ]);

        // 前置条件：这个 Release 里确实没有 8G 版，否则下面两条会退化成空跑
        Assert(no8G.Assets.All(a => !a.Name.Contains("ram8GB", StringComparison.OrdinalIgnoreCase)),
            "前置条件：构造的 Release 里不应有 __ram8GB.bin，否则这条回退用例没有意义");

        var fallback = picker(no8G, new WizardOptions { Language = "zh-Hans", Ram8Gb = true });
        Assert(fallback.Any(p => p.IsPayload && p.FileName == "hekate_ctcaer_6.5.3.bin"),
            "勾了 8G 但发布里没有 __ram8GB.bin 时，picker 仍退回标准版 payload（方案 A 只要求告警，不要求中止）"
            + $"；实际选中：{string.Join("、", fallback.Select(p => p.FileName))}");
        Assert(fallback.Count(p => p.IsPayload) == 1,
            "回退时标准版只应作为一个 payload 出现一次（不能既当主 payload、又当「额外保留」再来一份）");
    }

    // ── 8G 运存拿不到 8G payload 时的告警（用户 2026-09-17 决定 A：告警，不报错）──
    /// <summary>
    /// 勾了 8G 运存、但选中的 Release 里没有 <c>__ram8GB.bin</c> 时，必须**告警**，不能静默退回。
    ///
    /// 为什么不能只靠行为测试：判定是纯函数（可以穷举），但**告警本身**发生在下载循环里 ——
    /// 真实触发需要网络 + 一个恰好不带 8G payload 的发布，行为测试够不着。
    /// 所以这里分两半：① 穷举纯函数的四个方向；② 用**源码契约**钉住「MainViewModel 必须调它、
    /// 必须打 Warning、且必须被 IncludePayloads 挡住」（同 <c>CheckFirstRunSelectionContract</c>
    /// 里对 <c>EnsureComponentsSelected()</c> 的做法）。
    ///
    /// ⚠️ 只钉住「有调用」是不够的：把 <c>LogLevel.Warning</c> 改成 <c>Info</c>，
    /// 用户就再也看不见这条告警，而所有断言照样全绿 —— 所以级别也要一起钉。
    /// </summary>
    private static void CheckRam8GbPayloadWarning()
    {
        Section("8G 运存拿不到 8G payload：必须告警（用户 2026-09-17 决定 A）");

        // ① 判定的四个方向。判据写反（比如漏了 `!options.Ram8Gb`）比不告警更糟：
        //    没勾 8G 的正常用户会天天看到假告警，真告警就没人看了。
        var with8G = new ReleaseInfo("v6.5.3", "hekate", false, DateTimeOffset.UnixEpoch, string.Empty,
        [
            new ReleaseAsset("hekate_ctcaer_6.5.3.bin", "u1", 1),
            new ReleaseAsset("hekate_ctcaer_6.5.3_ram8GB.bin", "u2", 2),
        ]);

        var without8G = new ReleaseInfo("v6.5.3", "hekate", false, DateTimeOffset.UnixEpoch, string.Empty,
        [
            new ReleaseAsset("hekate_ctcaer_6.5.3.bin", "u1", 1),
        ]);

        // 一个 .bin 都没有（比如只有本地化 zip）：属于另一种情况，不该被这条判成「8G 版缺失」
        var noBin = new ReleaseInfo("v6.5.3", "hekate", false, DateTimeOffset.UnixEpoch, string.Empty,
        [
            new ReleaseAsset("hekate_ctcaer_6.5.3_Nyx_1.9.3_sc.zip", "u1", 1),
        ]);

        Assert(!ComponentCatalog.IsRam8GbPayloadMissing(with8G, new WizardOptions { Ram8Gb = true }),
            "发布里带 __ram8GB.bin 时不该告警（假告警多了，真告警就没人看了）");
        Assert(ComponentCatalog.IsRam8GbPayloadMissing(without8G, new WizardOptions { Ram8Gb = true }),
            "勾了 8G 而发布里没有 __ram8GB.bin 时**必须**告警 —— 这是用户 2026-09-17 的方案 A");
        Assert(!ComponentCatalog.IsRam8GbPayloadMissing(without8G, new WizardOptions { Ram8Gb = false }),
            "没勾 8G 时不该告警 —— picker 走的就是标准版，一切正常");
        Assert(!ComponentCatalog.IsRam8GbPayloadMissing(noBin, new WizardOptions { Ram8Gb = true }),
            "发布里一个 .bin 都没有属于另一种情况（「连 payload 都没有」另有告警），"
            + "不该被这条判成「8G 版缺失」—— 两条告警同时打，用户不知道该看哪条");

        // 非 hekate 的 payload 也不算数：Atmosphere 的 `fusee.bin` 是 .bin，但它永远不会有 8G 版。
        // 判据只看 ".bin" 后缀的话，勾了 8G 就会对**每个组件**都打一条「没有 __ram8GB.bin」的假告警
        // —— 2026-09-18 实测到的误报（e2e 日志里 Atmosphere 那一行就是这么来的）。
        // 假告警多了，真告警就没人看了。
        var atmosphereLike = new ReleaseInfo("1.11.2", "Atmosphere", false, DateTimeOffset.UnixEpoch, string.Empty,
        [
            new ReleaseAsset("atmosphere-1.11.2-master-abc+hbl-2.4.4+hbmenu-3.6.0.zip", "u1", 1),
            new ReleaseAsset("fusee.bin", "u2", 2),
        ]);

        Assert(!ComponentCatalog.IsRam8GbPayloadMissing(atmosphereLike, new WizardOptions { Ram8Gb = true }),
            "只有 fusee.bin 的组件（Atmosphere）不该被判定成「缺 8G payload」—— "
            + "那会给每个组件都打一条假告警，把真告警淹掉");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到源码目录，无法核对 8G 告警的源码契约");
            return;
        }

        // ② 命名判据只能有一份。这个带引号的字面量在 src 里出现第二次，就说明有人又手抄了一遍规则 ——
        //    上游改命名时两处会**悄悄脱节**：一处认、一处不认，而两边都不报错。
        //    这是本项目教训 ④（手抄的清单自己会腐烂）。
        //    只看**代码行**：注释里提到它（`// 别在这里再写一份 ram8GB 字面量`）不算实现，
        //    注释行一并剔除，否则改注释都会莫名变红。
        var literalSites = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var relative = Path.GetRelativePath(sourceRoot, file);
                return !relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            })
            .SelectMany(file => File.ReadAllLines(file)
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
                .Where(line => line.Contains("\"ram8GB\"", StringComparison.Ordinal))
                .Select(line => $"{Path.GetFileName(file)}: {line.Trim()}"))
            .ToList();

        Assert(literalSites.Count == 1 && literalSites[0].StartsWith("ComponentCatalog.cs:", StringComparison.Ordinal),
            "「8G 版 payload」的命名判据只应有一份，且必须落在 ComponentCatalog.IsRam8GbPayloadName 里"
            + "（picker 挑 payload、8G 缺失告警、ConfigGenerator 挑/删根目录 payload 都走它）；"
            + $"实际 {literalSites.Count} 处：" + string.Join("；", literalSites));

        // ③ 源码契约：告警必须真的挂在下载流程里。
        //    删掉调用点、或把 Warning 降成 Info，都不会有任何行为测试变红 —— 只能靠这一条。
        var vmSource = File.ReadAllLines(Path.Combine(sourceRoot, "ViewModels", "MainViewModel.cs"))
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
            .ToList();

        var callLines = vmSource
            .Select((line, index) => (Line: line, Index: index))
            .Where(item => item.Line.Contains("ComponentCatalog.IsRam8GbPayloadMissing(", StringComparison.Ordinal))
            .ToList();

        Assert(callLines.Count == 1,
            "MainViewModel 里 ComponentCatalog.IsRam8GbPayloadMissing 应恰好有一处调用"
            + $"（下载循环只有一处；多了说明逻辑分散，少了说明告警被删了）；实际 {callLines.Count} 处");
        if (callLines.Count != 1)
        {
            return;
        }

        var callLine = callLines[0];
        Assert(callLine.Line.Contains("IncludePayloads", StringComparison.Ordinal),
            "这条告警必须被 IncludePayloads 挡住 —— 不往 out 里放 payload 时根本没有「payload 版本错位」，"
            + "此时打扰用户只会稀释真告警");

        // 告警语句在调用行的下一行起（`if (...) { Log(LogLevel.Warning, ...); }`），看一个 4 行的小窗口
        var window = vmSource.Skip(callLine.Index).Take(4).ToList();
        Assert(window.Any(l => l.Contains("LogLevel.Warning", StringComparison.Ordinal)),
            "命中「拿不到 8G payload」时必须以 Warning 级别打日志 —— 降成 Info 就等于没告警"
            + "（用户看不见，产物却是「配置说 8G、payload 是 4G」）");
        Assert(!window.Any(l => l.Contains("LogLevel.Error", StringComparison.Ordinal)),
            "方案 A 要的是**告警**不是报错：8G 缺失只影响 payload 版本，其余文件仍然可用，"
            + "报错会让整次生成失败、用户拿不到任何产物");
    }

    // ── Ultrahand picker 的资源挑选：语言包绝不能被当成整包 SD 内容 ──
    /// <summary>
    /// 上游 <c>Ultrahand-Overlay</c> 的 Release 里**同时**有 <c>sdout.zip</c>（整套 SD 内容）
    /// 与 <c>lang.zip</c>（14 个语言 json），而且 <c>assets</c> 是**按名字排序**返回的 ——
    /// <c>lang.zip</c> 排在 <c>sdout.zip</c> **前面**。所以「<c>sdout.zip</c> 找不到就随便拿一个 zip」
    /// 拿到的是语言包：14 个 json 被当成整套 SD 内容铺到 <c>out/</c> 根目录，Ultrahand 本体一个文件都没有。
    ///
    /// 这里用**上游真实的资源名与真实顺序**构造 ReleaseInfo（v2.5.3 实测顺序：
    /// <c>lang.zip</c> → <c>ovlmenu.ovl</c> → <c>sdout.zip</c>），把两个方向都钉住。
    ///
    /// ⚠️ 为什么这条必须靠**用例**而不是「运行期检查」：语言包合并进来照样让
    /// 「已合并 N 个组件文件」> 0，逐组件对账也过 —— 产物是一张缺 Ultrahand 的 SD 卡，
    /// 而日志里一片祥和。
    /// </summary>
    private static void CheckUltrahandPickerAssets()
    {
        Section("Ultrahand 资源挑选：语言包 ≠ SD 包（上游资源按名排序，lang.zip 排在 sdout.zip 前面）");

        static ReleaseInfo Make(params string[] names) =>
            new("v2.5.3", "Ultrahand Overlay 2.5.3", false, DateTimeOffset.UnixEpoch, string.Empty,
                names.Select((n, i) => new ReleaseAsset(n, $"u{i}", i + 1)).ToList());

        // 上游真实的三个资源 + 真实顺序（GitHub API 按名字排序）
        var full = Make("lang.zip", "ovlmenu.ovl", "sdout.zip");

        // 前置条件：这个顺序就是缺口成立的前提，先把它钉住 —— 顺序变了，下面那条用例就失去意义
        Assert(full.Assets[0].Name == "lang.zip" && full.Assets[2].Name == "sdout.zip",
            "前置条件：构造的资源顺序应为 lang.zip → ovlmenu.ovl → sdout.zip（与上游实际返回一致）");

        var picker = ComponentCatalog.Get(ComponentKind.Ultrahand).Repos[0].Picker;

        // ① 默认（整包安装、不装语言包）→ 只挑 sdout.zip
        var normal = picker(full, new WizardOptions { Ultrahand = true });
        Assert(normal.Count == 1 && normal[0].FileName == "sdout.zip",
            $"整包安装时只应挑 sdout.zip，实际：{string.Join("、", normal.Select(p => p.FileName))}");

        // ② 勾了语言包 → sdout.zip 与 lang.zip 都要下，且两者落点不同
        var langOptions = new WizardOptions { Ultrahand = true };
        langOptions.Set(ComponentKind.Ultrahand, "installLang", "1");
        var withLang = picker(full, langOptions);

        var langPick = withLang.FirstOrDefault(p => p.FileName == "lang.zip");
        Assert(langPick is not null,
            $"勾了语言包时应挑上 lang.zip，实际：{string.Join("、", withLang.Select(p => p.FileName))}");
        Assert(langPick is not null && langPick.Targets is not null,
            "语言包的落点必须**显式声明**（UltrahandLangTargets）—— 不声明就会铺到 out 根目录");
        Assert(withLang.Any(p => p.FileName == "sdout.zip"),
            "勾了语言包**不该**把 sdout.zip 挤掉，两个都要下");

        // ③ 缺口用例：sdout.zip 不在（Release 刚发布、资源还在逐个上传）→ **绝不能拿 lang.zip 顶替**
        var missingSdout = Make("lang.zip", "ovlmenu.ovl");
        var degraded = picker(missingSdout, new WizardOptions { Ultrahand = true });
        Assert(degraded.All(p => p.FileName != "lang.zip"),
            "sdout.zip 缺失时**绝不能**退回 lang.zip —— 那会把 14 个语言 json 当成整套 SD 内容铺到 out 根目录，"
            + "而 Ultrahand 本体一个文件都没有（而且没有任何检查会报）"
            + $"；实际选中：{string.Join("、", degraded.Select(p => p.FileName))}");
        Assert(degraded.Count == 0,
            "sdout.zip 缺失且没勾语言包时应当一个资源都不挑（交给既有的「未找到匹配的资源文件」告警），"
            + $"实际：{string.Join("、", degraded.Select(p => p.FileName))}");

        // ④ 手动安装模式：挑 ovlmenu.ovl，不受语言包影响
        var manualOptions = new WizardOptions { Ultrahand = true };
        manualOptions.Set(ComponentKind.Ultrahand, "installMode", "manual");
        var manual = picker(full, manualOptions);
        Assert(manual.Count == 1 && manual[0].FileName == "ovlmenu.ovl",
            $"手动安装时应只挑 ovlmenu.ovl，实际：{string.Join("、", manual.Select(p => p.FileName))}");
    }

    /// <summary>⑦ 各插件的下载源可改。</summary>
    private static void CheckRepoSources()
    {
        Section("下载源可配置（高级设置）");

        // ① 地址解析：容错用户手输的常见形态
        Assert(RepoSpec.TryParse("owner/name")?.DisplayName == "owner/name", "owner/name 应能解析");
        Assert(RepoSpec.TryParse("  owner/name  ")?.DisplayName == "owner/name", "前后空格应被忽略");
        Assert(RepoSpec.TryParse("https://github.com/owner/name")?.DisplayName == "owner/name", "完整 URL 应能解析");
        Assert(RepoSpec.TryParse("https://github.com/owner/name/")?.DisplayName == "owner/name", "末尾斜杠应能解析");
        Assert(RepoSpec.TryParse("https://github.com/owner/name.git")?.DisplayName == "owner/name", "末尾 .git 应能解析");
        Assert(RepoSpec.TryParse("github.com/owner/name")?.DisplayName == "owner/name", "省略协议的写法应能解析");

        // ② 解析不出来时**返回 null，不做近似纠正**。
        //    猜错的代价是把组件指向一个陌生仓库 —— 宁拒绝，不猜错。
        Assert(RepoSpec.TryParse(null) is null && RepoSpec.TryParse("") is null
               && RepoSpec.TryParse("   ") is null, "空值应返回 null");
        Assert(RepoSpec.TryParse("just-a-name") is null, "只有一段的写法应被拒绝");
        Assert(RepoSpec.TryParse("owner/name/releases") is null, "多出来的路径段应被拒绝（不猜）");
        Assert(RepoSpec.TryParse("/") is null, "只有斜杠应被拒绝");

        // ③ 界面一行：非法地址就地标红，运行时回落到默认地址（不炸掉整个流程）
        var repo = ComponentCatalog.HekateRepo.DisplayName;
        var invalid = new RepoSourceViewModel(repo, "Hekate", null, "这不是地址");
        Assert(invalid.HasCustomValue && invalid.IsInvalid, "填了非法地址应被判定为非法并标红");
        Assert(invalid.EffectiveValue == repo, "非法地址的生效值应是默认地址");

        var blank = new RepoSourceViewModel(repo, "Hekate", null, string.Empty);
        Assert(!blank.HasCustomValue && !blank.IsInvalid, "留空 = 用默认，不该标红");
        Assert(blank.EffectiveValue == repo, "留空时生效值是默认地址");

        var same = new RepoSourceViewModel(repo, "Hekate", null, repo);
        Assert(!same.HasCustomValue,
            "填了与默认一模一样的地址不该算「改过」（否则 settings.json 里全是噪音，将来改默认值也跟不上）");

        var custom = new RepoSourceViewModel(repo, "Hekate", null, "myfork/hekate");
        Assert(custom.HasCustomValue && custom.EffectiveValue == "myfork/hekate", "改了地址应生效");

        // ④ 界面清单与目录里的仓库声明必须**双向一一对应**：
        //    少一行 → 用户改不了那个源；多一行 → 界面上的死控件。
        //    两侧清单都从目录取（声明用反射扫字段），不手抄 —— 手抄的清单自己会腐烂。
        var rows = ComponentCatalog.RepoRows;
        var declared = ComponentCatalog.DeclaredRepos;
        Assert(declared.Count > 0, "前置条件：目录里应声明了至少一个仓库，否则下面的比对恒真");

        var orphans = rows.Where(r => r.Declared is null).Select(r => r.Row.Key).ToList();
        Assert(orphans.Count == 0,
            "下载源的每一行都应能反查到目录里的仓库声明（孤儿行 = 界面上的死控件）"
            + (orphans.Count == 0 ? "" : "；实际没有来源的：" + string.Join("、", orphans)));

        // ④b 「声明的仓库必须有个能改它的入口」—— 但入口有**两种**，不是一种：
        //     ① 「下载源」栏里占一行（框架那 7 行）；
        //     ② 出现在某个组件的**文件槽**里（19 个插件类仓库 —— 它们的地址与「具体哪个文件」
        //        绑在一起，放在插件自己的分组里才说得清，硬塞进下载源栏会变成 26 行大杂烩）。
        //
        //     ⚠️ 判据仍然从**真源**取：槽来自 ComponentCatalog.All 的 Slots，不手抄。
        //     反过来说，这条断言的意义正是「加了仓库却没有任何入口改它」会当场报错。
        var slotRepos = ComponentCatalog.All
            .SelectMany(d => d.Slots)
            .SelectMany(s => UiLanguageCodes.Select(l => s.Address(l)))
            .Select(r => r.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unlisted = declared
            .Where(d => rows.All(r => r.Row.Key != d.DisplayName) && !slotRepos.Contains(d.DisplayName))
            .Select(d => d.DisplayName).ToList();
        Assert(unlisted.Count == 0,
            "目录里声明的每个仓库都应有能改它的入口（下载源栏一行，或某个组件的文件槽）"
            + (unlisted.Count == 0 ? "" : "；实际两个入口都没有的：" + string.Join("、", unlisted)));

        // ④c 反向：槽里引用的仓库必须是**声明过的字段**（反射扫得到）。
        //     写成槽里内联 `new RepoSpec("a","b")` 就绕过了 DeclaredRepos，于是 ④b 那条
        //     「声明的仓库都有入口」检查**看不见它**，而它同样是个能改地址的入口 ——
        //     两份清单从此不再对得上。RepoSpec 是 record（值相等），所以这里比的是值。
        var declaredSet = declared.ToHashSet();
        var undeclaredSlotRepos = ComponentCatalog.All
            .SelectMany(d => d.Slots.SelectMany(s =>
                UiLanguageCodes.Select(l => (d.Kind, s.Key, Repo: s.Address(l)))))
            .Where(x => !declaredSet.Contains(x.Repo))
            .Select(x => $"{x.Kind}/{x.Key} → {x.Repo.DisplayName}")
            .ToList();
        Assert(undeclaredSlotRepos.Count == 0,
            "文件槽引用的仓库都应声明成 ComponentCatalog 的静态字段（内联 new RepoSpec 会绕过仓库清单）"
            + (undeclaredSlotRepos.Count == 0 ? "" : "；实际没声明的：" + string.Join("、", undeclaredSlotRepos)));

        var dupes = rows.GroupBy(r => r.Row.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert(dupes.Count == 0,
            "下载源的行键不能重复（Key 就是 settings.json 里的键，重复会让两行互相盖掉）"
            + (dupes.Count == 0 ? "" : "；重复的：" + string.Join("、", dupes)));

        // ⑤ 每个声明了 HintKey 的下载源，提示文案必须真的取得到（不能漏出原始键名）。
        //    注意这条走索引器，**会被回退掩盖** —— 「单独一份包缺键」由 CheckLanguagePackKeyParity
        //    查原始键集来兜（那是权威判据）；这里只负责「界面上别漏出 Settings.Repo.* 这种原始键名」。
        //    ⚠️ 断言描述在**通过时也会原样打印**，所以必须写成陈述句；写成失败理由会让人把 ✓ 读成故障。
        foreach (var source in ComponentCatalog.RepoSources.Where(s => s.HintKey is not null))
        {
            var text = LocalizationService.Instance[source.HintKey!];
            Assert(!string.IsNullOrWhiteSpace(text) && text != source.HintKey,
                $"下载源 {source.Name} 的提示键 {source.HintKey} 应能取到文案（取不到会漏出原始键名）");
        }

        // ⑥ 逐行验证「填了真的会被采纳」—— 本用例的核心。
        //    加一行到清单里很容易，让 ResolveRepo 真的读它才难；不读的话界面上一切正常、
        //    下载时纹丝不动。所以这里对**每一行**都做一次探针。
        foreach (var (row, declaredRepo) in ComponentCatalog.RepoRows)
        {
            var probe = new WizardOptions { Language = "en-US" };
            probe.RepoOverrides[row.Key] = "probe-owner/probe-repo";
            var actual = ComponentCatalog.ResolveRepo(declaredRepo!, probe);
            Assert(actual.DisplayName == "probe-owner/probe-repo",
                $"下载源「{row.Name}」填的地址应被采纳（实际解析成 {actual.DisplayName}）");
        }

        // ⑦ hekate 两行的组合语义。它是唯一一个「一行仓库名 ≠ 实际仓库」的特例，
        //    规则必须钉死，否则两行都填时就成了「谁赢看运气」。
        var hekateOfficial = ComponentCatalog.HekateRepo;
        var hekateMirror = ComponentCatalog.EasyWorldHekateRepo;

        // 没填任何一行 → 回落到「中文用镜像」的老规矩
        Assert(ComponentCatalog.ResolveRepo(hekateOfficial, new WizardOptions { Language = "zh-Hans" }) == hekateMirror,
            "中文界面且两行都没填时，hekate 应默认走 easyworld 汉化包");
        Assert(ComponentCatalog.ResolveRepo(hekateOfficial, new WizardOptions { Language = "en-US" }) == hekateOfficial,
            "非中文界面且两行都没填时，hekate 应走官方源");

        // 8G 运存**不再**把包打回官方（汉化包里没有 __ram8GB.bin 这件事由另一个请求补，
        // 见 CheckLocalizedHekateSource ③b/⑤c）
        Assert(ComponentCatalog.ResolveRepo(hekateOfficial, new WizardOptions { Language = "zh-Hans", Ram8Gb = true })
               == hekateMirror,
            "勾了 8G 运存时，hekate 的包仍应走汉化镜像（payload 另有请求从官方取）");

        // 而那个「取 8G payload」的请求：关掉镜像规则，但**用户填的地址照旧生效**。
        // 少了后一半，用户在官方那一行填了自定义地址后，8G 那一路会硬走 CTCaer/hekate ——
        // 界面上一切正常，下载时纹丝不动，正是「死控件」那一类静默故障。
        var official8 = new WizardOptions { Language = "zh-Hans", Ram8Gb = true };
        official8.RepoOverrides[hekateOfficial.DisplayName] = "myfork/hekate";
        Assert(ComponentCatalog.ResolveRepo(hekateOfficial, official8, allowLanguageMirror: false).DisplayName
               == "myfork/hekate",
            "取 8G payload 的请求只该关掉「按语言换镜像」，用户在官方那一行填的地址仍须生效");

        var mirrorRow8 = new WizardOptions { Language = "zh-Hans", Ram8Gb = true };
        mirrorRow8.RepoOverrides[hekateMirror.DisplayName] = "myfork/hekate";
        Assert(ComponentCatalog.ResolveRepo(hekateOfficial, mirrorRow8, allowLanguageMirror: false) == hekateOfficial,
            "取 8G payload 的请求不该被镜像行的地址改道 —— 那一行是「语言镜像」入口，"
            + "汉化仓库里没有 __ram8GB.bin，跟过去就等于没有 8G payload");

        // 只填镜像行 → 英文界面也走用户填的那个地址（这正是「拆成两行」的意义：
        // 原本英文界面根本没法用汉化包，现在能了）
        var onlyMirror = new WizardOptions { Language = "en-US" };
        onlyMirror.RepoOverrides[hekateMirror.DisplayName] = "myfork/hekate";
        Assert(ComponentCatalog.ResolveRepo(hekateOfficial, onlyMirror).DisplayName == "myfork/hekate",
            "只填了 easyworld 那一行时，英文界面也应采用它（否则这一行对非中文用户是死的）");

        // 只填官方行 → 中文界面也用官方（同样是原本做不到的事）
        var onlyOfficial = new WizardOptions { Language = "zh-Hans" };
        onlyOfficial.RepoOverrides[hekateOfficial.DisplayName] = "myfork/hekate";
        Assert(ComponentCatalog.ResolveRepo(hekateOfficial, onlyOfficial).DisplayName == "myfork/hekate",
            "只填了官方那一行时，中文界面也应采用它");

        // 两行都填 → 官方优先（顺序固定、不猜；提示文案里也这么写）
        var bothFilled = new WizardOptions { Language = "zh-Hans" };
        bothFilled.RepoOverrides[hekateOfficial.DisplayName] = "official-fork/hekate";
        bothFilled.RepoOverrides[hekateMirror.DisplayName] = "mirror-fork/hekate";
        Assert(ComponentCatalog.ResolveRepo(hekateOfficial, bothFilled).DisplayName == "official-fork/hekate",
            "两行都填了自定义地址时应以官方那行为准（顺序必须固定，不能看运气）");

        // ⑧ 用户填的地址真的会被用上；解析不出来时回落到默认地址而不是让「开始」直接失败
        var options = new WizardOptions { Language = "en-US" };
        options.RepoOverrides[ComponentCatalog.SysPatchRepo.DisplayName] = "https://github.com/other/sys-patch";
        Assert(ComponentCatalog.ResolveRepo(ComponentCatalog.SysPatchRepo, options).DisplayName == "other/sys-patch",
            "用户填的 URL 应被解析并用于实际下载");

        options.RepoOverrides[ComponentCatalog.SysPatchRepo.DisplayName] = "乱七八糟";
        Assert(ComponentCatalog.ResolveRepo(ComponentCatalog.SysPatchRepo, options) == ComponentCatalog.SysPatchRepo,
            "用户填的地址解析不出来时应回落到默认地址");
    }

    /// <summary>
    /// 「文件槽」（<see cref="AssetSlot"/>）机制 —— 19 个插件类组件共用的那套。
    ///
    /// 用户 2026-09-18 的要求是：**每个插件的下载地址与下载文件名都要能改**，有多个文件的就出多个输入框，
    /// 不改就用他给的默认值，全部放高级设置里。这套机制把「一个可下载文件」声明成一个槽，
    /// 于是地址与文件名自动变成界面上可改的两项，不必为 42 个插件各手写一遍 picker。
    ///
    /// 这条用例盯四件事：
    ///   ① 声明的**自洽性**（键唯一、槽 Key 唯一、声明仓库都登记在册）；
    ///   ② 用户改了地址/文件名**真的会被采纳**（改了不生效是这套机制最典型的失败形态）；
    ///   ③ 名字对不上时的**容错四级瀑布**（且「猜」必须留痕 —— 命中方式随结果带出来）；
    ///   ④ 落点与用户清单**逐字一致**（那是需求，不是我们的设计选择）。
    /// </summary>
    private static void CheckAssetSlots()
    {
        Section("文件槽：下载地址与文件名可改 + 容错匹配 + 落点与用户清单一致");

        var slotComponents = ComponentCatalog.All.Where(d => d.Slots.Count > 0).ToList();
        Assert(slotComponents.Count > 0,
            "前置条件：应至少有一个组件用文件槽声明（否则本用例整段恒真）");

        // ── ① 声明的自洽性 ─────────────────────────────────────────
        foreach (var definition in slotComponents)
        {
            var dupes = definition.Slots.GroupBy(s => s.Key, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert(dupes.Count == 0,
                $"{definition.Kind} 的槽 Key 不能重复（重复 = 存档里是同一个键，两行会互相盖掉）"
                + (dupes.Count == 0 ? "" : "；重复的：" + string.Join("、", dupes)));

            foreach (var slot in definition.Slots)
            {
                Assert(!string.IsNullOrWhiteSpace(slot.Key),
                    $"{definition.Kind} 的每个槽都要有 Key（它是 settings.json 里的键，空 Key 会让所有槽撞在一起）");

                if (slot.Source == SlotSource.RepoFile)
                {
                    Assert(slot.Targets.Count > 0,
                        $"{definition.Kind}/{slot.Key} 是仓库文件树槽，必须声明落点 —— "
                        + "这种槽不经过 Release，没有落点就不知道摆哪儿（压缩包槽才允许铺 out 根）");
                }
            }
        }

        // settings.json 的键在**全目录**范围内也不能重复（跨组件撞车同样会互相盖掉）
        var allSlotKeys = slotComponents
            .SelectMany(d => d.Slots.Select(s => ComponentCatalog.SlotSettingKey(d, s)))
            .ToList();
        var crossDupes = allSlotKeys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert(crossDupes.Count == 0,
            "槽的设置键（<组件>/<槽 Key>）在全目录范围内也不能重复"
            + (crossDupes.Count == 0 ? "" : "；重复的：" + string.Join("、", crossDupes)));

        // 每个槽的声明仓库都必须是 ComponentCatalog 的静态字段（反射扫得到）——
        // 内联 new RepoSpec 会绕过 DeclaredRepos，「声明的仓库都有入口」那条检查就看不见它了。
        var declaredRepos = ComponentCatalog.DeclaredRepos.ToHashSet();
        var inlineRepos = slotComponents
            .SelectMany(d => d.Slots.SelectMany(s =>
                UiLanguageCodes.Select(l => (d.Kind, s.Key, Repo: s.Address(l)))))
            .Where(x => !declaredRepos.Contains(x.Repo))
            .Select(x => $"{x.Kind}/{x.Key} → {x.Repo.DisplayName}")
            .ToList();
        Assert(inlineRepos.Count == 0,
            "槽引用的仓库都应声明成 ComponentCatalog 的静态字段（内联 new RepoSpec 会绕过仓库清单）"
            + (inlineRepos.Count == 0 ? "" : "；实际没声明的：" + string.Join("、", inlineRepos)));

        // ── ② 用户改了地址 / 文件名真的会被采纳 ────────────────────
        var dbi = ComponentCatalog.All.First(d => d.Kind == ComponentKind.Dbi);
        var dbiNro = dbi.Slots.First(s => s.Key == "DbiNro");
        var dbiKey = ComponentCatalog.SlotSettingKey(dbi, dbiNro);

        Assert(dbiKey == "Dbi/DbiNro",
            "槽的设置键应是 <组件>/<槽 Key>（界面、存档、解析三边共用同一条规则，别各写一份）");

        var blank = new WizardOptions { Language = "zh-Hans" };
        Assert(ComponentCatalog.ResolveSlotAddress(dbi, dbiNro, blank) == "rashevskyv/DBIPatcher",
            "没改过时应用声明的默认地址（用户清单里给的那个）");
        Assert(ComponentCatalog.ResolveSlotFileName(dbi, dbiNro, blank) == "DBI.nro",
            "没改过时应用声明的默认文件名");

        var edited = new WizardOptions { Language = "zh-Hans" };
        edited.AssetAddresses[dbiKey] = "myfork/DBI";
        edited.AssetFileNames[dbiKey] = "MyDBI.nro";
        Assert(ComponentCatalog.ResolveSlotAddress(dbi, dbiNro, edited) == "myfork/DBI",
            "用户改的地址应被采纳（改了不生效是这套机制最典型的失败形态）");
        Assert(ComponentCatalog.ResolveSlotFileName(dbi, dbiNro, edited) == "MyDBI.nro",
            "用户改的文件名应被采纳");
        Assert(ComponentCatalog.ResolveSlotRepo(dbi, dbiNro, edited).DisplayName == "myfork/DBI",
            "改过的地址应真的用于下载（解析层与下载层必须是同一个判据）");

        // 完整 URL 形态也要能吃（用户很可能直接粘浏览器地址栏里的那串）
        edited.AssetAddresses[dbiKey] = "https://github.com/other/DBI";
        Assert(ComponentCatalog.ResolveSlotRepo(dbi, dbiNro, edited).DisplayName == "other/DBI",
            "粘完整 URL 也应能解析（与「下载源」那一栏同一套容错）");

        // 解析不出来 → 回落默认地址（**不抛**）：界面上已就地标红，这里再抛只会让「点开始」直接失败
        edited.AssetAddresses[dbiKey] = "乱七八糟";
        Assert(ComponentCatalog.ResolveSlotRepo(dbi, dbiNro, edited) == dbiNro.Address(edited.Language),
            "地址解析不出来时应回落到声明的默认地址，而不是让整个流程失败");

        // 只有空白 = 没改（用户先清空再想填，中途的状态不该被当成「改成了空地址」）
        edited.AssetAddresses[dbiKey] = "   ";
        Assert(ComponentCatalog.ResolveSlotAddress(dbi, dbiNro, edited) == "rashevskyv/DBIPatcher",
            "只填了空白应视作没改（否则会去请求一个空地址）");

        // ── ③ 默认文件名跟随界面语言（DBI 的翻译包）───────────────
        var dbiTranslation = dbi.Slots.First(s => s.Key == "DbiTranslation");
        foreach (var (code, expected) in new[]
                 {
                     ("zh-Hans", "translation_zhcn.bin"),
                     ("zh-Hant", "translation_zhtw.bin"),
                     ("en-US", "translation_en.bin"),
                 })
        {
            var localized = new WizardOptions { Language = code };
            var actual = ComponentCatalog.ResolveSlotFileName(dbi, dbiTranslation, localized);
            Assert(actual == expected,
                $"界面语言 {code} 时应取 {expected}（用户要求翻译文件跟随软件界面语言），实际 {actual}");
        }

        Assert(dbiTranslation.SaveAs == "translation.bin",
            "DBI 只认与自己同目录的 translation.bin —— 下载名按语言取、落盘名固定，这一步由 SaveAs 表达");

        // ── ③b KeyX 的覆盖层包按界面语言取（与 ③ 同源，但问题不同）────
        //
        // ⚠️ 这两件事容易被当成同一件，其实不一样：
        //    ③ DBI 是「一个 Release 里挂了 20 多份翻译，挑一份、**还要改名**」；
        //    ③b KeyX 是「两个包**都真实可用**，按语言挑一个」。
        //    后者更容易被写成「随便挑一个就行」—— 两个包只差 7 字节、功能几乎一样。
        //    但对中文用户不是「一样」：逐条目量过（2026-09-18），两包 8 个文件逐字节相同
        //    （**含四个语言 json**），唯一差异是 ovl-KeyX.ovl 里内嵌的显示名 ——
        //    CN 版中文（按键助手）、EN 版 KeyX，即呼出菜单里那行字。
        //    所以必须钉住「中文界面拿 CN 包」这个方向，而不是只钉住「能拿到一个包」。
        //    ⚠️ 这段注释曾写成「EN 包里没有 zh-tw.json、界面会变英文」—— 是编的，
        //       语言文件两包完全相同。见 ComponentCatalog.KeyXFileName 的注释。
        var keyX = ComponentCatalog.All.First(d => d.Kind == ComponentKind.KeyX);
        var keyXSlot = keyX.Slots[0];
        foreach (var (code, expected) in new[]
                 {
                     ("zh-Hans", "KeyX-CN.zip"),
                     ("zh-Hant", "KeyX-CN.zip"),
                     ("en-US", "KeyX-EN.zip"),
                     ("ja-JP", "KeyX-EN.zip"),
                 })
        {
            var localized = new WizardOptions { Language = code };
            var actual = ComponentCatalog.ResolveSlotFileName(keyX, keyXSlot, localized);
            Assert(actual == expected,
                $"界面语言 {code} 时 KeyX 应取 {expected}（中文界面取 CN 包，其余取 EN 包），实际 {actual}");
        }

        // 两个包名必须是**上游真实存在**的那两个。
        // 写成一个「听起来对」的名字（比如 KeyX-CN-v1.5.6.zip）不会有任何报错，
        // 只会静默落进容错瀑布去猜 —— 而这两个名字在 Release 里都是逐字命中的，本来不该猜。
        Assert(ComponentCatalog.KeyXFileName("zh-Hans") == "KeyX-CN.zip"
            && ComponentCatalog.KeyXFileName("en-US") == "KeyX-EN.zip",
            "KeyX 的包名必须是上游实测存在的 KeyX-CN.zip / KeyX-EN.zip（v1.5.6 的 assets 就是这两个）");

        // SaveAs 必须为空：KeyX 是**整包解压**，包名与落盘名无关。
        // 顺手声明一个 SaveAs 会去改一个不存在的「单文件名」，是纯粹的自伤。
        Assert(keyXSlot.SaveAs is null,
            "KeyX 整包解压，不需要 SaveAs（那是给 DBI 的翻译文件那种「单文件改名」用的）");
        Assert(keyXSlot.Targets.Count == 0,
            "KeyX 应整包铺到 out 根（包内路径已以 SD 卡根为基准），实际声明了落点："
            + string.Join("、", keyXSlot.Targets.Select(t => t.Directory.Length == 0 ? "(out 根)" : t.Directory)));

        // ── ③c 「Ultrahand插件」6 个的落点与解压声明 ────────────────
        //
        // 实测（2026-09-18）七个包的顶层目录全是 SD 卡根目录名
        // （atmosphere / switch / config / SaltySD），没有一层多余的包装目录。
        // 所以「不声明 Extract + 不声明 Targets」是**实测结论**，不是默认值的巧合。
        //
        // ⚠️ 这条断言钉的是**声明**，不是**上游**：只有当有人去改这 6 个组件的
        //    Extract/Targets 时才可能红（逼他先解释「包结构是不是变了」）。
        //    上游哪天多出一层 <仓库名>/ 包装目录，C# 声明一个字都没变 ⇒ 它**照样是绿的**。
        //    **上游那半边只有 e2e（真联网跑一遍、看落盘）或实测脚本够得着。**
        //    原先这里写的是「那时它会红」，把「人改声明」和「上游改包」混成了一件事 ——
        //    这正是「恒真断言」的变体：断言本身不假，假的是它自称覆盖的范围。
        var ultrahandKinds = new[]
        {
            ComponentKind.Emuiibo, ComponentKind.Zing, ComponentKind.ReverseNxRt,
            ComponentKind.StatusMonitorOverlay, ComponentKind.QuickNtp, ComponentKind.KeyX,
        };
        foreach (var kind in ultrahandKinds)
        {
            var definition = ComponentCatalog.All.First(d => d.Kind == kind);
            Assert(definition.Category == ComponentCategory.UltrahandPlugin,
                $"{kind} 应落在「Ultrahand插件」分类里（用户给的六类分组），实际 {definition.Category}");
            Assert(definition.Slots.Count == 1,
                $"{kind} 只有一个可下载文件，实际声明了 {definition.Slots.Count} 个槽");

            var slot = definition.Slots[0];
            Assert(slot.Source == SlotSource.Release,
                $"{kind} 走 Release 取包，实际 {slot.Source}");
            Assert(slot.Extract.KeepsEverything,
                $"{kind} 的包内路径已以 SD 卡根为基准，不该声明解压筛选"
                + "（声明了会静默少文件 —— 那正是本项目最忌讳的失败形态）");
            Assert(!slot.IsPayload,
                $"{kind} 是普通组件文件，不该标 IsPayload（见 ⑤b 那条穷举断言）");
            Assert(string.IsNullOrWhiteSpace(slot.SysmoduleTitleId),
                $"{kind} 不声明 SysmoduleTitleId（约定与理由见 ComponentCatalog 里那一组的注释）");
        }

        // ── ③d 「学习」7 个的落点与解压声明 ────────────────────────
        //
        // 实测（2026-09-18，用 HTTP Range 只取 ZIP 中央目录拿到的完整条目清单）：
        //   AtmoXL    switch/AtmoXL-Titel-Installer/…nro           → out 根（不筛选）
        //   Awoo      switch/Awoo-Installer/…nro                   → out 根（不筛选）
        //   linkalho  switch/linkalho/linkalho.nro                 → out 根（不筛选）
        //   90DNS     散装 .nro                                     → switch/Switch_90DNS_tester
        //   wiliwili  wiliwili/{README.md, wiliwili.nro, 安装必读.txt} → switch/wiliwili（剥前缀 + 只取 .nro）
        //   Moonlight 散装 .nro                                     → switch/Moonlight-Switch
        //   TriPlayer atmosphere/contents/4200000000000FFF/… + switch/… → out 根（不筛选）
        //
        // ⚠️ 与 ③c 一样，这条钉的是**声明**：上游哪天改了包结构，声明一个字都没变 ⇒ 它照样绿。
        //    上游那半边归 e2e 与 tools/check-upstream-facts.py 管（后者第 ② 项就查「第一层是不是 SD 根名」）。
        var learningKinds = new[]
        {
            ComponentKind.AtmoXL, ComponentKind.Awoo, ComponentKind.Linkalho,
            ComponentKind.Dns90Tester, ComponentKind.Wiliwili, ComponentKind.Moonlight,
            ComponentKind.TriPlayer,
        };
        Assert(learningKinds.Length == 7, "「学习」这一类是 7 个组件（用户 2026-09-18 的清单）");

        foreach (var kind in learningKinds)
        {
            var definition = ComponentCatalog.All.First(d => d.Kind == kind);
            Assert(definition.Category == ComponentCategory.Learning,
                $"{kind} 应落在「学习」分类里（用户给的六类分组），实际 {definition.Category}");
            Assert(definition.Slots.Count == 1,
                $"{kind} 只有一个可下载文件，实际声明了 {definition.Slots.Count} 个槽");

            var slot = definition.Slots[0];
            Assert(slot.Source == SlotSource.Release,
                $"{kind} 走 Release 取包，实际 {slot.Source}");
            Assert(!slot.IsPayload,
                $"{kind} 是普通组件文件，不该标 IsPayload（见 ⑤b 那条穷举断言）");
            Assert(string.IsNullOrWhiteSpace(slot.SysmoduleTitleId),
                $"{kind} 不声明 SysmoduleTitleId —— TriPlayer 的包里确实含一个 sysmodule"
                + "（4200000000000FFF），但它与已声明的五个没有交集，按现有约定（只声明「冲突可预期」的）不写");
        }

        // 落点分两类，逐条钉住「为什么是这一类」——两类之间的差别不是风格，是**包内结构**：
        //   ① 包内已以 SD 根为基准 → 不声明 Targets / Extract，整棵铺 out 根；
        //   ② 上游只发散装 .nro（或只该取其中一个文件）→ 显式声明落点。
        foreach (var kind in new[] { ComponentKind.AtmoXL, ComponentKind.Awoo, ComponentKind.Linkalho, ComponentKind.TriPlayer })
        {
            var slot = ComponentCatalog.All.First(d => d.Kind == kind).Slots[0];
            Assert(slot.Extract.KeepsEverything && slot.Targets.Count == 0,
                $"{kind} 的包内路径已以 SD 卡根为基准（实测），不该声明解压筛选或落点 —— "
                + "声明了会静默少文件或乱套一层，正是本项目最忌讳的失败形态");
        }

        foreach (var (kind, expectedDir, expectedName) in new[]
                 {
                     (ComponentKind.Dns90Tester, "switch/Switch_90DNS_tester", "Switch_90DNS_tester.nro"),
                     (ComponentKind.Moonlight, "switch/Moonlight-Switch", "Moonlight-Switch.nro"),
                 })
        {
            var slot = ComponentCatalog.All.First(d => d.Kind == kind).Slots[0];
            Assert(slot.Extract.KeepsEverything,
                $"{kind} 上游发的是**散装** .nro（不是压缩包）⇒ 没有包内路径要筛，不该声明 ExtractPlan");
            Assert(slot.FileName(null) == expectedName,
                $"{kind} 的资源名应是上游实测的 {expectedName}，实际 {slot.FileName(null)}");
            Assert(slot.Targets.Count == 1 && slot.Targets[0].Directory == expectedDir,
                $"{kind} 应落 {expectedDir}（homebrew 菜单按目录扫 .nro），实际 "
                + string.Join("、", slot.Targets.Select(t => t.Directory.Length == 0 ? "(out 根)" : t.Directory)));
        }

        // ★ wiliwili 是唯一「压缩包 + 剥前缀 + 只取一个文件」的槽，逐项钉住三件事：
        //   ① 必须剥掉 wiliwili/（实测包里有一层同名子目录，不剥会落成
        //      out/switch/wiliwili/wiliwili/wiliwili.nro）；
        //   ② 必须只取 wiliwili.nro（包里还有 README.md 与 安装必读.txt，用户只要 .nro）；
        //   ③ 落点是 switch/wiliwili。
        var wiliwili = ComponentCatalog.All.First(d => d.Kind == ComponentKind.Wiliwili).Slots[0];
        Assert(wiliwili.Extract.StripPrefix == "wiliwili",
            $"wiliwili 的包内多一层 wiliwili/ 子目录（实测），必须剥掉，实际「{wiliwili.Extract.StripPrefix}」");
        Assert(wiliwili.Extract.KeepFileNames.Count == 1 && wiliwili.Extract.KeepFileNames[0] == "wiliwili.nro",
            "wiliwili 包里还有 README.md / 安装必读.txt，用户只要那一个 .nro，实际保留清单："
            + string.Join("、", wiliwili.Extract.KeepFileNames));
        Assert(wiliwili.Targets.Count == 1 && wiliwili.Targets[0].Directory == "switch/wiliwili",
            "wiliwili.nro 应落 out/switch/wiliwili，实际 "
            + string.Join("、", wiliwili.Targets.Select(t => t.Directory.Length == 0 ? "(out 根)" : t.Directory)));

        // 用**真实包内条目名**（实测清单）喂给 Map，验证「剥前缀 + 只取一个文件」的组合真的对：
        //   只保留 wiliwili.nro，其余两个文件与目录项全部丢弃。
        Assert(wiliwili.Extract.Map("wiliwili/wiliwili.nro") == "wiliwili.nro",
            "包内 wiliwili/wiliwili.nro 剥掉前缀后应剩 wiliwili.nro（再落到 switch/wiliwili/）");
        foreach (var dropped in new[] { "wiliwili/README.md", "wiliwili/安装必读.txt", "wiliwili/" })
        {
            Assert(wiliwili.Extract.Map(dropped) is null,
                $"wiliwili 包里实测有「{dropped}」，用户只要 .nro ⇒ 应被丢弃，实际保留了");
        }

        // ★★ 按语言换**仓库**（linkalho）—— 本项目第一个「地址本身跟着语言变」的槽。
        //
        // 与 ③（DBI 按语言换文件名）、③b（KeyX 按语言换包）是**三件不同的事**，别混：
        //   ③  DBI：同一个仓库，同一个 Release，挑不同的**文件名**；
        //   ③b KeyX：同一个仓库，同一个 Release，挑不同的**文件**（两个都在）；
        //   ③c linkalho：**仓库都不一样**（中文汉化镜像 SwitchScriptTW / 上游 impeeza）。
        //
        // ⚠️ 最要命的一点：**地址与文件名必须成对**。两个仓库的文件名不同
        //    （CN 是逐字的 linkalho.zip、EN 是**模式** linkalho-x.x.x.zip），只切其中一个 ⇒
        //    名字与仓库对不上 ⇒ 静默落进容错瀑布「猜」一个（能跑通，日志里留一条本不该有的记录）。
        //    所以这条断言**同时**钉住两者，且钉住「两处用的是同一个判据」。
        //
        // ⚠️ EN 侧刻意用**用户清单里的模式**（linkalho-x.x.x.zip）而不是实测到的具体版本号
        //    （2026-09-18 实测上游发的是 linkalho-v2.0.2.zip）：用户给的是「跟着版本走的模式」，
        //    写成 v2.0.2 会让**每次上游发版都让逐字命中失效一次**、白留一条降级记录。
        //    「这个模式确实能对上上游的真实名字」由下面那条拿**实测名字**喂 matcher 的断言守住。
        var linkalho = ComponentCatalog.All.First(d => d.Kind == ComponentKind.Linkalho);
        var linkalhoSlot = linkalho.Slots[0];
        foreach (var (code, repo, file) in new[]
                 {
                     ("zh-Hans", "SwitchScriptTW/linkalho", "linkalho.zip"),
                     ("zh-Hant", "SwitchScriptTW/linkalho", "linkalho.zip"),
                     ("en-US", "impeeza/linkalho", "linkalho-x.x.x.zip"),
                     ("ja-JP", "impeeza/linkalho", "linkalho-x.x.x.zip"),
                 })
        {
            Assert(linkalhoSlot.Address(code).DisplayName == repo,
                $"界面语言 {code} 时 linkalho 应取仓库 {repo}（中文走汉化镜像，其余走上游），"
                + $"实际 {linkalhoSlot.Address(code).DisplayName}");
            Assert(linkalhoSlot.FileName(code) == file,
                $"界面语言 {code} 时 linkalho 应取包名 {file}（与仓库**成对**，见 ComponentCatalog.LinkalhoRepo），"
                + $"实际 {linkalhoSlot.FileName(code)}");
        }

        // 反向契约：CN 的包名必须是**上游实测存在**的那个（实测 v2.0.2：linkalho.zip）；
        // EN 侧是**用户清单里的模式**（linkalho-x.x.x.zip）—— 写成一个「听起来对」的名字
        // （比如 linkalho-cn.zip）不会有任何报错。
        Assert(ComponentCatalog.LinkalhoRepo("zh-Hans") == ComponentCatalog.LinkalhoCnRepo
            && ComponentCatalog.LinkalhoRepo("en-US") == ComponentCatalog.LinkalhoEnRepo,
            "LinkalhoRepo 必须真的返回那两个声明字段（而不是内联 new RepoSpec —— 那会绕过 DeclaredRepos）");
        Assert(ComponentCatalog.LinkalhoFileName("zh-Hans") == "linkalho.zip"
            && ComponentCatalog.LinkalhoFileName("en-US") == "linkalho-x.x.x.zip",
            "linkalho 的 CN 包名是逐字存在的 linkalho.zip；EN 侧是**用户清单里的模式** linkalho-x.x.x.zip"
            + "（不是实测到的具体版本号 —— 写成具体版本号会让每次上游发版都白留一条降级记录）");

        // ★ 只钉「字符串相等」是不够的：把模式写成 linkalho-zzz.zip 也「相等」。
        //   真正的契约是「这个模式能对上上游的真实资源名」，所以拿**实测到的名字**喂匹配器：
        //   impeeza/linkalho v2.0.2 发的是 linkalho-v2.0.2.zip（2026-09-18 实测）。
        //   并断言它属于「按声明命中」而**不是猜** —— 否则每次跑都会留一条误导性的 Warning。
        var linkalhoEnProbe = new ReleaseInfo(
            "v2.0.2", "t", false, DateTimeOffset.UnixEpoch, string.Empty,
            [new ReleaseAsset("linkalho-v2.0.2.zip", "https://example.invalid/l", 3156396)]);
        var linkalhoEnMatch = AssetNameMatcher.Find(linkalhoEnProbe, ComponentCatalog.LinkalhoFileName("en-US"));
        Assert(linkalhoEnMatch is { Kind: AssetMatchKind.Wildcard, IsGuess: false }
            && linkalhoEnMatch.Asset.Name == "linkalho-v2.0.2.zip",
            "EN 侧的模式 linkalho-x.x.x.zip 必须能对上实测的真实资源名 linkalho-v2.0.2.zip，"
            + "且属于「按声明命中」而不是猜（实际 "
            + (linkalhoEnMatch is null ? "没匹配到" : $"「{linkalhoEnMatch.Asset.Name}」（{linkalhoEnMatch.Kind}，IsGuess={linkalhoEnMatch.IsGuess}）") + "）");
        // 反向：CN 侧是逐字名，必须走 Exact（不是 Wildcard）—— 两边性质不同，别被「统一处理」抹平
        var linkalhoCnProbe = new ReleaseInfo(
            "2.0.2", "t", false, DateTimeOffset.UnixEpoch, string.Empty,
            [new ReleaseAsset("linkalho.zip", "https://example.invalid/c", 3184785)]);
        Assert(AssetNameMatcher.Find(linkalhoCnProbe, ComponentCatalog.LinkalhoFileName("zh-Hans"))
            is { Kind: AssetMatchKind.Exact },
            "CN 侧的 linkalho.zip 是逐字名，必须走 Exact 一级命中");

        Assert(string.IsNullOrWhiteSpace(linkalhoSlot.SaveAs),
            "linkalho 整包解压，不需要 SaveAs（那是给 DBI 的翻译文件那种「单文件改名」用的）");
        Assert(linkalhoSlot.Extract.KeepsEverything && linkalhoSlot.Targets.Count == 0,
            "linkalho 两个仓库的包内路径**结构相同**（实测都是 switch/linkalho/linkalho.nro）⇒ 整棵铺 out 根");

        // ── ④ 容错四级瀑布（样本取自 2026-09-18 实测的上游真实资源名，离线）──
        var matcherCases = new (string Pattern, string[] Assets, AssetMatchKind? Expected, string? ExpectedAsset)[]
        {
            // ① 逐字相同 —— 绝大多数插件走这一级
            ("sphaira.zip", ["sphaira.zip"], AssetMatchKind.Exact, "sphaira.zip"),

            // ② 通配：x / X / * 都当版本占位
            ("simple-mod-alchemist_x_x_x.zip", ["simple-mod-alchemist_v1_1_1.zip"],
                AssetMatchKind.Wildcard, "simple-mod-alchemist_v1_1_1.zip"),
            ("ldn_mitm_FW_x.x.x.zip", ["ldn_mitm_FW_22.5.0.zip"],
                AssetMatchKind.Wildcard, "ldn_mitm_FW_22.5.0.zip"),
            ("MissionControl-x.x.x-x-x.zip", ["MissionControl-0.6.0-1-1.zip"],
                AssetMatchKind.Wildcard, "MissionControl-0.6.0-1-1.zip"),
            ("sys-con-x.x.x.zip", ["sys-con-1.7.0.zip"], AssetMatchKind.Wildcard, "sys-con-1.7.0.zip"),

            // 「后台」那批的**真实**上游名字（2026-09-18 实测，见 tools/probe-background.py）。
            // MissionControl 的真实名字带版本 + 分支名 + 提交哈希（0.15.2-master-d3941d43），
            // 比上面那条合成样本更狠 —— 两个连字符加一串十六进制，通配必须还能对上。
            ("sys-botbasexx.zip", ["sys-botbase.nsp", "sys-botbase25.zip"],
                AssetMatchKind.Wildcard, "sys-botbase25.zip"),
            ("MissionControl-x.x.x-x-x.zip", ["MissionControl-0.15.2-master-d3941d43.zip"],
                AssetMatchKind.Wildcard, "MissionControl-0.15.2-master-d3941d43.zip"),
            ("release.zip", ["release.zip"], AssetMatchKind.Exact, "release.zip"),
            ("atmosphere.7z", ["atmosphere.7z"], AssetMatchKind.Exact, "atmosphere.7z"),

            // 「主题」那批的真实上游名字（2026-09-18 实测，见下方 ⑩ 段）。
            // ⚠️ sys-ticon 的 Release 里**同时**挂着 sys-ticon.zip（正式版）与 sys-ticon-log.zip
            // （带日志的调试版）—— 两者装的是**同一个 title**、包内结构逐项一样，只有 nsp 大小差一点。
            // 逐字命中时当然拿正式版；但正式版一旦改名消失，③「去版本号前缀」会把
            // sys-ticon-log.zip 选走。那不是灾难（同功能，只是会打日志），但**必须留痕**：
            // 命中方式会是 Stem，调用方据此打一条「声明叫 A、实际用了 B」的告警。
            // 两条都钉住，免得将来有人把 ③ 的判据改宽/改窄而无人察觉。
            ("sys-ticon.zip", ["sys-ticon-log.zip", "sys-ticon.zip"], AssetMatchKind.Exact, "sys-ticon.zip"),
            ("sys-ticon.zip", ["sys-ticon-log.zip"], AssetMatchKind.Stem, "sys-ticon-log.zip"),

            // NXThemesInstaller 的 Release 里还有两个 .zip（编辑器与工具包），都不能被误当成安装器：
            // 声明 .nro 就只该拿 .nro（扩展名不匹配时 ③④ 都不会接）。
            ("NXThemesInstaller.nro",
                ["nxtheme-editor-web-selfhost.zip", "NXThemesInstaller.nro", "NxThemeTool.zip"],
                AssetMatchKind.Exact, "NXThemesInstaller.nro"),
            ("NXThemesInstaller.nro",
                ["nxtheme-editor-web-selfhost.zip", "NxThemeTool.zip"], null, null),
            ("Avatool.nro", ["Avatool.nro"], AssetMatchKind.Exact, "Avatool.nro"),

            // ③ 去版本号前缀：声明带版本尾巴、上游却是光名字
            ("Fizeau-x.x.x.zip", ["Fizeau.zip"], AssetMatchKind.Stem, "Fizeau.zip"),

            // ④ 同扩展名唯一候选：上游改了资源名（Awoo-Installer → NSAInstaller.zip 是真实案例）
            ("Awoo-Installer.zip", ["NSAInstaller.zip"], AssetMatchKind.SoleCandidate, "NSAInstaller.zip"),

            // 宁拒绝不猜错：同扩展名有两个、名字又毫无共同点 → 返回 null，由调用方点名告警。
            // ⚠️ 这一条是「不猜」的判据 —— 少了它，容错就退化成「总能挑一个出来」，错得还很安静。
            ("release.zip", ["alpha.zip", "beta.zip"], null, null),

            // 扩展名也要对得上：声明 .nro 就不该被 .zip 顶上
            ("Moonlight-Switch.nro", ["Moonlight-Switch.zip"], null, null),
        };

        foreach (var (pattern, assets, expected, expectedAsset) in matcherCases)
        {
            var release = new ReleaseInfo(
                "v1", "t", false, DateTimeOffset.UnixEpoch, string.Empty,
                assets.Select(a => new ReleaseAsset(a, "https://example.invalid/" + a, 1)).ToList());

            var match = AssetNameMatcher.Find(release, pattern);
            Assert(match?.Kind == expected && match?.Asset.Name == expectedAsset,
                $"「{pattern}」在 [{string.Join(", ", assets)}] 里应匹配出 "
                + $"{(expectedAsset is null ? "「不猜」" : expectedAsset)}（方式 {expected?.ToString() ?? "无"}）"
                + $"，实际 {(match is null ? "没匹配到" : $"「{match.Asset.Name}」（{match.Kind}）")}");
        }

        // 「猜」必须留痕：命中方式要随结果带出去，否则 sys-ftpd 那种「声明叫 release.zip、
        // 实际下了别的名字」的事，用户永远不知道。
        var sampleRelease = new ReleaseInfo(
            "v1", "t", false, DateTimeOffset.UnixEpoch, string.Empty,
            [new ReleaseAsset("simple-mod-alchemist_v1_1_1.zip", "https://example.invalid/a", 1)]);
        var sma = ComponentCatalog.All.First(d => d.Kind == ComponentKind.SimpleModAlchemist);
        var smaSlot = sma.Slots[0];
        var smaPicks = ComponentCatalog.PickSlot(sma, smaSlot, sampleRelease, new WizardOptions { Language = "zh-Hans" });

        Assert(smaPicks.Count == 1, $"容错命中时也应产出 1 个下载物，实际 {smaPicks.Count} 个");
        Assert(smaPicks.Count == 1 && smaPicks[0].Slot is { Kind: AssetMatchKind.Wildcard },
            "下载物必须带上「怎么命中的」（Slot.Kind）—— 非逐字命中的要写进日志，不能让用户以为下的就是他要的那个");
        // ⚠️ 而「非逐字」**不等于**「猜」：声明里本来就带 x 占位（simple-mod-alchemist_x_x_x.zip）⇒
        //    按模式取到就是**如你所愿**，日志级别与措辞都该跟着变（见 AssetMatch.IsGuess）。
        //    把这一族当「猜」报 Warning，等于**教用户去改掉他自己给的模式**
        //    （2026-09-18 联网 e2e 跑到 TriPlayer 才发现：用户清单给的就是 triplayer-x.x.x.zip）。
        Assert(smaPicks.Count == 1 && smaPicks[0].Slot is { IsGuess: false },
            "声明里带 x/* 占位、按模式命中的，**不是猜**（IsGuess 应为 false）");

        // IsGuess 的两族分界穷举钉住（遍历枚举，不是手写四个值 —— 将来新增一级会自动落进来）
        var guessKinds = new[] { AssetMatchKind.Stem, AssetMatchKind.SoleCandidate };
        foreach (var kind in Enum.GetValues<AssetMatchKind>())
        {
            var probe = new AssetMatch(
                new ReleaseAsset("x.zip", "https://example.invalid/x", 1), kind, "x.zip");
            Assert(probe.IsGuess == guessKinds.Contains(kind),
                $"AssetMatchKind.{kind} 的 IsGuess 应当等于「哪些算猜」的清单"
                + "（Exact / Wildcard = **按声明命中**；Stem / SoleCandidate = **猜**）"
                + $"，实际 IsGuess={probe.IsGuess}");
        }

        // ★ 「命中方式 → 该怎么交代」也要穷举钉住（NoticeFor 是唯一分类处，见 SlotMatchNotice）。
        //    这条护栏存在的理由：日志级别分流过去是 MainViewModel 里三段**内联 if**
        //    （`非 Exact 就 Warning`），谁把它写回去都不会有任何断言变红 —— 而后果是
        //    每次跑都给用户留一条「请改成确切的文件名」，**教他改掉自己给的通配名**。
        //    2026-09-18 的联网 e2e 正是这么发现的（TriPlayer 的 triplayer-x.x.x.zip）。
        var noticeTable = new Dictionary<AssetMatchKind, SlotMatchNotice>
        {
            [AssetMatchKind.Exact] = SlotMatchNotice.Silent,
            [AssetMatchKind.Wildcard] = SlotMatchNotice.PatternHit,
            [AssetMatchKind.Stem] = SlotMatchNotice.Guess,
            [AssetMatchKind.SoleCandidate] = SlotMatchNotice.Guess,
        };
        // 前提：两边都非空，否则下面那圈断言会「一条都没跑」而全绿
        Assert(Enum.GetValues<AssetMatchKind>().Length > 0 && noticeTable.Count > 0,
            "前提：AssetMatchKind 与这张穷举表都非空 —— 否则下面的逐项断言会一条都不跑");
        foreach (var kind in Enum.GetValues<AssetMatchKind>())
        {
            var hasExpected = noticeTable.TryGetValue(kind, out var expected);
            Assert(hasExpected,
                $"AssetMatchKind.{kind} 必须在这张穷举表里 —— 新增命中方式时先想清楚它属于"
                + "「按声明命中」还是「猜」，再决定该报 Info 还是 Warning");
            Assert(!hasExpected || expected == kind.NoticeFor(),
                $"AssetMatchKind.{kind} 的 NoticeFor 应与这张穷举表一致（表里 "
                + $"{(hasExpected ? expected.ToString() : "（缺）")}、实际 {kind.NoticeFor()}）");
        }
        Assert(noticeTable.Count == Enum.GetValues<AssetMatchKind>().Length,
            "穷举表里不该有已经删掉的命中方式（多出来的条目说明这张表没跟着枚举走）");
        // 反向契约：这一族必须**真的分成两类**。全归 Guess ⇒ 用户被建议改掉自己给的名字；
        // 全归 Silent ⇒ 真降级静默（用户不知道版本号从哪来）。恒真与否由「有没有人把它们归成一类」决定。
        Assert(noticeTable.Values.Contains(SlotMatchNotice.PatternHit)
            && noticeTable.Values.Contains(SlotMatchNotice.Guess)
            && noticeTable[AssetMatchKind.Wildcard] == SlotMatchNotice.PatternHit,
            "命中方式必须真的分出「按声明命中」（报 Info）与「猜」（报 Warning）两族；"
            + "尤其 Wildcard（声明即通配）**必须**归前者 —— 归错就等于教用户改掉他自己给的通配名");

        // ★★ 光钉住「分类」还不够 —— 分类对、日志级别照样可能是错的：
        //     谁在 MainViewModel 里再判一次 Kind（`if (match.Kind != AssetMatchKind.Exact) { Warning }`），
        //     上面那张表一个字都不会变、所有行为断言全绿，而日志又变回「教用户改掉自己给的通配名」。
        //     真实触发需要联网 + 恰好按模式命中，行为测试够不着 ⇒ 只能用**源码契约**钉
        //     （同 CheckRam8GbPayloadWarning 里对告警级别的做法）。
        var vmSourceRoot = FindSourceRoot();
        if (vmSourceRoot is null)
        {
            Assert(false, "找不到源码目录，无法核对「命中方式 → 日志级别」的源码契约");
        }
        else
        {
            var vmAll = File.ReadAllLines(Path.Combine(vmSourceRoot, "ViewModels", "MainViewModel.cs"));
            var declIndex = Array.FindIndex(vmAll,
                line => line.Contains("private void NoteSlotFilled(", StringComparison.Ordinal));

            Assert(declIndex >= 0, "MainViewModel 里应有 NoteSlotFilled —— 它负责把「怎么命中的」写成日志");
            if (declIndex >= 0)
            {
                // 方法体窗口：从声明行的下一行起，到下一个同缩进的成员声明为止（再看 60 行封顶）。
                // 注释行剔除 —— 注释里提到 `AssetMatchKind.Exact`（比如解释「为什么不这么写」）不算实现，
                // 否则改注释都会莫名变红。
                var body = vmAll.Skip(declIndex + 1)
                    .TakeWhile((line, offset) => offset == 0
                        || !Regex.IsMatch(line, @"^    (private|public|internal|protected) "))
                    .Take(60)
                    .Select(line => line.TrimStart())
                    .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
                    .ToList();

                Assert(body.Count > 0, "前置条件：应能取到 NoteSlotFilled 的方法体，否则下面几条恒真");
                Assert(body.Any(l => l.Contains("NoticeFor()", StringComparison.Ordinal)),
                    "NoteSlotFilled 必须用 AssetMatchKindExtensions.NoticeFor() 决定日志级别 —— "
                    + "「哪些命中算猜」只能有一份定义；在 ViewModel 里再判一次，迟早与那张表漂开，"
                    + "而且漂开时**没有任何断言会红**（2026-09-18 就是这么漂的）");
                Assert(!body.Any(l => l.Contains("AssetMatchKind.", StringComparison.Ordinal)),
                    "NoteSlotFilled 里不该再出现任何 `AssetMatchKind.` 直接比较 —— "
                    + "当初就是写成 `Kind != AssetMatchKind.Exact` 才把「按用户给的模式命中」误报成 Warning 的");

                // 两条日志各自的级别：按模式命中必须是 Info（且不能是 Warning），猜必须是 Warning（且不能是 Info）。
                // 级别降级 / 升级都不会有任何行为测试变红 —— 只有这几条看得见。
                var patternAt = body.FindIndex(l => l.Contains("Log.AssetPatternMatch", StringComparison.Ordinal));
                Assert(patternAt >= 0,
                    "「按用户给的模式命中」要有一句中性说明，用语言键 Log.AssetPatternMatch");
                if (patternAt >= 0)
                {
                    var around = body.Skip(Math.Max(0, patternAt - 2)).Take(5).ToList();
                    Assert(around.Any(l => l.Contains("LogLevel.Info", StringComparison.Ordinal)),
                        "「按模式命中」必须报 Info —— 报 Warning 会让用户以为出了问题，"
                        + "进而去改掉他自己刚给的那个通配名");
                    Assert(!around.Any(l => l.Contains("LogLevel.Warning", StringComparison.Ordinal)),
                        "「按模式命中」不能报 Warning（同上一句：那是在教用户改掉自己的意图）");
                }

                var fuzzyAt = body.FindIndex(l => l.Contains("Log.AssetFuzzyMatch", StringComparison.Ordinal));
                Assert(fuzzyAt >= 0, "「猜」要有告警，用语言键 Log.AssetFuzzyMatch（并建议用户改成确切的文件名）");
                if (fuzzyAt >= 0)
                {
                    var around = body.Skip(Math.Max(0, fuzzyAt - 2)).Take(5).ToList();
                    Assert(around.Any(l => l.Contains("LogLevel.Warning", StringComparison.Ordinal)),
                        "「猜」必须报 Warning —— 降成 Info 用户就再也看不见「这个版本号是猜来的」，"
                        + "也无从判断猜得对不对");
                    Assert(!around.Any(l => l.Contains("LogLevel.Info", StringComparison.Ordinal)),
                        "「猜」不能报 Info（降级静默是本项目最忌讳的失败形态）");
                }

                // 反向契约（第 5 条教训：可空声明 + 写入器兜底 = 漏填永远不报错）：
                // `default: throw` 是「宁可炸掉也不要静默」的最后一道 —— 改成 `default: return;`
                // 之后，新增一种 SlotMatchNotice 会**悄悄什么都不写**，而上面所有断言照样绿
                // （它们只检查已有那几种各自报了什么，不检查「没安排的那几种会怎样」）。
                var defaultAt = body.FindIndex(l => l.StartsWith("default:", StringComparison.Ordinal));
                Assert(defaultAt >= 0,
                    "NoteSlotFilled 的 switch 要有 default 分支 —— 少了它，新增一种 SlotMatchNotice 会静默不写日志");
                if (defaultAt >= 0)
                {
                    var arm = body.Skip(defaultAt).Take(4).ToList();
                    Assert(arm.Any(l => l.Contains("throw ", StringComparison.Ordinal)),
                        "NoteSlotFilled 的 default 分支必须抛异常（宁可炸掉也不要静默按某一种处理）—— "
                        + "换成 `return` 会让新增的 SlotMatchNotice 取值悄悄什么都不做");
                }
            }
        }
        Assert(smaPicks.Count == 1 && smaPicks[0].Slot!.RequestedPattern == "simple-mod-alchemist_x_x_x.zip",
            "下载物还要带回声明的原名，日志才能说清「你要的是这个、我用的是那个」");

        // 一个都匹配不到时**返回空**（而不是随便挑一个），上层据此逐槽点名告警
        var emptyPicks = ComponentCatalog.PickSlot(
            sma, smaSlot,
            new ReleaseInfo("v1", "t", false, DateTimeOffset.UnixEpoch, string.Empty,
                [new ReleaseAsset("something-else.zip", "https://example.invalid/x", 1),
                 new ReleaseAsset("another.zip", "https://example.invalid/y", 1)]),
            new WizardOptions { Language = "zh-Hans" });
        Assert(emptyPicks.Count == 0,
            "一个都匹配不上时应返回空，由上层**逐槽点名**告警（静默跳过会让用户拿到一份少文件的 SD 卡）");

        // ── ⑤ 落点：与用户 2026-09-18 清单里的路径**逐字一致** ─────
        //
        // ⚠️ 这份对照表是**需求原文**（用户逐个插件给的放置位置），不是我们的设计选择 ——
        //    所以它必须手写在这里；改它等于改需求，得先问用户。
        //    空串 = 铺 out 根（压缩包槽的 Targets 为空就表示「整包铺到 out 根」）。
        var expectedPlacement = new Dictionary<ComponentKind, string>
        {
            // 「固件」1 个 —— 落点里带 `{asset}` 占位符（这里比的是**声明原文**，占位符不替换）。
            //
            // 为什么必须带占位符：THZoria/NX_Firmware 的包**内部是平的**（实测 238 个 .nca
            // 全在压缩包根、没有顶层目录），版本号只出现在**资源名**里（Firmware.23.0.0.zip），
            // 所以 out/Firmware/<版本>/ 那个目录只能从「下载下来的文件名」推出来 ——
            // 见 OutputTarget.AssetNameToken。断言「解析后到底是什么」在下面 ⑤d 那一段。
            [ComponentKind.Firmware] = "Firmware/{asset}",

            [ComponentKind.Breeze] = "",
            [ComponentKind.Dbi] = "switch/DBI",
            [ComponentKind.EdiZonOverlay] = "",
            [ComponentKind.Goldleaf] = "switch/Goldleaf",
            [ComponentKind.Nxdumptool] = "switch/nxdt_rw_poc",
            [ComponentKind.NxShell] = "switch/NX-Shell",
            [ComponentKind.Ftpd] = "switch/ftpd",
            [ComponentKind.Haku33] = "switch/Haku33",
            [ComponentKind.Jksv] = "switch/JKSV",
            [ComponentKind.LunaApp] = "",
            [ComponentKind.SimpleModAlchemist] = "",
            [ComponentKind.SysClk] = "",
            [ComponentKind.SysDvr] = "",
            [ComponentKind.LdnMitm] = "",
            [ComponentKind.Sphaira] = "",
            [ComponentKind.Fizeau] = "",
            [ComponentKind.NxActivityLog] = "switch/NX-Activity-Log",
            [ComponentKind.SwitchFirmwareDumper] = "switch/Firmware-Dumper",
            [ComponentKind.BatteryDesyncFix] = "switch/battery_desync_fix_nx",

            // 「后台」5 个 —— 全部铺 out 根（实测：这几个包的内部本来就是 SD 卡根目录的样子）
            [ComponentKind.SysBotbase] = "",
            [ComponentKind.UsbBotbase] = "",
            [ComponentKind.MissionControl] = "",
            [ComponentKind.SysCon] = "",
            [ComponentKind.SysFtpd] = "",

            // 「主题」3 个
            [ComponentKind.NxThemesInstaller] = "switch/NXThemesInstaller",
            [ComponentKind.Avatool] = "switch/Avatool",
            [ComponentKind.SysTicon] = "",

            // 「底层」2 个 —— 散装 payload，落 hekate 自带的 payloads/ 目录
            [ComponentKind.LockpickRcm] = "bootloader/payloads",
            [ComponentKind.TegraExplorer] = "bootloader/payloads",

            // 「Ultrahand插件」6 个 —— 全部铺 out 根。
            // 实测（2026-09-18）：七个包的顶层目录**全是 SD 卡根目录名**
            // （atmosphere / switch / config / SaltySD），没有一层多余的包装目录，
            // 所以整棵铺开就是对的，不需要 ExtractPlan。
            //
            // ⚠️ 这张表比的是**声明**（`main.Targets`）对**手写的期望值**，两边都不来自上游。
            //    所以它钉得住的是「有人把落点声明改坏了 / 改得不一致」，**钉不住「上游改了包结构」**
            //    —— 那种情况下 C# 声明一个字都不会变，这里照样全绿。
            //    上游那半边归联网 e2e 与 `tools/check-upstream-facts.py`（见第八节第 70 条）。
            [ComponentKind.Emuiibo] = "",
            [ComponentKind.Zing] = "",
            [ComponentKind.ReverseNxRt] = "",
            [ComponentKind.StatusMonitorOverlay] = "",
            [ComponentKind.QuickNtp] = "",
            [ComponentKind.KeyX] = "",

            // 「学习」7 个 —— 两类落点，判据是**包内结构**（2026-09-18 逐包实测，用 HTTP Range
            // 只取 ZIP 中央目录拿到的完整条目清单）：
            //   ① 包内已以 SD 卡根为基准 → 铺 out 根（空串）；
            //   ② 上游只发散装 .nro，或只该取其中一个文件 → 显式落点。
            [ComponentKind.AtmoXL] = "",          // 实测包内 switch/AtmoXL-Titel-Installer/*.nro
            [ComponentKind.Awoo] = "",            // 实测包内 switch/Awoo-Installer/*.nro
            [ComponentKind.Linkalho] = "",        // 实测两个仓库的包内都是 switch/linkalho/linkalho.nro
            [ComponentKind.TriPlayer] = "",       // 实测包内 atmosphere/contents/… + switch/…
            [ComponentKind.Dns90Tester] = "switch/Switch_90DNS_tester",   // 散装 .nro
            [ComponentKind.Wiliwili] = "switch/wiliwili",                 // 包里只取 wiliwili.nro
            [ComponentKind.Moonlight] = "switch/Moonlight-Switch",        // 散装 .nro
        };

        // 覆盖性：表里的组件集合必须**恰好**等于「所有用文件槽声明的组件」。
        // 少一个 → 新增的组件没被这条护栏覆盖；多一个 → 表里留了个已经不存在的组件。
        //
        // ⚠️ 判据是「有槽的组件」而不是「组件分类下的组件」：后者会在新增一个分类
        //    （比如「后台」）时**继续全绿**，而新分类的落点其实一个都没被钉住 ——
        //    正是「手写清单会腐烂」那条教训。
        var slotBearing = slotComponents.Select(d => d.Kind).ToHashSet();
        var tableKinds = expectedPlacement.Keys.ToHashSet();
        var uncovered = slotBearing.Except(tableKinds).Select(k => k.ToString()).ToList();
        var stale = tableKinds.Except(slotBearing).Select(k => k.ToString()).ToList();
        Assert(uncovered.Count == 0,
            "每个用文件槽声明的组件都要在这张落点对照表里有位置（否则它没被覆盖）"
            + (uncovered.Count == 0 ? "" : "；缺的：" + string.Join("、", uncovered)));
        Assert(stale.Count == 0,
            "落点对照表里不该有「不用文件槽」的组件"
            + (stale.Count == 0 ? "" : "；多余的：" + string.Join("、", stale)));

        foreach (var (kind, directory) in expectedPlacement)
        {
            var definition = ComponentCatalog.All.First(d => d.Kind == kind);
            var main = definition.Slots[0];

            if (directory.Length == 0)
            {
                Assert(main.Targets.Count == 0,
                    $"{kind} 的主文件应整包铺到 out 根（用户要求），实际声明了落点："
                    + string.Join("、", main.Targets.Select(t => t.Directory)));
            }
            else
            {
                Assert(main.Targets.Any(t => t.Directory == directory),
                    $"{kind} 的主文件应放到 out/{directory}（用户要求），实际声明的是："
                    + string.Join("、", main.Targets.Select(t => t.Directory.Length == 0 ? "(out 根)" : t.Directory)));
            }
        }

        // Luna 的第二个文件（enctemplate.zip）落点单独钉一下 —— 它是唯一一个「仓库文件树」槽
        var luna = ComponentCatalog.All.First(d => d.Kind == ComponentKind.LunaApp);
        var enctemplate = luna.Slots.First(s => s.Key == "LunaEnctemplate");
        Assert(enctemplate.Source == SlotSource.RepoFile,
            "enctemplate.zip 从来没发布过，只躺在仓库根目录 —— 必须走仓库文件树，不能走 Release");
        Assert(enctemplate.Targets.Any(t => t.Directory == "config/luna/enctemplate"),
            "enctemplate.zip 应解压到 out/config/luna/enctemplate（用户要求）");

        var repoFilePick = ComponentCatalog.PickRepoFileSlot(luna, enctemplate, new WizardOptions { Language = "zh-Hans" });
        Assert(repoFilePick.RepoFile is not null,
            "仓库文件树槽的下载物必须带上「哪个仓库的哪个路径」，下载阶段才能拼出备用地址");
        Assert(repoFilePick.Asset.DownloadUrl
               == "https://raw.githubusercontent.com/Ixaruz/Luna-App/HEAD/enctemplate.zip",
            "仓库文件树的直链应是 raw + HEAD 引用（写死 main/master 会在上游改默认分支时静默 404），"
            + $"实际 {repoFilePick.Asset.DownloadUrl}");
        Assert(ComponentCatalog.BuildRepoFileApiUrl(repoFilePick.RepoFile!.Repo, repoFilePick.RepoFile!.Path)
               == "https://api.github.com/repos/Ixaruz/Luna-App/contents/enctemplate.zip?ref=HEAD",
            "仓库文件树的备用地址应是 contents 端点（raw 在部分网络下不可达）");
        Assert(ComponentCatalog.RawContentAccept == "application/vnd.github.raw",
            "contents 端点必须发 application/vnd.github.raw，否则拿回的是这段文件的 JSON 元数据（HTTP 200）");

        // ── ⑤b IsPayload 只属于「框架」的手写 picker，槽声明一律不许标 ──
        //
        // IsPayload 的语义是「散装下载的**引导** payload（fusee.bin / hekate_ctcaer_*.bin）」：
        // 它让产物改道 download/<组件>/payload/，**并被「把 payload 放到对应目录」这个开关整个跳过**
        // （OutputStager 与 MainViewModel 两处都有 `if (IsPayload && !IncludePayloads) continue`）。
        //
        // 对槽驱动组件来说，标上它只有一个后果：用户勾了组件、没勾那个开关 → **静默什么都不产出**。
        // 「底层」那两个 .bin 看着最像 payload，所以最容易被人（包括我）顺手标上 ——
        // 这条因此穷举**所有**槽，而不是只钉这两个；恒真与否由「有没有人标」决定，不是由我记不记得住。
        var payloadSlots = slotComponents
            .SelectMany(d => d.Slots.Select(s => (Def: d, Slot: s)))
            .Where(x => x.Slot.IsPayload)
            .Select(x => $"{x.Def.Kind}/{x.Slot.Key}")
            .ToList();
        Assert(payloadSlots.Count == 0,
            "槽声明不该标 IsPayload —— 那会让它被「把 payload 放到对应目录」开关静默跳过"
            + (payloadSlots.Count == 0 ? "" : "；标了的：" + string.Join("、", payloadSlots)));

        // ── ⑤d 「离线固件」：默认值是通配模式，落点从**下载文件名**造版本目录 ──────
        //
        // ⚠️ 这一段里的字面量全部来自**上游实测**（2026-09-18：走 GitHub API 把 92 个版本
        //    逐个数过、再用 tools/zip-listing.py 读中央目录），不是照着用户原文编的：
        //      · 资源名一律 `Firmware.<版本>.zip`（**点分隔，从来没有空格**），例如 Firmware.23.0.0.zip；
        //      · 包内 238 个 .nca / .cnmt.nca **全在压缩包根**，没有顶层目录。
        //    第 70 条那条教训：这两件事只有联网工具够得着，而「注释里的实测」必须是真的。
        var firmware = ComponentCatalog.All.First(d => d.Kind == ComponentKind.Firmware);
        var firmwareSlot = firmware.Slots[0];

        Assert(firmwareSlot.FileName(null) == "Firmware.x.x.x.zip",
            "离线固件的默认文件名应是**版本占位模式**而不是某个具体版本号"
            + $"，实际 {firmwareSlot.FileName(null)}");

        // 该模式必须能真的命中上游那种名字，且命中的是「通配」这一级而不是「猜」。
        // 判据不只是「选出来了」：Stem / SoleCandidate 也能选中，但日志会把它标成猜，
        // 而这里根本不是猜 —— 前缀 `Firmware.`、分隔符与 `.zip` 都是逐字确定的。
        var firmwareRelease = new ReleaseInfo(
            "23.0.0", "23.0.0", false, DateTimeOffset.UnixEpoch, string.Empty,
            [new ReleaseAsset("Firmware.23.0.0.zip", "https://example.invalid/fw", 340421224)]);

        var firmwareMatch = AssetNameMatcher.Find(firmwareRelease, firmwareSlot.FileName(null));
        Assert(firmwareMatch is not null && firmwareMatch.Kind == AssetMatchKind.Wildcard,
            "Firmware.x.x.x.zip 应**通配**命中实测的资源名 Firmware.23.0.0.zip"
            + $"（不能降级成猜），实际 {(firmwareMatch is null ? "没命中" : firmwareMatch.Kind.ToString())}");

        // 匹配之后，落点里的 {asset} 必须解析成「下载文件名去掉扩展名」。
        // 这是「包内是平的」那条实测逼出来的唯一可行方案：版本号不在包里，只在资源名里。
        var firmwarePicks = ComponentCatalog.PickSlot(
            firmware, firmwareSlot, firmwareRelease, new WizardOptions { Language = "zh-Hans" });
        Assert(firmwarePicks.Count == 1,
            $"离线固件应恰好选中 1 个资源，实际 {firmwarePicks.Count}");
        Assert(firmwarePicks[0].ResolvedTargets.Count == 1
               && firmwarePicks[0].ResolvedTargets[0].Directory == "Firmware/Firmware.23.0.0",
            "离线固件的落点应是 out/Firmware/Firmware.23.0.0/（目录名 = 下载文件名去掉 .zip）"
            + "，实际：" + string.Join("、", firmwarePicks[0].ResolvedTargets.Select(t => t.Directory)));
        Assert(firmwarePicks[0].FileName == "Firmware.23.0.0.zip",
            "下载下来的文件名应是上游那个资源名（{asset} 就是按它算的）"
            + $"，实际 {firmwarePicks[0].FileName}");

        // 包内是平的 ⇒ 解压计划必须保持「原样铺开」。
        // 声明一个 StripPrefix 的话，238 个文件会被一次性筛空，而产物是**一个空文件夹**。
        Assert(firmwareSlot.Extract.KeepsEverything,
            "离线固件包内没有顶层目录可借（实测 238 个条目全在根），解压计划必须原样铺开");

        // 界面上显示的落点也要用同一条替换规则（否则高级设置里写的是 out/Firmware/{asset}/）。
        Assert(ComponentCatalog.DescribeSlotPlacement(firmwareSlot, firmwareSlot.FileName(null))
                   == "out/Firmware/Firmware.x.x.x/",
            "高级设置里那一行应显示替换后的落点（显示的默认值就是声明里那个模式，不另编版本号），"
            + "实际：" + ComponentCatalog.DescribeSlotPlacement(firmwareSlot, firmwareSlot.FileName(null)));

        // 反过来也要钉住：散装 .bin 的落点必须是 bootloader/payloads，
        // 而不是被当成压缩包铺 out 根（用户清单里这两个的落点是逐个写明的）。
        foreach (var kind in new[] { ComponentKind.LockpickRcm, ComponentKind.TegraExplorer })
        {
            var definition = ComponentCatalog.All.First(d => d.Kind == kind);
            var slot = definition.Slots[0];
            Assert(slot.Source == SlotSource.Release,
                $"{kind} 走 Release 资源（上游实测该 Release 只有一个 .bin），实际是 {slot.Source}");
            Assert(slot.Targets.Count == 1 && slot.Targets[0].Directory == "bootloader/payloads",
                $"{kind} 应放到 out/bootloader/payloads（用户要求，与 hekate 自带 payloads/ 同目录），"
                + "实际：" + string.Join("、", slot.Targets.Select(t => t.Directory)));
            Assert(slot.Extract.KeepsEverything,
                $"{kind} 是散装文件、不是压缩包，解压计划应保持原样（有筛选/去前缀会在解压时才发现没东西可筛）");
        }

        // ── ⑥ 槽驱动组件的请求按**实际仓库**分组（同一个仓库只查一次 Release）──
        var dbiRequests = ComponentCatalog.RepoRequestsFor(dbi, new WizardOptions { Language = "zh-Hans" });
        Assert(dbiRequests.Count == 1,
            $"DBI 的两个槽都在同一个仓库，应只产出 1 个请求（查两次 Release 纯属浪费配额），实际 {dbiRequests.Count}");

        var splitOptions = new WizardOptions { Language = "zh-Hans" };
        splitOptions.AssetAddresses["Dbi/DbiTranslation"] = "other/dbi-translations";
        var splitRequests = ComponentCatalog.RepoRequestsFor(dbi, splitOptions);
        Assert(splitRequests.Count == 2,
            $"两个槽被改到不同仓库时应产出 2 个请求，实际 {splitRequests.Count}");
        Assert(splitRequests.All(r => r.Slots.Count > 0),
            "槽驱动的请求必须带上自己负责的槽 —— 下载阶段靠它做逐槽对账");

        // 手写 picker 的组件（框架那四个）不受影响：原样返回 Repos，行为逐字节不变
        var framework = ComponentCatalog.All.First(d => d.Kind == ComponentKind.Atmosphere);
        Assert(ComponentCatalog.RepoRequestsFor(framework, new WizardOptions { Language = "zh-Hans" })
                   .SequenceEqual(framework.Repos),
            "手写 picker 的组件应原样返回声明的 Repos（Slots 为空时这条路径不该有任何改变）");

        // ── ⑦ 7z：usb-botbase 的 atmosphere.7z 是唯一一个非 zip 的包 ──
        // 夹具是预生成的 259 字节 7z（SharpCompress 0.50 没有写 7z 的能力，只能内联一段 base64）。
        var sevenZip = Path.Combine(AppPaths.DownloadRoot, "__fixture__.7z");
        var sevenZipOut = Path.Combine(AppPaths.DownloadRoot, "__fixture7z__");

        try
        {
            TryDeleteDirectory(sevenZipOut);
            AppPaths.EnsureDirectory(AppPaths.DownloadRoot);
            File.WriteAllBytes(sevenZip, Convert.FromBase64String(SevenZipFixtureBase64));

            Assert(ArchiveExtractor.IsArchive("atmosphere.7z"),
                "atmosphere.7z 必须被认成「要解压的包」—— 判据委托给 ArchiveExtractor，不在这里另写一份扩展名");
            Assert(ArchiveExtractor.IsArchive("ATMOSPHERE.7Z"),
                "扩展名判断应不区分大小写（上游改个大小写不该让包变成「散装文件」原样拷进 out）");
            Assert(ArchiveExtractor.IsArchive("sphaira.zip") && !ArchiveExtractor.IsArchive("ftpd.nro"),
                "zip 仍是压缩包、裸 .nro 仍是散装文件");

            var extracted = ArchiveExtractor.Extract(sevenZip, sevenZipOut, null, CancellationToken.None);
            Assert(extracted == 2, $"7z 夹具应解出 2 个文件，实际 {extracted} 个");
            Assert(File.Exists(Path.Combine(sevenZipOut, "atmosphere", "contents", "430000000000000B", "config.cfg")),
                "7z 里的目录结构应被完整还原（SharpCompress 的入口是 SevenZipArchive.OpenArchive，不是 Open）");
            Assert(File.Exists(Path.Combine(sevenZipOut, "atmosphere", "contents", "430000000000000B", "flags", "boot2.flag")),
                "7z 里嵌套目录下的文件也要解出来");
        }
        finally
        {
            TryDelete(sevenZip);
            TryDeleteDirectory(sevenZipOut);
        }

        // ── ⑧ 解压规则：剥前缀 + 白名单（sys-ftpd / wiliwili）────────
        //
        // 样本是**实测的真实包内路径**：
        //   ELY3M/sys-ftpd 的 release.zip 里是 out/config/… 与 out/atmosphere/…，
        //   比用户的落点要求（out 根）多了一层 out/（见 tools/probe-background.py 的探测）。
        //   wiliwili 的包内结构是用 **HTTP Range 只取 ZIP 中央目录**测的（见 tools/zip-listing.py）——
        //   它比 probe-background.py 更省事（不下载整个包），后者只探「后台」那 5 个。
        Assert(ExtractPlan.All.KeepsEverything,
            "默认的解压规则应是「原样铺开」—— 绝大多数包都靠这个默认值，它一变全体产物都会动");

        // ⚠️ 规则必须取自**声明本身**，不能在这里现造一份副本。
        //    现造的话，把声明里的 Extract 删掉/改错，下面那些用例照样全绿 ——
        //    而产物会变成 out/out/atmosphere/…（多一层），用户完全看不出来。
        //    这正是「恒真断言 = 橡皮图章」那条教训：断言引用的东西必须真的来自生产端。
        var sysFtpd = ComponentCatalog.All.First(d => d.Kind == ComponentKind.SysFtpd);
        var sysFtpdSlot = sysFtpd.Slots.First(s => s.Key == "SysFtpd");

        Assert(!sysFtpdSlot.Extract.KeepsEverything,
            "sys-ftpd 的 release.zip 里多一层 out/，必须声明解压规则"
            + "（删掉它，产物会变成 out/out/atmosphere/…）");
        Assert(sysFtpdSlot.Extract.StripPrefix == "out",
            "sys-ftpd 要剥掉的前缀是 out（实测：包里是 out/atmosphere/… 与 out/config/…），"
            + $"实际声明的是 {sysFtpdSlot.Extract.StripPrefix ?? "(空)"}");
        Assert(sysFtpdSlot.Extract.KeepTopLevelDirectories.OrderBy(x => x, StringComparer.Ordinal)
                   .SequenceEqual(["atmosphere", "config"]),
            "用户要求「只取 atmosphere 与 config 两个文件夹」，实际声明的是："
            + string.Join("、", sysFtpdSlot.Extract.KeepTopLevelDirectories));
        Assert(sysFtpdSlot.Extract.KeepFileNames.Count == 0,
            "sys-ftpd 按目录白名单筛，不该同时用文件名白名单（两个白名单是「或」的关系，混用会放宽筛选）");

        var sysFtpdPlan = sysFtpdSlot.Extract;

        var planCases = new (ExtractPlan Plan, string Entry, string? Expected)[]
        {
            // sys-ftpd：剥 out/ + 只留 atmosphere 与 config
            (sysFtpdPlan, "out/config/sys-ftpd/config.ini", "config/sys-ftpd/config.ini"),
            (sysFtpdPlan, "out/atmosphere/contents/420000000000000E/exefs.nsp",
                "atmosphere/contents/420000000000000E/exefs.nsp"),
            (sysFtpdPlan, "out/atmosphere/contents/420000000000000E/flags/boot2.flag",
                "atmosphere/contents/420000000000000E/flags/boot2.flag"),
            // 前缀目录项本身不该落成一个空文件夹
            (sysFtpdPlan, "out", null),
            // 不在 out/ 之下的东西（比如仓库自带的 README）不是我们要的内容
            (sysFtpdPlan, "README.md", null),
            // 白名单之外的顶层目录要丢掉 —— 这正是用户那句「只取 atmosphere 与 config 两个文件夹」
            (sysFtpdPlan, "out/switch/sys-ftpd.nro", null),
            // 目录穿越：剥完前缀仍要能识别出来（真正的拦截在 ResolveSafePath，这里只保证不被当成合法相对路径）
            (sysFtpdPlan, "out/../../evil.txt", null),

            // wiliwili：只要那一个文件，其余一概不要。
            // ⚠️ 这里**故意**现造一份 plan，不是「忘了取声明」—— 上面那条「不能现造副本」的规矩
            //    针对的是「用现造副本代替对声明的验证」。而 wiliwili 的**声明本身**已在 ③d 段
            //    逐项钉住（StripPrefix / KeepFileNames / Targets / 真实条目名的 Map 结果）。
            //    这几条要验的是**通用语义**：文件名白名单在任意层级都按文件名匹配、且返回完整路径。
            //    wiliwili 的真实 plan 带着 StripPrefix，走不到 `switch/wiliwili.nro` 这条分支，
            //    所以只有现造一份不带前缀的 plan 才能把这条语义单独测出来。
            (new ExtractPlan { KeepFileNames = ["wiliwili.nro"] }, "wiliwili.nro", "wiliwili.nro"),
            (new ExtractPlan { KeepFileNames = ["wiliwili.nro"] }, "switch/wiliwili.nro", "switch/wiliwili.nro"),
            (new ExtractPlan { KeepFileNames = ["wiliwili.nro"] }, "README.md", null),
            (new ExtractPlan { KeepFileNames = ["wiliwili.nro"] }, "atmosphere/package3", null),

            // 默认规则必须是恒等映射（拿一个真实路径验，别只验空串）
            (ExtractPlan.All, "atmosphere/contents/430000000000000B/exefs.nsp",
                "atmosphere/contents/430000000000000B/exefs.nsp"),
        };

        foreach (var (plan, entry, expected) in planCases)
        {
            var actual = plan.Map(entry);
            Assert(actual == expected,
                $"解压规则对「{entry}」应得到 {(expected is null ? "「丢弃」" : "「" + expected + "」")}，"
                + $"实际 {(actual is null ? "「丢弃」" : "「" + actual + "」")}");
        }

        // 声明了筛选却一个文件都没留下 ⇒ **必须报错**，不能留一个空文件夹而日志正常。
        // 这是本机制最重要的一条：上游改了包结构时，静默的空产物是看不见的。
        var mismatchZip = Path.Combine(AppPaths.DownloadRoot, "__fixture-mismatch__.zip");
        var mismatchOut = Path.Combine(AppPaths.DownloadRoot, "__fixture-mismatch-out__");

        try
        {
            TryDeleteDirectory(mismatchOut);
            TryDelete(mismatchZip);
            AppPaths.EnsureDirectory(AppPaths.DownloadRoot);

            using (var archive = ZipFile.Open(mismatchZip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("README.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("nothing the plan wants");
            }

            var threw = false;
            try
            {
                ArchiveExtractor.Extract(mismatchZip, mismatchOut, null, CancellationToken.None, sysFtpdPlan);
            }
            catch (IOException)
            {
                threw = true;
            }

            Assert(threw,
                "声明的解压规则一个文件都没匹配上时必须抛错 —— 静默留一个空文件夹等于「组件装了个寂寞」");

            // 反过来：没有筛选时，同样的包照常解出来（证明上面的报错确实来自「筛空」而不是包坏了）
            var kept = ArchiveExtractor.Extract(mismatchZip, mismatchOut, null, CancellationToken.None);
            Assert(kept == 1 && File.Exists(Path.Combine(mismatchOut, "README.txt")),
                "不加筛选时同一个包应正常解出 1 个文件（否则上面那条「筛空要报错」的判据就不成立）");
        }
        finally
        {
            TryDelete(mismatchZip);
            TryDeleteDirectory(mismatchOut);
        }

        // ── ⑨ 同一个 sysmodule title 的组件互斥（botbase 二选一）──────
        //
        // 用户要求「sys-botbase 与 usb-botbase 提示冲突，只能二选一」。
        // 判据不是写死这两个名字，而是各槽声明的 SysmoduleTitleId（实测得来）——
        // 所以这里既验真实的那一对，也验规则的**穷举性**。
        var titled = slotComponents
            .SelectMany(d => d.Slots
                .Where(s => !string.IsNullOrWhiteSpace(s.SysmoduleTitleId))
                .Select(s => (d.Kind, s.Key, Title: s.SysmoduleTitleId!)))
            .ToList();

        Assert(titled.Count > 0,
            "前置条件：应至少有一个槽声明了 SysmoduleTitleId（否则本段恒真）");

        foreach (var (kind, key, title) in titled)
        {
            Assert(title.Length == 16 && title.All(Uri.IsHexDigit),
                $"{kind}/{key} 声明的 title「{title}」应是 16 位十六进制"
                + "（atmosphere/contents/<title> 的目录名就是这个格式，写错了冲突检测会静默失灵）");
        }

        // 每个 title 都要和**实测值**对得上。
        //
        // 为什么必须有这张表：只验「格式合法 + 组内自洽」的话，把 SysCon 的 title 改成
        // 和 botbase 一样，所有断言**照样全绿** —— 规则自洽、格式合法，只是声明的值错了，
        // 于是两个本来井水不犯河水的组件被报成冲突（或者反过来，真冲突的那个被漏掉）。
        // 表里的值来自实测（逐个包看 `atmosphere/contents/<title>/` 的目录名；「后台」那 5 个见
        // tools/probe-background.py，其余几个是各自单独读包测的），不是设计选择。
        var expectedTitles = new Dictionary<ComponentKind, string>
        {
            [ComponentKind.SysBotbase] = "430000000000000B",
            [ComponentKind.UsbBotbase] = "430000000000000B",
            [ComponentKind.MissionControl] = "010000000000bd00",
            [ComponentKind.SysCon] = "690000000000000D",
            [ComponentKind.SysFtpd] = "420000000000000E",
            // 实测：下 masagrator/sys-ticon 1.0.8 的 sys-ticon.zip 看包内路径
            // → atmosphere/contents/00FF747765616BFF/。这是 twili 的**通用** title，
            // 将来别的 homebrew sysmodule 撞上它是**真冲突**，规则照样该报出来 —— 别因为它「通用」
            // 就把它从表里拿掉。
            [ComponentKind.SysTicon] = "00FF747765616BFF",
        };

        var titledKinds = titled.Select(x => x.Kind).ToHashSet();
        var untabulated = titledKinds.Except(expectedTitles.Keys).Select(k => k.ToString()).ToList();
        var staleTitles = expectedTitles.Keys.Except(titledKinds).Select(k => k.ToString()).ToList();
        Assert(untabulated.Count == 0,
            "声明了 SysmoduleTitleId 的组件都要在这张实测对照表里有值（否则它的 title 没被钉住）"
            + (untabulated.Count == 0 ? "" : "；缺的：" + string.Join("、", untabulated)));
        Assert(staleTitles.Count == 0,
            "实测对照表里不该有「没声明 title」的组件"
            + (staleTitles.Count == 0 ? "" : "；多余的：" + string.Join("、", staleTitles)));

        foreach (var (kind, expectedTitle) in expectedTitles)
        {
            var actual = titled.Where(x => x.Kind == kind).Select(x => x.Title).ToList();
            Assert(actual.Count == 1 && actual[0] == expectedTitle,
                $"{kind} 的 sysmodule title 实测是 {expectedTitle}，"
                + $"实际声明的是 {(actual.Count == 0 ? "(没声明)" : string.Join("、", actual))}");
        }

        // 真实的那一对：两个都勾 ⇒ 报一个冲突，且恰好是这两个
        var botbaseOptions = new WizardOptions { Language = "zh-Hans", SysBotbase = true, UsbBotbase = true };
        var botbaseConflicts = ComponentCatalog.FindTitleConflicts(botbaseOptions);
        Assert(botbaseConflicts.Count == 1,
            $"同时勾 sys-botbase 与 usb-botbase 应报 1 个冲突，实际 {botbaseConflicts.Count} 个");
        Assert(botbaseConflicts.Count == 1
               && botbaseConflicts[0].Kinds.ToHashSet().SetEquals([ComponentKind.SysBotbase, ComponentKind.UsbBotbase]),
            "冲突里应恰好是 sys-botbase 与 usb-botbase");
        Assert(botbaseConflicts.Count == 1 && botbaseConflicts[0].TitleId == "430000000000000B",
            "冲突要带上 title，日志里才能说清「撞的是哪个系统模块」");

        // 只勾一个 ⇒ 不报（否则提示会变成噪音，用户学会无视它）
        foreach (var only in new[] { ComponentKind.SysBotbase, ComponentKind.UsbBotbase })
        {
            var single = new WizardOptions { Language = "zh-Hans" };
            single.TrySetSelected(only, true);
            Assert(ComponentCatalog.FindTitleConflicts(single).Count == 0,
                $"只勾 {only} 时不该报冲突（误报会让人学会无视告警）");
        }

        // 穷举性：凡是**声明里**撞车的组合都必须被抓出来 —— 名单从声明现取，不手抄。
        // 这条保证「将来再加一个撞车的 sysmodule」不需要改任何代码就会被发现。
        var declaredTitleGroups = titled
            .GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Kind).Distinct().Count() > 1)
            .ToList();

        Assert(declaredTitleGroups.Count > 0,
            "前置条件：声明里应至少有一组撞车的 title（当前是 botbase 那两个）");

        foreach (var group in declaredTitleGroups)
        {
            var kinds = group.Select(x => x.Kind).Distinct().ToList();
            var all = new WizardOptions { Language = "zh-Hans" };
            foreach (var kind in kinds)
            {
                all.TrySetSelected(kind, true);
            }

            var found = ComponentCatalog.FindTitleConflicts(all);
            Assert(found.Any(c => c.Kinds.ToHashSet().SetEquals(kinds)),
                $"声明里撞车的 {string.Join("、", kinds)}（title {group.Key}）同时勾上时必须被报出来");
        }

        // 不同 title 的 sysmodule 一起勾不该报冲突（MissionControl / sys-con 各有自己的 title）
        var mixed = new WizardOptions
        {
            Language = "zh-Hans",
            MissionControl = true,
            SysCon = true,
            SysFtpd = true,
        };
        Assert(ComponentCatalog.FindTitleConflicts(mixed).Count == 0,
            "装不同系统模块的组件之间不该报冲突（判据是 title 相同，不是「都算后台组件」）");

        // 界面与校验器必须用**同一套判据**：拿同一组勾选分别问两边，结论要一致。
        var vm = new MainViewModel();
        foreach (var component in vm.Components)
        {
            component.IsSelected = component.Kind is ComponentKind.SysBotbase or ComponentKind.UsbBotbase;
        }

        var vmConflicts = vm.Components.Where(c => c.HasConflictHint).Select(c => c.Kind).ToList();
        Assert(vmConflicts.Count == 2,
            "同时勾上两个 botbase 时，两张卡片都要给出冲突提示"
            + (vmConflicts.Count == 2 ? "" : $"；实际提示的有 {vmConflicts.Count} 张"));

        // 取消一个之后提示要**消失**（只加不清的话会留一条「和另一个冲突」而另一个已经没了）
        vm.Components.First(c => c.Kind == ComponentKind.UsbBotbase).IsSelected = false;
        Assert(vm.Components.All(c => !c.HasConflictHint),
            "取消其中一个之后，冲突提示必须全部清掉（否则提示在说谎）");

        // ── ⑩ 仓库目录槽：取一个目录下的**所有文件**（theme-patches 的 systemPatches）──
        //
        // 用户要求原文：「另取 exelix11/theme-patches 的 tree/master/systemPatches 下**所有文件（不要文件夹）**
        //                → out/themes/systemPatches；与 NXThemesInstaller.nro **同框绑定**」。
        //
        // ⚠️ 这一段所有判据都**从声明本身取**（`themesInstaller.Slots[...]`），不在测试里另造一份副本 ——
        //    上一轮刚踩过：护栏验的是「测试里现造的副本」时，把声明里那一行删掉**照样全绿**。
        var themesInstaller = ComponentCatalog.All.First(d => d.Kind == ComponentKind.NxThemesInstaller);
        Assert(themesInstaller.Slots.Count == 2,
            $"NXThemesInstaller 应是「安装器 + 补丁目录」两个槽（用户要求同框绑定），实际 {themesInstaller.Slots.Count} 个");

        var installerSlot = themesInstaller.Slots.First(s => s.Key == "NxThemesInstaller");
        Assert(installerSlot.Source == SlotSource.Release,
            "NXThemesInstaller.nro 是 Release 里的资源，应走 Release 通道");
        Assert(installerSlot.FileName("zh-Hans") == "NXThemesInstaller.nro",
            $"NXThemesInstaller.nro 的默认文件名应为 NXThemesInstaller.nro，实际「{installerSlot.FileName("zh-Hans")}」");
        Assert(installerSlot.Targets.Any(t => t.Directory == "switch/NXThemesInstaller"),
            "NXThemesInstaller.nro 应放到 out/switch/NXThemesInstaller（用户要求）");

        var patchesSlot = themesInstaller.Slots.First(s => s.Key == "ThemePatches");
        Assert(patchesSlot.Source == SlotSource.RepoDirectory,
            "systemPatches 是仓库里的一个**目录**（不是一个压缩包），必须走 RepoDirectory —— "
            + "走 Release 会去查一个从来没有 Release 的仓库，走 RepoFile 只能取一个文件");
        Assert(patchesSlot.Address("zh-Hans").DisplayName == "exelix11/theme-patches",
            $"补丁目录的来源应是用户给的 exelix11/theme-patches，实际 {patchesSlot.Address("zh-Hans").DisplayName}");
        Assert(patchesSlot.FileName("zh-Hans") == "systemPatches",
            "目录槽的第二个可改项代表**仓库内的目录路径**，默认应为 systemPatches，"
            + $"实际「{patchesSlot.FileName("zh-Hans")}」");
        Assert(patchesSlot.Targets.Any(t => t.Directory == "themes/systemPatches"),
            "补丁应落到 out/themes/systemPatches（用户要求）");
        Assert(patchesSlot.Extract.KeepsEverything && !patchesSlot.IsPayload,
            "目录槽是逐个文件复制，既没有压缩包要解、也不是 payload —— 声明里不该有解压筛选或 payload 标记");

        Assert(ComponentCatalog.RepoDirectorySlots(themesInstaller).Count == 1,
            "NXThemesInstaller 里应恰好有一个目录槽（多了会重复下载，少了补丁就取不回来）");
        Assert(ComponentCatalog.ReleaseSlots(themesInstaller).Count == 1,
            "只有 NXThemesInstaller.nro 那一个槽该去查 Release（theme-patches 根本没有 Release）");
        Assert(ComponentCatalog.RepoRequestsFor(themesInstaller, new WizardOptions { Language = "zh-Hans" }).Count == 1,
            "目录槽不该被算进 Release 请求里 —— 否则会为一个没有 Release 的仓库白跑一次查询");

        // 列目录的地址：contents 端点、**不带 ?ref=**（该端点不认 HEAD 这种符号引用，带了会 404）
        Assert(ComponentCatalog.BuildRepoDirectoryApiUrl(ComponentCatalog.ThemePatchesRepo, "systemPatches")
               == "https://api.github.com/repos/exelix11/theme-patches/contents/systemPatches",
            "列目录应走 contents 端点且不带 ref（走默认分支，上游把 master 改名成 main 也不会静默失效）");

        // 解析：只留文件，子目录 / 子模块一律丢掉（用户原话「不要文件夹」）
        const string directoryJson = """
        [
          {"type":"file","name":"F35B0F5BA43F534A6B8839F527B7D38854B71C4B000000000000000000000000.ips",
           "path":"systemPatches/F35B0F5BA43F534A6B8839F527B7D38854B71C4B000000000000000000000000.ips",
           "size":19,
           "download_url":"https://raw.githubusercontent.com/exelix11/theme-patches/master/systemPatches/F35B0F5B.ips"},
          {"type":"dir","name":"old","path":"systemPatches/old","size":0,"download_url":null},
          {"type":"file","name":"121FDD88B9A3EB71D99F936E45A0C76538C6B5AF000000000000000000000000.ips",
           "path":"systemPatches/121FDD88B9A3EB71D99F936E45A0C76538C6B5AF000000000000000000000000.ips",
           "size":24,
           "download_url":"https://raw.githubusercontent.com/exelix11/theme-patches/master/systemPatches/121FDD88.ips"},
          {"type":"submodule","name":"vendor","path":"systemPatches/vendor","size":0,"download_url":null}
        ]
        """;

        var listed = GitHubReleaseService.ParseDirectoryFiles(directoryJson);
        Assert(listed.Count == 2,
            $"列目录应只留下 2 个文件（子目录与子模块要丢掉），实际 {listed.Count} 个");
        Assert(listed.All(f => f.Name.EndsWith(".ips", StringComparison.Ordinal)),
            "目录槽里留下的都应是 .ips 补丁文件");
        Assert(listed[0].Name.StartsWith("121FDD88", StringComparison.Ordinal),
            "列目录的结果应按文件名排序 —— 顺序不稳定的话两次运行的日志对不上，排查时无从比对");
        Assert(listed.All(f => f.Path.StartsWith("systemPatches/", StringComparison.Ordinal)),
            "每个文件都要带回仓库内的完整路径（备用通道要用它拼 contents 端点）");

        // 路径指向的是**文件**而不是目录时，contents 端点返回的是 JSON 对象（不是数组）→ 空表，
        // 由「逐槽对账」点名报出来。不在这里抛「解析失败」，那条信息对用户没用。
        Assert(GitHubReleaseService.ParseDirectoryFiles("""{"type":"file","name":"a.ips"}""").Count == 0,
            "路径指向文件（返回对象而非数组）时应得到空表，交给逐槽对账去点名");
        Assert(GitHubReleaseService.ParseDirectoryFiles("[]").Count == 0,
            "空目录应得到空表（不是「随便挑一个」）");

        // 挑：N 个文件 → N 个下载物，各自带着名字、落点、来源路径
        var patchPicks = ComponentCatalog.PickRepoDirectorySlot(patchesSlot, ComponentCatalog.ThemePatchesRepo, listed);
        Assert(patchPicks.Count == listed.Count,
            $"目录槽应把列出来的每个文件都变成一个下载物（{listed.Count} 个 → 实际 {patchPicks.Count} 个）");
        Assert(patchPicks.All(p => p.Slot is { Kind: AssetMatchKind.Exact }),
            "目录槽没有「猜」这件事 —— 文件名是列目录列出来的，命中方式恒为逐字");
        Assert(patchPicks.All(p => p.RepoFile is not null),
            "每个补丁都要带上「哪个仓库的哪个路径」，才能白捡仓库文件树那套备用通道（raw 不通改走 contents）");
        Assert(patchPicks.All(p => p.ResolvedTargets.Any(t => t.Directory == "themes/systemPatches")),
            "每个补丁都要落到 out/themes/systemPatches");
        Assert(patchPicks.Any(p => p.FileName == listed[0].Name),
            "下载物的文件名应就是列目录列出来的那个名字（不是声明的、也不是猜的）");

        var emptyPicks2 = ComponentCatalog.PickRepoDirectorySlot(patchesSlot, ComponentCatalog.ThemePatchesRepo, []);
        Assert(emptyPicks2.Count == 0,
            "目录为空时应产出 0 个下载物，交给逐槽对账告警 —— 静默跳过会让用户以为补丁装上了");

        // 界面提示：目录槽那一行必须说清「右边填的是目录」，否则用户会以为要下某个叫 systemPatches 的文件
        var themesGroup = vm.AssetSlotGroups.First(g => g.Definition.Kind == ComponentKind.NxThemesInstaller);
        var patchRow = themesGroup.Slots.First(s => s.Key == "NxThemesInstaller/ThemePatches");
        Assert(patchRow.IsDirectorySlot,
            "目录槽在界面上要被标出来（那一行会多一句「取该目录下所有文件」的说明）");
        Assert(patchRow.PlacementHint.Contains("themes/systemPatches", StringComparison.Ordinal),
            $"目录槽的落点提示应指向 out/themes/systemPatches/，实际「{patchRow.PlacementHint}」");
        Assert(!patchRow.PlacementHint.Contains("out/systemPatches", StringComparison.Ordinal),
            "目录槽的落点提示不该退化成「out/systemPatches」——那看起来像一个叫 systemPatches 的文件");
        Assert(!themesGroup.Slots.First(s => s.Key == "NxThemesInstaller/NxThemesInstaller").IsDirectorySlot,
            "普通文件槽不该被标成目录槽（判据是槽的来源类型，不是「这个组件里有目录槽」）");

        // ── ⑪ 目录槽的**快路径**：整仓打包一次取回（codeload 的 tar.gz）────────
        //
        // 为什么要它：目录槽的「列目录 + 逐个文件」在 raw.githubusercontent.com 不可达的网络里
        // 会退化成**每个文件一次 API 调用** —— 实测那 20 个补丁就是 20 次，而未登录配额只有
        // 60/小时，第 17 个文件就撞上了 HTTP 403（那次 e2e 的 `out/` 因此是空的）。
        // 整包一次取回只要 **1 次请求**，而且 codeload **不占 API 配额**。
        //
        // ⚠️ 快路径失败会**静默回落**到老路（行为与以前逐字节一致），所以这里**不能只验成功路径** ——
        //    「回落之后还能不能用」才是这套设计成立的前提。
        var patchRepo = ComponentCatalog.ThemePatchesRepo;

        // ① 地址：codeload + tar.gz + **符号引用 HEAD**
        Assert(ComponentCatalog.BuildRepoArchiveUrl(patchRepo)
               == "https://codeload.github.com/exelix11/theme-patches/tar.gz/HEAD",
            "整包地址应是 codeload + tar.gz + HEAD，实际 " + ComponentCatalog.BuildRepoArchiveUrl(patchRepo));
        Assert(!ComponentCatalog.BuildRepoArchiveUrl(patchRepo).Contains("api.github.com", StringComparison.Ordinal),
            "整包地址不该走 api.github.com —— 那样又占一次 API 配额，快路径就白做了");
        Assert(!ComponentCatalog.BuildRepoArchiveUrl(patchRepo).Contains("master", StringComparison.Ordinal),
            "整包地址不该写死分支名（上游把 master 改名成 main 就会静默 404），要用符号引用 HEAD");
        Assert(ComponentCatalog.RepoArchiveFileName(patchRepo) == "theme-patches-HEAD.tar.gz",
            $"整包的临时文件名应是「仓库名-HEAD.tar.gz」，实际 {ComponentCatalog.RepoArchiveFileName(patchRepo)}");

        // ② 挑条目：用**真实 tarball 的条目名**（2026-09-18 实测 `codeload …/tar.gz/HEAD` 得来），不是编的。
        //    真实的坑都在这里：根级散条目（pax_global_header）、带 SHA 的根目录名、
        //    .github/ 下的一堆无关文件、以及 systemPatches/ 目录项本身。
        var realTarEntries = new[]
        {
            "pax_global_header",
            "theme-patches-HEAD/",
            "theme-patches-HEAD/.github/",
            "theme-patches-HEAD/.github/install.gif",
            "theme-patches-HEAD/.github/make-json/Program.cs",
            "theme-patches-HEAD/.github/workflows/ci.yml",
            "theme-patches-HEAD/README.md",
            "theme-patches-HEAD/systemPatches/",
            "theme-patches-HEAD/systemPatches/121FDD88B9A3EB71D99F936E45A0C76538C6B5AF000000000000000000000000.ips",
            "theme-patches-HEAD/systemPatches/154BECB1F64B08E685892C17742703E7C536C428000000000000000000000000.ips",
        };

        var picked = ComponentCatalog.SelectDirectoryEntries(realTarEntries, "systemPatches");
        Assert(picked.Count == 2,
            $"应只挑出 systemPatches/ 下的 2 个文件，实际 {picked.Count} 个："
            + string.Join("、", picked.Select(x => x.FileName)));
        Assert(picked.All(x => !x.FileName.Contains('/')),
            "落盘名必须是**裸文件名** —— 带目录的话就与「逐个文件下载」产出不同的名字，"
            + "下游（暂存 / 落点 / 清单）全都要跟着改");
        Assert(picked.All(x => x.Entry.StartsWith("theme-patches-HEAD/systemPatches/", StringComparison.Ordinal)),
            "条目名应是**完整包内路径**（回写 RepoFileRef 要用它拼备用地址）");
        Assert(picked[0].FileName == "121FDD88B9A3EB71D99F936E45A0C76538C6B5AF000000000000000000000000.ips",
            "结果应按文件名排序 —— 与列目录那条路径同一个顺序，两条通道的日志才对得上");
        Assert(!picked.Any(x => x.FileName == "pax_global_header"),
            "根级散条目（pax_global_header 之类）没有第一层目录可剥，必须丢掉");

        // 第一层目录**叫什么都不影响**：tarball 根目录名带 HEAD/SHA，无法在声明里预先写死。
        // 两个名字都是**实测**来的：`codeload …/tar.gz/HEAD` 给 `theme-patches-HEAD/`，
        // 而 `api.github.com/…/tarball/HEAD`（302 到 codeload 的 legacy 形式）给的是
        // `exelix11-theme-patches-e9ba165/` —— 带 commit SHA。剥第一层时**必须两种都吃**。
        var renamedRoot = realTarEntries
            .Select(e => e.Replace("theme-patches-HEAD/", "exelix11-theme-patches-e9ba165/", StringComparison.Ordinal))
            .ToArray();
        Assert(ComponentCatalog.SelectDirectoryEntries(renamedRoot, "systemPatches").Count == 2,
            "剥第一层目录时不该关心那一层叫什么 —— codeload 给 <仓库名>-HEAD/，legacy 形式给 <owner>-<repo>-<sha>/，"
            + "两者都得能吃下（用 ExtractPlan.StripPrefix 这种常量前缀就会在这里失效）");

        // 界面上那一栏是自由输入，两侧斜杠要容忍
        Assert(ComponentCatalog.SelectDirectoryEntries(realTarEntries, "/systemPatches/").Count == 2,
            "目录名两侧的斜杠应被容忍（用户在「文件名 / 目录」那一栏可能带斜杠）");

        // 反向：拿不到就是拿不到，**不许拿别的东西凑数**
        Assert(ComponentCatalog.SelectDirectoryEntries(realTarEntries, "themes").Count == 0,
            "目录名对不上时应挑出 0 个，交给调用方回落到老路 —— 凑数会让用户拿到一包不相干的文件");
        Assert(ComponentCatalog.SelectDirectoryEntries(
                [.. realTarEntries, "theme-patches-HEAD/systemPatches/sub/deep.ips"], "systemPatches").Count == 2,
            "只取**直接**子文件：systemPatches/sub/deep.ips 更深一层，不该被取");
        Assert(ComponentCatalog.SelectDirectoryEntries([], "systemPatches").Count == 0,
            "空条目表应挑出 0 个（前置条件：这里不能抛异常）");
        Assert(ComponentCatalog.SelectDirectoryEntries(realTarEntries, "").Count == 0,
            "目录名为空时应挑出 0 个 —— 不能退化成「整个仓库都要」");

        // ③ 快路径的产物必须与老路**同构**：否则两条通道的落点与备用地址会各自腐烂
        var asDirectoryFiles = ComponentCatalog.AsDirectoryFiles(
            patchRepo, "systemPatches", [("a.ips", 19L), ("b.ips", 24L)]);
        Assert(asDirectoryFiles.Count == 2 && asDirectoryFiles[0].Path == "systemPatches/a.ips",
            "整包取回的文件要包成与列目录同构的 RepoDirectoryFile（Path = 目录/文件名）");
        Assert(asDirectoryFiles[0].DownloadUrl
               == "https://raw.githubusercontent.com/exelix11/theme-patches/HEAD/systemPatches/a.ips",
            "同构还包括备用通道的地址，实际 " + asDirectoryFiles[0].DownloadUrl);

        var bundlePicks = ComponentCatalog.PickRepoDirectorySlot(patchesSlot, patchRepo, asDirectoryFiles);
        Assert(bundlePicks.Count == 2
               && bundlePicks.All(p => p.Slot is { Kind: AssetMatchKind.Exact })
               && bundlePicks.All(p => p.RepoFile is not null)
               && bundlePicks.All(p => p.ResolvedTargets.Any(t => t.Directory == "themes/systemPatches")),
            "快路径与老路必须走同一个 PickRepoDirectorySlot 入口（落点、命中方式、备用通道全都一致）");

        // ④ 真造一个 tar.gz 走一遍解包。用 .NET 内置的 System.Formats.Tar 写夹具 ——
        //    与解包端**同一个库**，所以「格式对不对」本身也被覆盖到了；
        //    也不必像 7z 那样内联 base64（那个是因为 SharpCompress 只能读不能写）。
        var bundleScratch = Path.Combine(AppPaths.OutputRoot, "..", "bundle-fixture");
        bundleScratch = Path.GetFullPath(bundleScratch);
        TryDeleteDirectory(bundleScratch);

        try
        {
            var archivePath = Path.Combine(bundleScratch, "theme-patches-HEAD.tar.gz");
            WriteTarGz(archivePath,
                ("pax_global_header", string.Empty),
                ("theme-patches-HEAD/", string.Empty),
                ("theme-patches-HEAD/README.md", "readme"),
                ("theme-patches-HEAD/.github/install.gif", "gif"),
                ("theme-patches-HEAD/systemPatches/", string.Empty),
                ("theme-patches-HEAD/systemPatches/AAA.ips", "IPS32AAA"),
                ("theme-patches-HEAD/systemPatches/BBB.ips", "IPS32BBB"),
                ("theme-patches-HEAD/systemPatches/sub/CCC.ips", "IPS32CCC"));

            var bundleOut = Path.Combine(bundleScratch, "out");
            var extracted = ArchiveExtractor.ExtractDirectoryFromTarGz(archivePath, "systemPatches", bundleOut);

            Assert(extracted.Count == 2,
                $"应从整包里解出 2 个补丁，实际 {extracted.Count} 个："
                + string.Join("、", extracted.Select(x => x.FileName)));
            Assert(File.Exists(Path.Combine(bundleOut, "AAA.ips")) && File.Exists(Path.Combine(bundleOut, "BBB.ips")),
                "解出来的文件应落在目标目录**根**（裸文件名），而不是 systemPatches/ 子目录里");
            Assert(!Directory.Exists(Path.Combine(bundleOut, "systemPatches")),
                "不该在目标目录下再建一层 systemPatches/ —— 落点已由槽的 Targets 决定");
            Assert(File.ReadAllText(Path.Combine(bundleOut, "AAA.ips")) == "IPS32AAA",
                "解出来的内容应是原字节（不能被当成文本处理）");
            Assert(!File.Exists(Path.Combine(bundleOut, "CCC.ips")),
                "systemPatches/sub/CCC.ips 更深一层，不该被解出来");
            Assert(!File.Exists(Path.Combine(bundleOut, "README.md")),
                "systemPatches 之外的文件不该被解出来");

            // 筛空**必须抛错**（与 ExtractPlan 同一条规矩）—— 静默留个空目录在日志里看不见
            var threwOnEmpty = false;
            try
            {
                ArchiveExtractor.ExtractDirectoryFromTarGz(archivePath, "noSuchDir", Path.Combine(bundleScratch, "out2"));
            }
            catch (IOException)
            {
                threwOnEmpty = true;
            }

            Assert(threwOnEmpty,
                "目录在整包里不存在时必须抛 IOException，而不是安静地什么都不解 —— "
                + "否则用户会拿到一套没有补丁的配置，而日志一切正常");
        }
        finally
        {
            TryDeleteDirectory(bundleScratch);
        }
    }

    /// <summary>
    /// 7z 回归夹具：259 字节、含 <c>atmosphere/contents/430000000000000B/config.cfg</c> 与
    /// <c>…/flags/boot2.flag</c> 两个文件（用 py7zr 预生成后 base64 内联）。
    ///
    /// 为什么要内联而不是放个二进制文件：本项目的测试夹具一律走源码，
    /// 免得「夹具文件没被拷进仓库」这种失败看起来像功能坏了。
    /// ⚠️ SharpCompress 0.50 **只能读不能写** 7z，所以这份夹具只能预先造好、不能现场生成。
    /// </summary>
    private const string SevenZipFixtureBase64 =
        "N3q8ryccAATMZoEEzQAAAAAAAAAWAAAAAAAAANrRrlcBACJTRVZFTi1aSVAtUk9PVApuZXN0ZWQtZmlsZS1wYXlsb2FkCgDgASUAnl0AAIEzB64P0Hif/J85EJxt+2pcRsiMI2VEGnUqSfRybjuOZjDxQTlW/a+17EEF5b6OWNTXCNeKUPw/kspkPT7BFex/XQ16JqxnhTEfM/xKZ/6CWPtcLNPXvuBZVHfT6CUxoLVQOXqqV9PBYr+sbYHFxGrDm2uRQdp6ZxdjvqWTgHQuS8kfJI3puZFWAMva9ZDDw3H5hoQLUov9ZfAAAAAAFwYnAQmApgAHCwEAASEhARgMgSYAAA==";

    /// <summary>
    /// payload.bin 兜底：本地化 hekate 包只发 zip、不发独立 <c>.bin</c>，
    /// 包里那份叫 <c>hekate_ctcaer_&lt;ver&gt;.bin</c>，而 hekate 约定根目录叫 <c>payload.bin</c>。
    /// </summary>
    private static void CheckPayloadBinFallback()
    {
        Section("payload.bin 兜底：本地化包不发独立 .bin 时从包内复制");

        var hekateUnpacked = AppPaths.ComponentUnpackedRoot(ConfigGenerator.FolderName(ComponentKind.Hekate));
        TryDeleteDirectory(hekateUnpacked);

        var options = new WizardOptions
        {
            Atmosphere = false,
            Hekate = true,
            Ultrahand = false,
            SysPatch = false,
            BootStock = true,
            BootSysNand = false,
            BootEmuNand = false,
            IncludeComponentFilesInOutput = true,
            IncludePayloads = true,
            IncludeBootDat = false,
        };
        ApplyCatalogDefaults(options);

        try
        {
            // 模拟 easyworld/hekate 的包：只有 bootloader/** 和根目录的 hekate_ctcaer_<ver>.bin
            WriteText(Path.Combine(hekateUnpacked, "hekate_ctcaer_6.5.3.bin"), "HEKATE-PAYLOAD");
            WriteText(Path.Combine(hekateUnpacked, "bootloader", "update.bin"), "UPDATE-BIN");

            var result = Generate(options);
            var payload = Path.Combine(AppPaths.OutputRoot, "payload.bin");

            Assert(File.Exists(payload), "包内有 hekate_ctcaer_*.bin 时应在 out 根目录补出 payload.bin");
            Assert(File.ReadAllText(payload) == "HEKATE-PAYLOAD", "补出来的 payload.bin 内容应与包内那份一致");
            Assert(result.WrittenFiles.Contains("payload.bin"),
                "补出来的 payload.bin 必须进清单 —— 清单是清理与打包校验的唯一依据，漏记会被当垃圾删掉");

            // ① 已经存在 payload.bin 时**绝不覆盖**：官方源 / 8G 模式摆好的那份才是对的
            WriteText(Path.Combine(hekateUnpacked, "payload.bin"), "OFFICIAL-PAYLOAD");
            Generate(options);
            Assert(File.ReadAllText(payload) == "OFFICIAL-PAYLOAD",
                "根目录已有 payload.bin 时不该用包内的 hekate_ctcaer_*.bin 覆盖它");

            // ② 候选不唯一时**什么都不做** —— 与其猜一个，不如不做。
            //    猜错的代价是把一个错的 payload 当成启动项，机器直接起不来。
            File.Delete(Path.Combine(hekateUnpacked, "payload.bin"));
            WriteText(Path.Combine(hekateUnpacked, "hekate_ctcaer_9.9.9.bin"), "ANOTHER-PAYLOAD");
            Generate(options);
            Assert(!File.Exists(payload), "候选有多个时不该猜一个当 payload.bin");
        }
        finally
        {
            TryDeleteDirectory(hekateUnpacked);
            Generate(_fullOptions!);
        }
    }

    // ── 90DNS 拆成两个开关（真实 / 虚拟），并与 enable_dns_mitm 双向绑定 ──
    //
    // 改动前只有一个 Use90Dns，且 hosts 文件是「跟着引导模式」写的 —— 想只给虚拟系统挡遥测
    // 做不到。拆开之后两个开关各自决定写哪一份 hosts，而 90DNS 要生效必须打开
    // system_settings.ini 的 enable_dns_mitm，于是又多了一层联动。
    //
    // 联动是最容易写成「看着对、其实单向」的那种代码：勾了 90DNS 打开 mitm 容易想到，
    // 但「用户手动取消 mitm 时把 90DNS 一起取消」很容易漏 —— 漏了的后果是界面显示「已启用
    // 90DNS」而产物里 mitm 关着，遥测根本没被挡住。所以两个方向都要断言。
    private static void Check90DnsSwitches()
    {
        Section("90DNS 两个开关：各写各的 hosts，且与 enable_dns_mitm 完全双向绑定（mitm ⇔ 任一 90DNS）");

        // ① 四种组合 → out/atmosphere/hosts 里该有哪几份文件。
        //    两个引导项都开着，让「哪个 90DNS 开关在起作用」成为唯一变量。
        var cases = new (bool Sysmmc, bool Emummc, string[] Expected)[]
        {
            (false, false, []),
            (true, false, ["default.txt", "sysmmc.txt"]),
            (false, true, ["default.txt", "emummc.txt"]),
            (true, true, ["default.txt", "sysmmc.txt", "emummc.txt"]),
        };

        foreach (var (sysmmc, emummc, expected) in cases)
        {
            var options = new WizardOptions
            {
                Atmosphere = true,
                Hekate = false,
                Ultrahand = false,
                SysPatch = false,
                BootStock = true,
                BootSysNand = true,
                BootEmuNand = true,
                Use90DnsSysmmc = sysmmc,
                Use90DnsEmummc = emummc,
                IncludeComponentFilesInOutput = false,
                IncludePayloads = false,
                IncludeBootDat = false,
            };
            ApplyCatalogDefaults(options);
            Generate(options);

            var hostsDir = Path.Combine(AppPaths.OutputRoot, "atmosphere", "hosts");
            var actual = Directory.Exists(hostsDir)
                ? Directory.EnumerateFiles(hostsDir)
                    .Select(Path.GetFileName)
                    .Where(n => n is not null)
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList()
                : [];
            var want = expected.OrderBy(n => n, StringComparer.Ordinal).ToList();

            var label = $"(真实={sysmmc}, 虚拟={emummc})";
            Assert(actual.SequenceEqual(want),
                $"90DNS {label} 时 hosts 目录应正好是 [{string.Join("、", want)}]，"
                + $"实际 [{string.Join("、", actual)}]");
        }

        // ② 界面侧的联动。直接驱动 ViewModel —— 这是纯界面逻辑，走生成器测不到。
        var settingsPath = SettingsStore.FilePath;
        var backup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;

        try
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }

            var vm = new MainViewModel();
            var atmosphere = vm.Components.First(c => c.Kind == ComponentKind.Atmosphere);
            var mitm = atmosphere.FindOption(MainViewModel.DnsMitmOptionKey);

            // 键名拼错的话 FindOption 返回 null，联动**静默失效** —— 界面照旧能勾，
            // 产物里 enable_dns_mitm 却纹丝不动。所以这里必须先钉住它存在。
            Assert(mitm is not null,
                $"Atmosphere 的选项里应有 {MainViewModel.DnsMitmOptionKey}"
                + "（否则 90DNS ↔ mitm 的联动静默失效）");
            if (mitm is null)
            {
                return;
            }

            // ── 起点一致性（用户 2026-09-17 决定 B）──
            // 这一组断言在**动任何开关之前**做：settings.json 刚被删掉，所以拿到的就是目录里的默认值。
            // 它钉的不是「某个字段等于某个字面量」，而是**默认值 ⇔ 联动规则**这条自洽性：
            // 规则是「两个 90DNS 都不勾 ⇒ mitm 关」，那么刚打开界面时（两个 90DNS 都是默认的不勾）
            // 就必须是 mitm 关。默认值若还是 u8!0x1，界面一打开就是「90DNS 全关、mitm 却开着」——
            // 规则与起点互相矛盾，而产物会照默认值写出 mitm=1（遥测被挡住的假象：hosts 一份都没生成）。
            // 注意：CheckUserConfigBaseline 里那条 u8!0x0 只保证「生成器照默认值写」，
            // 不保证「界面打开时看到的就是关」—— 三条链路（对象默认值 / 生成的 ini / 界面初始勾选）
            // 必须各有一条，这里是第三条。
            Assert(!vm.Use90DnsSysmmc && !vm.Use90DnsEmummc,
                "刚打开界面时两个 90DNS 都不该勾着（它们的默认值是 false）");
            Assert(!mitm.BoolValue,
                "刚打开界面时 enable_dns_mitm 应为关 —— 必须与「两个 90DNS 都不勾 ⇒ mitm 关」的起点一致"
                + "（默认值若为开，界面与规则自相矛盾，产物里 mitm 还会莫名其妙地开着）");

            vm.BootSysNand = true;
            vm.BootEmuNand = true;

            // 起点：都不勾 → mitm 关
            mitm.BoolValue = false;
            vm.Use90DnsSysmmc = false;
            vm.Use90DnsEmummc = false;
            Assert(!mitm.BoolValue, "两个 90DNS 都不勾时 enable_dns_mitm 应为关");

            // 正向：勾任一个 → 自动打开 mitm
            vm.Use90DnsSysmmc = true;
            Assert(mitm.BoolValue, "勾上「真实破解系统 90DNS」后 enable_dns_mitm 应被自动打开");
            vm.Use90DnsSysmmc = false;
            vm.Use90DnsEmummc = true;
            Assert(mitm.BoolValue, "勾上「虚拟破解系统 90DNS」后 enable_dns_mitm 应被自动打开");

            // 正向：都取消 → 自动关掉 mitm
            vm.Use90DnsEmummc = false;
            Assert(!mitm.BoolValue, "两个 90DNS 都取消后 enable_dns_mitm 应自动关掉");

            // 反向：手动取消 mitm → 两个 90DNS 一起取消
            vm.Use90DnsSysmmc = true;
            vm.Use90DnsEmummc = true;
            Assert(mitm.BoolValue, "两个 90DNS 都勾上后 enable_dns_mitm 应为开");
            mitm.BoolValue = false;
            Assert(!vm.Use90DnsSysmmc && !vm.Use90DnsEmummc,
                "手动取消 enable_dns_mitm 时，两个 90DNS 开关应**同时**被取消"
                + "（否则界面显示已启用、产物里 mitm 关着，遥测根本没挡住）");

            // 规则④（2026-09-17 第二版，用户选定「勾 mitm 时自动补勾 90DNS」）：
            // 加上这条，mitm ⇔ (真实90DNS ∨ 虚拟90DNS) 才是**双向**的。
            // 只靠①②③时，用户仍能手动造出「mitm 开着、两个 90DNS 都不勾」这个状态 ——
            // 产物里 enable_dns_mitm=1 而 hosts 一份都没有，用户会以为遥测已经被挡住。
            mitm.BoolValue = true;
            Assert(vm.Use90DnsSysmmc && vm.Use90DnsEmummc,
                "两个引导模式都开着时，勾上 enable_dns_mitm 应把两个 90DNS 开关一起补勾上");

            // ④ 的可见性收窄：只开「真实破解系统」时，不该顺手勾上「虚拟破解系统 90DNS」——
            // 那个开关此刻在界面上是隐藏的（跟 ShowEmummcDns 走），勾上只会让用户在别处
            // 看到一个自己没点过的勾，产物里也不会多出 emummc.txt（生成端同样要求对应引导模式）。
            vm.BootEmuNand = false;
            mitm.BoolValue = false;
            Assert(!vm.Use90DnsSysmmc && !vm.Use90DnsEmummc,
                "复位：取消 mitm 后两个 90DNS 应都为不勾");
            mitm.BoolValue = true;
            Assert(vm.Use90DnsSysmmc && !vm.Use90DnsEmummc,
                "只勾了「真实破解系统」引导模式时，勾 mitm 只该补勾「真实破解系统 90DNS」，"
                + "不该去勾那个当前不可见的「虚拟破解系统 90DNS」");

            // 不变式：任何时刻都不该出现「mitm 开着、两个 90DNS 都不勾」
            vm.BootEmuNand = true;
            Assert(!mitm.BoolValue || vm.Use90DnsSysmmc || vm.Use90DnsEmummc,
                "不变式：enable_dns_mitm 开着时，至少有一个 90DNS 开关勾着"
                + "（否则产物里 mitm=1 却没有任何 hosts 文件，遥测根本没挡住）");

            // 载入时也要成立（规则③的载入分支）：老版本 settings.json 里 enable_dns_mitm 的
            // 默认值是 "1"，而两个 90DNS 开关那时还不存在。升级上来若不修正，界面一打开就是
            // 「mitm 开着、两个 90DNS 都不勾」，产物写出 enable_dns_mitm=1 却一份 hosts 都没有。
            // 这里**不**反向补勾 90DNS —— 载入不是用户动作，替他写出一份没要过的 hosts 更越权。
            SettingsStore.Save(new AppSettings
            {
                Language = "zh-Hans",
                OptionValues = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Atmosphere/" + MainViewModel.DnsMitmOptionKey] = "1",
                },
            });

            var vmStale = new MainViewModel();
            var staleMitm = vmStale.Components
                .First(c => c.Kind == ComponentKind.Atmosphere)
                .FindOption(MainViewModel.DnsMitmOptionKey);
            Assert(staleMitm is not null && !staleMitm.BoolValue,
                "存档里 mitm=1 而两个 90DNS 都不勾时，载入后应被拉回「关」"
                + "（否则界面显示 mitm 开着、产物里 hosts 一份都没有）");

            // 修正还得**落盘** —— 否则 settings.json 会一直留着 mitm=1，界面（已修正）与文件（旧值）
            // 长期不一致，用户去翻文件会以为根本没生效。
            // ⚠️ 这条不能靠「SelectedLanguage 的 setter 会顺手 Save」：那个 setter 只在语言**真的变了**
            //    时才落盘（SetProperty 返回 false 就不保存），语言没变时它什么也不写。
            SettingsStore.Load().OptionValues.TryGetValue(
                "Atmosphere/" + MainViewModel.DnsMitmOptionKey, out var savedMitm);
            Assert(savedMitm == "0",
                "载入时对 mitm 的修正必须回写 settings.json（否则文件里一直留着旧值，与界面不一致）"
                + $"，实际读回的是 {savedMitm ?? "（缺这个键）"}");
        }
        finally
        {
            if (backup is not null)
            {
                File.WriteAllText(settingsPath, backup);
            }
            else if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }

            if (_fullOptions is not null)
            {
                Generate(_fullOptions);
            }
        }
    }

    // ── override_config.ini 的 [hbl_config] 五个键变成可配置项 ──
    //
    // 这五个键原先在生成器里**硬编码**（Atmosphere 官方模板的固定内容），界面上没有对应选项。
    // 改成可配置时最容易犯的错是「存储键」与「ini 键」对不上：两段都有 override_key，
    // 存储键因此必须加前缀（hbl_*），而写进文件时又必须用真名 —— 中间任何一处错位，
    // 产物里就会多一个 Atmosphere 不认识的键、少一个它要的键，而界面上一片祥和。
    //
    // 所以这里的清单**从写入点扫出来**，不手抄；再配一次行为验证（改成非默认值看它真的落到文件里），
    // 因为只比默认值是恒真的：写入器写死同一个字面量时照样通过。
    private static void CheckHblConfigOptionsAreWired()
    {
        Section("override_config.ini 的 [hbl_config]：五个键都可配置，且落在对的段里");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到源码目录，无法核对 [hbl_config] 的写入点");
            return;
        }

        var generatorSource = File.ReadAllText(Path.Combine(sourceRoot, "Services", "ConfigGenerator.cs"));

        // ① 从写入点扫出 ini 键 ↔ 存储键的对应关系（真源是写入点本身，不手抄）。
        //    中间允许夹一层规范化函数（`NormalizeTitleId(Option(...))` 这种），
        //    所以用 `[^)]*?` 跨过它，而不是要求 Option( 紧跟其后。
        var callPattern = new Regex(
            "Set\\(\"hbl_config\",\\s*\"(?<ini>[^\"]+)\"[^)]*?Option\\(options,\\s*definition,\\s*\"(?<key>[^\"]+)\"\\)",
            RegexOptions.Compiled);
        var wired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in callPattern.Matches(generatorSource))
        {
            wired[match.Groups["ini"].Value] = match.Groups["key"].Value;
        }

        Assert(wired.Count > 0,
            "前置条件：应从 ConfigGenerator 里扫出 [hbl_config] 的 Option(...) 写入点，否则下面的比对恒真");

        var declared = ComponentCatalog.Get(ComponentKind.Atmosphere).Options
            .Where(o => string.Equals(o.Section, "hbl_config", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert(declared.Count > 0,
            "前置条件：[hbl_config] 段应有选项声明，否则下面的比对恒真");

        var declaredKeys = declared.Select(o => o.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unwritten = declaredKeys.Where(k => !wired.ContainsValue(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert(unwritten.Count == 0,
            "声明在 [hbl_config] 的选项都必须真的被写出来（声明了却没写 = 界面上能改、产物里没有）"
            + (unwritten.Count == 0 ? "" : "；实际没写的：" + string.Join("、", unwritten)));

        var undeclared = wired.Values.Where(k => !declaredKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert(undeclared.Count == 0,
            "写进 [hbl_config] 的每个存储键都必须在目录里有声明（没声明就取不到值，只会静默写成默认值）"
            + (undeclared.Count == 0 ? "" : "；实际没声明的：" + string.Join("、", undeclared)));

        // ② 落点与段：TargetFile / Section 是可空字段，漏填的默认值正是「本就不写」，
        //    于是漏填永远不会报错 —— 只能靠穷举断言钉住。
        foreach (var option in declared)
        {
            Assert(option.TargetFile == "atmosphere/config/override_config.ini",
                $"{option.Key} 的 TargetFile 应指向 atmosphere/config/override_config.ini，"
                + $"实际 {(string.IsNullOrEmpty(option.TargetFile) ? "未声明" : option.TargetFile)}");
        }

        // ③ 行为验证：把每个选项改成**非默认值**，断言它真的出现在 [hbl_config] 里。
        //    文本选项没法从 Choices 推探针值，所以给一张表；表里没有就**报错**而不是跳过 ——
        //    静默跳过等于这条用例对新增选项视而不见。
        var textProbes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hbl_program_id"] = "0100000000001000",
            ["hbl_path"] = "atmosphere/hbl-probe.nsp",
        };

        var iniKeyByKey = wired.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

        var probe = new WizardOptions
        {
            Atmosphere = true,
            Hekate = false,
            Ultrahand = false,
            SysPatch = false,
            IncludeComponentFilesInOutput = false,
            IncludePayloads = false,
            IncludeBootDat = false,
        };
        ApplyCatalogDefaults(probe);

        foreach (var option in declared)
        {
            string value;
            if (option.Choices is { Count: > 0 })
            {
                var choice = option.Choices.FirstOrDefault(
                    c => !string.Equals(c.Value, option.DefaultValue, StringComparison.Ordinal));
                Assert(choice is not null,
                    $"{option.Key} 的候选项里有「与默认值不同」的项（没有就无法验证它是否真的被写入）");
                value = choice?.Value ?? option.DefaultValue;
            }
            else
            {
                Assert(textProbes.ContainsKey(option.Key),
                    $"文本选项 {option.Key} 在 textProbes 里有探针值（没有就补一个，否则这条用例对它会静默跳过）");
                value = textProbes.TryGetValue(option.Key, out var probeValue) ? probeValue : option.DefaultValue;
            }

            probe.Set(ComponentKind.Atmosphere, option.Key, value);
        }

        Generate(probe);

        var hblSection = ReadSection(Read("atmosphere/config/override_config.ini"), "hbl_config");
        foreach (var option in declared)
        {
            if (!iniKeyByKey.TryGetValue(option.Key, out var iniKey))
            {
                Assert(false, $"{option.Key} 在 [hbl_config] 的写入点里有对应的 ini 键");
                continue;
            }

            var expected = probe.Get(ComponentKind.Atmosphere, option.Key, option.DefaultValue);
            Assert(hblSection.Contains($"{iniKey}={expected}"),
                $"[hbl_config] 里应写出 {iniKey}={expected}（证明 {option.Key} 的值真的流到了 ini）"
                + $"；该段实际内容：{Describe(hblSection)}");
        }

        // ④ [default_config] 的同名键不能被这次改动带偏：它仍是自己的那两个值
        var defaultSection = ReadSection(Read("atmosphere/config/override_config.ini"), "default_config");
        Assert(defaultSection.Contains("override_key=" + probe.Get(ComponentKind.Atmosphere, "override_key", "!L")),
            "两段都有 override_key，但 [default_config] 的那个必须仍是它自己的值，不能被 hbl_ 那套串了");
        Assert(hblSection.Contains("override_key=" + probe.Get(ComponentKind.Atmosphere, "hbl_override_key", "!R")),
            "[hbl_config] 的 override_key 必须用它自己的存储键（hbl_override_key），不能读到 [default_config] 的那个");

        if (_fullOptions is not null)
        {
            Generate(_fullOptions);
        }
    }

    // ── [hbl_config] 自由文本项的非法输入回落 ──────────────────────

    /// <summary>
    /// `[hbl_config]` 里两个自由文本项（`program_id` / `path`）的**非法输入**路径。
    ///
    /// `CheckHblConfigOptionsAreWired` 只覆盖了「合法值能流到 ini」；而这两个 normalizer 的
    /// **回落 + 告警**分支此前没有任何用例 —— 探针值填的都是合法值。这正是 §1 教训 8 那一族：
    /// 分支从未被走到，于是「校验被改坏（比如条件恒真）」不会有任何断言变红，
    /// 而后果是 Homebrew Menu 在真机上永远起不来（文件看着完全正常，界面上也没有任何提示）。
    /// </summary>
    private static void CheckHblConfigFallbacks()
    {
        Section("override_config.ini 的 [hbl_config]：非法输入应回落官方默认值并告警");

        var sink = new CapturingSink();
        var options = new WizardOptions
        {
            Atmosphere = true,
            Hekate = false,
            Ultrahand = false,
            SysPatch = false,
            IncludeComponentFilesInOutput = false,
            IncludePayloads = false,
            IncludeBootDat = false,
        };
        ApplyCatalogDefaults(options);
        options.Set(ComponentKind.Atmosphere, "hbl_program_id", "NOT-A-TITLE-ID");
        options.Set(ComponentKind.Atmosphere, "hbl_path", "../../outside.nsp");

        new ConfigGenerator(sink).Generate(options, CancellationToken.None);

        var hbl = ReadSection(Read("atmosphere/config/override_config.ini"), "hbl_config");

        Assert(!hbl.Contains("NOT-A-TITLE-ID", StringComparison.Ordinal),
            $"非法的 program_id 不该被原样写进 [hbl_config]（Homebrew Menu 会永远起不来）；该段实际内容：{Describe(hbl)}");
        Assert(hbl.Contains("program_id=" + ComponentCatalog.HblProgramIdDefault, StringComparison.Ordinal),
            $"非法的 program_id 应回落到官方默认值 {ComponentCatalog.HblProgramIdDefault}；该段实际内容：{Describe(hbl)}");
        Assert(sink.Warnings.Any(m => m.Contains("program_id", StringComparison.Ordinal)),
            "program_id 被回落到默认值时应留下告警（静默回落 = 用户以为配好了、实际没生效）");

        Assert(!hbl.Contains("outside.nsp", StringComparison.Ordinal),
            $"越界的 path 不该被原样写进 [hbl_config]（Atmosphere 会拒绝加载）；该段实际内容：{Describe(hbl)}");
        Assert(hbl.Contains("path=" + ComponentCatalog.HblPathDefault, StringComparison.Ordinal),
            $"非法的 path 应回落到官方默认值 {ComponentCatalog.HblPathDefault}；该段实际内容：{Describe(hbl)}");
        Assert(sink.Warnings.Any(m => m.Contains("hbl_config 的 path", StringComparison.Ordinal)),
            "path 被回落到默认值时应留下告警");

        // 反向：合法输入**不该**被回落。少了这条，normalizer 被写成「恒回落」也照样全绿。
        var okSink = new CapturingSink();
        var okOptions = new WizardOptions
        {
            Atmosphere = true,
            Hekate = false,
            Ultrahand = false,
            SysPatch = false,
            IncludeComponentFilesInOutput = false,
            IncludePayloads = false,
            IncludeBootDat = false,
        };
        ApplyCatalogDefaults(okOptions);
        okOptions.Set(ComponentKind.Atmosphere, "hbl_program_id", "0100000000001000");
        okOptions.Set(ComponentKind.Atmosphere, "hbl_path", "atmosphere/hbl-ok.nsp");

        new ConfigGenerator(okSink).Generate(okOptions, CancellationToken.None);

        var okHbl = ReadSection(Read("atmosphere/config/override_config.ini"), "hbl_config");
        Assert(okHbl.Contains("program_id=0100000000001000", StringComparison.Ordinal),
            $"合法的 program_id 应原样写进去（不能被回落吃掉）；该段实际内容：{Describe(okHbl)}");
        Assert(okHbl.Contains("path=atmosphere/hbl-ok.nsp", StringComparison.Ordinal),
            $"合法的 path 应原样写进去；该段实际内容：{Describe(okHbl)}");
        // ⚠️ 作用域必须收窄到「hbl 相关」：生成器在别的理由上**本来就会**告警
        //    （例如「未勾选 Hekate，引导项不会被写入」），断言「全局无告警」会因无关原因变红。
        //    断言要盯的是**被测对象**，不是整个世界的状态。
        var hblWarnings = okSink.Warnings.Where(m => m.Contains("hbl_config", StringComparison.Ordinal)).ToList();
        Assert(hblWarnings.Count == 0,
            $"合法的 program_id/path 不该被回落，也就不该有 hbl_config 的告警；实际：{string.Join("、", hblWarnings)}");

        if (_fullOptions is not null)
        {
            Generate(_fullOptions);
        }
    }

    // ── 首次运行的组件勾选：界面不勾 / 命令行自动全选 ──────────────
    //
    // 2026-09-16 需求：首次运行界面默认**不勾任何组件**（让用户自己挑），
    // 但「不勾组件」不等于「选项没有默认值」—— 各组件内部的选项仍要按目录默认值填好
    // （那部分由 CheckFreshUiState 第 ⑥ 段逐项守着）。
    //
    // 这条改动同时踩到两个地方，两边都得钉住：
    //   ① 界面路径（MainViewModel 构造函数）不勾 —— 有 CheckFreshUiState 的行为护栏；
    //   ② 命令行 --run 必须自动全选，否则 RunAsync 会撞上「一个组件都没勾」的保护分支，
    //      提示一句就返回，端到端验证**空跑却报退出码 0** —— 最容易被当成「通过了」。
    //
    // ② 没法直接用行为测试覆盖（App.RunHeadless 是 private，还要真开窗口），
    // 所以退一步用两条间接护栏把它钉住：
    //   · 保护分支：全新状态下 BuildWizardOptions().HasAnyComponentSelected 必须为 false，
    //     证明 RunAsync 里那条分支**真的会被走到**，而不是形同虚设；
    //   · 源码契约：App.xaml.cs 的 RunHeadless 里，EnsureComponentsSelected() 必须排在
    //     RunOnceAsync() 之前 —— 防的是「有人觉得这行多余顺手删掉」，删了没有任何行为测试会红。
    private static void CheckFirstRunSelectionContract()
    {
        Section("首次运行：界面不勾组件 / 命令行自动全选");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            // ── 保护分支：不勾任何组件时，RunAsync 应判定为「没有选择」──
            var vm = new MainViewModel();
            Assert(!vm.BuildWizardOptions().HasAnyComponentSelected,
                "全新状态下应判定为「一个组件都没勾」（RunAsync 据此提示并返回，不会空跑）");
            Assert(vm.ShowNoSelectionHint, "未勾任何组件时应显示「请至少勾选一个组件」的提示");

            // 反向：勾上任意一个组件后判据必须翻过来，证明上面那两条不是恒真
            vm.Components.First(c => c.Kind == ComponentKind.Hekate).IsSelected = true;
            Assert(vm.BuildWizardOptions().HasAnyComponentSelected,
                "勾上任意一个组件后「有选择」判据应立刻变为 true");
            Assert(!vm.ShowNoSelectionHint, "勾上任意一个组件后「请至少勾选一个组件」的提示应立刻消失");

            // ── 源码契约：命令行路径必须自动全选，且排在 RunOnceAsync 之前 ──
            var sourceRoot = FindSourceRoot();
            if (sourceRoot is null)
            {
                Assert(false, "找不到源码目录，无法核对 App.xaml.cs 的命令行契约");
                return;
            }

            var appPath = Path.Combine(sourceRoot, "App.xaml.cs");
            Assert(File.Exists(appPath), $"应能找到 App.xaml.cs（{appPath}）");

            // 只看代码行、跳过注释 —— 注释里同样会出现这两个方法名，不剔掉会把注释误判成调用
            var code = File.ReadAllLines(appPath)
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
                .ToList();

            var start = code.FindIndex(l => l.Contains("void RunHeadless(", StringComparison.Ordinal));
            var end = code.FindIndex(l => l.Contains("BuildRunReport(", StringComparison.Ordinal));
            Assert(start >= 0, "App.xaml.cs 里应有 RunHeadless 方法");
            Assert(end > start, "RunHeadless 之后应有 BuildRunReport（用来界定方法体范围）");

            var body = code.GetRange(start, end - start);
            var ensureIndex = body.FindIndex(
                l => l.Contains("viewModel.EnsureComponentsSelected()", StringComparison.Ordinal));
            var runIndex = body.FindIndex(
                l => l.Contains("viewModel.RunOnceAsync()", StringComparison.Ordinal));

            Assert(ensureIndex >= 0,
                "命令行 --run 必须调用 EnsureComponentsSelected()：没有界面可以勾，不自动选就一步都跑不动");
            Assert(runIndex >= 0, "命令行 --run 应调用 RunOnceAsync()");
            Assert(ensureIndex < runIndex,
                "EnsureComponentsSelected() 必须排在 RunOnceAsync() 之前 —— RunAsync 一开始就读勾选状态，晚了等于没勾");

            // ── 反向源码契约：调用点**只能**在这一处 ──
            // 上面几条只保证「命令行会调」。反方向（界面路径**不得**调）以前是靠 CheckFreshUiState 里一条
            // 恒真的日志断言假装守着的（见该处的注释），现在直接钉住调用点本身。
            // 界面路径调用它 = 软件一打开就全勾上，而用户明确要的是相反（打开软件全不勾）。
            // 只看以 `;` 结尾的行：定义行（`public bool EnsureComponentsSelected()`）不以分号结尾，会被跳过；
            // 注释行也一并剔除（注释里提到方法名不算调用）。
            // ⚠️ 必须查两个方向：只查「别的文件有没有」是不够的 —— **OnStartup（界面路径）也在 App.xaml.cs 里**。
            var appCallLines = code
                .Select((line, index) => (Line: line, Index: index))
                .Where(item => item.Line.Contains("EnsureComponentsSelected()", StringComparison.Ordinal)
                            && item.Line.EndsWith(";", StringComparison.Ordinal))
                .Select(item => item.Index)
                .ToList();

            Assert(appCallLines.Count == 1 && appCallLines[0] == start + ensureIndex,
                "App.xaml.cs 里 EnsureComponentsSelected() 只应有一处调用，且必须落在 RunHeadless 里"
                + $"（实际 {appCallLines.Count} 处）—— 在界面路径 OnStartup 里调用它会让软件一打开就全勾上");

            var otherCallSites = Directory
                .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(file =>
                {
                    var relative = Path.GetRelativePath(sourceRoot, file);
                    return !relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                })
                .SelectMany(file => File.ReadAllLines(file)
                    .Select(line => line.TrimStart())
                    .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
                    .Where(line => line.Contains("EnsureComponentsSelected()", StringComparison.Ordinal)
                                && line.EndsWith(";", StringComparison.Ordinal))
                    .Select(line => $"{Path.GetFileName(file)}: {line.Trim()}"))
                .Where(site => !site.StartsWith("App.xaml.cs:", StringComparison.Ordinal))
                .ToList();

            Assert(otherCallSites.Count == 0,
                "EnsureComponentsSelected() 只允许被命令行路径调用（App.xaml.cs）；界面路径调用它会让软件一打开就全勾上："
                + string.Join("；", otherCallSites));
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    /// <summary>
    /// ComponentKind 枚举与组件目录必须一一对应。
    /// 缺口形态：往枚举里加了第 5 个组件、却忘了在 <c>ComponentCatalog.Definitions</c> 里加定义 ——
    /// 该组件在界面上**根本不会出现**（界面是按 <c>Catalog.All</c> 建的），勾选状态也存不下来，
    /// 而且没有任何用例会红。反向则靠「目录项数 == 枚举项数」挡住重复声明。
    /// </summary>
    private static void CheckComponentKindCoverage()
    {
        Section("组件枚举 ↔ 目录定义必须一一对应");

        var kinds = Enum.GetValues<ComponentKind>();

        // 前置条件：枚举为空时下面的集合比对会恒真
        Assert(kinds.Length > 0, "前置条件：ComponentKind 枚举不应为空");

        var defined = ComponentCatalog.All.Select(definition => definition.Kind).ToList();

        var missing = kinds.Where(kind => !defined.Contains(kind)).ToList();
        Assert(missing.Count == 0,
            "每个 ComponentKind 都应有目录定义（否则该组件在界面上根本不会出现）：缺 "
            + string.Join("、", missing));

        // 反向：项数相等 + 无缺失 ⇒ 是双射。多出来的那种情况是「同一个 kind 声明了两次」，
        // 此时 Dictionary 会静默只留一个，界面与预期不符。
        Assert(defined.Count == kinds.Length,
            $"组件目录的项数（{defined.Count}）应等于 ComponentKind 的项数（{kinds.Length}）");
    }

    /// <summary>
    /// 分类分组完整性（2026-09-18 加「分类文件夹」时补的护栏）。
    ///
    /// 分组把「一张平铺列表」变成「若干组」，于是多出几类**只会在界面上现形、编译器和行为测试都够不着**
    /// 的错法：组标题的键写错（界面上直接显示 <c>Category.Framework</c>）、某个组件在分组时被
    /// <c>Where</c> 筛掉（卡片凭空消失）、两个分类标题撞名（用户分不清哪组是哪组）。
    /// 三条都在这里钉死。
    ///
    /// ⚠️ 分类清单**从枚举取**，不手写：手抄的清单新增分类时必然腐烂，而腐烂的表现正是「少一组」
    /// 这种没人会注意到的事。
    /// </summary>
    private static void CheckComponentCategoryCoverage()
    {
        Section("分类分组：每个组件都归了组、每个组都有三语文案、界面分组不丢组件");

        var categories = ComponentCatalog.Categories;

        // 前置条件：分类为空时下面的循环一条都不跑，整段变成空转
        Assert(categories.Count > 0, "前置条件：ComponentCategory 不应为空，否则本用例空转");
        Assert(categories.Distinct().Count() == categories.Count, "分类枚举不应有重复成员");

        // ① 每个分类在三份语言包里都要有文案。
        //    缺键时 LocalizationService 的索引器会**把键名原样返回**，界面上就是一行
        //    "Category.UltrahandPlugin" —— 编译、启动、回归全绿，只有用户看得见。
        var sourceRoot = FindSourceRoot();
        // 陈述句：断言成立时打印的是「找到源码目录了」，不成立时才是「找不到…」
        Assert(sourceRoot is not null, "应能找到 src/SwitchCfwWizard 源码目录，用来核对分类文案");
        if (sourceRoot is not null)
        {
            foreach (var category in categories)
            {
                var key = ComponentCatalog.CategoryTitleKey(category);
                foreach (var code in new[] { "zh-Hans", "zh-Hant", "en-US" })
                {
                    var text = ReadPackString(sourceRoot, code, key);
                    Assert(!string.IsNullOrWhiteSpace(text) && text != key,
                        $"分类「{category}」在 {code} 里应有自己的文案（键 {key}）—— "
                        + $"缺了界面上会直接显示键名；实际：{text ?? "<缺失>"}");
                }
            }
        }

        // ② 分类标题不能撞名 —— 两组叫同一个名字，用户没法按名字区分它们
        var titles = categories
            .Select(category => ComponentCatalog.CategoryTitleKey(category))
            .Select(key => LocalizationService.Instance[key])
            .ToList();
        Assert(titles.Distinct(StringComparer.Ordinal).Count() == titles.Count,
            "各分类的显示名不应重复，实际：" + string.Join("、", titles));

        // ③ 界面真的按分类渲染，且**一个组件都不少**。
        //    这是本用例的核心：分组是「Components → ComponentGroups」的一次投影，
        //    投影写错（比如 Where 的条件反了、或漏了某个分类）会让卡片在界面上凭空消失，
        //    而 Components 本身完全正常 —— 任何只看 Components 的检查都发现不了。
        var vm = new MainViewModel();
        var grouped = vm.ComponentGroups.SelectMany(g => g.Components).ToList();

        var lost = vm.Components.Where(c => !grouped.Contains(c)).Select(c => c.Kind.ToString()).ToList();
        Assert(lost.Count == 0,
            "分组后不应丢失任何组件（丢了就是卡片在界面上凭空消失）：丢 " + string.Join("、", lost));
        Assert(grouped.Count == vm.Components.Count,
            $"分组后的组件总数（{grouped.Count}）应等于组件总数（{vm.Components.Count}）");

        // ④ 组数 = 「有组员的分类」数。空组会被界面过滤掉（渲染一个空标题只会让人以为程序坏了），
        //    所以这里比的是集合而不是「分类总数」—— 后者在插件还没填满分类时会恒不相等。
        var expectedGroups = vm.Components.Select(c => c.Category).Distinct().Count();
        Assert(vm.ComponentGroups.Count == expectedGroups,
            $"界面上的组数（{vm.ComponentGroups.Count}）应等于「有组员的分类」数（{expectedGroups}）");

        // ⑤ 每个组件都必须属于某个**真的会显示出来**的组 —— 防止某个组件被分到一个不存在的分类
        //    （枚举里没有它，Categories 里自然也没有，那一组就永远不显示）。
        var shownCategories = vm.ComponentGroups.Select(g => g.Category).ToHashSet();
        var orphan = vm.Components.Where(c => !shownCategories.Contains(c.Category))
            .Select(c => $"{c.Kind}→{c.Category}").ToList();
        Assert(orphan.Count == 0,
            "每个组件都应落在界面上真的会显示的组里：孤立的 " + string.Join("、", orphan));

        // ⑥ 「离线固件」在**高级设置**里的那一行（用户 2026-09-18 要求：下载地址与下载文件名都要能改，
        //    不改就用我给的那一对）。这条钉的是**界面上的默认值**而不是声明本身 ——
        //    两者之间还隔着 AssetSlotViewModel 一层，声明对了但那层接错照样是错的。
        var firmwareGroup = vm.AssetSlotGroups
            .FirstOrDefault(g => g.Definition.Kind == ComponentKind.Firmware);
        Assert(firmwareGroup is not null,
            "「离线固件」必须在高级设置里有一行 —— 它是槽驱动组件，地址与文件名两个输入框该自动出现");

        var firmwareRow = firmwareGroup!.Slots.Single();
        Assert(firmwareRow.DefaultAddress == "THZoria/NX_Firmware",
            $"离线固件的默认地址应是 THZoria/NX_Firmware，实际 {firmwareRow.DefaultAddress}");
        Assert(firmwareRow.DefaultFileName == "Firmware.x.x.x.zip",
            $"离线固件的默认文件名应是版本占位模式 Firmware.x.x.x.zip，实际 {firmwareRow.DefaultFileName}");
        Assert(firmwareRow.PlacementHint.Contains("out/Firmware/Firmware.x.x.x/", StringComparison.Ordinal),
            "落点提示应显示**替换后**的路径（否则用户看到的是一个字面量的 out/Firmware/{asset}/）"
            + $"；实际 {firmwareRow.PlacementHint}");
    }

    /// <summary>
    /// `WizardOptions.IsSelected` 与 `HasAnyComponentSelected` 必须认到**每一个** `ComponentKind`。
    ///
    /// 与 <see cref="CheckComponentKindCoverage"/> 的分工：那条管「枚举 ↔ 组件目录」，这条管
    /// 「枚举 ↔ `WizardOptions` 的四个勾选位」。两者是**不同的**手抄清单，会各自腐烂。
    ///
    /// 为什么值得单独一条 —— 这两处都带静默兜底，而后果**完全隐形**：
    ///   · `IsSelected` 的 `_ => false` 会把新增组件判成「没勾」；
    ///   · `HasAnyComponentSelected` 是四个 bool 的 OR 链。
    /// 于是加第 5 个组件时：`ConfigGenerator` 跳过它（不产出任何文件），
    /// **`OutputValidator` 也用 `IsSelected` 过滤**，所以校验器**同样**跳过它 —— 连「逐组件对账」
    /// 都看不见（它虽然遍历 `ComponentCatalog.All`，却被 `Where(IsSelected)` 先筛掉了）。
    /// 结果是产出一份少了一个组件的 SD 卡内容，而**所有检查全绿**。若用户只勾那一个新组件，
    /// 界面还会显示「请至少勾选一个组件」而拒绝开始。
    ///
    /// 清单来源 = **枚举本身** + **反射**取同名属性，不手抄（§1 教训 3/4）。
    /// </summary>
    private static void CheckComponentKindExhaustiveness()
    {
        Section("WizardOptions：每个 ComponentKind 都必须被 IsSelected / HasAnyComponentSelected 认到");

        var kinds = Enum.GetValues<ComponentKind>();
        Assert(kinds.Length > 0, "前置条件：ComponentKind 应至少有一个成员，否则本用例恒真");

        foreach (var kind in kinds)
        {
            // 属性名必须与枚举名一致：IsSelected 就是按这个对应关系分派的，改名只改一边就脱节了
            var prop = typeof(WizardOptions).GetProperty(kind.ToString());
            Assert(prop is not null && prop.PropertyType == typeof(bool) && prop.CanWrite,
                $"WizardOptions 应有与 {kind} 同名的可写 bool 属性（IsSelected 按这个对应关系分派）");
            if (prop is null || prop.PropertyType != typeof(bool) || !prop.CanWrite)
            {
                continue;
            }

            var only = new WizardOptions();
            prop.SetValue(only, true);

            Assert(only.IsSelected(kind),
                $"只勾 {kind} 时 IsSelected({kind}) 应为 true —— `_ => false` 兜底会把新增组件静默判成「没勾」，"
                + "而生成器与校验器都用它过滤，结果是「少一个组件却全绿」");
            Assert(only.HasAnyComponentSelected,
                $"只勾 {kind} 时 HasAnyComponentSelected 应为 true —— 否则界面会显示「请至少勾选一个组件」而拒绝开始");
        }

        // 反向：一个都不勾时两者都必须是 false（防有人把兜底改成 true）
        var none = new WizardOptions();
        foreach (var kind in kinds)
        {
            Assert(!none.IsSelected(kind), $"一个组件都没勾时 IsSelected({kind}) 应为 false");
        }

        Assert(!none.HasAnyComponentSelected, "一个组件都没勾时 HasAnyComponentSelected 应为 false");
    }

    /// <summary>
    /// 总护栏：源码里引用到的每一个语言键，三份语言包都必须有文案。
    ///
    /// 起因是一个真实存在过的漏洞：<c>OutputValidator</c> 引用了
    /// <c>Check.Error.MissingConfigFile</c>，语言包里根本没有这个键 ——
    /// 而 <c>LocalizationService</c> 缺键时会**把键名本身返回**，
    /// 于是界面上赫然显示一串 <c>Check.Error.MissingConfigFile</c>。
    /// 编译、启动自检、回归测试全绿，只有用户看得见。
    ///
    /// 所以这里反过来做：从源码里把 <c>loc:Loc X</c> / <c>LocalizationService["X"]</c> /
    /// 形如 <c>"Log.Xxx"</c> 的字符串全收集起来，逐个确认能取到文案。
    /// 以后加新文案忘了补语言包，会立刻在这里报出来。
    /// </summary>
    /// <summary>
    /// 主题调色板完整性：两套表的键集必须**完全一致**，Keys 要列全，色值格式要合法。
    ///
    /// 这条钉的是暗夜模式里最容易漏、也最难发现的一类：两套表少写一个键 ⇒ 那一处颜色在切主题时
    /// 静止不动。不抛异常、不报错，只是「切过去之后有一块还是浅色」，而且用户多半会以为是显示器的事。
    /// </summary>
    private static void CheckThemePaletteIsComplete()
    {
        Section("主题调色板：两套表的键集必须一致、Keys 要列全、色值格式合法");

        var light = ThemePalette.Light;
        var dark = ThemePalette.Dark;

        // 前置条件：键太少说明调色板被误删，下面的差集断言会变成橡皮图章
        Assert(ThemePalette.Keys.Count >= 20,
            $"调色板应覆盖界面上所有颜色（实际 {ThemePalette.Keys.Count} 个，像是被误删了）");

        var lightOnly = light.Keys.Except(dark.Keys, StringComparer.Ordinal).ToList();
        Assert(lightOnly.Count == 0,
            "浅色表里的每个键暗夜表也要有（缺了 = 切到暗夜时那一块不变）"
            + (lightOnly.Count == 0 ? "" : "，只有浅色有：" + string.Join("、", lightOnly)));

        var darkOnly = dark.Keys.Except(light.Keys, StringComparer.Ordinal).ToList();
        Assert(darkOnly.Count == 0,
            "暗夜表里的每个键浅色表也要有"
            + (darkOnly.Count == 0 ? "" : "，只有暗夜有：" + string.Join("、", darkOnly)));

        // Keys 就是 Apply 遍历的那份清单：它少一个键，那个键永远不会被处理
        var notListed = light.Keys.Except(ThemePalette.Keys, StringComparer.Ordinal).ToList();
        Assert(notListed.Count == 0,
            "ThemePalette.Keys 必须列全两套表的所有键（Apply 只遍历它）"
            + (notListed.Count == 0 ? "" : "，漏了：" + string.Join("、", notListed)));

        var badColor = ThemePalette.Keys
            .Where(key => !ThemePalette.IsWellFormedColor(light[key]) || !ThemePalette.IsWellFormedColor(dark[key]))
            .ToList();
        Assert(badColor.Count == 0,
            "色值必须是 #AARRGGBB（写错了会在 Apply 里抛，而 Apply 在启动路径上）"
            + (badColor.Count == 0 ? "" : "，有问题：" + string.Join("、", badColor)));

        // 反向：For(System) 必须抛。System 是「偏好」不是「渲染结果」，静默给浅色的话，
        // 症状是「选了跟随系统却永远是浅色」，很难查。
        var threw = false;
        try
        {
            ThemePalette.For(ThemeMode.System);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        Assert(threw, "ThemePalette.For(System) 应当抛 ArgumentOutOfRangeException（System 必须先被 Resolve）");
    }

    /// <summary>
    /// Styles.xaml 里的初始色值必须与 <see cref="ThemePalette.Light"/> **逐字一致**。
    ///
    /// 两份数据是必然存在的（XAML 得有初值，否则没有 Application 的场景渲染不出来），
    /// 所以只能靠这条把它们钉在一起：不一致的表现是「窗口显示前第一次 Apply 时颜色轻微跳一下」——
    /// 一个大部分人永远看不到、看到也说不清的 bug。
    /// </summary>
    private static void CheckThemePaletteMatchesStyles()
    {
        Section("主题调色板：Styles.xaml 的初始值必须与浅色表逐字一致");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到 src/SwitchCfwWizard 源码目录，无法比对 Styles.xaml");
            return;
        }

        var path = Path.Combine(sourceRoot, "Themes", "Styles.xaml");
        Assert(File.Exists(path), $"应存在 {path}");
        var xaml = File.ReadAllText(path);

        var missing = new List<string>();
        var mismatched = new List<string>();

        foreach (var key in ThemePalette.Keys)
        {
            var match = Regex.Match(xaml, $"x:Key=\"{Regex.Escape(key)}\"\\s+Color=\"(#[0-9A-Fa-f]{{8}})\"");

            if (!match.Success)
            {
                missing.Add(key);
                continue;
            }

            var inXaml = match.Groups[1].Value;
            var inPalette = ThemePalette.Light[key];

            if (!string.Equals(inXaml, inPalette, StringComparison.OrdinalIgnoreCase))
            {
                mismatched.Add($"{key}（xaml {inXaml} ≠ 表里 {inPalette}）");
            }
        }

        Assert(missing.Count == 0,
            "每个调色板键都要在 Styles.xaml 里有定义（Apply 靠它才找得到东西可换）"
            + (missing.Count == 0 ? "" : "，缺：" + string.Join("、", missing)));

        Assert(mismatched.Count == 0,
            "Styles.xaml 的初值必须与 ThemePalette.Light 逐字一致（不一致 = 启动瞬间颜色跳一下）"
            + (mismatched.Count == 0 ? "" : "，不一致：" + string.Join("；", mismatched)));
    }

    /// <summary>
    /// 源码契约：**颜色键一律不许用 StaticResource**，XAML 里也不许有内联颜色。
    ///
    /// 这是整个暗夜模式唯一的「静默失效」入口：StaticResource 在解析时把引用拷给控件，
    /// 之后资源字典里的对象被换成新的（主题应用就是这么做的），界面仍指着旧的那个 ⇒
    /// 切主题纹丝不动。谁把一行改回 StaticResource，就是一处永远不变的颜色，不报错、不崩溃。
    /// </summary>
    private static void CheckThemeColorsAreDynamic()
    {
        Section("主题源码契约：颜色键必须 DynamicResource，XAML 里不许有内联颜色");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到源码目录，无法核对主题的引用方式");
            return;
        }

        var files = new[]
        {
            Path.Combine(sourceRoot, "Themes", "Styles.xaml"),
            Path.Combine(sourceRoot, "MainWindow.xaml"),
        };

        Assert(files.All(File.Exists), "两个 XAML 都要存在，否则下面的扫描会静默变成空转");

        var staticRefs = new List<string>();
        var inlineColors = new List<string>();
        var dynamicCount = 0;

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var body = File.ReadAllText(path);

            foreach (var key in ThemePalette.Keys)
            {
                if (body.Contains("{StaticResource " + key + "}", StringComparison.Ordinal))
                {
                    staticRefs.Add($"{name}: {key}");
                }

                dynamicCount += Regex.Matches(body, Regex.Escape("{DynamicResource " + key + "}")).Count;
            }

            // 内联颜色：`Value="White"`、`Background="#FFFDF3E3"` 这类。
            // Transparent 不算 —— 它是唯一一个「不依赖主题」的合法取值。
            // ⚠️ 刻意不含 Color="..." —— 那是**定义**资源，上面那条比对负责它们。
            foreach (Match match in Regex.Matches(
                         body,
                         "(?:Value|Background|Foreground|BorderBrush|Stroke|Fill)=\"(#[0-9A-Fa-f]{6,8}|White|Black|Gray)\""))
            {
                inlineColors.Add($"{name}: {match.Value}");
            }
        }

        Assert(dynamicCount >= 40,
            $"两个 XAML 里的颜色引用应当都是 DynamicResource（只数到 {dynamicCount} 处，像是被改回 StaticResource 了）");

        Assert(staticRefs.Count == 0,
            "颜色键必须用 {DynamicResource}（用 StaticResource 的那块切主题时不会变，且不报错）"
            + (staticRefs.Count == 0 ? "" : "，违规：" + string.Join("、", staticRefs)));

        Assert(inlineColors.Count == 0,
            "XAML 里不许有内联颜色（暗夜模式下会变成一块刺眼的白）"
            + (inlineColors.Count == 0 ? "" : "，违规：" + string.Join("、", inlineColors)));
    }

    /// <summary>主题档位的编解码：存档是用户看得见、也会手改的文本，坏值只许回落、不许抛。</summary>
    private static void CheckThemeModeCodec()
    {
        Section("主题档位编解码：坏输入只回落、不抛");

        Assert(ThemeModeCodec.Parse("dark") == ThemeMode.Dark, "dark → Dark");
        Assert(ThemeModeCodec.Parse("DARK") == ThemeMode.Dark, "大小写不敏感");
        Assert(ThemeModeCodec.Parse("  Light ") == ThemeMode.Light, "首尾空白要吃掉");
        Assert(ThemeModeCodec.Parse("system") == ThemeMode.System, "system → System");

        foreach (var bad in new string?[] { null, "", "   ", "darkk", "zh-Hans", "0", "true", "浅色" })
        {
            Assert(ThemeModeCodec.Parse(bad) == ThemeModeCodec.Default,
                $"认不出的档位（{bad ?? "null"}）要回落默认而不是抛");
        }

        foreach (var mode in ThemeModeCodec.All)
        {
            Assert(ThemeModeCodec.Parse(ThemeModeCodec.ToStorage(mode)) == mode,
                $"{mode} 的存档往返应当无损");
        }

        Assert(ThemeModeCodec.ToStorage(ThemeMode.Dark) == "dark"
               && ThemeModeCodec.ToStorage(ThemeMode.Light) == "light"
               && ThemeModeCodec.ToStorage(ThemeMode.System) == "system",
            "存档里写的必须是小写字面量 dark/light/system（不跟枚举名绑：改枚举名不该动到用户手里的存档）");

        // 用户 2026-09-20 明确要求「默认主题跟随系统」—— 钉住它，防以后被顺手改回浅色。
        Assert(ThemeModeCodec.Default == ThemeMode.System,
            "默认档位必须是「跟随系统」（用户明确要求；老存档里没有这个字段时就走它）");

        var nameKeys = ThemeModeCodec.All.Select(ThemeModeCodec.NameKey).ToList();
        Assert(nameKeys.Distinct(StringComparer.Ordinal).Count() == nameKeys.Count,
            "三档的显示名键必须互不相同（否则下拉框里会出现两项同名）");
    }

    /// <summary>主题档位的显示名：三份语言包都要有（下拉框里直接显示它）。</summary>
    private static void CheckThemeNamesAreLocalized()
    {
        Section("主题档位名：三份语言包都要有");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到源码目录，无法核对主题的档位名");
            return;
        }

        var missing = new List<string>();

        foreach (var mode in ThemeModeCodec.All)
        {
            var key = ThemeModeCodec.NameKey(mode);

            foreach (var code in new[] { "zh-Hans", "zh-Hant", "en-US" })
            {
                var value = ReadPackString(sourceRoot, code, key);

                if (string.IsNullOrWhiteSpace(value) || value == key)
                {
                    missing.Add($"{code}/{key}");
                }
            }
        }

        Assert(missing.Count == 0,
            "主题下拉框的三档名称三份包都要有（缺了会在下拉框里直接显示键名）"
            + (missing.Count == 0 ? "" : "，缺：" + string.Join("、", missing)));
    }

    /// <summary>
    /// <see cref="ThemeService.Apply"/> 的行为：注入一个假资源字典，断言它真的把**每个**键
    /// 换成了目标主题的颜色。
    ///
    /// 不注入的话，回归（控制台、没有 Application.Current）里 Apply 会直接返回，这条断言恒真。
    /// ⚠️ 这里只验「资源侧换对了」；「界面侧跟不跟」由 `--selftest` 的端到端检查负责
    /// （那条读的是真实控件的颜色）。
    /// </summary>
    private static void CheckThemeApply()
    {
        Section("主题应用：Apply 要真的把资源换成目标主题的颜色");

        var savedHost = ThemeService.Host;
        var savedProbe = ThemeService.SystemPrefersDark;

        try
        {
            System.Windows.Media.Color Parse(string hex)
                => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;

            var host = new System.Windows.ResourceDictionary();
            foreach (var key in ThemePalette.Keys)
            {
                host[key] = new System.Windows.Media.SolidColorBrush(Parse(ThemePalette.Light[key]));
            }

            ThemeService.Host = host;

            // ① 换成暗夜：每个键都要变成暗夜表的取值
            ThemeService.Apply(ThemeMode.Dark);

            Assert(ThemeService.Applied == ThemeMode.Dark, "Apply(Dark) 之后 Applied 应是 Dark");
            Assert(ThemeService.ResolvedKeysOnLastApply == ThemePalette.Keys.Count,
                $"每个键都要被处理（实际 {ThemeService.ResolvedKeysOnLastApply}/{ThemePalette.Keys.Count}）");
            Assert(ThemeService.MissingKeysOnLastApply.Count == 0, "假字典里的键是全的，不该报缺失");

            var wrong = ThemePalette.Keys
                .Where(key => host[key] is not System.Windows.Media.SolidColorBrush brush
                              || brush.Color != Parse(ThemePalette.Dark[key]))
                .ToList();

            Assert(wrong.Count == 0,
                "暗夜主题下每个键的颜色都要是暗夜表的取值"
                + (wrong.Count == 0 ? "" : "，没换过来的：" + string.Join("、", wrong)));

            // ② 幂等：同一个主题再 Apply 一次不该换实例（Apply 会被反复调用）
            var before = ThemePalette.Keys.ToDictionary(key => key, key => host[key], StringComparer.Ordinal);
            ThemeService.Apply(ThemeMode.Dark);

            var replaced = ThemePalette.Keys.Where(key => !ReferenceEquals(before[key], host[key])).ToList();
            Assert(replaced.Count == 0,
                "同一个主题重复 Apply 不该换实例（每次换会平白造一堆 Brush）"
                + (replaced.Count == 0 ? "" : "，被换掉的：" + string.Join("、", replaced)));

            // ③ 切回浅色也要全部换回来
            ThemeService.Apply(ThemeMode.Light);

            var backWrong = ThemePalette.Keys
                .Where(key => host[key] is not System.Windows.Media.SolidColorBrush brush
                              || brush.Color != Parse(ThemePalette.Light[key]))
                .ToList();

            Assert(backWrong.Count == 0,
                "从暗夜切回浅色也要全部换对"
                + (backWrong.Count == 0 ? "" : "，没换回来的：" + string.Join("、", backWrong)));

            // ④ 跟随系统：由探针决定，且 Applied 只会是 Light / Dark
            ThemeService.SystemPrefersDark = () => true;
            ThemeService.Apply(ThemeMode.System);
            Assert(ThemeService.Applied == ThemeMode.Dark, "系统偏好深色时，「跟随系统」应落到 Dark");

            ThemeService.SystemPrefersDark = () => false;
            ThemeService.Apply(ThemeMode.System);
            Assert(ThemeService.Applied == ThemeMode.Light, "系统偏好浅色时，「跟随系统」应落到 Light");
            Assert(ThemeService.Preference == ThemeMode.System,
                "Preference 仍要如实记着用户选的是「跟随系统」（会话头日志靠它区分这两件事）");

            // ⑤ 缺键要被**记下来**，而不是静默跳过 —— 这正是「Styles.xaml 漏定义一个键」的信号
            ThemeService.Host = new System.Windows.ResourceDictionary();
            ThemeService.Apply(ThemeMode.Dark);

            Assert(ThemeService.MissingKeysOnLastApply.Count == ThemePalette.Keys.Count,
                $"空字典下每个键都应被记成缺失（实际记了 {ThemeService.MissingKeysOnLastApply.Count}/{ThemePalette.Keys.Count}）");
        }
        finally
        {
            ThemeService.Host = savedHost;
            ThemeService.SystemPrefersDark = savedProbe;
            ThemeService.Apply(ThemeModeCodec.Default);
        }
    }

    /// <summary>主题要能过一遍 settings.json 再读回来（用户改了档位，下次打开还在）。</summary>
    private static void CheckThemeSettingPersistence()
    {
        Section("主题：写进 settings.json → 重新打开读回来");

        var path = SettingsStore.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            var vm = new MainViewModel();
            vm.SelectedTheme = vm.Themes.First(theme => theme.Mode == ThemeMode.Dark);
            TouchAndFlush(vm);

            var saved = SettingsStore.Load();
            Assert(saved.Theme == "dark",
                $"档位应写进 settings.json 的 Theme 字段；实际「{saved.Theme ?? "(null)"}」");

            var reloaded = new MainViewModel();
            Assert(reloaded.SelectedTheme?.Mode == ThemeMode.Dark, "重开之后应读回暗夜档位");

            // 反向：确认上面的断言不是恒真（换一档要能读回另一档）
            reloaded.SelectedTheme = reloaded.Themes.First(theme => theme.Mode == ThemeMode.Light);
            TouchAndFlush(reloaded);
            Assert(new MainViewModel().SelectedTheme?.Mode == ThemeMode.Light, "改成浅色后重开应读回浅色");

            // 老存档 / 手改坏值：应回落默认（跟随系统），而不是崩、也不是变成「没选中」
            File.WriteAllText(path, "{\"Theme\":\"weird-value\"}");
            var weird = new MainViewModel();
            Assert(weird.SelectedTheme?.Mode == ThemeModeCodec.Default,
                $"认不出的档位要回落默认（{ThemeModeCodec.Default}），实际 {weird.SelectedTheme?.Mode}");
            Assert(weird.SelectedTheme is not null,
                "档位认不出来时也要选中默认那一项（否则下拉框是空的，用户看不出当前是哪一档）");
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    private static void CheckLocalizationKeyCoverage()
    {
        Section("多语言键覆盖：源码引用的键三份语言包都要有");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到 src/SwitchCfwWizard 源码目录，无法核对语言键覆盖");
            return;
        }

        var referenced = CollectReferencedKeys(sourceRoot);

        Assert(referenced.Count > 200,
            $"应能从源码里收集到大量语言键（实际只找到 {referenced.Count} 个，正则可能失效了）");

        var missing = new List<string>();
        foreach (var code in new[] { "zh-Hans", "zh-Hant", "en-US" })
        {
            LocalizationService.Instance.SetLanguage(code);
            foreach (var key in referenced)
            {
                var text = LocalizationService.Instance[key];
                if (string.IsNullOrWhiteSpace(text) || text == key)
                {
                    missing.Add($"{code}/{key}");
                }
            }
        }

        LocalizationService.Instance.SetLanguage("zh-Hans");

        Assert(missing.Count == 0,
            $"源码里引用的 {referenced.Count} 个语言键三份语言包都要有文案（缺了会把键名直接显示给用户）"
            + (missing.Count == 0 ? "" : $"，缺 {missing.Count} 个：" + string.Join("、", missing)));

        // 扫描够不着「动态拼出来」的键（`"Component." + kind + ".Name"`），单独补一条：
        // 组件的名称与说明是界面上最显眼的文案，缺了会直接显示 Component.SysPatch.Name 这种东西。
        // ⚠️ 必须遍历**枚举**而不是写死那几个组件 —— 写死的话，新增第 5 个组件时这里会静默漏掉它，
        //    而这恰恰是**唯一**覆盖这类动态键的地方（静态扫描看不到拼出来的键名）。
        var dynamicMissing = new List<string>();
        foreach (var kind in Enum.GetValues<ComponentKind>())
        {
            var definition = ComponentCatalog.All.First(d => d.Kind == kind);

            // ⚠️ 有 DisplayName 字面量的组件**只要求 Desc**，不再要求 Name —— 这是用户 2026-09-18 的约定：
            //    「作者 zdm65477730 的插件如果没有语言文件，插件名显示默认的英文名」。
            //    字面量在三份包里各抄一遍才是坏做法：抄错一份就出现「切到繁体变成另一个名字」，
            //    而正确性完全取决于「三份包有没有抄得一样」这件没人检查的事。
            //    写成一处字面量，切语言必然同名（ComponentCatalog.ResolveComponentTitle 优先取它）。
            //    Desc 仍然三份都要 —— 说明文字本来就该跟着界面语言走。
            var suffixes = string.IsNullOrWhiteSpace(definition.DisplayName)
                ? new[] { "Name", "Desc" }
                : new[] { "Desc" };

            foreach (var suffix in suffixes)
            {
                var key = $"Component.{kind}.{suffix}";
                foreach (var code in new[] { "zh-Hans", "zh-Hant", "en-US" })
                {
                    LocalizationService.Instance.SetLanguage(code);
                    var text = LocalizationService.Instance[key];
                    if (string.IsNullOrWhiteSpace(text) || text == key)
                    {
                        dynamicMissing.Add($"{code}/{key}");
                    }
                }
            }
        }

        LocalizationService.Instance.SetLanguage("zh-Hans");
        Assert(dynamicMissing.Count == 0,
            "每个组件的名称/说明（动态拼键）三份语言包都要有"
            + (dynamicMissing.Count == 0 ? "" : "，缺：" + string.Join("、", dynamicMissing)));
    }

    /// <summary>
    /// 反向护栏：语言包里**不该有没人引用的死键**。
    ///
    /// 与 <see cref="CheckLocalizationKeyCoverage"/> 正好互为反方向 —— 那条管「源码引用的键，包里有没有」，
    /// 这条管「包里的键，源码有没有引用」。缺了这条，改名 / 改设计后留下的旧键会一直躺在包里：
    /// 本身无害（没人引用就不会显示出来），但**死键是线索** —— 一个没人引用的键，往往意味着
    /// 某条检查挂在了一条根本不会发出的消息上（第八节第 42 条那个「恒真断言」就是这么查出来的）。
    ///
    /// 白名单**不手抄**：唯一允许「拼出来」的键族是 <c>Component.&lt;Kind&gt;.Name/.Desc</c>
    /// （由 <c>ComponentViewModel</c> 拼），成员从**枚举本身**生成 —— 新增第 5 个组件时它会自动放行，
    /// 不会像手抄清单那样腐烂（§1 教训 4）。
    /// </summary>
    private static void CheckNoDeadLanguageKeys()
    {
        Section("语言包死键：包里的每个键都应能在源码里找到引用点");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到 src/SwitchCfwWizard 源码目录，无法核对死键");
            return;
        }

        var keys = ReadPackKeys(Path.Combine(sourceRoot, "Localization", "Strings.zh-Hans.json"));

        // 前置条件：解析不出键时下面的差集会恒空，护栏变成橡皮图章
        Assert(keys.Count > 200,
            $"应能从语言包解析出大量键（实际只解析出 {keys.Count} 个，解析可能失效）");

        var referenced = CollectReferencedKeys(sourceRoot);
        Assert(referenced.Count > 200,
            $"应能从源码收集到大量语言键（实际只找到 {referenced.Count} 个，正则可能失效了）");

        var dynamicKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in Enum.GetValues<ComponentKind>())
        {
            dynamicKeys.Add($"Component.{kind}.Name");
            dynamicKeys.Add($"Component.{kind}.Desc");
        }

        // 分类标题也是动态拼的（`ComponentCatalog.CategoryTitleKey` 里 "Category." + category）。
        // 清单**从枚举现取**，不手抄 —— 手抄的话新增一个分类就得记得回来加一行，
        // 忘了加的表现是「这条死键检查开始报一个其实活着的键」，久而久之大家就会学会无视它。
        // 用 CategoryTitleKey 本身生成前缀，保证与生产端同一处规则。
        foreach (var category in ComponentCatalog.Categories)
        {
            dynamicKeys.Add(ComponentCatalog.CategoryTitleKey(category));
        }

        var dead = keys
            .Where(key => !referenced.Contains(key) && !dynamicKeys.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert(dead.Count == 0,
            "语言包里每个键都应能在源码里找到引用点（死键会掩盖「某条检查挂在不会发出的消息上」这类问题）"
            + (dead.Count == 0 ? "" : $"，但有 {dead.Count} 个没人引用：" + string.Join("、", dead)));
    }

    /// <summary>
    /// 扫描源码，收集所有被引用的语言键。
    /// 单独抽出来是因为「键长什么样」这条规则必须只有一处 —— 正则漂移会让两条护栏一条松一条紧。
    /// </summary>
    private static SortedSet<string> CollectReferencedKeys(string sourceRoot)
    {
        // 每个点分段都必须非空 —— 否则 "Component." + kind + ".Name" 这种**动态拼键**的
        // 前半截会被当成一个键（"Component."）误报。
        const string KeyShape = @"[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*";
        var pattern = new Regex(
            @"loc:Loc\s+(" + KeyShape + @")"
            + @"|(?:LocalizationService\.Instance|loc)\[?""(" + KeyShape + @")"""
            + @"|""((?:App|Boot|Check|Common|Component|Dialog|Global|Group|Log|Opt|Settings)\." + KeyShape + @")""",
            RegexOptions.Compiled);

        var referenced = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".xaml"))
            {
                continue;
            }

            // 跳过构建产物：obj 下的 *.g.cs 是 XAML 生成的副本，扫描它只会引入陈旧内容的误报
            var relative = Path.GetRelativePath(sourceRoot, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                for (var group = 1; group <= 3; group++)
                {
                    if (match.Groups[group].Success)
                    {
                        referenced.Add(match.Groups[group].Value);
                        break;
                    }
                }
            }
        }

        return referenced;
    }

    /// <summary>
    /// 直接读语言包 JSON 的**原始键集** —— 刻意不走 LocalizationService 索引器。
    /// 索引器在缺键时会回退到默认语言（zh-Hans），于是「zh-Hant 单独缺键」会拿到简体文案、
    /// 判据 `text == key` 不成立，检查静默通过。要比的是「这一份包里到底有没有这个键」。
    /// 与 LocalizationService.MergeJson 保持一致：跳过下划线开头的元数据节点，只取字符串值。
    /// </summary>
    /// <summary>
    /// 从**指定的一份**语言包里读一个键的**取值**（读不到返回 <c>null</c>）。
    ///
    /// 与 <see cref="LocalizationService"/> 的索引器不同：索引器带**回退**（缺键时去翻默认语言），
    /// 于是「zh-Hant 单独缺键」会被简体文案悄悄补上、看起来一切正常。要断言「三份包取值一致」
    /// 就必须读原始键值，否则断言的其实是「回退后的结果一致」，那必然恒真。
    /// </summary>
    private static string? ReadPackString(string sourceRoot, string code, string key)
    {
        var path = Path.Combine(sourceRoot, "Localization", $"Strings.{code}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.ValueKind == JsonValueKind.Object
               && document.RootElement.TryGetProperty(key, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static HashSet<string> ReadPackKeys(string path)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return keys;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith('_'))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                keys.Add(property.Name);
            }
        }

        return keys;
    }

    /// <summary>
    /// 语言包**键集**完整性。与 CheckLocalizationKeyCoverage 的分工：
    /// 那条走索引器查「能不能取到文案」（会被回退掩盖），这条查「每一份包里到底有没有这个键」。
    /// </summary>
    private static void CheckLanguagePackKeyParity()
    {
        Section("语言包键集完整性：三份包键集必须完全一致，且每份都要有源码引用的键");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "找不到 src/SwitchCfwWizard 源码目录，无法核对语言包键集");
            return;
        }

        var codes = new[] { "zh-Hans", "zh-Hant", "en-US" };
        var packs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var code in codes)
        {
            var path = Path.Combine(sourceRoot, "Localization", $"Strings.{code}.json");
            Assert(File.Exists(path), $"语言包文件应存在：Localization/Strings.{code}.json");
            if (!File.Exists(path))
            {
                return;
            }

            packs[code] = ReadPackKeys(path);
        }

        // 前置条件：解析不出键时下面的集合比对会恒真，变成橡皮图章
        var smallest = codes.Min(code => packs[code].Count);
        Assert(smallest > 200,
            $"应能从三份语言包各解析出大量键（最少的一份只有 {smallest} 个，解析可能失效）");

        // ① 两两键集必须完全一致 —— 比集合，不比个数：少一个键、或换一个键，个数都看不出来
        var differences = new List<string>();
        for (var i = 0; i < codes.Length; i++)
        {
            for (var j = i + 1; j < codes.Length; j++)
            {
                foreach (var key in packs[codes[i]].Except(packs[codes[j]]).OrderBy(k => k, StringComparer.Ordinal))
                {
                    differences.Add($"{codes[i]} 有而 {codes[j]} 无：{key}");
                }

                foreach (var key in packs[codes[j]].Except(packs[codes[i]]).OrderBy(k => k, StringComparer.Ordinal))
                {
                    differences.Add($"{codes[j]} 有而 {codes[i]} 无：{key}");
                }
            }
        }

        Assert(differences.Count == 0,
            "三份语言包的键集必须完全一致（某份缺键时索引器会静默回退到简体中文，用户看到语言串味）"
            + (differences.Count == 0 ? "" : $"，差异 {differences.Count} 处：" + string.Join("；", differences.Take(12))));

        // ② 源码引用的键，逐包**直接查原始键集**（不经回退索引器）
        var referenced = CollectReferencedKeys(sourceRoot);
        Assert(referenced.Count > 200,
            $"应能从源码里收集到大量语言键（实际只找到 {referenced.Count} 个，正则可能失效了）");

        var missing = new List<string>();
        foreach (var code in codes)
        {
            foreach (var key in referenced)
            {
                if (!packs[code].Contains(key))
                {
                    missing.Add($"{code}/{key}");
                }
            }
        }

        Assert(missing.Count == 0,
            $"源码引用的 {referenced.Count} 个键，每份语言包都必须有（索引器的回退会把缺键掩盖成默认语言文案）"
            + (missing.Count == 0 ? "" : $"，缺 {missing.Count} 个：" + string.Join("、", missing.Take(12))));
    }

    /// <summary>
    /// XAML 里每个 <c>{Binding X}</c> 的 X 都必须是真实存在的属性。
    ///
    /// 这是「新增 X 时要记得同时改 Y」的反面：界面上写错一个绑定名**不会编译报错**，
    /// 运行期 WPF 只是把它绑到空值上（输入框永远空白、按钮永远不出现），而回归测试
    /// 一条都不会红 —— 唯一的兜底是 `--selftest` 的 binding-errors 计数，那要人主动去跑、
    /// 而且报告只写进 exe 同级的 `selftest.log`。
    ///
    /// 名字清单**不手抄**：从程序集里所有公开类型的公开属性取（§1 教训 4）。
    /// 放宽到「全程序集」是刻意的 —— 绑定散落在各种 <c>DataTemplate</c> 上，DataContext 分别是
    /// 组件 / 选项 / 下载源 / 日志等不同的 VM，逐类型归属会让护栏自己变成一份易腐的清单。
    /// 代价是「恰好撞上某个无关类型的同名属性」会漏过，但这换来零误报。
    /// </summary>
    private static void CheckXamlBindingsResolve()
    {
        Section("XAML 绑定：每个 {Binding X} 都必须是真实存在的属性");

        var sourceRoot = FindSourceRoot();
        if (sourceRoot is null)
        {
            Assert(false, "前置条件：应能找到 src/SwitchCfwWizard 目录");
            return;
        }

        var xamlPath = Path.Combine(sourceRoot, "MainWindow.xaml");
        Assert(File.Exists(xamlPath), "前置条件：MainWindow.xaml 应存在");
        if (!File.Exists(xamlPath))
        {
            return;
        }

        var roots = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in new Regex(@"\{Binding\s+([^},]+)").Matches(File.ReadAllText(xamlPath)))
        {
            var root = match.Groups[1].Value.Trim();
            if (root.StartsWith("Path=", StringComparison.Ordinal))
            {
                root = root["Path=".Length..];
            }

            root = root.Split('.')[0].Trim();
            if (root.Length > 0)
            {
                roots.Add(root);
            }
        }

        // 前置条件：正则要是失效了，下面那条断言会变成恒真的空转
        Assert(roots.Count > 50,
            $"前置条件：应从 MainWindow.xaml 里扫出大量绑定（实际只找到 {roots.Count} 个，正则可能失效了）");

        // 带 '=' 的其它写法（RelativeSource / ElementName / Source）绑的不是 DataContext 上的属性，
        // 本护栏管不了 —— 真出现了就**报错**而不是静默跳过，免得护栏悄悄少查一批。
        var unsupported = roots
            .Where(r => r.Contains('=') && !r.StartsWith("Path=", StringComparison.Ordinal))
            .ToList();
        Assert(unsupported.Count == 0,
            "出现了本护栏不认识的绑定写法，请扩展它而不是让它静默少查"
            + (unsupported.Count == 0 ? "" : "：" + string.Join("、", unsupported)));

        var known = typeof(MainViewModel).Assembly
            .GetTypes()
            .Where(t => t.IsPublic)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var unknown = roots.Where(r => !known.Contains(r)).ToList();
        Assert(unknown.Count == 0,
            $"界面上的 {roots.Count} 个绑定名都应能在某个类型上找到同名公开属性（写错只会静默绑到空值）"
            + (unknown.Count == 0 ? "" : $"，实际不存在：{string.Join("、", unknown)}"));
    }

    /// <summary>从测试程序集的位置往上找 <c>src/SwitchCfwWizard</c>。</summary>
    private static string? FindSourceRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(MainViewModel).Assembly.Location)!);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "SwitchCfwWizard");
            if (File.Exists(Path.Combine(candidate, "MainWindow.xaml")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void WriteText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
    }

    /// <summary>
    /// 造一个**真实的 tar.gz** 夹具（名字以 <c>/</c> 结尾的条目当成目录）。
    ///
    /// 用 .NET 内置的 <c>System.Formats.Tar</c> —— 与解包端**同一个库**，所以
    /// 「格式对不对」这件事本身也被覆盖到了；也不必像 7z 那样内联 base64
    /// （那个是因为 SharpCompress 0.50 只能读不能写）。
    /// </summary>
    private static void WriteTarGz(string path, params (string Name, string Content)[] entries)
    {
        AppPaths.EnsureDirectory(Path.GetDirectoryName(path)!);

        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);

        foreach (var (name, content) in entries)
        {
            if (name.EndsWith('/'))
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name));
                continue;
            }

            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            });
        }
    }

    private static void AddEntry(ZipArchive archive, string name, string? content)
    {
        var entry = archive.CreateEntry(name);
        if (content is null)
        {
            return; // 目录项
        }

        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    /// <summary>
    /// 删一个文件，失败就当没发生。
    ///
    /// ⚠️ 用 <c>File.Delete</c> 而不是 shell 的 <c>rm</c>：本机沙箱注入的 <c>rm</c> 会**拒绝删除
    /// <c>*.log</c>**（见 build.sh scrub_traces 的注释），而这套用例要删的正好是日志文件。
    /// </summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉无所谓：这些都是测试自己造出来的临时文件
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清理失败不该让测试变红
        }
    }

    /// <summary>同上，针对单个文件（临时夹具用）。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件清理失败不该让测试变红
        }
    }

    private static WizardOptions CloneFlags(WizardOptions source) => new()
    {
        Atmosphere = source.Atmosphere,
        Hekate = source.Hekate,
        Ultrahand = source.Ultrahand,
        SysPatch = source.SysPatch,
        Ram8Gb = source.Ram8Gb,
        BootStock = source.BootStock,
        BootSysNand = source.BootSysNand,
        BootEmuNand = source.BootEmuNand,
        BlankSerialSysmmc = source.BlankSerialSysmmc,
        BlankSerialEmummc = source.BlankSerialEmummc,
        Use90DnsSysmmc = source.Use90DnsSysmmc,
        Use90DnsEmummc = source.Use90DnsEmummc,
        PreferPrerelease = source.PreferPrerelease,
        IncludeComponentFilesInOutput = source.IncludeComponentFilesInOutput,
        IncludePayloads = source.IncludePayloads,
        IncludeBootDat = source.IncludeBootDat,
        // BootDatPath 是「这次生成要用哪一份下载好的 boot.dat」。漏拷会让克隆出来的场景
        // **静默**变成「勾了但没有源文件」—— 生成时只是少写一个文件，断言看着照样通过。
        BootDatPath = source.BootDatPath,
        // Language 是生成的真实输入（决定 default_lang 与 hekate 走哪个仓库），
        // 漏拷的话克隆出来的场景会**静默**用上空串（→ en），断言看着通过、其实测的是另一回事。
        Language = source.Language,
    };

    // ── 基础设施 ────────────────────────────────────────────────
    private static void ApplyCatalogDefaults(WizardOptions options)
    {
        foreach (var definition in ComponentCatalog.All)
        {
            foreach (var optionDefinition in definition.Options)
            {
                options.Set(definition.Kind, optionDefinition.Key, optionDefinition.DefaultValue);
            }
        }
    }

    private static ConfigGenerationResult Generate(WizardOptions options)
    {
        var generator = new ConfigGenerator(new SilentSink());
        return generator.Generate(options, CancellationToken.None);
    }

    private static string Read(string relativePath)
        => File.ReadAllText(Path.Combine(AppPaths.OutputRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// 读 out/ 里的文件；文件不存在时返回空串而不是抛异常。
    ///
    /// 断言「某个文件的内容应该是什么」时用这个：文件整个缺失也要给出可读的失败信息，
    /// 而不是抛 FileNotFound 把同一批断言后面的全带崩 —— 那样只能看到一条崩溃堆栈，
    /// 看不出到底错在哪几处。
    /// </summary>
    private static string ReadOrEmpty(string relativePath)
    {
        var full = Path.Combine(AppPaths.OutputRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllText(full) : string.Empty;
    }

    /// <summary>
    /// 同 <see cref="ReadOrEmpty"/>，但吃**绝对路径** —— 断言里用 <c>Path.Combine(resDir, "x.bmp")</c>
    /// 拼出来的那种。
    ///
    /// 为什么非要单独来一个：断言「文件内容应等于 …」时，如果文件压根不存在，
    /// 裸 <c>File.ReadAllText</c> 会抛 <see cref="FileNotFoundException"/> 把**整套**测试打成一条崩溃堆栈，
    /// 后面所有断言的结论全部丢失 —— 只能看到「第 4259 行崩了」，看不出到底错在哪几处。
    /// 返回空串则让 Assert 自己给出「内容不符（实际：（文件不存在或为空））」这种能直接读的结论。
    /// </summary>
    private static string ReadFileOrEmpty(string fullPath)
        => File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty;

    /// <summary>把内容转成断言信息里好读的形式（空串要说清是「文件不存在或为空」）。</summary>
    private static string Describe(string content)
        => string.IsNullOrEmpty(content) ? "（文件不存在或为空）" : content;

    private static int LineCount(string relativePath)
        => Read(relativePath)
            .Replace("\r\n", "\n")
            .TrimEnd('\n')
            .Split('\n')
            .Length;

    private static int SectionCount(string ini, string sectionName)
        => ini.Split('\n').Count(l => l.Trim() == $"[{sectionName}]");

    private static string ReadSection(string ini, string sectionName)
    {
        var lines = ini.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == $"[{sectionName}]");
        if (start < 0)
        {
            return string.Empty;
        }

        var body = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
            {
                break;
            }

            body.Add(lines[i]);
        }

        return string.Join('\n', body);
    }

    /// <summary>
    /// 取 ini 里某个键的**值**（第一个同名键；注释行跳过）。找不到返回 <c>null</c>。
    ///
    /// 用「取值再比」而不是 <c>text.Contains("键=值")</c>：后者会把前缀相同的值混进来 ——
    /// 断言 <c>default_lang=zh</c> 时，<c>default_lang=zh-tw</c> 也会命中。
    /// </summary>
    private static string? ReadIniValue(string ini, string key)
    {
        foreach (var rawLine in ini.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            var index = line.IndexOf('=');
            if (index > 0 && line[..index].Trim() == key)
            {
                return line[(index + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// 「存档本该不存在、却冒出来了」这件事最早被发现在哪个小节之后（见 <see cref="Section"/>）。
    ///
    /// 存在的理由：Main 末尾那条幂等护栏只能报「跑完与开跑前不一样」，**看不出是谁写的** ——
    /// 而这条链路的写入点有五处（自动落盘、语言切换、自动打包、两处载入期就地修正），
    /// 逐个去猜比跑一次还慢。小节边界是现成的粗粒度探针：够把范围缩到一段代码里。
    /// </summary>
    private static string? _settingsLeakSection;

    private static bool _settingsExistedAtStart = true;

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("── " + title + " ──");

        // 只在「开跑时存档就不存在」这个前提下才探测 —— 否则每段都有文件，探针恒真、毫无信息量。
        if (!_settingsExistedAtStart
            && _settingsLeakSection is null
            && File.Exists(SettingsStore.FilePath))
        {
            _settingsLeakSection = title;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("  ✓ " + message);
        }
        else
        {
            Failures.Add(message);
            Console.WriteLine("  ✗ " + message);
        }
    }

    private sealed class SilentSink : ILogSink
    {
        public void Log(LogLevel level, string message)
        {
            if (level is LogLevel.Warning or LogLevel.Error)
            {
                Console.WriteLine($"    [{level}] {message}");
            }
        }
    }

    /// <summary>
    /// 与 <see cref="SilentSink"/> 同族，但把消息**留下来**供断言检查。
    ///
    /// 存在的理由：本项目里「回落 + 告警」是一种标准修法，而**告警本身也是功能** ——
    /// 只断言「值回落了」而不断言「告警发了」，等于只验了一半：告警被顺手删掉时没有任何断言会红，
    /// 而用户看到的就是「静默回落」，与不做回落的体验一样糟（§1 教训 8）。
    ///
    /// ⚠️ `Messages` 收**全部**级别；要断言「没有告警」必须用 <see cref="Warnings"/> ——
    /// 生成器每次都会打一堆 Info（「已写入 xxx.ini」），拿 `Messages` 判空必然失败。
    /// </summary>
    private sealed class CapturingSink : ILogSink
    {
        public List<string> Messages { get; } = [];

        public List<string> Warnings { get; } = [];

        public void Log(LogLevel level, string message)
        {
            Messages.Add(message);
            if (level is LogLevel.Warning or LogLevel.Error)
            {
                Warnings.Add(message);
            }
        }
    }
}
