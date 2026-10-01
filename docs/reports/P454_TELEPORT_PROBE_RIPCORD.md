# P454 — You can't ripcord to a teleport probe (teleport-probe ripcord tiers)

Landing: **master** (not pushed — Domingo pushes). Branch at commit time:
`master`.

## Summary

Teleport probes are now real ripcord anchors, porting the two probe tiers of
native `CshipIGC::FindRipcordModel` (`src/Igc/shipIGC.cpp:3803-3814` and
`:3841-3896`) plus the probe anchor's per-tick legality re-check and the probe
landing in `FedSrvSiteBase::UseRipcord` (`src/FedSrv/FedSrv.CPP:13461-13476`).
A probe the P435 shop sells and the `N` key deploys now gives a team
something to ripcord onto: rushing, forward staging, and bringing a miner's
escort home.

Four files changed plus two test files:

- `src/Allegiance.Core/Sim/Probe.cs` — `Probe.CanRipcord`, the direct port of
  `CprobeIGC::GetCanRipcord` (`probeigc.h:248-254`).
- `src/Allegiance.Server/Server.cs` — `FindRipcordModel` gains the
  command-target probe tier (Tier 1) and the friendly-probe fallback tier
  (Tier 3), with the goal-position resolution from the command target.
- `src/Allegiance.Core/Model/Sector.cs` — `RipcordAnchorStillLegal` gains the
  probe branch.
- `src/Allegiance.Server/Server.Docking.cs` — `PerformRipcordTeleport` gains
  the probe branch; the ship/probe non-station placement is extracted into the
  shared `PlaceRipcordNearTarget`.
- `tests/Allegiance.Core.Tests/ProbeCanRipcordUnitTests.cs` — pure-domain
  `CanRipcord` conjunct proofs.
- `tests/Allegiance.Core.Tests/LiveTeleportProbeRipcordTests.cs` — real
  loopback wire tests (FIRE_EXPENDABLE deploy → ripcord → teleport, plus the
  denial / tier-ordering / abort scenarios), including the pre-fix failure
  evidence.

## `Probe.CanRipcord` — port of `GetCanRipcord` (`probeigc.h:248-254`)

```
bool GetCanRipcord(float ripcordSpeed) const
{
    float dt = m_probeType->GetRipcordDelay();
    return (dt >= 0.0f) &&
           ((GetMyLastUpdate() - m_time0) >= dt) &&
           ((GetMyLastUpdate() + (ripcordSpeed + 1.0f)) <= m_timeExpire);
}
```

Ported verbatim as `public bool CanRipcord(double now, float ripcordSpeed)` on
`Probe`, substituting `ProbeType.DtRipcord` for `GetRipcordDelay()`,
`DeployTime` for `m_time0`, `TimeExpire` for `m_timeExpire`, and the caller's
current sim time (`Mission.LastUpdate`) for `GetMyLastUpdate()`:

- **Conjunct 1** `dt >= 0` — teleport-ness is the *sign* of the delay field,
  **no capability-bit lookup** (the task's explicit note, and true of the
  real PCore001 data: the two `ripcordprobe` types id 6 / id 600 carry
  `DtRipcord` 30 / 15; every other probe is `-1`).
- **Conjunct 2** `(now − DeployTime) >= dt` — the activation delay must have
  elapsed.
- **Conjunct 3** `(now + (ripcordSpeed + 1)) <= TimeExpire` — the probe must
  not expire before the ripcord lands.

A real data nuance surfaced while designing the live test: PCore001 ships
**two** teleport-probe types, `ripcordprobe` id 6 (`DtRipcord 30`, `Lifespan
60`) and id 600 (`DtRipcord 15`, `Lifespan 60`). The default ripcord hull
decodes `RipcordSpeed = 30`, so for id 6 the delay conjunct (`now − T0 ≥ 30`)
and the expiry conjunct (`now + 31 ≤ T0 + 60` → `now − T0 ≤ 29`) are mutually
contradictory — id 6 is **never** ripcordable on that hull. id 600 has a real
`[15, 29]` window. The live tests therefore use id 600 (smallest `DtRipcord`),
and `PumpPastActivationDelay` advances sim time past the deployed probe's own
delay plus a small margin (staying inside its lifespan window), never a blind
long pump.

## `FindRipcordModel` — tiers added and their order

`Server.FindRipcordModel` (`Server.cs:802`) previously had two tiers
(friendly `c_sabmRipcord` station, then friendly ripcord-target ship) and a
doc comment deferring the command-target and probe tiers. The new search order
matches native exactly:

1. **Command-target probe** (`shipIGC.cpp:3803-3814`, P454): if
   `ship.CommandTarget` is a same-side `Probe` in the target sector that
   `CanRipcord(now, ripcordSpeed)`, it is the anchor.
2. **Friendly `c_sabmRipcord` station** (`shipIGC.cpp:3835-3838`, pre-existing).
3. **Friendly probe** (`shipIGC.cpp:3841-3896`, P454), players only
   (`ship.PilotType >= Ship.PilotTypePlayer`, native `m_pilotType >=
   c_ptPlayer`): the most recently deployed same-side `CanRipcord` probe in the
   target sector (DeployTime maximum — native searches the probe list
   *backwards* so the newest wins), or the one nearest the goal position when a
   goal exists.
4. **Friendly ripcord-target ship** (`shipIGC.cpp:3898-3930`, pre-existing).

`ripcordSpeed = ship.RipcordSpeed` is computed once at the top
(`GetRipcordSpeed()`, `shipIGC.cpp:3796`), and `now = ship.Mission.LastUpdate`.

### Goal position (tier 3 nearness pick)

Native resolves the goal as the command target (`m_commandTargets[c_cmdCurrent]`,
falling back to `c_cmdAccepted`) when it is seen by the side and in the target
cluster (`shipIGC.cpp:3775-3790`). The port replicates this as the command
target (`ship.CommandTarget ?? ship.AcceptedShipTarget`) when its sector is the
target sector; its position seeds `goal`. When a goal exists, tier 3 picks the
nearest `CanRipcord` probe (squared distance minimum); otherwise the most
recently deployed. (This port has no per-side `SeenBySide` model for this
decision; the same-side filter on the candidate probe list plays the
visibility gate's role — a noted simplification.)

### Can a probe be a command target today? — Yes

`Ship.CommandTarget` is typed `Model?` (`Ship.cs:1142`, `c_cmdCurrent`) and
`Probe` is a `Model`, so a probe **can** currently be a command target; no port
change was needed to make Tier 1 reachable. The live `Command_target_teleport_
probe_beats_friendly_station` test proves it end-to-end. (The task's "check
what CommandTarget can hold" is answered: it holds any `Model`, probes
included.) The command-target **ship** tier (`shipIGC.cpp:3816-3831`) remains a
scoped-out follow-up, as does the warp-adjacent-cluster expansion
(`shipIGC.cpp:3936-3972`).

## Anchor legality — `RipcordAnchorStillLegal` probe branch (`Sector.cs:1296`)

A probe anchor is legal (the per-tick re-check native runs, `FedSrv.CPP:
13385-13396`) when it:

1. **still exists in its cluster** — the probe is looked up in
   `cluster.Models`; destroyed or expired probes are removed from the sector on
   destruction/expiry (`Probe.OnUpdate` / `Probe.ReceiveDamage`), so a vanished
   probe is "no cluster" (`FedSrv.CPP:13388`);
2. **is still same-side** (`FedSrv.CPP:13387`); and
3. **still `CanRipcord`** — re-evaluated every tick with `now =
   mission.LastUpdate` and the ripper's `RipcordSpeed`. This makes a probe that
   expires mid-countdown (or a destroyed one) abort the ripcord, exactly like
   native's per-tick re-check.

Previously any non-Station/Ship anchor returned `false` immediately; the probe
branch replaces that dead-end.

## Landing — `PerformRipcordTeleport` probe branch (`Server.Docking.cs:505`)

A probe anchor is the native *non-station* case of `UseRipcord`
(`FedSrv.CPP:13461-13476`): place the ripper at
`probe.Position + dir · (probe.Radius + ripper.Radius + 25)` in a random unit
direction, velocity `dir · c_fcidExitStationSpeed`, turn rates zeroed, oriented
along `dir`, then move to the probe's cluster — **with no energy cost**: the
`RipcordCost` gate lives inside the `OT_ship` branch
(`FedSrv.CPP:13452-13460`), so a probe anchor pays nothing.

To avoid duplicating the ship branch's placement arithmetic, the ship/probe
relocation is extracted into `PlaceRipcordNearTarget(Ship rip, Vector3
targetPos, float targetRadius, Sector targetSector)` and both branches call it
(the ship branch passes `target.Position`/`ShipRadius(target)`; the probe
branch passes `probe.Position`/`probe.ProbeType.Radius`). The probe branch
resolves its probe in the target cluster, falls through to consume the attempt
(state cleared) if it vanished between revalidation and drain, and otherwise
places and logs.

## Client-side RIPCORD_ACTIVE / HUD finding — no change needed

`ClientWorld.Apply(RipcordActivate)` (`ClientWorld.cs:1038-1046`) keys the HUD
"RIPCORDING" tag **only** on `ShipidRipcord == MyShipId` and `SidRipcord` (the
*requested* sector). It never reads `OtRipcord` (the anchor's object type), so a
probe anchor travels through the exact same Activate/Aborted/SET_CLUSTER edges
as a station or ship anchor: `RipcordActive` is set on activate, cleared on
`RIPCORD_ABORTED`, our own `SET_CLUSTER`, or the MonoGame client's countdown
self-expiry (`ClientWorld.cs:1072-1083`). No crash and no blank — the
`otRipcord`/`oidRipcord` fields remain wire-carrying metadata the client does
not branch on. Confirmed by inspection; **no client change was required.**

## Pre-fix failing-test evidence

Temporarily disabling the two probe tiers of `FindRipcordModel` (the
command-target tier and the friendly-probe tier) turned the four probe-activation
live tests red — "no RIPCORD_ACTIVATE — the teleport probe was not chosen as the
anchor" — while the three denial / station-tier tests stayed green:

- RED `Wire_deployed_teleport_probe_is_the_anchor_and_ripcord_lands_near_it`
- RED `Command_target_teleport_probe_beats_friendly_station`
- RED `Most_recently_deployed_teleport_probe_wins_without_goal`
- RED `Probe_destroyed_mid_countdown_aborts_ripcord`
- GREEN `Probe_not_yet_active_is_denied_before_activation_delay` (correctly
  expects denial)
- GREEN `Non_teleport_probe_is_denied_even_after_long_delay` (correctly expects
  denial)
- GREEN `Friendly_ripcord_station_wins_over_probe` (anchors on the pre-existing
  station tier)

This is the exact discrimination the task requires: the headline wire test
fails on the unfixed code because no model is found (the ripcord is refused /
denied), and it passes with the fix.

## Tests added

`ProbeCanRipcordUnitTests` (pure domain):
- `Teleport_probe_ripcordable_after_activation_delay_only`
- `Non_teleport_probe_is_never_ripcordable`
- `Probe_about_to_expire_is_not_ripcordable`

`LiveTeleportProbeRipcordTests` (real loopback, PCore001 `ripcordprobe` id 600):
- `Wire_deployed_teleport_probe_is_the_anchor_and_ripcord_lands_near_it`
  (the headline: real FIRE_EXPENDABLE deploy in X, cross-sector request from an
  anchor-less sector Y, anchored on the probe, teleport lands in X within
  `probe.Radius + ship.Radius + 25`)
- `Probe_not_yet_active_is_denied_before_activation_delay`
- `Non_teleport_probe_is_denied_even_after_long_delay`
- `Friendly_ripcord_station_wins_over_probe` (tier 2 > tier 3)
- `Command_target_teleport_probe_beats_friendly_station` (tier 1 > tier 2; also
  proves a probe can be a command target)
- `Most_recently_deployed_teleport_probe_wins_without_goal` (tier 3, no goal)
- `Probe_destroyed_mid_countdown_aborts_ripcord` (vanished probe aborts)

## Verification / scope notes

- Whole-solution build via `/home/domingo/.dotnet/dotnet build`: **0 warnings,
  0 errors**.
- The teleport-probe deploy in the live test rides the **real FIRE_EXPENDABLE
  wire path** (a mounted `DataPartTypeIGC.ProbeLauncher` + the bot's
  `SendFireExpendable(Dispenser)` → `Server.HandleFireExpendable` →
  `Ship.DeployProbe`), so the whole deploy→anchor→teleport chain is exercised
  end-to-end, not mocked.
- **Flakes seen (all pre-existing live-wire timing flakes, proven pristine-tree
  environmental):** the full suite surfaces a *rotating* set of live-wire
  timing flakes that fail under full-suite load and in isolation, and that
  reproduce on the pristine tree with the P454 change stashed — none reference
  P454 symbols and none are the new P454 tests (which pass in every run).
  Named across the runs: `LiveTurretGunnerBindingTests.Board_binds_gunner_and_
  active_turret_update_roundtrips_live` (the skill's documented persistent
  machine-state flake), `LiveWinConditionTests.Multi_mission_disconnect_scopes_
  per_mission` (documented flap), `GameOverPlayersWireTests.*` (Conquest-leak +
  lists-every-player), `ConquestTerritoryLiveTests.*` (Flag_capture /
  Flags_disabled / Conquest-leak), `LiveMoneyTests.Board_refund_*`,
  `AddPartTests.*`, `Transport.TcpLoopbackProofTests.ClientHalf_*`,
  `Transport.ConsoleVerbosityLiveTests.Stuck_npc_*`, `ShipDeleteGoneLiveTests.
  DockOut_WatcherLatchesGone_*`, `JoinFlowLiveTests.EntityTable_*`,
  `FullSessionKillIntegrationTests.FullSession_ThreePeers_*`, and
  `ChatTargetComposeLiveTests.ComposeDefault_Team_*`. Proving one set
  environmental: stashing the 4 P454 production files (moving the new test
  files aside) and rerunning the failing classes left the same 7 Core failures
  (`LiveTurretGunner`, `LiveMoney.Board_refund`, `GameOverPlayersWire` ×2,
  `ConquestTerritoryLive` ×3) failing on the **pristine tree** — conclusive
  that they predate and are independent of P454.
- Out of scope, per the brief and unchanged: the command-target **ship** tier
  (`shipIGC.cpp:3816-3831`), allied ripcord (`bAllowAlliedRip`, no alliances in
  the port), native client-local "Teleport active in \<sector\>" text and
  `DestroyTeleportProbe` (`WinTrek/trekigc.cpp:3019-3033`), NPC/drone use of
  teleport probes, armed (gun-tower) probes (`probeigc.cpp:70-137,352-528`),
  enemy-spotting SHIP_STATUS reports, the in-flight turret-seat switch, and
  station capture/alerts/minefields (other named follow-ups).
