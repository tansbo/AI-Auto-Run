# 决策触发抓帧：仅当跑局日志出现 AI 决策行（地图选路/选牌/事件/遗物/奖励）时抓屏，
# 产出"AI 正在做决定"的帧序列，供演示剪辑（战斗时间不抓）。
param(
    [string]$Log = "$env:APPDATA\SlayTheSpire2\logs\godot.log",
    [string]$OutDir = 'D:\JAVA_WorkPlace\AIWithCombatSolver\.local\decision-frames',
    [int]$HoldMs = 2600,
    [int]$MaxCaptures = 40,
    [int]$TotalSeconds = 900
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Get-ChildItem $OutDir -Filter '*.png' | Remove-Item -Force -ErrorAction SilentlyContinue
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
public class W32 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[W32]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Drawing
# 决策行模式（日志行包含这些关键词才算决策瞬间）
$patterns = '地图选路|选牌 |事件：|领取奖励|篝火：|宝箱遗物|遗物评分|商店：|移除'
$seen = 0
$lastPos = 0
$started = Get-Date
$deadline = $started.AddSeconds($TotalSeconds)
$idx = 0
while ((Get-Date) -lt $deadline -and $seen -lt $MaxCaptures) {
    if (Test-Path $Log) {
        $len = (Get-Item $Log).Length
        if ($len -gt $lastPos) {
            $fs = [System.IO.File]::Open($Log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            try {
                $fs.Seek($lastPos, [System.IO.SeekOrigin]::Begin) | Out-Null
                $sr = New-Object System.IO.StreamReader($fs)
                $new = $sr.ReadToEnd()
                $lastPos = $fs.Position
                $sr.Dispose()
            } finally { $fs.Dispose() }
            foreach ($line in ($new -split "`n")) {
                if ($line -match $patterns) {
                    # 决策行出现即抓（定格正让决策界面停留）；立即抓 3 帧
                    $p = Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
                    if (-not $p) { break }
                    [W32]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
                    Start-Sleep -Milliseconds 120
                    $rect = New-Object RECT
                    [W32]::GetWindowRect($p.MainWindowHandle, [ref]$rect) | Out-Null
                    $wd = $rect.Right - $rect.Left; $ht = $rect.Bottom - $rect.Top
                    if ($wd -le 100 -or $ht -le 100) { continue }
                    foreach ($k in 1..3) {
                        $bmp = New-Object System.Drawing.Bitmap $wd, $ht
                        $g = [System.Drawing.Graphics]::FromImage($bmp)
                        $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($wd, $ht)))
                        $g.Dispose()
                        $tag = ($line -replace '[^\p{IsCJKUnifiedIdeographs}A-Za-z0-9]', '') 
                        if ($tag.Length -gt 24) { $tag = $tag.Substring(0, 24) }
                        $bmp.Save((Join-Path $OutDir ("d{0:D3}-$tag.png" -f $idx)), [System.Drawing.Imaging.ImageFormat]::Png)
                        $bmp.Dispose()
                        $idx++
                        Start-Sleep -Milliseconds 450
                    }
                    $seen++
                    Write-Host "captured decision $seen : $($line.Trim().Substring(0,[Math]::Min(80,$line.Trim().Length)))"
                    if ($seen -ge $MaxCaptures) { break }
                }
            }
        }
    }
    Start-Sleep -Milliseconds 400
}
Write-Host "done captures=$seen idx=$idx"


