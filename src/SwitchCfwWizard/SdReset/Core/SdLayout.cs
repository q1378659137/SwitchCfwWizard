// 移植自 yijianhuanbao（源文件 SdLayout.cs，sha256 c0f62273fbe9…）。
// 逻辑与原版逐行一致，**唯一例外**：缩小容量 emuMMC 的上限（原版 24 GiB）已放开
// （用户 2026-11-20 要求：emuMMC 不设上限，只保留「主数据分区 > 2 GiB」这一条约束）。
// 改动请同时对照源项目。
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SwitchCfwWizard.SdReset;

public sealed record SdLayout(long DiskBytes, SdFormatOptions Options, SdPartition[] Partitions)
{
	public SdPartition Main => Partitions[0];

	public const long MiB = 1048576L;

	public const long GiB = 1073741824L;

	public const long AlignmentSectors = 32768L;

	public const byte HiddenEmuPartitionType = 240;

	/// <summary>
	/// 从**现有**分区的 MBR 还原一份布局（不重新计算任何东西）。
	///
	/// 用途（用户 2026-11-21 的两个新选项）：「只清空 emuMMC 分区」「只创建 emuMMC 所需文件」
	/// 都要求**不动分区表**，所以不能走 <see cref="Create"/>（那是照容量重算一套），
	/// 而要照着卡上已有的分区表读回来。
	///
	/// ⚠️ 只认 4 个主分区表项；空表项（type=0 或长度 0）跳过。第一个分区视为主数据分区，
	/// 其余视为 emuMMC —— 这与本工具自己写出来的布局一致，也是 hekate 的约定。
	/// </summary>
	public static SdLayout FromExistingMbr(long diskBytes, ReadOnlySpan<byte> mbr)
	{
		if (mbr.Length < 512 || mbr[510] != 85 || mbr[511] != 170)
		{
			throw new InvalidDataException("这张卡上没有可识别的分区表（引导扇区签名不对）。");
		}

		List<SdPartition> parts = new List<SdPartition>(4);
		for (int i = 0; i < 4; i++)
		{
			ReadOnlySpan<byte> entry = mbr.Slice(446 + i * 16, 16);
			byte type = entry[4];
			uint start = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8));
			uint count = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12));
			if (type == 0 || count == 0)
			{
				continue;
			}

			parts.Add(new SdPartition(parts.Count + 1, type, start, count));
		}

		if (parts.Count == 0)
		{
			throw new InvalidDataException("这张卡上没有可用分区。");
		}

		SdFileSystem fileSystem = parts[0].Type == 7 ? SdFileSystem.ExFat : SdFileSystem.Fat32;
		SdFormatOptions options = new SdFormatOptions(fileSystem, parts.Count - 1, 0d, SdFullEmuSize.None);
		return new SdLayout(diskBytes, options, parts.ToArray());
	}

	public static SdLayout Create(long diskBytes, SdFormatOptions options)
	{		ArgumentNullException.ThrowIfNull(options, "options");
		if (!Enum.IsDefined(options.FileSystem) || !Enum.IsDefined(options.FullSize))
		{
			throw new InvalidDataException("不支持的文件系统或 emuMMC 容量。");
		}
		if (diskBytes < 536870912 || diskBytes % 512 != 0L || diskBytes / 512 > uint.MaxValue)
		{
			throw new InvalidDataException("此功能支持 512 MiB 至小于 2 TiB、512 字节逻辑扇区的 TF 卡。");
		}
		int emuCount = options.EmuCount;
		if ((emuCount < 0 || emuCount > 2) ? true : false)
		{
			throw new InvalidDataException("按 hekate 分区管理器的布局，emuMMC 分区数量支持 1 个或 2 个。");
		}
		bool flag = options.EmuCount > 0 && options.FullSize == SdFullEmuSize.None;
		if (flag)
		{
			// ⚠️ 上游这里是 `int emuCount = options.EmuSizeGiB;` 再判 `< 4 || > 24`。
			//    上限已按用户要求放开（2026-11-20）；类型也从 int 改成 double —— 容量可以设小数
			//    （用户 2026-11-20 报「无法设置小数」）。分区本来就按扇区算，小数 GiB 无非是
			//    换算成扇区时四舍五入，比整数 GiB 更细而已。
			double emuGiB = options.EmuSizeGiB;
			flag = emuGiB < 4d;
		}
		if (flag)
		{
			throw new InvalidDataException("缩小容量的 emuMMC 至少 4 GiB（可带小数）；上限由卡容量与「主数据分区 > 2 GiB」决定。");
		}
		long num = diskBytes / 512;
		long num2 = num / 32768 * 32768;
		long num3 = options.FullSize switch
		{
			SdFullEmuSize.Original32Gb => 29856L, 
			SdFullEmuSize.Oled64Gb => 59680L, 
			// 小数 GiB → 扇区：先按 MiB 取整（0.01 GiB = 10.24 MiB，仍然落得到秒级的扇区）。
			_ => (long)Math.Round(options.EmuSizeGiB * 1024d + 32d, MidpointRounding.AwayFromZero), 
		} * 2048;
		long num4 = ((options.EmuCount == 0) ? num : (num2 - num3 * options.EmuCount));
		long num5 = num4 - 32768;
		if (num5 <= 0 || (options.EmuCount > 0 && num5 <= 4194304))
		{
			throw new InvalidDataException("TF 卡容量不足：emuMMC 分区之外，主数据分区需保留超过 2 GiB。");
		}
		emuCount = 1;
		List<SdPartition> list = new List<SdPartition>(emuCount);
		CollectionsMarshal.SetCount(list, emuCount);
		Span<SdPartition> span = CollectionsMarshal.AsSpan(list);
		int index = 0;
		span[index] = new SdPartition(1, (byte)((options.FileSystem == SdFileSystem.Fat32) ? 12 : 7), 32768L, num5);
		List<SdPartition> list2 = list;
		for (int i = 0; i < options.EmuCount; i++)
		{
			long sectorCount = num3 - ((i == options.EmuCount - 1) ? 2048 : 0);
			list2.Add(new SdPartition(i + 2, 240, num4 + i * num3, sectorCount));
		}
		return new SdLayout(diskBytes, options, list2.ToArray());
	}

	public byte[] CreateMbr(uint signature)
	{
		byte[] array = new byte[512];
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(440), signature);
		SdPartition[] partitions = Partitions;
		foreach (SdPartition sdPartition in partitions)
		{
			Span<byte> span = array.AsSpan(446 + (sdPartition.Index - 1) * 16, 16);
			span[1] = (span[5] = 254);
			span[2] = (span[3] = (span[6] = (span[7] = byte.MaxValue)));
			span[4] = sdPartition.Type;
			BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, span.Length - 8), checked((uint)sdPartition.StartSector));
			BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, span.Length - 12), checked((uint)sdPartition.SectorCount));
		}
		array[510] = 85;
		array[511] = 170;
		return array;
	}
}
