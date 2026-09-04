using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 选牌智能：对战后卡牌奖励候选做结构评分，决定选哪张或跳过。
/// 只读不可变数据（CardModel/Player/RunState），无模拟、无副作用、主线程执行。
/// 评分 = 稀有度 + 费用效率 + 类型契合 + 关键词 + AOE/格挡覆盖 + 重复牌惩罚 + 牌组膨胀 + 精选表加成
///      + 卡组体系契合（同一机械轴在牌组里已成型时，同轴牌加分）
///      + 数据驱动（A10 胜率差，已含角色池中位，见 CardWinStats）
///      + 四维补位（攻击/防御/回费/过牌 画像合计 vs 分幕需求向量的缺口覆盖，见 <see cref="DeckGapFill"/>）
///      + 联动互乘（施加×依赖 / 回费×大费，见 <see cref="SynergyAmplify"/>）
///      + 本幕 Boss 取向（见 <see cref="BossAdjust"/>）。
/// 最高分低于 <see cref="SkipThreshold"/> 时返回 null（跳过）。
/// </summary>
internal static class CardPickerAI
{
    internal const float SkipThreshold = 9f;

    /// <summary>少数关键成型牌/废牌的手写加成。key 是卡牌 Id.Entry（实测为大写，如 "BATTLE_TRANCE"）；
    /// 用 OrdinalIgnoreCase 兼容手写的小写 key。注意：这些加成在修复前从未命中过（实测 Id.Entry 是大写）。</summary>
    private static readonly Dictionary<string, float> KnownCardBonuses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["battle_trance"] = 16f,   // 战吼：强过牌
        ["uppercut"] = 13f,        // 上勾拳：易伤 + 虚弱双负面
        ["crimson_mantle"] = 11f,  // 猩红斗篷：格挡成长
        ["anger"] = 10f,           // 愤怒：廉价多次打击
        ["armaments"] = 10f,       // 武装：批量升级
    };

    /// <summary>返回要选的卡；分数不足或不适合时返回 null（跳过）。</summary>
    public static CardModel? PickBest(IReadOnlyList<CardModel> options, Player? player, RunState? runState)
    {
        if (options.Count == 0)
            return null;
        DeckContext context = DeckContext.From(player, runState);
        CardModel? best = null;
        float bestScore = float.MinValue;
        foreach (CardModel card in options)
        {
            if (card.Type is CardType.Status or CardType.Curse)
                continue;
            float score = Evaluate(card, context);
            if (score > bestScore)
            {
                bestScore = score;
                best = card;
            }
        }
        return best != null && bestScore >= SkipThreshold ? best : null;
    }

    public static float Evaluate(CardModel card, DeckContext context)
    {
        float score = 0f;

        // 稀有度。
        score += card.Rarity switch
        {
            CardRarity.Rare => 14f,
            CardRarity.Uncommon => 7f,
            CardRarity.Common => 0f,
            CardRarity.Basic => -4f,
            _ => -2f,
        };

        // 费用效率。
        if (card.EnergyCost.CostsX)
            score -= 2f;
        else if (card.EnergyCost.Canonical <= 0)
            score += 8f;
        else if (card.EnergyCost.Canonical == 1)
            score += 5f;
        else if (card.EnergyCost.Canonical == 2)
            score += 0f;
        else if (card.EnergyCost.Canonical == 3)
            score -= 6f;
        else
            score -= 12f;

        // 类型契合。
        bool isAttack = card.Type == CardType.Attack;
        if (isAttack)
        {
            if (context.AttackRatio < 0.30f)
                score += 9f;
            if (context.ActIndex == 0)
                score += 6f;
        }
        if (card.GainsBlock && context.BlockRatio < 0.20f)
            score += 8f;
        if (card.Type == CardType.Power && context.PowerCount == 0)
            score += 7f;

        // 关键词。
        if (card.Keywords.Contains(CardKeyword.Retain))
            score += 4f;
        if (card.Keywords.Contains(CardKeyword.Innate))
            score += 3f;
        if (card.Keywords.Contains(CardKeyword.Ethereal))
            score -= 6f;
        if (card.Keywords.Contains(CardKeyword.Unplayable))
            score -= 20f;
        if (card.Keywords.Contains(CardKeyword.Sly))
            score += 2f;
        if (card.Keywords.Contains(CardKeyword.Exhaust) && card.Type == CardType.Power)
            score += 5f;

        // AOE 覆盖：多目标战斗没有 AOE 时高价值。
        bool isAoE = card.TargetType is TargetType.AllEnemies or TargetType.RandomEnemy;
        if (isAoE && context.AoECount == 0)
            score += context.ActIndex == 1 ? 10f : 6f;

        // 重复牌惩罚：同一张牌 3 张以上边际收益极低。
        if (context.CountOf(card) >= 3)
            score -= 10f;

        // 牌组膨胀。
        if (context.DeckSize >= 30)
            score -= 1f;

        // 精选表。
        if (KnownCardBonuses.TryGetValue(card.Id.Entry, out float known))
            score += known;

        // 低血量防御倾向。
        if (context.HpRatio < 0.4f && card.GainsBlock)
            score += 5f;

        // 成型度/冗余衰减（用户规则）：角色某维度已饱和时，多余的同类牌收益递减——会稀释成型卡组。
        // 攻击：攻击占比过半且牌组≥15；防御：格挡牌过半且牌组≥15；能力：已有 5+ 张 Power。
        if (context.DeckSize >= 15)
        {
            if (card.Type == CardType.Attack && context.AttackRatio > 0.5f)
                score -= 3f;
            if (card.GainsBlock && context.BlockRatio > 0.55f)
                score -= 3f;
            if (card.Type == CardType.Power && context.PowerCount >= 5)
                score -= 2f;
        }

        // 数据驱动（Spire Codex A10 真实对局）：同池卡对照自身池中位；跨池/无色卡（万花筒/海玻璃/
        // 棱彩宝石/色彩哲学家等渠道可拿到他职业或无色牌）对照"接收职业中位"，衡量这张外来卡对
        // 当前职业的相对强度——他职业高胜率牌对本职业可能极有价值（见 CardWinStats.BonusFor）。
        score += CardWinStats.BonusFor(card.Id.Entry, context.ReceivingRole);

        // 卡组体系契合：候选属于某机械轴，且牌组里该轴已成型（有 ≥1 张同轴牌）时加分。
        // 机械轴全部取自卡牌自身的可读数据（关键词/标签/DynamicVar/费用），不是手写卡表：
        // 比如牌组已在攒易伤/亡魂(Doom)/小刀(Shiv)/毒/力量/过牌引擎时，同轴新牌更值；
        // 轴越深加分越高，封顶避免无脑堆同一轴。未启动的轴不加分（不会硬塞引擎）。
        score += EngineSynergy(card, context);

        // 语义配合（跨职业价值补充）：引擎×终结 型组合（免费攻击引擎×七星、自动攻击×华丽收场等），
        // 同轴检测覆盖不到；条目见 CardComboProfiles（仅收录反编译机制核对过的方向）。
        score += CardComboProfiles.Bonus(card.Id.Entry, context);

        // 四维补位（2026-09-04 起取代 DeckGapBonus）：攻击/防御/回费/过牌四维画像合计 vs 分幕需求向量
        // 逐维比缺口，缺口存在时按"该候选能补的部分 ÷ 缺口"比例 × 每维小权重；只补不扣、封顶 10 防乱抓。
        // 数值口径与依据见 DeckGapFill 注释（启发式、保守、可解释）。
        score += DeckGapFill(card, context);

        // 联动互乘（SynergyAmplify，放在补位分后）：候选的"单卡价值"会随牌组已有能力被放大——
        // ①施加×依赖：牌组已能稳定施加易伤时，收益随易伤成长的卡（DOMINATE/COLOSSUS）升值；
        // ②回费×大费：牌组回费/免费卡充足时，≥3 费攻击的可打窗口更大。依据见 SynergyAmplify 注释。
        score += SynergyAmplify(card, context);

        // 本幕 Boss 取向：Boss 机制决定哪些卡更值（Vantom 墨影幻灵=每实例减免→多段/廉价攻击升值；
        // TestSubject 实验体=技能税→技能贬值）。规则与依据见 CardComboProfiles 同源 decomp 取证。
        score += BossAdjust(card, context);

        return score;
    }

    /// <summary>
    /// 本幕 Boss 取向修正（仅收录反编译机制核对的取向）：
    /// ①InstanceCapped（Vantom 墨影幻灵，SlipperyPower：每个伤害实例封顶 1 并逐实例扣 8/9 层）：
    ///   多段(Repeat 变量)/0-1 费攻击 +4；≥3 费单发攻击 −4（大单发在层数击穿前≈每击 1 伤）。
    /// ②SkillTax（TestSubject 实验体，EnragePower：玩家每打一张技能牌它 +力量）：技能候选 −3。
    /// </summary>
    private static float BossAdjust(CardModel card, DeckContext context)
    {
        switch (context.ActBoss)
        {
            case ActBossKind.InstanceCapped:
                if (card.Type != CardType.Attack || card.EnergyCost.CostsX)
                    return 0f;
                bool multiHit = card.DynamicVars.ContainsKey(RepeatVarName);
                if (multiHit || card.EnergyCost.Canonical <= 1)
                    return 4f;
                return card.EnergyCost.Canonical >= 3 ? -4f : 0f;
            case ActBossKind.SkillTax:
                return card.Type == CardType.Skill ? -3f : 0f;
            default:
                return 0f;
        }
    }

    /// <summary>
    /// 四维补位加分（2026-09-04 起取代 DeckGapBonus，fixture 相应重校）：
    /// 牌组四维画像合计（攻击/防御/回费/过牌，见 <see cref="CardAbilityProfileReader"/>）对比"分幕需求向量"，
    /// 每个维度有缺口（合计 &lt; 需求）时，把候选在该维能补的量除以缺口、按覆盖比例 × 小权重给分。
    /// 只补不扣（永远不因已饱和扣分——饱和后的冗余衰减由上方"冗余衰减"处理）、合计封顶 <see cref="MaxGapBonus"/>，
    /// 避免牌组已够用时还无脑乱抓同维卡。
    /// 需求口径（启发式，数值保守、可解释）：需求 = 幕基值 × 牌组规模缩放。
    ///  幕基值按 CurrentActIndex：act0（第 1 幕早期）= 攻击/防御双高（开局先能活下来打死小怪）；
    ///  act1（成长期）= 攻击 + 过牌（精英/Boss 斩杀层 + 找关键牌）；act≥2（中后段/终局）= 防御抬升 + 高攻击
    ///  （终局重视攻击强度），回费只在成长期给少量需求（能量往往由卡自身合计隐含）。
    ///  规模缩放 = clamp(DeckSize/12, 0.8, 1.5)：牌越多一轮能打出的量越大，需求相应抬高；小牌组不急着补位。
    /// </summary>
    private static float DeckGapFill(CardModel card, DeckContext context)
    {
        // 已 ≥3 张同名：候选是"重复卡"而非补位卡——重复惩罚（−10）已覆盖其边际价值，
        // 同一能力已在牌组超额存在，再给补位分只会把第 4/5/6 张同卡误当缺口填充（启发式防呆）。
        if (context.CountOf(card) >= 3)
            return 0f;
        CardAbilityProfile profile = CardAbilityProfileReader.Of(card);
        if (profile.IsZero)
            return 0f; // 候选四维全 0（纯工具/状态/诅咒式卡），没有任何维度可补
        DeckAbilityTotals totals = context.AbilityTotals; // 惰性统计一次，同一决策所有候选共享

        (float needAttack, float needDefense, float needEnergy, float needDraw) = context.ActIndex switch
        {
            0 => (38f, 26f, 0f, 0f),
            1 => (46f, 24f, 1f, 4f),
            _ => (58f, 34f, 0f, 2f),
        };
        float scale = Math.Clamp(context.DeckSize / 12f, 0.8f, 1.5f);
        needAttack *= scale;
        needDefense *= scale;
        needEnergy *= scale;
        needDraw *= scale;

        float bonus = 0f;
        bonus += GapCover(needAttack, totals.AttackTotal, profile.Attack, AttackGapWeight);
        bonus += GapCover(needDefense, totals.DefenseTotal, profile.Defense, DefenseGapWeight);
        bonus += GapCover(needEnergy, totals.EnergyTotal, profile.Energy, EnergyGapWeight);
        bonus += GapCover(needDraw, totals.DrawTotal, profile.Draw, DrawGapWeight);
        return Math.Min(MaxGapBonus, bonus);
    }

    /// <summary>单维缺口覆盖分：缺口 = max(0, 需求 − 牌组合计)；候选补上 min(缺口, 候选值)，
    /// 按覆盖比例（补上/缺口）× 小权重。缺口很小而候选能整口补上时给满权重（"差一口气就够"最值）。</summary>
    private static float GapCover(float need, float deckTotal, float candidateValue, float weight)
    {
        if (need <= 0f || candidateValue <= 0f)
            return 0f;
        float deficit = Math.Max(0f, need - deckTotal);
        if (deficit <= 0f)
            return 0f;
        float covered = Math.Min(deficit, candidateValue);
        return weight * (covered / deficit);
    }

    /// <summary>
    /// 联动互乘加成（2026-09-04 新增，放在补位分后）：候选的边际价值会被牌组已有能力放大，两条规则：
    /// ① 施加 × 依赖：牌组 DeckVarCount("VulnerablePower") &gt; 0（已能稳定施加易伤）时，
    ///    依赖表内候选 +5——这些卡的收益随"目标身上已有易伤"成长，先有施加源才值得拿：
    ///    DOMINATE（主宰）：打出时先施加 Vulnerable 1，再按目标当前易伤层数给力量
    ///      （decomp MegaCrit.Sts2.Core.Models.Cards/Dominate.cs OnPlay L40-42：PowerCmd.Apply&lt;VulnerablePower&gt;
    ///      之后 num = target.GetPower&lt;VulnerablePower&gt;()?.Amount，再 Apply&lt;StrengthPower&gt;(num)）——
    ///      牌组能前置易伤（如 Bash/Uppercut 先叠层）时一次可拿多力量，单张自施只有 1。
    ///    COLOSSUS（巨像）：挂 ColossusPower，其 ModifyDamageMultiplicative 让"带易伤的攻击者"对玩家伤害 ×0.5
    ///      （decomp MegaCrit.Sts2.Core.Models.Powers/ColossusPower.cs L27-46；卡本身 GainsBlock + Block 4，
    ///      见 Colossus.cs L32-37）——防御收益随牌组易伤能力成长，确认后进表。
    ///    其余依赖易伤/虚弱/摧残的卡未逐一核对机制，不收录（宁可漏判不误导）。
    /// ② 回费 × 大费：≥3 费（非 X）攻击候选，在牌组回费能力充足（带 "Energy" 变量的卡存在，或 0 费卡 ≥3）
    ///    时 +3——能量变量=真净产出，0 费卡多=把能量留给大牌的打法窗口大（读 lazy 统计，见
    ///    <see cref="DeckContext.AbilityTotals"/> 与 DeckVarCount("Energy")）。
    /// </summary>
    private static float SynergyAmplify(CardModel card, DeckContext context)
    {
        float bonus = 0f;
        if (VulnerableDependentCards.TryGetValue(card.Id.Entry, out float dependentBonus)
            && context.DeckVarCount(VulnerableVarKey) > 0)
            bonus += dependentBonus;
        if (card.Type == CardType.Attack
            && !card.EnergyCost.CostsX
            && card.EnergyCost.Canonical >= 3)
        {
            DeckAbilityTotals totals = context.AbilityTotals;
            if (context.DeckVarCount(CardAbilityProfileReader.EnergyVarKey) > 0 || totals.ZeroCostCards >= 3)
                bonus += BigAttackEnergyBonus;
        }
        return bonus;
    }

    /// <summary>候选卡与牌组既有机械轴的契合加成：每命中一个已启动的轴 +2×min(3, 同轴数)。</summary>
    private static float EngineSynergy(CardModel card, DeckContext context)
    {
        float bonus = 0f;
        if (card.Keywords.Contains(CardKeyword.Exhaust))
            bonus += AxisBonus(context.ExhaustAxis);
        if (card.Tags.Contains(CardTag.Shiv))
            bonus += AxisBonus(context.ShivAxis);
        if (card.Tags.Contains(CardTag.Minion))
            bonus += AxisBonus(context.MinionAxis);
        if (card.Tags.Contains(CardTag.OstyAttack))
            bonus += AxisBonus(context.OstyAxis);
        if (card.DynamicVars.ContainsKey(PoisonVarName))
            bonus += AxisBonus(context.PoisonAxis);
        if (card.DynamicVars.ContainsKey(DoomVarName))
            bonus += AxisBonus(context.DoomAxis);
        if (card.DynamicVars.ContainsKey(StrengthVarName))
            bonus += AxisBonus(context.StrengthAxis);
        if (card.DynamicVars.ContainsKey(DrawVarCards) || card.DynamicVars.ContainsKey(DrawVarEnergy))
            bonus += AxisBonus(context.DrawAxis);
        if (IsZeroCost(card))
            bonus += AxisBonus(context.ZeroCostAxis);
        return bonus;
    }

    private static float AxisBonus(int deckAxisCount)
        => deckAxisCount <= 0 ? 0f : 2f * Math.Min(3, deckAxisCount);

    private static bool IsZeroCost(CardModel card)
        => !card.EnergyCost.CostsX && card.EnergyCost.Canonical <= 0;

    private const string PoisonVarName = "PoisonPower";
    private const string DoomVarName = "DoomPower";
    private const string StrengthVarName = "StrengthPower";
    private const string DrawVarCards = "Cards";
    private const string DrawVarEnergy = "Energy";
    private const string RepeatVarName = "Repeat";

    /// <summary>易伤施加能力在牌组画像里的 DynamicVar 键（PowerVar&lt;VulnerablePower&gt; 的键 = 类型名，见 decomp PowerVar.cs / DynamicVarSet.cs）。</summary>
    private const string VulnerableVarKey = "VulnerablePower";

    /// <summary>四维补位：每维小权重与合计封顶（合计 ~0..10，封顶防乱抓）。</summary>
    private const float AttackGapWeight = 4f;
    private const float DefenseGapWeight = 4f;
    private const float EnergyGapWeight = 3f;
    private const float DrawGapWeight = 3.5f;
    private const float MaxGapBonus = 10f;

    /// <summary>联动互乘 ②：≥3 费攻击在回费充足牌组里的加成。</summary>
    private const float BigAttackEnergyBonus = 3f;

    /// <summary>联动互乘 ①：收益随"目标已带易伤"成长的候选（牌组已能稳定施加易伤时 +5）。decomp 依据见 SynergyAmplify。</summary>
    private static readonly Dictionary<string, float> VulnerableDependentCards = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DOMINATE"] = 5f, // 主宰：按目标易伤层数给力量（Dominate.cs OnPlay L40-42）
        ["COLOSSUS"] = 5f, // 巨像：易伤攻击者对玩家伤害 ×0.5 的防御成长（ColossusPower.cs L27-46）
    };
}

/// <summary>当前幕 Boss 的"卡牌价值取向"分类（由 runState.Act.BossEncounter 类型映射，开局已掷定）。</summary>
internal enum ActBossKind
{
    /// <summary>无已知取向（非本表 Boss/未知）。</summary>
    None,

    /// <summary>每伤害实例减免型（Vantom 墨影幻灵：SlipperyPower 把每个实例封顶 1 并逐实例扣层）
    /// → 多段/廉价攻击价值升、单发大伤害价值降。</summary>
    InstanceCapped,

    /// <summary>技能税型（TestSubject 实验体：玩家每打一张技能牌它 +力量）
    /// → 技能类候选价值降。</summary>
    SkillTax,
}

/// <summary>一次选牌决策的牌组画像，构造一次后复用。</summary>
internal sealed class DeckContext
{
    public IReadOnlyList<CardModel>? Deck { get; }
    public int DeckSize { get; }
    public int AttackCount { get; }
    public int PowerCount { get; }
    public int BlockCardCount { get; }
    public int AoECount { get; }
    /// <summary>牌组里 ≥2 费（非 X 费）的攻击牌数：衡量是否已有"高伤/终结"层。</summary>
    public int HeavyAttackCount { get; }
    public float HpRatio { get; }
    public int ActIndex { get; }

    /// <summary>接收职业（当前玩家的角色类名大写，如 IRONCLAD/SILENT/DEFECT/NECROBINDER/REGENT；未知为空）。
    /// 供 CardWinStats.BonusFor 判断跨池/无色卡对当前职业的相对强度。</summary>
    public string ReceivingRole { get; }

    /// <summary>当前幕 Boss 取向（由 runState.Act.BossEncounter 映射；无 runState/未知时 None）。</summary>
    public ActBossKind ActBoss { get; }

    /// <summary>机械轴同轴计数：牌组里已有多少张同关键词/标签/DynamicVar/零费牌（体系成型度）。</summary>
    public int ExhaustAxis { get; }
    public int ShivAxis { get; }
    public int MinionAxis { get; }
    public int OstyAxis { get; }
    public int PoisonAxis { get; }
    public int DoomAxis { get; }
    public int StrengthAxis { get; }
    public int DrawAxis { get; }
    public int ZeroCostAxis { get; }

    private readonly Dictionary<string, int> _cardCounts = new(StringComparer.Ordinal);

    public float AttackRatio => DeckSize == 0 ? 0f : (float)AttackCount / DeckSize;
    public float BlockRatio => DeckSize == 0 ? 0f : (float)BlockCardCount / DeckSize;

    private DeckContext(
        IReadOnlyList<CardModel>? deck,
        int deckSize,
        int attackCount,
        int powerCount,
        int blockCardCount,
        int aoECount,
        float hpRatio,
        int actIndex,
        string receivingRole,
        ActBossKind actBoss,
        int exhaustAxis,
        int shivAxis,
        int minionAxis,
        int ostyAxis,
        int poisonAxis,
        int doomAxis,
        int strengthAxis,
        int drawAxis,
        int zeroCostAxis,
        int heavyAttackCount)
    {
        Deck = deck;
        DeckSize = deckSize;
        AttackCount = attackCount;
        PowerCount = powerCount;
        BlockCardCount = blockCardCount;
        AoECount = aoECount;
        HpRatio = hpRatio;
        ActIndex = actIndex;
        ReceivingRole = receivingRole;
        ActBoss = actBoss;
        ExhaustAxis = exhaustAxis;
        ShivAxis = shivAxis;
        MinionAxis = minionAxis;
        OstyAxis = ostyAxis;
        PoisonAxis = poisonAxis;
        DoomAxis = doomAxis;
        StrengthAxis = strengthAxis;
        DrawAxis = drawAxis;
        ZeroCostAxis = zeroCostAxis;
        HeavyAttackCount = heavyAttackCount;
    }

    private static ActBossKind BossKindOf(RunState? runState)
    {
        if (runState == null || runState.Act == null || runState.Act.BossEncounter == null)
            return ActBossKind.None;
        return runState.Act.BossEncounter.GetType().Name switch
        {
            nameof(MegaCrit.Sts2.Core.Models.Encounters.VantomBoss) => ActBossKind.InstanceCapped,
            nameof(MegaCrit.Sts2.Core.Models.Encounters.TestSubjectBoss) => ActBossKind.SkillTax,
            _ => ActBossKind.None,
        };
    }

    public int CountOf(CardModel card)
        => _cardCounts.TryGetValue(card.Id.Entry, out int count) ? count : 0;

    /// <summary>牌组里带指定 DynamicVar 键（如 "VulnerablePower"/"WeakPower"/"FrailPower"/"Damage"…）的牌数，
    /// 惰性统计并缓存：衡量"稳定施加某减益/某能力族"的体系强度（主线程调用）。</summary>
    public int DeckVarCount(string varKey)
    {
        if (_deckVarCounts == null)
        {
            _deckVarCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (Deck != null)
            {
                foreach (CardModel card in Deck)
                {
                    foreach (KeyValuePair<string, MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar> pair in card.DynamicVars)
                    {
                        _deckVarCounts.TryGetValue(pair.Key, out int priorCount);
                        _deckVarCounts[pair.Key] = priorCount + 1;
                    }
                }
            }
        }
        return _deckVarCounts.TryGetValue(varKey, out int count) ? count : 0;
    }

    private Dictionary<string, int>? _deckVarCounts;

    /// <summary>
    /// 牌组"四维能力"合计（攻击/防御/回费/过牌逐卡求和 + 0 费卡数），惰性统计一次并缓存：
    /// 同一决策的所有候选（Evaluate 多次调用）共享同一份合计，避免每候选重复扫整副牌（主线程调用）。
    /// </summary>
    public DeckAbilityTotals AbilityTotals
    {
        get
        {
            if (_abilityTotals == null)
            {
                if (Deck == null)
                {
                    _abilityTotals = DeckAbilityTotals.Empty;
                }
                else
                {
                    float attack = 0f, defense = 0f, energy = 0f, draw = 0f;
                    int zeroCost = 0;
                    foreach (CardModel card in Deck)
                    {
                        CardAbilityProfile p = CardAbilityProfileReader.Of(card);
                        attack += p.Attack;
                        defense += p.Defense;
                        energy += p.Energy;
                        draw += p.Draw;
                        if (!card.EnergyCost.CostsX && card.EnergyCost.Canonical == 0)
                            zeroCost++;
                    }
                    _abilityTotals = new DeckAbilityTotals(attack, defense, energy, draw, zeroCost);
                }
            }
            return _abilityTotals.Value;
        }
    }

    private DeckAbilityTotals? _abilityTotals;

    /// <summary>牌组是否已含任一 Id.Entry（任意同名一张即可），供语义配合表查牌组构成。</summary>
    public bool ContainsAny(IEnumerable<string> entries)
    {
        foreach (string entry in entries)
        {
            if (_cardCounts.TryGetValue(entry, out int count) && count > 0)
                return true;
        }
        return false;
    }

    public static DeckContext From(Player? player, RunState? runState)
    {
        IReadOnlyList<CardModel>? deck = player == null ? null : PileType.Deck.GetPile(player).Cards;
        string receivingRole = player?.Character == null
            ? string.Empty
            : player.Character.GetType().Name.ToUpperInvariant();
        if (deck == null)
            return new DeckContext(null, 0, 0, 0, 0, 0, 1f, runState?.CurrentActIndex ?? 0,
                receivingRole, BossKindOf(runState), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        int attack = 0, power = 0, block = 0, aoE = 0;
        int exhaustAxis = 0, shivAxis = 0, minionAxis = 0, ostyAxis = 0;
        int poisonAxis = 0, doomAxis = 0, strengthAxis = 0, drawAxis = 0, zeroCostAxis = 0;
        int heavyAttack = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (CardModel card in deck)
        {
            counts.TryGetValue(card.Id.Entry, out int count);
            counts[card.Id.Entry] = count + 1;
            if (card.Type == CardType.Attack)
                attack++;
            if (card.Type == CardType.Power)
                power++;
            if (card.GainsBlock)
                block++;
            if (card.TargetType is TargetType.AllEnemies or TargetType.RandomEnemy)
                aoE++;
            if (card.Keywords.Contains(CardKeyword.Exhaust))
                exhaustAxis++;
            if (card.Tags.Contains(CardTag.Shiv))
                shivAxis++;
            if (card.Tags.Contains(CardTag.Minion))
                minionAxis++;
            if (card.Tags.Contains(CardTag.OstyAttack))
                ostyAxis++;
            if (card.DynamicVars.ContainsKey("PoisonPower"))
                poisonAxis++;
            if (card.DynamicVars.ContainsKey("DoomPower"))
                doomAxis++;
            if (card.DynamicVars.ContainsKey("StrengthPower"))
                strengthAxis++;
            if (card.DynamicVars.ContainsKey("Cards") || card.DynamicVars.ContainsKey("Energy"))
                drawAxis++;
            if (!card.EnergyCost.CostsX && card.EnergyCost.Canonical <= 0)
                zeroCostAxis++;
            if (card.Type == CardType.Attack
                && !card.EnergyCost.CostsX
                && card.EnergyCost.Canonical >= 2)
                heavyAttack++;
        }

        float hpRatio = player == null || player.Creature.MaxHp <= 0
            ? 1f
            : (float)player.Creature.CurrentHp / player.Creature.MaxHp;

        var context = new DeckContext(deck, deck.Count, attack, power, block, aoE, hpRatio, runState?.CurrentActIndex ?? 0,
            receivingRole, BossKindOf(runState), exhaustAxis, shivAxis, minionAxis, ostyAxis, poisonAxis, doomAxis, strengthAxis, drawAxis, zeroCostAxis, heavyAttack);
        foreach (KeyValuePair<string, int> pair in counts)
            context._cardCounts[pair.Key] = pair.Value;
        return context;
    }
}
