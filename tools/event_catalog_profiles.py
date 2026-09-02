#!/usr/bin/env python3
"""把 .local/event-catalog/all-events.json 目录生成运行时 C# 档案表 src/Run/EventOptionProfiles.cs。

每个带 textKey 的选项产出一条档案：事件类名 + 代价/收益列表（kind + 解析出的数值/详情）。
运行时 EventOptionValuer 按 option.TextKey 查表，用统一换算（掉血/失最大生命/诅咒/金币…为负，
遗物/卡/金币/治疗/升级…为正；随机项按稀有度期望常数）做**收益-代价综合评估**。
数值口径集中在本文件头的 ProfileScore 换算函数，随跑局数据校准。
纯标准库；用法: python tools/event_catalog_profiles.py
"""
import json
import re
from pathlib import Path

CATALOG = Path(r"D:\JAVA_WorkPlace\AIWithCombatSolver\.local\event-catalog\all-events.json")
OUT = Path(r"D:\JAVA_WorkPlace\AIWithCombatSolver\CombatSolver\src\Run\EventOptionProfiles.cs")

NUMS = re.compile(r"-?\d+(?:\.\d+)?")


def cs(s):
    """C# 字符串转义。"""
    if s is None:
        return "null"
    return json.dumps(str(s))  # JSON 转义与 C# 对引号/反斜杠兼容

def first_number(*texts):
    for t in texts:
        if t is None:
            continue
        m = NUMS.search(str(t))
        if m:
            return float(m.group())
    return None

def parse_amount(kind, detail, amount):
    """尽量把选项的代价/收益数量解析成单个数；范围取中值；百分比保留标记。"""
    if amount is not None and isinstance(amount, (int, float)):
        return float(amount)
    d = str(detail or "")
    if kind in ("losePercentHp", "healPercent"):
        p = first_number(d, amount)
        return p if p is not None else None
    nums = [float(x) for x in NUMS.findall(d)]
    if not nums:
        # amount 字段里的字面
        a = first_number(amount)
        return a
    if len(nums) == 1:
        return nums[0]
    # 范围/多个数字：取首尾中点（如 60±8、45–75、3…每次+1）
    if len(nums) >= 2 and max(nums) - min(nums) <= 200:
        return (min(nums) + max(nums)) / 2.0
    return nums[0]

def emit():
    data = json.loads(CATALOG.read_text(encoding="utf-8"))
    records = sorted(data["records"], key=lambda r: r.get("eventClass", ""))
    lines = []
    total_opts = 0
    emitted = 0
    skipped_dupes = 0
    seen = set()
    for rec in records:
        cls = rec.get("eventClass", "?")
        for opt in rec.get("options", []):
            total_opts += 1
            key = opt.get("textKey")
            if not key:
                continue
            if key in seen:
                skipped_dupes += 1  # 通用选项 key（如 PROCEED）跨事件重复：首个事件优先
                continue
            seen.add(key)
            costs = []
            for c in opt.get("costs") or []:
                amt = parse_amount(c.get("kind"), c.get("detail"), c.get("amount"))
                costs.append(
                    f"new P({cs(c.get('kind') or 'other')}, {(amt if amt is not None else 'null')}, "
                    f"{cs(c.get('detail'))})")
            outs = []
            for b in opt.get("outcome") or []:
                amt = parse_amount(b.get("kind"), b.get("detail"), b.get("amount"))
                outs.append(
                    f"new P({cs(b.get('kind') or 'other')}, {(amt if amt is not None else 'null')}, "
                    f"{cs(b.get('detail'))})")
            rnd = opt.get("random")
            rarity = (rnd or {}).get("rarity") if rnd else None
            rar = f'"{rarity}"' if rarity else "null"
            det = "true" if opt.get("deterministic") else "false"
            lines.append(f'    {{ "{key}", new Profile("{cls}", {det}, {rar}, new P[] {{ {", ".join(costs)} }}, new P[] {{ {", ".join(outs)} }}) }},')
            emitted += 1

    header = """using System;
using System.Collections.Generic;

namespace CombatSolver.Run;

/// <summary>
/// 事件选项运行时档案（由 tools/event_catalog_profiles.py 从 .local/event-catalog/all-events.json 生成，
/// 58 事件反编译目录：每选项 收益/代价 kind+数值）。事件驱动用它做"好处-负面效果"综合评估：
/// 价值换算见 EventOptionValuer.ProfileScore（掉血/失最大生命/诅咒/花钱为负，遗物/卡/金币/治疗为正，
/// 随机项按稀有度期望常数）。kind 全集：obtainRelic/obtainCard/randomCard/randomRelic/randomPotion/gold/
/// loseHp/losePercentHp/loseMaxHp/heal/maxHpGain/removeCard/transformCard/upgradeCard/curse/fight/other/leave。
/// </summary>
internal static partial class EventOptionProfiles
{
    internal sealed record Profile(
        string EventClass,
        bool Deterministic,
        string? RandomRarity,
        P[] Costs,
        P[] Outcomes);

    internal readonly record struct P(string Kind, double? Amount, string? Detail);

    internal static readonly Dictionary<string, Profile> ByTextKey = new(StringComparer.OrdinalIgnoreCase)
    {
"""
    body = "\n".join(lines)
    footer = """    };
}
"""
    OUT.write_text(header + body + "\n" + footer, encoding="utf-8")
    print(f"emitted={emitted}/{total_opts} options (skipped dup textKey={skipped_dupes}) -> {OUT.name}")

if __name__ == "__main__":
    emit()
