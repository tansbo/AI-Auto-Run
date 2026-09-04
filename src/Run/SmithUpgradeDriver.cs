using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 升级选牌屏幕（NDeckUpgradeSelectScreen）驱动：选一张价值最高的牌升级，
/// 等预览容器出现后点确认。篝火 Smith 打开该覆盖层时由 RestSiteDriver 调用。
/// </summary>
internal static class SmithUpgradeDriver
{
    public static async Task HandleAsync(NDeckUpgradeSelectScreen screen, CancellationToken cancellationToken)
    {
        if (screen == null)
            return;
        await RunUiHelper.WaitUntilAsync(
            () => RunUiHelper.FindAll<NGridCardHolder>(screen).Count > 0,
            cancellationToken,
            TimeSpan.FromSeconds(5),
            "升级屏幕卡牌未出现");

        List<NGridCardHolder> remaining = RunUiHelper.FindAll<NGridCardHolder>(screen);
        while (remaining.Count > 0
               && GodotObject.IsInstanceValid(screen)
               && screen.IsVisibleInTree()
               && !IsPreviewVisible(screen))
        {
            NGridCardHolder target = ChooseCardToUpgrade(remaining) ?? remaining[0];
            remaining.Remove(target);
            target.EmitSignal(NCardHolder.SignalName.Pressed, target);
            await Task.Delay(300);
        }

        if (!GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree())
            return;

        NConfirmButton? confirm = null;
        await RunUiHelper.WaitUntilAsync(
            () => (confirm = FindConfirmButton(screen)) != null && confirm.IsEnabled,
            cancellationToken,
            TimeSpan.FromSeconds(5),
            "升级确认按钮未就绪");
        if (confirm == null)
            return;

        await RunUiHelper.ClickAsync(confirm, 150);
        await RunUiHelper.WaitUntilAsync(
            () => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(),
            cancellationToken,
            TimeSpan.FromSeconds(10),
            "升级屏幕未关闭");
    }

    private static bool IsPreviewVisible(NDeckUpgradeSelectScreen screen)
    {
        Control? single = screen.GetNodeOrNull<Control>("%UpgradeSinglePreviewContainer");
        if (single != null && single.Visible)
            return true;
        Control? multi = screen.GetNodeOrNull<Control>("%UpgradeMultiPreviewContainer");
        return multi != null && multi.Visible;
    }

    private static NConfirmButton? FindConfirmButton(NDeckUpgradeSelectScreen screen)
    {
        Control? single = screen.GetNodeOrNull<Control>("%UpgradeSinglePreviewContainer");
        if (single != null && single.Visible)
            return single.GetNodeOrNull<NConfirmButton>("Confirm");
        Control? multi = screen.GetNodeOrNull<Control>("%UpgradeMultiPreviewContainer");
        if (multi != null && multi.Visible)
            return multi.GetNodeOrNull<NConfirmButton>("Confirm");
        return null;
    }

    /// <summary>
    /// 选"敲完最值"的牌：不再用静态稀有度/类型启发式，而是对每张可升级牌克隆出"敲后"模型
    /// （官方预览同款路径：ICardScope.CloneCard + UpgradeInternal + FinalizeUpgradeInternal），
    /// 用敲前/敲后的真实差异评分——升级带来的数值增长（伤害/格挡/易伤等 DynamicVar 增量）与
    /// 费用降低是最主要的信号，稀有度只做同分决胜。已升级的不选。
    /// </summary>
    private static NGridCardHolder? ChooseCardToUpgrade(List<NGridCardHolder> holders)
    {
        NGridCardHolder? best = null;
        float bestScore = float.MinValue;
        foreach (NGridCardHolder holder in holders)
        {
            CardModel? card = holder.CardModel;
            if (card == null || card.IsUpgraded)
                continue;
            float score = UpgradeValue(card, out bool measured);
            // 个别卡克隆/升级不可用时退化为旧静态启发式，保证总能给出选择。
            if (!measured)
                score = StaticFallbackScore(card);
            if (score > bestScore)
            {
                bestScore = score;
                best = holder;
            }
        }
        return best;
    }

    /// <summary>
    /// 敲前/敲后真实增量：克隆后升级，比较模型上的可读差异。
    /// 数值增量 = 升级后所有 DynamicVar 相对升级前的正向增长之和（升级只会加量或新增变量，
    /// 用 max(0, Δ) 避免自伤/代价类变量的符号误判）；费用降低（敲后 Canonical 费用更少）加权。
    /// </summary>
    private static float UpgradeValue(CardModel card, out bool measured)
    {
        measured = false;
        ICardScope? scope = card.CardScope;
        if (scope == null)
            return 0f;
        CardModel? upgraded = scope.CloneCard(card);
        if (upgraded == null)
            return 0f;
        upgraded.UpgradeInternal();
        upgraded.FinalizeUpgradeInternal();

        decimal numericGain = 0m;
        foreach (KeyValuePair<string, DynamicVar> pair in upgraded.DynamicVars)
        {
            decimal pre = card.DynamicVars.TryGetValue(pair.Key, out DynamicVar? preVar) && preVar != null
                ? preVar.BaseValue
                : 0m;
            decimal delta = pair.Value.BaseValue - pre;
            if (delta > 0m)
                numericGain += delta;
        }

        int costPre = card.EnergyCost.Canonical;
        int costPost = upgraded.EnergyCost.Canonical;
        int costReduction = Math.Max(0, costPre - costPost);

        measured = true;
        return (float)numericGain + 15f * costReduction + RarityTieBreak(card);
    }

    private static float RarityTieBreak(CardModel card)
        => card.Rarity switch
        {
            CardRarity.Rare => 6f,
            CardRarity.Uncommon => 4f,
            CardRarity.Common => 2f,
            _ => 0f,
        };

    /// <summary>克隆不可用时的旧静态启发式（仅兜底）。</summary>
    private static float StaticFallbackScore(CardModel card)
    {
        float score = card.Rarity switch
        {
            CardRarity.Rare => 10f,
            CardRarity.Uncommon => 7f,
            CardRarity.Common => 4f,
            _ => 2f,
        };
        if (card.EnergyCost.Canonical >= 1)
            score += 3f;
        if (card.Type is CardType.Attack or CardType.Power)
            score += 2f;
        return score;
    }
}
