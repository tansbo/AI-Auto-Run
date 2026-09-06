using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 精英追踪与袋预测（用户规则 2026-09-06；decomp ActModel.cs L377-382 实证）：
/// 每幕把 3 种精英遭遇塞进"袋内不重复"抽取 → 本幕前 3 场精英必是 3 种各一次。
/// 见过 2 种后第 3 场必是剩余那种（可预测）；见过 1 种时余下两种各半（按画像双预案）。
/// 只读地记录：本幕精英出现序（遥测+日志），供关联分析（某精英强度/组合胜负）与
/// 路线风险/药水保留的"前方窗口"（后续阶段注入 RoutePlanner/PotionRunPolicy）。
/// </summary>
internal static class EliteTracker
{
    /// <summary>每次精英战斗开始时调用（RunAutoController.OnCombatStarting）。</summary>
    public static void RecordCombatStart(RunAutoSession session, CombatState state)
    {
        if (session == null || state.Encounter?.RoomType != RoomType.Elite)
            return;
        RunState? runState = session.RunState;
        int act = runState?.CurrentActIndex ?? -1;
        if (act < 0)
            return;
        string id = state.Encounter.Id.Entry;
        string shortName = state.Encounter.GetType().Name;

        if (act != session.EliteTrackedAct)
        {
            // 新幕：重置本幕已见（预测预算也随之复位）。
            session.EliteTrackedAct = act;
            session.ElitesSeenThisAct.Clear();
            session.PredictedNextElite = null;
        }
        if (session.ElitesSeenThisAct.Contains(id))
            return; // 同一袋序内重复（不应发生），只记首见。

        session.ElitesSeenThisAct.Add(id);
        session.Telemetry.RecordEliteSeen(id);
        session.LogDecision($"精英追踪（第{act + 1}幕 第{session.ElitesSeenThisAct.Count}种）：{shortName} {id}");

        // 袋内不重复：见过 2 种 → 第 3 场必是剩余那种（本幕仅 3 种精英）。
        if (session.ElitesSeenThisAct.Count == 2)
        {
            string? remaining = RemainingEliteOfAct(runState, session.ElitesSeenThisAct);
            if (remaining != null)
            {
                session.PredictedNextElite = remaining;
                session.LogDecision($"精英预测：本幕已见 2 种，下场精英必为 {remaining}");
            }
        }
        else if (session.ElitesSeenThisAct.Count == 1)
        {
            session.LogDecision($"精英预测：本幕首见精英，余下两种各半（双预案备战）");
        }
    }

    /// <summary>本幕精英种集合（decomp ActModel.AllEliteEncounters，正常 3 种）减已见 → 剩余那种。</summary>
    private static string? RemainingEliteOfAct(RunState? runState, List<string> seen)
    {
        if (runState?.Act is not { } actModel || actModel.AllEliteEncounters == null)
            return null;
        try
        {
            string[] actElites = actModel.AllEliteEncounters
                .Select(static encounter => encounter.Id.Entry)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (actElites.Length < 2)
                return null;
            return actElites.FirstOrDefault(entry => !seen.Contains(entry));
        }
        catch (Exception)
        {
            return null; // 遭遇表访问失败不打断跑局。
        }
    }
}
