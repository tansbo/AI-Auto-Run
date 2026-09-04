using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver.Run;

/// <summary>
/// 遗物选择屏幕出现时启动遗物驱动。
/// 挂 NChooseARelicSelection.ShowScreen 的 Postfix，捕获返回的屏幕实例。
/// 覆盖 Boss 遗物 / 珠宝盒等所有走该屏幕的遗物选择。TestMode 下返回 null，不会启动。
/// </summary>
internal sealed class NChooseARelicScreenPatch : IPatchMethod
{
    public static string PatchId => "run_ai_choose_relic_selection";
    public static string Description => "AI自动跑局：遗物选择自动选取/跳过";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(
            typeof(NChooseARelicSelection),
            nameof(NChooseARelicSelection.ShowScreen),
            [typeof(IReadOnlyList<RelicModel>)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Postfix(NChooseARelicSelection? __result)
        => RelicRewardDriver.OnChooseARelicScreenShown(__result);
}

