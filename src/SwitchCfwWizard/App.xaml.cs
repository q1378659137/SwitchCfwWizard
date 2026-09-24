using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Models;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        IsSelfTest = e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
        IsHeadlessRun = e.Args.Any(a => string.Equals(a, "--run", StringComparison.OrdinalIgnoreCase));

        var shotArg = e.Args.FirstOrDefault(
            a => a.StartsWith("--shot", StringComparison.OrdinalIgnoreCase));

        if (shotArg is not null)
        {
            var separator = shotArg.IndexOf('=');
            var value = separator < 0 ? null : shotArg[(separator + 1)..].Trim('"');

            ShotPath = string.IsNullOrWhiteSpace(value)
                ? Path.Combine(AppPaths.BaseDirectory, "selftest.png")
                : Path.GetFullPath(value);
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
                BoxFor(nameof(MainViewModel.UpdateUrl))?.BringIntoView();

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
