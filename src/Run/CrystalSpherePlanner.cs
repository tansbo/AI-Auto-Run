using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Saves;

namespace CombatSolver.Run;

/// <summary>
/// 水晶球 SL 规划（入口二选一预计算）。机制（decomp 核对 CrystalSphere / CrystalSphereMinigame）：
/// 事件两个入口（UNCOVER_FUTURE 付金 50+Rng(1..50) 换 3 次占卜；PAYMENT_PLAN 塞 Debt 诅咒换 6 次占卜）
/// 都会 new CrystalSphereMinigame(owner, base.Rng, n) —— 同一个事件 Rng、中间不消耗 → **两分支棋盘布局
/// 完全相同**，只有占卜次数不同。占卜=点 1 格 Big 工具清 3×3；物品占格全部清空即揭示（诅咒=当场塞
/// Doubt 诅咒，见 CrystalSphereCurse.RevealItem）→ 规划应尽量完整揭示高价值物品、绝不碰诅咒。
///
/// 预测=克隆事件 Rng 快照后在**自己的纯棋盘**上按 PopulateItems 固定顺序/尺寸复刻 15 件物品铺定
/// （placement 每件恰消耗一次 NextInt —— 见 CrystalSphereItem.PlaceItem→Rng.NextItem），不构造真实
/// 小游戏、不推进真实 Rng、不读遗物池。价值锚与 EventOverlayDriver.SelectBestRevealCell 实时选格一致。
/// 净收益 = 揭示完成价值锚 − 入口代价（A 扣金币、B 扣诅咒），供 EventOptionValuer 二选一并记录依据。
/// </summary>
internal static class CrystalSpherePlanner
{
    private const int W = 11;
    private const int H = 11;

    /// <summary>规划用单件物品。Kind: Relic/Card/Potion/Gold/Curse。</summary>
    internal sealed class PItem
    {
        public required string Kind;
        public required int X;
        public required int Y;
        public required int PW;
        public required int PH;
        public required float Value;      // 与实时选格同锚（诅咒为负但完成惩罚单独计）
        public bool Curse;
        public List<(int X, int Y)> Remaining = []; // 尚未清空的占格
    }

    internal sealed class Board
    {
        public required List<PItem> Items;
        public double FreeValue;          // 开局即自动揭示（整体落在预清区）的价值（诅咒为负）
    }

    /// <summary>固定铺定规格（顺序=PopulateItems，尺寸=各子类 Size，价值锚=实时选格同锚，见 decomp）。</summary>
    private static readonly (string Kind, int PW, int PH, float Value, bool Curse)[] LayoutSpec =
    {
        ("Relic", 4, 4, 100f, false),                 // CrystalSphereRelic
        ("Potion", 1, 3, 30f, false),                 // Potion(Common) 1×3
        ("Potion", 1, 3, 30f, false),                 // Potion(Common)
        ("Potion", 2, 2, 55f, false),                 // Potion(Rare) 2×2
        ("Card", 2, 2, 32f, false),                   // CardReward(Common)
        ("Card", 2, 2, 50f, false),                   // CardReward(Uncommon)
        ("Card", 2, 2, 70f, false),                   // CardReward(Rare)
        ("Curse", 2, 2, -60f, true),                  // CrystalSphereCurse（揭示即塞 Doubt）
        ("Gold", 1, 1, 5f, false),                    // Gold(small,10金) ×5
        ("Gold", 1, 1, 5f, false),
        ("Gold", 1, 1, 5f, false),
        ("Gold", 1, 1, 5f, false),
        ("Gold", 1, 1, 5f, false),
        ("Gold", 2, 1, 14f, false),                   // Gold(big,30金) ×2
        ("Gold", 2, 1, 14f, false),
    };

    /// <summary>
    /// 克隆事件 Rng 后复刻铺定 → 纯棋盘（含四角+十字预清空造成的"开局自动揭示"物品）。
    /// 返回 null = 无法复刻（快照/铺定失败）→ 调用方退回未建模，不影响事件选择。
    /// </summary>
    internal static Board? Predict(CrystalSphere crystal)
    {
        if (crystal.Rng == null)
            return null;
        Rng clone;
        try
        {
            clone = new Rng(crystal.Rng.ToSerializable());
        }
        catch (Exception)
        {
            return null; // 快照失败不应影响事件推进：退回未建模。
        }

        bool[,] occ = new bool[W, H];
        var items = new List<PItem>(LayoutSpec.Length);
        foreach ((string kind, int pw, int ph, float value, bool curse) in LayoutSpec)
        {
            var cand = new List<(int X, int Y)>();
            for (int x = 0; x + pw <= W; x++)
            {
                for (int y = 0; y + ph <= H; y++)
                {
                    bool fits = true;
                    for (int i = 0; i < pw && fits; i++)
                        for (int j = 0; j < ph && fits; j++)
                            if (occ[x + i, y + j])
                                fits = false;
                    if (fits)
                        cand.Add((x, y));
                }
            }
            if (cand.Count == 0)
                return null; // 铺定失败（真实极小概率会重试；无法复刻→未建模）。
            (int px, int py) = cand[clone.NextInt(0, cand.Count)]; // PlaceItem→NextItem 恰 1 次 NextInt
            for (int i = 0; i < pw; i++)
                for (int j = 0; j < ph; j++)
                    occ[px + i, py + j] = true;
            items.Add(new PItem { Kind = kind, X = px, Y = py, PW = pw, PH = ph, Value = value, Curse = curse });
        }

        // 四角+十字预清空 S（复制 CrystalSphereMinigame 构造函数算法：从四角向水平/垂直延伸 2 轮）。
        bool[,] cleared = new bool[W, H];
        {
            var pts = new List<Vector2I> { new(0, 0), new(W - 1, 0), new(W - 1, H - 1), new(0, H - 1) };
            for (int k = 0; k < 2; k++)
            {
                var next = new List<Vector2I>(pts);
                foreach (Vector2I p in pts)
                {
                    if (p.X - 1 >= 0) next.Add(new Vector2I(p.X - 1, p.Y));
                    if (p.X + 1 < W) next.Add(new Vector2I(p.X + 1, p.Y));
                    if (p.Y - 1 >= 0) next.Add(new Vector2I(p.X, p.Y - 1));
                    if (p.Y + 1 < H) next.Add(new Vector2I(p.X, p.Y + 1));
                }
                pts = next;
            }
            foreach (Vector2I p in pts)
                cleared[p.X, p.Y] = true;
        }

        var board = new Board { Items = items };
        double free = 0d;
        foreach (PItem item in items)
        {
            for (int i = 0; i < item.PW; i++)
            {
                for (int j = 0; j < item.PH; j++)
                {
                    if (!cleared[item.X + i, item.Y + j])
                        item.Remaining.Add((item.X + i, item.Y + j));
                }
            }
            if (item.Remaining.Count == 0)
                free += item.Value; // 整体落在预清区 → 开局即揭示。
        }
        board.FreeValue = free;
        return board;
    }

    /// <summary>
    /// 用 n 次占卜（Big 3×3）在该棋盘上做贪婪规划，返回预计总价值
    /// （开局自动揭示 + 逐次点击完成的物品价值；诅咒完成按 −80 计，规划会极力避免）。
    /// 与实时选格同目标：完成价值 + 0.15×推进 − 80×本次触诅咒数（诅咒揭示才真正塞 Doubt）。
    /// </summary>
    internal static double PlanTotal(Board board, int clicks)
    {
        if (board == null || clicks <= 0)
            return board?.FreeValue ?? 0d;

        // 可变状态：每件物品剩余迷雾占格。
        var remaining = new List<HashSet<(int X, int Y)>>(board.Items.Count);
        foreach (PItem item in board.Items)
            remaining.Add(new HashSet<(int, int)>(item.Remaining));

        double total = board.FreeValue;
        for (int step = 0; step < clicks; step++)
        {
            // 本步最优点击：与 SelectBestRevealCell 相同评分。
            (int cx, int cy)? best = null;
            float bestScore = float.MinValue;
            for (int x = 0; x < W; x++)
            {
                for (int y = 0; y < H; y++)
                {
                    float completedValue = 0f;
                    float progressValue = 0f;
                    float curseHits = 0f;
                    bool touchedAny = false;
                    for (int t = 0; t < board.Items.Count; t++)
                    {
                        HashSet<(int, int)> left = remaining[t];
                        if (left.Count == 0)
                            continue;
                        bool touched = false;
                        bool allClear = true;
                        foreach ((int rx, int ry) in left)
                        {
                            bool inBlast = Math.Abs(rx - x) <= 1 && Math.Abs(ry - y) <= 1;
                            if (inBlast)
                                touched = true;
                            else
                                allClear = false;
                        }
                        if (!touched)
                            continue;
                        touchedAny = true;
                        if (board.Items[t].Curse)
                        {
                            curseHits += 1f;
                            continue;
                        }
                        if (allClear)
                            completedValue += board.Items[t].Value;
                        else
                            progressValue += board.Items[t].Value * 0.15f;
                    }
                    if (!touchedAny)
                        continue;
                    float score = completedValue + progressValue - curseHits * 80f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = (x, y);
                    }
                }
            }
            if (best == null)
                break;

            // 应用本步：清掉 3×3 内的剩余迷雾；完整揭示的计入价值。
            for (int t = 0; t < board.Items.Count; t++)
            {
                HashSet<(int, int)> left = remaining[t];
                if (left.Count == 0)
                    continue;
                bool touched = false;
                bool allClear = true;
                foreach ((int rx, int ry) in left)
                {
                    bool inBlast = Math.Abs(rx - best.Value.cx) <= 1 && Math.Abs(ry - best.Value.cy) <= 1;
                    if (inBlast)
                        touched = true;
                    else
                        allClear = false;
                }
                if (!touched)
                    continue;
                if (allClear)
                {
                    // 整件揭示。
                    left.Clear();
                    if (board.Items[t].Curse)
                        total -= 80f;   // 诅咒揭示 → 当场塞 Doubt（CrystalSphereCurse.RevealItem）。
                    else
                        total += board.Items[t].Value;
                }
                else
                {
                    var gone = new List<(int, int)>();
                    foreach ((int rx, int ry) in left)
                    {
                        if (Math.Abs(rx - best.Value.cx) <= 1 && Math.Abs(ry - best.Value.cy) <= 1)
                            gone.Add((rx, ry));
                    }
                    foreach ((int rx, int ry) in gone)
                        left.Remove((rx, ry));
                }
            }
        }
        return total;
    }
}
