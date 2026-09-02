#requires -Version 7.0
# 多种子可见实机整局回归：对 (角色, 种子) 列表逐对跑 RunAutoFullRun 完整局（可见 Steam 会话），
# 每局等结果 JSON（runId 匹配）或进程退出；进度写 .local\regression\progress.jsonl 可断点续跑。
#
# 用法：pwsh -NoProfile -File tools\run-visible-regression.ps1
param(
    [int]$PerRunTimeoutSeconds = 1500
)

$ErrorActionPreference = "Stop"
$toolsDir = $PSScriptRoot
$visUser = "$env:APPDATA\SlayTheSpire2"
$logPath = Join-Path $visUser "logs\godot.log"
$reqPath = Join-Path $visUser "combat_solver_test_request.json"
$resPath = Join-Path $visUser "combat_solver_test_result.json"
$progressDir = "D:\JAVA_WorkPlace\AIWithCombatSolver\.local\regression"
$progressFile = Join-Path $progressDir "progress.jsonl"
$summaryFile = Join-Path $progressDir "summary.txt"
New-Item -ItemType Directory -Path $progressDir -Force | Out-Null

# 本轮要回归的角色/种子（可追加；已完成的行会跳过）
$runs = @(
    @{ Character = "IRONCLAD";    Seed = "COMBATSOLVER" },
    @{ Character = "NECROBINDER"; Seed = "4634LZP01FBE" },
    @{ Character = "SILENT";      Seed = "SILENTSEED" },
    @{ Character = "DEFECT";      Seed = "DEFECTSEED" },
    @{ Character = "REGENT";      Seed = "REGENTSEED" }
)

$done = @()
if (Test-Path $progressFile) {
    $done = @(Get-Content $progressFile | ForEach-Object { try { ($_ | ConvertFrom-Json).key } catch { $null } })
}

. (Join-Path $toolsDir "mod-isolation.ps1")
$moved = Backup-ThirdPartyMods
Write-Host "Isolated $moved third-party mod(s). 已有完成: $($done -join ', ')"

try {
    foreach ($r in $runs) {
        $key = "$($r.Character)::$($r.Seed)"
        if ($key -in $done) { Write-Host "跳过已完成: $key"; continue }
        Write-Host ""
        Write-Host "=== [$key] 启动（$(Get-Date -Format 'HH:mm:ss')）==="
        $runId = [Guid]::NewGuid().ToString('N')
        $req = [ordered]@{
            schemaVersion = 1
            runId = $runId
            scenarioId = "REG-$($r.Character)"
            characterId = $r.Character
            encounterId = "FUZZY_WURM_CRAWLER_WEAK"
            seed = $r.Seed
            timeoutSeconds = $PerRunTimeoutSeconds
            runAutoFullRun = $true
            runAutoTelemetryEnabled = $true
            exitOnComplete = $true
        }
        $req | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reqPath -Encoding UTF8
        Start-Process 'steam://rungameid/2868840'

        # 轮询等结果（runId 匹配）或进程退出；每 30s 一次。
        $deadline = (Get-Date).AddMinutes([Math]::Ceiling(($PerRunTimeoutSeconds + 600) / 60.0))
        $entry = $null
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 30
            if (Test-Path -LiteralPath $resPath) {
                $res = Get-Content -LiteralPath $resPath -Raw | ConvertFrom-Json
                if ($res.runId -eq $runId) {
                    $entry = [ordered]@{
                        key = $key; runId = $runId; status = $res.status
                        elapsedMs = $res.elapsedMilliseconds
                        error = if ($res.error) { ($res.error -split "`n")[0] } else { "" }
                    }
                    break
                }
            }
            if (-not (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue)) { break }
        }
        if (-not $entry) {
            $entry = [ordered]@{ key = $key; runId = $runId; status = "NO-RESULT"; elapsedMs = 0; error = "进程退出但无结果（可能被杀/超时）" }
        }
        # 遥测结果
        $telemetry = Get-ChildItem (Join-Path $visUser "run_telemetry") -Filter "*$($r.Seed)*" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($telemetry) {
            $t = Get-Content $telemetry.FullName -Raw | ConvertFrom-Json
            $entry.floors = $t.floors
            $entry.actReached = $t.actReached
            $entry.victory = $t.victory
            $entry.roomsHandled = $t.roomsHandled
        }
        $entry | ConvertTo-Json -Compress | Add-Content -LiteralPath $progressFile -Encoding UTF8
        Write-Host "结果: $($entry | ConvertTo-Json -Compress)"
        # 游戏若未退出则关掉，准备下一局
        Stop-Process -Name SlayTheSpire2 -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 5
        @('combat_solver_test_request.json','combat_solver_test_running.json','combat_solver_test_result.json','combat_solver_test_ready.json') | ForEach-Object {
            $p = Join-Path $visUser $_; if (Test-Path $p) { Remove-Item $p -Force }
        }
    }
    Write-Host ""
    Write-Host "=== 汇总 ==="
    Get-Content $progressFile | ForEach-Object { ($_ | ConvertFrom-Json) | Select-Object key, status, elapsedMs, floors, actReached, victory | Format-Table -AutoSize | Out-String } | Set-Content $summaryFile
    Get-Content $progressFile | ForEach-Object { ($_ | ConvertFrom-Json) | Select-Object key, status, floors, actReached, victory } | Format-Table -AutoSize
}
finally {
    Stop-Process -Name SlayTheSpire2 -Force -ErrorAction SilentlyContinue
    $failed = Restore-ThirdPartyMods
    Write-Host "restore_failed=$failed (游戏已关闭，mod 已恢复)"
}