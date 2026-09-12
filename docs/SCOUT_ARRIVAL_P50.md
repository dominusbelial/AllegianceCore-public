# SCOUT_ARRIVAL_P50 — diagnose (and fix, where safe) ScoutExploreMap parking short of warps

Date: 2026-09-01. Source-reading diagnosis only — no VM, no live trial (same
constraint as P48; discipline: confirm precisely from source, don't guess,
report honestly). Managed C# edit to `ScoutExploreMap.cs` made but **unverified**
(no VM to build/run it) — flagged accordingly. Native `Allegiance` untouched
(**zero edits**, diagnosis-only per the standing rule).

## 0. Repos verified up front

- `AllegianceCore` (task's target for the report + any C# fix): branch `master`,
  top commit `62a7562` (≥ `a35d8ac`). `git status` before edits was clean.
- `Allegiance` (native, read-only): used only to read `src/Igc/common.cpp`,
  `src/Igc/shipIGC.cpp`. **No native edit made.**

## 1. The observed prior fact (from P42, not re-derived)

P42 (`Allegiance/TRANSPORT_P42.md`, §3) recorded, live on a real native
`Wopr.exe` client: a `ScoutExploreMap` autopilot GOTO toward warp 1051
("Haumea", **radius 100**) parked at `(261.8,-5034.6,1640.7)` vs the warp's
resolved center `(320.0,-5155.6,1613.3)` → **rest distance 137.0**, then sat
motionless (`delta1s=0.0`), `cmd=c_cidGoto` still active. I treat 137 as given.

## 2. Mechanism confirmed from source

**What command does the scout send?** Every warp navigation uses
`c_cidGoto` (`ScoutExploreMap.cs:598`, `:620`, `:652`, `:777`). On the client's
`sSetCommand`, `ResetWaypoint` converts a warp-target command with
`c_cidGoto` into waypoint objective **`c_oEnter`** (`shipIGC.cpp:4114-4117`).

**How does that waypoint navigate?** `GotoPlan::Execute` →
`GotoPlan::SetControls` calls `GetGotoPosition` (`common.cpp:1798`, def `:1830`)
on the `c_oEnter` warp waypoint. `Waypoint::GetGotoPosition` routes a non-`c_oGoto`
objective into a switch on object type; `OT_warp` has **no case of its own** and
falls through to `default:` → **`gpm = c_gpmFast; *pvectorGoto = itsPosition;`**
(`common.cpp:1556-1562`). So the autopilot simply flies flat-out at the warp
center: full throttle in `SetControls` (`common.cpp:2116` for `c_gpmEnter`;
for `c_gpmFast` the aligned-case at `:2233-2242` is *skipped* because the
condition is `(gpm & c_gpmFast)==0`, which is false, so throttle stays at
`+1.0`, `:2306` with `fThrottleMax` initialized `1.0`, `:2235`).

**There is NO arrival-radius formula on this path.** The `c_gpmFinished`
bit — the only thing that makes `SetControls` declare "arrived" and clear the
waypoints (`common.cpp:2021-2029`) — is only ever set in the `c_oGoto` branch
(`:1369`) and for asteroids (`:1426`), never for warps. The `c_oGoto`
threshold (`offset = radius + c_fOffsetFudge`, `common.cpp:1364`, with
`c_fOffsetFudge = 10.0f` at `:1150`) **does not run** for a warp. The
autopilot's only definition of "got there" is the engine's own warp-transition
flipping the cluster, at which point `m_maskWaypoints` is reset and `Execute`
returns true (`common.cpp:1844-1860`). So a warp-goto, unlike a plain point or
asteroid goto, has no built-in "stop at radius X" at all.

**What stops it, then?** The collision avoider. `c_gpmFast` (0x40) does **not**
carry the `c_gpmNoDodge` (0x10) bit (constants `common.cpp:1142,1145-1146`), and
`SetControls` calls `Dodge(...)` whenever `((gpm & c_gpmNoDodge)==0) && bDodge`
(`common.cpp:2001`) — true for a warp every frame (`bDodge` true from the
autopilot, e.g. `GotoPlan::Execute`'s caller). `Dodge` scans *pickable* models
including warps (not in `Ignore()`), predicts a collision with the warp and
thrusts the ship **away** from its center (`common.cpp:1711-1787`, esp.
`:1724-1745` for the collision predicate and `:1786` for the away-direction
thrust). Meanwhile the warp waypoint keeps commanding full throttle toward the
center. The two oppose, forming an equilibrium attractor at the combined
collision radius.

**Reconciliation with 137:** warp 100 + ship ⇒ rest 137 → ship radius ≈ 37,
with **no fudge factor needed** — this is not the `c_oGoto` `radius+10` formula
(that never runs for warps). Confidence in the *mechanism*: high (the full
force path is unambiguous in source — there is literally no other stopping
mechanism on the `c_oEnter` warp path besides `Dodge`). Confidence in the exact
37 from source alone: moderate — I could not independently extract the Adv
Scout hull radius from the compiled core binary (P42 §1 spawned an "Adv Scout"),
but the combined-radius prediction matching P42's measured 137.0 to the integer
strongly corroborates it.

**Confidence statement (honest): CONFIRMED.** The prior observation is
explained precisely: `ScoutExploreMap`'s warp GOTO is a `c_oEnter`/`c_gpmFast`
fly-to-center that has no native arrival-stop, and native anti-collision
`Dodge` pins it at warp+ship combined radius (137 = 100+37), just outside the
100-unit trigger. It is **not** an arrival-tolerance bug; the term
"arrival tolerance" was a misreading in P42. Root cause: bot commands the native
autopilot, which cannot be asked to cross a warp on its own — the only
counteracting force is its own safety reflex.

## 3. Conclusion & fix decision

- The defect is entirely native-**client-side** autopilot behavior, but the
  actionable fix is in managed C# (`ScoutExploreMap.cs`) — the WOPR bot layer —
  exactly where the task prefers it. No native edit warranted.
- Implemented (UNVERIFIED — see §5): a detector in the existing
  `MessageReceiver_FMD_S_SHIP_STATUS` handler (extends the existing "stopped"
  handling at `:128-139` rather than adding a new pathway): when the ship is
  effectively stopped (`|v| < 1.0`) **while** its current command is a warp
  GOTO and it sits in `(warp_radius, warp_radius + ship_radius]` from the warp
  center (parked just outside the trigger), re-affirm the GOTO with a 30s
  backoff. Diff is in `:128-162` (new block) and `:24-26` (new field
  `_lastWarpNudgeUtc`).

**Honest caveat that shapes §4's verification:** per §2, the binding force
holding the ship out is native `Dodge`, which re-arms instantly on every
re-issued GOTO — the two commands produce *identical* dynamics, so a pure
re-affirm may **not** escape the equilibrium. It is the minimal, task-prescribed
action and its logging alone proves the parked state is detected; but the VM
pass must actually measure whether it crosses. If it does not, the fix needs the
§4 "plan B" (approach-point perturbation), which is still C#-only.

## 4. What Claude Code's own VM follow-up should check

1. Confirm the new `P50:` log line actually fires in the parked state (ship
   stopped, `cmd=c_cidGoto`, distance between warpR and warpR+shipR).
2. Critical: observe **whether the ship then crosses** — server
   `[warp] ... crossed` line + client SECTOR CHANGE (the exact harness from P42
   §4/§6). Previously it parked at 137.0 and never crossed; the fix succeeds
   iff a previously-parking bot now actually produces a crossing within a
   reasonable time (~≤120s) and re-evaluates (`SelectNextAelphTarget`) in the
   new sector.
3. **No regressions:** real long-range navigation still works (position deltas,
   real `[ctrl]` frames), real sweep/random-target selection still engages, and
   no spurious nudges on GOTOs that aren't parked (a warp GOTO in transit must
   not trigger the block — the `|v|<1.0` gate plus the distance window guard
   against this; verify only near-stop near-warp states trip it).
4. **If it fails to cross (likely per §3):** plan B — on detection, don't re-aim
   radially; `CreateBuoy` a point offset ~45° from the ship-to-warp vector at
   just past the combined radius and `SetCommand` a GOTO to *that* buoy
   (`ScoutExploreMap.cs:267-270` / `:634-637` already show the buoy-GOTO idiom),
   so each retry approaches from a fresh tangent and the tangential momentum has
   a chance to carry the center inside warpR before `Dodge` redirects it.
   Measure with the same §4.2 criteria.

## 5. Explicit verification limits (required by the task)

- `ScoutExploreMap.cs` is a **managed C# WOPR client file**; no VM, so this edit
  is **unverified, diagnosis-quality code, not proven**. State: diff `:24-26`,
  `:128-162`.
- No native edit; `Allegiance` `git diff` should be empty. Nothing claimed as
  build-proven.
- `AllegianceCore` needs no change (confirmed: defect is client-side); only the
  report file is added here, no product code touched in `AllegianceCore`.

## 6. Git hygiene

`ScoutExploreMap.cs` lives in the native `Allegiance` tree (read-only repo). The
edit is committed **nowhere** — it is staged for Claude Code's VM pass to
evaluate, and per the task the WOPR/behavior report belongs in `AllegianceCore`,
whose tree I leave **clean** except this report + any staged file the operator
chooses. All changes are unverified until §4's live checks pass.

## 7. Session stats

```
20260901_222309_863616  start=22:23:18  dur=STILL RUNNING (at write time)  msgs=55  tool_calls=32  api_calls=23  tokens_in=1,331,696 tokens_out=25,199
```
(`python3 ~/Development/allegiance-tools/session_stats.py 20260901_222309_863616`)
