using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;

namespace CombatSolver;

internal static class PotionUsePolicy
{
    public const decimal AmbergrisMinimumHpSavedFraction = 0.40m;

    public static int RequiredHpSaved(int potionCount)
        => potionCount * SolverWeights.PotionMinimumHpSaved;

    public static int AdditionalRequiredUseStrategicHpCost(int strategicHpCost)
        => Math.Max(0, strategicHpCost - SolverWeights.PotionMinimumHpSaved);

    public static int StrategicHpCost(PotionModel potion)
        => potion.Rarity == PotionRarity.Token || IsPermanentUpside(potion)
            ? 0
            : SolverWeights.PotionMinimumHpSaved;

    /// <summary>
    /// 永久增益类药水：效果不随战斗时机变化、只赚不亏（如 FruitJuice 永久 +最大生命并回血等量）。
    /// 这类药水应在能喝的时候就建议喝掉——攒到阵亡就是浪费；对战斗的即时省血贡献可能 < 普通门槛，
    /// 因此战略成本记为 0，让 Smart 政策始终接受包含它的路线。
    /// </summary>
    public static bool IsPermanentUpside(PotionModel potion)
        => potion is FruitJuice;

    public static bool RequiresOpeningUse(PotionModel potion)
        => potion is DexterityPotion
            or FocusPotion
            or FyshOil
            or LiquidBronze
            or MazalethsGift
            or PotionOfCapacity
            or SoldiersStew
            or StrengthPotion;

    public static int StrategicHpCost(string potionId)
    {
        PotionModel potion = ModelDb.AllPotions.Single(candidate =>
            candidate.Id.Entry.Equals(potionId, StringComparison.Ordinal));
        return StrategicHpCost(potion);
    }

    public static int HpSaved(int potionFreeHpDeficit, int potionRouteHpDeficit)
        => Math.Max(0, potionFreeHpDeficit - potionRouteHpDeficit);

    public static int AmbergrisRequiredHpSaved(int maximumHp)
        => (int)Math.Ceiling(maximumHp * AmbergrisMinimumHpSavedFraction);

    public static int EffectiveStrategicHpCost(
        int strategicHpCost,
        int ambergrisCount,
        int maximumHp)
        => strategicHpCost + ambergrisCount
            * (AmbergrisRequiredHpSaved(maximumHp) - SolverWeights.PotionMinimumHpSaved);

    public static bool MeetsAmbergrisRestriction(
        bool hasPotionFreeBaseline,
        int ambergrisCount,
        int strategicHpCost,
        int maximumHp,
        int potionFreePlayerHp,
        int potionRoutePlayerHp)
    {
        if (ambergrisCount == 0)
            return true;
        if (!hasPotionFreeBaseline)
            return false;
        int required = EffectiveStrategicHpCost(strategicHpCost, ambergrisCount, maximumHp);
        return Math.Max(0, potionRoutePlayerHp - potionFreePlayerHp) >= required;
    }

    public static bool IsEligible(
        SolverPotionPolicy policy,
        int potionCount,
        int automaticPotionCount,
        int strategicHpCost,
        bool potionFreeWon,
        int potionFreeHpDeficit,
        bool anyRouteWon,
        bool potionRouteWon,
        int potionRouteHpDeficit)
        => policy switch
        {
            SolverPotionPolicy.Disabled => potionCount == automaticPotionCount,
            SolverPotionPolicy.RequireAtLeastOne => potionCount > 0
                && (!anyRouteWon || potionRouteWon)
                && (potionCount == 1
                    || !potionFreeWon
                    || HpSaved(potionFreeHpDeficit, potionRouteHpDeficit)
                        >= AdditionalRequiredUseStrategicHpCost(strategicHpCost)),
            SolverPotionPolicy.Smart => potionCount == 0
                || potionRouteWon && !potionFreeWon
                || HpSaved(potionFreeHpDeficit, potionRouteHpDeficit) >= strategicHpCost,
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
}

internal readonly record struct PotionFreePolicyBaseline(
    bool Won,
    int HpDeficit,
    int PlayerHp);

internal sealed class PotionPolicyUnsatisfiedException(string message) : InvalidOperationException(message);
