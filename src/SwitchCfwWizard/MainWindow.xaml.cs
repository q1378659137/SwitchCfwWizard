using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchCfwWizard.Localization;
using SwitchCfwWizard.SdReset;
using SwitchCfwWizard.Services;
using SwitchCfwWizard.ViewModels;

namespace SwitchCfwWizard;

public partial class MainWindow : Window
{
    private INotifyCollectionChanged? _observedLogs;

    /// <summary>是否已经排了一次「滚到最新」—— 一次运行会连着追加几百条，不必每条都排。</summary>
    private bool _scrollToLatestPending;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachLogObserver();

        if (e.NewValue is MainViewModel vm)
        {
            _observedLogs = vm.Logs;
            _observedLogs.CollectionChanged += OnLogsChanged;
        }
    }

    // ── 配置模板（2026-11-21）─────────────────────────────────────

    /// <summary>
    /// 选了模板就**立刻应用**（用户要求：「用户选择对应的模板后自动勾选上对应的位置」）。
    /// 模板只改组件勾选 / 选项 / 四个屏蔽开关，不会动 Token、代理与界面语言。
    /// </summary>
    private void Template_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ConfigTemplate template } || DataContext is not MainViewModel vm)
        {
            return;
        }

        ApplyTemplateTo(vm, template);
    }

    /// <summary>导入一个 json 当模板；导入成功后自动选中并应用它。</summary>
    private void TemplateImport_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var loc = LocalizationService.Instance;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = loc["App.Template.Import"],
            Filter = loc["App.Template.Filter"],
            CheckFileExists = true,
            InitialDirectory = System.IO.Directory.Exists(vm.TemplateFolder) ? vm.TemplateFolder : null,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var imported = vm.ImportTemplate(dialog.FileName);
        if (imported is null)
        {
            ShowTemplateStatus(loc["App.Template.ImportFailedShort"], isError: true);
            return;
        }

        // 列表刚被重建，选中态是空的 —— 这里选中它，SelectionChanged 会顺带应用一次。
        TemplatePicker.SelectedItem = imported;
    }

    private void ApplyTemplateTo(MainViewModel vm, ConfigTemplate template)
    {
        var loc = LocalizationService.Instance;

        try
        {
            var report = vm.ApplyTemplate(template);
            ShowTemplateStatus(
                string.Format(loc["App.Template.Applied"], template.Name, report.Components, report.Options, report.Settings),
                isError: false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException
                                       or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            ShowTemplateStatus(string.Format(loc["App.Template.ImportFailed"], ex.Message), isError: true);
        }
    }

    private void ShowTemplateStatus(string text, bool isError)
    {
        TemplateStatus.Text = text;
        TemplateStatus.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "TextSecondaryBrush");
        TemplateStatus.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 打开「格式化与重建虚拟系统」。
    ///
    /// 前置检查（用户要求④）：主界面下方一个定制选项都没勾时**不进窗口**，直接提醒 ——
    /// 进了也没东西可装，而这一步之后就是清空整卡。
    /// 生成进行中同样不许进：那会把同一套下载/输出目录并发写坏（本项目早就踩过并发抢 out/ 的坑）。
    /// </summary>
    private void OpenSdReset_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var loc = LocalizationService.Instance;

        if (vm.IsRunning)
        {
            NoticeDialog.Inform(this, loc["SdReset.Title"], loc["SdReset.Notice.Busy"], loc["SdReset.Button.Ok"]);
            return;
        }

        // 一个定制选项都没勾：**先问，不直接拒绝**（用户 2026-11-20 要求）。
        // 继续的话只做分区与格式化，不安装任何内容 —— 这个取舍由调用方通过 installPackage 传下去。
        var installPackage = true;
        if (!vm.HasCustomSelection)
        {
            installPackage = NoticeDialog.Ask(
                this,
                loc["SdReset.Title"],
                loc["SdReset.Notice.NoSelection"],
                loc["SdReset.Button.Continue"],
                loc["SdReset.Button.Cancel"]);

            if (!installPackage)
            {
                return;
            }
        }

        new SdResetWindow(vm, installPackage) { Owner = this }.ShowDialog();
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0)
        {
            return;
        }

        // 新日志追加到末尾后自动滚动，方便用户看到实时进度（用户 2026-11-19 要求）。
        //
        // ⚠️ 必须**等这一轮布局跑完**再滚。这一句原先就写在下面（直接在 CollectionChanged 里调），
        //    而 CollectionChanged 发生在布局之前 —— ScrollIntoView 按**旧的** extent 算偏移，
        //    于是等于什么都没做：代码在、界面不动。自检把这件事量化成了
        //    `log-scroll offset=0 scrollable=122`（列表溢出 122 条，位置还在 0）。
        //    推到 Background（渲染之后、空闲之前）才能拿到新的 extent。
        //
        // ⚠️ 再合并一次：一次运行会连着追加几百条，每条都排一次滚动没有意义。
        if (_scrollToLatestPending)
        {
            return;
        }

        _scrollToLatestPending = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _scrollToLatestPending = false;

            if (LogList.Items.Count > 0)
            {
                ScrollLogToLatest(LogList.Items[LogList.Items.Count - 1]);
            }
        }));
    }

    /// <summary>
    /// 把日志列表滚到最新一条。两条路一起用：
    /// <list type="bullet">
    ///   <item><c>ScrollIntoView(item)</c>：按**条目**定位（<c>CanContentScroll=true</c> 时是主路径）；</item>
    ///   <item><c>ScrollViewer.ScrollToEnd()</c>：把最后那点余量抹平 —— 消息会换行，条目高度不一致，
    ///   只按条目滚可能停在「最后一条的上沿」，看起来仍差半行。</item>
    /// </list>
    /// </summary>
    private void ScrollLogToLatest(object item)
    {
        LogList.ScrollIntoView(item);

        if (FindScrollViewer(LogList) is { } scroller)
        {
            scroller.ScrollToEnd();
        }
    }

    /// <summary>ListBox 的滚动条在它的模板里，只能从视觉树上找（不是它的公开属性）。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer hit)
        {
            return hit;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void DetachLogObserver()
    {
        if (_observedLogs is not null)
        {
            _observedLogs.CollectionChanged -= OnLogsChanged;
            _observedLogs = null;
        }
    }

    /// <summary>
    /// 关窗前把设置补写一次。
    ///
    /// 自动落盘有 400ms 的节流窗口，而「改完顺手就关窗」完全可能落在窗口里 ——
    /// 那时定时器还没到点，改动会凭空消失（界面一切正常，下次打开才发现没存）。
    /// 这里强制写完最后一份快照，把那个窗口补掉。
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.FlushSettings();
        }
    }

    private void OnClosed(object? sender, EventArgs e) => DetachLogObserver();
}
