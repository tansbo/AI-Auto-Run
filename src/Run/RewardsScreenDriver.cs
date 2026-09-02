using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
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
                        continue;
                    button = candidate;
                    break;
                }
                if (button == null)
                    break;

                attemptedButtons.Add(button);
                session.LogDecision($"领取奖励 {button.Reward?.GetType().Name ?? "unknown"}");
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
