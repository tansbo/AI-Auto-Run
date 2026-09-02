#requires -Version 7.0
# 种子重放 A/B 批量跑局：对每个种子 × 每个策略跑一局 headless 整局。
# 每局调 run-unattended-test.ps1（-RunAutoFullRun -Seed <s> -RunAutoForcedPicks <p> -RunAutoTelemetryEnabled），
# 跑后把本局新增的遥测 JSON 复制到 CollectRoot/{seed}__{policySafe}.json，并追加 summary.jsonl 一行。
# 复用 mod-isolation.ps1（第三方 mod 整个目录移出/恢复）与无声参数（--audio-driver Dummy）。
#
# 策略是完整强制抓牌串（"cardId:take,cardId:skip" 格式）。A/B 对照对即同种子下
# "<id>:take" 与 "<id>:skip" 两局；若某策略的 cardId 在整局里从未出现在奖励里，
# forcedCount=0，该对无效（两局完全相同）——看 summary.jsonl 的 forcedCount 判断。
#
# -Background 开关：detach 到隐藏 pwsh 进程，stdout/stderr 落盘到
#   <headlessRoot>\seedab-<id>.out.log / .err.log，写 pid 标记后立即返回。

[CmdletBinding()]
param(
    # 列表用分号分隔（策略本身含逗号，不能用逗号分隔列表）。如 -Seeds "COMBATSOLVER;SEED2"。
    [string]$Seeds = "COMBATSOLVER",
    [string]$Policies = "clash:take;clash:skip",
    [string]$ScenarioId = "RUN-AB-BATCH",
    [int]$TimeoutSeconds = 600,
    [string]$CollectRoot = ".local\seed-ab",
    [switch]$Background
)

$ErrorActionPreference = "Stop"

$seedList = @($Seeds -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$policyList = @($Policies -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($seedList.Count -eq 0) { throw "No seeds given (use -Seeds, ';'-separated)." }
if ($policyList.Count -eq 0) { throw "No policies given (use -Policies, ';'-separated)." }

$toolsDir = $PSScriptRoot
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$headlessRoot = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "CombatSolver\headless-runtime"
$telemetryDir = Join-Path $headlessRoot "Roaming\SlayTheSpire2\run_telemetry"
$resultJsonPath = Join-Path $headlessRoot "Roaming\SlayTheSpire2\combat_solver_test_result.json"

# CollectRoot 相对路径相对仓库根解析。
if (-not [System.IO.Path]::IsPathRooted($CollectRoot)) {
    $CollectRoot = Join-Path $repoRoot $CollectRoot
}

if ($Background) {
    New-Item -ItemType Directory -Path $headlessRoot -Force | Out-Null
    $runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $outLog = Join-Path $headlessRoot "seedab-$runId.out.log"
    $errLog = Join-Path $headlessRoot "seedab-$runId.err.log"
    $pidFile = Join-Path $headlessRoot "seedab-$runId.pid"

    $innerArgs = @("-NoProfile", "-File", "`"$PSCommandPath`"")
    $innerArgs += "-Seeds", "`"$Seeds`""
    $innerArgs += "-Policies", "`"$Policies`""
    $innerArgs += "-ScenarioId", "`"$ScenarioId`""
    $innerArgs += "-TimeoutSeconds", "$TimeoutSeconds"
    $innerArgs += "-CollectRoot", "`"$CollectRoot`""

    $proc = Start-Process -FilePath "pwsh" -ArgumentList $innerArgs `
        -WindowStyle Hidden -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru
    Set-Content -LiteralPath $pidFile -Value $proc.Id -Encoding UTF8
    Write-Host "Started background A/B batch $runId (pid=$($proc.Id))"
    Write-Host "  out: $outLog"
    Write-Host "  err: $errLog"
    Write-Host "  pid: $pidFile"
    Write-Host "  summary: $(Join-Path $CollectRoot 'summary.jsonl')"
    exit 0
}

New-Item -ItemType Directory -Path $CollectRoot -Force | Out-Null
$summaryPath = Join-Path $CollectRoot "summary.jsonl"

. (Join-Path $toolsDir "mod-isolation.ps1")
$summary = New-Object System.Collections.Generic.List[object]

try {
    $moved = Backup-ThirdPartyMods
    $total = $seedList.Count * $policyList.Count
    Write-Host "Isolated $moved third-party mod(s). Running $($seedList.Count) seeds x $($policyList.Count) policies (timeout=${TimeoutSeconds}s each)..."
    $index = 0
    foreach ($seed in $seedList) {
        foreach ($policy in $policyList) {
            $index++
            $policySafe = $policy -replace '[^A-Za-z0-9._-]', '_'
            Write-Host "[$index/$total] $seed :: $policy ..."
            $before = @(Get-ChildItem -LiteralPath $telemetryDir -File -ErrorAction SilentlyContinue |
                ForEach-Object { $_.FullName })

            & pwsh -NoProfile -File (Join-Path $toolsDir "run-unattended-test.ps1") `
                -ScenarioId $ScenarioId -RunAutoFullRun -Seed $seed `
                -RunAutoForcedPicks $policy -RunAutoTelemetryEnabled `
                -TimeoutSeconds $TimeoutSeconds -ExitOnComplete
            $exitCode = $LASTEXITCODE

            $entry = [ordered]@{
                seed     = $seed
                policy   = $policy
                exitCode = $exitCode
                status   = if ($exitCode -eq 0) { "Passed" } else { "Failed" }
            }

            if (Test-Path -LiteralPath $resultJsonPath) {
                try {
                    $res = Get-Content -Raw -LiteralPath $resultJsonPath | ConvertFrom-Json
                    # 超时局不会写新结果 JSON，会读到上一局残留 → 用 seed 匹配防止 stale 数据。
                    if ($res.seed -eq $seed) {
                        $entry.result = $res.status
                        $entry.elapsedMs = $res.elapsedMilliseconds
                    }
                    else {
                        $entry.result = "no-result"
                    }
                }
                catch {
                    $entry.result = "unknown"
                }
            }

            # 收集本局遥测：跑后新增的同 seed JSON 里最新的一个。
            $after = @(Get-ChildItem -LiteralPath $telemetryDir -File -ErrorAction SilentlyContinue |
                ForEach-Object { $_.FullName })
            $newFiles = @($after | Where-Object { $_ -notin $before })
            $telemetryFile = $newFiles |
                Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } -Descending |
                Select-Object -First 1
            if ($telemetryFile) {
                $dest = Join-Path $CollectRoot "$seed`_`_$policySafe.json"
                Copy-Item -LiteralPath $telemetryFile -Destination $dest -Force
                $entry.telemetry = Split-Path -Leaf $dest
                try {
                    $telem = Get-Content -Raw -LiteralPath $dest | ConvertFrom-Json
                    $entry.victory = $telem.victory
                    $entry.floors = $telem.floors
                    $entry.picks = @($telem.picks).Count
                    $entry.forcedCount = @($telem.picks | Where-Object { $_.forced }).Count
                }
                catch {
                    $entry.forcedCount = 0
                }
            }
            else {
                $entry.telemetry = ""
                $entry.forcedCount = 0
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
Write-Host "Summary written: $summaryPath"
$failedRuns = @($summary | Where-Object { $_.status -ne "Passed" })
if ($failedRuns.Count -gt 0) {
    Write-Warning "BATCH_PARTIAL: $($failedRuns.Count)/$total run(s) failed."
    exit 1
}
Write-Host "BATCH_OK ($total runs)"
