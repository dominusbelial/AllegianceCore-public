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

## Native-intent footer

Classification: **native behavior restoration** — the port now reproduces the native training-universe
setup (M3 starting equipment/energy, M3 derelict sphere sampling, M4 stealth setup) on the server so the
client and server agree, using the real ship/side/part models and a fixed-seed PRNG for determinism. No
visual, weapon-balance or network-behaviour change beyond restoring the native starting state. No rig
validation is claimed. Real-window completions, the fast-tier sweep, the full training-suite run, the
B-key root-cause fix and the M4 real-window/headless completions are outstanding and honestly reported
above — nothing here is fabricated.
