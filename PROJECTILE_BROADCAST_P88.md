# P88 — Broadcast real projectile instances (Phase 2, slice 2)

A real weapon shot (already resolved fully server-side by `Sim/Weapon.cs`
`FireWeapon`) is now broadcast to the shooter's same-side peers as a real
`FM_CS_PROJECTILE_INSTANCE(12)` message — the real game's wire message for a
freshly-created projectile (`Inc/Messages.h:131-135`). Until now a shot only
ever manifested as the target's health changing after the fact; this is the
first slice that lets a client actually *see* the projectile.

## Alignment assumption (stated plainly)

The C++ struct `DataProjectileIGC` is **not declared anywhere** in this
checkout. Verified independently (not taken on faith):

- `grep -rn "DataProjectileIGC" /home/domingo/Development/Allegiance/src/`
  finds only **usage sites**, no struct definition:
  - `src/Igc/weaponIGC.cpp:213-272` constructs one in this order —
    `projectileTypeID` (a `short`), `forward` (a `Vector`), `velocity` (a
    `Vector`), `lifespan` (a `float`).
  - `src/Igc/projectileIGC.cpp:38-92,149-158` — `Initialize` reads the same
    four fields; `Export` (the send-side serializer, line 149) writes back
    only `projectileTypeID`/`velocity`/`forward` (not `lifespan` — it is
    creation-time-only, never re-exported).
- `grep -rn "DataShipIGC" ~/Development/Allegiance/src/` likewise finds no
  definition (the tier-3 precedent this task mirrors) — so tier 3
  (usage-site inference) is exactly where `DataProjectileIGC` sits, same as
  `DataShipIGC`.

**What cannot be verified:** whether there is compiler-inserted alignment
padding between the leading `short projectileTypeID` and the first
`Vector forward`. Natural MSVC alignment inserts 2 bytes to 4-byte-align the
`Vector`; a `#pragma pack(1)` struct (like `ShipLoadout`) would not. There is
no header to read it from, no VM probe possible (a probe needs a real
declaration — there is none), and no corpus (`.igc` files never contain a live
projectile instance; this is an ephemeral runtime message, not static data).
Unlike every prior struct this project has ported, real native-client
byte-for-byte fidelity for this one specific struct **cannot be independently
verified with resources available to this project**.

**Choice:** natural-MSVC-alignment (**padded**). The codec inserts 2 padding
bytes at message offset 10 (after `projectileTypeID` at 8) so `forward` lands
4-byte-aligned at offset 12 — the layout MSVC produces for a struct of
`short` followed by 4-byte-aligned members when no packing pragma is in
effect. This is a reasoned default, not a verified fact. It makes
`DataProjectileIGC` 32 bytes and the whole message 52 bytes:
`header(4) + timeCreate(4) + DataProjectileIGC(32) + position(12)`.

The correctness bar this task enforces instead — and verifies for real — is
the server-encodes / client-decodes round-trip and real observed broadcast
behavior, both covered by live tests (below). Should a follow-up ever get a
real `DataProjectileIGC` declaration to compile against, flipping this to
`pack(1)` (30-byte struct, 50-byte message) is a one-field layout change in
`src/Allegiance.Core/Wire/ProjectileInstance.cs` (`DataProjectileSize`,
the `WriteZeros(2)`/`Advance(2)` padding calls) plus the two unit tests'
width constants.

## What was added

- **Codec** `src/Allegiance.Core/Wire/ProjectileInstance.cs`, fmid 12. `Time`
  → `uint` (the established codec convention, cf. `ServerShipUpdate.Time`);
  `Vector` → the shared 12-byte 3-float LE encode/decode; `DataProjectileIGC`
  inlines as `short projectileTypeID` + `Vector forward` + `Vector velocity` +
  `float lifespan` per the evidence above. `FromProjectile(Projectile, uint)`
  builds it from the just-created model.
- **Event** `ProjectileCreatedEvent(Projectile)` added to `IIgcSite`
  (`Sim/IIgcSite.cs`, no-op default), following the established
  `DamageShipEvent`/`KillShipEvent`/`ClusterUpdateEvent` pattern.
  `Weapon.FireWeapon` raises it immediately after `sector.Add(projectile)`
  (`Sim/Weapon.cs`).
- **Host hook** `ServerHost.ProjectileCreated` + `ProjectileCreatedEvent`
  implementation (`ServerHost.cs`), the same nullable-Action bridge as the
  sibling `ShipKilled`/`Payday`/`Mine` hooks.
- **Broadcast** `Server.BroadcastProjectileInstance`, wired at boot
  (`_host.ProjectileCreated = ...`, `Server.cs`), sending
  `ProjectileInstance.FromProjectile(p, (uint)TickIndex)`.

## Same-side scope — and why

Every prior multi-peer-visibility broadcast in this codebase (P58/P62/P63/
P85/P86) is deliberately same-side only; cross-side ship visibility is its own
still-open roadmap item (Phase 5). This matches that established, deliberate
scoping: the projectile is broadcast only to (a) the shooter's own peer and
(b) same-side teammates who have already received that shooter's
`ExportShip(26)` — the exact `_teammateExportSent`-gated idiom
`BroadcastShipStates`'s teammate loop uses (`Server.cs:539-557`). The gate is
not cosmetic: a teammate who has no client-side object for the shooter can't
meaningfully receive the shot yet, so the broadcast is withheld until that
export lands, exactly like every other teammate send. Cross-side/enemy
projectile visibility is **not** attempted — it needs Phase 5's cross-side
EXPORT first (not built).

## Test evidence (real loopback, no VM)

`tests/Allegiance.Core.Tests/ProjectileBroadcastTests.cs`, mirroring
`TeammatePositionBroadcastTests`/`TeammateLoadoutBroadcastTests` construction:
3 bots, round-robin sides (guaranteed same-side pair + cross-side enemy), a
real `StaticCore`-loaded weapon part mounted on the shooter's real ship, fired
by the server's authoritative sim (`ProjectileBroadcastTests.Real_weapon_fire...`).

1. **Same-side teammate receives a real decoded `ProjectileInstance`(12)**
   with `ProjectileTypeId` == the fired part's projectile type (from the
   loaded `StaticCore`), `Position` within 50 u of the shooter's real position,
   nonzero `Velocity` lying along the firing forward (dispersion 0 ⇒
   `velocity ∥ forward`), and `Lifespan` = the decoded type lifespan.
2. **Cross-side enemy receives none** — asserted after the observer has seen
   ≥4, plus 300 extra pumped ticks with nothing arriving. Same-side scope
   enforced for real, not just documented.
3. **Multiple shots ⇒ multiple distinct broadcasts** — the observer sees ≥2
   then ≥4 distinct messages as the sim keeps firing, proving a genuine
   per-shot event, not a single cached/one-time snapshot.
4. Codec round-trip tests pin the 52-byte width, fmid 12, and
   encode→decode field equality (`ProjectileInstance_round_trips_the_wire`,
   `ProjectileInstance_width_is_52_bytes`).

`/home/domingo/.dotnet/dotnet test tests/Allegiance.Core.Tests/...` →
**2310 passed, 0 failed** (3 consecutive runs, see commit message), vs. the
~2307 P87 baseline: the 3 new projectile tests, no regressions.

## Follow-ups this explicitly does not cover

- **Cross-side/enemy projectile visibility** (the enemy bot test proves it is
  excluded). Needs Phase 5's cross-side `ExportShip` to exist first — same-side
  EXPORT is all that exists today. Then the shooter's `ProjectileInstance`
  broadcast could be extended to peers who hold a cross-side export of the
  shooter; that is a later, separate task.
- **`AllegianceClientCore`'s own consumption** of `FM_CS_PROJECTILE_INSTANCE`.
  This task is `AllegianceCore` only (matching P87's own scope boundary); the
  sibling repo's entity-table client does not yet render projectiles. The Bot's
  decode of fmid 12 is test/observer instrumentation only.
- **Missiles/mines/probes/chaff** (`FIRE_MISSILE`, `FIRE_EXPENDABLE`,
  `MISSILE_DESTROYED`, etc.) — separate Phase 2 items needing their own object
  models, not this task.
- **No change to `Weapon.FireWeapon`'s combat math** — the broadcast is a pure
  side-effect; damage resolution is P87's finished work.
