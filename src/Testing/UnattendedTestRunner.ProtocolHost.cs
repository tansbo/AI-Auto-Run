using System.Diagnostics;
using System.Text.Json;
using Godot;
using CombatSolver.Run;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed class ProtocolHost
    {
        private bool _requestLoopStarted;
        private NGame? _host;
        private UnattendedTestRequest? _activeRequest;
        private DateTimeOffset _requestStartedAtUtc;
        // 整局结果定稿标记：0 未定稿；1 自然收尾（RunEnded 写 Passed+胜负）；3 存活监控判 Stuck。
        // Notify 与 Stuck 监控用 CompareExchange 竞争，谁先抢到谁定稿，避免互盖。
        private int _fullRunFinalized;
        private CancellationTokenSource? _livenessCts;
        private int _acceptedRequestCount;
        private int _injectPlayerHpLossTurn;
        private int _injectPlayerHpLossAmount;
        private int _injectedPlayerHpLoss;
        private int _clearPlayerBlockBeforeEndTurn;
        private int _clearedPlayerBlock;

        public bool IsActive { get; private set; }
        public bool AutomaticTurnSearchEnabled { get; private set; } = true;
        public bool VerifyIncrementalSearch { get; private set; }
        public bool ForceShortSearchOnly { get; private set; }
        public bool MeasureSearchPhases { get; private set; }
        public int? SearchMaxDegreeOfParallelismOverride { get; private set; }
        public int? ShortSearchBudgetOverrideMilliseconds { get; private set; }
        public int? DeepSearchBudgetOverrideMilliseconds { get; private set; }

        public void TryStart(NGame? host)
        {
            if (_requestLoopStarted || host == null)
                return;

            _requestLoopStarted = true;
            _host = host;
            TaskHelper.RunSafely(RunRequestLoopAsync(host));
            Entry.Logger.Info("[CombatSolver/Unattended] REQUEST_LOOP_STARTED reuse_process=true");
        }

        public void EnableAutomaticTurnSearch()
            => AutomaticTurnSearchEnabled = true;

        /// <summary>
        /// 整局模式收尾。RunEndedEvent 在主线程同步派发（实证可运行），这里直接写结果 JSON 并按需退出——
        /// 不能依赖 RunFullRunAsync 里的异步等待：跑局结束后该等待的延续被证实冻结、永不恢复，
        /// 导致结果永远不落盘、游戏永不退出。只对活动中的 FullRun 请求生效，一次跑局只收尾一次。
        /// </summary>
        public void NotifyFullRunEnded(CombatSolver.Run.RunAutoSession ended, STS2RitsuLib.RunEndedEvent evt)
        {
            if (!IsActive
                || _activeRequest is not { RunAutoFullRun: true } request
                || Interlocked.Exchange(ref _fullRunFinalized, 1) != 0)
            {
                return;
            }

            double elapsedMilliseconds = (DateTimeOffset.UtcNow - _requestStartedAtUtc).TotalMilliseconds;
            RuntimeMemorySnapshot memory = CaptureRuntimeMemory();
            WriteResultFile(new UnattendedTestResult
            {
                RunId = request.RunId,
                ScenarioId = request.ScenarioId,
                Status = "Passed",
                Stage = "full_run_driving",
                CharacterId = request.CharacterId,
                EncounterId = "-",
                Seed = request.Seed,
                StartedAtUtc = _requestStartedAtUtc,
                ElapsedMilliseconds = elapsedMilliseconds,
                MainThread = NGame.IsMainThread(),
                CombatEnded = true,
                StartedTurn = 0,
                FinishedTurn = 0,
                ManagedHeapBytes = memory.ManagedHeapBytes,
                ManagedFragmentedBytes = memory.ManagedFragmentedBytes,
                WorkingSetBytes = memory.WorkingSetBytes,
                PrivateMemoryBytes = memory.PrivateMemoryBytes,
                Victory = evt.IsVictory,
                Abandoned = evt.IsAbandoned,
                RoomsHandled = ended.RoomsHandled,
                ActReached = ended.RunState is { } runState ? runState.CurrentActIndex + 1 : 0,
            });

            Entry.Logger.Info(
                $"[CombatSolver/Unattended] FULL_RUN_ENDED run_id={request.RunId} " +
                $"victory={evt.IsVictory} abandoned={evt.IsAbandoned} rooms_handled={ended.RoomsHandled} " +
                $"elapsed_ms={elapsedMilliseconds:F1} result=Passed");
            if (request.ExitOnComplete)
            {
                UnattendedAsyncActivityTracker.AbortRequest();
                _host?.GetTree().Quit(0);
            }
        }

        /// <summary>整局是否已定稿（自然收尾或 Stuck）。RunFullRunAsync 用它避免覆盖带胜负的结果。</summary>
        public bool FullRunFinalized => Volatile.Read(ref _fullRunFinalized) != 0;

        private static void WriteResultFile(UnattendedTestResult result)
        {
            string resultPath = UnattendedTestFiles.GlobalPath(UnattendedTestFiles.ResultUri);
            string tempPath = resultPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(result, UnattendedTestFiles.JsonOptions));
            File.Move(tempPath, resultPath, true);
        }

        /// <summary>
        /// 整局存活监控（后台线程，主线程卡死也能判）：主线程帧停摆 ≥frameStallMs，或
        /// 会话已见但进度签名（房间/阶段/回合/敌我回合）≥noProgressMs 无变化 → 判 Stuck：
        /// 写 Status=Stuck 结果 JSON（含诊断快照）并从后台强制退出（exit 2）。
        /// 取代"任意固定时长陪跑"——健康慢跑跑多远跟多远，只有真停摆/卡死才终止并续下一局。
        /// 进度签名只读跨线程安全字段（后台探针早已同款读），不触碰会崩的节点引用。
        /// </summary>
        private void StartFullRunLivenessMonitor(UnattendedTestRequest request)
        {
            if (_livenessCts != null)
                return;
            var cts = new CancellationTokenSource();
            _livenessCts = cts;
            int frameStallMs = Math.Max(5_000, (int)(request.FullRunFrameStallSeconds * 1000));
            int noProgressMs = Math.Max(10_000, (int)(request.FullRunNoProgressSeconds * 1000));
            _ = Task.Run(() => RunFullRunLivenessLoopAsync(cts.Token, frameStallMs, noProgressMs));
            Entry.Logger.Info(
                $"[CombatSolver/Unattended] FULL_RUN_LIVENESS_STARTED frame_stall_ms={frameStallMs} no_progress_ms={noProgressMs}");
        }

        private async Task RunFullRunLivenessLoopAsync(CancellationToken token, int frameStallMs, int noProgressMs)
        {
            ulong lastFrame = 0;
            long frameStallSinceMs = -1;
            long lastSignatureChangeMs = System.Environment.TickCount64;
            string lastSignature = "";
            bool seenSession = false;
            while (true)
            {
                try
                {
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (token.IsCancellationRequested)
                    return;
                try
                {
                    // 无活动整局请求或结果已定稿（自然收尾/他处已 Stuck）→ 观望，不判。
                    if (!IsActive
                        || _activeRequest is not { RunAutoFullRun: true }
                        || Volatile.Read(ref _fullRunFinalized) != 0)
                    {
                        seenSession = false;
                        lastFrame = 0;
                        frameStallSinceMs = -1;
                        lastSignatureChangeMs = System.Environment.TickCount64;
                        continue;
                    }

                    long now = System.Environment.TickCount64;
                    ulong frames;
                    try
                    {
                        frames = Godot.Engine.GetProcessFrames();
                    }
                    catch
                    {
                        continue;
                    }
                    if (frames != lastFrame)
                    {
                        lastFrame = frames;
                        frameStallSinceMs = -1;
                    }
                    else if (frameStallSinceMs < 0)
                    {
                        frameStallSinceMs = now;
                    }
                    if (frameStallSinceMs >= 0 && now - frameStallSinceMs >= frameStallMs)
                    {
                        DeclareStuck($"主线程停摆：帧 {lastFrame} 连续 {frameStallMs / 1000}s 未推进");
                        return;
                    }

                    RunAutoSession? session = RunAutoController.Session;
                    if (session != null)
                        seenSession = true;
                    if (session == null || !seenSession)
                        continue; // 跑局未开始（或刚结束）：只盯帧停摆，不数无推进。
                    string signature = BuildLivenessSignature(session);
                    if (signature != lastSignature)
                    {
                        lastSignature = signature;
                        lastSignatureChangeMs = now;
                        continue;
                    }
                    if (now - lastSignatureChangeMs >= noProgressMs)
                    {
                        DeclareStuck($"跑局无推进：进度签名 [{signature}] 连续 {noProgressMs / 1000}s 未变化");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Entry.Logger.Warn(
                        $"[CombatSolver/Unattended] FULL_RUN_LIVENESS monitor error: {ex.GetType().Name} {ex.Message}");
                }
            }
        }

        /// <summary>抢 Stuck 定稿（只抢未定稿）；抢到后从后台线程写结果并强制退出。</summary>
        private void DeclareStuck(string reason)
        {
            if (Interlocked.CompareExchange(ref _fullRunFinalized, 3, 0) != 0)
                return;
            try
            {
                WriteFullRunStuckResult(reason);
            }
            catch (Exception ex)
            {
                Entry.Logger.Error($"[CombatSolver/Unattended] FULL_RUN_STUCK 结果写入失败：{ex}");
            }
            Entry.Logger.Error($"[CombatSolver/Unattended] FULL_RUN_STUCK run_id={_activeRequest?.RunId} reason={reason}");
            // 主线程可能已冻结（帧停摆），GetTree().Quit 未必被处理；后台线程直接终止进程。
            System.Environment.Exit(2);
        }

        private void WriteFullRunStuckResult(string reason)
        {
            UnattendedTestRequest? request = _activeRequest;
            if (request == null)
                return;
            double elapsedMilliseconds = (DateTimeOffset.UtcNow - _requestStartedAtUtc).TotalMilliseconds;
            RuntimeMemorySnapshot memory = CaptureRuntimeMemory();
            WriteResultFile(new UnattendedTestResult
            {
                RunId = request.RunId,
                ScenarioId = request.ScenarioId,
                Status = "Stuck",
                Stage = "full_run_liveness",
                CharacterId = request.CharacterId,
                EncounterId = "-",
                Seed = request.Seed,
                StartedAtUtc = _requestStartedAtUtc,
                ElapsedMilliseconds = elapsedMilliseconds,
                MainThread = NGame.IsMainThread(),
                CombatEnded = false,
                StartedTurn = 0,
                FinishedTurn = 0,
                ManagedHeapBytes = memory.ManagedHeapBytes,
                ManagedFragmentedBytes = memory.ManagedFragmentedBytes,
                WorkingSetBytes = memory.WorkingSetBytes,
                PrivateMemoryBytes = memory.PrivateMemoryBytes,
                StuckDetail = $"{reason}\n{BuildLivenessSnapshot()}",
            });
        }

        /// <summary>卡死诊断快照：会话/战斗/帧的当前文本，写到 Stuck 结果的 StuckDetail。</summary>
        private string BuildLivenessSnapshot()
        {
            var sb = new System.Text.StringBuilder();
            RunAutoSession? session = RunAutoController.Session;
            sb.Append("session=");
            if (session == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append($"phase={session.Phase} rooms={session.RoomsHandled} room={session.CurrentRoomType}");
                if (session.RunState is { } rs)
                    sb.Append($" floor={rs.TotalFloor} act={rs.CurrentActIndex + 1}");
            }
            sb.Append('\n').Append("combat=");
            try
            {
                if (CombatManager.Instance is { IsInProgress: true })
                {
                    CombatState? state = CombatManager.Instance.DebugOnlyGetState();
                    if (state != null)
                    {
                        Player? me = LocalContext.GetMe(state);
                        sb.Append(
                            $"round={state.RoundNumber} side={state.CurrentSide} " +
                            $"phase={me?.PlayerCombatState?.Phase} turn={me?.PlayerCombatState?.TurnNumber} " +
                            $"full_auto={SolverController.FullAutoEnabled}");
                    }
                    else
                    {
                        sb.Append("state-null");
                    }
                }
                else
                {
                    sb.Append("no-combat");
                }
            }
            catch (Exception ex)
            {
                sb.Append($"err={ex.GetType().Name}");
            }
            try
            {
                sb.Append($" frames={Godot.Engine.GetProcessFrames()}");
            }
            catch
            {
                // 帧读取失败省略。
            }
            return sb.ToString();
        }

        /// <summary>进度签名：只在健康推进时变化的计数器组合（房间/阶段/回合/敌我回合/搜索态）。
        /// 事件/奖励/篝火等有界等待 ≤30s，任何停滞超过 noProgressMs 都是真卡死候选。</summary>
        private static string BuildLivenessSignature(RunAutoSession session)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(session.RoomsHandled).Append('|').Append((int)session.Phase).Append('|')
                .Append(session.CurrentRoomType).Append('|');
            if (session.RunState is { } rs)
                sb.Append(rs.CurrentActIndex).Append('/').Append(rs.TotalFloor);
            sb.Append('|');
            try
            {
                if (CombatManager.Instance is { IsInProgress: true })
                {
                    CombatState? state = CombatManager.Instance.DebugOnlyGetState();
                    if (state != null)
                    {
                        Player? me = LocalContext.GetMe(state);
                        sb.Append("c").Append(state.RoundNumber)
                            .Append('|').Append((int)state.CurrentSide)
                            .Append('|').Append(me?.PlayerCombatState?.TurnNumber ?? -1);
                        bool searching = false;
                        bool deploying = false;
                        try
                        {
                            searching = SolverController.IsSearching;
                            deploying = SolverController.IsDeploying;
                        }
                        catch
                        {
                            // 搜索态读取失败按 false。
                        }
                        sb.Append("|srch").Append(searching).Append("|dep").Append(deploying);
                    }
                    else
                    {
                        sb.Append("state-null");
                    }
                }
                else
                {
                    sb.Append("no-combat");
                }
            }
            catch
            {
                sb.Append("combat-err");
            }
            return sb.ToString();
        }

        private static RuntimeMemorySnapshot CaptureRuntimeMemory()
        {
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            using Process process = Process.GetCurrentProcess();
            return new RuntimeMemorySnapshot(
                gc.HeapSizeBytes,
                gc.FragmentedBytes,
                process.WorkingSet64,
                process.PrivateMemorySize64);
        }

        public async Task ApplyScheduledStateDriftAsync(CombatState state, int turn)
        {
            if (!IsActive
                || turn != _injectPlayerHpLossTurn
                || _injectPlayerHpLossAmount <= 0
                || Interlocked.Exchange(ref _injectedPlayerHpLoss, 1) != 0)
            {
                return;
            }

            Player player = LocalContext.GetMe(state)
                ?? throw new InvalidOperationException("状态漂移测试找不到本地玩家。");
            int before = player.Creature.CurrentHp;
            int after = Math.Max(1, before - _injectPlayerHpLossAmount);
            await CreatureCmd.SetCurrentHp(player.Creature, after);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            Entry.Logger.Info(
                $"[CombatSolver/Unattended] INJECT_STATE_DRIFT turn={turn} field=hp before={before} after={after}");
        }

        public async Task ApplyScheduledPreEndTurnDriftAsync(CombatState state, int turn)
        {
            if (!IsActive
                || turn != _clearPlayerBlockBeforeEndTurn
                || Interlocked.Exchange(ref _clearedPlayerBlock, 1) != 0)
            {
                return;
            }

            Player player = LocalContext.GetMe(state)
                ?? throw new InvalidOperationException("结束回合漂移测试找不到本地玩家。");
            int before = player.Creature.Block;
            await SetBlockAsync(player.Creature, 0);
            Entry.Logger.Info(
                $"[CombatSolver/Unattended] INJECT_PRE_END_TURN_DRIFT turn={turn} field=block before={before} after=0");
        }

        private async Task RunRequestLoopAsync(NGame host)
        {
            string runningPath = UnattendedTestFiles.GlobalPath(UnattendedTestFiles.RunningUri);
            string requestPath = UnattendedTestFiles.GlobalPath(UnattendedTestFiles.RequestUri);
            try
            {
                while (true)
                {
                    if (!File.Exists(requestPath))
                    {
                        for (int frame = 0; frame < 10; frame++)
                            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                        continue;
                    }

                    string json = File.ReadAllText(requestPath);
                    UnattendedTestRequest request = JsonSerializer.Deserialize<UnattendedTestRequest>(
                        json,
                        UnattendedTestFiles.JsonOptions)
                        ?? throw new InvalidOperationException("无人测试请求为空。");
                    if (request.SchemaVersion != 1)
                        throw new InvalidOperationException($"不支持的无人测试协议版本 {request.SchemaVersion}。");
                    if (request.HoldAfterInitialSearch && request.ExitOnComplete)
                    {
                        throw new InvalidOperationException(
                            "无人测试请求不能同时暂停初始搜索并在完成后退出。");
                    }

                    File.Move(requestPath, runningPath, true);
                    Activate(request);
                    int requestSequence = ++_acceptedRequestCount;
                    Entry.Logger.Info(
                        $"[CombatSolver/Unattended] REQUEST_ACCEPTED run_id={request.RunId} " +
                        $"scenario={request.ScenarioId} process_sequence={requestSequence} reused_process={requestSequence > 1}");
                    RunCompletion completion;
                    try
                    {
                        completion = await new UnattendedTestRunner(host, request, this).RunAsync();
                    }
                    finally
                    {
                        Reset();
                    }
                    if (completion == RunCompletion.Failed)
                    {
                        UnattendedAsyncActivityTracker.AbortRequest();
                        Entry.Logger.Warn(
                            "[CombatSolver/Unattended] PROCESS_NOT_REUSABLE reason=failed_request exit=true");
                        host.GetTree().Quit(1);
                        return;
                    }
                    if (completion == RunCompletion.InitialSearchHeld)
                    {
                        if (!request.HoldAfterInitialSearch || request.ExitOnComplete)
                        {
                            throw new InvalidOperationException(
                                "执行器暂停了初始搜索，但请求没有声明合法的暂停生命周期。");
                        }
                        // The launcher intentionally keeps this live combat attached to a profiler
                        // until its release marker is written, then terminates the owned process.
                        await WaitUntilHeldAsync(host);
                        WriteReady(request.RunId, held: true);
                        return;
                    }
                    if (completion != RunCompletion.Passed)
                        throw new InvalidOperationException($"未知的无人测试完成状态 {completion}。");
                    if (request.HoldAfterInitialSearch)
                    {
                        throw new InvalidOperationException(
                            "请求暂停初始搜索，但执行器已在未暂停搜索的情况下完成。该进程不可复用。");
                    }
                    if (request.ExitOnComplete)
                    {
                        UnattendedAsyncActivityTracker.AbortRequest();
                        return;
                    }
                    await WaitUntilReusableAsync(host);
                    WriteReady(request.RunId, held: false);
                }
            }
            catch (Exception ex)
            {
                Reset();
                UnattendedAsyncActivityTracker.AbortRequest();
                Entry.Logger.Error(
                    $"[CombatSolver/Unattended] PROCESS_NOT_REUSABLE exit=true exception={ex}");
                host.GetTree().Quit(1);
            }
        }

        private static async Task WaitUntilReusableAsync(NGame host)
        {
            const int quiescenceTimeoutMilliseconds = 90_000;
            long deadline = System.Environment.TickCount64 + quiescenceTimeoutMilliseconds;
            int consecutiveIdleFrames = 0;
            bool reclaimedAfterQuiescence = false;
            while (System.Environment.TickCount64 < deadline)
            {
                bool gameIdle = !RunManager.Instance.IsInProgress
                    && !RunManager.Instance.IsCleaningUp
                    && !RunManager.Instance.ActionExecutor.IsRunning
                    && RunManager.Instance.ActionQueueSet.IsEmpty
                    && !CombatManager.Instance.IsStarting
                    && !CombatManager.Instance.IsInProgress
                    && CombatManager.Instance.DebugOnlyGetState() == null
                    && CardSelectCmd.Selector == null
                    && !SolverController.IsSearching
                    && !SolverController.IsDeploying
                    && host.RootSceneContainer.CurrentScene is NMainMenu;
                bool idle = UnattendedAsyncActivityTracker.IsIdle && gameIdle;
                if (!idle)
                {
                    consecutiveIdleFrames = 0;
                    reclaimedAfterQuiescence = false;
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                    continue;
                }

                consecutiveIdleFrames++;
                if (consecutiveIdleFrames < 2)
                {
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                    continue;
                }

                if (!reclaimedAfterQuiescence)
                {
                    int remainingMilliseconds = checked((int)Math.Max(
                        1,
                        deadline - System.Environment.TickCount64));
                    consecutiveIdleFrames = 0;
                    await SearchGcPolicy.ReclaimIfPendingAsync("unattended_reuse")
                        .WaitAsync(TimeSpan.FromMilliseconds(remainingMilliseconds));
                    reclaimedAfterQuiescence = true;
                    continue;
                }

                if (UnattendedAsyncActivityTracker.TryEndRequest())
                {
                    Entry.Logger.Info(
                        "[CombatSolver/Unattended] PROCESS_QUIESCENT reuse_process=true");
                    return;
                }
                consecutiveIdleFrames = 0;
                reclaimedAfterQuiescence = false;
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            throw new TimeoutException(
                $"无人测试进程在 {quiescenceTimeoutMilliseconds} ms 内没有完成场景清理。");
        }

        private static async Task WaitUntilHeldAsync(NGame host)
        {
            const int quiescenceTimeoutMilliseconds = 90_000;
            long deadline = System.Environment.TickCount64 + quiescenceTimeoutMilliseconds;
            int consecutiveIdleFrames = 0;
            while (System.Environment.TickCount64 < deadline)
            {
                bool idle = UnattendedAsyncActivityTracker.IsIdle
                    && RunManager.Instance.IsInProgress
                    && !RunManager.Instance.IsCleaningUp
                    && !RunManager.Instance.ActionExecutor.IsRunning
                    && RunManager.Instance.ActionQueueSet.IsEmpty
                    && !CombatManager.Instance.IsStarting
                    && CombatManager.Instance.IsInProgress
                    && !CombatManager.Instance.IsOverOrEnding
                    && CombatManager.Instance.DebugOnlyGetState() != null
                    && CardSelectCmd.Selector == null
                    && !SolverController.IsSearching
                    && !SolverController.IsDeploying;
                if (!idle)
                {
                    consecutiveIdleFrames = 0;
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                    continue;
                }

                consecutiveIdleFrames++;
                if (consecutiveIdleFrames < 2)
                {
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                    continue;
                }
                if (UnattendedAsyncActivityTracker.TryEndRequest())
                {
                    Entry.Logger.Info(
                        "[CombatSolver/Unattended] PROCESS_QUIESCENT held_search=true");
                    return;
                }
                consecutiveIdleFrames = 0;
            }

            throw new TimeoutException(
                $"无人测试暂停进程在 {quiescenceTimeoutMilliseconds} ms 内没有完成异步活动。");
        }

        private static void WriteReady(string runId, bool held)
        {
            string readyPath = UnattendedTestFiles.GlobalPath(UnattendedTestFiles.ReadyUri);
            string tempPath = readyPath + ".tmp";
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(
                    new { SchemaVersion = 1, RunId = runId, Held = held },
                    UnattendedTestFiles.JsonOptions));
            File.Move(tempPath, readyPath, true);
        }

        private void Activate(UnattendedTestRequest request)
        {
            // Exit requests never expose this process for reuse, so tracking their background
            // continuations would add work without strengthening the process boundary.
            if (!request.ExitOnComplete)
                UnattendedAsyncActivityTracker.BeginRequest();
            IsActive = true;
            _activeRequest = request;
            _requestStartedAtUtc = DateTimeOffset.UtcNow;
            _fullRunFinalized = 0;
            if (request.RunAutoFullRun)
                StartFullRunLivenessMonitor(request);
            _injectPlayerHpLossTurn = request.InjectPlayerHpLossBeforeAutoSearchTurn ?? 0;
            _injectPlayerHpLossAmount = request.InjectPlayerHpLossAmount;
            _injectedPlayerHpLoss = 0;
            _clearPlayerBlockBeforeEndTurn = request.ClearPlayerBlockBeforeEndTurnForTest ?? 0;
            _clearedPlayerBlock = 0;
            AutomaticTurnSearchEnabled = false;
            VerifyIncrementalSearch = request.VerifyIncrementalSearch;
            ForceShortSearchOnly = request.ForceShortSearchOnly;
            MeasureSearchPhases = request.MeasureSearchPhases;
            if (request.SearchMaxDegreeOfParallelismForTest is { } maxDegreeOfParallelism
                && (maxDegreeOfParallelism < 1
                    || maxDegreeOfParallelism > SolverWeights.MaximumSearchMaxDegreeOfParallelism))
            {
                throw new InvalidOperationException(
                    $"搜索并行度必须在 1..{SolverWeights.MaximumSearchMaxDegreeOfParallelism} 之间，" +
                    $"实际为 {maxDegreeOfParallelism}。");
            }
            SearchMaxDegreeOfParallelismOverride = request.SearchMaxDegreeOfParallelismForTest;
            ShortSearchBudgetOverrideMilliseconds = request.ShortSearchBudgetOverrideMilliseconds;
            DeepSearchBudgetOverrideMilliseconds = request.DeepSearchBudgetOverrideMilliseconds;
        }

        private void Reset()
        {
            IsActive = false;
            _activeRequest = null;
            if (_livenessCts is { } cts)
            {
                _livenessCts = null;
                try
                {
                    cts.Cancel();
                }
                catch
                {
                    // 忽略取消竞争。
                }
                cts.Dispose();
            }
            AutomaticTurnSearchEnabled = true;
            VerifyIncrementalSearch = false;
            ForceShortSearchOnly = false;
            MeasureSearchPhases = false;
            SearchMaxDegreeOfParallelismOverride = null;
            _injectPlayerHpLossTurn = 0;
            _injectPlayerHpLossAmount = 0;
            _injectedPlayerHpLoss = 0;
            _clearPlayerBlockBeforeEndTurn = 0;
            _clearedPlayerBlock = 0;
            ShortSearchBudgetOverrideMilliseconds = null;
            DeepSearchBudgetOverrideMilliseconds = null;
        }
    }
}
