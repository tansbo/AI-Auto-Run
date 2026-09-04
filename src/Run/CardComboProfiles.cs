using System;
using System.Collections.Generic;

namespace CombatSolver.Run;

/// <summary>
/// 语义级卡牌配合（跨职业价值的关键补充）：机械轴同轴加分只覆盖"同家族"，
/// 拦不住"引擎×终结"这类跨职业组合（如免费攻击引擎 × 攒星大招）。
/// 条目仅收录 用户点名 + 反编译机制核对 的配合方向（decomp 出处见注释），
/// 加分保守（+6）；命中=牌组已含任一伙伴牌（任一张同名即可）。
/// 未收录的组合不推断加分（宁可漏判不误导）。
/// </summary>
internal static class CardComboProfiles
{
    private readonly struct ComboRule
    {
        public readonly string[] Partners;
        public readonly float Bonus;
        public ComboRule(string[] partners, float bonus)
        {
            Partners = partners;
            Bonus = bonus;
        }
    }

    /// <summary>候选卡 Id.Entry → 配合规则。伙伴命中即加 <see cref="ComboRule.Bonus"/>。</summary>
    private static readonly Dictionary<string, ComboRule> Rules = new(StringComparer.Ordinal)
    {
        // STAMPEDE(惊逃, Ironclad): StampedePower 每层每回合在 AutoPostPlay 阶段从手牌随机
        //   自动打出一张攻击(decomp StampedePower.cs AfterAutoPostPlayPhaseEntered)。
        // GRAND_FINALE(华丽收场, Silent): 0费 60AOE, 抽牌堆为空才可打出(IsPlayable)。
        //   配合(源码推导): CardCmd.AutoPlay 只拦 Unplayable/Hook.ShouldPlay, 不检查 CanPlay
        //   (CardCmd.cs:51-130) → 惊逃自动打出会绕过华丽收场的"空抽牌堆"门槛, 每回合免费 AOE。
        //   双方向都成立: 牌组已有惊逃时, 华丽收场价值↑(白嫖窗口); 已有华丽收场时, 惊逃的
        //   自动输出池含高价值终结。反例(不收录): UNRELENTING×SEVEN_STARS——FreeAttackPower
        //   只免能量不免星能(FreeAttackPower.cs:14-42), 七星 7★ 瓶颈仍在, 无实质配合;
        //   SEVEN_STARS×GRAND_FINALE 无互触发点。
        ["STAMPEDE"] = new ComboRule(new[] { "GRAND_FINALE" }, 6f),
        ["GRAND_FINALE"] = new ComboRule(new[] { "STAMPEDE" }, 6f),
    };

    internal static float Bonus(string entry, DeckContext context)
    {
        if (!Rules.TryGetValue(entry, out ComboRule rule) || rule.Partners.Length == 0)
            return 0f;
        return context.ContainsAny(rule.Partners) ? rule.Bonus : 0f;
    }
}
