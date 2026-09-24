#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
联网核对「写进注释里的实测事实」—— 手工工具，**不在离线回归里**。

为什么要有它
------------
离线回归（tools/ConfigSmokeTest）只读 C# 声明，所以它**只能钉住「人有没有改声明」**，
钉不住「上游有没有改包」。而声明里那一串「实测」事实（资源名逐字是什么、多少字节、
包内有没有多一层包装目录、两个包差在哪）—— 一旦写错，**不会有任何断言变红**。

2026-09-18 就真的写错过一条：注释里说「EN 包里只有英文那一份语言 json」，
逐条目对比后发现两包的语言文件**逐字节相同**。结论没变，所以全绿通过。
那次之后定了规矩：**凡写「实测」，当场留下可复核的依据。**

这个脚本就是那个「依据」。用法：

    python tools/check-upstream-facts.py              # 全部检查（首次会下载约 60 MB）
    python tools/check-upstream-facts.py --cache D:   # 指定包缓存目录
    python tools/check-upstream-facts.py --only KeyX  # 只查某个组件

⚠️ 它要联网，且走 api.github.com（**未登录只有 60 次/小时**，本仓库的清单已不止这个数）。
   设了环境变量 `GITHUB_TOKEN` 就走 5000 次/小时 —— **token 只从环境变量读，绝不写进本文件**：

       GITHUB_TOKEN=xxx python tools/check-upstream-facts.py

   本沙箱实测 github.com 的 release 直链被代理挡 502，所以这里直接用 API 的 assets 端点。
"""

import argparse
import io
import json
import os
import re
import sys
import urllib.error
import urllib.request
import zipfile

API = "https://api.github.com"
UA = {"Accept": "application/vnd.github+json", "User-Agent": "SwitchCFWizard-fact-check"}
# ⚠️ token 只从环境变量取，**不要**把它写进这个文件（见文件头的说明）。
if os.environ.get("GITHUB_TOKEN"):
    UA["Authorization"] = "Bearer " + os.environ["GITHUB_TOKEN"]

# ── 事实清单**从源码注释里现取**，不在这里手抄第二份 ──
#   手抄的清单一定会腐烂（改了一处、忘了另一处，而且不会有任何断言变红）。
#   这里认的格式就是 ComponentCatalog.cs 里那段「上游实测」清单：
#       仓库名/仓库  版本 → 文件名（N 字节）
#   解析不到任何条目 = 格式被改坏了，直接报错退出（不能静默「全部通过」）。
FACT_LINE = re.compile(
    r"//\s+([\w.\-]+/[\w.\-]+)\s+(v?\d[\w.\-]*)\s+→\s*"
    r"([\w.\-]+)\s*（(\d+)\s*字节）"
    r"(?:\s*/\s*([\w.\-]+)\s*（(\d+)\s*字节）)?"
)

# 包内第一层允许出现的目录名（SD 卡根名）。出现别的名字 = 多了一层包装目录，
# 而「不声明 ExtractPlan / 落点」的结论正是建立在「没有包装目录」上的。
SD_ROOT_NAMES = {
    "atmosphere", "bootloader", "switch", "config", "themes", "SaltySD",
    "warmboot", "payloads", "hbmenu.nro", "boot.dat",
}

# 源码里声明的 sysmodule title（用来核对「不声明也不会漏掉冲突」那条结论）
CATALOG = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                       "..", "src", "SwitchCfwWizard", "Services", "ComponentCatalog.cs")


def load_documented_facts(path):
    """从 ComponentCatalog.cs 的注释里现取实测清单 → [(repo, tag, asset, size), ...]"""
    src = open(path, encoding="utf-8").read()
    facts = []
    for m in FACT_LINE.finditer(src):
        repo, tag, a1, s1, a2, s2 = m.groups()
        facts.append((repo, tag, a1, int(s1)))
        if a2:
            facts.append((repo, tag, a2, int(s2)))
    return facts


def fetch_json(url):
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=90) as r:
        return json.loads(r.read().decode("utf-8"))


def fetch_bytes(url):
    req = urllib.request.Request(url, headers={**UA, "Accept": "application/octet-stream"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return r.read()


def release_assets(repo):
    """返回 {资源名: (size, download_url)}（取最新一个非 draft 的 release）。"""
    rels = fetch_json(f"{API}/repos/{repo}/releases?per_page=10")
    if not rels:
        raise RuntimeError(f"{repo} 没有任何 Release")
    rel = next((r for r in rels if not r.get("draft")), rels[0])
    return rel.get("tag_name", "?"), {
        a["name"]: (a["size"], a["url"]) for a in rel.get("assets", [])
    }


def get_package(repo, name, size, url, cache):
    """取包：命中缓存就用缓存，否则下载。返回 (bytes, 来源说明)。"""
    path = os.path.join(cache, name)
    if os.path.exists(path) and os.path.getsize(path) == size:
        with open(path, "rb") as f:
            return f.read(), "缓存"
    data = fetch_bytes(url)
    os.makedirs(cache, exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)
    return data, "下载"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=os.path.join(os.path.expanduser("~"), ".cache", "switchcfw-facts"),
                    help="包缓存目录（默认放用户目录下，不污染仓库）")
    ap.add_argument("--only", default=None, help="只检查名字里含该串的组件")
    args = ap.parse_args()

    if not os.path.exists(CATALOG):
        print(f"!! 读不到清单来源：{CATALOG}")
        print("   本脚本靠相对路径去读 ComponentCatalog.cs，所以要放在仓库的 tools/ 目录下运行。")
        return 2

    facts = load_documented_facts(CATALOG)
    if not facts:
        print(f"!! 没能从 {CATALOG} 的注释里解析出任何实测条目 —— 清单格式被改坏了？")
        print("   期望格式： //  仓库名/仓库  版本 → 文件名（N 字节）")
        return 2
    print(f"清单来源：ComponentCatalog.cs 的注释，解析出 {len(facts)} 条")

    facts = [f for f in facts
             if not args.only or args.only.lower() in f[0].lower() or args.only.lower() in f[2].lower()]
    problems, notes = [], []
    packages = {}

    print("=" * 78)
    print("① 资源名逐字命中 + 字节数")
    print("=" * 78)
    by_repo = {}
    for repo, doc_tag, asset, size in facts:
        try:
            if repo not in by_repo:
                by_repo[repo] = release_assets(repo)
            tag, assets = by_repo[repo]
        except Exception as e:                      # noqa: BLE001
            problems.append(f"{repo}: 取 Release 失败 —— {e}")
            print(f"  !! {repo:38} 取 Release 失败: {e}")
            continue

        if asset not in assets:
            problems.append(f"{repo} 的 {tag} 里没有 {asset}（实际资源：{sorted(assets)}）")
            print(f"  !! {asset:20} **逐字不存在** —— {tag} 的实际资源：{sorted(assets)}")
            continue

        real_size, url = assets[asset]
        mark = "OK" if real_size == size else "!!"
        if real_size != size:
            problems.append(f"{asset}: 注释写 {size} 字节，上游是 {real_size}")
        if doc_tag != tag:
            notes.append(f"{asset}: 注释记的版本 {doc_tag}，当前最新是 {tag}（资源名仍逐字命中）")
        print(f"  {mark} {asset:20} {tag:12} {real_size:>9} 字节"
              + ("" if real_size == size else f"  (注释写的是 {size})"))

        try:
            data, src = get_package(repo, asset, real_size, url, args.cache)
        except Exception as e:                      # noqa: BLE001
            problems.append(f"{asset}: 取包失败 —— {e}")
            print(f"     !! 取包失败: {e}")
            continue
        if len(data) != real_size:
            problems.append(f"{asset}: 取回的字节数 {len(data)} 与 API 报的 {real_size} 不一致")
        packages[asset] = data
        print(f"     └ 已取回（{src}），{len(data)} 字节")

    print()
    print("=" * 78)
    print("② 包内第一层是不是 SD 卡根名（有没有多一层包装目录）")
    print("=" * 78)
    # 源码里声明的 StripPrefix —— 那一层包装目录是**刻意剥掉的**，所以它出现在第一层
    # 是预期行为，不是「多了一层」。这份清单同样**从源码现取**，不手抄。
    declared_strip = {m.strip().lower()
                      for m in re.findall(r'StripPrefix\s*=\s*"([^"]+)"', open(CATALOG, encoding="utf-8").read())}
    for name, data in packages.items():
        if not name.lower().endswith(".zip"):
            print(f"  -- {name:20} 不是压缩包（散装文件），跳过结构检查")
            continue
        try:
            with zipfile.ZipFile(io.BytesIO(data)) as z:
                entries = [n for n in z.namelist() if not n.endswith("/")]
        except zipfile.BadZipFile as e:
            problems.append(f"{name}: 不是有效的 zip（{e}）—— 声明里当成压缩包在处理")
            print(f"  !! {name:20} 打不开：{e}")
            continue
        tops = sorted({n.split("/")[0] for n in entries})
        bad = [t for t in tops if t not in SD_ROOT_NAMES and t.lower() not in declared_strip]
        if bad:
            problems.append(f"{name}: 第一层出现非 SD 根名 {bad} —— 可能多了一层包装目录，"
                            f"落点会全错（现在声明的是「不筛选、铺 out 根」）")
            print(f"  !! {name:20} 第一层 {tops}  ← {bad} 不像 SD 根名")
        else:
            stripped = [t for t in tops if t.lower() in declared_strip]
            tail = f"（其中 {stripped} 是声明了 StripPrefix 的包装目录，会被剥掉）" if stripped else ""
            print(f"  OK {name:20} 第一层 {tops}{tail}")

    print()
    print("=" * 78)
    print("③ KeyX 两个包到底差在哪（注释断言：只有 ovl 里内嵌的显示名不同）")
    print("=" * 78)
    if "KeyX-CN.zip" in packages and "KeyX-EN.zip" in packages:
        def index(data):
            with zipfile.ZipFile(io.BytesIO(data)) as z:
                return {i.filename: (i.file_size, z.read(i.filename))
                        for i in z.infolist() if not i.filename.endswith("/")}
        cn, en = index(packages["KeyX-CN.zip"]), index(packages["KeyX-EN.zip"])
        only_cn, only_en = set(cn) - set(en), set(en) - set(cn)
        if only_cn or only_en:
            notes.append(f"两包条目名集合不同（只 CN: {sorted(only_cn)} / 只 EN: {sorted(only_en)}）")
            print(f"  ~ 条目名集合不同：只 CN {sorted(only_cn)}、只 EN {sorted(only_en)}")
        else:
            print(f"  OK 两包条目名集合完全相同（各 {len(cn)} 个）")
        differing = [n for n in sorted(set(cn) & set(en)) if cn[n][1] != en[n][1]]
        same = len(set(cn) & set(en)) - len(differing)
        print(f"     逐字节相同的条目：{same} 个")
        for n in differing:
            da, db = cn[n][1], en[n][1]
            nbytes = sum(1 for i in range(min(len(da), len(db))) if da[i] != db[i])
            print(f"     内容不同：{n}（长度 {len(da)}/{len(db)}，{nbytes} 个字节不同）")
        lang = [n for n in set(cn) & set(en) if "/lang/" in n.lower()]
        print(f"     语言文件（{len(lang)} 个）是否两包相同："
              f"{'是' if all(cn[n][1] == en[n][1] for n in lang) else '**否**'}")
    else:
        notes.append("KeyX 两个包没同时取到，跳过 ③")

    print()
    print("=" * 78)
    print("④ 不声明 SysmoduleTitleId 会不会漏掉冲突（注释断言：无交集）")
    print("=" * 78)
    try:
        src = open(CATALOG, encoding="utf-8").read()
        declared = {m.upper() for m in re.findall(r'SysmoduleTitleId\s*=\s*"([0-9A-Fa-f]+)"', src)}
        found = {}
        for name, data in packages.items():
            if not name.lower().endswith(".zip"):
                continue
            with zipfile.ZipFile(io.BytesIO(data)) as z:
                for n in z.namelist():
                    m = re.match(r"atmosphere/contents/([0-9A-Fa-f]{16})/", n)
                    if m:
                        found.setdefault(m.group(1).upper(), set()).add(name)
        print(f"  包里实际出现的 title（{len(found)} 个）：")
        for t in sorted(found):
            print(f"    {t}  <- {sorted(found[t])}")
        print(f"  源码声明的 title：{sorted(declared)}")
        inter = sorted(set(found) & declared)
        if inter:
            problems.append(f"包内 title 与已声明的撞上了：{inter} —— 冲突检测会漏掉它们")
            print(f"  !! 交集非空：{inter}")
        else:
            print("  OK 交集为空 —— 「不声明也不会漏掉真实冲突」成立")
    except Exception as e:                          # noqa: BLE001
        notes.append(f"④ 跳过：{e}")
        print(f"  ~ 跳过：{e}")

    print()
    print("=" * 78)
    print("⑤ 按语言切换的候选包，包内结构是否一致")
    print("=" * 78)
    # 「按语言取哪个包 / 哪个仓库」的槽（KeyX 的 CN/EN、linkalho 的 CN/EN 仓库），
    # 落点声明是**同一份**。所以两个候选的包内结构必须一致 —— 若一个多了一层目录，
    # 那种语言的产物会静默落到错地方，而另一种语言完全正常。
    #
    # 分组方式：把包名规范化（去 .zip、去尾部 -cn/-en、去版本号）后同名的归为一组。
    # ⚠️ 这是**分析侧**的启发式，分组结果会原样打印出来供人复核（「猜」必须留痕）。
    def stem(name):
        s = name.lower()
        for ext in (".zip", ".nro", ".7z"):
            if s.endswith(ext):
                s = s[: -len(ext)]
                break
        s = re.sub(r"-(cn|en|zhtw|zhcn)$", "", s)
        s = re.sub(r"-v?\d+(\.\d+)*$", "", s)
        return s

    groups = {}
    for name, data in packages.items():
        groups.setdefault(stem(name), []).append(name)
    multi = {k: v for k, v in groups.items() if len(v) > 1}
    if not multi:
        print("  -- 没找到「同一组里的多个候选包」，跳过")
    for key, names in sorted(multi.items()):
        # ⚠️ 比的是**完整文件路径集合**，不是只看第一层。
        #    落点声明（含 StripPrefix / KeepFileNames）依赖的是**包内完整路径**，
        #    只看第一层会漏掉「第一层相同、里面差一层」这种差别 —— 那正是会静默落错的那种。
        #    （这一条是被自己审出来的：第一版只比 `n.split("/")[0]`，覆盖面小于它自称的。）
        shapes, paths = {}, {}
        for name in names:
            data = packages[name]
            if not name.lower().endswith(".zip"):
                shapes[name] = "<非压缩包>"
                paths[name] = None
                continue
            with zipfile.ZipFile(io.BytesIO(data)) as z:
                files = [n for n in z.namelist() if not n.endswith("/")]
            shapes[name] = tuple(sorted({n.split("/")[0] for n in files})) or ("<空包>",)
            paths[name] = tuple(sorted(files))

        print(f"  组 {key}: " + "  ".join(f"{n}→{list(shapes[n])}" for n in sorted(names)))
        comparable = {n: p for n, p in paths.items() if p is not None}
        if len(comparable) < 2:
            print("  -- 组内可比较的压缩包不足 2 个，跳过")
            continue
        baseline_name, baseline = next(iter(sorted(comparable.items())))
        for name, p in sorted(comparable.items()):
            if p == baseline:
                continue
            only_here = sorted(set(p) - set(baseline))
            only_base = sorted(set(baseline) - set(p))
            problems.append(
                f"{key} 组的候选包包内路径不一致（{name} vs {baseline_name}）："
                f"{name} 独有 {only_here[:6]}；{baseline_name} 独有 {only_base[:6]}"
                f" —— 落点声明是同一份，结构不同会让其中一种语言静默落错")
            print(f"  !! 结构不一致：{name} 独有 {only_here[:6]} / {baseline_name} 独有 {only_base[:6]}")
            break
        else:
            print(f"  OK 包内路径完全一致（{len(baseline)} 个文件；落点声明对两者都成立）")

    print()
    print("=" * 78)
    if problems:
        print(f"发现 {len(problems)} 个问题（注释里的「实测」与上游不符）：")
        for p in problems:
            print(f"  ✗ {p}")
    else:
        print("全部核对通过：注释里写的实测事实与上游一致。")
    for n in notes:
        print(f"  · {n}")
    print("=" * 78)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
