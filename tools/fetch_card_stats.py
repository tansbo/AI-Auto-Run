#!/usr/bin/env python3
"""抓取 Spire Codex A10 卡牌指标（真实对局聚合：winRate/pickRate/picks/pickByAct），存 .local/webstats/card_stats_a10.csv。

Spire Codex 页面把每卡统计以内嵌 JSON 行给出（id=卡牌 Id.Entry，如 REFLECT）。数据为社区提交的全部 A10 跑局聚合，
可作为 CombatSolver 选牌/遗物评分的校准先验（对应 CardPickerAI.KnownCardBonuses / 阈值）。
纯标准库。用法: python tools/fetch_card_stats.py
"""
import csv
import html
import json
import re
import urllib.request
from pathlib import Path

URL = "https://spire-codex.com/kor/leaderboards/metrics?bracket=a10"
OUT = Path(r"D:\JAVA_WorkPlace\AIWithCombatSolver\.local\webstats\card_stats_a10.csv")

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
    raw = urllib.request.urlopen(req, timeout=40).read().decode("utf-8", "replace")
    # 页面把对象包在多层转义里：\" -> "，html 实体还原后按行对象正则抓取。
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
    chars = sorted({r["color"] for r in rows})
    total_picks = sum(r["picks"] for r in rows)
    print(f"rows={len(rows)} chars={chars} total_picks={total_picks}")
    top = sorted(rows, key=lambda r: r["winRate"], reverse=True)[:5]
    for r in top:
        print(f"  {r['id']} {r['color']} win={r['winRate']}% pick={r['pickRate']}% picks={r['picks']}")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
