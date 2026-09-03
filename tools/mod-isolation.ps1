#requires -Version 7.0
# 第三方 mod 隔离共享模块。
#
# 背景（已实证）：游戏按 mods/ 内的 manifest 扫描加载 mod，**忽略文件夹名**。
# 把 mod 文件夹改名 .disabled-for-smoke 不会禁用它们——manifest 还在文件夹里，
# 游戏照样加载（日志可见 "Found mod manifest file ...\*.disabled-for-smoke\*.json"）。
# 正确做法：把第三方 mod 整个目录**移出** mods/，跑完再移回。
#
# 本模块提供 Backup-ThirdPartyMods / Restore-ThirdPartyMods，冒烟与批量跑局共用。
# 备份目录放在 headless 运行目录下（%LOCALAPPDATA%\CombatSolver\headless-runtime\mod-backup），
# 与游戏 mods/ 无关，游戏不会扫描它。

param(
    [string]$ModsRoot = "D:\Steam\steamapps\common\Slay the Spire 2\mods",
    [string]$BackupRoot = (Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "CombatSolver\headless-runtime\mod-backup")
)

# 必须保留的 mod（本 mod 与 headless RitsuLib 加载器）。
$script:KeepMods = @("CombatSolver", ".combatsolver-headless-ritsulib", ".combatsolver-headless-ritsulib-w1", ".combatsolver-headless-ritsulib-w2", ".combatsolver-headless-ritsulib-w3")

# 遗留的坏命名后缀（早期改名隔离的产物）；备份时剥掉，恢复回原名。
$script:DisabledSuffix = ".disabled-for-smoke"

$script:ManifestPath = Join-Path $BackupRoot "manifest.csv"

function Assert-IsolationBackupPath {
    $full = [IO.Path]::GetFullPath($BackupRoot)
    $modsFull = [IO.Path]::GetFullPath($ModsRoot)
    if ($full.StartsWith($modsFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "备份目录不能位于 mods/ 内（会被游戏扫描）: $BackupRoot"
    }
    if ($full -eq $modsFull) {
        throw "备份目录不能等于 mods/ 目录: $BackupRoot"
    }
}

function Backup-ThirdPartyMods {
    <#
    .SYNOPSIS
    把所有非保留第三方 mod 目录移出 mods/，写入恢复清单。
    .OUTPUTS
    int — 移出的 mod 数量。
    #>
    Assert-IsolationBackupPath
    if (-not (Test-Path -LiteralPath $ModsRoot -PathType Container)) {
        throw "mods 目录不存在: $ModsRoot"
    }
    # 自动恢复上次中断残留的隔离现场，再重新隔离（训练批量循环必须对中断健壮）。
    if ((Test-Path -LiteralPath $ManifestPath) -or (Test-Path -LiteralPath $BackupRoot)) {
        Write-Warning "发现未恢复的隔离现场（可能上次中断），先恢复再隔离。备份根: $BackupRoot"
        Restore-ThirdPartyMods
    }
    New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null

    $targets = Get-ChildItem -LiteralPath $ModsRoot -Directory | Where-Object {
        $_.Name -notin $script:KeepMods
    }
    $entries = @()
    foreach ($dir in $targets) {
        # 剥掉遗留后缀得到恢复名（原 mod 名）。
        $restoreName = $dir.Name
        if ($restoreName.EndsWith($script:DisabledSuffix)) {
            $restoreName = $restoreName.Substring(0, $restoreName.Length - $script:DisabledSuffix.Length)
        }
        $originalPath = Join-Path $ModsRoot $restoreName
        if ($originalPath -ne $dir.FullName -and (Test-Path -LiteralPath $originalPath)) {
            throw "恢复目标已存在，无法安全隔离（源名 `"$($dir.Name)`" -> 恢复名 `"$restoreName`" 冲突）: $originalPath"
        }
        $dest = Join-Path $BackupRoot $dir.Name
        Move-Item -LiteralPath $dir.FullName -Destination $dest
        $entries += [pscustomobject]@{
            Original = $originalPath
            Backup   = $dest
        }
        Write-Host "ISOLATED: $($dir.Name)"
    }
    $entries | Export-Csv -LiteralPath $script:ManifestPath -NoTypeInformation -Encoding UTF8
    return $entries.Count
}

function Restore-ThirdPartyMods {
    <#
    .SYNOPSIS
    按恢复清单把所有隔离的 mod 移回 mods/，并逐个验证。
    .OUTPUTS
    int — 恢复失败的数量（0 = 全部成功）。
    #>
    if (-not (Test-Path -LiteralPath $script:ManifestPath -PathType Leaf)) {
        Write-Host "No isolation manifest; nothing to restore."
        return 0
    }
    $entries = Import-Csv -LiteralPath $script:ManifestPath
    $failed = 0
    foreach ($entry in $entries) {
        $original = $entry.Original
        $backup = $entry.Backup
        if (-not (Test-Path -LiteralPath $backup)) {
            Write-Warning "MISSING BACKUP (cannot restore): $backup  (original: $original)"
            $failed++
            continue
        }
        if (Test-Path -LiteralPath $original) {
            Write-Warning "Original already exists, discarding backup: $original"
            Remove-Item -LiteralPath $backup -Recurse -Force
            continue
        }
        try {
            Move-Item -LiteralPath $backup -Destination $original
        } catch {
            Write-Warning "RESTORE FAILED: $original  ($($_.Exception.Message))"
            $failed++
            continue
        }
        if (Test-Path -LiteralPath $original) {
            Write-Host "RESTORED: $(Split-Path -Leaf $original)"
        } else {
            Write-Warning "RESTORE FAILED (moved but missing): $original"
            $failed++
        }
    }
    if ($failed -eq 0) {
        Remove-Item -LiteralPath $script:ManifestPath -Force -ErrorAction SilentlyContinue
        if (@(Get-ChildItem -LiteralPath $BackupRoot -Force -ErrorAction SilentlyContinue).Count -eq 0) {
            Remove-Item -LiteralPath $BackupRoot -Force -ErrorAction SilentlyContinue
        }
        Write-Host "All isolated mods restored."
    } else {
        Write-Warning "$failed mod(s) failed to restore. Manifest kept at $script:ManifestPath"
    }
    return $failed
}

