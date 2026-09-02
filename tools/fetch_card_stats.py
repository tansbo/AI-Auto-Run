#!/usr/bin/env python3
"""抓取 Spire Codex A10 统计（真实对局聚合：winRate/pickRate/picks/pickByAct），存 .local/webstats/{kind}_stats_a10.csv。

用法: python tools/fetch_card_stats.py [cards|relics]
页面把每项统计以内嵌 JSON 行给出（id=Id.Entry，如 REFLECT / RUNIC_PYRAMID）。数据为社区提交的全部 A10 跑局聚合，
作为 CombatSolver 卡牌/遗物评分校准先验。纯标准库。
"""
import csv
import html
import re
import sys
import urllib.request
from pathlib import Path

KIND = sys.argv[1] if len(sys.argv) > 1 else "cards"
URL = "https://spire-codex.com/kor/leaderboards/metrics?bracket=a10" + ("" if KIND == "cards" else "&type=relics")
OUT = Path(r"D:\JAVA_WorkPlace\AIWithCombatSolver\.local\webstats") / f"{KIND}_stats_a10.csv"

ROW = re.compile(
    r'"id":"(?P<id>[A-Z0-9_]+)","upgraded":(?P<upgraded>\w+),'
    r'"name":".*?","color":"(?P<color>\w+)","type":".*?","rarity":".*?",'
    r'"score":(?P<score>-?\d+),"tier":"(?P<tier>\w+)","elo":(?P<elo>[\d.]+),'
    r'"winRate":(?P<win>[\d.]+),"pickRate":(?P<pick>[\d.]+),'
    r'"picks":(?P<picks>\d+),"wins":(?P<wins>\d+),"losses":(?P<losses>\d+),'
    r'"offered":(?P<offered>\d+),"picked":(?P<picked>\d+)')


def main() -> int:
    print(f"GET {URL}")
    req = urllib.request.Request(URL, headers={
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36",
        "Accept": "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8",
    })
    raw = urllib.request.urlopen(req, timeout=60).read().decode("utf-8", "replace")
    text = html.unescape(raw).replace('\\"', '"')
    rows = []
    for m in ROW.finditer(text):
        d = m.groupdict()
        rows.append({
            "id": d["id"], "upgraded": d["upgraded"], "color": d["color"],
            "tier": d["tier"], "score": int(d["score"]), "elo": float(d["elo"]),
            "winRate": float(d["win"]), "pickRate": float(d["pick"]),
            "picks": int(d["picks"]), "wins": int(d["wins"]),
            "losses": int(d["losses"]), "offered": int(d["offered"]),
            "picked": int(d["picked"]),
        })
    if not rows:
        print("no rows matched — page structure changed")
        return 1
    OUT.parent.mkdir(parents=True, exist_ok=True)
    with OUT.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)
    print(f"rows={len(rows)} colors={sorted({r['color'] for r in rows})} "
          f"total_picks={sum(r['picks'] for r in rows)} -> {OUT.name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
