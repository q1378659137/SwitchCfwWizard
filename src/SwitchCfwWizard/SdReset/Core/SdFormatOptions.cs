// 移植自 yijianhuanbao（源文件 SdFormatOptions.cs，sha256 066da1324bc9…）。
// 逻辑与原版逐行一致，**唯一例外**：`EmuSizeGiB` 从 int 改成 double
// （用户 2026-11-20 要求容量可以设小数；分区大小本来按扇区算，本来就不必是整数 GiB）。
// 改动请同时对照源项目。
namespace SwitchCfwWizard.SdReset;

public sealed record SdFormatOptions(
    SdFileSystem FileSystem = SdFileSystem.Fat32,
    int EmuCount = 1,
    double EmuSizeGiB = 12d,
    SdFullEmuSize FullSize = SdFullEmuSize.None);
