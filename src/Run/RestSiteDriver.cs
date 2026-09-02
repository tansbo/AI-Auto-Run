using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace CombatSolver.Run;

/// <summary>
/// 篝火驱动：低血量先回血，否则优先升级（Smith），再力量/挖矿/烹饪，
/// 点选后处理可能的升级选牌覆盖层，最后点 Proceed 离开。
/// 由 RunAutoController 在进入 RestSite 房间时启动。
/// </summary>
internal static class RestSiteDriver
{
    private static bool _active;

    public static void OnRoomEntered()
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

            Node root = ((SceneTree)Godot.Engine.GetMainLoop()).Root;
            NRestSiteRoom? room = null;
            await RunUiHelper.WaitUntilAsync(
                () => (room = RunUiHelper.FindFirst<NRestSiteRoom>(root)) != null,
                token,
                TimeSpan.FromSeconds(15),
                "篝火房间未出现");
            if (room == null)
                return;

            List<NRestSiteButton> buttons = [];
            foreach (NRestSiteButton button in RunUiHelper.FindAll<NRestSiteButton>(room))
            {
                if (button.Option.IsEnabled)
                    buttons.Add(button);
            }
            if (buttons.Count == 0)
            {
                session.LogDecision("篝火没有可用选项");
                return;
            }

            NRestSiteButton? choice = ChooseOption(buttons);
            if (choice == null)
            {
                session.LogDecision("篝火没有合适的选项");
                return;
            }

            session.LogDecision($"篝火：{choice.Option.GetType().Name}");
            await RunUiHelper.ClickAsync(choice, 200);

            // 等选项生效：要么覆盖层打开（升级选牌），要么 Proceed 变可用。
            await RunUiHelper.WaitUntilAsync(
                () =>
                {
                    if (NOverlayStack.Instance is { ScreenCount: > 0 })
                        return true;
                    NProceedButton proceed = room.ProceedButton;
                    return proceed != null && proceed.IsEnabled;
                },
                token,
                TimeSpan.FromSeconds(10),
                "篝火选项未生效");

            if (NOverlayStack.Instance?.Peek() is NDeckUpgradeSelectScreen upgrade)
            {
                session.LogDecision("篝火升级：自动选牌升级");
                await SmithUpgradeDriver.HandleAsync(upgrade, token);
            }

            NProceedButton proceedButton = room.ProceedButton;
            await RunUiHelper.WaitUntilAsync(
                () => proceedButton != null && proceedButton.IsEnabled,
                token,
                TimeSpan.FromSeconds(10),
                "篝火 Proceed 未变可用");
            if (proceedButton != null && proceedButton.IsEnabled)
            {
                session.LogDecision("离开篝火");
                await RunUiHelper.ClickAsync(proceedButton, 150);
            }

            // 篝火 Proceed 只打开地图，不触发 RoomExited 房间退出事件（与事件房间同一缺口），
            // MapRouter 不会被 RunAutoController 触发，主动请求选路进下一房间。
            await RunUiHelper.WaitUntilAsync(
                () => NMapScreen.Instance is { IsOpen: true },
                token,
                TimeSpan.FromSeconds(10),
                "篝火地图未打开");
            session.LogDecision("篝火完成，地图已打开，请求选路");
            MapRouter.RequestRoute();
        }
        catch (OperationCanceledException)
        {
            // 跑局结束，静默退出。
        }
        catch (RunAutoTimeoutException ex)
        {
            RunAutoController.Session?.LogDecision($"篝火处理超时：{ex.Message}");
        }
        finally
        {
            _active = false;
        }
    }

    /// <summary>低血量先回血；否则升级；再否则力量成长/挖矿/烹饪；兜底选第一个可用项。</summary>
    private static NRestSiteButton? ChooseOption(List<NRestSiteButton> buttons)
    {
        Player? player = LocalContext.GetMe(RunAutoController.Session?.RunState);
        bool lowHp = player != null
            && player.Creature.MaxHp > 0
            && (float)player.Creature.CurrentHp / player.Creature.MaxHp < 0.6f;

        foreach (NRestSiteButton button in buttons)
        {
            if (button.Option is HealRestSiteOption)
            {
                if (lowHp)
                    return button;
                continue; // 满血不回血，继续看升级/其他选项。
            }
        }
        foreach (NRestSiteButton button in buttons)
        {
            if (button.Option is SmithRestSiteOption)
                return button;
        }
        foreach (NRestSiteButton button in buttons)
        {
            if (button.Option is LiftRestSiteOption or DigRestSiteOption or CookRestSiteOption or KindleRestSiteOption)
                return button;
        }
        foreach (NRestSiteButton button in buttons)
        {
            if (button.Option is MendRestSiteOption or CloneRestSiteOption or HatchRestSiteOption)
                return button;
        }
        return buttons[0];
    }
}
