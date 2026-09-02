#!/usr/bin/env python3
"""聚合 run_telemetry 的遗物语料：每局一行（seed/character/floors/act/victory/rooms + 逗号分隔 relicIds）。
供"遗物×胜负"内部校准：样本足够后统计每件遗物在胜利/失败局的出现率差。

用法: python tools/aggregate_relic_corpus.py [telemetry_dir] [out_csv]
默认目录 %APPDATA%\\SlayTheSpire2\\run_telemetry；输出 .local/relic-corpus.csv。纯标准库。
"""
import csv
import json
import os
import sys
from pathlib import Path

TELEMETRY = Path(os.environ.get("APPDATA", r"C:\Users\19148\AppData\Roaming")) / "SlayTheSpire2" / "run_telemetry"
OUT = Path(r"D:\JAVA_WorkPlace\AIWithCombatSolver\.local\relic-corpus.csv")


def main() -> int:
    src = Path(sys.argv[1]) if len(sys.argv) > 1 else TELEMETRY
    rows = []
    if src.is_dir():
        for f in sorted(src.glob("*.json")):
            try:
                j = json.loads(f.read_text(encoding="utf-8"))
            except Exception:  # noqa: BLE001 - 聚合工具允许跳过坏文件
                continue
            rows.append({
                "file": f.name,
                "seed": j.get("seed", ""),
                "characterId": j.get("characterId", ""),
                "floors": j.get("floors", 0),
                "actReached": j.get("actReached", 0),
                "victory": j.get("victory", False),
                "roomsHandled": j.get("roomsHandled", 0),
                "relics": ",".join(j.get("relicIds") or []),
            })
    OUT.parent.mkdir(parents=True, exist_ok=True)
    with OUT.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()) if rows else ["file"])
        w.writeheader()
        w.writerows(rows)
    wins = sum(1 for r in rows if r["victory"])
    with_relics = sum(1 for r in rows if r["relics"])
    print(f"runs={len(rows)} victory={wins} with_relicIds={with_relics} -> {OUT.name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
