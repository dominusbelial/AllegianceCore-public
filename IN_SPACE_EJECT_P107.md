# IN_SPACE_EJECT_P107 — phase 3 cluster C (part 2): in-space EJECT (drifting lifepod hull)

Branch `master`. P107 is the `!bKilled` branch of the native `KillShipEvent`
(`FedSrv.CPP:11634` onward); P105 (previous task) was the `bKilled == true`
branch. They are mutually exclusive per-death outcomes, and this doc records
(with test evidence) that the two do not overlap.

## The real `c_habmLifepod` bit value and its citation

`c_habmLifepod = 0x04` — `src/Igc/igc.h:728`:
```
726: const HullAbilityBitMask c_habmBoard          = 0x01;
727: const HullAbilityBitMask c_habmRescue         = 0x02;
728: const HullAbilityBitMask c_habmLifepod        = 0x04;
...
```
The port already decodes the hull-ability field `HabmCapabilities`
(`DataHullTypeIGC.cs:51,169`) but had no predicate. P107 adds
`DataHullTypeIGC.HasCapability(short bit)` and a named constant
`HabmLifepod = 0x04`, porting `IhullTypeIGC::HasCapability(habm)`
(`src/Igc/hullTypeIGC.h:243-246`: `return (m_data->habmCapabilities & habm) != 0;`).

Real-data validation: the tests assert the actual Lifepod hull record decoded
from `PCore001.igc` carries the bit —
`LiveInSpaceEjectTests` asserts `lifepodHull.HasCapability(DataHullTypeIGC.HabmLifepod)`
(observed `HabmCapabilities = 0x00A4`, bit 0x04 set).

## The `bEjectPods` simplification, stated plainly

The `bKilled` gate's first clause is `(!pmp->bEjectPods)` (`FedSrv.CPP:11779`).
`MissionParams.bEjectPods` (`Messages.h:472`, type `MissionParams`,
`AllMessages.h`) is **not decoded anywhere in this port** (no
`MissionParams`/`EjectPods` type exists under `src/Allegiance.Core`; grep
confirms). P107 simplifies: **eject pods are treated as always enabled** — the
`!bEjectPods → bKilled` clause never fires. This is the explicit out-of-scope
decision; `MissionParams` decoding is not attempted for this one field.

Consequence (important, test-evidenced below): with eject pods always enabled,
suicide on a **non-lifepod** player ship now routes to the in-space EJECT
branch, not to death. Native suicide passes `amount = 1.0f`
(`FedSrv.CPP:6347`), making `bKilled = false` for a non-lifepod ship. Two
prior-suite tests that used suicide-on-a-normal-fighter to mean "death"
therefore had to be re-based onto already-lifepod ships to reach the death
branch (see "test adaptation" section). This is a real, native-faithful
consequence, not a regression.

## The `amount < 0.0f` clause — finding

`bKilled` also forces true when `amount < 0.0f` (`FedSrv.CPP:11779`). I checked
every caller of `KillShipEvent` in the native tree: suicide passes `1.0f`
(`FedSrv.CPP:6347`), the docked suicide passes `1.0f` (`FedSrv.CPP:12404`),
rock collision passes `0.0f` (`FedSrv.CPP:13839`), and combat passes the lethal
hit's own `amount` (> 0). No caller passes a negative `amount` into
`KillShipEvent`. In this port the repair path (`Ship.ReceiveDamage`, negative
amount) returns `c_drNoDamage` and never raises `KillShipEvent`
(`Ship.cs:628-636,668-671`). So the clause is never satisfied — stated, and no
mechanism built around it.

## Implementation

- **`src/Allegiance.Core/Wire/Eject.cs`** — `FM_S_EJECT(77)`
  (`Messages.h:572-578`): `ShipID shipID; Vector position; Vector velocity;
  Vector forward; Cookie cookie`. `Cookie` is `BYTE` (`igc.h:516`); `Vector` is
  3 LE floats (the established 12-byte triple). MSVC-default layout (the model
  already documented in `JoinedMission.cs:29-44`): 2 pad after the shipID short
  to 4-align the first Vector, 3 trailing pad to the 4-aligned 48-byte struct.
  Encode emits exactly 48 bytes and asserts the layout; `Decode` round-trips.
- **`DataHullTypeIGC`** — `HasCapability(short bit)` + `HabmLifepod = 0x04`.
- **`Orientation`** — single-arg `Set(Vector3)` (port of
  `orientation.cpp:314-356`), which the eject math uses.
- **`Ship`** — `SetTurnRate(int axis, float rate)` (port of
  `SetCurrentTurnRate`), for the `pi·2` roll spin.
- **`Server.BroadcastKillShip`** (`Server.cs:2764` onward) — the seam P103/P105
  wired (`Server.cs:266`). P107 adds the `bKilled` gate before the kill
  broadcasts and, on `!bKilled`, calls `EjectSurvivor` and returns — so an
  ejecting ship gets **no** `SHIP_DELETE(20)`, **no** `KILL_SHIP(105)`, and
  **no** `ENTER_LIFEPOD(166)`, and is **not** removed from the sector.
- **`Server.EjectSurvivor`** — the port-relevant slice of `FedSrv.CPP:11918-12022`:
  hull swap to the side civilization's Lifepod (`SetBaseHullType`, 11942),
  reposition at the impact point `p1` with a directional outward burst and a
  `pi·2` roll spin (11944-12008), and a real `FM_S_EJECT(77)` broadcast to
  **everyone flying in the same sector** (`GetGroupSectorFlying(pcluster)`,
  12011-12021). The native missile-target-clearing loop (11925-11932) is a
  structural no-op here (this port's `Missile` carries no target back-ref).

### Audience: sector-scoped and cross-side (not same-side, not mission-wide)

The EJECT broadcast audience is `GetGroupSectorFlying(pcluster)`
(`FedSrv.CPP:12020`) — every ship flying in the victim's **one** sector,
regardless of side. This is distinct from the same-side gate
(`_teammateExportSent`) and from the mission-wide `WARP_BOMB`/`ASTEROID_DESTROYED`
precedent (P101/P102). P107 implements it as peers whose ship's `Sector == the
victim's sector` (the identical loop the kill broadcast already uses,
`Server.cs:2778-2781`). The cross-side proof is a test (below).

## Mutual exclusivity evidence (real loopback tests)

`tests/.../LiveInSpaceEjectTests.cs` drives a real in-process `Server` over
loopback TCP.

1. **Non-lifepod player, lethal hit → survives in space, EJECT(77) sector-wide**
   (`Player_ship_lethal_damage_ejects_in_sector_cross_side_broadcast`):
   the victim is a non-lifepod, peer-owned ship on a side that owns a friendly
   station (elsewhere), and the lethal hit comes from a **cross-side** launcher
   in the same sector. Asserts: still in the same sector (not removed, not
   relocated to the friendly station — so even the opportunity to respawn is
   bypassed), hull becomes the side's Lifepod, no `SHIP_DELETE`/`KILL_SHIP`/
   `ENTER_LIFEPOD` anywhere, and a real `EJECT(77)` reaches **both** the
   same-side victim peer and the cross-side observer peer (both named the
   victim id, identical position/velocity/forward).

2. **Already-a-lifepod, lethal hit → true death (P105 branch)**
   (`Already_a_lifepod_is_truly_killed_not_re_ejected`): the victim is built on
   its side's Lifepod hull. Lethal damage → no second EJECT, and a real
   `SHIP_DELETE(20,SDR_KILLED)` + `KILL_SHIP(105)` to both same-sector peers,
   plus the P105 respawn (`ENTER_LIFEPOD(166)` to the owner, relocation to the
   friendly station, docked).

Together these prove the two branches are genuinely **mutually exclusive**, not
overlapping: the first death of a non-lifepod ship ejects; the second death (of
the resulting lifepod) kills and respawns. No ship produces both an EJECT and a
death message for the same outcome. (Evidence: the two tests above assert each
branch's artefacts are absent in the other.)

### Test adaptation (necessary consequence — disclosed)

With eject pods always enabled, a **non-lifepod** player ship's suicide/lethal
hit ejects (native-faithful, `FedSrv.CPP:6347`+`11779`). Two prior tests that
used suicide-on-a-normal-fighter to mean "death" therefore had to be re-based
onto already-lifepod ships to keep exercising the death branch:

- `LiveSuicideShipDeleteTests.Suiciding_ship_dies_and_both_death_messages_arrive`
  — the suiciding ship now rides a Lifepod hull (a true second death).
- `LivePlayerDeathRespawnTests.Player_death_relocates_to_docked_lifepod_and_only_owner_gets_enter_lifepod`
  — the victim is now built already-a-lifepod; the "hull actually changed" check
  was replaced by the (still-true) "hull is/stays the side's Lifepod" check, and
  the earlier logs were updated.

Both files carry a comment block citing `FedSrv.CPP:6347` + `11776-11780`.
Their intent (SHIP_DELETE/KILL_SHIP on suicide death; death-relocation +
owner-only ENTER_LIFEPOD) is unchanged. `Bot_ship_death_never_triggers_respawn`
and `Player_death_with_no_friendly_station_does_not_respawn` were unaffected
(the latter's artificial absent side still falls through to the death path).

## Verification

- Three consecutive clean runs of the full suite: **2431 passed, 0 failed**
  each (see session log).
- No VM used; real in-process-server loopback tests.
- Session stats logged via `session_stats.py`.

## Out of scope (restated)

`MissionParams.bEjectPods` decoding; boarded/child-ship exclusion beyond
"there are no boarded ships yet" (cluster E); the `amount < 0.0f` finding only
(no mechanism built); P105's respawn/ENTER_LIFEPOD flow; any lifepod-specific
flight-model change beyond the hull swap + broadcast.
