#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
把 dist/ 刷新成「当前源码」的发布产物。

为什么需要它：`dist/` 是项目的正式发布目录（README 第一节），而它和
`publish/self-contained/` 是**同一个东西** —— 后者是 `bash tools/build.sh sc --verify`
的产物，已经做过语言包同步、运行痕迹清理、形态体检和 `--selftest` 验证。

所以这个脚本**不重新 publish**（那是 build.sh 的活），只做三件事：
    ① 体检 `publish/self-contained/` 真的是一份「干净、完整、含运行库」的产物；
    ② 把旧的 `dist/` **改名**挪走（不删 —— 改名是原子操作，任何环境都能过）；
    ③ 复制过去，然后逐项核对复制结果。

为什么要「改名 + 复制」而不是「直接 publish 到 dist/」：
原地覆盖不会清掉上一版多出来的文件（例如上次是别的形态留下的 dll），
而 build.sh 的注释里记着这个坑 ——「不含运行库的目录里残留着上次含运行库的运行时 dll，
看起来一切正常，用户却以为不用装 .NET」。

用法：

    bash tools/build.sh sc --verify        # 先产出（并验证）publish/self-contained/
    python tools/refresh-dist.py           # 再刷新 dist/

**不做任何删除**：旧目录改名成 `dist.stale-<月日-时分秒>` 后原样留着，路径会打印出来，
确认新产物没问题后你自己删。退出码 0 = 刷新成功且核对全过，1 = 有问题（会逐项点名）。
"""

import hashlib
import json
import os
import shutil
import sys
import time

# tools/ 的上一级就是项目根。用「能不能找到 src/SwitchCfwWizard」当判据，
# 而不是写死层数 —— 换个位置放脚本也不会静默指错地方。
PROJ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if not os.path.isdir(os.path.join(PROJ, "src", "SwitchCfwWizard")):
    print(f"!! 这个脚本要放在 <项目根>/tools/ 下（现在算出来的项目根是 {PROJ}）")
    sys.exit(2)

SRC = os.path.join(PROJ, "publish", "self-contained")
DST = os.path.join(PROJ, "dist")

# 产物里**不该有**的东西：运行痕迹（会被打进包交给用户）或构建中间物。
# 这些也是 build.sh 的 check_shape 会拦的 —— 这里再拦一次，是因为
# 「publish/self-contained 被人手动覆盖过」这种情形 build.sh 管不到。
FORBIDDEN = ["settings.json", "run.log", "selftest.log", "logs", "out", "download",
             "unpacked", "payload", "SwitchCfwWizard.pdb"]
# 产物里**必须**有的东西
REQUIRED = ["SwitchCfwWizard.exe", "SwitchCfwWizard.dll",
            "SwitchCfwWizard.runtimeconfig.json", "_BUILD-INFO.txt",
            os.path.join("lang", "Strings.zh-Hans.json"),
            os.path.join("lang", "Strings.zh-Hant.json"),
            os.path.join("lang", "Strings.en-US.json")]
# 逐字节复核的样本（全量比 468 个太慢；这几个覆盖「程序本体 + 配置 + 语言包」）
BYTE_CHECK = ["SwitchCfwWizard.exe", "SwitchCfwWizard.dll",
              "SwitchCfwWizard.runtimeconfig.json",
              os.path.join("lang", "Strings.zh-Hans.json")]


def count_files(d):
    return sum(len(f) for _, _, f in os.walk(d))


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def fail(msg):
    print(f"✗ {msg}")
    sys.exit(1)


# ── ① 体检源目录 ────────────────────────────────────────────────
print("=" * 70)
print("① 体检 publish/self-contained/（build.sh 的产物）")
print("=" * 70)

if not os.path.isdir(SRC):
    fail(f"源目录不存在：{SRC}\n  先跑：bash tools/build.sh sc --verify")

missing = [r for r in REQUIRED if not os.path.exists(os.path.join(SRC, r))]
if missing:
    fail(f"源目录缺文件：{missing}")

traces = [f for f in FORBIDDEN if os.path.exists(os.path.join(SRC, f))]
if traces:
    fail(f"源目录里有运行痕迹/中间物：{traces}（不该被打进发布包）")

# 判据：含运行库的产物，runtimeconfig.json 里必须有 includedFrameworks。
# 这一条同时也在防「publish 参数写错、安静地产出了框架依赖版」——那种产物
# 看着一样能用，但拿到没装 .NET 的机器上就双击不起来。
rc_path = os.path.join(SRC, "SwitchCfwWizard.runtimeconfig.json")
rc = json.load(open(rc_path, encoding="utf-8"))
if "includedFrameworks" not in rc.get("runtimeOptions", {}):
    fail("runtimeconfig.json 里没有 includedFrameworks ⇒ 这不是「自包含」产物")

src_n = count_files(SRC)
print(f"  ✓ 必需文件齐全（{len(REQUIRED)} 项）")
print(f"  ✓ 无运行痕迹（查了 {len(FORBIDDEN)} 项）")
print(f"  ✓ 含运行库（includedFrameworks 在）")
print(f"  ✓ 共 {src_n} 个文件")

# ── ② 旧 dist 改名挪走（不删） ──────────────────────────────────
print()
print("=" * 70)
print("② 挪走旧的 dist/（改名，不删除）")
print("=" * 70)

stale = None
if os.path.isdir(DST):
    stamp = time.strftime("%m%d-%H%M%S")
    stale = f"{DST}.stale-{stamp}"
    n = 0
    while os.path.exists(stale):
        n += 1
        stale = f"{DST}.stale-{stamp}-{n}"
    old_n = count_files(DST)
    os.rename(DST, stale)
    print(f"  ✓ 旧产物（{old_n} 个文件）已改名挪到：")
    print(f"    {stale}")
    print("  ⚠️ 没有删除它 —— 确认新产物没问题后可自行删除")
else:
    print("  -- dist/ 本来就不存在，直接新建")

# ── ③ 复制 ────────────────────────────────────────────────────
print()
print("=" * 70)
print("③ 复制 publish/self-contained/ → dist/")
print("=" * 70)

shutil.copytree(SRC, DST)
print(f"  ✓ 已复制 {count_files(DST)} 个文件")

# ── ④ 核对复制结果 ────────────────────────────────────────────
print()
print("=" * 70)
print("④ 核对 dist/")
print("=" * 70)

problems = []
if count_files(DST) != src_n:
    problems.append(f"文件数对不上：源 {src_n} / 目标 {count_files(DST)}")
missing = [r for r in REQUIRED if not os.path.exists(os.path.join(DST, r))]
if missing:
    problems.append(f"缺文件：{missing}")
extra = [f for f in FORBIDDEN if os.path.exists(os.path.join(DST, f))]
if extra:
    problems.append(f"多出不该有的：{extra}")

for rel in BYTE_CHECK:
    a, b = sha256(os.path.join(SRC, rel)), sha256(os.path.join(DST, rel))
    if a != b:
        problems.append(f"{rel} 逐字节不一致")
    else:
        print(f"  ✓ {rel:44} sha256 {a[:16]}…")

info = open(os.path.join(DST, "_BUILD-INFO.txt"), encoding="utf-8").read()
line = [l for l in info.splitlines() if "构建时间" in l]
print(f"  ✓ _BUILD-INFO.txt：{line[0].strip() if line else '（没找到构建时间行）'}")

if problems:
    print()
    for p in problems:
        print(f"✗ {p}")
    sys.exit(1)

print()
print("=" * 70)
print(f"✓ dist/ 已刷新：{count_files(DST)} 个文件，含运行库，可直接双击运行")
if stale:
    print(f"  旧产物留在：{stale}")
print("=" * 70)
