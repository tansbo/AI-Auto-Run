#requires -Version 7.0
# 5 角色卡牌评分验证：对每个可玩角色（IRONCLAD/SILENT/DEFECT/NECROBINDER/REGENT）
# 开新局跑同一组评分断言（打击/防御跳过、战吼评分 31、低血量防御倾向、AOE/攻击比例/
# Power 加成、遗物与先古遗物），并把每张候选的实际评分打到日志（PICKER_SCORES）。
#
# 期望值对 5 个角色通用：各角色初始牌组都含 >=3 张同名打击/防御（重复惩罚 -10 生效）、
# 无 AOE、攻击比例 >0.3（无攻击比例加成），起始特殊牌均为单目标基础牌。
#
# 用法：pwsh -NoProfile -File tools\run-picker-checks-all-chars.ps1
param(
    [int]$TimeoutSeconds = 150
)

$ErrorActionPreference = "Stop"
$toolsDir = $PSScriptRoot

# 各角色初始牌组攻击数不同（Ironclad 6 / Silent 6 / Defect 4 / Necrobinder 5 / Regent 5）。
# 注入 12 防御 + 3 头槌后，攻击比例 Defect=7/25=0.28<0.3 触发攻击比例加成 +9，其余角色 ≥0.32 不触发，
# 因此 3 个比例敏感检查按角色给出期望值（Defect 不同）。
$characters = @(
    @{ Id = "IRONCLAD";   Strike = "STRIKE_IRONCLAD";    Defend = "DEFEND_IRONCLAD";    Dupes = 1;  Act1Conf = 29; Act1Strike = -9 },
    @{ Id = "SILENT";     Strike = "STRIKE_SILENT";      Defend = "DEFEND_SILENT";      Dupes = 1;  Act1Conf = 29; Act1Strike = -9 },
    @{ Id = "DEFECT";     Strike = "STRIKE_DEFECT";      Defend = "DEFEND_DEFECT";      Dupes = 10; Act1Conf = 38; Act1Strike = 0 },
    @{ Id = "NECROBINDER"; Strike = "STRIKE_NECROBINDER"; Defend = "DEFEND_NECROBINDER"; Dupes = 1;  Act1Conf = 29; Act1Strike = -9 },
    @{ Id = "REGENT";     Strike = "STRIKE_REGENT";      Defend = "DEFEND_REGENT";      Dupes = 1;  Act1Conf = 29; Act1Strike = -9 }
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
  {"kind":"Card","optionIds":["BATTLE_TRANCE"],"expectedScore":31},
  {"kind":"Card","optionIds":["$defend"],"playerHp":20,"playerMaxHp":80,"expectedScore":-4},
  {"kind":"Card","optionIds":["CONFLAGRATION"],"playerHp":80,"playerMaxHp":80,"expectedScore":31},
  {"kind":"Card","optionIds":["HEADBUTT"],"deckCardIds":[$defend12],"playerHp":80,"playerMaxHp":80,"expectedScore":20},
  {"kind":"Card","optionIds":["HEADBUTT"],"deckCardIds":["HEADBUTT","HEADBUTT","HEADBUTT"],"playerHp":80,"playerMaxHp":80,"expectedScore":$($ch.Dupes)},
  {"kind":"Card","optionIds":["BARRICADE"],"playerHp":80,"playerMaxHp":80,"expectedScore":15},
  {"kind":"Relic","optionIds":["ANCHOR","ART_OF_WAR"],"expectedPickId":"ART_OF_WAR"},
  {"kind":"Relic","optionIds":["ANCHOR","RUNIC_PYRAMID"],"expectedPickId":"RUNIC_PYRAMID"},
  {"kind":"Relic","optionIds":["RUNIC_PYRAMID"],"expectedScore":21},
  {"kind":"AncientRelic","optionIds":["CURSED_PEARL","GOLDEN_PEARL"],"expectedPickId":"GOLDEN_PEARL"},
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
