using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;
using SwitchCfwWizard.Models;
using SwitchCfwWizard.ViewModels;

namespace SwitchCfwWizard.SdReset;

/// <summary>
/// 格式化与重建虚拟系统。
///
/// 移植自 yijianhuanbao 的同名功能，按用户 2026-11-20 的六项要求改过：
///   ① emuMMC 容量不再用「文本 + 下拉框」，改为拖动布局条上的分隔块分配，下方实时显示两者容量；
///   ② 设备**必须手动选择**（不再自动挑第一个，避免认错卡）；
///   ③ 文案统一用「TF 卡」（原文写的 TF 卡）；
///   ④ 打开前由调用方检查主界面定制选项（见 <see cref="MainViewModel.HasCustomSelection"/>）；
///   ⑤ 「准备与确认」阶段用主界面勾选的定制选项生成整合包（<see cref="MainViewModel.GeneratePackageForSdResetAsync"/>）；
///   ⑥ README 只写用法，不写本轮移植过程。
///
/// ⚠️ 这里是**破坏性操作**的唯一入口：一旦进入「清空并开始」，整卡数据不可恢复。
/// 所以分两道闸：确认页必须勾选知晓，写入前再核对一次设备身份与分区表（<see cref="VerifyBeforeErase"/> / <see cref="Guard"/>）。
/// </summary>
public partial class SdResetWindow : Window
{
    private enum Phase
    {
        Settings,
        Confirm,
        Format,
        Mount,
        Install,
        Deploy,
        Complete,
    }

    private static LocalizationService Loc => LocalizationService.Instance;

    private readonly MainViewModel _viewModel;

    /// <summary>本次是否要装整合包。false = 用户在主界面没勾任何定制选项、但选择了「继续」⇒ 只做分区。</summary>
    private readonly bool _installPackage;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    private readonly Stopwatch _speedClock = new();

    private UpdateProgress? _latest;

    private long _speedBytes;

    private long _nextGuard;

    private bool _ready;

    private bool _scanning;

    private bool _busy;

    private bool _cancelAllowed;

    private bool _ejected;

    private bool _pollViewModel;

    private Phase _phase;

    private CancellationTokenSource? _cancel;

    private SdDevice? _device;

    private SdFormatOptions? _options;

    private SdLayout? _layout;

    private string? _root;

    private byte[]? _publishedMbr;

    private uint _expectedSignature;

    private PackageSnapshot? _package;

    private Dictionary<string, byte[]>? _assets;

    private double _emuGiB = 12d;

    /// <summary>
    /// 这个窗口里可选的做法（用户 2026-11-21：六个选项**同级**）。
    ///
    /// 用一个枚举而不是六个 bool：那六个本来就是**互斥**的（同一个 <c>GroupName</c> 的单选按钮），
    /// 六份状态必然有一天出现「两个都为 true」，而写盘代码只能走一条路。
    /// </summary>
    private enum Mode
    {
        /// <summary>清卡 → 重建分区表（主 + emuMMC）→ 格式化 → 安装整合包。</summary>
        Rebuild,

        /// <summary>清卡 → 整卡一个主分区 → 格式化。</summary>
        FormatOnly,

        /// <summary>只重新格式化主数据分区，不动分区表与 emuMMC。</summary>
        FormatMain,

        /// <summary>只清空 emuMMC 隐藏分区前 16 MiB，不动分区表与主分区。</summary>
        FormatEmu,

        /// <summary>只把组件包写到卡上（下次开机由它创建 emuMMC），不动分区表。</summary>
        CreateFiles,

        /// <summary>重写分区表（主 + emuMMC）并格式化主分区，但**不装整合包**。</summary>
        Repartition,
    }

    /// <summary>当前选中的做法 —— 由那六个单选按钮的勾选状态算出来，不另存一份。</summary>
    private Mode CurrentMode
    {
        get
        {
            if (OnlyFormat.IsChecked == true)
            {
                return Mode.FormatOnly;
            }

            if (ModeFormatMain.IsChecked == true)
            {
                return Mode.FormatMain;
            }

            if (ModeFormatEmu.IsChecked == true)
            {
                return Mode.FormatEmu;
            }

            if (ModeCreateFiles.IsChecked == true)
            {
                return Mode.CreateFiles;
            }

            if (ModeRepartition.IsChecked == true)
            {
                return Mode.Repartition;
            }

            return Mode.Rebuild;
        }
    }

    /// <summary>日志里用的模式名。界面文案一律走语言包，这里只是给运行日志看的。</summary>
    private static string ModeText(Mode mode) => mode switch
    {
        Mode.FormatOnly => "格式化 TF 卡并恢复单分区",
        Mode.FormatMain => "只格式化主数据分区",
        Mode.FormatEmu => "只清空 emuMMC 分区",
        Mode.CreateFiles => "只创建 emuMMC 所需文件",
        Mode.Repartition => "重新分区主数据分区和 emuMMC",
        _ => "格式化并重建 emuMMC",
    };

    /// <summary>本次是「不动分区表」的手术（格式化主分区 / 清 emuMMC / 只创建所需文件）。</summary>
    private bool SurgeryMode => CurrentMode is Mode.FormatMain or Mode.FormatEmu or Mode.CreateFiles;

    /// <summary>重新分区档：和「重建」一样写分区表，区别只在于**不装整合包**。</summary>
    private bool RepartitionOnly => CurrentMode == Mode.Repartition;

    /// <summary>要显示「重建后的布局」与拖动条：只有会写分区表的两种（重建 / 重新分区）。</summary>
    private bool UsesLayout => CurrentMode is Mode.Rebuild or Mode.Repartition;

    /// <summary>
    /// 本次是否真的会装整合包。重做分区那个选项一律不装 —— 它就是「只要分区和格式化，
    /// 不要内容」，否则和第一个选项「格式化并重建 emuMMC」没有区别。
    /// </summary>
    private bool InstallsPackage => _installPackage && CurrentMode == Mode.Rebuild;

    private string _layoutSummary = string.Empty;

    /// <summary>上一条已写进日志的阶段文字 —— worker 会高频回报同一个阶段，按变化记录即可。</summary>
    private string _lastLoggedStage = string.Empty;

    /// <summary>把本窗口的动作写进主界面的运行日志（用户要求：这个窗口里的操作也要留痕）。</summary>
    private void Log(string message, LogLevel level = LogLevel.Info) => _viewModel.Log(level, "[重置] " + message);

    public SdResetWindow(MainViewModel viewModel, bool installPackage = true)
    {
        _viewModel = viewModel;
        _installPackage = installPackage;
        InitializeComponent();

        Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 24));
        MaxHeight = SystemParameters.WorkArea.Height - 12;

        _ready = true;
        SetStep(1);

        _timer.Tick += (_, _) => Tick();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _timer.Stop();
        Closing += (_, e) =>
        {
            if (!_busy)
            {
                return;
            }

            e.Cancel = true;
            if (_cancelAllowed)
            {
                _cancel?.Cancel();
            }
        };

        LayoutBarHost.SizeChanged += (_, _) => RenderBar();
    }

    // ── 设备 ───────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        if (_scanning || _busy)
        {
            return;
        }

        _scanning = true;
        RefreshButton.IsEnabled = false;
        DevicePicker.IsEnabled = false;
        StartButton.IsEnabled = false;

        try
        {
            var devices = await Task.Run(SdDisks.Scan);

            // 优先展示第一张卡：下拉框里有内容，用户才看得见「检测到了什么」。
            // 仍可改成别的卡；设备身份在点「清空并开始」之前还会再核对一次（EnsureSame）。
            BindDevices(devices);
            UpdatePreview();

            Log(devices.Count == 0
                ? "未检测到可用的 TF 卡设备"
                : string.Format("检测到 {0} 个设备：{1}", devices.Count,
                    string.Join("；", devices.Select(d => d.Display))),
                devices.Count == 0 ? LogLevel.Warning : LogLevel.Info);

            DeviceDetails.Text = devices.Count == 0
                ? Loc["SdReset.Device.Empty"]
                : string.Format(Loc["SdReset.Device.Count"], devices.Count);
        }
        catch (Exception ex)
        {
            Log("检测设备失败：" + ServerAddressPrivacy.Hide(ex.Message), LogLevel.Error);
            DeviceDetails.Text = Loc["SdReset.Device.Failed"] + ServerAddressPrivacy.Hide(ex.Message);
        }
        finally
        {
            _scanning = false;
            RefreshButton.IsEnabled = true;
            DevicePicker.IsEnabled = true;
        }
    }

    /// <summary>
    /// 把扫描结果放进下拉框，并**默认选中第一个**（用户 2026-11-20：「优先展示第一张 TF 卡」）。
    /// 单独抽出来是为了让自检能直接驱动这一步 —— 真实的 <see cref="RefreshAsync"/> 要枚举磁盘，
    /// 而「有没有默认选中」这件事与硬件无关，用合成设备就能钉住。
    /// </summary>
    internal void BindDevices(IReadOnlyList<SdDevice> devices)
    {
        DevicePicker.ItemsSource = devices;
        DevicePicker.SelectedItem = devices.FirstOrDefault();
    }

    private SdFormatOptions BuildOptions()
    {
        var fileSystem = FileSystemPicker.SelectedIndex == 1 ? SdFileSystem.ExFat : SdFileSystem.Fat32;

        // 「重新分区主数据分区和 emuMMC」虽然不装整合包，但它**要**一个 emuMMC 分区，
        // 所以这里必须把 emu 计成 1 —— 否则会按「整卡一个主分区」去算布局。
        var emu = UsesLayout ? 1 : 0;
        return new SdFormatOptions(fileSystem, emu, _emuGiB);
    }

    private void UpdatePreview()
    {
        if (!_ready)
        {
            return;
        }

        StartButton.IsEnabled = false;
        _layout = null;
        _layoutSummary = string.Empty;

        var rebuild = UsesLayout;
        LayoutBarHost.Visibility = Visibility.Collapsed;
        LegendPanel.Visibility = Visibility.Collapsed;
        LegendPanelEmu.Visibility = Visibility.Collapsed;
        DragHint.Visibility = Visibility.Collapsed;
        RangeHint.Visibility = Visibility.Collapsed;
        LayoutPlaceholder.Visibility = Visibility.Visible;
        LayoutPlaceholder.Text = Loc["SdReset.Layout.Empty"];

        if (DevicePicker.SelectedItem is not SdDevice device)
        {
            DeviceDetails.Text = Loc["SdReset.Device.Hint"];
            return;
        }

        try
        {
            device.EnsureResetAllowed();

            // 「不动分区表」的三种手术：没有「重建后的布局」可显示，
            // 这里必须**明说保留现有分区**，否则用户会以为下面那块空白是没加载出来。
            if (SurgeryMode)
            {
                _layout = null;

                // ⚠️ 三个字面量各写一次：语言键检查器按源码里的字面量对账。
                if (CurrentMode == Mode.FormatMain)
                {
                    _layoutSummary = Loc["SdReset.Surgery.MainSummary"];
                    LayoutPlaceholder.Text = Loc["SdReset.Surgery.Main"];
                }
                else if (CurrentMode == Mode.FormatEmu)
                {
                    _layoutSummary = Loc["SdReset.Surgery.WipeSummary"];
                    LayoutPlaceholder.Text = Loc["SdReset.Surgery.Wipe"];
                }
                else
                {
                    _layoutSummary = Loc["SdReset.Surgery.FilesSummary"];
                    LayoutPlaceholder.Text = Loc["SdReset.Surgery.Files"];
                }

                DeviceDetails.Text = string.Format(Loc["SdReset.Device.Detail"], device.Capacity / 1073741824d);
                LayoutPlaceholder.Visibility = Visibility.Visible;
                StartButton.IsEnabled = !_busy;
                return;
            }

            var options = BuildOptions();
            if (rebuild)
            {
                var max = SdDragPlan.MaxAllowedGiB(device.Capacity);
                if (max < SdDragPlan.MinEmuGiB)
                {
                    LayoutPlaceholder.Text = Loc["SdReset.Layout.TooSmall"];
                    DeviceDetails.Text = string.Format(Loc["SdReset.Device.Detail"], device.Capacity / 1073741824d);
                    return;
                }

                _emuGiB = SdDragPlan.ClampGiB(device.Capacity, _emuGiB);
                options = BuildOptions();
            }

            var layout = SdLayout.Create(device.Capacity, options);
            _layout = layout;

            DeviceDetails.Text = string.Format(Loc["SdReset.Device.Detail"], device.Capacity / 1073741824d);

            var parts = new List<string>
            {
                $"{Loc["SdReset.Layout.Main"]}：{Bytes(layout.Main.Length)}",
            };

            if (layout.Partitions.Length > 1)
            {
                parts.Add($"{Loc["SdReset.Layout.Emu"]}：{Bytes(layout.Partitions[1].Length)}");
            }

            _layoutSummary = string.Join("\n", parts);

            MainSizeBox.IsEnabled = rebuild;
            EmuSizeBox.IsEnabled = rebuild;
            SyncSizeInputs();

            LegendPanel.Visibility = Visibility.Visible;
            LegendPanelEmu.Visibility = rebuild ? Visibility.Visible : Visibility.Collapsed;
            LayoutBarHost.Visibility = rebuild ? Visibility.Visible : Visibility.Collapsed;
            DragHint.Visibility = rebuild ? Visibility.Visible : Visibility.Collapsed;
            LayoutPlaceholder.Visibility = rebuild ? Visibility.Collapsed : Visibility.Visible;

            // 可分配范围按**这张卡**算出来写在下面（用户 2026-11-20 要求：主分区 2 GiB / emuMMC 5 GiB 起）
            var maxEmu = SdDragPlan.MaxAllowedGiB(device.Capacity);
            RangeHint.Text = string.Format(Loc["SdReset.Layout.Range"], maxEmu);
            RangeHint.Visibility = rebuild ? Visibility.Visible : Visibility.Collapsed;

            if (!rebuild)
            {
                LayoutPlaceholder.Text = Loc["SdReset.Layout.FormatOnly"];
            }

            RenderBar();
            StartButton.IsEnabled = !_busy;
        }
        catch (Exception ex)
        {
            LayoutPlaceholder.Text = Loc["SdReset.Layout.Invalid"];
            DeviceDetails.Text = ServerAddressPrivacy.Hide(ex.Message);
        }
    }

    private void RenderBar()
    {
        if (_layout is null || !(LayoutBarHost.ActualWidth > 0))
        {
            return;
        }

        var width = LayoutBarHost.ActualWidth;
        var mainWidth = width * _layout.Main.Length / _layout.DiskBytes;

        MainBlock.Width = Math.Max(0, mainWidth);
        EmuBlock.Width = Math.Max(0, width - mainWidth);
        Canvas.SetLeft(EmuBlock, mainWidth);
        Canvas.SetLeft(DragThumb, Math.Max(0, mainWidth - DragThumb.Width / 2));
        DragThumb.Visibility = _layout.Partitions.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 拖动分配容量（① 的核心）────────────────────────────────────

    private void DragThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_busy || !(LayoutBarHost.ActualWidth > 0))
        {
            return;
        }

        var left = Canvas.GetLeft(DragThumb);
        if (double.IsNaN(left))
        {
            left = 0;
        }

        // DragDelta 给的是**增量**；换算成「分隔条中心在整条里的比例」，再交给纯函数吸附与夹取。
        var center = left + DragThumb.Width / 2 + e.HorizontalChange;
        ApplyDragFraction(center / LayoutBarHost.ActualWidth);
    }

    /// <summary>
    /// 把「分隔条位置（0～1）」应用成新的 emuMMC 容量。
    /// 抽成 internal 是为了让自检能直接调用：像素级拖动没法离线模拟，
    /// 但「给定比例 → 容量是否按预期变化」可以，而且这正是写反方向时唯一会露馅的地方。
    /// 返回是否真的改变了容量（同一档位 / 卡太小 / 没选设备都返回 false）。
    /// </summary>
    internal bool ApplyDragFraction(double fraction)
    {
        // ⚠️ 这里必须读**下拉框当前选中的设备**，不能用 `_device`：
        //    `_device` 只在「下一步」里赋值 —— 在①分区设置里拖的时候它还是 null，
        //    原先的判空直接 return，表现就是「方块完全拖不动」，而且没有任何报错。
        if (DevicePicker.SelectedItem is not SdDevice device)
        {
            return false;
        }

        var next = SdDragPlan.GiBFromFraction(device.Capacity, fraction);

        // 量化步长是 0.01 GiB：位移不到半步就当没动，避免拖动时整窗重排几十次
        if (next <= 0d || Math.Abs(next - _emuGiB) < SdDragPlan.StepGiB / 2d)
        {
            return false;
        }

        _emuGiB = next;
        UpdatePreview();
        return true;
    }

    // ── 手动输入容量（用户 2026-11-20 要求：大小也能直接输）──────────

    /// <summary>
    /// 把两个输入框刷成**当前布局的真实值**。
    ///
    /// 只在提交之后（以及布局变化之后）调用，不跟着每次按键刷 ——
    /// 边打字边回填会把「12」在打到「1」时就改成别的数。
    /// </summary>
    private void SyncSizeInputs()
    {
        if (_layout is null)
        {
            MainSizeBox.Text = string.Empty;
            EmuSizeBox.Text = string.Empty;
            return;
        }

        MainSizeBox.Text = (_layout.Main.Length / (double)SdLayout.GiB).ToString("0.00");

        // emuMMC 显示的是**我们配置的那个值**（可以是小数），不是分区字节数 ——
        // 后者含 32 MiB 开销，显示成 11.97 会让人以为自己输错了。
        EmuSizeBox.Text = _layout.Partitions.Length > 1
            ? _emuGiB.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            : "0.00";
    }

    private void SizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSizeInput(fromEmu: ReferenceEquals(sender, EmuSizeBox));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SyncSizeInputs();
            e.Handled = true;
        }
    }

    private void MainSizeBox_LostFocus(object sender, RoutedEventArgs e) => CommitSizeInput(fromEmu: false);

    private void EmuSizeBox_LostFocus(object sender, RoutedEventArgs e) => CommitSizeInput(fromEmu: true);

    /// <summary>
    /// 提交输入框里的数字。被夹回去（超范围 / 打错字 / 没选设备）时**把真实值写回框里** ——
    /// 悄悄改成别的数、框里却还留着你输的值，是最难查的一种「和界面不一致」。
    /// </summary>
    private void CommitSizeInput(bool fromEmu)
    {
        if (_busy || DevicePicker.SelectedItem is not SdDevice)
        {
            SyncSizeInputs();
            return;
        }

        var text = (fromEmu ? EmuSizeBox : MainSizeBox).Text;

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            && !double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out value))
        {
            SyncSizeInputs();
            return;
        }

        if (fromEmu)
        {
            ApplyEmuGiB(value);
        }
        else
        {
            ApplyMainGiB(value);
        }
    }

    /// <summary>
    /// 直接设定 emuMMC 容量（GiB，**可带小数**）。与拖动走同一套夹取 + 量化（步长 0.01 GiB），
    /// 超范围就贴到边界。返回是否真的改变了容量 —— 自检用它把「输入 → 布局变化」这条链接上。
    /// </summary>
    internal bool ApplyEmuGiB(double gib)
    {
        if (DevicePicker.SelectedItem is not SdDevice device)
        {
            return false;
        }

        var next = SdDragPlan.ClampGiB(device.Capacity, gib);
        if (next <= 0d)
        {
            SyncSizeInputs();
            return false;
        }

        // 量化后再比：12.004 与 12.00 是同一个档位，不该触发一次整窗刷新
        if (Math.Abs(next - _emuGiB) < SdDragPlan.StepGiB / 2d)
        {
            SyncSizeInputs();
            return false;
        }

        _emuGiB = next;
        UpdatePreview();
        return true;
    }

    /// <summary>直接设定**主数据分区**容量（GiB，可带小数），其余留给 emuMMC。反向换算见 <see cref="SdDragPlan.GiBForMainGiB"/>。</summary>
    internal bool ApplyMainGiB(double mainGiB)
    {
        if (DevicePicker.SelectedItem is not SdDevice device)
        {
            return false;
        }

        var next = SdDragPlan.GiBForMainGiB(device.Capacity, mainGiB);
        if (next <= 0d)
        {
            SyncSizeInputs();
            return false;
        }

        if (Math.Abs(next - _emuGiB) < SdDragPlan.StepGiB / 2d)
        {
            SyncSizeInputs();
            return false;
        }

        _emuGiB = next;
        UpdatePreview();
        return true;
    }

    // ── 步骤与忙碌态 ───────────────────────────────────────────────

    private void SetStep(int step)
    {
        var steps = new[] { Step1, Step2, Step3, Step4 };
        for (var i = 0; i < steps.Length; i++)
        {
            var active = i + 1 == step;
            steps[i].Foreground = (Brush)FindResource(active ? "AccentBrush" : "TextTertiaryBrush");
            steps[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void SetBusy(bool value, bool canCancel = false)
    {
        _busy = value;
        _cancelAllowed = canCancel;

        StartButton.IsEnabled = !value;
        SettingsPanel.IsEnabled = !value;
        BackButton.Visibility = !value && (int)_phase is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.IsEnabled = !value || canCancel;
        CloseButton.Content = value ? Loc["SdReset.Button.Cancel"] : Loc["SdReset.Button.Close"];

        if (value)
        {
            _timer.Start();
            return;
        }

        RenderProgress();
        _timer.Stop();
    }

    private IProgress<UpdateProgress> Reporter() => new RelayProgress(value => Interlocked.Exchange(ref _latest, value));

    private sealed class RelayProgress(Action<UpdateProgress> action) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => action(value);
    }

    // ── 组件包（下载 / 解压）进度 ────────────────────────────────

    /// <summary>
    /// 组件包下载 / 解压进度 → 设置页那张卡里的两条进度条（用户 2026-11-21 要求在这里展示）。
    ///
    /// 用 <see cref="Progress{T}"/> 而不是自己转发：它的回调会被 post 回**创建它的线程**
    /// （这里是 UI 线程），而下载与解压都跑在线程池上 —— 直接改控件会抛
    /// 「调用线程无法访问此对象」。
    /// </summary>
    private IProgress<AutoEmuProgress> EmuProgressReporter() => new Progress<AutoEmuProgress>(ShowEmuProgress);

    private void ShowEmuProgress(AutoEmuProgress progress)
    {
        EmuPreparePanel.Visibility = Visibility.Visible;

        // 「下载完 → 开始解压」时把下载那条补满：它不是没跑，是跑完了。
        if (progress.Kind == AutoEmuStage.Extract)
        {
            EmuDownloadBar.IsIndeterminate = false;
            EmuDownloadBar.Value = 100;
        }

        var useExtract = progress.Kind == AutoEmuStage.Extract;
        var bar = useExtract ? EmuExtractBar : EmuDownloadBar;
        var label = useExtract ? EmuExtractLabel : EmuDownloadLabel;

        label.Text = string.IsNullOrWhiteSpace(progress.Detail)
            ? progress.Stage
            : $"{progress.Stage} · {progress.Detail}";

        bar.IsIndeterminate = progress.Percent is null;
        bar.Value = progress.Percent ?? 0d;
    }

    /// <summary>
    /// 每次开始准备前把两条进度条归零：上一次留下的 100% 会被误读成
    /// 「这次已经下好了、正在解压」，可其实还在查 Release。
    /// </summary>
    private void ResetEmuProgress()
    {
        EmuDownloadLabel.Text = Loc["AutoEmu.Stage.Query"];
        EmuExtractLabel.Text = Loc["AutoEmu.Stage.Extract"];
        EmuDownloadBar.IsIndeterminate = true;
        EmuDownloadBar.Value = 0d;
        EmuExtractBar.IsIndeterminate = true;
        EmuExtractBar.Value = 0d;
        EmuPreparePanel.Visibility = Visibility.Visible;
    }

    private void Tick()
    {
        if (_pollViewModel)
        {
            StatusText.Text = _viewModel.OverallStatus;
            ResetProgress.IsIndeterminate = false;
            ResetProgress.Value = _viewModel.OverallProgress;
            ProgressPercent.Text = $"{_viewModel.OverallProgress:0}%";
            return;
        }

        RenderProgress();
    }

    private void RenderProgress()
    {
        var progress = Volatile.Read(ref _latest);
        if (progress is null)
        {
            return;
        }

        StatusText.Text = ServerAddressPrivacy.Hide(progress.Stage);
        ProgressPath.Text = progress.IsDownload ? string.Empty : ServerAddressPrivacy.Hide(progress.Path);
        ResetProgress.IsIndeterminate = progress.Total <= 0;
        ResetProgress.Value = progress.Percent;
        ProgressPercent.Text = progress.Total > 0 ? $"{progress.Percent}%" : string.Empty;

        if (progress.IsDownload)
        {
            if (!_speedClock.IsRunning)
            {
                _speedBytes = progress.Done;
                _speedClock.Start();
            }

            if (_speedClock.ElapsedMilliseconds >= 500)
            {
                var rate = Math.Max(0L, progress.Done - _speedBytes) / _speedClock.Elapsed.TotalSeconds;
                DownloadText.Text = $"{Bytes(progress.Done)} / {(progress.Total > 0 ? Bytes(progress.Total) : "?")}"
                                    + $"　·　{Bytes((long)rate)}/s";
                _speedBytes = progress.Done;
                _speedClock.Restart();
            }
        }
        else
        {
            _speedClock.Reset();
            DownloadText.Text = progress.TotalFiles > 0
                ? $"{Loc["SdReset.Files"]}：{progress.Files} / {progress.TotalFiles}"
                : string.Empty;
        }
    }

    private static string Bytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    // ── ① → ② 准备与确认 ──────────────────────────────────────────

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _scanning)
        {
            return;
        }

        if (_phase == Phase.Settings)
        {
            await PrepareAsync();
        }
        else if (_phase == Phase.Confirm && Acknowledgement.IsChecked == true)
        {
            await WriteAsync();
        }
    }

    private async Task PrepareAsync()
    {
        if (DevicePicker.SelectedItem is not SdDevice device)
        {
            Log("未选择 TF 卡，准备已中止", LogLevel.Warning);
            DeviceDetails.Text = Loc["SdReset.Device.Hint"];
            NoticeDialog.Inform(this, Loc["SdReset.Title"], Loc["SdReset.Device.Hint"], Loc["SdReset.Button.Ok"]);
            return;
        }

        try
        {
            _device = device;
            _options = BuildOptions();
            _layout = SurgeryMode ? null : SdLayout.Create(device.Capacity, _options);

            Log(SurgeryMode
                ? $"开始准备：{device.Display}；{ModeText(CurrentMode)}（分区表保持不变）"
                : $"开始准备：{device.Display}；主分区 {Bytes(_layout!.Main.Length)}"
                  + (_layout.Partitions.Length > 1 ? $"，emuMMC {Bytes(_layout.Partitions[1].Length)}" : "，恢复单分区"));

            SetStep(2);
            SetBusy(true, canCancel: true);
            await Task.Run(VerifyBeforeErase);

            if (!SurgeryMode && _layout is not null && _options.EmuCount == 1 && InstallsPackage)
            {
                // 现场复核：对话框打开期间主界面可能被改过（比如把勾选全取消了）。
                if (!_viewModel.HasCustomSelection)
                {
                    throw new IOException(Loc["SdReset.Error.NoSelection"]);
                }

                // ① 先取「重建 emuMMC」要用的组件包（下载 + 解压），进度显示在设置页那两条进度条上。
                //    刻意放在 ShowResult **之前**：ShowResult 会把整个设置页收起来，
                //    进度条也跟着消失 —— 而「下载进度要能在窗口里看到」正是这次的硬要求。
                _cancel = new CancellationTokenSource();
                ResetEmuProgress();
                _assets = await _viewModel.EnsureAutoEmuAssetsAsync(EmuProgressReporter(), _cancel.Token);
                EmuPreparePanel.Visibility = Visibility.Collapsed;

                // ② 再生成整合包（那一段的进度走主界面总的 OverallProgress）
                ShowResult(Loc["SdReset.Prepare.Title"], Loc["SdReset.Prepare.Hint"]);
                SetBusy(true, canCancel: true);

                _pollViewModel = true;

                // 开关只有一个，在「全局选项 → 输出内容」里。这里**只读、不改写**：
                // 以前在窗口里勾、准备结束又写回 false，等于把用户在主界面的选择清掉。
                Log(_viewModel.PreferLocalDownloads
                    ? "开始生成整合包（已启用「优先使用本地资源」：本地文件大小一致时不再下载）"
                    : "开始生成整合包（走网络下载）");

                var generated = await _viewModel.GeneratePackageForSdResetAsync();
                _pollViewModel = false;

                // 取消要把两件事都停掉：本窗口的等待，以及主视图模型里真正的下载/生成。
                // 只 CancellationToken 不够 —— 那条管线用的是它自己的 CTS（见 Close_Click）。
                _cancel.Token.ThrowIfCancellationRequested();

                Log(generated ? "整合包生成完成" : "整合包生成后没有 payload.bin",
                    generated ? LogLevel.Success : LogLevel.Error);

                if (!generated)
                {
                    throw new IOException(Loc["SdReset.Error.PackageMissing"]);
                }

                _package = await Task.Run(() => Package.Scan(AppPaths.OutputRoot), _cancel.Token);
                await Task.Run(() => EmuTask.CheckPackage(_package), _cancel.Token);
                Log($"整合包：{_package.Files.Length} 个文件 / {_package.Directories.Length} 个目录 / {Bytes(_package.TotalBytes)}");

                if (_options.FileSystem == SdFileSystem.Fat32
                    && _package.Files.Any(f => f.Length > uint.MaxValue))
                {
                    throw new IOException(Loc["SdReset.Error.Fat32Size"]);
                }

                var needed = _package.TotalBytes
                             + (_package.Files.Length + _package.Directories.Length + 1) * 65536L
                             + 33554432L;

                if (_layout.Main.Length < needed)
                {
                    throw new IOException(Loc["SdReset.Error.Space"]);
                }

                await Task.Run(VerifyBeforeErase);
            }
            else if (SurgeryMode && CurrentMode == Mode.CreateFiles)
            {
                // 只创建文件：不生成整合包，但**要**准备好组件包（同样走下载 + 进度条）。
                // 这里**不调 ShowResult** —— 它会把设置页（含两条进度条）整块收起来，
                // 用户就什么都看不到了。留在设置页，进度条自己会说话。
                _cancel = new CancellationTokenSource();

                ResetEmuProgress();
                _assets = await _viewModel.EnsureAutoEmuAssetsAsync(EmuProgressReporter(), _cancel.Token);
                EmuPreparePanel.Visibility = Visibility.Collapsed;
                Log($"组件包就绪：{_assets.Count} 个文件", LogLevel.Success);
            }

            _phase = Phase.Confirm;
            SettingsPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Collapsed;
            ConfirmPanel.Visibility = Visibility.Visible;

            ConfirmDevice.Text = device.Display;
            ConfirmLayout.Text = SurgeryMode
                ? _layoutSummary
                : _layoutSummary + "\n"
                  + $"{Loc["SdReset.FileSystem"]}：" +
                  (_options.FileSystem == SdFileSystem.Fat32
                      ? Loc["SdReset.FileSystem.Fat32"]
                      : Loc["SdReset.FileSystem.ExFat"]);

            // 只做分区的那一次，必须在这里说清楚「不会装东西」——
            // 后面的完成页说就晚了，用户已经按了「清空并开始」。
            if (!SurgeryMode && !InstallsPackage)
            {
                // 两条路的原因不同，不能共用一句：一个是「主界面没勾任何东西」，
                // 另一个是「你自己选了只重做分区表」。
                ConfirmLayout.Text += "\n" + (RepartitionOnly
                    ? Loc["SdReset.Confirm.RepartitionNoPackage"]
                    : Loc["SdReset.Confirm.NoPackage"]);
            }

            // 标题、警告与「知晓」文案都按当前做法的**真实破坏范围**写 ——
            // 只写几个文件的那一档要是还说「将清空整张卡」，用户就不敢按下一步了（2026-11-21 反馈）。
            ApplyConfirmWording();

            Acknowledgement.IsChecked = false;
            AckHint.Visibility = Visibility.Visible;

            // 按钮文字跟着做法走（用户 2026-11-21 反馈：选「创建 emuMMC 所需文件」时右下角还写着
            // 「清空并开始」，看着像要清整张卡）。只有真会清卡的那三种才用「清空并开始」。
            StartButton.Content = ConfirmStartText();
        }
        catch (OperationCanceledException)
        {
            Log("已取消准备", LogLevel.Warning);
            BackToSettings();
        }
        catch (Exception ex)
        {
            var message = ServerAddressPrivacy.Hide(ex.Message);
            Log("准备失败：" + message, LogLevel.Error);

            BackToSettings();
            DeviceDetails.Text = message;

            // ⚠️ 以前只把原因写进上面这行浅灰小字 —— 它在滚动区里、字号又小，
            //    用户看到的就是「点了下一步没反应」。现在一律给提示框，原因不可能被忽略。
            NoticeDialog.Inform(this, Loc["SdReset.Error.Title"],
                message + "\n\n" + Loc["SdReset.Error.HelpPrepare"], Loc["SdReset.Button.Ok"]);
        }
        finally
        {
            _pollViewModel = false;
            _cancel?.Dispose();
            _cancel = null;
            SetBusy(false);

            if (_phase == Phase.Confirm)
            {
                StartButton.IsEnabled = false;
            }
        }
    }

    /// <summary>
    /// 确认页的标题、警告语与「我已知晓…」文案，按**当前做法真实的破坏范围**写。
    ///
    /// 为什么必须分开（用户 2026-11-21 反馈）：三种手术里有一种只是往卡上写几个文件，
    /// 结果确认页照样写着「将删除整张卡的所有数据」—— 用户当场就不敢按了，
    /// 而且那句话本身就是错的（emuMMC 与主分区里的东西都不会被清）。
    /// </summary>
    private void ApplyConfirmWording()
    {
        var danger = (Brush)FindResource("DangerBrush");
        var warning = (Brush)FindResource("WarningBrush");
        var mode = CurrentMode;

        if (mode == Mode.FormatMain)
        {
            ConfirmTitle.Text = Loc["SdReset.Confirm.TitleFormatMain"];
            ConfirmWarning.Text = Loc["SdReset.Confirm.WarningFormatMain"];
            ConfirmWarning.Foreground = danger;
            Acknowledgement.Content = Loc["SdReset.Confirm.AckFormatMain"];
        }
        else if (mode == Mode.FormatEmu)
        {
            ConfirmTitle.Text = Loc["SdReset.Confirm.TitleFormatEmu"];
            ConfirmWarning.Text = Loc["SdReset.Confirm.WarningFormatEmu"];
            ConfirmWarning.Foreground = danger;
            Acknowledgement.Content = Loc["SdReset.Confirm.AckFormatEmu"];
        }
        else if (mode == Mode.CreateFiles)
        {
            ConfirmTitle.Text = Loc["SdReset.Confirm.TitleCreateFiles"];
            ConfirmWarning.Text = Loc["SdReset.Confirm.WarningCreateFiles"];
            // 这一档只**新增/替换**文件、不动分区也不删用户数据，用警告色而不是危险色。
            ConfirmWarning.Foreground = warning;
            Acknowledgement.Content = Loc["SdReset.Confirm.AckCreateFiles"];
        }
        else
        {
            ConfirmTitle.Text = Loc["SdReset.Confirm.Title"];
            ConfirmWarning.Text = Loc["SdReset.Confirm.Warning"];
            ConfirmWarning.Foreground = danger;
            Acknowledgement.Content = Loc["SdReset.Confirm.Ack"];
        }
    }

    /// <summary>
    /// 确认页主按钮该写什么。三种手术都不是「清空整张卡」，所以各有各的说法；
    /// 会真清卡的那三种（重建 / 恢复单分区 / 重新分区）才用「清空并开始」。
    ///
    /// 抽成方法是为了让自检能直接对它下断言 —— 这句话写错的后果是「用户不敢按」，
    /// 而它只在这一个分支里出现，靠肉眼比对三个档位很容易漏。
    /// </summary>
    private string ConfirmStartText() => CurrentMode switch
    {
        Mode.FormatMain => Loc["SdReset.Button.StartFormatMain"],
        Mode.FormatEmu => Loc["SdReset.Button.StartFormatEmu"],
        Mode.CreateFiles => Loc["SdReset.Button.StartCreateFiles"],
        _ => Loc["SdReset.Button.Start"],
    };

    private void BackToSettings()
    {
        _phase = Phase.Settings;
        SettingsPanel.Visibility = Visibility.Visible;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        AckHint.Visibility = Visibility.Collapsed;
        // 进度卡只在「准备」那一小段时间里有意义；回到设置页还挂着会让人以为它还在跑。
        EmuPreparePanel.Visibility = Visibility.Collapsed;
        StartButton.Visibility = Visibility.Visible;
        StartButton.Content = Loc["SdReset.Button.Next"];
        SetStep(1);
        UpdatePreview();
    }

    // ── ③ 写入 ───────────────────────────────────────────────────

    private void VerifyBeforeErase()
    {
        try
        {
            var current = SdDisks.Scan().SingleOrDefault(d => d.Number == _device!.Number)
                          ?? throw new IOException(Loc["SdReset.Error.CardGone"]);

            _device!.EnsureSame(current);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(Loc["SdReset.Error.AccessDenied"], ex);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 5 or 32 or 33)
        {
            // 5=拒绝访问、32/33=被占用：这两类以前都被报成「TF 卡已断开」，方向完全错了。
            throw new IOException(Loc["SdReset.Error.AccessDenied"], ex);
        }
    }

    private void Guard()
    {
        if (Environment.TickCount64 < _nextGuard)
        {
            return;
        }

        if (_device is null || _root is null || _publishedMbr is null)
        {
            throw new IOException(Loc["SdReset.Error.NotConfirmed"]);
        }

        if (!SdDisks.ReadTaskMbr(_device, _root).SequenceEqual(_publishedMbr))
        {
            throw new IOException(Loc["SdReset.Error.LayoutChanged"]);
        }

        _nextGuard = Environment.TickCount64 + 800;
    }

    private void ForceGuard()
    {
        _nextGuard = 0;
        Guard();
    }

    /// <summary>
    /// 三种「不动分区表」的手术（用户 2026-11-21 新增的三个选项）。
    ///
    /// 与 <see cref="WriteAsync"/> 分开是因为前置条件不同：那一条要重写分区表、再挂载、
    /// 再装整合包；这里**分区表一个字节都不碰**。但闸门一样 —— 设备身份与占用检查
    /// （<see cref="VerifyBeforeErase"/>）、以及每步的 <see cref="Guard"/> 都还在。
    /// </summary>
    private async Task RunSurgeryAsync()
    {
        var mode = CurrentMode;

        // 三个手术各走一条路（<see cref="SdResetAction"/>）。
        var action = mode switch
        {
            Mode.FormatMain => SdResetAction.FormatMain,
            Mode.FormatEmu => SdResetAction.WipeEmuPartition,
            _ => SdResetAction.CreateEmuFiles,
        };

        // 标题与提示按**实际动作**选，不用三元嵌套 —— 语言键检查器要求每处都是字面量。
        if (mode == Mode.FormatMain)
        {
            ShowResult(Loc["SdReset.Surgery.TitleMain"], Loc["SdReset.Surgery.Hint"]);
            Log("开始格式化主数据分区（分区表与 emuMMC 保持不变）");
        }
        else if (mode == Mode.FormatEmu)
        {
            ShowResult(Loc["SdReset.Surgery.TitleWipe"], Loc["SdReset.Surgery.Hint"]);
            Log("开始清空 emuMMC 分区前 16 MiB（分区表保持不变）");
        }
        else
        {
            ShowResult(Loc["SdReset.Surgery.TitleFiles"], Loc["SdReset.Surgery.Hint"]);
            Log("开始创建 emuMMC 所需文件（分区表保持不变）");
        }

        await Task.Run(VerifyBeforeErase);

        SdResetStatus? final = null;
        _lastLoggedStage = string.Empty;

        await SdResetWorker.ExecuteAsync(
            new SdResetRequest(
                _device!,
                _options!,
                MountOnly: false,
                ExpectedSignature: 0u,
                Action: action,
                AutoEmuZipPath: _viewModel.LastAutoEmuZipPath),
            status =>
            {
                StatusText.Text = status.Error ?? status.Stage;

                if (status.Stage != _lastLoggedStage)
                {
                    _lastLoggedStage = status.Stage;
                    Log(status.Stage, status.Error is null ? LogLevel.Info : LogLevel.Error);
                }

                if (status.Finished)
                {
                    final = status;
                }
            });

        if (final is null)
        {
            throw new IOException(Loc["SdReset.Error.NoResult"]);
        }

        if (final.Error is not null)
        {
            throw new IOException(final.Error);
        }

        // 清空 emuMMC 时引擎不挂载主分区（它没动分区表，盘符本来就在），
        // 这里把盘符补上 —— 「弹出 TF 卡」按钮要靠它。
        _root = final.Root ?? _device!.MountPoints.FirstOrDefault();
        _phase = Phase.Mount;

        Complete();
        Log("完成：分区表未改动", LogLevel.Success);
    }

    private async Task WriteAsync()
    {
        SetBusy(true);
        StartButton.Visibility = Visibility.Collapsed;
        EjectResetButton.Visibility = Visibility.Collapsed;

        ShowResult(_phase == Phase.Mount ? Loc["SdReset.Mount.Title"] : Loc["SdReset.Write.Title"],
            Loc["SdReset.Write.Hint"]);
        SetStep(3);

        try
        {
            // 两种「不动分区表」的手术：不安装整合包、不改分区表，跑完直接到完成页。
            if (SurgeryMode)
            {
                await RunSurgeryAsync();
                return;
            }

            if ((int)_phase is >= 1 and <= 3)
            {
                var mountOnly = _phase == Phase.Mount;

                if (!mountOnly)
                {
                    await Task.Run(VerifyBeforeErase);
                    _phase = Phase.Format;
                    _expectedSignature = 0;
                }

                StatusText.Text = mountOnly ? Loc["SdReset.Mount.Stage"] : Loc["SdReset.Format.Stage"];
                Log(mountOnly ? "开始检查 TF 卡盘符" : "开始写入分区表并格式化（会请求管理员权限）");

                SdResetStatus? final = null;
                await SdResetWorker.ExecuteAsync(
                    new SdResetRequest(_device!, _options!, mountOnly, _expectedSignature),
                    status =>
                    {
                        StatusText.Text = status.Error ?? status.Stage;

                        // 只在**阶段变化**时记一行：worker 会高频回报同一段文字，全记会把日志淹掉。
                        if (status.Stage != _lastLoggedStage)
                        {
                            _lastLoggedStage = status.Stage;
                            Log(status.Stage, status.Error is null ? LogLevel.Info : LogLevel.Error);
                        }

                        if (status.Finished)
                        {
                            final = status;
                        }
                    });

                if (final is null)
                {
                    throw new IOException(Loc["SdReset.Error.NoResult"]);
                }

                if (final.Error is not null)
                {
                    throw new IOException(final.Error);
                }

                _phase = Phase.Mount;
                _root = final.Root;

                if (final.Signature != 0)
                {
                    _expectedSignature = final.Signature;
                }

                if (_expectedSignature == 0)
                {
                    throw new IOException(Loc["SdReset.Error.NoSignature"]);
                }

                if (_root is null)
                {
                    throw new IOException(final.Stage);
                }

                _publishedMbr = await Task.Run(() => SdDisks.ReadTaskMbr(_device!, _root!));

                if (BinaryPrimitives.ReadUInt32LittleEndian(_publishedMbr.AsSpan(440)) != _expectedSignature)
                {
                    throw new IOException(Loc["SdReset.Error.SignatureChanged"]);
                }

                var expectedMbr = _layout!.CreateMbr(_expectedSignature);
                for (var i = 0; i < 4; i++)
                {
                    if (expectedMbr[450 + i * 16] != _publishedMbr[450 + i * 16]
                        || !expectedMbr.AsSpan(454 + i * 16, 8).SequenceEqual(_publishedMbr.AsSpan(454 + i * 16, 8)))
                    {
                        throw new IOException(Loc["SdReset.Error.LayoutMismatch"]);
                    }
                }

                ForceGuard();

                // 没有整合包（主界面没勾任何定制选项、用户选择了继续）：
                // 分区与格式化就是这次的全部内容，不再进入安装 / 自动创建。
                if (_options!.EmuCount == 0 || !InstallsPackage)
                {
                    Complete();
                    return;
                }

                _phase = Phase.Install;
            }

            if (_phase == Phase.Install)
            {
                ForceGuard();
                ShowResult(Loc["SdReset.Install.Title"], Loc["SdReset.Install.Hint"]);
                Log($"开始安装整合包：{_package!.Files.Length} 个文件 / {Bytes(_package.TotalBytes)}");
                await Task.Run(() => FreshInstall.ExecuteAsync(
                    _package!, _root!, Guard, Reporter(), CancellationToken.None));
                Log("整合包安装并逐文件校验完成", LogLevel.Success);
                ForceGuard();
                _phase = Phase.Deploy;
            }

            if (_phase != Phase.Deploy)
            {
                return;
            }

            ShowResult(Loc["SdReset.Deploy.Title"], Loc["SdReset.Deploy.Hint"]);
            Log("开始部署「下次开机自动创建虚拟系统」的任务");
            await Task.Run(() =>
            {
                ForceGuard();

                if (EmuTask.HasPending(_root!))
                {
                    try
                    {
                        EmuTask.Read(_root!);
                    }
                    catch (IOException)
                    {
                        EmuTaskFiles.Restore(_root!, Guard);
                    }
                }

                if (EmuTask.HasPending(_root!))
                {
                    EmuTaskFiles.Resume(_root!, _assets!, Guard);
                }
                else
                {
                    var payload = File.ReadAllBytes(PathPolicy.SafePath(_root!, "payload.bin"));
                    var hash = _package!.Files.Single(f => PathPolicy.Comparer.Equals(f.Relative, "payload.bin")).Hash;

                    if (!Convert.ToHexString(SHA256.HashData(payload)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException(Loc["SdReset.Error.PayloadChanged"]);
                    }

                    var task = EmuTask.Create(_layout!, _publishedMbr!, payload);
                    EmuTaskFiles.Deploy(_root!, task, _assets!, Guard);
                }

                ForceGuard();
                EmuTask.Read(_root!).ValidateLayout(_publishedMbr!, _device!.Capacity);
            });

            Complete();
            Log(InstallsPackage ? "全部完成：分区、格式化、整合包与自动创建任务均已就绪" : "全部完成：只做了分区与格式化（未安装内容）",
                LogLevel.Success);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log("已取消 Windows 授权，尚未开始本次操作", LogLevel.Warning);
            Error(Loc["SdReset.Error.UacCancelled"]);
        }
        catch (Exception ex)
        {
            var message = ServerAddressPrivacy.Hide(ex.Message);
            Log("写入失败：" + message, LogLevel.Error);
            Error(message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowResult(string title, string help)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        CompletionPanel.Visibility = Visibility.Collapsed;

        ResultTitle.Text = title;
        ResultTitle.Foreground = (Brush)FindResource("TextPrimaryBrush");
        ResultHelp.Text = help;

        StatusText.Text = string.Empty;
        ProgressPath.Text = string.Empty;
        ProgressPercent.Text = string.Empty;
        DownloadText.Text = string.Empty;

        ResetProgress.IsIndeterminate = true;
        ResetProgress.Value = 0;

        _latest = null;
        _speedClock.Reset();
    }

    private void Error(string text)
    {
        _latest = null;
        ResultTitle.Text = Loc["SdReset.Error.Title"];
        ResultTitle.Foreground = (Brush)FindResource("DangerBrush");
        StatusText.Text = text;
        ResetProgress.IsIndeterminate = false;
        ProgressPath.Text = string.Empty;
        ProgressPercent.Text = string.Empty;
        DownloadText.Text = string.Empty;

        ResultHelp.Text = _phase switch
        {
            Phase.Mount => Loc["SdReset.Error.HelpMount"],
            Phase.Install or Phase.Deploy => Loc["SdReset.Error.HelpPartial"],
            _ => Loc["SdReset.Error.HelpRestart"],
        };

        StartButton.Content = _phase == Phase.Mount ? Loc["SdReset.Button.Recheck"] : Loc["SdReset.Button.Retry"];
        StartButton.Visibility = _phase == Phase.Format ? Visibility.Collapsed : Visibility.Visible;

        if (_phase == Phase.Format)
        {
            BackButton.Visibility = Visibility.Visible;
        }
    }

    private void Complete()
    {
        _phase = Phase.Complete;
        _latest = null;
        SetStep(4);

        ResetProgress.IsIndeterminate = false;
        ResetProgress.Value = 100;
        ProgressPercent.Text = "100%";

        // 三种「不动分区表」的手术有各自的完成语 —— 套用「重建」那句会让人以为分区被重做了。
        if (SurgeryMode)
        {
            if (CurrentMode == Mode.FormatMain)
            {
                ResultTitle.Text = Loc["SdReset.Done.TitleFormatMain"];
            }
            else if (CurrentMode == Mode.FormatEmu)
            {
                ResultTitle.Text = Loc["SdReset.Done.TitleWipe"];
            }
            else
            {
                ResultTitle.Text = Loc["SdReset.Done.TitleCreateFiles"];
            }

            ResultTitle.Foreground = (Brush)FindResource("SuccessBrush");

            StatusText.Text = string.Empty;
            ProgressPath.Text = string.Empty;
            DownloadText.Text = string.Empty;
            ResultHelp.Text = string.Empty;

            if (CurrentMode == Mode.FormatMain)
            {
                CompletionAction.Text = Loc["SdReset.Done.ActionFormatMain"];
            }
            else if (CurrentMode == Mode.FormatEmu)
            {
                CompletionAction.Text = Loc["SdReset.Done.ActionWipe"];
            }
            else
            {
                CompletionAction.Text = Loc["SdReset.Done.ActionCreateFiles"];
            }

            RebuildInstructions.Visibility = Visibility.Collapsed;
            CompletionPanel.Visibility = Visibility.Visible;

            StartButton.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Collapsed;
            EjectResetButton.Visibility = _root is null ? Visibility.Collapsed : Visibility.Visible;
            EjectResetButton.IsEnabled = true;
            return;
        }

        var rebuild = _options!.EmuCount > 0;
        var withPackage = rebuild && InstallsPackage;

        ResultTitle.Text = RepartitionOnly
            ? Loc["SdReset.Done.TitleRepartition"]
            : rebuild
                ? (withPackage ? Loc["SdReset.Done.Title"] : Loc["SdReset.Done.TitlePartitionOnly"])
                : Loc["SdReset.Done.TitleFormatOnly"];
        ResultTitle.Foreground = (Brush)FindResource("SuccessBrush");

        StatusText.Text = string.Empty;
        ProgressPath.Text = string.Empty;
        DownloadText.Text = string.Empty;
        ResultHelp.Text = string.Empty;

        CompletionAction.Text = RepartitionOnly
            ? Loc["SdReset.Done.ActionRepartition"]
            : rebuild
                ? (withPackage ? Loc["SdReset.Done.Action"] : Loc["SdReset.Done.ActionPartitionOnly"])
                : Loc["SdReset.Done.ActionFormatOnly"];
        RebuildInstructions.Visibility = withPackage ? Visibility.Visible : Visibility.Collapsed;
        CompletionPanel.Visibility = Visibility.Visible;

        StartButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        EjectResetButton.Visibility = Visibility.Visible;
        EjectResetButton.IsEnabled = true;
    }

    // ── 底部按钮 ─────────────────────────────────────────────────

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        // ⚠️ XAML 加载期间就会触发：`WithEmu` 上写着 IsChecked="True"，BAML 把它落到
        //    ToggleButton 上时 Checked 事件已经接好了 —— 可这时排在它后面的控件
        //    （OnlyFormat、另外四个单选）还没被创建，字段全是 null。
        //    实测症状是「窗口一构造就 NullReferenceException」，外面只能看到「无法构造」。
        //    `_ready` 在 InitializeComponent 之后才置位，正好当这个总闸。
        if (!_ready)
        {
            return;
        }

        // 六个选项是**同一组单选按钮**（GroupName="Mode"），互斥由 WPF 自己保证：
        // 勾上一个，其余五个自动取消 —— 不需要我们再挨个 SetIsChecked(false)。
        // 这里只要按新的勾选状态重算预览即可（CurrentMode 是算出来的，不是存的）。
        UpdatePreview();
    }

    private void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DevicePicker.SelectedItem is SdDevice device)
        {
            Log("已选择设备：" + device.Display);
        }

        UpdatePreview();
    }

    private void Acknowledgement_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready && _phase == Phase.Confirm)
        {
            var acknowledged = Acknowledgement.IsChecked == true;
            StartButton.IsEnabled = acknowledged && !_busy;
            AckHint.Visibility = acknowledged ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            if (_cancelAllowed)
            {
                _cancel?.Cancel();

                // ⚠️ 主视图模型的生成管线用它**自己的** CancellationTokenSource ——
                //    只取消本窗口那个 token，下载/生成会照跑不误，用户看到的是「点了取消没反应」。
                if (_viewModel.IsRunning)
                {
                    _viewModel.CancelCommand.Execute(null);
                }
            }

            return;
        }

        Close();
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        BackToSettings();
        await RefreshAsync();
    }

    private async void Eject_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _ejected || _device is null || _root is null)
        {
            return;
        }

        SetBusy(true);
        EjectResetButton.IsEnabled = false;

        try
        {
            ForceGuard();
            var root = _root;
            await Task.Run(() => SafeEject.Eject(root));
            _ejected = true;
            ResultTitle.Text += " · " + Loc["SdReset.Done.Ejected"];
            ResultHelp.Text = string.Empty;
            EjectResetButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ResultHelp.Text = ServerAddressPrivacy.Hide(ex.Message);
            ResultHelp.Foreground = (Brush)FindResource("DangerBrush");
            EjectResetButton.IsEnabled = true;
        }
        finally
        {
            SetBusy(false);
        }
    }
}
