using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

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

    /// <summary>选升级价值最高的牌：稀有度优先，费用≥1 再加分，攻/能力牌加分，已升级的不选。</summary>
    private static NGridCardHolder? ChooseCardToUpgrade(List<NGridCardHolder> holders)
    {
        NGridCardHolder? best = null;
        float bestScore = float.MinValue;
        foreach (NGridCardHolder holder in holders)
        {
            CardModel? card = holder.CardModel;
            if (card == null)
                continue;
            float score = card.IsUpgraded
                ? -100f
                : card.Rarity switch
                {
                    CardRarity.Rare => 10f,
                    CardRarity.Uncommon => 7f,
                    CardRarity.Common => 4f,
                    _ => 2f,
                };
            if (!card.IsUpgraded && card.EnergyCost.Canonical >= 1)
                score += 3f;
            if (!card.IsUpgraded && card.Type is CardType.Attack or CardType.Power)
                score += 2f;
            if (score > bestScore)
            {
                bestScore = score;
                best = holder;
            }
        }
        return best;
    }
}
