using System.Collections.ObjectModel;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;
using SwitchCfwWizard.Models;
using SwitchCfwWizard.Services;

namespace SwitchCfwWizard.ViewModels;

/// <summary>一个组件（Atmosphere / Hekate / Ultrahand / Sys-patch）的可绑定视图模型。</summary>
public sealed class ComponentViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isSelectable = true;
    private string? _lockHintKey;
    private string? _conflictHintKey;
    private string[] _lockHintTitles = [];
    private string[] _conflictHintTitles = [];
    private double _progress;
    private string _status;
    private string _versionText = string.Empty;
    private string _channelText = string.Empty;
    private bool _isBusy;
    private bool _hasResolvedRelease;

    public ComponentViewModel(ComponentDefinition definition, IReadOnlyDictionary<string, string>? savedValues)
    {
        Definition = definition;
        Kind = definition.Kind;
        Category = definition.Category;
        FolderName = ConfigGenerator.FolderName(definition.Kind);
        _status = LocalizationService.Instance["Common.NotSelected"];

        OptionGroups = BuildOptionGroups(definition, savedValues);
    }

    public ComponentDefinition Definition { get; }

    public ComponentKind Kind { get; }

    /// <summary>界面上归到哪一组（见 <see cref="ComponentCategory"/>）。</summary>
    public ComponentCategory Category { get; }

    public string FolderName { get; }

    /// <summary>
    /// 卡片标题。**每次读取都现查**，不在构造函数里存下来。
    ///
    /// ⚠️ 这里原先是个只在构造函数里赋值一次的只读自动属性，而 <see cref="RefreshLocalizedText"/>
    /// 会为它发 <c>PropertyChanged</c> —— 于是「切语言」时界面重新读了一遍**同一个旧值**，
    /// 卡片的说明文字始终停在上一个语言。发通知却不换值，是最难发现的那种假刷新：
    /// 代码看起来该做的都做了。
    /// 改成计算属性后，「通知一发、值真的变」成为结构上的保证。
    /// </summary>
    public string Title => ComponentCatalog.ResolveComponentTitle(Definition);

    /// <summary>卡片说明文字。同 <see cref="Title"/>，现查而不是存快照。</summary>
    public string Description => ComponentCatalog.ResolveComponentDescription(Definition);

    public ObservableCollection<AssetViewModel> Assets { get; } = new();

    public ObservableCollection<OptionGroupViewModel> OptionGroups { get; }

    public bool HasOptions => OptionGroups.Count > 0;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                if (!value)
                {
                    Progress = 0;
                    Status = LocalizationService.Instance["Common.NotSelected"];
                    foreach (var asset in Assets)
                    {
                        asset.Reset();
                    }
                }
                else if (!IsBusy)
                {
                    Status = LocalizationService.Instance["Common.Ready"];
                }

                OnPropertyChanged(nameof(IsExpandedByDefault));
            }
        }
    }

    /// <summary>false 表示复选框被锁定（例如 Sys-patch 被强制勾选）。</summary>
    public bool IsSelectable
    {
        get => _isSelectable;
        set => SetProperty(ref _isSelectable, value);
    }

    /// <summary>
    /// 复选框被锁定时给出的原因（多语言 key，为空表示未锁定）。
    ///
    /// 只把复选框变灰是不够的——用户看不出「为什么不能点」，会以为是程序坏了。
    /// 存 key 而不是存文案，是为了切换语言时能跟着刷新。
    /// </summary>
    public string? LockHintKey
    {
        get => _lockHintKey;
        set
        {
            // 直接赋键（例如 Sys-patch 的强制锁）时把「点名的组件」清掉：
            // 留着上一把锁的名字，会被填进新文案的 {0} 里，说出一个根本不相关的组件。
            if (!string.Equals(_lockHintKey, value, StringComparison.Ordinal))
            {
                _lockHintTitles = [];
            }

            if (SetProperty(ref _lockHintKey, value))
            {
                OnPropertyChanged(nameof(LockHint));
                OnPropertyChanged(nameof(HasLockHint));
            }
        }
    }

    /// <summary>
    /// 锁定原因的本地化文案（未锁定时为空串）。
    ///
    /// 文案里的 <c>{0}</c> 会填上 <see cref="_lockHintTitles"/> —— 用户 2026-11-22 的要求：
    /// 「与另一个组件冲突」太空，必须点名是**哪一个**（`ldn_mitm` / `sys-botbase` …）。
    /// 名字取自卡片的 <see cref="Title"/>，所以切语言时这里跟着变（<see cref="RefreshLocalizedText"/> 会重发通知）。
    /// </summary>
    public string LockHint => Compose(_lockHintKey, _lockHintTitles);

    public bool HasLockHint => !string.IsNullOrEmpty(_lockHintKey);

    /// <summary>
    /// 设置锁定原因。<paramref name="titles"/> 是要点名的组件（填进文案的 <c>{0}</c>）；
    /// <paramref name="key"/> 传 <c>null</c> 表示解除锁定。
    ///
    /// 走方法而不是直接赋 <see cref="LockHintKey"/>，是因为「键没变、名字变了」时
    /// 属性 setter 的 <c>SetProperty</c> 不会发通知 —— 那样界面上会留着上一个名字。
    /// </summary>
    internal void SetLockHint(string? key, params string[] titles)
    {
        _lockHintKey = key;
        _lockHintTitles = titles;
        OnPropertyChanged(nameof(LockHintKey));
        OnPropertyChanged(nameof(LockHint));
        OnPropertyChanged(nameof(HasLockHint));
    }

    /// <summary>
    /// 「这个组件和另一个勾了的组件装同一个 sysmodule title」的提示（多语言 key，为空表示不冲突）。
    ///
    /// 与 <see cref="LockHintKey"/> 分开：锁定是**不让选**，冲突是**能选但不该同时选**。
    /// 混用同一个字段的话，界面上会看不出「这个是灰的不能点」还是「这个能点、但和另一个打架」。
    /// </summary>
    public string? ConflictHintKey
    {
        get => _conflictHintKey;
        set
        {
            if (!string.Equals(_conflictHintKey, value, StringComparison.Ordinal))
            {
                _conflictHintTitles = [];
            }

            if (SetProperty(ref _conflictHintKey, value))
            {
                OnPropertyChanged(nameof(ConflictHint));
                OnPropertyChanged(nameof(HasConflictHint));
            }
        }
    }

    /// <summary>冲突提示的本地化文案（不冲突时为空串）。占位符规则同 <see cref="LockHint"/>。</summary>
    public string ConflictHint => Compose(_conflictHintKey, _conflictHintTitles);

    public bool HasConflictHint => !string.IsNullOrEmpty(_conflictHintKey);

    /// <summary>设置冲突提示（语义同 <see cref="SetLockHint"/>）。</summary>
    internal void SetConflictHint(string? key, params string[] titles)
    {
        _conflictHintKey = key;
        _conflictHintTitles = titles;
        OnPropertyChanged(nameof(ConflictHintKey));
        OnPropertyChanged(nameof(ConflictHint));
        OnPropertyChanged(nameof(HasConflictHint));
    }

    /// <summary>
    /// 取文案并填空（实现在 <see cref="HintText.Compose"/> —— 那里是纯函数，回归能直接测）。
    /// </summary>
    private static string Compose(string? key, string[] titles) => HintText.Compose(key, titles);

    public bool IsExpandedByDefault => IsSelected;

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string VersionText
    {
        get => _versionText;
        set => SetProperty(ref _versionText, value);
    }

    public string ChannelText
    {
        get => _channelText;
        set => SetProperty(ref _channelText, value);
    }

    public bool HasVersion => !string.IsNullOrEmpty(VersionText);

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public bool HasResolvedRelease
    {
        get => _hasResolvedRelease;
        set => SetProperty(ref _hasResolvedRelease, value);
    }

    /// <summary>把当前勾选的配置项写入 WizardOptions。</summary>
    public void ApplyOptionValues(WizardOptions options)
    {
        foreach (var option in OptionGroups.SelectMany(g => g.Options))
        {
            options.Set(Kind, option.Key, option.Value);
        }
    }

    public OptionViewModel? FindOption(string key)
        => OptionGroups.SelectMany(g => g.Options).FirstOrDefault(o => o.Key == key);

    public string GetOptionValue(string key)
    {
        var option = FindOption(key);
        if (option is not null)
        {
            return option.Value;
        }

        var definition = Definition.Options.FirstOrDefault(o => o.Key == key);
        return definition?.DefaultValue ?? string.Empty;
    }

    public void ResetProgress()
    {
        Progress = 0;
        HasResolvedRelease = false;
        VersionText = string.Empty;
        ChannelText = string.Empty;
        Assets.Clear();
        Status = IsSelected
            ? LocalizationService.Instance["Common.Ready"]
            : LocalizationService.Instance["Common.NotSelected"];
    }

    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(LockHint));
        OnPropertyChanged(nameof(ConflictHint));

        foreach (var group in OptionGroups)
        {
            group.RefreshLocalizedText();
        }
    }

    private static ObservableCollection<OptionGroupViewModel> BuildOptionGroups(
        ComponentDefinition definition,
        IReadOnlyDictionary<string, string>? savedValues)
    {
        var options = new List<OptionViewModel>();

        foreach (var optionDefinition in definition.Options)
        {
            var initial = savedValues is not null && savedValues.TryGetValue(optionDefinition.Key, out var saved)
                ? saved
                : optionDefinition.DefaultValue;

            options.Add(new OptionViewModel(optionDefinition, initial));
        }

        // 建立选项之间的显示依赖
        foreach (var option in options)
        {
            var dependencyKey = option.Definition.VisibleWhenKey;
            if (string.IsNullOrEmpty(dependencyKey))
            {
                continue;
            }

            var dependency = options.FirstOrDefault(o => o.Key == dependencyKey);
            if (dependency is not null)
            {
                option.AttachDependency(dependency);
            }
        }

        var groups = new List<OptionGroupViewModel>();
        foreach (var group in options.GroupBy(o => o.GroupKey))
        {
            groups.Add(new OptionGroupViewModel(group.Key, group));
        }

        return new ObservableCollection<OptionGroupViewModel>(groups);
    }
}
