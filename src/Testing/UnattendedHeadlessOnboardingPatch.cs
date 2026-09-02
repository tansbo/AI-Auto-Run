using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Unlocks;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

/// <summary>
/// 无人测试 headless 下的新手引导（onboarding）旁路。
/// 隔离档案是"首次游玩"，游戏会跑 FTUE 引导：地图教学弹窗挡住点击、首局强制固定房间顺序，
/// 导致整局模式卡在首个房间无法前进。这里把两个门禁在无人测试期间关掉：
///   1. SeenFtue 恒返回 true → 所有 FTUE 教学弹窗不再创建（含 map_select / relic / rest / merchant 等）。
///   2. ApplyActDiscoveryOrderModifications 跳过 → 首局不再"按固定顺序呈现房间"，走正常随机地图。
/// </summary>
internal sealed class HeadlessSeenFtuePatch : IPatchMethod
{
    public static string PatchId => "combat_solver_unattended_headless_seen_ftue";
    public static string Description => "无人测试下所有 FTUE 教学视为已看过，不再弹出";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(
            typeof(ProgressSaveManager),
            nameof(ProgressSaveManager.SeenFtue),
            [typeof(string)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(ref bool __result)
    {
        if (!UnattendedTestRunner.IsActive)
            return true;
        __result = true;
        return false;
    }
}

/// <summary>无人测试下跳过"首次游玩固定房间顺序"，让整局冒烟/训练走正常随机地图。</summary>
internal sealed class HeadlessFirstRunOrderPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_unattended_headless_first_run_order";
    public static string Description => "无人测试下不应用首局固定房间顺序";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(
            typeof(Overgrowth),
            // 私有方法，nameof 跨程序集不可用，用字面量；RitsuLib 解析器按 NonPublic 查找。
            "ApplyActDiscoveryOrderModifications",
            [typeof(UnlockState)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static bool Prefix()
    {
        if (!UnattendedTestRunner.IsActive)
            return true;
        return false;
    }
}
