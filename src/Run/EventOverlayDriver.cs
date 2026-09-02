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
            case NDeckTransformSelectScreen transformScreen:
                await DriveGridPreviewSelectAsync(
                    transformScreen,
                    ["%PreviewContainer"],
                    token,
                    "事件转化选牌覆盖层未关闭");
                return true;
            case NDeckUpgradeSelectScreen upgradeScreen:
                await DriveGridPreviewSelectAsync(
                    upgradeScreen,
                    ["%UpgradeSinglePreviewContainer", "%UpgradeMultiPreviewContainer"],
                    token,
                    "事件升级选牌覆盖层未关闭");
                return true;
            case NChooseABundleSelectionScreen bundleScreen:
                await DriveBundleSelectAsync(bundleScreen, token);
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

    /// <summary>
    /// NChooseACardSelectionScreen（战斗内选牌，事件药水/遗物追加牌等，如 LeadPaperweight 无色 2 选 1）：
    /// 点一张牌。注意游戏 SelectHolder 有 350ms 误点保护（_openedTicks 检查），屏幕刚打开就点会被忽略，
    /// 必须先等网格就绪并越过该窗口；点击后未关闭则换下一张重试。
    /// </summary>
    private static async Task DriveChooseACardAsync(NChooseACardSelectionScreen screen, CancellationToken token)
    {
        await RunUiHelper.WaitUntilAsync(
            () => FindFirstHolder(screen) != null || !IsStillTop(screen),
            token,
            TimeSpan.FromSeconds(10),
            "事件选牌网格未出现");
        // 越过游戏 350ms 误点保护窗口（SelectHolder: Time.GetTicksMsec() - _openedTicks > 350）。
        await Task.Delay(600, token);
        List<NGridCardHolder> clicked = [];
        for (int attempt = 0; attempt < 3 && IsStillTop(screen); attempt++)
        {
            NGridCardHolder? next = RunUiHelper.FindAll<NGridCardHolder>(screen)
                .FirstOrDefault(holder => !clicked.Contains(holder));
            if (next == null)
                break;
            clicked.Add(next);
            next.EmitSignal(NCardHolder.SignalName.Pressed, next);
            await Task.Delay(300, token);
        }
        await WaitUntilClosed(screen, token, "事件选牌覆盖层未关闭");
    }

    /// <summary>
    /// NDeckTransformSelectScreen（牌组转化选牌：MORPHIC_GROVE 选 2 张、TANX 武器遗物把打击转成武器
    /// 选最多 6 张且 RequireManualConfirmation）与 NDeckUpgradeSelectScreen（牌组升级选牌）通用驱动：
    /// 逐张点选网格卡牌；选满 MaxSelect 时游戏自动弹预览（%PreviewContainer /
    /// %UpgradeSinglePreviewContainer / %UpgradeMultiPreviewContainer），点预览内 Confirm 完成。
    /// RequireManualConfirmation 屏（如 TANX 武器，MinSelect=0）主确认只打开预览不直接完成——
    /// 必须继续循环点预览确认；主确认只在没有更多可选卡时使用（避免 MinSelect=0 时空选直接收尾）。
    /// </summary>
    private static async Task DriveGridPreviewSelectAsync(
        CanvasItem screen,
        string[] previewContainerNames,
        CancellationToken token,
        string timeoutMessage)
    {
        await RunUiHelper.WaitUntilAsync(
            () => FindFirstHolder(screen) != null || !IsStillTop(screen),
            token,
            TimeSpan.FromSeconds(10),
            "选牌网格未出现");
        await Task.Delay(400, token);

        bool IsPreviewOpen()
        {
            foreach (string name in previewContainerNames)
            {
                if (screen.GetNodeOrNull<Control>(name) is { Visible: true })
                    return true;
            }
            return false;
        }

        List<NGridCardHolder> clicked = [];
        for (int attempt = 0; attempt < 16 && IsStillTop(screen); attempt++)
        {
            // 预览已开（选满 Max 自动弹，或主确认 ManualConfirm 打开）→ 点预览内 Confirm 完成。
            if (IsPreviewOpen())
            {
                foreach (string name in previewContainerNames)
                {
                    NConfirmButton? previewConfirm = screen
                        .GetNodeOrNull<Control>(name)?
                        .GetNodeOrNull<NConfirmButton>("Confirm");
                    if (previewConfirm != null && previewConfirm.IsEnabled)
                    {
                        await RunUiHelper.ClickAsync(previewConfirm, 200);
                        break;
                    }
                }
                break;
            }

            NGridCardHolder? next = RunUiHelper.FindAll<NGridCardHolder>(screen)
                .FirstOrDefault(holder => !clicked.Contains(holder));
            if (next == null)
            {
                // 没有更多可选卡：点主确认收尾。ManualConfirm 屏会打开预览，
                // 下一轮循环点预览确认；非 ManualConfirm 屏直接完成并关闭。
                NConfirmButton? mainConfirm = screen.GetNodeOrNull<NConfirmButton>("Confirm");
                if (mainConfirm != null && mainConfirm.IsEnabled)
                {
                    await RunUiHelper.ClickAsync(mainConfirm, 200);
                    continue;
                }
                break;
            }
            clicked.Add(next);
            next.EmitSignal(NCardHolder.SignalName.Pressed, next);
            await Task.Delay(300, token);
        }
        await WaitUntilClosed(screen, token, timeoutMessage);
    }

    /// <summary>
    /// NChooseABundleSelectionScreen（选一组卡牌捆绑包，如 ScrollBoxes 遗物）：点第一组，
    /// 预览容器（%BundlePreviewContainer）出现后点 %Confirm 完成。
    /// </summary>
    private static async Task DriveBundleSelectAsync(NChooseABundleSelectionScreen screen, CancellationToken token)
    {
        await RunUiHelper.WaitUntilAsync(
            () => RunUiHelper.FindFirst<NCardBundle>(screen) != null || !IsStillTop(screen),
            token,
            TimeSpan.FromSeconds(10),
            "捆绑包选项未出现");
        await Task.Delay(400, token);
        for (int attempt = 0; attempt < 5 && IsStillTop(screen); attempt++)
        {
            Control? preview = screen.GetNodeOrNull<Control>("%BundlePreviewContainer");
            if (preview is { Visible: true })
            {
                NConfirmButton? confirm = preview.GetNodeOrNull<NConfirmButton>("%Confirm");
                if (confirm != null && confirm.IsEnabled)
                {
                    await RunUiHelper.ClickAsync(confirm, 200);
                    break;
                }
            }
            NCardBundle? bundle = RunUiHelper.FindFirst<NCardBundle>(screen);
            if (bundle == null)
                break;
            bundle.EmitSignal(NCardBundle.SignalName.Clicked, bundle);
            await Task.Delay(400, token);
        }
        await WaitUntilClosed(screen, token, "事件捆绑包选牌覆盖层未关闭");
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
