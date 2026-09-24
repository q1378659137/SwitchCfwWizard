#!/usr/bin/env python3
"""删掉三份语言包里的死键（逐行删除，其余字节不动）。

用法：
    python tools/remove-dead-keys.py            # 演练：只报告会删哪些行
    python tools/remove-dead-keys.py --apply    # 真删

为什么用逐行删除而不是「解析 → 删键 → 重新序列化」：
    重新序列化会把**整份文件**过一遍 JSON 写出器，转义、缩进、空行任何一点差异
    都会变成几千行的假 diff，真正的改动被淹没。逐行删除只动那 16 行。

两个必须处理的边界：
  1. 删掉的若是**对象最后一条**，前一行会留下多余的逗号 → JSON 语法错误（逐行删看不见）。
  2. 三份包的键集必须始终一致 —— 删完立刻互相比对。
"""

import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PACKS = [ROOT / "src" / "SwitchCfwWizard" / "Localization" / f"Strings.{c}.json"
         for c in ("zh-Hans", "zh-Hant", "en-US")]

APPLY = "--apply" in sys.argv


def dead_keys():
    """真源：由 dead-key-audit.py 从「语言包键集 − src/ 引用点」算出来，不手抄。"""
    out = subprocess.run(
        [sys.executable, str(ROOT / "tools" / "dead-key-audit.py"), "--json"],
        capture_output=True, text=True, check=True,
    )
    return json.loads(out.stdout)


def remove_lines(path, keys):
    text = path.read_text(encoding="utf-8")
    lines = text.split("\n")

    pattern = re.compile(r'^  "(' + "|".join(re.escape(k) for k in keys) + r')":')
    kept, removed = [], []
    for line in lines:
        m = pattern.match(line)
        if m:
            removed.append(m.group(1))
        else:
            kept.append(line)

    if not removed:
        return [], text

    # 边界 1：最后一条非 `}` 的行若以逗号结尾，说明它后面原本还有条目 —— 去掉那个逗号。
    # 只看 `}` 之前的最后一行；缩进 2 空格说明它是对象成员（不是嵌套结构的收尾）。
    for i in range(len(kept) - 1, -1, -1):
        stripped = kept[i].strip()
        if stripped in ("", "}", "]"):
            continue
        if kept[i].endswith(","):
            kept[i] = kept[i][:-1]
        break

    return removed, "\n".join(kept)


def main():
    keys = dead_keys()
    print(f"死键 {len(keys)} 个：{', '.join(keys)}\n")

    before = {}
    for path in PACKS:
        before[path.name] = set(json.loads(path.read_text(encoding="utf-8")).keys())

    results = {}
    for path in PACKS:
        removed, new_text = remove_lines(path, keys)
        results[path.name] = (removed, new_text)
        print(f"{path.name}：命中 {len(removed)} 行"
              + ("" if set(removed) == set(keys) else f"  ⚠️ 与死键集不一致：{set(keys) ^ set(removed)}"))

    if not APPLY:
        print("\n（演练模式，未写盘。加 --apply 真删）")
        return

    for path in PACKS:
        _, new_text = results[path.name]
        # 写前先验证：能解析、且键集正好少了那 16 个
        parsed = set(json.loads(new_text).keys())
        expected = before[path.name] - set(keys)
        assert parsed == expected, f"{path.name} 键集不符：多 {parsed - expected}，少 {expected - parsed}"
        assert new_text.endswith("}\n"), f"{path.name} 结尾不对：{new_text[-5:]!r}"

        path.write_text(new_text, encoding="utf-8", newline="")
        print(f"已写 {path.name}：{len(before[path.name])} → {len(parsed)} 个键")

    # 三份包的键集必须仍然完全一致
    after = [set(json.loads(p.read_text(encoding="utf-8")).keys()) for p in PACKS]
    assert after[0] == after[1] == after[2], "删完后三份语言包键集不一致"
    print(f"三份语言包键集一致：{len(after[0])} 个键")


if __name__ == "__main__":
    main()
