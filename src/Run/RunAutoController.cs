using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
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
