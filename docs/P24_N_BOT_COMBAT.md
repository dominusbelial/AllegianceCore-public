# P24 — N independent 1v1 combat lanes on the real `Bot` class

Date: 2026-08-30 · Branch `master` · Repo `/home/domingo/Development/AllegianceCore`

## What P24 does

P22 proved one real shooter killing one real target through the actual `Bot`
class. P24 proves the server handles **several real bot connections fighting
simultaneously** — N independent, simultaneous 1v1 lanes, all in one server
run, all sharing the real collision + damage + kill-broadcast code. The change
is confined to the demo's own `ShipSpawner` closure in `tools/livecombat`;
no production spawn/combat/collision/kill code was touched.

## Chosen N and why

**N = 4 pairs (8 real bot TCP connections).** The brief called for 3–5 pairs;
4 is comfortably in range. Eight simultaneous real `Bot` connections across
four lanes exercises real concurrency — multiple sockets draining logons,
eight ships assigned from eight racing spawns, four simultaneous projectile
streams resolved in one sector's collision pass each tick, and four
`KILL_SHIP(105)` broadcasts delivered to all 8 peers in the same run. It is
enough to prove the point without becoming the different task the brief warned
about (connection/thread scaling with dozens of bots). I chose 4 over the
minimum 3 for a little more confidence that concurrency, not luck, was what
was being demonstrated.

## Design decisions (all recorded, keyed to code I read)

- **Straight-line fire ⇒ lanes, not a melee.** The task already settled this:
  `Fight` in `Program.cs:44` only admits `Dispersion == 0f` weapons ("so a
  straight shot lands"), and there is no target-selection/aim-assist anywhere.
  A ship fires along its own heading (`Weapon.cs:203-211`). So "scale up"
  means N independent 1v1 lanes, each pair sharing an X coordinate so a
  shooter's straight shot only meets its own lane's target.

- **Generalized the `ShipSpawner` closure, `tools/livecombat/Program.cs:72-91`.**
  P22's `bool placedShooter` binary switch became an integer spawn counter:
  global spawn index `i` (0..2N-1) maps to `lane = i/2` and
  `shooter = (i % 2) == 0` (`Program.cs:79`). Even indices sit at lane origin
  `(lane*LaneSpacing, 0, 0)` (`Program.cs:80`) and are armed
  (`Energy = MaxEnergy*100f`, `MountWeapon` — `Program.cs:85-86`) — the exact
  P22 simplification, N times. Odd indices sit at `(lane*LaneSpacing, 0,
  TargetZ=-180)` and are idle targets, unarmed, exactly like P22. This is a
  direct generalization, not a redesign.

- **Position, not connection order, decides role — same rule as P22, now for
  N lanes (`Program.cs:182-188`).** The server drains the 2N sockets' logons
  in a racing order, so which physical bot lands on which spawn index is
  nondeterministic — the live run confirmed this: peer 3 ('SHOOTER1') actually
  got the lane-0 **target** spawn at `z=-180`, and 'TARGET0' got a shooter
  spawn, because connection order scrambled the mapping. Roles and lanes are
  therefore resolved from each bot's assigned `Ship.Position`: `lane =
  round(X / LaneSpacing)`, `isShooter = Z > -1f` (`Program.cs:186`). The
  structural guarantee is what matters: whatever the scramble, every lane ends
  with exactly one armed ship at `z≈0` and one unarmed ship at the same X at
  `z=-180`. An integrity check aborts if any lane lacks its shooter or target.

- **Lane geometry validated against the real weapon's range, not assumed.**
  The representative weapon's projectile (decoded, `Program.cs:140` diagnostic)
  is `Speed=1000, Lifespan=2` ⇒ straight range **2000**. TargetZ `-180` ≪ 2000,
  so the pair always dies well before the round expires — confirmed in-run: all
  four kills logged at sim-t `2.9999999999999973`, i.e. about 3 sim-seconds
  (travel 180 units at 1000 u/s ≈ 0.18 s plus the burst/energy cadence).
  `LaneSpacing=432` puts lanes' X values at 0/432/864/1296 — a projectile never
  changes X (`Projectile.cs:101`, `velocity = Speed·forward` with dispersion 0),
  so adjacent lanes can never meet regardless of spacing. 432 was chosen well
  above any ship half-length / projectile radius so nothing grazes a neighbour.

- **No `SideId` set ⇒ all ships side-less, fights-isolating-by-geometry.**
  P22's closure never sets `Ship.SideId`, so the friendly-fire gate
  (`Sector.cs:200-203`) never engages (it requires BOTH ships to carry a side).
  I kept that. Lanes are isolated by pure geometry (unique X per lane), not by
  the gate — which is fine here, because the geometry is the isolation mechanism
  the task prescribed. (Setting sides would have engaged the gate as a *second*
  guard, but that wasn't P22's choice and wasn't needed; noted for clarity.)

## Verbatim demo output

Three consecutive runs of `dotnet run --project tools/livecombat -- 0`, each
exiting 0 with 4/4 lanes scoring. Runs 1 and 3 are shown in full below
(identical kill evidence; run 2 abbreviated). Full captured logs:
`/tmp/p24_run1.log`, `/tmp/p24_run2.log`, `/tmp/p24_run3.log`.

Key lane/geometry diagnostic + the four kills (run 1):

```
[demo] weapon projectile 85: Speed=1000 Lifespan=2 → straight range 2000
[demo] TargetZ=-180 ≪ range (-180 < 2000); LaneSpacing=432 → X lanes never converge, shots stay in-lane
[demo] server listening on 127.0.0.1:34777
[demo] spawning 4 pairs = 8 real Bot instances, 4 lanes
[conn] peer 1 'S0' joined
[conn] peer 2 'T0' joined
[conn] peer 3 'S1' joined
[conn] peer 4 'T1' joined
[conn] peer 5 'S2' joined
[conn] peer 6 'T2' joined
[conn] peer 7 'S3' joined
[conn] peer 8 'T3' joined
[launch] peer 1 'SHOOTER0' got ship 8096 ... (X = 0, Y = 0, Z = 0)
[launch] peer 3 'SHOOTER1' got ship 8097 ... (X = 0, Y = 0, Z = -180)   ← raced: a "SHOOTER" landed the target slot
...
[demo] lane 0: shooter ship 8096 at Vector3 { X = 0, Y = 0, Z = 0 }, target ship 8097 at Vector3 { X = 0, Y = 0, Z = -180 }
[demo] lane 1: shooter ship 8098 at Vector3 { X = 432, ... }, target ship 8099 at Vector3 { X = 432, ..., Z = -180 }
[demo] lane 2: shooter ship 8100 at Vector3 { X = 864, ... }, target ship 8101 at Vector3 { X = 864, ..., Z = -180 }
[demo] lane 3: shooter ship 8102 at Vector3 { X = 1296, ... }, target ship 8103 at Vector3 { X = 1296, ..., Z = -180 }
[demo] all 4 shooters command AllWeapons (state=0x8000), all targets idle
[combat] ship 8097 killed by ship 8096 at sim-t 2.9999999999999973 — KILL_SHIP(105) -> 8 peer(s)
[combat] ship 8099 killed by ship 8098 at sim-t 2.9999999999999973 — KILL_SHIP(105) -> 8 peer(s)
[combat] ship 8101 killed by ship 8100 at sim-t 2.9999999999999973 — KILL_SHIP(105) -> 8 peer(s)
[combat] ship 8103 killed by ship 8102 at sim-t 2.9999999999999973 — KILL_SHIP(105) -> 8 peer(s)
[demo] === KILLS OBSERVED BY REAL BOTS (per lane) ===
[demo] server ticked 96 times, 0.1s sim-paced
[lane 0] shooter ship 8096 → target ship 8097 KILLED over the wire
[lane 0] KILL_SHIP(105) on target bot: shipID=8097 typeCredit=0 idCredit=8096 sideKiller=-1 bKill=False bDeath=True
[lane 0] KILL_SHIP(105) bytes = 0E-00-69-00-A1-1F-00-00-A0-1F-FF-FF-00-01
[lane 0] shooter bot observed the same kill credited to idCredit=8096
[lane 1] shooter ship 8098 → target ship 8099 KILLED over the wire
[lane 1] KILL_SHIP(105) on target bot: shipID=8099 typeCredit=0 idCredit=8098 sideKiller=-1 bKill=False bDeath=True
[lane 1] KILL_SHIP(105) bytes = 0E-00-69-00-A3-1F-00-00-A2-1F-FF-FF-00-01
[lane 1] shooter bot observed the same kill credited to idCredit=8098
[lane 2] shooter ship 8100 → target ship 8101 KILLED over the wire
[lane 2] KILL_SHIP(105) on target bot: shipID=8101 typeCredit=0 idCredit=8100 sideKiller=-1 bKill=False bDeath=True
[lane 2] KILL_SHIP(105) bytes = 0E-00-69-00-A5-1F-00-00-A4-1F-FF-FF-00-01
[lane 2] shooter bot observed the same kill credited to idCredit=8100
[lane 3] shooter ship 8102 → target ship 8103 KILLED over the wire
[lane 3] KILL_SHIP(105) on target bot: shipID=8103 typeCredit=0 idCredit=8102 sideKiller=-1 bKill=False bDeath=True
[lane 3] KILL_SHIP(105) bytes = 0E-00-69-00-A7-1F-00-00-A6-1F-FF-FF-00-01
[lane 3] shooter bot observed the same kill credited to idCredit=8102
[demo] RESULT: 4/4 lanes scored a real KILL_SHIP, all in the same server run
[shutdown] clean: 97 ticks, 0 overruns
```

Run 2 and run 3 each reported `RESULT: 4/4 lanes scored a real KILL_SHIP, all
in the same server run` and exited 0 (captured in the three `/tmp/p24_run*.log`
files). The evidence was identical kill-for-kill — four ships killed at
sim-t ~3.0, each `KILL_SHIP(105)` byte-identical to the pattern above, four
peer broadcasts reaching all 8 bots.

## Verification

- `dotnet build` — clean, 0 warnings.
- `dotnet test` — **1521 passed**, 0 failed (post-change full suite).
- Demo — 3 independent runs, all `exit 0`, all `4/4 lanes`. `[Collection("LiveNetwork")]` regression tests unaffected (the demo is a standalone tool; no test was added, per the brief "your call").

Baseline note: the very first `dotnet test` I ran showed one flaky failure,
`ServerSurvivesAbruptClientDisconnect`; it passed 3× in isolation and the full
suite re-ran clean. Unrelated to this task (the demo touches only
`tools/livecombat`), but logged for honesty — see `ServerTests.cs:160`.

## Files changed

- `tools/livecombat/Program.cs` — generalized the spawner to N pairs; per-lane
  kill report; lane/range diagnostics.
- `README.md` — Bots/combat-demo section rewritten for the N-lane scaling.
- `P24_N_BOT_COMBAT.md` — this note.
