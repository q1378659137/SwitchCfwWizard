// 移植自 yijianhuanbao（源文件 SdResetEngine.cs，sha256 77475e3e0738…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace SwitchCfwWizard.SdReset;

internal static class SdResetEngine
{
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int DiskIo(int write, ulong sector, uint count, nint data);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int Format(uint sectors, int exfat, DiskIo callback);

	private sealed class PreparedDisk(SafeFileHandle disk) : IDisposable
	{
		internal SafeFileHandle Disk { get; } = disk;

		internal List<SafeFileHandle> Locks { get; } = new List<SafeFileHandle>();

		public void Dispose()
		{
			foreach (SafeFileHandle @lock in Locks)
			{
				@lock.Dispose();
			}
			Disk.Dispose();
		}
	}

	private static PreparedDisk Prepare(SdResetRequest request, Action<SdResetStatus> report)
	{
		return SdDeviceAccess.WaitUntilReady(() =>
		{
			report(new SdResetStatus("检查设备身份与分区信息"));
			List<SdVolume> list = SdDisks.EnumerateVolumes();
			PreparedDisk preparedDisk = new PreparedDisk(SdDisks.Open(request.Device.DevicePath, 3221225472u));
			try
			{
				request.Device.EnsureSame(SdDisks.Read(preparedDisk.Disk, request.Device.Number, list, SdDisks.ProtectedDisks(list)));
				report(new SdResetStatus("锁定 TF 卡，请关闭正在访问此卡的其他程序"));
				int nativeErrorCode = default;
				foreach (SdVolume item in list.Where((SdVolume v) => v.Disks.Contains(request.Device.Number)))
				{
					SafeFileHandle safeFileHandle = SdDisks.Open(item.Name.TrimEnd('\\'), 3221225472u);
					preparedDisk.Locks.Add(safeFileHandle);
					try
					{
						SdDisks.Control(safeFileHandle, 589848u);
						SdDisks.Control(safeFileHandle, 589856u);
					}
					catch (Win32Exception ex) when (((Func<bool>)delegate
					{
						// Could not convert BlockContainer to single expression
						nativeErrorCode = ex.NativeErrorCode;
						return (nativeErrorCode == 5 || (uint)(nativeErrorCode - 32) <= 1u) ? true : false;
					}).Invoke())
					{
						throw new IOException("TF 卡正在被其他程序使用。请关闭文件窗口、同步软件等后重试。", ex);
					}
				}
				List<SdVolume> list2 = SdDisks.EnumerateVolumes();
				HashSet<string> locked = (from v in list
					where v.Disks.Contains(request.Device.Number)
					select v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
				if (list2.Any((SdVolume v) => v.Disks.Contains(request.Device.Number) && !locked.Contains(v.Name)))
				{
					throw new Win32Exception(21, "TF 卡卷正在重新连接，等待卷信息稳定。");
				}
				request.Device.EnsureSame(SdDisks.Read(preparedDisk.Disk, request.Device.Number, list2, SdDisks.ProtectedDisks(list2)));
				SdDisks.Control(preparedDisk.Disk, 458788u);
				return preparedDisk;
			}
			catch
			{
				preparedDisk.Dispose();
				throw;
			}
		}, (int attempt) =>
		{
			report(new SdResetStatus($"等待 TF 卡设备就绪，正在重新检查（{attempt}/4）"));
		});
	}

	/// <summary>
	/// 「不动分区表」的三种手术（用户 2026-11-21 的三个新选项）：
	///   · <see cref="SdResetAction.FormatMain"/>：就地格式化主数据分区（emuMMC 不碰）；
	///   · <see cref="SdResetAction.WipeEmuPartition"/>：只把现有 emuMMC 分区前 16 MiB 写零；
	///   · <see cref="SdResetAction.CreateEmuFiles"/>：只把组件包写到卡上，让下次开机由它创建 emuMMC。
	///
	/// 三条都**先读卡上现有的 MBR**（<see cref="SdLayout.FromExistingMbr"/>）再动手 ——
	/// 不重算布局，也就不会顺手把用户的现有分区改掉。失败时的报告带上「是否已经动过盘」，
	/// 让用户知道该不该重新执行完整流程。
	///
	/// ⚠️ 需要挂载主域的步骤一律放在 <c>using (PreparedDisk …)</c> **之外**：
	/// <see cref="Prepare"/> 会把卷锁住并卸载，锁还握着的时候去等盘符只会等到超时。
	/// </summary>
	private static void RunSurgery(SdResetRequest request, Action<SdResetStatus> report)
	{
		string stage = "读取现有分区表";
		bool touchedDisk = false;
		SdLayout existing;
		byte[] mbr;
		string? root = null;

		try
		{
			using (PreparedDisk prepared = Prepare(request, report))
			{
				SafeFileHandle disk = prepared.Disk;

				// ⚠️ 必须**合成** MBR：ReadLayout 返回的是 DRIVE_LAYOUT_INFORMATION_EX，
				//    直接当 MBR 用 → 签名检查必然失败（「这张卡上没有可识别的分区表」）。
				mbr = SdDisks.MbrFromLayout(SdDisks.ReadLayout(disk));
				existing = SdLayout.FromExistingMbr(request.Device.Capacity, mbr);

				// ① 只格式化主数据分区：不需要 emuMMC，也不需要挂载。
				if (request.Action == SdResetAction.FormatMain)
				{
					stage = "格式化主数据分区";
					report(new SdResetStatus(stage + "（分区表保持不变）", Finished: false, null, Written: true));

					// ⚠️ 这里必须读**磁盘上真正的扇区 0**来当"不许变"的基准，不能用上面那份合成 MBR：
					// 合成只填了磁盘签名（440）与分区表项（450+），前 440 字节是 0；
					// 而格式化组件会把**它读到的原始扇区 0** 原样写回 —— 两者必然不等，
					// 于是每次都被判成「格式化程序试图更改分区表」（2026-11-21 用户实测报的就是这个）。
					byte[] sector0 = new byte[512];
					if (RandomAccess.Read(disk, sector0, 0L) != sector0.Length)
					{
						throw new IOException("读取 TF 卡的引导扇区失败，已停止。");
					}

					FormatMainPartition(disk, existing, sector0, request.Options.FileSystem, step =>
					{
						stage = step;
						report(new SdResetStatus(step, Finished: false, null, Written: true));
					},
					// 「已经改动过卡」只能在**真的写了第一笔之后**才置位：
					// 格式化组件有可能在第一次写之前就被校验拦下，那时卡上一个字节都没动，
					// 再报「已经改动过」会让用户以为数据已经坏了。
					markTouched: () => touchedDisk = true);
				}
				else
				{
					if (existing.Partitions.Length < 2)
					{
						throw new IOException("这张卡上没有 emuMMC 分区（只有一个主数据分区）。请改用「格式化并重建 emuMMC」。");
					}

					SdPartition emu = existing.Partitions[1];
					if (emu.Type != SdLayout.HiddenEmuPartitionType)
					{
						throw new IOException($"第二个分区不是 emuMMC 隐藏分区（类型 {emu.Type}，应为 {SdLayout.HiddenEmuPartitionType}），已停止。");
					}

					if (request.Action == SdResetAction.WipeEmuPartition)
					{
						stage = "清空 emuMMC 分区的前 16 MiB";
						report(new SdResetStatus(stage, Finished: false, null, Written: true));
						touchedDisk = true;

						byte[] zeros = new byte[1048576];
						long total = Math.Min(16777216L, emu.Length);
						for (long at = 0L; at < total; at += zeros.Length)
						{
							RandomAccess.Write(disk, zeros, emu.Offset + at);
						}
						RandomAccess.FlushToDisk(disk);

						report(new SdResetStatus("emuMMC 分区已清空（分区表未改动）", Finished: true, null, Written: true));
						return;
					}
				}
			}

			// 到这里锁已释放，可以安全地等 Windows 把主数据分区挂回来。
			stage = "挂载主数据分区";
			report(new SdResetStatus(stage, Finished: false, null, touchedDisk));
			root = SdDisks.MountMain(request.Device, existing);

			if (request.Action == SdResetAction.FormatMain)
			{
				report(new SdResetStatus("主数据分区已格式化（分区表与 emuMMC 未改动）", Finished: true, null, Written: true, root));
				return;
			}

			stage = "读取组件包";
			if (string.IsNullOrWhiteSpace(request.AutoEmuZipPath))
			{
				// 程序里不再内嵌这份组件包（2026-11-21 用户要求删掉那 2.7 MB），
				// 所以拿不到 zip 路径就没有任何可用的来源 —— 必须说清楚，而不是让子进程去猜。
				throw new IOException("没有可用的 emuMMC 组件包（这次既没下载到，也没有可复用的本地文件）。");
			}

			Dictionary<string, byte[]> assets = AutoPayloadAssets.LoadFromFile(request.AutoEmuZipPath);

			if (!assets.TryGetValue("payload.bin", out var assetPayload) || assetPayload.Length == 0)
			{
				throw new IOException("组件包里没有 payload.bin，无法创建 emuMMC 所需文件。");
			}

			// 卡上原有的 payload.bin（hekate 的）才是任务里要记的**原版** ——
			// 开机跑完自动创建后要把它还原回去。卡上没有就记组件包这份（还原等于空操作）。
			stage = "检查卡上现有的 payload.bin";
			var cardPayloadPath = PathPolicy.SafePath(root, "payload.bin");
			byte[]? cardPayload = null;

			if (File.Exists(cardPayloadPath))
			{
				var length = new FileInfo(cardPayloadPath).Length;
				if (length < 288 || length > 196608)
				{
					throw new IOException(
						$"卡根目录的 payload.bin 大小异常（{length} 字节，正常在 288 字节～192 KiB 之间）。"
						+ "请先确认它是不是有效的启动文件，或把它移走后再试。");
				}

				cardPayload = File.ReadAllBytes(cardPayloadPath);
			}

			stage = "准备自动创建任务";
			EmuTask task = EmuTask.Create(existing, mbr, cardPayload ?? assetPayload);

			// 上一次没做完（卡上还留着 ready.flag / task.bin）时要先收拾干净，
			// 否则 Deploy 会以「此卡已有未完成的重建任务」直接拒绝。顺序照搬主流程：
			// 能读的任务就继续（Resume），读不了的先 Restore 再重新部署。
			if (EmuTask.HasPending(root))
			{
				stage = "清理上一次未完成的任务";
				try
				{
					EmuTask.Read(root);
				}
				catch (Exception ex) when (ex is IOException or InvalidDataException)
				{
					EmuTaskFiles.Restore(root, static () => { });
				}
			}

			stage = "写入组件包文件";
			touchedDisk = true;

			if (EmuTask.HasPending(root))
			{
				EmuTaskFiles.Resume(root, assets, static () => { });
			}
			else
			{
				// 卡上那份 payload.bin 可能不存在（空卡）—— 所以只在**它确实存在**时才做
				// 「已有 payload 必须与任务记录一致」的校验，否则 Deploy 会去读一个不存在的文件。
				EmuTaskFiles.Deploy(root, task, assets, static () => { }, verifyExistingPayload: cardPayload is not null);
			}

			EmuTask.Read(root).ValidateLayout(mbr, request.Device.Capacity);

			report(new SdResetStatus("已创建 emuMMC 所需文件（分区表未改动）", Finished: true, null, Written: true, root));
		}
		catch (Exception ex)
		{
			string text = "失败步骤：" + stage + "\n" + ServerAddressPrivacy.Hide(ex.Message);
			for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
			{
				if (inner is Win32Exception win32)
				{
					text += $"\n系统错误码：{win32.NativeErrorCode}";
					break;
				}
			}

			report(new SdResetStatus(
				"操作未完成",
				Finished: true,
				text + (touchedDisk ? "\n已经改动过 TF 卡上的内容。" : "\n尚未改动 TF 卡。"),
				Written: touchedDisk));
		}
	}

	/// <summary>
	/// 就地格式化**主数据分区**（用户 2026-11-21 追加的「仅格式化主数据分区」）。
	///
	/// 复用发布包里那个原生 fatfs 组件（<c>ns_format</c>），但把它的写入范围**卡死在主分区内**：
	/// 任何越界写（尤其是往 emuMMC 里写）立刻返回失败、整次格式化中止；
	/// 扇区 0 只允许**原样写回**卡上原来的那 512 字节 —— 那把分区表就不可能被这个操作改掉。
	///
	/// <paramref name="originalSector0"/> 必须是**从磁盘读出来的**原始扇区 0，
	/// 不能是 <see cref="SdDisks.MbrFromLayout"/> 合成出来的那份（合成件只有磁盘签名与分区表项，
	/// 前 440 字节是 0，和真实扇区永远不等 —— 拿它当基准会把每一次格式化都判成「试图更改分区表」）。
	/// </summary>
	private static void FormatMainPartition(
		SafeFileHandle disk,
		SdLayout layout,
		byte[] originalSector0,
		SdFileSystem fileSystem,
		Action<string> stage,
		Action markTouched)
	{
		bool exfat = fileSystem == SdFileSystem.ExFat;
		SdPartition main = layout.Main;

		stage("加载格式化组件");
		nint module = NativeLibrary.Load(NativeFormatter.EnsurePath());
		try
		{
			Format format = Marshal.GetDelegateForFunctionPointer<Format>(NativeLibrary.GetExport(module, "ns_format"));

			stage(exfat ? "正在把主数据分区格式化为 exFAT" : "正在把主数据分区格式化为 FAT32");

			Exception? ioError = null;
			DiskIo io = (int operation, ulong sector, uint count, nint data) =>
			{
				try
				{
					if (operation == 2)
					{
						RandomAccess.FlushToDisk(disk);
						return 1;
					}

					long at = (long)sector * 512;
					long size = unchecked((long)count) * 512L;

					if (size <= 0 || size > 4194304)
					{
						throw new IOException("格式化写入范围异常，已停止。");
					}

					byte[] buffer = new byte[(int)size];
					switch (operation)
					{
					case 0:
						// 读：先校验范围（不比对内容），越界就不去读
						EnsureFormatWriteAllowed(at, size, main, null, originalSector0);

						if (RandomAccess.Read(disk, buffer, at) != buffer.Length)
						{
							throw new IOException("读取 TF 卡时返回的数据不完整。");
						}

						Marshal.Copy(buffer, 0, data, buffer.Length);
						break;
					case 1:
						Marshal.Copy(data, buffer, 0, buffer.Length);
						EnsureFormatWriteAllowed(at, size, main, buffer, originalSector0);
						markTouched();
						RandomAccess.Write(disk, buffer, at);
						break;
					default:
						return 0;
					}

					return 1;
				}
				catch (Exception ex)
				{
					ioError ??= ex;
					return 0;
				}
			};

			int code = format((uint)unchecked(layout.DiskBytes / 512), exfat ? 1 : 0, io);
			GC.KeepAlive(io);

			if (ioError is not null)
			{
				throw new IOException("读写 TF 卡失败：" + ioError.Message, ioError);
			}

			if (code != 0)
			{
				throw new IOException($"格式化未完成，文件系统错误码：{code}。请检查 TF 卡状态后重试。");
			}

			stage("校验文件系统与分区表");
			byte[] boot = new byte[512];
			bool bootOk = RandomAccess.Read(disk, boot, main.Offset) == 512
						  && boot[510] == 85 && boot[511] == 170
						  && (exfat
							  ? ((ReadOnlySpan<byte>)boot.AsSpan(3, 8)).SequenceEqual("EXFAT   "u8)
							  : ((ReadOnlySpan<byte>)boot.AsSpan(82, 8)).SequenceEqual("FAT32   "u8));

			if (!bootOk)
			{
				throw new IOException("主数据分区文件系统校验失败。");
			}

			byte[] after = new byte[512];
			if (RandomAccess.Read(disk, after, 0L) != 512
				|| !((ReadOnlySpan<byte>)after).SequenceEqual((ReadOnlySpan<byte>)originalSector0))
			{
				throw new IOException("格式化后分区表发生了变化，已停止。");
			}

			RandomAccess.FlushToDisk(disk);

			stage("刷新 Windows 分区信息");
			SdDisks.Control(disk, 459072u);
		}
		finally
		{
			NativeLibrary.Free(module);
		}
	}

	/// <summary>
	/// 「格式化主数据分区」时每一次读/写的范围与内容校验（**纯函数**，回归直接调它）。
	///
	/// 两条规则：
	/// <list type="bullet">
	///   <item>除了扇区 0，任何读/写都必须落在主数据分区之内 —— 越界（尤其是写进 emuMMC）立刻中止；</item>
	///   <item>扇区 0 允许读；写的话必须与**卡上原来那 512 字节**逐字节相同。</item>
	/// </list>
	///
	/// ⚠️ 第二条的基准只能是**从磁盘读出来的原始扇区 0**，不能是
	/// <see cref="SdDisks.MbrFromLayout"/> 合成出来的那份：合成件只填了磁盘签名（440）与分区表项（450+），
	/// 前 440 字节是 0，而格式化组件会把读到的真实扇区原样写回 —— 拿合成件当基准，
	/// 每次格式化都会被判成「试图更改分区表」（2026-11-21 实测报的就是这个）。
	///
	/// <paramref name="written"/> 为 <c>null</c> 表示这是一次**读**：只校验范围，不比对内容。
	/// </summary>
	internal static void EnsureFormatWriteAllowed(
		long at,
		long size,
		SdPartition main,
		byte[]? written,
		byte[] originalSector0)
	{
		bool isMbrSector = at == 0L && size == 512L;

		if (!isMbrSector && (at < main.Offset || at + size > main.Offset + main.Length))
		{
			throw new IOException("格式化程序试图写到主数据分区之外，已停止（emuMMC 与分区表都不会被改动）。");
		}

		if (isMbrSector && written is not null
			&& !((ReadOnlySpan<byte>)written).SequenceEqual((ReadOnlySpan<byte>)originalSector0))
		{
			throw new IOException("格式化程序试图更改分区表，已停止。");
		}
	}

	internal static void Run(SdResetRequest request, Action<SdResetStatus> report)
	{
		request.Device.EnsureResetAllowed();

		// 用户 2026-11-21 的三个新选项：都不动分区表，走单独一条更短的路。
		if (request.Action is SdResetAction.FormatMain
			or SdResetAction.WipeEmuPartition
			or SdResetAction.CreateEmuFiles)
		{
			RunSurgery(request, report);
			return;
		}

		SdLayout layout = SdLayout.Create(request.Device.Capacity, request.Options);
		if (request.MountOnly)
		{
			try
			{
				report(new SdResetStatus("正在核对原 TF 卡身份与已写入的分区", Finished: false, null, Written: true));
				using (SafeFileHandle disk = SdDisks.Open(request.Device.DevicePath, 0u))
				{
					List<SdVolume> volumes = SdDisks.EnumerateVolumes();
					SdDevice sdDevice = SdDisks.Read(disk, request.Device.Number, volumes, SdDisks.ProtectedDisks(volumes));
					if (sdDevice.Identity != request.Device.Identity)
					{
						throw new IOException("TF 卡设备已更换。");
					}
					sdDevice.EnsureResetAllowed();
					byte[] array = SdDisks.ReadLayout(disk);
					if (request.ExpectedSignature == 0 || BinaryPrimitives.ReadInt32LittleEndian(array) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(8)) != request.ExpectedSignature)
					{
						throw new IOException("TF 卡分区身份已改变，禁止沿用上次的重建任务。");
					}
				}
				report(new SdResetStatus("正在检查主数据分区与 emuMMC 隐藏状态", Finished: false, null, Written: true));
				report(new SdResetStatus("TF 卡盘符检查完成", Finished: true, null, Written: true, SdDisks.MountMain(request.Device, layout), request.ExpectedSignature));
				return;
			}
			catch (Exception ex)
			{
				report(new SdResetStatus("分区已写入，本次盘符检查未通过：" + ServerAddressPrivacy.Hide(ex.Message), Finished: true, null, Written: true, null, request.ExpectedSignature));
				return;
			}
		}
		bool flag = false;
		uint num = 0u;
		nint num2 = IntPtr.Zero;
		string stage = "准备格式化组件";
		try
		{
			num2 = NativeLibrary.Load(NativeFormatter.EnsurePath());
			Format delegateForFunctionPointer = Marshal.GetDelegateForFunctionPointer<Format>(NativeLibrary.GetExport(num2, "ns_format"));
			using PreparedDisk preparedDisk = Prepare(request, Progress);
			SafeFileHandle disk2 = preparedDisk.Disk;
			flag = true;
			SdDiskMetadata.Clear(layout.DiskBytes, (long at, byte[] data) =>
			{
				RandomAccess.Write(disk2, data, at);
			}, (string step) =>
			{
				Progress(new SdResetStatus(step, Finished: false, null, Written: true));
			});
			byte[] array2 = new byte[1048576];
			Progress(new SdResetStatus((request.Options.EmuCount == 0) ? "创建主数据分区" : "创建主数据分区和隐藏的 emuMMC 原始分区", Finished: false, null, Written: true));
			num = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4)) | 1;
			byte[] array3 = new byte[24];
			BinaryPrimitives.WriteUInt32LittleEndian(array3.AsSpan(4), num);
			SdDisks.Control(disk2, 507992u, array3);
			byte[] array4 = new byte[624];
			BinaryPrimitives.WriteInt32LittleEndian(array4.AsSpan(4), 4);
			BinaryPrimitives.WriteUInt32LittleEndian(array4.AsSpan(8), num);
			for (int num3 = 0; num3 < 4; num3++)
			{
				Span<byte> span = array4.AsSpan(48 + num3 * 144, 144);
				span[28] = 1;
				if (num3 < layout.Partitions.Length)
				{
					SdPartition sdPartition = layout.Partitions[num3];
					BinaryPrimitives.WriteInt64LittleEndian(span.Slice(8, span.Length - 8), sdPartition.Offset);
					BinaryPrimitives.WriteInt64LittleEndian(span.Slice(16, span.Length - 16), sdPartition.Length);
					BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, span.Length - 24), sdPartition.Index);
					span[32] = sdPartition.Type;
					span[34] = ((num3 == 0) ? ((byte)1) : ((byte)0));
					BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(36, span.Length - 36), checked((uint)sdPartition.StartSector));
				}
			}
			byte[] mbr = layout.CreateMbr(num);
			Progress(new SdResetStatus("写入新分区表", Finished: false, null, Written: true));
			RandomAccess.Write(disk2, mbr, 0L);
			if (request.Options.EmuCount > 0)
			{
				Progress(new SdResetStatus("初始化 emuMMC 分区保护区域", Finished: false, null, Written: true));
			}
			foreach (SdPartition item in layout.Partitions.Skip(1))
			{
				for (long num4 = 0L; num4 < 16777216; num4 += array2.Length)
				{
					RandomAccess.Write(disk2, array2, item.Offset + num4);
				}
			}
			RandomAccess.FlushToDisk(disk2);
			Progress(new SdResetStatus((request.Options.FileSystem == SdFileSystem.Fat32) ? "正在格式化主分区为 FAT32" : "正在格式化主分区为 exFAT", Finished: false, null, Written: true));
			Exception? ioError = null;
			byte[] array7;
			checked
			{
				DiskIo diskIo = (int operation, ulong sector, uint count, nint pointer) =>
				{
					try
					{
						if (operation == 2)
						{
							RandomAccess.FlushToDisk(disk2);
							return 1;
						}
						long num7 = (long)sector * 512;
						long num8 = unchecked((long)count) * 512L;
						if (unchecked(num8 <= 0 || num8 > 4194304 || num7 < 0 || num7 + num8 > layout.DiskBytes || ((num7 != 0L || num8 != 512) && (num7 < layout.Main.Offset || num7 + num8 > layout.Main.Offset + layout.Main.Length))))
						{
							throw new IOException("格式化写入范围超出主分区，已停止。");
						}
						byte[] array8 = new byte[(int)num8];
						switch (operation)
						{
						case 0:
							if (RandomAccess.Read(disk2, array8, num7) != array8.Length)
							{
								throw new IOException("读取 TF 卡时返回的数据不完整。");
							}
							Marshal.Copy(array8, 0, pointer, array8.Length);
							break;
						case 1:
							Marshal.Copy(pointer, array8, 0, array8.Length);
							if (num7 == 0L && !((ReadOnlySpan<byte>)array8.AsSpan()).SequenceEqual((ReadOnlySpan<byte>)mbr))
							{
								throw new IOException("格式化程序意外更改分区表，已停止。");
							}
							RandomAccess.Write(disk2, array8, num7);
							break;
						default:
							return 0;
						}
						return 1;
					}
					catch (Exception ex7)
					{
						if (ioError == null)
						{
							ioError = ex7;
						}
						return 0;
					}
				};
				int num5 = delegateForFunctionPointer((uint)unchecked(layout.DiskBytes / 512), (request.Options.FileSystem == SdFileSystem.ExFat) ? 1 : 0, diskIo);
				GC.KeepAlive(diskIo);
				if (ioError != null)
				{
					throw new IOException("读写 TF 卡失败：" + ioError.Message, ioError);
				}
				if (num5 != 0)
				{
					throw new IOException($"格式化未完成，文件系统错误码：{num5}。请检查 TF 卡状态后重新执行。");
				}
				Progress(new SdResetStatus("校验分区表与文件系统", Finished: false, null, Written: true));
				byte[] array5 = new byte[512];
				if (RandomAccess.Read(disk2, array5, 0L) != 512 || !((ReadOnlySpan<byte>)array5.AsSpan()).SequenceEqual((ReadOnlySpan<byte>)mbr))
				{
					throw new IOException("分区表写入校验失败。");
				}
				byte[] array6 = new byte[512];
				if (RandomAccess.Read(disk2, array6, layout.Main.Offset) != 512 || array6[510] != 85 || array6[511] != 170 || !((request.Options.FileSystem == SdFileSystem.ExFat) ? ((ReadOnlySpan<byte>)array6.AsSpan(3, 8)).SequenceEqual("EXFAT   "u8) : ((ReadOnlySpan<byte>)array6.AsSpan(82, 8)).SequenceEqual("FAT32   "u8)))
				{
					throw new IOException("主分区文件系统校验失败。");
				}
				RandomAccess.FlushToDisk(disk2);
				Progress(new SdResetStatus("刷新 Windows 分区信息", Finished: false, null, Written: true));
				SdDisks.Control(disk2, 507988u, array4);
				SdDisks.Control(disk2, 459072u);
				array7 = new byte[512];
				if (RandomAccess.Read(disk2, array7, 0L) != 512 || array7[510] != 85 || array7[511] != 170)
				{
					throw new IOException("刷新后的分区表校验失败。");
				}
			}
			for (int num6 = 0; num6 < 4; num6++)
			{
				if (array7[450 + num6 * 16] != mbr[450 + num6 * 16] || !((ReadOnlySpan<byte>)array7.AsSpan(454 + num6 * 16, 8)).SequenceEqual((ReadOnlySpan<byte>)mbr.AsSpan(454 + num6 * 16, 8)))
				{
					throw new IOException("刷新后的分区范围校验失败。");
				}
			}
		}
		catch (Exception ex2)
		{
			string text = "失败步骤：" + stage + "\n" + ServerAddressPrivacy.Hide(ex2.Message);
			for (Exception? ex3 = ex2; ex3 != null; ex3 = ex3.InnerException)
			{
				if (ex3 is Win32Exception ex4)
				{
					text += $"\n系统错误码：{ex4.NativeErrorCode}";
					break;
				}
			}
			report(new SdResetStatus("操作未完成", Finished: true, text + (flag ? "\n已开始重建分区，旧数据不会自动恢复。请重新执行完整操作。" : "\n尚未开始重建分区。"), flag));
			return;
		}
		finally
		{
			if (num2 != IntPtr.Zero)
			{
				NativeLibrary.Free(num2);
			}
		}
		report(new SdResetStatus("检查 emuMMC 隐藏状态并等待主数据分区挂载", Finished: false, null, Written: true));
		try
		{
			report(new SdResetStatus("TF 卡格式化与分区完成", Finished: true, null, flag, SdDisks.MountMain(request.Device, layout), num));
		}
		catch (Exception ex5)
		{
			string text2 = ServerAddressPrivacy.Hide(ex5.Message);
			if (ex5 is Win32Exception ex6)
			{
				text2 += $"\n系统错误码：{ex6.NativeErrorCode}";
			}
			report(new SdResetStatus("分区和文件系统已写入，但 Windows 盘符或隐藏检查未完成：" + text2, Finished: true, null, flag, null, num));
		}
		void Progress(SdResetStatus status)
		{
			stage = status.Stage;
			report(status);
		}
	}
}
