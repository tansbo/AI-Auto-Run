using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace CombatSolver.Run;

/// <summary>
/// 在 RitsuLib 模组设置（系统设置 → 模组设置 → 战斗路线求解器）中注册"全自动跑局"设置页。
/// 主菜单即可访问，不需要进入战斗。绑定直接读写 <see cref="SolverSettingsData"/>，
/// 不复制第二份持久化：Write 时立即走 SolverSettings.Update 落盘，因此 Save() 为空实现。
/// </summary>
internal static class RunAutoSettingsPage
{
    public static void Register()
    {
        RitsuLibFramework.RegisterModSettings(Entry.ModId, page =>
        {
            page.WithModDisplayName(ModSettingsText.Literal("AI自动跑局"));
            page.AddSection("run_ai", section =>
            {
                section.WithTitle(ModSettingsText.Literal("全自动跑局"));
                section.AddToggle(
                    "run_auto_enabled",
                    ModSettingsText.Literal("启用全自动跑局"),
                    new SolverBoolBinding("run_auto_enabled", () => RunAutoSettings.Enabled, v => RunAutoSettings.SetEnabled(v)),
                    ModSettingsText.Literal("主菜单即可开启。开启后单人跑局从开局先古遗物到结局全自动：自动选牌、选路线、选遗物、商店、事件与战斗。"));
                section.AddToggle(
                    "run_auto_fast_mode",
                    ModSettingsText.Literal("快速模式（加速动画）"),
                    new SolverBoolBinding("run_auto_fast_mode", () => RunAutoSettings.FastMode, v => RunAutoSettings.SetFastMode(v)),
                    ModSettingsText.Literal("开启后接管游戏加速动画，大幅缩短自动跑局时间。"));
                section.AddToggle(
                    "run_auto_stop_on_game_over",
                    ModSettingsText.Literal("失败后停止"),
                    new SolverBoolBinding("run_auto_stop_on_game_over", () => RunAutoSettings.StopOnGameOver, v => RunAutoSettings.SetStopOnGameOver(v)),
                    ModSettingsText.Literal("开启后跑局失败自动停止，不会自动开始新的一局。"));
                section.AddToggle(
                    "run_auto_debug_log",
                    ModSettingsText.Literal("跑局调试日志"),
                    new SolverBoolBinding("run_auto_debug_log", () => RunAutoSettings.DebugLog, v => RunAutoSettings.SetDebugLog(v)),
                    ModSettingsText.Literal("输出跑局 AI 的决策日志，便于排查问题。"));
                section.AddString(
                    "run_auto_seed_override",
                    ModSettingsText.Literal("种子覆盖（A/B 训练）"),
                    new SolverStringBinding("run_auto_seed_override", () => RunAutoSettings.SeedOverride ?? "", v => RunAutoSettings.SetSeedOverride(v)),
                    ModSettingsText.Literal("留空 = 随机种子。填种子可重放同一局（路线/发牌/先古固定）。"),
                    64,
                    ModSettingsText.Literal("用于种子重放 A/B 训练：同一局只改一个抓牌决策，重放两次比较结局。"));
                section.AddString(
                    "run_auto_forced_picks",
                    ModSettingsText.Literal("A/B 强制抓牌策略"),
                    new SolverStringBinding("run_auto_forced_picks", () => RunAutoSettings.ForcedPicks, v => RunAutoSettings.SetForcedPicks(v)),
                    ModSettingsText.Literal("格式：cardId:take,cardId:skip（如 bash:take,clothesline:skip）。"),
                    256,
                    ModSettingsText.Literal("命中 take 的牌必抓；命中 skip 的牌不抓（全被跳过则跳过奖励）。批量训练用，留空走智能评分。"));
                section.AddToggle(
                    "run_auto_telemetry_enabled",
                    ModSettingsText.Literal("记录对局遥测"),
                    new SolverBoolBinding("run_auto_telemetry_enabled", () => RunAutoSettings.TelemetryEnabled, v => RunAutoSettings.SetTelemetryEnabled(v)),
                    ModSettingsText.Literal("每局写 JSON 到 user://run_telemetry/：种子、抓牌决策、结局，供离线聚合卡牌胜率差。"));
            });
        });
    }

    /// <summary>
    /// 把 RitsuLib 设置开关的读写转发到 <see cref="SolverSettingsData"/>。
    /// Scope 声明为 Global，与 SolverSettings 的 user:// 存档一致。
    /// </summary>
    private sealed class SolverBoolBinding : IModSettingsValueBinding<bool>, IModSettingsBinding
    {
        private readonly string _dataKey;
        private readonly Func<bool> _read;
        private readonly Action<bool> _write;

        public SolverBoolBinding(string dataKey, Func<bool> read, Action<bool> write)
        {
            _dataKey = dataKey;
            _read = read;
            _write = write;
        }

        public string ModId => Entry.ModId;

        public string DataKey => _dataKey;

        public SaveScope Scope => SaveScope.Global;

        public bool Read() => _read();

        public void Write(bool value) => _write(value);

        public void Save() { }
    }

    /// <summary>字符串输入绑定，转发到 <see cref="SolverSettingsData"/>，Scope=Global。</summary>
    private sealed class SolverStringBinding : IModSettingsValueBinding<string>, IModSettingsBinding
    {
        private readonly string _dataKey;
        private readonly Func<string> _read;
        private readonly Action<string> _write;

        public SolverStringBinding(string dataKey, Func<string> read, Action<string> write)
        {
            _dataKey = dataKey;
            _read = read;
            _write = write;
        }

        public string ModId => Entry.ModId;

        public string DataKey => _dataKey;

        public SaveScope Scope => SaveScope.Global;

        public string Read() => _read();

        public void Write(string value) => _write(value);

        public void Save() { }
    }
}
