using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

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

            NRestSiteButton? choice = ChooseOption(buttons, out string reason);
            if (choice == null)
            {
                session.LogDecision("篝火没有合适的选项");
                return;
            }

            session.LogDecision($"篝火：{choice.Option.GetType().Name}（{reason}）");
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

    /// <summary>
    /// 决策：普通位置低血量回血；**幕末 Boss 前**则按"失败风险"判断 —— Boss 战损很大，
    /// 预估失败风险高时不敲牌改回血（用户规则 2026-09-02）；反之风险可控时继续敲牌，
    /// 低血出 Boss，靠 Boss 后 Ancient 补回缺失生命（A2+ 补 80%）。
    /// </summary>
    private static NRestSiteButton? ChooseOption(List<NRestSiteButton> buttons, out string reason)
    {
        reason = "";
        RunState? runState = RunAutoController.Session?.RunState;
        Player? player = runState == null ? null : LocalContext.GetMe(runState);
        bool healWanted = player != null
            && player.Creature.MaxHp > 0
            && ShouldHeal(player, runState!, out reason);

        foreach (NRestSiteButton button in buttons)
        {
            if (button.Option is HealRestSiteOption)
            {
                if (healWanted)
                    return button;
                continue; // 不回血，继续看升级/其他选项。
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
        reason = "无匹配选项，兜底";
        return buttons[0];
    }

    /// <summary>
    /// 估算"该不该回血"（启发式，阈值待跑局数据校准）：
    ///   - 极低血（&lt;35%）任何时候都回；
    ///   - Boss 在 1-2 步内：Boss 战损大，失败风险高就不敲牌改回血。
    ///     风险信号：血量低、没有药水兜底、A10 双 Boss —— 阈值相应抬高；
    ///   - 其余位置维持既有 &lt;60% 回血规则。
    /// </summary>
    private static bool ShouldHeal(Player player, RunState runState, out string reason)
    {
        reason = "";
        float hpFraction = player.Creature.MaxHp > 0
            ? (float)player.Creature.CurrentHp / player.Creature.MaxHp
            : 1f;

        RunActContext.RouteAhead ahead = RunActContext.CaptureAhead(runState);
        // 战士每场战斗胜利回 6 血：回血需求本来就低，阈值整体放宽（适当卖血可接受，用户规则）。
        decimal regen = RunActContext.PassivePostCombatHeal(player);
        bool isIronclad = RunActContext.IsIronclad(player);
        const float ironcladRebate = 0.05f;

        float emergencyFloor = isIronclad ? 0.30f : 0.35f;
        if (hpFraction < emergencyFloor)
        {
            reason = $"极低血 {hpFraction:P0}{(isIronclad ? "（战士有战后回血仍留 30% 保险）" : "")}";
            return true;
        }

        bool doubleBoss = false;
        try
        {
            doubleBoss = RunManager.Instance.HasAscension(AscensionLevel.DoubleBoss);
        }
        catch
        {
            // 非跑局间隙没有 AscensionManager。
        }

        if (ahead.RowsLeftToBoss <= 2)
        {
            // Boss 前最后一两个节点：评估失败风险。
            bool hasInsurance = player.Potions.Any();
            float threshold = 0.55f - (isIronclad ? ironcladRebate : 0f);
            string signals = $"距Boss {ahead.RowsLeftToBoss} 步";
            if (regen > 0)
                signals += "、战后回血";
            if (!hasInsurance)
            {
                threshold += 0.10f;
                signals += "、无药水兜底";
            }
            if (doubleBoss)
            {
                threshold += 0.05f;
                signals += "、双Boss";
            }
            reason = $"Boss前 {signals}：血 {hpFraction:P0} {(hpFraction < threshold ? "<" : "≥")} 阈值 {threshold:P0}";
            return hpFraction < threshold;
        }

        float midThreshold = 0.6f - (isIronclad ? ironcladRebate : 0f);
        reason = $"幕中血量 {hpFraction:P0}（阈值 {midThreshold:P0}）";
        return hpFraction < midThreshold;
    }
}
