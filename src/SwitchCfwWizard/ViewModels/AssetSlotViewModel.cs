using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;
using SwitchCfwWizard.Models;
using SwitchCfwWizard.Services;

namespace SwitchCfwWizard.ViewModels;

/// <summary>
/// 高级设置里「一个可下载文件」的一行：左边一个地址框、右边一个文件名框。
///
/// 空值 = 用声明的默认值（界面上把默认值当占位符显示）。这样用户不必先清空再填，
/// settings.json 里也不会存一堆「等于默认值」的噪音；将来默认值变了，没改过的用户会自动跟上。
/// 与 <see cref="RepoSourceViewModel"/> 是同一套约定，只是多了「文件名」这第二个维度。
/// </summary>
public sealed class AssetSlotViewModel : ObservableObject
{
    private readonly ComponentDefinition _definition;
    private readonly AssetSlot _slot;
    private string _address;
    private string _fileName;

    public AssetSlotViewModel(
        ComponentDefinition definition,
        AssetSlot slot,
        string? savedAddress,
        string? savedFileName)
    {
        _definition = definition;
        _slot = slot;
        _address = savedAddress ?? string.Empty;
        _fileName = savedFileName ?? string.Empty;
    }

    /// <summary>settings.json 里的键（<c>&lt;组件&gt;/&lt;槽 Key&gt;</c>）。</summary>
    public string Key => ComponentCatalog.SlotSettingKey(_definition, _slot);

    /// <summary>
    /// 默认下载地址。**每次读取都现算** —— 与 <see cref="DefaultFileName"/> 同理：
    /// 它可能跟着界面语言变（linkalho 中文界面走 <c>SwitchScriptTW</c> 的镜像），
    /// 存成快照的话切语言时占位符不会更新，用户就看不到「跟随界面语言」真的生效了。
    /// </summary>
    public string DefaultAddress => _slot.Address(LocalizationService.Instance.Current.Code).DisplayName;

    /// <summary>
    /// 默认文件名。**每次读取都现算** —— 它可能跟着界面语言变（DBI 的翻译文件），
    /// 存成快照的话切语言时占位符不会更新，用户就看不到「跟随界面语言」这件事真的生效了。
    /// </summary>
    public string DefaultFileName => _slot.FileName(LocalizationService.Instance.Current.Code);

    /// <summary>用户填的地址。空串表示「用默认」。</summary>
    public string Address
    {
        get => _address;
        set
        {
            if (SetProperty(ref _address, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasCustomAddress));
                OnPropertyChanged(nameof(IsAddressInvalid));
                OnPropertyChanged(nameof(EffectiveAddress));
                OnPropertyChanged(nameof(PlacementHint));
                OnPropertyChanged(nameof(IsAddressEmpty));
            }
        }
    }

    /// <summary>用户填的文件名。空串表示「用默认」。</summary>
    public string FileName
    {
        get => _fileName;
        set
        {
            if (SetProperty(ref _fileName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasCustomFileName));
                OnPropertyChanged(nameof(EffectiveFileName));
                OnPropertyChanged(nameof(PlacementHint));
                OnPropertyChanged(nameof(DirectoryHint));
                OnPropertyChanged(nameof(IsFileNameEmpty));
            }
        }
    }

    /// <summary>
    /// 框里是空的 —— 界面上据此把默认值当**灰色占位文字**显示在框内。
    ///
    /// 为什么要有它：用户要求「未修改时用提供的默认地址和文件名」。把默认值当占位文字显示
    /// （而不是先填进去）有两个好处：① 用户不必先清空再填；② settings.json 里不会存一堆
    /// 「等于默认值」的噪音，将来默认值变了，没改过的用户会自动跟上。
    /// WPF 的 TextBox 没有原生的占位文字，所以由外面叠一个 TextBlock 来显示。
    /// </summary>
    public bool IsAddressEmpty => string.IsNullOrEmpty(_address);

    /// <inheritdoc cref="IsAddressEmpty"/>
    public bool IsFileNameEmpty => string.IsNullOrEmpty(_fileName);

    public bool HasCustomAddress =>
        !string.IsNullOrWhiteSpace(Address)
        && !string.Equals(Address.Trim(), DefaultAddress, StringComparison.OrdinalIgnoreCase);

    public bool HasCustomFileName =>
        !string.IsNullOrWhiteSpace(FileName)
        && !string.Equals(FileName.Trim(), DefaultFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 填了东西但解析不出仓库（不是 <c>owner/name</c> 形态）。
    /// 运行时**回落到默认地址**而不是报错，界面上要就地标出来，别让用户以为改动生效了。
    /// </summary>
    public bool IsAddressInvalid => HasCustomAddress && RepoSpec.TryParse(Address) is null;

    /// <summary>这次实际会用的地址。</summary>
    public string EffectiveAddress =>
        HasCustomAddress && RepoSpec.TryParse(Address) is { } parsed ? parsed.DisplayName : DefaultAddress;

    /// <summary>这次实际会下的文件名。</summary>
    public string EffectiveFileName =>
        string.IsNullOrWhiteSpace(FileName) ? DefaultFileName : FileName.Trim();

    /// <summary>
    /// 这一行取的是**一个目录下的所有文件**（而不是单个文件）。
    ///
    /// 界面上据此把「第二个框是仓库内的目录名」说清楚 —— 否则用户看到一个写着
    /// <c>systemPatches</c> 的「文件名」框，会以为要去下某个叫 systemPatches 的文件。
    /// 这是 <see cref="SlotSource.RepoDirectory"/> 的槽独有的语义，见该枚举成员的说明。
    /// </summary>
    public bool IsDirectorySlot => _slot.Source == SlotSource.RepoDirectory;

    /// <summary>目录槽专属的说明（非目录槽不显示）。</summary>
    public string DirectoryHint => string.Format(
        LocalizationService.Instance["Settings.Slots.DirectoryHint"],
        EffectiveFileName);

    /// <summary>这个文件会落到 out/ 的哪儿（压缩包报目录、散装文件报文件）。</summary>
    public string PlacementHint => string.Format(
        LocalizationService.Instance["Settings.Slots.Placement"],
        ComponentCatalog.DescribeSlotPlacement(_slot, EffectiveFileName));

    public string DefaultAddressHint => string.Format(
        LocalizationService.Instance["Settings.Slots.DefaultAddress"],
        DefaultAddress);

    public string DefaultFileNameHint => string.Format(
        LocalizationService.Instance["Settings.Slots.DefaultFileName"],
        DefaultFileName);

    /// <summary>
    /// 「地址填错了」的完整提示（含槽名与填错的值）。
    ///
    /// 在视图模型里拼而不是让 XAML 直接显示 <c>Settings.Slots.InvalidAddress</c>：那条文案带
    /// <c>{0}</c>/<c>{1}</c> 占位符，而语言标记扩展**不做格式化**，直接绑上去用户会看到花括号。
    /// </summary>
    public string InvalidAddressHint => string.Format(
        LocalizationService.Instance["Settings.Slots.InvalidAddress"],
        Key,
        Address.Trim());

    /// <summary>切换界面语言后刷新文案与那些「按语言求值」的默认值。</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(DefaultFileName));
        OnPropertyChanged(nameof(DefaultAddress));
        OnPropertyChanged(nameof(DefaultFileNameHint));
        OnPropertyChanged(nameof(DefaultAddressHint));
        OnPropertyChanged(nameof(PlacementHint));
        OnPropertyChanged(nameof(DirectoryHint));
        OnPropertyChanged(nameof(InvalidAddressHint));
    }
}
