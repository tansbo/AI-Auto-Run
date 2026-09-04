// 由 tools/card_stats_snapshot.py 从 Spire Codex A10 真实对局统计生成（勿手改）。
// 数据表：WinRateById=每卡绝对胜率，RoleById=归属池，MedianWinRateByRole=各池胜率中位(picks>=500 计算)。
// BonusFor(entry, receivingRole)：
//   同池/未知接收职业 → clamp((winRate − 自身池中位)×0.45, ±8)（与原 BonusById 语义一致）
//   跨池（无色/他职业，如万花筒/海玻璃/棱彩宝石/色彩哲学家等渠道拿到）→ clamp((winRate − 接收职业中位)×0.45, ±8)
// 仅 picks>=1000 高置信样本计。
using System;
using System.Collections.Generic;

namespace CombatSolver.Run;

internal static class CardWinStats
{
    private const float K = 0.45f;
    private const float Cap = 8f;

    /// <summary>卡牌 Id.Entry → 绝对胜率（口径与源站一致）。</summary>
    internal static readonly Dictionary<string, float> WinRateById = new(StringComparer.Ordinal)
    {
    ["ABRASIVE"] = 43.9f, // role=SILENT picks=15120 selfBonus=+2.812
    ["ACCELERANT"] = 41.2f, // role=SILENT picks=15115 selfBonus=+1.597
    ["ACCURACY"] = 40.3f, // role=SILENT picks=35722 selfBonus=+1.192
    ["ACROBATICS"] = 41.1f, // role=SILENT picks=37652 selfBonus=+1.552
    ["ADAPTIVE_STRIKE"] = 32.5f, // role=DEFECT picks=5461 selfBonus=+0.585
    ["ADRENALINE"] = 48.9f, // role=SILENT picks=21952 selfBonus=+5.062
    ["AFTERIMAGE"] = 50.0f, // role=SILENT picks=19713 selfBonus=+5.557
    ["AFTERLIFE"] = 33.0f, // role=NECROBINDER picks=21853 selfBonus=-2.565
    ["AGGRESSION"] = 44.9f, // role=IRONCLAD picks=9291 selfBonus=+4.050
    ["ALCHEMIZE"] = 50.3f, // role=COLORLESS picks=5192 selfBonus=+2.610
    ["ALIGNMENT"] = 42.1f, // role=REGENT picks=14783 selfBonus=+1.170
    ["ALL_FOR_ONE"] = 43.6f, // role=DEFECT picks=10451 selfBonus=+5.580
    ["ANGER"] = 27.5f, // role=IRONCLAD picks=22454 selfBonus=-3.780
    ["ANOINTED"] = 59.3f, // role=COLORLESS picks=1592 selfBonus=+6.660
    ["ANTICIPATE"] = 30.4f, // role=SILENT picks=19023 selfBonus=-3.263
    ["ARMAMENTS"] = 30.8f, // role=IRONCLAD picks=40318 selfBonus=-2.295
    ["ARSENAL"] = 50.8f, // role=REGENT picks=10875 selfBonus=+5.085
    ["ASHEN_STRIKE"] = 41.1f, // role=IRONCLAD picks=18072 selfBonus=+2.340
    ["ASSASSINATE"] = 41.7f, // role=SILENT picks=10808 selfBonus=+1.822
    ["ASTRAL_PULSE"] = 26.2f, // role=REGENT picks=23320 selfBonus=-5.985
    ["AUTOMATION"] = 46.7f, // role=COLORLESS picks=11613 selfBonus=+0.990
    ["BACKFLIP"] = 37.7f, // role=SILENT picks=57963 selfBonus=+0.022
    ["BACKSTAB"] = 29.0f, // role=SILENT picks=17170 selfBonus=-3.893
    ["BALL_LIGHTNING"] = 20.3f, // role=DEFECT picks=22744 selfBonus=-4.905
    ["BANSHEES_CRY"] = 45.8f, // role=NECROBINDER picks=6451 selfBonus=+3.195
    ["BARRAGE"] = 25.9f, // role=DEFECT picks=16016 selfBonus=-2.385
    ["BARRICADE"] = 47.2f, // role=IRONCLAD picks=12291 selfBonus=+5.085
    ["BASH"] = 21.8f, // role=IRONCLAD picks=118536 selfBonus=-6.345
    ["BATTLE_TRANCE"] = 40.0f, // role=IRONCLAD picks=35091 selfBonus=+1.845
    ["BEACON_OF_HOPE"] = 64.9f, // role=COLORLESS picks=10034 selfBonus=+8.000
    ["BEAM_CELL"] = 28.4f, // role=DEFECT picks=28798 selfBonus=-1.260
    ["BEAT_DOWN"] = 49.6f, // role=COLORLESS picks=1865 selfBonus=+2.295
    ["BEAT_INTO_SHAPE"] = 41.0f, // role=REGENT picks=6210 selfBonus=+0.675
    ["BEGONE"] = 38.7f, // role=REGENT picks=36669 selfBonus=-0.360
    ["BELIEVE_IN_YOU"] = 41.7f, // role=COLORLESS picks=12717 selfBonus=-1.260
    ["BIASED_COGNITION"] = 29.0f, // role=DEFECT picks=1309 selfBonus=-0.990
    ["BIG_BANG"] = 48.2f, // role=REGENT picks=17740 selfBonus=+3.915
    ["BLACK_HOLE"] = 31.9f, // role=REGENT picks=18738 selfBonus=-3.420
    ["BLADE_DANCE"] = 33.9f, // role=SILENT picks=45412 selfBonus=-1.688
    ["BLADE_OF_INK"] = 47.4f, // role=SILENT picks=15258 selfBonus=+4.387
    ["BLIGHT_STRIKE"] = 24.8f, // role=NECROBINDER picks=12149 selfBonus=-6.255
    ["BLOODLETTING"] = 35.5f, // role=IRONCLAD picks=61313 selfBonus=-0.180
    ["BLOOD_WALL"] = 30.4f, // role=IRONCLAD picks=39012 selfBonus=-2.475
    ["BLUDGEON"] = 28.3f, // role=IRONCLAD picks=12608 selfBonus=-3.420
    ["BLUR"] = 39.4f, // role=SILENT picks=27531 selfBonus=+0.787
    ["BODYGUARD"] = 35.7f, // role=NECROBINDER picks=23472 selfBonus=-1.350
    ["BODY_SLAM"] = 37.9f, // role=IRONCLAD picks=21810 selfBonus=+0.900
    ["BOLAS"] = 46.5f, // role=COLORLESS picks=2623 selfBonus=+0.900
    ["BOMBARDMENT"] = 41.4f, // role=REGENT picks=8311 selfBonus=+0.855
    ["BONE_SHARDS"] = 30.6f, // role=NECROBINDER picks=10514 selfBonus=-3.645
    ["BOOST_AWAY"] = 30.3f, // role=DEFECT picks=36872 selfBonus=-0.405
    ["BOOT_SEQUENCE"] = 32.9f, // role=DEFECT picks=21843 selfBonus=+0.765
    ["BORROWED_TIME"] = 45.9f, // role=NECROBINDER picks=23708 selfBonus=+3.240
    ["BOUNCING_FLASK"] = 30.8f, // role=SILENT picks=14029 selfBonus=-3.083
    ["BRAND"] = 41.3f, // role=IRONCLAD picks=13523 selfBonus=+2.430
    ["BREAKTHROUGH"] = 23.7f, // role=IRONCLAD picks=26056 selfBonus=-5.490
    ["BUBBLE_BUBBLE"] = 35.5f, // role=SILENT picks=15753 selfBonus=-0.968
    ["BUFFER"] = 43.2f, // role=DEFECT picks=8734 selfBonus=+5.400
    ["BULK_UP"] = 34.4f, // role=DEFECT picks=17745 selfBonus=+1.440
    ["BULLET_TIME"] = 41.1f, // role=SILENT picks=10087 selfBonus=+1.552
    ["BULLY"] = 36.7f, // role=IRONCLAD picks=20660 selfBonus=+0.360
    ["BULWARK"] = 40.0f, // role=REGENT picks=26534 selfBonus=+0.225
    ["BUNDLE_OF_JOY"] = 49.6f, // role=REGENT picks=7670 selfBonus=+4.545
    ["BURNING_PACT"] = 43.4f, // role=IRONCLAD picks=27718 selfBonus=+3.375
    ["BURST"] = 45.5f, // role=SILENT picks=13779 selfBonus=+3.532
    ["BURY"] = 38.9f, // role=NECROBINDER picks=5871 selfBonus=+0.090
    ["CALAMITY"] = 54.4f, // role=COLORLESS picks=2498 selfBonus=+4.455
    ["CALCIFY"] = 35.2f, // role=NECROBINDER picks=11049 selfBonus=-1.575
    ["CALCULATED_GAMBLE"] = 43.1f, // role=SILENT picks=30546 selfBonus=+2.452
    ["CALL_OF_THE_VOID"] = 48.8f, // role=NECROBINDER picks=12114 selfBonus=+4.545
    ["CAPACITOR"] = 32.4f, // role=DEFECT picks=26362 selfBonus=+0.540
    ["CAPTURE_SPIRIT"] = 41.2f, // role=NECROBINDER picks=20080 selfBonus=+1.125
    ["CASCADE"] = 44.8f, // role=IRONCLAD picks=10223 selfBonus=+4.005
    ["CATASTROPHE"] = 42.3f, // role=COLORLESS picks=4320 selfBonus=-0.990
    ["CELESTIAL_MIGHT"] = 30.5f, // role=REGENT picks=10385 selfBonus=-4.050
    ["CHAOS"] = 26.3f, // role=DEFECT picks=7929 selfBonus=-2.205
    ["CHARGE"] = 41.7f, // role=REGENT picks=24123 selfBonus=+0.990
    ["CHARGE_BATTERY"] = 26.8f, // role=DEFECT picks=45983 selfBonus=-1.980
    ["CHILD_OF_THE_STARS"] = 40.2f, // role=REGENT picks=26786 selfBonus=+0.315
    ["CHILL"] = 31.7f, // role=DEFECT picks=19803 selfBonus=+0.225
    ["CINDER"] = 23.4f, // role=IRONCLAD picks=9814 selfBonus=-5.625
    ["CLAW"] = 25.4f, // role=DEFECT picks=28053 selfBonus=-2.610
    ["CLEANSE"] = 44.2f, // role=NECROBINDER picks=16047 selfBonus=+2.475
    ["CLOAK_AND_DAGGER"] = 34.3f, // role=SILENT picks=46851 selfBonus=-1.508
    ["CLOAK_OF_STARS"] = 33.7f, // role=REGENT picks=30292 selfBonus=-2.610
    ["COLD_SNAP"] = 24.3f, // role=DEFECT picks=25194 selfBonus=-3.105
    ["COLLISION_COURSE"] = 35.0f, // role=REGENT picks=20168 selfBonus=-2.025
    ["COLOSSUS"] = 38.5f, // role=IRONCLAD picks=36011 selfBonus=+1.170
    ["COMET"] = 41.1f, // role=REGENT picks=7941 selfBonus=+0.720
    ["COMPILE_DRIVER"] = 33.9f, // role=DEFECT picks=16534 selfBonus=+1.215
    ["CONFLAGRATION"] = 39.4f, // role=IRONCLAD picks=11364 selfBonus=+1.575
    ["CONQUEROR"] = 38.1f, // role=REGENT picks=14881 selfBonus=-0.630
    ["CONSUMING_SHADOW"] = 29.6f, // role=DEFECT picks=3758 selfBonus=-0.720
    ["CONVERGENCE"] = 41.5f, // role=REGENT picks=24349 selfBonus=+0.900
    ["COOLANT"] = 36.7f, // role=DEFECT picks=5929 selfBonus=+2.475
    ["COOLHEADED"] = 31.2f, // role=DEFECT picks=42873 selfBonus=+0.000
    ["COORDINATE"] = 33.3f, // role=COLORLESS picks=3162 selfBonus=-5.040
    ["CORROSIVE_WAVE"] = 42.0f, // role=SILENT picks=8913 selfBonus=+1.957
    ["CORRUPTION"] = 24.9f, // role=IRONCLAD picks=3471 selfBonus=-4.950
    ["COSMIC_INDIFFERENCE"] = 37.9f, // role=REGENT picks=41017 selfBonus=-0.720
    ["CRASH_LANDING"] = 42.1f, // role=REGENT picks=6222 selfBonus=+1.170
    ["CREATIVE_AI"] = 39.9f, // role=DEFECT picks=11681 selfBonus=+3.915
    ["CRIMSON_MANTLE"] = 43.0f, // role=IRONCLAD picks=14862 selfBonus=+3.195
    ["CRUELTY"] = 40.7f, // role=IRONCLAD picks=19921 selfBonus=+2.160
    ["CRUSH_UNDER"] = 30.4f, // role=REGENT picks=21199 selfBonus=-4.095
    ["DAGGER_THROW"] = 28.2f, // role=SILENT picks=31345 selfBonus=-4.253
    ["DANSE_MACABRE"] = 44.8f, // role=NECROBINDER picks=13733 selfBonus=+2.745
    ["DARKNESS"] = 28.2f, // role=DEFECT picks=12431 selfBonus=-1.350
    ["DARK_EMBRACE"] = 53.8f, // role=IRONCLAD picks=10424 selfBonus=+8.000
    ["DARK_SHACKLES"] = 49.0f, // role=COLORLESS picks=12641 selfBonus=+2.025
    ["DASH"] = 29.8f, // role=SILENT picks=13813 selfBonus=-3.533
    ["DEADLY_POISON"] = 28.0f, // role=SILENT picks=25872 selfBonus=-4.343
    ["DEATHBRINGER"] = 34.1f, // role=NECROBINDER picks=18321 selfBonus=-2.070
    ["DEATHS_DOOR"] = 35.9f, // role=NECROBINDER picks=13268 selfBonus=-1.260
    ["DEATH_MARCH"] = 34.8f, // role=NECROBINDER picks=5990 selfBonus=-1.755
    ["DEBILITATE"] = 51.7f, // role=NECROBINDER picks=27054 selfBonus=+5.850
    ["DECISIONS_DECISIONS"] = 49.4f, // role=REGENT picks=12755 selfBonus=+4.455
    ["DEFEND_DEFECT"] = 19.8f, // role=DEFECT picks=101793 selfBonus=-5.130
    ["DEFEND_IRONCLAD"] = 24.8f, // role=IRONCLAD picks=20997 selfBonus=-4.995
    ["DEFEND_REGENT"] = 36.4f, // role=REGENT picks=53103 selfBonus=-1.395
    ["DEFILE"] = 29.0f, // role=NECROBINDER picks=13805 selfBonus=-4.365
    ["DEFLECT"] = 34.5f, // role=SILENT picks=32914 selfBonus=-1.418
    ["DEFRAGMENT"] = 39.5f, // role=DEFECT picks=13391 selfBonus=+3.735
    ["DELAY"] = 40.3f, // role=NECROBINDER picks=19973 selfBonus=+0.720
    ["DEMESNE"] = 51.1f, // role=NECROBINDER picks=15815 selfBonus=+5.580
    ["DEMONIC_SHIELD"] = 43.0f, // role=IRONCLAD picks=10009 selfBonus=+3.195
    ["DEMON_FORM"] = 38.4f, // role=IRONCLAD picks=9707 selfBonus=+1.125
    ["DEVASTATE"] = 26.8f, // role=REGENT picks=6196 selfBonus=-5.715
    ["DEVOUR_LIFE"] = 43.2f, // role=NECROBINDER picks=6859 selfBonus=+2.025
    ["DIRGE"] = 43.2f, // role=NECROBINDER picks=20085 selfBonus=+2.025
    ["DISCOVERY"] = 43.5f, // role=COLORLESS picks=13765 selfBonus=-0.450
    ["DISMANTLE"] = 28.8f, // role=IRONCLAD picks=15049 selfBonus=-3.195
    ["DOMINATE"] = 39.8f, // role=IRONCLAD picks=33619 selfBonus=+1.755
    ["DOUBLE_ENERGY"] = 33.3f, // role=DEFECT picks=13631 selfBonus=+0.945
    ["DRAIN_POWER"] = 29.0f, // role=NECROBINDER picks=16468 selfBonus=-4.365
    ["DRAMATIC_ENTRANCE"] = 28.5f, // role=COLORLESS picks=6908 selfBonus=-7.200
    ["DREDGE"] = 50.1f, // role=NECROBINDER picks=14873 selfBonus=+5.130
    ["DRUM_OF_BATTLE"] = 43.8f, // role=IRONCLAD picks=13442 selfBonus=+3.555
    ["DUALCAST"] = 34.7f, // role=DEFECT picks=15241 selfBonus=+1.575
    ["DYING_STAR"] = 46.6f, // role=REGENT picks=11306 selfBonus=+3.195
    ["ECHOING_SLASH"] = 27.9f, // role=SILENT picks=6988 selfBonus=-4.388
    ["ECHO_FORM"] = 43.6f, // role=DEFECT picks=17601 selfBonus=+5.580
    ["EIDOLON"] = 50.8f, // role=NECROBINDER picks=3047 selfBonus=+5.445
    ["END_OF_DAYS"] = 36.8f, // role=NECROBINDER picks=8448 selfBonus=-0.855
    ["ENERGY_SURGE"] = 41.7f, // role=DEFECT picks=18239 selfBonus=+4.725
    ["ENFEEBLING_TOUCH"] = 45.0f, // role=NECROBINDER picks=31943 selfBonus=+2.835
    ["ENTROPY"] = 54.5f, // role=COLORLESS picks=4463 selfBonus=+4.500
    ["ENVENOM"] = 38.7f, // role=SILENT picks=11002 selfBonus=+0.472
    ["EQUILIBRIUM"] = 44.0f, // role=COLORLESS picks=8267 selfBonus=-0.225
    ["ERADICATE"] = 52.5f, // role=NECROBINDER picks=8434 selfBonus=+6.210
    ["ESCAPE_PLAN"] = 40.2f, // role=SILENT picks=30044 selfBonus=+1.147
    ["ETERNAL_ARMOR"] = 51.4f, // role=COLORLESS picks=3386 selfBonus=+3.105
    ["EVIL_EYE"] = 37.6f, // role=IRONCLAD picks=19346 selfBonus=+0.765
    ["EXPECT_A_FIGHT"] = 35.5f, // role=IRONCLAD picks=14453 selfBonus=-0.180
    ["EXPERTISE"] = 43.1f, // role=SILENT picks=9716 selfBonus=+2.452
    ["FALLING_STAR"] = 37.9f, // role=REGENT picks=8753 selfBonus=-0.720
    ["FAN_OF_KNIVES"] = 42.6f, // role=SILENT picks=14415 selfBonus=+2.227
    ["FASTEN"] = 37.0f, // role=COLORLESS picks=17102 selfBonus=-3.375
    ["FEAR"] = 35.6f, // role=NECROBINDER picks=25161 selfBonus=-1.395
    ["FEED"] = 32.8f, // role=IRONCLAD picks=6967 selfBonus=-1.395
    ["FEEL_NO_PAIN"] = 46.3f, // role=IRONCLAD picks=24560 selfBonus=+4.680
    ["FERAL"] = 33.2f, // role=DEFECT picks=17962 selfBonus=+0.900
    ["FETCH"] = 38.0f, // role=NECROBINDER picks=16172 selfBonus=-0.315
    ["FIEND_FIRE"] = 45.7f, // role=IRONCLAD picks=9023 selfBonus=+4.410
    ["FIGHT_ME"] = 26.5f, // role=IRONCLAD picks=12294 selfBonus=-4.230
    ["FIGHT_THROUGH"] = 32.6f, // role=DEFECT picks=15144 selfBonus=+0.630
    ["FINESSE"] = 44.1f, // role=COLORLESS picks=16163 selfBonus=-0.180
    ["FINISHER"] = 36.5f, // role=SILENT picks=13771 selfBonus=-0.518
    ["FISTICUFFS"] = 41.0f, // role=COLORLESS picks=8595 selfBonus=-1.575
    ["FLAK_CANNON"] = 43.5f, // role=DEFECT picks=11006 selfBonus=+5.535
    ["FLAME_BARRIER"] = 31.6f, // role=IRONCLAD picks=24596 selfBonus=-1.935
    ["FLANKING"] = 50.9f, // role=SILENT picks=15846 selfBonus=+5.962
    ["FLASH_OF_STEEL"] = 42.6f, // role=COLORLESS picks=12261 selfBonus=-0.855
    ["FLATTEN"] = 29.5f, // role=NECROBINDER picks=17317 selfBonus=-4.140
    ["FLECHETTES"] = 34.5f, // role=SILENT picks=10423 selfBonus=-1.418
    ["FLICK_FLACK"] = 22.8f, // role=SILENT picks=21122 selfBonus=-6.683
    ["FOCUSED_STRIKE"] = 21.9f, // role=DEFECT picks=14514 selfBonus=-4.185
    ["FOOTWORK"] = 38.7f, // role=SILENT picks=47984 selfBonus=+0.472
    ["FORBIDDEN_GRIMOIRE"] = 32.3f, // role=NECROBINDER picks=16925 selfBonus=-2.880
    ["FOREGONE_CONCLUSION"] = 45.9f, // role=REGENT picks=6281 selfBonus=+2.880
    ["FRIENDSHIP"] = 42.3f, // role=NECROBINDER picks=30589 selfBonus=+1.620
    ["FTL"] = 29.4f, // role=DEFECT picks=16979 selfBonus=-0.810
    ["FURNACE"] = 36.1f, // role=REGENT picks=19412 selfBonus=-1.530
    ["FUSION"] = 30.7f, // role=DEFECT picks=11201 selfBonus=-0.225
    ["GAMMA_BLAST"] = 38.0f, // role=REGENT picks=15455 selfBonus=-0.675
    ["GANG_UP"] = 48.9f, // role=COLORLESS picks=8089 selfBonus=+1.980
    ["GATHER_LIGHT"] = 33.4f, // role=REGENT picks=47421 selfBonus=-2.745
    ["GENESIS"] = 42.4f, // role=REGENT picks=10631 selfBonus=+1.305
    ["GENETIC_ALGORITHM"] = 34.5f, // role=DEFECT picks=7532 selfBonus=+1.485
    ["GLACIER"] = 31.9f, // role=DEFECT picks=24084 selfBonus=+0.315
    ["GLASSWORK"] = 24.0f, // role=DEFECT picks=12656 selfBonus=-3.240
    ["GLIMMER"] = 45.5f, // role=REGENT picks=11939 selfBonus=+2.700
    ["GLIMPSE_BEYOND"] = 55.9f, // role=NECROBINDER picks=8290 selfBonus=+7.740
    ["GLITTERSTREAM"] = 34.2f, // role=REGENT picks=28252 selfBonus=-2.385
    ["GLOW"] = 36.5f, // role=REGENT picks=43109 selfBonus=-1.350
    ["GOLD_AXE"] = 56.6f, // role=COLORLESS picks=6749 selfBonus=+5.445
    ["GO_FOR_THE_EYES"] = 28.5f, // role=DEFECT picks=34966 selfBonus=-1.215
    ["GRAND_FINALE"] = 39.8f, // role=SILENT picks=3521 selfBonus=+0.967
    ["GRAVEBLAST"] = 44.7f, // role=NECROBINDER picks=28713 selfBonus=+2.700
    ["GRAVE_WARDEN"] = 35.7f, // role=NECROBINDER picks=49122 selfBonus=-1.350
    ["GUARDS"] = 49.4f, // role=REGENT picks=10809 selfBonus=+4.455
    ["GUNK_UP"] = 26.9f, // role=DEFECT picks=18322 selfBonus=-1.935
    ["HAILSTORM"] = 27.3f, // role=DEFECT picks=12504 selfBonus=-1.755
    ["HAMMER_TIME"] = 45.2f, // role=REGENT picks=5230 selfBonus=+2.565
    ["HAND_OF_GREED"] = 35.8f, // role=COLORLESS picks=1110 selfBonus=-3.915
    ["HAND_TRICK"] = 33.6f, // role=SILENT picks=13546 selfBonus=-1.823
    ["HANG"] = 45.9f, // role=NECROBINDER picks=10212 selfBonus=+3.240
    ["HAUNT"] = 36.7f, // role=NECROBINDER picks=12327 selfBonus=-0.900
    ["HAVOC"] = 38.3f, // role=IRONCLAD picks=12312 selfBonus=+1.080
    ["HAZE"] = 32.0f, // role=SILENT picks=16561 selfBonus=-2.543
    ["HEADBUTT"] = 35.7f, // role=IRONCLAD picks=31159 selfBonus=-0.090
    ["HEAVENLY_DRILL"] = 44.5f, // role=REGENT picks=6624 selfBonus=+2.250
    ["HEGEMONY"] = 34.4f, // role=REGENT picks=8748 selfBonus=-2.295
    ["HEIRLOOM_HAMMER"] = 44.4f, // role=REGENT picks=5449 selfBonus=+2.205
    ["HELIX_DRILL"] = 38.2f, // role=DEFECT picks=5515 selfBonus=+3.150
    ["HELLRAISER"] = 37.0f, // role=IRONCLAD picks=10045 selfBonus=+0.495
    ["HEMOKINESIS"] = 26.1f, // role=IRONCLAD picks=8627 selfBonus=-4.410
    ["HIDDEN_CACHE"] = 34.5f, // role=REGENT picks=40970 selfBonus=-2.250
    ["HIDDEN_DAGGERS"] = 39.2f, // role=SILENT picks=30275 selfBonus=+0.697
    ["HIDDEN_GEM"] = 50.9f, // role=COLORLESS picks=18598 selfBonus=+2.880
    ["HIGH_FIVE"] = 35.9f, // role=NECROBINDER picks=13386 selfBonus=-1.260
    ["HOLOGRAM"] = 33.7f, // role=DEFECT picks=48663 selfBonus=+1.125
    ["HOTFIX"] = 28.0f, // role=DEFECT picks=25612 selfBonus=-1.440
    ["HOWL_FROM_BEYOND"] = 30.9f, // role=IRONCLAD picks=9686 selfBonus=-2.250
    ["HUDDLE_UP"] = 54.2f, // role=COLORLESS picks=7325 selfBonus=+4.365
    ["HYPERBEAM"] = 30.5f, // role=DEFECT picks=3461 selfBonus=-0.315
    ["ICE_LANCE"] = 35.9f, // role=DEFECT picks=7685 selfBonus=+2.115
    ["IGNITION"] = 44.0f, // role=DEFECT picks=7341 selfBonus=+5.760
    ["IMPATIENCE"] = 48.2f, // role=COLORLESS picks=4074 selfBonus=+1.665
    ["IMPERVIOUS"] = 45.3f, // role=IRONCLAD picks=11657 selfBonus=+4.230
    ["INFERNAL_BLADE"] = 30.6f, // role=IRONCLAD picks=13998 selfBonus=-2.385
    ["INFERNO"] = 30.3f, // role=IRONCLAD picks=17950 selfBonus=-2.520
    ["INFINITE_BLADES"] = 37.8f, // role=SILENT picks=28610 selfBonus=+0.067
    ["INFLAME"] = 29.1f, // role=IRONCLAD picks=20546 selfBonus=-3.060
    ["INTERCEPT"] = 43.8f, // role=COLORLESS picks=1852 selfBonus=-0.315
    ["INVOKE"] = 37.6f, // role=NECROBINDER picks=39212 selfBonus=-0.495
    ["IRON_WAVE"] = 26.1f, // role=IRONCLAD picks=17206 selfBonus=-4.410
    ["ITERATION"] = 40.0f, // role=DEFECT picks=17428 selfBonus=+3.960
    ["I_AM_INVINCIBLE"] = 39.5f, // role=REGENT picks=6854 selfBonus=+0.000
    ["JACKPOT"] = 51.0f, // role=COLORLESS picks=6527 selfBonus=+2.925
    ["JACK_OF_ALL_TRADES"] = 40.0f, // role=COLORLESS picks=5015 selfBonus=-2.025
    ["JUGGERNAUT"] = 39.8f, // role=IRONCLAD picks=6422 selfBonus=+1.755
    ["JUGGLING"] = 41.9f, // role=IRONCLAD picks=11687 selfBonus=+2.700
    ["KINGLY_KICK"] = 25.8f, // role=REGENT picks=6787 selfBonus=-6.165
    ["KINGLY_PUNCH"] = 25.6f, // role=REGENT picks=7004 selfBonus=-6.255
    ["KNIFE_TRAP"] = 49.7f, // role=SILENT picks=15099 selfBonus=+5.422
    ["KNOCKDOWN"] = 63.9f, // role=COLORLESS picks=4098 selfBonus=+8.000
    ["KNOW_THY_PLACE"] = 35.5f, // role=REGENT picks=36866 selfBonus=-1.800
    ["LARGESSE"] = 43.4f, // role=REGENT picks=8900 selfBonus=+1.755
    ["LEADING_STRIKE"] = 33.6f, // role=SILENT picks=44633 selfBonus=-1.823
    ["LEAP"] = 24.4f, // role=DEFECT picks=24954 selfBonus=-3.060
    ["LEGION_OF_BONE"] = 51.9f, // role=NECROBINDER picks=10731 selfBonus=+5.940
    ["LEG_SWEEP"] = 37.6f, // role=SILENT picks=24345 selfBonus=-0.023
    ["LETHALITY"] = 41.8f, // role=NECROBINDER picks=29096 selfBonus=+1.395
    ["LIFT"] = 38.0f, // role=COLORLESS picks=3350 selfBonus=-2.925
    ["LIGHTNING_ROD"] = 23.9f, // role=DEFECT picks=32016 selfBonus=-3.285
    ["LOOP"] = 28.4f, // role=DEFECT picks=16837 selfBonus=-1.260
    ["LUNAR_BLAST"] = 39.3f, // role=REGENT picks=6330 selfBonus=-0.090
    ["MACHINE_LEARNING"] = 43.1f, // role=DEFECT picks=12457 selfBonus=+5.355
    ["MAKE_IT_SO"] = 42.4f, // role=REGENT picks=8008 selfBonus=+1.305
    ["MALAISE"] = 51.4f, // role=SILENT picks=10272 selfBonus=+6.187
    ["MANGLE"] = 46.9f, // role=IRONCLAD picks=10104 selfBonus=+4.950
    ["MANIFEST_AUTHORITY"] = 41.1f, // role=REGENT picks=19254 selfBonus=+0.720
    ["MASTER_OF_STRATEGY"] = 58.1f, // role=COLORLESS picks=8309 selfBonus=+6.120
    ["MASTER_PLANNER"] = 42.7f, // role=SILENT picks=10432 selfBonus=+2.272
    ["MAYHEM"] = 49.3f, // role=COLORLESS picks=5940 selfBonus=+2.160
    ["MELANCHOLY"] = 34.4f, // role=NECROBINDER picks=8552 selfBonus=-1.935
    ["MEMENTO_MORI"] = 30.1f, // role=SILENT picks=5682 selfBonus=-3.398
    ["METEOR_SHOWER"] = 37.5f, // role=REGENT picks=9338 selfBonus=-0.900
    ["METEOR_STRIKE"] = 39.8f, // role=DEFECT picks=4447 selfBonus=+3.870
    ["MIMIC"] = 57.2f, // role=COLORLESS picks=3466 selfBonus=+5.715
    ["MIND_BLAST"] = 36.1f, // role=COLORLESS picks=2627 selfBonus=-3.780
    ["MIRAGE"] = 38.8f, // role=SILENT picks=10611 selfBonus=+0.517
    ["MISERY"] = 48.0f, // role=NECROBINDER picks=10478 selfBonus=+4.185
    ["MODDED"] = 39.4f, // role=DEFECT picks=12958 selfBonus=+3.690
    ["MOLTEN_FIST"] = 34.5f, // role=IRONCLAD picks=42333 selfBonus=-0.630
    ["MOMENTUM_STRIKE"] = 22.1f, // role=DEFECT picks=13858 selfBonus=-4.095
    ["MONARCHS_GAZE"] = 48.2f, // role=REGENT picks=6966 selfBonus=+3.915
    ["MONOLOGUE"] = 35.7f, // role=REGENT picks=7336 selfBonus=-1.710
    ["MULTI_CAST"] = 33.3f, // role=DEFECT picks=4363 selfBonus=+0.945
    ["NECRO_MASTERY"] = 39.0f, // role=NECROBINDER picks=6943 selfBonus=+0.135
    ["NEGATIVE_PULSE"] = 27.9f, // role=NECROBINDER picks=31455 selfBonus=-4.860
    ["NEUROSURGE"] = 54.7f, // role=NECROBINDER picks=12945 selfBonus=+7.200
    ["NEUTRALIZE"] = 37.6f, // role=SILENT picks=6500 selfBonus=-0.023
    ["NEUTRON_AEGIS"] = 41.1f, // role=REGENT picks=5598 selfBonus=+0.720
    ["NIGHTMARE"] = 48.1f, // role=SILENT picks=9009 selfBonus=+4.702
    ["NOSTALGIA"] = 52.6f, // role=COLORLESS picks=4721 selfBonus=+3.645
    ["NOT_YET"] = 42.6f, // role=IRONCLAD picks=11251 selfBonus=+3.015
    ["NOXIOUS_FUMES"] = 33.8f, // role=SILENT picks=32980 selfBonus=-1.733
    ["NO_ESCAPE"] = 35.7f, // role=NECROBINDER picks=17281 selfBonus=-1.350
    ["NULL"] = 29.7f, // role=DEFECT picks=13007 selfBonus=-0.675
    ["OBLIVION"] = 43.6f, // role=NECROBINDER picks=8504 selfBonus=+2.205
    ["OFFERING"] = 49.2f, // role=IRONCLAD picks=20123 selfBonus=+5.985
    ["OMNISLICE"] = 29.4f, // role=COLORLESS picks=6573 selfBonus=-6.795
    ["ONE_TWO_PUNCH"] = 39.1f, // role=IRONCLAD picks=10586 selfBonus=+1.440
    ["ORBIT"] = 43.7f, // role=REGENT picks=31109 selfBonus=+1.890
    ["OUTBREAK"] = 32.0f, // role=SILENT picks=14468 selfBonus=-2.543
    ["OVERCLOCK"] = 37.3f, // role=DEFECT picks=22636 selfBonus=+2.745
    ["PACTS_END"] = 41.1f, // role=IRONCLAD picks=9007 selfBonus=+2.340
    ["PAGESTORM"] = 49.0f, // role=NECROBINDER picks=11855 selfBonus=+4.635
    ["PALE_BLUE_DOT"] = 52.4f, // role=REGENT picks=11244 selfBonus=+5.805
    ["PANACHE"] = 38.4f, // role=COLORLESS picks=7433 selfBonus=-2.745
    ["PANIC_BUTTON"] = 43.4f, // role=COLORLESS picks=2521 selfBonus=-0.495
    ["PARRY"] = 39.6f, // role=REGENT picks=19192 selfBonus=+0.045
    ["PARTICLE_WALL"] = 39.2f, // role=REGENT picks=18612 selfBonus=-0.135
    ["PATTER"] = 34.3f, // role=REGENT picks=26831 selfBonus=-2.340
    ["PERFECTED_STRIKE"] = 25.2f, // role=IRONCLAD picks=24399 selfBonus=-4.815
    ["PHANTOM_BLADES"] = 37.7f, // role=SILENT picks=24789 selfBonus=+0.022
    ["PHOTON_CUT"] = 31.5f, // role=REGENT picks=13706 selfBonus=-3.600
    ["PIERCING_WAIL"] = 38.4f, // role=SILENT picks=63683 selfBonus=+0.337
    ["PILLAGE"] = 38.0f, // role=IRONCLAD picks=12694 selfBonus=+0.945
    ["PILLAR_OF_CREATION"] = 46.7f, // role=REGENT picks=20485 selfBonus=+3.240
    ["PINPOINT"] = 30.8f, // role=SILENT picks=10408 selfBonus=-3.083
    ["POISONED_STAB"] = 24.1f, // role=SILENT picks=17942 selfBonus=-6.098
    ["POKE"] = 29.5f, // role=NECROBINDER picks=17902 selfBonus=-4.140
    ["POMMEL_STRIKE"] = 34.4f, // role=IRONCLAD picks=54477 selfBonus=-0.675
    ["POUNCE"] = 29.3f, // role=SILENT picks=9791 selfBonus=-3.758
    ["PRECISE_CUT"] = 27.9f, // role=SILENT picks=6917 selfBonus=-4.388
    ["PREDATOR"] = 29.4f, // role=SILENT picks=10432 selfBonus=-3.713
    ["PREPARED"] = 36.1f, // role=SILENT picks=67239 selfBonus=-0.698
    ["PREP_TIME"] = 35.6f, // role=COLORLESS picks=5302 selfBonus=-4.005
    ["PRIMAL_FORCE"] = 32.3f, // role=IRONCLAD picks=5496 selfBonus=-1.620
    ["PRODUCTION"] = 43.5f, // role=COLORLESS picks=13635 selfBonus=-0.450
    ["PROLONG"] = 43.9f, // role=COLORLESS picks=10009 selfBonus=-0.270
    ["PROPHESIZE"] = 48.0f, // role=REGENT picks=5152 selfBonus=+3.825
    ["PROTECTOR"] = 35.5f, // role=NECROBINDER picks=3833 selfBonus=-1.440
    ["PROWESS"] = 38.8f, // role=COLORLESS picks=13174 selfBonus=-2.565
    ["PULL_AGGRO"] = 31.1f, // role=NECROBINDER picks=22533 selfBonus=-3.420
    ["PULL_FROM_BELOW"] = 43.8f, // role=NECROBINDER picks=10172 selfBonus=+2.295
    ["PURITY"] = 50.1f, // role=COLORLESS picks=7254 selfBonus=+2.520
    ["PUTREFY"] = 43.2f, // role=NECROBINDER picks=20611 selfBonus=+2.025
    ["PYRE"] = 42.6f, // role=IRONCLAD picks=16020 selfBonus=+3.015
    ["QUADCAST"] = 26.5f, // role=DEFECT picks=3773 selfBonus=-2.115
    ["QUASAR"] = 43.4f, // role=REGENT picks=14216 selfBonus=+1.755
    ["RADIATE"] = 37.6f, // role=REGENT picks=12459 selfBonus=-0.855
    ["RAGE"] = 32.7f, // role=IRONCLAD picks=19727 selfBonus=-1.440
    ["RAINBOW"] = 29.3f, // role=DEFECT picks=4114 selfBonus=-0.855
    ["RALLY"] = 59.9f, // role=COLORLESS picks=4761 selfBonus=+6.930
    ["RATTLE"] = 34.0f, // role=NECROBINDER picks=8178 selfBonus=-2.115
    ["REANIMATE"] = 46.6f, // role=NECROBINDER picks=9138 selfBonus=+3.555
    ["REAP"] = 29.6f, // role=NECROBINDER picks=15075 selfBonus=-4.095
    ["REAPER_FORM"] = 44.3f, // role=NECROBINDER picks=10125 selfBonus=+2.520
    ["REAVE"] = 28.9f, // role=NECROBINDER picks=13158 selfBonus=-4.410
    ["REBOOT"] = 43.2f, // role=DEFECT picks=8735 selfBonus=+5.400
    ["REFINE_BLADE"] = 35.3f, // role=REGENT picks=29564 selfBonus=-1.890
    ["REFLECT"] = 36.2f, // role=REGENT picks=25220 selfBonus=-1.485
    ["REFLEX"] = 43.4f, // role=SILENT picks=19727 selfBonus=+2.587
    ["REFRACT"] = 24.2f, // role=DEFECT picks=10914 selfBonus=-3.150
    ["REND"] = 53.7f, // role=COLORLESS picks=1105 selfBonus=+4.140
    ["RESONANCE"] = 36.2f, // role=REGENT picks=8808 selfBonus=-1.485
    ["RESTLESSNESS"] = 42.1f, // role=COLORLESS picks=3977 selfBonus=-1.080
    ["RICOCHET"] = 29.5f, // role=SILENT picks=26586 selfBonus=-3.668
    ["RIGHT_HAND_HAND"] = 35.9f, // role=NECROBINDER picks=8771 selfBonus=-1.260
    ["ROCKET_PUNCH"] = 40.5f, // role=DEFECT picks=15564 selfBonus=+4.185
    ["ROLLING_BOULDER"] = 39.1f, // role=COLORLESS picks=3797 selfBonus=-2.430
    ["ROYALTIES"] = 38.6f, // role=REGENT picks=5830 selfBonus=-0.405
    ["ROYAL_GAMBLE"] = 40.7f, // role=REGENT picks=19794 selfBonus=+0.540
    ["RUPTURE"] = 34.5f, // role=IRONCLAD picks=22290 selfBonus=-0.630
    ["SACRIFICE"] = 39.5f, // role=NECROBINDER picks=3630 selfBonus=+0.360
    ["SALVO"] = 53.8f, // role=COLORLESS picks=2167 selfBonus=+4.185
    ["SCARE"] = 33.2f, // role=SILENT picks=7573 selfBonus=-2.003
    ["SCAVENGE"] = 38.9f, // role=DEFECT picks=14405 selfBonus=+3.465
    ["SCOURGE"] = 31.6f, // role=NECROBINDER picks=23012 selfBonus=-3.195
    ["SCRAPE"] = 31.8f, // role=DEFECT picks=9101 selfBonus=+0.270
    ["SCRAWL"] = 62.2f, // role=COLORLESS picks=3760 selfBonus=+7.965
    ["SCULPTING_STRIKE"] = 37.8f, // role=NECROBINDER picks=13367 selfBonus=-0.405
    ["SEANCE"] = 50.3f, // role=NECROBINDER picks=10569 selfBonus=+5.220
    ["SECOND_WIND"] = 45.1f, // role=IRONCLAD picks=19869 selfBonus=+4.140
    ["SECRET_TECHNIQUE"] = 61.1f, // role=COLORLESS picks=3984 selfBonus=+7.470
    ["SECRET_WEAPON"] = 51.3f, // role=COLORLESS picks=2598 selfBonus=+3.060
    ["SEEKER_STRIKE"] = 40.7f, // role=COLORLESS picks=3583 selfBonus=-1.710
    ["SEEKING_EDGE"] = 40.0f, // role=REGENT picks=8952 selfBonus=+0.225
    ["SENTRY_MODE"] = 43.2f, // role=NECROBINDER picks=7308 selfBonus=+2.025
    ["SERPENT_FORM"] = 43.5f, // role=SILENT picks=11280 selfBonus=+2.632
    ["SETUP_STRIKE"] = 24.9f, // role=IRONCLAD picks=17101 selfBonus=-4.950
    ["SEVEN_STARS"] = 37.2f, // role=REGENT picks=6670 selfBonus=-1.035
    ["SEVERANCE"] = 37.8f, // role=NECROBINDER picks=10901 selfBonus=-0.405
    ["SHADOWMELD"] = 43.2f, // role=SILENT picks=10196 selfBonus=+2.497
    ["SHADOW_SHIELD"] = 27.6f, // role=DEFECT picks=14920 selfBonus=-1.620
    ["SHADOW_STEP"] = 41.7f, // role=SILENT picks=8613 selfBonus=+1.822
    ["SHARED_FATE"] = 53.2f, // role=NECROBINDER picks=6866 selfBonus=+6.525
    ["SHATTER"] = 30.7f, // role=DEFECT picks=8273 selfBonus=-0.225
    ["SHINING_STRIKE"] = 31.4f, // role=REGENT picks=14049 selfBonus=-3.645
    ["SHOCKWAVE"] = 45.0f, // role=COLORLESS picks=10993 selfBonus=+0.225
    ["SHROUD"] = 36.7f, // role=NECROBINDER picks=12849 selfBonus=-0.900
    ["SHRUG_IT_OFF"] = 32.7f, // role=IRONCLAD picks=61266 selfBonus=-1.440
    ["SIC_EM"] = 33.1f, // role=NECROBINDER picks=8945 selfBonus=-2.520
    ["SIGNAL_BOOST"] = 40.7f, // role=DEFECT picks=9890 selfBonus=+4.275
    ["SKEWER"] = 32.0f, // role=SILENT picks=5008 selfBonus=-2.543
    ["SKIM"] = 40.9f, // role=DEFECT picks=16457 selfBonus=+4.365
    ["SLEIGHT_OF_FLESH"] = 38.7f, // role=NECROBINDER picks=18793 selfBonus=+0.000
    ["SLICE"] = 26.1f, // role=SILENT picks=11983 selfBonus=-5.198
    ["SMOKESTACK"] = 29.3f, // role=DEFECT picks=13089 selfBonus=-0.855
    ["SNAKEBITE"] = 30.5f, // role=SILENT picks=29386 selfBonus=-3.218
    ["SNAP"] = 36.0f, // role=NECROBINDER picks=21092 selfBonus=-1.215
    ["SNEAKY"] = 53.7f, // role=SILENT picks=9808 selfBonus=+7.222
    ["SOLAR_STRIKE"] = 26.7f, // role=REGENT picks=16654 selfBonus=-5.760
    ["SOUL_STORM"] = 39.2f, // role=NECROBINDER picks=5129 selfBonus=+0.225
    ["SOW"] = 23.0f, // role=NECROBINDER picks=12624 selfBonus=-7.065
    ["SPECTRUM_SHIFT"] = 45.1f, // role=REGENT picks=23217 selfBonus=+2.520
    ["SPINNER"] = 31.2f, // role=DEFECT picks=7938 selfBonus=+0.000
    ["SPIRIT_OF_ASH"] = 49.5f, // role=NECROBINDER picks=7337 selfBonus=+4.860
    ["SPITE"] = 32.6f, // role=IRONCLAD picks=9306 selfBonus=-1.485
    ["SPLASH"] = 43.7f, // role=COLORLESS picks=13159 selfBonus=-0.360
    ["SPOILS_OF_BATTLE"] = 37.4f, // role=REGENT picks=23962 selfBonus=-0.945
    ["SPUR"] = 36.7f, // role=NECROBINDER picks=15587 selfBonus=-0.900
    ["SQUEEZE"] = 40.7f, // role=NECROBINDER picks=5834 selfBonus=+0.900
    ["STAMPEDE"] = 30.5f, // role=IRONCLAD picks=22454 selfBonus=-2.430
    ["STARDUST"] = 38.8f, // role=REGENT picks=16340 selfBonus=-0.315
    ["STOKE"] = 51.1f, // role=IRONCLAD picks=12576 selfBonus=+6.840
    ["STOMP"] = 26.4f, // role=IRONCLAD picks=11709 selfBonus=-4.275
    ["STONE_ARMOR"] = 33.0f, // role=IRONCLAD picks=19225 selfBonus=-1.305
    ["STORM"] = 33.2f, // role=DEFECT picks=13086 selfBonus=+0.900
    ["STORM_OF_STEEL"] = 42.0f, // role=SILENT picks=9376 selfBonus=+1.957
    ["STRANGLE"] = 31.0f, // role=SILENT picks=13339 selfBonus=-2.993
    ["STRATAGEM"] = 50.0f, // role=COLORLESS picks=5589 selfBonus=+2.475
    ["STRIKE_DEFECT"] = 26.4f, // role=DEFECT picks=9698 selfBonus=-2.160
    ["STRIKE_IRONCLAD"] = 42.5f, // role=IRONCLAD picks=14884 selfBonus=+2.970
    ["STRIKE_NECROBINDER"] = 42.1f, // role=NECROBINDER picks=7917 selfBonus=+1.530
    ["STRIKE_REGENT"] = 42.1f, // role=REGENT picks=34547 selfBonus=+1.170
    ["STRIKE_SILENT"] = 48.8f, // role=SILENT picks=11819 selfBonus=+5.017
    ["SUBROUTINE"] = 34.6f, // role=DEFECT picks=20655 selfBonus=+1.530
    ["SUCKER_PUNCH"] = 28.5f, // role=SILENT picks=16567 selfBonus=-4.118
    ["SUMMON_FORTH"] = 39.2f, // role=REGENT picks=16702 selfBonus=-0.135
    ["SUNDER"] = 20.2f, // role=DEFECT picks=5965 selfBonus=-4.950
    ["SUPERCRITICAL"] = 41.1f, // role=DEFECT picks=10817 selfBonus=+4.455
    ["SUPERMASSIVE"] = 42.7f, // role=REGENT picks=11554 selfBonus=+1.440
    ["SUPPRESS"] = 36.9f, // role=SILENT picks=6214 selfBonus=-0.338
    ["SURVIVOR"] = 34.9f, // role=SILENT picks=25035 selfBonus=-1.238
    ["SWEEPING_BEAM"] = 22.6f, // role=DEFECT picks=14822 selfBonus=-3.870
    ["SWORD_BOOMERANG"] = 31.7f, // role=IRONCLAD picks=18617 selfBonus=-1.890
    ["SWORD_SAGE"] = 46.3f, // role=REGENT picks=9953 selfBonus=+3.060
    ["SYNCHRONIZE"] = 32.4f, // role=DEFECT picks=9691 selfBonus=+0.540
    ["SYNTHESIS"] = 28.3f, // role=DEFECT picks=6128 selfBonus=-1.305
    ["TACTICIAN"] = 42.0f, // role=SILENT picks=23596 selfBonus=+1.957
    ["TAG_TEAM"] = 43.6f, // role=COLORLESS picks=3911 selfBonus=-0.405
    ["TANK"] = 53.2f, // role=IRONCLAD picks=1925 selfBonus=+7.785
    ["TAUNT"] = 34.8f, // role=IRONCLAD picks=38976 selfBonus=-0.495
    ["TEAR_ASUNDER"] = 42.2f, // role=IRONCLAD picks=9228 selfBonus=+2.835
    ["TEMPEST"] = 28.7f, // role=DEFECT picks=11960 selfBonus=-1.125
    ["TERRAFORMING"] = 38.0f, // role=REGENT picks=7291 selfBonus=-0.675
    ["TESLA_COIL"] = 24.5f, // role=DEFECT picks=12030 selfBonus=-3.015
    ["THE_GAMBIT"] = 61.7f, // role=COLORLESS picks=1395 selfBonus=+7.740
    ["THE_HUNT"] = 33.2f, // role=SILENT picks=6165 selfBonus=-2.003
    ["THE_SCYTHE"] = 36.4f, // role=NECROBINDER picks=5329 selfBonus=-1.035
    ["THE_SEALED_THRONE"] = 47.5f, // role=REGENT picks=4642 selfBonus=+3.600
    ["THE_SMITH"] = 41.1f, // role=REGENT picks=9779 selfBonus=+0.720
    ["THINKING_AHEAD"] = 44.5f, // role=COLORLESS picks=5903 selfBonus=+0.000
    ["THRASH"] = 42.9f, // role=IRONCLAD picks=12707 selfBonus=+3.150
    ["THRUMMING_HATCHET"] = 29.5f, // role=COLORLESS picks=3425 selfBonus=-6.750
    ["THUNDER"] = 28.7f, // role=DEFECT picks=17463 selfBonus=-1.125
    ["THUNDERCLAP"] = 30.7f, // role=IRONCLAD picks=25469 selfBonus=-2.340
    ["TIMES_UP"] = 44.4f, // role=NECROBINDER picks=5856 selfBonus=+2.565
    ["TOOLS_OF_THE_TRADE"] = 48.6f, // role=SILENT picks=15609 selfBonus=+4.927
    ["TRACKING"] = 48.5f, // role=SILENT picks=14333 selfBonus=+4.882
    ["TRANSFIGURE"] = 49.7f, // role=NECROBINDER picks=12303 selfBonus=+4.950
    ["TRASH_TO_TREASURE"] = 41.8f, // role=DEFECT picks=9353 selfBonus=+4.770
    ["TREMBLE"] = 33.2f, // role=IRONCLAD picks=54064 selfBonus=-1.215
    ["TRUE_GRIT"] = 36.1f, // role=IRONCLAD picks=41603 selfBonus=+0.090
    ["TURBO"] = 34.0f, // role=DEFECT picks=43578 selfBonus=+1.260
    ["TWIN_STRIKE"] = 25.3f, // role=IRONCLAD picks=20102 selfBonus=-4.770
    ["TYRANNY"] = 53.6f, // role=REGENT picks=10963 selfBonus=+6.345
    ["ULTIMATE_DEFEND"] = 38.9f, // role=COLORLESS picks=16593 selfBonus=-2.520
    ["ULTIMATE_STRIKE"] = 33.8f, // role=COLORLESS picks=15535 selfBonus=-4.815
    ["UNLEASH"] = 33.3f, // role=NECROBINDER picks=11607 selfBonus=-2.430
    ["UNMOVABLE"] = 45.5f, // role=IRONCLAD picks=16827 selfBonus=+4.320
    ["UNRELENTING"] = 29.5f, // role=IRONCLAD picks=12059 selfBonus=-2.880
    ["UNTOUCHABLE"] = 33.5f, // role=SILENT picks=33979 selfBonus=-1.868
    ["UPPERCUT"] = 35.4f, // role=IRONCLAD picks=20591 selfBonus=-0.225
    ["UPROAR"] = 28.1f, // role=DEFECT picks=12452 selfBonus=-1.395
    ["UP_MY_SLEEVE"] = 34.9f, // role=SILENT picks=21944 selfBonus=-1.238
    ["VEILPIERCER"] = 38.0f, // role=NECROBINDER picks=9721 selfBonus=-0.315
    ["VENERATE"] = 25.9f, // role=REGENT picks=11567 selfBonus=-6.120
    ["VICIOUS"] = 43.2f, // role=IRONCLAD picks=23807 selfBonus=+3.285
    ["VOID_FORM"] = 45.7f, // role=REGENT picks=15116 selfBonus=+2.790
    ["VOLLEY"] = 32.5f, // role=COLORLESS picks=3257 selfBonus=-5.400
    ["WELL_LAID_PLANS"] = 42.6f, // role=SILENT picks=14634 selfBonus=+2.227
    ["WHIRLWIND"] = 30.8f, // role=IRONCLAD picks=15137 selfBonus=-2.295
    ["WHITE_NOISE"] = 30.4f, // role=DEFECT picks=22207 selfBonus=-0.360
    ["WISP"] = 37.3f, // role=NECROBINDER picks=33720 selfBonus=-0.630
    ["WRAITH_FORM"] = 25.4f, // role=SILENT picks=4179 selfBonus=-5.513
    ["WROUGHT_IN_WAR"] = 29.3f, // role=REGENT picks=15909 selfBonus=-4.590
    ["ZAP"] = 19.4f, // role=DEFECT picks=100683 selfBonus=-5.310
    };

    /// <summary>角色池（含无色）→ 该池内卡牌胜率中位（快照计算口径 picks>=500）。</summary>
    internal static readonly Dictionary<string, float> MedianWinRateByRole = new(StringComparer.Ordinal)
    {
    ["COLORLESS"] = 44.5f,
    ["DEFECT"] = 31.2f,
    ["IRONCLAD"] = 35.900000000000006f,
    ["NECROBINDER"] = 38.7f,
    ["REGENT"] = 39.5f,
    ["SILENT"] = 37.650000000000006f
    };

    /// <summary>卡牌 Id.Entry → 归属池（COLORLESS/IRONCLAD/SILENT/DEFECT/NECROBINDER/REGENT）。</summary>
    internal static readonly Dictionary<string, string> RoleById = new(StringComparer.Ordinal)
    {
    ["ABRASIVE"] = "SILENT",
    ["ACCELERANT"] = "SILENT",
    ["ACCURACY"] = "SILENT",
    ["ACROBATICS"] = "SILENT",
    ["ADAPTIVE_STRIKE"] = "DEFECT",
    ["ADRENALINE"] = "SILENT",
    ["AFTERIMAGE"] = "SILENT",
    ["AFTERLIFE"] = "NECROBINDER",
    ["AGGRESSION"] = "IRONCLAD",
    ["ALCHEMIZE"] = "COLORLESS",
    ["ALIGNMENT"] = "REGENT",
    ["ALL_FOR_ONE"] = "DEFECT",
    ["ANGER"] = "IRONCLAD",
    ["ANOINTED"] = "COLORLESS",
    ["ANTICIPATE"] = "SILENT",
    ["ARMAMENTS"] = "IRONCLAD",
    ["ARSENAL"] = "REGENT",
    ["ASHEN_STRIKE"] = "IRONCLAD",
    ["ASSASSINATE"] = "SILENT",
    ["ASTRAL_PULSE"] = "REGENT",
    ["AUTOMATION"] = "COLORLESS",
    ["BACKFLIP"] = "SILENT",
    ["BACKSTAB"] = "SILENT",
    ["BALL_LIGHTNING"] = "DEFECT",
    ["BANSHEES_CRY"] = "NECROBINDER",
    ["BARRAGE"] = "DEFECT",
    ["BARRICADE"] = "IRONCLAD",
    ["BASH"] = "IRONCLAD",
    ["BATTLE_TRANCE"] = "IRONCLAD",
    ["BEACON_OF_HOPE"] = "COLORLESS",
    ["BEAM_CELL"] = "DEFECT",
    ["BEAT_DOWN"] = "COLORLESS",
    ["BEAT_INTO_SHAPE"] = "REGENT",
    ["BEGONE"] = "REGENT",
    ["BELIEVE_IN_YOU"] = "COLORLESS",
    ["BIASED_COGNITION"] = "DEFECT",
    ["BIG_BANG"] = "REGENT",
    ["BLACK_HOLE"] = "REGENT",
    ["BLADE_DANCE"] = "SILENT",
    ["BLADE_OF_INK"] = "SILENT",
    ["BLIGHT_STRIKE"] = "NECROBINDER",
    ["BLOODLETTING"] = "IRONCLAD",
    ["BLOOD_WALL"] = "IRONCLAD",
    ["BLUDGEON"] = "IRONCLAD",
    ["BLUR"] = "SILENT",
    ["BODYGUARD"] = "NECROBINDER",
    ["BODY_SLAM"] = "IRONCLAD",
    ["BOLAS"] = "COLORLESS",
    ["BOMBARDMENT"] = "REGENT",
    ["BONE_SHARDS"] = "NECROBINDER",
    ["BOOST_AWAY"] = "DEFECT",
    ["BOOT_SEQUENCE"] = "DEFECT",
    ["BORROWED_TIME"] = "NECROBINDER",
    ["BOUNCING_FLASK"] = "SILENT",
    ["BRAND"] = "IRONCLAD",
    ["BREAKTHROUGH"] = "IRONCLAD",
    ["BUBBLE_BUBBLE"] = "SILENT",
    ["BUFFER"] = "DEFECT",
    ["BULK_UP"] = "DEFECT",
    ["BULLET_TIME"] = "SILENT",
    ["BULLY"] = "IRONCLAD",
    ["BULWARK"] = "REGENT",
    ["BUNDLE_OF_JOY"] = "REGENT",
    ["BURNING_PACT"] = "IRONCLAD",
    ["BURST"] = "SILENT",
    ["BURY"] = "NECROBINDER",
    ["CALAMITY"] = "COLORLESS",
    ["CALCIFY"] = "NECROBINDER",
    ["CALCULATED_GAMBLE"] = "SILENT",
    ["CALL_OF_THE_VOID"] = "NECROBINDER",
    ["CAPACITOR"] = "DEFECT",
    ["CAPTURE_SPIRIT"] = "NECROBINDER",
    ["CASCADE"] = "IRONCLAD",
    ["CATASTROPHE"] = "COLORLESS",
    ["CELESTIAL_MIGHT"] = "REGENT",
    ["CHAOS"] = "DEFECT",
    ["CHARGE"] = "REGENT",
    ["CHARGE_BATTERY"] = "DEFECT",
    ["CHILD_OF_THE_STARS"] = "REGENT",
    ["CHILL"] = "DEFECT",
    ["CINDER"] = "IRONCLAD",
    ["CLAW"] = "DEFECT",
    ["CLEANSE"] = "NECROBINDER",
    ["CLOAK_AND_DAGGER"] = "SILENT",
    ["CLOAK_OF_STARS"] = "REGENT",
    ["COLD_SNAP"] = "DEFECT",
    ["COLLISION_COURSE"] = "REGENT",
    ["COLOSSUS"] = "IRONCLAD",
    ["COMET"] = "REGENT",
    ["COMPILE_DRIVER"] = "DEFECT",
    ["CONFLAGRATION"] = "IRONCLAD",
    ["CONQUEROR"] = "REGENT",
    ["CONSUMING_SHADOW"] = "DEFECT",
    ["CONVERGENCE"] = "REGENT",
    ["COOLANT"] = "DEFECT",
    ["COOLHEADED"] = "DEFECT",
    ["COORDINATE"] = "COLORLESS",
    ["CORROSIVE_WAVE"] = "SILENT",
    ["CORRUPTION"] = "IRONCLAD",
    ["COSMIC_INDIFFERENCE"] = "REGENT",
    ["CRASH_LANDING"] = "REGENT",
    ["CREATIVE_AI"] = "DEFECT",
    ["CRIMSON_MANTLE"] = "IRONCLAD",
    ["CRUELTY"] = "IRONCLAD",
    ["CRUSH_UNDER"] = "REGENT",
    ["DAGGER_THROW"] = "SILENT",
    ["DANSE_MACABRE"] = "NECROBINDER",
    ["DARKNESS"] = "DEFECT",
    ["DARK_EMBRACE"] = "IRONCLAD",
    ["DARK_SHACKLES"] = "COLORLESS",
    ["DASH"] = "SILENT",
    ["DEADLY_POISON"] = "SILENT",
    ["DEATHBRINGER"] = "NECROBINDER",
    ["DEATHS_DOOR"] = "NECROBINDER",
    ["DEATH_MARCH"] = "NECROBINDER",
    ["DEBILITATE"] = "NECROBINDER",
    ["DECISIONS_DECISIONS"] = "REGENT",
    ["DEFEND_DEFECT"] = "DEFECT",
    ["DEFEND_IRONCLAD"] = "IRONCLAD",
    ["DEFEND_REGENT"] = "REGENT",
    ["DEFILE"] = "NECROBINDER",
    ["DEFLECT"] = "SILENT",
    ["DEFRAGMENT"] = "DEFECT",
    ["DELAY"] = "NECROBINDER",
    ["DEMESNE"] = "NECROBINDER",
    ["DEMONIC_SHIELD"] = "IRONCLAD",
    ["DEMON_FORM"] = "IRONCLAD",
    ["DEVASTATE"] = "REGENT",
    ["DEVOUR_LIFE"] = "NECROBINDER",
    ["DIRGE"] = "NECROBINDER",
    ["DISCOVERY"] = "COLORLESS",
    ["DISMANTLE"] = "IRONCLAD",
    ["DOMINATE"] = "IRONCLAD",
    ["DOUBLE_ENERGY"] = "DEFECT",
    ["DRAIN_POWER"] = "NECROBINDER",
    ["DRAMATIC_ENTRANCE"] = "COLORLESS",
    ["DREDGE"] = "NECROBINDER",
    ["DRUM_OF_BATTLE"] = "IRONCLAD",
    ["DUALCAST"] = "DEFECT",
    ["DYING_STAR"] = "REGENT",
    ["ECHOING_SLASH"] = "SILENT",
    ["ECHO_FORM"] = "DEFECT",
    ["EIDOLON"] = "NECROBINDER",
    ["END_OF_DAYS"] = "NECROBINDER",
    ["ENERGY_SURGE"] = "DEFECT",
    ["ENFEEBLING_TOUCH"] = "NECROBINDER",
    ["ENTROPY"] = "COLORLESS",
    ["ENVENOM"] = "SILENT",
    ["EQUILIBRIUM"] = "COLORLESS",
    ["ERADICATE"] = "NECROBINDER",
    ["ESCAPE_PLAN"] = "SILENT",
    ["ETERNAL_ARMOR"] = "COLORLESS",
    ["EVIL_EYE"] = "IRONCLAD",
    ["EXPECT_A_FIGHT"] = "IRONCLAD",
    ["EXPERTISE"] = "SILENT",
    ["FALLING_STAR"] = "REGENT",
    ["FAN_OF_KNIVES"] = "SILENT",
    ["FASTEN"] = "COLORLESS",
    ["FEAR"] = "NECROBINDER",
    ["FEED"] = "IRONCLAD",
    ["FEEL_NO_PAIN"] = "IRONCLAD",
    ["FERAL"] = "DEFECT",
    ["FETCH"] = "NECROBINDER",
    ["FIEND_FIRE"] = "IRONCLAD",
    ["FIGHT_ME"] = "IRONCLAD",
    ["FIGHT_THROUGH"] = "DEFECT",
    ["FINESSE"] = "COLORLESS",
    ["FINISHER"] = "SILENT",
    ["FISTICUFFS"] = "COLORLESS",
    ["FLAK_CANNON"] = "DEFECT",
    ["FLAME_BARRIER"] = "IRONCLAD",
    ["FLANKING"] = "SILENT",
    ["FLASH_OF_STEEL"] = "COLORLESS",
    ["FLATTEN"] = "NECROBINDER",
    ["FLECHETTES"] = "SILENT",
    ["FLICK_FLACK"] = "SILENT",
    ["FOCUSED_STRIKE"] = "DEFECT",
    ["FOOTWORK"] = "SILENT",
    ["FORBIDDEN_GRIMOIRE"] = "NECROBINDER",
    ["FOREGONE_CONCLUSION"] = "REGENT",
    ["FRIENDSHIP"] = "NECROBINDER",
    ["FTL"] = "DEFECT",
    ["FURNACE"] = "REGENT",
    ["FUSION"] = "DEFECT",
    ["GAMMA_BLAST"] = "REGENT",
    ["GANG_UP"] = "COLORLESS",
    ["GATHER_LIGHT"] = "REGENT",
    ["GENESIS"] = "REGENT",
    ["GENETIC_ALGORITHM"] = "DEFECT",
    ["GLACIER"] = "DEFECT",
    ["GLASSWORK"] = "DEFECT",
    ["GLIMMER"] = "REGENT",
    ["GLIMPSE_BEYOND"] = "NECROBINDER",
    ["GLITTERSTREAM"] = "REGENT",
    ["GLOW"] = "REGENT",
    ["GOLD_AXE"] = "COLORLESS",
    ["GO_FOR_THE_EYES"] = "DEFECT",
    ["GRAND_FINALE"] = "SILENT",
    ["GRAVEBLAST"] = "NECROBINDER",
    ["GRAVE_WARDEN"] = "NECROBINDER",
    ["GUARDS"] = "REGENT",
    ["GUNK_UP"] = "DEFECT",
    ["HAILSTORM"] = "DEFECT",
    ["HAMMER_TIME"] = "REGENT",
    ["HAND_OF_GREED"] = "COLORLESS",
    ["HAND_TRICK"] = "SILENT",
    ["HANG"] = "NECROBINDER",
    ["HAUNT"] = "NECROBINDER",
    ["HAVOC"] = "IRONCLAD",
    ["HAZE"] = "SILENT",
    ["HEADBUTT"] = "IRONCLAD",
    ["HEAVENLY_DRILL"] = "REGENT",
    ["HEGEMONY"] = "REGENT",
    ["HEIRLOOM_HAMMER"] = "REGENT",
    ["HELIX_DRILL"] = "DEFECT",
    ["HELLRAISER"] = "IRONCLAD",
    ["HEMOKINESIS"] = "IRONCLAD",
    ["HIDDEN_CACHE"] = "REGENT",
    ["HIDDEN_DAGGERS"] = "SILENT",
    ["HIDDEN_GEM"] = "COLORLESS",
    ["HIGH_FIVE"] = "NECROBINDER",
    ["HOLOGRAM"] = "DEFECT",
    ["HOTFIX"] = "DEFECT",
    ["HOWL_FROM_BEYOND"] = "IRONCLAD",
    ["HUDDLE_UP"] = "COLORLESS",
    ["HYPERBEAM"] = "DEFECT",
    ["ICE_LANCE"] = "DEFECT",
    ["IGNITION"] = "DEFECT",
    ["IMPATIENCE"] = "COLORLESS",
    ["IMPERVIOUS"] = "IRONCLAD",
    ["INFERNAL_BLADE"] = "IRONCLAD",
    ["INFERNO"] = "IRONCLAD",
    ["INFINITE_BLADES"] = "SILENT",
    ["INFLAME"] = "IRONCLAD",
    ["INTERCEPT"] = "COLORLESS",
    ["INVOKE"] = "NECROBINDER",
    ["IRON_WAVE"] = "IRONCLAD",
    ["ITERATION"] = "DEFECT",
    ["I_AM_INVINCIBLE"] = "REGENT",
    ["JACKPOT"] = "COLORLESS",
    ["JACK_OF_ALL_TRADES"] = "COLORLESS",
    ["JUGGERNAUT"] = "IRONCLAD",
    ["JUGGLING"] = "IRONCLAD",
    ["KINGLY_KICK"] = "REGENT",
    ["KINGLY_PUNCH"] = "REGENT",
    ["KNIFE_TRAP"] = "SILENT",
    ["KNOCKDOWN"] = "COLORLESS",
    ["KNOW_THY_PLACE"] = "REGENT",
    ["LARGESSE"] = "REGENT",
    ["LEADING_STRIKE"] = "SILENT",
    ["LEAP"] = "DEFECT",
    ["LEGION_OF_BONE"] = "NECROBINDER",
    ["LEG_SWEEP"] = "SILENT",
    ["LETHALITY"] = "NECROBINDER",
    ["LIFT"] = "COLORLESS",
    ["LIGHTNING_ROD"] = "DEFECT",
    ["LOOP"] = "DEFECT",
    ["LUNAR_BLAST"] = "REGENT",
    ["MACHINE_LEARNING"] = "DEFECT",
    ["MAKE_IT_SO"] = "REGENT",
    ["MALAISE"] = "SILENT",
    ["MANGLE"] = "IRONCLAD",
    ["MANIFEST_AUTHORITY"] = "REGENT",
    ["MASTER_OF_STRATEGY"] = "COLORLESS",
    ["MASTER_PLANNER"] = "SILENT",
    ["MAYHEM"] = "COLORLESS",
    ["MELANCHOLY"] = "NECROBINDER",
    ["MEMENTO_MORI"] = "SILENT",
    ["METEOR_SHOWER"] = "REGENT",
    ["METEOR_STRIKE"] = "DEFECT",
    ["MIMIC"] = "COLORLESS",
    ["MIND_BLAST"] = "COLORLESS",
    ["MIRAGE"] = "SILENT",
    ["MISERY"] = "NECROBINDER",
    ["MODDED"] = "DEFECT",
    ["MOLTEN_FIST"] = "IRONCLAD",
    ["MOMENTUM_STRIKE"] = "DEFECT",
    ["MONARCHS_GAZE"] = "REGENT",
    ["MONOLOGUE"] = "REGENT",
    ["MULTI_CAST"] = "DEFECT",
    ["NECRO_MASTERY"] = "NECROBINDER",
    ["NEGATIVE_PULSE"] = "NECROBINDER",
    ["NEUROSURGE"] = "NECROBINDER",
    ["NEUTRALIZE"] = "SILENT",
    ["NEUTRON_AEGIS"] = "REGENT",
    ["NIGHTMARE"] = "SILENT",
    ["NOSTALGIA"] = "COLORLESS",
    ["NOT_YET"] = "IRONCLAD",
    ["NOXIOUS_FUMES"] = "SILENT",
    ["NO_ESCAPE"] = "NECROBINDER",
    ["NULL"] = "DEFECT",
    ["OBLIVION"] = "NECROBINDER",
    ["OFFERING"] = "IRONCLAD",
    ["OMNISLICE"] = "COLORLESS",
    ["ONE_TWO_PUNCH"] = "IRONCLAD",
    ["ORBIT"] = "REGENT",
    ["OUTBREAK"] = "SILENT",
    ["OVERCLOCK"] = "DEFECT",
    ["PACTS_END"] = "IRONCLAD",
    ["PAGESTORM"] = "NECROBINDER",
    ["PALE_BLUE_DOT"] = "REGENT",
    ["PANACHE"] = "COLORLESS",
    ["PANIC_BUTTON"] = "COLORLESS",
    ["PARRY"] = "REGENT",
    ["PARTICLE_WALL"] = "REGENT",
    ["PATTER"] = "REGENT",
    ["PERFECTED_STRIKE"] = "IRONCLAD",
    ["PHANTOM_BLADES"] = "SILENT",
    ["PHOTON_CUT"] = "REGENT",
    ["PIERCING_WAIL"] = "SILENT",
    ["PILLAGE"] = "IRONCLAD",
    ["PILLAR_OF_CREATION"] = "REGENT",
    ["PINPOINT"] = "SILENT",
    ["POISONED_STAB"] = "SILENT",
    ["POKE"] = "NECROBINDER",
    ["POMMEL_STRIKE"] = "IRONCLAD",
    ["POUNCE"] = "SILENT",
    ["PRECISE_CUT"] = "SILENT",
    ["PREDATOR"] = "SILENT",
    ["PREPARED"] = "SILENT",
    ["PREP_TIME"] = "COLORLESS",
    ["PRIMAL_FORCE"] = "IRONCLAD",
    ["PRODUCTION"] = "COLORLESS",
    ["PROLONG"] = "COLORLESS",
    ["PROPHESIZE"] = "REGENT",
    ["PROTECTOR"] = "NECROBINDER",
    ["PROWESS"] = "COLORLESS",
    ["PULL_AGGRO"] = "NECROBINDER",
    ["PULL_FROM_BELOW"] = "NECROBINDER",
    ["PURITY"] = "COLORLESS",
    ["PUTREFY"] = "NECROBINDER",
    ["PYRE"] = "IRONCLAD",
    ["QUADCAST"] = "DEFECT",
    ["QUASAR"] = "REGENT",
    ["RADIATE"] = "REGENT",
    ["RAGE"] = "IRONCLAD",
    ["RAINBOW"] = "DEFECT",
    ["RALLY"] = "COLORLESS",
    ["RATTLE"] = "NECROBINDER",
    ["REANIMATE"] = "NECROBINDER",
    ["REAP"] = "NECROBINDER",
    ["REAPER_FORM"] = "NECROBINDER",
    ["REAVE"] = "NECROBINDER",
    ["REBOOT"] = "DEFECT",
    ["REFINE_BLADE"] = "REGENT",
    ["REFLECT"] = "REGENT",
    ["REFLEX"] = "SILENT",
    ["REFRACT"] = "DEFECT",
    ["REND"] = "COLORLESS",
    ["RESONANCE"] = "REGENT",
    ["RESTLESSNESS"] = "COLORLESS",
    ["RICOCHET"] = "SILENT",
    ["RIGHT_HAND_HAND"] = "NECROBINDER",
    ["ROCKET_PUNCH"] = "DEFECT",
    ["ROLLING_BOULDER"] = "COLORLESS",
    ["ROYALTIES"] = "REGENT",
    ["ROYAL_GAMBLE"] = "REGENT",
    ["RUPTURE"] = "IRONCLAD",
    ["SACRIFICE"] = "NECROBINDER",
    ["SALVO"] = "COLORLESS",
    ["SCARE"] = "SILENT",
    ["SCAVENGE"] = "DEFECT",
    ["SCOURGE"] = "NECROBINDER",
    ["SCRAPE"] = "DEFECT",
    ["SCRAWL"] = "COLORLESS",
    ["SCULPTING_STRIKE"] = "NECROBINDER",
    ["SEANCE"] = "NECROBINDER",
    ["SECOND_WIND"] = "IRONCLAD",
    ["SECRET_TECHNIQUE"] = "COLORLESS",
    ["SECRET_WEAPON"] = "COLORLESS",
    ["SEEKER_STRIKE"] = "COLORLESS",
    ["SEEKING_EDGE"] = "REGENT",
    ["SENTRY_MODE"] = "NECROBINDER",
    ["SERPENT_FORM"] = "SILENT",
    ["SETUP_STRIKE"] = "IRONCLAD",
    ["SEVEN_STARS"] = "REGENT",
    ["SEVERANCE"] = "NECROBINDER",
    ["SHADOWMELD"] = "SILENT",
    ["SHADOW_SHIELD"] = "DEFECT",
    ["SHADOW_STEP"] = "SILENT",
    ["SHARED_FATE"] = "NECROBINDER",
    ["SHATTER"] = "DEFECT",
    ["SHINING_STRIKE"] = "REGENT",
    ["SHOCKWAVE"] = "COLORLESS",
    ["SHROUD"] = "NECROBINDER",
    ["SHRUG_IT_OFF"] = "IRONCLAD",
    ["SIC_EM"] = "NECROBINDER",
    ["SIGNAL_BOOST"] = "DEFECT",
    ["SKEWER"] = "SILENT",
    ["SKIM"] = "DEFECT",
    ["SLEIGHT_OF_FLESH"] = "NECROBINDER",
    ["SLICE"] = "SILENT",
    ["SMOKESTACK"] = "DEFECT",
    ["SNAKEBITE"] = "SILENT",
    ["SNAP"] = "NECROBINDER",
    ["SNEAKY"] = "SILENT",
    ["SOLAR_STRIKE"] = "REGENT",
    ["SOUL_STORM"] = "NECROBINDER",
    ["SOW"] = "NECROBINDER",
    ["SPECTRUM_SHIFT"] = "REGENT",
    ["SPINNER"] = "DEFECT",
    ["SPIRIT_OF_ASH"] = "NECROBINDER",
    ["SPITE"] = "IRONCLAD",
    ["SPLASH"] = "COLORLESS",
    ["SPOILS_OF_BATTLE"] = "REGENT",
    ["SPUR"] = "NECROBINDER",
    ["SQUEEZE"] = "NECROBINDER",
    ["STAMPEDE"] = "IRONCLAD",
    ["STARDUST"] = "REGENT",
    ["STOKE"] = "IRONCLAD",
    ["STOMP"] = "IRONCLAD",
    ["STONE_ARMOR"] = "IRONCLAD",
    ["STORM"] = "DEFECT",
    ["STORM_OF_STEEL"] = "SILENT",
    ["STRANGLE"] = "SILENT",
    ["STRATAGEM"] = "COLORLESS",
    ["STRIKE_DEFECT"] = "DEFECT",
    ["STRIKE_IRONCLAD"] = "IRONCLAD",
    ["STRIKE_NECROBINDER"] = "NECROBINDER",
    ["STRIKE_REGENT"] = "REGENT",
    ["STRIKE_SILENT"] = "SILENT",
    ["SUBROUTINE"] = "DEFECT",
    ["SUCKER_PUNCH"] = "SILENT",
    ["SUMMON_FORTH"] = "REGENT",
    ["SUNDER"] = "DEFECT",
    ["SUPERCRITICAL"] = "DEFECT",
    ["SUPERMASSIVE"] = "REGENT",
    ["SUPPRESS"] = "SILENT",
    ["SURVIVOR"] = "SILENT",
    ["SWEEPING_BEAM"] = "DEFECT",
    ["SWORD_BOOMERANG"] = "IRONCLAD",
    ["SWORD_SAGE"] = "REGENT",
    ["SYNCHRONIZE"] = "DEFECT",
    ["SYNTHESIS"] = "DEFECT",
    ["TACTICIAN"] = "SILENT",
    ["TAG_TEAM"] = "COLORLESS",
    ["TANK"] = "IRONCLAD",
    ["TAUNT"] = "IRONCLAD",
    ["TEAR_ASUNDER"] = "IRONCLAD",
    ["TEMPEST"] = "DEFECT",
    ["TERRAFORMING"] = "REGENT",
    ["TESLA_COIL"] = "DEFECT",
    ["THE_GAMBIT"] = "COLORLESS",
    ["THE_HUNT"] = "SILENT",
    ["THE_SCYTHE"] = "NECROBINDER",
    ["THE_SEALED_THRONE"] = "REGENT",
    ["THE_SMITH"] = "REGENT",
    ["THINKING_AHEAD"] = "COLORLESS",
    ["THRASH"] = "IRONCLAD",
    ["THRUMMING_HATCHET"] = "COLORLESS",
    ["THUNDER"] = "DEFECT",
    ["THUNDERCLAP"] = "IRONCLAD",
    ["TIMES_UP"] = "NECROBINDER",
    ["TOOLS_OF_THE_TRADE"] = "SILENT",
    ["TRACKING"] = "SILENT",
    ["TRANSFIGURE"] = "NECROBINDER",
    ["TRASH_TO_TREASURE"] = "DEFECT",
    ["TREMBLE"] = "IRONCLAD",
    ["TRUE_GRIT"] = "IRONCLAD",
    ["TURBO"] = "DEFECT",
    ["TWIN_STRIKE"] = "IRONCLAD",
    ["TYRANNY"] = "REGENT",
    ["ULTIMATE_DEFEND"] = "COLORLESS",
    ["ULTIMATE_STRIKE"] = "COLORLESS",
    ["UNLEASH"] = "NECROBINDER",
    ["UNMOVABLE"] = "IRONCLAD",
    ["UNRELENTING"] = "IRONCLAD",
    ["UNTOUCHABLE"] = "SILENT",
    ["UPPERCUT"] = "IRONCLAD",
    ["UPROAR"] = "DEFECT",
    ["UP_MY_SLEEVE"] = "SILENT",
    ["VEILPIERCER"] = "NECROBINDER",
    ["VENERATE"] = "REGENT",
    ["VICIOUS"] = "IRONCLAD",
    ["VOID_FORM"] = "REGENT",
    ["VOLLEY"] = "COLORLESS",
    ["WELL_LAID_PLANS"] = "SILENT",
    ["WHIRLWIND"] = "IRONCLAD",
    ["WHITE_NOISE"] = "DEFECT",
    ["WISP"] = "NECROBINDER",
    ["WRAITH_FORM"] = "SILENT",
    ["WROUGHT_IN_WAR"] = "REGENT",
    ["ZAP"] = "DEFECT"
    };

    /// <summary>
    /// 数据驱动加成：同池或接收职业未知按自身池中位（与原语义一致）；跨池卡对照接收职业中位。
    /// 返回值四舍五入到 0.001，与旧快照表数值在容差内一致。
    /// </summary>
    internal static float BonusFor(string entry, string receivingRole)
    {
        if (!WinRateById.TryGetValue(entry, out float win))
            return 0f;
        if (!RoleById.TryGetValue(entry, out string? ownRole) || ownRole is null)
            return 0f;
        if (!MedianWinRateByRole.TryGetValue(ownRole, out float median))
            return 0f;
        if (string.IsNullOrEmpty(receivingRole) || ownRole != receivingRole)
        {
            if (MedianWinRateByRole.TryGetValue(receivingRole, out float receivingMedian))
                median = receivingMedian;
        }
        float delta = Math.Clamp((win - median) * K, -Cap, Cap);
        return MathF.Round(delta, 3, MidpointRounding.ToEven);
    }
}
