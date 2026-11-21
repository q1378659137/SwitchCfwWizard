// 移植自 yijianhuanbao（源文件 EmuTaskFiles.cs，sha256 270c42ae69c7…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SwitchCfwWizard.SdReset;

public static class EmuTaskFiles
{
	public static readonly string[] AssetNames = new string[6] { "payload.bin", "nyx.bin", "libsys_minerva.bso", "libsys_lp0.bso", "res.pak", "thk.bin" };

	/// <summary>
	/// 把「下次开机自动创建 emuMMC」要用的文件写到卡上。
	///
	/// <paramref name="verifyExistingPayload"/> 默认 <c>true</c>：卡上**已有的** payload.bin 必须与
	/// task 记录的那份一致 —— 重建流程正是这样（刚格式化，卡上是整合包里的 payload）。
	/// 「仅创建 emuMMC 所需文件」这一档要传 <c>false</c>：那张卡根目录**本来就可能没有 payload.bin**
	/// （用户 2026-11-21 报的「点下一步之后创建不了」就是这里抛了 FileNotFoundException），
	/// 而且这一档的目的就是用组件包里的 payload 覆盖上去（原文件先备份成 payload.bin.backup）。
	/// </summary>
	public static void Deploy(string root, EmuTask task, IReadOnlyDictionary<string, byte[]> assets, Action guard,
		bool verifyExistingPayload = true)
	{
		guard();
		if (EmuTask.HasPending(root))
		{
			throw new IOException("此卡已有未完成的重建任务，请先恢复或继续准备。");
		}
		if (assets.Count != AssetNames.Length || AssetNames.Any((string n) => !assets.TryGetValue(n, out var value) || value.Length == 0))
		{
			throw new InvalidDataException("内置自动创建组件不完整。");
		}
		string text = P("payload.bin");
		string text2 = P("payload.bin.backup");
		if (verifyExistingPayload)
		{
			task.VerifyPayload(File.ReadAllBytes(text));
		}
		Directory.CreateDirectory(P("bootloader/ns-toolbox"));
		WriteVerified(P("bootloader/ns-toolbox/task.bin"), task.Encode(), guard);
		string[] assetNames = AssetNames;
		foreach (string name in assetNames)
		{
			WriteVerified(P("bootloader/ns-toolbox/" + name), assets[name], guard);
		}
		guard();

		// 备份卡上原有的 payload.bin：有就备份，**没有就跳过**（这一档允许空卡）。
		// 注意 backup 已存在时这里不必处理 —— <see cref="EmuTask.HasPending"/> 看到 backup
		// 就会判定「上一次没做完」，压根不会走到这里（那种情况该走 Resume）。
		if (File.Exists(text))
		{
			File.Move(text, text2);
		}

		WriteVerified(P("payload.bin"), assets["payload.bin"], guard);
		WriteVerified(P("ready.flag.tmp"), task.Encode(), guard);
		guard();
		File.Move(P("ready.flag.tmp"), P("ready.flag"));
		string P(string p)
		{
			return PathPolicy.SafePath(root, p);
		}
	}

	public static void Resume(string root, IReadOnlyDictionary<string, byte[]> assets, Action guard)
	{
		guard();
		EmuTask emuTask = EmuTask.Read(root);
		if (File.Exists(P("ready.flag.restore")))
		{
			Restore(root, guard);
			return;
		}
		string text = P("payload.bin.backup");
		emuTask.VerifyPayload(File.ReadAllBytes(File.Exists(text) ? text : P("payload.bin")));
		if (AssetNames.Any((string n) => !assets.TryGetValue(n, out var value) || value.Length == 0))
		{
			throw new IOException("内置自动创建组件不完整。");
		}
		Directory.CreateDirectory(P("bootloader/ns-toolbox"));
		WriteVerified(P("bootloader/ns-toolbox/task.bin"), emuTask.Encode(), guard);
		string[] assetNames = AssetNames;
		foreach (string text2 in assetNames)
		{
			WriteVerified(P("bootloader/ns-toolbox/" + text2), assets[text2], guard);
		}
		guard();
		if (!File.Exists(text))
		{
			File.Move(P("payload.bin"), text);
		}
		WriteVerified(P("payload.bin"), assets["payload.bin"], guard);
		WriteVerified(P("ready.flag.tmp"), emuTask.Encode(), guard);
		guard();
		File.Move(P("ready.flag.tmp"), P("ready.flag"), overwrite: true);
		string P(string p)
		{
			return PathPolicy.SafePath(root, p);
		}
	}

	public static void Restore(string root, Action guard)
	{
		guard();
		string text = P("payload.bin.backup");
		EmuTask emuTask;
		try
		{
			emuTask = EmuTask.Read(root);
		}
		catch (Exception ex) when ((ex is IOException || ex is InvalidDataException) ? true : false)
		{
			if (File.Exists(text) || File.Exists(P("ready.flag")) || File.Exists(P("ready.flag.restore")) || !CanDiscardStaging(root))
			{
				throw;
			}
			RemoveTemporary(root, guard);
			return;
		}
		byte[] array = File.ReadAllBytes(File.Exists(text) ? text : P("payload.bin"));
		emuTask.VerifyPayload(array);
		WriteVerified(P("ready.flag.restore"), emuTask.Encode(), guard);
		WriteVerified(P("payload.bin.restore"), array, guard);
		guard();
		File.Move(P("payload.bin.restore"), P("payload.bin"), overwrite: true);
		emuTask.VerifyPayload(File.ReadAllBytes(P("payload.bin")));
		WriteVerified(P("ready.flag"), emuTask.Encode(), guard);
		RemoveTemporary(root, guard);
		string P(string p)
		{
			return PathPolicy.SafePath(root, p);
		}
	}

	private static bool CanDiscardStaging(string root)
	{
		string path = PathPolicy.SafePath(root, "bootloader/ns-toolbox");
		byte[] array = File.ReadAllBytes(PathPolicy.SafePath(root, "payload.bin"));
		int num = array.Length;
		bool flag = ((num < 288 || num > 196608) ? true : false);
		if (flag || ((ReadOnlySpan<byte>)array.AsSpan(0, Math.Min(512, array.Length))).IndexOf("ICTC"u8) < 0)
		{
			return false;
		}
		string path2 = PathPolicy.SafePath(root, "bootloader/ns-toolbox/payload.bin");
		if (File.Exists(path2) && File.ReadAllBytes(path2).SequenceEqual(array))
		{
			return false;
		}
		string[] allowed = AssetNames.Append("task.bin").ToArray();
		if (Directory.Exists(path))
		{
			return Directory.EnumerateFileSystemEntries(path).All((string p) => !Directory.Exists(p) && allowed.Contains(Path.GetFileName(p), PathPolicy.Comparer));
		}
		return true;
	}

	private static void RemoveTemporary(string root, Action guard)
	{
		guard();
		string text = PathPolicy.SafePath(root, "bootloader/ns-toolbox");
		if (Directory.Exists(text))
		{
			Check(text);
			Directory.Delete(text, recursive: true);
		}
		string[] array = new string[5] { "payload.bin.restore", "ready.flag.tmp", "payload.bin.backup", "ready.flag", "ready.flag.restore" };
		foreach (string relative in array)
		{
			guard();
			string path = PathPolicy.SafePath(root, relative);
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		static void Check(string dir)
		{
			foreach (string item in Directory.EnumerateFileSystemEntries(dir))
			{
				PathPolicy.EnsureNotLink(item);
				if (Directory.Exists(item))
				{
					Check(item);
				}
			}
		}
	}

	internal static void WriteVerified(string path, byte[] bytes, Action guard)
	{
		guard();
		using (FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			fileStream.Write(bytes);
			fileStream.Flush(flushToDisk: true);
		}
		guard();
		if (!File.ReadAllBytes(path).SequenceEqual(bytes))
		{
			throw new IOException("写入后读回校验失败：" + Path.GetFileName(path));
		}
	}
}
