#requires -Version 7.0
# 5 角色卡牌评分验证：对每个可玩角色（IRONCLAD/SILENT/DEFECT/NECROBINDER/REGENT）
# 开新局跑同一组评分断言（打击/防御跳过、战吼评分 31、低血量防御倾向、AOE/攻击比例/
# Power 加成、遗物与先古遗物），并把每张候选的实际评分打到日志（PICKER_SCORES）。
#
# 期望值对 5 个角色通用：各角色初始牌组都含 >=3 张同名打击/防御（重复惩罚 -10 生效）、
# 无 AOE、攻击比例 >0.3（无攻击比例加成），起始特殊牌均为单目标基础牌。
# 2026-09-04：战吼（抽牌轴，CardsVar）单卡期望按角色参数化——SILENT（12 张，含幸存者抽牌）与
# REGENT（10 张，含抽牌/能量轴起始牌）初始牌组带抽牌轴，触发体系契合 +2（32.845 → 34.845）；
# IRONCLAD/DEFECT/NECROBINDER 初始牌组无抽牌轴，维持 32.845。
#
# 用法：pwsh -NoProfile -File tools\run-picker-checks-all-chars.ps1
param(
    [int]$TimeoutSeconds = 150,
    [string]$CaptureLogDir = ""
)

$ErrorActionPreference = "Stop"
$toolsDir = $PSScriptRoot

$characters = @(
    @{ Id = "IRONCLAD";   Strike = "STRIKE_IRONCLAD";    Defend = "DEFEND_IRONCLAD";    Dupes = 0.91;  Def20 = -8.995; Act1Conf = 30.575; Act1Strike = -6.03;  Bt = 32.845 },
    @{ Id = "SILENT";     Strike = "STRIKE_SILENT";      Defend = "DEFEND_SILENT";      Dupes = 0.91;  Def20 = -4;     Act1Conf = 30.575; Act1Strike = -3.983; Bt = 34.845 },
    @{ Id = "DEFECT";     Strike = "STRIKE_DEFECT";      Defend = "DEFEND_DEFECT";      Dupes = 9.91;  Def20 = -9.13;  Act1Conf = 39.575; Act1Strike = -2.16;  Bt = 32.845 },
    @{ Id = "NECROBINDER"; Strike = "STRIKE_NECROBINDER"; Defend = "DEFEND_NECROBINDER"; Dupes = 0.91;  Def20 = -4;     Act1Conf = 30.575; Act1Strike = -7.47;  Bt = 32.845 },
    @{ Id = "REGENT";     Strike = "STRIKE_REGENT";      Defend = "DEFEND_REGENT";      Dupes = 0.91;  Def20 = -5.395; Act1Conf = 30.575; Act1Strike = -7.83;  Bt = 34.845 }
)

$failed = @()
foreach ($ch in $characters) {
    $strike = $ch.Strike
    $defend = $ch.Defend
    $defend12 = (1..12 | ForEach-Object { "`"$defend`"" }) -join ","
    $checks = @"
[
  {"kind":"Card","optionIds":["$strike"]},
  {"kind":"Card","optionIds":["$strike","$defend","BATTLE_TRANCE"],"expectedPickId":"BATTLE_TRANCE"},
  {"kind":"Card","optionIds":["BATTLE_TRANCE"],"expectedScore":$($ch.Bt)},
  {"kind":"Card","optionIds":["$defend"],"playerHp":20,"playerMaxHp":80,"expectedScore":$($ch.Def20)},
  {"kind":"Card","optionIds":["CONFLAGRATION"],"playerHp":80,"playerMaxHp":80,"expectedScore":32.575},
  {"kind":"Card","optionIds":["HEADBUTT"],"deckCardIds":[$defend12],"playerHp":80,"playerMaxHp":80,"expectedScore":19.91},
  {"kind":"Card","optionIds":["HEADBUTT"],"deckCardIds":["HEADBUTT","HEADBUTT","HEADBUTT"],"playerHp":80,"playerMaxHp":80,"expectedScore":$($ch.Dupes)},
  {"kind":"Card","optionIds":["BARRICADE"],"playerHp":80,"playerMaxHp":80,"expectedScore":20.085},
  {"kind":"Relic","optionIds":["ANCHOR","ART_OF_WAR"],"expectedPickId":"ART_OF_WAR"},
  {"kind":"Relic","optionIds":["ANCHOR","RUNIC_PYRAMID"],"expectedPickId":"RUNIC_PYRAMID"},
  {"kind":"Relic","optionIds":["RUNIC_PYRAMID"],"expectedScore":21},
  {"kind":"AncientRelic","optionIds":["CURSED_PEARL","GOLDEN_PEARL"],"expectedPickId":"CURSED_PEARL"},
  {"kind":"AncientRelic","optionIds":["NEOWS_BONES","GOLDEN_PEARL"],"expectedPickId":"GOLDEN_PEARL"},
  {"kind":"Card","optionIds":["CONFLAGRATION"],"actIndexForTest":1,"expectedScore":$($ch.Act1Conf)},
  {"kind":"Card","optionIds":["$strike"],"actIndexForTest":1,"expectedScore":$($ch.Act1Strike)}
]
"@
    Write-Host ""
    Write-Host "=== 角色 $($ch.Id) 评分验证 ==="
    & pwsh -NoProfile -File (Join-Path $toolsDir "run-unattended-test.ps1") `
        -ScenarioId "PICKER-AI-$($ch.Id)" -CharacterId $ch.Id -Seed "PICKER$($ch.Id)" `
        -PickerChecksJson $checks -TimeoutSeconds $TimeoutSeconds -ExitOnComplete 2>&1 | Select-String -Pattern 'PICKER_SCORES|PICKER PASSED|status|Failed|错误' | Select-Object -First 40 | ForEach-Object { $_.Line }
    if ($CaptureLogDir) {
        New-Item -ItemType Directory -Path $CaptureLogDir -Force | Out-Null
        $hl = Join-Path $env:LOCALAPPDATA "CombatSolver\headless-runtime\godot-headless.log"
        if (Test-Path $hl) { Copy-Item $hl (Join-Path $CaptureLogDir "$($ch.Id).log") -Force }
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "!!! $($ch.Id) 失败 (exit=$LASTEXITCODE)"
        $failed += $ch.Id
    }
}

Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "ALL_CHARS_OK (5 角色评分验证全部通过)"
    exit 0
} else {
    Write-Host "ALL_CHARS_PARTIAL: 失败角色 = $($failed -join ',')"
    exit 1
}
