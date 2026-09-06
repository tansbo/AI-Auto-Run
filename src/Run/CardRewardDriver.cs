using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 战后卡牌奖励驱动：屏幕出现后等动画/点击禁用窗口过去，读候选，
/// 交给 <see cref="CardPickerAI"/> 选牌或跳过，然后等屏幕关闭。
/// 由 <see cref="NCardRewardScreenPatch"/> 在 ShowScreen Postfix 里启动。
/// </summary>
internal static class CardRewardDriver
{
    private static readonly HashSet<NCardRewardSelectionScreen> ActiveScreens = [];

    private const int SettleDelayMilliseconds = 450;

    public static void OnCardRewardScreenShown(NCardRewardSelectionScreen? screen)
    {
        if (screen == null || RunAutoController.Session == null || !ActiveScreens.Add(screen))
            return;
        TaskHelper.RunSafely(HandleAsync(screen));
    }

    private static async Task HandleAsync(NCardRewardSelectionScreen screen)
    {
        try
        {
            CancellationToken token = RunAutoController.Session?.CancellationToken ?? CancellationToken.None;
            // 等卡牌飞入动画与 0.35s 的"防止误点"窗口过去，再读取候选。
            await Task.Delay(SettleDelayMilliseconds, token);
            RunAutoSession? session = RunAutoController.Session;
            if (session == null || !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree())
                return;

            List<NGridCardHolder> holders = RunUiHelper.FindAll<NGridCardHolder>(screen);
            List<CardModel> cards = [];
            foreach (NGridCardHolder holder in holders)
            {
                if (holder.CardModel != null)
                    cards.Add(holder.CardModel);
            }

            if (cards.Count == 0)
            {
                session.LogDecision("卡牌奖励屏幕没有可选卡牌，尝试跳过");
                await ClickSkipAsync(screen);
                return;
            }

            Player? player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
            RunState? runState = session.RunState;
            RunTelemetryData telemetry = session.Telemetry;
            RoomType roomType = session.CurrentRoomType;
            CardModel? chosen = null;
            bool forced = false;
            string? forcedAction = null;

            // A/B 强制策略：take 规则优先——任一候选命中 take 就直接强制选它、跳过评分。
            // 受控对照：同一种子只改这一张牌，两局结局差异归因于它（见 RunAutoSettings.TryGetForcedPick）。
            foreach (CardModel candidate in cards)
            {
                if (RunAutoSettings.TryGetForcedPick(candidate.Id.Entry, out bool take) && take)
                {
                    chosen = candidate;
                    forced = true;
                    forcedAction = "take";
                    session.LogDecision($"强制抓牌 {candidate.Id.Entry}（A/B take 规则）");
                    break;
                }
            }

            IReadOnlyList<CardModel> candidates = cards;
            if (chosen == null)
            {
                // 过滤命中 skip 规则的候选，再走正常评分。
                List<CardModel> remaining = cards
                    .Where(static card => !RunAutoSettings.TryGetForcedPick(card.Id.Entry, out bool take) || take)
                    .ToList();
                if (remaining.Count != cards.Count)
                {
                    session.LogDecision(
                        $"按 A/B skip 规则过滤 {cards.Count - remaining.Count} 张牌，剩 {remaining.Count} 张");
                }
                if (remaining.Count == 0)
                {
                    session.LogDecision("全部候选被 A/B skip 规则过滤，跳过卡牌奖励");
                    telemetry.RecordPick(runState, roomType, cards, null, true, true, "skip", 0f);
                    await ClickSkipAsync(screen);
                    return;
                }
                candidates = remaining;
                chosen = CardPickerAI.PickBest(candidates, player, runState);
            }

            if (chosen == null)
            {
                // 事件内卡牌奖励（THE_FUTURE_OF_POTIONS/水晶球等 OfferCustom 屏）：跳过会触发
                // PostAlternateCardRewardAction.EndSelectionAndDoNotCompleteReward → RewardsSet 不完成、
                // 外层奖励屏滞留 → EventDriver 收不到事件体 → 整局卡死（150 实证）。
                // 事件给的卡通常是事件专属/保留价值高，评分不足时退而选评分最高者，避免走死路。
                if (roomType == RoomType.Event && candidates.Count > 0)
                {
                    chosen = candidates
                        .OrderByDescending(card => CardPickerAI.Evaluate(card, DeckContext.From(player, runState)))
                        .First();
                    session.LogDecision($"事件卡牌奖励评分均不足，退而选评分最高 {chosen.Id.Entry}");
                }
                else
                {
                    session.LogDecision("卡牌奖励评分不足，跳过");
                    telemetry.RecordPick(runState, roomType, cards, null, true, false, null, 0f);
                    await ClickSkipAsync(screen);
                }
            }
            else
            {
                NGridCardHolder? holder = null;
                foreach (NGridCardHolder candidate in holders)
                {
                    if (candidate.CardModel == chosen)
                    {
                        holder = candidate;
                        break;
                    }
                }
                if (holder == null)
                {
                    session.LogDecision($"选中的牌 {chosen.Id.Entry} 不在屏幕上，改跳过");
                    telemetry.RecordPick(runState, roomType, cards, chosen, true, forced, forcedAction, 0f);
                    await ClickSkipAsync(screen);
                    return;
                }

                float chosenScore = CardPickerAI.Evaluate(chosen, DeckContext.From(player, runState));
                telemetry.RecordPick(runState, roomType, cards, chosen, false, forced, forcedAction, chosenScore);
                session.PickedCardIds.Add(chosen.Id.ToString());
                session.LogDecision(
                    $"选牌 {chosen.Id.Entry} rarity={chosen.Rarity} cost={chosen.EnergyCost.Canonical} " +
                    $"type={chosen.Type}" + (forced ? $" forced={forcedAction}" : string.Empty));
                holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
            }

            await RunUiHelper.WaitUntilAsync(
                () => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(),
                token,
                TimeSpan.FromSeconds(10),
                "卡牌奖励屏幕在选择后未关闭");
        }
        catch (OperationCanceledException)
        {
            // 跑局结束，静默退出。
        }
        catch (RunAutoTimeoutException ex)
        {
            // 事件内 OfferCustom 单卡奖励（THE_FUTURE_OF_POTIONS 等）：选卡/跳过后游戏不自动关
            // 残留 NCardRewardSelectionScreen（153/150 实证：depth=2 卡死整局）。选/跳已生效
            // （卡已入组/跳过记录），显式移除残留屏交还事件驱动；战斗后场景仍由游戏正常关闭，
            // 走到这里是事件特例，移除安全。
            RunAutoSession? s = RunAutoController.Session;
            if (s != null && s.CurrentRoomType == RoomType.Event
                && GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree())
            {
                s.LogDecision($"事件卡牌屏超时未自关，显式移除残留屏（{ex.Message}）");
                NOverlayStack.Instance?.Remove(screen);
            }
            else
            {
                s?.LogDecision($"卡牌奖励处理超时：{ex.Message}");
            }
        }
        finally
        {
            ActiveScreens.Remove(screen);
        }
    }

    /// <summary>点击第一个可用的替代项按钮（正常是索引 0 的 Skip）。</summary>
    private static async Task ClickSkipAsync(NCardRewardSelectionScreen screen)
    {
        if (!GodotObject.IsInstanceValid(screen))
            return;
        NCardRewardAlternativeButton? skip = null;
        foreach (NCardRewardAlternativeButton button in RunUiHelper.FindAll<NCardRewardAlternativeButton>(screen))
        {
            if (button.IsEnabled)
            {
                skip = button;
                break;
            }
        }
        if (skip != null)
            await RunUiHelper.ClickAsync(skip, 150);
    }
}
