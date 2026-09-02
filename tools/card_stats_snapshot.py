#!/usr/bin/env python3
"""从 Spire Codex A10 卡牌统计 CSV 生成运行时快照 src/Run/CardWinStats.cs：
每卡 id(Id.Entry) → 胜率差 = winRate − 角色中位（仅 picks≥1000 的高置信样本；Colorless 对整局通用）。
CardPickerAI.Evaluate 末尾加 clamp(胜率差×0.45, ±8) 数据驱动加成。
纯标准库。用法: python tools/card_stats_snapshot.py
"""
import csv
import statistics
import collections
from pathlib import Path

CSV = Path(r"D:\JAVA_WorkPlace\AIWithCombatSolver\.local\webstats\card_stats_a10.csv")
OUT = Path(r"D:\JAVA_WorkPlace\AIWithCombatSolver\CombatSolver\src\Run\CardWinStats.cs")

MIN_PICKS = 1000
K = 0.45
CAP = 8.0


def main() -> int:
    rows = list(csv.DictReader(open(CSV, encoding="utf-8")))
    by = collections.defaultdict(list)
    for r in rows:
        by[r["color"]].append(r)
    medians = {
        ch: statistics.median(float(r["winRate"]) for r in rs if int(r["picks"]) >= 500)
        for ch, rs in by.items()
        if sum(1 for r in rs if int(r["picks"]) >= 500) > 0
    }
    entries = []
    for ch, med in medians.items():
        for r in by[ch]:
            if int(r["picks"]) < MIN_PICKS:
                continue
            delta = float(r["winRate"]) - med
            bonus = max(-CAP, min(CAP, delta * K))
            entries.append((r["id"], round(bonus, 3), int(r["picks"]), ch))
    entries.sort()
    lines = [f'        ["{cid}"] = {b}f, // {ch} picks={p}' for cid, b, p, ch in entries]
    header = """// 由 tools/card_stats_snapshot.py 从 Spire Codex A10 真实对局统计生成（勿手改）。
// 每卡数据驱动加成 = clamp((winRate − 角色中位)×0.45, ±8)，picks≥1000 高置信才计。
using System;
using System.Collections.Generic;

namespace CombatSolver.Run;

internal static class CardWinStats
{
    /// <summary>卡牌 Id.Entry → 胜率差加成（已含角色中位与置信门槛）。</summary>
    internal static readonly Dictionary<string, float> BonusById = new(StringComparer.Ordinal)
    {
"""
    footer = """    };
}
"""
    OUT.write_text(header + "\n".join(lines) + "\n" + footer, encoding="utf-8")
    print(f"entries={len(entries)} medians={ {k: round(v,1) for k, v in medians.items()} } -> {OUT.name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
