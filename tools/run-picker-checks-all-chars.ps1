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
# 2026-09-04（四维补位 + 联动互乘）：四维补位取代 DeckGapBonus 后全部评分期望按重校实测更新；
# 末尾新增"施加×依赖"对照对：DOMINATE/COLOSSUS 无注入 vs 注入 BASH（Bash 的 DynamicVars 带
# "VulnerablePower" 键，decomp Bash.cs CanonicalVars——选它做注入是因为 UPPERCUT 只有通用 "Power"
# 键（Uppercut.cs L19-23），DeckVarCount("VulnerablePower") 看不到，拿不准时以可读数据为准）。
#
# 用法：pwsh -NoProfile -File tools\run-picker-checks-all-chars.ps1
param(
    [int]$TimeoutSeconds = 150,
    [string]$CaptureLogDir = ""
)

$ErrorActionPreference = "Stop"
$toolsDir = $PSScriptRoot

$characters = @(
    @{ Id = "IRONCLAD";   Strike = "STRIKE_IRONCLAD";    Defend = "DEFEND_IRONCLAD";    Def20 = -8.995; Bt = 32.845; Conf = 36.575; Head12 = 25.805; Dupes = 4.91;  Barr = 20.085; Act1Conf = 34.575; Act1Strike = -6.03;  ComboFinale = 45.755; Dom0 = 25.755; Col0 = 15.17;   DomBash = 25.755; ColBash = 15.17 },
    @{ Id = "SILENT";     Strike = "STRIKE_SILENT";      Defend = "DEFEND_SILENT";      Def20 = -4;     Bt = 34.057; Conf = 39.787; Head12 = 24.622; Dupes = 4.122; Barr = 19.297; Act1Conf = 33.787; Act1Strike = -3.983; ComboFinale = 47.967; Dom0 = 19.967; Col0 = 9.382;  DomBash = 24.967; ColBash = 14.382 },
    @{ Id = "DEFECT";     Strike = "STRIKE_DEFECT";      Defend = "DEFEND_DEFECT";      Def20 = -9.13;  Bt = 34.96;  Conf = 42.69;  Head12 = 27.116; Dupes = 16.025; Barr = 22.2;  Act1Conf = 43.912; Act1Strike = -2.16;  ComboFinale = 56.87;  Dom0 = 22.87;  Col0 = 12.285; DomBash = 27.87;  ColBash = 17.285 },
    @{ Id = "NECROBINDER"; Strike = "STRIKE_NECROBINDER"; Defend = "DEFEND_NECROBINDER"; Def20 = -4;     Bt = 31.585; Conf = 39.315; Head12 = 23.741; Dupes = 3.65;  Barr = 18.825; Act1Conf = 31.537; Act1Strike = -7.47;  ComboFinale = 44.495; Dom0 = 19.495; Col0 = 8.91;   DomBash = 24.495; ColBash = 13.91 },
    @{ Id = "REGENT";     Strike = "STRIKE_REGENT";      Defend = "DEFEND_REGENT";      Def20 = -5.395; Bt = 33.225; Conf = 34.955; Head12 = 23.73;  Dupes = 3.29;  Barr = 18.465; Act1Conf = 32.955; Act1Strike = -7.83;  ComboFinale = 47.135; Dom0 = 24.135; Col0 = 13.55;  DomBash = 24.135; ColBash = 13.55 }
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
  {"kind":"Card","optionIds":["GRAND_FINALE"],"deckCardIds":["STAMPEDE"],"expectedScore":$($ch.ComboFinale)},
  {"kind":"Card","optionIds":["DOMINATE"],"playerHp":80,"playerMaxHp":80,"expectedScore":$($ch.Dom0)},
  {"kind":"Card","optionIds":["COLOSSUS"],"playerHp":80,"playerMaxHp":80,"expectedScore":$($ch.Col0)},
  {"kind":"Card","optionIds":["DOMINATE"],"deckCardIds":["BASH"],"playerHp":80,"playerMaxHp":80,"expectedScore":$($ch.DomBash)},
  {"kind":"Card","optionIds":["COLOSSUS"],"playerHp":80,"playerMaxHp":80,"expectedScore":$($ch.ColBash)}
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
