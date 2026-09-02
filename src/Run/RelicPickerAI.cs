using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 遗物选择智能：对遗物选择屏幕（Boss 遗物/珠宝盒）、宝箱遗物和开局先古遗物（Neow）做评分。
/// 只读不可变 RelicModel，主线程执行。评分 = 稀有度 + 精选表加成。
/// 低于 <see cref="SkipThreshold"/> 时跳过（仅 NChooseARelicSelection 用，宝箱必拿）。
/// 先古遗物全部是 <see cref="RelicRarity.Ancient"/>，稀有度无法区分正向/诅咒，须按运行时类名识别诅咒。
/// 先古遗物额外做路线感知：地图在 Neow 之前已生成（RunManager 开局流程设置 State.Map），
/// 按当前幕前几行可到达节点的构成（精英/小怪/篝火/商店密度）微调各遗物加成。
/// </summary>
internal static class RelicPickerAI
{
    public const float SkipThreshold = 4f;

    /// <summary>少数强泛用遗物的手写加成。key 是遗物 Id.Entry（实测为大写，如 "RUNIC_PYRAMID"）；
    /// 用 OrdinalIgnoreCase 兼容手写的小写 key（与 CardPickerAI.KnownCardBonuses 同款死代码修复，
    /// 否则这些加成从不命中）。</summary>
    private static readonly Dictionary<string, float> KnownRelicBonuses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bloody_idol"] = 8f,
        ["runic_pyramid"] = 8f,
        ["snecko_eye"] = 7f,
        ["apotheosis_skull"] = 6f,
    };

    /// <summary>Neow 先古遗物的 10 个诅咒遗物（类名）。NeowsBones 的描述是 POSITIVE 但仍属诅咒列表，故按类名而非描述识别。</summary>
    private static readonly HashSet<string> KnownCurseRelicTypeNames = new(StringComparer.Ordinal)
    {
        nameof(CursedPearl),
        nameof(DowsingRod),
        nameof(HeftyTablet),
        nameof(LargeCapsule),
        nameof(LeafyPoultice),
        nameof(NeowsBones),
        nameof(NeowsSacrifice),
        nameof(PrecariousShears),
        nameof(SilkenTress),
        nameof(SilverCrucible),
    };

    /// <summary>
    /// Neow 正向先古遗物（14 项）的相对强度，权重按反编译出的实际效果校准（2026-08-31）：
    /// GoldenPearl=+150 金币（最强开局经济）、ArcaneScroll=随机稀有牌、PhialHolster=+1 药水槽+2 药水、
    /// BoomingConch=精英首回合+2 抽+1 能量（路线相关）、FishingRod=每 3 场小怪随机升级一张牌、
    /// LostCoffer=3 选 1 卡牌奖励+药水（无选择权）、ScrollBoxes=选一组卡牌捆绑包、Kaleidoscope=跨角色 2 次选牌、
    /// PreciseScissors=移除 1 张牌、WingedBoots=3 次自由移动、LeadPaperweight=无色 2 选 1、
    /// NewLeaf=随机转化 1 张牌、NeowsTorment=加入 NeowsFury（1 费消耗攻击）。
    /// MassiveScroll 仅多人局可拿（单人不会出现），权重无关紧要。全部是结构信号，不做模拟。
    /// </summary>
    private static readonly Dictionary<string, float> KnownAncientChoiceBonuses = new(StringComparer.Ordinal)
    {
        [nameof(GoldenPearl)] = 5f,
        [nameof(ArcaneScroll)] = 5f,
        [nameof(PhialHolster)] = 4f,
        [nameof(LostCoffer)] = 4f,
        [nameof(Kaleidoscope)] = 4f,
        [nameof(BoomingConch)] = 3f,
        [nameof(FishingRod)] = 3f,
        [nameof(PreciseScissors)] = 3f,
        [nameof(ScrollBoxes)] = 3f,
        [nameof(WingedBoots)] = 3f,
        [nameof(LeadPaperweight)] = 2f,
        [nameof(NewLeaf)] = 1f,
        [nameof(NeowsTorment)] = 1f,
        [nameof(MassiveScroll)] = 1f,
    };

    public static float Score(RelicModel relic)
    {
        float score = relic.Rarity switch
        {
            RelicRarity.Rare => 14f,
            RelicRarity.Ancient => 13f,
            RelicRarity.Uncommon => 8f,
            RelicRarity.Shop => 7f,
            RelicRarity.Event => 6f,
            RelicRarity.Common => 4f,
            RelicRarity.Starter => 1f,
            _ => 0f,
        };
        if (KnownRelicBonuses.TryGetValue(relic.Id.Entry, out float known))
            score += known;
        return score;
    }

    /// <summary>返回分数最高且不低于跳过阈值的遗物；否则返回 null（跳过）。</summary>
    public static RelicModel? PickBest(IReadOnlyList<RelicModel> options)
    {
        RelicModel? best = null;
        float bestScore = float.MinValue;
        foreach (RelicModel relic in options)
        {
            float score = Score(relic);
            if (score > bestScore)
            {
                bestScore = score;
                best = relic;
            }
        }
        return best != null && bestScore >= SkipThreshold ? best : null;
    }

    /// <summary>是否为 Neow 诅咒遗物（以运行时类名为准，比 Id.Entry 更稳）。</summary>
    public static bool IsAncientCurse(RelicModel relic) => KnownCurseRelicTypeNames.Contains(relic.GetType().Name);

    /// <summary>先古遗物评分：诅咒为不可选（-1000），正向按精选表加成，基础分按 Ancient，再加路线调整。</summary>
    public static float ScoreAncientChoice(RelicModel relic, RunState? runState)
    {
        if (IsAncientCurse(relic))
            return -1000f;
        float score = 13f;
        if (KnownAncientChoiceBonuses.TryGetValue(relic.GetType().Name, out float known))
            score += known;
        if (runState != null)
            score += RouteAdjust(relic, runState.Map);
        return score;
    }

    /// <summary>选最优先古遗物：绝不主动选诅咒；若所有选项都是诅咒（理论上不会发生）则退回第一个。</summary>
    public static RelicModel PickBestAncientChoice(IReadOnlyList<RelicModel> options, RunState? runState)
    {
        RelicModel? best = null;
        float bestScore = float.MinValue;
        foreach (RelicModel relic in options)
        {
            if (IsAncientCurse(relic))
                continue;
            float score = ScoreAncientChoice(relic, runState);
            if (score > bestScore)
            {
                bestScore = score;
                best = relic;
            }
        }
        return best ?? options[0];
    }

    /// <summary>
    /// 路线感知调整：分析当前幕地图从第 0 行起前几行可到达节点的构成，
    /// 按遗物实际效果微调（金币→商店密度、精英加成→精英密度、小怪升级→小怪密度、
    /// 药水兜底→篝火密度、自由移动→恒定小加成）。地图在 Neow 之前已生成，能真实结合路线。
    /// </summary>
    private static float RouteAdjust(RelicModel relic, ActMap map)
    {
        if (map is NullActMap)
            return 0f;
        int monsters = 0, elites = 0, restSites = 0, shops = 0;
        HashSet<MapPoint> seen = [];
        Queue<(MapPoint Point, int Depth)> queue = [];
        foreach (MapPoint start in map.GetPointsInRow(0))
            queue.Enqueue((start, 0));
        const int depthLimit = 5;
        while (queue.Count > 0)
        {
            (MapPoint point, int depth) = queue.Dequeue();
            if (!seen.Add(point))
                continue;
            switch (point.PointType)
            {
                case MapPointType.Monster: monsters++; break;
                case MapPointType.Elite: elites++; break;
                case MapPointType.RestSite: restSites++; break;
                case MapPointType.Shop: shops++; break;
            }
            if (depth < depthLimit)
            {
                foreach (MapPoint child in point.Children)
                    queue.Enqueue((child, depth + 1));
            }
        }
        return relic.GetType().Name switch
        {
            // 金币：商店密度高时更值（能买到更多牌）。
            nameof(GoldenPearl) => shops >= 2 ? 1.5f : shops == 1 ? 0.5f : 0f,
            // 精英首回合 +2 抽 +1 能量：精英多时更值。
            nameof(BoomingConch) => elites >= 3 ? 1.5f : elites == 2 ? 0.5f : 0f,
            // 每 3 场小怪随机升级一张牌：小怪多时更值。
            nameof(FishingRod) => monsters >= 4 ? 1f : 0f,
            // 药水兜底：篝火少时更值。
            nameof(PhialHolster) => restSites < 2 ? 1f : 0f,
            // 3 次自由移动：对任意路线都有弹性价值。
            nameof(WingedBoots) => 0.5f,
            _ => 0f,
        };
    }
}
