# TURRET_GUNNER_BINDING — P109: Phase 3 cluster E (part 2)

The turret **weapon-gunner binding**: occupying a turret slot (P108) now
actually **does** something — a turret's fire direction and blast target follow
the **gunner**, not the carrier ship, and the gunner's facing is driven by the
new `FM_C_ACTIVE_TURRET_UPDATE(99)` message rather than a normal
`SHIP_UPDATE(24)`.

Branch `master` at P108. No Windows VM. `dotnet` invoked explicitly as
`/home/domingo/.dotnet/dotnet` (the box's PATH snap stub is broken).

---

## Design picked — orientation integration / boarded-routing (the task's first "state plainly")

The port has the **two real `ExecuteTurretMove` call sites**, exactly as the
native does — they are not alternatives, they share one implementation:

1. **`Ship.ExecuteTurretMove(timeStart, timeStop, Orientation pOrientation)`**
   — `CshipIGC::ExecuteTurretMove` (`src/Igc/shipIGC.cpp:1618-1702`), ported
   once into `Ship` (`Sim/Ship.cs:1011-1035`): controls constrained to a
   sphere, slew rate scaling with throttle (7.5°/s–75°/s,
   `shipIGC.cpp:1640-1643`), turn rates set outright (not ramped), yaw/pitch/
   roll applied to the passed orientation and re-normalized. The native's
   turret-limit clamp block is `/* commented out */` in the reference
   (`shipIGC.cpp:1656-1697`), so only the re-normalize remains — reproduced
   verbatim. `Ship.Orientation` was get-only; `SetOrientation(Orientation)` was
   added to install a freshly-integrated orientation (the C++ setter that swaps
   the whole matrix, `shipIGC.cpp:3341,3386`).

2. **Call site A — the message handler** (`Server.HandleActiveTurretUpdate`,
   `src/Allegiance.Server/Server.cs:2142-2179`), the port of
   `CshipIGC::ProcessShipUpdate(const ClientActiveTurretUpdate&)`
   (`shipIGC.cpp:3349-3392`): `deltaT = gunner.LastUpdate − atu.time` bounded to
   ±1.5 s (else drop), `SetStateM(weaponsMaskIGC)`, controls export, decode
   orientation, then the n-step snapshot catch-up
   (`n = 1 + ⌊|deltaT|/c_fTimeIncrement⌋`, `c_fTimeIncrement = 0.25 s`,
   `igc.h:5120`), each step calling `ExecuteTurretMove`, then `SetOrientation`.
   Gated on the sender being a genuine boarded gunner (`Sector != null &&
   ParentShip != null && TurretId != null` — the same
   `FedSrv.CPP:4873-4875` gate).

3. **Call site B — the carrier's own tick** (`Ship.OnUpdate`,
   `Sim/Ship.cs:1080-1100`): for each turret weapon (`MountId >= MaxFixedWeapons`)
   with a gunner, copy the gunner's orientation, `ExecuteTurretMove(now−dt, now,
   &o)`, `SetOrientation(o)` — `shipIGC.cpp:336-338`. Between real
   `ACTIVE_TURRET_UPDATE` snapshots this keeps the turret aiming continuously.

**Boarded-routing choice:** native gates `FM_C_SHIP_UPDATE(24)` on
`pship->GetParentShip() == NULL` (`FedSrv.CPP:4859`) — a boarded gunner's normal
ship update is ignored (its client sends 99/100 instead, `clintlib.cpp:1884-1899`).
The port mirrors this exactly in the `SHIP_UPDATE` handler
(`Server.cs:1391-1402`): a boarded ship's `SHIP_UPDATE` no longer applies
`Controls`/`StateM`; the `ACTIVE_TURRET_UPDATE` message is the sole driver of a
boarded gunner's controls/orientation.

**INACTIVE handling** mirrors native `CFSPlayer::SetInactiveTurretUpdate`
(`fsship.cpp:1220-1224`): `SetStateM(0)` — a full assignment, not a mask-clear.

## `ClientActiveTurretUpdate` field layout — confirmed against real test evidence

The struct the arc left as "needs inference" is confirmed as the native
declares it (`src/Igc/igc.h:2470-2476`):

```
Time time; CompactControlData controls; CompactOrientation orientation;   // 4 + 4 + 8 = 16 B
```

confirmed three ways:
- **by usage** — the quoted `ProcessShipUpdate` reads exactly
  `shipupdate.time` / `.controls` / `.orientation` (`shipIGC.cpp:3353,3361,3363`);
- **by the client producer** — `ExportShipUpdate` sets `pshipupdate->time =
  ServerTimeFromClientTime(GetMyLastUpdate())`, `.controls.Set(m_controls)`,
  `.orientation.Set(GetOrientation())` (`shipIGC.h:1206-1212`);
- **by live codec evidence** — the new `Wire/ActiveTurretUpdate.cs` reuses the
  port's existing `CompactQuant.ControlAxis` + `CompactOrientationCodec` (the
  exact shapes `ClientShipUpdate`/`ServerShipUpdate` already decode) and
  `TurretWireTests.ActiveTurretUpdate_RoundTrip_IsAFixedPoint_ByteExact` +
  `_IsByteExact_24ByteLayout` prove the encode→decode→encode fixed point and the
  exact 24-byte message layout.

**Message 99 carries an outer `Time timeUpdate`** (`Messages.h:673-676`) in
addition to the inner `atu.time` (both derive from the same source, `clintlib.cpp:1892`
+ `shipIGC.h:1208`; the native server discards the outer field, `FedSrv.CPP:4877`).
The codec keeps both for byte-exact wire fidelity (same redundancy `ShipUpdate.cs`
already carries), 24 bytes total. **No position/velocity** — a gunner only turns.

## Dead-client-code finding — `FM_S_INACTIVE_TURRET_UPDATE` is genuinely unreachable

`appmsg.cpp:2110`'s `case FM_S_INACTIVE_TURRET_UPDATE:` (and its sibling
`FM_S_ACTIVE_TURRET_UPDATE` at 2087) sit inside a `/* NYI ... */` block comment
(`appmsg.cpp:2086-2127`), so they are never compiled. No
`DEFINE_FEDMSG(S, ACTIVE/INACTIVE_TURRET_UPDATE, ...)` exists anywhere in
`Messages.h` (only the `C` directions at 673 and 678), and `FedSrv` only ever
sends nothing for these ids (its two cases at `FedSrv.CPP:4868,4882` are `C`
receivers). **Confirmed: genuinely dead client code, no hidden second message.**

## Observer broadcast finding (task item 7) — REFUTED, reported plainly

The claim that "the gunner is a real Ship so the existing broadcast already
covers it" does **not** hold. Confirmed with a real live test
(`LiveTurretGunnerTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`,
scenario 7): a gunner whose position is parked (brake controls, zero velocity)
and whose orientation changes receives **no** `SINGLE_SHIP_UPDATE(78)` at a
same-side observer peer. Reason, structural:
- `SHIP_STATUS(90)` carries **no orientation field at all** (`ShipStatus.cs:67-76`
  — shipID/hull/sector/station/parent/state/stateTime only), and
- the only orientation-bearing broadcast, `SINGLE_SHIP_UPDATE(78)`
  (`ServerShipUpdate` carries orientation at byte 24), is gated on **position
  movement** (`(ship.Position − LastBroadcastPos).Length() ≥ DirtyEpsilon`,
  `Server.cs:452`), which a turret gunner never produces (it only turns).

So a gunner's updated facing does **not** reach observers through the existing
machinery. Per the task's out-of-scope note, **no new broadcast path was built**
— that is P110 (the periodic turret-state broadcast). Finding stated plainly.

## What landed

### Wire codecs (round-trip byte-exact, `Wire/TurretWireTests.cs`, 8 tests)
- `Wire/ActiveTurretUpdate.cs` — `FM_C_ACTIVE_TURRET_UPDATE`(99), 24 B:
  header + outer `Time` + `ClientActiveTurretUpdate` payload (`Time time;
  CompactControlData controls; CompactOrientation orientation`, reusing the
  existing rich/wire types). `Wire/InactiveTurretUpdate.cs` — `FM_C_
  INACTIVE_TURRET_UPDATE`(100), no fields, exactly the 4-byte header. Both
  registered in `FedMessageCatalog`/`FedMessageDumper` + formatters.

### Weapon / aim (`Sim/Weapon.cs`)
- `Weapon.Gunner` (nullable `Ship?`) + `SetGunner(Ship?)`/`GetGunner()`
  (`weaponIGC.h:184-193`).
- `FireWeapon`: aim orientation = `_gunner?.Orientation ?? shipOrientation`
  (`weaponIGC.cpp:184-195`); the mount's physical position still derives from
  the carrier ship (unchanged). `IsWeaponSelected`: a turret gun fires only when
  it has a gunner AND the gunner's `StateM` has `weaponsMaskIGC`
  (`weaponIGC.h:248-251`) — the bit `ACTIVE_TURRET_UPDATE` sets.
- Blast-power gunner target: for `BlastPower != 0`, the target source is the
  **gunner's** `Ship.CommandTarget` (new nullable property) when a gunner is
  assigned, else the carrier's (`weaponIGC.cpp:226-231`), with the native
  intercept-leading (`weaponIGC.cpp:273-288`) rewriting the round's lifespan.
  Same-sector = the port's only visibility stand-in for `CanSee`.

### Simulation (`Sim/Ship.cs`, `Sim/Projectile.cs`)
- `Ship.SetOrientation`, `Ship.ExecuteTurretMove`, `Ship.CommandTarget`
  (nullable `Model?`, the `m_commandTargets[c_cmdCurrent]` subset).
- `Ship.OnUpdate` turret-gunner prediction loop (`shipIGC.cpp:327-343`).
- `Projectile` gained an optional `lifespanOverride` so leading can rewrite
  expiry (`weaponIGC.cpp:290-304`).

### Server (`Allegiance.Server/Server.cs`)
- Boarding handler binds the carrier's weapon at the chosen turret slot to the
  boarder as gunner (native `SetTurretID`→`SetGunner`, `shipIGC.h:1405-1437`);
  leaving clears it (`shipIGC.cpp:3652,3681`). Mount lookup reuses the existing
  `MountedPart.MountId` / `Ship.Weapons[i].MountId` mapping (no new bookkeeping).
- Dispatch + `HandleActiveTurretUpdate` for 99; `InactiveTurretUpdate` handler
  for 100; `SHIP_UPDATE(24)` gated on `ParentShip == null`.

## Verification

- Build clean: **0 warnings, 0 errors**.
- New P109 tests: **12** (8 codec `[Fact]`s in `Wire/TurretWireTests.cs`,
  1 live proof in `LiveTurretGunnerTests`, 3 firing proofs in
  `TurretGunnerFiringTests`). Scenarios 1–6 + item-7/8 all covered:
  1. Board ⇒ carrier weapon at slot 2 has `Gunner == boarder`; leave clears it. ✅
  2. Live `ACTIVE_TURRET_UPDATE(99)` with a real yawed orientation ⇒ gunner's
     server-side facing updated to match (snapshot install) and
     `StateM == weaponsMaskIGC`. ✅
  3. Between real `ACTIVE_TURRET_UPDATE`s the carrier's tick keeps predicting
     the gunner's facing forward (no new message ⇒ it still turns). ✅
  4. `INACTIVE_TURRET_UPDATE(100)` ⇒ `StateM == 0` (fsship.cpp:1223). ✅
  5. Firing with a gunner whose orientation differs from the carrier ⇒ the
     projectile's direction follows the **gunner** (component-exact on the
     yaw-discriminating axis), not the carrier. Blast weapon + gunner target ⇒
     the round's lifespan is led to the computed intercept (exact match). ✅
  6. Fixed mount (never a gunner) fires along the carrier as before — no
     regression (full existing suite passes). ✅
  7. Observer reach refuted (above), via a real parked-gunner live test. ✅
  8. Both codecs round-trip byte-exact. ✅
- Full suite: **3 consecutive clean runs, 2459/2459 each** (2447 baseline + 12
  new). The only intermittent failure observed across the whole session was the
  pre-existing `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_
  relay_to_real_peers_over_the_wire` network-timing flake (confirmed by running
  it in isolation — it fails on its own at ~79 ms; this P109 diff touches no
  chat code).
- No VM used.
