namespace SwitchCfwWizard.Models;

/// <summary>
/// 一个产物在 <c>out/</c> 里的落点。路径一律相对 <c>out/</c> 根目录，
/// 例如 <c>new OutputTarget("switch/.overlays")</c> 对应 <c>out/switch/.overlays/</c>。
///
/// 之所以要有这个东西：上游各个发布包的「内部结构」并不统一 ——
///   · <c>sdout.zip</c> / <c>nx-ovlloader.zip</c> / Atmosphere / Hekate / Sys-patch 的包
///     内部本来就是 SD 卡根目录的样子，整棵铺开即可；
///   · <c>lang.zip</c> 里是 14 个**裸 json**（连一层目录都没有），必须显式指定落到
///     <c>config/ultrahand/lang/</c>；
///   · <c>ovlSysmodules.ovl</c>、<c>ovlmenu.ovl</c> 是裸文件，要落到 <c>switch/.overlays/</c>；
///   · <c>fusee.bin</c> 要落到 <c>bootloader/payloads/</c>；
///   · hekate 的 payload 要**同时**落到根目录 <c>payload.bin</c> 和 <c>bootloader/update.bin</c>。
/// 以前这些差异靠「按文件后缀猜 + 平铺到 out 根目录」糊过去，结果就是文件错位。
/// 现在每个下载物在选取的那一刻（<c>ComponentCatalog</c> 的 picker 里）就声明自己的落点。
/// </summary>
/// <param name="Directory">目标目录（相对 <c>out/</c>）。空串表示 <c>out/</c> 根目录。</param>
/// <param name="FileName">
/// 落盘文件名。<c>null</c> 表示沿用来源文件名。
/// 压缩包忽略此项 —— 包内容按它自己的内部结构在 <see cref="Directory"/> 下铺开。
/// </param>
public sealed record OutputTarget(string Directory, string? FileName = null)
{
    /// <summary>
    /// <see cref="Directory"/> 里的占位符：**下载文件名去掉扩展名**。
    ///
    /// 存在的理由（2026-09-18 用户要求）：离线固件要落在 <c>out/Firmware/&lt;版本&gt;/</c>，
    /// 而 <c>THZoria/NX_Firmware</c> 的包**内部是平的**（实测 238 个 <c>.nca</c> 全在根，
    /// 没有顶层目录可借），版本只出现在**资源名**里（<c>Firmware.23.0.0.zip</c>）。
    /// 于是那个目录名只能从「下载下来的那个文件名」推出来 ⇒ 声明成
    /// <c>new OutputTarget("Firmware/{asset}")</c>。
    ///
    /// 顺带得到一个好性质：**目录跟着文件名走**。用户把文件名改成
    /// <c>Firmware 20.0.0.zip</c>，产物就是 <c>out/Firmware/Firmware 20.0.0/</c>，
    /// 不会出现「文件叫 A、目录叫 B」这种要对着两个名字猜的产物。
    /// </summary>
    public const string AssetNameToken = "{asset}";

    /// <summary>这个落点的目录里有没有 <see cref="AssetNameToken"/>。</summary>
    public bool HasAssetNameToken =>
        Directory.Contains(AssetNameToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>把 <see cref="AssetNameToken"/> 换成给定的名字（不含扩展名）。没有占位符时原样返回。</summary>
    public OutputTarget ResolveAssetName(string assetNameWithoutExtension) =>
        HasAssetNameToken
            ? this with
            {
                Directory = Directory.Replace(
                    AssetNameToken, assetNameWithoutExtension, StringComparison.OrdinalIgnoreCase),
            }
            : this;

    /// <summary>把来源文件名解析成相对 <c>out/</c> 的完整路径（统一用 <c>/</c> 分隔）。</summary>
    public string Resolve(string sourceFileName)
    {
        var name = string.IsNullOrEmpty(FileName) ? sourceFileName : FileName;
        return string.IsNullOrEmpty(Directory)
            ? name
            : $"{Directory.TrimEnd('/', '\\')}/{name}";
    }
}
