# CombatSolver 架构与职责地图

本文描述当前源码的所有权边界。它面向维护者和 coding agent；玩家功能说明见根目录 `README.md`，历史重构证据见 `docs/refactoring/`。

职责迁移时优先更新本文，并同步更新 Windows 的 `tools/verify-refactor-boundaries.ps1` 与 Linux 的 `tools/verify-refactor-boundaries.sh`。历史审计记录保留当时结论，不承担当前导航职责。

## 1. 运行链

```text
Entry / turn hooks
  -> SolverController（主线程会话与请求）
  -> CombatRootSnapshot.Capture（主线程稳定根）
  -> CombatSearchCoordinator（后台主搜索与反事实审计）
  -> CombatBeamSolver（分支搜索）
  -> SolverResult
  -> SolverOverlaySnapshot.Capture（主线程 UI 投影）
  -> Overlay renderer / 原版部署入口
```

搜索 worker 接收 `CombatRootSnapshot`、`SearchPolicySnapshot`、诊断 sink、帧压力信号和取消令牌。它不读取全局设置、控制器、UI 或无人测试状态。

## 2. Runtime

| 文件 | 职责 | 不负责 |
|---|---|---|
| `src/Runtime/Entry.cs` | Mod 初始化、补丁安装、战斗与回合生命周期入口、无人请求循环启动 | 搜索策略和战斗语义 |
| `src/Runtime/SolverController.cs` | 主线程搜索/续用/部署/全自动编排，结果过期与重算审计 | Beam 内部算法和 UI 布局 |
| `src/Runtime/SolverControllerSessions.cs` | combat/search/deployment 三类会话的状态与取消所有权 | 跨会话全局静态字段堆积 |
| `src/Runtime/CombatRootSnapshot.cs` | 主线程捕获完整预测根，比较捕获前后 live 状态，并向 worker 提供 Fork 根 | worker 惰性读取 live 战斗 |
| `src/Runtime/ContinuationStamp.cs` | 跨回合 live/predicted 状态文本、首个差异与完整差异；九条战斗 RNG 使用计数器与四段内部状态共同核对 | Beam 状态去重 |
| `src/Runtime/BaseLibCloneConcurrencyPatch.cs` | BaseLib 克隆扩展存在时，让原版 `MutableClone` 与内嵌模拟的模型深克隆共用窄串行边界，保护其全局弱表 | 整段搜索串行化、BaseLib 业务语义与候选政策 |
| `src/Runtime/PowerDynamicVarWarmup.cs` | 主线程根捕获时物化规范 Power 与当前战斗 Power 的显示变量 | 搜索评分、Power 语义与 worker 本地化 |
| `src/Runtime/PowerDynamicVarMaterializationGuardPatch.cs` | 搜索模拟惰性创建 Power 显示变量时立即报告根捕获缺失 | Power 语义、显示内容与搜索阶段串行化 |
| `src/Runtime/SearchGcPolicy.cs` | 战斗级 No-GC、搜索内后台全代回收续搜、战斗结束后台回收及新搜索入口协调 | Beam 剪枝、候选评分与模拟语义 |
| `src/Runtime/SearchMemoryPressureSignal.cs` | 将 Runtime 的进程分配边界和回收入口注入搜索；不让 Search 直接操作 GC 模式 | 设置读取与搜索评分 |
| `src/Runtime/SolverSettings.cs` | 持久化性能、执行、搜索并行度和搜索结束通知设置，并在主线程捕获不可变搜索 snapshot | 搜索期读取全局设置 |
| `src/Runtime/PlayerTurnSetupPatches.cs` | 首回合原生页面出现后的 Start 根搜索；后续回合观察上一轮 `EndTurn.TurnStartChoices` 的原生页面，全自动直接可见重放，单步默认交还玩家并允许执行/全自动入口接管既有选择；进入 Play 后交给 continuation 核对 | 普通 Play 阶段搜索与动作部署 |
| `src/Runtime/NativeChoiceRuntime.cs` | 观测原版战斗选择请求，按卡牌语义状态匹配计划实例，并锁定、驱动真实页面控件 | 选择分支枚举和战斗结算 |
| `src/Runtime/CombatBugReportExporter.cs` | 主线程冻结当前/最近战斗的实机取证状态；单消费者后台 FIFO 按检查点顺序整理、序列化和写盘，导出任务作为队列屏障等待此前记录完成 | 后台读取 live 战斗、通用 replay/native-state 导入 |
| `src/Runtime/CombatBugReportDescription.cs` | 汇总本场结构化异常、重算和战损信号，并把自动分类附到玩家问题描述后 | 后端字段、问题包内容和搜索决策 |
| `src/Runtime/CombatBugReportUploader.cs` | 通过不继承游戏进程代理的专用客户端直连接收服务；校验问题包与文本上限，以 multipart 流式上传并传播取消，限制服务端响应，并以反馈编号和实收字节数确认完整接收 | 问题包内容生成、隐私脱敏、UI 单实例与确认流程 |
| `src/Runtime/SearchCompletionNotifier.cs` | 搜索成功、失败、停止或过期后按设置决定是否通知；Windows 使用原生通知和系统提示音，先核对前台进程并在非 Windows/headless 环境停止 | 搜索生命周期、跨平台伪通知和自定义声音播放 |

`SolverCombatSession` 持有本场路线、续用和重算状态；`SolverSearchSession` 持有 generation、取消、进度和帧观测；`SolverDeploymentSession` 持有部署取消。旧回调只能写回创建它的 search session。

## 3. Search

### 3.1 请求级编排

- `SearchPolicySnapshot.cs`：主线程捕获的不可变搜索设置。
- `SearchDiagnosticsSink.cs`：搜索日志出口。
- `SearchFramePressureSignal.cs`：Runtime 向 worker 提供的帧压力信号。
- `CombatSearchCoordinator.cs`：一次请求的主搜索，以及 Disabled/Smart/RequireAtLeastOne 所需的无药或限药反事实审计；合并多次搜索的总指标。
- `CombatPlan.cs`：Runtime 消费的计划、结果和续用数据。结果不得保留历史 Simulator 对象图。

### 3.2 CombatBeamSolver 分片

| 文件 | 权威职责 |
|---|---|
| `CombatBeamSolver.cs` | 构造参数、不可变根配置、`SearchRunContext` 与两个策略对象接线 |
| `CombatBeamSolver.Models.cs` | `SearchNode`、`SimulationSnapshot`、转置标签、`SearchFeatures`、单次运行 `SearchRunContext` |
| `CombatBeamSolver.Phases.cs` | `Solve`、阶段循环、预算边界与进度检查点 |
| `CombatBeamSolver.Expansion.cs` | 可执行卡牌/药水/结束回合候选展开和动作回放入口 |
| `CombatBeamSolver.ParallelExpansion.cs` | 固定 worker lane、父节点原始候选并发物化、按输入顺序串行提交与快照所有权 |
| `CombatBeamSolver.Retention.cs` | prune/retention 调用边界与相关小型辅助 |
| `CombatBeamSolver.BeamRetentionPolicy.cs` | 状态去重、中间分数排序、多样性通道、动作/回合开始选牌保路、药水配额和小型 Pareto |
| `CombatBeamSolver.FinalPlanOrdering.cs` | 终局胜负、偷窃、战损、药水、卖血和搜索边界排序 |
| `CombatBeamSolver.StateEvaluation.cs` | 搜索快照、评分、威胁、stand-pat 和状态特征 |
| `CombatBeamSolver.Terminal.cs` | 终局精确回放、逐回合结果、击杀与遗物标注 |

`SearchRunContext` 只活于一次 solver：计数器、性能指标、节流器、转置表和 stand-pat/威胁/coverage/路由缓存均在这里。根配置留在 solver，不把可变运行状态退回入口文件。

普通搜索按进程可用逻辑处理器数量选择初始展开 lane：至少 4 个时默认 DOP4，2–3 个时默认 DOP2，只有 1 个时使用 DOP1；用户显式设置始终优先。设置中的“关闭（单线程）”映射 DOP1，数值项为 `2..8`，实际值还会按可用逻辑处理器钳制。coordinator 自己执行 lane 0，其余低优先级后台 lane 在一次 `Solve` 内复用 solver、缓存和 `SearchWorkPacer`。worker 不写全局 transposition、dominance 或 fallback：它们只物化原始候选，coordinator 仍按父节点输入顺序提交，因此固定节点预算下 DOP 不改变搜索语义。详细诊断和增量严格回放强制 DOP1。并行搜索失败提示会保留本次请求的 DOP；DOP 大于 1 时先引导上传问题包，再建议切换为“关闭（单线程）”。并行阶段指标为各 lane 的累计 CPU 时间，可以超过墙钟耗时；`parallel_waves / work_items / max_concurrency` 单独证明并发实际发生。一个 wave 会在提交前同时持有至多 DOP 个父节点的原始候选快照；高于默认值属于用户主动的速度/CPU/峰值内存权衡，扩大上限前必须单独验证高选择分支的峰值 live graph。节点预算截断时，coordinator 立即释放未展开父节点和不会进入下一层的候选模拟器，并用 `node_limit_snapshots_released` 记录实际释放数。

`BeamRetentionPolicy` 决定哪些中间候选继续活着；动作选牌、嵌套选牌和 `EndTurn.TurnStartChoices` 都以来源、效果、卡牌语义状态和上下文形成保路签名。`FinalPlanOrdering` 决定完整候选中最终采用哪条。两者不能合并成单一“总分排序”。`SearchFeatures` 是终局排序读取节点状态的只读投影。转置状态键中的九条战斗 RNG 必须包含完整内部状态；相同调用计数不能证明两个 RNG 后续等价。

### 3.3 分支战斗状态

`SimulatedCombatState*.cs` 把内嵌引擎状态适配为搜索所需的战斗领域视图：

- `Fork.cs`：统一稳定边界和对象图复制；
- `MonsterAi.cs` / `MonsterState.cs`：分支行动、私有 AI、已知怪物静态值；
- `DeathLifecycle.cs`：死亡、复活与阵容事务；
- `ActionChoices.cs` / `TurnStartChoices.cs` / `AutoPlay.cs`：嵌套选择与自动出牌；
- `CardLifecycle.cs` / `CardPowerHistory.cs` / `PowerLifecycle.cs`：卡牌和 Power 跨事件状态；
- `Relics.cs`、`PowerRelics.cs`、`ReactiveRelics.cs` 等：遗物与组合事务；
- `Potions.cs`：药水槽和使用状态。

活动 roster 只决定当前可行动、可选目标和 listener。已经捕获的怪物 AI/静态参数属于已知怪物和分支生命周期，不能在移出活动 roster 时提前删除。

## 4. 内嵌模拟引擎

### 4.1 基础层

`src/Engine/InCombat/Simulation/` 负责通用战斗命令时序、伤害、牌堆、历史、RNG、球和 Fork。它不包含单张卡、单个 Power 或具体怪物的搜索策略。

`src/Engine/Common/` 提供 `PredictedCard`、`PredictionForkContext`、`PredictionStateStore` 和通用模型克隆。一次 Fork 内的所有结构必须共享同一个 context；分支可变对象必须显式重映射。`BaseLibCloneConcurrency` 是原版与预测克隆共用的外部扩展并发边界，只包围模型深克隆阶段。

### 4.2 Mirror

`src/Engine/InCombat/Mirrors/` 精确实现原版 Hook、卡牌、药水、附魔和球方法。Facade 保持原版调用时序，registry 按运行时类型与方法分派。

`MethodMirrorRegistry` 同时实现 `IMethodMirrorRegistryDescriptorProvider`。`MethodMirrorRegistryDescriptor` 描述基础方法、receiver、显式 Handled/Ignored 注册和当前 inferrer；CoverageCatalog 只消费该描述符，不读取 registry 私有字段或 `MirrorMethodSpec` 内部布局。

## 5. Prediction 领域补偿

`src/Prediction/` 处理基础命令和单个 mirror 不能独立表达的领域语义：

- 卡牌/Power/遗物/药水/球的跨 Hook 生命周期；
- 怪物行动图、随机分支、私有 AI 与召唤；
- 死亡、复活、自动出牌和嵌套选牌；
- 第三方 ModHook subscriber 的主线程捕获与分支重建；
- 覆盖分类和动态状态字段政策。

这里可以保存具体领域规则，但不能决定 Beam 配额、最终路线或 UI 显示。新增补偿前检查 mirror、spec、support 和 `SimulatedCombatState` 的完整调用链，确保只有一个权威结算点。

## 6. UI

`src/UI/SolverOverlaySnapshot.cs` 是结果到显示数据的唯一转换边界。它在主线程复制状态、概览、详情、回合、动作标题、选牌文本、遗物标注、击杀、tooltip 和视觉类别。

以下 renderer 只接受不可变 snapshot：

- `SolverOverlay.ShowResult(Node, SolverOverlaySnapshot)`；
- `SolverRouteRow.Populate(SolverOverlayTurnSnapshot)`；
- `SolverActionPill.Create(SolverOverlayActionSnapshot)`。

renderer 不得重新读取 `SolverResult`、`PlanAction`、`PlanCardChoice` 或 `ModelDb`。部署需要的标量由 Runtime 单独持有，不从控件反向读取。

`SolverSettingsPanel` 是设置页的单一控件所有者，按 partial 分离构建职责：主文件负责标题、常规/性能/反馈三页切换、重载、提交、恢复默认和固定状态栏；`General` 负责求解器、药水、通知及自动执行设置；`Performance` 负责预设、并行度、内存预算和折叠的自定义搜索参数；`BugReports` 负责诊断、联系方式与问题包导出/上传；`Controls` 只提供本面板共享的 Godot 控件样式、输入校验和行布局。partial 之间不建立第二份设置状态，持久化仍只写 `SolverSettingsData`。

`SolverSettingsPanel.BugReports` 持有问题包导出/上传的单实例 UI 生命周期、取消令牌、进度条和线程安全完成邮箱，并把文件发送和服务端确认显示为两个阶段。后台任务只向完成邮箱发布一次 `Succeeded / Canceled / Failed`；面板自己的 `_Process` 每帧先消费终态，再处理字节进度或取消等待，并在同一次终态消费中释放令牌、收起进度条、替换状态消息和恢复按钮。上传生命周期不依赖搜索使用的 `SolverDispatcher`。`CombatBugReportUploader` 不持有 Godot 控件，后台传输只通过 `IProgress<CombatBugReportUploadProgress>` 发布字节计数。进度到达文件总字节数只代表请求正文已经写出，只有服务端回执同时确认反馈编号和实收字节数才算上传成功。

## 7. Run AI（AI自动跑局子系统）

`src/Run/` 是一个独立的跑局级编排子系统，全部由设置项 `RunAutoEnabled` 门控，仅单人跑局生效。它**不**参与战斗内搜索与模拟，只接管战斗之间（奖励/地图/非战斗房）的 UI 驱动。战斗内仍由战斗求解器全自动接管。

运行链：

```text
RitsuLib 跑局事件（RunStarted/RoomEntered/CombatVictory/...）
  -> RunAutoController（阶段机分派，主线程）
  -> 各房间/屏幕驱动（RunSafely 后台任务，主线程轮询 UI）
  -> 选牌/遗物/地图评分 AI（只读不可变模型，无模拟）
```

| 文件 | 职责 | 不负责 |
|---|---|---|
| `src/Run/RunAutoController.cs` | 订阅 RitsuLib 跑局事件、维护 `RunAutoSession` 阶段机、按 `RoomType` 分派到各驱动；开启时接管原版 FastMode | 战斗搜索、卡牌/遗物评分 |
| `src/Run/RunAutoSession.cs` | 跑局级状态（当前房型/阶段/已选卡牌/最近决策）与跑局级取消令牌（RunEnded 时取消） | 驱动逻辑 |
| `src/Run/RunAutoSettings.cs` | 读取持久化在 `SolverSettingsData` 的 Run AI 设置，及四个开关的 setter | 设置 UI 与持久化本身 |
| `src/Run/RunAutoSettingsPage.cs` | 在 RitsuLib 模组设置中注册"AI自动跑局"四个开关（主菜单可访问），绑定读写 `SolverSettingsData` | 持久化与决策 |
| `src/Run/RunUiHelper.cs` | 移植 AutoSlay 的点击/查找/等待配方（`ForceClick`、递归 `FindAll<T>`、超时轮询） | 决策逻辑 |
| `src/Run/CardPickerAI.cs` | 战后卡牌结构评分（稀有度+费用效率+牌组契合+关键词+精选表），返回选/跳过 | UI 驱动 |
| `src/Run/RelicPickerAI.cs` | 遗物结构评分（稀有度+精选表）；先古遗物按运行时类名识别诅咒并选最优正向，返回选/跳过 | UI 驱动 |
| `src/Run/CardRewardDriver.cs` + `NCardRewardScreenPatch.cs` | 战后卡牌奖励自动选牌/跳过 | 评分 |
| `src/Run/RewardsScreenDriver.cs` | 战后 `NRewardsScreen` 逐个领取奖励，子覆盖层等其自处理 | 评分 |
| `src/Run/MapRouter.cs` | 地图按 `MapPointType` 评分选路并点击前进，等 `RoomEntered` | 房间内部驱动 |
| `src/Run/NMapScreenPatch.cs` | Postfix 钩 `NMapScreen.Open` 统一触发地图选路（手动顶栏开图不触发）；`RunAutoController` 不再在 `MapGeneratedEvent` 触发选路 | 选路评分与点击 |
| `src/Run/RestSiteDriver.cs` | 篝火低血量回血、否则升级，处理升级选牌覆盖层 | 评分 |
| `src/Run/ShopDriver.cs` | 商店打开商品栏评分购买，购买排空移除覆盖层，关栏离开 | 评分 |
| `src/Run/EventDriver.cs` | 事件逐个选非致死选项，纯遗物选项走先古遗物评分选最优正向，事件内战斗交给求解器，Ancient 翻页 | 战斗 |
| `src/Run/RelicRewardDriver.cs` + `NChooseARelicScreenPatch.cs` | 遗物选择屏幕（Boss/珠宝盒）与宝箱房拾取 | 评分 |
| `src/Run/RunAutoOverlay.cs` | 屏幕底部一行跑局状态展示（幕/层/阶段/最近决策） | 一切决策与交互 |

关键约束（与战斗求解器一致）：

- 所有驱动只在主线程轮询 UI；后台搜索不读取 live 值；评分 AI 只读不可变 `CardModel`/`RelicModel`/`RunState`。
- 所有 `await Task.Delay` 走会话级取消令牌；`RunEndedEvent` 时各驱动以 `OperationCanceledException` 静默退出。
- 购买卡牌移除会打开 `NDeckCardSelectScreen` 覆盖层并 await 其 completion source，因此先启动购买 Task 不 await、边等边排空覆盖层，避免死锁。
- 重复启动用静态 `_active` 标志去重。

## 8. Unattended 测试

`UnattendedTestRunner` 保留请求级编排和现有 fixture helper。新增流程应落到明确所有者：

| 组件 | 职责 |
|---|---|
| `ProtocolHost` | 请求文件循环、协议校验、进程复用、每请求测试开关、漂移注入与 reset |
| `ScenarioBuilder` | 建立跑局/遭遇、加载快照、注入牌/Power/遗物/药水/球/RNG，返回 `ScenarioContext` |
| `Executor` | 分派严格差分、应用临时设置、启动搜索/全自动、等待复用/暂停/结束并恢复设置 |
| `Assertions` | 执行前边界检查和执行后的回合、生命、出牌、药水、Power 断言 |
| `Writer` | Passed/Held/Failed 公共协议字段、内存采集和结果文件原子替换 |

不要从深层 fixture 直接写结果，不要在 entry 中重新建立战斗，也不要让断言负责执行动作。

## 9. 工具与结构门禁

- `tools/run-unattended-test.ps1` / `tools/run-unattended-test.sh`：Windows / Linux 的平台原生入口，负责隔离 headless 进程、请求协议和结果读取。
- `tools/run-visible-steam-benchmark.ps1` / `tools/run-visible-steam-benchmark.sh`：Windows / Linux 的平台原生入口，负责正常可见 Steam 会话的搜索、GC 与帧口径。
- `tools/CoverageCatalog/Program.cs`：当前程序集和 registry descriptor 的覆盖目录生成/验证。
- `tools/verify-refactor-boundaries.ps1` / `tools/verify-refactor-boundaries.sh`：Windows / Linux 的等价门禁，阻止 Search 全局依赖、旧 controller 字段、worker live 回读、Beam 职责回流、unattended 编排回流、UI mutable 类型回流和 registry 私有反射；规则变化时必须同步维护两端。

纯职责移动至少运行 Release 编译与当前平台的结构门禁。改变语义、搜索或显示行为时，再按影响面选择严格差分、完整 headless、CoverageCatalog 或可见 Steam。

