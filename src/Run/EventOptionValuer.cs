using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 事件选项的价值评估（用户规则 2026-09-02）：
///   - **可 SL / 确定奖励**：选项上直接看得到具体奖励（<see cref="EventOption.Relic"/> 或
///     HoverTips 里的 <see cref="CardHoverTip"/> 携带具体卡牌）→ 用**实际价值**评分
///     （遗物走 RelicPickerAI.Score、卡牌走 CardPickerAI.Evaluate 的牌组上下文评分）；
///   - **不可 SL / 随机奖励**（点了才掷出，可能多张卡或多类遗物）→ 只能按该选项的
///     **综合期望评分**：从 <see cref="RandomAverageByTextKey"/> 目录按 TextKey 取作者填写的平均值；
///   - 目录未覆盖的随机选项返回"未建模"（价值 0、非确定性）——不做冒险重排，等逐事件阅读
///     decomp 填表（跑局日志会记录 TextKey 供收集）。
/// **陷阱修复（2026-09-02，实机反馈）**：选项上展示的卡不一定是奖励——SLIPPERY_BRIDGE 的
/// OVERCOME 展示的是**即将被移除的那张卡**（代价）。按事件建模先行：
///   每轮 OVERCOME = 移除当前展示卡：价值 = -CardPickerAI.Evaluate(该卡)（垃圾卡正值=该移除，
///   好卡强负值=别丢）；HOLD_ON = 掉 CurrentHpLoss（3+已撑轮次，不可挡）+ 换一张新威胁卡：
///   价值 = -掉血量 × 血量风险权重（血越低越贵；战士战后回血 → 掉血更便宜）。
/// 引擎由此自动做"展示卡值钱就掉血换重抽、展示卡是垃圾就移除"的最优权衡。
/// </summary>
internal static class EventOptionValuer
{
    public readonly record struct OptionScore(float Value, string Basis, bool Deterministic);

    /// <summary>
    /// 随机奖励选项的期望评分目录（key = <see cref="EventOption.TextKey"/>，值 = 综合期望评分）。
    /// 需要逐事件阅读 decomp：统计该选项可能掷出的卡池/遗物类别并按概率加权。
    /// 初始为空，随事件逐个填表（见 DEVELOPMENT_NOTES 待办）。
    /// </summary>
    private static readonly Dictionary<string, float> RandomAverageByTextKey = new(StringComparer.OrdinalIgnoreCase);

    public static OptionScore Score(EventOption option, EventModel? eventModel, Player? player, RunState? runState)
    {
        // 逐事件建模优先：展示的卡/代价语义可能反直觉（如 SLIPPERY_BRIDGE 的移除代价）。
        if (eventModel is SlipperyBridge bridge)
            return ScoreSlipperyBridge(bridge, option, player, runState);

        // 确定奖励：选项挂着具体遗物（会显示图标/悬停）→ 实际价值。
        if (option.Relic != null)
        {
            return new OptionScore(
                RelicPickerAI.Score(option.Relic),
                $"遗物实际:{option.Relic.Id.Entry}",
                Deterministic: true);
        }

        // 确定奖励：悬停里带具体卡牌 → 实际价值（多张卡时取平均，按 DeckContext 牌组上下文评分）。
        DeckContext? context = null;
        float sum = 0f;
        int count = 0;
        foreach (IHoverTip tip in option.HoverTips)
        {
            if (tip is not CardHoverTip cardTip)
                continue;
            context ??= DeckContext.From(player, runState);
            sum += CardPickerAI.Evaluate(cardTip.Card, context);
            count++;
        }
        if (count > 0)
        {
            return new OptionScore(
                sum / count,
                count == 1 ? $"卡牌实际:{sum:0.#}" : $"卡牌×{count}平均:{sum / count:0.#}",
                Deterministic: true);
        }

        // 随机奖励：作者填写的综合期望。
        if (RandomAverageByTextKey.TryGetValue(option.TextKey, out float average))
        {
            return new OptionScore(
                average,
                $"随机期望({option.TextKey}):{average:0.#}",
                Deterministic: false);
        }

        // 未建模：价值 0、非确定 —— 调用方不做冒险重排（保持既有顺序）。
        return new OptionScore(0f, $"未建模({option.TextKey})", Deterministic: false);
    }

    /// <summary>
    /// SLIPPERY_BRIDGE（桥摔落）：OVERCOME 展示的是"即将移除的随机卡"（代价不是奖励）。
    /// 决策：OVERCOME = -卡牌评分（卡越值钱越负，别丢）；HOLD_ON = -当前掉血量 × 血量风险权重
    /// （CurrentHpLoss 从事件 DynamicVars 实时读；血越低掉血越贵；战士战后回血 6 → 掉血×0.85 更便宜）。
    /// </summary>
    private static OptionScore ScoreSlipperyBridge(
        SlipperyBridge bridge,
        EventOption option,
        Player? player,
        RunState? runState)
    {
        if (option.Relic != null || option.TextKey.Contains(".OVERCOME", StringComparison.OrdinalIgnoreCase))
        {
            foreach (IHoverTip tip in option.HoverTips)
            {
                if (tip is CardHoverTip cardTip)
                {
                    DeckContext context = DeckContext.From(player, runState);
                    float lostValue = CardPickerAI.Evaluate(cardTip.Card, context);
                    // 移除垃圾卡（负分）= 正值该移除；移除强卡 = 强负值别丢。
                    return new OptionScore(
                        -lostValue,
                        $"移除代价:-({cardTip.Card.Id.Entry}:{lostValue:0.#})",
                        Deterministic: true);
                }
            }
            return new OptionScore(0f, "SLIPPERY_BRIDGE.OVERCOME(无展示卡)", Deterministic: false);
        }

        // HOLD_ON：掉当前轮次血量（3 + 已撑轮数）并换新威胁卡。
        int hpCost = 3;
        try
        {
            DynamicVar? var = bridge.DynamicVars["HpLoss"];
            if (var != null)
                hpCost = var.IntValue;
        }
        catch
        {
            // 动态变量未初始化（极少见）→ 用首轮 3。
        }
        float hpFraction = player?.Creature != null && player.Creature.MaxHp > 0
            ? (float)player.Creature.CurrentHp / player.Creature.MaxHp
            : 0.7f;
        float hpWeight = Math.Clamp(1f + (0.5f - hpFraction) * 1.6f, 0.35f, 2.2f);
        float regen = (float)RunActContext.PassivePostCombatHeal(player);
        if (regen > 0f)
            hpWeight *= 0.85f; // 战士战后回血：这轮掉的血更便宜。
        return new OptionScore(
            -hpCost * hpWeight,
            $"掉血代价:-{hpCost}×{hpWeight:0.##}",
            Deterministic: true);
    }
}
