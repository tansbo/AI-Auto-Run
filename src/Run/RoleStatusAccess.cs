using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Run;

/// <summary>
/// 职业 × 状态产出指数（真实池统计 + 惰性静态缓存；主线程首次访问时构建一次）：
/// 对 <see cref="ModelDb.AllCharacters"/> 每个角色的 <c>CharacterModel.CardPool.AllCards</c>
/// （decomp 依据：ModelDb.cs L145 AllCharacters / CharacterModel.cs CardPool /
/// CardPoolModel.cs AllCards 惰性枚举全池卡并缓存）枚举池内卡牌，统计各卡
/// DynamicVars 命中 StrengthPower/VulnerablePower/PoisonPower/DoomPower/WeakPower 的去重卡种数
/// ——口径与牌组 DeckVarCount 一致（键即"该卡施加/产出该状态"的读取信号），数字是运行时真实枚举，
/// 不是拍脑袋估计。
/// 用途（联动互乘精化）：
/// ①多段(Repeat/X)攻击的每段都被力量放大——按本职业 StrengthPower 产出指数相对中位加权
///   （指数高=力量易得→每段更值；低→少算），见 <see cref="MultiHitStrengthScale"/>；
/// ②易伤依赖类候选（DOMINATE/COLOSSUS）按本职业 VulnerablePower 产出指数加权（前置易伤来源的池内可得性），
///   见 <see cref="VulnerableDependentScale"/>。
/// 统计口径局限（诚实声明）：只认 DynamicVar 键，UPPERCUT 这类共用通用 "Power" 变量
/// （decomp Uppercut.cs CanonicalVars = Damage+Power）的双负面卡不计入；池统计忽略 Unlocks/Epoch
/// （AllCards 全量口径，decomp CardPoolModel.AllCards 注释 "ignores Unlocks/Epoch state"）。
/// "产出难易 → 评分数"的换算（中位相对值截断）仍是启发式，注释写明范围与理由。
/// </summary>
internal static class RoleStatusAccess
{
    /// <summary>统计时跟踪的状态 DynamicVar 键（主要产出源；与 DeckVarCount 同口径）。</summary>
    private static readonly string[] TrackedVars =
    {
        "StrengthPower", "VulnerablePower", "PoisonPower", "DoomPower", "WeakPower",
    };

    /// <summary>多段攻击的攻击端画像缩放范围（相对池中位；±25% 封顶，小步保守）。</summary>
    private const float MultiHitStrengthMin = 0.8f;
    private const float MultiHitStrengthMax = 1.25f;

    /// <summary>易伤依赖卡加成缩放范围（相对池中位；±30% 封顶，小步保守）。</summary>
    private const float VulnerableDependentMin = 0.8f;
    private const float VulnerableDependentMax = 1.3f;

    /// <summary>role(ReceivingRole 大写，如 IRONCLAD) → varKey → 池内去重卡种数。只读缓存，主线程构建。</summary>
    private static Dictionary<string, Dictionary<string, int>>? _byRole;

    /// <summary>本职业池中带指定状态变量的去重卡种数；未知角色返回 0。</summary>
    public static int Count(string role, string varKey)
    {
        EnsureBuilt();
        if (string.IsNullOrEmpty(role) || _byRole == null)
            return 0;
        return _byRole.TryGetValue(role, out Dictionary<string, int>? byKey)
            && byKey.TryGetValue(varKey, out int count)
            ? count
            : 0;
    }

    /// <summary>多段(Repeat/X 段)攻击的攻击端画像缩放：本职业 StrengthPower 产出数相对 5 角色中位。
    /// 力量易得的职业（指数&gt;中位）多段每段都被力量放大 → 画像按比例上调；稀缺职业下调。恒截断在
    /// [0.8, 1.25]，中位为 0 时返回 1（无数可参照不猜）；未知角色（无接收职业）不缩放。
    /// 实测池数据（2026-09-04 运行时枚举）：StrengthPower 产出 IC=7、SILENT=0、DEFECT=1、NECRO=1、REGENT=1
    /// （中位 1）→ IC 多段 ×1.25，SILENT ×0.8，其余 ×1.0。</summary>
    public static float MultiHitStrengthScale(string role)
        => string.IsNullOrEmpty(role)
            ? 1f
            : RelativeScale(Count(role, "StrengthPower"), MedianAcrossRoles("StrengthPower"), MultiHitStrengthMin, MultiHitStrengthMax);

    /// <summary>易伤依赖卡（DOMINATE/COLOSSUS）加成缩放：本职业 VulnerablePower 产出数相对中位，
    /// 表达"这职业后续还能补到前置易伤源"的可得性（牌组已实际施加易伤时仍按此放大价值）。
    /// 截断 [0.8, 1.3]；未知角色不缩放。
    /// 实测池数据：VulnerablePower 产出 IC=6、SILENT=1、DEFECT=1、NECRO=2、REGENT=5（中位 2）
    /// → IC/REGENT ×1.3，NECRO ×1.0，SILENT/DEFECT ×0.8。</summary>
    public static float VulnerableDependentScale(string role)
        => string.IsNullOrEmpty(role)
            ? 1f
            : RelativeScale(Count(role, "VulnerablePower"), MedianAcrossRoles("VulnerablePower"), VulnerableDependentMin, VulnerableDependentMax);

    /// <summary>count 相对 median 的线性比并截断；median ≤ 0 时按 1（无参照不缩放）。</summary>
    private static float RelativeScale(int count, int median, float min, float max)
    {
        if (median <= 0)
            return 1f;
        return Math.Clamp(count / (float)median, min, max);
    }

    /// <summary>5 个可玩角色的池内产出数中位（含 0；取排序中间值——角色数为奇数）。</summary>
    private static int MedianAcrossRoles(string varKey)
    {
        EnsureBuilt();
        if (_byRole == null)
            return 0;
        List<int> values = new();
        foreach (Dictionary<string, int> byKey in _byRole.Values)
            values.Add(byKey.TryGetValue(varKey, out int c) ? c : 0);
        values.Sort();
        return values.Count == 0 ? 0 : values[values.Count / 2];
    }

    /// <summary>从 ModelDb.AllCharacters 逐角色枚举 CardPool.AllCards 构建一次并缓存；
    /// 只在主线程调用（跑局内 ModelDb 已就绪）。构建后打一条 INFO 统计日志（每进程一次），
    /// 供语料/文档核对真实产出数。</summary>
    private static void EnsureBuilt()
    {
        if (_byRole != null)
            return;
        Dictionary<string, Dictionary<string, int>> byRole = new(StringComparer.Ordinal);
        foreach (CharacterModel character in ModelDb.AllCharacters)
        {
            string role = character.GetType().Name.ToUpperInvariant();
            Dictionary<string, int> byKey = new(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CardModel card in character.CardPool.AllCards)
            {
                if (!seen.Add(card.Id.Entry))
                    continue; // 同种卡去重（池内同卡只算一次产出源）
                foreach (string tracked in TrackedVars)
                {
                    if (card.DynamicVars.ContainsKey(tracked))
                    {
                        byKey.TryGetValue(tracked, out int prior);
                        byKey[tracked] = prior + 1;
                    }
                }
            }
            byRole[role] = byKey;
            Entry.Logger.Info(
                $"[CombatSolver/Run] ROLE_STATUS_ACCESS role={role} poolCards={seen.Count} " +
                string.Join(" ", Array.ConvertAll(TrackedVars, k => $"{k}={(byKey.TryGetValue(k, out int c) ? c : 0)}")));
        }
        _byRole = byRole;
    }
}
