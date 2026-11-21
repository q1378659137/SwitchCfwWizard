// 移植自 yijianhuanbao（源文件 SdDisks.cs，sha256 adc0682c69c5…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SwitchCfwWizard.SdReset;

internal static class SdDisks
{
	internal const uint Geometry = 458912u;

	internal const uint Layout = 458832u;

	internal const uint StorageProperty = 2954240u;

	internal const uint ReadWrite = 3221225472u;

	internal static List<SdDevice> Scan()
	{
		if (!OperatingSystem.IsWindows())
		{
			return new List<SdDevice>();
		}
		char[] array = new char[65536];
		if (QueryDosDevice(null, array, array.Length) == 0)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
		List<SdVolume> volumes = EnumerateVolumes();
		HashSet<int> protectedDisks = ProtectedDisks(volumes);
		List<SdDevice> list = new List<SdDevice>();
		string[] array2 = new string(array).Split('\0', StringSplitOptions.RemoveEmptyEntries);
		foreach (string text in array2)
		{
			if (!text.StartsWith("PhysicalDrive", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string text2 = text;
			if (!int.TryParse(text2.Substring(13, text2.Length - 13), out var result))
			{
				continue;
			}
			try
			{
				using SafeFileHandle disk = Open("\\\\.\\" + text, 0u);
				SdDevice sdDevice = Read(disk, result, volumes, protectedDisks);
				int busType = sdDevice.BusType;
				if ((busType == 7 || (uint)(busType - 12) <= 1u) ? true : false)
				{
					list.Add(sdDevice);
				}
			}
			catch (Exception ex) when ((ex is IOException || ex is Win32Exception || ex is UnauthorizedAccessException) ? true : false)
			{
			}
		}
		return list.OrderBy((SdDevice d) => d.Number).ToList();
	}

	internal static SdDevice Read(SafeFileHandle disk, int number, List<SdVolume> volumes, HashSet<int> protectedDisks)
	{
		byte[] array = Control(disk, 458912u, null, 256);
		if (array.Length < 32)
		{
			throw new IOException("无法读取设备容量。");
		}
		long num = BinaryPrimitives.ReadInt64LittleEndian(array.AsSpan(24));
		int num2 = BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(20));
		byte[] descriptor = Control(disk, 2954240u, new byte[12], 16384);
		if (descriptor.Length < 36)
		{
			throw new IOException("无法读取设备型号。");
		}
		byte[] array2 = ReadLayout(disk);
		int num3 = ((array2.Length >= 8) ? BinaryPrimitives.ReadInt32LittleEndian(array2.AsSpan(4)) : 0);
		bool flag = ((num3 < 0 || num3 > 128) ? true : false);
		if (flag || array2.Length < 48 + num3 * 144)
		{
			throw new IOException("设备分区信息无效。");
		}
		string text = Field(12);
		string text2 = Field(16);
		string text3 = Field(24);
		int num4 = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(28));
		string text4 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{number}|{text}|{text2}|{text3}|{num4}|{num}|{num2}")));
		using (MemoryStream memoryStream = new MemoryStream())
		{
			memoryStream.Write(array2.AsSpan(0, 8));
			int num5 = BinaryPrimitives.ReadInt32LittleEndian(array2);
			switch (num5)
			{
			case 0:
				memoryStream.Write(array2.AsSpan(8, 4));
				break;
			case 1:
				memoryStream.Write(array2.AsSpan(8, 40));
				break;
			default:
				throw new IOException("无法识别设备分区类型。");
			case 2:
				break;
			}
			for (int i = 0; i < num3; i++)
			{
				ReadOnlySpan<byte> readOnlySpan = array2.AsSpan(48 + i * 144, 144);
				memoryStream.Write(readOnlySpan.Slice(0, 4));
				memoryStream.Write(readOnlySpan.Slice(8, 20));
				switch (num5)
				{
				case 1:
					memoryStream.Write(readOnlySpan.Slice(32, 112));
					break;
				case 0:
					memoryStream.Write(readOnlySpan.Slice(32, 3));
					memoryStream.Write(readOnlySpan.Slice(36, 4));
					break;
				}
			}
			string text5 = Convert.ToHexString(SHA256.HashData(memoryStream.ToArray()));
			bool flag2 = protectedDisks.Contains(number) || HasSystemPartitions(array2);
			bool flag3 = volumes.Any((SdVolume v) => v.Disks.Contains(number) && v.Disks.Length != 1);
			int number2 = number;
			string vendor = text;
			string model = text2;
			string serial = text3;
			int busType = num4;
			long capacity = num;
			int sectorSize = num2;
			string identity = text4;
			string layoutIdentity = text5;
			string[] mountPoints = volumes.Where((SdVolume v) => v.Disks.Contains(number)).SelectMany((SdVolume v) => v.Mounts).Distinct()
				.ToArray();
			bool flag4 = flag2 | flag3;
			string blockReason;
			if (flag2)
			{
				blockReason = "此设备包含 Windows、工具运行文件、缓存或分页文件，禁止整卡重置。";
			}
			else
			{
				blockReason = (flag3 ? "不支持跨磁盘卷。" : "");
			}
			return new SdDevice(number2, vendor, model, serial, busType, capacity, sectorSize, identity, layoutIdentity, mountPoints, flag4, blockReason);
		}
		string Field(int position)
		{
			int num6 = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(position));
			if (num6 < 36 || num6 >= descriptor.Length)
			{
				return "";
			}
			int num7 = Array.IndexOf(descriptor, (byte)0, num6);
			return Encoding.ASCII.GetString(descriptor, num6, ((num7 < 0) ? descriptor.Length : num7) - num6).Trim();
		}
	}

	private static bool HasSystemPartitions(byte[] layout)
	{
		int num = BinaryPrimitives.ReadInt32LittleEndian(layout);
		int num2 = BinaryPrimitives.ReadInt32LittleEndian(layout.AsSpan(4));
		for (int i = 0; i < num2; i++)
		{
			ReadOnlySpan<byte> readOnlySpan = layout.AsSpan(48 + i * 144, 144);
			bool flag = num == 0;
			if (flag)
			{
				byte b = readOnlySpan[32];
				bool flag2 = ((b == 39 || b == 66) ? true : false);
				flag = flag2;
			}
			if (flag)
			{
				return true;
			}
			if (num == 1)
			{
				Guid guid = new Guid(readOnlySpan.Slice(32, 16));
				if (guid == new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B") || guid == new Guid("E3C9E316-0B5C-4DB8-817D-F92DF00215AE") || guid == new Guid("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC"))
				{
					return true;
				}
			}
		}
		return false;
	}

	internal static void VerifyMainVolume(SdDevice expected, string root)
	{
		List<SdVolume> volumes = EnumerateVolumes();
		SdVolume sdVolume = ReadDriveVolume(root);
		if (sdVolume.Disks.Length != 1 || sdVolume.Disks[0] != expected.Number || sdVolume.Offset != 16777216)
		{
			throw new IOException("盘符已指向其他设备，请重新确认 TF 卡。");
		}
		using SafeFileHandle disk = Open(expected.DevicePath, 0u);
		SdDevice sdDevice = Read(disk, expected.Number, volumes, ProtectedDisks(volumes));
		if (sdDevice.Identity != expected.Identity)
		{
			throw new IOException("TF 卡设备已更换，请重新确认。");
		}
		sdDevice.EnsureResetAllowed();
	}

	internal static byte[] ReadTaskMbr(SdDevice expected, string root)
	{
		VerifyMainVolume(expected, root);
		using SafeFileHandle disk = Open(expected.DevicePath, 0u);
		return MbrFromLayout(ReadLayout(disk));
	}

	/// <summary>
	/// 把 <see cref="ReadLayout"/> 拿到的 <c>DRIVE_LAYOUT_INFORMATION_EX</c> **合成**成 512 字节 MBR。
	///
	/// ⚠️ 这里必须合成，不能把 <see cref="ReadLayout"/> 的返回值直接当 MBR 用 ——
	/// 那个结构里没有引导扇区，字节 510/511 也不是 <c>55 AA</c>。
	/// 「不动分区表」的三种手术就是拿它去喂 <see cref="SdLayout.FromExistingMbr"/> 的：
	/// 直接把结构体递进去，用户看到的是「这张卡上没有可识别的分区表（引导扇区签名不对）」，
	/// 而卡本身好好的（2026-11-21 用户报的就是这个）。
	/// </summary>
	internal static byte[] MbrFromLayout(byte[] layout)
	{
		if (BinaryPrimitives.ReadInt32LittleEndian(layout) != 0)
		{
			throw new IOException("自动创建要求 MBR 分区表。");
		}

		byte[] mbr = new byte[512];
		layout.AsSpan(8, 4).CopyTo(mbr.AsSpan(440));

		int count = BinaryPrimitives.ReadInt32LittleEndian(layout.AsSpan(4));
		if (count > 4)
		{
			throw new IOException("TF 卡包含额外分区，无法继续重建。");
		}

		for (int i = 0; i < count; i++)
		{
			ReadOnlySpan<byte> entry = layout.AsSpan(48 + i * 144, 144);
			long offset = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(8, entry.Length - 8));
			long length = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(16, entry.Length - 16));
			if (offset % 512 != 0L || length % 512 != 0L)
			{
				throw new IOException("TF 卡分区未按扇区对齐。");
			}

			mbr[450 + i * 16] = entry[32];
			checked
			{
				BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(unchecked(454 + i * 16)), (uint)unchecked(offset / 512));
				BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(unchecked(458 + i * 16)), (uint)unchecked(length / 512));
			}
		}

		mbr[510] = 85;
		mbr[511] = 170;
		return mbr;
	}

	internal static string MountMain(SdDevice expected, SdLayout layout)
	{
		HashSet<string> knownLetters = expected.MountPoints.Where(SdVolumeMounts.IsDriveLetter).ToHashSet(StringComparer.OrdinalIgnoreCase);
		string text = SdVolumeMounts.WaitForMain(expected.Number, layout, Observe, RemoveMount, ReadyMain);
		VerifyMainVolume(expected, text);
		return text;
		List<SdVolume> Observe()
		{
			List<SdVolume> list = EnumerateVolumes();
			knownLetters.UnionWith(list.Where((SdVolume v) => v.Disks.Contains(expected.Number)).SelectMany((SdVolume v) => v.Mounts).Where(SdVolumeMounts.IsDriveLetter));
			uint logicalDrives = GetLogicalDrives();
			if (logicalDrives == 0)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			int nativeErrorCode = default;
			for (int num = 0; num < 26; num++)
			{
				if ((logicalDrives & (uint)(1 << num)) != 0)
				{
					string root = (char)(65 + num) + ":\\";
					SdVolume? direct;
					try
					{
						direct = SdVolumeScope.ReadDrive(expected.Number, root, DeviceName(root.Substring(0, 2)), list, () => ReadDriveVolume(root));
					}
					catch (Win32Exception ex) when (((Func<bool>)delegate
					{
						// Could not convert BlockContainer to single expression
						nativeErrorCode = ex.NativeErrorCode;
						return ((nativeErrorCode == 1 || nativeErrorCode == 50) ? true : false) || SdDeviceAccess.IsUnavailable(ex.NativeErrorCode);
					}).Invoke())
					{
						if (knownLetters.Contains(root))
						{
							throw new Win32Exception(21, "TF 卡的旧盘符尚未完成刷新。");
						}
						continue;
					}
					if (!(direct == null))
					{
						if (direct.Disks.Contains(expected.Number))
						{
							knownLetters.Add(root);
						}
						for (int num2 = 0; num2 < list.Count; num2++)
						{
							list[num2] = list[num2] with
							{
								Mounts = list[num2].Mounts.Where((string m) => !m.Equals(root, StringComparison.OrdinalIgnoreCase)).ToArray()
							};
						}
						int num3 = list.FindIndex((SdVolume v) => v.Offset == direct.Offset && v.Length == direct.Length && v.Disks.SequenceEqual(direct.Disks));
						if (num3 < 0)
						{
							list.Add(direct);
						}
						else
						{
							// 原始写法为集合展开表达式：with { Mounts = [.. list[num3].Mounts, root] }
							// ILSpy 曾将其拆成对 init-only 属性的赋值（非法），此处按编译语义复原。
							list[num3] = list[num3] with
							{
								Mounts = [.. list[num3].Mounts, root]
							};
						}
					}
				}
			}
			using SafeFileHandle disk = Open(expected.DevicePath, 0u);
			SdDevice sdDevice = Read(disk, expected.Number, list, ProtectedDisks(list));
			if (sdDevice.Identity != expected.Identity)
			{
				throw new IOException("TF 卡设备已断开或更换。");
			}
			sdDevice.EnsureResetAllowed();
			return list;
		}
		string? ReadyMain(SdVolume main)
		{
			string? text2 = main.Mounts.FirstOrDefault(SdVolumeMounts.IsDriveLetter);
			if (text2 == null && main.Name.Length > 0)
			{
				uint logicalDrives = GetLogicalDrives();
				if (logicalDrives == 0)
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
				for (int i = 3; i < 26 && ((logicalDrives & (uint)(1 << i)) != 0 || !SetVolumeMountPoint((char)(65 + i) + ":\\", main.Name)); i++)
				{
				}
				return null;
			}
			if (text2 == null)
			{
				return null;
			}
			try
			{
				DriveInfo driveInfo = new DriveInfo(text2);
				string value = ((layout.Options.FileSystem == SdFileSystem.Fat32) ? "FAT32" : "exFAT");
				return (driveInfo.IsReady && driveInfo.DriveFormat.Equals(value, StringComparison.OrdinalIgnoreCase)) ? text2 : null;
			}
			catch (IOException ex) when (ex.HResult == -2147023891)
			{
				throw new Win32Exception(1005, "检查主数据分区 " + text2 + " 的文件系统失败：" + ex.Message);
			}
		}
		void RemoveMount(SdVolume expectedVolume, string mount)
		{
			SdVolume? sdVolume = Observe().SingleOrDefault((SdVolume v) => v.Mounts.Contains(mount, StringComparer.OrdinalIgnoreCase));
			if (sdVolume == null || !SdVolumeMounts.IsEmu(sdVolume, expected.Number, layout) || sdVolume.Offset != expectedVolume.Offset || sdVolume.Length != expectedVolume.Length || DeleteVolumeMountPoint(mount))
			{
				return;
			}
			throw new Win32Exception(Marshal.GetLastWin32Error(), "无法移除 emuMMC 分区的盘符或挂载点：" + mount);
		}
	}

	private static SdVolume ReadDriveVolume(string root)
	{
		if (!SdVolumeMounts.IsDriveLetter(root))
		{
			throw new IOException("无效的 TF 卡盘符。");
		}
		try
		{
			SafeFileHandle handle = Open("\\\\.\\" + root.Substring(0, 2), 0u);
			try
			{
				return SdVolumeMapping.Read("", new string[1] { root }, (uint code) => Control(handle, code, null, 32768));
			}
			finally
			{
				if (handle != null)
				{
					((IDisposable)handle).Dispose();
				}
			}
		}
		catch (Win32Exception ex)
		{
			throw new Win32Exception(ex.NativeErrorCode, "读取盘符 " + root + " 的设备映射失败：" + ex.Message);
		}
	}

	internal static byte[] ReadLayout(SafeFileHandle disk)
	{
		return Control(disk, 458832u, null, 18480);
	}

	private static string DeviceName(string dosName)
	{
		char[] array = new char[32768];
		if (QueryDosDevice(dosName, array, array.Length) == 0)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "读取设备链接失败：" + dosName);
		}
		return new string(array, 0, Array.IndexOf(array, '\0'));
	}

	internal static HashSet<int> ProtectedDisks(List<SdVolume> volumes)
	{
		int num = 4;
		List<string> list = new List<string>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<string> span = CollectionsMarshal.AsSpan(list);
		int num2 = 0;
		span[num2] = Environment.SystemDirectory;
		num2++;
		span[num2] = AppContext.BaseDirectory;
		num2++;
		span[num2] = Path.GetTempPath();
		num2++;
		span[num2] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		List<string> list2 = list;
		string? processPath = Environment.ProcessPath;
		if (processPath != null)
		{
			list2.Add(processPath);
		}
		string? environmentVariable = Environment.GetEnvironmentVariable("NS_TOOLBOX_EXECUTABLE");
		if (environmentVariable != null)
		{
			list2.Add(environmentVariable);
		}
		string[] array = new string[2] { "NS_TOOLBOX_SESSION", "DOTNET_BUNDLE_EXTRACT_BASE_DIR" };
		for (num2 = 0; num2 < array.Length; num2++)
		{
			string? environmentVariable2 = Environment.GetEnvironmentVariable(array[num2]);
			if (environmentVariable2 != null && environmentVariable2.Length > 0)
			{
				list2.Add(environmentVariable2);
			}
		}
		using RegistryKey? registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management");
		if (registryKey?.GetValue("ExistingPageFiles") is string[] source)
		{
			list2.AddRange(source.Select((string p) => (!p.StartsWith("\\??\\", StringComparison.Ordinal)) ? p : p.Substring(4, p.Length - 4)));
		}
		HashSet<int> hashSet = new HashSet<int>();
		foreach (string item in list2.Where((string p) => !string.IsNullOrWhiteSpace(p)))
		{
			StringBuilder stringBuilder = new StringBuilder(32768);
			StringBuilder volume = new StringBuilder(1024);
			string text = item;
			while (!Directory.Exists(text) && !File.Exists(text))
			{
				string? directoryName = Path.GetDirectoryName(text);
				if (directoryName == null || !(directoryName != text))
				{
					break;
				}
				text = directoryName;
			}
			if (!GetVolumePathName(text, stringBuilder, stringBuilder.Capacity) || !GetVolumeNameForVolumeMountPoint(stringBuilder.ToString(), volume, volume.Capacity))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确认系统与工具所在磁盘，已停止设备识别。");
			}
			SdVolume? sdVolume = volumes.FirstOrDefault((SdVolume v) => v.Name.Equals(volume.ToString(), StringComparison.OrdinalIgnoreCase));
			if (sdVolume == null)
			{
				throw new IOException("无法确认受保护磁盘，已停止设备识别。");
			}
			hashSet.UnionWith(sdVolume.Disks);
		}
		return hashSet;
	}

	internal static List<SdVolume> EnumerateVolumes()
	{
		List<SdVolume> list = new List<SdVolume>();
		StringBuilder stringBuilder = new StringBuilder(1024);
		nint num = FindFirstVolume(stringBuilder, stringBuilder.Capacity);
		if (num == new IntPtr(-1))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
		try
		{
			do
			{
				string path = stringBuilder.ToString();
				string text2;
				try
				{
					string text = path;
					text2 = DeviceName(text.Substring(4, text.Length - 4).TrimEnd('\\'));
				}
				catch (Win32Exception ex) when (SdDeviceAccess.IsUnavailable(ex.NativeErrorCode))
				{
					continue;
				}
				if (!SdVolumeScope.IsPartitionDevice(text2))
				{
					continue;
				}
				SdVolume? sdVolume = SdVolumeMapping.ReadEnumerated(path, () =>
				{
					char[] array = new char[32768];
					if (!GetVolumePathNamesForVolumeName(path, array, array.Length, out var _))
					{
						throw new Win32Exception(Marshal.GetLastWin32Error());
					}
					return new string(array).Split('\0', StringSplitOptions.RemoveEmptyEntries);
				}, (string[] mounts) =>
				{
					SafeFileHandle handle = Open(path.TrimEnd('\\'), 0u);
					try
					{
						return SdVolumeMapping.Read(path, mounts, (uint code) => Control(handle, code, null, 32768));
					}
					finally
					{
						if (handle != null)
						{
							((IDisposable)handle).Dispose();
						}
					}
				});
				if (sdVolume != null)
				{
					list.Add(sdVolume with
					{
						DeviceName = text2
					});
				}
			}
			while (FindNextVolume(num, stringBuilder, stringBuilder.Capacity));
			if (Marshal.GetLastWin32Error() != 18)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			return list;
		}
		finally
		{
			FindVolumeClose(num);
		}
	}

	internal static SafeFileHandle Open(string path, uint access)
	{
		SafeFileHandle safeFileHandle = CreateFile(path, access, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
		if (!safeFileHandle.IsInvalid)
		{
			return safeFileHandle;
		}
		int lastWin32Error = Marshal.GetLastWin32Error();
		safeFileHandle.Dispose();
		throw new Win32Exception(lastWin32Error, $"打开设备 {path} 失败（系统错误码：{lastWin32Error}）：{new Win32Exception(lastWin32Error).Message}");
	}

	internal static byte[] Control(SafeFileHandle handle, uint code, byte[]? input = null, int size = 0)
	{
		byte[]? array = ((size > 0) ? new byte[size] : null);
		if (!DeviceIoControl(handle, code, input, input?.Length ?? 0, array, size, out var returned, IntPtr.Zero))
		{
			int lastWin32Error = Marshal.GetLastWin32Error();
			throw new Win32Exception(lastWin32Error, $"设备查询 0x{code:X8} 失败（系统错误码：{lastWin32Error}）：{new Win32Exception(lastWin32Error).Message}");
		}
		if (array != null)
		{
			return array[..returned];
		}
		return Array.Empty<byte>();
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
	private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint GetLogicalDrives();

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetVolumeMountPointW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetVolumeMountPoint(string mount, string volume);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "DeleteVolumeMountPointW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeleteVolumeMountPoint(string mount);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, nint overlapped);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryDosDeviceW", SetLastError = true)]
	private static extern uint QueryDosDevice(string? name, char[] target, int size);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindFirstVolumeW", SetLastError = true)]
	private static extern nint FindFirstVolume(StringBuilder name, int length);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindNextVolumeW", SetLastError = true)]
	private static extern bool FindNextVolume(nint find, StringBuilder name, int length);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool FindVolumeClose(nint find);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumePathNamesForVolumeNameW", SetLastError = true)]
	private static extern bool GetVolumePathNamesForVolumeName(string name, char[] paths, int size, out int needed);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumePathNameW", SetLastError = true)]
	private static extern bool GetVolumePathName(string file, StringBuilder volume, int size);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true)]
	private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder name, int size);
}
