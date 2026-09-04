# AI Auto-Run / AI自动跑局

An **AI full-run autopilot** Mod for *Slay the Spire 2*: instead of only solving the current fight, it plays the **whole run** for you — route, events, potions, gold, rest sites, relics and card picks are all decided and executed by the AI; inside fights it uses an async solver to play the optimal lines.

> Built on top of [Combat Solver](https://github.com/Torch1230/CombatSolver) by Torch (in-combat solving engine). Technical identifiers (namespace / mod id / folder) stay `CombatSolver`; product & public name is **AI Auto-Run**. Attribution, licensing and sources: see bottom of this file and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

---

## What AI Auto-Run does

### 🗺️ Plays the whole run (Neow → Boss)
- **Auto opening**: Neow ancient relic picks are valued net of cost (cursed/trade-off relics are evaluated as "effect − cost" instead of being blanket-rejected).
- **Auto map routing**: each path decision runs a danger-aware evaluation over the whole map DAG to the act boss (memoized look-ahead) — lower HP punishes elites, potions act as insurance, Ironclad post-combat heal lowers fight risk; rest/treasure/shop gain relative value when hurt.
- **Auto events**: all 58 events decompiled into a catalog; options are scored as **benefit − cost** — SL-able (reward visible) options use the actual card/relic value, non-SL (rolled on click) options use pool-weighted expectation; e.g. the bridge event holds off good cards (pay HP to reroll) and removes junk ones; Lost Wisp skips the relic when your deck has no power cards.
- **Auto rewards/shop/rest/chest/Boss**: card & relic pick scoring, purchases/removals, rest-site "heal vs upgrade" by route & HP risk, treasure rooms, fake merchant and other special rooms are all automated.
- **Potion & gold economy**: out-of-combat potions are only drunk for Juice/Blood to free slots (others must be discarded); retention cost adapts to route danger; near act end it saves potions and enters the boss low because A2+ heals only 80% of missing HP after the boss; gold value scales with shop reachability on the current map and measured event gold gates (≥100/120/125/149).

### 🧠 Data-driven scoring & ongoing calibration
- **Card scoring** uses community real-run stats (Spire Codex A10: winRate/pickRate, 483 cards, millions of picks) as win-rate-delta bonuses.
- **Ancient relic pools** (Neow/Orobas/Darv/Tezcatara/Vakuu/Nonupeipe/Pael/Tanx — 9 ancients, 70+ relics) are tabled one-by-one from decompiled effects, including deck synergy (e.g. power-synergy relics scaled by power count) and route/gold context.
- **Anonymous telemetry (opt-in)**: per-run stats (seed/character/ascension/floors/picks/relics/outcome) can be auto-uploaded for continued tuning (self-hosted receiver example: `tools/telemetry_receiver.py`).

### ⚙️ Usage & integration
- Settings page (main menu): master switch, fast mode, stop-on-fail, debug log, seed replay (A/B training), telemetry toggle & upload endpoint.
- In combat it reuses the Combat Solver engine: view recommendations only, execute the current turn, or fully auto-play the fight (cross-turn search, route reuse, three-state potion policy, etc. — see the engine section below).
- Single-player only; neither solving nor run decisions modify game RNG.

---

## In-combat engine (inherited from upstream Combat Solver)

Inside fights it uses Combat Solver's async engine: capture a stable combat root on the main thread, search across shadow states in the background (hand, draw pile, enemy intent, potions, relics, card choices, persistent effects), and present recommended lines with predicted HP loss and key actions — view-only / execute turn / continuous auto-play, native card-select flows, cross-turn route reuse, tunable search budgets (low→very high, 2-8 way parallel), UI themes & notifications. See upstream [Combat Solver](https://github.com/Torch1230/CombatSolver) for the engine; our docs live in `docs/` (ARCHITECTURE / DEVELOPMENT_NOTES / TEST_MATRIX).

---

## Open source, licensing & code provenance

Built on the following projects; secondary distribution or public release must credit the sources with links (permission granted by the authors):

- **Combat Solver** (in-combat engine, author Torch) — the base of this repository and the in-combat core.
  - GitHub: https://github.com/Torch1230/CombatSolver
  - Steam Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3790899961
  - Torch agreed (2026-09-04) to usage/secondary distribution with attribution and source links.
- **Random Foreseer** (source of the combat simulation core, author hotwords123; written permission granted).
  - GitHub: https://github.com/hotwords123/StS2.RandomForeseer
  - Steam Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3747531952

Provenance: the built-in combat simulation core uses and modifies parts of Random Foreseer (combat state, piles, RNG, fork, history & mirrors); the AI Auto-Run decision layer and run autopilot drivers are new work of this project. This project does not load or distribute the upstream assemblies as runtime dependencies; the runtime separation does not change the code-provenance relationship above.

Thanks to Torch, hotwords123 and their work. Written permission, attribution terms and third-party notices: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), shipped with every binary distribution.

This repository is public, but has no single license covering the whole tree; public visibility is not unrestricted copying/modification/redistribution. The licensing boundary of the source code is governed by `THIRD_PARTY_NOTICES.md`.

---

*中文版 README: [README.md](README.md)*
