using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;

namespace CombatSolver.Run;

/// <summary>
/// 跑局级药水保留/槽位策略（用户规则 2026-09-02，与战斗内 Smart 政策 Search/PotionUsePolicy 分离）：
///  1) 只有果汁 FruitJuice（永久 +5 最大生命）与鲜血药水 BloodPotion（回 20% 最大生命）可以在战斗外用掉；
///     其余药水在需要腾栏位时只能丢弃（不能战斗外喝）。
///  2) 保留成本随路线上下文调整：
///     - 前方路线危险（routeDanger）→ 战斗药水保留价值上调（留着救命）；
///     - 幕末（healCarryFraction &lt; 1）→ Boss 后 Ancient 会补回缺失生命的大头
///       （A2+ WearyTraveler 补 80%，其余难度满补），回血药在幕末的跨幕价值只剩没被补回的部分，
///       保留价值下调 —— 幕末省药、低血出 Boss，靠 Ancient 补回。
///  3) 果汁是"占位药水"：保留分恒为 0，栏位满 + 拿到更好的药时第一个被喝掉腾栏（+5 最大生命不浪费）。
/// </summary>
internal static class PotionRunPolicy
{
    public enum IntakeKind
    {
        /// <summary>有空栏或无需腾栏：直接领取。</summary>
        Take,
        /// <summary>满栏且新药不值得挤掉最弱持有药水：跳过这次药水奖励。</summary>
        SkipOffer,
        /// <summary>满栏且新药更好：腾掉最弱持有药水（喝或丢）后领取。</summary>
        MakeRoomAndTake,
    }

    public readonly record struct IntakePlan(
        IntakeKind Kind,
        PotionModel? ToRemove,
        bool DrinkInsteadOfDiscard);

    /// <summary>用户指定：只有果汁与鲜血药水可在战斗外喝掉腾栏；其余只能丢弃。</summary>
    public static bool IsOutOfCombatDrinkable(PotionModel potion)
        => potion is FruitJuice or BloodPotion;

    /// <summary>
    /// 保留分数（越高越值得留在栏位）。用于满栏时挑"最弱持有药水"腾栏，
    /// 以及判断新药值不值得挤掉它。
    /// </summary>
    /// <param name="healCarryFraction">
    /// 回血药的"跨幕留存系数"：幕末 = 1 - Ancient 补血比例（A2+ 为 0.2，A0-1 为 0），
    /// 其余位置 = 1（血在当前幕内随时有用）。1 表示完全按当前缺血量保留。
    /// </param>
    public static int KeepScore(
        PotionModel potion,
        decimal hpFraction,
        int routeDanger,
        decimal healCarryFraction)
    {
        if (potion is FruitJuice)
            return 0; // 占位药水：任何时候都先拿它开刀（喝掉 +5 最大生命腾栏）。

        if (potion is BloodPotion)
        {
            // 回 20% 最大生命。战斗外喝时：缺的血越多越值；
            // 幕末时只有没被 Ancient 补回的部分（1-补血比例）真正留存，保留价值按比例打折。
            decimal missing = 1m - Math.Clamp(hpFraction, 0m, 1m);
            return (int)(10m + missing * 40m * Math.Clamp(healCarryFraction, 0m, 1m));
        }

        if (potion is FairyInABottle)
            return 130 + routeDanger; // 濒死救命药：最高档保留，危险路线更不舍得丢。

        int rarityBase = potion.Rarity switch
        {
            PotionRarity.Rare => 70,
            PotionRarity.Uncommon => 45,
            PotionRarity.Event => 55,
            PotionRarity.Token => 30,
            _ => 25, // Common
        };
        // 战斗药水：前方路线越危险保留价值越高（留着救命），幕末 Ancient 只影响回血类。
        return rarityBase + routeDanger / 3;
    }

    /// <summary>
    /// 满栏时的药水奖励决策。heldPotions 为当前栏内全部药水（满栏）；hpFraction 当前血量比例。
    /// </summary>
    public static IntakePlan PlanIntake(
        PotionModel offered,
        IReadOnlyList<PotionModel> heldPotions,
        decimal hpFraction,
        int routeDanger,
        decimal healCarryFraction)
    {
        if (heldPotions.Count == 0)
            return new IntakePlan(IntakeKind.Take, null, false);

        PotionModel weakest = heldPotions[0];
        int weakestScore = int.MaxValue;
        foreach (PotionModel held in heldPotions)
        {
            int score = KeepScore(held, hpFraction, routeDanger, healCarryFraction);
            if (score < weakestScore
                || (score == weakestScore && IsOutOfCombatDrinkable(held) && !IsOutOfCombatDrinkable(weakest)))
            {
                weakest = held;
                weakestScore = score;
            }
        }

        int offeredScore = KeepScore(offered, hpFraction, routeDanger, healCarryFraction);
        if (offeredScore <= weakestScore)
            return new IntakePlan(IntakeKind.SkipOffer, null, false);

        bool drink = IsOutOfCombatDrinkable(weakest)
            && (weakest is FruitJuice
                || 1m - Math.Clamp(hpFraction, 0m, 1m) > 0.05m); // 鲜血药水：血缺得不够多时喝=浪费，丢。
        return new IntakePlan(IntakeKind.MakeRoomAndTake, weakest, drink);
    }
}
