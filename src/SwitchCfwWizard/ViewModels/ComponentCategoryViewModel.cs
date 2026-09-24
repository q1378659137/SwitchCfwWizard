using System.Collections.ObjectModel;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;
using SwitchCfwWizard.Models;
using SwitchCfwWizard.Services;

namespace SwitchCfwWizard.ViewModels;

/// <summary>
/// 界面上的一组组件卡片（一个「分类文件夹」）。
///
/// 存在的理由：主界面的组件区原先是一个平铺的 <c>ItemsControl</c>，四个组件还好认；
/// 用户 2026-09-18 要求把插件按用途分组（框架 / 组件 / 后台 / 主题 / 底层 / Ultrahand插件 / 学习），
/// 分组之后**必须有个东西承载组标题**，否则只是一堆卡片叠在一起，看不出分界。
/// </summary>
public sealed class ComponentCategoryViewModel : ObservableObject
{
    public ComponentCategoryViewModel(ComponentCategory category, IEnumerable<ComponentViewModel> components)
    {
        Category = category;
        Components = new ObservableCollection<ComponentViewModel>(components);
    }

    public ComponentCategory Category { get; }

    public ObservableCollection<ComponentViewModel> Components { get; }

    /// <summary>
    /// 是不是「框架」那一组。
    ///
    /// 为什么需要它：<c>boot.dat</c> 的开关不是组件、没有卡片，但用户要求把它放进「框架」这一组里
    /// （2026-09-21，原先在「输出内容」）。分组模板是按分类渲染的，所以得有个判据让模板只在这一组
    /// 多渲染一张卡片 —— 放在这里而不是在 XAML 里比较枚举：XAML 里比较枚举要写 <c>x:Static</c>
    /// 加一长串类型名，而**写错的比较不会报错**，只会让那张卡片永远不出现。
    /// </summary>
    public bool IsFramework => Category == ComponentCategory.Framework;

    /// <summary>
    /// 组标题。语言键由 <see cref="ComponentCatalog.CategoryTitleKey"/> 统一生成 ——
    /// 界面与护栏共用同一处命名规则，免得护栏按 "Category.X" 去查、界面按别的写法取。
    ///
    /// 现查而不是存快照：理由同 <see cref="ComponentViewModel.Title"/>（发通知却不换值 = 假刷新）。
    /// </summary>
    public string Title => LocalizationService.Instance[ComponentCatalog.CategoryTitleKey(Category)];

    /// <summary>切语言后由 <see cref="MainViewModel.RefreshLocalizedText"/> 调用。</summary>
    public void RefreshLocalizedText() => OnPropertyChanged(nameof(Title));
}
