#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""验证 `tools/refresh-dist.py` —— 正常路径 2 条 + 拒绝路径 5 条，共 10 项。

为什么要有它：`refresh-dist.py` 的职责是**拒绝坏输入**（源目录不存在 / 混进运行痕迹 /
不是自包含产物 / 复制不完整）。这些分支在真实项目里几乎不会自然发生 —— 也就是说，
**它们永远得不到验证**，除非专门造出来。一个从没被执行过的拒绝路径，是一句声明，不是护栏。

它在一个**假项目**里跑（小文件、假 `runtimeconfig.json`），**不碰真产物**。
假项目默认落在 `<工作区根>/.workbuddy-ai/scratch/tooltest-refresh/`（`tools/` 的上两级，即含
`SwitchCFWizard/` 与 `.workbuddy-ai/` 的那一层），跑完保留供人查看（每次重跑会先清掉）。

⚠️ 这个脚本会 `rmtree` 夹具目录，所以 `--work-dir` **必须先过一道安全检查**
（见 `assert_safe_work_dir`）：根目录、用户主目录、**真项目根**、含**真发布产物**的目录一律拒绝，
退出码 2 并说明原因。这道检查本身也在场景 ⑦ 里被验证过 —— **没被验证过的拒绝路径只是一句声明**。

用法：

    python tools/test-refresh-dist.py
    python tools/test-refresh-dist.py --work-dir D:/somewhere     # 换夹具位置
    python tools/test-refresh-dist.py --tool path/to/other.py     # 验证另一份实现

退出码：0 = 全部通过；1 = 有失败项；2 = 参数/环境问题（含 `--work-dir` 不安全）。
"""

import argparse
import json
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

# 自包含产物的 runtimeconfig 里有 includedFrameworks；框架依赖版是 frameworks。
RC_SELF_CONTAINED = {"runtimeOptions": {"tfm": "net8.0", "includedFrameworks": [
    {"name": "Microsoft.WindowsDesktop.App", "version": "8.0.0"}]}}
RC_FRAMEWORK_DEPENDENT = {"runtimeOptions": {"tfm": "net8.0", "frameworks": [
    {"name": "Microsoft.WindowsDesktop.App", "version": "8.0.0"}]}}

INFO = ("SwitchCFWizard —— 含运行库（自包含，多文件）\n"
        "构建时间   ：2026-09-18 19:23:24\n")

# 夹具的「身份证」：`fresh_fake` 在夹具根写这个文件，`unsafe_reason` 见到就放行。
# 用标记而不是猜特征 —— 见 unsafe_reason 的说明。
MARKER = ".tooltest-fixture"

results = []          # (ok, label, got, want)


def check(ok, label, got=None, want=None):
    results.append((bool(ok), label, got, want))
    print(f"  {'✓' if ok else '✗'} {label}"
          + ("" if ok else f"（得到 {got}，期望 {want}）"))
    return bool(ok)


def unsafe_reason(path):
    """`--work-dir` 会不会指到「真东西」上？返回原因字符串，安全时返回 None。

    存在理由：`fresh_fake()` 上来就 `rmtree(work_dir)`。这个脚本的默认位置是 scratch，
    但 `--work-dir` 是**用户可传**的 —— 传成项目根就等于把整个项目删掉。
    这类「参数一旦传错后果不可逆」的入口，护栏必须写在**动手之前**。

    ⚠️ 判据用**自证身份**（本脚本自己写的标记文件）而不是「猜特征」。
    第一版按特征判（见到 `dist/SwitchCfwWizard.exe` 就拦），结果**把夹具自己的产物误判成真产物** ——
    夹具里当然会有这个文件，它就是在验证「刷新 dist」。**猜特征一定会误伤，标记不会。**
    """
    ap = os.path.abspath(path)
    if os.path.isfile(os.path.join(ap, MARKER)):
        return None                      # 本脚本自己建的夹具，放行
    if os.path.dirname(ap) == ap:
        return f"这是盘符根目录：{ap}"
    home = os.path.abspath(os.path.expanduser("~"))
    if ap == home or home.startswith(ap + os.sep):
        return f"这是用户主目录（{home}）或其祖先：{ap}"
    proj = os.path.join(ap, "src", "SwitchCfwWizard")
    if os.path.isdir(proj) and any(f.endswith(".csproj") for f in os.listdir(proj)):
        return f"这看起来是**真项目根**（含 src/SwitchCfwWizard/*.csproj）：{ap}"
    for marker in (("dist", "SwitchCfwWizard.exe"),
                   ("publish", "self-contained", "SwitchCfwWizard.exe")):
        if os.path.isfile(os.path.join(ap, *marker)):
            return f"这里面有**真发布产物**（{'/'.join(marker)}）：{ap}"
    return None


def safe_rmtree(path):
    """只在确认安全时递归删除。不安全就抛 —— 由调用方决定怎么报。"""
    why = unsafe_reason(path)
    if why:
        raise SystemExit(f"!! 拒绝在 {path} 上做递归删除：{why}")
    if os.path.isdir(path):
        shutil.rmtree(path)


def run_tool(tool_path, expect_rc, expect_text, label):
    """跑一次工具，断言退出码 + 输出里出现某句话。"""
    p = subprocess.run([sys.executable, tool_path],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = (p.stdout or "") + (p.stderr or "")
    ok = (p.returncode == expect_rc) and (expect_text in out)
    results.append((ok, label, p.returncode, expect_rc))
    print(f"  {'✓' if ok else '✗'} {label}")
    print(f"      退出码 {p.returncode}（期望 {expect_rc}）"
          + ("" if expect_text in out else f"  ← 输出里没找到「{expect_text}」"))
    if not ok:
        print("      ---- 输出尾部 ----")
        for line in out.splitlines()[-12:]:
            print("      " + line)
    return p.returncode


def make_source(fake, rc=RC_SELF_CONTAINED, extra=None):
    src = os.path.join(fake, "publish", "self-contained")
    if os.path.isdir(src):
        shutil.rmtree(src)
    os.makedirs(os.path.join(src, "lang"))
    for name, body in [("SwitchCfwWizard.exe", "EXE-BYTES"),
                       ("SwitchCfwWizard.dll", "DLL-BYTES"),
                       ("_BUILD-INFO.txt", INFO)]:
        open(os.path.join(src, name), "w", encoding="utf-8").write(body)
    for lang in ("zh-Hans", "zh-Hant", "en-US"):
        open(os.path.join(src, "lang", f"Strings.{lang}.json"), "w",
             encoding="utf-8").write("{}\n")
    json.dump(rc, open(os.path.join(src, "SwitchCfwWizard.runtimeconfig.json"), "w",
                       encoding="utf-8"))
    for name in (extra or []):
        open(os.path.join(src, name), "w", encoding="utf-8").write("junk")


def fresh_fake(fake, tool_path):
    safe_rmtree(fake)                    # 旧标记还在 ⇒ 这一步会被放行
    os.makedirs(fake, exist_ok=True)
    open(os.path.join(fake, MARKER), "w", encoding="utf-8").write(
        "这个目录由 tools/test-refresh-dist.py 创建，可以被它递归删除。\n")
    os.makedirs(os.path.join(fake, "tools"))
    # 工具会去找「项目根」（含 src/SwitchCfwWizard 的那一层），给一个假的让它认出来。
    os.makedirs(os.path.join(fake, "src", "SwitchCfwWizard"))
    shutil.copy2(tool_path, os.path.join(fake, "tools", "refresh-dist.py"))


def main():
    ap = argparse.ArgumentParser(description="验证 refresh-dist.py 的 8 条路径")
    ap.add_argument("--tool", default=os.path.join(HERE, "refresh-dist.py"))
    ap.add_argument("--work-dir", default=os.path.normpath(
        os.path.join(HERE, "..", "..", ".workbuddy-ai", "scratch", "tooltest-refresh")))
    args = ap.parse_args()

    tool_path = os.path.abspath(args.tool)
    fake = os.path.abspath(args.work_dir)
    if not os.path.isfile(tool_path):
        print(f"!! 找不到被验证的工具：{tool_path}")
        return 2

    why = unsafe_reason(fake)
    if why:
        print(f"!! 拒绝把夹具放在 {fake}")
        print(f"   原因：{why}")
        print("   这个脚本会递归删除夹具目录，所以不允许指向真项目 / 真产物 / 主目录 / 盘符根。")
        print(f"   （如果这确实是本脚本留下的**旧夹具** —— 早于 {MARKER} 标记出现的那版 ——")
        print("     把它改名挪走再跑即可，新的一版会自己带上标记。）")
        return 2

    fake_tool = os.path.join(fake, "tools", "refresh-dist.py")

    print("=" * 70)
    print("准备假项目（不碰真产物）")
    print("=" * 70)
    fresh_fake(fake, tool_path)
    print(f"  被验证的工具：{tool_path}")
    print(f"  夹具位置    ：{fake}")

    print()
    print("=" * 70)
    print("① 正常路径：源目录干净完整 → 应成功（dist/ 不存在，直接新建）")
    print("=" * 70)
    make_source(fake)
    run_tool(fake_tool, 0, "dist/ 已刷新", "首次刷新（新建 dist/）")

    print()
    print("=" * 70)
    print("② 正常路径：再跑一次 → 应走「旧 dist 改名留档」这条路")
    print("=" * 70)
    run_tool(fake_tool, 0, "已改名挪到", "二次刷新（旧 dist 改名成 .stale-*）")
    stale = sorted(d for d in os.listdir(fake) if d.startswith("dist.stale-"))
    print(f"      实际看到的 dist.stale-* ：{stale or '（没有！）'}")
    check(len(stale) == 1, "旧 dist 被**改名留档**而不是删除", len(stale), 1)
    check(os.path.isfile(os.path.join(fake, "dist", "SwitchCfwWizard.exe")),
          "新 dist/ 里有 exe")

    print()
    print("=" * 70)
    print("③ 拒绝路径 A：源目录不存在")
    print("=" * 70)
    shutil.rmtree(os.path.join(fake, "publish"))
    run_tool(fake_tool, 1, "源目录不存在", "源目录不存在 → 退出码 1 并点名")

    print()
    print("=" * 70)
    print("④ 拒绝路径 B：源目录里混进了运行痕迹")
    print("=" * 70)
    make_source(fake, extra=["settings.json"])
    run_tool(fake_tool, 1, "运行痕迹", "源目录有 settings.json → 退出码 1 并点名")

    print()
    print("=" * 70)
    print("⑤ 拒绝路径 C：不是自包含产物")
    print("=" * 70)
    make_source(fake, rc=RC_FRAMEWORK_DEPENDENT)
    run_tool(fake_tool, 1, "不是「自包含」产物", "框架依赖版 → 退出码 1 并点名")

    print()
    print("=" * 70)
    print("⑥ 拒绝路径 D：脚本放错位置（算不出项目根）")
    print("=" * 70)
    # 把脚本放到 `fake/lonely/tools/` 里 —— 让 refresh-dist.py 算出来的「项目根」是 `fake/lonely`，
    # 那里**明确没有** src/SwitchCfwWizard，于是它必然报「算不出项目根」。
    # ⚠️ 别图省事放成 `fake` 的兄弟目录（`fake + "-lonely"`）：那样项目根会算到 scratch，
    # 而 scratch 里**有没有** src/SwitchCfwWizard 取决于别的测试留了什么 ⇒ 这条断言的成立与否
    # 会被外部环境左右（做注入验证时就真的被带偏过）。
    lonely = os.path.join(fake, "lonely", "tools")
    safe_rmtree(os.path.join(fake, "lonely"))
    os.makedirs(lonely)
    shutil.copy2(tool_path, os.path.join(lonely, "refresh-dist.py"))
    p = subprocess.run([sys.executable, os.path.join(lonely, "refresh-dist.py")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = (p.stdout or "") + (p.stderr or "")
    check(p.returncode == 2 and "项目根" in out,
          "放错位置 → 退出码 2 并说明原因", p.returncode, 2)
    if not (p.returncode == 2 and "项目根" in out):
        print("      ---- 输出尾部 ----")
        for line in out.splitlines()[-8:]:
            print("      " + line)

    print()
    print("=" * 70)
    print("⑦ 拒绝路径 E：--work-dir 指向真项目根（防误删整个项目）")
    print("=" * 70)
    # 造一个「长得像真项目根」的目录：有 src/SwitchCfwWizard/*.csproj。
    # 放在夹具内部 ⇒ 每次 fresh_fake 会连它一起清掉，不留垃圾。
    danger = os.path.join(fake, "danger-proj")
    os.makedirs(os.path.join(danger, "src", "SwitchCfwWizard"))
    marker = os.path.join(danger, "src", "SwitchCfwWizard", "SwitchCfwWizard.csproj")
    open(marker, "w", encoding="utf-8").write("<Project Sdk=\"Microsoft.NET.Sdk\" />\n")
    p = subprocess.run([sys.executable, os.path.abspath(__file__), "--work-dir", danger],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = (p.stdout or "") + (p.stderr or "")
    check(p.returncode == 2 and "拒绝把夹具放在" in out,
          "work-dir 指向真项目根 → 退出码 2 并说明原因", p.returncode, 2)
    check(os.path.isfile(marker),
          "危险目录**一个文件都没少**（护栏拦在动手之前，不是拦在删完之后）")
    if not (p.returncode == 2 and "拒绝把夹具放在" in out):
        print("      ---- 输出尾部 ----")
        for line in out.splitlines()[-8:]:
            print("      " + line)

    print()
    print("=" * 70)
    bad = [r for r in results if not r[0]]
    print(f"共 {len(results)} 项，失败 {len(bad)} 项")
    for _, label, got, want in bad:
        print(f"  ✗ {label}（得到 {got}，期望 {want}）")
    print("=" * 70)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
