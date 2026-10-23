# P791 — nanite support pilots stuck at home repairing each other: diagnose, then send them to the fight

Branch `pi/p791` (worktree `~/allegiance-pi-p791`, base master `0e87f190`). Bot-side only: every production edit is in
`tools/allegiance-mcp/**` (+ tests). No Core/server/client/wire change — the root cause turned out to be harness policy,
not an engine or wire defect (evidence in §3).

## 1. Evidence

### 1.1 The incident (90v90 showcase arena, Alderan, 2026-10-22 17:20)

Logs: `/tmp/claude-1000/-home-domingo/dc695f76-6842-48d1-ae59-da657c8a8a7c/scratchpad/hero/session-classic-volumetric-171954/`
(`mcp_err.log` bot log, `server.out`). Raw copies: `~/allegiance-evidence/P791/incident/`.

- 18 support pilots (`[support]` ship ids 8099/8100/8119/8120 … 8259/8260 — every 20th id, both sides), 8,175 `[support]`
  target lines; **all** of them at home: 3,561 lines in Ganymede (1050), 4,557 in Organia (1051), **57** in the battle
  sector Alderan (1052).
- They target each other and their neighbours in a ring: `target=8119` 655 lines, `target=8219` 643, `target=8259` 627;
  one pilot (8140) is the target of 367 lines. They never leave.
- No damaged station anywhere in the log (`repair=station` never appears), so the station-recall branch is not involved.
- **What damaged them:** 127 `[collision]` events in `server.out`, **120 of them in the two home sectors**, 19 involving
  support pilots (3 support×support). Ship–ship collisions damage both parties (native velocity blend + damage split,
  `Igc/shipIGC.cpp:1093-1124,1141-1158`), and the repair approach itself flies hard at the target (native throttle
  `0.5 + ((pi - da) / 2pi)`, shipIGC.cpp:2624-2626), so every repair pass risks another bump. This is a positive feedback
  loop: bump → two slightly damaged ships → two repair locks → two more approach passes → more bumps.
- 90v90 rig batch 20 (2026-10-23, 4× 200v200): ~75% of `[support]` picks in the two home sectors, 2,011 of 2,044 repairs on
  ships, home-sector ship collisions 62/25 per 10 min — same signature at scale.

### 1.2 Headless reproduction (base = master behaviour, unfixed code)

`SC2SupportPhaseLiveTests.Support_pilots_follow_the_side_phase_instead_of_parking_at_home`
(tests/Allegiance.ClientCore.Tests/SC2SupportPhaseLiveTests.cs) runs a 30-ship showcase arena (m2-style ring, staging on,
finale at 2 min, research mode, 4 support pilots/side) and logs `[p791-sample]` per holding pilot: target, observed
`Latest.HullFraction`, pose age (`ClientWorld.PoseAgeSeconds`), and the **server's real** `Ship.Fraction`.

Base run (`.scratch/p791_base_run.log`, evidence copy `~/allegiance-evidence/P791/base/`):

- **0 / 604** support-pilot samples in the phase's battle sector (0.0%); all 858 `[support]` target lines are in the two
  home sectors (305 + 553), none in the battle sector. The phase accessor (read-only) had a valid anchor the whole time.
- The ring reproduces exactly: `target=8134` picked 179 times, `8117` 122, and support pilots 8100/8109 repair **each
  other** (120/118 picks). 422 collisions, 409 in the home sectors — the bump→repair→bump loop from §1.1 at small scale.

### 1.3 Diagnostic answers (brief §Scope 1)

- **(a) Are the targets really damaged, and by what?** YES — really damaged, by home-sector collisions (congestion at
  staging/relaunch + repair approaches; §1.1/§1.2 numbers). In the fixed runs, observed vs server hull fraction never
  differed by more than 0.02 on any of 78 held-target samples (`[p791-sample]`, run log
  `.scratch/p791_fixed_run2.log`): with `minUpdateHz = 2` the wire keeps observations fresh, so this is **not** a stale
  snapshot bug at these update rates. Quantization matters though: hull is quantized to 1/240 on the wire
  (`ClientWorld.cs:3102` `BpHullFraction / 240`), so a single gentle bump puts a ship at observed 0.9958 "forever" —
  visible damage to the pick rule, even though the ship is effectively whole.
- **(b) Why do support pilots stay at home after relaunch?** Because `PickRepairTarget` scored the **current sector
  first** with no damage floor: any 0.95+ quantization bump at home qualified as a repair target, the route-to-strike
  branch (`TryDriveSupport`'s tail) never fired, and the ring sustained itself through collision feedback. Root cause is
  **harness policy** (`BotManager.Support.cs` selection), not engine/wire — no Core change needed.

## 2. Native reference (what must NOT change)

- Repair fire law `c_cidRepair` (`Igc/shipIGC.cpp:2608-2676`): dodge, lead, fire `allWeaponsIGC` only while
  `da <= 0.10f && t <= lifespan * 0.95f` and `needsNan`; `needsNan = false` when the target ship's `GetFraction() == 1.0f`
  (`:2668-2671`). `FlyRepairTarget` mirrors this including the new observed-full stop.
- Target selection for autonomous support pilots is harness policy (comment at `BotManager.Support.cs`, P778a). P778a's
  ordering (finale bomber > pushers > station > others) is preserved and unit-tested.
- Payday context (`Igc/shipIGC.cpp:1985-1988`, `FedSrv/fsmission.cpp:2282`): the opening fight is about payday growth,
  hence support pilots belong with the fighter fleet in the contested sector during the opening.

## 3. The fix (all in `tools/allegiance-mcp/**`)

`BotManager.Support.cs`:

1. **Phase signal (minimal shared accessor, bot-side).** `SupportPhaseFrom` (pure) + `SupportPhaseForSide` (gather):
   an active finale run (bomb runs, `BotManager.Finale.cs`) outranks an active SC-7/8/9 strike wave
   (`ResearchTactics.cs` — new internal `StrikeWaveActive`/`StrikeSector` accessors, no behaviour change to the
   commander), which outranks the side's fighter-fleet rendezvous (mode of the fighters' `TargetSector`, falling back to
   where the fighters actually are — needed while the map is still fogged, P797). `PickSupportDestination` (pure):
   damaged-own-station recall > phase anchor > own rendezvous. The same inputs with a different phase yield a different
   destination (unit-tested). **Note for P792:** the phase switch is driven by ONE global timer —
   `ShowcaseFinale.Settings.AfterMinutes` (default 30) via `ShowcaseFinale.TriggerSides` (`ShowcaseFinale.cs:35-48`) —
   not per civ, exactly as the brief suspected.
2. **Phase-following destination.** `TryDriveSupport` now routes the pilot to the phase anchor sector and repairs THERE:
   anchor = strike wing position when the wing is in the anchor sector, else nearest companion already there.
3. **Damage bars.** In the battle sector any damaged own ship (0, 1) still qualifies (P778a unchanged). At home only
   genuine damage starts a repair: observed `< 0.85` (`RepairStartFraction`), and **never another support pilot** — the
   mutual ring is dead. Support pilots are candidates anywhere only when genuinely damaged (< 0.85).
4. **Hysteresis (`EvaluateRepairHold`, pure + `ApplyRepairHold`).** A hold starts when the target changes; it is dropped
   when the target reaches observed full (completed — the native == 1.0 stop), reads dead/gone, its observation goes
   stale (`PoseAgeSeconds > 6 s`), it stops improving for 10 s (improvement = any rise over the PREVIOUS observation ≥
   half a quantization step — an all-time-best check masks progress on re-damaged ships and churned 26+ starts with 0
   completions in the second live run), or the 45 s leash expires (`RepairLeashSeconds`; the light gate is 60 s).
   A dropped target is put on a 10 s per-target cooldown and cannot be re-picked while it lasts.
5. **Prefer-held target.** `PickRepairTarget` keeps the held ship while it still qualifies: nearest-first re-picking
   flip-flops between two equally damaged ships and churned holds in the live runs.
6. **Telemetry.** `[support-summary]` once per minute per side (bounded, rate-limited): phase, anchor, pilots
   battle/travelling/home, repairs started/completed, mean completed repair duration. The `[support]` target-change line
   now carries the observed hull and the phase.
7. **Fire law.** `FlyRepairTarget` holds fire when the observed fraction is 1.0 (native `needsNan` mirror,
   shipIGC.cpp:2668-2671).

State: new `ActiveBot` fields (`SupportHoldTarget/Since/Last/ImproveAt`, `SupportCooldownTarget/Until`), reset in
`ResetNewLife` (`BotManager.Lifecycle.cs`). Phases switch as before — not re-tuned here (P792 owns civ-driven timing).

## 4. Before / after

| Metric | Base (unfixed) | Fixed (run5) |
|---|---|---|
| Support-pilot samples in phase anchor sector (30-ship arena, post-warmup) | **0 / 604 (0.0%)** | see §6 |
| `[support]` target lines in home sectors vs battle | 858 home / 0 battle | see §6 |
| Mutual home support-repair pairs | ring (targets 120/118 lines each) | 0 |
| Longest single-target hold | — (no hold tracking on base) | ≤ 60 s gate |

## 5. Rig recipe (200v200, Claude's runner)

1. Run the rig as for batch 20 and capture bot+server logs.
2. `grep '\[support-summary\]'` — one line per side per minute: `phase=` opening|strike, `anchor=`, `battle=/travelling=/home=`,
   `repairsStarted=`/`repairsCompleted=`/`meanRepairSeconds=`. Compare **before** (master) and **after** (this branch):
   the after-rig should show `battle` ≥ 3 of 4 pilots in the opening anchor (Alderan) during the opening, and the pilots
   riding the strike/finale anchor once bomb runs start; `home` should stay ≈ 0 except station damage.
3. `grep '\[support\] .* hull=' | grep -o 'sector=[0-9]*' | sort | uniq -c` — target lines by sector; the base rig showed
   ~75% of picks at home; after the fix the home share should collapse to genuine-damage repairs only
   (`hull=` < 0.85 on every home line, and no `target=` that is another support pilot at home).
4. The `SC2SupportPhaseLiveTests` gate encodes the same checks for loopback (fails on master with 0.0%).

## 6. Verification

- Build: whole solution 0/0 and `tools/build-tools.sh` — see §6.1.
- Unit tests (pure helper, `SC2NaniteSupportTests` / `BotFixRegressionTests`): phase precedence + destination (same
  inputs, different phase → different destination; fog fallback), hold hysteresis (full/stale/stuck/leash drop rules and
  precedence), pick bars (home 0.85 bar, support-pilot exclusion, cooldown, battle-sector < 1.0), P778a ordering
  unchanged with the new parameters, `ApplyRepairHold` full-drop + cooldown integration. 3× alone: 5/5, 5/5, 5/5.
- Live gate: `SC2SupportPhaseLiveTests` — fails on the unfixed code (measured: 0.0% in battle sector, base run);
  fixed-run result and the showcase never-parks 5× in §6.2.
- Fast tier: `tools/run-tests.sh --tier fast` — §6.3.

### 6.1 Builds

(to be filled by the verification run)

### 6.2 Live results

(to be filled by the verification run)

### 6.3 Fast tier

(to be filled by the verification run)

## 7. Unfinished / next tasks

- **P792 (next task, per brief):** the phase switch is one global timer — `ShowcaseFinale.Settings.AfterMinutes`
  (`ShowcaseFinale.cs:11`, applied in `TriggerSides`, `ShowcaseFinale.cs:35-48`) — not civ-dependent. The support driver
  reads whatever phase exists; when phases become civ-driven, no support-side change should be needed.
- The strike-wave phase signal (`ResearchTactics.StrikeWaveActive`) only exists in research-mode matches; in unlocked
  showcase matches the bomb-runs phase is the finale. If unlocked matches later gain pre-finale station attacks, the
  accessor needs that signal too.
- Station observations (`StationLive`) carry no timestamp, so the stale-snapshot rule cannot apply to station repairs;
  the leash and no-improve rules cover them. If a rig run shows station-repair holds sticking, give `StationLiveState`
  an arrival stamp (Core change — out of scope here).
- Rig (221) numbers pending Claude's next batch (§5 recipe).
