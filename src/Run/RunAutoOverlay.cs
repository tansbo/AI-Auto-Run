using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 跑局 AI 状态覆盖层：屏幕底部一行小字，显示当前幕/层、阶段和最近决策。
/// 与战斗求解器覆盖层（左上）错开，避免互相遮挡。跑局结束时隐藏。
/// 只做展示，不含任何交互。
/// </summary>
internal static class RunAutoOverlay
{
    private const string LayerName = "RunAutoOverlay";

    private static CanvasLayer? _layer;
    private static PanelContainer? _panel;
    private static Label? _label;

    public static void Update(RunAutoSession? session)
    {
        if (session == null)
        {
            Hide();
            return;
        }
        if (!RunAutoSettings.Enabled)
            return;
        Node? host = NGame.Instance;
        if (host == null || !GodotObject.IsInstanceValid(host))
        {
            MainLoop? mainLoop = Godot.Engine.GetMainLoop();
            host = mainLoop is SceneTree tree ? tree.Root : null;
            if (host == null)
                return;
        }
        EnsureCreated(host);
        if (_label == null || _panel == null)
            return;
        _label.Text = BuildStatusText(session);
        _label.Visible = true;
        _panel.Visible = true;
        if (_layer != null)
            _layer.Visible = true;
    }

    public static void Hide()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
            _layer.Visible = false;
    }

    private static string BuildStatusText(RunAutoSession session)
    {
        string act = "·";
        int? actIndex = session.RunState?.CurrentActIndex;
        int? floor = session.RunState?.TotalFloor;
        if (actIndex is { } actValue)
            act = actValue + 1 >= 0 ? $"第 {actValue + 1} 幕" : "·";
        string floorText = floor is { } floorValue ? $"{floorValue} 层" : "";
        string phase = session.Phase switch
        {
            RunAutoPhase.InCombat => "战斗中",
            RunAutoPhase.RewardsPending => "奖励结算",
            RunAutoPhase.MapPending => "地图选路",
            RunAutoPhase.NonCombatRoom => "房间处理",
            _ => "待命",
        };
        string? decision = session.LastDecision;
        string decisionText = string.IsNullOrEmpty(decision) ? "" : $"｜{decision}";
        return $"全自动跑局｜{act}{floorText}｜{phase}{decisionText}";
    }

    private static void EnsureCreated(Node host)
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
            return;
        CanvasLayer layer = new()
        {
            Name = LayerName,
            Layer = 121,
        };
        PanelContainer panel = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        panel.AddThemeStyleboxOverride(
            "panel",
            SolverUiTokens.CreateBox(
                SolverUiTokens.Palette.Background,
                SolverUiTokens.Palette.BorderSubtle,
                SolverUiTokens.Radius.Medium,
                SolverUiTokens.Spacing.Sm,
                SolverUiTokens.Spacing.Sm,
                shadow: true));
        _label = SolverUiTokens.CreateLabel(
            string.Empty,
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary,
            FontType.Bold);
        _label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _label.CustomMinimumSize = new Vector2(0, 24);
        panel.AddChild(_label);
        layer.AddChild(panel);
        host.AddChild(layer);
        _layer = layer;
        _panel = panel;
        AnchorBottomCenter(host);
    }

    private static void AnchorBottomCenter(Node host)
    {
        if (_panel == null || host?.GetViewport() is not { } viewport)
            return;
        Vector2 viewportSize = viewport.GetVisibleRect().Size;
        _panel.OffsetLeft = Mathf.Max(8f, (viewportSize.X - _panel.GetCombinedMinimumSize().X) * 0.5f - 60f);
        _panel.OffsetRight = _panel.OffsetLeft + _panel.GetCombinedMinimumSize().X + 120f;
        _panel.OffsetTop = viewportSize.Y - _panel.GetCombinedMinimumSize().Y - 16f;
        _panel.OffsetBottom = viewportSize.Y - 12f;
    }
}
