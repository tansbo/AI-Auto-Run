using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver.Run;

/// <summary>
/// 地图屏幕打开时自动选路。
/// 挂 NMapScreen.Open 的 Postfix：所有回到地图的路径（Neow 后开局、离开房间、
/// 事件 PROCEED 只开地图不触发 RoomExited、商店/篝火/宝箱收尾）都经过 Open，
/// 统一在此触发 MapRouter.RequestRoute，替代散落在各驱动里"等地图打开再选路"的重复等待。
/// isOpenedFromTopBar=true（玩家手动从顶栏开地图）不触发，避免在房间内被选路带离。
/// 延迟 ~350ms 再请求：水晶球/OfferCustom 等"事件自收尾开图"路径里地图刚 Open 时节点图
/// 可能未就绪，同步请求会白等 30s 超时后无人重试（133 实证卡死）；延迟一拍让节点就绪。
/// </summary>
internal sealed class NMapScreenPatch : IPatchMethod
{
    public static string PatchId => "run_ai_map_screen";
    public static string Description => "AI自动跑局：地图打开时自动选路";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(
            typeof(NMapScreen),
            nameof(NMapScreen.Open),
            [typeof(bool)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Postfix(bool isOpenedFromTopBar)
    {
        if (isOpenedFromTopBar)
            return;
        TaskHelper.RunSafely(DelayRouteAsync());
    }

    private static async Task DelayRouteAsync()
    {
        await Task.Delay(350);
        MapRouter.RequestRoute();
    }
}

