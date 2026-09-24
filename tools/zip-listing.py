#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""只看压缩包里有什么，**不把包下回来** —— 用 HTTP Range 取 ZIP 的中央目录。

原理：ZIP 的条目清单不在文件开头，而在**末尾的中央目录**里（EOCD 记录它的偏移与长度）。
所以分两段 Range 请求就够：① 末尾 128 KB 找 EOCD；② 按它给的偏移取回中央目录那一段。
包内有多少个文件、叫什么、原始大小多少，全都有了 —— 代价是**几十 KB 流量、几秒钟**，
而不是把 18 MB 的包在限速 25 KB/s 的链路上拖十几分钟。

什么时候值得这么做（判据，别当默认姿势）：
  - 链路很慢或按流量计费（本项目实测沙箱限速 ~25 KB/s）；
  - 只想确认**包内结构**（有没有多一层包装目录、某个文件在不在子目录里）——
    这正是「声明落点 / 声明 `ExtractPlan`」需要的依据，见 ComponentCatalog 里各组的注释；
  - 不想消耗一次完整的 asset 下载配额。
正常网络下直接下整包更省事。

用法：
    python tools/zip-listing.py xfangfang/wiliwili wiliwili-NintendoSwitch.zip
    python tools/zip-listing.py --specs-file specs.txt      # 每行「owner/repo asset-name」
    # 也可以一次给多组：
    python tools/zip-listing.py o/r a.zip o/r2 b.zip

⚠️ Token：只从环境变量 `GITHUB_TOKEN` 读，**绝不写进文件**（用户 2026-09-18 明确：
   仅用于调试，不写进代码或软件）。不设也能跑，只是匿名 60 次/小时。

⚠️ 已知限制：分卷 ZIP / ZIP64（>4 GB）的 EOCD 定位不适用；遇到会直接报错，不猜。
"""

import io
import json
import os
import struct
import sys
import urllib.error
import urllib.request

API = "https://api.github.com"
TIMEOUT = 120


def _headers(accept: str = "application/vnd.github+json") -> dict:
    h = {"Accept": accept, "User-Agent": "SwitchCFWizard-zip-listing"}
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        h["Authorization"] = "Bearer " + token
    return h


def _get(url: str, headers: dict):
    return urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=TIMEOUT)


def latest_release(repo: str) -> dict:
    """取最新的非草稿、非预发布 Release（拿不到就退回第一个）。"""
    with _get(f"{API}/repos/{repo}/releases?per_page=10", _headers()) as r:
        releases = json.loads(r.read().decode("utf-8"))
    if not releases:
        raise RuntimeError("这个仓库没有任何 Release")
    return next((x for x in releases if not x.get("draft") and not x.get("prerelease")), releases[0])


def total_size(asset_url: str) -> int:
    """用 `Range: bytes=0-0` 问出总长度 —— 比下载整个包便宜得多。"""
    r = _get(asset_url, _headers("application/octet-stream") | {"Range": "bytes=0-0"})
    content_range = r.headers.get("Content-Range")
    if content_range and "/" in content_range:
        return int(content_range.split("/")[-1])
    # 服务器忽略了 Range：退一步用 Content-Length（调用方会因拿不到 206 而报错，不会静默）
    length = r.headers.get("Content-Length")
    if not length:
        raise RuntimeError("既没有 Content-Range 也没有 Content-Length，无法确定包大小")
    return int(length)


def fetch_range(asset_url: str, start: int, end: int) -> bytes:
    r = _get(asset_url, _headers("application/octet-stream") | {"Range": f"bytes={start}-{end}"})
    if r.status != 206:
        raise RuntimeError(f"服务器不支持 Range（HTTP {r.status}）—— 请改用完整下载")
    return r.read()


def read_central_directory(asset_url: str):
    """返回 (总字节数, [(包内路径, 原始大小), …])。"""
    total = total_size(asset_url)

    # ① 末尾 128 KB，从里面找 EOCD 签名（PK\x05\x06）
    tail_start = max(0, total - 131072)
    tail = fetch_range(asset_url, tail_start, total - 1)
    eocd = tail.rfind(b"PK\x05\x06")
    if eocd < 0:
        raise RuntimeError("末尾 128 KB 里没有 EOCD 签名 —— 不是普通 ZIP，或文件被截断")
    cd_size, cd_offset = struct.unpack_from("<II", tail, eocd + 12)
    if cd_size == 0xFFFFFFFF or cd_offset == 0xFFFFFFFF:
        raise RuntimeError("这是 ZIP64（或分卷）—— 本脚本不支持，请改用完整下载")

    # ② 中央目录：已经在 tail 里就切出来，否则再请求一段
    if tail_start <= cd_offset and cd_offset + cd_size <= total:
        cd = tail[cd_offset - tail_start: cd_offset - tail_start + cd_size]
    else:
        cd = fetch_range(asset_url, cd_offset, cd_offset + cd_size - 1)

    # ③ 逐条解析中央目录记录（PK\x01\x02）
    entries, pos = [], 0
    while pos + 46 <= len(cd) and cd[pos:pos + 4] == b"PK\x01\x02":
        comp_size, uncomp_size = struct.unpack_from("<II", cd, pos + 20)
        name_len, extra_len, comment_len = struct.unpack_from("<HHH", cd, pos + 28)
        name = cd[pos + 46: pos + 46 + name_len].decode("utf-8", "replace")
        entries.append((name, uncomp_size))
        pos += 46 + name_len + extra_len + comment_len

    if not entries:
        raise RuntimeError("中央目录解析出 0 个条目 —— 格式假设不成立，不猜")
    return total, entries


def report(repo: str, asset: str) -> bool:
    """打印一个资源的包内结构。返回是否成功。"""
    print("=" * 78)
    print(f"{repo}  {asset}")
    print("=" * 78)
    try:
        release = latest_release(repo)
        match = next((a for a in release.get("assets", []) if a["name"] == asset), None)
        if match is None:
            names = sorted(a["name"] for a in release.get("assets", []))
            print(f"  !! {release['tag_name']} 里没有这个资源名。实际有：{names}")
            return False

        if not asset.lower().endswith(".zip"):
            print(f"  ~ 不是 .zip（{match['size']} 字节，{release['tag_name']}）—— 跳过结构检查")
            return True

        total, entries = read_central_directory(match["url"])
        files = [n for n, _ in entries if not n.endswith("/")]
        tops = sorted({n.split("/")[0] for n in files})
        print(f"  tag={release['tag_name']}  包 {total} 字节  条目 {len(entries)} 个（文件 {len(files)}）")
        print(f"  第一层：{tops}")
        print("  全部条目：")
        for name, size in sorted(entries):
            print(f"    {size:>10}  {name}")
        return True
    except (urllib.error.HTTPError, urllib.error.URLError, RuntimeError, ValueError) as e:
        print(f"  !! 失败：{e}")
        return False


def main(argv: list[str]) -> int:
    args = list(argv[1:])
    specs: list[tuple[str, str]] = []
    if args and args[0] == "--specs-file":
        if len(args) < 2:
            print("用法：--specs-file <文件>（每行「owner/repo 资源名」，# 开头为注释）")
            return 2
        with open(args[1], encoding="utf-8") as f:
            for line in f:
                line = line.split("#", 1)[0].strip()
                if line:
                    parts = line.split()
                    specs.append((parts[0], parts[1]))
    else:
        if len(args) % 2 != 0 or not args:
            print(__doc__)
            return 2
        specs = list(zip(args[0::2], args[1::2]))

    ok = all(report(repo, asset) for repo, asset in specs)
    print()
    print("全部成功" if ok else "有失败项（见上）")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
