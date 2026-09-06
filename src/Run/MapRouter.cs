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
    private static int _noNodeRetries;      // 同一段"选不到前进节点"的重试数（防死循环）
    private static long _lastNullLogTick;   // SelectNext 空因诊断日志节流

    private static TaskCompletionSource? _roomEnteredTcs;

    public static void RequestRoute()
    {
        RouteInternal(resetRetries: true);
    }

    private static void RouteInternal(bool resetRetries)
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
                // 避免 FakeMerchant/水晶球等"事件开图"的路由请求被永久丢弃。
                _retryScheduled = true;
                TaskHelper.RunSafely(RetryAfterAsync(3000, resetRetries: false));
            }
            return;
        }
        if (resetRetries)
            _noNodeRetries = 0;
        StartRouting();
    }

    private static async Task RetryAfterAsync(int delayMs, bool resetRetries)
    {
        try
        {
            await Task.Delay(delayMs);
        }
        finally
        {
            _retryScheduled = false;
        }
        RouteInternal(resetRetries);
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

            // 房间过渡帧排空：游戏 QueueFreeSafely 把 NodePool.Free 排到下一帧（CallDeferred），
            // 我们"奖励→立即离房→马上进下一房"会让上一房间的延迟释放没执行就开新房间 UI
            // （建卡从同一 NodePool 取用）→ 同一 NCard 被释放两次（122 等 ≥5 局卡死）。
            // 先停一拍让 deferred 队列跑完，再点地图节点。
            await Task.Delay(150, token);

            // 等可前进节点；每 ~5s 打印一次等待状态（目标存在但未启用 / 地图未开等），
            // 供"选路超时无日志"卡死定位（水晶球自收尾开图后 133 实证）。
            NMapPoint? target = null;
            bool targetReady = false;
            for (int tick = 0; tick < 60; tick++)
            {
                target = SelectNext();
                if (target != null && target.IsEnabled)
                {
                    targetReady = true;
                    break;
                }
                if (tick % 10 == 9)
                {
                    string state = target != null
                        ? $"目标({target.Point.coord.row},{target.Point.coord.col}) {target.Point.PointType} 未启用"
                        : "无可前进节点(空因见上)";
                    RunAutoController.Session?.LogDecision(
                        $"地图选路等待 {tick + 1}/60：{state} mapOpen={NMapScreen.Instance?.IsOpen}");
                }
                await Task.Delay(500, token);
            }
            if (!targetReady)
            {
                bool mapOpenNow = NMapScreen.Instance is { IsOpen: true };
                if (target == null)
                    RunAutoController.Session?.LogDecision(
                        $"地图选路：30s 无可前进节点，放弃本轮（mapOpen={mapOpenNow}）");
                else
                    RunAutoController.Session?.LogDecision(
                        "地图选路：30s 目标始终未启用，放弃本轮");
                // 有界重试：地图刚开/稍后才开时给后续请求机会（133 实证 30s 白等后无人接力卡死）。
                if (_noNodeRetries < 5 && !_retryScheduled)
                {
                    _noNodeRetries++;
                    RunAutoController.Session?.LogDecision($"地图选路：稍后重试（{_noNodeRetries}/5）");
                    _retryScheduled = true;
                    _ = TaskHelper.RunSafely(RetryAfterAsync(mapOpenNow ? 1500 : 3000, resetRetries: false));
                }
                return;
            }
            _noNodeRetries = 0; // 成功选到节点：本轮"无节点"重试预算复位。

            session.LogDecision(
                $"地图选路 ({target.Point.coord.row},{target.Point.coord.col}) {target.Point.PointType}");

            _roomEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RunManager.Instance.RoomEntered += OnRoomEntered;
            try
            {
                RunAutoSettings.DemoShot("map");
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
    /// 返回 null 且地图开着时，按原因节流记诊断日志，供"选不到节点卡死"定位（水晶球等事件后）。
    /// </summary>
    private static NMapPoint? SelectNext()
    {
        NMapScreen? map = NMapScreen.Instance;
        if (map == null || !map.IsOpen)
            return NullWait("地图未打开");
        List<NMapPoint> points = RunUiHelper.FindAll<NMapPoint>(map);
        if (points.Count == 0)
            return NullWait("无地图节点");

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return NullWait("runState 空");

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
                return NullWait($"当前坐标({lastCoord.row},{lastCoord.col})不在可见地图");
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
        NMapPoint? fallback = LegacyGreedyFallback(points, currentPoint, runState);
        if (fallback == null)
        {
            string cur = currentPoint == null
                ? "无(开局)"
                : $"({currentPoint.coord.row},{currentPoint.coord.col})";
            NullWait($"无前进候选（规划器空+贪心空，当前={cur}）");
        }
        return fallback;
    }

    /// <summary>地图开着却选不到节点时的节流诊断日志（≥3s 一条），避免刷屏。</summary>
    private static NMapPoint? NullWait(string reason)
    {
        if (NMapScreen.Instance is { IsOpen: true }
            && System.Environment.TickCount64 - _lastNullLogTick > 3000)
        {
            _lastNullLogTick = System.Environment.TickCount64;
            RunAutoController.Session?.LogDecision($"地图选路等待：{reason}");
        }
        return null;
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

