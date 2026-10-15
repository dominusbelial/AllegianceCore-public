# P462 — draw own shots and play the fire sound at the trigger, not a round trip later

## Why this task exists

This came out of the 2026-09-29 gameplay-parity pass (weapons feel).

- **Native fires on the client.** Every native client runs
  `CshipIGC::Update` → `weapon->Update` for ships
  (`~/Development/Allegiance/src/Igc/shipIGC.cpp:312-340`). The weapon
  creates its projectiles locally in `CweaponIGC::FireWeapon`
  (`Igc/weaponIGC.cpp:142-322`). `FM_CS_PROJECTILE_INSTANCE(12)` is defined
  (`Inc/Messages.h:131`) but nothing in FedSrv or clintlib sends it. So the
  player's own shots appear, and their sound plays, on the frame the
  trigger is pulled.
- **The port waits for the server.** The client sends controls only
  (`ConnectionSession.CommandControls`, `src/Allegiance.ClientCore/Session/ConnectionSession.cs:128-139`,
  "the server reads controls only"). Then:
  1. The server fires (`src/Allegiance.Core/Sim/Weapon.cs:189-362`).
  2. It broadcasts `PROJECTILE_INSTANCE` for every round (`Weapon.cs:315`
     → `Server.Combat.cs` `BroadcastProjectileInstance`).
  3. The client creates a `ProjectileEntity` only on receipt
     (`ClientWorld.cs:1459-1470`).
  4. The fire sound plays only when that echo arrives within
     `FireOriginRadius = 150` of us (`Game1.Audio.cs:64-86`,
     `Audio/ProjectileSoundPlanner.cs` `IsLocalFire`,
     `ProjectileAmbientSoundCurve.cs:61`).

  Shot and sound therefore lag the trigger by a full round trip plus up to
  one server tick.
- **Everything needed is already client-side:**
  - The static core loads (`Game1.Lifecycle.cs:208` →
    `ShopCatalog.TryLoad` → `StaticCore.Load`, `Rendering/ShopCatalog.cs:57`).
  - Our mounted parts are known (`ShipEntity.Loadout`, set by
    `ClientWorld.Apply(BuyLoadoutAck)`, `ClientWorld.cs:1601-1607`, and
    already walked by `Game1.Panes.cs:379-395`).
  - The fire bits are composed locally (`Game1.FlightInput.cs:547-624`,
    sent at `:994-999`).

## Scope

1. **Local own-shot echo (visual only).** Add a pure, unit-testable
   `OwnShotEcho` (or similarly named) class in `Allegiance.ClientCore.MonoGame/Flight/`.
   - Each frame it takes: our fire bits (AllWeapons/OneWeapon plus the
     selected mount), our mounted weapon parts resolved against the loaded
     core, our pose, and the last server-reported energy and ammo
     fractions.
   - While the trigger is held, it emits local visual rounds at each
     weapon's real cadence: `DtimeBurst`, with the first shot immediate,
     mirroring `Weapon.FireWeapon`'s `_nextFire` logic.
   - Each round gets the real projectile speed and lifespan, inherits our
     velocity when `!AbsoluteF`, and applies dispersion.
   - It stops emitting when the last-known energy can't cover
     `EnergyPerShot`, or ammo is 0 for an ammo weapon. No local
     energy/ammo bookkeeping beyond that gate.
   - Local rounds are rendered through the existing projectile draw path
     (`Game1.Rendering.cs:585-654`) and **never** affect damage.
2. **Sound at the trigger.** Play `PlayWeaponFire()` (`SoundManager.cs:386`)
   when a local round is emitted. Stop the echo-driven local-fire sound for
   our own rounds so each shot plays exactly once.
3. **De-duplicate the server echo.** `ProjectileInstance` carries no
   launcher id. Drop an incoming `PROJECTILE_INSTANCE` as "our own echo"
   when all of these hold:
   - its type is one we're currently emitting,
   - it spawned within `FireOriginRadius` of our server-reported position,
   - it arrived within a bounded echo window (derive the window, document
     it, and make it a named constant).

   Other ships' rounds are unaffected. If a round is dropped but no local
   round was emitted (a local/server disagreement), render the server round
   instead. Document the heuristic's failure modes.
4. **Compatibility with P447.** P447 will add own-ship client prediction as
   a separate task. The echo must read our pose through **one** accessor
   used by the renderer, so a predicted pose flows in automatically once
   P447 lands. Don't depend on P447 or implement prediction here.

## Explicitly out of scope

- Any server change, wire change, or change to damage/hit authority. All
  hits stay server-side.
- Own-ship movement prediction (P447).
- Per-weapon sounds and per-type projectile visuals (P465/P465b). Use the
  existing single sound and tracer.
- Turret-gunner shots (P464).
- Missiles. They already have their own lock and fire path; don't touch it.

## Verification

- Unit tests for `OwnShotEcho`:
  - cadence matches `DtimeBurst`
  - the first shot is immediate
  - no rounds when energy or ammo is gated
  - dispersion stays within bounds
  - inherited velocity is applied when `!AbsoluteF`
- Unit tests for the de-dup rule: an own echo is dropped, a foreign round
  is kept, a round outside the window is kept, and a dropped-but-unmatched
  round falls back to the server copy.
- A test showing the **pre-fix** behaviour: with the current code, no own
  round exists before the server echo. Record it in the report.
- Live proof against a real server: timestamps from the MonoGame log show
  the shot and sound at the trigger frame, and exactly one sound per shot.
- `/home/domingo/.dotnet/dotnet build` → 0 warnings, 0 errors.
  `/home/domingo/.dotnet/dotnet test` → 3 consecutive clean runs. Only the
  pre-existing documented timing flakes are acceptable; name them.

## Deliverables

1. The echo, the de-dup and tests, committed on **`master`**. Don't push.
   Check `git branch --show-current` before committing.
2. Report `docs/reports/P462_OWN_SHOTS_LOCAL_AT_TRIGGER.md`: the
   before/after latency evidence, the echo-window derivation, the de-dup
   failure modes, and the P447 compatibility seam.
