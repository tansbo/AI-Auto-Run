using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Run;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed record ScenarioContext(
        CharacterModel Character,
        EncounterModel Encounter,
        CombatState CombatState,
        Player Player,
        int StartedTurn,
        IReadOnlyList<UnattendedOrbCheck> OrbChecks,
        IReadOnlyList<UnattendedPotionCheck> PotionChecks,
        IReadOnlyList<UnattendedMonsterMoveCheck> MonsterMoveChecks);

    private sealed class ScenarioBuilder(UnattendedTestRunner runner)
    {
        public CombatState? CombatState { get; private set; }
        public int StartedTurn { get; private set; }

        /// <summary>整局探针：主线程卡住时后台线程仍可写文件，用于定位战斗回合卡在哪一步。</summary>
        private CancellationTokenSource? _probeCts;
        private string? _probePath;

        public async Task<ScenarioContext> BuildAsync()
        {
            UnattendedTestRequest request = runner._request;
            runner.SetStage("game_startup");
            await runner._host.GameStartupComplete;
            runner.ApplyHeadlessFastModeOverride();
            runner.EnsureWithinDeadline();
            if (RunManager.Instance.IsInProgress)
                throw new InvalidOperationException("无人测试要求从无进行中跑局的独立游戏进程启动。");

            // 整局模式：开跑前注入 RunAuto 设置，否则 RunStartedEvent 时 Enabled 为 false 不会建会话。
            if (request.RunAutoFullRun)
            {
                ApplyRunAutoFullRunSettings();
                StartRunProbe();
            }

            CharacterModel character = ResolveUnique(ModelDb.AllCharacters, request.CharacterId, "角色");
            EncounterModel encounter = ResolveUnique(ModelDb.AllEncounters, request.EncounterId, "遭遇");
            ModifierModel[] modifiers = request.ModifierIds
                .Select(id => ResolveUnique(
                    ModelDb.GoodModifiers.Concat(ModelDb.BadModifiers),
                    id,
                    "自定义规则").ToMutable())
                .ToArray();

            runner.SetStage("start_run");
            await runner._host.StartNewSingleplayerRun(
                character,
                shouldSave: false,
                ActModel.GetDefaultList(),
                modifiers,
                request.Seed,
                GameMode.Standard,
                request.Ascension);
            runner.EnsureWithinDeadline();

            // 整局模式：不进战斗，等 RunAuto 驱动到跑局结束（会话在 RunEnded 后被清空）。
            if (request.RunAutoFullRun)
            {
                try
                {
                    await WaitForRunAutoSessionToClearAsync();
                    return null!;
                }
                finally
                {
                    StopRunProbe();
                }
            }
            StopRunProbe();

            runner.SetStage("inject_run_relics");
            RunState runState = RunManager.Instance.DebugOnlyGetState()
                ?? throw new InvalidOperationException("创建跑局后找不到 RunState。");
            if (request.ActIndexForTest != 0)
            {
                if ((uint)request.ActIndexForTest >= (uint)runState.Acts.Count)
                    throw new InvalidOperationException($"测试幕索引超出范围：{request.ActIndexForTest}。");
                await RunManager.Instance.SetActInternal(request.ActIndexForTest);
            }
            if (request.MarkEncounterAsSecondBossForTest)
                runState.Act.SetSecondBossEncounter(encounter);
            Player runPlayer = LocalContext.GetMe(runState)
                ?? throw new InvalidOperationException("创建跑局后找不到本地玩家。");
            foreach (UnattendedRelicInjection injection in request.Relics)
                await InjectRelicAsync(runPlayer, injection);
            if (!string.IsNullOrWhiteSpace(request.RunSnapshotPath))
                await ApplyRunSnapshotAsync(runState, runPlayer, request.RunSnapshotPath);
            foreach (UnattendedCardInjection injection in request.RunCards)
                await InjectRunCardAsync(runState, runPlayer, injection);

            runner.SetStage("enter_encounter");
            EncounterModel mutableEncounter = encounter.ToMutable();
            await RunManager.Instance.EnterRoomDebug(
                RoomType.Monster,
                MapPointType.Unassigned,
                mutableEncounter);

            runner.SetStage("wait_player_turn");
            CombatState = await runner.WaitForPlayableCombatAsync();
            Player player = LocalContext.GetMe(CombatState)
                ?? throw new InvalidOperationException("进入战斗后找不到本地玩家。");
            StartedTurn = player.PlayerCombatState!.TurnNumber;

            runner.SetStage("inject_state");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            IReadOnlyList<UnattendedOrbCheck> orbChecks = request.OrbChecks;
            IReadOnlyList<UnattendedPotionCheck> potionChecks = runner.GetPotionChecks();
            IReadOnlyList<UnattendedMonsterMoveCheck> monsterMoveChecks = runner.GetMonsterMoveChecks();
            foreach (string monsterId in request.AdditionalMonsterIds
                         .Where(static id => !string.IsNullOrWhiteSpace(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                await EnsureMonsterExistsAsync(CombatState, monsterId, null);
            foreach (IGrouping<string, UnattendedMonsterMoveCheck> group in monsterMoveChecks
                         .Where(static check => !string.IsNullOrWhiteSpace(check.MonsterId))
                         .GroupBy(static check => check.MonsterId, StringComparer.OrdinalIgnoreCase))
            {
                string[] initialMoveIds = group
                    .Select(static check => check.SpawnInitialMoveId)
                    .Where(static moveId => !string.IsNullOrWhiteSpace(moveId))
                    .Distinct(StringComparer.Ordinal)
                    .Cast<string>()
                    .ToArray();
                if (initialMoveIds.Length > 1)
                    throw new InvalidOperationException($"怪物 {group.Key} 配置了多个出生初始行动。");
                string? initialMoveId = initialMoveIds.SingleOrDefault();
                await EnsureMonsterExistsAsync(CombatState, group.Key, initialMoveId);
                int requiredCount = group.Max(static check => check.MonsterOccurrence) + 1;
                int existingCount = CombatState.Enemies.Count(candidate =>
                    candidate.Monster != null && ModelMatches(candidate.Monster, group.Key));
                while (existingCount < requiredCount)
                {
                    await AddMonsterForTestAsync(CombatState, group.Key, initialMoveId);
                    existingCount++;
                }
            }
            if (request.InitialEnemyCurrentHps.Length > 0)
            {
                if (request.InitialEnemyCurrentHps.Length != CombatState.Enemies.Count)
                {
                    throw new InvalidOperationException(
                        $"逐敌生命数量 {request.InitialEnemyCurrentHps.Length} 与敌人数 {CombatState.Enemies.Count} 不同。");
                }
                for (int enemyIndex = 0; enemyIndex < CombatState.Enemies.Count; enemyIndex++)
                {
                    Creature enemy = CombatState.Enemies[enemyIndex];
                    await CreatureCmd.SetCurrentHp(
                        enemy,
                        Math.Clamp(request.InitialEnemyCurrentHps[enemyIndex], 0, enemy.MaxHp));
                }
            }
            else
            {
                foreach (Creature enemy in CombatState.Enemies.Where(static enemy => !enemy.IsDead))
                    await CreatureCmd.SetCurrentHp(enemy, Math.Min(request.EnemyCurrentHp, enemy.MaxHp));
            }
            runner.ForceInitialEnemyMoves(CombatState);
            runner.ForceInitialEnemyStateLogs(CombatState);
            if (request.InitialPlayerMaxHp is { } initialPlayerMaxHp)
                await CreatureCmd.SetMaxHp(player.Creature, initialPlayerMaxHp);
            if (request.InitialPlayerHp is { } initialPlayerHp)
            {
                await CreatureCmd.SetCurrentHp(
                    player.Creature,
                    Math.Clamp(initialPlayerHp, 1, player.Creature.MaxHp));
            }
            if (request.InitialPlayerBlock is { } initialPlayerBlock)
                await SetBlockAsync(player.Creature, initialPlayerBlock);
            if (request.InitialPlayerEnergy is { } initialPlayerEnergy)
                SetEnergy(player, initialPlayerEnergy);
            if (request.InitialPlayerStars is { } initialPlayerStars)
                SetStars(player, initialPlayerStars);
            if (request.InitialRoundNumber is { } initialRoundNumber)
                CombatState.RoundNumber = initialRoundNumber;
            if (request.InitialPlayerTurnNumber is { } initialPlayerTurnNumber)
            {
                PlayerCombatState playerState = player.PlayerCombatState!;
                if (initialPlayerTurnNumber < playerState.TurnNumber)
                {
                    throw new InvalidOperationException(
                        $"玩家测试回合号不能从 {playerState.TurnNumber} 回退到 {initialPlayerTurnNumber}。");
                }
                while (playerState.TurnNumber < initialPlayerTurnNumber)
                    playerState.IncrementTurnNumber();
            }

            if (request.ClearPlayerPiles)
                await ClearPlayerPilesAsync(player);
            else if (request.ClearPlayerHand)
            {
                await CardCmd.Discard(
                    new BlockingPlayerChoiceContext(),
                    player.PlayerCombatState!.Hand.Cards.ToArray());
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            foreach (UnattendedCardInjection injection in request.Cards)
                await InjectCardAsync(CombatState, player, injection);
            foreach (UnattendedOrbInjection injection in request.Orbs)
                await InjectOrbAsync(player, injection);
            foreach (UnattendedPotionInjection injection in request.Potions)
                InjectPotionForTest(player, injection.PotionId);
            foreach (UnattendedRelicInjection injection in request.CombatRelics)
                await InjectRelicAsync(player, injection);
            if (request.ClearAllPowers)
            {
                foreach (PowerModel power in CombatState.Creatures
                             .SelectMany(creature => creature.Powers)
                             .ToArray())
                {
                    await PowerCmd.Remove(power);
                }
            }
            foreach (UnattendedPowerInjection injection in request.Powers)
                await InjectPowerAsync(CombatState, player, injection);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            if (request.ReloadRunRngAfterStateInjection)
            {
                if (string.IsNullOrWhiteSpace(request.RunSnapshotPath))
                    throw new InvalidOperationException("战斗状态注入后回载 RNG 需要跑局快照。");
                ReloadRunSnapshotRng(runState, request.RunSnapshotPath);
            }
            StartedTurn = player.PlayerCombatState!.TurnNumber;
            await runner.NextFrameAsync();

            return new ScenarioContext(
                character,
                encounter,
                CombatState,
                player,
                StartedTurn,
                orbChecks,
                potionChecks,
                monsterMoveChecks);
        }

        /// <summary>注入整局模式设置：全自动跑局开启 + 快速动画 + 调试日志。战斗求解器全自动由 RunAuto 的 OnCombatStarting 自动开启。</summary>
        private void ApplyRunAutoFullRunSettings()
        {
            // BeginRequest 会关掉自动回合搜索；整局模式依赖 Entry.OnTurnStarted 的自动搜索，需重新打开。
            runner._protocolHost.EnableAutomaticTurnSearch();
            SolverSettingsData data = SolverSettings.Current with
            {
                RunAutoEnabled = true,
                RunAutoStopOnGameOver = true,
                // FastMode 疑似在 headless 下让游戏原生自动出牌阶段卡死，先关掉定位。
                RunAutoFastMode = false,
                RunAutoDebugLog = true,
                RunAutoForcedPicks = runner._request.RunAutoForcedPicks ?? string.Empty,
                RunAutoTelemetryEnabled = runner._request.RunAutoTelemetryEnabled,
                // 偏差诊断：整局冒烟开启详细日志，战斗每回合结束输出
                // HP_PREDICTION（搜索预测 vs 实机复核投影），用于定位计划掉血偏差。
                EnableDetailedDiagnosticLogs = true,
                // 无人整局训练：没有人类旁观，live_end_turn_risk 停止全自动只会让战斗卡死。
                // 实机偏差或预测死亡时，让战斗自然打到底，由 RunEnded 收尾并记录胜负。
                StopFullAutoOnDeathTurn = false,
                StopFullAutoOnWorseRecalculation = false,
            };
            SolverSettings.ApplyForTesting(data);
            // ApplyForTesting 只改 SolverSettings._current；SolverController 的
            // _stopFullAutoOnDeathTurn/_stopFullAutoOnWorseRecalculation 等静态字段
            // 是启动时由 ApplyPersistentSettings 快照来的，必须再同步一次才会生效。
            SolverController.ApplyPersistentSettings(SolverSettings.Capture());
        }

        /// <summary>
        /// 等整局被 RunAuto 驱动结束：先见到会话（RunStarted），再等到会话被清空（RunEnded）。
        /// 超时由 runner 的 EnsureWithinDeadline（请求 TimeoutSeconds）兜底。
        /// 注意：轮询用 Task.Delay（线程池），不用 ProcessFrame——跑局结束后（死亡/结算屏）场景树
        /// 可能停住，ProcessFrame 信号不再恢复等待，导致整局收尾永远卡住（已实证）。
        /// </summary>
        private async Task WaitForRunAutoSessionToClearAsync()
        {
            bool seenSession = false;
            var diagnosticTimer = Stopwatch.StartNew();
            TimeSpan lastDiagnostic = TimeSpan.Zero;
            while (true)
            {
                runner.EnsureWithinDeadline();
                bool active = RunAutoController.Session != null;
                if (active)
                    seenSession = true;
                if (seenSession && !active)
                    return;
                TimeSpan elapsed = diagnosticTimer.Elapsed;
                if (elapsed - lastDiagnostic >= TimeSpan.FromSeconds(30))
                {
                    lastDiagnostic = elapsed;
                    LogFullRunDiagnostic(elapsed);
                }
                await Task.Delay(250);
            }
        }

        /// <summary>整局冒烟诊断：每 30s 打印战斗/跑局状态，便于定位 headless 卡在哪一步。</summary>
        private void LogFullRunDiagnostic(TimeSpan elapsed)
        {
            RunAutoSession? session = RunAutoController.Session;
            string runState = session == null
                ? "no-session"
                : $"phase={session.Phase} room={session.CurrentRoomType} rooms_handled={session.RoomsHandled}";
            string combatState = "no-combat";
            if (CombatManager.Instance is { IsInProgress: true })
            {
                CombatState? state = CombatManager.Instance.DebugOnlyGetState();
                if (state != null)
                {
                    Player? player = LocalContext.GetMe(state);
                    combatState =
                        $"round={state.RoundNumber} side={state.CurrentSide} " +
                        $"phase={player?.PlayerCombatState?.Phase} " +
                        $"turn={player?.PlayerCombatState?.TurnNumber} " +
                        $"setup_pending={PlayerTurnSetupCoordinator.HasPendingPlannedChoice(state)} " +
                        $"full_auto={SolverController.FullAutoEnabled}";
                }
            }
            Entry.Logger.Info(
                $"[CombatSolver/Unattended] FULL_RUN_DIAGNOSTIC elapsed_s={elapsed.TotalSeconds:0.#} " +
                $"run=[{runState}] combat=[{combatState}]");
        }

        /// <summary>
        /// 后台探针：主线程被 StartNewSingleplayerRun 卡住时，后台线程仍每秒写一次
        /// 战斗/跑局状态到 user://full_run_probe.log，用于定位回合不开始的原因。
        /// 只在整局模式启用，跑局结束（会话清空）后停止。
        /// </summary>
        private void StartRunProbe()
        {
            if (_probeCts != null)
                return;
            _probePath = Path.Combine(
                ProjectSettings.GlobalizePath("user://"),
                "full_run_probe.log");
            File.AppendAllText(_probePath, $"probe_started_t={System.Environment.TickCount64}\n");
            CancellationTokenSource cts = new();
            _probeCts = cts;
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        File.AppendAllText(_probePath, BuildProbeLine());
                    }
                    catch
                    {
                        // 探针文件写入失败不致命，继续尝试。
                    }
                    try
                    {
                        await Task.Delay(1000, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
        }

        private void StopRunProbe()
        {
            CancellationTokenSource? cts = _probeCts;
            _probeCts = null;
            if (cts == null)
                return;
            try
            {
                if (_probePath != null)
                    File.AppendAllText(_probePath, "probe_stopped\n");
            }
            catch
            {
                // 忽略探针停止时的写入失败。
            }
            cts.Cancel();
            cts.Dispose();
        }

        private string BuildProbeLine()
        {
            string combat = "no-combat";
            try
            {
                if (CombatManager.Instance is { IsInProgress: true })
                {
                    CombatState? state = CombatManager.Instance.DebugOnlyGetState();
                    if (state != null)
                    {
                        Player? player = LocalContext.GetMe(state);
                        combat =
                            $"combat round={state.RoundNumber} side={state.CurrentSide} " +
                            $"phase={player?.PlayerCombatState?.Phase} turn={player?.PlayerCombatState?.TurnNumber} " +
                            $"setup_pending={PlayerTurnSetupCoordinator.HasPendingPlannedChoice(state)} " +
                            $"full_auto={SolverController.FullAutoEnabled}";
                    }
                }
            }
            catch (Exception ex)
            {
                combat = $"probe-combat-error={ex.GetType().Name}";
            }
            string run = "no-session";
            try
            {
                RunAutoSession? session = RunAutoController.Session;
                if (session != null)
                    run = $"run phase={session.Phase} room={session.CurrentRoomType} rooms={session.RoomsHandled}";
            }
            catch (Exception ex)
            {
                run = $"probe-run-error={ex.GetType().Name}";
            }
            ulong frames = 0;
            try
            {
                frames = Godot.Engine.GetProcessFrames();
            }
            catch
            {
                // 帧计数读取失败忽略。
            }
            return $"t={System.Environment.TickCount64} frames={frames} {run} | {combat}\n";
        }
    }
}
