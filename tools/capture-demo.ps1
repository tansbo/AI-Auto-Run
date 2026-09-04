# 按游戏窗口矩形抓屏（主屏无关），供 AI Auto-Run 演示 GIF。
param(
    [string]$FrameDir = 'D:\JAVA_WorkPlace\AIWithCombatSolver\.local\demo-frames2',
    [int]$Frames = 150,
    [int]$IntervalMs = 650,
    [int]$WarmupMs = 12000
)
$ErrorActionPreference = 'Stop'
$visUser = "$env:APPDATA\SlayTheSpire2"
New-Item -ItemType Directory -Path $FrameDir -Force | Out-Null
Get-ChildItem $FrameDir -Filter '*.png' | Remove-Item -Force -ErrorAction SilentlyContinue
Stop-Process -Name SlayTheSpire2 -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$runId = [Guid]::NewGuid().ToString('N')
$req = [ordered]@{ schemaVersion=1; runId=$runId; scenarioId='DEMO-CAP3'; characterId='IRONCLAD'; encounterId='FUZZY_WURM_CRAWLER_WEAK'; seed='COMBATSOLVER'; timeoutSeconds=1800; runAutoFullRun=$true; runAutoTelemetryEnabled=$true; exitOnComplete=$false }
$req | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $visUser 'combat_solver_test_request.json') -Encoding UTF8
Start-Process 'steam://rungameid/2868840'
$proc = $null
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 4
    $proc = Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($proc) { break }
}
if (-not $proc) { throw 'game window not up' }
Write-Host "window pid=$($proc.Id) handle=$($proc.MainWindowHandle)"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
public class Win32 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@
Start-Sleep -Milliseconds $WarmupMs
Add-Type -AssemblyName System.Drawing
[Win32]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
$idx = 0
for ($i = 0; $i -lt $Frames; $i++) {
    $p = Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { break }
    $rect = New-Object RECT
    [Win32]::GetWindowRect($p.MainWindowHandle, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { Start-Sleep -Milliseconds 300; continue }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $bmp.Save((Join-Path $FrameDir ("f{0:D3}.png" -f $idx)), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $idx++
    Start-Sleep -Milliseconds $IntervalMs
}
Write-Host "captured $idx frames"
