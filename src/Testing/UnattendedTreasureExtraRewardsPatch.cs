using System;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Rooms;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

/// <summary>
/// 无人测试跳过宝箱房的额外奖励生成（DoExtraRewardsIfNeeded）。
/// 游戏该路径在 headless 下会抛 NullReferenceException，中断 OpenChest，导致遗物集合永不初始化。
/// 该方法文档契约是"正常情况下什么都不做，仅当有遗物追加奖励时才显示奖励屏"，
/// 无人测试开局没有任何这类遗物，直接跳过完全等价，且规避崩溃。
/// 仅 UnattendedTestRunner.IsActive（headless 批量）时生效，可见模式行为不变。
/// </summary>
internal sealed class UnattendedTreasureExtraRewardsPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_unattended_treasure_extra_rewards";
    public static string Description => "无人测试跳过宝箱房额外奖励生成（规避 headless NRE）";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(
            typeof(TreasureRoom),
            nameof(TreasureRoom.DoExtraRewardsIfNeeded),
            Type.EmptyTypes),
    ];

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(ref Task __result)
    {
        if (!UnattendedTestRunner.IsActive)
            return true;
        __result = Task.CompletedTask;
        Entry.Logger.Info("[CombatSolver/Unattended] TREASURE_EXTRA_REWARDS_SKIPPED reason=game_nre_workaround");
        return false;
    }
}
