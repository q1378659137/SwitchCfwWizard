// 移植自 yijianhuanbao（源文件 SdResetRequest.cs，sha256 bed6aa31b8d7…）。
// 2026-11-21 扩展：新增 <see cref="SdResetAction"/> —— 用户要求「仅格式化」下还能
// 「只清空 emuMMC 分区」与「只创建 emuMMC 所需文件」两种更小的手术。

namespace SwitchCfwWizard.SdReset;

/// <summary>
/// 这一次要做什么。默认值是 <see cref="Rebuild"/>，所以老调用方（只传前两个参数）行为不变。
/// </summary>
internal enum SdResetAction
{
	/// <summary>清卡 → 新分区表（主 + emuMMC）→ 格式化主分区。就是「格式化并重建 emuMMC」。</summary>
	Rebuild = 0,

	/// <summary>清卡 → 单主分区 → 格式化。「仅格式化」。</summary>
	FormatOnly = 1,

	/// <summary>
	/// **不动分区表**，只把现有 emuMMC 隐藏分区的前 16 MiB 写零（用户 2026-11-21 的新选项）。
	/// 主数据分区里的东西一个字节都不碰。
	/// </summary>
	WipeEmuPartition = 2,

	/// <summary>
	/// **不动分区表**，只把组件包里的文件写到卡上（bootloader/ns-toolbox 与 payload.bin），
	/// 让下次开机由它创建 emuMMC（用户 2026-11-21 的新选项）。
	/// </summary>
	CreateEmuFiles = 3,

	/// <summary>
	/// **不动分区表、不碰 emuMMC**，只就地格式化主数据分区（用户 2026-11-21 追加的选项）。
	/// 主分区里的文件会被清空；emuMMC 与分区表原样保留。
	/// </summary>
	FormatMain = 4,
}

internal sealed record SdResetRequest(
	SdDevice Device,
	SdFormatOptions Options,
	bool MountOnly = false,
	uint ExpectedSignature = 0u,
	SdResetAction Action = SdResetAction.Rebuild,
	string? AutoEmuZipPath = null);
