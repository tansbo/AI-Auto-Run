using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace CombatSolver;

internal enum SingleStepResumeMode
{
    ExecuteCurrentTurn,
    FullAuto,
}

internal sealed class UnattendedTestRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public string ScenarioId { get; init; } = "SMOKE-001";
    public string CharacterId { get; init; } = "IRONCLAD";
    public string EncounterId { get; init; } = "FUZZY_WURM_CRAWLER_WEAK";
    public string[] ModifierIds { get; init; } = [];
    public string Seed { get; init; } = "COMBATSOLVER";
    public string? RunSnapshotPath { get; init; }
    public int Ascension { get; init; }
    public int ActIndexForTest { get; init; }
    public bool MarkEncounterAsSecondBossForTest { get; init; }
    public int EnemyCurrentHp { get; init; } = 1;
    public int[] InitialEnemyCurrentHps { get; init; } = [];
    public int? InitialPlayerHp { get; init; }
    public int? InitialPlayerMaxHp { get; init; }
    public int? InitialPlayerBlock { get; init; }
    public int? InitialPlayerEnergy { get; init; }
    public int? InitialPlayerStars { get; init; }
    public int? InitialRoundNumber { get; init; }
    public int? InitialPlayerTurnNumber { get; init; }
    public string[][] InitialEnemyStateLogs { get; init; } = [];
    public bool ReloadRunRngAfterStateInjection { get; init; }
    public UnattendedCardInjection[] Cards { get; init; } =
    [
        new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand", Count = 1 },
    ];
    public UnattendedCardInjection[] RunCards { get; init; } = [];
    public UnattendedPowerInjection[] Powers { get; init; } = [];
    public UnattendedOrbInjection[] Orbs { get; init; } = [];
    public UnattendedOrbCheck[] OrbChecks { get; init; } = [];
    public UnattendedPotionInjection[] Potions { get; init; } = [];
    public UnattendedRelicInjection[] Relics { get; init; } = [];
    public UnattendedRelicInjection[] CombatRelics { get; init; } = [];
    public UnattendedPotionCheck? PotionCheck { get; init; }
    public UnattendedPotionCheck[] PotionChecks { get; init; } = [];
    public UnattendedMonsterMoveCheck? MonsterMoveCheck { get; init; }
    public UnattendedMonsterMoveCheck[] MonsterMoveChecks { get; init; } = [];
    public string[] AdditionalMonsterIds { get; init; } = [];
    public string[] InitialEnemyMoveIds { get; init; } = [];
    public double TimeoutSeconds { get; init; } = 120;
    public int? ExpectedFinishedTurn { get; init; }
    public int? ExpectedFinishedTurnAtMost { get; init; }
    public int? ExpectedFinishedPlayerHpAtLeast { get; init; }
    public bool ClearPlayerHand { get; init; }
    public bool ClearPlayerPiles { get; init; }
    public bool ClearAllPowers { get; init; }
    public bool VerifyPredictionFailureBoundaries { get; init; }
    public bool VerifySearchPolicySnapshot { get; init; }
    public bool VerifyControllerSessionLifecycle { get; init; }
    public bool VerifyForkBoundaries { get; init; }
    public bool VerifyCombatRootSnapshot { get; init; }
    public bool VerifyBaseLibCardModifierBoundary { get; init; }
    public bool StopAfterCombatRootSnapshotAssertion { get; init; }
    public bool VerifyIncrementalSearch { get; init; }
    public bool ForceShortSearchOnly { get; init; }
    public bool MeasureSearchPhases { get; init; }
    public bool HoldAfterInitialSearch { get; init; }
    public int? ShortSearchBudgetOverrideMilliseconds { get; init; }
    public int? DeepSearchBudgetOverrideMilliseconds { get; init; }
    public int? SearchMaxDegreeOfParallelismForTest { get; init; }
    public SolverSearchPhase? ExpectedInitialSearchPhase { get; init; }
    public bool? ExpectedInitialDeepSearchTriggered { get; init; }
    public bool? ExpectedInitialDeepSearchImprovedResult { get; init; }
    public double? ExpectedInitialTotalElapsedMillisecondsAtMost { get; init; }
    public long? ExpectedInitialTotalAllocatedBytesAtMost { get; init; }
    public int? ExpectedInitialGen2CollectionsAtMost { get; init; }
    public double? ExpectedInitialTotalGcPauseMillisecondsAtMost { get; init; }
    public double? ExpectedInitialMaxGcPauseMillisecondsAtMost { get; init; }
    public double? ExpectedInitialMaxMainThreadFrameGapMillisecondsAtMost { get; init; }
    public int? ExpectedInitialMainThreadFramesOver50MillisecondsAtMost { get; init; }
    public int? ExpectedInitialMainThreadFramesOver100MillisecondsAtMost { get; init; }
    public int? ExpectedInitialTransitionCacheHitsAtLeast { get; init; }
    public int? ExpectedInitialRepeatableNoProgressBranchesPrunedAtLeast { get; init; }
    public int? ExpectedInitialChoiceBranchesEvaluatedAtLeast { get; init; }
    public int? ExpectedInitialExecutableActionCountAtLeast { get; init; }
    public int? ExpectedInitialSoldHp { get; init; }
    public int? ExpectedInitialSoldHpAtMost { get; init; }
    public int? ExpectedInitialSoldHpBranchesPrunedAtLeast { get; init; }
    public int? ExpectedInitialPotionCount { get; init; }
    public SolverTheftPolicy? ExpectedInitialTheftPolicy { get; init; }
    public int? ExpectedInitialOutstandingStolenResource { get; init; }
    public int? ExpectedInitialPotionHpSavedAtLeast { get; init; }
    public int? ExpectedInitialPotionBranchesRejectedAtLeast { get; init; }
    public int? ExpectedInitialSearchedTurnsAtLeast { get; init; }
    public int? ExpectedInitialShufflesCrossedAtLeast { get; init; }
    public int? ExpectedInitialUnmirroredCount { get; init; }
    public int? ExpectedInitialHpLostAtMost { get; init; }
    public int? ExpectedInitialProjectedBattleHpLost { get; init; }
    public int? ExpectedInitialProjectedBattleHpLostAtMost { get; init; }
    public int? ExpectedInitialLongTermResourceValueAtLeast { get; init; }
    public int? ExpectedInitialFinalMaxHp { get; init; }
    public int? ExpectedInitialMaxBlockAtLeast { get; init; }
    public int? ExpectedInitialActualBlockAtLeast { get; init; }
    public string? ExpectedInitialActionCardId { get; init; }
    public string? ExpectedInitialAbsentActionCardId { get; init; }
    public string? ExpectedInitialFirstActionCardId { get; init; }
    public string? ExpectedInitialFirstActionPotionId { get; init; }
    public string? ExpectedInitialActionTitle { get; init; }
    public int? ExpectedInitialActionReplayCount { get; init; }
    public bool? ExpectedInitialOnlyDeathRoutesFound { get; init; }
    public int? ExpectedInitialCombatEndedTurn { get; init; }
    public int? ExpectedInitialDeathTurn { get; init; }
    public int? ExpectedInitialDeathTurnAtLeast { get; init; }
    public int? ExpectedInitialFinalEnemyHpAtMost { get; init; }
    public bool? ExpectedInitialActEndingBoss { get; init; }
    public string? ExpectedInitialPlannedChoiceCardId { get; init; }
    public int? ExpectedInitialTurnStartChoiceTurn { get; init; }
    public string? ExpectedInitialTurnStartChoiceSourceId { get; init; }
    public string? ExpectedInitialTurnStartChoiceCardId { get; init; }
    public string? ExpectedInitialTurnStartChoiceStateContains { get; init; }
    public string? ExpectedInitialTurnStartChoiceStateExcludes { get; init; }
    public int? ExpectedInitialSetupChoiceCountAtLeast { get; init; }
    public string? ExpectedInitialSetupChoiceSourceId { get; init; }
    public string? ExpectedInitialSetupChoiceTextStartsWith { get; init; }
    public bool VerifyInitialSetupWaitsForUserStart { get; init; }
    public bool StopAfterInitialSetupAssertion { get; init; }
    public bool StopAfterInitialSolverResultAssertion { get; init; }
    public bool ExpectedFullAutoPausedAtDeathTurn { get; init; }
    public bool ExpectedFullAutoPausedAfterWorseRecalculation { get; init; }
    public bool ExpectedFullAutoPausedAtLiveRisk { get; init; }
    public bool EnableStopOnWorseRecalculationForTest { get; init; }
    public string? ExpectedInitialRelicEffectId { get; init; }
    public string? ExpectedInitialRelicEffectSummary { get; init; }
    public int? ExpectedReusedTurn { get; init; }
    public int? ExpectedReusedProjectedBattleHpLost { get; init; }
    public int? ExpectedUnexpectedReplansAtMost { get; init; }
    public bool StopAfterExpectedReuse { get; init; }
    public string? ExpectedPlayedCardId { get; init; }
    public string? ExpectedUsedPotionId { get; init; }
    public string? ExpectedObservedPlayerPowerId { get; init; }
    public string? ExpectedNativeChoiceOwnerPrefix { get; init; }
    public NativeChoiceSurfaceKind? ExpectedNativeChoiceSurface { get; init; }
    public int? ExpectedNativeChoiceVisibleAtLeast { get; init; }
    public int? ExpectedNativeChoiceSearchStartedAtMost { get; init; }
    public bool StopAfterExpectedPlayerPower { get; init; }
    public bool ExpectedPlayerDeath { get; init; }
    public SolverDeploymentFastMode? HeadlessFastModeForTest { get; init; }
    public SolverDeploymentFastMode? DeploymentFastModeForTest { get; init; }
    public SolverPerformancePreset? PerformancePresetForTest { get; init; }
    public int? ShortMaxCardBranchesPerNodeForTest { get; init; }
    public int? DeepMaxCardBranchesPerNodeForTest { get; init; }
    public SolverPotionPolicy? PotionPolicyForTest { get; init; }
    public SolverTheftPolicy? TheftPolicyForTest { get; init; }
    public double? NoGcRegionBudgetGigabytesForTest { get; init; }
    public double? DeploymentInterActionDelaySecondsForTest { get; init; }
    public bool AssertDeploymentSpeedRestored { get; init; }
    public bool ExportBugReportAfterSetup { get; init; }
    public bool ExportBugReportAfterCombat { get; init; }
    public bool? EnableDetailedDiagnosticLogsForTest { get; init; }
    public bool ManualEndTurnAfterInitialSearch { get; init; }
    public bool SingleStepAfterInitialSearch { get; init; }
    public SingleStepResumeMode? SingleStepResumeModeForTest { get; init; }
    public int? ExpectedTurnSetupToDeploymentDelayMillisecondsAtLeast { get; init; }
    public bool EnableFullAutoAfterManualEndTurn { get; init; }
    public int? ExpectedManualDivergencesAtLeast { get; init; }
    public int? ExpectedUnexpectedReplansAtLeast { get; init; }
    public bool StopAfterExpectedUnexpectedReplan { get; init; }
    public bool ExpectedUnexpectedReplanWarning { get; init; }
    public bool ExportBugReportAfterUnexpectedReplan { get; init; }
    public string? ExpectedBugReportControlMode { get; init; }
    public int? ExpectedNoGcRegionRolloversAtLeast { get; init; }
    public int? InjectPlayerHpLossBeforeAutoSearchTurn { get; init; }
    public int InjectPlayerHpLossAmount { get; init; }
    public int? ClearPlayerBlockBeforeEndTurnForTest { get; init; }
    /// <summary>整局模式：开新局后让 RunAutoController 驱动到跑局结束（用于种子重放训练），不进入战斗场景。</summary>
    public bool RunAutoFullRun { get; init; }
    /// <summary>评分 AI 纯逻辑检查：开新局（不进入战斗）后逐项调用 CardPickerAI/RelicPickerAI 并断言。</summary>
    public UnattendedPickerCheck[] PickerChecks { get; init; } = [];
    /// <summary>A/B 强制抓牌策略（格式 "cardId:take,cardId:skip"，见 RunAutoSettings.TryGetForcedPick）。</summary>
    public string RunAutoForcedPicks { get; init; } = string.Empty;
    /// <summary>整局遥测开关：跑局结束后写 user://run_telemetry/ 结构化 JSON（种子重放 A/B 数据源）。</summary>
    public bool RunAutoTelemetryEnabled { get; init; }
    /// <summary>遥测自动上传（opt-in）：开启并把本局匿名遥测 POST 到 RunAutoTelemetryUrl。</summary>
    public bool RunAutoTelemetryUpload { get; init; }
    public string? RunAutoTelemetryUrl { get; init; }
    /// <summary>演示定格毫秒（0=关）：关键决策前停顿，便于录制展示 AI 选牌/选路/事件/遗物。</summary>
    public int RunAutoDemoHoldMs { get; init; }
    /// <summary>演示截图（默认关）：决策瞬间进程内截图，不改速度。</summary>
    public bool RunAutoDemoCapture { get; init; }
    /// <summary>渠道演示脚手架：开局 Neow 强制选该先古遗物（Id.Entry/类名，如 KALEIDOSCOPE）。</summary>
    public string? RunAutoForceNeowRelicId { get; init; }
    /// <summary>渠道演示脚手架：指定幕（0 起）的首个事件房入口强制获得该遗物（如 SEA_GLASS）。</summary>
    public string? RunAutoForceActRelicId { get; init; }
    public int RunAutoForceActRelicAct { get; init; } = -1;
    public bool ExitOnComplete { get; init; } = true;
}

/// <summary>
/// 评分 AI 纯逻辑检查：开新局（不进入战斗）后，在给定牌组/生命/幕下调用
/// CardPickerAI 或 RelicPickerAI，并断言选牌结果或精确评分。
/// </summary>
internal sealed class UnattendedPickerCheck
{
    /// <summary>检查类型："Card"（卡牌评分/选牌）、"Relic"（遗物评分/选牌）、"AncientRelic"（先古遗物选最优正向）。</summary>
    public string Kind { get; init; } = "Card";
    /// <summary>候选卡/遗物 ID（Id.Entry、完整 ID 或运行时类名均可，大小写不敏感）。</summary>
    public string[] OptionIds { get; init; } = [];
    /// <summary>仅 Card：评分前把这些卡牌注入跑局牌组（构造重复牌惩罚/牌组画像）。注入是粘性的，后续检查继续保留这些牌。</summary>
    public string[] DeckCardIds { get; init; } = [];
    /// <summary>仅 Card：评分前设置玩家当前生命。设置是粘性的，依赖满血的检查须显式重设。</summary>
    public int? PlayerHp { get; init; }
    /// <summary>仅 Card：评分前设置玩家最大生命。设置是粘性的，依赖满血的检查须显式重设。</summary>
    public int? PlayerMaxHp { get; init; }
    /// <summary>评分前切换到的幕索引（影响 ActIndex 与先古遗物路线感知）。注意：切换是粘性的，后续检查继续留在该幕。</summary>
    public int? ActIndexForTest { get; init; }
    /// <summary>期望选中的卡/遗物 ID；留空/null 表示期望跳过（返回 null）。AncientRelic 额外断言绝不选诅咒。</summary>
    public string? ExpectedPickId { get; init; }
    /// <summary>仅 AncientRelic：期望全部选项都是诅咒时按文档回退到第一个选项（跳过"绝不选诅咒"断言）。</summary>
    public bool AllowAncientCurseFallback { get; init; }
    /// <summary>Card/Relic：可选，断言 Evaluate/Score 返回的精确评分（浮点容差 0.001，仅支持单个候选）。</summary>
    public float? ExpectedScore { get; init; }
    /// <summary>仅评分断言（ExpectedScore）校准用：为 true 时评分不符只记录实际值并继续，不中止本局。
    /// 用于公式调整后的期望值批量重校（重校后应去掉该开关复跑确认）。</summary>
    public bool ScoreLogOnly { get; init; }
}

internal sealed class UnattendedPotionCheck
{
    public string PotionId { get; init; } = string.Empty;
    public string Target { get; init; } = "Player";
    public bool ProcureThroughGame { get; init; }
    public int TargetIndex { get; init; }
    public int? PlayerHpBefore { get; init; }
    public int? PlayerBlockBefore { get; init; }
    public int? PlayerEnergyBefore { get; init; }
    public int? PlayerStarsBefore { get; init; }
    public int? EnemyHpBefore { get; init; }
    public bool TriggerPlayerSideTurnEndAfterUse { get; init; }
    public bool TriggerEnemySideTurnEndAfterUse { get; init; }
    public bool TriggerAutomaticDeath { get; init; }
    public string[] ChoiceCardIds { get; init; } = [];
    public string[] NestedChoiceCardIds { get; init; } = [];
    public bool ClearPlayerHandBeforeUse { get; init; }
    public UnattendedCardInjection[] Cards { get; init; } = [];
    public int? ExpectedPlayerHp { get; init; }
    public int? ExpectedPlayerBlock { get; init; }
    public int? ExpectedPlayerEnergy { get; init; }
    public int? ExpectedPlayerStars { get; init; }
    public int? ExpectedPlayerOrbCapacity { get; init; }
    public int? ExpectedEnemyHp { get; init; }
    public Dictionary<string, int> ExpectedPlayerPowers { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedEnemyPowers { get; init; } = new(StringComparer.Ordinal);
    public string[] ExpectedAbsentPlayerPowers { get; init; } = [];
    public string[] ExpectedAbsentEnemyPowers { get; init; } = [];
    public Dictionary<string, int> ExpectedPlayerCardUpgrades { get; init; } = new(StringComparer.Ordinal);
    public string? ExpectedSurroundedFacing { get; init; }
}

internal sealed class UnattendedOrbCheck
{
    public string OrbId { get; init; } = string.Empty;
    public int TargetIndex { get; init; }
}

internal sealed class UnattendedMonsterMoveCheck
{
    public int EnemyIndex { get; init; }
    public string MonsterId { get; init; } = string.Empty;
    public int MonsterOccurrence { get; init; }
    public string? SpawnInitialMoveId { get; init; }
    public string MoveId { get; init; } = string.Empty;
    public bool UseCurrentMove { get; init; }
    public SearchBoundaryReason? ExpectedSearchBoundary { get; init; }
    public bool? ExpectedSimulatedDynamicResolution { get; init; }
    public int? PlayerHpBefore { get; init; }
    public int? PlayerBlockBefore { get; init; }
    public int? PlayerEnergyBefore { get; init; }
    public int? PlayerStarsBefore { get; init; }
    public int? PlayerGoldBefore { get; init; }
    public int? EnemyHpBefore { get; init; }
    public int? EnemyBlockBefore { get; init; }
    public int? OstyHpBefore { get; init; }
    public int? RoundNumberBefore { get; init; }
    public int? PlayerTurnNumberBefore { get; init; }
    public bool ClearAllRelicsBeforeMove { get; init; }
    public UnattendedRelicInjection[] RelicsBeforeMove { get; init; } = [];
    public bool ClearPlayerOrbsBeforeMove { get; init; }
    public UnattendedOrbInjection[] OrbsBeforeMove { get; init; } = [];
    public bool ClearAllPowersBeforeMove { get; init; }
    public bool ClearPlayerPilesBeforeMove { get; init; }
    public bool ClearPlayerHandBeforeMove { get; init; }
    public string DerivedHookTarget { get; init; } = "Player";
    public int? ExpectedModifiedHandDraw { get; init; }
    public int? ExpectedModifiedMaxEnergy { get; init; }
    public string DerivedHookCardId { get; init; } = string.Empty;
    public int DerivedHookBaseValue { get; init; }
    public int? ExpectedModifiedXValue { get; init; }
    public string DerivedHookOrbId { get; init; } = string.Empty;
    public int? ExpectedModifiedOrbValue { get; init; }
    public int? ExpectedModifiedHandDrawAfterPlayerSetup { get; init; }
    public int? ExpectedModifiedMaxEnergyAfterPlayerSetup { get; init; }
    public string? ExpectedStatefulRelicStateAfterPlayerSetup { get; init; }
    public string? ExpectedMirrorRelicState { get; init; }
    public bool? ExpectedShouldClearBlock { get; init; }
    public bool? ExpectedShouldFlush { get; init; }
    public bool? ExpectedShouldFlushAfterPlayerSetup { get; init; }
    public bool? ExpectedShouldPlayerResetEnergy { get; init; }
    public bool? ExpectedShouldPlayerResetEnergyAfterPlayerSetup { get; init; }
    public bool? ExpectedSimulatedSkipNextMove { get; init; }
    public bool RollNextMoveAfterActual { get; init; }
    public string[] MonsterStateLogBefore { get; init; } = [];
    public bool TriggerPlayerSideTurnEndBeforeMove { get; init; }
    public bool TriggerEnemySideTurnEndBeforeMove { get; init; }
    public bool TriggerPlayerSideTurnEndAfterMove { get; init; }
    public bool TriggerEnemySideTurnEndAfterMove { get; init; }
    public int EnemySideTurnEndTriggerCount { get; init; }
    public bool TriggerPlayerSideTurnStartAfterMove { get; init; }
    public bool TriggerEnemySideTurnStartAfterMove { get; init; }
    public bool TriggerPlayerTurnEndAfterMove { get; init; }
    public bool TriggerPlayerSetupAfterMove { get; init; }
    public string[] PlayerSetupChoiceCardIds { get; init; } = [];
    public bool TriggerAutoPrePlayAfterPlayerSetup { get; init; }
    public string[] AutoPrePlayChoiceCardIds { get; init; } = [];
    public bool KillMonsterAfterMove { get; init; }
    public int? KillEnemyIndexAfterMove { get; init; }
    public UnattendedCardInjection? CardBeforeMove { get; init; }
    public UnattendedCardInjection[] CardsBeforeMove { get; init; } = [];
    public UnattendedCardInjection? CardAfterMove { get; init; }
    public UnattendedCardInjection[] CardsAfterMove { get; init; } = [];
    public UnattendedCardTransformCheck[] CardTransformsAfterMove { get; init; } = [];
    public UnattendedCardInjection? PlayCardAfterMove { get; init; }
    public UnattendedCardPlayCheck[] CardPlayChecksBeforeMove { get; init; } = [];
    public UnattendedCardPlayCheck[] CardPlayChecksAfterMove { get; init; } = [];
    public UnattendedCardPlayCheck[] CardPlayChecksAfterPlayerSideTurnEnd { get; init; } = [];
    public UnattendedCardPlayCheck[] CardPlayChecksAfterPlayerSetup { get; init; } = [];
    public string? LiveEndTurnRiskCardId { get; init; }
    public string LiveEndTurnRiskChoiceSourceId { get; init; } = string.Empty;
    public string[] LiveEndTurnRiskChoiceCardIds { get; init; } = [];
    public string? LiveEndTurnRiskKnowledgeChoiceCardId { get; init; }
    public UnattendedPowerInjection? PowerBeforeMove { get; init; }
    public UnattendedPowerInjection[] PowersBeforeMove { get; init; } = [];
    public UnattendedPowerInjection[] PowersAfterMove { get; init; } = [];
    public string? ExpectedNextMoveId { get; init; }
    public int? ExpectedPlayerHp { get; init; }
    public int? ExpectedPlayerHpLoss { get; init; }
    public int? ExpectedOstyHp { get; init; }
    public int? ExpectedOstyMaxHp { get; init; }
    public Dictionary<string, int> ExpectedOstyPowers { get; init; } = new(StringComparer.Ordinal);
    public int? ExpectedPlayerBlock { get; init; }
    public int? ExpectedPlayerBlockAfterMoveActions { get; init; }
    public int? ExpectedPlayerBlockGain { get; init; }
    public int? ExpectedPlayerEnergy { get; init; }
    public int? ExpectedPlayerStars { get; init; }
    public int? ExpectedPlayerGold { get; init; }
    public int? ExpectedPlayerOrbCapacity { get; init; }
    public int? ExpectedPlayerHandCount { get; init; }
    public int? ExpectedEnemyBlockGain { get; init; }
    public int? ExpectedEnemyHpGain { get; init; }
    public Dictionary<string, int> ExpectedPlayerPowers { get; init; } = new(StringComparer.Ordinal);
    public string[] ExpectedAbsentPlayerPowers { get; init; } = [];
    public Dictionary<string, int> ExpectedEnemyPowers { get; init; } = new(StringComparer.Ordinal);
    public string[] ExpectedAbsentEnemyPowers { get; init; } = [];
    public Dictionary<string, int> ExpectedPlayerPowerStates { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedEnemyPowerStates { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerPileCards { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerPileCardDamageTotals { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerCardStates { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerCardCosts { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerCardEnchantments { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerCardUpgrades { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedEnemyHpsByModel { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedEnemyBlocksByModel { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExpectedPlayerOrbs { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class UnattendedCardPlayCheck
{
    public string CardId { get; init; } = string.Empty;
    public int Occurrence { get; init; }
    public string Target { get; init; } = "Enemy";
    public bool ExpectedPlayable { get; init; } = true;
    public bool UseChoice { get; init; }
    public string[] ChoiceCardIds { get; init; } = [];
    public string[] ExpectedExcludedChoiceCardIds { get; init; } = [];
    public string? ExpectedCardIdAfterPlay { get; init; }
    public string? ExpectedCardPileAfterPlay { get; init; }
    public bool AssertForkableAfterPlay { get; init; }
}

internal sealed class UnattendedCardTransformCheck
{
    public string OriginalCardId { get; init; } = string.Empty;
    public string ReplacementCardId { get; init; } = string.Empty;
    public int Occurrence { get; init; }
}

internal sealed class UnattendedPowerInjection
{
    public string PowerId { get; init; } = string.Empty;
    public string Target { get; init; } = "Enemy";
    public int TargetIndex { get; init; }
    public string? PowerTarget { get; init; }
    public int PowerTargetIndex { get; init; }
    public string? Applier { get; init; }
    public int ApplierIndex { get; init; }
    public int Amount { get; init; } = 1;
    public Dictionary<string, int> DynamicVars { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> InternalIntegerMembers { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class UnattendedCardInjection
{
    public string CardId { get; init; } = string.Empty;
    public string Pile { get; init; } = "Hand";
    public int Count { get; init; } = 1;
    public int UpgradeLevels { get; init; }
    public string? EnchantmentId { get; init; }
    public int EnchantmentAmount { get; init; } = 1;
    public string? AfflictionId { get; init; }
    public int AfflictionAmount { get; init; } = 1;
    public bool TreatAsDeckCard { get; init; }
    public Dictionary<string, int> DynamicVars { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> EnumMembers { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class UnattendedPotionInjection
{
    public string PotionId { get; init; } = string.Empty;
}

internal sealed class UnattendedRelicInjection
{
    public string RelicId { get; init; } = string.Empty;
    public bool AddWithoutObtainedEffects { get; init; }
    public Dictionary<string, int> IntegerMembers { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, bool> BooleanMembers { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class UnattendedOrbInjection
{
    public string OrbId { get; init; } = string.Empty;
    public int Count { get; init; } = 1;
    public Dictionary<string, decimal> DecimalMembers { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class UnattendedTestResult
{
    public int SchemaVersion { get; init; } = 1;
    public required string RunId { get; init; }
    public required string ScenarioId { get; init; }
    public required string Status { get; init; }
    public required string Stage { get; init; }
    public required string CharacterId { get; init; }
    public required string EncounterId { get; init; }
    public required string Seed { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public bool MainThread { get; init; }
    public bool CombatEnded { get; init; }
    public int StartedTurn { get; init; }
    public int FinishedTurn { get; init; }
    public long ManagedHeapBytes { get; init; }
    public long ManagedFragmentedBytes { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public UnattendedStageTiming[] StageTimings { get; init; } = [];
    public string[] CompletedChecks { get; init; } = [];
    public string? Error { get; init; }
    public DateTimeOffset FinishedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed class UnattendedStageTiming
{
    public required string Stage { get; init; }
    public double StartedMilliseconds { get; init; }
    public double DurationMilliseconds { get; init; }
}

internal static class UnattendedTestFiles
{
    public const string RequestUri = "user://combat_solver_test_request.json";
    public const string RunningUri = "user://combat_solver_test_running.json";
    public const string ResultUri = "user://combat_solver_test_result.json";
    public const string ReadyUri = "user://combat_solver_test_ready.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string GlobalPath(string uri) => ProjectSettings.GlobalizePath(uri);
}

