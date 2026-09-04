using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Run;

/// <summary>
/// 牌组"启动/抽牌可达性"系数（第 4 条设计 C，deck-level）：
/// 衡量"同回合组合（先施灾厄再打死亡之门）"/"每回合引擎或高价值 Power"能否尽快上手。
/// 口径 = clamp(0.6 + 0.4 × 廉价占比 + 2.5 × 抽牌密度, 0.7, 1.3)：
///  廉价占比 = 0/1 费卡数/牌组（一回合内能连打多张、先铺组件再打 payoff）；
///  抽牌密度 = Cards 变量总量/牌组（关键卡更快过到手上）；
/// 系数 >1 = 抽牌/廉价好 → 补位与引擎加分；<1 = 抽牌差 + 引擎费用高 → 鬼抽（扣分由调用方施加）。
/// 数值为启发式（0.7..1.3 小步保守，可随语料重校），全部主线程只读。
/// </summary>
internal static class DeckReachability
{
    public static float Coefficient(DeckContext context)
    {
        if (context.DeckSize <= 0)
            return 1f;
        DeckAbilityTotals totals = context.AbilityTotals; // 惰性统计一次
        float cheapRatio = totals.CheapCards / (float)context.DeckSize;
        float drawDensity = totals.DrawTotal / context.DeckSize;
        return Math.Clamp(0.6f + 0.4f * cheapRatio + 2.5f * drawDensity, 0.7f, 1.3f);
    }
}

/// <summary>
/// 第 4 条设计 A（浓度-凸收益）+ B（每回合触发引擎）的候选表与加成。
/// 规则：只有 decomp 机制核对的卡入表（条目标注文件:行依据）；未核对/机制不符的卡不入表
/// （宁可漏判不猜——例如"精神过载" NEUROSURGE 已定位，但其每回合 Doom 是施给 Owner 自己
/// （NeurosurgePower.cs AfterSideTurnStart L20-27 目标 = base.Owner），不是对敌正值收益，故不入引擎表）。
/// </summary>
internal static class ConcentrationProfiles
{
    private readonly struct ConcentrationRule
    {
        /// <summary>浓度轴：牌组 DeckVarCount 统计的 DynamicVar 键。</summary>
        public readonly string AxisVarKey;
        /// <summary>小档/高档触发阈值（轴内去重卡数 ≥）。</summary>
        public readonly int LowThreshold;
        public readonly int HighThreshold;
        /// <summary>小档/高档加成（倍数于可达性系数前的小分）。</summary>
        public readonly float LowBonus;
        public readonly float HighBonus;

        public ConcentrationRule(string axisVarKey, int lowThreshold, int highThreshold, float lowBonus, float highBonus)
        {
            AxisVarKey = axisVarKey;
            LowThreshold = lowThreshold;
            HighThreshold = highThreshold;
            LowBonus = lowBonus;
            HighBonus = highBonus;
        }
    }

    /// <summary>浓度档表：候选卡 Id.Entry → 规则（轴、档位阈值与加成）。</summary>
    private static readonly Dictionary<string, ConcentrationRule> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        // DEATHS_DOOR 死亡之门（Necrobinder 1 费 Uncommon 技能，DeathsDoor.cs）：Block 6（升级 +1 → 7，
        //   OnUpgrade L40-42）+ Repeat 2；本回合 Owner 对任意目标施加过 Doom（战斗历史 PowerReceivedEntry：
        //   Power is DoomPower 且 Applier==Owner，WasDoomAppliedThisTurn L21-23）→ blockGains = 1+Repeat = 3
        //   次 GainBlock(6) = 18（升级 21，OnPlay L31-37）。浓度-凸收益：价值 = f(同回合"先施灾厄再打它"
        //   达成率)——牌组 DoomPower 轴 DoomAxisCount==0 → 0；≥1（有前置施灾厄源）小档；≥3（叠灾厄/多源
        //   循环）高档；再乘 DeckReachability 可达性系数（先施后打的组件要廉/抽得到）。
        ["DEATHS_DOOR"] = new ConcentrationRule("DoomPower", 1, 3, 3f, 6f),
    };

    /// <summary>每回合触发引擎表：候选卡 → 每回合收益的点数基准（保守小分）与启动费用。
    /// 价值口径 ≈ 每回合收益 × 本幕战斗节奏回合数 − 启动成本（见 <see cref="EngineBonus"/>）。</summary>
    private static readonly Dictionary<string, (float BasePoints, int Cost)> PerTurnEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        // COUNTDOWN 倒数计时（Power 1 费，Countdown.cs L27-37 施 CountdownPower 6，升级 +3 → 9）：
        //   CountdownPower 每玩家回合开始对随机可命中敌施 Doom amount（CountdownPower.cs AfterSideTurnStart
        //   L26-36；DoomPower 为"血线 ≤ 层数则回合末处决"的倒计时，DoomPower.cs IsOwnerDoomed L119-121）——
        //   启动后每回合恒定产出（幕越长越值），但要尽快上手 → 乘可达性系数；抽牌差且引擎费 ≥2 有鬼抽罚。
        ["COUNTDOWN"] = (5f, 1),
    };

    /// <summary>A. 浓度-凸收益补位分：命中浓度表且牌组轴计数达标时，按档位给补位分并乘可达性系数。</summary>
    public static float ConcentrationBonus(CardModel card, DeckContext context)
    {
        if (!Rules.TryGetValue(card.Id.Entry, out ConcentrationRule rule))
            return 0f;
        int axis = context.DeckVarCount(rule.AxisVarKey);
        float tierBonus = axis >= rule.HighThreshold
            ? rule.HighBonus
            : axis >= rule.LowThreshold ? rule.LowBonus : 0f;
        if (tierBonus <= 0f)
            return 0f;
        return tierBonus * DeckReachability.Coefficient(context);
    }

    /// <summary>B. 每回合引擎加分：基准 × 幕节奏系数 × 可达性系数，抽牌差且引擎费用 ≥2 时扣鬼抽罚。</summary>
    public static float EngineBonus(CardModel card, DeckContext context)
    {
        if (!PerTurnEngines.TryGetValue(card.Id.Entry, out (float BasePoints, int Cost) row))
            return 0f;
        // 幕节奏：战斗回合数随幕推进变长（启发式 act0 第 1 幕 ~5 回合起），引擎每回合收益按幕放大小分。
        float tempo = context.ActIndex switch
        {
            0 => 1.0f,
            1 => 1.15f,
            _ => 1.3f,
        };
        float bonus = row.BasePoints * tempo * DeckReachability.Coefficient(context);
        // 鬼抽惩罚：牌组没有像样抽牌（Cards 变量总量 &lt;2）且引擎费用 ≥2 时，上手慢使引擎贬值。
        DeckAbilityTotals totals = context.AbilityTotals;
        if (row.Cost >= 2 && totals.DrawTotal < 2f)
            bonus -= 2f;
        return Math.Max(0f, bonus);
    }
}
