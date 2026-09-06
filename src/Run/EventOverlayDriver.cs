using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

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
                // TANX 三刃回旋镖等"选多张附魔"（MaxSelect>1）与 SELF_HELP_BOOK 单张附魔共用网格：
                // 通用驱动逐张点未选牌，选满自动弹预览（%EnchantSingle/MultiPreviewContainer），
                // 点预览内 Confirm 完成；MaxSelect==1 时点第一张即自动弹单牌预览（行为不变）。
                await DriveGridPreviewSelectAsync(
                    enchantScreen,
                    ["%EnchantSinglePreviewContainer", "%EnchantMultiPreviewContainer"],
                    token,
                    "事件附魔选牌覆盖层未关闭");
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
            case NCrystalSphereScreen crystalSphere:
                await DriveCrystalSphereAsync(crystalSphere, token);
                return true;
            default:
                // NCardRewardSelectionScreen 等由 CardRewardDriver(ShowScreen Postfix) 独立驱动，
                // 不识别时返回 false 会让 EventDriver 兜底等 15s 抢走其处理窗口（153 实证）
                // → 若当前顶层是卡牌奖励屏则直接交还主循环（不等待不干预）。
                if (top is NCardRewardSelectionScreen)
                    return true;
                return false;
        }
    }

    /// <summary>事件给的奖励屏幕（如 WELLSPRING 装瓶给药水、水晶球揭幕、THE_FUTURE_OF_POTIONS）：
    /// 先让事件奖励 worker(OnEventRewards)处理。worker 可能因'子屏幕超时'提前返回、屏仍残留且
    /// Proceed 可用（w4 seed226 实证 overlayTop=NRewardsScreen/1 enabledProceed=1 卡死），此处兜底。
    /// 但 worker 活跃且最近有推进时**不点 Proceed**：其等待都有界（腾栏喝药等动作队列 ≤10s、
    /// 子屏关闭 ≤10s），点早了会跳过正在领取的奖励（净亏药水/卡）。只有 worker 已退出或停摆
    /// （无推进超阈值）才兜底点 Proceed 收尾（SkipLocalRewards 关屏，非 terminal 不触发离房）。
    /// 返回后由 EventDriver 主循环按需再驱动，全程不阻塞 EventDriver 超时。</summary>
    private static async Task DriveRewardsAsync(NRewardsScreen screen, CancellationToken token)
    {
        RunAutoController.Session?.LogDecision("覆盖层奖励屏驱动开始（EventOverlayDriver）");
        RewardsScreenDriver.OnEventRewards();
        if (await TryWaitRewardsClosedAsync(screen, token, "事件奖励屏幕未处理完", TimeSpan.FromSeconds(6)))
            return;

        bool workerProgressing = RewardsScreenDriver.IsWorkerActive
            && System.Environment.TickCount64 - RewardsScreenDriver.LastProgressTick < 12_000;
        if (workerProgressing)
        {
            // worker 仍在正常干活（如满栏腾栏/等子屏回来）：给完整处理窗口，不介入。
            RunAutoController.Session?.LogDecision("覆盖层奖励：worker 活跃推进中，延长等待不介入");
            if (await TryWaitRewardsClosedAsync(screen, token, "事件奖励屏幕未处理完", TimeSpan.FromSeconds(9)))
                return;
        }

        RunAutoController.Session?.LogDecision($"覆盖层奖励：worker 未推进，top={NOverlayStack.Instance?.Peek()?.GetType().Name}");
        if (!GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree())
            return;
        NProceedButton? proceed = screen.GetNodeOrNull<NProceedButton>("%ProceedButton");
        if (proceed != null && proceed.IsEnabled)
        {
            RunAutoController.Session?.LogDecision("事件奖励屏兜底：worker 未收尾，点 Proceed 跳过残留奖励");
            await RunUiHelper.ClickAsync(proceed, 150);
        }
        try
        {
            await WaitUntilClosed(screen, token, "事件奖励屏幕未处理完");
        }
        catch (RunAutoTimeoutException)
        {
            RunAutoController.Session?.LogDecision("事件奖励屏兜底后仍未关闭（交还 EventDriver 再试）");
        }
    }

    /// <summary>等奖励屏关闭：关闭返回 true；超时返回 false（不抛，调用方决定下一步）。</summary>
    private static async Task<bool> TryWaitRewardsClosedAsync(
        NRewardsScreen screen, CancellationToken token, string message, TimeSpan timeout)
    {
        try
        {
            await WaitUntilClosed(screen, token, message, timeout);
            return true;
        }
        catch (RunAutoTimeoutException)
        {
            return false;
        }
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
        // min==0 的"任意选"网格（典型：欧罗巴斯海玻璃——15 张他职业牌任选 0..15 入组）确认按钮一开始
        // 就可点：若不先选牌，AI 会一张不拿直接确认，跨职业渠道零收益。先按价值择优再确认。
        if (confirm != null && confirm.IsEnabled && IsStillTop(screen))
            await PickBestFreePicksAsync(screen, grid, token);
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

    /// <summary>
    /// 任意选（min==0）网格的择优政策：用 CardPickerAI 对当前可见候选评分（接收职业中位数据驱动
    /// + 牌组体系契合），按分降序最多拿 <see cref="MaxFreePicks"/> 张、低于跳过阈值不拿；无玩家上下文
    /// 或一张都不值则保持 0 张（由调用方直接确认）。状态/诅咒不拿。
    /// </summary>
    private static async Task PickBestFreePicksAsync(NSimpleCardSelectScreen screen, NCardGrid grid, CancellationToken token)
    {
        RunAutoSettings.DemoShot("freepick");
        List<NGridCardHolder> holders = RunUiHelper.FindAll<NGridCardHolder>(screen)
            .Where(static h => h.CardModel != null)
            .ToList();
        CardModel? sample = holders.Count == 0 ? null : holders[0].CardModel;
        Player? player = sample?.Owner;
        if (player == null || holders.Count == 0)
            return;
        RunState? runState = sample?.RunState as RunState;
        var scored = new List<(NGridCardHolder Holder, float Score)>();
        foreach (NGridCardHolder holder in holders)
        {
            CardModel? card = holder.CardModel;
            if (card == null || card.Type is CardType.Status or CardType.Curse)
                continue;
            scored.Add((holder, CardPickerAI.Evaluate(card, DeckContext.From(player, runState))));
        }
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        int taken = 0;
        foreach ((NGridCardHolder holder, float score) in scored)
        {
            if (taken >= MaxFreePicks || score < CardPickerAI.SkipThreshold || !IsStillTop(screen))
                break;
            RunAutoController.Session?.LogDecision($"任意选牌：择优 {holder.CardModel?.Id.Entry}（评分 {score:0.##}）");
            grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);
            taken++;
            await Task.Delay(300, token);
        }
        if (taken > 0)
            RunAutoController.Session?.LogDecision($"任意选牌：共择优 {taken} 张入组");
    }

    private const int MaxFreePicks = 5;

    /// <summary>当前覆盖层是否仍是这个选牌屏幕（未被关闭/替换）。</summary>
    private static bool IsStillTop(CanvasItem screen)
        => GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree()
           && NOverlayStack.Instance?.Peek() == screen;

    /// <summary>
    /// NDeckCardSelectScreen（从牌组移除/转换，事件里可能是多选：如 FIELD_OF_MAN_SIZED_HOLES 抵抗诱惑移除 2 张）。
    /// 移植 AutoSlay DeckCardSelectScreenHandler 配方：循环点未选过的牌，直到"预览出现或主确认可用"
    /// （选满 MaxSelect 会自动弹预览）；预览没弹就点主确认弹预览；最后点 %PreviewConfirm 确认。
    /// 修复：旧实现只点 1 张就找确认，多选屏（选满前确认不可用）会 10s 超时 → 事件房永久卡死。
    /// </summary>
    private static async Task DriveDeckSelectAsync(NDeckCardSelectScreen screen, CancellationToken token)
    {
        List<NGridCardHolder> cards = [];
        await RunUiHelper.WaitUntilAsync(
            () => (cards = RunUiHelper.FindAll<NGridCardHolder>(screen)).Count > 0,
            token,
            TimeSpan.FromSeconds(10),
            "改牌选牌网格未出现");
        await Task.Delay(300, token);

        int maxSelections = Math.Min(cards.Count, 5);
        List<NGridCardHolder> selected = [];
        for (int i = 0; i < maxSelections; i++)
        {
            if (!IsStillTop(screen))
                return;
            Control? preview = screen.GetNodeOrNull<Control>("%PreviewContainer");
            NConfirmButton? mainConfirm = screen.GetNodeOrNull<NConfirmButton>("%Confirm");
            if ((preview?.Visible ?? false) || (mainConfirm != null && mainConfirm.IsEnabled))
                break; // 已选满（或本来就可确认）。
            NGridCardHolder? next = cards.FirstOrDefault(c => !selected.Contains(c));
            if (next == null)
                break;
            selected.Add(next);
            RunAutoController.Session?.LogDecision($"事件改牌：选择第 {selected.Count} 张");
            next.EmitSignal(NCardHolder.SignalName.Pressed, next);
            await Task.Delay(250, token);
        }

        // 预览未自动出现时（选满未触发或单确认流程），点主确认弹预览。
        Control? previewNow = screen.GetNodeOrNull<Control>("%PreviewContainer");
        NConfirmButton? mainNow = screen.GetNodeOrNull<NConfirmButton>("%Confirm");
        if ((previewNow == null || !previewNow.Visible) && mainNow != null && mainNow.IsEnabled)
        {
            await RunUiHelper.ClickAsync(mainNow, 200);
            await Task.Delay(250, token);
            previewNow = screen.GetNodeOrNull<Control>("%PreviewContainer");
        }

        // 等预览出现或屏幕自关。
        await RunUiHelper.WaitUntilAsync(
            () => !IsStillTop(screen)
                  || (previewNow = screen.GetNodeOrNull<Control>("%PreviewContainer")) is { Visible: true },
            token,
            TimeSpan.FromSeconds(10),
            "改牌预览未出现");
        if (!IsStillTop(screen))
            return;

        NConfirmButton? confirm = previewNow?.GetNodeOrNull<NConfirmButton>("%PreviewConfirm")
            ?? RunUiHelper.FindAll<NConfirmButton>(screen).FirstOrDefault(b => b.IsEnabled);
        if (confirm != null)
        {
            await RunUiHelper.WaitUntilAsync(
                () => !IsStillTop(screen) || confirm.IsEnabled,
                token,
                TimeSpan.FromSeconds(5),
                "改牌确认未变可用");
            if (IsStillTop(screen))
                await RunUiHelper.ClickAsync(confirm, 200);
        }
        await WaitUntilClosed(screen, token, "事件改牌覆盖层未关闭");
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

    /// <summary>
    /// CrystalSphere（水晶球揭示小游戏）屏驱动（移植 AutoSlay CrystalSphereScreenHandler）。
    /// 机制（decomp 核对 CrystalSphereMinigame/CrystalSphereCell/NCrystalSphereScreen）：
    /// 11×11 迷雾棋盘，四角+十字预清空，15 件奖品（1 遗物 4×4/2 普药/1 稀药/白蓝金卡各 2×2/
    /// 1 诅咒 2×2/5 小金 1×1/2 大金 2×1）随机铺定——**每格 Cell.Item 公开可读（SL 确定性）**。
    /// 每次占卜 = 点 1 格，Big 工具清 3×3（点击格+8 邻域）；某物品全部占格被清即揭示
    /// （RevealItem → 计入 _revealed → 水晶球结束后逐件/批量发奖励屏）。
    /// **选格策略（SL 级，用户规则）**：读棋盘布局，优先完成高价值物品（遗物>稀有卡>稀有药水>
    /// 白/蓝卡与普药>金币），诅咒占格禁区不主动碰；同价值取顺带推进最多的点击。
    /// </summary>
    private static async Task DriveCrystalSphereAsync(NCrystalSphereScreen screen, CancellationToken token)
    {
        await Task.Delay(1000, token); // 等小游戏动画/初始化。

        NProceedButton? proceed = screen.GetNodeOrNull<NProceedButton>("%ProceedButton");
        if (proceed != null && proceed.IsEnabled)
        {
            await LeaveCrystalSphereAsync(screen, token);
            return;
        }

        int clicks = 0;
        while (clicks < 30)
        {
            if (!IsStillTop(screen))
                return; // 奖励屏已接管/屏幕关闭 → 交还主循环。
            proceed = screen.GetNodeOrNull<NProceedButton>("%ProceedButton");
            if (proceed != null && proceed.IsEnabled)
                break;

            Control? cells = screen.GetNodeOrNull<Control>("%Cells");
            if (cells == null)
            {
                await Task.Delay(100, token);
                continue;
            }
            List<NCrystalSphereCell> all = [];
            foreach (NCrystalSphereCell cell in RunUiHelper.FindAll<NCrystalSphereCell>(cells))
            {
                if (cell.Visible)
                    all.Add(cell);
            }
            NCrystalSphereCell? pick = SelectBestRevealCell(all);
            if (pick == null)
                break; // 无可点格：等奖励屏或离开。

            RunAutoController.Session?.LogDecision(
                $"水晶球：揭示 ({pick.Entity.X},{pick.Entity.Y})（SL 选格）");
            pick.EmitSignal(NClickableControl.SignalName.Released, pick);
            await Task.Delay(500, token);
            clicks++;
        }

        // 等离开可用 / 顶层被奖励屏接管 / 屏幕自关。
        await RunUiHelper.WaitUntilAsync(
            () =>
            {
                if (!IsStillTop(screen))
                    return true;
                NProceedButton? p = screen.GetNodeOrNull<NProceedButton>("%ProceedButton");
                return p != null && p.IsEnabled;
            },
            token,
            TimeSpan.FromSeconds(15),
            "水晶球：既无离开按钮也无奖励屏");
        if (!IsStillTop(screen))
            return;
        proceed = screen.GetNodeOrNull<NProceedButton>("%ProceedButton");
        if (proceed != null && proceed.IsEnabled)
        {
            await LeaveCrystalSphereAsync(screen, token);
        }
    }

    /// <summary>
    /// SL 级选格：读全盘 Item 布局，选一次点击（Big 3×3）能"完整揭示价值最高物品"的格；
    /// 无物品可当场完成时，选覆盖最多高价值物品剩余迷雾格、且不碰诅咒的格。
    /// </summary>
    private static NCrystalSphereCell? SelectBestRevealCell(List<NCrystalSphereCell> all)
    {
        if (all.Count == 0)
            return null;

        // 索引：item 实例 → 其占格（Position..Position+Size-1 处的 cell），并统计剩余迷雾占格。
        var items = new List<(CrystalSphereItem Item, List<NCrystalSphereCell> Occupied)>();
        var seen = new HashSet<CrystalSphereItem>();
        foreach (NCrystalSphereCell cell in all)
        {
            CrystalSphereItem? item = cell.Entity.Item;
            if (item == null || !seen.Add(item))
                continue;
            var occupied = new List<NCrystalSphereCell>();
            for (int x = item.Position.X; x < item.Position.X + item.Size.X && x < 11; x++)
            {
                for (int y = item.Position.Y; y < item.Position.Y + item.Size.Y && y < 11; y++)
                {
                    NCrystalSphereCell? owner = all.FirstOrDefault(c => c.Entity.X == x && c.Entity.Y == y);
                    if (owner != null)
                        occupied.Add(owner);
                }
            }
            items.Add((item, occupied));
        }

        // 候选点击格 = 仍隐藏的格（Big 3×3 以它为中心）。
        var hidden = all.Where(c => c.Entity.IsHidden).ToList();
        if (hidden.Count == 0)
            return null;

        // 价值分级（物品类型由具体子类决定；具体内容由揭示后奖励 rng 决定，这里按类型/稀有度定锚）。
        float ItemValue(CrystalSphereItem item)
        {
            return item switch
            {
                CrystalSphereRelic => 100f,
                CrystalSphereCurse => -60f, // 诅咒：不主动拿
                CrystalSphereCardReward card => CardRewardValue(card),
                CrystalSpherePotion potion => PotionRewardValue(potion),
                CrystalSphereGold gold => GoldValue(gold),
                _ => 10f,
            };
        }

        // 卡奖励稀有度私有字段 _rarity（CardRarity）：反射读（readonly 字段，只读安全）。
        static float CardRewardValue(CrystalSphereCardReward card)
        {
            System.Reflection.FieldInfo? f = typeof(CrystalSphereCardReward)
                .GetField("_rarity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            string? rarity = f?.GetValue(card)?.ToString();
            return rarity switch
            {
                "Rare" => 70f,
                "Uncommon" => 50f,
                _ => 32f,
            };
        }

        static float PotionRewardValue(CrystalSpherePotion potion)
        {
            System.Reflection.FieldInfo? f = typeof(CrystalSpherePotion)
                .GetField("_rarity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            string? rarity = f?.GetValue(potion)?.ToString();
            return rarity == "Rare" ? 55f : 30f;
        }

        static float GoldValue(CrystalSphereGold gold)
        {
            System.Reflection.FieldInfo? f = typeof(CrystalSphereGold)
                .GetField("_isBig", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            bool big = f?.GetValue(gold) is true;
            return big ? 14f : 5f;
        }

        bool IsCurse(CrystalSphereItem item) => item is CrystalSphereCurse;

        // 候选点击格计分：完成物品价值优先；触碰诅咒重罚。
        NCrystalSphereCell? best = null;
        float bestScore = float.MinValue;
        string? bestWhy = null;
        foreach (NCrystalSphereCell candidate in hidden)
        {
            // 本次点击将清空的 3×3 邻域（Big 工具：点击格 + 8 邻域，越界裁剪）。
            var cleared = new HashSet<NCrystalSphereCell>();
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = candidate.Entity.X + dx;
                    int ny = candidate.Entity.Y + dy;
                    if (nx < 0 || nx >= 11 || ny < 0 || ny >= 11)
                        continue;
                    NCrystalSphereCell? hit = all.FirstOrDefault(c => c.Entity.X == nx && c.Entity.Y == ny);
                    if (hit != null && hit.Entity.IsHidden)
                        cleared.Add(hit);
                }
            }

            // 本击后能完整揭示的物品（剩余迷雾占格全部落在本次 cleared 内）。
            float completedValue = 0f;
            float curseHits = 0f;
            float progressValue = 0f;
            var touchedItems = new HashSet<CrystalSphereItem>();
            foreach ((CrystalSphereItem item, List<NCrystalSphereCell> occupied) in items)
            {
                if (touchedItems.Contains(item))
                    continue;
                bool anyTouched = false;
                foreach (NCrystalSphereCell occ in occupied)
                {
                    if (cleared.Contains(occ))
                    {
                        anyTouched = true;
                        break;
                    }
                }
                if (!anyTouched)
                    continue;
                touchedItems.Add(item);
                if (IsCurse(item))
                {
                    curseHits += 1f;
                    continue;
                }
                bool allClear = occupied.All(occ => !occ.Entity.IsHidden || cleared.Contains(occ));
                if (allClear)
                    completedValue += ItemValue(item);
                else
                    progressValue += ItemValue(item) * 0.15f; // 推进了但没完成：给一点进度分
            }

            float score = completedValue + progressValue - curseHits * 80f;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
                bestWhy = $"完成={completedValue:0.#} 推进={progressValue:0.#} 触诅咒={curseHits}";
            }
        }

        if (best != null)
        {
            RunAutoController.Session?.LogDecision($"水晶球选格：{bestWhy}");
        }
        return best;
    }

    /// <summary>
    /// 若当前覆盖层栈顶是水晶球"服务收尾"屏（Proceed 可用）则点离开收尾。
    /// 奖励屏全部结束后水晶球屏回到栈顶等待离开——由最后一个奖励处理者（RewardsScreenDriver
    /// 事件模式）负责收尾最可靠（EventDriver 在长奖励链期间可能停在等待上，133 实证无心跳）。
    /// </summary>
    internal static async Task TryFinalizeCrystalScreenIfTopAsync(CancellationToken token)
    {
        if (NOverlayStack.Instance?.Peek() is not NCrystalSphereScreen crystalScreen)
            return;
        NProceedButton? proceed = crystalScreen.GetNodeOrNull<NProceedButton>("%ProceedButton");
        if (proceed == null || !proceed.IsEnabled)
            return;
        RunAutoController.Session?.LogDecision("水晶球收尾：奖励已结清，点离开");
        await LeaveCrystalSphereAsync(crystalScreen, token);
    }

    /// <summary>点水晶球离开按钮；点后若未自动退栈则显式移除（卡牌屏超时移除同款防御，
    /// 153 已验证模式）。奖励收尾后水晶球屏回到栈顶(Proceed 可用)——必须点离开才会
    /// 触发事件收尾开图，拖住会导致 map 永远不开（133 实证 NCrystalSphereScreen/1 卡死）。</summary>
    private static async Task LeaveCrystalSphereAsync(NCrystalSphereScreen screen, CancellationToken token)
    {
        NProceedButton? proceed = screen.GetNodeOrNull<NProceedButton>("%ProceedButton");
        if (proceed == null || !proceed.IsEnabled)
            return;
        RunAutoController.Session?.LogDecision("水晶球：点离开按钮收尾");
        await RunUiHelper.ClickAsync(proceed, 200);
        await Task.Delay(400, token);
        if (IsStillTop(screen))
        {
            RunAutoController.Session?.LogDecision("水晶球：点离开后仍在栈顶，显式移除覆盖层");
            NOverlayStack.Instance?.Remove(screen);
            await Task.Delay(100, token);
        }
        try
        {
            await WaitUntilClosed(screen, token, "水晶球离开未完成");
        }
        catch (RunAutoTimeoutException)
        {
            RunAutoController.Session?.LogDecision("水晶球离开兜底完成（超时不再等待）");
        }
    }

    private static NGridCardHolder? FindFirstHolder(Node screen)
    {
        foreach (NGridCardHolder holder in RunUiHelper.FindAll<NGridCardHolder>(screen))
            return holder;
        return null;
    }

    /// <summary>等覆盖层被处理掉（屏幕释放/移出树/顶部换成别的）。</summary>
    private static async Task WaitUntilClosed(
        CanvasItem screen,
        CancellationToken token,
        string message,
        TimeSpan? timeout = null)
    {
        await RunUiHelper.WaitUntilAsync(
            () => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree()
                  || NOverlayStack.Instance?.Peek() != screen,
            token,
            timeout ?? TimeSpan.FromSeconds(15),
            message);
    }
}
