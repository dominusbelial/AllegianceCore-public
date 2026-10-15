# P464 — a boarded turret gunner can aim and fire from the MonoGame client

## Why this task exists

This came out of the 2026-09-29 gameplay-parity pass. Crewing a bomber
or capital-ship turret is a signature Allegiance role, and in the port
it's dead end to end.

- **Server: done.**
  - Boarding: `PerformBoard`, `Server.Docking.cs:74`
  - Turret slot: `ChooseTurretSlot`, `:1263`
  - Gunner binding: `Weapon.SetGunner`, `:147-153`
  - `HandleActiveTurretUpdate` (`Server.ShipCommands.cs:202-240`) is a
    faithful port of `ProcessShipUpdate(ClientActiveTurretUpdate)`
    (`shipIGC.cpp:3349-3392`). On receipt it sets `gunner.StateM =
    WeaponsMask`, applies the controls, runs catch-up `ExecuteTurretMove`
    and sets the orientation.
  - Gunner-aimed firing works in the sim (`Weapon.cs:212-221`, aim follows
    `_gunner.Orientation`; `Ship.OnUpdate` turret loop).
  - See `docs/reports/P416_TURRET_SEAT_KEYS.md`.
- **Client: boarding and camera work, but nothing is sent.** Boarding is
  reachable from the docked TeleportPane (P273). The gunner camera
  exists (`src/Allegiance.ClientCore.MonoGame/Game1.Camera.cs:280-312`,
  `IsTurretGunner`, `TryGetGunnerCarrierPose`). But
  `ConnectionSession.SendActiveTurret` (`src/Allegiance.ClientCore/Session/ConnectionSession.cs:429`)
  and `SendInactiveTurret` (`:437`) have **no caller anywhere** in
  MonoGame. A seated gunner can look but never aims or fires.
- **What the native client does.** `clintlib.cpp:1870-1905` sends one
  message every `c_dsecUpdateClient`:
  - `FM_C_ACTIVE_TURRET_UPDATE`, with `ExportShipUpdate` of the gunner's
    own locally integrated orientation and controls, while `stateM &
    weaponsMaskIGC` (firing)
  - otherwise `FM_C_INACTIVE_TURRET_UPDATE`

  The gunner's orientation is integrated locally by `ExecuteTurretMove`,
  subject to the hardpoint's turn limits.
- **Gotcha:** the server drops an update whose `Time` is more than 1.5 s
  from the gunner's `LastUpdate` (`Server.ShipCommands.cs:213-214`). The
  MonoGame client has no server-clock estimate today; grep finds none.
  Derive one from the server times already carried on our own pose
  updates, and document it.

## Scope

1. **Gunner input → turret orientation.** While `IsTurretGunner()`, map
   mouse and joystick look (the same input sources flight uses,
   `Game1.FlightInput.cs`) to a locally integrated turret orientation.
   - Reuse Core's `Ship.ExecuteTurretMove` (`Sim/Ship.cs:4086`) math, or a
     pure extraction of it, so turn-rate and arc limits match the server.
   - Feed that orientation to the gunner camera (turret view) so the
     player sees where they aim.
2. **Send loop.** At the control cadence, send `ACTIVE_TURRET_UPDATE(99)`
   (`ClientActiveTurretUpdate.From(time, controls, orientation)`,
   `Wire/ActiveTurretUpdate.cs`) while the fire button is held, and
   `INACTIVE_TURRET_UPDATE(100)` otherwise. `time` is the derived server
   clock. Don't send any flight `CommandControls` while seated as a
   gunner; check what the flight loop does today and gate it.
3. **Feedback.** The turret's shots already render from the server
   broadcast. Make sure the HUD shows a turret reticle or an equivalent
   aim marker at screen centre in turret view, so the gunner can aim.

## Explicitly out of scope

- The server boarding and gunner code. It's done; don't change it except
  to fix a real bug you prove.
- In-flight seat switching (`TK_OccupyNextTurret`, needs a port of
  native's "move within the same ship" branch, `FedSrv.CPP:2338-2339`)
  and boarding while not docked. Both are named follow-ups (see P416).
- Local shot echo for gunner shots (P462 covers pilots only).
- Own-ship prediction (P447).

## Verification

- A loopback `Bot`/`ConnectionSession` test in the P109/P140 style:
  1. The gunner boards a docked carrier.
  2. Both launch.
  3. The gunner's client-side send loop emits ACTIVE updates while
     "firing".
  4. The server's turret weapon creates projectiles aimed along the
     gunner's orientation.

  Run it on unfixed code first. With no send loop, zero turret
  projectiles are created; record that.
- A pure test for the server-clock estimate staying within the 1.5 s gate
  under simulated latency.
- Live: two MonoGame clients, one pilot with a turreted hull and one
  gunner. The gunner aims and hits a target. Include the server log lines.
- `/home/domingo/.dotnet/dotnet build` → 0 warnings, 0 errors.
  `/home/domingo/.dotnet/dotnet test` → 3 consecutive clean runs.

## Deliverables

1. Committed on **`master`**, not pushed. Check the branch first.
2. Report `docs/reports/P464_TURRET_GUNNER_FIRE.md`: the pre-fix evidence,
   the input mapping, the clock derivation and the live proof.
