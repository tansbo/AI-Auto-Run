#!/usr/bin/env python3
"""分析 Spire Codex A10 卡牌统计：各角色胜率中位/分布 + 高置信样本的胜率差候选加成。
"""
import csv
import statistics
import collections
from pathlib import Path

CSV = Path(r"D:\JAVA_WorkPlace\AIWithCombatSolver\.local\webstats\card_stats_a10.csv")


def main() -> int:
    rows = list(csv.DictReader(open(CSV, encoding="utf-8")))
    by = collections.defaultdict(list)
    for r in rows:
        by[r["color"]].append(r)

    for ch, rs in sorted(by.items()):
        big = [r for r in rs if int(r["picks"]) >= 500]
        wins = [float(r["winRate"]) for r in big]
        if not wins:
            continue
        med = statistics.median(wins)
        top = sorted(big, key=lambda r: float(r["winRate"]), reverse=True)[:3]
        bot = sorted(big, key=lambda r: float(r["winRate"]))[:3]
        print(f"== {ch}: n={len(rs)} highconf={len(big)} median={med:.1f}%")
        for t in top:
            print(f"   TOP {t['id']} win={t['winRate']}% pick={t['pickRate']}% picks={t['picks']} tier={t['tier']}")
        for b in bot:
            print(f"   BOT {b['id']} win={b['winRate']}% pick={b['pickRate']}% picks={b['picks']} tier={b['tier']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
