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
///      + 数据驱动（A10 胜率差，已含角色池中位，见 CardWinStats）。
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

        // 卡组短板/成型度：牌组缺某类角色能力时，能补位的候选更值（成型后自然衰减见上方"冗余衰减"）。
        score += DeckGapBonus(card, context);

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
    /// 短板补位加分（启发式，随语料校准）：
    /// ①过牌/启动缺口：牌组一张抽牌/能量轴牌都没有且已 ≥8 张时，带抽轴候选 +5（首张过牌最值）；
    /// ②高伤/终结缺口：已 ≥12 张且一张 ≥2 费攻击都没有时（幕 1 中段后），≥2 费攻击候选 +5
    ///   （打精英/Boss 的斩杀线层缺失）。只加不减、阈值保守，避免开局乱抓大费牌。
    /// </summary>
    private static float DeckGapBonus(CardModel card, DeckContext context)
    {
        float bonus = 0f;
        if (context.DrawAxis == 0 && context.DeckSize >= 8
            && (card.DynamicVars.ContainsKey(DrawVarCards) || card.DynamicVars.ContainsKey(DrawVarEnergy)))
            bonus += 5f;
        if (context.ActIndex >= 1
            && context.DeckSize >= 12
            && context.HeavyAttackCount == 0
            && card.Type == CardType.Attack
            && !card.EnergyCost.CostsX
            && card.EnergyCost.Canonical >= 2)
            bonus += 5f;
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
