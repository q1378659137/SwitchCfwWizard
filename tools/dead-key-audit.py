#!/usr/bin/env python3
"""语言包死键审计：从**真源**（语言包键集 + src/ 的引用点）算出哪些键没人引用。

用法：
    python tools/dead-key-audit.py            # 只报告
    python tools/dead-key-audit.py --json     # 机器可读输出（给删键脚本用）

判据：
  * 键的来源 = Strings.zh-Hans.json 的键集（真源，不手抄）。
  * 「被引用」= 该键以**带引号的字符串字面量**出现在 src/ 的 .cs/.xaml 里，
    或出现在 XAML 的 `{loc:Loc <键>}` 里。
  * **动态拼键**（`"Category." + category`、`"Component." + kind + ".Name"` 这类）
    的「被引用」是**编译期看不见**的，所以单独放行。放行清单**从源码现取**
    （见 `dynamic_prefixes()`），不手抄 —— 否则新增一个拼键函数就会静默多出一批
    「可删死键」，而**没有任何断言会红**。

⚠️ 与回归的分工（2026-09-18 查明并修好）：
  查死键这件事**回归里已经有常驻护栏** `ConfigSmokeTest.CheckNoDeadLanguageKeys()`，
  而且它是**对的**（用 `ComponentCatalog.CategoryTitleKey` 本身生成动态前缀）。
  本脚本的独特价值只有「**给 remove-dead-keys.py 供待删清单**」——
  因为回归只能报、不能删。
  ⚠️ 历史坑：`Category.*` 那 7 个键是 `CategoryTitleKey` 动态拼的，而本脚本的放行清单
  当时只写死了 `"Component."` ⇒ 它把这 7 个**界面分组标题**报成了「可删死键」，
  而 `remove-dead-keys.py --apply` 会真的照删 —— 界面上 7 个组标题会变成
  `Category.Framework` 这样的原始键。**回归全绿、脚本说可删，两边对同一件事给出相反结论**，
  谁也没红。这就是放行清单必须现取、且删除类工具必须偏保守的理由。
"""

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src" / "SwitchCfwWizard"
PACK = SRC / "Localization" / "Strings.zh-Hans.json"


def source_files():
    """src/ 下所有 .cs / .xaml（跳过 bin/obj —— 那里也有 .cs，如自动生成的 AssemblyInfo）。"""
    for path in list(SRC.rglob("*.cs")) + list(SRC.rglob("*.xaml")):
        parts = set(path.parts)
        if "bin" in parts or "obj" in parts:
            continue
        yield path


# 动态拼键的形态：`"前缀." + 变量`。实测全仓库只有三处，都是这个形状：
#     ComponentCatalog.CategoryTitleKey   => "Category." + category
#     ComponentCatalog.…                  => "Component." + definition.Kind + ".Name"
#     ComponentCatalog.…                  => "Component." + definition.Kind + ".Desc"
# 刻意只认「引号内就是一个纯前缀 + 点、后面紧跟 + 」—— 这样 `"https://" + host` 之类不会中。
PREFIX_PATTERN = re.compile(r'"([A-Za-z][A-Za-z0-9_]*)\.\s*"\s*\+')


def dynamic_prefixes():
    """从源码现取动态拼键的前缀集合（不手抄 —— 手抄的清单一定会腐烂）。"""
    prefixes = set()
    for path in source_files():
        text = path.read_text(encoding="utf-8", errors="replace")
        prefixes |= set(PREFIX_PATTERN.findall(text))
    return prefixes


def collect_literals():
    """扫 src/ 下所有 .cs / .xaml，返回出现过的字面量集合。"""
    literals = set()
    for path in source_files():
        text = path.read_text(encoding="utf-8", errors="replace")
        # 双引号字符串字面量（够用：这些键里没有转义引号）
        for m in re.finditer(r'"([^"\r\n]*)"', text):
            literals.add(m.group(1))
        # XAML 的 {loc:Loc Some.Key}
        for m in re.finditer(r"loc:Loc\s+([A-Za-z0-9_.]+)", text):
            literals.add(m.group(1))
    return literals


def main():
    keys = list(json.loads(PACK.read_text(encoding="utf-8")).keys())
    literals = collect_literals()

    prefixes = dynamic_prefixes()
    if not prefixes:
        # 正则一旦失效，全部动态键都会被当成「可删」—— 那是会删掉界面文案的静默事故。
        print("!! 没能从 src/ 里解析出任何「动态拼键前缀」—— PREFIX_PATTERN 失效了？")
        print("   期望形态： \"Category.\" + category   /   \"Component.\" + kind + \".Name\"")
        print("   在查清之前**不要**用 --json 的结果去删键。")
        return 2

    unreferenced = [k for k in keys if k not in literals]
    dynamic = [k for k in unreferenced if any(k.startswith(p + ".") for p in prefixes)]
    rest = [k for k in unreferenced if k not in dynamic]

    # 兜底（删除类工具必须偏保守）：某个键虽没命中放行清单，但它的前缀在语言包里
    # **成组出现**（≥2 个）⇒ 更像是「我们漏掉的动态家族」，而不是「一堆没人引用的键」。
    # 判给「疑似」而不是「可删」—— 漏报一个真死键只是留个尾巴，误删一组界面文案是事故。
    def prefix_of(k):
        return k.split(".", 1)[0] if "." in k else ""

    counts = {}
    for k in keys:
        counts[prefix_of(k)] = counts.get(prefix_of(k), 0) + 1

    suspected = [k for k in rest if counts.get(prefix_of(k), 0) >= 2]
    dead = [k for k in rest if k not in suspected]

    if "--json" in sys.argv:
        # 只输出**真死键** —— 疑似项绝不进待删清单。
        print(json.dumps(dead, ensure_ascii=False))
        return 0

    print(f"语言包 {PACK.name}：{len(keys)} 个键")
    print(f"  src/ 引用   ：{len(keys) - len(unreferenced)}")
    print(f"  未被引用    ：{len(unreferenced)}")
    print(f"    ├ 动态拼键（放行）：{len(dynamic)}   前缀：{sorted(p + '.' for p in prefixes)}")
    for k in dynamic:
        print(f"    │   {k}")
    if suspected:
        print(f"    ├ 疑似动态家族（**不放行、也不算可删**）：{len(suspected)}")
        print(f"    │   ⚠️ 这些键没命中放行清单，但同前缀在语言包里成组出现 ——")
        print(f"    │      先查清是不是新的动态拼键（漏放行），别直接删。")
        for k in suspected:
            print(f"    │   {k}")
    print(f"    └ 死键（可删）    ：{len(dead)}")
    for k in dead:
        print(f"        {k}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
