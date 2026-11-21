using System;

namespace SwitchCfwWizard.SdReset;

/// <summary>
/// 「拖动分配容量」与「手动输入容量」的全部换算（纯函数，离线可测）。
///
/// 为什么单独抽出来：这条交互的两个方向都很容易写反 ——
///   ① 拖动位置 → emuMMC 容量（手指往右拖 = 主分区变大 = emuMMC 变小）；
///   ② emuMMC 容量 → 分隔条位置（重绘时用）。
/// 两者必须互为逆运算，任何一侧写错，界面上都只是「拖起来有点怪」，
/// 而真正写进卡里的分区表却是另一套尺寸 —— 这种错只能靠离线用例钉住。
///
/// 约束只有一条硬性的：**主数据分区必须 &gt; 2 GiB**。emuMMC 只有下界（5 GiB），
/// **没有固定上限** —— 上界就是「这张卡剩下的空间」（用户 2026-11-20 要求）。
///
/// 容量单位一律是 **GiB 的 double**，量化到 <see cref="StepGiB"/> = 0.01 GiB（10.24 MiB）。
/// 早期版本用的是「整数 GiB」，于是用户怎么输都只能整 GiB —— 这正是他 2026-11-20 报的
/// 「无法设置小数」。分区本来就按扇区算，小数只是换算时多一次四舍五入。
///
/// ⚠️ 这条**只在本层成立**：`SdLayout.Create` 里上游那条 4～24 GiB 校验也已同步放宽 / 改成 double。
/// </summary>
internal static class SdDragPlan
{
    /// <summary>emuMMC 下界（GiB）。</summary>
    internal const double MinEmuGiB = 5d;

    /// <summary>主数据分区下界（GiB）：必须**大于** 2 GiB，所以界面上显示 2.00、输入也不许更小。</summary>
    internal const double MinMainGiB = 2d;

    /// <summary>容量量化步长（GiB）：0.01 GiB = 10.24 MiB，比一个像素细得多，也足够对齐。</summary>
    internal const double StepGiB = 0.01d;

    private const long AlignmentSectors = 32768L;

    /// <summary>主分区至少要留下的扇区数：源项目要求 <c>num5 &gt; 4194304</c>（= 2 GiB），所以下界取 4194305。</summary>
    private const long MinMainSectors = 4194305L;

    private const long EmuOverheadMiB = 32L;

    private const long SectorsPerMiB = 2048L;

    internal static long DiskSectors(long diskBytes) => diskBytes / 512;

    /// <summary>
    /// 把任意容量夹到量化步长上（四舍五入到 0.01 GiB），并**去掉浮点噪声**。
    ///
    /// ⚠️ 最后那次 Math.Round(…, 2) 不是多余的：`Math.Round(v / 0.01) * 0.01` 会留下
    /// 13.950000000000001 这种尾巴 —— 界面上看不出来（格式化后是 13.95），
    /// 但 `== 13.95` 判不成立，回归里当场红两条。数字要**能比较**，不能只是「看着像」。
    /// </summary>
    internal static double Quantize(double gib)
        => Math.Round(Math.Round(gib / StepGiB, MidpointRounding.AwayFromZero) * StepGiB, 2,
            MidpointRounding.AwayFromZero);

    /// <summary>
    /// 这张卡最多能分出多大的 emuMMC（GiB）—— **唯一的约束是给主分区留下 &gt; 2 GiB**。
    /// 返回 0 = 卡太小，做不了。
    /// </summary>
    internal static double MaxAllowedGiB(long diskBytes)
    {
        if (diskBytes < 536870912 || diskBytes % 512 != 0)
        {
            return 0d;
        }

        var aligned = DiskSectors(diskBytes) / AlignmentSectors * AlignmentSectors;
        var maxEmuSectors = aligned - AlignmentSectors - MinMainSectors;
        if (maxEmuSectors <= 0)
        {
            return 0d;
        }

        // 不再夹到 24：emuMMC 可以比 Switch 的 eMMC 还大（多出来的容量 hekate 用不上，但不算非法）。
        var gib = (maxEmuSectors / (double)SectorsPerMiB - EmuOverheadMiB) / 1024d;

        // 向下取整到步长：上限本身也必须是「能设出来的值」，否则输入框会把 61.958 显示成 61.96、
        // 而实际按 61.95 算 —— 又回到「框里和结果不一致」。Quantize 顺带把浮点尾巴擦掉。
        return Quantize(Math.Floor(gib / StepGiB) * StepGiB);
    }

    /// <summary>分隔条位置（0～1，按整盘扇区算）→ emuMMC 容量（GiB，已夹到合法区间并量化）。</summary>
    internal static double GiBFromFraction(long diskBytes, double fraction)
    {
        var max = MaxAllowedGiB(diskBytes);
        if (max < MinEmuGiB)
        {
            return 0d;
        }

        var sectors = DiskSectors(diskBytes);
        var aligned = sectors / AlignmentSectors * AlignmentSectors;
        var boundary = (long)Math.Round(Math.Clamp(fraction, 0d, 1d) * sectors);
        var emuSectors = aligned - boundary;
        var gib = (emuSectors / (double)SectorsPerMiB - EmuOverheadMiB) / 1024d;

        return ClampGiB(diskBytes, gib);
    }

    /// <summary>emuMMC 容量（GiB）→ 分隔条位置（0～1）。与 <see cref="GiBFromFraction"/> 互为逆运算。</summary>
    internal static double FractionForGiB(long diskBytes, double gib)
    {
        var sectors = DiskSectors(diskBytes);
        if (sectors <= 0)
        {
            return 1d;
        }

        var aligned = sectors / AlignmentSectors * AlignmentSectors;
        var emuSectors = (Math.Max(gib, 0d) * 1024d + EmuOverheadMiB) * SectorsPerMiB;
        return Math.Clamp((aligned - emuSectors) / sectors, 0d, 1d);
    }

    /// <summary>夹到这张卡真正能用的区间并量化；卡太小返回 0。</summary>
    internal static double ClampGiB(long diskBytes, double gib)
    {
        var max = MaxAllowedGiB(diskBytes);
        if (max < MinEmuGiB)
        {
            return 0d;
        }

        var quantized = Quantize(gib);
        return Math.Min(Math.Max(quantized, MinEmuGiB), max);
    }

    /// <summary>
    /// **手动输入主数据分区容量**（GiB）→ emuMMC 容量（GiB）。
    ///
    /// 用户在「主数据分区」那一栏里敲数字时走这条：主分区要 X GiB，其余留给 emuMMC。
    /// 与 <see cref="MainGiBFor"/> 互为逆运算（回归里往返核对）。
    /// 结果同样夹到 [MinEmuGiB, 本卡上限]，所以「输入 0.5」不会得到负的 emuMMC，
    /// 而是把主分区顶到它自己能达到的最小值 —— 界面随后会把真实值回填到输入框里。
    /// </summary>
    internal static double GiBForMainGiB(long diskBytes, double mainGiB)
    {
        var max = MaxAllowedGiB(diskBytes);
        if (max < MinEmuGiB)
        {
            return 0d;
        }

        var sectors = DiskSectors(diskBytes);
        var aligned = sectors / AlignmentSectors * AlignmentSectors;

        var mainSectors = (long)Math.Round(Math.Max(mainGiB, 0d) * 1024d * SectorsPerMiB,
            MidpointRounding.AwayFromZero);
        var emuSectors = aligned - AlignmentSectors - mainSectors;
        var gib = (emuSectors / (double)SectorsPerMiB - EmuOverheadMiB) / 1024d;

        return ClampGiB(diskBytes, gib);
    }

    /// <summary>emuMMC 容量（GiB）→ 主数据分区实际大小（GiB）。与 <see cref="GiBForMainGiB"/> 对称，用于回填输入框。</summary>
    internal static double MainGiBFor(long diskBytes, double emuGiB)
    {
        var sectors = DiskSectors(diskBytes);
        if (sectors <= 0)
        {
            return 0d;
        }

        var aligned = sectors / AlignmentSectors * AlignmentSectors;
        var emuSectors = (Math.Max(emuGiB, 0d) * 1024d + EmuOverheadMiB) * SectorsPerMiB;
        var mainSectors = aligned - emuSectors - AlignmentSectors;

        return Math.Max(0d, mainSectors / (double)SectorsPerMiB / 1024d);
    }
}
