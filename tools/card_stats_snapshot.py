#!/usr/bin/env python3
"""从 Spire Codex A10 卡牌统计 CSV 生成运行时快照 src/Run/CardWinStats.cs。

每卡带归属池(RoleById)与绝对胜率(WinRateById)，以及各池中位(MedianWinRateByRole)。
BonusFor(entry, receivingRole) 运行时计算：
  - 卡属于接收职业（或接收职业未知）→ clamp((winRate − 自身池中位)×0.45, ±8)
    （与原 BonusById 语义一致，浮点容差内等价）
  - 跨池卡（无色池/他职业池，如万花筒/海玻璃/棱彩宝石/色彩哲学家等渠道）→ 对照"接收职业中位"，
    衡量这张外来卡对该职业的相对强度。
仅 picks>=1000 高置信样本计。纯标准库。用法: python tools/card_stats_snapshot.py
"""
import csv
import statistics
import collections
from pathlib import Path

CSV = Path(r"D:\JAVA_WorkPlace\AIWithCombatSolver\.local\webstats\card_stats_a10.csv")
OUT = Path(r"D:\JAVA_WorkPlace\AIWithCombatSolver\CombatSolver\src\Run\CardWinStats.cs")

MIN_PICKS = 1000
MEDIAN_MIN_PICKS = 500
K = 0.45
CAP = 8.0

ROLE_KEY = {
    "colorless": "COLORLESS",
    "ironclad": "IRONCLAD",
    "silent": "SILENT",
    "defect": "DEFECT",
    "necrobinder": "NECROBINDER",
    "regent": "REGENT",
}


def clamp(v: float) -> float:
    return max(-CAP, min(CAP, v))


def main() -> int:
    rows = list(csv.DictReader(open(CSV, encoding="utf-8")))
    by = collections.defaultdict(list)
    for r in rows:
        role = ROLE_KEY.get(r["color"], r["color"].upper())
        r["_role"] = role
        by[role].append(r)

    medians = {
        role: statistics.median(float(r["winRate"]) for r in rs if int(r["picks"]) >= MEDIAN_MIN_PICKS)
        for role, rs in by.items()
        if sum(1 for r in rs if int(r["picks"]) >= MEDIAN_MIN_PICKS) > 0
    }

    by_id = {}
    for role, rs in by.items():
        for r in rs:
            if int(r["picks"]) < MIN_PICKS:
                continue
            by_id[r["id"]] = (float(r["winRate"]), role, int(r["picks"]))

    win_lines = []
    for cid in sorted(by_id):
        win, role, picks = by_id[cid]
        delta = clamp((win - medians[role]) * K)
        win_lines.append(
            f'    ["{cid}"] = {win!r}f, // role={role} picks={picks} selfBonus={delta:+.3f}')

    med_lines = ",\n".join(
        f'    ["{role}"] = {med!r}f' for role, med in sorted(medians.items()))

    role_lines = ",\n".join(
        f'    ["{cid}"] = "{role}"' for cid, (_, role, _) in sorted(by_id.items()))

    chunks = []
    chunks.append("""// 由 tools/card_stats_snapshot.py 从 Spire Codex A10 真实对局统计生成（勿手改）。
// 数据表：WinRateById=每卡绝对胜率，RoleById=归属池，MedianWinRateByRole=各池胜率中位(picks>=500 计算)。
// BonusFor(entry, receivingRole)：
//   同池/未知接收职业 → clamp((winRate − 自身池中位)×0.45, ±8)（与原 BonusById 语义一致）
//   跨池（无色/他职业，如万花筒/海玻璃/棱彩宝石/色彩哲学家等渠道拿到）→ clamp((winRate − 接收职业中位)×0.45, ±8)
// 仅 picks>=1000 高置信样本计。
using System;
using System.Collections.Generic;

namespace CombatSolver.Run;

internal static class CardWinStats
{
    private const float K = 0.45f;
    private const float Cap = 8f;

    /// <summary>卡牌 Id.Entry → 绝对胜率（口径与源站一致）。</summary>
    internal static readonly Dictionary<string, float> WinRateById = new(StringComparer.Ordinal)
    {
""")
    chunks.append("\n".join(win_lines))
    chunks.append("""
    };

    /// <summary>角色池（含无色）→ 该池内卡牌胜率中位（快照计算口径 picks>=500）。</summary>
    internal static readonly Dictionary<string, float> MedianWinRateByRole = new(StringComparer.Ordinal)
    {
""")
    chunks.append(med_lines)
    chunks.append("""
    };

    /// <summary>卡牌 Id.Entry → 归属池（COLORLESS/IRONCLAD/SILENT/DEFECT/NECROBINDER/REGENT）。</summary>
    internal static readonly Dictionary<string, string> RoleById = new(StringComparer.Ordinal)
    {
""")
    chunks.append(role_lines)
    chunks.append("""
    };

    /// <summary>
    /// 数据驱动加成：同池或接收职业未知按自身池中位（与原语义一致）；跨池卡对照接收职业中位。
    /// 返回值四舍五入到 0.001，与旧快照表数值在容差内一致。
    /// </summary>
    internal static float BonusFor(string entry, string receivingRole)
    {
        if (!WinRateById.TryGetValue(entry, out float win))
            return 0f;
        if (!RoleById.TryGetValue(entry, out string? ownRole) || ownRole is null)
            return 0f;
        if (!MedianWinRateByRole.TryGetValue(ownRole, out float median))
            return 0f;
        if (string.IsNullOrEmpty(receivingRole) || ownRole != receivingRole)
        {
            if (MedianWinRateByRole.TryGetValue(receivingRole, out float receivingMedian))
                median = receivingMedian;
        }
        float delta = Math.Clamp((win - median) * K, -Cap, Cap);
        return MathF.Round(delta, 3, MidpointRounding.ToEven);
    }
}
""")

    OUT.write_text("".join(chunks), encoding="utf-8")
    print(f"entries={len(by_id)} roles={len(medians)}")
    print("medians=" + ", ".join(f"{k}={round(v,1)}" for k, v in sorted(medians.items())))
    print(f"-> {OUT.name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
