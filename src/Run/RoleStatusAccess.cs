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
/// 机制事实（第二份取证报告 + decomp 逐文件核对，2026-09-04）：
///  ① 乘算门槛：Weak/Vulnerable/Frail/Colossus 等攻防乘算只作用于 powered（吃力量/敏捷的）攻击/格挡
///     ——毒、球、不具名伤害不吃（WeakPower.cs ModifyDamageMultiplicative L29-31、VulnerablePower.cs
///     ModifyDamageMultiplicative L39-41 均先判 !props.IsPoweredAttack() 返回 1；FrailPower.cs
///     ModifyBlockMultiplicative L25-27 判 IsPoweredCardOrMonsterMoveBlock）。
///  ② 虚弱 WeakPower：带虚弱者打出的 powered 攻击 ×0.75（WeakPower.cs CanonicalVars DamageDecrease 0.75m L20-22）。
///  ③ 易伤 VulnerablePower：对带易伤目标打出的 powered 攻击 ×1.5（VulnerablePower.cs DamageIncrease 1.5m L26-28）。
///  ④ 易碎 FrailPower：Owner 获得 powered 格挡 ×0.75（FrailPower.cs L30-32 返回 0.75m）。
///  ⑤ DebilitatePower（Necrobinder）：把目标易伤倍率加深 amount+(amount-1)（1.5→2.0）、把带虚弱的
///     dealer 虚弱压到 amount-(1-amount)（0.75→0.5）（DebilitatePower.cs ModifyVulnerableMultiplier L25-31 /
///     ModifyWeakMultiplier L34-40）。
///  ⑥ 力量衰减：负 StrengthPower/临时 TemporaryStrengthPower 即给目标 -X 力量（其每段 powered 攻击 -X）：
///     TemporaryStrengthPower 走 PowerCmd.Apply&lt;StrengthPower&gt;(Sign*amount)（TemporaryStrengthPower.cs
///     BeforeApplied L104-107，Sign 由 IsPositive 决定 L54-63），回合末移除并反向补回。
///  ⑦ 巨像 Colossus（Ironclad 1 费技能，Colossus.cs L27-37）：Block 4（升级 7）+ ColossusPower 1 层
///     （每敌方半回合末 -1，ColossusPower.cs AfterSideTurnEnd L48-54）；该 Power 让"带易伤的 powered
///     攻击者"对 Owner 的伤害 ×0.5（ColossusPower.cs ModifyDamageMultiplicative L27-46：target==Owner、
///     IsPoweredAttack、dealer 非空且带 VulnerablePower）——敌方易伤在防御端双重变现（你要么先打爆它，
///     要么借它降它的输出）。
///  ⑧ 主宰 Dominate（Ironclad 1 费 Rare Exhaust，Dominate.cs L31-43）：施 Vulnerable 1（升级 2，
///     OnUpgrade L46-48）后按目标"施放后总易伤层数 V"给 Owner 常驻 Strength V（先读
///     DynamicVars["VulnerablePower"] 施加，再 GetPower&lt;VulnerablePower&gt;().Amount 取 V）——
///     易伤放大器随既有层数递增，是"易伤依赖"方向（联动表 Vulnerable-payoff 条目的机制铁证）。
///  ⑨ 职业身份（供口径/注释，五职业核心折算，2026-09-04 取证报告 #10）：
///     Ironclad 铁甲 = 易伤 ×1.5 乘法 + 力量加法（起步 Bash 带易伤 2，Dominate 把易伤换力量、Colossus
///     把易伤折防御）→ 多段卡每段都被力量/易伤放大 → 多段画像按"力量+易伤产出指数"加权；
///     Necrobinder 亡灵 = Doom 执行阈值 + Debilitate 加深易伤/压虚弱；
///     Silent 静默 = 毒递减 DoT Σ(1..N)=N(N+1)/2（加速剂额外多跳）；
///     Defect 故障 = 球每回合末固定收益（闪电 3 直伤/霜 2 格挡，Evoke 8/5，Focus 线性放大）；
///     Regent 君王 = MonarchsGaze 命中给临时力量衰减 + 星辰经济。
/// 用途：
/// ①多段(Repeat/X)攻击的每段都被力量（加法）与易伤（对 powered 段 ×1.5）放大——按本职业
///   "StrengthPower+VulnerablePower 产出指数"相对 5 角色中位加权（指数高=力量/易伤易得→每段更值；
///   低→少算），见 <see cref="MultiHitScale"/>；
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
    private const float MultiHitScaleMin = 0.8f;
    private const float MultiHitScaleMax = 1.25f;

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

    /// <summary>多段(Repeat/X 段)攻击的攻击端画像缩放：本职业 StrengthPower+VulnerablePower 产出数相对
    /// 5 角色同口径中位。多段每段都被力量（加法）与易伤（对 powered 段 ×1.5）放大（事实 ②③⑦⑧⑨），
    /// 铁甲两路都易得（力量 7 + 易伤 6）→ 画像上调；稀缺职业下调。恒截断在 [MultiHitScaleMin, MultiHitScaleMax]，
    /// 中位为 0 时返回 1（无数可参照不猜）；未知角色（无接收职业）不缩放。
    /// 实测池数据（2026-09-04 运行时枚举）：力量+易伤合计 IC=13、SILENT=1、DEFECT=2、NECRO=3、REGENT=6
    /// （中位 3）→ IC/REGENT ×1.25，NECRO ×1.0，SILENT/DEFECT ×0.8。</summary>
    public static float MultiHitScale(string role)
    {
        if (string.IsNullOrEmpty(role))
            return 1f;
        int combined = Count(role, "StrengthPower") + Count(role, "VulnerablePower");
        return RelativeScale(combined, CombinedMedianAcrossRoles("StrengthPower", "VulnerablePower"), MultiHitScaleMin, MultiHitScaleMax);
    }

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

    /// <summary>跨多个状态键合计（每角色求和）后的中位，用于"力量+易伤"这类组合口径。</summary>
    private static int CombinedMedianAcrossRoles(params string[] varKeys)
    {
        EnsureBuilt();
        if (_byRole == null)
            return 0;
        List<int> values = new();
        foreach (Dictionary<string, int> byKey in _byRole.Values)
        {
            int sum = 0;
            foreach (string key in varKeys)
                sum += byKey.TryGetValue(key, out int c) ? c : 0;
            values.Add(sum);
        }
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
