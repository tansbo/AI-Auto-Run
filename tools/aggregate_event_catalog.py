#!/usr/bin/env python3
"""聚合 .local/event-catalog/group*.json 事件目录：合并、校验键集、输出统计，供 EventOptionValuer 填表参考。

用法: python tools/aggregate_event_catalog.py [catalog_dir] [out]
默认 catalog_dir=D:\\JAVA_WorkPlace\\AIWithCombatSolver\\.local\\event-catalog
输出聚合 JSON 与统计到 stdout。
纯标准库，无第三方依赖。
"""
import json
import sys
from pathlib import Path

OPTION_KEYS = {"textKey", "deterministic", "outcome", "costs", "random", "slAble", "evidence", "uncertain"}
RECORD_KEYS = {"eventFile", "eventClass", "isAncient", "layoutType", "options", "notes"}

def main() -> int:
    catalog_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(r"D:\JAVA_WorkPlace\AIWithCombatSolver\.local\event-catalog")
    out_path = Path(sys.argv[2]) if len(sys.argv) > 2 else catalog_dir / "all-events.json"

    records = []
    problems = []
    for group_file in sorted(catalog_dir.glob("group*.json")):
        try:
            data = json.loads(group_file.read_text(encoding="utf-8"))
        except Exception as exc:  # noqa: BLE001 - 目录校验工具允许宽捕获
            problems.append(f"{group_file.name}: 解析失败 {exc}")
            continue
        if not isinstance(data, list):
            problems.append(f"{group_file.name}: 不是数组")
            continue
        for rec in data:
            missing = RECORD_KEYS - set(rec.keys())
            if missing:
                problems.append(f"{group_file.name}/{rec.get('eventClass', '?')}: 缺记录键 {missing}")
            for opt in rec.get("options", []):
                miss = OPTION_KEYS - set(opt.keys())
                if miss:
                    problems.append(
                        f"{group_file.name}/{rec.get('eventClass', '?')}/{opt.get('textKey')}: 缺选项键 {miss}")
            records.append(rec)

    # 按 eventClass 去重（组间不应重复）
    by_class = {}
    for rec in records:
        by_class.setdefault(rec["eventClass"], []).append(rec)
    dupes = {k: v for k, v in by_class.items() if len(v) > 1}
    if dupes:
        problems.append(f"重复事件类: {list(dupes.keys())}")

    seen_files = [r["eventFile"] for r in records]
    det = sum(1 for r in records for o in r.get("options", []) if o.get("deterministic"))
    rnd = sum(1 for r in records for o in r.get("options", []) if not o.get("deterministic"))
    with_key = sum(1 for r in records for o in r.get("options", []) if o.get("textKey"))
    no_key = sum(1 for r in records for o in r.get("options", []) if not o.get("textKey"))
    stat = {
        "groups_merged": len(list(catalog_dir.glob("group*.json"))),
        "records": len(records),
        "options_total": sum(len(r.get("options", [])) for r in records),
        "deterministic_options": det,
        "non_deterministic_options": rnd,
        "options_with_textKey": with_key,
        "options_without_textKey": no_key,
        "ancients": sum(1 for r in records if r.get("isAncient")),
    }

    out = {"stat": stat, "problems": problems, "records": records}
    out_path.write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    print(json.dumps({"stat": stat, "problem_count": len(problems), "problems_head": problems[:8]},
                     ensure_ascii=False, indent=1))
    print(f"聚合文件: {out_path}")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
