#!/usr/bin/env python3
"""聚合种子重放 A/B 遥测，输出受控对照的卡牌胜率差 CSV（纯标准库）。

用法：python aggregate_run_telemetry.py <collect-dir> [--out prefix]

读取 collect-dir 下的遥测 JSON（run-seed-ab-batch.ps1 产出 {seed}__{policySafe}.json，
或原始的 {seed}_{runId}.json）。每个文件的 forcedPicks 字段是原始策略串
（"cardId:take,cardId:skip" 格式）。按 seed 分组做受控对照：
同一种子下同一张卡的 take 局 vs skip 局，结局差异归因于那张卡（其余全由种子固定）。

配对只使用恰好一条规则的策略局（多卡规则跑局会同时改多张牌，无法干净归因，跳过配对）。

输出两个 CSV：
- {out}.pairs.csv   —— 每行一对 (seed, card) 的 take/skip 结局明细
- {out}.summary.csv —— 每张卡聚合：take 胜率 - skip 胜率 = delta
"""
import argparse
import csv
import json
import sys
from collections import defaultdict
from pathlib import Path


def parse_policy(forced_picks: str) -> dict[str, str]:
    """解析 "a:take,b:skip" -> {"a": "take", "b": "skip"}。"""
    rules: dict[str, str] = {}
    for part in (forced_picks or "").split(","):
        part = part.strip()
        if not part or ":" not in part:
            continue
        card_id, _, action = part.partition(":")
        card_id = card_id.strip()
        action = action.strip().lower()
        if card_id and action in ("take", "skip"):
            rules[card_id] = action
    return rules


def load_runs(directory: Path):
    runs = []
    for f in sorted(directory.glob("*.json")):
        if f.name.startswith("summary"):
            continue
        try:
            data = json.loads(f.read_text(encoding="utf-8"))
        except Exception as exc:  # noqa: BLE001 — 跳过坏文件，不能吞掉但在聚合器里记一条
            print(f"WARN: skip {f.name}: {exc}", file=sys.stderr)
            continue
        if not isinstance(data, dict):
            continue
        seed = str(data.get("seed", ""))
        if not seed:
            continue
        forced_picks = []
        for pick in (data.get("picks") or []):
            if isinstance(pick, dict) and pick.get("forced"):
                forced_picks.append((str(pick.get("chosen", "")), str(pick.get("forcedAction", ""))))
        runs.append({
            "file": f.name,
            "seed": seed,
            "victory": bool(data.get("victory", False)),
            "floors": int(data.get("floors", 0)),
            "policy": parse_policy(str(data.get("forcedPicks", ""))),
            "forced_picks": forced_picks,
        })
    return runs


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("collect_dir", help="遥测 JSON 目录（run-seed-ab-batch.ps1 的 CollectRoot）")
    ap.add_argument("--out", default="seed_ab_results", help="输出 CSV 前缀")
    args = ap.parse_args()

    directory = Path(args.collect_dir)
    if not directory.is_dir():
        print(f"ERROR: not a directory: {directory}", file=sys.stderr)
        sys.exit(1)

    runs = load_runs(directory)
    print(f"Loaded {len(runs)} telemetry run(s) from {directory}")

    single_rule = []
    for r in runs:
        if len(r["policy"]) == 1:
            (card_id, action), = r["policy"].items()
            r["card"] = card_id
            r["action"] = action
            single_rule.append(r)
        else:
            print(f"  (skip multi-rule run {r['file']}: {r['policy']})")

    # 配对：同 seed 同 card 同时有 take 局与 skip 局才算受控对照对。
    # 守卫：take 局必须真强制到了该卡（forced=true 且 action=take）。若卡在整局从未
    # 出现，take 局无强制记录，两局完全相同 → 退化对，不配对（同种子发牌固定，take
    # 局能强制到说明 skip 局也一定见到同一张卡，无需再验 skip 侧）。
    by_seed_card: dict[tuple[str, str], dict[str, dict]] = defaultdict(dict)
    for r in single_rule:
        by_seed_card[(r["seed"], r["card"])][r["action"]] = r

    pairs = []
    skipped_degenerate = 0
    for (seed, card_id), runs_by_action in sorted(by_seed_card.items()):
        take_run = runs_by_action.get("take")
        skip_run = runs_by_action.get("skip")
        if take_run is None or skip_run is None:
            continue
        # 大小写不敏感：策略串用用户输入（如 blood_wall），telemetry 的 chosen 是 Id.Entry（BLOOD_WALL）。
        if not any(c.lower() == card_id.lower() and a == "take" for c, a in take_run["forced_picks"]):
            skipped_degenerate += 1
            print(
                f"  (skip degenerate pair {seed}/{card_id}: take 局未强制到该卡，"
                f"说明卡从未出现，两局相同)"
            )
            continue
        pairs.append({
            "seed": seed,
            "card": card_id,
            "take_victory": int(take_run["victory"]),
            "take_floors": take_run["floors"],
            "take_file": take_run["file"],
            "skip_victory": int(skip_run["victory"]),
            "skip_floors": skip_run["floors"],
            "skip_file": skip_run["file"],
        })

    pairs_out = Path(args.out + ".pairs.csv")
    with pairs_out.open("w", encoding="utf-8", newline="") as fh:
        writer = csv.DictWriter(fh, fieldnames=[
            "seed", "card", "take_victory", "take_floors", "take_file",
            "skip_victory", "skip_floors", "skip_file",
        ])
        writer.writeheader()
        writer.writerows(pairs)

    # 按卡聚合 delta = take胜率 - skip胜率；另有连续信号 floors_delta =
    # mean(take floors) - mean(skip floors)（当样本全是败局时胜率退化，层数仍可区分）。
    agg = defaultdict(lambda: {
        "take_w": 0, "take_t": 0, "skip_w": 0, "skip_t": 0,
        "take_floors": 0, "skip_floors": 0, "seeds": set(),
    })
    for p in pairs:
        a = agg[p["card"]]
        a["take_w"] += p["take_victory"]
        a["take_t"] += 1
        a["skip_w"] += p["skip_victory"]
        a["skip_t"] += 1
        a["take_floors"] += p["take_floors"]
        a["skip_floors"] += p["skip_floors"]
        a["seeds"].add(p["seed"])

    summary_out = Path(args.out + ".summary.csv")
    with summary_out.open("w", encoding="utf-8", newline="") as fh:
        writer = csv.writer(fh)
        writer.writerow([
            "card", "take_wins", "take_total", "take_winrate",
            "skip_wins", "skip_total", "skip_winrate", "winrate_delta",
            "take_avg_floors", "skip_avg_floors", "floors_delta", "paired_seeds",
        ])
        for card_id, a in sorted(agg.items()):
            take_wr = a["take_w"] / a["take_t"] if a["take_t"] else 0.0
            skip_wr = a["skip_w"] / a["skip_t"] if a["skip_t"] else 0.0
            take_af = a["take_floors"] / a["take_t"] if a["take_t"] else 0.0
            skip_af = a["skip_floors"] / a["skip_t"] if a["skip_t"] else 0.0
            writer.writerow([
                card_id, a["take_w"], a["take_t"], f"{take_wr:.3f}",
                a["skip_w"], a["skip_t"], f"{skip_wr:.3f}",
                f"{take_wr - skip_wr:+.3f}",
                f"{take_af:.2f}", f"{skip_af:.2f}", f"{take_af - skip_af:+.2f}",
                len(a["seeds"]),
            ])

    print(f"Wrote {len(pairs)} paired seed-card comparison(s): {pairs_out}")
    print(f"Wrote aggregate summary: {summary_out}")
    print(f"  cards with paired data: {len(agg)}")
    print(f"  skipped degenerate pairs (card never appeared): {skipped_degenerate}")
    for card_id, a in sorted(agg.items()):
        take_wr = a["take_w"] / a["take_t"] if a["take_t"] else 0.0
        skip_wr = a["skip_w"] / a["skip_t"] if a["skip_t"] else 0.0
        take_af = a["take_floors"] / a["take_t"] if a["take_t"] else 0.0
        skip_af = a["skip_floors"] / a["skip_t"] if a["skip_t"] else 0.0
        print(
            f"  {card_id}: take {a['take_w']}/{a['take_t']} ({take_wr:.2f}) "
            f"vs skip {a['skip_w']}/{a['skip_t']} ({skip_wr:.2f}) "
            f"winrate_delta={take_wr - skip_wr:+.2f} "
            f"floors take {take_af:.1f} vs skip {skip_af:.1f} "
            f"-> {take_af - skip_af:+.2f}"
        )


if __name__ == "__main__":
    main()
