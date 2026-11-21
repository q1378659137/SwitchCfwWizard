using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;
using SwitchCfwWizard.Models;
using SwitchCfwWizard.SdReset;
using SwitchCfwWizard.Services;
using SwitchCfwWizard.ViewModels;

namespace SwitchCfwWizard;

public partial class App : Application
{
    /// <summary>是否处于命令行自检模式（只解析界面，不联网）。</summary>
    internal static bool IsSelfTest { get; private set; }

    /// <summary>是否处于命令行执行模式（真实联网跑一次完整流程）。</summary>
    internal static bool IsHeadlessRun { get; private set; }

    /// <summary>无人值守模式：不弹任何对话框，避免卡住。</summary>
    private static bool IsUnattended => IsSelfTest || IsHeadlessRun;

    /// <summary>
    /// `--shot[=路径]`：自检时**额外把窗口渲染成一张 PNG**（不写路径 = 输出到 selftest.png，
    /// 与 selftest.log 同目录）。用户 2026-09-22 要求「检查更新的地址」那一段**不折行** ——
    /// 这类要求是纯视觉的：坐标能证明边缘对齐，但「这段话被折成了三行」「框里的示例被截断了」
    /// 只有图能说清。所以留一条出图的路，省得靠猜（也省得让用户截图来问）。
    /// </summary>
    internal static string? ShotPath { get; private set; }

    /// <summary>
    /// `--shot-focus=属性名`：出图时滚到哪个输入框（默认「检查更新的地址」）。
    /// 改哪个区域就先滚到那个区域的框 —— 否则它多半在滚动区外，图上看不到（白出一次）。
    /// </summary>
    internal static string? ShotFocus { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 「格式化与重建虚拟系统」的提权辅助进程：主进程用 runas 再起一个自己，
        // 由它独占打开物理磁盘并调用原生格式化组件。
        //
        // ⚠️ 必须在**创建任何窗口之前**处理：否则会先弹出主界面再退出，用户会看到窗口闪一下。
        //    参数形状由 SdResetWorker 自己校验（管道名 + 父进程号），这里只做转发。
        if (e.Args.Length == 3
            && string.Equals(e.Args[0], "--sd-reset-worker", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(SdResetWorker.Run(e.Args));
            return;
        }

        IsSelfTest = e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
        IsHeadlessRun = e.Args.Any(a => string.Equals(a, "--run", StringComparison.OrdinalIgnoreCase));

        // ⚠️ 只认 `--shot` 与 `--shot=…` 两种写法：写成 StartsWith("--shot") 会把
        //    `--shot-focus=…` 也吞进来（它同样以 --shot 开头）。
        var shotArg = e.Args.FirstOrDefault(
            a => string.Equals(a, "--shot", StringComparison.OrdinalIgnoreCase)
                 || a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase));

        if (shotArg is not null)
        {
            var separator = shotArg.IndexOf('=');
            var value = separator < 0 ? null : shotArg[(separator + 1)..].Trim('"');

            ShotPath = string.IsNullOrWhiteSpace(value)
                ? Path.Combine(AppPaths.BaseDirectory, "selftest.png")
                : Path.GetFullPath(value);
        }

        var focusArg = e.Args.FirstOrDefault(
            a => a.StartsWith("--shot-focus=", StringComparison.OrdinalIgnoreCase));

        if (focusArg is not null)
        {
            ShotFocus = focusArg["--shot-focus=".Length..].Trim('"');
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain", args.ExceptionObject as Exception);

        try
        {
            StartUp(e);
        }
        catch (Exception ex)
        {
            WriteCrashLog("OnStartup", ex);

            if (IsUnattended)
            {
                TryWriteText(
                    Path.Combine(AppPaths.BaseDirectory, IsHeadlessRun ? "run.log" : "selftest.log"),
                    "FAILED" + Environment.NewLine + ex + Environment.NewLine);
                Shutdown(2);
                return;
            }

            MessageBox.Show(
                ex.ToString(),
                "Switch CFW Wizard - 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// 退出时摘掉系统主题变化的静态事件订阅。
    /// 静态事件（<c>SystemEvents.UserPreferenceChanged</c>）不取消的话，
    /// 进程结束后仍有人引用着我们的静态字段 —— 这是泄漏，也是「退出不干净」。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        ThemeService.StopWatching();
        base.OnExit(e);
    }

    private void StartUp(StartupEventArgs e)
    {
        BindingErrorRecorder? recorder = null;

        if (IsSelfTest)
        {
            recorder = BindingErrorRecorder.Attach();
        }

        var viewModel = new MainViewModel();
        viewModel.EnsureHttpClients();

        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        MainWindow = window;

        if (IsSelfTest)
        {
            RunSelfTest(window, recorder!);
            return;
        }

        if (IsHeadlessRun)
        {
            RunHeadless(window, viewModel);
            return;
        }

        // 只有**正常带界面启动**这一条路才监听系统主题变化。无人值守那两条
        // （--selftest / --run）刻意不订阅：监听在那里没有任何用处，却会让进程
        // 挂着一个静态事件订阅（回归里跑完不退出就是这个味道）。
        // 「跟随系统」这一档即使监听失败也只退化成「启动时读一次」，不影响出图。
        ThemeService.StartWatching();

        window.Show();
    }

    /// <summary>
    /// 命令行执行一次完整流程（真实联网下载 + 生成配置），日志写入 run.log。
    /// 用法：SwitchCfwWizard.exe --run
    ///
    /// 组件勾选、引导项、90DNS 等全部沿用 settings.json。**首次运行（文件里没有组件勾选记录）
    /// 时在这里补一次默认全选**：界面路径没有这一步（用户要求打开软件时全不勾、自己挑，
    /// 见 <see cref="MainViewModel"/> 构造函数），而 <c>--run</c> 是无人值守路径，没有界面可以勾，
    /// 不自动选就会撞上「一个组件都没勾」的保护分支，提示一句就返回 —— 端到端验证等于空跑。
    /// </summary>
    private async void RunHeadless(MainWindow window, MainViewModel viewModel)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowInTaskbar = false;
        viewModel.SuppressShellOpen = true;

        try
        {
            window.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            // 必须排在 RunOnceAsync 之前：RunAsync 一开始就读勾选状态，晚了等于没勾。
            // 这里不打日志 —— RunAsync 开头的 Logs.Clear() 会把它清掉（run.log 里看不到），
            // 组件勾选状态由 BuildRunReport 的 [x] 列体现，够用了。
            viewModel.EnsureComponentsSelected();

            await viewModel.RunOnceAsync();
        }
        catch (Exception ex)
        {
            WriteCrashLog("HeadlessRun", ex);
        }
        finally
        {
            var failed = viewModel.Logs.Any(l => l.Level == LogLevel.Error);
            TryWriteText(Path.Combine(AppPaths.BaseDirectory, "run.log"), BuildRunReport(viewModel));
            Shutdown(failed ? 1 : 0);
        }
    }

    private static string BuildRunReport(MainViewModel viewModel)
    {
        var report = new StringBuilder();

        report.AppendLine($"总体状态: {viewModel.OverallStatus}");
        report.AppendLine($"输出目录: {AppPaths.OutputRoot}");
        report.AppendLine($"下载目录: {AppPaths.DownloadRoot}");
        report.AppendLine();

        foreach (var component in viewModel.Components)
        {
            report.AppendLine(
                $"[{(component.IsSelected ? "x" : " ")}] {component.Title} · {component.VersionText} · {component.ChannelText} · {component.Status} · {component.Progress:0}%");

            foreach (var asset in component.Assets)
            {
                report.AppendLine($"      - {asset.DisplayName}  {asset.Status}  {asset.Progress:0}%  {asset.Detail}");
            }
        }

        report.AppendLine();
        report.AppendLine("──── 运行日志 ────");

        foreach (var entry in viewModel.Logs)
        {
            report.AppendLine($"{entry.TimeText} [{entry.Level}] {entry.Message}");
        }

        return report.ToString();
    }

    /// <summary>
    /// 无界面自检：构造窗口（解析 XAML）、跑一遍布局与绑定，收集绑定错误后退出。
    /// 用于 CI / 命令行验证，正常使用不会触发。
    /// </summary>
    private void RunSelfTest(Window window, BindingErrorRecorder recorder)
    {
        var report = new StringBuilder();

        // 逐段落盘：即使中途卡住或崩溃，也能从 selftest.log 看到进行到哪一步
        void Flush() => TryWriteText(Path.Combine(AppPaths.BaseDirectory, "selftest.log"), report.ToString());

        Flush();
        report.AppendLine("SELFTEST: startup ok");

        try
        {
            Flush();
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.ShowInTaskbar = false;
            report.AppendLine("SELFTEST: showing window");
            Flush();

            window.Show();
            report.AppendLine("SELFTEST: window shown");
            Flush();

            // 让布局与数据绑定全部完成
            Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            report.AppendLine("SELFTEST: context-idle reached");
            Flush();

            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            report.AppendLine("SELFTEST: application-idle reached");
            report.AppendLine("SELFTEST: window rendered");
            Flush();
        }
        catch (Exception ex)
        {
            report.AppendLine("SELFTEST: FAILED " + ex);
            Flush();
        }

        // ── 主题资源体检 ────────────────────────────────────────────
        // 两套调色板各拿**真实的**资源字典过一遍，再逐个键**读回来核对颜色**。
        // 这条体检抓的是三类症状全是「切了主题有一块颜色不动」、而且都不报错的毛病：
        //   ① 键名拼错（ThemePalette 里写的键在 Styles.xaml 里不存在）
        //   ② Styles.xaml 漏定义某个键（两套表里有、资源里没有）
        //   ③ 资源里的对象被换掉了但颜色没写对（例如色值解析异常）
        var themeProblems = new List<string>();
        var savedPreference = ThemeService.Preference;

        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            ThemeService.Apply(mode);

            if (ThemeService.ResolvedKeysOnLastApply != ThemePalette.Keys.Count)
            {
                themeProblems.Add(
                    $"{mode}：只处理了 {ThemeService.ResolvedKeysOnLastApply}/{ThemePalette.Keys.Count} 个颜色键");
            }

            foreach (var key in ThemeService.MissingKeysOnLastApply)
            {
                themeProblems.Add($"{mode}：资源字典里没有 {key}");
            }

            var expected = ThemePalette.For(mode);
            foreach (var key in ThemePalette.Keys)
            {
                var want = (Color)ColorConverter.ConvertFromString(expected[key])!;

                if (ThemeService.Resource(key) is not SolidColorBrush brush)
                {
                    themeProblems.Add($"{mode}：{key} 取不到画刷");
                }
                else if (brush.Color != want)
                {
                    themeProblems.Add($"{mode}：{key} 的颜色没换过来（{brush.Color} ≠ {want}）");
                }
            }
        }

        // ── 端到端：**真实的控件**有没有跟着主题变 ──────────────────
        // 上面那条只证明了「资源字典里的值换对了」。这条回答的是真正的问题：
        // 换成暗夜之后，**界面上已经建好的控件**会不会重绘。
        // 它是 StaticResource/DynamicResource 那个坑的唯一直接判据 —— 资源换对了但引用方式
        // 写成了 StaticResource 的话，界面就是纹丝不动，而且什么都不报。
        static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(root);

            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);

                if (child is T hit)
                {
                    return hit;
                }

                if (FindDescendant<T>(child) is { } deep)
                {
                    return deep;
                }
            }

            return null;
        }

        ThemeService.Apply(ThemeMode.Light);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        var lightWindowBg = (window.Background as SolidColorBrush)?.Color;
        var lightTextFg = (FindDescendant<System.Windows.Controls.TextBlock>(window)?.Foreground as SolidColorBrush)?.Color;

        ThemeService.Apply(ThemeMode.Dark);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        var darkWindowBg = (window.Background as SolidColorBrush)?.Color;
        var darkTextFg = (FindDescendant<System.Windows.Controls.TextBlock>(window)?.Foreground as SolidColorBrush)?.Color;

        if (lightWindowBg is null || darkWindowBg is null || lightTextFg is null || darkTextFg is null)
        {
            themeProblems.Add("端到端检查失效：窗口背景或示例文本的前景不是 SolidColorBrush");
        }
        else
        {
            if (lightWindowBg == darkWindowBg)
            {
                themeProblems.Add($"窗口背景没有跟着主题变（两次都是 {lightWindowBg}）—— DynamicResource 没生效");
            }

            if (lightTextFg == darkTextFg)
            {
                themeProblems.Add($"文本前景没有跟着主题变（两次都是 {lightTextFg}）—— DynamicResource 没生效");
            }
        }

        report.AppendLine(
            $"SELFTEST: theme-e2e window {lightWindowBg} → {darkWindowBg} / text {lightTextFg} → {darkTextFg}");

        // ── 下拉框「关闭状态」显示的文本 ────────────────────────────
        // 2026-09-20 用户实测报出的两个 bug，根因是同一个：重画 ComboBox 模板时漏了
        // `ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"`。
        // `DisplayMemberPath` 正是通过那个选择器生效的 —— 漏了它，`SelectionBoxItemTemplate`
        // 会是 null，ContentPresenter 于是直接把 `SelectedItem.ToString()` 显示出来
        // （用户看到「LanguageInfo { Code = … }」「SwitchCfwWizard.ViewModels.ThemeInfo」）。
        // ⚠️ 特征是「**展开时正常、关着显示类型名**」—— 因为展开那条路用的是 ItemTemplate。
        //
        // 这里刻意不检查「模板里有没有那一行」，而是**读渲染出来的文本**：
        // 前者只能证明我写了什么，后者才证明用户看到什么。
        static void Collect<T>(DependencyObject root, List<T> sink) where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(root);

            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);

                if (child is T hit)
                {
                    sink.Add(hit);
                }

                Collect(child, sink);
            }
        }

        var combos = new List<System.Windows.Controls.ComboBox>();
        Collect(window, combos);

        var langCombo = combos.FirstOrDefault(c => c.SelectedItem is SwitchCfwWizard.Localization.LanguageInfo);
        var themeCombo = combos.FirstOrDefault(c => c.SelectedItem is ThemeInfo);

        string? langShown = null;
        string? themeShown = null;

        if (langCombo is null || themeCombo is null)
        {
            themeProblems.Add("自检失效：窗口里找不到语言 / 主题下拉框（判定已选中的那一项失败）");
        }
        else
        {
            langShown = FindDescendant<System.Windows.Controls.TextBlock>(langCombo)?.Text;
            themeShown = FindDescendant<System.Windows.Controls.TextBlock>(themeCombo)?.Text;

            var expectedLang = (langCombo.SelectedItem as SwitchCfwWizard.Localization.LanguageInfo)?.DisplayName;
            var expectedTheme = (themeCombo.SelectedItem as ThemeInfo)?.DisplayName;

            if (langShown != expectedLang)
            {
                themeProblems.Add(
                    $"语言下拉框**关闭时**显示的文本是「{langShown}」，应当是「{expectedLang}」"
                    + "（显示成类型名 = ComboBox 模板漏了 ContentTemplateSelector）");
            }

            if (themeShown != expectedTheme)
            {
                themeProblems.Add(
                    $"主题下拉框**关闭时**显示的文本是「{themeShown}」，应当是「{expectedTheme}」");
            }
        }

        report.AppendLine($"SELFTEST: combo-shown lang=\"{langShown}\" theme=\"{themeShown}\"");

        // ── 滚动条：模板结构对不对 ────────────────────────────────
        // 滚动条是**唯一**「画错了会让功能坏掉」的那种重画：拖动与点击翻页由框架的 `Track`
        // 负责，而它靠**名字**找 `PART_Track`（还有 Track.Thumb / Track.*RepeatButton）。
        // 名字写错或结构缺失 ⇒ 滚动条拖不动，而「用鼠标拖一下」这件事离线验不了。
        // 能验的是结构本身：`PART_Track` 找得到、且里面真有 Thumb。
        // ⚠️ 不可见的滚动条（内容没超出时 ScrollViewer 会把它们藏起来）可能还没应用模板，
        //    所以先 `ApplyTemplate()` 强制应用，再验。
        var scrollBars = new List<System.Windows.Controls.Primitives.ScrollBar>();
        Collect(window, scrollBars);

        foreach (var bar in scrollBars)
        {
            bar.ApplyTemplate();

            if (bar.Template?.FindName("PART_Track", bar) is not System.Windows.Controls.Primitives.Track track)
            {
                themeProblems.Add($"滚动条（{bar.Orientation}）的模板里找不到 PART_Track —— 那就是「拖不动」");
            }
            else if (track.Thumb is null)
            {
                themeProblems.Add($"滚动条（{bar.Orientation}）的 Track 里没有 Thumb");
            }
        }

        report.AppendLine($"SELFTEST: scrollbars={scrollBars.Count}");

        // ── boot.dat 的卡片：跨 DataContext 的绑定 + 锚点 ──────────────
        // 勾选框的绑定要跨出分组模板的 DataContext（ComponentCategoryViewModel）到 MainViewModel；
        // ⚠️ 漏写 / 写错时 WPF **不报错**：框照常显示，只是永远是未勾选、点一下也会弹回来，
        //    而那正是「这个开关是坏的」最难被发现的样子（用户会以为是自己没点对）。
        // 所以这里读**渲染出来的**勾选状态与 VM 比对，并且数它只该出现一次（只在「框架」那一组里）。
        //
        // ⚠️⚠️ 锚点是**标题文字**，不是勾选框的 Content：2026-09-22 按用户要求把这张卡片改成与
        //    组件卡片逐项对齐之后，标题从勾选框搬到了旁边的 TextBlock（勾选框的 Content 是空的）——
        //    再按 Content 找会一个都找不到（这次改动当场打穿了原来那条判据，正是它该有的样子）。
        var bootDatName = SwitchCfwWizard.Localization.LocalizationService.Instance["App.Framework.BootDat"];

        var cardTitleTexts = new List<System.Windows.Controls.TextBlock>();
        Collect(window, cardTitleTexts);

        // 从卡片里的任意一个子元素往上找**第一个 Border** = 那张卡片本身
        // （子元素自己的模板子树在它下面，不在父链上，所以沿父链找是对的）。
        static DependencyObject? UpToCard(DependencyObject? node)
        {
            while (node is not null && node is not System.Windows.Controls.Border)
            {
                node = VisualTreeHelper.GetParent(node);
            }

            return node;
        }

        // ⚠️ 判据必须是 IsVisible 而不是「在不在视觉树里」：分组模板是按分类渲染的，
        //    boot.dat 那张卡片在**每个**分组里都实例化了一份，只是被 Visibility 收起来了；
        //    Collapsed 的元素仍在视觉树里，按存在性数会数出「8 个」（分类数）这个假警报。
        //    真正要问的是「用户看得到几张」。
        var visibleTitles = cardTitleTexts
            .Where(t => t.IsVisible && string.Equals(t.Text, bootDatName, StringComparison.Ordinal))
            .ToList();

        var bootDatCount = visibleTitles.Count;
        var bootDatCard = UpToCard(visibleTitles.FirstOrDefault());

        var cardBoxes = new List<System.Windows.Controls.CheckBox>();
        if (bootDatCard is not null)
        {
            Collect(bootDatCard, cardBoxes);
        }

        var bootDatBox = cardBoxes.FirstOrDefault();
        var bootDatChecked = bootDatBox?.IsChecked;

        if (window.DataContext is not SwitchCfwWizard.ViewModels.MainViewModel selfViewModel)
        {
            themeProblems.Add("自检失效：窗口的 DataContext 不是 MainViewModel");
        }
        else if (bootDatBox is null)
        {
            themeProblems.Add(
                $"界面上找不到「{bootDatName}」那张卡片（或卡片里没有勾选框）—— 它应当显示在「框架」分组里");
        }
        else if (bootDatBox.IsChecked != selfViewModel.IncludeBootDat)
        {
            themeProblems.Add(
                $"「{bootDatName}」渲染出来的勾选状态是 {bootDatBox.IsChecked}，而设置里是 "
                + $"{selfViewModel.IncludeBootDat} —— 绑定没有跨到窗口的 DataContext"
                + "（分组模板里漏了 RelativeSource）");
        }

        if (bootDatCount != 1)
        {
            themeProblems.Add(
                $"可见的「{bootDatName}」卡片有 {bootDatCount} 张 —— 应当只在「框架」分组里显示、恰好 1 张");
        }

        report.AppendLine(
            $"SELFTEST: bootdat-card titles={bootDatCount} boxes={cardBoxes.Count} checked={bootDatChecked}");

        // ── 配置模板（2026-11-21）──
        // 用户要求「选模板后自动勾选上对应位置」「把 json 的所有信息全部导入」。
        // 这里真的调一次 ApplyTemplate，然后：
        //   ① 核对界面状态（勾选 / 选项 / 屏蔽开关）；
        //   ② 用 MaterializeInto 把界面搬回一份 AppSettings，与模板**逐字段**比对 ——
        //      这一条才防得住「某个字段忘了搬」（那种 bug 只在单个字段上表现，肉眼看不出来）。
        // 之后**原样还原**：自检不能把用户的勾选留在测试状态。
        var tplVm = window.DataContext as MainViewModel;
        var templates = tplVm?.Templates ?? [];
        var studyTemplate = templates.FirstOrDefault(t => t.Id == "study");
        var playTemplate = templates.FirstOrDefault(t => t.Id == "play");
        var templatesPresent = templates.Count >= 2 && studyTemplate is not null && playTemplate is not null;

        var templateApplyOk = false;
        var templateFullOk = false;
        var templateFullDetail = "skipped";
        var templateDetail = "skipped";

        if (templatesPresent && tplVm is not null)
        {
            var selectedBackup = tplVm.Components.ToDictionary(c => c.Kind, c => c.IsSelected);
            var optionBackup = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var component in tplVm.Components)
            {
                foreach (var option in component.OptionGroups.SelectMany(g => g.Options))
                {
                    optionBackup[$"{component.Kind}/{option.Key}"] = option.Value;
                }
            }

            var blankSys = tplVm.BlankSerialSysmmc;
            var blankEmu = tplVm.BlankSerialEmummc;
            var dnsSys = tplVm.Use90DnsSysmmc;
            var dnsEmu = tplVm.Use90DnsEmummc;

            try
            {
                tplVm.ApplyTemplate(studyTemplate!);
                var studySysPatch = tplVm.Components.First(c => c.Kind == ComponentKind.SysPatch).IsSelected;
                var studyDnsMitm = tplVm.Components
                    .First(c => c.Kind == ComponentKind.Atmosphere).GetOptionValue("enable_dns_mitm");
                var studyBlank = tplVm.BlankSerialSysmmc && tplVm.BlankSerialEmummc
                                 && tplVm.Use90DnsSysmmc && tplVm.Use90DnsEmummc;

                // 全量核对：界面 → 存档 的映射再跑一遍，结果必须与模板一致
                var materialize = typeof(MainViewModel).GetMethod("MaterializeInto",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var expected = TemplateStore.Load(studyTemplate!);
                var actual = new AppSettings();
                materialize?.Invoke(tplVm, [actual]);
                var diffs = materialize is null ? ["MaterializeInto 找不到"] : CompareSettings(expected, actual);
                templateFullOk = diffs.Count == 0;
                templateFullDetail = templateFullOk ? "same" : string.Join("、", diffs.Take(6));

                tplVm.ApplyTemplate(playTemplate!);
                var playSysPatchOff = !tplVm.Components.First(c => c.Kind == ComponentKind.SysPatch).IsSelected;
                var playDnsMitm = tplVm.Components
                    .First(c => c.Kind == ComponentKind.Atmosphere).GetOptionValue("enable_dns_mitm");
                var playBlankOff = !tplVm.BlankSerialSysmmc && !tplVm.BlankSerialEmummc;

                templateApplyOk = studySysPatch && studyDnsMitm == "1" && studyBlank
                                  && playSysPatchOff && playDnsMitm == "0" && playBlankOff;

                templateDetail =
                    $"study(syspatch={studySysPatch},dns_mitm={studyDnsMitm},blank={studyBlank}) "
                    + $"play(syspatch_off={playSysPatchOff},dns_mitm={playDnsMitm},blank_off={playBlankOff})";
            }
            finally
            {
                // 还原：勾选、每个选项、四个开关都放回去，别让自检改了用户的配置。
                foreach (var component in tplVm.Components)
                {
                    component.IsSelected = selectedBackup[component.Kind];
                }

                foreach (var component in tplVm.Components)
                {
                    foreach (var option in component.OptionGroups.SelectMany(g => g.Options))
                    {
                        if (optionBackup.TryGetValue($"{component.Kind}/{option.Key}", out var value))
                        {
                            option.Value = value;
                        }
                    }
                }

                tplVm.BlankSerialSysmmc = blankSys;
                tplVm.BlankSerialEmummc = blankEmu;
                tplVm.Use90DnsSysmmc = dnsSys;
                tplVm.Use90DnsEmummc = dnsEmu;
            }
        }

        report.AppendLine(
            $"SELFTEST: templates present={templatesPresent} count={templates.Count} apply={templateApplyOk} {templateDetail}");
        report.AppendLine(
            $"SELFTEST: template-full-import ok={templateFullOk} diff={templateFullDetail}");

        if (!templatesPresent)
        {
            themeProblems.Add("输出目录里应自带「联机」「学习」两份配置模板（检查 csproj 的 EmbeddedResource 是否丢）");
        }
        else if (!templateApplyOk)
        {
            themeProblems.Add("应用配置模板没有真的改到界面（勾选 / 选项 / 屏蔽开关至少有一处没生效）");
        }

        if (templatesPresent && !templateFullOk)
        {
            themeProblems.Add(
                "模板没有把 json 的全部信息导入（应用后再存回去与模板不一致："
                + templateFullDetail + "）");
        }

        // ── 与 Sys-patch 卡片逐项比对（用户 2026-09-22：「以 Sys-patch 那张卡片为准」）──
        // 「我写了 FontSize=14」证明不了什么 —— 直接拿**运行时那张卡片**当参照物：
        // 标题 / 说明 / 状态文字三项的字号、字重、前景色都要与它一致。
        // ⚠️ 这类「两处应当长得一样」的约定最容易在后续某次改动里悄悄分叉，而分叉是**静默**的。
        var selfVm = window.DataContext as SwitchCfwWizard.ViewModels.MainViewModel;
        var sysPatch = selfVm?.Components
            .FirstOrDefault(c => c.Kind == SwitchCfwWizard.Models.ComponentKind.SysPatch);

        System.Windows.Controls.TextBlock? TextIn(DependencyObject? root, string text)
        {
            if (root is null)
            {
                return null;
            }

            var texts = new List<System.Windows.Controls.TextBlock>();
            Collect(root, texts);
            return texts.FirstOrDefault(
                t => t.IsVisible && string.Equals(t.Text, text, StringComparison.Ordinal));
        }

        if (selfVm is null || bootDatCard is null)
        {
            // 上面已经报过「找不到卡片」了，这里不重复刷一条。
        }
        else if (sysPatch is null)
        {
            themeProblems.Add("自检失效：找不到 Sys-patch 组件 —— 字体比对的参照物没了");
        }
        else
        {
            var referenceCard = UpToCard(cardTitleTexts.FirstOrDefault(
                t => t.IsVisible && string.Equals(t.Text, sysPatch.Title, StringComparison.Ordinal)));

            if (referenceCard is null)
            {
                themeProblems.Add(
                    $"自检失效：界面上找不到 Sys-patch 那张卡片（标题「{sysPatch.Title}」）—— 比对无法进行");
            }
            else
            {
                static string Describe(System.Windows.Controls.TextBlock t)
                    => $"{t.FontSize}/{t.FontWeight}/"
                       + $"{(t.Foreground as System.Windows.Media.SolidColorBrush)?.Color}";

                var parity = new List<string>();

                void Compare(string label, string bootDatText, string sysPatchText)
                {
                    var mine = TextIn(bootDatCard, bootDatText);
                    var theirs = TextIn(referenceCard, sysPatchText);

                    if (mine is null || theirs is null)
                    {
                        themeProblems.Add(
                            $"{label}：找不到要比对的那段文字"
                            + $"（boot.dat 侧 {(mine is null ? "缺失" : "在")} / Sys-patch 侧 "
                            + $"{(theirs is null ? "缺失" : "在")}）");
                        return;
                    }

                    var same = mine.FontSize == theirs.FontSize
                               && mine.FontWeight == theirs.FontWeight
                               && Equals(mine.Foreground, theirs.Foreground);

                    parity.Add($"{label} {Describe(mine)}");

                    if (!same)
                    {
                        themeProblems.Add(
                            $"{label}与 Sys-patch 卡片不一致：boot.dat 是 {Describe(mine)}、"
                            + $"Sys-patch 是 {Describe(theirs)} —— 用户要求以 Sys-patch 为准");
                    }
                }

                var loc = SwitchCfwWizard.Localization.LocalizationService.Instance;
                Compare("标题", bootDatName, sysPatch.Title);
                Compare("说明", loc["App.Framework.BootDat.Desc"], sysPatch.Description);
                Compare("状态文字", selfVm.BootDatStatusText, sysPatch.Status);

                report.AppendLine(
                    $"SELFTEST: card-parity syspatch=\"{sysPatch.Title}\" " + string.Join(" | ", parity));
            }
        }

        // ── boot.dat 卡片上的进度条：**始终**显示（用户 2026-09-22 要求）──────
        // 与上面那条同一个理由：`Visibility` 绑错了，XAML 照样加载、卡片照样在，
        // 只是那一行**永远不出现** —— 而它一开始正是「有下载状态才显示」，所以
        // 「有人把那个绑定加回来」是这条判据真正要防的事。所以读**渲染出来的** IsVisible。
        if (bootDatCard is not null)
        {
            var cardBars = new List<System.Windows.Controls.ProgressBar>();
            Collect(bootDatCard, cardBars);

            var visibleCardBars = cardBars.Where(b => b.IsVisible).ToList();

            if (visibleCardBars.Count != 1)
            {
                themeProblems.Add(
                    "boot.dat 卡片上的进度条应当**始终**显示、恰好 1 个，实际可见 "
                    + $"{visibleCardBars.Count} 个（卡片里共 {cardBars.Count} 个）—— "
                    + "「始终显示」靠的是那一行**不加** Visibility 绑定，加回来就会在没下过时整行消失");
            }

            report.AppendLine(
                $"SELFTEST: bootdat-bar visible={visibleCardBars.Count}/{cardBars.Count}"
                + $" value={visibleCardBars.FirstOrDefault()?.Value}");
        }

        // ── 暗夜下不该有「近黑的前景文字」──────────────────────────
        // 重画模板时漏掉 `Foreground="{TemplateBinding Foreground}"` 这类「属性传递」，
        // 控件就会退回自己的元数据默认值（`SystemColors.ControlTextBrush` = **黑**）——
        // 在近黑背景上等于「字没了」，而**不报错、不崩溃**。
        //
        // 2026-09-20 用户报的「『配置选项』四个字是黑的」就是这么来的（Expander 模板漏了那一行）；
        // 上一次是 ComboBox 漏了 `ContentTemplateSelector`。两次都是「重画模板时漏了默认模板里的一行」，
        // 而两次都只能靠用户肉眼发现 —— 所以这里加一条**通用**判据：
        // 暗夜主题下，界面上任何可见文字的前景亮度都不得低于 0.15。
        static double Luminance(System.Windows.Media.Color c)
            => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

        ThemeService.Apply(ThemeMode.Dark);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        var allTexts = new List<System.Windows.Controls.TextBlock>();
        Collect(window, allTexts);

        var tooDark = allTexts
            .Where(t => t.IsVisible
                        && !string.IsNullOrWhiteSpace(t.Text)
                        && t.Foreground is SolidColorBrush b
                        && Luminance(b.Color) < 0.15)
            .Select(t => $"「{t.Text.Trim()}」{((SolidColorBrush)t.Foreground).Color}")
            .Distinct()
            .Take(10)
            .ToList();

        if (tooDark.Count > 0)
        {
            themeProblems.Add(
                "暗夜主题下这些文字的前景接近黑（在近黑底上等于看不见，通常是模板漏了 Foreground 传递）："
                + string.Join("、", tooDark));
        }

        // ── 两个地址框的「框内格式提示」 ────────────────────────────
        // 用户 2026-09-21 要求「把可填的样式写清楚」。这行提示的可见性是**派生属性**驱动的
        // （地址为空时显示），而绑定写错/派生属性忘了发通知，症状都只是「提示不出现」——
        // 不报错、界面照样能用，只是那一行说明没了。所以这里读**渲染出来的**状态，并做一次往返：
        // 空 → 提示在；填上 → 提示消失；清空 → 提示回来。
        var bootDatHintText = SwitchCfwWizard.Localization.LocalizationService.Instance["Settings.BootDatUrl.Placeholder"];
        var updateHintText = SwitchCfwWizard.Localization.LocalizationService.Instance["Settings.UpdateUrl.Placeholder"];

        int VisibleHintCount(string hint)
        {
            var texts = new List<System.Windows.Controls.TextBlock>();
            Collect(window, texts);
            return texts.Count(t => t.IsVisible && string.Equals(t.Text, hint, StringComparison.Ordinal));
        }

        // ⚠️ 这两个框在**高级设置**里，而高级设置默认是折叠的 ⇒ 祖先 `Collapsed` 时
        // `IsVisible` 为 false，直接数会得到 0（我第一版就这么错了，被这条自检自己抓出来）。
        // 「用户能不能看到」本来就该在**展开之后**问 —— 这也正是用户看它的方式。
        var hintViewModelForExpand = window.DataContext as SwitchCfwWizard.ViewModels.MainViewModel;
        var originalShowAdvanced = hintViewModelForExpand?.ShowAdvancedSettings ?? false;

        if (hintViewModelForExpand is not null)
        {
            hintViewModelForExpand.ShowAdvancedSettings = true;
            Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        }

        var bootDatHintVisible = VisibleHintCount(bootDatHintText);
        var updateHintVisible = VisibleHintCount(updateHintText);

        report.AppendLine($"SELFTEST: url-hints bootdat={bootDatHintVisible} update={updateHintVisible} (empty state)");

        // ── 两个地址框**同宽同对齐**（用户 2026-09-22，以「boot.dat 下载地址」为准）──
        // 判据是**渲染出来的坐标**：XAML 里写了多少 margin 证明不了对齐 —— 父容器缩进、父级宽度都会
        // 影响最终位置；而「框窄了一截、前面空出一块」这种事肉眼要凑近才看得出、改坏了也不报错。
        // ⚠️ 定位用**绑定名**（`BootDatUrl` / `UpdateUrl`），不再靠「旁边的文字」：上一轮靠文字定位，
        //    用户一改文案那条判据就自己失效了；按绑定找与文案无关，也不会撞上重名（上一轮踩过）。
        double LeftIn(System.Windows.UIElement element)
            => element.TransformToAncestor(window).Transform(new System.Windows.Point(0, 0)).X;

        System.Windows.Controls.TextBox? BoxFor(string propertyName)
        {
            var boxes = new List<System.Windows.Controls.TextBox>();
            Collect(window, boxes);

            return boxes.FirstOrDefault(box =>
                System.Windows.Data.BindingOperations
                    .GetBinding(box, System.Windows.Controls.TextBox.TextProperty)?.Path?.Path
                == propertyName);
        }

        var bootDatUrlBox = BoxFor(nameof(SwitchCfwWizard.ViewModels.MainViewModel.BootDatUrl));
        var updateUrlBox = BoxFor(nameof(SwitchCfwWizard.ViewModels.MainViewModel.UpdateUrl));

        if (bootDatUrlBox is null || updateUrlBox is null)
        {
            themeProblems.Add(
                "对齐判据失效：找不到那两个地址框"
                + $"（boot.dat {(bootDatUrlBox is null ? "缺失" : "在")} / "
                + $"检查更新 {(updateUrlBox is null ? "缺失" : "在")}）");
        }
        else
        {
            var bootDatLeft = LeftIn(bootDatUrlBox);
            var updateLeft = LeftIn(updateUrlBox);
            var bootDatWidth = bootDatUrlBox.ActualWidth;
            var updateWidth = updateUrlBox.ActualWidth;

            report.AppendLine(
                $"SELFTEST: box-parity update=({updateLeft:0.#},{updateWidth:0.#}) "
                + $"bootdat=({bootDatLeft:0.#},{bootDatWidth:0.#})");

            if (Math.Abs(updateLeft - bootDatLeft) > 0.5 || Math.Abs(updateWidth - bootDatWidth) > 0.5)
            {
                themeProblems.Add(
                    "两个地址框应当**同宽同对齐**（用户要求以「boot.dat 下载地址」为准）："
                    + $"boot.dat 在 x={bootDatLeft:0.#}、宽 {bootDatWidth:0.#}，"
                    + $"检查更新在 x={updateLeft:0.#}、宽 {updateWidth:0.#}"
                    + $" —— 左边缘差 {Math.Abs(updateLeft - bootDatLeft):0.#}、"
                    + $"宽差 {Math.Abs(updateWidth - bootDatWidth):0.#} 像素");
            }
        }

        // 用实际布局检查三个引导项的输入框左右边缘，包括带选择／清除按钮的行。
        var bootFields = new[]
        {
            nameof(MainViewModel.BootStockTitle), nameof(MainViewModel.BootStockId),
            nameof(MainViewModel.BootStockLogo), nameof(MainViewModel.BootStockIcon),
            nameof(MainViewModel.BootSysNandTitle), nameof(MainViewModel.BootSysNandId),
            nameof(MainViewModel.BootSysNandLogo), nameof(MainViewModel.BootSysNandIcon),
            nameof(MainViewModel.BootEmuNandTitle), nameof(MainViewModel.BootEmuNandId),
            nameof(MainViewModel.BootEmuNandLogo), nameof(MainViewModel.BootEmuNandIcon),
        };

        var bootFieldBounds = bootFields.Select(name =>
        {
            var box = BoxFor(name);
            if (box is null || !box.IsVisible || box.ActualWidth <= 0)
            {
                themeProblems.Add($"找不到可见的引导项输入框：{name}");
                return (Name: name, Left: double.NaN, Right: double.NaN);
            }

            var left = LeftIn(box);
            return (Name: name, Left: left, Right: left + box.ActualWidth);
        }).ToArray();

        var firstBootField = bootFieldBounds[0];
        report.AppendLine($"SELFTEST: boot-field-alignment left={firstBootField.Left:0.#} right={firstBootField.Right:0.#}");

        foreach (var field in bootFieldBounds.Skip(1))
        {
            if (Math.Abs(field.Left - firstBootField.Left) > 0.5
                || Math.Abs(field.Right - firstBootField.Right) > 0.5)
            {
                themeProblems.Add($"引导项输入框 {field.Name} 的左右边缘未与 {firstBootField.Name} 对齐");
            }
        }

        // ── 三个引导项的 id 输入框（用户 2026-11-19：id 允许用户配置）──────────
        // 判据落在**渲染出来的控件**上：框被挪走 / `MaxLength` 漏了 / 绑定绑到空值，
        // 这三种毛病都不报错、也不崩溃，只表现为「框在，但不起作用」。
        var locForIds = SwitchCfwWizard.Localization.LocalizationService.Instance;
        var idEntries = new[]
        {
            (Property: nameof(MainViewModel.BootStockId), Label: locForIds["Global.Boot.Stock"]),
            (Property: nameof(MainViewModel.BootSysNandId), Label: locForIds["Global.Boot.SysNand"]),
            (Property: nameof(MainViewModel.BootEmuNandId), Label: locForIds["Global.Boot.EmuNand"]),
        };

        var idBoxReport = new List<string>();

        foreach (var entry in idEntries)
        {
            var box = BoxFor(entry.Property);

            if (box is null)
            {
                themeProblems.Add($"「{entry.Label}」的 id 输入框找不到（绑定 {entry.Property} 没有落到界面上）");
                continue;
            }

            idBoxReport.Add($"{entry.Label}={box.MaxLength}");

            if (box.MaxLength != SwitchCfwWizard.Services.ComponentCatalog.MaxBootIdLength)
            {
                themeProblems.Add(
                    $"「{entry.Label}」的 id 输入框 MaxLength 是 {box.MaxLength}，应为 "
                    + $"{SwitchCfwWizard.Services.ComponentCatalog.MaxBootIdLength}"
                    + " —— 多打的字会被 hekate 静默截断（它内部是 char id[8]）");
            }

            if (!box.IsVisible)
            {
                themeProblems.Add($"「{entry.Label}」的 id 输入框不可见（被折叠、或被挪进了别的容器）");
            }
        }

        report.AppendLine($"SELFTEST: bootid-boxes {string.Join(" | ", idBoxReport)}");

        // 就地标红：能用不许红、不能用必须红。三条共用一个文案，所以「有 1 条可见」就够了。
        if (hintViewModelForExpand is not null)
        {
            var invalidHint = locForIds["Global.Boot.Id.Invalid"];
            var originalStockId = hintViewModelForExpand.BootStockId;

            try
            {
                hintViewModelForExpand.BootStockId = "ok_id-1";
                Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                var withValid = VisibleHintCount(invalidHint);

                hintViewModelForExpand.BootStockId = "12345678";   // 8 位 = 超上限
                Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                var withTooLong = VisibleHintCount(invalidHint);

                hintViewModelForExpand.BootStockId = "有空格 的";
                Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                var withBadChar = VisibleHintCount(invalidHint);

                report.AppendLine(
                    $"SELFTEST: bootid-invalid valid={withValid} tooLong={withTooLong} badChar={withBadChar}");

                if (withValid != 0 || withTooLong != 1 || withBadChar != 1)
                {
                    themeProblems.Add(
                        "id 的「不能用」提示接线不对：能用时应 0 条、超 7 位应 1 条、含非法字符应 1 条，"
                        + $"实际 {withValid}/{withTooLong}/{withBadChar}");
                }
            }
            finally
            {
                // 这三个是**会落盘**的设置项：自检不许改坏用户的存档
                hintViewModelForExpand.BootStockId = originalStockId;
                Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            }
        }

        if (window.DataContext is SwitchCfwWizard.ViewModels.MainViewModel hintViewModel)
        {
            // ⚠️ 这两个是**会落盘的设置项**：自检里改完必须还原，否则用户跑一次 --selftest
            // 就把自己填过的地址清空了（自检不该有这种副作用）。还原放在最后。
            var originalBootDatUrl = hintViewModel.BootDatUrl;
            var originalUpdateUrl = hintViewModel.UpdateUrl;

            // 空着时必须各有一条
            if (bootDatHintVisible != 1 || updateHintVisible != 1)
            {
                themeProblems.Add(
                    $"两个地址框的格式提示应当各显示 1 条（地址为空时），实际 boot.dat={bootDatHintVisible}、"
                    + $"更新={updateHintVisible} —— 提示的可见性绑定没生效");
            }

            // 填上之后必须消失（否则提示会和用户输入叠在一起）
            hintViewModel.BootDatUrl = "https://example.com/boot.dat";
            hintViewModel.UpdateUrl = "https://example.com/o/r/releases";
            Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var afterFill = (VisibleHintCount(bootDatHintText), VisibleHintCount(updateHintText));
            if (afterFill.Item1 != 0 || afterFill.Item2 != 0)
            {
                themeProblems.Add(
                    $"输入框有内容时格式提示应当消失，实际 boot.dat={afterFill.Item1}、更新={afterFill.Item2}");
            }

            // 清空后必须回来
            hintViewModel.BootDatUrl = string.Empty;
            hintViewModel.UpdateUrl = string.Empty;
            Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var afterClear = (VisibleHintCount(bootDatHintText), VisibleHintCount(updateHintText));
            report.AppendLine($"SELFTEST: url-hints after fill={afterFill.Item1}/{afterFill.Item2} after clear={afterClear.Item1}/{afterClear.Item2}");

            if (afterClear.Item1 != 1 || afterClear.Item2 != 1)
            {
                themeProblems.Add(
                    $"清空之后格式提示应当回来，实际 boot.dat={afterClear.Item1}、更新={afterClear.Item2}");
            }

            // 还原用户原来的值（自检不该改动别人的存档）
            hintViewModel.BootDatUrl = originalBootDatUrl;
            hintViewModel.UpdateUrl = originalUpdateUrl;

            // 高级设置那一栏也还原成原样（它只是个界面开关，但没必要替用户改界面状态）
            hintViewModel.ShowAdvancedSettings = originalShowAdvanced;
        }
        else
        {
            themeProblems.Add("自检失效：窗口的 DataContext 不是 MainViewModel（无法验证格式提示）");
        }

        report.AppendLine($"SELFTEST: dark-texts={allTexts.Count} too-dark={tooDark.Count}");

        // 直接把「配置选项」那四个字的实际颜色读出来 —— 用户报的就是它。
        // 上面那条 too-dark 已经能证明「不是黑的」，这里给的是**具体色值**（写进日志好对照）。
        var expanderHeaderInfo = "(窗口里没找到 Expander)";
        if (FindDescendant<System.Windows.Controls.Expander>(window) is { } anyExpander)
        {
            var headerBlock = FindDescendant<System.Windows.Controls.TextBlock>(anyExpander);
            var headerColor = (headerBlock?.Foreground as SolidColorBrush)?.Color;
            expanderHeaderInfo = $"「{headerBlock?.Text}」{headerColor}";
        }

        report.AppendLine($"SELFTEST: expander-header {expanderHeaderInfo}");

        // ── 运行日志：新条目要自动滚到最新一条（用户 2026-11-19 要求）──────────
        // ⚠️ 判据读**渲染出来的滚动位置**。这一条原先是「有 ScrollIntoView 就算有」的写法 ——
        //    而代码本来就有那句 ScrollIntoView，界面却不动：它跑在 CollectionChanged 里，
        //    此刻这一轮布局还没做，于是按**旧的** extent 算偏移，等于什么都没滚。
        //    所以「有没有那句代码」证明不了任何事，只能量滚动条的实际位置。
        if (window.FindName("LogList") is System.Windows.Controls.ListBox logList
            && hintViewModelForExpand is not null)
        {
            var savedLogCount = hintViewModelForExpand.Logs.Count;

            try
            {
                // 条目直接进集合：走 vm.Log 会写进用户的日志文件，自检不该污染它。
                for (var i = 1; i <= 120; i++)
                {
                    hintViewModelForExpand.Logs.Add(new LogEntry(
                        DateTimeOffset.Now,
                        LogLevel.Info,
                        $"自检：滚动检查第 {i} 条 —— 一条足够长的日志，用来把列表撑到出现滚动条。"));
                }

                // 自动滚动的实现排在 Background 优先级 ⇒ 这里必须推到**更晚**的优先级再读，
                // 否则读到的是「还没滚」的那一帧（这就是第一版判据会误报绿的原因）。
                Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var logScroller = FindDescendant<System.Windows.Controls.ScrollViewer>(logList);

                if (logScroller is null)
                {
                    themeProblems.Add("运行日志的 ScrollViewer 找不到 —— 判据无从谈起（列表模板变了？）");
                }
                else
                {
                    var offset = logScroller.VerticalOffset;
                    var scrollable = logScroller.ScrollableHeight;

                    report.AppendLine(
                        $"SELFTEST: log-scroll offset={offset:0.#} scrollable={scrollable:0.#} "
                        + $"items={hintViewModelForExpand.Logs.Count}");

                    // 前置条件：列表必须真的溢出了。否则「在底部」是恒真的，
                    // 这条判据就成了橡皮图章（§1 教训：恒真的判据比没有更坏）。
                    if (scrollable <= 0)
                    {
                        themeProblems.Add(
                            "日志列表没有产生滚动条 —— 这条判据等于没测（自检条目数不够，或列表高度没被约束）");
                    }
                    else if (offset < scrollable - 1)
                    {
                        themeProblems.Add(
                            $"新日志到达后没有滚到最新一条：滚动位置 {offset:0.#} / 可滚 {scrollable:0.#}"
                            + " —— 用户要求「有新的运行日志时滚动到最新的一条」");
                    }
                }
            }
            finally
            {
                // 把这批自检条目摘掉，别留在界面/截图里（它们只是判据的夹具）
                while (hintViewModelForExpand.Logs.Count > savedLogCount)
                {
                    hintViewModelForExpand.Logs.RemoveAt(hintViewModelForExpand.Logs.Count - 1);
                }
            }
        }
        else
        {
            themeProblems.Add("自检失效：找不到运行日志列表 LogList（或窗口 DataContext 不是 MainViewModel）");
        }

        // ── `--shot` 出图（放在这里：暗夜相关的体检都已跑完，动主题不会污染判据）──────
        if (ShotPath is not null)
        {
            try
            {
                // 高级设置那一段刚才为验证提示文字展开过、随后还原了；出图要的是**用户去看它时**
                // 的样子（展开着），所以这里再展开一次。
                if (hintViewModelForExpand is not null)
                {
                    hintViewModelForExpand.ShowAdvancedSettings = true;
                }

                ThemeService.Apply(savedPreference);

                // 先把「检查更新的地址」那个框滚进视野：它在右列的滚动区里，不滚就落在窗口外面，
                // 出图只能看到上半段（第一版就是这样，白出一次）。
                // `--shot-focus` 也可以指一个**勾选框**（不是所有要确认的控件都是 TextBox）：
                // 先按 TextBox 找，找不到再按 CheckBox 的绑定路径找。
                System.Windows.FrameworkElement? shotFocusElement =
                    BoxFor(ShotFocus ?? nameof(MainViewModel.UpdateUrl));

                if (shotFocusElement is null && ShotFocus is not null)
                {
                    var focusChecks = new List<System.Windows.Controls.CheckBox>();
                    Collect(window, focusChecks);
                    shotFocusElement = focusChecks.FirstOrDefault(c =>
                        System.Windows.Data.BindingOperations
                            .GetBinding(c, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)
                            ?.Path?.Path == ShotFocus);
                }

                shotFocusElement?.BringIntoView();

                // boot.dat 那张卡片同理在**左列**的滚动区里 —— 也要滚到它那儿，否则「卡片上那行文字」
                // 根本不在图上（每一轮要改的往往正是卡片里的东西）。两列各有各的滚动条，所以
                // 一次出图能同时看到两处，不用出两张。
                // ⚠️ 这里必须转成 FrameworkElement：`bootDatCard` 是 DependencyObject（UpToCard 的返回类型），
                // 而 BringIntoView 定义在 FrameworkElement 上（不是 UIElement，第一版就写错成 UIElement）。
                (bootDatCard as System.Windows.FrameworkElement)?.BringIntoView();

                window.UpdateLayout();
                Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                window.UpdateLayout();

                var shotWidth = (int)Math.Ceiling(window.ActualWidth);
                var shotHeight = (int)Math.Ceiling(window.ActualHeight);

                var shot = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    shotWidth, shotHeight, 96, 96, PixelFormats.Pbgra32);
                shot.Render(window);

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));

                using (var stream = File.Create(ShotPath))
                {
                    encoder.Save(stream);
                }

                report.AppendLine($"SELFTEST: shot {ShotPath} {shotWidth}x{shotHeight}");
            }
            catch (Exception ex)
            {
                themeProblems.Add("出图失败：" + ex.Message);
            }
        }

        // ── 「格式化与重建虚拟系统」：入口按钮 + 窗口能否构造 ───────────────
        // 这个窗口平时**自检根本碰不到**（只有点了按钮才构造）。XAML 里写错一个 StaticResource
        // 键、或把某个控件挪走，自检照样全绿 —— 直到用户点下去才崩。所以这里显式构造一次。
        try
        {
            var sdLoc = LocalizationService.Instance;
            var openLabel = sdLoc["SdReset.Button.Open"];

            var buttons = new List<System.Windows.Controls.Button>();
            Collect(window, buttons);

            var openButton = buttons.FirstOrDefault(
                b => string.Equals(b.Content as string, openLabel, StringComparison.Ordinal));

            var probe = new SdResetWindow((MainViewModel)window.DataContext);

            var required = new[]
            {
                "DevicePicker", "LayoutBarHost", "MainSizeBox", "EmuSizeBox", "RangeHint",
                "ConfirmPanel", "ResultPanel",
            };
            var missing = required.Where(n => probe.FindName(n) is null).ToList();

            // 用户要求「移除 emuMMC 大小文本和下拉框」：这两个名字不该再出现。
            // 用**名字**断言，而不是「看着像」—— 否则哪天有人换个名字把下拉框加回来也发现不了。
            var obsolete = new[] { "SizePicker", "SizePanel" }.Where(n => probe.FindName(n) is not null).ToList();

            report.AppendLine(
                $"SELFTEST: sdreset entry={(openButton is null ? "missing" : "ok")} "
                + $"window={(missing.Count == 0 ? "ok" : "missing:" + string.Join(",", missing))} "
                + $"obsolete-size-controls={obsolete.Count}");

            if (openButton is null)
            {
                themeProblems.Add($"没找到「{openLabel}」按钮（应放在界面语言左侧）");
            }

            if (missing.Count > 0)
            {
                themeProblems.Add("重置窗口缺少控件：" + string.Join("、", missing));
            }

            if (obsolete.Count > 0)
            {
                themeProblems.Add("重置窗口里又出现了应被移除的容量控件：" + string.Join("、", obsolete));
            }

            // 有设备时，布局条必须真的画出**两块非零宽度**的色块。
            // 这一条防的是「拖动条只是个空 Canvas」：那种状态下编译、绑定、自检都不会报错，
            // 用户看到的是「重建后的布局」下面什么都没有，也没法拖。
            // 用一个合成设备（USB 总线、512 字节扇区、64 GiB）驱动 UpdatePreview，
            // 只走「算布局 + 画面」这一段，不碰任何真实磁盘。
            var fake = new SdDevice(
                99, "Selftest", "Card", "SN", 7, 64L * 1024 * 1024 * 1024, 512,
                "selftest-identity", "selftest-layout", new[] { "E:\\" }, false, string.Empty);
            var fake2 = fake with { Number = 98, Identity = "selftest-identity-2" };

            // 走**生产路径**绑定设备：BindDevices 里就包含「默认选中第一个」。
            probe.BindDevices(new[] { fake, fake2 });

            var selected = probe.DevicePicker.SelectedItem as SdDevice;
            report.AppendLine(
                $"SELFTEST: sdreset-picker selected={selected?.Number.ToString() ?? "none"} "
                + $"items={probe.DevicePicker.Items.Count} display-path={probe.DevicePicker.DisplayMemberPath}");

            if (selected?.Number != 99)
            {
                themeProblems.Add("设备下拉框应默认选中第一张卡（用户要求「优先展示第一张 TF 卡」）");
            }

            if (!string.Equals(probe.DevicePicker.DisplayMemberPath, "Display", StringComparison.Ordinal))
            {
                themeProblems.Add("设备下拉框必须用 DisplayMemberPath 显示文字，否则收起时是一个空框");
            }

            // ⚠️ 不能只对 Window 调 Measure：**没显示过的窗口 ActualWidth 恒为 0**，
            //    布局条会因此提前 return、一块都不画（第一版就是这样，判据当场报 main=0 emu=0）。
            //    直接给布局条一个确定宽度做测量+排布，量到的就是真实绘制尺寸。
            probe.LayoutBarHost.Measure(new System.Windows.Size(600, 34));
            probe.LayoutBarHost.Arrange(new System.Windows.Rect(0, 0, 600, 34));
            probe.UpdateLayout();

            report.AppendLine($"SELFTEST: sdreset-bar main={probe.MainBlock.Width:0.#} emu={probe.EmuBlock.Width:0.#}");

            if (!(probe.MainBlock.Width > 0) || !(probe.EmuBlock.Width > 0))
            {
                themeProblems.Add(
                    $"重置窗口的布局条没有画出两块色块（主 {probe.MainBlock.Width:0.#} / emu {probe.EmuBlock.Width:0.#}）");
            }
            else
            {
                // 两块必须**铺满**整条：只画出一块、或按比例算错（少算/多算一段），
                // 都会让「拖动条长度」与「实际容量」对不上 —— 拖到哪都分不出正确大小。
                var total = probe.MainBlock.Width + probe.EmuBlock.Width;
                if (Math.Abs(total - 600) > 1)
                {
                    themeProblems.Add($"布局条两块色块合计应铺满 600（实际 {total:0.#}）");
                }
            }

            // 拖动：调用 Thumb 的 DragDelta 最终会走的那个方法。
            // ⚠️ 这一条防的是「方块完全拖不动」—— 原先它读 `_device`，而那个字段只在
            //    「下一步」里赋值，所以在①分区设置里拖动时永远是 null、直接 return，且没有报错。
            var beforeDrag = probe.MainSizeBox.Text;
            var movedRight = probe.ApplyDragFraction(0.95);
            var afterRight = probe.MainSizeBox.Text;
            var movedLeft = probe.ApplyDragFraction(0.05);
            var afterLeft = probe.MainSizeBox.Text;

            report.AppendLine(
                $"SELFTEST: sdreset-drag {beforeDrag} -> right:{afterRight} -> left:{afterLeft} "
                + $"moved={movedRight}/{movedLeft}");

            // 手动输入：直接设 emuMMC = 6.5 GiB（**带小数**），主分区那一栏必须跟着变；
            // 再设主分区 = 4.25 GiB，emuMMC 必须反过来变大、且主分区回填成 4.25 左右。
            // 用小数而不是整数是关键：早期实现把 emuMMC 存成 int，输 6.5 会被静默圆成 7 ——
            // 「输入了但没按输入的值算」正是用户 2026-11-20 报的那条。
            var emuBefore = probe.EmuSizeBox.Text;
            var typedEmu = probe.ApplyEmuGiB(6.5);
            var emuAfter = probe.EmuSizeBox.Text;
            var mainAfterTypedEmu = probe.MainSizeBox.Text;
            var typedMain = probe.ApplyMainGiB(4.25);
            var emuAfterMain = probe.EmuSizeBox.Text;
            var mainAfterMain = probe.MainSizeBox.Text;

            report.AppendLine(
                $"SELFTEST: sdreset-input emu {emuBefore}→{emuAfter} (main {mainAfterTypedEmu}) "
                + $"main=4.25→emu {emuAfterMain} / main {mainAfterMain} applied={typedEmu}/{typedMain}");

            var mainParsed = double.TryParse(mainAfterMain,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var mainValue) ? mainValue : -1d;

            if (!typedEmu || !typedMain || emuAfter != "6.50"
                || emuAfterMain == emuAfter
                || Math.Abs(mainParsed - 4.25d) > 0.05d)
            {
                themeProblems.Add(
                    $"手动输入容量没按原值生效（emu {emuBefore}→{emuAfter}，输入主分区 4.25 后 "
                    + $"emu={emuAfterMain}、主分区={mainAfterMain}）");
            }

            if (!movedRight || !movedLeft || afterRight == beforeDrag || afterLeft == afterRight)
            {
                themeProblems.Add("拖动分隔条没有改变容量（主分区大小应随拖动变化）");
            }

            // 六个**同级**的做法（用户 2026-11-21：四个手术不再挂在「恢复单分区」底下）：
            // ① 六个选项必须真的存在（XAML 写错名字，界面上就少一项，但不会有任何报错）；
            // ② 它们必须是**同一组单选**，勾一个自动取消其他五个（互斥由 GroupName 保证，
            //    代码里那份「挨个 SetIsChecked(false)」已经删掉了）；
            // ③ 勾选必须真的改变布局区：不动分区表的三种要收起布局条，重新分区那种要露出布局条
            //    —— 这条防的是「勾了但 CurrentMode 没跟上」，那会让界面和实际写盘的做法不一致。
            var modes = new[]
            {
                probe.WithEmu, probe.OnlyFormat,
                probe.ModeFormatMain, probe.ModeFormatEmu, probe.ModeCreateFiles, probe.ModeRepartition,
            };

            var modesPresent = modes.All(b => b is not null);
            var defaultOnlyRebuild = modesPresent
                                     && probe.WithEmu.IsChecked == true
                                     && modes.Skip(1).All(b => b.IsChecked != true);

            void Pick(System.Windows.Controls.RadioButton radio) => radio.IsChecked = true;

            Pick(probe.ModeFormatMain);
            var mainExclusive = probe.ModeFormatMain.IsChecked == true
                                && probe.WithEmu.IsChecked != true
                                && probe.OnlyFormat.IsChecked != true;
            var mainHidesBar = probe.LayoutBarHost.Visibility == System.Windows.Visibility.Collapsed;

            Pick(probe.ModeFormatEmu);
            var emuHidesBar = probe.LayoutBarHost.Visibility == System.Windows.Visibility.Collapsed
                              && probe.ModeFormatMain.IsChecked != true;

            Pick(probe.ModeCreateFiles);
            var filesHidesBar = probe.LayoutBarHost.Visibility == System.Windows.Visibility.Collapsed
                                && probe.ModeFormatEmu.IsChecked != true;

            Pick(probe.ModeRepartition);
            var repartitionShowsBar = probe.LayoutBarHost.Visibility == System.Windows.Visibility.Visible
                                      && probe.ModeCreateFiles.IsChecked != true;

            Pick(probe.OnlyFormat);
            var formatOnlyHidesBar = probe.LayoutBarHost.Visibility == System.Windows.Visibility.Collapsed
                                     && probe.ModeRepartition.IsChecked != true;

            // 组件包进度卡：两条进度条 + 两个文案位必须在，且默认不显示（还没开始下载）。
            var emuPanelOk = probe.EmuDownloadBar is not null && probe.EmuExtractBar is not null
                             && probe.EmuDownloadLabel is not null && probe.EmuExtractLabel is not null
                             && probe.EmuPreparePanel.Visibility == System.Windows.Visibility.Collapsed;

            report.AppendLine(
                $"SELFTEST: sdreset-modes present={modesPresent} default-rebuild={defaultOnlyRebuild} "
                + $"exclusive={mainExclusive} bars=hide:{mainHidesBar}/{emuHidesBar}/{filesHidesBar}/{formatOnlyHidesBar} "
                + $"show:{repartitionShowsBar} emu-panel={(emuPanelOk ? "ok" : "bad")}");

            if (!modesPresent)
            {
                themeProblems.Add("六个做法没有全部出现在「分区设置」里（重建 / 恢复单分区 / 格式化主分区 / 格式化 emuMMC / 建文件 / 重新分区）");
            }

            if (!defaultOnlyRebuild)
            {
                themeProblems.Add("默认应只选中「格式化并重建 emuMMC」，其余五个都不选中");
            }

            if (!mainExclusive)
            {
                themeProblems.Add("六个选项必须是同一组单选：勾上「仅格式化主数据分区」后，「重建」与「恢复单分区」都应自动取消");
            }

            if (!(mainHidesBar && emuHidesBar && filesHidesBar && formatOnlyHidesBar))
            {
                themeProblems.Add(
                    $"不动分区表的做法应收起布局条（主分区 {mainHidesBar} / emuMMC {emuHidesBar} / "
                    + $"建文件 {filesHidesBar} / 恢复单分区 {formatOnlyHidesBar}）");
            }

            if (!repartitionShowsBar)
            {
                themeProblems.Add("「重新分区主数据分区和 emuMMC」要写分区表，布局条必须照常显示（否则用户没法定容量）");
            }

            if (!emuPanelOk)
            {
                themeProblems.Add("组件包进度卡缺少进度条/文案元素，或一开始就显示着");
            }

            // 确认页的标题 / 警告 / 「知晓」文案必须跟着做法走。
            // 用户 2026-11-21 报的正是这个：选了「创建 emuMMC 所需文件」（只写几个文件），
            // 确认页却说「将删除整张卡的所有数据」—— 于是根本不敢往下点。
            // 这里逐个做法调一次那段换文案的方法，拿**语言包里的原文**比对，而不是「看起来不一样」。
            var applyWording = typeof(SdResetWindow).GetMethod("ApplyConfirmWording",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            var wordingOk = applyWording is not null;

            string WordingFor(System.Windows.Controls.RadioButton radio)
            {
                Pick(radio);
                applyWording!.Invoke(probe, null);
                return probe.ConfirmWarning.Text;
            }

            var wordingCreate = wordingOk ? WordingFor(probe.ModeCreateFiles) : string.Empty;
            var wordingEmu = wordingOk ? WordingFor(probe.ModeFormatEmu) : string.Empty;
            var wordingMain = wordingOk ? WordingFor(probe.ModeFormatMain) : string.Empty;
            var wordingDestructive = wordingOk ? WordingFor(probe.WithEmu) : string.Empty;

            if (wordingOk)
            {
                wordingOk = wordingCreate == sdLoc["SdReset.Confirm.WarningCreateFiles"]
                            && wordingEmu == sdLoc["SdReset.Confirm.WarningFormatEmu"]
                            && wordingMain == sdLoc["SdReset.Confirm.WarningFormatMain"]
                            && wordingDestructive == sdLoc["SdReset.Confirm.Warning"]
                            // 最后这条才是用户真正在意的：只写文件的那一档**不能**再说整卡。
                            && wordingCreate != sdLoc["SdReset.Confirm.Warning"];
            }

            report.AppendLine($"SELFTEST: sdreset-confirm wording={(wordingOk ? "ok" : "bad")} create=「{wordingCreate}」");

            if (!wordingOk)
            {
                themeProblems.Add(
                    "确认页文案没按做法切换（「创建 emuMMC 所需文件」不该提示会清空整张卡）");
            }

            // 右下角那个按钮也必须跟着做法走（用户 2026-11-21：「创建文件」时还写着「清空并开始」）。
            var startTextMethod = typeof(SdResetWindow).GetMethod("ConfirmStartText",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            string StartTextFor(System.Windows.Controls.RadioButton radio)
            {
                Pick(radio);
                return (string)startTextMethod!.Invoke(probe, null)!;
            }

            var startTextOk = startTextMethod is not null;
            var startCreate = startTextOk ? StartTextFor(probe.ModeCreateFiles) : string.Empty;
            var startMain = startTextOk ? StartTextFor(probe.ModeFormatMain) : string.Empty;
            var startEmu = startTextOk ? StartTextFor(probe.ModeFormatEmu) : string.Empty;
            var startRebuild = startTextOk ? StartTextFor(probe.WithEmu) : string.Empty;

            if (startTextOk)
            {
                startTextOk = startCreate == sdLoc["SdReset.Button.StartCreateFiles"]
                          && startMain == sdLoc["SdReset.Button.StartFormatMain"]
                          && startEmu == sdLoc["SdReset.Button.StartFormatEmu"]
                          && startRebuild == sdLoc["SdReset.Button.Start"]
                          // 关键一条：三种手术都不能沿用「清空并开始」
                          && startCreate != sdLoc["SdReset.Button.Start"]
                          && startMain != sdLoc["SdReset.Button.Start"]
                          && startEmu != sdLoc["SdReset.Button.Start"];
            }

            report.AppendLine($"SELFTEST: sdreset-start-button text={(startTextOk ? "ok" : "bad")} create=「{startCreate}」");

            if (!startTextOk)
            {
                themeProblems.Add(
                    "确认页主按钮文案没按做法切换（三种手术都不该写「清空并开始」）");
            }

            // 复位成默认档，后面的截图与其它断言按「重建」跑。
            probe.WithEmu.IsChecked = true;
            applyWording?.Invoke(probe, null);

            // 提示弹窗也是新窗口：不构造一次，XAML 里写错资源键只有用户点到才会炸。
            var notice = new NoticeDialog();
            var noticeOk = notice.Heading is not null && notice.Body is not null
                           && notice.PrimaryButton is not null && notice.CancelButton is not null;
            report.AppendLine($"SELFTEST: sdreset-notice={(noticeOk ? "ok" : "missing")}");
            if (!noticeOk)
            {
                themeProblems.Add("提示弹窗缺少必要元素（标题/正文/按钮）");
            }

            // 「优先使用本地资源」只有一个开关，长在「全局选项 → 输出内容」里（用户 2026-11-20）。
            // 重置窗口里**不许**再有第二个：两份状态必然有一份说了不算 —— 上一轮就是窗口自己那份
            // 每次准备时覆盖主界面的值，准备结束又写回 false。
            string? TogglePath(System.Windows.Controls.CheckBox box)
                => System.Windows.Data.BindingOperations
                    .GetBinding(box, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)
                    ?.Path?.Path;

            var hostChecks = new List<System.Windows.Controls.CheckBox>();
            Collect(window, hostChecks);
            var hostPreferLocal = hostChecks.FirstOrDefault(
                b => TogglePath(b) == nameof(SwitchCfwWizard.ViewModels.MainViewModel.PreferLocalDownloads));

            // ⚠️ 未显示过的 Window 自己**没有视觉子级**（要等 HwndSource 建出来）；
            //    连它的 Content 量了/排了也建不出子树（实测：Collect 恒为 0）。
            //    那样下面那条 absent 就是**假绿** —— 「扫不到」和「确实没有」看起来一样。
            //    所以这里走**逻辑树**：XAML 声明的对象在 InitializeComponent 之后就已经存在，
            //    与有没有渲染过无关。
            var resetChecks = new List<System.Windows.Controls.CheckBox>();
            void WalkLogical(DependencyObject root)
            {
                foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
                {
                    if (child is System.Windows.Controls.CheckBox box)
                    {
                        resetChecks.Add(box);
                    }

                    if (child is DependencyObject node)
                    {
                        WalkLogical(node);
                    }
                }
            }

            WalkLogical(probe);
            var resetPreferLocal = resetChecks.Any(
                b => TogglePath(b) == nameof(SwitchCfwWizard.ViewModels.MainViewModel.PreferLocalDownloads));

            report.AppendLine(
                $"SELFTEST: prefer-local main="
                + $"{(hostPreferLocal is null ? "missing" : hostPreferLocal.IsChecked == true ? "on" : "off")}"
                + $" reset={(resetPreferLocal ? "duplicated" : "absent")}");

            if (hostPreferLocal is null)
            {
                themeProblems.Add("主界面「输出内容」里找不到「优先使用本地资源」勾选框");
            }
            else if (hostPreferLocal.IsChecked == true)
            {
                themeProblems.Add("「优先使用本地资源」默认应为未勾选（勾上会跳过下载、拿本地旧文件装上卡）");
            }

            // 「没扫到」和「扫到了、但没有那一个」是两件事：前者说明视觉树没建起来，
            // 此时 absent 是**假绿**。所以先要求重置窗口里至少有一个勾选框（知晓那一个）。
            if (resetChecks.Count == 0)
            {
                themeProblems.Add("判据失效：重置窗口里一个勾选框都没扫到（视觉树没建起来，absent 不可信）");
            }
            else if (resetPreferLocal)
            {
                themeProblems.Add("重置窗口里又出现了「优先使用本地资源」勾选框（现在只保留主界面那一处）");
            }

            // 「复制进 TF 卡」窗口同样是新 XAML：不构造一次，写错的资源键/语言键只有用户点到才炸。
            // 注意这里**不 Show**，所以它的 Loaded（枚举真实设备）不会跑 —— 自检不该碰用户的卡。
            try
            {
                var cardCopy = new SwitchCfwWizard.SdReset.CardCopyWindow((MainViewModel)window.DataContext);

                var pickerOk = cardCopy.DevicePicker is not null
                               && string.Equals(cardCopy.DevicePicker.DisplayMemberPath, "Display", StringComparison.Ordinal);
                var keepCount = SwitchCfwWizard.SdReset.CardCopyPlan.KeepNames.Length;

                // 「不删除卡上的文件与文件夹，直接复制」：默认必须**不勾**（勾上是另一条路径，
                // 会保留旧整合包的残留文件）。窗口里真有这个勾选框，也一并查出来。
                var skipBox = cardCopy.SkipClearCheck;
                var skipOk = skipBox is not null && skipBox.IsChecked != true;

                // 右下角那个按钮一开始必须是「清空并复制」。它复制完会变成「弹出 TF 卡」，
                // 但**初始态**要是就写着弹出，等于一进来就让人把卡拔了。
                var startLabel = cardCopy.StartButton?.Content as string;
                var copyLabel = SwitchCfwWizard.Localization.LocalizationService
                    .Instance["CardCopy.Button.Copy"];
                var startOk = string.Equals(startLabel, copyLabel, StringComparison.Ordinal);

                // 「复制完成后按钮变成弹出 TF 卡」这件事也验一遍：
                // 不跑完一次真实复制（那要写卡），只把那个状态位翻上去，再问按钮现在是什么。
                // 判据是**文案与样式同时换**——只换文案会留下一个看起来像主操作的「弹出」按钮。
                var ejectLabel = SwitchCfwWizard.Localization.LocalizationService
                    .Instance["CardCopy.Button.Eject"];
                var copiedField = typeof(SwitchCfwWizard.SdReset.CardCopyWindow)
                    .GetField("_copied", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var applyButton = typeof(SwitchCfwWizard.SdReset.CardCopyWindow)
                    .GetMethod("ApplyStartButton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

                var ejectOk = false;
                if (copiedField is not null && applyButton is not null)
                {
                    copiedField.SetValue(cardCopy, true);
                    applyButton.Invoke(cardCopy, null);

                    ejectOk = string.Equals(cardCopy.StartButton?.Content as string, ejectLabel, StringComparison.Ordinal)
                              && cardCopy.StartButton?.Style?.Equals(cardCopy.FindResource("SecondaryButton")) == true;

                    // 复原：截图与后面的检查看到的仍应是「还没复制」的样子
                    copiedField.SetValue(cardCopy, false);
                    applyButton.Invoke(cardCopy, null);
                }

                report.AppendLine(
                    $"SELFTEST: cardcopy window=ok keep={keepCount} picker={(pickerOk ? "Display" : "bad")}"
                    + $" skip={(skipBox is null ? "missing" : skipOk ? "off" : "on")}"
                    + $" button={(startOk ? "copy" : "bad:" + startLabel)}"
                    + $" eject={(ejectOk ? "ok" : "bad")}");

                // 这个窗口也想看一眼：`Render(window)` 对没显示过的窗口画不出东西（没有 HwndSource），
                // 所以渲染它的 **Content**（普通 FrameworkElement，量过就有子树）。
                if (ShotPath is not null && cardCopy.Content is System.Windows.FrameworkElement cardRoot)
                {
                    cardRoot.Measure(new System.Windows.Size(820, 740));
                    cardRoot.Arrange(new System.Windows.Rect(0, 0, 820, 740));
                    cardRoot.UpdateLayout();

                    var cardShot = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        820, 740, 96, 96, PixelFormats.Pbgra32);
                    cardShot.Render(cardRoot);

                    var cardEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    cardEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cardShot));

                    var cardPath = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(ShotPath)!, "cardcopy.png");

                    using (var stream = File.Create(cardPath))
                    {
                        cardEncoder.Save(stream);
                    }

                    report.AppendLine($"SELFTEST: cardcopy-shot {cardPath}");
                }

                if (!pickerOk)
                {
                    themeProblems.Add("复制窗口的设备下拉没有按 Display 绑定（收起后会显示类型名，与重置窗口那次同一个坑）");
                }

                if (keepCount != 3)
                {
                    themeProblems.Add($"复制窗口的保留目录应恰好 3 个（实际 {keepCount}）");
                }

                if (skipBox is null)
                {
                    themeProblems.Add("复制窗口里找不到「不删除卡上的文件与文件夹，直接复制」勾选框");
                }
                else if (!skipOk)
                {
                    themeProblems.Add("「不删除，直接复制」默认应为未勾选（推荐路径是只保留三个文件夹）");
                }

                if (!startOk)
                {
                    themeProblems.Add($"复制窗口的右下角按钮初始应显示「清空并复制」，实际是「{startLabel}」"
                        + "（它只在复制成功后才变成「弹出 TF 卡」）");
                }

                if (copiedField is null || applyButton is null)
                {
                    themeProblems.Add("判据失效：复制窗口里找不到 _copied / ApplyStartButton（反射锚点被改名了）");
                }
                else if (!ejectOk)
                {
                    themeProblems.Add("复制完成后右下角按钮没有变成「弹出 TF 卡」（或没降成次要样式）");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("SELFTEST: cardcopy window=missing");
                themeProblems.Add("复制窗口无法构造：" + ex.Message);
            }
        }
        catch (Exception ex)
        {
            themeProblems.Add("重置窗口无法构造：" + ex.Message);
        }

        // 复原用户自己的档位（体检是借这一趟跑，不该留在别人身上）
        ThemeService.Apply(savedPreference);

        report.AppendLine(
            $"SELFTEST: theme={ThemeService.Applied} colors={ThemePalette.Keys.Count} theme-problems={themeProblems.Count}");
        foreach (var problem in themeProblems)
        {
            report.AppendLine("  " + problem);
        }

        var errors = recorder.Errors;
        report.AppendLine($"SELFTEST: binding-errors={errors.Count}");
        foreach (var error in errors.Take(50))
        {
            report.AppendLine("  " + error);
        }

        Flush();
        Shutdown(errors.Count == 0
                 && themeProblems.Count == 0
                 && report.ToString().Contains("window rendered", StringComparison.Ordinal) ? 0 : 2);
    }

    /// <summary>
    /// 逐字段比较两份 <see cref="AppSettings"/>，返回差异描述（空表 = 完全一致）。
    ///
    /// 为什么要这么比：`MainViewModel.ApplyFrom`（存档 → 界面）与 `MaterializeInto`（界面 → 存档）
    /// 是一对**手工镜像**。漏搬一个字段的症状只是「模板里写着 A，应用后界面还是 B」，
    /// 而且只在那一个字段上表现 —— 肉眼几乎发现不了。让它们互为逆运算，一漏就在自检里露出来。
    /// </summary>
    private static List<string> CompareSettings(AppSettings expected, AppSettings actual)
    {
        var diffs = new List<string>();

        foreach (var property in typeof(AppSettings).GetProperties())
        {
            var left = property.GetValue(expected);
            var right = property.GetValue(actual);

            if (left is IDictionary<string, bool> leftFlags && right is IDictionary<string, bool> rightFlags)
            {
                if (leftFlags.Count != rightFlags.Count
                    || leftFlags.Any(p => !rightFlags.TryGetValue(p.Key, out var flag) || flag != p.Value))
                {
                    diffs.Add($"{property.Name}[{leftFlags.Count}≠{rightFlags.Count}]");
                }

                continue;
            }

            if (left is IDictionary<string, string> leftMap && right is IDictionary<string, string> rightMap)
            {
                if (leftMap.Count != rightMap.Count
                    || leftMap.Any(p => !rightMap.TryGetValue(p.Key, out var value)
                                        || !string.Equals(value, p.Value, StringComparison.Ordinal)))
                {
                    diffs.Add($"{property.Name}[{leftMap.Count}≠{rightMap.Count}]");
                }

                continue;
            }

            if (!Equals(left, right))
            {
                diffs.Add($"{property.Name}({left ?? "null"}≠{right ?? "null"})");
            }
        }

        return diffs;
    }

    private static void TryWriteText(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 忽略
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("Dispatcher", e.Exception);

        if (IsUnattended)
        {
            TryWriteText(
                Path.Combine(AppPaths.BaseDirectory, IsHeadlessRun ? "run.log" : "selftest.log"),
                "FAILED" + Environment.NewLine + e.Exception + Environment.NewLine);
            e.Handled = true;
            Shutdown(2);
            return;
        }

        MessageBox.Show(
            e.Exception.ToString(),
            "Switch CFW Wizard - 未处理的异常",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    /// <summary>把异常写到运行目录下的 crash.log，方便用户反馈问题时附上。</summary>
    private static void WriteCrashLog(string source, Exception? exception)
    {
        var body = exception?.ToString() ?? "(no exception object)";

        // 崩溃也必须进**同一份日志文件**（logs/ 下当天那个）。
        //
        // 为什么不能只留 crash.log：用户报问题时通常会发「日志文件」，而崩溃往往就是
        // 「为什么会这样」的答案 —— 让它单独待在一个他没注意到的文件里，等于没记。
        // ⚠️ 多行异常按行拆开写：LogFile 一条一行，整段塞进去会让后续日志的时戳错位。
        foreach (var line in body.Split('\n'))
        {
            LogFile.WriteRaw($"{DateTimeOffset.Now:HH:mm:ss.fff} [崩溃] {source}：{line.TrimEnd('\r')}");
        }

        try
        {
            var path = Path.Combine(AppPaths.BaseDirectory, "crash.log");
            var text = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {source}")
                .AppendLine(body)
                .AppendLine()
                .ToString();

            File.AppendAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写日志失败时不再抛出，避免掩盖原始异常
        }
    }
}

/// <summary>收集 WPF 数据绑定错误（仅自检模式使用）。</summary>
internal sealed class BindingErrorRecorder : TraceListener
{
    private readonly List<string> _errors = new();

    public IReadOnlyList<string> Errors => _errors;

    public static BindingErrorRecorder Attach()
    {
        var recorder = new BindingErrorRecorder();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(recorder);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        return recorder;
    }

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _errors.Add(message);
        }
    }
}
