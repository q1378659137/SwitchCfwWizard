#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""探测「后台」5 个插件的 release 资源与压缩包内部结构（curl 版）。

⚠️ **这是历史工具，覆盖范围只有下面 REPOS 里那 5 个**（sys-botbase / usb-botbase /
MissionControl / sys-con / sys-ftpd）。要探**别的**仓库、或只想看包内结构，
用 `tools/zip-listing.py` —— 它走 HTTP Range 只取 ZIP 中央目录，几十 KB 拿全清单，
**不必下载整个包**（这个脚本会整包下下来）。留着的唯一理由是它记了一个沙箱事实：
本机代理会把 urllib 的 `Accept: application/octet-stream` 丢掉、而 curl 带 `-L` 能正确
拿到二进制（应用侧走自己的 HttpClient，不受影响）。

为什么用 curl 而不是 urllib：本机代理会把 urllib 的 `Accept: application/octet-stream`
丢掉（拿回来的是 asset 元数据 JSON，1.4KB），而 curl 带 -L 能正确跟随重定向拿到二进制。
应用侧走的是自己的 HttpClient，不受这个影响。

只探测，不改任何声明。
"""
import json
import os
import subprocess
import sys
import zipfile

TMP = os.path.join(os.environ.get("TEMP", "/tmp"), "probe-bg3")
os.makedirs(TMP, exist_ok=True)

REPOS = [
    ("olliz0r", "sys-botbase"),
    ("Koi-3088", "usb-botbase"),
    ("ndeadly", "MissionControl"),
    ("o0Zz", "sys-con"),
    ("ELY3M", "sys-ftpd"),
]


def curl(url, out=None, accept="application/vnd.github+json"):
    cmd = ["curl", "-sSL", "--max-time", "180", "-H", f"Accept: {accept}",
           "-H", "User-Agent: SwitchCfwWizard-probe"]
    if out:
        cmd += ["-o", out]
    cmd.append(url)
    r = subprocess.run(cmd, capture_output=True)
    if r.returncode != 0:
        raise RuntimeError(f"curl 失败({r.returncode}): {r.stderr.decode('utf-8', 'replace')[:200]}")
    return r.stdout


def show_tree(names, limit=25):
    tops = {}
    for n in names:
        parts = n.split("/")
        top = parts[0] if len(parts) > 1 else "(根文件)"
        tops[top] = tops.get(top, 0) + 1
    print("    顶层目录统计:")
    for k in sorted(tops):
        print(f"      {k!r:34s} {tops[k]:4d} 项")
    print(f"    全部路径（共 {len(names)}）:")
    for n in names[:limit]:
        print("      ", n)
    if len(names) > limit:
        print(f"      ... 还有 {len(names) - limit} 项")


def main():
    for owner, repo in REPOS:
        print("=" * 78)
        print(f"{owner}/{repo}")
        print("=" * 78)
        try:
            rel = json.loads(curl(f"https://api.github.com/repos/{owner}/{repo}/releases/latest"))
        except Exception as e:
            print("  !! 取 release 失败:", e)
            continue

        print(f"  tag = {rel.get('tag_name')}")
        assets = rel.get("assets", [])
        for a in assets:
            print(f"  asset: {a['name']:45s} {a['size']:>9} B  id={a['id']}")

        for a in assets:
            name = a["name"]
            if not name.lower().endswith((".zip", ".7z")):
                continue
            dest = os.path.join(TMP, f"{owner}__{repo}__{name}")
            print(f"\n  --- 下载 {name} ---")
            try:
                curl(a["url"], out=dest, accept="application/octet-stream")
            except Exception as e:
                print("    !! 下载失败:", e)
                continue
            size = os.path.getsize(dest)
            print(f"    已下载 {size} B（声明 {a['size']} B）")
            if size != a["size"]:
                print("    ⚠️ 大小对不上，可能又拿到元数据了")

            if name.lower().endswith(".zip"):
                try:
                    with zipfile.ZipFile(dest) as z:
                        infos = z.infolist()
                    names = [i.filename for i in infos if not i.is_dir()]
                    dirs = [i.filename for i in infos if i.is_dir()]
                    print(f"    文件 {len(names)} 项 / 目录项 {len(dirs)} 项")
                    show_tree(names)
                except Exception as e:
                    print("    !! 解 zip 失败:", e)
            else:
                print("    (.7z —— 交给应用侧的 SharpCompress)")
        print()


if __name__ == "__main__":
    main()
