using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 战后奖励屏幕（NRewardsScreen）驱动：逐个领取可用的奖励按钮，
/// 点开子覆盖层（卡牌奖励等）时等它自己处理完再继续；全部领完点 Proceed。
/// 由 RunAutoController 在战斗胜利时启动。
/// </summary>
internal static class RewardsScreenDriver
{
    private static bool _active;
    private static long _lastProgressTick;  // worker 最近一次实际推进时刻（看门狗/兜底判定"停摆"用）

    public static void OnCombatVictory()
        => Start(advanceFromRoom: true);

    /// <summary>
    /// 事件内奖励屏（水晶球揭幕等子奖励，DriveRewardsAsync 复用）：
    /// 领完奖励**不点 Proceed**——事件本体还在，离开/选路由 EventDriver 继续处理。
    /// 复用战斗流程（OnCombatVictory）会点 Proceed 触发离房/地图选路 → 事件未完成即被带离
    /// （133/139 实证：水晶球"休息后"事件驱动死亡、整局卡死 25min 无遥测）。
    /// </summary>
    public static void OnEventRewards()
        => Start(advanceFromRoom: false);

    /// <summary>奖励 worker 是否正在跑（供看门狗/EventOverlayDriver 区分"健康处理中"与"已退出/停摆"）。</summary>
    internal static bool IsWorkerActive => _active;

    /// <summary>worker 最近一次实际推进的时刻（领奖点击/腾栏/收尾点击），毫秒。</summary>
    internal static long LastProgressTick => _lastProgressTick;

    private static void MarkProgress() => _lastProgressTick = System.Environment.TickCount64;

    private static void Start(bool advanceFromRoom)
    {
        RunAutoSession? session = RunAutoController.Session;
        if (session == null || !RunAutoSettings.Enabled || _active)
        {
            if (_active)
                session?.LogDecision($"奖励 worker 已在跑，忽略新请求(advance={advanceFromRoom})");
            return;
        }
        _active = true;
        MarkProgress();
        session.LogDecision($"奖励 worker 启动 advance={advanceFromRoom}");
        TaskHelper.RunSafely(HandleAsync(advanceFromRoom));
    }

    private static async Task<bool> TryMakeRoomAndClaimPotionAsync(
        RunAutoSession session,
        NRewardsScreen screen,
        NRewardButton button,
        CancellationToken token)
    {
        if (button.Reward is not PotionReward reward || reward.Potion == null)
            return false;
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        Player? player = runState == null ? null : LocalContext.GetMe(runState);
        if (player == null)
            return false;

        if (player.HasOpenPotionSlots)
        {
            // 循环里已判满栏才进来，这里兜底再领一次。
            return await ClaimPotionRewardAsync(session, screen, button, token);
        }

        List<PotionModel> held = player.Potions.ToList();
        if (held.Count == 0)
            return await ClaimPotionRewardAsync(session, screen, button, token);

        RunActContext.RouteAhead ahead = RunActContext.CaptureAhead(runState);
        // 幕末回血药只保留"Ancient 没补回"的部分（A2+ 补 80% → 留存 20%；其余难度满补 → 0）。
        decimal healCarry = ahead.NearActEnd
            ? 1m - RunActContext.ActBoundaryHealFraction()
            : 1m;
        decimal hpFraction = player.Creature.MaxHp > 0
            ? (decimal)player.Creature.CurrentHp / player.Creature.MaxHp
            : 1m;

        PotionRunPolicy.IntakePlan plan = PotionRunPolicy.PlanIntake(
            reward.Potion, held, hpFraction, ahead.RouteDanger, healCarry);
        if (plan.Kind == PotionRunPolicy.IntakeKind.SkipOffer)
        {
            session.LogDecision(
                $"药水奖励跳过：栏位满且新药 {reward.Potion.Id.Entry} 不优于最弱持有药水（危险度 {ahead.RouteDanger}）");
            return false;
        }

        if (plan.ToRemove != null)
        {
            string removeDesc = plan.DrinkInsteadOfDiscard ? "喝掉" : "丢弃";
            session.LogDecision(
                $"药水奖励腾栏：{removeDesc} {plan.ToRemove.Id.Entry} 腾位领 {reward.Potion.Id.Entry}");
            if (plan.DrinkInsteadOfDiscard)
            {
                try
                {
                    // 战斗外用药通道：进原版动作队列（UsePotionAction NonCombat），同步 UI 与结算。
                    plan.ToRemove.EnqueueManualUse(null);
                }
                catch (InvalidOperationException ex)
                {
                    session.LogDecision($"药水腾栏失败（喝 {plan.ToRemove.Id.Entry} 不可用）：{ex.Message}");
                    return false;
                }
                try
                {
                    await RunUiHelper.WaitUntilAsync(
                        () => player.HasOpenPotionSlots,
                        token,
                        TimeSpan.FromSeconds(10),
                        "战斗外喝药未腾出栏位");
                }
                catch (RunAutoTimeoutException)
                {
                    // 动作队列延迟：本奖励先不领，防止把没结算完的药水状态一起带进战斗。
                    session.LogDecision("战斗外喝药超时未腾栏，本奖励留待下次处理");
                    return false;
                }
            }
            else
            {
                await PotionCmd.Discard(plan.ToRemove);
            }
            MarkProgress();
        }

        if (!player.HasOpenPotionSlots)
            return false;
        return await ClaimPotionRewardAsync(session, screen, button, token);
    }

    /// <summary>点药水奖励按钮并等领取完成。true=已领走（按钮消失或整屏关闭），false=仍留在屏上。</summary>
    private static async Task<bool> ClaimPotionRewardAsync(
        RunAutoSession session,
        NRewardsScreen screen,
        NRewardButton button,
        CancellationToken token)
    {
        session.LogDecision($"领取药水奖励 {button.Reward?.GetType().Name}");
        RunAutoSettings.DemoShot("potion");
        await RunAutoSettings.HoldForDemoAsync(token); // 演示定格：奖励屏留屏
        await RunUiHelper.ClickAsync(button, 200);
        MarkProgress();
        // 药水领取不打开子覆盖层：成功 = 按钮被消耗移除（或整屏关闭/跑局结束），
        // 失败（如 TooFull 拒绝）会保留启用按钮 —— 10s 内没移除即视为未领到。
        try
        {
            await RunUiHelper.WaitUntilAsync(
                () => !GodotObject.IsInstanceValid(screen)
                      || !screen.IsVisibleInTree()
                      || !GodotObject.IsInstanceValid(button)
                      || !button.IsInsideTree(),
                token,
                TimeSpan.FromSeconds(10),
                "药水奖励领取未完成");
        }
        catch (RunAutoTimeoutException)
        {
            // 领取没把按钮消耗掉（如又被 TooFull 拒绝），留待外层标记跳过。
            return false;
        }
        return !GodotObject.IsInstanceValid(button) || !button.IsInsideTree();
    }

    private static async Task HandleAsync(bool advanceFromRoom)
    {
        try
        {
            RunAutoSession? session = RunAutoController.Session;
            if (session == null)
                return;
            CancellationToken token = session.CancellationToken;

            // 等战后奖励屏幕出现在覆盖层顶部。
            NRewardsScreen? screen = null;
            await RunUiHelper.WaitUntilAsync(
                () => (screen = NOverlayStack.Instance?.Peek() as NRewardsScreen) != null,
                token,
                TimeSpan.FromSeconds(15),
                "战后奖励屏幕未出现");
            MarkProgress();

            var attemptedButtons = new HashSet<NRewardButton>();
            while (true)
            {
                session = RunAutoController.Session;
                if (session == null
                    || screen == null
                    || !GodotObject.IsInstanceValid(screen)
                    || !screen.IsVisibleInTree())
                {
                    return;
                }

                bool hasPotionSlots =
                    LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState())?.HasOpenPotionSlots ?? false;
                NRewardButton? button = null;
                foreach (NRewardButton candidate in RunUiHelper.FindAll<NRewardButton>(screen))
                {
                    if (!candidate.IsEnabled || attemptedButtons.Contains(candidate))
                        continue;
                    if (candidate.Reward is PotionReward && !hasPotionSlots)
                    {
                        // 栏位满：先按保留价值腾栏（喝果汁/鲜血药水腾栏，其余丢弃），腾出再领；
                        // 新药不值得挤掉最弱持有药水时跳过本次药水奖励。
                        if (await TryMakeRoomAndClaimPotionAsync(session, screen, candidate, token))
                            continue; // 已领取（按钮移除），继续找下一个奖励。
                        // 未领取：标记尝试过，避免死循环（Proceed 收尾时会把它留成"跳过"）。
                        attemptedButtons.Add(candidate);
                        continue;
                    }
                    button = candidate;
                    break;
                }
                if (button == null)
                    break;

                attemptedButtons.Add(button);
                session.LogDecision($"领取奖励 {button.Reward?.GetType().Name ?? "unknown"}");
                RunAutoSettings.DemoShot("reward");
                await RunAutoSettings.HoldForDemoAsync(token); // 演示定格：奖励/选牌入口留屏
                await RunUiHelper.ClickAsync(button, 200);
                MarkProgress();

                // 子覆盖层（如卡牌奖励）打开时，等它关闭、覆盖层顶部回到本奖励屏再继续。
                // 完成信号用 OR 覆盖两条路径（反编译 CardRewardAlternative/NRewardsScreen 确认）：
                // 1) Peek()==screen —— 子屏已关，回到奖励屏。必须用它：卡牌奖励"跳过"的
                //    AfterSelected=EndSelectionAndDoNotCompleteReward，OnSelect 返回 false →
                //    NRewardButton.GetReward 走 Enable() 分支（按钮保留、重新启用），
                //    RewardSkippedFrom 只记入 _skippedRewardButtons 不移除按钮，所以"按钮被消耗"
                //    这类信号永远不会满足；skip 后由本循环点 Proceed 收尾（SkipLocalRewardsSet+Remove）。
                // 2) 按钮移出树 —— 领取成功路径（RewardClaimed → RewardCollectedFrom → RemoveButton
                //    同步 RemoveChild+QueueFree）；最后一个奖励领完时非 terminal 分支还会把整个
                //    NRewardsScreen 移出覆盖层栈，Peek() 不会等于 screen，必须靠它兜底。
                await RunUiHelper.WaitUntilAsync(
                    () => !GodotObject.IsInstanceValid(screen)
                          || !screen.IsVisibleInTree()
                          || !GodotObject.IsInstanceValid(button)
                          || !button.IsInsideTree()
                          || NOverlayStack.Instance?.Peek() == screen,
                    token,
                    TimeSpan.FromSeconds(10),
                    "奖励子屏幕未关闭");
                if (!GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree())
                    return;
            }

            if (advanceFromRoom)
            {
                // 战斗奖励：全部领完点 Proceed 收尾（离房 → RewardsScreenContinuing → 地图选路）。
                NProceedButton? proceed = RunUiHelper.FindFirst<NProceedButton>(screen);
                if (proceed != null && proceed.IsEnabled)
                {
                    session.LogDecision("奖励结算完毕，继续前进");
                    await RunUiHelper.ClickAsync(proceed, 150);
                    MarkProgress();
                }
            }
            else
            {
                // 事件内奖励屏：领完即回——Proceed 属事件本体（离开/下一步由 EventDriver 决策），
                // 战斗流程的 OnCombatVictory 会点 Proceed 提前离房/选路导致事件驱动死亡（133/139 旧实证）。
                // 但若有"跳过/未领"奖励遗留（如栏位满被跳过的药水，OfferCustom 不会自动关屏），
                // 必须点 Proceed(=Skip 剩余) 收尾关屏——否则 OfferCustom 永不结束、事件不完成、
                // 地图不开（133 实证：奖励后 mapOpen=False ×5 轮 30s 重试整局卡死）。
                // 此 Proceed 只跳过剩余奖励并关屏（非 terminal 路径），不会触发离房/选路。
                if (GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree())
                {
                    NProceedButton? skipRemaining = RunUiHelper.FindFirst<NProceedButton>(screen);
                    if (skipRemaining != null && skipRemaining.IsEnabled)
                    {
                        session.LogDecision("事件奖励收尾：有遗留跳过奖励，点跳过剩余关屏");
                        await RunUiHelper.ClickAsync(skipRemaining, 150);
                        MarkProgress();
                        try
                        {
                            await RunUiHelper.WaitUntilAsync(
                                () => !GodotObject.IsInstanceValid(screen)
                                      || !screen.IsVisibleInTree()
                                      || NOverlayStack.Instance?.Peek() != screen,
                                token,
                                TimeSpan.FromSeconds(10),
                                "事件奖励屏跳过未关屏");
                        }
                        catch (RunAutoTimeoutException)
                        {
                            session.LogDecision("事件奖励屏跳过后未自动关屏（留给 EventDriver 兜底）");
                        }
                    }
                }
                session.LogDecision("事件奖励结算完毕，交还事件驱动");
                // 奖励链结束后水晶球"服务收尾"屏回到栈顶(Proceed 可用)：在此直接收尾离开——
                // EventDriver 在长奖励链期间可能停在等待上不再驱动覆盖层（133 实证心跳停 in 奖励期），
                // 由最后一个奖励处理者收尾最可靠；非水晶球场景（栈顶不是水晶球屏）自动跳过。
                await EventOverlayDriver.TryFinalizeCrystalScreenIfTopAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            // 跑局结束，静默退出。
        }
        catch (RunAutoTimeoutException ex)
        {
            RunAutoController.Session?.LogDecision($"奖励屏幕处理超时：{ex.Message}");
        }
        finally
        {
            _active = false;
        }
    }
}



