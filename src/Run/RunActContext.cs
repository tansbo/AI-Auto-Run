using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 跑局"当前节点之后的路线"上下文：距幕末 Boss 的行数、前方可到达的战斗/精英数量。
/// 由 RunAuto 各决策点（奖励领药水、篝火选择、地图选路）从 <see cref="RunState.Map"/> 实时估算，
/// 用于路线危险度与药水保留成本（用户规则 2026-09-02）：
///   - 难度 A2+（WearyTraveler）时，每幕 Ancient（Boss 后）只补缺失生命的 80%，
///     所以幕末"顶满血/喝回血药"只值 20% 的跨幕留存 —— 幕末应省药、低血出 Boss，靠 Ancient 补回大头；
///   - 前方精英/战斗越多 → 药水保留价值越高（留着救命），越不值得为普通奖励浪费；
///   - 战士（IRONCLAD）每场战斗胜利后由燃烧之血回 6 血 → 适当卖血省药可接受
///     （选路风险容忍更高、篝火回血不那么急）。
/// </summary>
internal static class RunActContext
{
    /// <summary>Ancient（Boss 后）对缺失生命的补偿比例：A2+ 为 0.8，其余难度满补。</summary>
    public static decimal ActBoundaryHealFraction()
    {
        try
        {
            return RunManager.Instance.HasAscension(AscensionLevel.WearyTraveler) ? 0.8m : 1.0m;
        }
        catch
        {
            // 非跑局中（如无人测试建局间隙）没有 AscensionManager，按最低难度处理。
            return 1.0m;
        }
    }

    /// <summary>
    /// 角色被动"每场战斗胜利后回血"（decomp 核对：战士起始遗物燃烧之血 BurningBlood，
    /// AfterCombatVictory 且未死时 Heal 6）。其余角色 0。
    /// </summary>
    public static decimal PassivePostCombatHeal(Player? player)
        => player?.Character is Ironclad ? 6m : 0m;

    public static bool IsIronclad(Player? player)
        => player?.Character is Ironclad;

    /// <summary>
    /// 金币价值系数（用户规则 2026-09-02）：
    /// 1) 金币只有在"能安全较快到达商店"时才值钱——本幕没有可达商店/商店位置差 → 系数低（约 0.5）；
    ///    商店近而多 → 系数高（最高 ~1.4）。
    /// 2) 事件金币门槛（decomp 实测）：多数事件要求金币 ≥100 才出现（CrystalSphere/FakeMerchant/
    ///    MorphicGrove/Ranwid 金选项/WelcomeToWongos），另有 120(EndlessConveyor)/125(ZenWeaver)/
    ///    100–149(LuminousChoir) 等高门槛。金币价值在门槛下方更高（别花到掉出门槛）、
    ///    高于 150 后边际价值回落。地图上事件显示为 Unknown 节点。
    /// 应用到所有"金币↔分数"换算（事件金币/代价、CursedPearl、SilkenTress 等）。
    /// </summary>
    public static float GoldValueScale(RunState? runState)
    {
        if (runState == null || runState.VisitedMapCoords.Count == 0
            || runState.Map is null or NullActMap)
            return 0.8f;

        ActMap map = runState.Map;
        MapCoord current = runState.VisitedMapCoords[runState.VisitedMapCoords.Count - 1];
        MapPoint? currentNode = FindPoint(map, current);
        if (currentNode == null)
            return 0.8f;

        int shops = 0;
        int unknownAhead = 0;
        int nearestShopSteps = int.MaxValue;
        var seen = new HashSet<MapPoint>();
        var queue = new Queue<(MapPoint Point, int Steps)>();
        foreach (MapPoint child in currentNode.Children)
            queue.Enqueue((child, 1));
        const int depthLimit = 12;
        while (queue.Count > 0 && seen.Count < 300)
        {
            (MapPoint point, int steps) = queue.Dequeue();
            if (!seen.Add(point))
                continue;
            if (point.PointType == MapPointType.Shop)
            {
                shops++;
                if (steps < nearestShopSteps)
                    nearestShopSteps = steps;
            }
            else if (point.PointType == MapPointType.Unknown)
            {
                unknownAhead++; // 事件/未知节点（多数事件有 ≥100 金币门槛）。
            }
            if (steps < depthLimit)
            {
                foreach (MapPoint child in point.Children)
                    queue.Enqueue((child, steps + 1));
            }
        }
        float scale;
        if (shops == 0)
            scale = 0.5f; // 本幕没有可达商店：金币大幅贬值。
        else
            scale = 0.8f + 0.15f * Math.Min(shops, 6) - 0.08f * Math.Max(0, nearestShopSteps - 1);

        // 前方还有可能带金币门槛的事件（Unknown 节点）→ 按实测门槛分段给边际加成：
        //   <100：多数事件（CrystalSphere/FakeMerchant/MorphicGrove/Ranwid/Wongo）门槛，加成最大；
        //   100–150：120/125/100–149 高门槛带，加成中等；≥150 全部解锁，不加成。
        if (unknownAhead > 0)
        {
            int gold = LocalContext.GetMe(runState)?.Gold ?? 0;
            int rowsLeft = Math.Max(0, FindBossRow(map, current.row) - current.row);
            if (rowsLeft >= 3)
            {
                if (gold < 100)
                    scale += 0.25f;
                else if (gold < 150)
                    scale += 0.12f;
            }
        }
        return Math.Clamp(scale, 0.5f, 1.55f);
    }

    public readonly record struct RouteAhead(
        int RowsLeftToBoss,
        int ElitesAhead,
        int FightsAhead)
    {
        /// <summary>距离幕末 Boss 很近（含已站在 Boss 行）：跨幕回血补偿会让幕末回血药/顶血贬值。</summary>
        public bool NearActEnd => RowsLeftToBoss <= 2;

        /// <summary>路线危险度 0..100：前方战斗越多越危险（精英权重更高）。</summary>
        public int RouteDanger => Math.Min(100, ElitesAhead * 14 + (FightsAhead - ElitesAhead) * 5);
    }

    /// <summary>估算当前节点之后的地图构成。找不到地图/节点时返回"看不到前方"的空上下文。</summary>
    public static RouteAhead CaptureAhead(RunState? runState)
    {
        if (runState == null || runState.VisitedMapCoords.Count == 0)
            return new RouteAhead(int.MaxValue / 4, 0, 0);

        MapCoord current = runState.VisitedMapCoords[runState.VisitedMapCoords.Count - 1];
        if (runState.Map is null or NullActMap)
            return new RouteAhead(int.MaxValue / 4, 0, 0);

        ActMap map = runState.Map;
        MapPoint? currentNode = FindPoint(map, current);
        int bossRow = FindBossRow(map, current.row);
        int rowsLeft = Math.Max(0, bossRow - current.row);
        if (currentNode == null)
            return new RouteAhead(rowsLeft, 0, 0);

        // 从前节点 Children 起有界 BFS（最多 8 层 / 300 节点），统计可到达的战斗。
        int elites = 0, fights = 0;
        var seen = new HashSet<MapPoint>();
        var queue = new Queue<(MapPoint Point, int Depth)>();
        foreach (MapPoint child in currentNode.Children)
            queue.Enqueue((child, 1));
        const int depthLimit = 8;
        while (queue.Count > 0 && seen.Count < 300)
        {
            (MapPoint point, int depth) = queue.Dequeue();
            if (!seen.Add(point))
                continue;
            switch (point.PointType)
            {
                case MapPointType.Elite:
                    elites++;
                    fights++;
                    break;
                case MapPointType.Monster:
                    fights++;
                    break;
            }
            if (depth < depthLimit)
            {
                foreach (MapPoint child in point.Children)
                    queue.Enqueue((child, depth + 1));
            }
        }
        return new RouteAhead(rowsLeft, elites, fights);
    }

    private static MapPoint? FindPoint(ActMap map, MapCoord coord)
    {
        int top = Math.Min(map.GetRowCount() - 1, coord.row + 1);
        for (int row = Math.Max(0, coord.row - 1); row <= top; row++)
        {
            foreach (MapPoint point in map.GetPointsInRow(row))
            {
                if (point.coord.Equals(coord))
                    return point;
            }
        }
        return null;
    }

    private static int FindBossRow(ActMap map, int currentRow)
    {
        int rowCount = map.GetRowCount();
        for (int row = currentRow + 1; row < rowCount; row++)
        {
            foreach (MapPoint point in map.GetPointsInRow(row))
            {
                if (point.PointType is MapPointType.Boss or MapPointType.Ancient)
                    return row;
            }
        }
        return rowCount;
    }
}
