using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;
using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.ViewModels;

/// <summary>
/// 高级设置里「下载源」的一行：一个插件仓库 + 用户填的地址。
///
/// **空值 = 用内置默认地址**（界面上把默认值当占位符显示）。这样用户不必先清空再填，
/// settings.json 里也不会存一堆「等于默认值」的噪音；将来默认地址变了，没改过的用户会自动跟上。
/// </summary>
public sealed class RepoSourceViewModel : ObservableObject
{
    private string _value;

    public RepoSourceViewModel(string key, string name, string? hintKey, string? value)
    {
        Key = key;
        Name = name;
        HintKey = hintKey;
        _value = value ?? string.Empty;
    }

    /// <summary>仓库的默认地址（<c>owner/name</c>）。它同时是 settings.json 里的键。</summary>
    public string Key { get; }

    /// <summary>插件名。三个语言下写法相同，不走多语言表。</summary>
    public string Name { get; }

    public string? HintKey { get; }

    public bool HasHint => !string.IsNullOrEmpty(HintKey);

    /// <summary>插件名（界面上显示在输入框左边）。</summary>
    public string Label => Name;

    public string Hint => HasHint ? LocalizationService.Instance[HintKey!] : string.Empty;

    /// <summary>默认地址。界面上当占位符（placeholder）显示。</summary>
    public string DefaultValue => Key;

    /// <summary>用户填的内容。空串表示「用默认」。</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasCustomValue));
                OnPropertyChanged(nameof(IsInvalid));
                OnPropertyChanged(nameof(EffectiveValue));
            }
        }
    }

    /// <summary>是否填了「与默认不同」的值。空白不算。</summary>
    public bool HasCustomValue =>
        !string.IsNullOrWhiteSpace(Value)
        && !string.Equals(Value.Trim(), Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 填了东西但解析不出来（不是 <c>owner/name</c> 形态）。
    /// 运行时会**回落到默认地址**而不是报错，界面上要就地标出来，别让用户以为改动生效了。
    /// </summary>
    public bool IsInvalid => HasCustomValue && RepoSpec.TryParse(Value) is null;

    /// <summary>这次实际会用的地址。</summary>
    public string EffectiveValue => HasCustomValue && RepoSpec.TryParse(Value) is { } parsed
        ? parsed.DisplayName
        : Key;

    /// <summary>切换界面语言后刷新文案。</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Hint));
    }
}
