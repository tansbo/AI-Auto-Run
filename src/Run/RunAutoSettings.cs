using Godot;

namespace CombatSolver.Run;

/// <summary>
/// 全自动跑局（Run AI）设置访问器。持久化在 <see cref="SolverSettingsData"/> 中，
/// 与战斗求解器设置共用同一份 user:// 存档，避免新增一个设置文件。
/// </summary>
internal static class RunAutoSettings
{
    public static bool Enabled => SolverSettings.Current.RunAutoEnabled;

    public static bool StopOnGameOver => SolverSettings.Current.RunAutoStopOnGameOver;

    public static bool FastMode => SolverSettings.Current.RunAutoFastMode;

    public static bool DebugLog => SolverSettings.Current.RunAutoDebugLog;

    /// <summary>A/B 种子覆盖：非空时以该种子新建标准单局（见 RunStartSeedPatch）。</summary>
    public static string? SeedOverride => SolverSettings.Current.RunAutoSeedOverride;

    /// <summary>A/B 强制抓牌策略原文（格式 "cardId:take,cardId:skip"）。</summary>
    public static string ForcedPicks => SolverSettings.Current.RunAutoForcedPicks ?? string.Empty;

    public static bool TelemetryEnabled => SolverSettings.Current.RunAutoTelemetryEnabled;

    /// <summary>解析强制抓牌规则：命中返回 true 并给出动作（take=抓，skip=跳过）。</summary>
    public static bool TryGetForcedPick(string cardId, out bool take)
    {
        string raw = ForcedPicks;
        if (string.IsNullOrWhiteSpace(raw))
        {
            take = false;
            return false;
        }
        foreach (string part in raw.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0)
                continue;
            string id = part[..colon].Trim();
            string action = part[(colon + 1)..].Trim();
            if (string.Equals(id, cardId, StringComparison.OrdinalIgnoreCase))
            {
                take = string.Equals(action, "take", StringComparison.OrdinalIgnoreCase);
                return true;
            }
        }
        take = false;
        return false;
    }

    public static void SetSeedOverride(string? value, bool persist = true)
    {
        SolverSettingsData data = SolverSettings.Current with
        {
            RunAutoSeedOverride = string.IsNullOrWhiteSpace(value) ? null : value,
        };
        if (persist)
            SolverSettings.Update(data);
        else
            SolverSettings.ApplyForTesting(data);
    }

    public static void SetForcedPicks(string value, bool persist = true)
    {
        SolverSettingsData data = SolverSettings.Current with { RunAutoForcedPicks = value ?? string.Empty };
        if (persist)
            SolverSettings.Update(data);
        else
            SolverSettings.ApplyForTesting(data);
    }

    public static void SetTelemetryEnabled(bool value, bool persist = true)
    {
        SolverSettingsData data = SolverSettings.Current with { RunAutoTelemetryEnabled = value };
        if (persist)
            SolverSettings.Update(data);
        else
            SolverSettings.ApplyForTesting(data);
    }

    public static bool TelemetryUploadEnabled => SolverSettings.Current.RunAutoTelemetryUpload;

    public static string TelemetryUploadUrl => SolverSettings.Current.RunAutoTelemetryUrl ?? string.Empty;

    /// <summary>演示定格毫秒（0=关）：关键跑局决策前把界面/决策条留屏，便于录制展示。</summary>
    public static int DemoHoldMs => SolverSettings.Current.RunAutoDemoHoldMs;

    /// <summary>演示截图开关（默认关）：决策瞬间进程内截图到 user://demo_frames，不改游戏速度。</summary>
    public static bool DemoCaptureEnabled => SolverSettings.Current.RunAutoDemoCapture;

    /// <summary>渠道演示脚手架：开局 Neow 强制选该先古遗物（如 KALEIDOSCOPE；空=不强制）。</summary>
    public static string ForceNeowRelicId => SolverSettings.Current.RunAutoForceNeowRelicId ?? string.Empty;

    /// <summary>渠道演示脚手架：指定幕（0 起）首个事件房入口强制获得的遗物（如 SEA_GLASS；空=不强制）。</summary>
    public static string ForceActRelicId => SolverSettings.Current.RunAutoForceActRelicId ?? string.Empty;

    /// <summary>渠道演示脚手架：强制获得的幕索引（默认 -1=不强制）。</summary>
    public static int ForceActRelicAct => SolverSettings.Current.RunAutoForceActRelicAct;

    /// <summary>进程内截图保存到 user://demo_frames（演示用，主线程调用；失败仅记日志）。</summary>
    public static void DemoShot(string tag)
    {
        if (!DemoCaptureEnabled)
            return;
        try
        {
            var root = ((SceneTree)Godot.Engine.GetMainLoop()).Root;
            Godot.Image image = root.GetTexture().GetImage();
            string directory = ProjectSettings.GlobalizePath("user://demo_frames");
            Directory.CreateDirectory(directory);
            string clean = new string(tag.Where(static c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
            if (clean.Length > 40)
                clean = clean[^40..];
            string path = Path.Combine(directory, $"{DateTime.Now:HHmmssfff}-{clean}.png");
            image.SavePng(path);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[RunAuto] 演示截图失败：{ex.Message}");
        }
    }

    public static async Task HoldForDemoAsync(CancellationToken token)
    {
        int ms = DemoHoldMs;
        if (ms <= 0)
            return;
        try
        {
            await Task.Delay(ms, token);
        }
        catch (OperationCanceledException)
        {
            // 跑局结束/取消，静默。
        }
    }

    public static void SetTelemetryUploadEnabled(bool value, bool persist = true)
    {
        SolverSettingsData data = SolverSettings.Current with { RunAutoTelemetryUpload = value };
        if (persist)
            SolverSettings.Update(data);
        else
            SolverSettings.ApplyForTesting(data);
    }

    public static void SetTelemetryUploadUrl(string value, bool persist = true)
    {
        SolverSettingsData data = SolverSettings.Current with
        {
            RunAutoTelemetryUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim(),
        };
        if (persist)
            SolverSettings.Update(data);
        else
            SolverSettings.ApplyForTesting(data);
    }

    public static void SetEnabled(bool enabled, bool persist = true)
    {
        SolverSettingsData data = SolverSettings.Current with { RunAutoEnabled = enabled };
        if (persist)
            SolverSettings.Update(data);
        else
            SolverSettings.ApplyForTesting(data);
    }

    public static void SetStopOnGameOver(bool value, bool persist = true)
    {
        SolverSettingsData data = SolverSettings.Current with { RunAutoStopOnGameOver = value };
        if (persist)
            SolverSettings.Update(data);
        else
            SolverSettings.ApplyForTesting(data);
    }

    public static void SetFastMode(bool value, bool persist = true)
    {
        SolverSettingsData data = SolverSettings.Current with { RunAutoFastMode = value };
        if (persist)
            SolverSettings.Update(data);
        else
            SolverSettings.ApplyForTesting(data);
    }

    public static void SetDebugLog(bool value, bool persist = true)
    {
        SolverSettingsData data = SolverSettings.Current with { RunAutoDebugLog = value };
        if (persist)
            SolverSettings.Update(data);
        else
            SolverSettings.ApplyForTesting(data);
    }
}
