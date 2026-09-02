using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver.Run;

/// <summary>
/// 战后卡牌奖励屏幕出现时启动选牌驱动。
/// 挂 NCardRewardSelectionScreen.ShowScreen 的 Postfix，捕获返回的屏幕实例。
/// TestMode 下 ShowScreen 返回 null，不会启动驱动（走原版测试选择器）。
/// </summary>
internal sealed class NCardRewardScreenPatch : IPatchMethod
{
    public static string PatchId => "run_ai_card_reward_selection";
    public static string Description => "全自动跑局：战后卡牌奖励自动选牌/跳过";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(
            typeof(NCardRewardSelectionScreen),
            nameof(NCardRewardSelectionScreen.ShowScreen),
            [typeof(IReadOnlyList<CardCreationResult>), typeof(IReadOnlyList<CardRewardAlternative>)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Postfix(NCardRewardSelectionScreen? __result)
        => CardRewardDriver.OnCardRewardScreenShown(__result);
}
