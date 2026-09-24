using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using SwitchCfwWizard.ViewModels;

namespace SwitchCfwWizard;

public partial class MainWindow : Window
{
    private INotifyCollectionChanged? _observedLogs;

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

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0)
        {
            return;
        }

        // 新日志追加到末尾后自动滚动，方便用户看到实时进度
        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
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
