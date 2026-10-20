# SP-6 — single-player Game speed (Normal / Fast / Showcase)

Base: `e87bd466` (amended to the final commit). Changes target the local single-player
host only: `Allegiance.SinglePlayer` (SkirmishOptions, SkirmishScreenModel, benchmark),
`Allegiance.ClientCore.Game` (status line, player-config persistence, proof hooks), and
tests. No change to native economy rules, multiplayer servers, or the rig.

## Why

Domingo's first offline skirmish on the Mac (2026-10-19) ran the real economy at native
pace (income x1, research x1): after a while only 2 vs 5 developments and one station per
side. Correct, but slow for a quick single-player session. The rig runs income x5000 and
research x8. SP-6 offers a speed choice.

## Player flow

Skirmish setup gains a **Game speed** row (after Commander, before Start as spectator):

```
Your race: Iron Coalition
Enemy race: Ga'Taraan
Size: Small 5v5
Tech: research
Bot difficulty: Normal
Commander: AI
Game speed: Normal          <- SP-6
Start as spectator: No
Start Skirmish
Back
```

LEFT/RIGHT cycles Normal -> Fast -> Showcase (wrap). The chosen speed is shown in the
in-game skirmish status line, e.g. `Skirmish [Fast speed] - AI commander active`
(captured from the real 2560x1440 client: `SP6_assets/skirmish_status_Fast.png`).

## Speed profiles

`SkirmishOptions.SpeedProfile` maps each speed onto the existing
`showcase.incomeMultiplier` / `showcase.researchTimeScale` / `showcase.finaleTimeoutSeconds`
through `SkirmishOptions.CreateConfig` (src/Allegiance.SinglePlayer/SkirmishOptions.cs).

| Speed | Income | Research | Finale timeout | Source |
| --- | ---: | ---: | ---: | --- |
| Normal | x1 | x1 (honours the benchmark/legacy `ResearchSpeed` override) | 300 s | native |
| Fast | x8 | x4 | 300 s | SP-6 local measurement (below) |
| Showcase | x5000 | x8 | 300 s | rig-authored showcase config |

The Showcase row mirrors the rig's authored config verbatim —
`docs/reports/P733_assets/rig-showcase-delta.json`: `incomeMultiplier 5000`,
`researchTimeScale 8`, `finaleTimeoutSeconds 300`, `loopResearchPlans false`.
Because `SkirmishSize`/team and tech mode are separate rows, size and research/unlocked
choice are untouched by the speed row.

Normal is byte-for-byte identical to the pre-SP-6 behaviour: it keeps `ResearchSpeed`
(the double that feeds `ResearchTimeScale` in the old `CreateConfig`) as the research
factor, so benchmark runs and the default offline game are unchanged. Fast and Showcase
override research to their fixed factors even if a benchmark override is present.

## Remembered in the player config (offline)

`SkirmishPrefs` (a JSON record of every setup row, including `Speed`) is saved to
`player.json` via `IntroPreferences.Skirmish` whenever a non-benchmark skirmish starts
(Game1.Skirmish.cs), and restored once when the Skirmish setup screen opens — unless a
saved benchmark recommendation already seeded the screen this run. Missing/empty config
degrades to defaults (Normal). This matches how the other skirmish choices are
remembered, and it is fully offline.

## Benchmark (P729) recommendation

`BenchmarkPolicy.Recommend` now sets `Speed` and emits a **Game speed** reason instead of
the old "Research speed: 1x":
- **Fast** is suggested only when the measured server tick has real headroom (tick p95 < 20 ms and not bot-bound), with a reason quoting the tick p95 and the Fast pace;
- **Showcase** (x5000 income) is never auto-applied — only machines that hold the tick budget at the chosen size should run it. That matches the brief's example.

`BenchmarkCommand`, `CurrentBenchmarkSettings`, the apply-on-suggestion path, and the
`TestedSettings` record all carry `Speed` so a benchmark that runs at a chosen speed
remembers it.

## Fast measurement (Small, income x8 / research x4)

20-sim-minute local skirmish, snapshotting completed developments and stations per side
from the embedded host (`host.OnSimulation`); full trail in `SP6_assets/fast-measurement.json`.

| Sim time | Side | Developments | Stations |
| --- | --- | ---: | ---: |
| 10 min | Alpha / Bravo | 3 / 5 | 1 / 1 |
| 20 min | Alpha / Bravo | 6 / 6 | 1 / 2 |

Development climbs steadily (1,1 -> 6,6) and both sides reach 6 completed developments
by 20 min — roughly the mid-tech region for a Small match, well ahead of the native 2v5
plateau Domingo observed. Stations lag because the bots prioritise research/combat over
expansion, which is expected for a Small fight. These values land inside the brief's
suggested band (income x5-10, research x3-4) and hit the "~20-30 min to mid tech" goal,
so Fast is fixed at income x8 / research x4.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
| --- | --- | --- | --- |
| Periodic economy treasury at incomeMultiplier | `FedSrv/fsmission.cpp:2294-2324` (income enters team treasury, distributed to wallets) | design | `showcase.incomeMultiplier` scales the existing treasury path (server config, unchanged); SP-6 only chooses the multiplier |
| Research accelerator | server `ShowcaseConfig.ResearchTimeScale`; documented as "not native gameplay" | design (port orchestration) | scales research rate in research tech mode |
| Bounded research-showcase finale | `Server.Showcase.cs` `FinaleTimeoutSeconds` | design | keeps the rig's 300 s bound for every speed |
| Setup row menu (native keyboard lists) | `WinTrek/introscreen.cpp`; P727/SP-5 rows | design | one more row, consistent indices |

SP-6 adds no new native rules; it selects existing configurable pacing, so the "known
bug/exploit" and "era constraint" classifications do not apply.

## Modern alternative considered

Native (and this port) couples economy pacing to two scalar accelerators and a bounded
finale. A modern approach could offer explicit **match-length presets** (e.g. "Quick 20
min", "Standard 45 min") that dynamically tune income/research/tick to hold a target
wall-clock length. We chose the simpler three-speed selector because it is transparent
(exact income/research factors), offline, and reuses the rig's already-audited showcase
plumbing rather than introducing a scheduler. A dynamic length-target could be a follow-up
if Domingo wants "ends in N minutes" semantics.

## Verification

- **Whole-solution build: 0 warnings / 0 errors** (`dotnet build AllegianceCore.slnx`).
- **New/affected tests 3x alone each** (SP6GameSpeedTests 8; SP5 spectator row, P727 menu
  cycle, P729 round-trip) — all pass 3/3 in fresh processes.
  New `SP6GameSpeedTests` covers: each speed -> correct config values (income/research/finale);
  Fast/Showcase override the benchmark research override; the row cycles all three speeds and
  shows in `Rows`; the speed+choices persist through `player.json` (`IntroPreferences.Skirmish`
  round-trip) and are restored; missing config yields Normal; the benchmark suggests Fast only
  with tick headroom and always with a Game speed reason.
- **Fast tier once on final code: PASS — 8884 tests, 0 failures** (271.1 s), plus a prior
  PASS on the core code (8884 tests). Summary: `TestResults/tiers-20261020T043351.264838Z/summary.json`.
- **AOT publish**: not required — `src/Allegiance.Server` and `src/Allegiance.Core` were not
  changed (only `SinglePlayer`, `ClientCore.Game`, tests). No source-generated-JSON shape changed.
- **P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds: 5/5**
  (110s each), since bots/flight/sim sit on the config path this feature feeds.
- **Visual proof (real client, 2560x1440 backbuffer via the autoshot hooks)**:
  - `SP6_assets/skirmish_setup_proof.png` — Single Player -> Skirmish setup showing the
    `Game speed: Normal` row (after Commander).
  - `SP6_assets/skirmish_status_Fast.png` — in-game status banner `Spectating: Skirmish [Fast speed]`.

Raw TRX/logs and the 938 MB artwork copy used for the live captures are outside the repo at
`~/allegiance-scratch/SP6/` (removed after the task; scratch rule); only the two PNGs and the
measurement JSON are committed.
