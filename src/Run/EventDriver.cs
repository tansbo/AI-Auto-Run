using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 事件房间驱动：逐个点选未锁定的事件选项（避免会杀死玩家的选项），
/// 事件内开战或打开覆盖层时等待其处理完再继续，直到事件结束回地图。
/// 由 RunAutoController 在进入 Event 房间时启动。
/// </summary>
internal static class EventDriver
{
    private static bool _active;
    private static long _lastBeatTick;

    /// <summary>事件驱动当前是否在跑（供 RunAutoController 看门狗判断是否需要重启）。</summary>
    internal static bool IsActive => _active;

    private const int MaxIterations = 300;

    public static void OnRoomEntered()
    {
        RunAutoSession? session = RunAutoController.Session;
        if (session == null || !RunAutoSettings.Enabled || _active)
            return;
        _active = true;
        TaskHelper.RunSafely(HandleAsync());
    }

    private static async Task HandleAsync()
    {
        try
        {
            RunAutoSession? session = RunAutoController.Session;
            if (session == null)
                return;
            CancellationToken token = session.CancellationToken;

            Node root = ((SceneTree)Godot.Engine.GetMainLoop()).Root;
            NEventRoom? room = null;
            await RunUiHelper.WaitUntilAsync(
                () => (room = RunUiHelper.FindFirst<NEventRoom>(root)) != null,
                token,
                TimeSpan.FromSeconds(15),
                "事件房间未出现");
            if (room == null)
                return;

            // 渠道演示脚手架（每幕一次）：指定幕的首个事件房入口先强制获得目标遗物（如海玻璃）。
            // 注意海玻璃 AfterObtained 用 BlockingPlayerChoiceContext 等玩家从网格选牌——若在此
            // await 会与驱动循环互锁（同商店移除避死锁配方）：改为 fire-and-forget，由主循环
            // 驱动其弹出的覆盖层（NSimpleCardSelectScreen 网格已支持择优），选择完成后任务收尾入组。
            RunState? rs = session.RunState;
            if (rs != null
                && RunAutoSettings.ForceActRelicAct >= 0
                && rs.CurrentActIndex == RunAutoSettings.ForceActRelicAct
                && !string.IsNullOrWhiteSpace(RunAutoSettings.ForceActRelicId)
                && session.ForceRelicActsDone.Add(rs.CurrentActIndex))
            {
                Player? forcePlayer = LocalContext.GetMe(rs);
                RelicModel? forced = BuildForceRelic(rs, RunAutoSettings.ForceActRelicId);
                if (forcePlayer != null && forced != null)
                {
                    session.LogDecision($"脚手架：事件房入口强制获得 {forced.Id.Entry}");
                    _ = TaskHelper.RunSafely(ForceObtainFireAndForgetAsync(forced, forcePlayer, token));
                }
            }

            for (int iteration = 0; iteration < MaxIterations; iteration++)
            {
                session = RunAutoController.Session;
                if (session == null)
                    return;

                // 心跳（节流）：确认事件驱动仍在主循环（水晶球后"无任何日志"疑云用）。
                if (System.Environment.TickCount64 - _lastBeatTick > 5000)
                {
                    _lastBeatTick = System.Environment.TickCount64;
                    session.LogDecision(
                        $"事件驱动心跳 it={iteration} roomChildren={(room != null && GodotObject.IsInstanceValid(room) ? room.GetChildCount() : -1)} " +
                        $"top={NOverlayStack.Instance?.Peek()?.GetType().Name ?? "无"}/{NOverlayStack.Instance?.ScreenCount ?? 0} " +
                        $"map={NMapScreen.Instance?.IsOpen}");
                }

                // 房间节点可能被游戏重建/替换（自定义事件切布局页）：用最新实例，旧引用失效会误退。
                NEventRoom? currentRoom = RunUiHelper.FindFirst<NEventRoom>(root);
                if (currentRoom == null)
                {
                    // 事件房消失但地图未开：可能是房→图过渡的一瞬。短等（≤10s）让地图/新房间出现。
                    bool mapOpen = NMapScreen.Instance is { IsOpen: true };
                    for (int w = 0; w < 40 && !mapOpen; w++)
                    {
                        await Task.Delay(250, token);
                        mapOpen = NMapScreen.Instance is { IsOpen: true };
                        currentRoom = RunUiHelper.FindFirst<NEventRoom>(root);
                        if (currentRoom != null)
                            break;
                    }
                    if (mapOpen)
                    {
                        MapRouter.RequestRoute();
                        return;
                    }
                    if (currentRoom == null)
                    {
                        session.LogDecision("事件驱动：事件房消失且地图未开（过渡未完成），退出等待兜底");
                        return;
                    }
                }
                if (!ReferenceEquals(currentRoom, room))
                {
                    session.LogDecision("事件驱动：事件房节点已重建/替换，采用最新实例继续");
                    room = currentRoom;
                }
                if (NMapScreen.Instance is { IsOpen: true })
                {
                    // 事件自收尾把地图打开（水晶球 OfferCustom 等奖励屏关掉后事件即结束开图）：
                    // 这里可能没有"选项→地图"的显式路径，补一次选路请求。
                    // 若 NMapScreenPatch 已在路由则去重/排队重试（有界），不会重复选路。
                    MapRouter.RequestRoute();
                    return;
                }

                // 事件触发的战斗：交给战斗求解器，等打完再继续。
                if (CombatManager.Instance.IsInProgress)
                {
                    session.LogDecision("事件触发战斗，等战斗求解器处理");
                    await RunUiHelper.WaitUntilAsync(
                        () => !CombatManager.Instance.IsInProgress,
                        token,
                        TimeSpan.FromSeconds(120),
                        "事件战斗未结束");
                    continue;
                }

                // 事件选项打开的覆盖层（奖励/选牌/移除等）：优先驱动它处理完；
                // 不认识的覆盖层才退回"等它自己关闭"（有界）。
                if (NOverlayStack.Instance is { ScreenCount: > 0 })
                {
                    if (!await EventOverlayDriver.DriveAsync(token))
                    {
                        // 诊断：打印顶层覆盖层类型，定位"水晶球服务结束/未知屏"卡死的真实 UI 状态。
                        string topDesc = NOverlayStack.Instance?.Peek()?.GetType().Name ?? "null";
                        int depth = NOverlayStack.Instance?.ScreenCount ?? 0;
                        RunAutoController.Session?.LogDecision(
                            $"事件覆盖层未识别：top={topDesc} depth={depth}");
                        await RunUiHelper.WaitUntilAsync(
                            () => NOverlayStack.Instance is { ScreenCount: 0 },
                            token,
                            TimeSpan.FromSeconds(15),
                            "事件覆盖层未关闭");
                    }
                    continue;
                }

                // Ancient 对话（Neow 开场/DONE 页）：点命区翻页直到出现可点选项。
                if (await TryClickAncientDialogueAsync(room, token))
                    continue;

                // 自定义事件：FakeMerchant（假商人）没有事件选项，用 NProceedButton 离开（AutoSlay 配方）。
                if (RunUiHelper.FindFirst<NFakeMerchant>(room) is { } fakeMerchant)
                {
                    await DriveFakeMerchantAsync(fakeMerchant, token);
                    continue;
                }

                List<NEventOptionButton> options = [];
                foreach (NEventOptionButton button in RunUiHelper.FindAll<NEventOptionButton>(room))
                {
                    if (button.IsEnabled && !button.Option.IsLocked)
                        options.Add(button);
                }
                if (options.Count == 0)
                {
                    // 事件收尾页常无 EventOption 而是房间级离开钮（FakeMerchant 同款 NProceedButton）：
                    // 先点它离开（THE_FUTURE 等收尾页，226 实证 rewards 移除后仍无选项卡死）。
                    bool clickedLeave = false;
                    if (NOverlayStack.Instance is not { ScreenCount: > 0 }
                        && NMapScreen.Instance is not { IsOpen: true }
                        && !CombatManager.Instance.IsInProgress)
                    {
                        NProceedButton? roomLeave = null;
                        foreach (NProceedButton button in RunUiHelper.FindAll<NProceedButton>(room))
                        {
                            if (button.Visible && button.IsEnabled)
                            {
                                roomLeave = button;
                                break;
                            }
                        }
                        if (roomLeave != null)
                        {
                            session.LogDecision("事件选项空但房间有可用离开钮，点离开收尾");
                            await RunUiHelper.ClickAsync(roomLeave, 200);
                            clickedLeave = true;
                        }
                    }
                    if (clickedLeave)
                        continue;
                    // 事件 UI 还没就绪。之前只 delay 200ms 快速空转，MaxIterations=50 约 10s 就
                    // 静默放弃，headless 下事件场景加载慢/未完成时会把跑局永久卡死在事件房。
                    // 改为有界等待（可点选项/远古对话可翻页/地图/战斗/覆盖层/房间消失任一即恢复），
                    // 超时打印完整状态定位卡点后再按原逻辑抛出。等待中每 ~5s 打一次状态，
                    // 供"事件收尾后无选项卡死"定位（水晶球奖励后 133 实证，无任何日志）。
                    bool ready = false;
                    for (int tick = 0; tick < 90; tick++)
                    {
                        if (EventReadyOrGone(room))
                        {
                            ready = true;
                            break;
                        }
                        if (tick % 10 == 9)
                        {
                            RunAutoController.Session?.LogDecision(
                                $"事件选项未出现等待 {tick + 1}/90：{DescribeEventState(room)}");
                        }
                        await Task.Delay(500, token);
                    }
                    if (!ready)
                    {
                        session.LogDecision($"事件 UI 超时就绪失败：{DescribeEventState(room)}");
                        throw new RunAutoTimeoutException("事件选项未出现");
                    }
                    continue;
                }

                RunState? runState = RunManager.Instance.DebugOnlyGetState();
                NEventOptionButton? choice = ChooseOption(options, runState, out string basis);
                if (choice == null)
                    return;

                var before = new HashSet<NEventOptionButton>(options);
                session.LogDecision(
                    $"事件：{choice.Event?.Id.Entry ?? "unknown"} → {choice.Option.Title.GetFormattedText()}（{basis}）");
                if (choice.Option.Relic != null)
                {
                    // 遗物获得语料：事件/先古发遗物的选项。
                    RunAutoController.Session?.Telemetry.RecordRelicObtained(choice.Option.Relic.Id.Entry);
                }
                // 先古遗物等关键选择：停顿一下让底部覆盖层显示推荐，用户能看清再点。
                if (choice.Option.Relic != null)
                    await Task.Delay(1500, token);
                RunAutoSettings.DemoShot("event");
                await RunAutoSettings.HoldForDemoAsync(token); // 演示定格：事件选项+决策条留屏
                await RunUiHelper.ClickAsync(choice, 250);

                // 等选项刷新 / 覆盖层打开 / 地图打开 / 房间消失 / 战斗开始。
                bool mapOpened = false;
                await RunUiHelper.WaitUntilAsync(
                    () =>
                    {
                        if (!GodotObject.IsInstanceValid(room) || !room.IsInsideTree())
                            return true;
                        if (NMapScreen.Instance is { IsOpen: true })
                        {
                            mapOpened = true;
                            return true;
                        }
                        if (CombatManager.Instance.IsInProgress)
                            return true;
                        if (NOverlayStack.Instance is { ScreenCount: > 0 })
                            return true;
                        List<NEventOptionButton> now = [];
                        foreach (NEventOptionButton button in RunUiHelper.FindAll<NEventOptionButton>(room))
                        {
                            if (!button.Option.IsLocked)
                                now.Add(button);
                        }
                        return now.Count == 0 || !SetsEqual(before, now);
                    },
                    token,
                    TimeSpan.FromSeconds(10),
                    "事件选项未刷新");

                if (mapOpened)
                {
                    // 事件完成（PROCEED）只打开地图，不触发 RoomExited 房间退出事件。
                    // 选路主路径是 NMapScreenPatch 在 Open 时触发，这里保留调用作兜底
                    // （若补丁未生效仍能前进），_routingActive 去重不会双路由。
                    session.LogDecision("事件完成，地图已打开，请求选路");
                    MapRouter.RequestRoute();
                    return;
                }
            }

            // 循环耗尽（MaxIterations）仍未处理完：事件房还在但没有可交互选项。
            // 之前这里静默返回，_active=false 后事件房无人驱动，整局永久卡死。
            // 现在打印完整状态定位卡点（Ancient 对话翻页/选项未出现等），供下一轮修复。
            if (GodotObject.IsInstanceValid(room) && room.IsInsideTree()
                && NMapScreen.Instance is not { IsOpen: true }
                && !CombatManager.Instance.IsInProgress
                && NOverlayStack.Instance is not { ScreenCount: > 0 })
            {
                bool hasClickable = false;
                foreach (NEventOptionButton button in RunUiHelper.FindAll<NEventOptionButton>(room))
                {
                    if (button.IsEnabled && !button.Option.IsLocked)
                    {
                        hasClickable = true;
                        break;
                    }
                }
                if (!hasClickable)
                    session.LogDecision($"事件驱动循环耗尽：{DescribeEventState(room)}");
            }
        }
        catch (OperationCanceledException)
        {
            // 跑局结束，静默退出。
        }
        catch (RunAutoTimeoutException ex)
        {
            RunAutoController.Session?.LogDecision($"事件处理超时：{ex.Message}");
        }
        finally
        {
            _active = false;
        }
    }

    /// <summary>
    /// FakeMerchant（假商人）自定义事件处理：该事件不用事件选项按钮，而是 NProceedButton
    /// （假遗物商店可直接离开）。移植 AutoSlay EventRoomHandler.HandleFakeMerchantEvent 配方；
    /// 离开后 HideScreen 打开地图，等地图出现后请求选路（兜底，补丁触发时会被去重）。
    /// </summary>
    private static async Task DriveFakeMerchantAsync(NFakeMerchant fakeMerchant, CancellationToken token)
    {
        RunAutoController.Session?.LogDecision("自定义事件：FakeMerchant（假商人），点离开");
        NProceedButton? proceed = null;
        await RunUiHelper.WaitUntilAsync(
            () => (proceed = RunUiHelper.FindFirst<NProceedButton>(fakeMerchant)) != null
                  && proceed.IsEnabled && proceed.Visible,
            token,
            TimeSpan.FromSeconds(10),
            "FakeMerchant 离开按钮不可用");
        if (proceed != null)
            await RunUiHelper.ClickAsync(proceed, 200);
        // HideScreen -> NMapScreen.Open()：等地图出现（或假商人节点被释放），再请求选路。
        await RunUiHelper.WaitUntilAsync(
            () => NMapScreen.Instance is { IsOpen: true }
                  || !GodotObject.IsInstanceValid(fakeMerchant)
                  || !fakeMerchant.IsInsideTree(),
            token,
            TimeSpan.FromSeconds(10),
            "FakeMerchant 地图未打开");
        if (NMapScreen.Instance is { IsOpen: true })
        {
            RunAutoController.Session?.LogDecision("自定义事件完成，地图已打开，请求选路");
            MapRouter.RequestRoute();
        }
    }

    /// <summary>渠道演示脚手架：按 Id 构建强制遗物（海玻璃预置一个非当前职业的 CharacterId 才有跨职业意义）。</summary>
    private static RelicModel? BuildForceRelic(RunState runState, string relicId)
    {
        Player? me = LocalContext.GetMe(runState);
        string receivingName = me?.Character?.GetType().Name ?? string.Empty;
        return relicId.ToUpperInvariant() switch
        {
            "KALEIDOSCOPE" => ModelDb.Relic<Kaleidoscope>().ToMutable(),
            "SEA_GLASS" => BuildSeaGlass(receivingName),
            "PRISMATIC_GEM" => ModelDb.Relic<PrismaticGem>().ToMutable(),
            _ => null,
        };
    }

    private static RelicModel BuildSeaGlass(string receivingName)
    {
        SeaGlass seaGlass = (SeaGlass)ModelDb.Relic<SeaGlass>().ToMutable();
        // 海玻璃 CharacterId 决定 15 张牌的来源职业：选一个不同于当前玩家的职业（优先 Silent）。
        seaGlass.CharacterId = receivingName.Equals(nameof(Silent), StringComparison.Ordinal)
            ? ModelDb.Character<Ironclad>().Id
            : ModelDb.Character<Silent>().Id;
        return seaGlass;
    }

    /// <summary>渠道演示脚手架：fire-and-forget 获得遗物（不 await，避免与覆盖层驱动互锁）。</summary>
    private static async Task ForceObtainFireAndForgetAsync(RelicModel relic, Player player, CancellationToken token)
    {
        try
        {
            await RelicCmd.Obtain(relic, player);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        catch (OperationCanceledException)
        {
            // 跑局结束取消，正常。
        }
        catch (Exception ex)
        {
            RunAutoController.Session?.LogDecision($"脚手架：强制获得遗物失败 {relic.Id.Entry}: {ex.Message}");
        }
        token.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 选一个选项：先排除会杀死玩家的选项；先古遗物（全遗物三选）用遗物评分；
    /// 其余事件按 <see cref="EventOptionValuer"/> 的价值排序：
    ///   可 SL（选项看得见具体卡/遗物）→ 实际价值优先；随机奖励选项 → 目录综合期望；
    ///   都未建模时维持既有行为（第一个非"离开"的可行动项）。
    /// </summary>
    private static NEventOptionButton? ChooseOption(
        List<NEventOptionButton> options,
        RunState? runState,
        out string basis)
    {
        basis = "";
        Player? player = runState == null ? null : LocalContext.GetMe(runState);

        var nonKill = new List<NEventOptionButton>();
        foreach (NEventOptionButton button in options)
        {
            bool kills = player != null
                && button.Option.WillKillPlayer?.Invoke(player) == true;
            if (!kills)
                nonKill.Add(button);
        }
        if (nonKill.Count > 0)
            options = nonKill;

        // 先古遗物（Neow）：三选全是遗物选项（2 正向 + 1 诅咒）时，用遗物评分选最优正向，绝不选诅咒。
        var relicOptions = new List<NEventOptionButton>();
        foreach (NEventOptionButton button in options)
        {
            if (button.Option.Relic != null)
                relicOptions.Add(button);
        }
        if (relicOptions.Count > 0 && relicOptions.Count == options.Count)
        {
            // 渠道演示脚手架：开局 Neow 强制选指定先古遗物（如万花筒），否则按评分。
            string forceId = RunAutoSettings.ForceNeowRelicId;
            if (!string.IsNullOrWhiteSpace(forceId) && (runState?.CurrentActIndex ?? 0) == 0)
            {
                foreach (NEventOptionButton button in relicOptions)
                {
                    RelicModel relic = button.Option.Relic!;
                    if (relic.Id.Entry.Equals(forceId, StringComparison.OrdinalIgnoreCase)
                        || relic.GetType().Name.Equals(forceId, StringComparison.OrdinalIgnoreCase))
                    {
                        basis = $"强制Neow遗物:{relic.Id.Entry}";
                        return button;
                    }
                }
            }
            RelicModel best = RelicPickerAI.PickBestAncientChoice(
                relicOptions.ConvertAll(b => b.Option.Relic!), runState);
            foreach (NEventOptionButton button in relicOptions)
            {
                if (ReferenceEquals(button.Option.Relic, best))
                {
                    basis = $"遗物评分:{RelicPickerAI.ScoreAncientChoice(best, runState):0.#}";
                    return button;
                }
            }
        }

        // 其余事件：按价值排序。凡是被建模的选项（确定性实际价值或事件期望，含负值——如
        // SLIPPERY_BRIDGE 移除代价是负分）都参与比较取最大；全部未建模才退回"第一个可行动项"。
        float bestScore = float.MinValue;
        NEventOptionButton? bestChoice = null;
        string bestBasis = "";
        foreach (NEventOptionButton button in options)
        {
            if (button.Option.IsProceed)
                continue;
            EventOptionValuer.OptionScore score =
                EventOptionValuer.Score(button.Option, button.Event, player, runState);
            if (score.Basis.StartsWith("未建模", StringComparison.Ordinal))
                continue; // 没建模的不参与（不冒险重排）。
            if (score.Value > bestScore)
            {
                bestScore = score.Value;
                bestChoice = button;
                bestBasis = score.Basis;
            }
        }
        if (bestChoice != null)
        {
            basis = bestBasis;
            return bestChoice;
        }

        var actionable = new List<NEventOptionButton>();
        foreach (NEventOptionButton button in options)
        {
            if (!button.Option.IsProceed)
                actionable.Add(button);
        }
        if (actionable.Count > 0)
        {
            basis = "未建模（保持原顺序）";
            return actionable[0];
        }
        basis = "仅离开选项";
        return options.Count > 0 ? options[0] : null;
    }

    /// <summary>
    /// Ancient 事件（Neow 等）对话翻页。检测到 <see cref="NAncientEventLayout"/> 且还没有可点选项时，
    /// 点 %DialogueHitbox 翻页；hitbox 未就绪则返回 true 让主循环重试，不退出。
    /// 返回 true = 仍处于 Ancient 对话阶段（继续等/翻页）；false = 非 Ancient 或对话已结束。
    /// 与 AutoSlay 的 HandleAncientEventDialogue 配方一致。
    /// </summary>
    private static async Task<bool> TryClickAncientDialogueAsync(NEventRoom room, CancellationToken token)
    {
        NAncientEventLayout? layout = RunUiHelper.FindFirst<NAncientEventLayout>(room);
        if (layout == null)
            return false;

        // 已有可点选项：对话已翻完，交给普通选项流程。
        foreach (NEventOptionButton button in RunUiHelper.FindAll<NEventOptionButton>(layout))
        {
            if (button.IsEnabled && !button.Option.IsLocked)
                return false;
        }

        NButton? dialogue = layout.GetNodeOrNull<NButton>("%DialogueHitbox");
        if (dialogue == null || !dialogue.Visible || !dialogue.IsEnabled)
        {
            // 对话行场景还在异步加载（headless 下尤其慢，日志会先出现 ancient_dialogue_line 的
            // "Asset not cached" 警告）。这里绝不能立即 return true 空转——否则主循环 50 次
            // 迭代会在 hitbox 就绪前瞬间耗尽，HandleAsync 静默返回、_active=false，事件房永久卡死。
            // 对齐 AutoSlay 配方：等 100ms 再重试，直到 hitbox 就绪。
            await Task.Delay(100, token);
            return true;
        }

        dialogue.EmitSignal(NClickableControl.SignalName.Released, dialogue);
        await Task.Delay(400, token);
        return true;
    }

    /// <summary>事件 UI 就绪条件：可点选项 / 远古对话可翻页 / 地图已开 / 开战 / 覆盖层 / 房间消失。</summary>
    private static bool EventReadyOrGone(NEventRoom room)
    {
        if (!GodotObject.IsInstanceValid(room) || !room.IsInsideTree())
            return true;
        if (NMapScreen.Instance is { IsOpen: true })
            return true;
        if (CombatManager.Instance.IsInProgress)
            return true;
        if (NOverlayStack.Instance is { ScreenCount: > 0 })
            return true;
        foreach (NEventOptionButton button in RunUiHelper.FindAll<NEventOptionButton>(room))
        {
            if (button.IsEnabled && !button.Option.IsLocked)
                return true;
        }
        NAncientEventLayout? layout = RunUiHelper.FindFirst<NAncientEventLayout>(room);
        if (layout != null)
        {
            NButton? dialogue = layout.GetNodeOrNull<NButton>("%DialogueHitbox");
            if (dialogue != null && dialogue.Visible && dialogue.IsEnabled)
                return true;
        }
        return false;
    }

    /// <summary>打印事件房当前 UI 状态，用于定位 headless 下事件不就绪的卡点。</summary>
    private static string DescribeEventState(NEventRoom room)
    {
        int options = 0;
        int locked = 0;
        try
        {
            foreach (NEventOptionButton button in RunUiHelper.FindAll<NEventOptionButton>(room))
            {
                if (button.Option.IsLocked)
                    locked++;
                else
                    options++;
            }
        }
        catch
        {
            // 状态枚举失败不致命，保持占位。
        }
        string layoutState = "no-layout";
        try
        {
            NAncientEventLayout? layout = RunUiHelper.FindFirst<NAncientEventLayout>(room);
            if (layout != null)
            {
                NButton? dialogue = layout.GetNodeOrNull<NButton>("%DialogueHitbox");
                layoutState = dialogue == null
                    ? "layout-hitbox-missing"
                    : $"layout-hitbox visible={dialogue.Visible} enabled={dialogue.IsEnabled}";
            }
        }
        catch
        {
            // 同上。
        }
        string mapState;
        try
        {
            NMapScreen? map = NMapScreen.Instance;
            mapState = map == null ? "map=null" : map.IsOpen ? "map=open" : "map=closed(instance有)";
        }
        catch
        {
            mapState = "map=?";
        }
        string overlayState = "overlay=null";
        int proceedCount = 0;
        try
        {
            if (NOverlayStack.Instance is { } stack && stack.ScreenCount > 0)
                overlayState = $"overlay=top:{stack.Peek()?.GetType().Name} n:{stack.ScreenCount}";
            foreach (NProceedButton p in RunUiHelper.FindAll<NProceedButton>(room))
            {
                if (p.Visible && p.IsEnabled)
                    proceedCount++;
            }
        }
        catch
        {
            // 状态枚举失败不致命。
        }
        return $"options={options} locked={locked} {layoutState} {mapState} {overlayState} proceedEnabled={proceedCount} children={room.GetChildCount()}";
    }

    private static bool SetsEqual(HashSet<NEventOptionButton> a, List<NEventOptionButton> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (NEventOptionButton button in b)
        {
            if (!a.Contains(button))
                return false;
        }
        return true;
    }
}

