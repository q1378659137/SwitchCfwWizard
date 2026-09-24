"""把一次 `SwitchCfwWizard.exe --run` 的结果整理成证据报告（跑完再执行）。

为什么需要它：e2e 的**判据是退出码**，但退出码只说「没炸」，说不出「落对地方了没有」。
这份报告把「跑完之后磁盘上到底有什么」固定成文本，好让每轮 e2e 的结论可复核 ——
尤其是三件只有看磁盘才能确认的事：

  ① `out/` 顶层是不是 SD 卡根名（出现别的东西 = 多了一层包装目录，落点全错）；
  ② 声明的落点是不是真的命中（`--expect`）；
  ③ **「按语言选包」到底选了哪个** —— 只有把落盘文件与候选包逐字节比才作数
     （就算包名写错，容错瀑布「猜中」也会让 e2e 变绿，只在日志里留一行痕）。

用法：

    python tools/e2e-evidence.py <exe 目录> <输出 .md> [选项]

    --label 名字              报告标题里的场景名（默认取 exe 目录名）
    --cache 目录              包缓存目录；给了才能做 `--bytecheck`
    --expect 相对路径         必须存在的落盘文件（可重复）
    --absent 相对路径         必须**不**存在的路径（可重复）—— 用来逮「多了一层包装目录」
    --bytecheck 落盘路径=包1,包2
                              把落盘文件与候选压缩包里的同名条目逐字节比（可重复）
    --max-list N              落盘清单最多列几条（默认 60，其余只给计数）

例：

    python tools/e2e-evidence.py "src/SwitchCfwWizard/bin/Debug/net8.0-windows" \\
        .agent/scratch/e2e-study.md --label "学习 7 个" --cache .agent/scratch/zips \\
        --expect switch/wiliwili/wiliwili.nro \\
        --absent switch/wiliwili/wiliwili/wiliwili.nro \\
        --expect switch/Switch_90DNS_tester/Switch_90DNS_tester.nro \\
        --bytecheck switch/linkalho/linkalho.nro=linkalho.zip,linkalho-v2.0.2.zip
"""

import argparse
import hashlib
import os
import sys
import zipfile

# SD 卡根目录名。out/ 顶层出现别的名字 = 多了一层包装目录。
SD_ROOT_NAMES = {
    "atmosphere", "bootloader", "switch", "config", "themes", "SaltySD",
    "warmboot", "payloads", "hbmenu.nro", "boot.dat", "Nintendo",
}

# out/ 顶层**预期会出现**的、非 SD 卡根名的东西（向导自己的产物）。
# 不加进 SD_ROOT_NAMES 是因为它确实不是 SD 根名 —— 只是它的出现有正当理由，
# 报告里要把它标出来说明，而不是当成「多了一层包装目录」报假警。
EXPECTED_NON_ROOT = {
    ".wizard-manifest.json": "向导自己写的产物清单（不是 SD 卡内容）",
}


def count_files(root):
    n = 0
    for _, _, files in os.walk(root):
        n += len(files)
    return n


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser(description="把一次 e2e 的结果整理成证据报告")
    ap.add_argument("exe_dir")
    ap.add_argument("out_md")
    ap.add_argument("--label", default=None)
    ap.add_argument("--cache", default=None)
    ap.add_argument("--expect", action="append", default=[])
    ap.add_argument("--absent", action="append", default=[])
    ap.add_argument("--bytecheck", action="append", default=[])
    ap.add_argument("--max-list", type=int, default=60)
    args = ap.parse_args()

    exe_dir = os.path.abspath(args.exe_dir)
    out_dir = os.path.join(exe_dir, "out")
    run_log = os.path.join(exe_dir, "run.log")
    label = args.label or os.path.basename(exe_dir)
    problems = []

    L = []
    w = L.append
    w(f"# {label} —— 真实联网 e2e 证据")
    w("")
    w(f"- exe 目录：`{exe_dir}`")
    w(f"- 报告生成时间：见文件 mtime（本脚本不写时间戳，避免每次 diff 都变）")
    w("")

    # ── 一、run.log ──────────────────────────────────────────────
    w("## 一、`run.log`")
    w("")
    if os.path.exists(run_log):
        text = open(run_log, encoding="utf-8", errors="replace").read()
        errors = [l for l in text.splitlines() if "[Error]" in l]
        warnings = [l for l in text.splitlines() if "[Warning]" in l]
        w(f"- **{len(errors)} 个 Error**、{len(warnings)} 个 Warning")
        if errors:
            problems.append(f"run.log 里有 {len(errors)} 个 Error")
        for l in errors[:15]:
            w(f"  - ⚠️ Error: {l.strip()}")
        if warnings:
            w("- Warning 明细（多数是「直链 502 → 改用 API asset 端点」的降级链，属环境噪声）：")
            for l in warnings[:25]:
                w(f"  - {l.strip()}")
    else:
        problems.append("run.log 不存在 —— e2e 可能没跑起来")
        w("- ⚠️ `run.log` 不存在")
    w("")

    # ── 二、out/ 顶层 ────────────────────────────────────────────
    w("## 二、`out/` 顶层（必须是 SD 卡根名）")
    w("")
    if os.path.isdir(out_dir):
        tops = sorted(os.listdir(out_dir))
        w(f"- 文件总数：**{count_files(out_dir)}**")
        bad = [t for t in tops if t not in SD_ROOT_NAMES and t not in EXPECTED_NON_ROOT]
        for t in tops:
            if t in bad:
                mark = "  ⚠️ 不是 SD 根名"
            elif t in EXPECTED_NON_ROOT:
                mark = f"  （预期：{EXPECTED_NON_ROOT[t]}）"
            else:
                mark = ""
            w(f"  - `{t}`{mark}")
        if bad:
            problems.append(f"out/ 顶层出现非 SD 根名：{bad} —— 可能多了一层包装目录")
    else:
        problems.append("out/ 不存在 —— 说明这次跑**拒绝了生成配置**（配额/网络失败保护）")
        w("- ⚠️ `out/` 不存在 —— 这次跑**拒绝了生成配置**（失败保护生效，不是 bug）")
    w("")

    # ── 三、落盘清单 ─────────────────────────────────────────────
    w(f"## 三、落盘清单（最多 {args.max_list} 条）")
    w("")
    if os.path.isdir(out_dir):
        shown = 0
        for root, dirs, files in os.walk(out_dir):
            dirs.sort()
            for f in sorted(files):
                if shown >= args.max_list:
                    break
                rel = os.path.relpath(os.path.join(root, f), out_dir).replace("\\", "/")
                w(f"- `{rel}`")
                shown += 1
            if shown >= args.max_list:
                break
        total = count_files(out_dir)
        if total > shown:
            w(f"- …（另有 {total - shown} 条未列）")
    w("")

    # ── 四、声明的落点有没有命中 ─────────────────────────────────
    w("## 四、关键落点（`--expect`）")
    w("")
    if not args.expect:
        w("- （没给 `--expect`）")
    for rel in args.expect:
        p = os.path.join(out_dir, rel.replace("/", os.sep))
        if os.path.exists(p):
            w(f"- ✅ `{rel}`（{os.path.getsize(p)} 字节）")
        else:
            problems.append(f"落点缺失：{rel}")
            w(f"- ⚠️ **`{rel}` 不存在**")
    w("")

    # ── 四点五、不该存在的东西（多一层包装目录 = 静默失败）─────────
    w("## 四点五、不该存在的路径（`--absent`）")
    w("")
    if not args.absent:
        w("- （没给 `--absent`）")
    for rel in args.absent:
        p = os.path.join(out_dir, rel.replace("/", os.sep))
        if os.path.exists(p):
            problems.append(f"出现了不该存在的路径：{rel} —— 多半是包装目录没剥干净")
            w(f"- ⚠️ **`{rel}` 居然存在** —— 包装目录没剥干净（落点全错，但没有任何报错）")
        else:
            w(f"- ✅ `{rel}` 不存在（符合预期）")
    w("")

    # ── 五、按语言选包：逐字节比 ─────────────────────────────────
    w("## 五、「按语言选包」的端到端核对（只有比字节才作数）")
    w("")
    if not args.bytecheck:
        w("- （没给 `--bytecheck`）")
    for spec in args.bytecheck:
        if "=" not in spec:
            w(f"- ⚠️ `--bytecheck` 参数格式应为 `落盘路径=包1,包2`，收到 `{spec}`")
            problems.append(f"--bytecheck 参数格式错：{spec}")
            continue
        rel, zips = spec.split("=", 1)
        zips = [z.strip() for z in zips.split(",") if z.strip()]
        landed = os.path.join(out_dir, rel.replace("/", os.sep))
        if not os.path.exists(landed):
            problems.append(f"--bytecheck 的落盘文件不存在：{rel}")
            w(f"- ⚠️ `{rel}` 不存在 —— 跳过")
            continue
        h = sha256(landed)
        w(f"- 落盘 `{rel}` sha256 = `{h}`（{os.path.getsize(landed)} 字节）")
        if not args.cache:
            w("  - 没给 `--cache`，无法比对（**这一项没验**）")
            problems.append(f"{rel} 没做字节比对（缺 --cache）")
            continue
        hit = None
        for z in zips:
            zp = os.path.join(args.cache, z)
            if not os.path.exists(zp):
                w(f"  - `{z}` 不在缓存里，无法比对")
                continue
            try:
                with zipfile.ZipFile(zp) as f:
                    inner = f.read(rel)
            except KeyError:
                w(f"  - `{z}` 里没有 `{rel}`")
                continue
            except zipfile.BadZipFile:
                w(f"  - `{z}` 不是压缩包（散装资源？）")
                continue
            zh = hashlib.sha256(inner).hexdigest()
            ok = zh == h
            if ok:
                hit = z
            w(f"  - `{z}` 内同名条目 sha256 = `{zh}`{ '  ← **命中**' if ok else ''}")
        if hit is None:
            problems.append(f"{rel} 与任何候选包都对不上 —— 「按语言选包」可能没生效")
    w("")

    # ── 结论 ────────────────────────────────────────────────────
    w("## 结论")
    w("")
    if problems:
        w(f"**{len(problems)} 项待查：**")
        for p in problems:
            w(f"- ⚠️ {p}")
    else:
        w("**全部核对通过**：无 Error、落点命中、语言选包逐字节正确。")
    w("")

    with open(args.out_md, "w", encoding="utf-8") as f:
        f.write("\n".join(L) + "\n")
    print(f"已写入 {args.out_md}（{len(L)} 行，{len(problems)} 项待查）")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
