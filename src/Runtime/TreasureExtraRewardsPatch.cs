using System;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Rooms;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

/// <summary>
/// 跳过宝箱房的额外奖励生成（TreasureRoom.DoExtraRewardsIfNeeded）。
/// 游戏该路径在 0.111.0 会抛 NullReferenceException，中断 NTreasureRoom.OpenChest，
/// 导致遗物集合永不初始化、宝箱永久卡死（headless 与可见实机均已复现）。
/// 该方法文档契约是"正常情况下什么都不做，仅当有遗物追加奖励时才显示奖励屏"；
/// 跳过完全等价于正常路径，且规避崩溃。代价：带"追加宝箱奖励"遗物的玩家在
/// 该版本会少拿这些额外奖励（该路径本身已损坏，此取舍记录于 DEVELOPMENT_NOTES）。
/// 全模式生效（不再限于无人测试）。
/// </summary>
internal sealed class TreasureExtraRewardsPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_treasure_extra_rewards";
    public static string Description => "跳过宝箱房额外奖励生成（规避 0.111.0 宝箱卡死 NRE）";

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
        __result = Task.CompletedTask;
        Entry.Logger.Info("[CombatSolver] TREASURE_EXTRA_REWARDS_SKIPPED reason=game_nre_workaround");
        return false;
    }
}
