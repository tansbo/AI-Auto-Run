using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 一局的结构化遥测。种子重放 A/B 训练的数据源：
/// 同一种子跑两局、只改一个抓牌决策，结局差异归因于那张牌。
/// 由 RunAutoController.OnRunEnded 在 RunAutoTelemetryEnabled 时写 JSON 到
/// user://run_telemetry/{seed}_{runId}.json（headless 在隔离档案的 Roaming 下）。
/// </summary>
internal sealed class RunTelemetryData
{
    public string Seed { get; set; } = string.Empty;

    public string CharacterId { get; set; } = string.Empty;

    public int Ascension { get; set; }

    public string ForcedPicks { get; set; } = string.Empty;

    public bool Victory { get; set; }

    public bool Abandoned { get; set; }

    public int Floors { get; set; }

    public int ActReached { get; set; }

    public int RoomsHandled { get; set; }

    public List<TelemetryPick> Picks { get; } = [];

    public List<TelemetryRelicPick> RelicPicks { get; } = [];

    /// <summary>本局最终获得的遗物（奖励/宝箱/事件/先古），供"遗物×胜负"语料校准。</summary>
    public List<string> RelicIds { get; } = [];

    public void RecordRelicObtained(string relicId) => RelicIds.Add(relicId);

    public void RecordPick(
        RunState? runState,
        RoomType roomType,
        IReadOnlyList<CardModel> offered,
        CardModel? chosen,
        bool skipped,
        bool forced,
        string? forcedAction,
        float chosenScore)
    {
        Picks.Add(new TelemetryPick
        {
            Floor = runState?.TotalFloor ?? 0,
            RoomType = roomType.ToString(),
            Offered = offered.Select(static card => card.Id.Entry).ToArray(),
            Chosen = chosen?.Id.Entry,
            ChosenScore = chosen == null ? 0d : Math.Round(chosenScore, 2),
            Skipped = skipped,
            Forced = forced,
            ForcedAction = forcedAction,
        });
    }

    public void RecordRelicPick(
        RunState? runState,
        IReadOnlyList<RelicModel> offered,
        RelicModel? chosen,
        bool skipped,
        float chosenScore)
    {
        RelicPicks.Add(new TelemetryRelicPick
        {
            Floor = runState?.TotalFloor ?? 0,
            Offered = offered.Select(static relic => relic.Id.Entry).ToArray(),
            Chosen = chosen?.Id.Entry,
            ChosenScore = chosen == null ? 0d : Math.Round(chosenScore, 2),
            Skipped = skipped,
        });
    }
}

internal sealed class TelemetryPick
{
    public int Floor { get; set; }

    public string? RoomType { get; set; }

    public string[] Offered { get; set; } = [];

    public string? Chosen { get; set; }

    public double ChosenScore { get; set; }

    public bool Skipped { get; set; }

    public bool Forced { get; set; }

    public string? ForcedAction { get; set; }
}

internal sealed class TelemetryRelicPick
{
    public int Floor { get; set; }

    public string[] Offered { get; set; } = [];

    public string? Chosen { get; set; }

    public double ChosenScore { get; set; }

    public bool Skipped { get; set; }
}

/// <summary>把 RunTelemetryData 序列化写入 user://run_telemetry/。</summary>
internal static class RunTelemetry
{
    public static string Write(RunAutoSession session)
    {
        string directory = ProjectSettings.GlobalizePath("user://run_telemetry");
        Directory.CreateDirectory(directory);
        string runId = Guid.NewGuid().ToString("N")[..8];
        string file = Path.Combine(directory, $"{SanitizeSeed(session.Telemetry.Seed)}_{runId}.json");
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        File.WriteAllText(file, JsonSerializer.Serialize(session.Telemetry, options));
        return file;
    }

    private static string SanitizeSeed(string seed)
    {
        if (seed.Length == 0)
            return "empty";
        return new string(seed.Where(static c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
    }

    /// <summary>
    /// 遥测自动上传（opt-in，见 RunAutoSettings.TelemetryUploadEnabled/Url）：
    /// 把刚落盘的匿名对局遥测 JSON POST 到收集端点。只含对局统计；失败仅记日志不上报。
    /// </summary>
    public static async Task UploadAsync(string filePath, string url)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var content = new System.Net.Http.StringContent(
                File.ReadAllText(filePath), System.Text.Encoding.UTF8, "application/json");
            using System.Net.Http.HttpResponseMessage response = await client.PostAsync(url, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                Entry.Logger.Warn($"[RunAuto] 遥测上传失败 status={(int)response.StatusCode} url={url}");
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[RunAuto] 遥测上传异常：{ex.Message}");
        }
    }
}
