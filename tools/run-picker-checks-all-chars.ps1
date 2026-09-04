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
# 2026-09-04（联动互乘精化 v2/v3）：多段/易伤依赖强度按"本职业池内状态产出指数"加权（RoleStatusAccess，
# 运行时枚举 ModelDb.AllCharacters → CardPool.AllCards 的真实去重卡数）。①多段(Repeat/X)攻击画像按
# StrengthPower+VulnerablePower 产出合计相对中位缩放（[0.8,1.25]——多段每段都被力量(加法)与易伤
# (对 powered 段 ×1.5)放大，实测合计 IC=13/REGENT=6/NECRO=3/DEFECT=2/SILENT=1，中位 3 → IC/REGENT
# ×1.25、NECRO ×1.0、SILENT/DEFECT ×0.8）——新增 Conf12 行（12 张防御后的燃烧，攻击缺口大 → 能分辨
# 每段缩放，SILENT ×0.8 使第 2 幕燃烧期望略降即旁证）；②DOMINATE/COLOSSUS 加成按 VulnerablePower 产出
# 相对中位缩放（[0.8,1.3]，实测 IC=6/REGENT=5/NECRO=2/SILENT=1/DEFECT=1，中位 2 → 注入 BASH 后实测
# +6.5/+6.5/+5/+4/+4）。BossAdjust（墨灵每段减免）不动。机制事实（Dominate 按施放后总易伤层数给力量、
# Colossus 让带易伤的 powered 攻击者对 Owner ×0.5、乘算只吃 powered）见 RoleStatusAccess.cs 类文档。
#
# 用法：pwsh -NoProfile -File tools\run-picker-checks-all-chars.ps1
param(
    [int]$TimeoutSeconds = 150,
    [string]$CaptureLogDir = ""
)

$ErrorActionPreference = "Stop"
$toolsDir = $PSScriptRoot

$characters = @(
    @{ Id = "IRONCLAD";   Strike = "STRIKE_IRONCLAD";    Defend = "DEFEND_IRONCLAD";    Def20 = -8.995; Bt = 32.845; Conf = 36.575; Head12 = 25.805; Conf12 = 48.207; Dupes = 4.91;  Barr = 20.085; Act1Conf = 34.575; Act1Strike = -6.03;  ComboFinale = 45.755; Dom0 = 27.255; Col0 = 16.67;  DomBash = 27.255; ColBash = 16.67 },
    @{ Id = "SILENT";     Strike = "STRIKE_SILENT";      Defend = "DEFEND_SILENT";      Def20 = -4;     Bt = 34.057; Conf = 39.787; Head12 = 24.622; Conf12 = 46.12;  Dupes = 4.122; Barr = 19.297; Act1Conf = 33.343; Act1Strike = -3.983; ComboFinale = 47.967; Dom0 = 19.967; Col0 = 9.382;  DomBash = 23.967; ColBash = 13.382 },
    @{ Id = "DEFECT";     Strike = "STRIKE_DEFECT";      Defend = "DEFEND_DEFECT";      Def20 = -9.13;  Bt = 34.96;  Conf = 42.69;  Head12 = 27.116; Conf12 = 48.66; Dupes = 16.025; Barr = 22.2;  Act1Conf = 43.468; Act1Strike = -2.16;  ComboFinale = 56.87;  Dom0 = 22.87;  Col0 = 12.285; DomBash = 26.87;  ColBash = 16.285 },
    @{ Id = "NECROBINDER"; Strike = "STRIKE_NECROBINDER"; Defend = "DEFEND_NECROBINDER"; Def20 = -4;     Bt = 31.585; Conf = 39.315; Head12 = 23.741; Conf12 = 45.527; Dupes = 3.65;  Barr = 18.825; Act1Conf = 31.537; Act1Strike = -7.47;  ComboFinale = 44.495; Dom0 = 19.495; Col0 = 8.91;   DomBash = 24.495; ColBash = 13.91 },
    @{ Id = "REGENT";     Strike = "STRIKE_REGENT";      Defend = "DEFEND_REGENT";      Def20 = -5.395; Bt = 33.225; Conf = 34.955; Head12 = 23.73;  Conf12 = 45.955; Dupes = 3.29;  Barr = 18.465; Act1Conf = 32.955; Act1Strike = -7.83;  ComboFinale = 47.135; Dom0 = 25.635; Col0 = 15.05;  DomBash = 25.635; ColBash = 15.05 }
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
  {"kind":"Card","optionIds":["CONFLAGRATION"],"playerHp":80,"playerMaxHp":80,"expectedScore":$($ch.Conf12)},
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


