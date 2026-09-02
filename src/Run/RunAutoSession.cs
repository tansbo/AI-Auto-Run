using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 跑局 AI 当前所处的阶段。RunAutoController 用它做分派门禁，防止同一阶段被重复驱动。
/// </summary>
internal enum RunAutoPhase
{
    /// <summary>没有活动跑局，或跑局已结束。</summary>
    Idle,

    /// <summary>在战斗房里，由 Combat Solver 全自动接管，本子系统只等战斗结束。</summary>
    InCombat,

    /// <summary>战斗已胜利，奖励结算中（NRewardsScreen / 卡牌奖励 / 遗物等）。</summary>
    RewardsPending,

    /// <summary>奖励结算完成，地图已打开，准备选择下一房间。</summary>
    MapPending,

    /// <summary>在非战斗房里（篝火/商店/事件/宝箱），对应驱动正在执行。</summary>
    NonCombatRoom,
}

/// <summary>
/// 一局的跑局级状态。由 RunStartedEvent 创建、RunEndedEvent 清除。
/// </summary>
internal sealed class RunAutoSession
{
    public RunState? RunState { get; set; }

    public RunAutoPhase Phase { get; set; } = RunAutoPhase.Idle;

    /// <summary>当前战斗是否已获胜（用于奖励结算前的等待判定）。</summary>
    public bool CombatVictorySeen { get; set; }

    /// <summary>当前房间类型（最近一次 RoomEnteredEvent 记录）。</summary>
    public RoomType CurrentRoomType { get; set; } = RoomType.Unassigned;

    /// <summary>已处理的房间数。</summary>
    public int RoomsHandled { get; set; }

    /// <summary>本局已选卡牌 Id，供日志/覆盖层展示。</summary>
    public List<string> PickedCardIds { get; } = [];

    /// <summary>上一次决策的描述（供覆盖层显示）。</summary>
    public string? LastDecision { get; set; }

    /// <summary>本局结构化遥测（种子/抓牌/遗物/结局），由 RunAutoTelemetryEnabled 时在 RunEnded 落盘。</summary>
    public RunTelemetryData Telemetry { get; } = new();

    /// <summary>跑局级取消令牌：跑局结束（RunEndedEvent）时取消，各驱动用它提前退出。</summary>
    public CancellationToken CancellationToken { get; } = new CancellationTokenSource().Token;

    public void Cancel()
        => CancellationTokenSource.Cancel();

    public void LogDecision(string description)
    {
        LastDecision = description;
        RunAutoOverlay.Update(this);
        if (RunAutoSettings.DebugLog)
            Entry.Logger.Info($"[RunAuto] {description}");
    }

    private CancellationTokenSource CancellationTokenSource { get; } = new();
}
