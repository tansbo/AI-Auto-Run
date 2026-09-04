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
