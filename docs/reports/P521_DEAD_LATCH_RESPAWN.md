# P521 — after a death + respawn, the own ship stays "dead" forever on the client (and remote ships too): the IsDead latch must clear at the real respawn signal

**Stage:** P521 · **Branch:** `master` (unpushed) · **Date:** 2026-10-03

## Symptom (Domingo's live playtest, 2026-10-03)

"Controls not working after undocking." Evidence log: `~/monogame-client-run-deadlatch.log`.
After a suicide → lifepod-eject → death, Domingo respawned in the hangar with the **same**
ship id (8104), but on every later undock the ship stayed dead on the client: `[sim] freeze:
dead`, `[sim] STOPPED sending native SHIP_UPDATE`, and no RESUME on any later undock. The
server's `[ctrlak]` showed `accepted` frozen at 6208 with every `dropped[...]` counter zero —
the client had simply stopped sending (P507/P518 acceptance and cookies were fine).

## Root cause (verified in source + reproduced live)

The port's persistent `ShipEntity` table latches death permanently. `ClientWorld.Apply(KillShip)`
(`ClientWorld.cs:2150`) and the `SDR_KILLED` branch of `Apply(ShipDelete)` (`:2197`) both set
`ShipEntity.IsDead = true`; **nothing ever cleared it**. `IsGone` (the transient "out of view"
latch) is cleared by later pose/identity updates (SetCluster / ShipReset / HeavyShipsUpdate /
ExportShip), but `IsDead` is not — and `IsNotVisible => IsDead || IsGone` (`ShipEntity.cs:49`).
So after a death the own ship is `IsNotVisible` forever, and P508's own-ship sim freezes
whenever `mine.IsNotVisible` (`Game1.Prediction.cs:103-118`) — freezing exactly at "dead" and
never resuming after respawn + undock. The same sticky `IsDead` hid a **remote** ship that died
and respawned with the same id on every other client forever.

**Native has no sticky death.** Native's client runs a live IGC sim: `SHIP_DELETE(20)
SDR_KILLED` evicts the ship with `ship->SetCluster(NULL)` (`clintlib/appmsg.cpp:470-476`), and
when the server re-seats the ship the `FM_S_SET_CLUSTER(7)` handler re-homes it
(`pship->ProcessShipUpdate(...)`, `pship->SetCluster(pcluster)`, `SetCookie`, `appmsg.cpp:
1041-1087`). There is no permanent "dead" flag — death is transient by construction; the port's
persistent id-keyed entity is what made it sticky.

## Fix (scope 2 — clear the latch at the native-matching real respawn signal)

`ShipEntity.IsDead` is now cleared at the exact sites that already clear the transient `IsGone`,
i.e. the server-authoritative "real presence is back" signals, and **not** at the weak one:

| signal | meaning | clears IsDead |
|---|---|---|
| **SET_CLUSTER** (own ship) | respawn / launch / undock / warp / join — native re-homes `m_ship` into the cluster here | ✅ own ship |
| **SHIP_RESET** | server actively re-engaged the ship's live pose (pose rejection) | ✅ |
| **HeavyShipsUpdate(176)** | remote ship genuinely flying in the recipient's own cluster (P375) | ✅ remote |
| **ExportShip** | identity re-established — the server never re-exports a corpse | ✅ remote |
| **SingleShipUpdate** | also reaches out-of-view same-side teammates (P85) — semantically weak | ❌ **kept latched** |

Files: `ClientWorld.cs` `Apply(SetCluster)` / `Apply(ShipReset)` / `Apply(HeavyShipsUpdate)` /
`Apply(ExportShip)` each add `e.IsDead = false;` next to their existing `e.IsGone = false;`;
`Apply(SingleShipUpdate)` is deliberately untouched (the P370 protection preserved).

**Why this cannot revive a corpse from a stale or late packet:** (1) the client/server
connection is a single **TCP** stream, which delivers in order — a pose/export generated before
the death is necessarily processed *before* the `KILL_SHIP`, so any strong presence message the
client processes *after* the death was sent by the server *after* it (a genuine respawn signal).
(2) `HeavyShipsUpdate(176)` only ever carries ships the server considers alive in the
recipient's own cluster, and `ExportShip` is never sent for a corpse. (3) The one message that
*can* legitimately arrive for an out-of-view ship — `SingleShipUpdate` — is the single site that
remains excluded, exactly mirroring the pre-existing P370 rule for `IsGone`.

## Scope items

1. **Native respawn semantics found** (see Root cause): evict-on-death + re-home-on-SET_CLUSTER,
   no sticky flag.
2. **Cleared the death latch at the right moment** — the four strong presence-establishing sites
   for own/remote ships (table above), preserving the P344/P370 corpse protections.
3. **P508 sim resumes + SHIP_UPDATEs restart + `accepted` climbs** — proven live (below).

Out of scope confirmed untouched: P507/P518 server acceptance & cookies; lifepod rescue.

## Verification

### Whole-solution build
`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug` → **0 warnings, 0 errors**.

### Unit tests (ClientCore) — new `DeadLatchRespawnTests` (6) + updated `ShipDeleteGoneTests` / `ClientWorldNewContactCueTests`
Pin the exact semantics: own-ship SET_CLUSTER clears IsDead after death; own-ship SHIP_RESET
clears it; remote HeavyShipsUpdate(176) clears it; remote ExportShip clears it; **SingleShipUpdate
does NOT** (P370 protection); and a second death re-latches IsDead after a respawn clear.
The formerly contradictory `ShipDeleteGoneTests.Dead_ship_stays_hidden_even_if_identity_reappears`
(the old "death is permanent" assertion) is rewritten to the P521 behavior
(`Dead_ship_returns_and_renders_via_respawn_export`), plus a new
`Dead_ship_stays_hidden_on_cross_sector_single_ship_update`. `ClientWorldNewContactCueTests.
Dead_ship_never_cues_even_if_identity_arrives` becomes `Dead_ship_stays_silent_until_it_genuinely_respawns`
— a corpse stays silent while dead (even a weak pose), but a genuine respawn re-EXPORT re-cues
as a fresh not-visible→visible edge (P373). Filtered run of these classes +
`ClientWorldShipResetTests`: **31/31 passed**.

### Live test #1 (REQUIRED, failing-first) — `DeadLatchRespawnLiveTests.Own_ship_sim_resumes_and_accepted_climbs_after_death_respawn_undock`
A real `ConnectionSession` drives the real production own-ship stack (`OwnShipSimulator` +
`OwnShipServerClock` from real PING echoes + `SendShipPose`) over a real TCP wire to a real
in-process server. The ship is a Lifepod from spawn (the P107 trick — a non-lifepod player ship
would *eject* instead of die), a real lethal `ReceiveDamage` on the real combat path raises the
full death → respawn → docked sequence (KILL_SHIP + SHIP_DELETE SDR_KILLED + docked SHIP_STATUS
+ ENTER_LIFEPOD), and the test replicates the production freeze gate (only send while not
docked/not dead/not gone). Sequence run **twice**: fly → real death → respawn (docked) → undock →
fly.

**Failing first** (fix reverted to the original "death is permanent" code): the test reaches
DEATH #1, then fails exactly at the discriminator —
`death latch was NEVER cleared by the respawn+undock — the ship stayed dead on the client (the P521 bug)`
— the sim stayed frozen and `accepted` never climbed.

**Green measurement** (real wire, real server, both deaths):

    [P521-LIVE] baseline: flying, accepted -> 15
    [P521-LIVE] DEATH #1: client IsDead=true, sim frozen; respawned docked; accepted frozen at 15
    [P521-LIVE] RESPAWN+UNDOCK #1: IsDead cleared + StationId=-1 after 2 ticks
    [P521-LIVE] AFTER #1: accepted 15 -> 31 (climbed 16); IsDead=false
    [P521-LIVE] DEATH #2: IsDead=true, sim frozen; accepted frozen at 31
    [P521-LIVE] AFTER #2: accepted 31 -> 46 (climbed 15)
    [P521-LIVE] FINAL: accepted=46 rejected=0 dropped[cookie]=0

- The sim **resumed** after both respawn+undocks (`IsDead` cleared + `StationId` cleared), and the
  server's `accepted` **climbed** after each death (15 → 31 → 46). **`rejected=0`,
  `dropped[cookie]=0`** — no acceptance/cookie regression (P507/P518 intact). Repeat death twice:
  both arcs green.

### Live test #2 (two-client, REQUIRED) — `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn`
Two real `ConnectionSession`s A and B in the same friendly garrison sector. A dies for real;
B's `ClientWorld` latches `Ships[A].IsDead`. A respawns docked, then undocks back into the shared
sector; B's in-cluster `HeavyShipsUpdate(176)` clears the latch:

    [P521-2C] B saw A die (IsDead latched) after 0 ticks; A respawned docked
    [P521-2C] B sees A again after A's respawn+undock (2 ticks); IsDead cleared on B

(Note: on the unfixed code B's `IsDead` for A stays latched forever, so B never sees A again.)

### Full suite — `scripts/run-tests.sh` (P471), three consecutive runs
Grand total per run: **5354** = Lobby 12 + MonoGame 1204 + ClientCore 430 + Core 3708.

| run | Lobby | MonoGame | ClientCore | Core | failures |
|---|---|---|---|---|---|
| 1 | 12/12 | 1204/1204 | **430/430** (clean) | 3705/3708 | Core: `SideEliminationLiveTests…Eliminating_side2…`, `MineCrossSideVisibilityLiveTests…Enemy_ship_spots…`, `GameOverPlayersWireTests…Conquest_win…leak…` |
| 2 | 12/12 | 1204/1204 | 428/430 | 3704/3708 | ClientCore `FullSessionKillIntegration…CrossSideKill`, `ClientWarpApproachSlowHullLiveTests…Legacy_200_450…`; Core `StationUnderAttackAlertLiveTests…Defending_side…`, `SideEliminationLiveTests…`, `LiveRipcordTests…First_activation…`, `CargoBayReloadTests…Reload_malformed…` |
| 3 | 12/12 | 1204/1204 | 429/430 | 3698/3708 | ClientCore `FullSessionKillIntegration…CrossSideKill`; Core `StationUnderAttackAlertLiveTests…Defending_side…`, `SideEliminationLiveTests…`, `ShipsUpdateLiveTests…Locked_target…`, `LiveRelaunchTests…Carrier_without_launch_slots…`, `GameOverPlayersWireTests…Conquest_win…leak…`, `ConquestTerritoryLiveTests…Conquest_win…leak…`, `CargoBayReloadTests…Reload_NA…` + `…Reload_swaps…`, `AddPartTests…Part_pickup_destroys…` + `…Part_pickup_decline…` |

Every failure is a **pre-existing rotating live-wire load-flake** in an assembly/file that does
**not** reference any P521 symbol (all in `Allegiance.Core`, plus the two ClientCore live tests
`FullSessionKillIntegration…CrossSideKill` and `ClientWarpApproachSlowHullLiveTests…`, none of
which use the own-ship native path or `ShipEntity.IsDead`). All are cited in
`docs/TEST_FLAKES.md` — the P521 observations section lists them with their ledger lines; the
previously-undocumented `StationUnderAttackAlertLiveTests…Defending_side…` is added there as a
new load-flake. The two ClientCore failures reproduce clean in isolation (re-verified **2/2
green standalone**); the Core ones rotate member-to-member even across reduced/isolation runs,
confirming the timing class. **No P521-area failure**: `DeadLatchRespawnTests`,
`DeadLatchRespawnLiveTests` (both live tests), `ShipDeleteGoneTests`, and
`ClientWorldNewContactCueTests` passed in **all three runs**.

## Files changed
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — **the fix**: clear `IsDead` in
  `Apply(SetCluster)` (own ship), `Apply(ShipReset)`, `Apply(HeavyShipsUpdate)` (remote),
  `Apply(ExportShip)` (remote). `Apply(SingleShipUpdate)` deliberately unchanged.
- `src/Allegiance.ClientCore/State/ShipEntity.cs` — `IsDead` doc: not permanent; cleared on real
  respawn / re-presence signals, not on SingleShipUpdate.
- `tests/Allegiance.ClientCore.Tests/DeadLatchRespawnTests.cs` — **new** (6 unit tests).
- `tests/Allegiance.ClientCore.Tests/DeadLatchRespawnLiveTests.cs` — **new** (2 live tests,
  failing-first + two-client).
- `tests/Allegiance.ClientCore.Tests/ShipDeleteGoneTests.cs` — updated one assertion + docs to
  the P521 behavior, added a SingleShipUpdate-doesn't-revive test.
- `tests/Allegiance.ClientCore.Tests/ClientWorldNewContactCueTests.cs` — the P373 corpse-cue
  test updated for the respawn re-cue (fresh-sighting edge).
- `docs/TEST_FLAKES.md` — P521 session observations (all flakes citable; adds the newly-observed
  `StationUnderAttackAlertLiveTests` load-flake).
- `docs/reports/P521_DEAD_LATCH_RESPAWN.md` — this report.

## Commit
`master` only, **not pushed** (branch verified with `git branch --show-current`). `git log -1`
confirms the `P521:`-prefixed commit is HEAD.
