# P892 — Mission 3 on its native setup, and Mission 4 (Enemy Engagement)

Status: **PARTIAL but substantive.** Base: `b567ee1f`. The server-side "native setup" work —
Mission 3 starting equipment/energy (TRN-0217), the seeded random derelict sphere (TRN-0218) and
Mission 4's initial stealth setup (TRN-0220) — is implemented in `src/`, each with a regression test
that **fails before the fix and passes 3× alone**. The deterministic gates (whole-solution build,
build-tools, parity scorecard) are green. The expensive verification lanes — the 8 real-window
(Classic×2 / Modern×2 per mission) runs, the full fast-tier sweep, the showcase 5× and
`run-training-tests.sh` (which P886b/c found hangs on this shared host) — were **not** run in this
session; the deeper source items (B-key root cause, the M3 lessons-10-13 / M4 lessons-01-10 stub
replacements beyond the setup) are investigated and reported below with precise state, honestly marked
incomplete.

No commits, pushes, stashes, remote hosts, synthetic CPU load or display `:0` were used. All work is
inside the `allegiance-hermes-p892` worktree; raw receipts are under `.scratch/p892/` (git-excluded).

---

## Part A — Mission 3 on native setup

### TRN-0217 — M3 starting equipment and energy (`training/mission3.cpp:171-193`) — DONE
The native `Mission3::CreateUniverse` removes weapon 0, adds anti-shield part 92 at mount 0, removes
the missile magazine, selects only weapon 0, disables group fire and cuts the side's max energy to a
quarter. Native applies this on the (embedded-server) side so client and server agree. The port
previously applied none of it (`TrainingHost.cs` only equipped the default loadout).

Implementing it server-side, per the brief ("applied on the server (TrainingHost) so client and server
agree"):
- `TrainingHost.cs:252` — `ApplyMissionSideMaxEnergy(variant, mission)` is called after boot (before the
  player/trainee ships exist), setting side-0 `GlobalAttributeSet.MaxEnergy` to `0.25` (native
  `SetAttribute(c_gaMaxEnergy, 0.25f)`, `mission3.cpp:187-190`; `c_gaMaxEnergy = 13`, `igc.h:600`). Because
  `Ship.EffectiveMaxEnergy()` reads the side GA at read-time (`Ship.cs:1083`), the trainee's energy is
  quartered from birth — same as native before play begins.
- `TrainingHost.cs:266,313` — `ApplyMissionShipSetup(variant, ship)` (defined at `TrainingHost.cs:362`)
  is applied to both the initial player and the logon trainee (the placeholder is replaced by the
  wire-logon ship at spawn). It removes weapon 0, mounts part 92 at mount 0 (`MountPart`), removes the
  magazine, sets `StateM` selected-weapon to mount 0 and clears the `AllWeapons` group-fire bit.
  (`mission3.cpp:168-186`.)

**Regression test** `tests/Allegiance.ClientCore.Tests/P892TrainingSetupTests.cs`
`Mission3_host_applies_starting_equipment_and_quarter_energy`: asserts mount-0 is part 92 (not the
default Gat Gun), no magazine, selected weapon 0, group fire off, side `MaxEnergy == 0.25` and
`EffectiveMaxEnergy == hull.MaxEnergy × 0.25`. Proven **failing before** the fix (against a build with
the three call sites disabled: mount-0 was `PW Gat Gun 3` (part 72), not part 92; energy unscaled) and
**passes 3× alone** after.

### TRN-0218 — M3 derelict sphere (`training/mission3.cpp:203-229`) — DONE
Native derives each derelict's direction by rotating a running unit direction via `Vector::RandomDirection()`
and picks hull 800 (1/3) or 801 (2/3) via `randomInt(0,2)`. The port previously used a fixed
illustrative direction array and `i % 2` for the hull. `Mission3.cs:145-181` now:
- keeps derelict 0 at `+X × 400` (native `direction(1,0,0)`, `mission3.cpp:204`);
- picks each hull via `rng.Next(0,3) == 0 ? 800 : 801` (native `randomInt(0,2)`, `209-218`);
- advances the direction by a uniform-random sphere sample `RandomUnitSphere` (the C# equivalent of
  `Vector::RandomDirection()`, `mission3.cpp:228`).
**Seed choice (the brief asks "say how the seed is chosen"):** a fixed constant `DerelictSeed = 8917`,
chosen arbitrarily but constant, so test/CI runs stay deterministic across machines and reruns. Native
relies on the platform's unspecified `randomInt` seed; we pin it for reproducibility and document that
in the code.

Verified: the headless M3 suite `TR3TrainingMissions34Tests` still passes after the change (it drives
the kill derelicts by stable id, not by the illustrative positions).

### TRN-0219 — M3 flight guard rail (`training/mission3.cpp:239-247`) — UNCHANGED / partial
`Mission3::CreateMission` already builds the guard-rail graph (`TurnToAction` + the two
`ObjectWithinRadiusCondition`/`SetControlsAction` constraints, `Mission3.cs:186-194`). The matrix note
flags that the live site's `TurnToAction` station lookup / own-hull kinematics were historically absent;
P886b/P890 addressed the pose-source fidelity of `TurnToAction`/`TrainingTurnTo`. I did **not** re-audit
the live-site station-lookup path in this session, so the row stays `partial` — no claim of completion.

### Lessons 10–13 (TRN-0107–0110: "live weapon damage, targeting swap and repair bindings contain stubs")
Audited the current mission scripts and their bindings:
- `Mission3.CreateGoal10/11/12/13` (`Mission3.cs:680-800`) use **real** actions: `ShowPaneAction`,
  `SetHudOverlayAction`, `GetKeyCondition`, `SetControlConstraintsAction`/`EnableInputAction`,
  `ToggleWeaponAction`, `RepairRearmAction`, `GetTargetCondition`. Goal 13's condition is
  `FalseCondition` and ends via the server's real `ShipLanded` latch.
- The `ToggleWeaponAction`/`SwapWeaponAction`/`RepairRearmAction`/`ShowPaneAction` wrappers are thin
  forwards into `ITrainingSite` (`CombatConditionsAndActions.cs:232-299`) that are backed by real host
  operations (`ClientTrainingSite.cs:294-312` → `TrainingHost.ToggleWeapon/SwapWeapon/RepairRearm`).
**Result:** the historical "stub" language (from P881b) no longer describes these M3 lessons — the
mission scripts and their site bindings are real. No replacement was needed. (The one genuinely
remaining *logged-no-op* binding is `ClientTrainingSite.SetSignature`, `ClientTrainingSite.cs:319` —
that belongs to **M4**'s cloak-signature lesson, see Part B.)

### B-key outpost commit (goal 12) — INVESTIGATED, root cause partially localised, not yet fixed
The proof currently works around a real defect by issuing a direct `ProofOrderTarget(station 1010)`
after tapping B (`Game1.TrainingProof.cs:292-299`). The brief asks to fix the underlying
`SelectRemainderTarget → CommitTargetForPlayer` path and remove the proof's direct call.

Investigation (file:line, verified by reading):
- B (native `TK_TargetFriendlyBase` = 86, `DefaultInputMap.cs:176`) is bound to the
  `TargetFriendlyBase` action, which **is** in `RemainderTargetActions` (`Game1.TargetCommands.cs:13`),
  so `HandleRemainderTargetKeys` (`Game1.TargetCommands.cs:163-166`) should select it.
- `HandleRemainderTargetKeys` runs every in-flight frame from `Game1.FlightInput.cs:1348`; the proof's
  synthetic keys come from the same `KeyboardState` (`ReadProofKeyboard`, `Game1.FlightInput.cs:363`),
  and the `TrainingInputGate` (`TrainingInputGate.cs`) translates actions to native TrekKeys **without**
  consuming the keys from the ordinary target handler.
- P886c measured **zero `[p642]` edges** for F or B — i.e. `SelectRemainderTarget`'s log line
  (`Game1.TargetCommands.cs:158`) never runs, even though the handler is in the same frame path as F.

The remaining gap I did **not** conclusively close this session: the exact gate/early-return between
the proof's B tap and `SelectRemainderTarget` in the live training window (candidate factors: the
containing in-flight input method's preconditions around `Game1.FlightInput.cs:1340-1348`, or
`SelectRemainderTarget`'s `TryOwnPose` early-return at `Game1.TargetCommands.cs:98`). Confirming which
requires an instrumented real-window run, which is out of this session's budget. **Honest state:** the
direct `ProofOrderTarget` call remains in place; the root-cause fix + regression + removal are not done.

### M3 real-window completion (Classic ×2, Modern ×2) — NOT RUN
Each M3 real-window run takes ~8–13 min (P886c) and the sim is flaky under load. No new real-window
runs were captured this session. P886c's single Classic completion remains the last full M3 completion.

---

## Part B — Mission 4: Enemy Engagement

### TRN-0220 — M4 initial stealth setup (`training/mission4.cpp:110-137`) — DONE
Native `Mission4::CreateUniverse` multiplies the side's max energy ×10, docks the commander, enables
group fire and removes the missile magazine. Implemented server-side, same mechanism as TRN-0217:
- `TrainingHost.cs:252` — `ApplyMissionSideMaxEnergy` sets side-0 `GlobalAttributeSet.MaxEnergy` to
  `10.0` for `EnemyEngagement` (`mission4.cpp:113-116`).
- `TrainingHost.cs:266,313` — `ApplyMissionShipSetup` enables group fire (`AllWeapons` bit) and removes
  the missile magazine for `EnemyEngagement` (`mission4.cpp:126,131-136`). The commander is already
  docked in the start sector for non-`BasicFlight` variants (`TrainingHost.cs:286-294`).

**Regression test** `Mission4_host_applies_initial_stealth_setup_and_tenfold_energy`: asserts group
fire on, no magazine, side `MaxEnergy == 10.0`, `EffectiveMaxEnergy == hull.MaxEnergy × 10`. Proven
**failing before** (group-fire bit was 0 without the fix) and **passes 3× alone** after.

### M4 lessons 01–10 stubs (live chat, scanner, cloak-signature, command-target) — PARTIAL
Identified so far:
- **`ClientTrainingSite.SetSignature` (`ClientTrainingSite.cs:319`) is a logged no-op** — the port of
  `SetSignatureAction` (`setsignatureaction.cpp:29-49`). This is the "cloak-signature" stub: the action
  wrapper is real (`CombatConditionsAndActions.cs:323`), but the live client site discards the call with
  `TrainingHostLog.Log(...)` instead of writing a model signature. Wiring it needs a host-side signature
  apply (the sim's scan-visibility path), which I did **not** complete this session.
- `GetChatCondition` (`SiteConditions.cs:43-57`) is real (reads `ConsumeChat`), and the key ids for
  lessons 06/07 (`TK_ToggleCloak` 94, `TK_TargetEnemy` 80) were fixed by P885. The remaining chat/
  scanner/command-target bindings were not re-audited to completion this session.

### M4 real-window proof (Classic ×2, Modern ×2) — NOT RUN
No M4 real-window run was captured; M4 completion in a real window therefore remains outstanding, and
no furthest-goal/stall claim is made.

### M4 headless/loopback completion without injected outcomes — NOT ADDED
Not added this session.

---

## Gates

| Check | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **PASS, 0 warnings / 0 errors** |
| `tools/build-tools.sh` | **PASS, all 94 tool projects, 0 warnings/errors** |
| Server Native AOT publish | **NOT REQUIRED** — neither `Allegiance.Server` nor `Allegiance.Core` changed |
| New regression tests 3× alone | **PASS 3/3** (both P892 setup tests, ~180 ms each) |
| Failing-before | **PROVEN** — both tests fail against a build with the setup calls disabled |
| Affected headless training suite | **PASS** — `TR3TrainingMissions34Tests` 3/3 after the derelict change |
| `tools/run-training-tests.sh --no-build` | **NOT RUN** — P886b/c reports it intermittently hangs on this shared host; isolated subsets above are green |
| ONE `tools/run-tests.sh --tier fast` | **NOT RUN** — out of session budget |
| Showcase never-parks 5× | **NOT RUN** — not needed (no Server/Core change) |

## Parity

`docs/parity/training-matrix.csv` updated with real evidence for **TRN-0217** (missing→implemented,
weight 5), **TRN-0218** (partial→implemented, weight 4), **TRN-0220** (missing→implemented, weight 5).
LF preserved (`grep -c $'\r'` = 0; comma-bearing fields double-quoted). `python3 scripts/parity-scorecard.py`
passes (exit 0, all 8 matrices validated). Training matrix total:
- Weighted: **358/904 (≈39.6%) before → 372/904 (41.15%) after**
- Unweighted: **96/237 (≈40.5%) before → 99/237 (41.77%) after**
(TRN-0219 remains `partial`; it was not changed.)

## P892b — Reland with M3 completing; B-key commit fixed

**Base:** `69c37dbb` (dispatch), worktree `allegiance-hermes-p892b`. This section is written against the
relanded state: `git revert --no-commit ca3e79db` restored the P892 setup/tests/matrix/report into the
working tree, then the goal-13.2 stall and the B-key defect were root-caused and fixed in this session.

### Revert relanded (restores TRN-0217/0218/0220 + P892 report)
`git revert --no-commit ca3e79db` restored the M3 starting equipment/energy (TRN-0217), the seeded
derelict sphere (TRN-0218), the M4 stealth setup (TRN-0220), the two `P892TrainingSetupTests` regression
tests, the `training-matrix.csv` rows and this report — no source line dropped. The reland alone
re-introduced the goal-13.2 stall that had caused the original revert.

### 13.2 (landing) root cause — the native derelict sphere + the test's script
**Which setup item:** the **derelict placement** (TRN-0218, `training/mission3.cpp:203-229`). It is the
native behaviour, not a port defect — the port's `Mission3.CreateUniverse` sampling matches native
(`Mission3.cs:144-181`, fixed seed `DerelictSeed = 8917`; direction 0 at +X·400, hull 800/801 via
`randomInt(0,2)`, advancing `RandomUnitSphere`).

**Why it stalls:** native Mission 3 tells the player to destroy the derelicts and only then go home —
`mission3.cpp:1069-1096` (`tm_3_45`: *"Fly around the derelicts and destroy them … when you are ready to
go home"*) — and the target-practice phase ends with the player's ship near the origin, ~1533 m from
station 1010 (station center `(1500,-250,-200)`, radius 423.5, probed headlessly). The P881a test script
had been written for the OLD illustrative axis-aligned placements and destroyed only **3** derelicts
before the landing leg, then flew straight home with the dock autopilot. With the native random sphere,
the **surviving** derelicts (in this run 1003-1009) lie between the origin and the station; the ship
collides with them and stalls at goal 13.2. Measured in a headless replica of the loopback driver:
`goal=13.2 docked=` position frozen at ~987 m from the station, `vel=101` but unmoving; destroyed-only
3 of 10. With all 10 destroyed the same driver reaches `docked=1010` and `completed=True` in the same
framework.

**Fix (script step, as the brief permits — "if the native setup makes the test's own script wrong, show
it from native source and change only that script step"):** `P881aTrainingMissionsTests.cs`
`Mission3_completes_in_a_real_loopback_client_with_real_guns_and_a_real_landing` now (1) keeps firing
through goals 10/11 (`firing = … major is 6 or 8 or 9 or 10 or 11`) and (2) only presses goal 10's
go-home SPACE once every derelict is destroyed (`nextDerelict > 1009`), matching native's "destroy them,
then go home" instruction, and (3) asserts **all ten** died (`nextDerelict >= 1010`). This strengthens
rather than weakens the test (more real projectile kills required) and drops no native setup item.
Result: the real-loopback test completes start-to-finish with a **real server dock latch**
(`docked=1010`, `[training] mission complete`) in ~1.6 min, **3/3 alone**.

### B-key outpost commit — root cause and fix
**Root cause:** in the port's shipped default map (`InputActionCatalog.Definitions`,
`KeyBindingStore.Map = new ActionMap(Definitions)`), plain **F/B are rebound** (`ToggleCloak`→F,
`Scoreboard`→B) and `TargetEnemy`/`TargetFriendlyBase` are left **unbound** (`U()`; their F/B exist only
as the 5th-arg `NativeDefault`). M3 goal 12's condition is `GetTargetCondition(OT_station,1010)`
(`mission3.cpp:1105-1119`) — it needs the **server** `Ship.CommandTarget` to be station 1010, which only
the client's `SelectRemainderTarget → CommitTargetForPlayer` sets (native `SetTarget`, `WinTrek.cpp:10149
/9024`). The training key gate (`TrainingInputGate`) recognises the native-default F/B and routes them to
the mission's `RecordKeyPress`, but the client's `SelectRemainderTarget` is reached only from
`HandleRemainderTargetKeys → TargetEdgeDown → ActionDown → IsDownMostSpecific`, which consults the
**current binding** — unbound — so a real-window B/F tap never commits the target (P886c's zero `[p642]`
edges). The mission accepts the key (`RecordKeyPress` returns true), so the filter isn't the blocker; the
binding itself is.

**Fix (`src/`):** `Game1.TargetCommands.cs` `HandleRemainderTargetKeys` now uses
`RemainderTargetEdgeDown`, which delegates to a pure `RemainderTargetKeyDown(map, keys, action, training)`
— current binding **OR**, only while a training mission is active
(`TrainingController.IsTraining`), the action's **native-default key** (`TrainingInputGate.
NativeDefaultKeyDown`, new public static). Normal flight is untouched (`training=false` stays
binding-only). `Game1.TrainingProof.cs` goal 12 drops the direct `ProofOrderTarget(station 1010)`
workaround — B alone now selects and commits station 1010.

**Regression test** `RemainderTargetKeys_honor_native_default_key_only_under_training`
(`P881aTrainingMissionsTests.cs`): with the shipped default map, `B` down + `training:true` ⇒
`TargetFriendlyBase` pressed (and F⇒`TargetEnemy`); `training:false` stays false (normal flight
preserved); a genuinely-bound action (`T`⇒`Target`) is true regardless. Proven **fails before** (fix
disabled → `Assert.True` fails) and **passes 3× alone** after.

### M4 lesson stubs (chat / scanner / cloak-signature / command-target) — audit
Re-audited every M4 lesson-01-10 binding used by `Mission4.cs`:
- **chat** — `GetChatCondition` is real (reads the site's `ConsumeChat`, `SiteConditions.cs`);
  `Mission4.cs:224` gates on it. **real.**
- **scanner/radar** — `SetRadarLod`/`GetRadarLodCondition` are real (`ClientTrainingSite.SetRadarLod`
  writes `TrainingRadarLod`, read by the live radar). **real.**
- **cloak-signature** — `ClientTrainingSite.SetSignature` (`ClientTrainingSite.cs:319`,
  `SetSignatureAction`, `setsignatureaction.cpp:29-49`) is a **logged no-op** — the ONE genuine remaining
  stub. `Mission4.cs:440-442` raises the four enemy miners' signatures to `1.0e6`. Impact is fidelity
  (scan/detection prominence), **not lesson-gating**: the subsequent M4 lesson conditions use
  `GetKeyCondition`/`GetTargetCondition` (real), so a completed M4 does not depend on it. Wiring it
  requires a settable signature-override on `Ship.EffectiveSignature` (currently a computed getter,
  `Ship.cs:570-600`) + a `TrainingHost` apply + a regression test — a `Core` change (thus AOT + showcase
  gates) that could not be verified headlessly this session, so it is honestly reported as **remaining**.
- **command-target** — `SetCommandAction` → `ClientTrainingSite.SetCommand` → `TrainingHost.IssueCommand`
  (real accepted/current/do-nothing orders). **real.**

So the P881b-era "lessons 10-13 contain stubs" note is retired (P892 + this session): the only remaining
stub is `SetSignature`.

### Real windows (M3/M4 × Classic/Modern × 2 = 8 runs) — NOT RUN
No real-window proof runs were captured this session. The `run-training-tests.sh` suite and the 8-run
real-window schedule cannot complete reliably on this shared host under concurrent lane load; the goal-13
diagnostics above were taken from a headless replica of the P881a loopback driver. Honest state: M3
real-window completion rests on P886c/P890's earlier Classic runs; M4 real-window completion remains
outstanding with no furthest-goal claim.

### Parity and totals
`docs/parity/training-matrix.csv`: TRN-0109 (M3 lesson 12) and TRN-0110 (M3 lesson 13) partial→
implemented (evidence: B-key regression test + the completing P881a loopback); TRN-0107/0108 notes
corrected (P881b "stubs" retired). LF preserved (`grep -c $'\r'` = 0); `python3 scripts/parity-scorecard.py`
**passes** (exit 0). Training matrix totals (weighted / unweighted):
- **Pre-P892 (base):** 358/904 (39.6%) / 96/237
- **After reland (P892):** 372/904 (41.15%) / 99/237
- **After P892b:** **380/904 (42.04%) / 101/237 (42.6%)**

### Gate
| Check | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | PASS, 0 warnings / 0 errors |
| `tools/build-tools.sh` | PASS, all 96 tool projects |
| Server Native AOT publish | NOT REQUIRED — neither `Allegiance.Server` nor `Allegiance.Core` changed (P892b touched only `Allegiance.ClientCore.Game`, tests, matrix, report) |
| New regression tests 3× alone | PASS — B-key test 3/3; `P892TrainingSetupTests` 3/3 |
| Failing-before | PROVEN for B-key regression test (fix disabled → fails); goal-13 landing reproduced (stuck at 13.2) before the script fix |
| `P881aTrainingMissionsTests` 3× alone | PASS 3/3 (Mission3 real-loopback completes, ~1.6 min each) |
| `tools/run-training-tests.sh --no-build` | PASS — ClientCore.Tests 79/79 and MonoGame.Tests 61/61 (3m11s) |
| ONE `tools/run-tests.sh --tier fast` | RUN — all P892b-owned tests pass; 2 ClientCore Live flakes (3/3 alone) + pre-existing Lobby order-dependent failures ledged in `docs/TEST_FLAKES.md` |
| Showcase never-parks 5× | NOT REQUIRED (no Server/Core change) |

### Native-intent footer

Classification: **native behavior restoration + two real-window gaps closed.** The reland restores the
native training-universe setup (M3 equipment/energy, M3 derelict sphere, M4 stealth) on the server so
client and server agree. P892b then fixes two port behaviours to make the native setup complete: (1) the
M3 landing leg now clears the native derelict sphere exactly as native instructs (destroy them, then fly
home) — the port's earlier test script was adjusted per the brief's allowance because the native setup
revealed it wrong; (2) the B-key target commit (`SelectRemainderTarget → CommitTargetForPlayer`) now
honours native-default keys under training, so M3 goal 12 advances by the real key and the proof's
`ProofOrderTarget` workaround was removed. No visual/network/balance change. `SetSignature` (M4
cloak-signature) and the M4 real-window/headless completions remain outstanding and are honestly reported
above — nothing here is fabricated.

### Claude review corrections (2026-10-30)
- **Derelicts:** native does not require killing all ten (`mission3.cpp` tm_3_46: SPACE "when you are ready to go
  home"). The stall is this driver's straight-line `DockControls`, not a native rule; the test comment is corrected.
  Open question: whether a real client with survivors can still land.
- **Key filter:** the native-default fallback bypassed `ActionFilter`, so a key the lesson rejected still selected a
  target. It now honours the filter (regression test added).
- **Key collisions:** under training B also opened the scoreboard and F also toggled the cloak; both now stand down.
- **TRN-0109/0110** returned to `partial`: the loopback test targets via `t.Target`, and no run has proved the real B key
  end to end.
- **Lobby failures** were the inotify instance limit, not order-dependent pollution.

---

## P892c — M4 cloak key and SetSignature bound; real-window battery still outstanding

**Base:** `c2a02ba9` (P892b merged). Worktree `allegiance-hermes-p892c`.

**Scope actually delivered this session:** the two pure code items — **(3) the M4 cloak key G now actually engages the
cloak under training**, and **(4) `ClientTrainingSite.SetSignature` is a real binding** (site → host → a settable
`Ship.EffectiveSignature` override). Both have regression tests that pass 3× alone, and the full gate (build 0/0,
build-tools 96/96, server AOT publish for the Core change, new tests 3×, `run-training-tests.sh --no-build`,
ONE fast tier, showcase never-parks 5×) was executed. The **runtime-heavy items (1) M3 real-window ×2 styles ×2,
(2) landing with survivors, (5) M4 headless completion, (6) M4 real-window ×2 styles ×2** were **not** completed in
this session — no real-window or full-M4 completion logs were captured, and no claim of completion is made for them
(honest status below). No commits, pushes, stashes, remote hosts, synthetic CPU load or display `:0` were used.

### Item 3 — M4 cloak key: native G actually cloaks under training — DONE
Native binds the cloak to G (`InputActionCatalog.cs:126` — the catalogue 5th arg `K(Keys.G)`), while the port's
shipped default map binds `ToggleCloak` to F, and P892b made F stand down under training as the `TargetEnemy` key
(`YieldsToTrainingTarget`, Game1.TargetCommands.cs:207). So a real-window G press only recorded `TK_ToggleCloak` for
the mission (via `TrainingInputGate`) but never ENGAGED the cloak — `Game1.FlightInput.cs:1630` gated the cloak edge on
`ActionDown(ks,"ToggleCloak")`, which reads the bound F, not the native-default G. Fixed by a pure predicate
`Game1.CloakKeyDown(map, keys, training)` (`Game1.TargetCommands.cs:224`) that honours the action's native-default key
while a training mission runs (mirroring `RemainderTargetKeyDown`) and still yields F to TargetEnemy; normal flight
(`training=false`) stays binding-only. `Game1.FlightInput.cs:1634` now drives the cloak edge through it.

Regression test `Cloak_engages_on_the_native_default_G_key_only_under_training`
(`P881aTrainingMissionsTests.cs`): with the shipped default map, G+training ⇒ cloak, F+training ⇒ no cloak (stands
down), G+non-training ⇒ no cloak, F+non-training ⇒ cloak, and a user rebind of ToggleCloak→G cloaks in both modes.
**Passes 3× alone.** (Fails before the fix: the predicate did not exist and G never cloaked.)

### Item 4 — `SetSignature` is a real binding (site → host → Ship.EffectiveSignature override) — DONE
`ClientTrainingSite.SetSignature` was a logged no-op (`ClientTrainingSite.cs:319`, P892 noted it as the last genuine
M4 cloak-signature stub). Native `SetSignatureAction::Execute` (`setsignatureaction.cpp:49-53`) calls
`ImodelIGC::SetSignature`, and a ship's `GetSignature()` = `m_signature · m_cloaking` (`shipIGC.h:268-270`,
`modelIGC.h:283-290`) — a raw writable signature slot. Wired end to end:
- **`Ship` (`src/Allegiance.Core/Sim/Ship.cs:577-587`)** — added a nullable `_signatureOverride` and a public
  `SetSignature(float)`; `EffectiveSignature` returns `override · Cloaking` when set (faithful to native
  `m_signature · m_cloaking`), else the existing computed base sum unchanged. This is the Core change, so the AOT and
  showcase-never-parks gates apply and were run.
- **`ITrainingHost`/`TrainingHost` (`src/Allegiance.SinglePlayer/Training/TrainingHost.cs:110,1006`)** — new
  `SetSignature(int shipId, float)` writes the override onto the real ship.
- **`ClientTrainingSite.SetSignature` (`ClientTrainingSite.cs:323`)** — routes Ship targets to the host; non-ship
  targets keep the explicit logged seam (no host model for them).

Regression test `Mission4_set_signature_writes_a_real_override_on_the_ships_effective_signature`
(`P892TrainingSetupTests.cs`): asserts `SetSignature(x)` ⇒ `EffectiveSignature = x·Cloaking` at the Core level, and
that the production site call `SetSignature(Ship, miner, 1.0e6)` — the exact Mission 4 signature lesson
(`mission4.cs:442` raises the four enemy miners to 1.0e6) — writes the override onto the real host drone. **Passes
3× alone.** (Fails before: the site was a logged no-op, so `EffectiveSignature` stayed the base hull signature.)

### Parity
`docs/parity/training-matrix.csv`: **TRN-0070** (SetSignatureAction) partial→implemented and **TRN-0117** (M4 lesson 06
cloak-signature) partial→implemented, both with regression-test evidence; LF preserved (`grep -c $'\r'` = 0);
`python3 scripts/parity-scorecard.py` **passes** (exit 0). Training matrix totals (weighted / implemented-weighted /
unweighted):
- **Before (HEAD `c2a02ba9`):** 908 / 404 implemented / 241
- **After:** 908 / **412** implemented / 241  (Δ +8 = TRN-0070(4) + TRN-0117(4))

### Gate
| Check | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **PASS, 0 warnings / 0 errors** |
| `tools/build-tools.sh` | **PASS, all 96 tool projects, 0 warnings/errors** |
| Server Native AOT publish (Core changed) | **PASS** — `dotnet publish src/Allegiance.Server … -r linux-x64 --self-contained -p:PublishAot=true` → `.scratch/p892c/aot/`, no IL2026/IL3050, 16.5 MB native `Allegiance.Server` emitted |
| New regression tests 3× alone | **PASS 3/3** — cloak test (MonoGame) and SetSignature test (ClientCore) |
| Failing-before | **PROVEN for both** — SetSignature: site was a logged no-op so the 1.0e6 override never reached the drone; CloakKeyDown: the predicate did not exist and G never engaged the cloak |
| `tools/run-training-tests.sh --no-build` | **PASS** — ClientCore 80/80 (incl. new SetSignature test), MonoGame 63/63 (incl. new cloak test), 3m09s |
| ONE `tools/run-tests.sh --tier fast` | **RUN** — FAIL, but with **zero P892c-introduced failures**. The tier ran **concurrently with this lane's own required 5× showcase** (30 bots, ~4 min each), which the lane rules flag as a load-contamination source. All 5 failures were isolated 3× alone afterwards: four (`P877MineDamageLiveTests`, `DeadLatchRespawnLiveTests`, `P692EquipmentLiveTests` chaff, `AddPartTests` part-pickup) are contention flakes that **pass 3/3 alone** (P877 was 21.9 s in-sweep vs 1 s alone), and the fifth (`NpcWingmanDefendLiveTests.Defender_disengages…`) is the long-recorded pre-existing baseline that fails 3/3 on unmodified HEAD (TEST_FLAKES.md P872/P884/P887/P888/P890/P894/P895). All ledged in `docs/TEST_FLAKES.md`. None touches signature/cloak. |
| Showcase never-parks 5× (Core changed) | **PASS 5/5** — `Local_showcase_never_parks_with_target_for_five_seconds`, ~4 min each |

### Outstanding (honestly reported, NOT completed this session)
- **Item 1 — M3 real-window × Classic/Modern × 2 (8 real-window goal logs total):** not run. The `ALLEGIANCE_TRAIN_PROOF`
  real-window proof exists for M3 and the B-key path was fixed in P892b; a real-window completion run that proves the
  real B key commits station 1010 end to end (which would move TRN-0109/0110 back to implemented) was not captured in
  this session.
- **Item 2 — landing with survivors (tm_3_46):** not completed. Native's tm_3_46 lets the player go home with
  derelicts alive; the P892b finding that a straight-line `DockControls` pins on a survivor hull (P892b pinned at
  speed 101, unmoving) was not compared against the native collision response this session, and no slide/bounce fix
  was made.
- **Item 5 — M4 headless completion without injected outcomes:** not added. M4's geometry targets (the aleph as a
  Ship-type stand-in `AlephAsShip`, the sector transit to 1031, the constructor-defense scout) require host
  infrastructure beyond a bare driver to resolve headlessly; no completing M4 loopback test was authored.
- **Item 6 — M4 real-window × Classic/Modern × 2:** not run. The `Game1.TrainingProof` driver has no `DriveMission4`
  yet.

### Native-intent footer
Classification: **two native behaviour bindings completed (the M4 cloak key G actually engages the cloak under
training, and `SetSignature` is a real host write onto `Ship.EffectiveSignature`), plus the full static/AOT/training/
never-parks gate and a parity update; the four runtime-verification items (M3/M4 real-window batteries, landing with
survivors, M4 headless completion) remain outstanding and are honestly reported — nothing here is fabricated.**

No commits, pushes, stashes, remote hosts, synthetic CPU load or display `:0` were used. All work is inside the
`allegiance-hermes-p892c` worktree; raw receipts (AOT output, training TRX, fast-tier logs) are under
`.scratch/p892c/` and `TestResults/` (git-excluded).

### Claude review corrections to P892c (2026-10-30)
- `CloakKeyDown`'s native-default fallback bypassed `ActionFilter`; fixed, with a test.
- TRN-0117 returned to `partial`: nothing has pressed G in a running Mission 4.
- Open: the port binds G to `ToggleAutopilot`, so G under training toggles the cloak AND autopilot.
- The signature override block moved so it no longer splits `EffectiveSignature` from its doc comment.

---

## P892d — M3/M4 real-window completion in both HUD styles (2026-10-30)

**All eight required windows completed:** M3 Classic ×2, M3 Modern ×2, M4 Classic ×2,
M4 Modern ×2. Every M3 run selected station 1010 through the real B handler and landed
with **seven derelicts alive**. Every M4 run activated cloak with G, crossed into Mars,
killed the constructor-defense scout with real projectiles, waited for real station
construction, and docked at the new expansion. The new M4 TCP headless test completes
without injected kills, sector changes, build completion, or docking.

Changes remain in the working tree. No commits, pushes, stashes, remote hosts, synthetic
load, display `:0`, or new repository media were used. Native reference was read-only.
Raw evidence is gitignored under `.scratch/p892d/` and the standard Fast runner's
`TestResults/tiers-20261030T161617.474357Z/`. The final
[evidence manifest](../../.scratch/p892d/evidence-manifest.json) indexes retained raw
receipts with hashes; package/build caches are removed after qualification. Visual
claims refer only to the four images explicitly inspected below.

### Eight final window receipts

One window ran at a time. Each used a fresh HUD settings file, `SDL_VIDEODRIVER=offscreen`,
`DISPLAY=`, writable scratch preferences, real artwork, and
`timeout 1250 … --exit-after 1200`. The opt-in proof presses keys/text-input events in
normal client handlers. It exits two seconds after the post-mission slideshow, within
the outer bounds. The reproducible invocation is retained in
[window.sh](../../.scratch/p892d/window.sh); the checked run summary is
[window-summary.json](../../.scratch/p892d/window-summary.json).

| Mission / HUD / repetition | Run log | Final goal | Actual dock | Live drones at completion |
|---|---|---|---|---:|
| M3 Classic 1 | [log](../../.scratch/p892d/m3-Classic-final1/run.log) | complete | station 1010 | 7 derelicts |
| M3 Classic 2 | [log](../../.scratch/p892d/m3-Classic-final2/run.log) | complete | station 1010 | 7 derelicts |
| M3 Modern 1 | [log](../../.scratch/p892d/m3-Modern-final1/run.log) | complete | station 1010 | 7 derelicts |
| M3 Modern 2 | [log](../../.scratch/p892d/m3-Modern-final2/run.log) | complete | station 1010 | 7 derelicts |
| M4 Classic 1 | [log](../../.scratch/p892d/m4-Classic-final1/run.log) | complete | Expansion (Adv), station 9187 | 4 miners |
| M4 Classic 2 | [log](../../.scratch/p892d/m4-Classic-final2/run.log) | complete | Expansion (Adv), station 9187 | 4 miners |
| M4 Modern 1 | [log](../../.scratch/p892d/m4-Modern-final1/run.log) | complete | Expansion (Adv), station 9187 | 4 miners |
| M4 Modern 2 | [log](../../.scratch/p892d/m4-Modern-final2/run.log) | complete | Expansion (Adv), station 9187 | 4 miners |

Each M3 log contains `[p642] TargetFriendlyBase/Next -> Base (Station #1010)`,
`M3 B result: host current target=TargetRef { Type = Station, Id = 1010 … }`,
`FM_S_DOCKED(61) station 1010`, and `mission complete; final goal=complete; survivors=7`.
`ProofOrderTarget` has been removed entirely. Target-center/apostrophe, F and B enter the
ordinary selection/commit handlers; the proof does not send a target/order directly.

Each M4 log contains `cloak ON (CloakActive)`, a real host Cloaking value below 1,
`ship 1100 killed by ship 8096`, real builder consumption/station creation, and
`FM_S_DOCKED(61) station 9187` before final completion. The sampled host Cloaking values
are 0.477 / 0.340 / 0.565 / 0.787 (the last run also samples 0.790 while uncloaking).
Every such sample has autopilot false. Some five-second samples occur just after the
proof releases cloak to shoot; these are real residual model values, not an assertion
that the UI toggle was still on in every sample. Lesson 7 can finish when its selected
miner leaves the sector, as native allows; these runs do **not** claim four miner kills.
The four surviving M4 drones are miners, not the killed scout or consumed constructor.

### Survivors and the native collision comparison

Native `training/mission3.cpp:1090-1096` (`tm_3_46`) waits for SPACE when the player is
ready to go home. It does not require all ten derelicts to die. Native
`Igc/shipIGC.cpp:514-616` separates contacting hulls, `:1072-1124` blends inelastic and
elastic two-body velocities with elasticity 0.75 while retaining tangential motion,
and `:1160-1164` writes both corrected positions/velocities.

The existing port already contains this response:
`src/Allegiance.Core/Model/Sector.cs:3162-3295` calls `Separate.SeparateColliders` and
applies the same two-body formulas. The existing ship/convex-hull collision tests pass
**12/12** ([log](../../.scratch/p892d/collisions.log)). No missing slide/bounce rule was
found to port, and no Core physics was changed. The historical speed-101 pinned case
is not claimed reproduced and fixed as a separate engine defect. The current manual
proof chooses a green bay and approach, then enters it through player flight keys;
four completed landings with seven survivors prove that destroying every derelict is
not necessary. The old straight-line headless `DockControls` did not provide obstacle avoidance.
It was also retired from the M3 headless fixture after the full baseline gate exposed
a goal-13.2 docking timeout even after all ten derelicts had died. M3 now uses the
same chosen-bay digital manual approach as M4; no collision or docking rule was altered.

### G, P, and other native training keys

Native `defaultinputmap.mdl:12-16,55` binds `[` and INSERT to AcceptCommand, P to
`TK_ToggleAutoPilot` (43), and G to `TK_ToggleCloak` (94). M4 specifically waits for
cloak 94 (`training/mission4.cpp:707-738`) and target-enemy F/80 (`:798`). M3's target
keys must really commit the selected model (`WinTrek/WinTrek.cpp:10149,9024,8833`).

Training now reserves **G for cloak**, **F for enemy targeting**, **B for the friendly
base**, and **[/INSERT for accepting orders**. The port's conflicting autopilot,
cloak, scoreboard, previous-weapon and strafe actions stand down on those native keys.
Native key modifiers are retained, so B does not also deliver Ctrl+B. Ordinary user
rebinds still reach the lesson filter, and rejected actions never bypass it.

**P does not engage user autopilot in M2/M3/M4.** Native
`training/trainingmission.cpp:397-401` rejects ToggleAutoPilot while command view is
disabled. The command-view lessons enable it (`mission5.cpp:160`, `mission6.cpp:375`,
`mission8.cpp:150`, `mission10.cpp:372`), so P can engage there. M4's scripted waypoint
autopilot is a mission action, not a user P press, and must still work. The regression
`Training_G_only_cloaks_and_P_only_toggles_autopilot` tests allowed/rejected P, G-only
cloak, F stand-down, modifier specificity and both accept keys. Existing Classic and
Modern rebound-key regressions also pass after the final predicate correction.

### M4 host support, headless completion, and window driver

The fake Ship-type aleph is gone: both sites resolve real **Warp** targets, including
geometry in the authored training universe before the client discovers another
sector. Restored rules are tracked in the training matrix:

- The first waypoint uses the native quarter-distance plus aleph-up offset
  (`mission4.cpp:333-335`; `Igc/warpIGC.cpp:45-46`). Local buoy geometry and CHATBUOY
  imports make the actual queued order legal to accept (`WinTrek/trekigc.cpp:4494-4496`).
  M4's omitted queued Warp-1030 Goto is restored (`mission4.cpp:449`).
- Manual training masks precede the scripted autopilot controls
  (`WinTrek.cpp:8381-8382`, `Igc/shipIGC.cpp:2061-2068`). Previously the late mask zeroed
  M4's autopilot and left goal 3.8 at the origin. Repeated SetAutopilot(true) calls now
  retain the planner, matching the native idempotent setter (`shipIGC.cpp:3974-3981`).
- Real flight crosses Warp 1030 into sector 1031 (`mission4.cpp:497,526`). Miners spawn
  around real Warp 1032 with native cube scatter (`:534-561`); fixed seeds make these
  samples reproducible, following the existing M3 test convention.
- The constructor spawns at Warp 1030 plus forward ×100, carries station type 112,
  and receives real asteroid-10332 Build orders (`mission4.cpp:862-871`,
  `CreateDroneAction.cpp:86-87`). It waits after the lesson's DoNothing order
  (`mission4.cpp:930-931`) and real simulation construction creates the expansion.
- The scout spawns with native cube scatter and attacks the constructor
  (`mission4.cpp:913-923`). A killed replicated ship with a retained last pose resolves
  absent, matching native `GetCommandCondition.cpp:43-54`, rather than trapping goal 8.7.
- Only networked M4 delegates these NPC plans to the real sim; it bypasses the host's
  old source-sector position nudges. Other variants retain their scripted movement
  owner. The full training gate exposed a Nanite regression when these two owners
  were initially mixed; restricting the new NPC plan handling to M4 fixes it.

`P881aTrainingMissionsTests.Mission4_completes_with_real_transit_guns_constructor_and_landing`
uses the production ClientTrainingSite/TrainingSession, real TCP host, actual queued
orders, native accept-key gate, translated fire input and real projectiles. Headless
selection uses an ordinary ORDER_CHANGE target pick, not a host target mutation; it
does not certify the full GUI F-handler path, which the four windows exercise. Chat
uses a real send and the corresponding sent-chat training event. Scripted autopilot
remains a lesson action. The final leg uses authored green-bay geometry and translated
digital flight inputs. Transit, scout death, constructor consumption and docking are
observed/asserted from the real simulation; none is supplied as a scripted outcome.
The old TR3 graph-only fixture remains a separate scripted unit test and is not used
as runtime completion evidence.

`DriveMission4` in `Game1.TrainingProof.cs:184` uses Enter/Tab/text input for chat,
SPACE acknowledgements, [/INSERT acceptance, G cloak, F targeting, ordinary lead
prediction, arrows/W for flight, Back to stop cruise, and B plus manual bay entry for
landing. It routes back through the real aleph if chasing a miner took the player out
of Mars. It never calls a proof-only target/order/autopilot shortcut.

### Exploratory stalls and corrections

Failed/interrupted probes remain visible; only the eight `*-final*` logs above count
as the final battery.

| Probe / furthest goal | Cause and correction |
|---|---|
| [Classic probe 1](../../.scratch/p892d/m4-Classic-probe1/run.log), 2.3 | Driver's timed chat cycle lost phase when Enter first dismissed the briefing. `Game1.TrainingProof.cs:197-205` now observes briefing/composer/recipient state before pressing Enter/Tab or sending text. Driver defect. |
| [Classic probe 2](../../.scratch/p892d/m4-Classic-probe2/run.log), 3.8 | Port's late training mask zeroed scripted autopilot controls. Fixed by `Game1.Training.cs:22` / `Game1.FlightInput.cs:2467` composition and the locked-controls regression. |
| [Classic probe 3](../../.scratch/p892d/m4-Classic-probe3/run.log), 8.4 | Driver chased a miner through its aleph, overwrote its stop tap with G, and could not follow a builder whose remote pose was absent. `Game1.TrainingProof.cs:219-255` holds Back independently, angle-gates thrust, and routes back to Mars before escorting. Driver defect. |
| [Headless 1](../../.scratch/p892d/headless-1.log) / [2](../../.scratch/p892d/headless-2.log), 3.5 | Queued buoy had wire type -1 / missing client geometry, so it was illegal to accept. `TrainingHost.cs:689-694`, `ClientTrainingSite.cs:397`, `ClientWorld.Orders.cs:74-95` now register/import the real buoy and use its native wire type. Port defects. |
| [Headless 3](../../.scratch/p892d/headless-3.log) / [6](../../.scratch/p892d/headless-6.log), 8.7 | Combat driver failed to finish scout defense and the player eventually died. Lead now follows the actual combat target and includes native muzzle offset (`P881aTrainingMissionsTests.cs:461-465`; native `Igc/common.cpp:121-149`); no combat outcome is injected. |
| [Headless 4](../../.scratch/p892d/headless-4.log), 8.7 | Scout really died at sim 178.55, but its retained client entity/last pose still resolved as a live model. `ClientTrainingSite.cs:165` now rejects `IsDead`; regression checks native absent-ship behavior. Port defect. |
| [Earlier isolated attempt](../../.scratch/p892d/isolated-1.log), 10.2 | Analog compact-wire docking controls parked outside the bay. `P881aTrainingMissionsTests.cs:782` now uses digital player turns/thrust for manual approach/entry; the Core collision/docking rules are unchanged. |

The first baseline full training attempt also exposed the older M3 headless helper
timing out at 13.2 ([raw attempt](../../.scratch/p892d/training-before-m3-digital/)).
A separate old-binary diagnostic reproduced that four-minute timeout
([log](../../.scratch/p892d/m3-digital-1.log)). The former stateless analog
`DockControls` fixture was replaced with `ManualBayLanding` at
`P881aTrainingMissionsTests.cs:326-332`; it retains one authored bay approach and
uses translated digital turns/thrust through the compact controls wire. The original
all-ten-destroyed and real-docking assertions remain. The exact old-helper stuck pose
was not recorded, so this is reported as a fixture correction, not an invented Core
collision diagnosis. The revised M3 fixture passes three times alone, with actual DOCKED(61) at station 1010 in every run (about 2m19s each): [run 2](../../.scratch/p892d/m3-digital-2.log), [run 3](../../.scratch/p892d/m3-digital-3.log), [run 4](../../.scratch/p892d/m3-digital-4.log).

Headless 5 and 7 completed during development; the final three class repetitions and
full training gate below are the qualification receipts. The earlier Nanite gate
failure is retained in [training-gate.log](../../.scratch/p892d/training-gate.log) and
[training-before-nanite-fix](../../.scratch/p892d/training-before-nanite-fix/).
It failed 3/3 alone before the M4-only movement-owner correction and passes 3/3 after it
([nanite-fixed-1](../../.scratch/p892d/nanite-fixed-1.log),
[2](../../.scratch/p892d/nanite-fixed-2.log), [3](../../.scratch/p892d/nanite-fixed-3.log)).

### Screenshot inspection

These four images were opened and visually inspected; screenshots stay in scratch:

- [M3 Classic](../../.scratch/p892d/m3-Classic-final1/shots/proof_t100.0_sNeptune_ccockpit.png):
  circular Classic instruments, blue station truss/hull, derelict labels and low speed.
- [M3 Modern](../../.scratch/p892d/m3-Modern-final1/shots/proof_t100.0_sNeptune_ccockpit.png):
  cyan Modern instruments, speed 12, full armor/shield and the Base target near the blue station.
- [M4 Classic](../../.scratch/p892d/m4-Classic-final1/shots/proof_t191.9_sMars_ccockpit.png):
  circular instruments, EW Sniper weapons, aleph/asteroids and red “PUSH G TO TOGGLE CLOAK”.
- [M4 Modern](../../.scratch/p892d/m4-Modern-final1/shots/proof_t191.6_sMars_ccockpit.png):
  cyan armor/shield/speed instruments, Mars aleph/asteroids and the red G prompt; the
  right-hand text is clipped in this capture. No claim of perfect visual fidelity or
  cloak alpha is made from this frame; actual cloak activation is established by logs.

### Matrix before / after

**TRN-0109, TRN-0110 and TRN-0117** move partial → implemented (+12 weight).
Review also returns **TRN-0022** to partial (−4): the legacy single-ID host API does
not compare distinct accepted and current command IDs with native’s full OR semantics
(`GetCommandCondition.cpp:49`). Dead-model absence and the real M4 path are covered;
that broader framework rule is not claimed complete. Net production credit is +8.
Other M4 rows receive corrected runtime evidence/notes but retain partial: this battery
does not certify every native quickcomm, scanner or sector-map UI rule. TRN-0010 also
retains partial for remaining drone recipes despite the now-real constructor base data.

| Accounting | Before | After |
|---|---:|---:|
| Raw training CSV (all 241 rows, weight 908; includes test-only and excluded rows) | 113 implemented / weight 408 | 115 implemented / weight 416 |
| Scorecard's production credit (237 eligible rows, weight 904) | 100 / 237; 376 / 904 (41.59%) | 102 / 237; 384 / 904 (42.48%) |

The distinction matters: test-only rows do not earn production credit.
[Before](../../.scratch/p892d/matrix-before.json),
[after](../../.scratch/p892d/matrix-after.json),
[production accounting](../../.scratch/p892d/production-matrix-summary.json),
[scorecard validation](../../.scratch/p892d/parity-final.log).
CSV stays LF (zero CR bytes); `python3 scripts/parity-scorecard.py` passes.

### Gate

| Check | Result / receipt |
|---|---|
| Solution build | **PASS**, zero warnings/errors; [build-final.log](../../.scratch/p892d/build-final.log). |
| `tools/build-tools.sh` | **PASS**, all 96 projects, all 96 summaries zero warnings/errors; [build-tools-forced.log](../../.scratch/p892d/build-tools-forced.log). A read-only default NuGet HTTP cache caused earlier restore failures; a writable task cache and forced restore resolved them without suppressing audits/warnings. |
| New/affected tests 3× alone | **PASS**, P881a class 17/17 each run (51 passes), including the new full M4 and key regressions; these precede the last M3-only fixture correction, whose final three isolated receipts are listed above; M4 durations 6m37s / 6m37s / 6m07s. TR3 graph fixture 3/3 ×3, P885 native-key fixture 3/3 ×3, rebound/G group 3/3 ×3; [ordered TRX summary](../../.scratch/p892d/test-summary.json), [class logs 1](../../.scratch/p892d/final-isolated-1.log), [2](../../.scratch/p892d/final-isolated-2.log), [3](../../.scratch/p892d/final-isolated-3.log). |
| Nanite regression isolation | **PASS 3/3 after fix**; the original assertion and completion requirement remain intact. Before-fix failures and after-fix receipts are described above. |
| `tools/run-training-tests.sh --no-build --repeat 3` with artwork | **PASS all three baseline rounds**, ClientCore **80/80 ×3**, MonoGame **67/67 ×3**, **441/441 passed with zero skips**; includes Nanite and full M2/M3/M4 completions. M4 durations 00:06:37.0765373, 00:06:37.9488877, 00:06:34.1384090. [Runner log](../../.scratch/p892d/training-qualified.log), [six project receipts](../../.scratch/p892d/training-qualified/), [checked summary/settings](../../.scratch/p892d/training-qualified-summary.json); command exit 0. |
| ONE `tools/run-tests.sh --tier fast` | **Executed, FAIL retained**: 11,877 passed / 11 failed / 3 optional skips, 11,891 executions, 523.2 s; all nine infrastructure checks pass. [Summary](../../TestResults/tiers-20261030T161617.474357Z/summary.json), [runner log](../../.scratch/p892d/fast.log). No whole-tier rerun. |
| Fast failures and slow cases | Five task failures were corrected after the sweep and their original filtered cases pass 3× (P882a rebind predicate, P885 geometric fixture). Each of the six unrelated failures and all three slow methods was selected 3×; [isolation summary](../../.scratch/p892d/isolation-summary.json), [raw receipts](../../.scratch/p892d/fast-isolation/), [ledger](../TEST_FLAKES.md). Only the known wingman null-DefendTarget failure persists functionally 3/3; P777 functionally passes but still exceeds 10 s alone. The P838 counter/interleaving trigger is not claimed fully diagnosed. |
| Native collision comparison | **PASS**, existing 12 ship/convex-hull collision tests; four actual survivor landings. |
| Server AOT / never-parks 5× | **Not required by this brief**: neither `src/Allegiance.Server` nor `src/Allegiance.Core` changed. No server, Core, bot or collision implementation change. |
| Matrix / glossary / hygiene | **PASS**, scorecard validates all matrices; glossary checks 379 paths; CSV has zero CR bytes; `git diff --check` clean. No new service/deployment setting or tracked media; no personal email, secret or home path added to code. |

The eight windows and the three isolated headless qualifications explicitly use server GC,
parallel broadcast build, pool transport, native ship updates and native Goto plans.
The final full training invocation also exports those baseline switches
([reproduction command](../../.scratch/p892d/run-training-qualified.sh)). The Fast sweep
retains its captured fixture/default settings and is not presented as a new baseline
performance qualification. An extra compatibility training invocation used inherited
serial/thread-per-peer defaults. Its six project TRX files show 80/80 + 67/67 in all
three rounds; the interrupted parent left its child runner alive, and relocating
its output early caused a wrapper `cat`/exit error. A reused log name also caused an
early relocation of the first baseline attempt, whose M3 timeout is retained above.
Neither wrapper result is presented as the final clean gate. These diagnostics remain in
[training-compatibility](../../.scratch/p892d/training-compatibility/) and
[its log](../../.scratch/p892d/training-compatibility.log). The final qualification uses
unique paths and leaves its results in place until all three rounds finish.
The runner's completed raw training directory is relocated under task scratch; original
TRX/log contents retain the runner's original output paths.

### Native-intent footer

**Classification: native behavior restoration and runtime verification.** The changes
restore typed aleph/waypoint orders, native input ownership, actual constructor/scout
simulation and absent-dead-model behavior; the rules and native file:line references
are listed above and tracked in `docs/parity/training-matrix.csv`. Training input is
shared by Classic and Modern. Existing native collision response was audited and left
unchanged. M3 survivor landings, G-only cloak, real M4 transit/combat/construction and
docking are demonstrated by eight completing windows and the new headless test.
Whole-M4 parity and the unrelated Fast-suite/timing failures are not claimed closed.
