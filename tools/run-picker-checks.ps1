#requires -Version 7.0
# 评分 AI 纯逻辑单测（PICKER-AI-001）：headless 开新局不进战斗，逐项断言
# CardPickerAI / RelicPickerAI 的选牌/跳过/精确评分（对应 TEST_MATRIX "评分 AI 纯逻辑单测" 行）。
#
# 重要：同一局内检查共享状态——牌组注入（DeckCardIds）、玩家生命（PlayerHp）和幕
# （ActIndexForTest）都是粘性的。因此 fixture 按"需要干净状态的检查在前、状态注入在后"
# 排列；依赖特定状态的检查必须显式声明（如满血检查显式给 80/80）。act-1 检查放最后。
#
# 用法：pwsh -NoProfile -File tools\run-picker-checks.ps1 [-ScenarioId PICKER-AI-001] [-Seed <seed>]
param(
    [string]$ScenarioId = "PICKER-AI-001",
    [string]$Seed = "COMBATSOLVER",
    [int]$TimeoutSeconds = 150
)

$ErrorActionPreference = "Stop"

$checks = @'
[
  {"kind":"Card","optionIds":["STRIKE_IRONCLAD"]},
  {"kind":"Card","optionIds":["STRIKE_IRONCLAD","DEFEND_IRONCLAD","BATTLE_TRANCE"],"expectedPickId":"BATTLE_TRANCE"},
  {"kind":"Card","optionIds":["HEADBUTT"],"expectedPickId":"HEADBUTT"},
  {"kind":"Card","optionIds":["BATTLE_TRANCE"],"expectedScore":31},
  {"kind":"Card","optionIds":["DEFEND_IRONCLAD"],"playerHp":20,"playerMaxHp":80,"expectedScore":-4},
  {"kind":"Card","optionIds":["CONFLAGRATION"],"playerHp":80,"playerMaxHp":80,"expectedScore":31},
  {"kind":"Card","optionIds":["HEADBUTT"],"deckCardIds":["DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD","DEFEND_IRONCLAD"],"playerHp":80,"playerMaxHp":80,"expectedScore":20},
  {"kind":"Card","optionIds":["HEADBUTT"],"deckCardIds":["HEADBUTT","HEADBUTT","HEADBUTT"],"playerHp":80,"playerMaxHp":80},
  {"kind":"Card","optionIds":["BARRICADE"],"playerHp":80,"playerMaxHp":80,"expectedScore":15},
  {"kind":"Relic","optionIds":["ANCHOR","ART_OF_WAR"],"expectedPickId":"ART_OF_WAR"},
  {"kind":"Relic","optionIds":["ANCHOR","RUNIC_PYRAMID"],"expectedPickId":"RUNIC_PYRAMID"},
  {"kind":"Relic","optionIds":["BURNING_BLOOD","BLACK_BLOOD"]},
  {"kind":"Relic","optionIds":["RUNIC_PYRAMID"],"expectedScore":21},
  {"kind":"AncientRelic","optionIds":["CURSED_PEARL","GOLDEN_PEARL"],"expectedPickId":"GOLDEN_PEARL"},
  {"kind":"AncientRelic","optionIds":["NEOWS_BONES","GOLDEN_PEARL"],"expectedPickId":"GOLDEN_PEARL"},
  {"kind":"AncientRelic","optionIds":["CURSED_PEARL","NEOWS_BONES"],"allowAncientCurseFallback":true,"expectedPickId":"CURSED_PEARL"},
  {"kind":"Card","optionIds":["CONFLAGRATION"],"actIndexForTest":1,"expectedScore":29},
  {"kind":"Card","optionIds":["STRIKE_IRONCLAD"],"actIndexForTest":1,"expectedScore":-9}
]
'@

pwsh -NoProfile -File (Join-Path $PSScriptRoot "run-unattended-test.ps1") `
    -ScenarioId $ScenarioId -CharacterId IRONCLAD -Seed $Seed `
    -PickerChecksJson $checks -TimeoutSeconds $TimeoutSeconds -ExitOnComplete
if ($LASTEXITCODE -ne 0) { throw "Picker checks exited with code $LASTEXITCODE" }
Write-Host "PICKER_OK"
