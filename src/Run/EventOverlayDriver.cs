using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace CombatSolver.Run;

/// <summary>
/// 事件选项打开的覆盖层驱动（奖励/选牌/移除等）。EventDriver 发现覆盖层存在时调用，
/// 返回 true 表示已驱动（调用方继续主循环），false 表示不认识该覆盖层（调用方等它自关）。
/// 配方移植自游戏 AutoSlay 的 ScreenHandler；选牌先取第一张（冒烟级，后续批次接入评分）。
/// 所有等待都有界，超时抛 RunAutoTimeoutException 由 EventDriver 兜底记录。
/// </summary>
internal static class EventOverlayDriver
{
    public static async Task<bool> DriveAsync(CancellationToken token)
    {
        IOverlayScreen? top = NOverlayStack.Instance?.Peek();
        switch (top)
        {
            case NRewardsScreen rewardsScreen:
                await DriveRewardsAsync(rewardsScreen, token);
                return true;
            case NSimpleCardSelectScreen simpleScreen:
                await DriveSimpleSelectAsync(simpleScreen, token);
                return true;
            case NDeckCardSelectScreen deckScreen:
                await DriveDeckSelectAsync(deckScreen, token);
                return true;
            case NDeckEnchantSelectScreen enchantScreen:
                await DriveEnchantSelectAsync(enchantScreen, token);
                return true;
            case NChooseACardSelectionScreen chooseScreen:
                await DriveChooseACardAsync(chooseScreen, token);
                return true;
            default:
                return false;
        }
    }

    /// <summary>事件给的奖励屏幕（如 WELLSPRING 装瓶给药水）：复用战后奖励驱动，等它处理完。</summary>
    private static async Task DriveRewardsAsync(NRewardsScreen screen, CancellationToken token)
    {
        RewardsScreenDriver.OnCombatVictory();
        await RunUiHelper.WaitUntilAsync(
            () => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree()
                  || NOverlayStack.Instance?.Peek() != screen,
            token,
            TimeSpan.FromSeconds(30),
            "事件奖励屏幕未处理完");
    }

    /// <summary>
    /// NSimpleCardSelectScreen（任意选牌）：部分事件要求选多张（如 ROOM_FULL_OF_CHEESE 大快朵颐选 2 张），
    /// 确认按钮在选够之前不可用。移植 AutoSlay SimpleCardSelectScreenHandler 的循环：
    /// 反复点未选过的牌，直到确认可用或屏幕自关。
    /// </summary>
    private static async Task DriveSimpleSelectAsync(NSimpleCardSelectScreen screen, CancellationToken token)
    {
        NCardGrid? grid = RunUiHelper.FindFirst<NCardGrid>(screen);
        await RunUiHelper.WaitUntilAsync(
            () => grid != null && FindFirstHolder(screen) != null,
            token,
            TimeSpan.FromSeconds(10),
            "选牌网格未出现");
        if (grid == null)
            return;

        NConfirmButton? confirm = screen.GetNodeOrNull<NConfirmButton>("%Confirm");
        List<NGridCardHolder> picked = [];
        for (int i = 0; i < 10 && IsStillTop(screen); i++)
        {
            if (confirm != null && confirm.IsEnabled)
            {
                await RunUiHelper.ClickAsync(confirm, 200);
                break;
            }
            NGridCardHolder? next = RunUiHelper.FindAll<NGridCardHolder>(screen)
                .FirstOrDefault(h => !picked.Contains(h));
            if (next == null)
                break;
            picked.Add(next);
            RunAutoController.Session?.LogDecision($"事件选牌：点击第 {picked.Count} 张");
            grid.EmitSignal(NCardGrid.SignalName.HolderPressed, next);
            await Task.Delay(300, token);
        }
        await WaitUntilClosed(screen, token, "事件选牌覆盖层未关闭");
    }

    /// <summary>当前覆盖层是否仍是这个选牌屏幕（未被关闭/替换）。</summary>
    private static bool IsStillTop(CanvasItem screen)
        => GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree()
           && NOverlayStack.Instance?.Peek() == screen;

    /// <summary>NDeckCardSelectScreen（从牌组移除/转换）：点一张，走预览确认按钮。</summary>
    private static async Task DriveDeckSelectAsync(NDeckCardSelectScreen screen, CancellationToken token)
    {
        NGridCardHolder? first = FindFirstHolder(screen);
        if (first == null)
            return;
        await Task.Delay(300, token);
        first.EmitSignal(NCardHolder.SignalName.Pressed, first);
        await Task.Delay(200, token);

        Control? previewContainer = screen.GetNodeOrNull<Control>("%PreviewContainer");
        NConfirmButton? confirm = previewContainer?.GetNodeOrNull<NConfirmButton>("%PreviewConfirm");
        if (confirm == null || !confirm.IsEnabled)
        {
            NConfirmButton? mainConfirm = screen.GetNodeOrNull<NConfirmButton>("%Confirm");
            if (mainConfirm != null && mainConfirm.IsEnabled)
            {
                await RunUiHelper.ClickAsync(mainConfirm, 200);
                await Task.Delay(300, token);
            }
            previewContainer = screen.GetNodeOrNull<Control>("%PreviewContainer");
            confirm = previewContainer?.GetNodeOrNull<NConfirmButton>("%PreviewConfirm");
        }
        if (confirm != null && confirm.IsEnabled)
        {
            await RunUiHelper.ClickAsync(confirm, 200);
        }
        await WaitUntilClosed(screen, token, "事件改牌覆盖层未关闭");
    }

    /// <summary>
    /// NDeckEnchantSelectScreen（牌组附魔选牌，如 SELF_HELP_BOOK 读下封底选一张攻击牌附魔）：
    /// 点一张牌，MaxSelect==1 时选满会自动弹单牌附魔预览（%EnchantSinglePreviewContainer），
    /// 再点预览里的 Confirm 确认。选牌先取第一张（冒烟级，后续批次接入评分）。
    /// </summary>
    private static async Task DriveEnchantSelectAsync(NDeckEnchantSelectScreen screen, CancellationToken token)
    {
        NGridCardHolder? first = FindFirstHolder(screen);
        if (first == null)
        {
            await RunUiHelper.WaitUntilAsync(
                () => FindFirstHolder(screen) != null,
                token,
                TimeSpan.FromSeconds(10),
                "附魔选牌网格未出现");
            first = FindFirstHolder(screen);
            if (first == null)
                return;
        }
        await Task.Delay(300, token);
        first.EmitSignal(NCardHolder.SignalName.Pressed, first);
        await Task.Delay(200, token);

        // 单牌附魔预览：确认按钮在预览容器里。预览未出现时退回等它自关（有界）。
        Control? previewContainer = screen.GetNodeOrNull<Control>("%EnchantSinglePreviewContainer");
        await RunUiHelper.WaitUntilAsync(
            () => !IsStillTop(screen)
                  || (previewContainer != null && previewContainer.Visible),
            token,
            TimeSpan.FromSeconds(10),
            "附魔预览未出现");
        if (!IsStillTop(screen))
            return;
        if (previewContainer != null && previewContainer.Visible)
        {
            NConfirmButton? confirm = previewContainer.GetNodeOrNull<NConfirmButton>("Confirm");
            if (confirm != null && confirm.IsEnabled)
            {
                await RunUiHelper.ClickAsync(confirm, 200);
            }
        }
        await WaitUntilClosed(screen, token, "事件附魔选牌覆盖层未关闭");
    }

    /// <summary>NChooseACardSelectionScreen（战斗内选牌，事件药水等）：点一张。</summary>
    private static async Task DriveChooseACardAsync(NChooseACardSelectionScreen screen, CancellationToken token)
    {
        NCardHolder? first = null;
        foreach (NCardHolder holder in RunUiHelper.FindAll<NCardHolder>(screen))
        {
            first = holder;
            break;
        }
        if (first == null)
            return;
        first.EmitSignal(NCardHolder.SignalName.Pressed, first);
        await Task.Delay(100, token);
        await WaitUntilClosed(screen, token, "事件选牌覆盖层未关闭");
    }

    private static NGridCardHolder? FindFirstHolder(Node screen)
    {
        foreach (NGridCardHolder holder in RunUiHelper.FindAll<NGridCardHolder>(screen))
            return holder;
        return null;
    }

    /// <summary>等覆盖层被处理掉（屏幕释放/移出树/顶部换成别的）。</summary>
    private static async Task WaitUntilClosed(CanvasItem screen, CancellationToken token, string message)
    {
        await RunUiHelper.WaitUntilAsync(
            () => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree()
                  || NOverlayStack.Instance?.Peek() != screen,
            token,
            TimeSpan.FromSeconds(15),
            message);
    }
}
