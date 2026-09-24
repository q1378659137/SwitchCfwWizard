using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;
using SwitchCfwWizard.Models;
using SwitchCfwWizard.Services;

namespace SwitchCfwWizard.ViewModels;

public sealed class MainViewModel : ObservableObject, ILogSink
{
    private const double ResolvePhaseEnd = 8d;
    private const double ConfigPhaseStart = 92d;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly AppSettings _settings;

    private HttpClient? _http;
    private HttpClient? _downloadHttp;
    private CancellationTokenSource? _cts;

    private bool _isRunning;
    private bool _overallIsIndeterminate;
    private double _overallProgress;
    private string _overallStatus = string.Empty;
    private string _outputPath = AppPaths.OutputRoot;
    private string _downloadPath = AppPaths.DownloadRoot;
    private bool _showAdvancedSettings;

    // 初始值必须与 AppSettings.AutoPackZip 的默认值一致（都是 false）。
    // 构造函数里虽然会用 _settings.AutoPackZip 覆盖它，但两者不一致就是埋雷：
    // 哪天有人删掉那行赋值，默认行为会**静默反转**成「生成后自动打包」，与用户要求相反。
    private bool _autoPackZip;

    /// <summary>最近一次生成写入的文件（相对 out 的路径），打包后用来比对清单。</summary>
    private List<string> _lastGeneratedFiles = new();

    // ── 自动落盘 ────────────────────────────────────────────────────
    //
    // 用户要求（2026-09-18）：**打开软件后的每一次操作都要写进 settings.json**，下次打开时读回来。
    // 原先只有「保存设置」按钮与语言下拉会落盘，其他改动关掉程序就没了 ——
    // 而「改了没存」是静默的：界面上一切正常，下次启动才发现被打回原形。
    //
    // 做法是**在通知这一层接线**，不是在每个 setter 里手写一次 SaveSettings()：
    // 手写块漏一处就是同一个病，而且漏了不报错（§1 教训 7 的同族问题）。
    /// <summary>
    /// 自动落盘的间隔。取 400ms：拖一次文本框、连点几个勾选框都只写一次文件，
    /// 而「改完立刻关窗」也不会丢（<c>MainWindow.Closing</c> 还会补一次强制落盘）。
    /// </summary>
    internal const int AutoSaveDelayMs = 400;

    /// <summary>
    /// 自动落盘的定时器。
    ///
    /// ⚠️ **必须在这里就建出来，不能挪到构造函数末尾**：<see cref="ScheduleAutoSave"/> 会被
    /// 组件/配置项的变更通知调到，而构造函数的**中途**就会发出这种通知
    /// （<see cref="UpdateForcedSelections"/> 会去改组件的 <c>IsSelected</c>，而那个通知是更早订阅的）。
    /// 定时器若是构造函数的产物，就存在「通知先到、定时器还没建」的时序窗口 ——
    /// 那不是理论风险：本轮就是在这条路径上空引用崩溃的（回归当场抓到）。
    ///
    /// <c>Tick</c> 只能在构造函数里挂（字段初始化器里不许调实例方法），但这不影响安全性：
    /// 构造函数跑完之前 <c>_autoSaveArmed</c> 一直是 false，<see cref="ScheduleAutoSave"/> 不会真的启动它。
    /// </summary>
    private readonly DispatcherTimer _autoSaveTimer = new(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
    {
        Interval = TimeSpan.FromMilliseconds(AutoSaveDelayMs),
    };

    /// <summary>正在自动落盘。落盘过程本身若触发了通知，不要再排一次，免得自激。</summary>
    private bool _autoSaving;

    /// <summary>
    /// 上一次落盘时的设置快照（摊平后的「键 → 值」）。用来算「这次改了什么」写进日志。
    ///
    /// 与 <see cref="SaveSettings"/> 一起维护：它推进得太早会把两次改动合并报错，
    /// 推进得太晚会把同一件事报两遍。
    /// </summary>
    private IReadOnlyDictionary<string, string> _lastSettingsSnapshot =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>本次运行的警告 / 错误条数（退出时写进日志末行）。</summary>
    private int _warningCount;
    private int _errorCount;

    /// <summary>
    /// 自动落盘是否已「上膛」——构造函数跑完才置 true。
    ///
    /// 为什么需要它：构造函数**中途**就会发变更通知（读回存档、按存档做联动归一化时都会改属性），
    /// 那些不是用户操作。少了这道闸，程序一启动就会把刚读进来的存档原样写一遍 ——
    /// 磁盘上看着没变，但「启动即写」会让排查问题的人无法用文件时间戳判断
    /// 「这个值是用户改的还是程序写的」。
    /// </summary>
    private bool _autoSaveArmed;

    /// <summary>
    /// 建 HttpClient 时用的「网络设置」指纹。只有 Token / 超时 / 代理真的变了才重建客户端 ——
    /// 自动落盘每 400ms 就可能写一次文件，无条件重建会把 HttpClient 建爆
    /// （套接字耗尽，而且连接池刚建好就被丢掉）。
    /// </summary>
    private string _httpSignature = string.Empty;

    /// <summary>
    /// 运行时状态属性：**不是设置**，却会在下载过程中每秒变很多次。
    ///
    /// 这是一张「性能跳过表」，**不是**白名单 —— 判据是「漏一个名字会怎样」：
    /// 漏了这里最多是多写几次文件（无害），而白名单漏一个名字就是**悄悄不落盘**（正是要修的病）。
    /// 所以宁可多写，也不许少写。
    /// </summary>
    internal static readonly HashSet<string> TransientPropertyNames = new(StringComparer.Ordinal)
    {
        nameof(IsRunning),
        nameof(OverallProgress),
        nameof(OverallIsIndeterminate),
        nameof(OverallStatus),
        nameof(CanStart),
        nameof(ShowNoSelectionHint),
        nameof(TokenStatus),
        nameof(HasTokenStatus),
        nameof(IsVerifyingToken),
        nameof(MirrorIsInvalid),
        nameof(BootDatUrlIsInvalid),
        nameof(UpdateUrlIsInvalid),
        // 上一轮补「检查更新」时漏掉的四个（它们同样是运行时状态，不属于设置）。
        // 漏进这张表的后果不是错误、只是浪费：每次状态变化都会排一次自动落盘，
        // 而那次落盘会因为「快照没变」自己停下 —— 但这张表的用意本来就是别去排它。
        nameof(UpdateStatus),
        nameof(HasUpdateStatus),
        nameof(IsCheckingUpdate),
        nameof(CurrentVersionText),
        // 下载新版时的百分比（2026-09-22）—— 同样是运行时状态，不属于设置。
        nameof(UpdateProgress),
        nameof(UpdateProgressText),
        nameof(IsUpdateDownloading),
        // boot.dat 的下载进度与状态（2026-09-21）：只在下一次生成时才有值。
        nameof(BootDatProgress),
        nameof(BootDatStatus),
        // 卡片上那一行文字（空状态也要有内容 —— 进度条现在始终显示，见 BootDatStatusText）。
        nameof(BootDatStatusText),
        // 两个地址框的「空」判据（驱动框里的格式提示）。
        nameof(BootDatUrlIsEmpty),
        nameof(UpdateUrlIsEmpty),
        nameof(Logs),
    };

    public MainViewModel()
    {
        _settings = SettingsStore.Load();

        LocalizationService.Instance.SetLanguage(_settings.Language);
        LocalizationService.Instance.LanguageChanged += (_, _) => RefreshLocalizedText();

        // 顺序很重要：必须先把 Components 建好，再设置 SelectedLanguage。
        // SelectedLanguage 的 setter 会调用 RefreshAutobootChoices()，而那里要读 Components。
        Components = new ObservableCollection<ComponentViewModel>(
            ComponentCatalog.All.Select(definition =>
            {
                var saved = ExtractSavedValues(definition.Kind);
                var vm = new ComponentViewModel(definition, saved);
                vm.IsSelected = _settings.Components.TryGetValue(definition.Kind.ToString(), out var selected) && selected;
                vm.PropertyChanged += OnComponentPropertyChanged;
                return vm;
            }));

        // 分类分组：**从 Components 现算**，只保留非空的组。
        //
        // 保留空组也能跑（会渲染出一个光秃秃的标题），但那正是「新增分类却忘了填组件」的症状 ——
        // 界面上出现一个空标题，用户只会以为程序坏了。空组直接不显示，同时由
        // CheckComponentCategoryCoverage 在回归里报出来（那才是该发现它的地方）。
        ComponentGroups = new ObservableCollection<ComponentCategoryViewModel>(
            ComponentCatalog.Categories
                .Select(category => new ComponentCategoryViewModel(
                    category,
                    Components.Where(c => c.Category == category)))
                .Where(group => group.Components.Count > 0));

        // 高级设置里「插件文件」那一区：每个「文件槽」一行（地址 + 文件名）。
        //
        // **从 ComponentCatalog.All 现算，不手抄清单** —— 与 Components / ComponentGroups 同一条原则：
        // 抄一份清单出来，就一定会出现「加了槽却忘了加进界面」而没人报错的情况。
        // 判据是 Slots.Count > 0：框架那四个组件走手写 picker，没有槽，自然不出现（它们的地址在
        // 「下载源」那一栏改）。
        //
        // ⚠️ 顺序必须排在这里（Components 之后）：下面 RefreshAutobootChoices / UpdateForcedSelections
        //    会读 Components，而槽位视图不依赖它们，只是顺手沿用同一个「先建集合、后跑联动」的次序。
        AssetSlotGroups = new ObservableCollection<AssetSlotGroupViewModel>(
            ComponentCatalog.All
                .Where(definition => definition.Slots.Count > 0)
                .Select(definition => new AssetSlotGroupViewModel(
                    definition,
                    definition.Slots
                        .Select(slot =>
                        {
                            var key = ComponentCatalog.SlotSettingKey(definition, slot);
                            return new AssetSlotViewModel(
                                definition,
                                slot,
                                _settings.AssetAddresses.TryGetValue(key, out var address) ? address : null,
                                _settings.AssetFileNames.TryGetValue(key, out var fileName) ? fileName : null);
                        })
                        .ToList())));

        // 90DNS ↔ enable_dns_mitm 的四条联动（全部在 HookDnsMitmCoupling 附近有说明）：
        // ①②③ 由两个 90DNS 属性与 mitm 的 ValueChanged 驱动；④（勾 mitm 补勾 90DNS）在 OnDnsMitmOptionChanged 里。
        HookDnsMitmCoupling();

        // default_lang 选了非 en 的语言 → 补勾「安装语言包」（见 EnsureLangPackForDefaultLang）。
        HookLangPackCoupling();

        Languages = new ObservableCollection<LanguageInfo>(LocalizationService.Instance.Languages);

        // 主题是**固定三项**（跟随系统 / 浅色 / 暗夜），一次建好。显示名是派生属性
        // （见 ThemeInfo），切语言时刷新即可 —— 不重建集合，免得 SelectedItem 抖动。
        Themes = new ObservableCollection<ThemeInfo>(ThemeModeCodec.All.Select(mode => new ThemeInfo(mode)));

        // 引导项状态必须在设置 SelectedLanguage **之前**赋好。
        // SelectedLanguage 的 setter 会调用 RefreshAutobootChoices()，而那里要读 BootStock / BootSysNand / BootEmuNand。
        // 若此刻它们还是字段默认的 false，重建出来的 autoboot 候选项就只剩「关闭」一项，
        // 存档里的值（例如 "3" = 自动进入虚拟破解系统）不在候选项里，会被 ReplaceChoices 自愈成 "0"。
        // 后果：用户设好的自动引导项每次启动都被静默清成「关闭」；而 settings.json 里还留着旧值
        // （setter 保存的是 _settings.OptionValues，没被改动），界面与文件长期不一致，
        // 直到用户某次点「保存设置」才把 "0" 永久写回去。
        _bootStock = _settings.BootStock;
        _bootSysNand = _settings.BootSysNand;
        _bootEmuNand = _settings.BootEmuNand;

        // 两个 90DNS 的开关状态要先读进来，下面那次归一化要用它做判据。
        _use90DnsSysmmc = _settings.Use90DnsSysmmc;
        _use90DnsEmummc = _settings.Use90DnsEmummc;

        // 载入后立刻把 mitm 拉回与两个 90DNS 一致（规则③：两个 90DNS 都不勾 ⇒ mitm 关）。
        // 老版本的 settings.json 里 enable_dns_mitm 的默认值是 "1"，升级上来就是
        // 「mitm 开着、两个 90DNS 都不勾」：产物写出 enable_dns_mitm=1 却一份 hosts 都没有，
        // 用户会以为遥测已经被挡住 —— 正是这条联动要消灭的状态。
        // 这里**不**反向补勾 90DNS（规则④只在用户亲手勾 mitm 时走）：载入不是用户动作，
        // 替他写出一份没要过的 hosts 文件更越权。
        if (ApplyDnsMitmCoupling())
        {
            // 修正了就得回写，否则 settings.json 会一直留着旧值（界面已修正、文件没改）。
            // ⚠️ 这里**不能**用 SaveSettings()：它会遍历 RepoSources 与全部组件选项，
            //    而这两者此刻还没建好 —— 会把用户的下载源覆盖成空、选项值写丢。
            //    只回写被修正的那一项，其余原样保留。
            var corrected = DnsMitmOption;
            if (corrected is not null)
            {
                _settings.OptionValues[$"{ComponentKind.Atmosphere}/{DnsMitmOptionKey}"] = corrected.Value;
                SettingsStore.Save(_settings);
            }
        }

        _bootStockTitle = _settings.BootStockTitle ?? string.Empty;
        _bootSysNandTitle = _settings.BootSysNandTitle ?? string.Empty;
        _bootEmuNandTitle = _settings.BootEmuNandTitle ?? string.Empty;
        _bootStockLogo = _settings.BootStockLogo ?? string.Empty;
        _bootSysNandLogo = _settings.BootSysNandLogo ?? string.Empty;
        _bootEmuNandLogo = _settings.BootEmuNandLogo ?? string.Empty;
        _bootStockIcon = _settings.BootStockIcon ?? string.Empty;
        _bootSysNandIcon = _settings.BootSysNandIcon ?? string.Empty;
        _bootEmuNandIcon = _settings.BootEmuNandIcon ?? string.Empty;

        SelectedLanguage = Languages.FirstOrDefault(l => l.Code == _settings.Language) ?? Languages.FirstOrDefault();

        // 主题同理：构造期走 setter ⇒ **启动就把存档里的档位应用上**。这一步发生在窗口 Show 之前，
        // 所以看不到「先浅色、再变暗」那一下。
        // 认不出来的取值（老存档没有这个字段 / 用户手改错）由 ThemeModeCodec.Parse 回落成「跟随系统」。
        SelectedTheme = Themes.FirstOrDefault(t => t.Mode == ThemeModeCodec.Parse(_settings.Theme))
                        ?? Themes.FirstOrDefault();

        // 载入后归一化：界面语言定下来了，default_lang 若是「跟随界面语言」且解析结果非 en，
        // 就把「安装语言包」补上。与 ApplyDnsMitmCoupling 那次显式调用同一个理由 ——
        // 载入归一化要**写在构造函数里**，不能只藏在某个属性的 setter 里（读代码的人看不见）。
        // 上面那个 setter 也会做一遍，这里是幂等的第二次调用。
        EnsureLangPackForDefaultLang();

        _blankSerialSysmmc = _settings.BlankSerialSysmmc;
        _blankSerialEmummc = _settings.BlankSerialEmummc;
        _ram8Gb = _settings.Ram8Gb;
        _preferPrerelease = _settings.PreferPrerelease;
        _includeComponentFilesInOutput = _settings.IncludeComponentFilesInOutput;
        _includePayloads = _settings.IncludePayloads;
        _includeBootDat = _settings.IncludeBootDat;
        _autoPackZip = _settings.AutoPackZip;

        _gitHubToken = _settings.GitHubToken ?? string.Empty;
        _timeoutSeconds = _settings.TimeoutSeconds;
        _proxy = _settings.Proxy ?? string.Empty;
        _mirror = _settings.Mirror ?? string.Empty;
        _bootDatUrl = _settings.BootDatUrl ?? string.Empty;
        _updateUrl = _settings.UpdateUrl ?? string.Empty;

        // ⚠️ 全部经 Command(...) 建，别改回 new RelayCommand( —— 那样按钮点击就不会进日志文件，
        //    而「漏记一条」是静默的（用户恰恰是靠日志排查问题的人）。
        //    源码契约 CheckOperationLogging 会断言这里再也搜不到 new RelayCommand(。
        // 标签里的语言键与按钮上显示的文字**是同一批**（最后一个键就是按钮文字），
        // 前几个键是「哪一行」—— 界面上有 6 个「选择…」6 个「清除」，只记按钮文字等于没记。
        StartCommand = Command(new("Common.Start"), () => _ = RunAsync(), () => CanStart);
        CancelCommand = Command(new("Common.Cancel"), RequestCancel, () => IsRunning);
        OpenOutputCommand = Command(new("App.OpenOutput"), () => OpenFolder(AppPaths.OutputRoot));
        OpenDownloadCommand = Command(new("App.OpenDownload"), () => OpenFolder(AppPaths.DownloadRoot));
        // OpenFolder 内部会先 EnsureDirectory，所以「还没写过日志」时点它也能正常打开
        // （用户点这个按钮的动机就是「日志在哪」，给一句「找不到路径」最没有帮助）。
        OpenLogsCommand = Command(new("App.OpenLogs"), () => OpenFolder(AppPaths.LogRoot));
        ClearLogCommand = Command(new("Common.ClearLog"), () => Logs.Clear());

        // ⚠️ 这里**没有**「保存设置」按钮（用户 2026-09-19 明确要求取消）：
        //    界面上的每一处改动都会经自动落盘自己写进 settings.json（400ms 节流 + 关窗补写），
        //    再放一个按钮等于给用户出选择题——「我改了没点它，到底存没存？」
        //    唯一的落盘实现是 SaveSettings()，它只被 SaveSettingsQuietly() 调用（见那里的注释）。
        ToggleAdvancedCommand = Command(new("Settings.Section"), () => ShowAdvancedSettings = !ShowAdvancedSettings);
        PackZipCommand = Command(new("App.PackZip"), PackZip, () => !IsRunning && OutputPackager.HasContent(AppPaths.OutputRoot));

        OpenTokenPageCommand = Command(new("Settings.GitHubToken.Open"), OpenTokenPage);
        VerifyTokenCommand = Command(new("Settings.GitHubToken.Verify"), () => _ = VerifyTokenAsync(), () => !IsRunning && !IsVerifyingToken);

        // 检查更新：查询走 _releases（**官方优先、只有连接层失败才用镜像** —— 镜像出口 IP 公用，
        // 直接拿它查 API 反而更容易被限流），下载走 _download（**填了镜像就直接走镜像**）。
        // 这两条通道的分工与其余下载完全一致，不另设一套。
        CheckUpdateCommand = Command(new("App.CheckUpdate"), () => _ = CheckUpdateAsync(), () => !IsRunning && !IsCheckingUpdate);

        // 紧挨着镜像站输入框的那个按钮：把「镜像站怎么搭」的说明打开。
        OpenMirrorGuideCommand = Command(new("Settings.Mirror.BuildGuide"), OpenMirrorGuide);
        ResetRepoSourcesCommand = Command(new("Settings.Repos.Reset"), ResetRepoSources);
        ResetAssetSlotsCommand = Command(new("Settings.Slots.Reset"), ResetAssetSlots);

        PickStockLogoCommand = Command(new("Global.Boot.Stock", "Global.Boot.Logo", "Global.Boot.Logo.Browse"), () => PickBootImage(BootStockLogo, "Global.Boot.Logo.PickTitle", value => BootStockLogo = value));
        PickSysNandLogoCommand = Command(new("Global.Boot.SysNand", "Global.Boot.Logo", "Global.Boot.Logo.Browse"), () => PickBootImage(BootSysNandLogo, "Global.Boot.Logo.PickTitle", value => BootSysNandLogo = value));
        PickEmuNandLogoCommand = Command(new("Global.Boot.EmuNand", "Global.Boot.Logo", "Global.Boot.Logo.Browse"), () => PickBootImage(BootEmuNandLogo, "Global.Boot.Logo.PickTitle", value => BootEmuNandLogo = value));
        ClearStockLogoCommand = Command(new("Global.Boot.Stock", "Global.Boot.Logo", "Global.Boot.Logo.Clear"), () => BootStockLogo = string.Empty);
        ClearSysNandLogoCommand = Command(new("Global.Boot.SysNand", "Global.Boot.Logo", "Global.Boot.Logo.Clear"), () => BootSysNandLogo = string.Empty);
        ClearEmuNandLogoCommand = Command(new("Global.Boot.EmuNand", "Global.Boot.Logo", "Global.Boot.Logo.Clear"), () => BootEmuNandLogo = string.Empty);

        PickStockIconCommand = Command(new("Global.Boot.Stock", "Global.Boot.Icon", "Global.Boot.Logo.Browse"), () => PickBootImage(BootStockIcon, "Global.Boot.Icon.PickTitle", value => BootStockIcon = value));
        PickSysNandIconCommand = Command(new("Global.Boot.SysNand", "Global.Boot.Icon", "Global.Boot.Logo.Browse"), () => PickBootImage(BootSysNandIcon, "Global.Boot.Icon.PickTitle", value => BootSysNandIcon = value));
        PickEmuNandIconCommand = Command(new("Global.Boot.EmuNand", "Global.Boot.Icon", "Global.Boot.Logo.Browse"), () => PickBootImage(BootEmuNandIcon, "Global.Boot.Icon.PickTitle", value => BootEmuNandIcon = value));
        ClearStockIconCommand = Command(new("Global.Boot.Stock", "Global.Boot.Icon", "Global.Boot.Logo.Clear"), () => BootStockIcon = string.Empty);
        ClearSysNandIconCommand = Command(new("Global.Boot.SysNand", "Global.Boot.Icon", "Global.Boot.Logo.Clear"), () => BootSysNandIcon = string.Empty);
        ClearEmuNandIconCommand = Command(new("Global.Boot.EmuNand", "Global.Boot.Icon", "Global.Boot.Logo.Clear"), () => BootEmuNandIcon = string.Empty);

        foreach (var source in ComponentCatalog.RepoSources)
        {
            var custom = _settings.RepoOverrides.TryGetValue(source.Key, out var saved) ? saved : string.Empty;
            RepoSources.Add(new RepoSourceViewModel(source.Key, source.Name, source.HintKey, custom));
        }

        RefreshAutobootChoices();
        UpdateForcedSelections();
        RefreshTitleConflicts();
        RefreshLocalizedText();

        _overallStatus = LocalizationService.Instance["Common.Ready"];

        // 会话头：把「这次是在什么环境、哪份设置下跑的」钉在日志开头。
        // 用户把日志发过来时，这几行往往就够定位一半的问题（版本不对、读的是另一份存档、
        // 工作目录不是他以为的那个……）。
        LogSessionHeader();

        Log(LogLevel.Info, string.Format(LocalizationService.Instance["Log.Begin"],
            LocalizationService.Instance["App.Title"]));
        Log(LogLevel.Info, LocalizationService.Instance["Log.TokenMissing"]);
        Log(LogLevel.Info, LocalizationService.Instance["Log.RateLimitHint"]);

        // 注意：这里**刻意不**做「首次运行默认全选」。
        //
        // 用户要求：首次运行时所有组件都不勾，让用户自己按需要挑。勾选与否只由 settings.json
        // 里的记录决定 —— 没有记录就是全不勾，「开始」按钮保持不可用（CanStart 要求至少勾一个）。
        //
        // **配置项的默认值仍然照旧填好**（见 Components 构造处的 ExtractSavedValues）：
        // 「不勾组件」与「选项没有默认值」是两件事，别把前者做成后者。
        //
        // 默认全选的能力保留在 EnsureComponentsSelected()，但只给命令行 --run 用 ——
        // 无人值守路径没有界面可以勾，不自动选就一步都跑不动（见 App.RunHeadless）。

        // ── 自动落盘接线 ────────────────────────────────────────────
        //
        // ⚠️ 订阅必须排在构造函数**最后**：此前的赋值是「读回存档」与按存档做联动归一化，
        //    都不是用户操作，不该反过来触发落盘。
        _autoSaveTimer.Tick += (_, _) =>
        {
            // 先停再写：写的过程中若又来了通知，会重新 Start，不会漏掉那次改动。
            _autoSaveTimer.Stop();
            SaveSettingsQuietly();
        };

        PropertyChanged += OnViewModelPropertyChanged;
        HookAutoSaveSources();

        // 差异基线 = 「现在这份界面取值，如果保存下去会写成什么」。
        //
        // ⚠️ 不能直接 `Flatten(_settings)`：存档对象里的 **Components / OptionValues 是等到保存那一刻
        //    才填进去的**（见 MaterializeInto），所以构造期直接摊平会把它们读成「空」——
        //    于是稍后**任何一次**落盘都会把这 46 个勾选 + 76 个配置项逐条报成「新增」。
        //    实测过：首次运行时日志的第一条是「设置变更：…（另有 110 项，见 settings.json）」，
        //    而用户一件事都没做 —— 日志的第一句话就是假的。
        //
        // ⚠️ 探针用**新读一份磁盘存档**（不是 _settings）：MaterializeInto 会把 Components 填成 46 条，
        //    而 _settings.Components 被 EnsureComponentsSelected 当作「文件里到底有没有记录」的判据，
        //    在构造期污染它会让「首次运行默认全选」当场失效。新读一份则两不相扰。
        var baseline = SettingsStore.Load();
        MaterializeInto(baseline);
        _lastSettingsSnapshot = SettingsSnapshot.Flatten(baseline);

        // ⚠️ 必须**最后**才上膛：早了的话，构造函数中途那些赋值会被当成用户操作写回存档。
        //    护栏 CheckAutoSaveWiring 钉住了这个次序。
        _autoSaveArmed = true;
    }

    /// <summary>
    /// 会话头：把「这次是在什么环境、哪份设置下跑的」钉在日志开头。
    ///
    /// 用户把日志发过来时，这几行常常就够定位一半的问题：版本不对（他跑的不是最新版）、
    /// 读的是另一份存档、工作目录不是他以为的那个、代理根本没配上……
    /// </summary>
    private void LogSessionHeader()
    {
        var loc = LocalizationService.Instance;
        var self = typeof(MainViewModel).Assembly.GetName();

        Log(LogLevel.Info, loc["Log.Session.Start"]);
        Log(LogLevel.Info, string.Format(
            loc["Log.Session.App"],
            loc["App.Title"],
            self.Version?.ToString(3) ?? "?",
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription));

        Log(LogLevel.Info, string.Format(
            loc["Log.Session.Paths"],
            AppPaths.BaseDirectory,
            SettingsStore.FilePath,
            LogFile.CurrentFilePath));

        // 代理可能带「用户名:密码」，只记「设了没有」；Token 同理（而且 Token 写进日志
        // 等于把它交给了下一个看日志的人）。
        if (!string.IsNullOrWhiteSpace(Proxy))
        {
            Log(LogLevel.Info, loc["Log.Session.Proxy"]);
        }

        // 镜像站不是机密 —— 它就是一条公开的加速地址，所以**连地址一起记**：
        // 「这次到底走没走镜像」是排查下载问题的第一条线索。
        // 记**规范化之后**的那个（用户少写个 https:// 时，能看出程序是怎么理解的）。
        if (Services.GitHubMirror.Normalize(Mirror) is { } mirrorRoot)
        {
            Log(LogLevel.Info, string.Format(loc["Log.Session.Mirror"], mirrorRoot));

            // ⚠️ 镜像站是**第三方转发**：配了 Token 就等于把 Token 也交给它。
            //    这不是拒绝的理由（用户可能正需要它），但**必须说出来** ——
            //    不说的话，用户根本不知道自己的 Token 经了谁的手。
            if (!string.IsNullOrWhiteSpace(GitHubToken))
            {
                Log(LogLevel.Warning, loc["Log.Session.MirrorToken"]);
            }
        }
        else if (!string.IsNullOrWhiteSpace(Mirror))
        {
            // 填了但用不了 —— 这种「填了等于没填」必须说出来，否则用户会以为在走镜像。
            Log(LogLevel.Warning, string.Format(loc["Log.Session.MirrorInvalid"], Mirror.Trim()));
        }

        // 主题记**两个**：实际生效的那一档 + 用户的设置。只在「跟随系统」时才分得清
        // 「他选的是跟随系统」还是「他选了暗夜」—— 而这两种情况下一次排查的起点不一样。
        Log(LogLevel.Info, string.Format(
            loc["Log.Session.Theme"],
            loc[ThemeModeCodec.NameKey(ThemeService.Applied)],
            loc[ThemeModeCodec.NameKey(ThemeService.Preference)]));
    }

    /// <summary>会话尾：把「这次跑完是什么结果」收在日志最后一行。</summary>
    private void LogSessionFooter() => Log(LogLevel.Info, string.Format(
        LocalizationService.Instance["Log.Session.End"],
        _warningCount,
        _errorCount));

    /// <summary>
    /// 记一次「设置变了」，攒到 <see cref="AutoSaveDelayMs"/> 后整体落盘一次。
    ///
    /// 用**节流**（已经在跑就不重置）而不是去抖（每次都重置）：下载过程中进度通知很密，
    /// 去抖会被无限推迟，等于「任务跑完才写」；节流保证最长 400ms 就落一次盘。
    /// </summary>
    private void ScheduleAutoSave()
    {
        if (!_autoSaveArmed || _autoSaving || _autoSaveTimer.IsEnabled)
        {
            return;
        }

        _autoSaveTimer.Start();
    }

    /// <summary>
    /// 给「子视图模型上的设置源」接上自动落盘。
    ///
    /// 配置项用 <see cref="OptionViewModel.ValueChanged"/> 而不是 <c>PropertyChanged</c>：
    /// 后者还会为 <c>IsVisible</c> / <c>Label</c> / <c>BoolValue</c> 这些**派生**属性发通知，
    /// 值没变也白写一轮文件。
    /// </summary>
    private void HookAutoSaveSources()
    {
        foreach (var component in Components)
        {
            foreach (var option in component.OptionGroups.SelectMany(group => group.Options))
            {
                option.ValueChanged += (_, _) => ScheduleAutoSave();
            }
        }

        // 下载源与插件文件槽：地址/文件名一改就要存，下次打开还得是用户改过的那个。
        foreach (var source in RepoSources)
        {
            source.PropertyChanged += (_, _) => ScheduleAutoSave();
        }

        foreach (var slot in AssetSlotGroups.SelectMany(group => group.Slots))
        {
            slot.PropertyChanged += (_, _) => ScheduleAutoSave();
        }
    }

    /// <summary>
    /// 界面上任何**设置类**属性变了都排一次落盘。
    ///
    /// 与 <see cref="HookAutoSaveSources"/> 的分工：这里管 MainViewModel 自己的属性
    /// （开关、标题、路径、Token…），那里管子视图模型（组件勾选、配置项、下载源、文件槽）。
    /// 两处合起来覆盖界面上全部可操作项。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || TransientPropertyNames.Contains(e.PropertyName))
        {
            return;
        }

        ScheduleAutoSave();
    }

    // ── 操作日志（用户要求：点了哪个按钮、输了什么文字，都要能查）────────────
    //
    // 两条路，都不逐个手写：
    //   · 按钮 → 走**建命令的唯一入口** <see cref="Command"/>（25 个按钮，手写必漏）；
    //   · 文字/勾选/下拉 → 走**设置快照的差异**（见 <see cref="LogSettingsChanges"/>）——
    //     能被改的东西必然出现在快照里，所以「有没有漏」不再取决于谁记得住。
    // 源码契约 CheckOperationLogging 断言这两条路都没被人绕过。

    /// <summary>
    /// 按钮在日志里的名字：若干语言键用「 · 」拼起来，点击时**现查**（这样切了界面语言，
    /// 日志里也是当时的语言）。
    ///
    /// 为什么要拼多个键而不是一个：界面上有 6 个「选择…」和 6 个「清除」，只记按钮文字的话
    /// 日志里是一串「点击「选择…」」—— 等于没记。「引导项 · 启动图 · 选择…」才答得了
    /// 「最后那一下到底点了什么」。
    /// </summary>
    private sealed record CommandLabel(params string[] Keys)
    {
        public string Resolve() => string.Join(
            " · ",
            Keys.Select(key => LocalizationService.Instance[key]));
    }

    /// <summary>
    /// 建一个「点了就记一笔」的命令。**所有 <c>*Command</c> 都必须经这里建**
    /// （<c>CheckOperationLogging</c> 会断言 MainViewModel 里除本方法外不出现 <c>new RelayCommand(</c>）。
    /// </summary>
    private RelayCommand Command(CommandLabel label, Action execute, Func<bool>? canExecute = null)
    {
        return new RelayCommand(
            () =>
            {
                // 先记点击再执行：动作本身（下载、生成……）会往日志里写很多行，
                // 点击那一条排在它前面，「这一串是谁触发的」一目了然。
                Log(LogLevel.Info, string.Format(
                    LocalizationService.Instance["Log.CommandClicked"],
                    label.Resolve()));

                execute();
            },
            canExecute);
    }

    /// <summary>
    /// 把这次落盘与上一次的差异写进日志 —— 这就是「用户输了什么文字 / 勾了什么」那条路。
    ///
    /// 由自动落盘驱动（它本来就在每次改动后结算一次），所以连续输入会被那 400ms 的节流
    /// **合并成一条**（不然打一个词会产生一行日志）。
    /// </summary>
    private void LogSettingsChanges()
    {
        var snapshot = SettingsSnapshot.Flatten(_settings);
        var changes = SettingsSnapshot.Diff(_lastSettingsSnapshot, snapshot);

        // 无论有没有差异都要推进基线：不然「这次没写、下次写」时会把两次的差异混在一起报。
        _lastSettingsSnapshot = snapshot;

        if (changes.Count == 0)
        {
            return;
        }

        // ⚠️ 逐项列但有**上限**：一次「操作」完全可能改动几十项 —— 屏蔽序列号会强制勾上 Sys-patch、
        //    「恢复默认地址」会重置一批文件槽、Ultrahand 的语言联动会同时动两个配置项。
        //    把 4 KB 的一行写进日志，真正要看的东西就被淹掉了。
        var rendered = string.Join("；", changes.Take(MaxReportedSettingChanges).Select(DescribeSettingChange));

        if (changes.Count > MaxReportedSettingChanges)
        {
            rendered += string.Format(
                LocalizationService.Instance["Log.SettingsChangedMore"],
                changes.Count - MaxReportedSettingChanges);
        }

        Log(LogLevel.Info, string.Format(LocalizationService.Instance["Log.SettingsChanged"], rendered));
    }

    /// <summary>一条日志里最多逐项列出几处设置改动（超过部分只报个数，见 <see cref="LogSettingsChanges"/>）。</summary>
    private const int MaxReportedSettingChanges = 12;

    /// <summary>
    /// 一次改动的可读形式：<c>「人话标签」旧值 → 新值</c>。机密项只报「已设置 / 已清空」。
    /// </summary>
    private string DescribeSettingChange(SettingsSnapshot.SettingChange change)
    {
        var label = DescribeSettingKey(change.Key);

        return change.IsSecret
            // ⚠️ 机密项**绝不**出现原文（日志是要发出去给人看的）。这条判断放在这里而不是
            //    SettingsSnapshot 里，是因为「怎么显示」是界面层的事，而「是不是机密」是数据层的事。
            ? string.Format("{0} {1}", label, SettingsSnapshot.DescribeSecretValue(change.NewValue))
            : string.Format(
                "{0} {1} → {2}",
                label,
                LogFile.Shorten(change.OldValue),
                LogFile.Shorten(change.NewValue));
    }

    /// <summary>
    /// 把一个设置键说成人话。认不出来就**原样返回键** ——
    /// 宁可显示得技术一点，也不要显示错（把「下载源」说成「组件勾选」比不说更坏）。
    /// </summary>
    private string DescribeSettingKey(string key)
    {
        var loc = LocalizationService.Instance;

        if (key.StartsWith("Components.", StringComparison.Ordinal)
            && FindComponent(key["Components.".Length..]) is { } checkedComponent)
        {
            return string.Format(loc["Log.Setting.Component"], checkedComponent.Title);
        }

        if (key.StartsWith("OptionValues.", StringComparison.Ordinal))
        {
            var rest = key["OptionValues.".Length..];
            var slash = rest.IndexOf('/');

            if (slash > 0
                && FindComponent(rest[..slash]) is { } owner
                && owner.FindOption(rest[(slash + 1)..]) is { } option)
            {
                return string.Format(loc["Log.Setting.Option"], owner.Title, option.Label);
            }
        }

        if (key.StartsWith("RepoOverrides.", StringComparison.Ordinal)
            && RepoSources.FirstOrDefault(s => string.Equals(
                   s.Key, key["RepoOverrides.".Length..], StringComparison.OrdinalIgnoreCase)) is { } row)
        {
            return string.Format(loc["Log.Setting.RepoOverride"], row.Name);
        }

        if (key.StartsWith("AssetAddresses.", StringComparison.Ordinal)
            && FindSlotGroup(key["AssetAddresses.".Length..]) is { } addressGroup)
        {
            return string.Format(loc["Log.Setting.SlotAddress"], addressGroup);
        }

        if (key.StartsWith("AssetFileNames.", StringComparison.Ordinal)
            && FindSlotGroup(key["AssetFileNames.".Length..]) is { } nameGroup)
        {
            return string.Format(loc["Log.Setting.SlotFileName"], nameGroup);
        }

        // boot.dat 的下载地址 / 检查更新的地址：两项都**有默认值**，日志里要能看出
        // 「用户改过没有」，所以给人话标签而不是原始键名。
        if (string.Equals(key, nameof(BootDatUrl), StringComparison.Ordinal))
        {
            return loc["Log.Setting.BootDatUrl"];
        }

        if (string.Equals(key, nameof(UpdateUrl), StringComparison.Ordinal))
        {
            return loc["Log.Setting.UpdateUrl"];
        }

        // 镜像站：键名 `Mirror`，直接显示成「镜像站 https://… → https://…」比 `Mirror …` 好读得多。
        if (string.Equals(key, nameof(Mirror), StringComparison.Ordinal))
        {
            return loc["Log.Setting.Mirror"];
        }

        // 主题：VM 上的属性叫 SelectedTheme，而存档字段叫 Theme ⇒ 这里用 AppSettings 的名字。
        // ⚠️ 认的是**字段名**而不是属性名，写错的话日志里会退回一串原始键名（不报错）。
        if (string.Equals(key, nameof(AppSettings.Theme), StringComparison.Ordinal))
        {
            return loc["Log.Setting.Theme"];
        }

        return key;
    }

    /// <summary>按 <see cref="ComponentKind"/> 名字找组件（设置键里存的就是它的 ToString）。</summary>
    private ComponentViewModel? FindComponent(string kindName) =>
        Components.FirstOrDefault(c => string.Equals(c.Kind.ToString(), kindName, StringComparison.Ordinal));

    /// <summary>
    /// 按槽键（<c>&lt;组件&gt;/&lt;槽 Key&gt;</c>）找出**插件名** —— 槽键本身是给机器看的，
    /// 日志里要的是「插件文件「DBI」」这种能对上界面的说法。
    /// 认不出来返回 <c>null</c>，由调用方回落到原键。
    /// </summary>
    private string? FindSlotGroup(string slotKey)
    {
        var slash = slotKey.IndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        var ownerKey = slotKey[..slash];

        // 组标题就是插件名（与界面上「插件文件」那一栏的标题同一个来源）。
        return AssetSlotGroups
            .FirstOrDefault(group => string.Equals(
                group.Definition.Kind.ToString(), ownerKey, StringComparison.Ordinal))
            ?.Title;
    }

    /// <summary>
    /// 静默落盘：调**唯一**那处 <see cref="SaveSettings"/>（同一份快照、同一个格式），
    /// 只是不刷屏 —— 自动写盘每 400ms 就可能来一次，逐条播报会把真日志淹掉。
    ///
    /// 「用户 2026-09-19 取消了『保存设置』按钮」之后，这里是 <see cref="SaveSettings"/> 的**唯一**调用者；
    /// 名字里的「Quietly」现在指的是「不额外播报」，而不是「相对按钮那条路」。
    /// </summary>
    private void SaveSettingsQuietly()
    {
        // ⚠️ 「这一轮通知其实没改动任何存档字段」时**不要写盘**。
        //
        //    触发自动落盘的不止用户操作：子视图模型刷新本地化文本（切语言、重建插件列表）
        //    也会发变更通知，而它们不改任何持久化的东西。不判断的话，**每次打开软件都会把
        //    settings.json 按一模一样的内容重写一遍**（实测：启动后约 1 秒写一次，内容逐字节相同）——
        //    于是文件时间戳不能再用来判断「这个值是谁写的」，而那正是排查问题时最省事的一条线索。
        //
        //    判据是「快照有没有变」，不是「有没有收到通知」：通知只说明**可能**变了。
        //    这里复用差异函数（纯函数）拿当前界面取值与上一次落盘的快照比 ——
        //    只影响自动落盘；「保存设置」按钮那条路（SaveSettings）不动，用户按了就该有反馈。
        var probe = SettingsStore.Load();
        MaterializeInto(probe);

        if (SettingsSnapshot.Diff(_lastSettingsSnapshot, SettingsSnapshot.Flatten(probe)).Count == 0)
        {
            return;
        }

        _autoSaving = true;
        try
        {
            SaveSettings();
        }
        finally
        {
            _autoSaving = false;
        }
    }

    /// <summary>
    /// 立刻落盘（不等待节流窗口）。关窗时由 <c>MainWindow</c> 调用 ——
    /// 少了它，「改完马上关掉」那一次改动会停在定时器里没写出去。
    /// </summary>
    internal void FlushSettings()
    {
        _autoSaveTimer.Stop();
        SaveSettingsQuietly();

        // 收尾行写在最后：它是对「这一次运行」的总结，排在所有设置变更之后才读得通。
        // 只写一次 —— 窗口若因为别的原因再触发一次 Closing，不该出现两条总结。
        if (!_sessionFooterWritten)
        {
            _sessionFooterWritten = true;
            LogSessionFooter();
        }
    }

    /// <summary>会话尾只写一次（见 <see cref="FlushSettings"/>）。</summary>
    private bool _sessionFooterWritten;

    // ── 集合与命令 ──────────────────────────────────────────────
    public ObservableCollection<ComponentViewModel> Components { get; }

    /// <summary>
    /// 组件区按分类分好组的视图（界面直接绑它，见 <see cref="ComponentCategoryViewModel"/>）。
    ///
    /// 由 <see cref="Components"/> 现算而来，**不另存一份组件列表** —— 两份列表迟早会不同步
    /// （比如某处直接往 Components 里加一项，分组里就看不见它）。
    /// 分类顺序取自 <see cref="ComponentCatalog.Categories"/>（即枚举声明序），不手写。
    ///
    /// ⚠️ 只读快照：构造完就不再变。将来若支持运行期增删组件，这里要跟着重建。
    /// </summary>
    public ObservableCollection<ComponentCategoryViewModel> ComponentGroups { get; }

    /// <summary>
    /// 高级设置里「插件文件」区：每个插件一组，每组里每个可下载文件一行（地址 + 文件名）。
    ///
    /// 同样**从 <see cref="ComponentCatalog.All"/> 现算**，不另存一份槽位清单。
    /// 只含 <see cref="ComponentDefinition.Slots"/> 非空的组件 —— 框架那四个走手写 picker，
    /// 它们的地址在「下载源」那一栏改（<see cref="RepoSources"/>）。
    /// </summary>
    public ObservableCollection<AssetSlotGroupViewModel> AssetSlotGroups { get; }

    public ObservableCollection<LanguageInfo> Languages { get; }

    /// <summary>主题下拉框的候选项（固定三项：跟随系统 / 浅色 / 暗夜）。</summary>
    public ObservableCollection<ThemeInfo> Themes { get; }

    public ObservableCollection<LogEntry> Logs { get; } = new();

    public RelayCommand StartCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand OpenOutputCommand { get; }

    public RelayCommand OpenDownloadCommand { get; }

    /// <summary>打开日志目录（<c>&lt;运行目录&gt;/logs</c>）。出问题时用户要能找到那个文件。</summary>
    public RelayCommand OpenLogsCommand { get; }

    /// <summary>日志目录路径。界面上显示出来，免得用户只能靠猜「日志写到哪儿了」。</summary>
    public string LogFolder => AppPaths.LogRoot;

    /// <summary>
    /// 日志目录的说明文字，含**当天**那个文件名。
    ///
    /// 与其它「按语言求值」的文本一样**现查**（不在构造里存快照）：切语言后要跟着变，
    /// 而且跨过零点之后文件名也会变 —— 存快照的话第二天点开还指着昨天的文件。
    /// </summary>
    public string LogFolderHint => string.Format(
        LocalizationService.Instance["App.Logs.Hint"],
        LogFile.CurrentFilePath);

    public RelayCommand ClearLogCommand { get; }

    public RelayCommand ToggleAdvancedCommand { get; }

    /// <summary>把 out/ 打包成 zip（手动触发）。</summary>
    public RelayCommand PackZipCommand { get; }

    /// <summary>打开 GitHub 的「新建 Token」页面（预填用途说明），省得用户自己找入口。</summary>
    public RelayCommand OpenTokenPageCommand { get; }

    /// <summary>用当前填的 Token 调一次 API，确认它真的可用并显示剩余配额。</summary>
    public RelayCommand VerifyTokenCommand { get; }

    /// <summary>
    /// 检查更新：查最新 Release → 与当前版本比较 → 有新版就下载、启动它、退出旧版。
    /// </summary>
    public RelayCommand CheckUpdateCommand { get; }

    /// <summary>打开「Cloudflare 搭建镜像站」的说明页（用系统浏览器）。</summary>
    public RelayCommand OpenMirrorGuideCommand { get; }

    /// <summary>把所有下载源恢复成内置默认地址。</summary>
    public RelayCommand ResetRepoSourcesCommand { get; }

    /// <summary>把所有插件文件的地址与文件名恢复成内置默认值。</summary>
    public RelayCommand ResetAssetSlotsCommand { get; }

    public RelayCommand PickStockLogoCommand { get; }

    public RelayCommand PickSysNandLogoCommand { get; }

    public RelayCommand PickEmuNandLogoCommand { get; }

    public RelayCommand ClearStockLogoCommand { get; }

    public RelayCommand ClearSysNandLogoCommand { get; }

    public RelayCommand ClearEmuNandLogoCommand { get; }

    public RelayCommand PickStockIconCommand { get; }

    public RelayCommand PickSysNandIconCommand { get; }

    public RelayCommand PickEmuNandIconCommand { get; }

    public RelayCommand ClearStockIconCommand { get; }

    public RelayCommand ClearSysNandIconCommand { get; }

    public RelayCommand ClearEmuNandIconCommand { get; }

    // ── 语言 ────────────────────────────────────────────────────
    private LanguageInfo? _selectedLanguage;

    /// <summary>
    /// 主题档位。与 <see cref="SelectedLanguage"/> 同构：setter 里立刻把主题应用到界面，
    /// 而**不写存档**（落盘只走 MaterializeInto + 自动落盘那一层）。
    /// </summary>
    private ThemeInfo? _selectedTheme;

    /// <summary>
    /// 当前界面语言。改它会立刻切换界面文案，并让自动落盘把语言写进存档。
    ///
    /// ⚠️ 这里**刻意不写盘**（曾经写过一次 <c>SettingsStore.Save(_settings)</c>）。三笔账：
    /// <list type="number">
    ///   <item>**它会「启动即写」**：构造函数末尾要把它读回成存档里的语言（<c>_selectedLanguage</c>
    ///     从 null 变成实值），于是每次打开软件都会写一次 <c>settings.json</c> ——
    ///     而「启动即写」正是 <c>_autoSaveArmed</c> 那道闸要防的事，只是从这条侧路漏了过去。
    ///     后果是**文件时间戳不再能说明「值是谁写的」**，而这恰恰是排查问题时最常用的一条线索。</item>
    ///   <item>**它写的是没映射过的快照**：<c>_settings.Components</c> / <c>OptionValues</c> 要等到
    ///     第一次 <see cref="SaveSettings"/> 才被填满，所以首次运行切换语言会把存档写成
    ///     <c>"Components": {}</c> —— 而**空 Components 在别处是有语义的**（见
    ///     <see cref="EnsureComponentsSelected"/>：它正是「用户从没勾过 ⇒ 命令行默认全选」的判据）。</item>
    ///   <item>**它违反「只有一个搬运点」**：语言本来就是设置项之一，统一落盘层
    ///     （<see cref="MaterializeInto"/> + 自动落盘）已经覆盖它。两处都写，迟早分叉。</item>
    /// </list>
    /// 实测这条侧路是靠「回归跑完 settings.json 应与开跑前逐字节相同」那条幂等护栏抓出来的 ——
    /// 它只会报「多出来一个文件」，看不出是谁写的，所以又加了按小节定位的探针。
    /// </summary>
    public LanguageInfo? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetProperty(ref _selectedLanguage, value) && value is not null)
            {
                // 立刻生效的是**界面**（这条不能等落盘）。落盘交给自动落盘那一层 ——
                // 它读的语言是 LocalizationService 的当前语言（见 MaterializeInto），
                // 所以这里连 _settings.Language 都不必碰：**属性 setter 一律不写存档**，
                // 这条不变式由护栏 CheckPropertySettersDoNotWriteSettings 钉住。
                LocalizationService.Instance.SetLanguage(value.Code);
                RefreshAutobootChoices();
            }
        }
    }

    /// <summary>
    /// 主题档位。setter 里**立刻把主题应用到界面** —— 用户拖一下下拉框就该看见颜色变了，
    /// 这条不能等落盘那 400ms。落盘交给自动落盘那一层：**属性 setter 一律不写存档**
    /// （这条不变式由护栏 CheckPropertySettersDoNotWriteSettings 钉住）。
    /// </summary>
    public ThemeInfo? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value) && value is not null)
            {
                ThemeService.Apply(value.Mode);
            }
        }
    }

    // ── 全局选项 ────────────────────────────────────────────────
    private bool _bootStock;
    private bool _bootSysNand;
    private bool _bootEmuNand;

    // 引导项显示名与启动图（留空 = 用默认名 / 不设启动图）
    private string _bootStockTitle = string.Empty;
    private string _bootSysNandTitle = string.Empty;
    private string _bootEmuNandTitle = string.Empty;
    private string _bootStockLogo = string.Empty;
    private string _bootSysNandLogo = string.Empty;
    private string _bootEmuNandLogo = string.Empty;
    private string _bootStockIcon = string.Empty;
    private string _bootSysNandIcon = string.Empty;
    private string _bootEmuNandIcon = string.Empty;

    private bool _blankSerialSysmmc;
    private bool _blankSerialEmummc;

    /// <summary>
    /// 重入保护：**只要是程序自己**去勾 Sys-patch，就不要再去「默认勾上屏蔽序列号」。
    ///
    /// 两个地方需要它：
    /// ① 屏蔽序列号 → 强制勾选 Sys-patch（<see cref="UpdateForcedSelections"/>）；
    /// ② 首次运行的默认全选（<see cref="EnsureComponentsSelected"/>）。
    ///
    /// 少了它会出现连锁：勾一个屏蔽序列号 → Sys-patch 被自动勾上 → 触发默认逻辑
    /// → 另一个屏蔽序列号也被勾上。那等于「勾一个就等于两个都勾」，直接废掉
    /// 「想屏蔽哪个就屏蔽哪个」这个需求。
    ///
    /// 情形②的后果同样静默：用户从没勾过「屏蔽序列号」，生成的 exosphere.ini 里却写进了
    /// <c>blank_prodinfo_*=1</c>，还会把 Sys-patch 锁死。
    /// </summary>
    private bool _applyingForcedSelections;
    private bool _use90DnsSysmmc;
    private bool _use90DnsEmummc;

    /// <summary>
    /// 正在由 90DNS ↔ <c>enable_dns_mitm</c> 的联动写值。两侧的 setter 都会回头看对方，
    /// 少了这道闸就会互相触发（勾一个 90DNS → 打开 mitm → 反向又去关 90DNS）。
    /// </summary>
    private bool _applyingDnsMitmCoupling;

    private bool _ram8Gb;
    private bool _preferPrerelease;
    private bool _includeComponentFilesInOutput;
    private bool _includePayloads = true;
    private bool _includeBootDat;

    public bool BootStock
    {
        get => _bootStock;
        set
        {
            if (SetProperty(ref _bootStock, value))
            {
                OnBootEntriesChanged();
            }
        }
    }

    public bool BootSysNand
    {
        get => _bootSysNand;
        set
        {
            if (SetProperty(ref _bootSysNand, value))
            {
                OnBootEntriesChanged();
            }
        }
    }

    public bool BootEmuNand
    {
        get => _bootEmuNand;
        set
        {
            if (SetProperty(ref _bootEmuNand, value))
            {
                OnBootEntriesChanged();
            }
        }
    }

    // ── 引导项显示名（用户可改，留空用默认名）────────────────────
    /// <summary>
    /// 正版系统在 hekate 启动菜单里的显示名。留空 = 用界面语言的默认名（<c>Boot.Entry.Stock</c>）。
    /// 这串字只影响菜单显示，不影响引导行为。
    /// </summary>
    public string BootStockTitle
    {
        get => _bootStockTitle;
        set
        {
            if (SetProperty(ref _bootStockTitle, value ?? string.Empty))
            {
                // 改了名字，autoboot 下拉框里那一项也要跟着变 —— 否则用户改完名字，
                // 「自动进入」里还是旧名字，看起来像没生效。
                RefreshAutobootChoices();
            }
        }
    }

    public string BootSysNandTitle
    {
        get => _bootSysNandTitle;
        set
        {
            if (SetProperty(ref _bootSysNandTitle, value ?? string.Empty))
            {
                RefreshAutobootChoices();
            }
        }
    }

    public string BootEmuNandTitle
    {
        get => _bootEmuNandTitle;
        set
        {
            if (SetProperty(ref _bootEmuNandTitle, value ?? string.Empty))
            {
                RefreshAutobootChoices();
            }
        }
    }

    // ── 引导项启动图（用户指定的本地图片）────────────────────────
    /// <summary>
    /// 正版系统的启动图（本地图片绝对路径）。留空 = 不设置。
    /// 生成时会被复制进 <c>bootloader/res/</c>，并在该引导项写 <c>logopath=bootloader/res/&lt;文件名&gt;</c>。
    /// </summary>
    public string BootStockLogo
    {
        get => _bootStockLogo;
        set
        {
            if (SetProperty(ref _bootStockLogo, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasBootStockLogo));
            }
        }
    }

    public string BootSysNandLogo
    {
        get => _bootSysNandLogo;
        set
        {
            if (SetProperty(ref _bootSysNandLogo, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasBootSysNandLogo));
            }
        }
    }

    public string BootEmuNandLogo
    {
        get => _bootEmuNandLogo;
        set
        {
            if (SetProperty(ref _bootEmuNandLogo, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasBootEmuNandLogo));
            }
        }
    }

    /// <summary>是否已选启动图（决定「清除」按钮要不要出现）。</summary>
    public bool HasBootStockLogo => !string.IsNullOrWhiteSpace(BootStockLogo);

    public bool HasBootSysNandLogo => !string.IsNullOrWhiteSpace(BootSysNandLogo);

    public bool HasBootEmuNandLogo => !string.IsNullOrWhiteSpace(BootEmuNandLogo);

    // ── 引导项图标（Nyx 菜单里的小图标，与启动图是同一族的引导项级键）──
    /// <summary>
    /// 正版系统的图标（本地图片绝对路径）。留空 = 不设置。
    /// 生成时会被复制进 <c>bootloader/res/</c>，并在该引导项写 <c>icon=bootloader/res/&lt;文件名&gt;</c>。
    /// </summary>
    public string BootStockIcon
    {
        get => _bootStockIcon;
        set
        {
            if (SetProperty(ref _bootStockIcon, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasBootStockIcon));
            }
        }
    }

    public string BootSysNandIcon
    {
        get => _bootSysNandIcon;
        set
        {
            if (SetProperty(ref _bootSysNandIcon, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasBootSysNandIcon));
            }
        }
    }

    public string BootEmuNandIcon
    {
        get => _bootEmuNandIcon;
        set
        {
            if (SetProperty(ref _bootEmuNandIcon, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasBootEmuNandIcon));
            }
        }
    }

    /// <summary>是否已选图标（决定「清除」按钮要不要出现）。</summary>
    public bool HasBootStockIcon => !string.IsNullOrWhiteSpace(BootStockIcon);

    public bool HasBootSysNandIcon => !string.IsNullOrWhiteSpace(BootSysNandIcon);

    public bool HasBootEmuNandIcon => !string.IsNullOrWhiteSpace(BootEmuNandIcon);

    /// <summary>屏蔽真实破解系统（sysMMC）的序列号。与 emuMMC 那项互相独立。</summary>
    public bool BlankSerialSysmmc
    {
        get => _blankSerialSysmmc;
        set
        {
            if (SetProperty(ref _blankSerialSysmmc, value))
            {
                OnPropertyChanged(nameof(SysPatchForced));
                UpdateForcedSelections();
            }
        }
    }

    /// <summary>屏蔽虚拟破解系统（emuMMC）的序列号。与 sysMMC 那项互相独立。</summary>
    public bool BlankSerialEmummc
    {
        get => _blankSerialEmummc;
        set
        {
            if (SetProperty(ref _blankSerialEmummc, value))
            {
                OnPropertyChanged(nameof(SysPatchForced));
                UpdateForcedSelections();
            }
        }
    }

    /// <summary>
    /// 为真实破解系统（sysMMC）启用 90DNS → 写 <c>atmosphere/hosts/default.txt</c> + <c>sysmmc.txt</c>。
    ///
    /// 与 emuMMC 那项**互相独立**，且与屏蔽序列号同一套「跟着对应引导模式」的规矩：
    /// 只在勾了真实破解系统时才有意义（<see cref="ShowSysmmcDns"/>），也只有那时才写文件。
    /// </summary>
    public bool Use90DnsSysmmc
    {
        get => _use90DnsSysmmc;
        set
        {
            if (SetProperty(ref _use90DnsSysmmc, value))
            {
                ApplyDnsMitmCoupling();
            }
        }
    }

    /// <summary>
    /// 为虚拟破解系统（emuMMC）启用 90DNS → 写 <c>atmosphere/hosts/default.txt</c> + <c>emummc.txt</c>。
    /// **默认关闭**（同 <see cref="Use90DnsSysmmc"/>）。
    /// </summary>
    public bool Use90DnsEmummc
    {
        get => _use90DnsEmummc;
        set
        {
            if (SetProperty(ref _use90DnsEmummc, value))
            {
                ApplyDnsMitmCoupling();
            }
        }
    }

    public bool Ram8Gb
    {
        get => _ram8Gb;
        set => SetProperty(ref _ram8Gb, value);
    }

    public bool PreferPrerelease
    {
        get => _preferPrerelease;
        set => SetProperty(ref _preferPrerelease, value);
    }

    public bool IncludeComponentFilesInOutput
    {
        get => _includeComponentFilesInOutput;
        set => SetProperty(ref _includeComponentFilesInOutput, value);
    }

    /// <summary>是否把 payload 文件（fusee.bin / hekate 引导 payload）也放进 out 根目录。</summary>
    public bool IncludePayloads
    {
        get => _includePayloads;
        set => SetProperty(ref _includePayloads, value);
    }

    /// <summary>
    /// 是否把 boot.dat（SX GEAR 引导文件）写到 out 根目录。
    /// 它不再内嵌在程序里，改为下载 —— 地址见 <see cref="BootDatUrl"/>。
    /// **默认不勾选**（用户 2026-09-21 要求）。
    /// </summary>
    public bool IncludeBootDat
    {
        get => _includeBootDat;
        set
        {
            if (SetProperty(ref _includeBootDat, value))
            {
                // 卡片上那行文字在「未勾选 / 就绪」之间切换，所以要跟着刷新。
                OnPropertyChanged(nameof(BootDatStatusText));
            }
        }
    }

    /// <summary>
    /// boot.dat 的下载进度（0–100）与状态文字，显示在「框架」那张 boots.dat 卡片上。
    ///
    /// 与组件卡片里的资源行是同一套东西（下载时才有内容）—— 但 boot.dat 不是组件、没有卡片可挂，
    /// 所以这几个属性直接挂在主视图模型上。用户 2026-09-21 要求「boot.dat 也要有下载进度条」。
    /// ⚠️ 全是**运行时**属性：已进 <see cref="TransientPropertyNames"/>，不会落盘。
    /// </summary>
    public double BootDatProgress
    {
        get => _bootDatProgress;
        private set => SetProperty(ref _bootDatProgress, value);
    }

    public string BootDatStatus
    {
        get => _bootDatStatus;
        private set
        {
            if (SetProperty(ref _bootDatStatus, value))
            {
                OnPropertyChanged(nameof(BootDatStatusText));
            }
        }
    }

    /// <summary>
    /// 卡片上显示的那一行文字。**空状态不返回空串**：进度条现在**始终**显示（用户 2026-09-22 要求），
    /// 上面那行若是空的，卡片看上去就像坏了 —— 所以没下过时明确写「未勾选 / 就绪」。
    ///
    /// ⚠️ 「就绪」用的就是组件卡片那一套（<c>Common.Ready</c>，见 <see cref="ComponentViewModel"/>）：
    /// 勾上了、只等按下开始 —— 说「等待中」会让人以为它已经在跑了（用户 2026-09-22 要求改）。
    ///
    /// 这两种兜底文案跟着 <see cref="IncludeBootDat"/> 走，切语言时由 <see cref="RefreshLocalizedText"/> 刷新。
    /// </summary>
    public string BootDatStatusText =>
        string.IsNullOrWhiteSpace(BootDatStatus)
            ? LocalizationService.Instance[IncludeBootDat ? "Common.Ready" : "Common.NotSelected"]
            : BootDatStatus;

    /// <summary>
    /// boot.dat 的下载地址。**留空 = 用默认地址**（与「未填写时按默认地址下载」这条要求对应）。
    ///
    /// 红字提示是**派生**的（<see cref="BootDatUrlIsInvalid"/>）：空着是默认状态、不该红，
    /// 只有「填了但解析不出可用地址」才提示 —— 与 <see cref="Mirror"/> 同一套做法。
    /// </summary>
    public string BootDatUrl
    {
        get => _bootDatUrl;
        set
        {
            if (SetProperty(ref _bootDatUrl, value))
            {
                OnPropertyChanged(nameof(BootDatUrlIsInvalid));
                OnPropertyChanged(nameof(BootDatUrlIsEmpty));
            }
        }
    }

    /// <summary>填了 boot.dat 地址但解析不出可用地址（见 <see cref="BootDatSource.IsAcceptable"/>）。</summary>
    public bool BootDatUrlIsInvalid => !BootDatSource.IsAcceptable(BootDatUrl);

    /// <summary>
    /// 地址框是不是空的 —— 决定框里那句「可以填成什么样」的提示要不要显示。
    ///
    /// 为什么不直接 `ElementName` 绑 TextBox 的 `Text`：`CheckXamlBindingsResolve` 要求每个
    /// `{Binding X}` 的 X 是**本程序集**里某个类型的公开属性，而 `Text` 是 WPF 控件的属性
    /// （本程序集没有同名属性）⇒ 那样写会让护栏报「绑定名不存在」。
    /// 走派生属性既过护栏，也与 <see cref="MirrorIsInvalid"/> / <see cref="HasTokenStatus"/> 同一套做法。
    /// </summary>
    public bool BootDatUrlIsEmpty => string.IsNullOrWhiteSpace(BootDatUrl);

    /// <summary>
    /// 「检查更新」查版本用的地址。**留空 = 用默认地址**。
    /// 与 <see cref="BootDatUrl"/> 同构：存原话、用时推导、坏值就地标红。
    /// </summary>
    public string UpdateUrl
    {
        get => _updateUrl;
        set
        {
            if (SetProperty(ref _updateUrl, value))
            {
                OnPropertyChanged(nameof(UpdateUrlIsInvalid));
                OnPropertyChanged(nameof(UpdateUrlIsEmpty));
            }
        }
    }

    /// <summary>填了更新地址但推不出仓库（见 <see cref="UpdateSource.IsAcceptable"/>）。</summary>
    public bool UpdateUrlIsInvalid => !UpdateSource.IsAcceptable(UpdateUrl);

    /// <summary>更新地址框是不是空的（同 <see cref="BootDatUrlIsEmpty"/>）。</summary>
    public bool UpdateUrlIsEmpty => string.IsNullOrWhiteSpace(UpdateUrl);

    /// <summary>
    /// 生成完成后是否自动把 out/ 打包成 zip（方便整体拷进 SD 卡）。
    ///
    /// ⚠️ 与 <see cref="SelectedLanguage"/> 同理，这里**不写盘**：它是设置项之一，
    /// 由统一落盘层负责，点写会写出一份没映射过的快照（理由见那边的注释）。
    /// 生成时读的是**这个属性**（<c>if (AutoPackZip)</c>），不依赖「有没有落盘」，
    /// 所以去掉那次点写不会让「改了不生效」。
    /// </summary>
    public bool AutoPackZip
    {
        get => _autoPackZip;
        set
        {
            // 同样不碰 _settings：它是「界面取值 → 存档」那唯一一个搬运点（MaterializeInto）的事。
            SetProperty(ref _autoPackZip, value);
        }
    }

    /// <summary>勾选了真实破解或虚拟破解后才显示屏蔽序列号 / 90DNS 这一组。</summary>
    public bool ShowSerialOptions => BootSysNand || BootEmuNand;

    /// <summary>屏蔽真实破解系统的序列号只在勾了「真实破解系统」时才有意义，所以跟着它显示。</summary>
    public bool ShowSysmmcBlank => BootSysNand;

    /// <summary>屏蔽虚拟破解系统的序列号只在勾了「虚拟破解系统」时才有意义，所以跟着它显示。</summary>
    public bool ShowEmummcBlank => BootEmuNand;

    /// <summary>真实破解系统的 90DNS 只在勾了「真实破解系统」时才有意义，所以跟着它显示。</summary>
    public bool ShowSysmmcDns => BootSysNand;

    /// <summary>虚拟破解系统的 90DNS 只在勾了「虚拟破解系统」时才有意义，所以跟着它显示。</summary>
    public bool ShowEmummcDns => BootEmuNand;

    /// <summary>
    /// 任意一个屏蔽序列号被勾选时，Sys-patch 必须强制勾选并锁定。
    ///
    /// 原因：屏蔽序列号会写 `exosphere.ini` 的 `blank_prodinfo_*`，把 prodinfo 里的
    /// 序列号/校准数据抹掉，证书随之失效，系统会拒绝正常启动——必须靠 Sys-patch
    /// 在运行期补上签名校验才走得通。
    ///
    /// 两个屏蔽项**各自独立**，任一个成立就触发（`||`，不是 `&&`）。
    ///
    /// 每一项都叠加了对应的引导模式条件，这一层不能省：
    /// `ConfigGenerator` 只在对应引导模式勾选时才把 `blank_prodinfo_*` 写成 1，
    /// 而且复选框本身也只在那时才显示。少了它就会出现
    /// 「复选框已经藏起来、Sys-patch 却被锁死」的死局——用户找不到能解锁的那个勾。
    /// </summary>
    public bool SysPatchForced =>
        (BlankSerialSysmmc && BootSysNand) || (BlankSerialEmummc && BootEmuNand);

    public string OutputPath => _outputPath;

    public string DownloadPath => _downloadPath;

    // ── 高级设置 ────────────────────────────────────────────────
    private string _gitHubToken;
    private int _timeoutSeconds;
    private string _proxy;
    private string _mirror;
    private string _bootDatUrl;
    private string _updateUrl;

    public bool ShowAdvancedSettings
    {
        get => _showAdvancedSettings;
        set => SetProperty(ref _showAdvancedSettings, value);
    }

    public string GitHubToken
    {
        get => _gitHubToken;
        set => SetProperty(ref _gitHubToken, value);
    }

    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set => SetProperty(ref _timeoutSeconds, value);
    }

    public string Proxy
    {
        get => _proxy;
        set => SetProperty(ref _proxy, value);
    }

    /// <summary>
    /// GitHub 镜像站地址。**留空 = 走官方地址**（默认状态，也是用户要的「未填写时照原 GitHub 地址」）。
    ///
    /// 用法**故意分两种**（见 <see cref="Services.GitHubMirror"/>）：**下载**填了就直接走它，
    /// **版本查询/列目录**先直连官方、只有连不上才改走它（镜像出口 IP 公用，未登录的 API 额度按 IP 算）。
    /// 填错不会更糟：规范化不出来就当成没配，整条退回官方地址。
    /// </summary>
    public string Mirror
    {
        get => _mirror;
        set
        {
            if (SetProperty(ref _mirror, value))
            {
                // 红字提示是**派生**的：填了但拼不出可用根才提示（空着是默认状态，不该红）。
                OnPropertyChanged(nameof(MirrorIsInvalid));
            }
        }
    }

    /// <summary>
    /// 填了镜像站但解析不出可用地址（见 <see cref="Services.GitHubMirror.IsAcceptable"/>）。
    ///
    /// 判据直接问那一处规范化，**不在这里另写一份校验**：两份迟早分叉，而症状是
    /// 「界面不报红、下载却全 404」—— 最难查的一种。
    /// </summary>
    public bool MirrorIsInvalid => !Services.GitHubMirror.IsAcceptable(Mirror);

    private string _tokenStatus = string.Empty;
    private bool _isVerifyingToken;
    private string _updateStatus = string.Empty;
    private bool _isCheckingUpdate;
    private double _updateProgress;
    private bool _isUpdateDownloading;
    private double _bootDatProgress;
    private string _bootDatStatus = string.Empty;

    /// <summary>
    /// 「验证 Token」的结果文案（显示登录名与剩余配额）。空 = 还没验证过。
    /// </summary>
    public string TokenStatus
    {
        get => _tokenStatus;
        private set
        {
            if (SetProperty(ref _tokenStatus, value))
            {
                OnPropertyChanged(nameof(HasTokenStatus));
            }
        }
    }

    public bool HasTokenStatus => !string.IsNullOrWhiteSpace(TokenStatus);

    public bool IsVerifyingToken
    {
        get => _isVerifyingToken;
        private set => SetProperty(ref _isVerifyingToken, value);
    }

    /// <summary>
    /// 「检查更新」的结果文本（与 <see cref="TokenStatus"/> 同一套做法：就地显示结论，
    /// 不是只往日志里写 —— 用户点这个按钮就是为了当场知道答案）。
    /// </summary>
    public string UpdateStatus
    {
        get => _updateStatus;
        private set
        {
            if (SetProperty(ref _updateStatus, value))
            {
                OnPropertyChanged(nameof(HasUpdateStatus));
            }
        }
    }

    public bool HasUpdateStatus => !string.IsNullOrWhiteSpace(UpdateStatus);

    /// <summary>正在查 / 正在下新版。期间按钮灰掉，避免连点两次下载同一个包。</summary>
    public bool IsCheckingUpdate
    {
        get => _isCheckingUpdate;
        private set => SetProperty(ref _isCheckingUpdate, value);
    }

    /// <summary>
    /// 下载新版的进度（0–100）。用户 2026-09-22 要求「在版本更新的后方增加下载百分比」。
    ///
    /// 与 <see cref="UpdateStatus"/> **分开**：状态文字会整句写进日志（一条干净的结论），
    /// 而百分比是高频变化的界面数字，混进日志只会把它刷满。
    /// </summary>
    public double UpdateProgress
    {
        get => _updateProgress;
        private set
        {
            if (SetProperty(ref _updateProgress, value))
            {
                OnPropertyChanged(nameof(UpdateProgressText));
            }
        }
    }

    /// <summary>结论后面那个百分比（含 % 号，免得 XAML 里再套 StringFormat 去转义）。</summary>
    public string UpdateProgressText => $"{UpdateProgress:0}%";

    /// <summary>正在下载新版 —— 百分比只在这一段时间里显示（查完发现已是最新版时不该留个「0%」）。</summary>
    public bool IsUpdateDownloading
    {
        get => _isUpdateDownloading;
        private set => SetProperty(ref _isUpdateDownloading, value);
    }

    /// <summary>
    /// 当前程序版本（界面上显示在按钮旁边）。取的是**程序集版本** ——
    /// 与「检查更新」比较用的、以及日志会话头里记的，是同一个来源。
    /// </summary>
    public string CurrentVersionText =>
        UpdateSource.Format(typeof(MainViewModel).Assembly.GetName().Version);

    /// <summary>下载源列表（高级设置里逐项可改）。</summary>
    public ObservableCollection<RepoSourceViewModel> RepoSources { get; } = new();

    // ── 运行状态 ────────────────────────────────────────────────
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanStart));
                StartCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                PackZipCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanStart => !IsRunning && Components.Any(c => c.IsSelected);

    /// <summary>
    /// 是否显示「请至少勾选一个组件」的提示。
    ///
    /// 首次运行默认**不勾任何组件**，此时「开始」按钮是灰的 —— 用户点不动，也就永远看不到
    /// <see cref="RunAsync"/> 里那句 <c>Common.NoSelection</c>，只能对着灰按钮猜为什么点不了。
    /// 所以把同一句话直接摆在组件列表上，勾上任意一个就消失。
    /// </summary>
    public bool ShowNoSelectionHint => !Components.Any(c => c.IsSelected);

    public bool OverallIsIndeterminate
    {
        get => _overallIsIndeterminate;
        private set => SetProperty(ref _overallIsIndeterminate, value);
    }

    public double OverallProgress
    {
        get => _overallProgress;
        private set => SetProperty(ref _overallProgress, value);
    }

    public string OverallStatus
    {
        get => _overallStatus;
        private set => SetProperty(ref _overallStatus, value);
    }

    // ── 主流程 ──────────────────────────────────────────────────
    /// <summary>
    /// 取回 <c>boot.dat</c>（不再内嵌在程序里）。返回本地路径；没勾或没取到都返回 <c>null</c>。
    ///
    /// 走的是 <see cref="DownloadService"/>，所以**自动吃到两件事**：
    /// ① 镜像站（填了就变成从镜像下）；② 备用通道 —— <c>raw.githubusercontent.com</c>
    /// 在部分网络下被整段阻断（本沙箱就是，DNS 都解析不出来），那时改走 <c>api.github.com</c>
    /// 的 contents 端点（见 <see cref="BootDatSource"/>）。
    ///
    /// ⚠️ 失败**只警告、不中止**：它是可选文件，为它放弃其余组件的产物不划算。
    /// 但警告里必须写明原因，否则用户只知道「out 里少了 boot.dat」，不知道该改哪里。
    /// </summary>
    private async Task<string?> PrepareBootDatAsync(WizardOptions options, CancellationToken token)
    {
        var loc = LocalizationService.Instance;

        if (!options.IncludeBootDat)
        {
            // 没勾就把上一次的状态清掉：否则卡片上会一直留着「已下载到 …」，
            // 而这一轮根本没打算要它 —— 那种残留最容易让人以为「它自己下了」。
            ClearBootDatStatus();
            return null;
        }

        var plan = BootDatSource.Resolve(BootDatUrl);
        if (plan is null)
        {
            ClearBootDatStatus();

            // 用界面那行红字的**同一句**文案：同一个问题在两处说成两样最让人困惑。
            // 刻意不做 string.Format —— 这句文案在 XAML 里是静态显示的，带占位符的话
            // 界面上会原样显示「{0}」；用户填的那个值由上面的「设置变更」日志如实记录。
            Warn(loc["Settings.BootDatUrl.Invalid"]);
            return null;
        }

        // 落点：download/boot/boot.dat（用户要求放进 boot 文件夹）。
        // DownloadService 会自己 EnsureDirectory，所以这里不必先建目录。
        var target = Path.Combine(AppPaths.BootDatRoot, BootDatSource.FileName);

        try
        {
            BootDatProgress = 0;
            BootDatStatus = loc["Common.Downloading"];

            Log(LogLevel.Info, loc["Log.BootDatBegin"]);

            // 与其余下载点同一套做法：印**实际会请求**的地址，而它与 DownloadService 里那次改写
            // 调的是**同一个纯函数、同样的入参** ⇒ 不可能印错（印直链会把「镜像挂了」读成「GitHub 挂了」）。
            Log(LogLevel.Info, "  " + Services.GitHubMirror.Apply(plan.DownloadUrl, Mirror));

            if (plan.Fallback is not null)
            {
                // 备用通道也印出来：它同样会被镜像改写，而「主通道挂了之后到底去请求了哪里」
                // 正是排查时最想知道的一件事。
                Log(LogLevel.Info, "  " + Services.GitHubMirror.Apply(plan.Fallback.Url, Mirror));
            }

            // 进度只喂给卡片上那一行（不参与总的加权进度：一个 11 KB 的文件混进几百 MB 的
            // 权重里只会让进度条多跳一下，见调用点的注释）。
            var progress = new Progress<DownloadProgress>(p =>
            {
                BootDatProgress = p.Percent;
                BootDatStatus = $"{p.ProgressText} · {p.SpeedText}"
                    + (string.IsNullOrEmpty(p.RemainingText) ? string.Empty : $" · {p.RemainingText}");
            });

            await _download!.DownloadAsync(
                plan.DownloadUrl,
                target,
                progress,
                token,
                retry =>
                {
                    if (retry.IsChannelSwitch)
                    {
                        Log(LogLevel.Warning, string.Format(
                            loc["Log.DownloadFallback"], BootDatSource.FileName, retry.Reason));
                        return;
                    }

                    Log(LogLevel.Warning, string.Format(
                        retry.IsFallback ? loc["Log.DownloadRetryFallback"] : loc["Log.DownloadRetry"],
                        BootDatSource.FileName,
                        retry.Reason,
                        retry.Delay.TotalSeconds,
                        retry.Attempt,
                        retry.MaxAttempts));
                },
                plan.Fallback);

            BootDatProgress = 100;
            BootDatStatus = loc["Common.Done"];
            Log(LogLevel.Success, string.Format(loc["Log.BootDatDownloaded"], target));
            return target;
        }
        catch (OperationCanceledException)
        {
            ClearBootDatStatus();
            throw;
        }
        catch (Exception ex)
        {
            BootDatProgress = 0;
            BootDatStatus = loc["Common.Failed"];
            Warn(string.Format(loc["Log.BootDatFailed"], NetworkErrors.Describe(ex)));
            return null;
        }
    }

    /// <summary>
    /// 把卡片上的下载状态清空（没勾 / 地址坏 / 被取消时）。
    /// 留着上一次的「已下载到 …」比不显示更坏：用户会以为这一轮也下过了。
    /// </summary>
    private void ClearBootDatStatus()
    {
        BootDatProgress = 0;
        BootDatStatus = string.Empty;
    }

    private async Task RunAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var loc = LocalizationService.Instance;
        var options = BuildWizardOptions();

        if (!options.HasAnyComponentSelected)
        {
            Warn(loc["Common.NoSelection"]);
            return;
        }

        if (options.Hekate && !options.HasAnyBootEntry)
        {
            Warn(loc["Global.BootRequired"]);
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsRunning = true;
        OverallProgress = 0;
        OverallIsIndeterminate = false;
        OverallStatus = loc["Common.Resolving"];
        Logs.Clear();

        foreach (var component in Components)
        {
            component.ResetProgress();
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var selected = Components.Where(c => c.IsSelected).ToList();

            // ── 阶段 1：解析 Release ────────────────────────────────
            var jobs = new List<ComponentJob>();

            for (var i = 0; i < selected.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var component = selected[i];
                component.IsBusy = true;
                component.Status = loc["Common.Resolving"];
                OverallStatus = $"{loc["Common.Resolving"]} {component.Title}";
                OverallProgress = ResolvePhaseEnd * i / Math.Max(1, selected.Count);

                var job = await ResolveComponentAsync(component, options, token);
                jobs.Add(job);
            }

            OverallProgress = ResolvePhaseEnd;

            if (jobs.Count == 0)
            {
                Warn(loc["Common.NoSelection"]);
                return;
            }

            // ── 阶段 2：下载 + 解压 ────────────────────────────────
            var totalWeight = jobs.Sum(j => j.TotalWeight);
            var doneWeight = 0d;

            void UpdateOverall(double extra, string status)
            {
                OverallStatus = status;
                OverallProgress = totalWeight <= 0
                    ? ResolvePhaseEnd
                    : ResolvePhaseEnd + (ConfigPhaseStart - ResolvePhaseEnd) * Math.Clamp((doneWeight + extra) / totalWeight, 0, 1);
            }

            var failures = new List<string>();

            foreach (var job in jobs)
            {
                token.ThrowIfCancellationRequested();
                var component = job.Component;
                var componentDone = 0d;

                try
                {
                    foreach (var item in job.Items)
                    {
                        token.ThrowIfCancellationRequested();

                        var asset = new AssetViewModel(
                            item.AssetId, item.Pick.FileName, item.Repo.DisplayName, item.Pick.Asset.SizeText);

                        component.Assets.Add(asset);
                        asset.IsActive = true;
                        asset.Status = loc["Common.Downloading"];
                        component.Status = loc["Common.Downloading"];

                        if (item.PreDownloaded)
                        {
                            // 目录槽的快路径已经把这个文件解出来了，文件名与「逐个下载」**完全一致**。
                            // 不再下载，但**仍然记账** —— 少记一笔，组件进度条就会永远差一小截，
                            // 用户看到的是「进度没满却已完成」，而这类不齐是没人会去查的。
                            Log(LogLevel.Info, string.Format(
                                loc["Log.RepoBundleReuse"], item.Pick.FileName, item.DestinationPath));

                            asset.IsActive = false;
                            asset.IsDone = true;
                            asset.Progress = 100;
                            asset.Status = loc["Common.Done"];

                            componentDone += item.Weight;
                            doneWeight += item.Weight;
                            UpdateOverall(0, $"{component.Title} · {loc["Common.Downloading"]}");
                            continue;
                        }

                        Log(LogLevel.Info, string.Format(loc["Log.DownloadBegin"], $"{item.Repo.DisplayName} → {item.Pick.FileName}"));

                        // 打印**实际会请求**的那个地址（走镜像时与直链不同）。
                        // 直接印直链的话，日志里就是一条谁也没请求过的地址 ——
                        // 「镜像挂了」会被读成「GitHub 挂了」，方向全错。
                        // 与 DownloadService 里那次改写调的是**同一个纯函数**、同样的入参 ⇒ 不可能印错。
                        Log(LogLevel.Info, "  " + Services.GitHubMirror.Apply(item.Pick.Asset.DownloadUrl, Mirror));

                        var progress = new Progress<DownloadProgress>(p =>
                        {
                            asset.Progress = p.Percent;
                            asset.Detail = $"{p.ProgressText} · {p.SpeedText}" + (string.IsNullOrEmpty(p.RemainingText) ? string.Empty : $" · {p.RemainingText}");
                            component.Progress = job.TotalWeight <= 0
                                ? 0
                                : Math.Clamp((componentDone + item.Weight * p.Percent / 100d) / job.TotalWeight * 100d, 0, 100);
                            UpdateOverall(item.Weight * p.Percent / 100d, $"{component.Title} · {loc["Common.Downloading"]} {item.Pick.FileName}");
                        });

                        await _download!.DownloadAsync(
                            item.Pick.Asset.DownloadUrl,
                            item.DestinationPath,
                            progress,
                            token,
                            retry =>
                            {
                                if (retry.IsChannelSwitch)
                                {
                                    // 直链放弃了，改走 API asset 端点。这不是「重试」，得说清楚换了通道 ——
                                    // 否则日志读起来像在原地打转，用户也不知道该不该去换代理。
                                    Log(LogLevel.Warning, string.Format(
                                        loc["Log.DownloadFallback"], item.Pick.FileName, retry.Reason));
                                    asset.Status = loc["Common.SwitchingChannel"];
                                    return;
                                }

                                Log(LogLevel.Warning, string.Format(
                                    retry.IsFallback ? loc["Log.DownloadRetryFallback"] : loc["Log.DownloadRetry"],
                                    item.Pick.FileName,
                                    retry.Reason,
                                    retry.Delay.TotalSeconds,
                                    retry.Attempt,
                                    retry.MaxAttempts));
                                asset.Status = string.Format(loc["Common.Retrying"], retry.Attempt, retry.MaxAttempts);
                            },
                            // 备用通道在**解析时**就定好了（见 DownloadItem.Fallback）：
                            // release 资源走 API asset 端点，仓库文件树槽走 contents 端点，
                            // 两者要求的 Accept 头不同，在这里现算会分不清该用哪个。
                            item.Fallback);

                        asset.Progress = 100;
                        asset.IsActive = false;
                        asset.IsDone = true;
                        asset.Status = loc["Common.Done"];

                        componentDone += item.Weight;
                        doneWeight += item.Weight;
                        UpdateOverall(0, $"{component.Title} · {loc["Common.Downloading"]}");

                        Log(LogLevel.Success, string.Format(loc["Log.DownloadComplete"], item.DestinationPath));
                    }

                    // 暂存：把下载物摆成「相对 out/ 根目录」的样子，稍后由 ConfigGenerator
                    // 整棵合并进 out/。落点由 AssetPick.Targets 在 ComponentCatalog 里声明，
                    // 这里只负责照着摆。
                    //
                    // 组件本体 → download/<组件>/unpacked/
                    // 散装 payload → download/<组件>/payload/（单独开关控制，与组件合并互不影响）
                    var unpackedRoot = AppPaths.ComponentUnpackedRoot(component.FolderName);
                    var payloadRoot = AppPaths.ComponentPayloadRoot(component.FolderName);

                    foreach (var item in job.Items)
                    {
                        if (item.Pick.IsPayload && !IncludePayloads)
                        {
                            Log(LogLevel.Info, $"跳过 payload {item.Pick.FileName}（未开启「把 payload 放到对应目录」）");
                        }
                        else if (item.Pick.IsArchive)
                        {
                            component.Status = loc["Common.Extracting"];
                            Log(LogLevel.Info, string.Format(loc["Log.ExtractBegin"], item.Pick.FileName));
                        }
                    }

                    var stagingProgress = new Progress<StagingProgress>(p =>
                    {
                        var weight = job.Items.FirstOrDefault(i => i.Pick == p.Pick)?.Weight ?? 0;
                        var ratio = p.Total <= 0 ? 0 : (double)p.Current / p.Total;
                        UpdateOverall(weight * 0.1 * ratio, $"{component.Title} · {loc["Common.Extracting"]}");
                    });

                    var staged = await OutputStager.StageComponentAsync(
                        job.Items.Select(i => new StagingItem(i.DestinationPath, i.Pick)).ToList(),
                        unpackedRoot,
                        payloadRoot,
                        IncludePayloads,
                        stagingProgress,
                        token);

                    foreach (var entry in staged)
                    {
                        var root = entry.IsPayload ? payloadRoot : unpackedRoot;
                        var where = Path.Combine(
                            root,
                            entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                        if (entry.Pick.IsArchive)
                        {
                            Log(LogLevel.Success, string.Format(loc["Log.ExtractComplete"], entry.FileCount, where));
                        }
                        else
                        {
                            Log(LogLevel.Info, $"放置 {entry.Pick.FileName} → {where}");
                        }
                    }

                    component.Progress = 100;
                    component.IsBusy = false;
                    component.Status = loc["Common.Done"];
                    doneWeight += job.TotalWeight * 0.1;
                }
                catch (OperationCanceledException)
                {
                    // 用户点了取消，交给外层统一处理
                    throw;
                }
                catch (Exception ex)
                {
                    // 单个组件失败不再中止整个流程：先把其余组件跑完，最后统一汇总，
                    // 这样用户一次就能看到到底哪些成功、哪些失败，不用改一个跑一次。
                    var reason = NetworkErrors.Describe(ex);
                    component.IsBusy = false;
                    component.Status = loc["Common.Failed"];

                    foreach (var asset in component.Assets.Where(a => a.IsActive))
                    {
                        asset.IsActive = false;
                        asset.IsFailed = true;
                        asset.Status = loc["Common.Failed"];
                    }

                    failures.Add($"{component.Title}：{reason}");
                    Log(LogLevel.Error, string.Format(loc["Log.ComponentFailed"], component.Title, reason));
                }
            }

            if (failures.Count > 0)
            {
                OverallStatus = loc["Common.PartialFailed"];
                Log(LogLevel.Error, string.Format(loc["Log.SomeComponentsFailed"], failures.Count, jobs.Count));

                foreach (var failure in failures)
                {
                    Log(LogLevel.Error, "  · " + failure);
                }

                // 有组件没准备好就不生成配置，避免产出一套不完整的 SD 卡内容
                return;
            }

            // ── 阶段 2.5：boot.dat（自 2026-09-21 起不再内置，改为下载）──
            //    放在这里而不是解析阶段：它只是一条 URL，没有「版本列表」要挑。
            //    也刻意**不参与**组件那套加权进度 —— 一个 11 KB 的文件混进几百 MB 的权重里，
            //    只会让进度条多跳一下；而它失败也不该影响其余组件的进度语义。
            options.BootDatPath = await PrepareBootDatAsync(options, token);

            // ── 阶段 3：生成配置 ──────────────────────────────────
            token.ThrowIfCancellationRequested();
            OverallStatus = loc["Common.WritingConfig"];
            OverallProgress = ConfigPhaseStart;
            Log(LogLevel.Info, loc["Log.ConfigBegin"]);

            var generator = new ConfigGenerator(this);
            var result = await Task.Run(() => generator.Generate(options, token), token);
            _lastGeneratedFiles = result.WrittenFiles.ToList();

            // ── 阶段 3.5：完整性校验 ──────────────────────────────
            Log(LogLevel.Info, loc["Log.Validating"]);
            var report = OutputValidator.Validate(options, AppPaths.OutputRoot, result);

            foreach (var item in report.Items)
            {
                var level = item.Level switch
                {
                    ValidationLevel.Error => LogLevel.Error,
                    ValidationLevel.Warning => LogLevel.Warning,
                    _ => LogLevel.Info,
                };

                Log(level, string.Format(loc[item.Key], item.Detail));
            }

            if (report.IsHealthy)
            {
                Log(LogLevel.Success, string.Format(loc["Log.ValidationPassed"], report.Items.Count));
            }
            else
            {
                Log(LogLevel.Error, string.Format(loc["Log.ValidationFailed"], report.ErrorCount));
            }

            OverallProgress = 100;
            OverallStatus = report.IsHealthy ? loc["Common.Done"] : loc["Common.ValidationFailed"];
            stopwatch.Stop();

            Log(LogLevel.Success, string.Format(loc["Log.Finished"], AppPaths.OutputRoot));
            Log(LogLevel.Info, $"耗时 {stopwatch.Elapsed:mm\\:ss}，共写入 {result.ConfigFiles.Count} 个配置文件"
                               + (result.PayloadFiles.Count > 0 ? $"，{result.PayloadFiles.Count} 个 payload" : string.Empty)
                               + (result.MergedFileCount > 0 ? $"，合并 {result.MergedFileCount} 个组件文件" : string.Empty)
                               + $"，清单登记 {result.WrittenFiles.Count} 个文件");

            // ── 阶段 4：打包 ──────────────────────────────────────
            if (AutoPackZip)
            {
                PackZip();
            }

            PackZipCommand.RaiseCanExecuteChanged();

            if (!SuppressShellOpen)
            {
                OpenFolder(AppPaths.OutputRoot);
            }
        }
        catch (OperationCanceledException)
        {
            OverallStatus = loc["Common.Cancelled"];
            Log(LogLevel.Warning, loc["Log.Cancelled"]);
            foreach (var component in Components.Where(c => c.IsBusy))
            {
                component.IsBusy = false;
                component.Status = loc["Common.Cancelled"];
            }
        }
        catch (Exception ex)
        {
            OverallStatus = loc["Common.Failed"];
            Log(LogLevel.Error, string.Format(loc["Log.Failed"], NetworkErrors.Describe(ex)));
            foreach (var component in Components.Where(c => c.IsBusy))
            {
                component.IsBusy = false;
                component.Status = loc["Common.Failed"];
            }
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            OverallIsIndeterminate = false;
        }
    }

    private async Task<ComponentJob> ResolveComponentAsync(ComponentViewModel component, WizardOptions options, CancellationToken token)
    {
        var loc = LocalizationService.Instance;
        var job = new ComponentJob(component);
        var tags = new List<string>();
        var isPrerelease = false;

        // 声明的槽 → 这次真的下到了没有。**逐槽对账**（见方法末尾）：
        // 声明了 3 个文件却只下到 2 个时，缺的那个必须被点名，不能只报一句「解析完成」。
        var filledSlots = new HashSet<string>(StringComparer.Ordinal);

        var destination = AppPaths.ComponentDownloadRoot(component.FolderName);
        AppPaths.EnsureDirectory(destination);

        // ⚠️ 用 RepoRequestsFor 而不是 Definition.Repos：槽驱动的组件（19 个插件）的请求是
        //    由声明的槽**现算**出来的 —— 用户改过的地址、按语言取的文件名都在这一步生效。
        //    手写 picker 的框架组件（Slots 为空）原样返回 Repos，行为逐字节不变。
        foreach (var request in ComponentCatalog.RepoRequestsFor(component.Definition, options))
        {
            token.ThrowIfCancellationRequested();

            // 声明的地址只是「默认值」：用户可能在高级设置里改过，hekate 还会因界面语言换成本地化源。
            //
            // ⚠️ 槽驱动的请求（Slots 非空）**跳过这一步**：RepoRequestsFor 交给我们的 Repo 已经是
            //    ResolveSlotRepo 解析过的实际地址。再走一遍 ResolveRepo 会让「下载源」栏里一条
            //    恰好同名的覆盖记录二次生效 —— 用户改的是槽里的地址、实际却按别处取，
            //    而界面上两个地方都看不出这件事。
            //
            // ⚠️ AllowLanguageMirror = false 的请求（hekate 的 8G payload）要**跳过镜像那一条规则**，
            //    但仍走 ResolveRepo —— 用户给官方那一行填的地址照旧生效。
            //    整段跳过的话，「填了地址却不生效」会成为一个界面上看不出来的死控件。
            var repo = request.Slots.Count > 0
                ? request.Repo
                : ComponentCatalog.ResolveRepo(request.Repo, options, request.AllowLanguageMirror);

            if (!repo.Equals(request.Repo))
            {
                Log(LogLevel.Info, string.Format(loc["Log.RepoSwitched"], request.Repo.DisplayName, repo.DisplayName));
            }

            Log(LogLevel.Info, string.Format(loc["Log.Resolving"], repo.DisplayName));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 10, 600)));

            IReadOnlyList<ReleaseInfo> releases;
            try
            {
                releases = await _releases!.GetReleasesAsync(repo, 10, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    string.Format(loc["Log.NetworkError"], repo.DisplayName + " 请求超时，可在「高级设置」里调大超时时间"));
            }

            var best = ReleaseInfo.SelectBest(releases, options.PreferPrerelease);
            if (best is null)
            {
                Log(LogLevel.Warning, $"  {repo.DisplayName} 没有可用的 Release，已跳过。");
                continue;
            }

            tags.Add(best.Tag);
            isPrerelease |= best.IsPrerelease;

            Log(LogLevel.Info, string.Format(
                loc["Log.Resolved"],
                repo.DisplayName,
                best.Tag,
                best.IsPrerelease ? loc["Common.PreRelease"] : loc["Common.Stable"],
                best.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd")));

            var picks = request.Picker(best, options);
            if (picks.Count == 0)
            {
                Log(LogLevel.Warning, $"  {repo.DisplayName} {best.Tag} 中未找到匹配的资源文件。");
                continue;
            }

            // 勾了 8G 却拿不到 8G payload 时**别静默降级**（用户 2026-09-17 决定：告警，不报错）。
            // 判定放在这里而不是 picker 里，是因为 picker 的委托签名 `Func<ReleaseInfo, WizardOptions,
            // IReadOnlyList<AssetPick>>` **没有 logger**，想就地告警也没有出口 ——
            // 而这里同时有 ReleaseInfo、选项和日志出口。
            // 判据本身在 ComponentCatalog.IsRam8GbPayloadMissing（与 picker 共用同一个谓词）。
            if (IncludePayloads && ComponentCatalog.IsRam8GbPayloadMissing(best, options))
            {
                Log(LogLevel.Warning,
                    $"  {repo.DisplayName} {best.Tag} 里没有 8G 版 payload（__ram8GB.bin），已退回标准版；"
                    + "但配置仍按 8G 写（enable_mem_mode=1 / memmode=1）——"
                    + "产物会「配置说 8G、payload 却是 4G」。请确认上游是否改了命名，或关掉「8G 运存」。");
            }

            foreach (var pick in picks)
            {
                var assetId = repo.DisplayName + "::" + pick.FileName;
                var destinationPath = Path.Combine(destination, pick.FileName);

                // 容错命中要留痕：声明叫 release.zip、实际下到 sys-ftpd-1.0.5.zip 这种事，
                // 不写日志的话用户永远不知道「自己看到的版本号从哪来」，也无从判断猜得对不对。
                NoteSlotFilled(pick, filledSlots, loc);

                job.Items.Add(new DownloadItem(
                    repo,
                    pick,
                    assetId,
                    destinationPath,
                    Math.Max(1d, pick.Asset.Size),
                    BuildDownloadFallback(repo, pick.Asset)));

                Log(LogLevel.Info, string.Format(loc["Log.Asset"], pick.FileName, pick.Asset.SizeText));
            }
        }

        // ── 仓库文件树槽（SlotSource.RepoFile）──────────────────────
        // 这些槽没有 Release 可查（Luna 的 enctemplate.zip 从来没发布过，只在仓库根目录），
        // 所以不能走上面那条循环 —— 但**落点、SaveAs、IsPayload 的语义完全一样**，
        // 产物照样进 job.Items、照样进暂存与合并。
        foreach (var slot in ComponentCatalog.RepoFileSlots(component.Definition))
        {
            token.ThrowIfCancellationRequested();

            var pick = ComponentCatalog.PickRepoFileSlot(component.Definition, slot, options);
            var repo = pick.RepoFile!.Repo;
            var destinationPath = Path.Combine(destination, pick.FileName);

            Log(LogLevel.Info, string.Format(loc["Log.RepoFileFetch"], repo.DisplayName, pick.RepoFile.Path));

            NoteSlotFilled(pick, filledSlots, loc);

            job.Items.Add(new DownloadItem(
                repo,
                pick,
                repo.DisplayName + "::" + pick.FileName,
                destinationPath,
                // 长度未知（raw 端点不报 Content-Length），给个不比别人小的权重，
                // 免得大文件在总进度里被算成 0 权重、进度条早早跑到 100%。
                Math.Max(1d, pick.Asset.Size),
                BuildRepoFileFallback(pick.RepoFile)));

            Log(LogLevel.Info, string.Format(loc["Log.Asset"], pick.FileName, pick.Asset.SizeText));
        }

        // ── 仓库目录槽（SlotSource.RepoDirectory）────────────────────
        // 一个槽 → **一批**下载物：先把目录列出来（联网），再把每个文件塞进 job.Items。
        // 与仓库文件树槽走**同一条下载通道** —— 每个 pick 都带着 RepoFileRef，
        // 于是备用通道（raw 不通改走 contents 端点）与落点、SaveAs 的语义全部白捡。
        foreach (var slot in ComponentCatalog.RepoDirectorySlots(component.Definition))
        {
            token.ThrowIfCancellationRequested();

            var repo = ComponentCatalog.ResolveSlotRepo(component.Definition, slot, options);
            var directory = ComponentCatalog.ResolveSlotDirectory(component.Definition, slot, options);

            // ① 先试**快路径**：整仓打包一次取回（codeload 的 tar.gz，**1 次请求、不占 API 配额**）。
            //    为什么需要它：下面那条路在 raw.githubusercontent.com 不可达的网络里会退化成
            //    「1 次列目录 + **每个文件一次** API 调用」—— 实测那 20 个补丁就是 20 次，
            //    而未登录配额只有 60/小时，第 17 个文件就撞上 403 了。
            //    拿不到就**静默回落**到老路，行为与以前逐字节一致。
            var bundledFiles = await TryFetchDirectoryBundleAsync(
                component, repo, directory, destination, loc, token);

            IReadOnlyList<AssetPick> picks;
            var preDownloaded = bundledFiles is not null;

            if (bundledFiles is not null)
            {
                picks = ComponentCatalog.PickRepoDirectorySlot(slot, repo, bundledFiles);

                Log(LogLevel.Info, string.Format(
                    loc["Log.RepoBundleFetch"], repo.DisplayName, directory, picks.Count));
            }
            else
            {
                // ② 回落：列目录（1 次请求）+ 逐个文件（每个文件 1 次请求）。
                using var dirTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                dirTimeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 10, 600)));

                IReadOnlyList<RepoDirectoryFile> files;
                try
                {
                    files = await _releases!.GetDirectoryFilesAsync(repo, directory, dirTimeoutCts.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new InvalidOperationException(string.Format(
                        loc["Log.NetworkError"], repo.DisplayName + " 列目录请求超时，可在「高级设置」里调大超时时间"));
                }

                picks = ComponentCatalog.PickRepoDirectorySlot(slot, repo, files);

                Log(LogLevel.Info, string.Format(
                    loc["Log.RepoDirectoryFetch"], repo.DisplayName, directory, picks.Count));
            }

            foreach (var pick in picks)
            {
                job.Items.Add(new DownloadItem(
                    repo,
                    pick,
                    repo.DisplayName + "::" + pick.FileName,
                    Path.Combine(destination, pick.FileName),
                    Math.Max(1d, pick.Asset.Size),
                    BuildRepoFileFallback(pick.RepoFile!),
                    preDownloaded));

                Log(LogLevel.Info, string.Format(loc["Log.Asset"], pick.FileName, pick.Asset.SizeText));
            }

            // 对账只关心「这个槽有没有出东西」，不关心出了几个 —— 目录里空着才是问题。
            // 传 picks[0] 只是复用「取槽名」这条路径；Kind 恒为 Exact，不会打出容错告警。
            if (picks.Count > 0)
            {
                NoteSlotFilled(picks[0], filledSlots, loc);
            }
        }

        // ── 逐槽对账 ────────────────────────────────────────────────
        // 声明了槽却没产出文件的，逐个点名。**这不能靠「picks.Count == 0」那一条聚合告警** ——
        // 一个插件有多个文件时，只要有一个命中，聚合判据就是「有东西」，缺的那个静默消失。
        // 而「聚合检查旁边的单点特例 = 缺口的自白书」。
        foreach (var slot in component.Definition.Slots)
        {
            if (!filledSlots.Contains(slot.Key))
            {
                Log(LogLevel.Warning, string.Format(
                    loc["Log.SlotUnfilled"],
                    component.Title,
                    slot.Key,
                    ComponentCatalog.ResolveSlotFileName(component.Definition, slot, options),
                    ComponentCatalog.ResolveSlotRepo(component.Definition, slot, options).DisplayName));
            }
        }

        // 去重：一个组件可能由**多个**请求供同一个版本（hekate 的「汉化包 + 官方 8G payload」
        // 就是这种），原样拼出来是「v6.5.3 + v6.5.3」—— 看着像两个版本，其实是同一个。
        component.VersionText = tags.Count > 0
            ? string.Join(" + ", tags.Distinct(StringComparer.Ordinal))
            : string.Empty;
        component.ChannelText = isPrerelease ? loc["Common.PreRelease"] : loc["Common.Stable"];

        // 判据加上 job.Items.Count：仓库文件树槽（Luna 的 enctemplate.zip）没有 Release 可查，
        // 只按 tags 判断会把「真的下到了东西」的组件标成 Failed。
        component.HasResolvedRelease = tags.Count > 0 || job.Items.Count > 0;
        component.Status = component.HasResolvedRelease ? loc["Common.Ready"] : loc["Common.Failed"];

        return job;
    }

    /// <summary>
    /// 目录槽的**快路径**：一次请求把整个仓库快照（codeload 的 tar.gz）取回来，再从中只挑出
    /// <paramref name="directory"/> 下的直接子文件，落成**裸文件名**写进下载目录。
    ///
    /// 返回的是一批 <see cref="RepoDirectoryFile"/> —— 与「列目录」那条路**同构**，
    /// 于是两边都走 <see cref="ComponentCatalog.PickRepoDirectorySlot"/> 这一个入口：
    /// 文件名、落点、备用通道、逐槽对账全都一致，下游（暂存 / 合并 / 清单）**一行都不用改**。
    ///
    /// 拿不到就返回 <c>null</c>（网络不通 / 目录改名 / 包结构变了），调用方回落到老路。
    /// **不抛异常是有意的**：快路径失败不是错误，只是「这次没省下请求」。
    ///
    /// ⚠️ 进度只报在这一个 asset 上（组件条与总进度条要等真正的下载阶段才开始动）。
    /// 这是**有意**的取舍：整包取回发生在「解析」阶段，那里没有组件级的进度上下文；
    /// 为了让它也驱动组件条，得把整个下载循环改成「可动态插队」，代价远大于收益。
    /// </summary>
    private async Task<IReadOnlyList<RepoDirectoryFile>?> TryFetchDirectoryBundleAsync(
        ComponentViewModel component,
        RepoSpec repo,
        string directory,
        string destination,
        LocalizationService loc,
        CancellationToken token)
    {
        var url = ComponentCatalog.BuildRepoArchiveUrl(repo);
        var archiveName = ComponentCatalog.RepoArchiveFileName(repo);
        var archivePath = Path.Combine(destination, archiveName);

        var asset = new AssetViewModel(
            repo.DisplayName + "::" + archiveName, archiveName, repo.DisplayName, string.Empty);
        component.Assets.Add(asset);
        asset.IsActive = true;
        asset.Status = loc["Common.Downloading"];

        Log(LogLevel.Info, string.Format(loc["Log.DownloadBegin"], $"{repo.DisplayName} → {archiveName}"));

        // 同上面那个下载点：印实际请求的地址（走镜像时是镜像地址）。
        Log(LogLevel.Info, "  " + Services.GitHubMirror.Apply(url, Mirror));

        var progress = new Progress<DownloadProgress>(p =>
        {
            asset.Progress = p.Percent;
            asset.Detail = $"{p.ProgressText} · {p.SpeedText}"
                + (string.IsNullOrEmpty(p.RemainingText) ? string.Empty : $" · {p.RemainingText}");
        });

        try
        {
            // 超时交给 DownloadService 自己的「停滞超时」（连续 45 秒收不到数据才断），
            // 这里不再叠一层总时长上限 —— 整包可能比单个文件大得多，按文件那套时限卡它会误杀。
            await _download!.DownloadAsync(url, archivePath, progress, token);

            var extracted = ArchiveExtractor.ExtractDirectoryFromTarGz(archivePath, directory, destination);

            asset.Progress = 100;
            asset.IsActive = false;
            asset.IsDone = true;
            asset.Status = loc["Common.Done"];

            Log(LogLevel.Success, string.Format(loc["Log.DownloadComplete"], archivePath));

            return ComponentCatalog.AsDirectoryFiles(repo, directory, extracted);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            asset.IsActive = false;
            asset.Status = loc["Common.Failed"];

            Log(LogLevel.Warning, string.Format(
                loc["Log.RepoBundleFailed"], repo.DisplayName, directory, ex.Message));

            return null;
        }
        finally
        {
            // 整包是**中间物**：解开之后就不该再留在下载目录里 ——
            // 它既不是产物（不该进 out/），也不该进清单（清单是清理的依据，多一条就多一个要清的）。
            TryDeleteFile(archivePath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 删不掉就算了：它是下载目录里的临时物，下次跑会覆盖，不影响产物。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 记下「这个槽真的产出了文件」，并按命中方式留一条合适的日志。
    ///
    /// 容错命中必须说出来：用户清单里的 <c>release.zip</c> 实际会下到 <c>sys-ftpd-1.0.5.zip</c>，
    /// 这类事不写日志，用户既不知道版本号从哪来，也无从判断猜得对不对。
    ///
    /// ⚠️ 但**要分两族**（2026-09-18 联网 e2e 才发现）：「猜」要 **Warning + 建议改成确切的文件名**；
    /// 而「**按用户给的模式命中**」只需中性 Info —— 用户清单里 <c>triplayer-x.x.x.zip</c> 这种通配写法
    /// **是用户的意图**（跟着版本走），对它报 Warning 等于**教用户去改掉他自己给的名字**。
    ///
    /// ⚠️ 分类**不在本方法里判**，一律走 <see cref="AssetMatchKindExtensions.NoticeFor"/> ——
    /// 这里只负责「拿到分类之后该说什么」。曾经写成三段内联 if（`非 Exact 就 Warning`），
    /// 结果漂成了上面那个错误，而**没有任何断言变红**。
    /// </summary>
    private void NoteSlotFilled(AssetPick pick, HashSet<string> filledSlots, LocalizationService loc)
    {
        if (pick.Slot is not { } match)
        {
            return;
        }

        filledSlots.Add(match.SlotKey);

        switch (match.Kind.NoticeFor())
        {
            case SlotMatchNotice.Silent:
                return;

            case SlotMatchNotice.PatternHit:
                // 声明里本来就带 x/* 占位 ⇒ 按模式取到就是「如你所愿」，不是降级。
                Log(LogLevel.Info, string.Format(
                    loc["Log.AssetPatternMatch"],
                    match.SlotKey,
                    match.RequestedPattern,
                    pick.Asset.Name));
                return;

            case SlotMatchNotice.Guess:
                Log(LogLevel.Warning, string.Format(
                    loc["Log.AssetFuzzyMatch"],
                    match.SlotKey,
                    match.RequestedPattern,
                    pick.Asset.Name,
                    match.Kind.ToString()));
                return;

            default:
                // 新增了 SlotMatchNotice 却没在这里安排措辞 —— 宁可炸掉也不要静默按某一种处理。
                throw new ArgumentOutOfRangeException(
                    nameof(pick), match.Kind.NoticeFor(),
                    "新增了 SlotMatchNotice 取值，但 NoteSlotFilled 没安排对应的日志措辞");
        }
    }

    private void RequestCancel()
    {
        if (_cts is null || _cts.IsCancellationRequested)
        {
            return;
        }

        Log(LogLevel.Warning, LocalizationService.Instance["Log.CancelRequested"]);
        _cts.Cancel();
    }

    // ── 选项装配 ────────────────────────────────────────────────
    /// <summary>
    /// 把界面状态装配成 <see cref="WizardOptions"/>。
    ///
    /// 可见性是 <c>internal</c> 而非 <c>private</c>：唯一调用方 <see cref="RunAsync"/> 必须联网下载，
    /// 离线测试够不着，而这一步的赋值错位（比如两个屏蔽项互相写错）后果很静默 ——
    /// 界面看着正常，生成的 exosphere.ini 却是错的。故用 InternalsVisibleTo 开放给回归测试。
    /// </summary>
    internal WizardOptions BuildWizardOptions()
    {
        var options = new WizardOptions
        {
            BootStock = BootStock,
            BootSysNand = BootSysNand,
            BootEmuNand = BootEmuNand,
            BootStockTitle = BootStockTitle,
            BootSysNandTitle = BootSysNandTitle,
            BootEmuNandTitle = BootEmuNandTitle,
            BootStockLogo = BootStockLogo,
            BootSysNandLogo = BootSysNandLogo,
            BootEmuNandLogo = BootEmuNandLogo,
            BootStockIcon = BootStockIcon,
            BootSysNandIcon = BootSysNandIcon,
            BootEmuNandIcon = BootEmuNandIcon,
            BlankSerialSysmmc = BlankSerialSysmmc,
            BlankSerialEmummc = BlankSerialEmummc,
            Use90DnsSysmmc = Use90DnsSysmmc,
            Use90DnsEmummc = Use90DnsEmummc,
            Ram8Gb = Ram8Gb,
            PreferPrerelease = PreferPrerelease,
            IncludeComponentFilesInOutput = IncludeComponentFilesInOutput,
            IncludePayloads = IncludePayloads,
            IncludeBootDat = IncludeBootDat,
            Language = LocalizationService.Instance.Current.Code,
        };

        // 下载源：只把「与默认值不同」的记进去，避免存档里塞一堆等于默认值的噪音
        foreach (var source in RepoSources)
        {
            var value = source.Value?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(value)
                && !string.Equals(value, source.Key, StringComparison.OrdinalIgnoreCase))
            {
                options.RepoOverrides[source.Key] = value;
            }
        }

        // 插件文件槽：同上，只记「与默认不同」的。
        //
        // 判据用 HasCustomAddress / HasCustomFileName 而不是「非空」—— 槽的默认值是按**语言**现算的
        // （DBI 翻译文件），「填了 translation_zhtw.bin」在繁体界面下**等于默认值**，不该被当成自定义，
        // 否则用户在中文界面填一次、切到繁体就变成一条多余且可能过期的覆盖记录。
        foreach (var slot in AssetSlotGroups.SelectMany(group => group.Slots))
        {
            if (slot.HasCustomAddress)
            {
                options.AssetAddresses[slot.Key] = slot.Address.Trim();
            }

            if (slot.HasCustomFileName)
            {
                options.AssetFileNames[slot.Key] = slot.FileName.Trim();
            }
        }

        foreach (var component in Components)
        {
            options.Set(component.Kind, "__selected", component.IsSelected ? "1" : "0");
            component.ApplyOptionValues(options);
        }

        // 组件勾选：**逐个 ComponentKind 按同名属性写**。
        //
        // ⚠️ 以前这里只写死四个（Atmosphere/Hekate/Ultrahand/SysPatch）。加了 19 个插件类组件之后，
        //    那种写法会让它们**永远停在 false**：用户勾了，WizardOptions 里却还是没勾 ——
        //    而 ConfigGenerator 与 OutputValidator **都用 IsSelected 过滤**，
        //    于是「勾了却不下载、不合并」，界面上却看不出任何异常。
        //    改成遍历枚举，新增组件时这里自动跟上（读侧 IsSelected 也是同一张表，不会各认一套名字）。
        foreach (var kind in Enum.GetValues<ComponentKind>())
        {
            options.TrySetSelected(kind, Components.First(c => c.Kind == kind).IsSelected);
        }

        return options;
    }

    private Dictionary<string, string> ExtractSavedValues(ComponentKind kind)
    {
        var prefix = kind + "/";
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in _settings.OptionValues)
        {
            if (pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                result[pair.Key[prefix.Length..]] = pair.Value;
            }
        }

        return result;
    }

    /// <summary>
    /// 把当前界面取值写进 <c>settings.json</c> —— **落盘的唯一实现**。
    ///
    /// 调用者只有 <see cref="SaveSettingsQuietly"/>（自动落盘 / 关窗补写）：
    /// 用户 2026-09-19 取消了「保存设置」按钮，所以这里不再区分「用户按的」与「自动的」。
    /// 那个区分本身就是个隐患 —— 两条路各有一套 `if` 就等于有两种行为，而它们迟早会分叉
    /// （症状是「日志说的改动」与「文件里写的东西」对不上，两句话都出自同一个程序）。
    /// </summary>
    private void SaveSettings()
    {
        MaterializeInto(_settings);

        // 「改了什么」写进日志：用户要求「输了什么文字、点了什么」都能查。
        // 放在写文件**之前**：改动在内存里已经成立了，落盘失败是另一条独立的 Error ——
        // 两件事分开记，日志里才分得清「改了但没存」。
        LogSettingsChanges();

        // 填了不合法的地址也要留痕。同样放在落盘之前：写失败时这条更该在日志里。
        ReportInvalidAddressesOnce();

        if (SettingsStore.Save(_settings))
        {
            // 代理/Token/超时/**镜像**改了要立刻生效，否则「填了镜像却没走镜像」会一直持续到重启。
            // 但只在**这几个值真的变了**时重建：自动落盘随时可能来一次，
            // 无条件重建会把 HttpClient 建爆（套接字耗尽）。
            RebuildHttpClientsIfNetworkChanged();
        }
        else
        {
            Log(LogLevel.Error, "settings.json 写入失败。");
        }
    }

    /// <summary>
    /// 把「填了但不合法」的地址逐条点名，**同一个值只报一次**。
    ///
    /// 为什么要去重：这条原先挂在「保存设置」按钮上（用户按一次 = 报一次，天然不会重复）。
    /// 按钮取消后它挪进了自动落盘，而自动落盘每 400ms 就可能结算一次 ——
    /// 不去重的话，边打字边告警，一次输入能刷出十几行「格式不对」，真正要看的那条被淹掉
    /// （这正是当初给这个上报设开关的理由，换了个位置而已，理由没变）。
    ///
    /// 判据是「**这个值**报过没有」而不是「报过没有」：用户改成另一个非法值时要能再报一次。
    /// </summary>
    private void ReportInvalidAddressesOnce()
    {
        foreach (var (id, message) in CollectInvalidAddresses())
        {
            if (_reportedInvalidAddresses.Add(id))
            {
                Log(LogLevel.Warning, message);
            }
        }
    }

    /// <summary>已经报过的非法地址（见 <see cref="ReportInvalidAddressesOnce"/>）。</summary>
    private readonly HashSet<string> _reportedInvalidAddresses = new(StringComparer.Ordinal);

    /// <summary>
    /// 当前填得不合法的地址（下载源 + 插件文件槽），逐条给出「id + 可读文案」。
    /// id 用来去重，与文案分开 —— 从文案里反解析出 id 是把自己写死在一个格式上。
    /// </summary>
    private IEnumerable<(string Id, string Message)> CollectInvalidAddresses()
    {
        var loc = LocalizationService.Instance;

        foreach (var invalid in RepoSources.Where(s => s.IsInvalid))
        {
            yield return (
                "repo\u0001" + invalid.Key + "\u0001" + invalid.Value.Trim(),
                string.Format(loc["Settings.Repo.Invalid"], invalid.Key, invalid.Value.Trim()));
        }

        foreach (var slot in AssetSlotGroups.SelectMany(group => group.Slots).Where(s => s.IsAddressInvalid))
        {
            yield return (
                "slot\u0001" + slot.Key + "\u0001" + slot.Address.Trim(),
                string.Format(loc["Settings.Slots.InvalidAddress"], slot.Key, slot.Address.Trim()));
        }

        // 镜像站填错了同样要留痕：它不像前两类会「回落到默认地址」，而是**整条退回官方**，
        // 用户以为在走镜像、实际没走 —— 不说出来的话，「怎么还是这么慢」永远查不到原因。
        if (MirrorIsInvalid && !string.IsNullOrWhiteSpace(Mirror))
        {
            yield return (
                "mirror\u0001\u0001" + Mirror.Trim(),
                // ⚠️ 不做 string.Format：这句文案同时是输入框下面那行红字（XAML 静态显示），
                // 带占位符的话界面上会原样显示「{0}」—— 移植进来时它就是这样，顺手修掉。
                loc["Settings.Mirror.Invalid"]);
        }
    }

    /// <summary>
    /// 把界面上的当前取值搬进一份 <see cref="AppSettings"/> —— **「界面 → 存档」唯一的搬运点**。
    ///
    /// ⚠️ 两个调用者，而且要求产物**逐字一致**：
    /// <list type="number">
    ///   <item><see cref="SaveSettings"/>：把结果写进 <c>settings.json</c>；</item>
    ///   <item>构造函数：把结果当成**差异基线**（见 <see cref="_lastSettingsSnapshot"/>）。</item>
    /// </list>
    /// 所以映射只留这一份 —— 谁也别在别处再手写一遍，两份映射迟早会分叉，
    /// 而症状是「日志说的改动」与「文件里实际写的东西」对不上。
    ///
    /// ⚠️ 第二个调用者**刻意传一份临时对象**而不是 <see cref="_settings"/>：那一份的
    /// <c>Components</c> 被 <see cref="EnsureComponentsSelected"/> 当作「文件里到底有没有记录」的判据，
    /// 在构造期往里塞 46 个条目会让「首次运行默认全选」当场失效。
    ///
    /// ⚠️ 这里**只做映射**，没有任何副作用：非法地址的告警曾以 <c>reportInvalidAddresses</c> 旗标的形式
    /// 挂在这个方法上，但那样一来「搬值」和「报错」挤在一处，而且两个探针调用还得特意传 false 才不出错。
    /// 现在告警单独成一步（<see cref="ReportInvalidAddressesOnce"/>，只从 <see cref="SaveSettings"/> 调），
    /// 映射于是成了纯的 —— 探针调用与真实保存走的是同一条路，不会再有「顺手报了一次」的可能。
    /// </summary>
    private void MaterializeInto(AppSettings target)
    {
        target.Language = LocalizationService.Instance.Current.Code;
        // 主题存的是**档位名**（system/light/dark），不是这次渲染出来的颜色 —— 选「跟随系统」时
        // 存下 "system"，下次启动重新读一次系统设置，而不是把当时解析出的颜色钉死。
        target.Theme = SelectedTheme is null ? null : ThemeModeCodec.ToStorage(SelectedTheme.Mode);
        target.GitHubToken = string.IsNullOrWhiteSpace(GitHubToken) ? null : GitHubToken.Trim();
        target.TimeoutSeconds = Math.Clamp(TimeoutSeconds, 10, 600);
        target.Proxy = string.IsNullOrWhiteSpace(Proxy) ? null : Proxy.Trim();
        target.Mirror = string.IsNullOrWhiteSpace(Mirror) ? null : Mirror.Trim();
        target.PreferPrerelease = PreferPrerelease;
        target.IncludeComponentFilesInOutput = IncludeComponentFilesInOutput;
        target.IncludePayloads = IncludePayloads;
        target.IncludeBootDat = IncludeBootDat;
        target.BootDatUrl = string.IsNullOrWhiteSpace(BootDatUrl) ? null : BootDatUrl.Trim();
        target.UpdateUrl = string.IsNullOrWhiteSpace(UpdateUrl) ? null : UpdateUrl.Trim();
        target.BootStock = BootStock;
        target.BootSysNand = BootSysNand;
        target.BootEmuNand = BootEmuNand;
        target.BootStockTitle = BootStockTitle.Trim();
        target.BootSysNandTitle = BootSysNandTitle.Trim();
        target.BootEmuNandTitle = BootEmuNandTitle.Trim();
        target.BootStockLogo = BootStockLogo.Trim();
        target.BootSysNandLogo = BootSysNandLogo.Trim();
        target.BootEmuNandLogo = BootEmuNandLogo.Trim();
        target.BootStockIcon = BootStockIcon.Trim();
        target.BootSysNandIcon = BootSysNandIcon.Trim();
        target.BootEmuNandIcon = BootEmuNandIcon.Trim();
        target.AutoPackZip = AutoPackZip;
        target.BlankSerialSysmmc = BlankSerialSysmmc;
        target.BlankSerialEmummc = BlankSerialEmummc;
        target.Use90DnsSysmmc = Use90DnsSysmmc;
        target.Use90DnsEmummc = Use90DnsEmummc;
        target.Ram8Gb = Ram8Gb;

        // 只存「与默认不同」的下载源；非法地址照样存下来（免得用户白填一遍），
        // 但会明确警告一次，并说明运行时仍会回落到默认地址（见 ReportInvalidAddressesOnce）。
        target.RepoOverrides = RepoSources
            .Where(s => s.HasCustomValue)
            .ToDictionary(s => s.Key, s => s.Value.Trim(), StringComparer.OrdinalIgnoreCase);

        // 插件文件槽：同样只存「与默认不同」的；非法地址也照样存下来（免得用户白填一遍），
        // 但逐条点名警告 —— 与下载源那一栏同一套约定（运行时回落到默认地址，不抛错）。
        target.AssetAddresses = AssetSlotGroups
            .SelectMany(group => group.Slots)
            .Where(slot => slot.HasCustomAddress)
            .ToDictionary(slot => slot.Key, slot => slot.Address.Trim(), StringComparer.OrdinalIgnoreCase);

        target.AssetFileNames = AssetSlotGroups
            .SelectMany(group => group.Slots)
            .Where(slot => slot.HasCustomFileName)
            .ToDictionary(slot => slot.Key, slot => slot.FileName.Trim(), StringComparer.OrdinalIgnoreCase);

        target.Components = Components.ToDictionary(c => c.Kind.ToString(), c => c.IsSelected);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in Components)
        {
            foreach (var option in component.OptionGroups.SelectMany(g => g.Options))
            {
                values[component.Kind + "/" + option.Key] = option.Value;
            }
        }

        target.OptionValues = values;
    }

    // ── 联动逻辑 ────────────────────────────────────────────────

    /// <summary>
    /// <c>system_settings.ini</c> 里承载 90DNS 生效前提的那个键。
    ///
    /// 单独抽成常量是因为它被**两处**用到：这里做联动、以及 <c>ConfigGenerator</c> 写 ini。
    /// 拼错的话 <see cref="DnsMitmOption"/> 会返回 <c>null</c>，联动**静默失效** ——
    /// 界面照旧能勾，产物里 <c>enable_dns_mitm</c> 却纹丝不动，90DNS 于是完全不生效。
    /// 护栏 <c>Check90DnsSwitches</c> 会拿这个常量的值回目录里核对，防止改一处漏一处。
    /// </summary>
    internal const string DnsMitmOptionKey = "enable_dns_mitm";

    private OptionViewModel? DnsMitmOption
        => Components.FirstOrDefault(c => c.Kind == ComponentKind.Atmosphere)?.FindOption(DnsMitmOptionKey);

    /// <summary>
    /// Ultrahand <c>default_lang</c> 与「安装语言包」两个选项的 Key。
    /// 与 <see cref="DnsMitmOptionKey"/> 同理抽成常量：拼错的话下面两条查找都会返回 <c>null</c>，
    /// 联动**静默失效** —— 界面照旧能选语言，卡上却没有对应的语言包。
    /// </summary>
    internal const string DefaultLangOptionKey = "default_lang";

    internal const string InstallLangOptionKey = "installLang";

    private OptionViewModel? UltrahandOption(string key)
        => Components.FirstOrDefault(c => c.Kind == ComponentKind.Ultrahand)?.FindOption(key);

    /// <summary>
    /// 订阅 <c>enable_dns_mitm</c>：用户手动取消它 → 反向关掉两个 90DNS；
    /// 用户手动勾上它 → 反向补勾 90DNS（见 <see cref="ApplyDnsMitmDefaults"/>）。
    /// </summary>
    private void HookDnsMitmCoupling()
    {
        var option = DnsMitmOption;
        if (option is not null)
        {
            option.ValueChanged += OnDnsMitmOptionChanged;
        }
    }

    /// <summary>
    /// 规则①③：任一 90DNS 勾着 → 强制打开 <c>enable_dns_mitm</c>；两个都不勾 → 关掉它。
    ///
    /// 这条把 mitm 变成 90DNS 的**派生量**：不勾任何 90DNS 时它必为关。
    /// 与规则④（勾 mitm 时补勾 90DNS）合起来就是
    /// <c>mitm ⇔ (真实90DNS ∨ 虚拟90DNS)</c>，于是「mitm 开着却一份 hosts 都没有」这个
    /// 会让人误以为遥测已被挡住的状态，在两条路径上都到不了。
    /// 载入存档后也会调用一次（见构造函数），把老版本留下的不一致状态拉回来。
    /// </summary>
    /// <returns>真的改动了才返回 <c>true</c> —— 构造函数靠它决定要不要回写存档。</returns>
    private bool ApplyDnsMitmCoupling()
    {
        if (_applyingDnsMitmCoupling)
        {
            return false;
        }

        var option = DnsMitmOption;
        if (option is null)
        {
            return false;
        }

        var want = Use90DnsSysmmc || Use90DnsEmummc;
        if (option.BoolValue == want)
        {
            return false;
        }

        _applyingDnsMitmCoupling = true;
        try
        {
            option.BoolValue = want;
        }
        finally
        {
            _applyingDnsMitmCoupling = false;
        }

        return true;
    }

    /// <summary>
    /// 规则④：用户**手动勾上** <c>enable_dns_mitm</c> 时，把对应的 90DNS 开关一并补勾上。
    ///
    /// 只补**当前可见**的那几项（可见性跟 <see cref="ShowSysmmcDns"/> / <see cref="ShowEmummcDns"/> 走）——
    /// 没勾对应引导模式的 90DNS 开关没有意义：硬勾上只会让用户在别处看到一个自己没点过的勾，
    /// 而且产物里也不会多出 hosts 文件（生成端同样要求对应引导模式开着）。
    /// 两个引导模式都没勾时退化成「两个都补」：此时整个 90DNS 区域都不可见、不会立刻产生任何文件，
    /// 但守住了 <c>mitm ⇔ 至少一个 90DNS</c> 这条不变式 —— 否则用户之后勾上引导模式，
    /// 会看到 mitm 开着而 hosts 一份都没有。
    /// </summary>
    private void ApplyDnsMitmDefaults()
    {
        if (!BootSysNand && !BootEmuNand)
        {
            Use90DnsSysmmc = true;
            Use90DnsEmummc = true;
            return;
        }

        if (BootSysNand)
        {
            Use90DnsSysmmc = true;
        }

        if (BootEmuNand)
        {
            Use90DnsEmummc = true;
        }
    }

    /// <summary>
    /// 规则②④：用户在配置项里**切换** <c>enable_dns_mitm</c> 时，同步两个 90DNS 开关。
    ///
    /// 取消 → 两个 90DNS 同时取消（否则界面显示已启用、产物里 mitm 却关着，遥测根本没挡住）。
    /// 勾上 → 补勾 90DNS（见 <see cref="ApplyDnsMitmDefaults"/>）。
    /// </summary>
    private void OnDnsMitmOptionChanged(object? sender, EventArgs e)
    {
        if (_applyingDnsMitmCoupling || sender is not OptionViewModel option)
        {
            return;
        }

        _applyingDnsMitmCoupling = true;
        try
        {
            if (option.BoolValue)
            {
                ApplyDnsMitmDefaults();
            }
            else
            {
                Use90DnsSysmmc = false;
                Use90DnsEmummc = false;
            }
        }
        finally
        {
            _applyingDnsMitmCoupling = false;
        }
    }

    // ── default_lang ↔ 安装语言包（软联动） ──────────────────────
    /// <summary>
    /// 订阅 <c>default_lang</c>：用户选了非 en 的语言 → 补勾「安装语言包 lang.zip」。
    ///
    /// **刻意不订阅反向**（语言包 → default_lang）：用户取消勾选语言包时不去改他刚选的语言，
    /// 那等于把界面上的选择悄悄抹掉。产物层面的不一致交给
    /// <c>OutputValidator</c> 的 <c>Check.Warn.DefaultLangNoLangFile</c> 兜底 ——
    /// 与其把用户的输入改掉，不如照实生成 + 明确告警。
    /// </summary>
    private void HookLangPackCoupling()
    {
        var option = UltrahandOption(DefaultLangOptionKey);
        if (option is not null)
        {
            option.ValueChanged += (_, _) => EnsureLangPackForDefaultLang();
        }
    }

    /// <summary>
    /// 规则：<c>default_lang</c> 解析出来不是 <c>en</c> 时，程序替用户勾上「安装语言包」。
    ///
    /// 为什么需要它：Ultrahand 拿 <c>default_lang</c> 拼 <c>config/ultrahand/lang/&lt;代码&gt;.json</c>，
    /// 文件不存在时该语言在它自己的设置里会被**跳过**，于是「配置说中文、卡上没有中文包」
    /// 会静默退回编译进去的英文 —— 用户以为换了语言，实际什么都没发生。
    /// 而「安装语言包」的默认值是**不勾**，所以这条联动正是让 default_lang 真的生效的那一步。
    ///
    /// 刻意是**软**的（不锁定，用户可以再取消）：他可能打算自己往 SD 卡上放语言包，
    /// 或者之后用 Ultrahand 自带的更新器补。取消之后也不会被自动勾回来 ——
    /// 只有「选项值变了」或「界面语言变了」才会补一次。
    ///
    /// 这里**不需要**重入闸（与 <see cref="ApplyDnsMitmCoupling"/> 不同）：写的是
    /// <c>installLang</c>，而它身上只挂了「可见性依赖」这一种订阅者，不会回头再动
    /// <c>default_lang</c>，所以不构成环。
    /// </summary>
    /// <returns>真的改动了才返回 <c>true</c> —— 构造函数靠它决定要不要回写存档。</returns>
    private bool EnsureLangPackForDefaultLang()
    {
        var ultrahand = Components.FirstOrDefault(c => c.Kind == ComponentKind.Ultrahand);

        // 组件没勾就不用管：这一项此时对产物没有任何影响，用户还没决定要不要装 Ultrahand
        // 之前就替他勾上一个安装项是越权。勾上 Ultrahand 时会再走一遍（见 OnComponentPropertyChanged）。
        if (ultrahand is null || !ultrahand.IsSelected)
        {
            return false;
        }

        var lang = ultrahand.FindOption(DefaultLangOptionKey);
        var installLang = ultrahand.FindOption(InstallLangOptionKey);

        if (lang is null || installLang is null || installLang.BoolValue)
        {
            return false;
        }

        var resolved = ComponentCatalog.ResolveUltrahandDefaultLang(
            lang.Value, LocalizationService.Instance.Current.Code);

        if (string.Equals(resolved, ComponentCatalog.FallbackLanguageCode, StringComparison.Ordinal))
        {
            return false;
        }

        installLang.BoolValue = true;
        Log(LogLevel.Info, string.Format(LocalizationService.Instance["Log.LangPackAutoEnabled"], resolved));

        // 就地回写，理由同 ApplyDnsMitmCoupling 那处：只写被修正的那一项。
        // ⚠️ 这里**不能**用 SaveSettings() —— 它会遍历 RepoSources 与全部组件选项，
        //    而构造函数里调用本方法时 RepoSources 还没建好，会把用户的下载源覆盖成空。
        _settings.OptionValues[$"{ComponentKind.Ultrahand}/{InstallLangOptionKey}"] = installLang.Value;
        SettingsStore.Save(_settings);

        return true;
    }

    private void OnBootEntriesChanged()
    {
        OnPropertyChanged(nameof(ShowSerialOptions));
        OnPropertyChanged(nameof(ShowSysmmcBlank));
        OnPropertyChanged(nameof(ShowEmummcBlank));
        OnPropertyChanged(nameof(ShowSysmmcDns));
        OnPropertyChanged(nameof(ShowEmummcDns));
        OnPropertyChanged(nameof(SysPatchForced));
        UpdateForcedSelections();
        RefreshAutobootChoices();
    }

    private void UpdateForcedSelections()
    {
        var forced = SysPatchForced;
        var sysPatch = Components.First(c => c.Kind == ComponentKind.SysPatch);

        // 锁定时给出原因，别让用户对着一个灰掉的复选框猜
        sysPatch.LockHintKey = forced ? "Component.SysPatch.ForcedHint" : null;

        if (sysPatch.IsSelectable == forced)
        {
            sysPatch.IsSelectable = !forced;
        }

        if (forced && !sysPatch.IsSelected)
        {
            // 这里是**程序**替用户勾的，不能让 OnComponentPropertyChanged 把它当成
            // 「用户手动勾了 Sys-patch」，否则会反过来把两个屏蔽序列号一起勾上，
            // 「想屏蔽哪个就屏蔽哪个」就废了。
            _applyingForcedSelections = true;
            try
            {
                sysPatch.IsSelected = true;
            }
            finally
            {
                _applyingForcedSelections = false;
            }

            Log(LogLevel.Info, LocalizationService.Instance["Log.SysPatchForced"]);
        }
    }

    /// <summary>
    /// 用户**手动**勾上 Sys-patch 时，把两个屏蔽序列号默认勾上（省得再点两次）。
    ///
    /// 与 <see cref="SysPatchForced"/> 方向相反：那边是「屏蔽序列号 → 强制 Sys-patch」（硬约束，
    /// 会锁定），这边只是「Sys-patch → 默认勾上屏蔽序列号」（软默认，**用户可以自己取消**）。
    ///
    /// 只勾**当前可见**的那几项——没勾对应引导模式的屏蔽项没有意义，硬勾上只会让
    /// 用户在别处看到一个自己没点过的勾。
    /// </summary>
    private void ApplySysPatchDefaults()
    {
        if (BootSysNand && !BlankSerialSysmmc)
        {
            BlankSerialSysmmc = true;
        }

        if (BootEmuNand && !BlankSerialEmummc)
        {
            BlankSerialEmummc = true;
        }
    }

    /// <summary>autoboot 编号指向的引导项。编号是位置相关的，所以必须用这个「身份」来跨变更保持。</summary>
    private enum AutobootTarget
    {
        Off,
        Stock,
        SysNand,
        EmuNand,
    }

    /// <summary>
    /// 上次构建 autoboot 候选项时各引导项的启用状态。
    /// 有了它才能把「编号」按**当时**的排列反查回「指向哪个系统」——
    /// 否则一旦前面的引导项被取消勾选，编号整体前移，旧数字就会指向另一个系统。
    /// </summary>
    private (bool Stock, bool SysNand, bool EmuNand)? _autobootChoiceBasis;

    private void RefreshAutobootChoices()
    {
        var hekate = Components?.FirstOrDefault(c => c.Kind == ComponentKind.Hekate);
        var autoboot = hekate?.FindOption("autoboot");
        if (autoboot is null)
        {
            return;
        }

        // 先把当前编号反查成「指向哪个系统」，再按新的勾选情况把同一个系统重新编号。
        // 只认数字不认身份的话，取消勾选靠前的一项会让后面所有编号前移，
        // 旧数字要么越界被重置成「关闭」，要么仍合法但**指向了另一个系统**（静默换系统）。
        var target = ResolveAutobootTarget(autoboot.Value);

        var choices = new List<OptionChoice> { new("0", "Opt.Value.Off") };
        var index = 1;
        string? remapped = target == AutobootTarget.Off ? "0" : null;

        if (BootStock)
        {
            choices.Add(new OptionChoice(index.ToString(), BootEntryName(AutobootTarget.Stock)));
            if (target == AutobootTarget.Stock)
            {
                remapped = index.ToString();
            }

            index++;
        }

        if (BootSysNand)
        {
            choices.Add(new OptionChoice(index.ToString(), BootEntryName(AutobootTarget.SysNand)));
            if (target == AutobootTarget.SysNand)
            {
                remapped = index.ToString();
            }

            index++;
        }

        if (BootEmuNand)
        {
            choices.Add(new OptionChoice(index.ToString(), BootEntryName(AutobootTarget.EmuNand)));
            if (target == AutobootTarget.EmuNand)
            {
                remapped = index.ToString();
            }
        }

        autoboot.ReplaceChoices(choices);

        // 指回同一个系统；只有那个系统本身被取消勾选了，才回落到「关闭」。
        var final = remapped ?? "0";
        if (!string.Equals(autoboot.Value, final, StringComparison.Ordinal))
        {
            autoboot.Value = final;
        }

        _autobootChoiceBasis = (BootStock, BootSysNand, BootEmuNand);
    }

    /// <summary>
    /// 把 autoboot 的编号按**上次构建候选项时**的启用状态反查成「指向哪个系统」。
    /// 首次调用（还没有基准）时用当前的启用状态，这正是存档值被写下来时的那套排列。
    /// </summary>
    private AutobootTarget ResolveAutobootTarget(string value)
    {
        if (!int.TryParse(value, out var number) || number <= 0)
        {
            return AutobootTarget.Off;
        }

        var basis = _autobootChoiceBasis ?? (BootStock, BootSysNand, BootEmuNand);
        var position = 1;

        if (basis.Stock)
        {
            if (number == position)
            {
                return AutobootTarget.Stock;
            }

            position++;
        }

        if (basis.SysNand)
        {
            if (number == position)
            {
                return AutobootTarget.SysNand;
            }

            position++;
        }

        if (basis.EmuNand && number == position)
        {
            return AutobootTarget.EmuNand;
        }

        // 编号越界（例如设置被手改过，或候选项变少了）：当作「关闭」处理
        return AutobootTarget.Off;
    }

    /// <summary>
    /// 某个引导项在界面上显示的名字：用户改过就用用户的，否则用界面语言的默认名。
    ///
    /// **界面候选项与生成的 hekate_ipl.ini 必须用同一个解析规则**（那边在
    /// <c>ConfigGenerator.ResolveBootTitle</c>）。两处只要有一处漏掉自定义名，
    /// 「autoboot 指向第几个引导项」的跨层护栏就会失效 —— 界面显示 A、文件里是 B。
    /// </summary>
    private string BootEntryName(AutobootTarget target) => target switch
    {
        AutobootTarget.Stock => ComponentCatalog.ResolveBootTitle(BootStockTitle, "Boot.Entry.Stock"),
        AutobootTarget.SysNand => ComponentCatalog.ResolveBootTitle(BootSysNandTitle, "Boot.Entry.SysNand"),
        AutobootTarget.EmuNand => ComponentCatalog.ResolveBootTitle(BootEmuNandTitle, "Boot.Entry.EmuNand"),
        _ => LocalizationService.Instance["Opt.Value.Off"],
    };

    private void OnComponentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ComponentViewModel.IsSelected))
        {
            // 进度、状态、版本号这些是**运行时**属性，下载中每秒变很多次，不该触发落盘。
            // 勾选是设置，必须落盘 —— 用户勾了哪些组件，下次打开要一模一样。
            return;
        }

        ScheduleAutoSave();

        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowNoSelectionHint));
        StartCommand.RaiseCanExecuteChanged();

        // 用户**手动**勾上 Sys-patch → 把两个屏蔽序列号默认勾上（可自行取消）。
        // _applyingForcedSelections 为 true 时说明是程序自己勾的，不能触发，
        // 否则「勾一个屏蔽序列号」会连锁把另一个也勾上。
        if (!_applyingForcedSelections
            && sender is ComponentViewModel { Kind: ComponentKind.SysPatch, IsSelected: true })
        {
            ApplySysPatchDefaults();
        }

        // 勾上 Ultrahand → 若 default_lang 指向非 en 的语言，补勾「安装语言包」。
        // 这一步是必需的：EnsureLangPackForDefaultLang 在组件没勾时**刻意**不动作
        // （见那里的注释），而「跟随界面语言」的默认值在中文界面下解析出来就是 zh-cn。
        if (sender is ComponentViewModel { Kind: ComponentKind.Ultrahand, IsSelected: true })
        {
            EnsureLangPackForDefaultLang();
        }

        // 勾选变化会改变「有没有两个组件撞同一个 sysmodule title」，所以每次都要重算。
        RefreshTitleConflicts();
    }

    /// <summary>
    /// 重算「哪些勾了的组件装的是同一个 sysmodule title」，并在卡片上给出提示。
    ///
    /// 判据来自各槽声明的 <see cref="AssetSlot.SysmoduleTitleId"/>（实测得来），
    /// 与生成端的 <c>OutputValidator</c> **调同一个方法**，不会一边说冲突一边照生成。
    ///
    /// ⚠️ 先**全部清空**再重算。只加不清的话，用户把 usb-botbase 取消勾选后，
    /// sys-botbase 卡片上的提示会一直挂着 —— 提示说「和另一个冲突」，可另一个已经没了。
    /// </summary>
    private void RefreshTitleConflicts()
    {
        foreach (var component in Components)
        {
            component.ConflictHintKey = null;
        }

        var selected = Components.Where(c => c.IsSelected).Select(c => c.Kind);
        foreach (var conflict in ComponentCatalog.FindTitleConflicts(selected))
        {
            foreach (var kind in conflict.Kinds)
            {
                if (Components.FirstOrDefault(c => c.Kind == kind) is { } component)
                {
                    component.ConflictHintKey = "Component.Conflict.SameTitle";
                }
            }
        }
    }

    private void RefreshLocalizedText()
    {
        foreach (var group in ComponentGroups)
        {
            group.RefreshLocalizedText();
        }

        foreach (var component in Components)
        {
            component.RefreshLocalizedText();
        }

        foreach (var source in RepoSources)
        {
            source.RefreshLocalizedText();
        }

        // 槽位的默认文件名可能**跟着界面语言变**（DBI 的翻译文件），所以这里必须遍历刷新 ——
        // 少了这一步，切到繁体后占位符还写着 translation_zhcn.bin，用户会以为联动没生效，
        // 而实际下载时按新语言取的是 zhtw。
        foreach (var group in AssetSlotGroups)
        {
            group.RefreshLocalizedText();
        }

        // 主题下拉项的显示名也要跟着语言走（暗夜 / Dark）—— 少了这一步，切到英文后
        // 下拉框里还写着中文，看着像「语言没切干净」。
        foreach (var theme in Themes)
        {
            theme.RefreshLocalizedText();
        }

        // 日志目录的说明里嵌了「当天那个文件名」，切语言与跨零点时都要重算。
        OnPropertyChanged(nameof(LogFolderHint));

        // 卡片上那行文字在「空状态」时取自语言包（未勾选 / 就绪），必须跟着语言走。
        OnPropertyChanged(nameof(BootDatStatusText));
    }

    // ── 日志 ────────────────────────────────────────────────────
    public void Log(LogLevel level, string message)
    {
        if (_dispatcher.CheckAccess())
        {
            Append(level, message);
        }
        else
        {
            _dispatcher.BeginInvoke(() => Append(level, message));
        }
    }

    private void Append(LogLevel level, string message)
    {
        Logs.Add(new LogEntry(DateTimeOffset.Now, level, message));
        while (Logs.Count > 2000)
        {
            Logs.RemoveAt(0);
        }

        // 计数放在写文件**之前**：计数的是「本次运行发生过多少条警告/错误」，
        // 与文件写不写得进去无关（文件写不进去本身就是一条要报的警告）。
        if (level == LogLevel.Warning)
        {
            _warningCount++;
        }
        else if (level == LogLevel.Error)
        {
            _errorCount++;
        }

        // 同一条日志**同时**落到 logs/ 的当天文件里（用户要求：出问题能靠文件排查）。
        //
        // ⚠️ 写在这里而不是每个调用点：日志有几十处出口，逐个加一行迟早会漏，
        //    而漏掉的那条恰恰可能是用户要拿来定位问题的那条。
        //    这也是唯一一处「界面日志 → 文件日志」的桥，别在别处再写一份。
        LogFile.Write(level, message);

        // 文件写不进去时只在第一次提醒（LogFile 内部只报一次原因），否则会把界面刷满。
        if (LogFile.DisabledReason is { } reason && !_reportedLogFileFailure)
        {
            _reportedLogFileFailure = true;
            Logs.Add(new LogEntry(
                DateTimeOffset.Now,
                LogLevel.Warning,
                string.Format(LocalizationService.Instance["Log.FileLogFailed"], reason)));
        }
    }

    /// <summary>日志文件写不进去只提醒一次（原因见 <see cref="LogFile.DisabledReason"/>）。</summary>
    private bool _reportedLogFileFailure;

    private void Warn(string message)
    {
        Log(LogLevel.Warning, message);
        OverallStatus = message;
    }

    // ── 网络 ────────────────────────────────────────────────────
    private GitHubReleaseService? _releases;
    private DownloadService? _download;

    public void RebuildHttpClients()
    {
        _httpSignature = NetworkSignature;

        _http?.Dispose();
        _downloadHttp?.Dispose();

        // 下载不设整体超时（大文件耗时不可预估），改由 DownloadService 的停滞超时兜底；
        // 查询 Release 的超时在 ResolveComponentAsync 里按设置逐次控制。
        _http = NetworkClientFactory.Create(Proxy, Timeout.InfiniteTimeSpan, gitHubApiHeaders: true);

        // 下载**单独**一个 client。上面那个带着 GitHub API 的默认 Accept（application/vnd.github+json），
        // 而备用通道（API asset 端点）必须发 application/octet-stream —— 少了它 GitHub 返回的是资源的
        // **JSON 元数据**（HTTP 200），会被当成组件包写进 SD 卡。分开建 client 就不必去赌
        // 「逐请求的 Accept 能不能盖掉默认头」这件事。
        _downloadHttp = NetworkClientFactory.Create(Proxy, Timeout.InfiniteTimeSpan);

        // 镜像**两边都给**，但两边**用法不同**（这一点由各自的服务负责，见它们的注释）：
        //   · 下载：填了就直接走镜像（用户填它就是嫌官方地址慢或不通）；
        //   · 查询：先直连官方，只有**连接层**失败才改走镜像 —— 镜像出口 IP 是公用的，
        //     未登录的 API 额度按 IP 算，直接换过去会「填了镜像反而一个组件都查不到」。
        _releases = new GitHubReleaseService(_http, this, GitHubToken, Mirror);
        _download = new DownloadService(_downloadHttp, Mirror);
    }

    public void EnsureHttpClients() => RebuildHttpClients();

    /// <summary>
    /// 影响 HttpClient / 下载通道 / 查询通道的四个设置，拼成一个指纹。用 <c>'\u0001'</c> 分隔，
    /// 免得「代理刚好叫 <c>10.0.0.1:8080</c>」这类取值把两个字段拼出同一个串。
    ///
    /// ⚠️ 镜像必须在这里：它决定下载器与查询服务拿哪条地址，改了不重建的话
    /// 「填了镜像却没走镜像」会一直持续到重启 —— 而用户会以为是镜像失效了。
    /// </summary>
    private string NetworkSignature =>
        string.Join('\u0001', GitHubToken, TimeoutSeconds.ToString(), Proxy, Mirror);

    /// <summary>只在 Token / 超时 / 代理 / 镜像真的变了时重建客户端（判据见 <see cref="NetworkSignature"/>）。</summary>
    private void RebuildHttpClientsIfNetworkChanged()
    {
        if (!string.Equals(_httpSignature, NetworkSignature, StringComparison.Ordinal))
        {
            RebuildHttpClients();
        }
    }

    /// <summary>
    /// 给直链配一条备用通道：GitHub 的 API asset 端点。
    ///
    /// 为什么需要它：release 直链会 302 跳到 <c>objects.githubusercontent.com</c>，有些代理（以及
    /// 本项目的开发沙箱）恰好在这一跳上返回 502，表现为「版本查询一切正常、下载全军覆没」。
    /// 同一次会话里 asset 端点却能完整下载 —— 它走的是 <c>api.github.com</c>。
    ///
    /// 拿不到 asset id（老 JSON、手写样例）时返回 null，行为与以前一致：只重试直链。
    ///
    /// **带上 Token 是安全的**：asset 端点会 302 跳到 <c>objects.githubusercontent.com</c> 上的预签名
    /// URL，而 S3 对「URL 已带签名、请求头又带 Authorization」会直接 400。好在 .NET 会自动剥掉它 ——
    /// 官方文档原文：<i>"The Authorization header is cleared on auto-redirects … No other headers are
    /// cleared."</i>（<c>RedirectHandler</c> 里就是 <c>request.Headers.Authorization = null;</c>，
    /// 而且对**所有**跳转都清，不只是跨域）。所以 Token 只出现在发给 api.github.com 的那一跳上。
    /// </summary>
    internal DownloadFallback? BuildDownloadFallback(RepoSpec repo, ReleaseAsset asset)
    {
        if (!asset.HasApiId)
        {
            return null;
        }

        // 配了 Token 就带上：asset 端点计入 API 配额（未登录只有 60 次/小时）。
        var token = GitHubToken?.Trim();
        return new DownloadFallback(
            ReleaseAsset.BuildApiAssetUrl(repo, asset.Id),
            "application/octet-stream",
            string.IsNullOrWhiteSpace(token) ? null : "Bearer " + token);
    }

    /// <summary>
    /// 仓库文件树槽的备用通道：<c>api.github.com</c> 的 contents 端点。
    ///
    /// 与 <see cref="BuildDownloadFallback"/> 同一套理由（<c>raw.githubusercontent.com</c> 在部分网络下
    /// 不可达，而 API 端点往往能通），但**Accept 头不一样**：contents 端点必须发
    /// <c>application/vnd.github.raw</c>，否则返回的是这段文件的 JSON 元数据（HTTP 200），
    /// 会被当成插件包原样写进 SD 卡。这个头与 URL 是一对，所以两处都从
    /// <see cref="ComponentCatalog"/> 取，不在这里另抄一份字符串。
    ///
    /// 与 release 那条通道不同，这里**没有「拿不到 id 就不给备用」的退路**：raw 直链是唯一入口，
    /// 没有备用通道就等于「网络不通时这个文件永远下不到」。
    /// </summary>
    internal DownloadFallback BuildRepoFileFallback(RepoFileRef source)
    {
        var token = GitHubToken?.Trim();
        return new DownloadFallback(
            ComponentCatalog.BuildRepoFileApiUrl(source.Repo, source.Path),
            ComponentCatalog.RawContentAccept,
            string.IsNullOrWhiteSpace(token) ? null : "Bearer " + token);
    }

    // ── 命令行（--run）模式入口 ──────────────────────────────────
    /// <summary>命令行模式下不自动打开资源管理器。</summary>
    public bool SuppressShellOpen { get; set; }

    /// <summary>直接执行一次完整流程（解析 → 下载 → 解压 → 生成配置）。</summary>
    public Task RunOnceAsync() => RunAsync();

    /// <summary>
    /// 如果 settings.json 里从来没有任何组件的勾选记录（首次运行），就默认全选。
    /// 返回 true 表示确实做了默认全选。
    ///
    /// **只给命令行 <c>--run</c> 用**（<see cref="App"/> 的 RunHeadless）。界面路径**不调用**它 ——
    /// 用户要求首次运行打开软件时所有组件都不勾，自己按需要挑；所以构造函数里没有这一步。
    ///
    /// 为什么命令行非调不可：<c>--run</c> 是无人值守路径，没有界面可以勾。不自动选，
    /// <see cref="RunAsync"/> 会直接撞上「一个组件都没勾」的保护分支，提示一句
    /// <c>Common.NoSelection</c> 就返回 —— 整个端到端验证等于空跑，却还报退出码 0。
    ///
    /// **判断依据刻意用 <c>_settings.Components</c>（文件里存了什么），而不是当前的
    /// <see cref="Components"/> 状态。** 两者不等价：构造函数会先调
    /// <see cref="UpdateForcedSelections"/>，只要用户上次开着任一「屏蔽序列号」，
    /// Sys-patch 就会被**强制勾上**（需求②在重启后同样生效）。此时实时状态是非空的，
    /// 但文件里其实从没被显式勾选过 —— 按实时状态判断就会得出「用户已经选过了」，
    /// 从而漏掉默认全选。
    ///
    /// **判据是「文件里有没有 Components 记录」（<c>Count == 0</c>），而不是「有没有哪个是 true」。**
    /// 「用户主动取消全部勾选」与「从来没勾过」语义不同：前者是用户明确的「什么都不要」，
    /// 命令行也要尊重它（此时就该什么都不做，而不是替他勾满）。只有前者会在文件里留下显式的
    /// <c>false</c> 记录（<see cref="SaveSettings"/> 会把未勾选项也写进去），所以必须按
    /// 「记录存在与否」判断。用 <c>Values.Any(v =&gt; v)</c> 会把这两种情况混为一谈，
    /// 用户明确表达的「什么都不要」会在下次 <c>--run</c> 时被悄悄改回全选。
    /// </summary>
    public bool EnsureComponentsSelected()
    {
        if (_settings.Components.Count > 0)
        {
            return false;
        }

        // 这里是**程序**替用户勾的（首次运行的默认全选），不能让 OnComponentPropertyChanged
        // 把它当成「用户手动勾了 Sys-patch」——否则会连锁触发软默认，把两个屏蔽序列号也勾上：
        // 用户从没说过要屏蔽序列号，exosphere.ini 里却写进了 blank_prodinfo_*=1，且完全静默。
        _applyingForcedSelections = true;
        try
        {
            foreach (var component in Components)
            {
                component.IsSelected = true;
            }
        }
        finally
        {
            _applyingForcedSelections = false;
        }

        UpdateForcedSelections();
        return true;
    }

    // ── 杂项 ────────────────────────────────────────────────────
    /// <summary>
    /// 把 out/ 打包成 zip。压缩包内不套外层目录，解压后即可直接拖进 SD 卡根目录。
    /// 打包失败不会中断主流程，只记一条日志。
    /// </summary>
    private void PackZip()
    {
        var loc = LocalizationService.Instance;

        try
        {
            Log(LogLevel.Info, loc["Log.Packing"]);
            var zipPath = OutputPackager.Create(AppPaths.OutputRoot);
            var size = new FileInfo(zipPath).Length;
            Log(LogLevel.Success, string.Format(loc["Log.Packed"], zipPath, FormatSize(size)));

            // 回读压缩包，比对清单，确认没有漏文件
            if (_lastGeneratedFiles.Count > 0)
            {
                var missing = OutputValidator.FindMissingInZip(zipPath, _lastGeneratedFiles);
                if (missing.Count == 0)
                {
                    Log(LogLevel.Success, string.Format(loc["Log.ZipVerified"], _lastGeneratedFiles.Count));
                }
                else
                {
                    Log(LogLevel.Warning, string.Format(loc["Log.ZipMissing"], missing.Count, string.Join("、", missing)));
                }
            }
        }
        catch (InvalidOperationException)
        {
            Log(LogLevel.Warning, loc["Log.PackEmpty"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            Log(LogLevel.Warning, string.Format(loc["Log.PackFailed"], NetworkErrors.Describe(ex)));
        }
    }

    // ── GitHub Token 辅助 ───────────────────────────────────────

    /// <summary>GitHub「新建 Token」页面的地址（预填用途说明）。</summary>
    internal const string TokenCreateUrl =
        "https://github.com/settings/tokens/new?description=Switch%20CFW%20Wizard";

    /// <summary>
    /// 直接打开 GitHub 的「新建 Token」页面。
    ///
    /// 只预填 description，**刻意不预填任何 scope**：本工具读的全是公开仓库，
    /// 无 scope 的 Token 就够用，而且权限最小。「填 Token」最容易卡住人的地方
    /// 就是不知道该勾哪个权限 —— 与其在界面里写一段说明，不如把这一步直接省掉。
    /// </summary>
    /// <summary>
    /// 用系统浏览器打开镜像站搭建说明。
    ///
    /// 与 <see cref="OpenTokenPage"/> 同一套写法：打不开浏览器时**把地址写进日志**，
    /// 用户还能自己粘一遍 —— 直接吞掉异常的话，他只会看到「点了没反应」。
    /// </summary>
    private void OpenMirrorGuide()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = GitHubMirror.BuildGuideUrl, UseShellExecute = true });
            Log(LogLevel.Info, LocalizationService.Instance["Settings.Mirror.BuildGuide.Opened"]);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            Log(LogLevel.Warning, GitHubMirror.BuildGuideUrl);
        }
    }

    private void OpenTokenPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = TokenCreateUrl, UseShellExecute = true });
            Log(LogLevel.Info, LocalizationService.Instance["Settings.GitHubToken.Opened"]);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            // 打不开浏览器时至少把地址给出来，用户还能自己粘到浏览器里
            Log(LogLevel.Warning, TokenCreateUrl);
        }
    }

    /// <summary>
    /// 验证 Token 是否可用，并显示剩余配额。
    ///
    /// 「有没有必要配 Token」看这个数字最直观：匿名 60 次/小时，带 Token 5000 次/小时。
    /// 401 说明 Token 抄错了或已失效 —— 与其等下载时报 403 再让用户猜，不如在这里说清楚。
    /// 没填 Token 时也照查一次，让用户看到「我现在的额度是多少」。
    ///
    /// ⚠️ **刻意不走镜像站**（哪怕填了）：它回答的是「**我**这个 Token 对 GitHub 有效吗、
    /// **我**还剩多少额度」，而额度是**按 IP** 算的 —— 走镜像拿回来的是镜像那个公用出口的额度，
    /// 数字会莫名其妙地小，用户会以为自己的 Token 坏了。回归里有一条**反向**断言钉住这件事，
    /// 防的是「顺手也给它套上镜像」那种好意改动。
    /// </summary>
    /// <summary>
    /// 检查更新：查最新 Release → 与当前版本比较 → 有新版就下载、启动它、退出旧版。
    ///
    /// 三件事按用户 2026-09-21 的要求定：
    /// <list type="bullet">
    ///   <item>查询地址可在高级设置里改，留空就用默认（一条 GitHub 的 releases 网页地址，
    ///         由 <see cref="UpdateSource.ResolveRepo"/> 推出真正的 API 地址）；</item>
    ///   <item>下载落盘用 <c>SwitchCfwWizard_&lt;版本号&gt;.exe</c> —— 与旧版同名的话，
    ///         新包会去覆盖**正在运行**的那个 exe，Windows 上必然失败且异常含义模糊；</item>
    ///   <item>下载完成后启动新版、再销毁旧版进程；**检查更新本身不写配置**。</item>
    /// </list>
    ///
    /// ⚠️ 顺序是刻意的：**先确认新进程活着，再退出自己**。这个 exe 是框架依赖的，
    ///    机器上没装 .NET 8 桌面运行时时它会立刻退出 —— 若反过来先退出，用户手里就什么都不剩了。
    /// </summary>
    private async Task CheckUpdateAsync()
    {
        var loc = LocalizationService.Instance;

        // 窗口那条路径里 App 已经调过 EnsureHttpClients()；这里是防御（--selftest 之类的入口
        // 可能没走到那一步），只在真的没建时才建 —— 每次点击都重建会耗尽套接字。
        if (_releases is null || _download is null)
        {
            EnsureHttpClients();
        }

        // 地址填坏了**回落到默认地址**（用户 2026-09-22 要求），而不是让这次检查直接作废 ——
        // 与「下载源 / 插件文件槽」同一约定。回落必须**说出来**：静默回落会让用户
        // 以为「我填的那条生效了」，于是永远查不到自己填错了。
        var (repo, fellBack) = UpdateSource.ResolveWithFallback(UpdateUrl);

        if (repo is null)
        {
            // 理论上到不了这里（默认地址是写死的合法地址）—— 留一条明确的出口，而不是空引用。
            UpdateStatus = loc["Settings.UpdateUrl.Invalid"];
            Log(LogLevel.Error, UpdateStatus);
            return;
        }

        if (fellBack)
        {
            // 与输入框旁边那行红字用**同一句文案**：同一个问题在两处说成两样最让人困惑。
            // 不做 string.Format 的理由同上（那句在界面上是静态显示的）。
            UpdateStatus = loc["Settings.UpdateUrl.Invalid"];
            Log(LogLevel.Warning, UpdateStatus);
        }

        var current = typeof(MainViewModel).Assembly.GetName().Version;

        IsCheckingUpdate = true;
        UpdateStatus = loc["Update.Checking"];
        CheckUpdateCommand.RaiseCanExecuteChanged();

        try
        {
            Log(LogLevel.Info, string.Format(
                loc["Log.UpdateCheckBegin"], repo.DisplayName, UpdateSource.Format(current)));

            var releases = await _releases!.GetReleasesAsync(repo, 20, CancellationToken.None);

            // 稳定版优先，**刻意不跟随「优先预览版」那个开关**：那个开关是给组件用的
            // （预览版常常先修好某个兼容问题），而整套程序换成预览版是另一件事 ——
            // 用户按的是「检查更新」，期望拿到的是正式版。
            var latest = ReleaseInfo.SelectBest(releases, preferPrerelease: false);

            if (latest is null)
            {
                UpdateStatus = loc["Update.NoRelease"];
                Log(LogLevel.Warning, UpdateStatus);
                return;
            }

            Log(LogLevel.Info, string.Format(loc["Log.UpdateLatest"], latest.Tag, latest.ChannelText));

            if (!UpdateSource.IsNewer(latest.Tag, current?.ToString()))
            {
                UpdateStatus = string.Format(loc["Update.IsLatest"], UpdateSource.Format(current));
                Log(LogLevel.Success, UpdateStatus);
                return;
            }

            Log(LogLevel.Success, string.Format(loc["Log.UpdateFound"], latest.Tag));

            var asset = latest.FindAssetByName(UpdateSource.AssetName);
            if (asset is null)
            {
                UpdateStatus = string.Format(loc["Update.NoAsset"], UpdateSource.AssetName);
                Log(LogLevel.Error, UpdateStatus);
                return;
            }

            var destination = Path.Combine(
                AppPaths.BaseDirectory, UpdateSource.BuildAssetFileName(latest.Tag));

            // 进度只喂给按钮旁边那个百分比（用户 2026-09-22 要求）。
            // 刻意**不写日志**：一个几 MB 的包会触发几十次回调，逐条记只会把日志淹掉。
            var updateProgress = new Progress<DownloadProgress>(p => UpdateProgress = p.Percent);

            IsUpdateDownloading = true;
            UpdateProgress = 0;

            UpdateStatus = string.Format(loc["Update.Downloading"], latest.Tag);
            Log(LogLevel.Info, string.Format(loc["Log.DownloadBegin"], $"{asset.Name}（{asset.SizeText}）"));
            Log(LogLevel.Info, "  " + Services.GitHubMirror.Apply(asset.DownloadUrl, Mirror));

            await _download!.DownloadAsync(
                asset.DownloadUrl,
                destination,
                progress: updateProgress,
                CancellationToken.None,
                retry =>
                {
                    if (retry.IsChannelSwitch)
                    {
                        Log(LogLevel.Warning, string.Format(
                            loc["Log.DownloadFallback"], asset.Name, retry.Reason));
                        return;
                    }

                    Log(LogLevel.Warning, string.Format(
                        retry.IsFallback ? loc["Log.DownloadRetryFallback"] : loc["Log.DownloadRetry"],
                        asset.Name,
                        retry.Reason,
                        retry.Delay.TotalSeconds,
                        retry.Attempt,
                        retry.MaxAttempts));
                },
                BuildDownloadFallback(repo, asset));

            // 先验「这是个 exe 吗」：错误页也可能 200 + 一堆字节，直接启动只会得到
            // 「不是有效的 Win32 应用程序」，而那个提示指向不了真正的原因（网络中间那层）。
            if (!LooksLikeExecutable(destination))
            {
                UpdateStatus = string.Format(loc["Update.NotExecutable"], destination);
                Log(LogLevel.Error, UpdateStatus);
                return;
            }

            UpdateStatus = string.Format(loc["Update.Downloaded"], destination);
            Log(LogLevel.Success, string.Format(loc["Log.DownloadComplete"], destination));

            await HandOverToUpdatedCopyAsync(destination);
        }
        catch (Exception ex)
        {
            UpdateStatus = string.Format(loc["Update.Failed"], NetworkErrors.Describe(ex));
            Log(LogLevel.Error, UpdateStatus);
        }
        finally
        {
            IsCheckingUpdate = false;

            // 收掉百分比：留着 100% 没有意义（结论那句话才是结果），而它一隐藏就自然消失。
            IsUpdateDownloading = false;
            UpdateProgress = 0;

            CheckUpdateCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>下回来的头两个字节是不是 PE 的 <c>MZ</c>。</summary>
    private static bool LooksLikeExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[2];
            return stream.ReadAtLeast(head, 2, throwOnEndOfStream: false) == 2
                   && UpdateSource.HasPortableExecutableHeader(head);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 启动新下载的那一份，确认它活着之后再退出自己（用户要求「打开刚下载的软件，
    /// 然后销毁旧版软件进程」）。
    ///
    /// ⚠️ 为什么不是「先退出自己再启动」：下载下来的 exe 是**框架依赖**的（Release 里那个
    ///     只有 3 MB 左右），机器上没装 .NET 8 桌面运行时时它会弹个系统错误框然后立刻退出。
    ///     先退出自己就等于把用户手里的程序清空了 —— 那种失败方式最难恢复。
    ///     所以这里等一小会儿：**秒退就判失败、并且不退出**，把原因写清楚。
    ///
    /// 退出走的是 <see cref="Application.Shutdown()"/>（正常关窗路径）而不是
    /// <c>Environment.Exit</c>：前者会触发关窗补写存档、摘掉系统主题订阅等收尾工作，
    /// 后者会跳过它们 —— 而「更新完发现设置丢了」是很难联想到更新这一步的。
    /// </summary>
    private async Task HandOverToUpdatedCopyAsync(string path)
    {
        var loc = LocalizationService.Instance;

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(path)
            {
                // UseShellExecute 才走「像用户双击那样启动」这条路：
                // 没有它会以当前进程的身份直接 exec，权限/工作目录与双击不一致。
                UseShellExecute = true,
                WorkingDirectory = AppPaths.BaseDirectory,
            });
        }
        catch (Exception ex)
        {
            UpdateStatus = string.Format(loc["Update.LaunchFailed"], NetworkErrors.Describe(ex));
            Log(LogLevel.Error, UpdateStatus);
            return;
        }

        if (process is null)
        {
            UpdateStatus = loc["Update.LaunchFailedUnknown"];
            Log(LogLevel.Error, UpdateStatus);
            return;
        }

        using (process)
        {
            Log(LogLevel.Info, loc["Log.UpdateLaunching"]);

            // 给它一点时间：起得来就什么都不发生，起不来（缺运行时的典型表现）会很快退出。
            await Task.Delay(TimeSpan.FromSeconds(2));

            if (process.HasExited)
            {
                UpdateStatus = string.Format(loc["Update.LaunchDied"], process.ExitCode, path);
                Log(LogLevel.Error, UpdateStatus);
                Log(LogLevel.Error, loc["Log.UpdateKeptRunning"]);
                return;
            }
        }

        Log(LogLevel.Success, string.Format(loc["Log.UpdateHandOver"], path));

        // 这里写了完整的 System.Windows 前缀，而不是加一条 using：
        // 视图模型里主动结束应用是个**刻意为之**的例外（用户要求「销毁旧版软件进程」），
        // 让它在代码里显眼一点，比藏在一个 using 后面好。
        // 用 ?. 是因为无界面入口（--run / --selftest）没有 Application —— 那种情况下没有窗口要关，
        // 进程会在它自己的流程结束时退掉。
        System.Windows.Application.Current?.Shutdown();
    }

    private async Task VerifyTokenAsync()
    {
        var loc = LocalizationService.Instance;
        var token = GitHubToken?.Trim() ?? string.Empty;

        IsVerifyingToken = true;
        TokenStatus = loc["Settings.GitHubToken.Verifying"];
        VerifyTokenCommand.RaiseCanExecuteChanged();

        try
        {
            using var client = NetworkClientFactory.Create(
                Proxy,
                TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 10, 600)),
                gitHubApiHeaders: true);

            // 带 Token 时用 /user：既能验证 Token，又能拿到登录名（用户一眼认得出是不是自己那个）
            // 匿名时用 /rate_limit：它是公开端点，能直接查到当前额度
            var path = string.IsNullOrEmpty(token) ? "/rate_limit" : "/user";
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseAsset.ApiRoot + path);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            }

            using var response = await client.SendAsync(request);
            var remaining = ReadRateLimitHeader(response, "x-ratelimit-remaining");
            var limit = ReadRateLimitHeader(response, "x-ratelimit-limit");

            if (!response.IsSuccessStatusCode)
            {
                TokenStatus = string.Format(loc["Settings.GitHubToken.Invalid"], (int)response.StatusCode);
                return;
            }

            if (string.IsNullOrEmpty(token))
            {
                TokenStatus = string.Format(loc["Settings.GitHubToken.Anonymous"], remaining, limit);
                return;
            }

            var login = string.Empty;
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (json.RootElement.TryGetProperty("login", out var node))
                {
                    login = node.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                // 拿不到登录名不影响结论：HTTP 200 已经说明 Token 可用
            }

            TokenStatus = string.Format(loc["Settings.GitHubToken.Valid"], login, remaining, limit);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            TokenStatus = string.Format(loc["Settings.GitHubToken.CheckFailed"], NetworkErrors.Describe(ex));
        }
        finally
        {
            IsVerifyingToken = false;
            VerifyTokenCommand.RaiseCanExecuteChanged();
        }
    }

    private static string ReadRateLimitHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? "?" : "?";

    // ── 启动图 / 下载源 ─────────────────────────────────────────

    /// <summary>
    /// 选一张图片（启动图或图标）。只做「文件存在」的检查，**不校验格式与尺寸** ——
    /// hekate 的启动图与 Nyx 的图标都是 bmp，但硬拦别的格式只会碍事（用户可能先选好再自己转），
    /// 真正的判定交给 hekate / Nyx 自己（两者都有「找不到就用默认图」的回退，不是致命错误）。
    /// </summary>
    private void PickBootImage(string current, string titleKey, Action<string> apply)
    {
        var loc = LocalizationService.Instance;
        var dialog = new OpenFileDialog
        {
            Title = loc[titleKey],
            Filter = loc["Global.Boot.Logo.Filter"],
            CheckFileExists = true,
        };

        if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
            dialog.FileName = Path.GetFileName(current);
        }

        if (dialog.ShowDialog() == true)
        {
            apply(dialog.FileName);
        }
    }

    /// <summary>把所有下载源清空 = 恢复内置默认地址（清空即「用默认」）。</summary>
    private void ResetRepoSources()
    {
        foreach (var source in RepoSources)
        {
            source.Value = string.Empty;
        }

        Log(LogLevel.Info, LocalizationService.Instance["Settings.Repo.Reset"]);
    }

    private void ResetAssetSlots()
    {
        // 清空两个框 = 回到默认（默认值是按语言现算的，所以这里不能把默认值写进去当「用户值」，
        // 否则切语言时那一行就永远停在旧语言的默认文件名上）。
        foreach (var slot in AssetSlotGroups.SelectMany(group => group.Slots))
        {
            slot.Address = string.Empty;
            slot.FileName = string.Empty;
        }

        Log(LogLevel.Info, LocalizationService.Instance["Settings.Slots.Reset"]);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.00} MB",
        >= 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B",
    };

    private static void OpenFolder(string path)
    {
        try
        {
            AppPaths.EnsureDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            // 打开资源管理器失败时静默忽略
        }
    }

    /// <param name="PreDownloaded">
    /// 这个文件**已经躺在 <paramref name="DestinationPath"/> 上了**，不必再下载。
    ///
    /// 目前只有目录槽会用到：它先试「整仓打包一次取回」（1 次请求、不占 API 配额），
    /// 解包出来的文件名与「逐个文件下载」**完全一致**，于是这里只需把下载那一步跳过。
    /// 用「标记」而不是「事后 File.Exists 判断」，是因为后者会把**上一次运行残留的旧文件**
    /// 也当成「已下载」—— 那会让「换个版本重跑」静默沿用旧内容。
    /// </param>
    private sealed record DownloadItem(
        RepoSpec Repo,
        AssetPick Pick,
        string AssetId,
        string DestinationPath,
        double Weight,
        DownloadFallback? Fallback,
        bool PreDownloaded = false);

    private sealed class ComponentJob
    {
        public ComponentJob(ComponentViewModel component) => Component = component;

        public ComponentViewModel Component { get; }

        public List<DownloadItem> Items { get; } = new();

        /// <summary>该组件在总进度中的权重（字节数 + 解压预留）。</summary>
        public double TotalWeight => Items.Sum(i => i.Weight) * 1.1 + 1;
    }
}
