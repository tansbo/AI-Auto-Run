using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 选牌智能：对战后卡牌奖励候选做结构评分，决定选哪张或跳过。
/// 只读不可变数据（CardModel/Player/RunState），无模拟、无副作用、主线程执行。
/// 评分 = 稀有度 + 费用效率 + 类型契合 + 关键词 + AOE/格挡覆盖 + 重复牌惩罚 + 牌组膨胀 + 精选表加成。
/// 最高分低于 <see cref="SkipThreshold"/> 时返回 null（跳过）。
/// </summary>
internal static class CardPickerAI
{
    private const float SkipThreshold = 9f;

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

        // 数据驱动（Spire Codex A10 真实对局）：胜率差加成，卡在牌组里会随 DeckContext 一并复算。
        if (CardWinStats.BonusById.TryGetValue(card.Id.Entry, out float winBonus))
            score += winBonus;

        return score;
    }
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
    public float HpRatio { get; }
    public int ActIndex { get; }

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
        int actIndex)
    {
        Deck = deck;
        DeckSize = deckSize;
        AttackCount = attackCount;
        PowerCount = powerCount;
        BlockCardCount = blockCardCount;
        AoECount = aoECount;
        HpRatio = hpRatio;
        ActIndex = actIndex;
    }

    public int CountOf(CardModel card)
        => _cardCounts.TryGetValue(card.Id.Entry, out int count) ? count : 0;

    public static DeckContext From(Player? player, RunState? runState)
    {
        IReadOnlyList<CardModel>? deck = player == null ? null : PileType.Deck.GetPile(player).Cards;
        if (deck == null)
            return new DeckContext(null, 0, 0, 0, 0, 0, 1f, runState?.CurrentActIndex ?? 0);

        int attack = 0, power = 0, block = 0, aoE = 0;
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
        }

        float hpRatio = player == null || player.Creature.MaxHp <= 0
            ? 1f
            : (float)player.Creature.CurrentHp / player.Creature.MaxHp;

        var context = new DeckContext(deck, deck.Count, attack, power, block, aoE, hpRatio, runState?.CurrentActIndex ?? 0);
        foreach (KeyValuePair<string, int> pair in counts)
            context._cardCounts[pair.Key] = pair.Value;
        return context;
    }
}
