using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Models;
using SwitchCfwWizard.Services;

namespace SwitchCfwWizard.ViewModels;

/// <summary>
/// 高级设置里「一个插件的全部可下载文件」—— 一组 <see cref="AssetSlotViewModel"/>。
///
/// 分组只是为了让用户看得出「这几行属于同一个插件」：一个插件有多个文件时（DBI、Luna），
/// 平铺成一长串输入框根本分不清哪行是哪行的。
/// </summary>
public sealed class AssetSlotGroupViewModel : ObservableObject
{
    public AssetSlotGroupViewModel(ComponentDefinition definition, IReadOnlyList<AssetSlotViewModel> slots)
    {
        Definition = definition;
        Slots = slots;
    }

    public ComponentDefinition Definition { get; }

    public IReadOnlyList<AssetSlotViewModel> Slots { get; }

    /// <summary>组标题 = 插件名。**每次读取都现查**，切语言时才会真的换值（同 <see cref="ComponentViewModel.Title"/>）。</summary>
    public string Title => ComponentCatalog.ResolveComponentTitle(Definition);

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Title));

        foreach (var slot in Slots)
        {
            slot.RefreshLocalizedText();
        }
    }
}
