using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
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
/// 不含 HP/金币等结构化代价（原版只在描述文本/闭包内，无法结构读取）；致命选项由
/// EventOption.WillKillPlayer 在驱动层排除。
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

    public static OptionScore Score(EventOption option, Player? player, RunState? runState)
    {
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
}
