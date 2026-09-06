# RunAuto 长期阶梯指标（A0→A10 自强化跑局）

本文件记录 RunAuto 全自动跑局的长期胜率、死亡主因与调参因果，作为 A0→A10 自强化进度的单一指标来源。
原始逐局遥测/日志在 `.local/ladder`（不入库），本文件只沉淀结论性指标。

## 阶梯进度
- 目标：每级 ≥10 局且胜率 ≥60% 自动升 1 级；A10 累计 200 连胜。
- 当前：**A0 停留中**（样本 27 局，3 胜 24 败 = 11.1%，未达 60% 门）。

## A0 胜率表（按批次）
| 批次 | DLL | 种子 | 有效局 | 胜 | 败 | 胜率 | 备注 |
|---|---|---|---|---|---|---|---|
| 旧基线(参考) | d9832b6 前 | 1-6 | 3 | 0 | 3 | 0% | 仅方向性 |
| 批1 | d9832b6 | 101-110 | 4 | 0 | 4 | 0% | 6 局被基础设施 bug 吃光(无遥测) |
| w5 批 | 6c8263f | 111-120 | 7 | 2 | 5 | 29% | TANX 修复后 |
| 批3 | 0157937 | 121-130 | 9 | 1 | 8 | 11% | 完成率 9/10 |
| **合计** | — | — | **27** | **3** | **24** | **11.1%** | 不升档 |

## 死亡主因（A0，24 败）
1. **Act3 QUEEN_BOSS 尾王墙：14 局（58%）**——稳定到达 48 房（act3 boss 房）后死亡。
   - 机制（decomp Queen.cs）：`YOU_ARE_MINE`=Frail/Weak/Vulnerable 99 层；`BURN_BRIGHT`=给火炬头 +1 力量 + 自身 20 甲；
     `OFF_WITH_YOUR_HEAD`=5 段攻击；`EXECUTION`；`ENRAGE`=+2 力量。
   - 核心缺陷：**模拟对 QUEEN+火炬头组合伤害系统性低估**。实证：128 局 82HP 进 QUEEN、连续 3 回合 26/60/26 格挡，
     turn4 `HP_PREDICTION planned=1` 实际 82 暴毙；103 局 `planned=9` 实际 26。FORECAST 对 YOU_ARE_MINE 报 hits=-
     未计入 Vulnerable/Frail 对后续攻击的放大 → solver 误判安全 → 该防御不防、血量该留不留。
2. **Act2 boss / act2 精英线：10 局**（其中 6 局死于 33 房 = act2 boss；含薄牌组：123/124/125 遗物仅 6-8）。
3. **基础设施卡死致整局无遥测（已修复为主）**：TANX 附魔屏挂机（3 局，6c8263f 修复✅）、
   MAYHEM 自动打出空选择卡崩溃（108，0157937 修复✅）、NCard 双重释放后奖励/宝箱屏卡死（≥5 局，修复中）、
   搜索爆炸/GC 超时（110/115/119，环境性+搜索预算侧）。

## 胜局画像（3 胜）
- 112（w5）：48 房 victory=True，27 抓 15 遗物；QUEEN 战从 92HP 高位起手，连招完整。
- 118（w5）：22 抓 10 遗物，FEEL_NO_PAIN + RAGE 引擎。
- 127（批3）：26 抓 10 遗物，连续大格挡（28-32/回合）扛过 QUEEN debuff 链后反杀。
- 共性：**进 QUEEN 时血量充足（59-92）或格挡引擎每回合 28+**；败局普遍 6-26HP 进 boss 或格挡断裂。

## 调参因果记录
- `6c8263f`（事件驱动）：NDeckEnchantSelectScreen 多选附魔（TANX 三刃回旋镖）改走通用网格预览驱动 →
  修 3/10 局整局超时无遥测（102/105/110 中 102/105 恢复完整完成）。
- `0157937`（搜索/自动打出）：MAYHEM 自动打出空选择卡时构造 0..0 空 spec（对照 ResolveManualCardChoice 同款）→
  消除 `BuildSpec` 空候选崩溃（108 类 SEARCH_FAILURE 整局卡死）。门禁：Release 0 警告/REFACTOR_BOUNDARIES_OK/ALL_CHARS_OK 5/5。
- 待修（按优先级，需 fixture 复现后闭环）：
  1. QUEEN sim/live 伤害低估（Vulnerable/Frail 99 层 × 火炬头力量成长在 FORECAST/HP_PREDICTION 中漏乘）——尾王墙主因。
     证据链：128 局 82HP 进 QUEEN、前 3 回合 26/60/26 格挡扛住，turn4 `HP_PREDICTION planned=1` 实际 82 暴毙；
     103 局 `planned=9` 实际 26。模拟对 QUEEN 行动/力量成长+99 层 debuff 净伤害估计偏低。已核对
     MonsterMoveEffects.cs(694-697) 确实施加 99 层 Frail/Weak/Vulnerable；AfterDeathMirrors.cs(63-75) 已处理
     Amalgam 死后 _hasAmalgamDied+强制 ENRAGE——偏差疑在 Vulnerable 放大乘算门槛（ModifyDamageMirrors 仅
     IsPoweredAttack 生效）或行动分支预测（IntentForecaster 条件分支近似），需 fixture 逐回合对照定位。
     128 精确机制：turn4 玩家满 82HP 无格挡手牌，TORCH 单次 TACKLE_3(基础14) 打出 82 = QUEEN 前面多次
     BURN_BRIGHT(每次给 TORCH +1 力) 已把 TORCH 力量叠高，一次秒杀；模拟 predicted QUEEN 仍为 BUR…(buff)
     且低估 TORCH 力量累积 → planned=1。修复需验证模拟内 BURN_BRIGHT 力量叠加次数与实机一致。
  2. NCard 双重释放（游戏侧 QueueFreeSafely 延迟回调在"奖励→离房→进房"快速切换下双 Free）——房间过渡加帧等待候选。
  3. 106 类 Bolas 回手 DeckVersion 漂移致终态校验误杀（Phases.cs:546）。
- 观察：DLL 修复后批次完成率 4/10 → 7/10 → 9/10，但胜率受 QUEEN 墙压制在 ~11%，与基础设施完成率解耦。

## 并行/吞吐
- 双安装并行验证可行（P1+P2 各一 worker，无换页拖慢），内存 15.7GB 上限 2 worker（单实例 ~10GB 提交内存）。
- 完成率提升后每批 10 局约 2.5-3h；A0 达标（≥60%）前继续按 分析→修复→复测 循环推进。

## 2026-09-06 基线快照（analyze.py 44 有效局，未含进行中 200-207 批）
- A0 IRONCLAD: runs=44 wins=4 rate=9.1% avgFloors=41.5
- 死亡幕分布: act2=16 act3=24；death-report 多数最后房间=进入战斗(Boss) 后败 → act3 Boss(队列 QUEEN 系)为主墙
- 里程碑记录：水晶球链闭环(d209432..193ea34)；L1 rng 药水夹具 F1-F4(fa9ae35/19e8363)；
  精英追踪/画像/路线注入(015c125/0e16fb9/0fe4ba3/707caf3)；事件看门狗(707caf3)。
- 未达标(A0 <60%)→ 继续 分析→修复→复测；修复验证用定向短跑；门禁三全。

## 2026-09-06 中批快照（telemetry 口径）
- A0: runs=47(含200批) wins=4 rate=8.5%；act3 死 26。精英样本已入（含 seed200 六种精英）。
- 假设检验：牌组虚胖非主因（胜局抓牌 25.8 vs 败 22.6）→ 未改 CardPickerAI。

## 2026-09-06 批次 200-207 中快照
- w5 全 OK(204-207)：seed207 胜(act3 f48, 新构建含水晶球/事件看门狗/精英链后首胜)；204-206 败(act2/3)。
- A0 合计: runs=51 wins=5 rate=9.8%（语料 73 行含旧 NO-TELEMETRY 去重）。
- w4 200-202 已完成(200 f42/201 f48/202 f48 败)；203 在跑。

## 2026-09-06 批次 208-215 完成
- OK:209胜(act3)/210/211/212/213/214败；208/215 深跑 no-telemetry。
- A0: runs=58 wins=6 rate=10.3%；精英袋预测 hits=1 misses=0。
- act3(f48) 败为主（QUEEN 墙）；新构建后胜局 207/209（水晶球/看门狗/精英链修复后首胜场在增加）。

## 2026-09-06 QUEEN L1 夹具矩阵（死亡回合/总掉血）
- mid deck: turn~5 / 72+ (seed QUEENMID01)
- formed v3: turn~6-7 / 80 (QUEENFORMED03)
- formed v4 +Whirlwind: turn~6-7 / 80 (QUEENV401)

## 2026-09-06 act3 Boss 败局回合掉血模式（Boss尾段聚合）
- 205 peak31 t3 / 206 peak30(死亡t6) / 201 peak28 t3 / 212 peak23 / 213 peak21 / 214 peak18/t3。
- 共同模式：第2-3回合起 18-31 爆发掉血 → 累积败。指向 QUEEN 中前期(BURN_BRIGHT/EXEC/易伤堆叠)破防窗口。
- 后续主线假设优先方向：该窗口的格挡/净化/斩杀时点；用 QUEEN L1 夹具矩阵度量验证。

## 2026-09-06 QUEEN 取向验证批 224-231（post 376930c）
- seed224 胜(act3 f48, pred 事件1)。样本小；继续收批后与 207/209 前段对比 act3 胜率。
