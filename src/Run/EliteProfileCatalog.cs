using System;
using System.Collections.Generic;

namespace CombatSolver.Run;

/// <summary>
/// 精英需求画像表（用户规则 2026-09-06；构成来自 decomp EncounterModel + 敌人组，
/// 精确出招表后续阶段逐一核对）。用途：路线/药水保留/卡组取向的前方窗口输入。
/// 标签语义：
///   NeedsHighSingle  需要高伤害单体（通常单个高 HP/单回合大数目标，要求斩杀/爆发）
///   NeedsAoe         需要高伤害 AOE（多目标同场，小怪群）
///   NeedsDefense     需要足够防御（承伤压力/持续性输出；初值待数据校准）
/// </summary>
internal sealed class EliteProfile
{
    public required string EncounterId { get; init; }      // ENCOUNTER.XXX_ELITE
    public required bool NeedsHighSingle { get; init; }
    public required bool NeedsAoe { get; init; }
    public required bool NeedsDefense { get; init; }
    public required int ExpectedEnemies { get; init; }     // 敌人数（按固定构成，Slots/GenerateMonsters）
    public required string Note { get; init; }
}

internal static class EliteProfileCatalog
{
    /// <summary>enemy 类型名 → 遭遇条目标识前缀（用于反向补全）。</summary>
    public static readonly Dictionary<string, string> EncounterEntryByClass = new(StringComparer.Ordinal)
    {
        ["BygoneEffigyElite"] = "ENCOUNTER.BYGONE_EFFIGY_ELITE",
        ["ByrdonisElite"] = "ENCOUNTER.BYRDONIS_ELITE",
        ["PhrogParasiteElite"] = "ENCOUNTER.PHROG_PARASITE_ELITE",
        ["PhantasmalGardenersElite"] = "ENCOUNTER.PHANTASMAL_GARDENERS_ELITE",
        ["SkulkingColonyElite"] = "ENCOUNTER.SKULKING_COLONY_ELITE",
        ["TerrorEelElite"] = "ENCOUNTER.TERROR_EEL_ELITE",
        ["DecimillipedeElite"] = "ENCOUNTER.DECIMILLIPEDE_ELITE",
        ["EntomancerElite"] = "ENCOUNTER.ENTOMANCER_ELITE",
        ["InfestedPrismsElite"] = "ENCOUNTER.INFESTED_PRISMS_ELITE",
        ["SoulNexusElite"] = "ENCOUNTER.SOUL_NEXUS_ELITE",
        ["MechaKnightElite"] = "ENCOUNTER.MECHA_KNIGHT_ELITE",
        ["KnightsElite"] = "ENCOUNTER.KNIGHTS_ELITE",
    };

    /// <summary>
    /// 画像（provisional：构成=decomp 已核；单体/AOE/防御需求=构成+用户经验，出招表待逐一核对）。
    /// 密林 Overgrowth：BygoneEffigy(1)、Byrdonis(1) → 单体；PhrogParasite+4×Wriggler → AOE/群。
    /// 暗港 Underdocks：4×PhantasmalGardener → 多目标；SkulkingColony(1) → 防御/持久；TerrorEel(1) → 单体。
    /// 蜂巢 Hive / 荣耀 Glory 条目先留构成骨架，需求标签待补。
    /// </summary>
    public static IReadOnlyList<EliteProfile> All { get; } = new[]
    {
        new EliteProfile { EncounterId = "ENCOUNTER.BYGONE_EFFIGY_ELITE", NeedsHighSingle = true, NeedsAoe = false, NeedsDefense = true, ExpectedEnemies = 1, Note = "单体高HP精英(密林)；单体爆发+防御" },
        new EliteProfile { EncounterId = "ENCOUNTER.BYRDONIS_ELITE", NeedsHighSingle = true, NeedsAoe = false, NeedsDefense = true, ExpectedEnemies = 1, Note = "单体精英(密林)；单体高伤害" },
        new EliteProfile { EncounterId = "ENCOUNTER.PHROG_PARASITE_ELITE", NeedsHighSingle = false, NeedsAoe = true, NeedsDefense = true, ExpectedEnemies = 5, Note = "Phrog+4×Wriggler(密林群战)；需要高伤害AOE" },
        new EliteProfile { EncounterId = "ENCOUNTER.PHANTASMAL_GARDENERS_ELITE", NeedsHighSingle = false, NeedsAoe = true, NeedsDefense = true, ExpectedEnemies = 4, Note = "4×PhantasmalGardener(暗港多目标)；需要AOE/清理" },
        new EliteProfile { EncounterId = "ENCOUNTER.SKULKING_COLONY_ELITE", NeedsHighSingle = false, NeedsAoe = false, NeedsDefense = true, ExpectedEnemies = 1, Note = "单体(暗港)；疑似防御/持久压力，待核对出招" },
        new EliteProfile { EncounterId = "ENCOUNTER.TERROR_EEL_ELITE", NeedsHighSingle = true, NeedsAoe = false, NeedsDefense = true, ExpectedEnemies = 1, Note = "单体(暗港)；高伤害单体需求" },
        // 蜂巢 Hive / 荣耀 Glory（构成=decomp 已核；需求标签 provisional，出招表待核对）
        new EliteProfile { EncounterId = "ENCOUNTER.DECIMILLIPEDE_ELITE", NeedsHighSingle = true, NeedsAoe = false, NeedsDefense = true, ExpectedEnemies = 3, Note = "3 段节肢(蜂巢链状)；单体斩杀段需求" },
        new EliteProfile { EncounterId = "ENCOUNTER.ENTOMANCER_ELITE", NeedsHighSingle = true, NeedsAoe = true, NeedsDefense = true, ExpectedEnemies = 1, Note = "Entomancer(蜂巢，疑似召唤，需清场能力待核)" },
        new EliteProfile { EncounterId = "ENCOUNTER.INFESTED_PRISMS_ELITE", NeedsHighSingle = false, NeedsAoe = true, NeedsDefense = true, ExpectedEnemies = 1, Note = "InfestedPrism(蜂巢，疑似多生成物，待核)" },
        new EliteProfile { EncounterId = "ENCOUNTER.SOUL_NEXUS_ELITE", NeedsHighSingle = true, NeedsAoe = false, NeedsDefense = true, ExpectedEnemies = 1, Note = "SoulNexus(荣耀单体)" },
        new EliteProfile { EncounterId = "ENCOUNTER.MECHA_KNIGHT_ELITE", NeedsHighSingle = true, NeedsAoe = false, NeedsDefense = true, ExpectedEnemies = 1, Note = "MechaKnight(荣耀单体/装甲，待核)" },
        new EliteProfile { EncounterId = "ENCOUNTER.KNIGHTS_ELITE", NeedsHighSingle = false, NeedsAoe = true, NeedsDefense = true, ExpectedEnemies = 3, Note = "3 骑士 Flail/Spectral/Magi(荣耀多目标)；AOE 或定点优先杀待核" },
    };

    /// <summary>按遭遇条目标识或类名取画像（未收录返回 null）。
    /// 实机 Id.Entry 是裸名(如 BYRDONIS_ELITE)，目录键带 ENCOUNTER. 前缀——两种都兼容。</summary>
    public static EliteProfile? Find(string encounterIdOrClass)
    {
        string key = encounterIdOrClass ?? string.Empty;
        string prefixed = key.StartsWith("ENCOUNTER.", StringComparison.OrdinalIgnoreCase)
            ? key
            : "ENCOUNTER." + key;
        string classKey = EncounterEntryByClass.TryGetValue(key, out string? mapped) ? mapped : key;
        foreach (EliteProfile profile in All)
        {
            if (profile.EncounterId.Equals(key, StringComparison.OrdinalIgnoreCase)
                || profile.EncounterId.Equals(prefixed, StringComparison.OrdinalIgnoreCase)
                || profile.EncounterId.Equals(classKey, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }
        return null;
    }

    public static string Describe(string encounterIdOrClass)
    {
        EliteProfile? profile = Find(encounterIdOrClass);
        return profile == null ? "（画像未收录）" : $"{profile.ExpectedEnemies}敌 ST={profile.NeedsHighSingle} AOE={profile.NeedsAoe} DEF={profile.NeedsDefense}";
    }
}
