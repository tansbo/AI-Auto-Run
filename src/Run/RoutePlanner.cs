using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 地图选路的危险度感知评分（用户规则 2026-09-02：智能评判每条路线的危险度，选最优走法）。
/// 对从候选节点到幕末 Boss 的地图 DAG 做记忆化回溯：每个节点 = 自身价值/风险 + 最优后继 × 折扣。
///   - 战斗节点按类型计"收益 - 风险成本"；风险成本随当前血量放大（低血更危险），
///     随持有的药水（保险）衰减；
///   - 低血时篝火（回血）与宝箱/商店（安全收益）相对变值，精英被惩罚；
///   - 满血健康时精英的高收益占优 —— 路线选择自然随生命/药水状态调整。
/// 数值均为启发式（待跑局数据校准）；决策会连同评分写进跑局日志供聚合分析。
/// </summary>
internal static class RoutePlanner
{
    // 风险随血量的放大曲线：血 55% 为中性（×1.0）；越低越危险，越高越从容。
    private const float HpNeutral = 0.55f;
    // 药水保险：每个药水把风险成本除以 (1 + 0.12×数量)（最多按 4 个计）。
    private const float PotionInsurancePer = 0.12f;

    /// <summary>
    /// 从当前节点（或开局第 0 行）的候选后继中挑评分最高的一条分支。
    /// </summary>
    /// <param name="currentPoint">当前所在节点；null 表示开局（从第 0 行候选）。</param>
    /// <returns>最优候选节点；找不到时返回 null。</returns>
    public static MapPoint? PickBest(RunState runState, MapPoint? currentPoint, out float bestScore)
    {
        bestScore = float.MinValue;
        if (runState.Map is null or NullActMap)
            return null;

        Player? player = LocalContext.GetMe(runState);
        float hpFraction = player?.Creature != null && player.Creature.MaxHp > 0
            ? (float)player.Creature.CurrentHp / player.Creature.MaxHp
            : 0.7f;
        int potionCount = player == null ? 0 : Math.Min(player.Potions.Count(), 4);

        var memo = new Dictionary<MapPoint, float>();
        int evaluated = 0;

        float RiskCost(float baseRisk)
        {
            float hpMult = Math.Clamp(1f + (HpNeutral - hpFraction) * 2f, 0.55f, 2.2f);
            float insuranceDiv = 1f + PotionInsurancePer * potionCount;
            return baseRisk * hpMult / insuranceDiv;
        }

        IEnumerable<MapPoint> candidates = currentPoint != null
            ? currentPoint.Children
            : ((ActMap)runState.Map).GetPointsInRow(0);

        MapPoint? best = null;
        foreach (MapPoint candidate in candidates)
        {
            if (candidate == null)
                continue;
            float score = ScorePath(candidate, runState.Map, RiskCost, hpFraction, memo, ref evaluated, 0);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>记忆化：节点价值 = 自身收益/风险 + 0.9 × 最优后继价值；Boss/Ancient 为终点（0）。</summary>
    private static float ScorePath(
        MapPoint node,
        ActMap map,
        Func<float, float> riskCost,
        float hpFraction,
        Dictionary<MapPoint, float> memo,
        ref int evaluated,
        int depth)
    {
        if (memo.TryGetValue(node, out float cached))
            return cached;
        if (++evaluated > 600)
            return 0f; // 防御：异常超大图不无限展开。
        if (depth > 16)
            return 0f;

        MapPointType type = node.PointType;
        if (type is MapPointType.Boss or MapPointType.Ancient)
        {
            memo[node] = 0f;
            return 0f;
        }

        float value = type switch
        {
            MapPointType.Monster => 1.0f - riskCost(3.0f),
            MapPointType.Elite => 3.2f - riskCost(6.5f),
            MapPointType.RestSite => hpFraction < 0.45f ? 3.4f : 1.6f,
            MapPointType.Treasure => 7.0f,
            MapPointType.Shop => 3.8f,
            _ => 0.4f, // 事件等中性/未知类型给小幅方差价值。
        };

        float bestChild = 0f;
        foreach (MapPoint child in node.Children)
        {
            float childValue = ScorePath(child, map, riskCost, hpFraction, memo, ref evaluated, depth + 1);
            if (childValue > bestChild)
                bestChild = childValue;
        }

        float result = value + bestChild * 0.9f;
        memo[node] = result;
        return result;
    }
}
