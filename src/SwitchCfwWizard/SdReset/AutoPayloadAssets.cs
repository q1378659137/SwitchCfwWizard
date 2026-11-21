// 移植自 yijianhuanbao（源文件 AutoPayloadAssets.cs，sha256 9696bacbe8c6…）。
// 2026-11-21 修改：组件包改为**从 GitHub Release 下载**（用户要求），
// 且程序里**不再内嵌**那份 2.7 MB 的 <c>NsToolbox.AutoEmu.zip</c> ——
// 离线场景交给「优先使用本地资源」复用 download/AutoEmu_*.zip。
// 所以这个类现在只剩一件事：把磁盘上的 zip 读出来并逐个文件校验。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace SwitchCfwWizard.SdReset;

internal static class AutoPayloadAssets
{
	/// <summary>
	/// 读**磁盘上**的组件包（下载得到的 <c>auto-emu.zip</c>，或 <c>download/</c> 里已下载的那份）。
	///
	/// <paramref name="onEntry"/> 每读出一个文件回调一次（名字, 0～1），
	/// 只用于界面上那条「解压进度」。
	///
	/// 校验按包内自带的 <c>sha256.json</c> **逐个文件**做：所以「下到一半的残骸」「被改过的包」
	/// 都进不来。
	/// </summary>
	internal static Dictionary<string, byte[]> LoadFromFile(string path, Action<string, double>? onEntry = null)
	{
		using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		return ReadZip(stream, onEntry);
	}

	private static Dictionary<string, byte[]> ReadZip(Stream stream, Action<string, double>? onEntry)
	{
		using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read);
		using Stream utf8Json = (zipArchive.GetEntry("sha256.json") ?? throw new IOException("自动创建组件索引缺失。")).Open();
		Dictionary<string, string> dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(utf8Json) ?? throw new IOException("组件索引无效。");
		Dictionary<string, byte[]> dictionary2 = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		string[] assetNames = EmuTaskFiles.AssetNames;

		for (var index = 0; index < assetNames.Length; index++)
		{
			string name = assetNames[index];
			onEntry?.Invoke(name, index / (double)assetNames.Length);

			ZipArchiveEntry? zipArchiveEntry = zipArchive.GetEntry(name) ?? throw new IOException("自动创建组件不完整。");
			long length = zipArchiveEntry.Length;
			bool flag = ((length <= 0 || length > 16777216) ? true : false);
			if (flag || Enumerable.Count(zipArchive.Entries, (ZipArchiveEntry e) => e.FullName == name) != 1)
			{
				throw new IOException("自动创建组件大小或名称无效。");
			}
			using Stream stream2 = zipArchiveEntry.Open();
			using MemoryStream memoryStream = new MemoryStream();
			stream2.CopyTo(memoryStream);
			byte[] array = memoryStream.ToArray();
			if (!dictionary.TryGetValue(name, out var value) || Convert.ToHexString(SHA256.HashData(array)) != value)
			{
				throw new IOException("自动创建组件校验失败。");
			}
			dictionary2.Add(name, array);
		}
		if (dictionary2["payload.bin"].Length > 196608)
		{
			throw new IOException("自动创建启动文件超过安全大小。");
		}
		onEntry?.Invoke(string.Empty, 1d);
		return dictionary2;
	}
}
