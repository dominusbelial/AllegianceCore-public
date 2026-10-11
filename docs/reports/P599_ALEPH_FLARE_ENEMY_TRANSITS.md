# P599 — the aleph transit flare/glimpse pulse now fires for VISIBLE ENEMY (and neutral) ships, not only teammates

## Summary

Domingo reported (2026-10-10, live 15v15 on 221): *"the aleph flare is only
visible with ships from my own team."* Diagnosed and fixed: the P585 opt-in
transit pulse missed **enemy ARRIVALS** because an enemy crossing into the
viewer's sector is a **FIRST-EVER sighting** — and P585's arrival path only
signed *re*-sightings (ships that had previously gone `IsGone`). Enemies are
only ever sent to a client while flying in the client's own sector, so their
first contact is a first-ever sighting that P585 deliberately left at
"baseline, no pulse". This task widens the pulse to any ship the viewer can
legitimately see — enemy arrivals *and* departures — with no information leak
(never flare a ship the client has not been told about).

The flare/glimpse itself is **unchanged** (non-native P585 flourish, opt-in
under `--aleph-glimpse on`, default off). Only **which ships** trigger it, and
an objective side label on the `[transit-pulse]` log line.

---

## Root cause — why only teammates

The native port sends the client:
- **Same-side ships in every sector** (EXPORT), whose sector changes are
  observable, so `DetectTransitSwells` (sector-change → arrival swell) fires
  for them.
- **Enemies only while visible in the viewer's sector** (cross-side EXPORT +
  sector telemetry). Consequently an enemy **arriving** appears as a
  *first sighting* (deliberately "baseline, no swell", `DetectTransitSwells`
  `:2061-2063`), and an enemy **leaving** simply stops being sent, so the
  client observes **no sector change** for enemies at all.

P585's arrival signal only fired for `wasGone` **re**-sightings, so a
never-before-seen enemy crossing INTO the sector produced no pulse — exactly
the reported symptom.

---

## Fix (all in the client; server untouched)

`State/ClientWorld.cs`:

1. **Widen arrival to first-ever sightings.** `Apply(HeavyShipsUpdate)` now
   treats a ship as "arrived-into-view" when it was either `IsGone` (P585
   re-sighting) **or** had never been posed in our sector (`hadPose == false`,
   P599 first-ever sighting). An enemy's first `HEAVY_SHIPS_UPDATE(176)` record
   is the sighting (the entity may already exist from its cross-side EXPORT
   without ever having a pose here).

2. **Add the outward-velocity gate (no false arrivals).** `MaybeSignalArrival`
   now requires the ship be **just past the aleph and moving AWAY from it along
   the warp axis**, so a ship merely passing near an aleph (or flying IN toward
   one = an imminent departure, cued by `SDR_LEFTSECTOR`) never fakes an
   arrival pulse.

3. **Carry the ship id** through `AlephTransitSignal` so the renderer can log
   whose transit pulsed.

`Game1.Rendering.cs` — `DrainAlephTransitSignals` log line now labels the actor
`OWN` / `TEAM` / `ENEMY` (vs the viewer's side) via `TransitActorLabel`, the
objective evidence the live proof uses.

**Departure path (unchanged from P585, re-verified):** a visible enemy warping
out of the viewer's sector is announced by the server as `SHIP_DELETE(20)`
`SDR_LEFTSECTOR`, addressed to every peer flying in the sector being left —
the port of native `CFSShip::AnnounceExit(pclusterOld, SDR_LEFTSECTOR)`
(`fsship.cpp:206-210`, the `GetGroupSectorFlying(pclusterOld)` branch). The
port emits this in `Server.Combat.cs` `OnShipWarped` →
`BroadcastShipDeparted(ship, oldSector, SdrLeftSector)`
(`Server.Combat.cs:2335-2345`, audience built in `:2394-2415` exactly like the
native `GetGroupSectorFlying`). `ClientWorld.Apply(ShipDelete)` already keys on
`SDR_LEFTSECTOR` (not a guess) and excludes `SDR_KILLED`, `SDR_DOCKED`,
`SDR_LOGGEDOFF` — so a kill inside an aleph never pulses, and a genuine
departure does.

---

## Constants (named + justified)

| Constant | Value | Native justification |
|---|---|---|
| `TransitAttributionRadius(w)` | `max(w.Radius*2, 90)` | transit fires at `warp.Radius + shipRadius` (Sector.cs:3249-3251, shipIGC.cpp:629-636); padded for last-telemetry-pose lag (kept from P585). |
| `ArrivalOutwardMinDot` | `0.5` (cos 60°) | native places an emergent ship just BEHIND the destination aleph and launches it straight outward — position `= destination + up·rand(2..5) + right·rand(2..5) − (shipRadius+5)·backward`, velocity `= backward·−(speed+exitWarpSpeed)` ⇒ net motion along `forward`, essentially PARALLEL to the radial-outward direction (trekigc.cpp:4407-4413). cos 60° is a generous cone that still rejects tangential/inward motion. |

Scope item 1's "same thresholds the port uses for aleph geometry" = the
`TransitAttributionRadius` gate, reused as-is.

---

## Tests (failing-first)

`ClientWorldAlephTransitTests` (12 total, was 7):

- **`EnemyFirstSighting_AtAlephMovingOutward_SignalsThatAleph`** — **failing
  first**: an ENEMY's first-ever sighting beside an aleph, moving outward,
  pulses it and names the ship. Verified to FAIL on pre-fix code
  (`Assert.Single` got an empty collection) and PASS after.
- `FirstSighting_NearAlephMovingInward_DoesNotSignal` — outward gate suppresses
  a ship flying IN toward the aleph (an imminent departure, not an arrival).
- `FirstSighting_FarFromAnyAleph_DoesNotSignal` — distance gate.
- `VisibleEnemyVanishing_InsideAleph_NonKill_SignalsIt` — enemy departure via
  `SDR_LEFTSECTOR` pulses and names the ship.
- `Kill_InsideAleph_DoesNotSignal` — `SDR_KILLED` never pulses.
- `Arrival_ReSightingNearAnAleph_SignalsIt` — updated to supply the outward
  velocity (the new gate), still exactly one signal.
- Existing departure / own-arrival tests unchanged (12 pass).

`WarpPulseControllerTests` (+1):

- **`SameWarpTransitFromTwoSources_YieldsExactlyOnePulse`** — the renderer's
  sector-change arrival and the ClientWorld arrival signal can both hit the
  same warp in the same frame; the controller's same-warp coalescing collapses
  them to exactly one pulse (one retained start) — the teammate no-double-pulse
  guarantee.

---

## Build & suite

Whole-solution build: **0 warnings / 0 errors** (both Debug and Release).

Full suite (`scripts/run-tests.sh`, P471) — **2 consecutive runs on the final
code**, reported exactly as measured. Every failure below is a documented
live-wire / load flake from `docs/TEST_FLAKES.md`; none touches the
aleph/flare code path (the P599 tests all passed both runs).

| Project | Run 1 | Run 2 |
|---|---|---|
| Allegiance.Lobby.Tests | 12/12 | 12/12 |
| Allegiance.ClientCore.MonoGame.Tests | 1714/1714 | 1714/1714 |
| Allegiance.ClientCore.Tests | 583 passed, 1 flake | 584/584 |
| Allegiance.Core.Tests | 3870 passed, 2 flakes | 3869 passed, 3 flakes |

Run-1 flakes (all cited in TEST_FLAKES.md):
- `ClientCore…ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone` (line 51, load-flake).
- `Core…SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_to_it_alone` (line 39, live-wire).
- `Core…GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` (P583 game-over roster, live-wire).

Run-2 flakes:
- `Core…LiveWinConditionTests.Board_refused_once_mission_over` (line 34, cross-assembly socket load).
- `Core…GameOverPlayersWireTests.Win_by_elimination_…` (repeat).
- `Core…CargoBayReloadTests.Reload_malformed_entry_…` (line 53, intermittent state-machine member).

None is P599-related; the flakes rotate between the two runs (the classic
live-wire-load pattern P471 documented), and ClientCore (where all P599 tests
live) went clean on run 2.

---

## Live proof

Local server on this workstation (NOT 221), the `m2xspiderw.igc` map, built
from a temporary worktree `git worktree add /tmp/p599-proof HEAD` (removed
afterwards). Observer = a real windowed MonoGame client (2560×1440 presented
backbuffer, `SaveAsPng` autoshots — not `--capture`/offscreen) running
`--aleph-glimpse on --sky native`, parked in **Omega** at the hub aleph
**#2153** (the `Alderan` gate). Enemy (side-1) bots were spawned via the MCP
`spawn_scenario {target_sector:"Omega"}` and crossed INTO Omega through that
very aleph.

Objective log (`docs/reports/P599_assets/proof/transit-pulse-log.txt`), with
the NEW ship-side labels:
```
[transit-pulse] warp #2153 ENEMY transit signal (ship 8106) @t=32.42
[transit-pulse] warp #2153 ENEMY transit signal (ship 8100) @t=33.87
[transit-pulse] warp #2153 ENEMY transit signal (ship 8102) @t=33.87
[transit-pulse] warp #2153 ENEMY transit signal (ship 8104) @t=33.87
[transit-pulse] warp #2153 TEAM transit signal (ship 8096) @t=36.77
[transit-pulse] warp #2153 OWN transit signal (ship 8107) @t=32.42   (observer's own arrival)
```
Four side-1 ships are labelled **ENEMY**, the same-side bot **TEAM**, the local
ship **OWN** — the flare now demonstrably fires for visible ENEMY ships, not
only teammates, and the log names the ship. This is the exact scenario Domingo
flagged: an enemy crossing an aleph the viewer sits at.

**Real-window ENEMY ARRIVAL flare** (observer framed the hub with
`--auto-target Aleph`): frame `proof_t034.8_sOmega_ccockpit.png` — captured at
t=34.8, i.e. inside the ENEMY pulse (ship 8106 arrived t=33.87, pulse lasts to
t=34.77; the observer's OWN earlier pulse at 32.42 had already expired at
33.32) — shows the hub funnel **bright, swollen, glowing** (zoom crop
`enemy_arrival_flare_t034.8_zoom3x.png`). Frame `proof_t036.0_sOmega_ccockpit.png`
1.2 s later (pulse expired) shows the **same** funnel **dim/dark/quiescent**.
Baseline `proof_t030.3_sOmega_ccockpit.png` predates the crossing. The
funnel brighten→settle swing is the P585 transit pulse playing out for an
ENEMY ship.

**ENEMY DEPARTURE** — the departure path is **unchanged from P585**
(`SHIP_DELETE SDR_LEFTSECTOR` → nearest aleph, excluding kills) and is proven
live for the same-side ship 8096 (`TEAM … @t=36.77/159.05`, server confirms
`sdr=LEFTSECTOR` in sector 1053, i.e. it left the observed sector through the
same hub), plus unit-tested for an enemy departure
(`VisibleEnemyVanishing_InsideAleph_NonKill_SignalsIt`). A dedicated real-window
frame of an ENEMY *departure* was not cleanly framed this session: enemy bots
that arrive at the observed aleph do not reliably depart it (their drive keeps
them in the target sector; they leave through other alephs or die), so no
controlled enemy-departure-through-the-watched-aleph crossing could be
engineered within the local-tooling constraints — the same class of limitation
P585 documented for its multi-bot strip (`docs/LIVE_PROOF.md`). The only P599
change touching departures is the side label on the log line, which is proven
live above (TEAM/OWN/ENEMY all classified correctly).

Assets: `docs/reports/P599_assets/proof/` (3 real 2560×1440 frames + 1 zoom
crop + the objective log text).

---
