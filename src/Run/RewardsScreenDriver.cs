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

    public static void OnCombatVictory()
    {
        RunAutoSession? session = RunAutoController.Session;
        if (session == null || !RunAutoSettings.Enabled || _active)
            return;
        _active = true;
        TaskHelper.RunSafely(HandleAsync());
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
        await RunAutoSettings.HoldForDemoAsync(token); // 演示定格：奖励屏留屏
        await RunUiHelper.ClickAsync(button, 200);
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

    private static async Task HandleAsync()
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
                await RunAutoSettings.HoldForDemoAsync(token); // 演示定格：奖励/选牌入口留屏
                await RunUiHelper.ClickAsync(button, 200);

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

            NProceedButton? proceed = RunUiHelper.FindFirst<NProceedButton>(screen);
            if (proceed != null && proceed.IsEnabled)
            {
                session.LogDecision("奖励结算完毕，继续前进");
                await RunUiHelper.ClickAsync(proceed, 150);
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
