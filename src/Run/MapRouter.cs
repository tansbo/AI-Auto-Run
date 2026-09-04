using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 地图选路：等地图可前进后按 MapPointType 评分选下一个点并点击，
/// 再等房间进入（RunManager.RoomEntered）完成一轮。
/// 由 RunAutoController 在离开房间 / 奖励结算完毕 / 地图生成时触发，
/// 用 <see cref="_routingActive"/> 去重，一轮只跑一次。
/// </summary>
internal static class MapRouter
{
    private static bool _routingActive;
    private static bool _retryScheduled;
    private static long _routingStartedTick;

    private static TaskCompletionSource? _roomEnteredTcs;

    public static void RequestRoute()
    {
        RunAutoSession? session = RunAutoController.Session;
        if (session == null || !RunAutoSettings.Enabled)
            return;
        if (_routingActive)
        {
            // 上一轮路由在跑（或病态卡住）。内部所有等待都有界（≤30s），超过 60s 视为卡死
            // （如房间进入事件未触发），强制复位让后续请求能继续。
            if (System.Environment.TickCount64 - _routingStartedTick > 60_000)
            {
                Entry.Logger.Warn("[RunAuto] 地图路由超过 60s 未完成（房间进入事件可能未触发），强制复位");
                _routingActive = false;
            }
            else if (!_retryScheduled)
            {
                // 挂起一次延迟重试：上一轮结束后（或看门狗复位后）本请求能补跑，
                // 避免 FakeMerchant 等"事件开图"的路由请求被永久丢弃。
                _retryScheduled = true;
                TaskHelper.RunSafely(RetryRouteAsync());
            }
            return;
        }
        StartRouting();
    }

    private static async Task RetryRouteAsync()
    {
        try
        {
            await Task.Delay(3000);
        }
        finally
        {
            _retryScheduled = false;
        }
        RequestRoute();
    }

    private static void StartRouting()
    {
        _routingActive = true;
        _routingStartedTick = System.Environment.TickCount64;
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

            NMapPoint? target = null;
            await RunUiHelper.WaitUntilAsync(
                () => (target = SelectNext()) != null && target.IsEnabled,
                token,
                TimeSpan.FromSeconds(30),
                "地图可前进节点未出现");
            if (target == null)
                return;

            session.LogDecision(
                $"地图选路 ({target.Point.coord.row},{target.Point.coord.col}) {target.Point.PointType}");

            _roomEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RunManager.Instance.RoomEntered += OnRoomEntered;
            try
            {
                await RunAutoSettings.HoldForDemoAsync(token); // 演示定格：地图+决策条留屏
                await RunUiHelper.ClickAsync(target, 150);
                await RunUiHelper.WaitForTaskAsync(
                    _roomEnteredTcs.Task,
                    token,
                    TimeSpan.FromSeconds(20),
                    "点击地图后未进入房间");
            }
            finally
            {
                RunManager.Instance.RoomEntered -= OnRoomEntered;
                _roomEnteredTcs = null;
            }
        }
        catch (OperationCanceledException)
        {
            // 跑局结束，静默退出。
        }
        catch (RunAutoTimeoutException ex)
        {
            RunAutoController.Session?.LogDecision($"地图选路超时：{ex.Message}");
        }
        finally
        {
            _routingActive = false;
            _routingStartedTick = 0;
        }
    }

    private static void OnRoomEntered()
    {
        _roomEnteredTcs?.TrySetResult();
    }

    /// <summary>
    /// 解析下一个要去的节点：开局选第 0 行，之后选当前节点的子节点。
    /// 分支评分由 <see cref="RoutePlanner"/> 做危险度感知的全路线评估（看当前血量与药水保险），
    /// 找不到图数据时退回旧的"单点类型"贪心。
    /// </summary>
    private static NMapPoint? SelectNext()
    {
        NMapScreen? map = NMapScreen.Instance;
        if (map == null || !map.IsOpen)
            return null;
        List<NMapPoint> points = RunUiHelper.FindAll<NMapPoint>(map);
        if (points.Count == 0)
            return null;

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return null;

        MapPoint? currentPoint = null;
        if (runState.VisitedMapCoords.Count > 0)
        {
            MapCoord lastCoord = runState.VisitedMapCoords[runState.VisitedMapCoords.Count - 1];
            foreach (NMapPoint point in points)
            {
                if (point.Point.coord.Equals(lastCoord))
                {
                    currentPoint = point.Point;
                    break;
                }
            }
            if (currentPoint == null)
                return null;
        }

        MapPoint? best = RoutePlanner.PickBest(runState, currentPoint, out float bestScore);
        if (best != null)
        {
            foreach (NMapPoint point in points)
            {
                if (point.Point.coord.Equals(best.coord) && point.IsEnabled)
                {
                    LogRouteChoice(runState, point, bestScore);
                    return point;
                }
            }
        }

        // 兜底：退回旧"单点类型"贪心（仅当规划器拿不到图/节点时）。
        return LegacyGreedyFallback(points, currentPoint, runState);
    }

    private static void LogRouteChoice(RunState runState, NMapPoint target, float score)
    {
        RunAutoSession? session = RunAutoController.Session;
        if (session == null)
            return;
        Player? player = LocalContext.GetMe(runState);
        float hpFraction = player?.Creature != null && player.Creature.MaxHp > 0
            ? (float)player.Creature.CurrentHp / player.Creature.MaxHp
            : 1f;
        int potions = player?.Potions.Count() ?? 0;
        session.LogDecision(
            $"地图选路 ({target.Point.coord.row},{target.Point.coord.col}) {target.Point.PointType} " +
            $"分支评分={score:F1} 血={hpFraction:P0} 药水={potions}");
    }

    private static NMapPoint? LegacyGreedyFallback(List<NMapPoint> points, MapPoint? currentPoint, RunState runState)
    {
        List<MapCoord> candidates = [];
        if (currentPoint == null)
        {
            foreach (NMapPoint point in points)
            {
                if (point.Point.coord.row == 0)
                    candidates.Add(point.Point.coord);
            }
        }
        else
        {
            foreach (MapPoint child in currentPoint.Children)
                candidates.Add(child.coord);
        }
        if (candidates.Count == 0)
            return null;

        Player? player = LocalContext.GetMe(runState);
        bool lowHp = player != null && player.Creature.MaxHp > 0
            && (float)player.Creature.CurrentHp / player.Creature.MaxHp < 0.5f;

        NMapPoint? best = null;
        float bestScore = float.MinValue;
        foreach (NMapPoint point in points)
        {
            if (!Contains(candidates, point.Point.coord))
                continue;
            float score = ScorePointType(point.Point.PointType, lowHp);
            if (score > bestScore)
            {
                bestScore = score;
                best = point;
            }
        }
        return best;
    }

    private static bool Contains(List<MapCoord> coords, MapCoord coord)
    {
        foreach (MapCoord candidate in coords)
        {
            if (candidate.Equals(coord))
                return true;
        }
        return false;
    }

    private static float ScorePointType(MapPointType type, bool lowHp)
    {
        switch (type)
        {
            case MapPointType.Treasure:
                return 3f;
            case MapPointType.RestSite:
                return lowHp ? 3f : 1f;
            case MapPointType.Elite:
                return lowHp ? -3f : 2f;
            case MapPointType.Shop:
                return 1.5f;
            case MapPointType.Boss:
            case MapPointType.Ancient:
                return 4f;
            default:
                return 0f;
        }
    }
}
