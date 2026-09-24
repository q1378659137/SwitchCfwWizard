using System.Collections.ObjectModel;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;
using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.ViewModels;

public sealed class OptionChoiceViewModel : ObservableObject
{
    public OptionChoiceViewModel(OptionChoice choice)
    {
        Value = choice.Value;
        LabelKey = choice.LabelKey;
        DisplayText = choice.DisplayText;
    }

    public string Value { get; }

    public string LabelKey { get; }

    /// <summary>非空时直接显示，不查多语言表（组合键这类候选项）。</summary>
    public string? DisplayText { get; }

    public string Label => DisplayText ?? LocalizationService.Instance[LabelKey];

    public void RefreshLocalizedText() => OnPropertyChanged(nameof(Label));
}

/// <summary>一个可配置项的可绑定包装。</summary>
public sealed class OptionViewModel : ObservableObject
{
    private OptionViewModel? _dependency;
    private string _value;

    public OptionViewModel(OptionDefinition definition, string value)
    {
        Definition = definition;
        _value = value;

        Choices = new ObservableCollection<OptionChoiceViewModel>(
            (definition.Choices ?? []).Select(c => new OptionChoiceViewModel(c)));

        if (definition.DynamicChoices)
        {
            // 候选项还没建全（真正的列表由 MainViewModel 稍后生成），此时**不能**自愈：
            // 否则存档里的合法值（如 autoboot="3"）会因为占位列表里只有 "0" 而被当场改成 "0"，
            // 等真正的候选项建出来时，值已经被改掉了。原样保留，交由重建那一步校验。
            OnPropertyChanged(nameof(SelectedChoice));
        }
        else
        {
            // 静态候选项：值不在列表里就回退到第一项（从损坏/过期设置里自愈）
            SelectedChoice = Choices.FirstOrDefault(c => string.Equals(c.Value, _value, StringComparison.Ordinal))
                             ?? Choices.FirstOrDefault();
        }

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    public OptionDefinition Definition { get; }

    public string Key => Definition.Key;

    public string GroupKey => Definition.GroupKey;

    public ObservableCollection<OptionChoiceViewModel> Choices { get; }

    public bool HasChoices => Choices.Count > 0;

    public bool IsCheckBox => Definition.Editor == OptionEditor.CheckBox;

    public bool IsComboBox => Definition.Editor == OptionEditor.ComboBox;

    public bool IsText => Definition.Editor is OptionEditor.TextBox or OptionEditor.Number;

    public string Label => LocalizationService.Instance[Definition.LabelKey];

    public string Description => string.IsNullOrEmpty(Definition.DescriptionKey)
        ? string.Empty
        : LocalizationService.Instance[Definition.DescriptionKey];

    public bool HasDescription => !string.IsNullOrEmpty(Definition.DescriptionKey);

    /// <summary>当前值（统一以字符串保存，写入 ini 时直接使用）。</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(BoolValue));
                OnPropertyChanged(nameof(TextValue));
                OnPropertyChanged(nameof(SelectedChoice));
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool BoolValue
    {
        get => _value is "1" or "true" or "True";
        set
        {
            var normalized = value ? "1" : "0";
            if (!string.Equals(_value, normalized, StringComparison.Ordinal))
            {
                Value = normalized;
            }
        }
    }

    public string TextValue
    {
        get => _value;
        set => Value = value ?? string.Empty;
    }

    public OptionChoiceViewModel? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => string.Equals(c.Value, _value, StringComparison.Ordinal));
        set
        {
            if (value is not null && !string.Equals(_value, value.Value, StringComparison.Ordinal))
            {
                Value = value.Value;
            }
        }
    }

    /// <summary>是否满足显示条件（由外部设置依赖项后计算）。</summary>
    public bool IsVisible
    {
        get
        {
            if (_dependency is null)
            {
                return true;
            }

            return string.Equals(_dependency.Value, Definition.VisibleWhenValue, StringComparison.Ordinal);
        }
    }

    public event EventHandler? ValueChanged;

    /// <summary>为 autoboot 这类动态下拉框替换候选项。</summary>
    public void ReplaceChoices(IEnumerable<OptionChoice> choices)
    {
        var selected = _value;

        Choices.Clear();
        foreach (var choice in choices)
        {
            Choices.Add(new OptionChoiceViewModel(choice));
        }

        OnPropertyChanged(nameof(HasChoices));

        // 原选择若仍存在则保留，否则回退到第一项
        if (!Choices.Any(c => string.Equals(c.Value, selected, StringComparison.Ordinal)))
        {
            var fallback = Choices.FirstOrDefault()?.Value ?? "0";
            Value = fallback;
        }
        else
        {
            OnPropertyChanged(nameof(SelectedChoice));
        }
    }

    internal void AttachDependency(OptionViewModel dependency)
    {
        _dependency = dependency;
        dependency.ValueChanged += (_, _) => OnPropertyChanged(nameof(IsVisible));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedText();
    }

    /// <summary>语言切换后刷新所有本地化文本。</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Description));

        foreach (var choice in Choices)
        {
            choice.RefreshLocalizedText();
        }
    }
}

/// <summary>一组配置项（对应 ini 文件或功能分区）。</summary>
public sealed class OptionGroupViewModel : ObservableObject
{
    public OptionGroupViewModel(string headerKey, IEnumerable<OptionViewModel> options)
    {
        HeaderKey = headerKey;
        Options = new ObservableCollection<OptionViewModel>(options);
    }

    public string HeaderKey { get; }

    public string Header => LocalizationService.Instance[HeaderKey];

    public ObservableCollection<OptionViewModel> Options { get; }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Header));

        foreach (var option in Options)
        {
            option.RefreshLocalizedText();
        }
    }
}
