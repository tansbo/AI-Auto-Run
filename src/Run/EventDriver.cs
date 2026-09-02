using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
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

            for (int iteration = 0; iteration < MaxIterations; iteration++)
            {
                session = RunAutoController.Session;
                if (session == null)
                    return;
                if (!GodotObject.IsInstanceValid(room) || !room.IsInsideTree())
                    return;
                if (NMapScreen.Instance is { IsOpen: true })
                    return;

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
                    // 事件 UI 还没就绪。之前只 delay 200ms 快速空转，MaxIterations=50 约 10s 就
                    // 静默放弃，headless 下事件场景加载慢/未完成时会把跑局永久卡死在事件房。
                    // 改为有界等待（可点选项/远古对话可翻页/地图/战斗/覆盖层/房间消失任一即恢复），
                    // 超时打印完整状态定位卡点后再按原逻辑抛出。
                    try
                    {
                        await RunUiHelper.WaitUntilAsync(
                            () => EventReadyOrGone(room),
                            token,
                            TimeSpan.FromSeconds(45),
                            "事件选项未出现");
                    }
                    catch (RunAutoTimeoutException)
                    {
                        session.LogDecision($"事件 UI 超时就绪失败：{DescribeEventState(room)}");
                        throw;
                    }
                    continue;
                }

                RunState? runState = RunManager.Instance.DebugOnlyGetState();
                NEventOptionButton? choice = ChooseOption(options, runState);
                if (choice == null)
                    return;

                var before = new HashSet<NEventOptionButton>(options);
                string scoreText = choice.Option.Relic != null
                    ? $"（评分 {RelicPickerAI.ScoreAncientChoice(choice.Option.Relic, runState):0.#}）"
                    : "";
                session.LogDecision(
                    $"事件：{choice.Event?.Id.Entry ?? "unknown"} → {choice.Option.Title.GetFormattedText()}{scoreText}");
                // 先古遗物等关键选择：停顿一下让底部覆盖层显示推荐，用户能看清再点。
                if (choice.Option.Relic != null)
                    await Task.Delay(1500, token);
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

    /// <summary>
    /// 选一个选项：优先非"离开"且不会杀死玩家的选项；只剩离开或全部会致死时选第一个不会致死的。
    /// </summary>
    private static NEventOptionButton? ChooseOption(List<NEventOptionButton> options, RunState? runState)
    {
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
            RelicModel best = RelicPickerAI.PickBestAncientChoice(
                relicOptions.ConvertAll(b => b.Option.Relic!), runState);
            foreach (NEventOptionButton button in relicOptions)
            {
                if (ReferenceEquals(button.Option.Relic, best))
                    return button;
            }
        }

        var actionable = new List<NEventOptionButton>();
        foreach (NEventOptionButton button in options)
        {
            if (!button.Option.IsProceed)
                actionable.Add(button);
        }
        return actionable.Count > 0 ? actionable[0] : options[0];
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
        return $"options={options} locked={locked} {layoutState} children={room.GetChildCount()}";
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
