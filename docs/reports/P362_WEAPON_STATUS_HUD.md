# P362 — Weapon/systems status HUD list (ammo, reload state, per mounted system)

Status: landed on `master` (Domingo pushes). Client-side HUD rendering; the
sim mechanic (P361 auto-reload-on-empty) is already landed and unchanged.

---

## 1. What was actually available to display (confirmed first, per scope #1)

Before building anything, I audited the client's data sources against the task's
assumption that "everything needed already exists client-side". Findings:

- **Per-mounted identity + ammo amount** — YES: `ShipEntity.Loadout`
  (`IReadOnlyList<ExpandedPartData>`, `ShipEntity.cs:77`) carries `PartId`,
  `Amount`, `MountId`, `FractionMounted` for every granted part, populated by the
  `BUY_LOADOUT_ACK` round-trip. Names/types resolve via the already-loaded
  `ShopCatalog` (the same decoded `PCore001.igc` the shop uses).
- **Live total ammo fraction** — NO (a real gap): `ServerShipUpdate.FromModel`
  hardcoded the ammo byte (byte 42 of the 47-byte payload) to `1.0` as a
  "flight demo never drains it" constant, so no client could ever see the pool
  deplete. Fixed (§3) — this is a correctness fill of an existing slot, not a
  format change (same 47-byte layout).
- **Reload-in-progress state** — NO on the wire as a per-tick value: P361's
  `Ship.LastAutoReloadOutcome` is **server-side sim state**. On SUCCESS the
  server refills the pool **synchronously in the same tick** and then broadcasts
  the applied entries sector-wide as `RELOAD(129)` — so the ammo fraction never
  shows 0 in the success case, and the only authoritative client-observable
  success signal is that relay (which P361 already broadcasts and this port's
  client simply never decoded). On FAILURE nothing is relayed and the fraction
  stays ~0. So "reloading" is derived from these two real signals:
  **RELOAD(129) relay** (native "Reloading ammo...") and **sustained-zero ammo
  trajectory** (native "Ammo depleted.").

## 2. How it reads P361's reload state

Two cheap client-observable signals, in the same spirit as
`DamageFlashTracker` (which infers damage from the hull fraction because there
is no discrete "hit" event client-side):

- **Success — the RELOAD(129) relay.** On a successful P361 auto-reload the
  server broadcasts the applied entries with `ShipID` filled
  (`Server.Combat.BroadcastAutoReload`). The client now decodes that already-
  broadcast message (a consumer, **no wire change**) and counts it per ship
  (`ShipEntity.ReloadCount`, bumped under the world gate by
  `ClientWorld.Apply(Reload)`). The weapon-status tracker edge-detects the
  count rising for the own ship and shows the affected projectile mounts as
  `RELOAD` for a brief `ReloadShowSeconds` (0.6s) window — the port's display of
  native's momentary "Reloading ammo..." — then back to `READY` (ammo is already
  restored, since the server refilled synchronously).
- **Failure — the ammo trajectory.** On failure (no spare pack in cargo) nothing
  is relayed and the pool stays at zero, so the own ship's live ammo fraction
  stays at/below an empty epsilon; after `DepleteThresholdSeconds` (0.35s) of
  sustained emptiness the mount is shown `DEPLETED` (native "Ammo depleted."),
  reverting to `READY` once the fraction recovers.

Because the port models ammo as ONE shared magazine (`Ship.Ammo`, drained by
every projectile weapon in `Weapon.cs`), all projectile mounts deplete/reload
together — faithful to the sim. Systems that never consume the pool (energy
weapons, missile racks) are always `READY` and never enter Reloading/Depleted,
which is what makes distinct mounted systems show genuinely **independent**
statuses.

## 3. The HUD list

A small persistent glass panel on the right edge (top-right, below the P298 Roll
gauge — clear of the speed readout and the P359 bearing arrow), titled
**SYSTEMS**. Each mounted weapon/missile system is one row with:

- an ASCII-safe short name (from the decoded part record; ~17 chars truncated);
- its live ammo as `N/CAP` plus a thin `DrawBar` fraction (its share of the
  shared magazine — reuses the exact fraction-bar primitive the player's own
  gauges use);
- a colour-coded status tag: dim **READY**, amber **RELOAD** (P361 reload in
  flight), red **DEPLETED** (P361 reload failed).

Non-ammo systems (energy weapons, missile racks) render `--` with always-**READY**
so they read as independent. Empty until the own ship has a `BUY_LOADOUT_ACK`
loadout (a stock auto-connect flight never buys one — same as native, you must
equip weapons). Reuses the existing `_small` font, the `HudColor` palette, and
the `FillRect`/`OutlineRect`/`DrawBar` primitives — no new asset, no new
rendering idiom.

## 4. Files changed

- `src/Allegiance.Core/Wire/ServerShipUpdate.cs` — `FromModel` now fills the
  existing byte-42 "ammo fraction" slot with the REAL `ship.Ammo /
  hull.MaxAmmo` instead of a constant 1.0 (same 47-byte layout; the fill makes
  the slot truthful — the client cannot detect depletion/reload otherwise).
- `src/Allegiance.ClientCore/State/ShipEntity.cs` — `ReloadCount` (long): counts
  `RELOAD(129)` relays received for the ship.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `Apply(Reload)` bumps the
  target ship's `ReloadCount` (a consumer of the already-broadcast relay).
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — routes
  `Reload.Fmid` (129) to the world.
- `src/Allegiance.ClientCore.MonoGame/Flight/WeaponReloadTracker.cs` (new) —
  pure per-frame status derivation (`Ready`/`Reloading`/`Depleted`) from
  ammo-fraction + relay signals; `WeaponStatus`, `WeaponSlotDescriptor`,
  `WeaponStatusRow` live here too.
- `src/Allegiance.ClientCore.MonoGame/Rendering/HudFrame.cs` — new
  `WeaponSystems` (IReadOnlyList<WeaponStatusRow>) field.
- `src/Allegiance.ClientCore.MonoGame/Rendering/HudRenderer.cs` —
  `DrawWeaponStatusList` + `StatusTag`, invoked from `Draw` when a frame has
  systems; `ReloadAmber` colour.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — `_weaponReloadTracker`,
  `BuildWeaponStatusRows` (mirrors the Loadout→slots→tracker pipeline), wired
  into `DrawHud`.
- `tests/Allegiance.ClientCore.MonoGame.Tests/WeaponReloadTrackerTests.cs`
  (new) — 7 pure tests of the tracker (7/7 pass).
- `tests/Allegiance.Core.Tests/Wire/GameMessageRoundTripTests.cs` — the
  byte-exact `SetCluster_EmitsExactDeclaredBytes` golden's ammo byte updated
  240→0 with an explanatory comment: P362 makes the ammo fraction real, and the
  test's `TestHull` has `MaxAmmo=0`, so a MaxAmmo=0 hull truthfully reports 0
  ammo (same 47-byte layout).
- `tools/weapon-status-proof/` (new) — the live-proof harness (below).

No wire-protocol changes: the only byte altered on the wire is the *value*
already occupying the documented ammo-fraction slot, and the RELOAD relay was
already broadcast on success — this port now merely decodes it.

## 5. Live verification (real client, real multi-weapon loadout)

Harness `tools/weapon-status-proof` (built on the P359 offscreen-harness
pattern): boots a REAL in-process server (`XXV_simple.igc`, `PCore001.igc`),
connects a REAL `ConnectionSession` ("Me_Weapon"), which BUYS a genuine two-
weapon loadout over the wire — **PE Gat Gun 1** (projectile, `CAmmoPerShot=1`,
consumes ammo) at mount 0 + **EW Utl Can 1** (energy, never consumes ammo) at
mount 1 — then FIRES the gatling through the real client→server fire path
(`CommandControls(AllWeapons)`) until P361's auto-reload triggers. The client's
own live world observes the real wire (ammo fraction + the RELOAD relay), drives
the REAL `WeaponReloadTracker`, and renders the REAL `HudRenderer.Draw`
offscreen to `.verify_p362/*.png`.

### CASE A — reload SUCCESS (spare ammo pack in cargo)
```
me hull #13, MaxAmmo=800, CAmmoPerShot=1; seeding pool to 48 (frac 0.060)
[proof] A: captured a_ready      — gatling READY,    ammoFrac=0.058 (LastAutoReload=NotAttempted)
[autoreload] ship 8096: ammo hit zero -> auto-reload 1 cargo entry applied -> RELOAD(129) shipID=8096 wide
[proof] A: captured a_reloading  — gatling RELOAD,   ammoFrac=0.500 (LastAutoReload=Reloaded)
[proof] A: captured a_recovered  — gatling READY,    ammoFrac=0.496 (server Ammo=394, LastAutoReload=Reloaded)
```
Visually (frames reviewed): `p362_a_ready` → gatling READY; `p362_a_reloading`
→ the SYSTEMS panel shows **PE Gat Gun 1 = RELOAD** (amber) while **EW Utl Can 1
= READY**, and the left AMMO bar reads ~half; `p362_a_recovered` → gatling back
to READY with ammo restored. The energy weapon stayed READY the whole time —
two distinct mounted systems, independent states.

### CASE B — reload FAILURE (empty cargo) → "Ammo depleted."
```
[proof] B: seeded Ammo=48 (frac 0.060), cargo ammo pack present: False
[autoreload] ship 8096: ammo hit zero, no cargo spare — Ammo depleted (no RELOAD relay)
[proof] B: captured b_depleted   — gatling DEPLETED, ammoFrac=0.000, energy=READY (server Ammo=0, LastAutoReload=Depleted)
```
`p362_b_depleted` shows **PE Gat Gun 1 = DEPLETED** (red) with an empty AMMO bar,
while **EW Utl Can 1 = READY** (cyan). The client's `DEPLETED` was cross-checked
against the server's real `Ship.LastAutoReloadOutcome == Depleted` and
`Ship.Ammo == 0` — the derived HUD state matches the server's ground truth.

Net: real client, real multi-weapon loadout, real firing to zero; both native
texts are represented — "Reloading ammo..." (RELOAD, success, via the relay) and
"Ammo depleted." (DEPLETED, failure, via sustained-zero), with distinct systems
remaining independent throughout.

## 6. Build & test status

- Whole-solution build: **0 warnings, 0 errors** (forced clean rebuild).
- New tracker tests: **7/7 pass** (`WeaponReloadTrackerTests`).
- Full suite run twice (`dotnet test AllegianceCore.slnx`):
  - Lobby 12/12; MonoGame 678/678 (incl. the 7 new tracker tests) every run.
  - ClientCore and Core.Tests: the only failures are the **pre-existing
    documented live-wire timing flakes** — a rotating subset each run
    (`ProjectileBroadcastTests`, `LiveTurretGunnerBindingTests`, `LiveMoneyTests`,
    `LiveStationRescueTests`, `GameOverPlayersWireTests`,
    `ThreePeerSeededSessionTests`, `ReadyCheckLiveTests`,
    `Auto_reload_relays_RELOAD129_sector_wide`, …). Each flake passes
    consistently in isolation (e.g. `ThreePeerSeededSession` 3/3,
    `Auto_reload_relays_RELOAD129_sector_wide` 3/3), confirming they are
    environmental, not regressions. The one byte-exact Core test affected by the
    ammo-fraction fix (`SetCluster_EmitsExactDeclaredBytes`) passes after its
    golden was updated (§4).
