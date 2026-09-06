using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using STS2RitsuLib;
using STS2RitsuLib.Interop;

namespace CombatSolver.Run;

/// <summary>
/// 全自动跑局编排器。战斗内交给 Combat Solver 全自动，战斗间由本控制器通过
/// RitsuLib 跑局事件驱动各房间/屏幕驱动。仅在 <see cref="RunAutoSettings.Enabled"/>
/// 时工作；所有事件处理都在主线程执行，驱动任务经 TaskHelper.RunSafely 启动。
///
/// 阶段机（RunAutoPhase）：
///   进入战斗房 → InCombat（Combat Solver 接管）
///   战斗结束/胜利 → RewardsPending（奖励结算）
///   奖励结算完毕/离开房间 → MapPending（地图选路）
///   非战斗房 → NonCombatRoom（对应驱动）
/// 由 RunEndedEvent 清理。
/// </summary>
internal static class RunAutoController
{
    private static readonly object Sync = new();
    private static RunAutoSession? _session;
    private static FastModeType? _originalFastMode;
    private static ulong _lastStuckTopId;   // 看门狗：上次卡住覆盖层实例 id
    private static int _stuckTicks;         // 看门狗：同一覆盖层卡住 tick 数（约 2s/tick）

    /// <summary>当前跑局会话；没有活动跑局时为 null。</summary>
    public static RunAutoSession? Session
    {
        get
        {
            lock (Sync)
                return _session;
        }
    }

    public static void Subscribe()
    {
        RitsuLibFramework.SubscribeLifecycle<RunStartedEvent>(OnRunStarted);
        RitsuLibFramework.SubscribeLifecycle<RunEndedEvent>(OnRunEnded);
        RitsuLibFramework.SubscribeLifecycle<RoomEnteredEvent>(OnRoomEntered);
        RitsuLibFramework.SubscribeLifecycle<RoomExitedEvent>(OnRoomExited);
        RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(OnCombatStarting);
        RitsuLibFramework.SubscribeLifecycle<CombatVictoryEvent>(OnCombatVictory);
        RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(OnCombatEnded);
        RitsuLibFramework.SubscribeLifecycle<RewardsScreenContinuingEvent>(OnRewardsScreenContinuing);
        RitsuLibFramework.SubscribeLifecycle<RewardTakenEvent>(OnRewardTaken);
        RitsuLibFramework.SubscribeLifecycle<MapGeneratedEvent>(OnMapGenerated);
    }

    private static void OnRunStarted(RunStartedEvent evt)
    {
        if (!RunAutoSettings.Enabled || evt.IsMultiplayer)
            return;
        lock (Sync)
        {
            _session = new RunAutoSession { RunState = evt.RunState, Phase = RunAutoPhase.Idle };
        }
        InitializeTelemetryHeader(_session, evt.RunState);
        if (RunAutoSettings.FastMode && SaveManager.Instance?.PrefsSave != null)
        {
            _originalFastMode ??= SaveManager.Instance.PrefsSave.FastMode;
            SaveManager.Instance.PrefsSave.FastMode = FastModeType.Fast;
        }
        RunAutoOverlay.Update(_session);
        Entry.Logger.Info(
            $"[RunAuto] RUN_STARTED act={evt.RunState?.CurrentActIndex + 1 ?? 0} " +
            $"floor={evt.RunState?.TotalFloor ?? 0} fast_mode={RunAutoSettings.FastMode}");
        _ = TaskHelper.RunSafely(RoomDriverWatchdogAsync(_session));
    }

    /// <summary>
    /// 房间驱动看门狗（176 实证 BATTLEWORN 等事件战后 EventDriver 静默退出、地图永不开）：
    /// 事件房仍在场景树、地图未开、无覆盖层/战斗、事件驱动不在跑 → 重启事件驱动。
    /// 事件驱动 OnRoomEntered 有 _active 去重 + 顶层"地图已开即退出"，重启安全。
    /// </summary>
    private static async Task RoomDriverWatchdogAsync(RunAutoSession session)
    {
        CancellationToken token = session.CancellationToken;
        while (true)
        {
            try
            {
                await Task.Delay(2000, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                if (!RunAutoSettings.Enabled || Session != session)
                    return;
                if (session.Phase == RunAutoPhase.InCombat)
                    continue;
                if (NMapScreen.Instance is { IsOpen: true })
                    continue;
                if (CombatManager.Instance.IsInProgress)
                    continue;
                // 事件奖励屏残留收尾升级（EventDriver/worker 可能互锁停住）：
                // 同一残留屏(栈顶) + 地图未开持续 ≥6 tick(12s) → 点 Proceed；≥12 tick(24s) → 显式移除覆盖层。
                if (NOverlayStack.Instance?.Peek() is MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen rewardsLeft
                    && rewardsLeft.IsVisibleInTree())
                {
                    ulong inst = rewardsLeft.GetInstanceId();
                    if (_lastStuckTopId != inst)
                    {
                        _lastStuckTopId = inst;
                        _stuckTicks = 0;
                    }
                    // 奖励 worker 活跃且近 20s 内有实际推进 → 健康处理中（战后奖励结算同样经过这里，
                    // 常见一屏多个奖励/卡牌子屏可合法 >12s）：不累计、不介入。worker 的等待都有界
                    // （腾栏/子屏 ≤10s），真死锁会在其超时退出后由本看门狗接管，无需抢跑。
                    if (RewardsScreenDriver.IsWorkerActive
                        && System.Environment.TickCount64 - RewardsScreenDriver.LastProgressTick < 20_000)
                    {
                        _stuckTicks = 0;
                        continue;
                    }
                    _stuckTicks++;
                    var p = rewardsLeft.GetNodeOrNull<NProceedButton>("%ProceedButton");
                    if (_stuckTicks >= 6 && _stuckTicks < 12 && p != null && p.IsEnabled)
                    {
                        session.LogDecision($"事件看门狗：奖励屏残留 {_stuckTicks}tick，点 Proceed 收尾");
                        await RunUiHelper.ClickAsync(p, 150);
                    }
                    else if (_stuckTicks >= 12)
                    {
                        if (_stuckTicks % 3 == 0 && p != null && p.IsEnabled)
                        {
                            session.LogDecision($"事件看门狗：奖励屏残留 {_stuckTicks}tick 仍卡，再点 Proceed");
                            await RunUiHelper.ClickAsync(p, 150);
                        }
                        if (_stuckTicks >= 21)
                        {
                            session.LogDecision("事件看门狗：奖励屏 21tick 仍未关，显式移除覆盖层");
                            NOverlayStack.Instance?.Remove(rewardsLeft);
                            _lastStuckTopId = 0;
                            _stuckTicks = 0;
                        }
                    }
                    continue;
                }
                _lastStuckTopId = 0;
                _stuckTicks = 0;
                // 事件完成页残留（THE_FUTURE DONE 等）：无覆盖层且事件房在但驱动处理不掉 → 点可用按钮离开。
                // 优先房间级 NProceedButton（完成页离开钮）；没有才退化到任意可用 NButton——注意
                // NEventOptionButton 也是 NButton，盲点第一个会绕过 ChooseOption 的杀玩家排除/价值评分。
                if (NOverlayStack.Instance is not { ScreenCount: > 0 }
                    && !EventDriver.IsActive
                    && RunUiHelper.FindFirst<NEventRoom>(((SceneTree)Godot.Engine.GetMainLoop()).Root) is { } eventRoomLeft)
                {
                    _stuckTicks++;
                    if (_stuckTicks >= 6 && _stuckTicks % 2 == 0)
                    {
                        NButton? leaveButton = null;
                        foreach (NProceedButton pb in RunUiHelper.FindAll<NProceedButton>(eventRoomLeft))
                        {
                            if (pb.Visible && pb.IsEnabled)
                            {
                                leaveButton = pb;
                                break;
                            }
                        }
                        if (leaveButton == null)
                        {
                            foreach (NButton b in RunUiHelper.FindAll<NButton>(eventRoomLeft))
                            {
                                if (b.Visible && b.IsEnabled)
                                {
                                    leaveButton = b;
                                    break;
                                }
                            }
                        }
                        if (leaveButton != null)
                        {
                            session.LogDecision($"事件看门狗：事件完成页残留 {_stuckTicks}tick，点 {leaveButton.GetType().Name} 离开");
                            await RunUiHelper.ClickAsync(leaveButton, 150);
                        }
                    }
                    continue;
                }
                _stuckTicks = 0;
                if (NOverlayStack.Instance is { ScreenCount: > 0 })
                    continue;
                if (EventDriver.IsActive)
                    continue;
                Node root = ((SceneTree)Godot.Engine.GetMainLoop()).Root;
                if (RunUiHelper.FindFirst<NEventRoom>(root) == null)
                    continue;
                session.LogDecision("事件看门狗：事件房在但事件驱动不在，重启事件驱动");
                EventDriver.OnRoomEntered();
            }
            catch (Exception ex)
            {
                session.LogDecision($"事件看门狗异常：{ex.GetType().Name}");
            }
        }
    }

    private static void OnRunEnded(RunEndedEvent evt)
    {
        RunAutoSession? ended = Session;
        lock (Sync)
            _session = null;
        if (ended == null)
            return;
        ended.Cancel();
        RunAutoOverlay.Hide();
        if (_originalFastMode is { } previous && SaveManager.Instance?.PrefsSave != null)
        {
            SaveManager.Instance.PrefsSave.FastMode = previous;
            _originalFastMode = null;
        }
        WriteTelemetry(ended, evt);
        Entry.Logger.Info(
            $"[RunAuto] RUN_ENDED victory={evt.IsVictory} abandoned={evt.IsAbandoned} " +
            $"rooms_handled={ended.RoomsHandled} cards_picked={ended.PickedCardIds.Count}");
        // 整局无人测试收尾：跑局结束后异步等待的延续被证实冻结，只能在这里同步写结果并退出。
        if (UnattendedTestRunner.IsActive)
            UnattendedTestRunner.NotifyFullRunEnded(ended, evt);
    }

    private static void InitializeTelemetryHeader(RunAutoSession session, RunState? runState)
    {
        Player? player = runState == null ? null : LocalContext.GetMe(runState);
        session.Telemetry.Seed = runState?.Rng.StringSeed ?? string.Empty;
        session.Telemetry.CharacterId = player?.Character?.Id.Entry ?? string.Empty;
        session.Telemetry.Ascension = runState?.AscensionLevel ?? 0;
        session.Telemetry.ForcedPicks = RunAutoSettings.ForcedPicks;
    }

    private static void WriteTelemetry(RunAutoSession ended, RunEndedEvent evt)
    {
        RunTelemetryData telemetry = ended.Telemetry;
        telemetry.Victory = evt.IsVictory;
        telemetry.Abandoned = evt.IsAbandoned;
        telemetry.Floors = ended.RunState?.TotalFloor ?? 0;
        telemetry.ActReached = (ended.RunState?.CurrentActIndex ?? 0) + 1;
        telemetry.RoomsHandled = ended.RoomsHandled;
        if (!RunAutoSettings.TelemetryEnabled)
            return;
        string path;
        try
        {
            path = RunTelemetry.Write(ended);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[RunAuto] 遥测写入失败：{ex.Message}");
            return;
        }
        Entry.Logger.Info(
            $"[RunAuto] TELEMETRY_WRITTEN path={path} seed={telemetry.Seed} " +
            $"victory={telemetry.Victory} floors={telemetry.Floors} picks={telemetry.Picks.Count} " +
            $"relic_picks={telemetry.RelicPicks.Count}");

        // 自动上传（opt-in）：开启并填 URL 后异步 POST，不阻塞跑局收尾。
        string url = RunAutoSettings.TelemetryUploadUrl;
        if (RunAutoSettings.TelemetryUploadEnabled && !string.IsNullOrWhiteSpace(url))
        {
            TaskHelper.RunSafely(RunTelemetry.UploadAsync(path, url));
        }
    }

    private static void OnRoomEntered(RoomEnteredEvent evt)
    {
        RunAutoSession? session = Session;
        if (session == null)
            return;
        AbstractRoom room = evt.Room;
        session.CurrentRoomType = room.RoomType;
        session.RoomsHandled++;
        session.RunState = evt.RunState as RunState ?? session.RunState;
        switch (room.RoomType)
        {
            case RoomType.Monster:
            case RoomType.Elite:
            case RoomType.Boss:
                session.Phase = RunAutoPhase.InCombat;
                session.CombatVictorySeen = false;
                session.LogDecision($"进入战斗（{room.RoomType}），由战斗求解器全自动接管");
                break;
            case RoomType.RestSite:
                session.Phase = RunAutoPhase.NonCombatRoom;
                session.LogDecision("进入篝火，启动房间驱动");
                RestSiteDriver.OnRoomEntered();
                break;
            case RoomType.Shop:
                session.Phase = RunAutoPhase.NonCombatRoom;
                session.LogDecision("进入商店，启动房间驱动");
                ShopDriver.OnRoomEntered();
                break;
            case RoomType.Event:
                session.Phase = RunAutoPhase.NonCombatRoom;
                session.LogDecision("进入事件，启动房间驱动");
                EventDriver.OnRoomEntered();
                break;
            case RoomType.Treasure:
                session.Phase = RunAutoPhase.NonCombatRoom;
                session.LogDecision("进入宝箱房，启动房间驱动");
                RelicRewardDriver.OnTreasureRoomEntered();
                break;
            default:
                session.LogDecision($"进入房间 {room.RoomType}");
                break;
        }
    }

    private static void OnMapGenerated(MapGeneratedEvent evt)
    {
        RunAutoSession? session = Session;
        if (session == null)
            return;
        session.RunState = evt.RunState as RunState ?? session.RunState;
        // 选路不在这里触发：地图生成早于地图屏幕打开（如开局先 Neow 事件），
        // 此时请求选路会白等 30s 超时。统一由 NMapScreenPatch 在 NMapScreen.Open 时触发。
        session.LogDecision($"地图生成（第 {evt.ActIndex + 1} 幕）");
    }

    private static void OnRoomExited(RoomExitedEvent evt)
    {
        RunAutoSession? session = Session;
        if (session == null)
            return;
        // 离开房间后回到地图，准备选择下一房间。
        session.Phase = RunAutoPhase.MapPending;
        session.LogDecision("离开房间，地图选路");
        MapRouter.RequestRoute();
    }

    private static void OnCombatStarting(CombatStartingEvent evt)
    {
        RunAutoSession? session = Session;
        if (session == null)
            return;
        session.Phase = RunAutoPhase.InCombat;
        if (evt.CombatState is CombatState combatState)
        {
            // 精英追踪：记录本幕精英袋序（袋内不重复 → 见过 2 种可预测第 3 场）。
            EliteTracker.RecordCombatStart(session, combatState);
            session.LogDecision("进入战斗，等待战斗求解器全自动接管");
            // 战斗开始瞬间玩家回合尚未进入 Play，SetFullAuto 会被 CanSolve 拒绝；
            // 轮询到玩家可出牌后重试开启，headless 整局与 visible 全自动跑局都依赖它。
            TaskHelper.RunSafely(EnableFullAutoWhenPlayableAsync(combatState));
        }
    }

    /// <summary>轮询到玩家回合可出牌（Phase=Play）或存在待接管的回合开始选牌后开启战斗求解器全自动。
    /// 首回合带回合开始选牌（如战略类卡）时，原生选牌页先于 Play 出现并等待计划执行——
    /// 只等 Play 会死锁（选牌不解决 Play 永不出现），有 pending 计划选择时也立即接管。</summary>
    private static async Task EnableFullAutoWhenPlayableAsync(CombatState combatState)
    {
        for (int attempt = 0; attempt < 2400; attempt++)
        {
            await Task.Delay(50);
            if (!Entry.Enabled || SolverController.SolverDisabled)
                return;
            if (SolverController.FullAutoEnabled)
                return;
            if (!CombatManager.Instance.IsInProgress
                || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), combatState))
            {
                return;
            }
            if (combatState.CurrentSide != CombatSide.Player)
                continue;
            Player? me = LocalContext.GetMe(combatState);
            if (me?.PlayerCombatState?.Phase != PlayerTurnPhase.Play
                && !PlayerTurnSetupCoordinator.HasPendingPlannedChoice(combatState))
            {
                continue;
            }
            if (NGame.Instance is not { } host)
                return;
            SolverController.SetFullAuto(host, combatState, enabled: true);
            Entry.Logger.Info("[RunAuto] 战斗求解器已接管全自动（玩家回合可出牌/回合开始选牌就绪后开启）");
            return;
        }
    }

    private static void OnCombatVictory(CombatVictoryEvent evt)
    {
        RunAutoSession? session = Session;
        if (session == null)
            return;
        session.CombatVictorySeen = true;
        session.Phase = RunAutoPhase.RewardsPending;
        session.LogDecision("战斗胜利，等待奖励结算");
        RewardsScreenDriver.OnCombatVictory();
    }

    private static void OnCombatEnded(CombatEndedEvent evt)
    {
        RunAutoSession? session = Session;
        if (session == null)
            return;
        // 无论胜负都回到奖励/结算阶段；败北会走游戏结算界面。
        // 胜利时的奖励排空已由 OnCombatVictory 启动 RewardsScreenDriver
        // （NRewardsScreen → 卡牌奖励 → 遗物选择），这里只做阶段推进。
        session.Phase = RunAutoPhase.RewardsPending;
        session.LogDecision("战斗结束，奖励/结算处理");
    }

    private static void OnRewardsScreenContinuing(RewardsScreenContinuingEvent evt)
    {
        RunAutoSession? session = Session;
        if (session == null)
            return;
        session.Phase = RunAutoPhase.MapPending;
        session.LogDecision("奖励结算完毕，地图选路");
        MapRouter.RequestRoute();
    }

    private static void OnRewardTaken(RewardTakenEvent evt)
    {
        RunAutoSession? session = Session;
        if (session == null)
            return;
        if (evt.Reward is MegaCrit.Sts2.Core.Rewards.CardReward cardReward)
        {
            int before = session.PickedCardIds.Count;
            foreach (MegaCrit.Sts2.Core.Models.CardModel card in cardReward.Cards)
                session.PickedCardIds.Add(card.Id.ToString());
            if (session.PickedCardIds.Count > before)
                session.LogDecision($"获得卡牌 {session.PickedCardIds[^1]}");
        }
        else if (evt.Reward is MegaCrit.Sts2.Core.Rewards.RelicReward relicReward
                 && relicReward.Relic != null)
        {
            session.Telemetry.RecordRelicObtained(relicReward.Relic.Id.Entry);
        }
    }
}
