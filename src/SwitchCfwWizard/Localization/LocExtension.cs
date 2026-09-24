using System.Windows.Data;
using System.Windows.Markup;

namespace SwitchCfwWizard.Localization;

/// <summary>
/// XAML 中取多语言文本：<c>Text="{loc:Loc App.Title}"</c>。
/// 语言切换时由 LocalizationService 触发 "Item[]" 变更通知自动刷新。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding("[" + Key + "]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
        };

        return binding.ProvideValue(serviceProvider);
    }
}
