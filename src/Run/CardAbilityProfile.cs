using System;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Run;

/// <summary>单卡"能力四维"画像（纯数值、启发式；只读 CardModel / DynamicVar 的 BaseValue，不区分 ValueProp 语义）。</summary>
internal readonly record struct CardAbilityProfile(float Attack, float Defense, float Energy, float Draw)
{
    /// <summary>四维全为 0（无法给任何维度补位）。</summary>
    public bool IsZero => Attack <= 0f && Defense <= 0f && Energy <= 0f && Draw <= 0f;
}

/// <summary>
/// 牌组画像合计（四维总和 + 回费端细分），由 <see cref="DeckContext.AbilityTotals"/> 惰性统计一次并缓存。
/// </summary>
internal readonly record struct DeckAbilityTotals(
    float AttackTotal,
    float DefenseTotal,
    float EnergyTotal,
    float DrawTotal,
    int ZeroCostCards)
{
    /// <summary>空牌组（无 player/牌组时）的合计。</summary>
    public static DeckAbilityTotals Empty => new(0f, 0f, 0f, 0f, 0);
}

/// <summary>
/// 四维能力读取器：把一张卡压缩成 攻击/防御/回费/过牌 四个能力分。
/// 口径（全部为启发式，注释写清"为什么这样估"；只服务牌组画像与补位评估，不进入任何模拟）：
/// 攻击 = 基础伤害 × 命中段数（"Damage"×"Repeat"，无 Repeat 变量视为 1 段）；
///       全体/随机目标视为对单等效后再乘 AOE 小系数（≤1.3，不按敌方数量放大）；
/// 防御 = "Block" 变量值；GainsBlock 但无 Block 变量的给保守 4；
/// 回费 = "Energy" 变量值；费用 Canonical≤0 且非 X 费视为 +1 经济价值；
///       星能(star) 一律不折算（另一种经济，注释说明）；
/// 过牌 = "Cards" 变量值（抽牌量），Retain 关键词 +0.5。
/// 每维输出可正可 0。
/// </summary>
internal static class CardAbilityProfileReader
{
    // DynamicVar 键名（与 decomp DynamicVarSet.cs 的类型化访问器对应："Damage"/"Block"/"Energy"/"Cards" 等）。
    public const string DamageVarKey = "Damage";
    public const string RepeatVarKey = "Repeat";
    public const string BlockVarKey = "Block";
    public const string EnergyVarKey = "Energy";
    public const string CardsVarKey = "Cards";

    public static CardAbilityProfile Of(CardModel card)
    {
        float attack = 0f;
        if (card.Type == CardType.Attack)
        {
            float damage = VarBase(card, DamageVarKey);
            if (damage > 0f)
            {
                // X 费攻击（如 Whirlwind/烈焰斩）："Damage" 是每点 X 的伤害，打出时以剩余能量为段数
                // （decomp Whirlwind.cs OnPlay：WithHitCount(ResolveEnergyXValue())），而
                // CardEnergyCost.Canonical 对 X 费恒为 0（decomp CardEnergyCost.cs L86）。
                // 补位画像按"名义投入 X=2"保守折算：既不把 X 当 0 费白嫖，也不按满能量高估。
                float repeat = card.EnergyCost.CostsX ? 2f : Math.Max(1f, VarBase(card, RepeatVarKey));
                attack = damage * repeat;
                // 全体/随机目标：对单等效后再乘小系数（全体按 3 敌折算但取 1.25 封顶内的保守值，
                // 随机目标不能集火所以更小）——启发式，只求数量级可比。
                attack *= card.TargetType switch
                {
                    TargetType.AllEnemies => 1.25f,
                    TargetType.RandomEnemy => 1.1f,
                    _ => 1f,
                };
            }
        }

        // 防御：Block 变量即"一次性格挡量"。GainsBlock 但无 Block 变量的卡（如自回手/条件格挡）给保守 4
        // （约一张 1 费普通格挡的量）。含"格挡相关 Power"的卡（壁垒/巨像/格挡成长等）不做折算：
        // 跨回合持续收益无法用单次数值表达，宁可漏计（保守），避免拍脑袋放大。
        float defense = VarBase(card, BlockVarKey);
        if (defense <= 0f && card.GainsBlock)
            defense = 4f;

        // 回费：Energy 变量是显式净能量产出。费用 Canonical≤0 且非 X 费 → +1 经济价值（免费就能打）。
        // X 费不享受该加成（要吃掉大量能量）；星能（"Stars" 变量 / CanonicalStarCost）一律不折算进回费端
        // ——星能是另一套经济（攒星放终结技用），不能拿去喂 ≥3 费攻击（decomp 见 CardModel.CanonicalStarCost）。
        float energy = 0f;
        if (!card.EnergyCost.CostsX && card.EnergyCost.Canonical <= 0)
            energy += 1f;
        energy += Math.Max(0f, VarBase(card, EnergyVarKey));

        // 过牌：Cards 变量=抽牌量。Retain +0.5：保留把"这回合用不掉的价值"延后到需要的回合，
        // 近似小半张过牌的柔韧性（启发式小分，不给整张）。
        float draw = Math.Max(0f, VarBase(card, CardsVarKey));
        if (card.Keywords.Contains(CardKeyword.Retain))
            draw += 0.5f;

        return new CardAbilityProfile(attack, defense, energy, draw);
    }

    private static float VarBase(CardModel card, string key)
        => card.DynamicVars.TryGetValue(key, out DynamicVar? v) ? (float)v.BaseValue : 0f;
}
