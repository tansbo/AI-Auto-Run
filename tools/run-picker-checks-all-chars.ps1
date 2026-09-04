#requires -Version 7.0
# 5 角色卡牌评分验证：对每个可玩角色（IRONCLAD/SILENT/DEFECT/NECROBINDER/REGENT）
# 开新局跑同一组评分断言（打击/防御跳过、战吼评分 31、低血量防御倾向、AOE/攻击比例/
# Power 加成、遗物与先古遗物），并把每张候选的实际评分打到日志（PICKER_SCORES）。
#
# 期望值对 5 个角色通用：各角色初始牌组都含 >=3 张同名打击/防御（重复惩罚 -10 生效）、
# 无 AOE、攻击比例 >0.3（无攻击比例加成），起始特殊牌均为单目标基础牌。
# 2026-09-04：跨职业（接收职业中位）数据驱动校准后，他职业候选卡（战吼/燃烧/头槌/壁垒）的
# 期望按角色各不相同：同池=对照自身池中位，跨池=对照接收职业中位（CardWinStats.BonusFor）。
# 另：SILENT（初始含幸存者抽牌）与 REGENT（起始特殊牌带抽牌/能量轴）带抽牌轴 → 战吼+2 体系契合。
#
# 用法：pwsh -NoProfile -File tools\run-picker-checks-all-chars.ps1
param(
    [int]$TimeoutSeconds = 150,
    [string]$CaptureLogDir = ""
)

$ErrorActionPreference = "Stop"
$toolsDir = $PSScriptRoot

$characters = @(
    @{ Id = "IRONCLAD";   Strike = "STRIKE_IRONCLAD";    Defend = "DEFEND_IRONCLAD";    Def20 = -8.995; Bt = 37.845;  Conf = 36.575; Head12 = 23.91;  Dupes = 4.91;  Barr = 20.085; Act1Conf = 30.575; Act1Strike = -6.03;   ComboFinale = 39.755 },
    @{ Id = "SILENT";     Strike = "STRIKE_SILENT";      Defend = "DEFEND_SILENT";      Def20 = -4;     Bt = 39.057;  Conf = 35.787; Head12 = 23.122; Dupes = 4.122; Barr = 19.297; Act1Conf = 29.787; Act1Strike = -3.983;  ComboFinale = 40.967 },
    @{ Id = "DEFECT";     Strike = "STRIKE_DEFECT";      Defend = "DEFEND_DEFECT";      Def20 = -9.13;  Bt = 39.96;   Conf = 38.69;  Head12 = 26.025; Dupes = 16.025; Barr = 22.2;   Act1Conf = 41.69;  Act1Strike = -2.16;   ComboFinale = 50.87 },
    @{ Id = "NECROBINDER"; Strike = "STRIKE_NECROBINDER"; Defend = "DEFEND_NECROBINDER"; Def20 = -4;     Bt = 36.585;  Conf = 35.315; Head12 = 22.65;  Dupes = 3.65; Barr = 18.825; Act1Conf = 29.315; Act1Strike = -7.47;  ComboFinale = 38.495 },
    @{ Id = "REGENT";     Strike = "STRIKE_REGENT";      Defend = "DEFEND_REGENT";      Def20 = -5.395; Bt = 38.225;  Conf = 34.955; Head12 = 22.29;  Dupes = 3.29; Barr = 18.465; Act1Conf = 28.955; Act1Strike = -7.83;  ComboFinale = 40.135 }
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
  {"kind":"Card","optionIds":["CONFLAGRATION"],"playerHp":80,"playerMaxHp":80,"expectedScore":$($ch.Conf)},
  {"kind":"Card","optionIds":["HEADBUTT"],"deckCardIds":[$defend12],"playerHp":80,"playerMaxHp":80,"expectedScore":$($ch.Head12)},
  {"kind":"Card","optionIds":["HEADBUTT"],"deckCardIds":["HEADBUTT","HEADBUTT","HEADBUTT"],"playerHp":80,"playerMaxHp":80,"expectedScore":$($ch.Dupes)},
  {"kind":"Card","optionIds":["BARRICADE"],"playerHp":80,"playerMaxHp":80,"expectedScore":$($ch.Barr)},
  {"kind":"Relic","optionIds":["ANCHOR","ART_OF_WAR"],"expectedPickId":"ART_OF_WAR"},
  {"kind":"Relic","optionIds":["ANCHOR","RUNIC_PYRAMID"],"expectedPickId":"RUNIC_PYRAMID"},
  {"kind":"Relic","optionIds":["RUNIC_PYRAMID"],"expectedScore":21},
  {"kind":"AncientRelic","optionIds":["CURSED_PEARL","GOLDEN_PEARL"],"expectedPickId":"CURSED_PEARL"},
  {"kind":"AncientRelic","optionIds":["NEOWS_BONES","GOLDEN_PEARL"],"expectedPickId":"GOLDEN_PEARL"},
  {"kind":"AncientRelic","optionIds":["KALEIDOSCOPE","GOLDEN_PEARL","ARCANE_SCROLL"],"expectedPickId":"KALEIDOSCOPE"},
  {"kind":"AncientRelic","optionIds":["SEA_GLASS","PRISMATIC_GEM","TOUCH_OF_OROBAS"],"expectedPickId":"SEA_GLASS"},
  {"kind":"Card","optionIds":["CONFLAGRATION"],"actIndexForTest":1,"expectedScore":$($ch.Act1Conf)},
  {"kind":"Card","optionIds":["$strike"],"actIndexForTest":1,"expectedScore":$($ch.Act1Strike)},
  {"kind":"Card","optionIds":["GRAND_FINALE"],"deckCardIds":["STAMPEDE"],"expectedScore":$($ch.ComboFinale)}
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
