using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Run;

/// <summary>浓度维度（浓度键）：DeckContext 可测的"牌组在某机制上的浓度"；None=未分类。</summary>
internal enum ConcentrationDim
{
    None,
    VulnerableApply, // 易伤：牌组 VulnerablePower 变量总量（施加层数）
    DoomApply,       // 灾厄/Doom：牌组 DoomPower 变量总量
    PoisonAmount,    // 毒：牌组 PoisonPower 变量总量
    Exhaust,         // 消耗：牌组 Exhaust 关键词卡数
    Ethereal,        // 虚灵：牌组 Ethereal 关键词卡数
    DrawDiscard,     // 抽/弃：Cards 变量总量 + 0.5×Sly(被弃触发)卡数
    SummonGen,       // 召唤/生成：Minion/Osty 标签 + Summon 变量卡数
    CardPlays,       // 出牌密度（0/1 费卡数代理）
    CostHigh,        // 高费（≥2 费卡数；本版无该维行）
    AttackCount,     // 攻击牌数（连击/攻击浓度代理）
    SkillCount,      // 技能牌数
    Orbs,            // 球/Evoke（无可靠牌组口径，未激活）
    Stars,           // 星（无统一可读键，未激活）
    Soul,            // Soul（无统一可读键/标签，未激活）
    BlockGain,       // 格挡获得事件（口径未可靠化，未激活）
    DebuffCount,     // Debuff 施加次数（口径未可靠化，未激活）
    EnergySpent,     // 能量消耗（需战斗态，未激活）
    PerTurn,         // 每回合触发（B 型引擎，走 v4 EngineBonus 口径，未激活）
}

/// <summary>引擎普查行：浓度维度 + 是否凸（浓度² 放坡）。</summary>
internal readonly record struct EngineCatalogRow(ConcentrationDim Dim, bool Convex);

/// <summary>
/// 浓度动态择优（第 5 条设计，2026-09-04）：把 .local/engine-catalog 全池普查（210 原始 / 剔除
/// MultiplayerOnly 10 后 200 单人条目）落库为显式 浓度键(id→维度+凸性) 表，并按"当前牌组浓度"给
/// 分档加分（凸型按浓度² 放坡）。
/// 口径（生成自 .local/picker-v5/gen-engine-rows.ps1，规则先命中先赢）：
///  triggerTrait/title/mechanism 含 易伤→VulnerableApply、Doom/灾厄→DoomApply、毒→PoisonAmount、
///  消耗/Exhaust→Exhaust、虚灵→Ethereal、弃/抽/draw→DrawDiscard、召唤/生成/Summon→SummonGen、
///  攻击/连击→AttackCount、技能→SkillCount、球/Orb→Orbs、星→Stars、每回合→PerTurn、Soul→Soul、
///  格挡→BlockGain、能量→EnergySpent 等；含多人(MultiplayerOnly)剔除；已由 v1-v4 专表处理的
///  DOMINATE/COLOSSUS/DEATHS_DOOR/COUNTDOWN 剔除（避免双计）。
/// 实测落库：映射 191 条；unclassified（宁缺毋滥不入表）6 条 = Omnislice/Hang/Melancholy/Arsenal/
///  FanOfKnives/Tracking；未激活维度（Orbs/Stars/Soul/BlockGain/DebuffCount/EnergySpent/PerTurn/
///  CostHigh 等 67 条）暂无可靠牌组口径 → 运行时计 0（文档如实说明，后续可激活）。
/// 匹配键 = 卡牌运行时类型名 CardModel.GetType().Name（Catalog id 是普查用的类型名变体，
///  OrdinalIgnoreCase 兼容），与 Id.Entry 斜杠大小写无关。
/// 数值口径：每维 profile = (cap, high)；rate = clamp(测度/high, 0, 1)；score = cap × (凸? rate² : rate)，
///  全部为保守小分（0..cap，cap≤5）可随语料重校。
/// </summary>
internal static class EngineConcentration
{
    /// <summary>当前激活（DeckContext 可真实测度）维度的 profile：维度 → (上限 cap, 高浓度标尺 high)。</summary>
    private static readonly Dictionary<ConcentrationDim, (float Cap, float High)> Profiles = new()
    {
        [ConcentrationDim.VulnerableApply] = (5f, 6f),  // 6 ≈ 开局级易伤施加总量（Bash 2 + Uppercut 级）
        [ConcentrationDim.DoomApply] = (5f, 10f),       // 10 ≈ 两三张灾厄卡一轮的量
        [ConcentrationDim.PoisonAmount] = (5f, 12f),    // 12 ≈ 毒卡一轮叠的量
        [ConcentrationDim.Exhaust] = (4f, 6f),          // 6 ≈ 消耗流初成
        [ConcentrationDim.Ethereal] = (4f, 6f),         // 6 ≈ 虚灵流初成
        [ConcentrationDim.DrawDiscard] = (4f, 6f),      // 6 ≈ 一张抽牌卡(Cards 3)+弃牌引擎
        [ConcentrationDim.SummonGen] = (4f, 5f),        // 5 ≈ 召唤系初成
        [ConcentrationDim.CardPlays] = (3f, 14f),       // 14 ≈ 0/1 费卡过半
        [ConcentrationDim.AttackCount] = (3f, 8f),      // 8 ≈ 攻击流
        [ConcentrationDim.SkillCount] = (3f, 10f),      // 10 ≈ 技能流
    };

    /// <summary>引擎普查表（由 .local 普查生成；键=运行时类型名，忽略大小写）。</summary>
    private static readonly Dictionary<string, EngineCatalogRow> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Accelerant"] = new EngineCatalogRow(ConcentrationDim.PoisonAmount, true),
        ["Accuracy"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["AdaptiveStrike"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["Afterimage"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["AGGRESSION"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["AllForOne"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["ASHENSTRIKE"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["Automation"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["BansheesCry"] = new EngineCatalogRow(ConcentrationDim.Ethereal, false),
        ["Barrage"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["BeatDown"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["BeatIntoShape"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["BiasedCognition"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["BlackHole"] = new EngineCatalogRow(ConcentrationDim.Stars, false),
        ["BlightStrike"] = new EngineCatalogRow(ConcentrationDim.VulnerableApply, false),
        ["BODYSLAM"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["Bolas"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["BubbleBubble"] = new EngineCatalogRow(ConcentrationDim.PoisonAmount, false),
        ["BULLY"] = new EngineCatalogRow(ConcentrationDim.VulnerableApply, false),
        ["Calamity"] = new EngineCatalogRow(ConcentrationDim.AttackCount, true),
        ["Calcify"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["Capacitor"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["ChildOfTheStars"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["Chill"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["Claw"] = new EngineCatalogRow(ConcentrationDim.CardPlays, true),
        ["CompileDriver"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["Conqueror"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["ConsumingShadow"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["Coolant"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["CorrosiveWave"] = new EngineCatalogRow(ConcentrationDim.PoisonAmount, true),
        ["CORRUPTION"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["CreativeAi"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["CrescentSpear"] = new EngineCatalogRow(ConcentrationDim.Stars, false),
        ["CRIMSONMANTLE"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["DanseMacabre"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["DARKEMBRACE"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["Darkness"] = new EngineCatalogRow(ConcentrationDim.Orbs, true),
        ["DeathMarch"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["Debilitate"] = new EngineCatalogRow(ConcentrationDim.VulnerableApply, false),
        ["DecisionsDecisions"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["Defragment"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["DEMONFORM"] = new EngineCatalogRow(ConcentrationDim.Orbs, true),
        ["DevourLife"] = new EngineCatalogRow(ConcentrationDim.Soul, false),
        ["Dirge"] = new EngineCatalogRow(ConcentrationDim.Soul, false),
        ["DISMANTLE"] = new EngineCatalogRow(ConcentrationDim.VulnerableApply, false),
        ["DoubleEnergy"] = new EngineCatalogRow(ConcentrationDim.EnergySpent, true),
        ["Dualcast"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["EchoForm"] = new EngineCatalogRow(ConcentrationDim.CardPlays, false),
        ["Eidolon"] = new EngineCatalogRow(ConcentrationDim.Ethereal, false),
        ["EndOfDays"] = new EngineCatalogRow(ConcentrationDim.DoomApply, false),
        ["Entropy"] = new EngineCatalogRow(ConcentrationDim.CardPlays, false),
        ["Envenom"] = new EngineCatalogRow(ConcentrationDim.PoisonAmount, true),
        ["Eradicate"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["EVILEYE"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["EXPECTAFIGHT"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["Fasten"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["FEELNOPAIN"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["Feral"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["FIENDFIRE"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["Finisher"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["Fisticuffs"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["FlakCannon"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["Flechettes"] = new EngineCatalogRow(ConcentrationDim.SkillCount, false),
        ["FocusedStrike"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["Ftl"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["Furnace"] = new EngineCatalogRow(ConcentrationDim.PerTurn, false),
        ["GangUp"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["Genesis"] = new EngineCatalogRow(ConcentrationDim.Stars, false),
        ["GeneticAlgorithm"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["Glacier"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["GoldAxe"] = new EngineCatalogRow(ConcentrationDim.CardPlays, true),
        ["GrandFinale"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["Hailstorm"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["Haunt"] = new EngineCatalogRow(ConcentrationDim.Soul, false),
        ["HeavenlyDrill"] = new EngineCatalogRow(ConcentrationDim.EnergySpent, true),
        ["HelixDrill"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["HELLRAISER"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["Hotfix"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["HOWLFROMBEYOND"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["IceLance"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["Impatience"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["INFERNO"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["InfiniteBlades"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["Iteration"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["JUGGERNAUT"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["JUGGLING"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["KinglyKick"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["KinglyPunch"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, true),
        ["KnifeTrap"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["LightningRod"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["Loop"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["LunarBlast"] = new EngineCatalogRow(ConcentrationDim.SkillCount, false),
        ["MachineLearning"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["MakeItSo"] = new EngineCatalogRow(ConcentrationDim.SkillCount, false),
        ["Mayhem"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["MementoMori"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["MeteorStrike"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["MindBlast"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["Mirage"] = new EngineCatalogRow(ConcentrationDim.PoisonAmount, false),
        ["Misery"] = new EngineCatalogRow(ConcentrationDim.DebuffCount, true),
        ["MOLTENFIST"] = new EngineCatalogRow(ConcentrationDim.VulnerableApply, true),
        ["MomentumStrike"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["MonarchsGaze"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["Monologue"] = new EngineCatalogRow(ConcentrationDim.PerTurn, false),
        ["MultiCast"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["Murder"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["NecroMastery"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["NoEscape"] = new EngineCatalogRow(ConcentrationDim.DoomApply, false),
        ["NoxiousFumes"] = new EngineCatalogRow(ConcentrationDim.PoisonAmount, true),
        ["Oblivion"] = new EngineCatalogRow(ConcentrationDim.DoomApply, false),
        ["Orbit"] = new EngineCatalogRow(ConcentrationDim.EnergySpent, false),
        ["Outbreak"] = new EngineCatalogRow(ConcentrationDim.PoisonAmount, false),
        ["PACTSEND"] = new EngineCatalogRow(ConcentrationDim.Exhaust, true),
        ["Pagestorm"] = new EngineCatalogRow(ConcentrationDim.Ethereal, false),
        ["PaleBlueDot"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["Panache"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["Parry"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["PERFECTEDSTRIKE"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["PILLAGE"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, true),
        ["PillarOfCreation"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["Pinpoint"] = new EngineCatalogRow(ConcentrationDim.SkillCount, false),
        ["PreciseCut"] = new EngineCatalogRow(ConcentrationDim.CardPlays, false),
        ["PrepTime"] = new EngineCatalogRow(ConcentrationDim.PerTurn, false),
        ["Prolong"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["Protector"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["PullFromBelow"] = new EngineCatalogRow(ConcentrationDim.Ethereal, false),
        ["Quadcast"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["Radiate"] = new EngineCatalogRow(ConcentrationDim.Stars, false),
        ["RAGE"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["Rainbow"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["Rattle"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["ReaperForm"] = new EngineCatalogRow(ConcentrationDim.DoomApply, false),
        ["Reflect"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["Reflex"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["Refract"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["Rend"] = new EngineCatalogRow(ConcentrationDim.VulnerableApply, false),
        ["Restlessness"] = new EngineCatalogRow(ConcentrationDim.EnergySpent, false),
        ["RightHandHand"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["RollingBoulder"] = new EngineCatalogRow(ConcentrationDim.PerTurn, true),
        ["RUPTURE"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["Sacrifice"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["Scrape"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["SECONDWIND"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["SeekingEdge"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["SerpentForm"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["Shatter"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["ShiningStrike"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["Shroud"] = new EngineCatalogRow(ConcentrationDim.DoomApply, false),
        ["SicEm"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["SignalBoost"] = new EngineCatalogRow(ConcentrationDim.SkillCount, false),
        ["SleightOfFlesh"] = new EngineCatalogRow(ConcentrationDim.DebuffCount, false),
        ["Smokestack"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["SoulStorm"] = new EngineCatalogRow(ConcentrationDim.Soul, false),
        ["SpectrumShift"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["Speedster"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["Spinner"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["SpiritOfAsh"] = new EngineCatalogRow(ConcentrationDim.Ethereal, false),
        ["SPITE"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["Squeeze"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["STAMPEDE"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["Stardust"] = new EngineCatalogRow(ConcentrationDim.Stars, false),
        ["STOMP"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["Storm"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["StormOfSteel"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["Strangle"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["Stratagem"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["Subroutine"] = new EngineCatalogRow(ConcentrationDim.SkillCount, false),
        ["SummonForth"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["Supermassive"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["SwordSage"] = new EngineCatalogRow(ConcentrationDim.CardPlays, false),
        ["Synchronize"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["Tactician"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["TEARASUNDER"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["Tempest"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["TeslaCoil"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["TheScythe"] = new EngineCatalogRow(ConcentrationDim.CardPlays, false),
        ["TheSealedThrone"] = new EngineCatalogRow(ConcentrationDim.Stars, false),
        ["THRASH"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["ThrummingHatchet"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["Thunder"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["TimesUp"] = new EngineCatalogRow(ConcentrationDim.DoomApply, false),
        ["ToolsOfTheTrade"] = new EngineCatalogRow(ConcentrationDim.DrawDiscard, false),
        ["TrashToTreasure"] = new EngineCatalogRow(ConcentrationDim.Orbs, false),
        ["Tyranny"] = new EngineCatalogRow(ConcentrationDim.Exhaust, false),
        ["Unleash"] = new EngineCatalogRow(ConcentrationDim.SummonGen, false),
        ["UNMOVABLE"] = new EngineCatalogRow(ConcentrationDim.BlockGain, false),
        ["VICIOUS"] = new EngineCatalogRow(ConcentrationDim.VulnerableApply, false),
        ["VoidForm"] = new EngineCatalogRow(ConcentrationDim.Ethereal, false),
        ["Volley"] = new EngineCatalogRow(ConcentrationDim.AttackCount, false),
        ["Voltaic"] = new EngineCatalogRow(ConcentrationDim.Orbs, true),
    };

    /// <summary>维度是否已激活（有可靠牌组测度）。</summary>
    private static bool IsMeasured(ConcentrationDim dim) => Profiles.ContainsKey(dim);

    /// <summary>候选的浓度动态加分（并入 Evaluate）：命中普查表、维度已激活且牌组浓度 &gt; 0 时
    /// 按 浓度→cap 分档（凸型浓度² 放坡）。</summary>
    public static float ConcentrationBonus(CardModel card, DeckContext context)
    {
        string key = card.GetType().Name;
        if (!Catalog.TryGetValue(key, out EngineCatalogRow row) || !IsMeasured(row.Dim))
            return 0f;
        float measure = context.ConcentrationOf(row.Dim);
        if (measure <= 0f)
            return 0f;
        (float cap, float high) = Profiles[row.Dim];
        float rate = Math.Min(1f, measure / high);
        return cap * (row.Convex ? rate * rate : rate);
    }

    /// <summary>
    /// 同机制上行指数（协同上行，仅用于 3 选 1 平局取舍，不直接进 Evaluate）：候选=普查行且维度激活时，
    /// 上行 = 牌组内同机制伙伴数×2 + 本职业池中同维度普查条目数；否则 0。近似衡量"继续走这条路还有多少
    /// 同机制卡可拿"，用于相近分时偏好高上行（避免只看远景乱抓，触发另受 PickBest 门槛约束）。
    /// </summary>
    public static int UpsideIndex(string typeName, string role, DeckContext context)
    {
        if (!Catalog.TryGetValue(typeName, out EngineCatalogRow row) || !IsMeasured(row.Dim))
            return 0;
        int upside = 0;
        if (context.Deck != null)
        {
            foreach (CardModel card in context.Deck)
            {
                if (card.GetType().Name == typeName)
                    continue;
                if (Catalog.TryGetValue(card.GetType().Name, out EngineCatalogRow partner) && partner.Dim == row.Dim)
                    upside += 2;
            }
        }
        return upside + PoolDimDensity(role, row.Dim);
    }

    /// <summary>职业池内同维度普查条目数（静态懒缓存：ModelDb.AllCharacters→CardPool.AllCards 扫一遍）。</summary>
    private static int PoolDimDensity(string role, ConcentrationDim dim)
    {
        EnsurePoolCache();
        return _poolDims.TryGetValue(role, out Dictionary<ConcentrationDim, int>? byDim)
            && byDim.TryGetValue(dim, out int count)
            ? count
            : 0;
    }

    private static readonly Dictionary<string, Dictionary<ConcentrationDim, int>> _poolDims = new(StringComparer.Ordinal);
    private static bool _poolBuilt;

    private static void EnsurePoolCache()
    {
        if (_poolBuilt)
            return;
        foreach (CharacterModel character in ModelDb.AllCharacters)
        {
            string role = character.GetType().Name.ToUpperInvariant();
            var byDim = new Dictionary<ConcentrationDim, int>();
            foreach (CardModel card in character.CardPool.AllCards)
            {
                if (Catalog.TryGetValue(card.GetType().Name, out EngineCatalogRow row) && IsMeasured(row.Dim))
                {
                    byDim.TryGetValue(row.Dim, out int prior);
                    byDim[row.Dim] = prior + 1;
                }
            }
            _poolDims[role] = byDim;
        }
        _poolBuilt = true;
    }
}
