#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检查「源码包」的完整性 —— 拿包**逐条对磁盘**比，而不是只看包自己说不说「已生成」。

用法：
    python tools/check-source-zip.py                          # 检查 publish/source 下最新的那个包
    python tools/check-source-zip.py <某个.zip>               # 检查指定的包

检查七项（任何一项不过 → 打印问题 + 非零退出码）：
  1. 能打开、`testzip()` 报无损坏（CRC 层面）
  2. 顶层只有一个目录，且名字 = 包名去掉 .zip
  3. 包内有 _SOURCE-INFO.txt，且它声明的「文件数」与实际一致
  4. 关键文件齐全（改了游戏规则：ico.ico / README.md / csproj / 三份语言包 / build.sh / 回归入口）
  5. **双向集合核对**：磁盘上「按打包规则该进包的」↔「包内实际有的」
     —— 这条才是「完整性」的正解：单向看「包里的文件都在磁盘上」证明不了**没漏东西**。
  6. 逐条 CRC 与磁盘一致（能抓截断 / 损坏 / 同名不同内容）
  7. 不该在包里的（bin obj publish dist .pkgs __pycache__ .workbuddy-ai *.log settings.json）一条都没有

⚠️ 第 5 项用的跳过规则是**从 tools/pack-source.ps1 抄过来的，故意重复**：
   检查器如果跟被检查者共用同一份规则，那它就只能证明「我同意我自己」。
   改 ps1 里那三张 $Skip* 表时，**这里要一起改**。
"""

import io
import os
import re
import sys
import time
import zipfile
import zlib

# ── 与 tools/pack-source.ps1 的 $Skip* 保持一致（故意重复，见文件头说明）────────
SKIP_DIRS = {
    "bin", "obj", ".pkgs", "__pycache__",
    "dist", "publish", "out", "download", "logs",
    ".vs", ".idea", ".git", "node_modules",
    "TestResults",
    ".workbuddy-ai",
}
SKIP_FILE_NAMES = {"settings.json", "Thumbs.db", ".DS_Store", "desktop.ini"}
SKIP_EXTENSIONS = {".log", ".user", ".suo", ".pdb", ".cache", ".tmp"}

MUST_HAVE = [
    "_SOURCE-INFO.txt",
    "README.md",
    "ico.ico",
    "global.json",
    "NuGet.config",
    "src/SwitchCfwWizard/SwitchCfwWizard.csproj",
    "src/SwitchCfwWizard/MainWindow.xaml",
    "src/SwitchCfwWizard/Localization/Strings.zh-Hans.json",
    "src/SwitchCfwWizard/Localization/Strings.zh-Hant.json",
    "src/SwitchCfwWizard/Localization/Strings.en-US.json",
    "tools/build.sh",
    "tools/ConfigSmokeTest/Program.cs",
]

FORBIDDEN = ["/bin/", "/obj/", "/.pkgs/", "/publish/", "/dist/", "/.workbuddy-ai/", "/__pycache__/"]


def find_project_root(start):
    """本文件在 <项目>/tools/ 下 ⇒ 往上一级就是项目根。"""
    root = os.path.dirname(os.path.dirname(os.path.abspath(start)))
    if not os.path.isfile(os.path.join(root, "src", "SwitchCfwWizard", "SwitchCfwWizard.csproj")):
        sys.exit(f"✗ 找不到项目根（应该是 {root}）—— 本脚本要放在 <项目>/tools/ 下")
    return root


def pick_zip(root, argv):
    if len(argv) > 1:
        return os.path.abspath(argv[1])

    src_dir = os.path.join(root, "publish", "source")
    if not os.path.isdir(src_dir):
        sys.exit(f"✗ 没有 {src_dir} —— 先跑一次 tools/pack-source.ps1")

    zips = [os.path.join(src_dir, n) for n in os.listdir(src_dir)
            if n.startswith("SwitchCFWizard-") and n.endswith("-src.zip")]
    if not zips:
        sys.exit(f"✗ {src_dir} 下没有 SwitchCFWizard-*-src.zip")
    return max(zips, key=os.path.getmtime)


def walk_expected(root):
    """按打包规则在磁盘上走一遍，返回 {相对路径（/ 分隔）: 绝对路径}。"""
    expected = {}
    stack = [root]
    while stack:
        cur = stack.pop()
        for name in os.listdir(cur):
            full = os.path.join(cur, name)
            rel = os.path.relpath(full, root)
            if os.path.isdir(full):
                if name in SKIP_DIRS:
                    continue
                stack.append(full)
                continue
            if name in SKIP_FILE_NAMES or os.path.splitext(name)[1].lower() in SKIP_EXTENSIONS:
                continue
            expected[rel.replace(os.sep, "/")] = full
    return expected


def main():
    root = find_project_root(__file__)
    zip_path = pick_zip(root, sys.argv)

    print("  源码包完整性检查")
    print(f"  项目根   {root}")
    print(f"  压缩包   {zip_path}")
    stamp = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(os.path.getmtime(zip_path)))
    print(f"  大小     {os.path.getsize(zip_path)} 字节（{stamp}）")

    problems = []

    with zipfile.ZipFile(zip_path) as zf:
        entries = zf.namelist()
        bad_crc = zf.testzip()
        if bad_crc:
            problems.append(f"包内 CRC 损坏：{bad_crc}")

        # ② 顶层只有一个目录，且 = 包名
        tops = sorted({n.split("/")[0] for n in entries})
        want_top = os.path.basename(zip_path)[:-4]
        if tops != [want_top]:
            problems.append(f"顶层应当只有 [{want_top}]，实际 {tops}")

        # ④⑦ 关键文件 & 禁用路径
        for must in MUST_HAVE:
            if f"{want_top}/{must}" not in entries:
                problems.append(f"缺少必须有的文件：{must}")
        for bad in FORBIDDEN:
            hit = [n for n in entries if bad in "/" + n]
            if hit:
                problems.append(f"不该出现 {bad}（{len(hit)} 项，例如 {hit[0]}）")

        # ③ 清单自洽
        manifest_name = f"{want_top}/_SOURCE-INFO.txt"
        manifest = zf.read(manifest_name).decode("utf-8-sig") if manifest_name in entries else ""
        packed = {n[len(want_top) + 1:] for n in entries if n != manifest_name}
        m = re.search(r"文件数\s+(\d+)", manifest)
        if not m:
            problems.append("清单里没有「文件数」一行")
        elif int(m.group(1)) != len(packed):
            problems.append(f"清单声明 {m.group(1)} 个文件，实际 {len(packed)} 个")
        if manifest_name in entries:
            print("\n  ── 包内 _SOURCE-INFO.txt 前 8 行 ──")
            for line in manifest.splitlines()[:8]:
                print("     " + line)

        # ⑤ 双向集合核对
        expected = walk_expected(root)
        # 压缩包自己也可能落在项目内（默认在 publish/ 下、已被规则排掉），这里仍显式排一次
        zip_abs = os.path.abspath(zip_path)
        expected = {k: v for k, v in expected.items() if os.path.abspath(v) != zip_abs}

        missing = sorted(set(expected) - packed)
        extra = sorted(packed - set(expected))
        zip_stamp = os.path.getmtime(zip_path)

        # 「磁盘上有、包里没有」分两种，混在一起会让人白查一轮：
        #   ① 文件比包还新 ⇒ 它是在打包**之后**才新增/改动的，也就是说**这个包是旧的**（重打即可）
        #   ② 文件比包旧 ⇒ 说不通，规则漏了或包被改过 —— 这才是真缺陷
        stale = [rel for rel in missing if os.path.getmtime(expected[rel]) > zip_stamp]
        unexplained = [rel for rel in missing if rel not in stale]

        if stale:
            problems.append(
                f"包是旧的：{len(stale)} 个文件比包（{time.strftime('%H:%M:%S', time.localtime(zip_stamp))}）还新，"
                f"重新打一次包即可 —— {stale[:3]}")
        if unexplained:
            problems.append(f"磁盘上有、包里没有（且说不通）：{len(unexplained)} 个（例如 {unexplained[:3]}）")
        if extra:
            problems.append(f"包里有、磁盘上找不到：{len(extra)} 个（例如 {extra[:3]}）")

        # ⑥ 逐条 CRC
        mismatch = []
        for rel, disk in expected.items():
            if rel not in packed:
                continue
            info = zf.getinfo(f"{want_top}/{rel}")
            data = io.open(disk, "rb").read()
            if info.file_size != len(data) or info.CRC != (zlib.crc32(data) & 0xffffffff):
                mismatch.append(rel)
        if mismatch:
            problems.append(f"CRC / 大小与磁盘不一致：{len(mismatch)} 个（例如 {mismatch[:3]}）")

    print()
    print(f"  包内条目       {len(entries)}（含 _SOURCE-INFO.txt）")
    print(f"  双向集合核对   该打包 {len(expected)} 个 → 缺 {len(missing)}"
          f"（其中包生成后才新增/改动 {len(stale)}）、多 {len(extra)}")
    print(f"  CRC 逐条比对   {len(expected) - len(missing)} 条 → 不一致 {len(mismatch)}")

    if problems:
        print("\n  ✗ 不通过：")
        for p in problems:
            print(f"     - {p}")
        return 1

    print("\n  ✓ 通过：条目齐全、逐条 CRC 与磁盘一致、没有构建产物混入")
    return 0


if __name__ == "__main__":
    sys.exit(main())
