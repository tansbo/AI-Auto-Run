using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
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

    /// <summary>先古遗物评分：诅咒类不再是"永不选"，而是按实际效果建模**净价值**（用户规则：
    /// 很多先古遗物带代价但有强正面效果——如 Neow 第三排 CursedPearl +333 金带 1 诅咒、HeftyTablet
    /// 稀有 3 选 1 带瘀伤、NeowsBones 2 遗物、SilverCrucible 3 次强化等）。未知诅咒仍不选（-1000）。</summary>
    public static float ScoreAncientChoice(RelicModel relic, RunState? runState)
    {
        if (IsAncientCurse(relic))
            return AncientCurseNet(relic, runState);
        float score = 13f;
        if (KnownAncientChoiceBonuses.TryGetValue(relic.GetType().Name, out float known))
            score += known;
        if (runState != null)
            score += RouteAdjust(relic, runState.Map);
        return score;
    }

    /// <summary>选最优先古遗物（含代价类）：全部按净价值比较取最大，不再排除诅咒列表。</summary>
    public static RelicModel PickBestAncientChoice(IReadOnlyList<RelicModel> options, RunState? runState)
    {
        RelicModel? best = null;
        float bestScore = float.MinValue;
        foreach (RelicModel relic in options)
        {
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
    /// 代价类先古遗物的净价值（分数口径与 CardPickerAI/ScoreAncientChoice 一致；常量随跑局数据校准）。
    /// 数值来自反编译实际效果（2026-09-02）：
    ///   CursedPearl +333金+1 Greed诅咒；DowsingRod 入组 Dowsing 卡；HeftyTablet 稀有卡3选1(可跳)+1 Injury；
    ///   LargeCapsule 2随机遗物+打击/防御各1；LeafyPoultice −12最大生命+转化2张基础牌；
    ///   NeowsBones 2遗物(不可跳)+1诅咒；NeowsSacrifice 龙涎香药水+Guilty诅咒；
    ///   PrecariousShears 移除2张(自选)+16不可挡伤；SilkenTress 清空当前金币、后续卡奖励附 Glam 附魔；
    ///   SilverCrucible 3 次卡奖励升级类强化。
    /// 金币 0.15/金、诅咒 14/张、最大生命 −2.5/点、随机遗物 ~11、稀有卡 ~16、移除 ~2/张（可除垃圾加分）、
    /// 转化基础牌按基础牌数量定值、掉血 ×血量风险权重（战士战后回血折扣）。
    /// </summary>
    private static float AncientCurseNet(RelicModel relic, RunState? runState)
    {
        Player? player = runState == null ? null : LocalContext.GetMe(runState);
        float hpFraction = player?.Creature != null && player.Creature.MaxHp > 0
            ? (float)player.Creature.CurrentHp / player.Creature.MaxHp
            : 0.7f;
        float hpWeight = Math.Clamp(1f + (0.5f - hpFraction) * 1.6f, 0.35f, 2.2f);
        float regen = (float)RunActContext.PassivePostCombatHeal(player);
        if (regen > 0f)
            hpWeight *= 0.85f;

        int basics = 0, removable = 0, curses = 0;
        if (player?.Deck != null)
        {
            foreach (CardModel card in player.Deck.Cards)
            {
                if (card.Rarity == CardRarity.Basic)
                    basics++;
                if (card.Rarity == CardRarity.Curse)
                    curses++;
                if (card.IsRemovable)
                    removable++;
            }
        }
        int gold = player?.Gold ?? 0;
        float goldScale = RunActContext.GoldValueScale(runState); // 金币价值随本幕商店可达性浮动。
        float transformBasics = basics > 0 ? Math.Min(2, basics) * 7f : 0f;
        float removalValue = 2f * Math.Min(2, removable) + (curses > 0 ? 8f : 0f);

        return relic.GetType().Name switch
        {
            nameof(CursedPearl) => 333f * 0.15f * goldScale - 14f,     // 333 金 − Greed 诅咒
            nameof(DowsingRod) => 6f,                              // Dowsing 卡入组（效果待考）
            nameof(HeftyTablet) => 16f - 14f,                      // 稀有 3 选 1（可跳）− Injury
            nameof(LargeCapsule) => 2f * 11f - 8f,                 // 2 随机遗物 − 打击/防御稀释
            nameof(LeafyPoultice) => -12f * 2.5f + transformBasics, // −12 最大生命 + 转化 2 基础
            nameof(NeowsBones) => 6f,                              // 2 遗物（不可跳）− 1 诅咒
            nameof(NeowsSacrifice) => 5f - 14f,                    // 龙涎香 − Guilty
            nameof(PrecariousShears) => removalValue - 16f * hpWeight, // 移除 2 − 16 不可挡
            nameof(SilkenTress) => 12f - gold * 0.15f * goldScale, // 附魔奖励 − 清空金币
            nameof(SilverCrucible) => 21f,                         // 3 次奖励强化
            _ => -1000f,                                           // 未知诅咒仍不选
        };
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
