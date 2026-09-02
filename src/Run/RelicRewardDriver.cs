using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Ftue;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 遗物获取驱动：处理两类遗物来源——
/// 1) NChooseARelicSelection 屏幕（Boss 遗物 / 珠宝盒等），按 RelicPickerAI 评分选或跳过；
/// 2) 宝箱房（NTreasureRoom），开箱后拾取宝箱遗物。
/// 宝箱房若有新手引导弹窗（NRelicRewardFtue）会先点掉，避免挡住后续点击。
/// </summary>
internal static class RelicRewardDriver
{
    private static bool _chooseRelicActive;
    private static bool _treasureActive;
    private static bool _loggedTreasureHolderState;

    public static void OnChooseARelicScreenShown(NChooseARelicSelection? screen)
    {
        RunAutoSession? session = RunAutoController.Session;
        if (session == null || !RunAutoSettings.Enabled || screen == null || _chooseRelicActive)
            return;
        _chooseRelicActive = true;
        TaskHelper.RunSafely(HandleChooseRelicAsync(screen));
    }

    public static void OnTreasureRoomEntered()
    {
        RunAutoSession? session = RunAutoController.Session;
        if (session == null || !RunAutoSettings.Enabled || _treasureActive)
            return;
        _treasureActive = true;
        TaskHelper.RunSafely(HandleTreasureAsync());
    }

    private static async Task HandleChooseRelicAsync(NChooseARelicSelection screen)
    {
        try
        {
            RunAutoSession? session = RunAutoController.Session;
            if (session == null)
                return;
            CancellationToken token = session.CancellationToken;

            List<NRelicBasicHolder> holders = [];
            await RunUiHelper.WaitUntilAsync(
                () => (holders = RunUiHelper.FindAll<NRelicBasicHolder>(screen)).Count > 0,
                token,
                TimeSpan.FromSeconds(10),
                "遗物选择未出现");
            if (holders.Count == 0)
                return;

            List<RelicModel> options = [];
            foreach (NRelicBasicHolder holder in holders)
                options.Add(holder.Relic.Model);

            RelicModel? best = RelicPickerAI.PickBest(options);
            if (best == null)
            {
                session.LogDecision("遗物选择：没有值得拿的，跳过");
                NChoiceSelectionSkipButton? skip = RunUiHelper.FindFirst<NChoiceSelectionSkipButton>(screen);
                if (skip != null && skip.IsEnabled)
                    await RunUiHelper.ClickAsync(skip, 200);
            }
            else
            {
                NRelicBasicHolder? target = null;
                foreach (NRelicBasicHolder holder in holders)
                {
                    if (holder.Relic.Model.Equals(best))
                    {
                        target = holder;
                        break;
                    }
                }
                if (target != null)
                {
                    session.LogDecision($"遗物选择：{best.Id.Entry}（{RelicPickerAI.Score(best):0.#} 分）");
                    await RunUiHelper.ClickAsync(target, 200);
                }
            }

            await RunUiHelper.WaitUntilAsync(
                () => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(),
                token,
                TimeSpan.FromSeconds(10),
                "遗物选择屏幕未关闭");
        }
        catch (OperationCanceledException)
        {
            // 跑局结束，静默退出。
        }
        catch (RunAutoTimeoutException ex)
        {
            RunAutoController.Session?.LogDecision($"遗物选择超时：{ex.Message}");
        }
        finally
        {
            _chooseRelicActive = false;
        }
    }

    private static async Task HandleTreasureAsync()
    {
        try
        {
            RunAutoSession? session = RunAutoController.Session;
            if (session == null)
                return;
            CancellationToken token = session.CancellationToken;

            Node root = ((SceneTree)Godot.Engine.GetMainLoop()).Root;
            NTreasureRoom? room = null;
            await RunUiHelper.WaitUntilAsync(
                () => (room = RunUiHelper.FindFirst<NTreasureRoom>(root)) != null,
                token,
                TimeSpan.FromSeconds(15),
                "宝箱房未出现");
            if (room == null)
                return;

            NClickableControl? chest = room.GetNodeOrNull<NClickableControl>("%Chest");
            if (chest == null)
            {
                session.LogDecision("宝箱房：未找到箱子");
                return;
            }

            // 开箱前等遗物同步器就绪：BeginRelicPicking 是 TreasureRoom.EnterInternal 的最后一步，
            // 而 RoomEnteredEvent（AfterRoomEntered hook）在其之前触发——驱动此时点箱会让 OpenChest
            // 跑在遗物生成之前，InitializeRelics 读到 null 的 CurrentRelics → 空宝箱（已实证）。
            await RunUiHelper.WaitUntilAsync(
                () => RunManager.Instance?.TreasureRoomRelicSynchronizer?.CurrentRelics != null,
                token,
                TimeSpan.FromSeconds(15),
                "宝箱遗物同步未就绪");

            _loggedTreasureHolderState = false;
            session.LogDecision("宝箱房：开箱");
            await RunUiHelper.ClickAsync(chest, 300);

            // 开箱后等遗物出现（模型就绪、可点的才算）或空宝箱直接放行 Proceed；
            // 顺带点掉可能弹出的新手引导。首次诊断 dump 一次所有 holder 状态，定位 headless 差异。
            List<NTreasureRoomRelicHolder> holders = [];
            NProceedButton proceedButton = room.ProceedButton;
            await RunUiHelper.WaitUntilAsync(
                () =>
                {
                    DismissRelicFtueIfOpen();
                    List<NTreasureRoomRelicHolder> allHolders = RunUiHelper.FindAll<NTreasureRoomRelicHolder>(room);
                    if (!_loggedTreasureHolderState)
                    {
                        _loggedTreasureHolderState = true;
                        foreach (NTreasureRoomRelicHolder h in allHolders)
                        {
                            bool modelReady = false;
                            try { modelReady = h.Relic?.Model != null; }
                            catch (InvalidOperationException) { modelReady = false; }
                            RunAutoController.Session?.LogDecision(
                                $"宝箱诊断 holder={h.GetPath()} enabled={h.IsEnabled} visible={h.Visible} inTree={h.IsInsideTree()} modelReady={modelReady}");
                        }
                    }
                    holders = allHolders
                        .Where(h => h.IsEnabled && h.Visible && TryGetRelicModel(h) != null)
                        .ToList();
                    return holders.Count > 0 || (proceedButton != null && proceedButton.IsEnabled);
                },
                token,
                TimeSpan.FromSeconds(15),
                "宝箱遗物未出现");

            foreach (NTreasureRoomRelicHolder holder in holders)
            {
                RelicModel? relicModel = TryGetRelicModel(holder);
                if (relicModel == null)
                    continue;
                session.LogDecision($"宝箱遗物：{relicModel.Id.Entry}");
                await RunUiHelper.ClickAsync(holder, 300);
            }

            // 拾取后引导弹窗可能才出现，再点一次。
            DismissRelicFtueIfOpen();

            await RunUiHelper.WaitUntilAsync(
                () => proceedButton != null && proceedButton.IsEnabled,
                token,
                TimeSpan.FromSeconds(15),
                "宝箱 Proceed 未变可用");
            if (proceedButton != null && proceedButton.IsEnabled)
            {
                session.LogDecision("离开宝箱房");
                await RunUiHelper.ClickAsync(proceedButton, 150);
            }

            // 宝箱 Proceed 只打开地图，不触发 RoomExited（与事件/篝火同一缺口），主动请求选路。
            await RunUiHelper.WaitUntilAsync(
                () => NMapScreen.Instance is { IsOpen: true },
                token,
                TimeSpan.FromSeconds(10),
                "宝箱地图未打开");
            session.LogDecision("宝箱完成，地图已打开，请求选路");
            MapRouter.RequestRoute();
        }
        catch (OperationCanceledException)
        {
            // 跑局结束，静默退出。
        }
        catch (RunAutoTimeoutException ex)
        {
            RunAutoController.Session?.LogDecision($"宝箱房处理超时：{ex.Message}");
        }
        finally
        {
            _treasureActive = false;
        }
    }

    /// <summary>模型未初始化时 get_Model 会抛 InvalidOperationException，这里转成 null 让调用方跳过/重试。</summary>
    private static RelicModel? TryGetRelicModel(NTreasureRoomRelicHolder holder)
    {
        try
        {
            return holder.Relic.Model;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void DismissRelicFtueIfOpen()
    {
        if (NModalContainer.Instance?.OpenModal is not NRelicRewardFtue ftue)
            return;
        NButton? confirm = ftue.GetNodeOrNull<NButton>("FtuePopup/FtueConfirmButton");
        if (confirm != null && confirm.IsEnabled)
            confirm.ForceClick();
    }
}
