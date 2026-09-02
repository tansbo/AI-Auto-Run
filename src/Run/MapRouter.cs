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

    private static TaskCompletionSource? _roomEnteredTcs;

    public static void RequestRoute()
    {
        RunAutoSession? session = RunAutoController.Session;
        if (session == null || !RunAutoSettings.Enabled || _routingActive)
            return;
        _routingActive = true;
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
        }
    }

    private static void OnRoomEntered()
    {
        _roomEnteredTcs?.TrySetResult();
    }

    /// <summary>解析下一个要去的节点：首幕选第 0 行，之后选当前节点的子节点中评分最高的。</summary>
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
        bool lowHp = IsLowHp(runState);

        List<MapCoord> candidates = [];
        if (runState.VisitedMapCoords.Count == 0)
        {
            foreach (NMapPoint point in points)
            {
                if (point.Point.coord.row == 0)
                    candidates.Add(point.Point.coord);
            }
        }
        else
        {
            MapCoord lastCoord = runState.VisitedMapCoords[runState.VisitedMapCoords.Count - 1];
            NMapPoint? current = null;
            foreach (NMapPoint point in points)
            {
                if (point.Point.coord.Equals(lastCoord))
                {
                    current = point;
                    break;
                }
            }
            if (current == null)
                return null;
            foreach (MapPoint child in current.Point.Children)
                candidates.Add(child.coord);
        }

        if (candidates.Count == 0)
            return null;

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

    private static bool IsLowHp(RunState runState)
    {
        Player? player = LocalContext.GetMe(runState);
        return player != null && player.Creature.MaxHp > 0
            && (float)player.Creature.CurrentHp / player.Creature.MaxHp < 0.5f;
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
