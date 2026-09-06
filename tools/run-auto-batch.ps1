#requires -Version 7.0
# 连续自动跑局批量（headless 可观测 + 自动续局）。
#
# 目标：跑局"可知"——不再靠任意固定时长陪跑。每局调 run-unattended-test.ps1
# （-RunAutoFullRun -ExitOnComplete），它是一次阻塞子进程调用：
#   - 跑局自然结束（胜负皆然）→ 游戏自写结果 JSON(带 victory/rooms/act) 并退出 → 立即下一局
#   - 游戏侧存活监控判卡死（帧停摆/无推进超阈值）→ 写 Status=Stuck 结果 + exit 2 → 立即下一局
#   - 只剩最后兜底：run-unattended-test.ps1 的 -TimeoutSeconds（本脚本的 -TimeoutSeconds，
#     默认很大）才会强杀；真卡死已被监控提前结束，健康慢跑不会挨定时砍。
# 每局删掉残留结果 JSON 再启动（避免同 seed 重复跑局读到上一局残留）。
# 每局结果与遥测副本汇总到 CollectRoot/summary.jsonl，批量结束打印聚合。
#
# -Background 开关：detach 到隐藏 pwsh，stdout/stderr 落盘 <headlessRoot>\autorun-<id>.*.log，
# 写 pid 文件后立即返回；Claude 挂后台跑整批，结束后一次性读 summary 即可得知每局结果。

[CmdletBinding()]
param(
    # 种子列表，分号分隔。如 -Seeds "COMBATSOLVER;IRONCLAD;SEED3"
    [string]$Seeds = "",
    [string]$CharacterId = "IRONCLAD",
    [int]$Ascension = 10,
    # 每个种子跑几局（跑完所有局才进下一个种子）。
    [int]$RunsPerSeed = 1,
    [string]$ScenarioId = "AUTO-RUN-BATCH",
    # 每局最后兜底上限（秒）。监控会在真卡死/自然结束后自己退出，健康慢跑不会被此值砍；
    # 只有进程彻底挂死/被挂起才轮到它。
    [int]$TimeoutSeconds = 3600,
    # 整局存活监控阈值，透传给游戏侧（见 UnattendedTestProtocol.FullRun*Seconds）。
    [double]$FullRunFrameStallSeconds = 30,
    [double]$FullRunNoProgressSeconds = 120,
    [string]$CollectRoot = ".local\auto-run",
    [switch]$NoTelemetry,
    [switch]$Background
)

$ErrorActionPreference = "Stop"

$seedList = @($Seeds -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($seedList.Count -eq 0) { throw "No seeds given (use -Seeds, ';'-separated)." }
if ($RunsPerSeed -lt 1) { throw "RunsPerSeed must be >= 1." }

$toolsDir = $PSScriptRoot
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$headlessRoot = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "CombatSolver\headless-runtime"
$telemetryDir = Join-Path $headlessRoot "Roaming\SlayTheSpire2\run_telemetry"
$resultJsonPath = Join-Path $headlessRoot "Roaming\SlayTheSpire2\combat_solver_test_result.json"

if (-not [System.IO.Path]::IsPathRooted($CollectRoot)) {
    $CollectRoot = Join-Path $repoRoot $CollectRoot
}

if ($Background) {
    New-Item -ItemType Directory -Path $headlessRoot -Force | Out-Null
    $runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $outLog = Join-Path $headlessRoot "autorun-$runId.out.log"
    $errLog = Join-Path $headlessRoot "autorun-$runId.err.log"
    $pidFile = Join-Path $headlessRoot "autorun-$runId.pid"

    $innerArgs = @("-NoProfile", "-File", "`"$PSCommandPath`"")
    $innerArgs += "-Seeds", "`"$Seeds`""
    $innerArgs += "-CharacterId", "`"$CharacterId`""
    $innerArgs += "-Ascension", "$Ascension"
    $innerArgs += "-RunsPerSeed", "$RunsPerSeed"
    $innerArgs += "-ScenarioId", "`"$ScenarioId`""
    $innerArgs += "-TimeoutSeconds", "$TimeoutSeconds"
    $innerArgs += "-FullRunFrameStallSeconds", "$FullRunFrameStallSeconds"
    $innerArgs += "-FullRunNoProgressSeconds", "$FullRunNoProgressSeconds"
    $innerArgs += "-CollectRoot", "`"$CollectRoot`""
    if ($NoTelemetry) { $innerArgs += "-NoTelemetry" }

    $proc = Start-Process -FilePath "pwsh" -ArgumentList $innerArgs `
        -WindowStyle Hidden -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru
    Set-Content -LiteralPath $pidFile -Value $proc.Id -Encoding UTF8
    Write-Host "Started background auto-run batch $runId (pid=$($proc.Id))"
    Write-Host "  out: $outLog"
    Write-Host "  err: $errLog"
    Write-Host "  pid: $pidFile"
    Write-Host "  summary: $(Join-Path $CollectRoot 'summary.jsonl')"
    exit 0
}

New-Item -ItemType Directory -Path $CollectRoot -Force | Out-Null
$summaryPath = Join-Path $CollectRoot "summary.jsonl"
$runScript = Join-Path $toolsDir "run-unattended-test.ps1"

. (Join-Path $toolsDir "mod-isolation.ps1")
$summary = New-Object System.Collections.Generic.List[object]
$utcStamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

try {
    $null = Backup-ThirdPartyMods
    $total = $seedList.Count * $RunsPerSeed
    Write-Host ("Third-party mods isolated. Running $total run(s) " +
        "(char=$CharacterId asc=$Ascension timeoutBackstop=${TimeoutSeconds}s " +
        "frameStall=${FullRunFrameStallSeconds}s noProgress=${FullRunNoProgressSeconds}s)...")

    $index = 0
    foreach ($seed in $seedList) {
        for ($run = 1; $run -le $RunsPerSeed; $run++) {
            $index++
            Write-Host "[$index/$total] seed=$seed run=$run/$RunsPerSeed start=$((Get-Date).ToUniversalTime().ToString('HH:mm:ss'))"

            # 上一局残留结果先删掉：同 seed 重复跑局时，seed 匹配不足以防 stale。
            Remove-Item -LiteralPath $resultJsonPath -Force -ErrorAction SilentlyContinue

            $beforeTelemetry = @(Get-ChildItem -LiteralPath $telemetryDir -File -ErrorAction SilentlyContinue |
                ForEach-Object { $_.FullName })

            $entry = [ordered]@{
                seed       = $seed
                run        = $run
                startedUtc = $utcStamp
            }

            $childArgs = @(
                "-NoProfile", "-File", $runScript,
                "-ScenarioId", $ScenarioId, "-RunAutoFullRun",
                "-Seed", $seed, "-CharacterId", $CharacterId, "-Ascension", "$Ascension",
                "-TimeoutSeconds", "$TimeoutSeconds",
                "-FullRunFrameStallSeconds", "$FullRunFrameStallSeconds",
                "-FullRunNoProgressSeconds", "$FullRunNoProgressSeconds",
                "-ExitOnComplete"
            )
            if (-not $NoTelemetry) {
                $childArgs += "-RunAutoTelemetryEnabled"
            }
            & pwsh @childArgs
            $entry.exitCode = $LASTEXITCODE

            # 本局结果：child 退出后读（已删残留 → 有文件即本局）。
            if (Test-Path -LiteralPath $resultJsonPath) {
                try {
                    $res = Get-Content -Raw -LiteralPath $resultJsonPath | ConvertFrom-Json
                    $entry.status = $res.status
                    $entry.elapsedMs = [int64]$res.elapsedMilliseconds
                    $entry.victory = $res.victory
                    $entry.abandoned = $res.abandoned
                    $entry.rooms = $res.roomsHandled
                    $entry.act = $res.actReached
                    if (-not [string]::IsNullOrWhiteSpace([string]$res.stuckDetail)) {
                        # Stuck 诊断首行即可（"原因\n快照"）。
                        $entry.stuck = ([string]$res.stuckDetail -split "`n")[0]
                    }
                }
                catch {
                    $entry.status = "UnreadableResult"
                }
            }
            else {
                # 无结果 JSON：按退出码归类（1 = child launcher 自身失败/兜底超时；130 = 取消）。
                $entry.status = if ($entry.exitCode -eq 130) { "Cancelled" } else { "NoResult" }
            }

            # 收集本局遥测（可选）：跑后新增的同目录最新一个。
            if (-not $NoTelemetry) {
                $after = @(Get-ChildItem -LiteralPath $telemetryDir -File -ErrorAction SilentlyContinue |
                    ForEach-Object { $_.FullName })
                $newFiles = @($after | Where-Object { $_ -notin $beforeTelemetry })
                $telemetryFile = $newFiles |
                    Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } -Descending |
                    Select-Object -First 1
                if ($telemetryFile) {
                    $safeSeed = $seed -replace '[^A-Za-z0-9._-]', '_'
                    $dest = Join-Path $CollectRoot "${safeSeed}__run${run}.json"
                    Copy-Item -LiteralPath $telemetryFile -Destination $dest -Force
                    $entry.telemetry = Split-Path -Leaf $dest
                }
            }

            $summary.Add([pscustomobject]$entry)
            Write-Host ("  -> " + ($entry | ConvertTo-Json -Compress))
        }
    }
}
finally {
    $failed = Restore-ThirdPartyMods
    if ($failed -gt 0) {
        Write-Warning "$failed mod(s) failed to restore."
    }
}

$summary | ForEach-Object { $_ | ConvertTo-Json -Compress } |
    Set-Content -LiteralPath $summaryPath -Encoding UTF8

# 聚合。
$byStatus = $summary | Group-Object status | Sort-Object Name
$natural = @($summary | Where-Object { $_.status -eq "Passed" })
$wins = @($natural | Where-Object { $_.victory })
$stuck = @($summary | Where-Object { $_.status -eq "Stuck" })
$failed = @($summary | Where-Object { $_.status -eq "Failed" -or $_.status -eq "NoResult" -or $_.status -eq "UnreadableResult" })

Write-Host ""
Write-Host ("BATCH_SUMMARY total=$total wins=$($wins.Count) naturalEnd=$($natural.Count) " +
    "stuck=$($stuck.Count) failed=$($failed.Count)")
$byStatus | ForEach-Object {
    Write-Host ("  status {0,-14} {1}" -f $_.Name, $_.Count)
}
if ($wins.Count -gt 0) {
    $winRooms = @($wins | Where-Object { $_.rooms } | ForEach-Object { [int]$_.rooms })
    $winActs = @($wins | Where-Object { $_.act } | ForEach-Object { [int]$_.act })
    $avgRooms = if ($winRooms.Count -gt 0) { [math]::Round(($winRooms | Measure-Object -Average).Average, 1) } else { "-" }
    $avgAct = if ($winActs.Count -gt 0) { [math]::Round(($winActs | Measure-Object -Average).Average, 1) } else { "-" }
    Write-Host ("  wins avg_rooms=$avgRooms avg_actReached=$avgAct")
}
if ($stuck.Count -gt 0) {
    Write-Warning "BATCH_STUCK_RUNS: $($stuck.Count) run(s) hit liveness monitor (see summary.jsonl 'stuck')."
}
Write-Host "Summary written: $summaryPath"

if ($natural.Count -eq $total -and $stuck.Count -eq 0 -and $failed.Count -eq 0) {
    Write-Host "BATCH_OK (all $total runs ended naturally; wins=$($wins.Count))"
    exit 0
}
exit 1
