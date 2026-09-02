using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver.Run;

/// <summary>
/// A/B 种子覆盖：visible 模式下用设置页的"种子覆盖"改写新建跑局的种子。
/// 种子经 <see cref="RunState.CreateForNewRun"/> 进入 RunRngSet，决定整局路线/发牌/先古，
/// 因此同一种子配不同抓牌策略重放两次即受控对照。headless 批量模式不走这里
/// （ScenarioBuilder 直接把 request.Seed 传给 StartNewSingleplayerRun），且批处理会先清空 SeedOverride。
/// 仅在标准单人局覆盖，避免误伤每日挑战/多人/联机。
/// </summary>
internal sealed class RunStartSeedPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_run_start_seed_override";
    public static string Description => "用设置中的种子覆盖新建跑局种子（A/B 重放训练）";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(
            typeof(RunState),
            nameof(RunState.CreateForNewRun),
            [
                typeof(IReadOnlyList<Player>),
                typeof(IReadOnlyList<ActModel>),
                typeof(IReadOnlyList<ModifierModel>),
                typeof(GameMode),
                typeof(int),
                typeof(string),
            ]),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Prefix(
        IReadOnlyList<Player> players,
        GameMode gameMode,
        ref string seed)
    {
        if (string.IsNullOrEmpty(RunAutoSettings.SeedOverride))
            return;
        if (gameMode != GameMode.Standard || players.Count != 1)
            return;
        string requested = RunAutoSettings.SeedOverride;
        if (string.Equals(seed, requested, System.StringComparison.Ordinal))
            return;
        Entry.Logger.Info(
            $"[RunAuto] SEED_OVERRIDE original={seed} override={requested} game_mode={gameMode} players={players.Count}");
        seed = requested;
    }
}
