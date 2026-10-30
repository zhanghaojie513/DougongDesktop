#!/usr/bin/env python3
"""从 dsh 源码生成 dsh 的配色 token。

用法：
    python scripts/sync-dsh-tokens.py <dsh-src-root>

例：
    python scripts/sync-dsh-tokens.py C:/work/myProject/dsh-src

**为什么要脚本而不是手抄**：
1. 108 个 token × 2 套主题 = 216 个值。手抄必然错，而且错的那几个
   只有在特定组件上才看得出来。
2. dsh 处于 developer preview，README 原话是 "THERE WILL BE
   COMPATIBILITY-BREAKING CHANGES"。配色会变，手抄的版本没法跟着更新。
3. `color-mix()` 与 `var()` 需要求值。这一步手算更容易错——
   尤其 `color-mix(in srgb, A p%, B)` 与 `color-mix(in srgb, A, B p%)`
   的权重语义是**相反的**（前者 A 占 p%，后者 B 占 p%）。

生成物是 `src/DougongDesktop.Ui.Core/Theme/DshTokens.g.cs`，不要手改。
"""

import json
import re
import sys
from pathlib import Path

CSS_RELATIVE = "packages/client/ui-theme/src/styles/design-platform.css"


def parse_rgb(text):
    """解析 rgb()/rgba()，支持空格与斜杠写法。返回 (a, r, g, b)。"""
    text = text.strip()

    m = re.match(r"rgb\(\s*(\d+)\s+(\d+)\s+(\d+)\s*/\s*([\d.]+)%\s*\)", text)
    if m:
        return (
            round(float(m.group(4)) * 255 / 100),
            int(m.group(1)),
            int(m.group(2)),
            int(m.group(3)),
        )

    m = re.match(r"rgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)", text)
    if m:
        return (255, int(m.group(1)), int(m.group(2)), int(m.group(3)))

    m = re.match(r"rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([\d.]+)\s*\)", text)
    if m:
        alpha = float(m.group(4))
        a = round(alpha * 255) if alpha <= 1 else round(alpha)
        return (a, int(m.group(1)), int(m.group(2)), int(m.group(3)))

    return None


def resolve(value, table, depth=0):
    """把 var() / color-mix() 求值成具体 (a,r,g,b)。求不出返回 None。"""
    if depth > 8:
        return None

    value = value.strip()

    if value in ("transparent", "none"):
        return (0, 0, 0, 0)
    if value == "white":
        return (255, 255, 255, 255)
    if value == "black":
        return (0, 0, 0, 255)

    m = re.fullmatch(r"var\(\s*(--dsw-[a-z0-9-]+)\s*\)", value)
    if m:
        raw = table.get(m.group(1))
        return resolve(raw, table, depth + 1) if raw else None

    # 语法 A：color-mix(in srgb, A p%, B) —— A 占 p%
    m = re.match(r"color-mix\(in srgb,\s*(.+?)\s+([\d.]+)%\s*,\s*(.+?)\)\s*$", value)
    if m:
        a = resolve(m.group(1), table, depth + 1)
        weight = float(m.group(2)) / 100
        b = resolve(m.group(3), table, depth + 1)
        if a and b:
            return tuple(round(x * weight + y * (1 - weight)) for x, y in zip(a, b))
        return None

    # 语法 B：color-mix(in srgb, A, B p%) —— B 占 p%（与 A 相反！）
    m = re.match(r"color-mix\(in srgb,\s*(.+?)\s*,\s*(.+?)\s+([\d.]+)%\s*\)\s*$", value)
    if m:
        a = resolve(m.group(1), table, depth + 1)
        weight = float(m.group(3)) / 100
        b = resolve(m.group(2), table, depth + 1)
        if a and b:
            return tuple(round(y * weight + x * (1 - weight)) for x, y in zip(a, b))
        return None

    return parse_rgb(value)


def extract(css_text):
    """从 CSS 里切出 static / light alias / dark alias 三段。"""
    lines = css_text.split("\n")

    def grab(lo, hi, prefix):
        out = {}
        for line in lines[lo:hi]:
            m = re.match(r"\s*(--dsw-[a-z0-9-]+)\s*:\s*(.+?);\s*$", line)
            if m and m.group(1).startswith(prefix):
                out[m.group(1)] = m.group(2)
        return out

    # 文件里有**四个** body 块，顺序是：
    #   1. body { ...static 浅色... }
    #   2. body[data-ds-dark-theme] { ...static 暗色覆盖... }
    #   3. body { ...alias 浅色... }          ← 这里才是 light alias
    #   4. body[data-ds-dark-theme] { ...alias 暗色... }
    #
    # 踩过的坑：把第 2 块当成 dark alias 的起点，结果 light 抓到 0 个 token——
    # 因为第 1、2 块里根本没有 alias 定义，全在 static 段。
    blocks = [
        (i, l.strip())
        for i, l in enumerate(lines)
        if l.strip() in ("body {", "body[data-ds-dark-theme] {")
    ]

    alias_blocks = [
        (start, end) for (start, _), (end, _) in zip(blocks, blocks[1:] + [(len(lines), "")])
        if any(re.match(r"\s*--dsw-alias-", l) for l in lines[start:end])
    ]

    if len(alias_blocks) != 2:
        raise SystemExit(
            f"期望 2 个含 alias 的块（light / dark），实际 {len(alias_blocks)} 个"
        )

    static = grab(0, blocks[0][0] if False else alias_blocks[0][0], "--dsw-static-")
    # static 的暗色覆盖也要并进来（第 2 块），因为 dark alias 会引用它
    static.update(grab(blocks[0][0], alias_blocks[0][0], "--dsw-static-"))

    alias_light = grab(alias_blocks[0][0], alias_blocks[0][1], "--dsw-alias-")
    alias_dark = grab(alias_blocks[1][0], alias_blocks[1][1], "--dsw-alias-")

    if not (alias_light and alias_dark):
        raise SystemExit(f"alias 抽取失败：light {len(alias_light)}，dark {len(alias_dark)}")

    return static, alias_light, alias_dark


def to_member(token):
    """--dsw-alias-bg-layer-1 → DswAliasBgLayer1"""
    parts = token.replace("--dsw-", "").split("-")
    return "Dsw" + "".join(p.capitalize() for p in parts)


HEADER = '''using Avalonia.Media;

namespace DougongDesktop.Ui.Core.Theme;

/// <summary>
/// dsh 的 <c>--dsw-alias-*</c> 语义 token，light / dark 各一套。
/// </summary>
/// <remarks>
/// <b>本文件是生成物，不要手改。</b>
/// 由 <c>scripts/sync-dsh-tokens.py</c> 从 dsh 的
/// <c>packages/client/ui-theme/src/styles/design-platform.css</c> 生成。
/// 配色有变请重跑脚本，手改会在下次生成时丢失。
/// <para>
/// <b>数值已把 <c>var()</c> 与 <c>color-mix()</c> 求值成具体色值</b>——运行期不做混色，
/// 避免每帧算一遍。求值时的两个坑：
/// <list type="bullet">
/// <item><c>color-mix(in srgb, A p%, B)</c> 中 <b>A 占 p%</b></item>
/// <item><c>color-mix(in srgb, A, B p%)</c> 中 <b>B 占 p%</b>（与上一条相反）</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么不用 XAML ResourceDictionary</b>：这些值要按 light/dark 在代码里切换，
/// 强类型常量拼错在编译期就报；字符串查资源拼错要等运行期。
/// 给 Arc 用时再转 <c>IBrush</c>。
/// </para>
/// </remarks>
public static class DshTokens
{
'''


def build(resolved):
    out = [HEADER]

    for theme in ("light", "dark"):
        values = resolved[theme]
        out.append(f"    /// <summary>dsh 的 {theme} 配色（{len(values)} 个 token）。</summary>\n")
        out.append(f"    public static class {theme.capitalize()}\n    {{\n")

        for token in sorted(values):
            a, r, g, b = values[token]
            out.append(
                f'        public static readonly Color {to_member(token)} = '
                f'Color.Parse("#{a:02X}{r:02X}{g:02X}{b:02X}");\n'
            )

        out.append("    }\n\n")

    out.append("}\n")
    return "".join(out)


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)

    root = Path(sys.argv[1])
    css_path = root / CSS_RELATIVE
    if not css_path.exists():
        raise SystemExit(f"找不到 {css_path}")

    static, alias_light, alias_dark = extract(css_path.read_text(encoding="utf-8"))

    resolved = {}
    for name, alias in (("light", alias_light), ("dark", alias_dark)):
        table = {**static, **alias}
        values, missing = {}, []
        for token, raw in alias.items():
            parsed = resolve(raw, table)
            if parsed:
                values[token] = parsed
            else:
                missing.append((token, raw))

        if missing:
            # 不求值就静默跳过会让生成的配色缺几档，而缺的那几档只在
            # 个别组件上看出问题。宁可失败。
            for token, raw in missing:
                print(f"  ✗ [{name}] {token} = {raw}", file=sys.stderr)
            raise SystemExit(f"{name}: {len(missing)} 个 token 求值失败")

        resolved[name] = values
        print(f"{name}: {len(values)}/{len(alias)} 个 token")

    out_path = Path(__file__).resolve().parent.parent / (
        "src/DougongDesktop.Ui.Core/Theme/DshTokens.g.cs"
    )
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(build(resolved), encoding="utf-8")
    print(f"已写入 {out_path}")


if __name__ == "__main__":
    main()
