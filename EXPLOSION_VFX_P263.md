# EXPLOSION_VFX_P263 — real explosion visual effects (ships, missiles, mines, stations)

Closes the visual half of the explosion story in the MonoGame client. The port
already had the real server-side blast-radius **simulation** (P97,
`src/Allegiance.Core/Sim/Explosion.cs` → `Sector.ResolveExplosion`) — but there
was **no client rendering** of any blast anywhere in
`src/Allegiance.ClientCore.MonoGame/Rendering/`. This stage renders real
explosions in the MonoGame client using the **real original-game explosion
sprite artwork**, scaled by the real native blast category, with a real (not
instant, not indefinite) duration, triggered by the real server-side sim events.

## The key findings

1. **Real assets exist — no procedural substitute was needed.** The task asked
   us to check the artwork corpus first. The exact files native loads
   (`artwork.h:198-203` → `exp20..exp25bmp.mdl` fireball films + `fx18bmp.mdl`
   shock wave) are all present at the artwork root. They are RGB565 (bitcount=16),
   so the P241 `MdlTextureDecoder` decodes them directly.
2. **Native derives the visual *category* from WHAT died** (not from the blast
   radius): the 7-type `ExplosionType` table (`igc.h:36-45`) and the
   `WinTrekClient::AddExplosion` switch (`trekigc.cpp:95-177`) fix each
   category's sprite count, fireball radius, shock-wave radius and tint. The
   scale passed in is only the *audio* size tier (`trekigc.cpp:94-104`). So the
   rendered size/duration is category-driven — a fighter death is small/short, a
   station death is huge/long.
3. **No wire explosion broadcast existed.** The P97 `Explosion` is pure
   in-memory sim state (its own remarks say "never serialized"); the client is a
   server-authoritative pure renderer with no static core, so it cannot derive
   the category client-side. A **small addition was required**: a new
   server→client `EXPLOSION_EVENT(207)` carrying the category + position +
   velocity, emitted from the four destruction broadcasts that the P97 blast sim
   already fires.

## What landed

| Piece | Location | Kind |
|---|---|---|
| `ExplosionType` constants | `src/Allegiance.Core/Sim/ExplosionType.cs` | 7 native categories (`igc.h:36-45`) |
| `ExplosionEvent`(207) codec | `src/Allegiance.Core/Wire/ExplosionEvent.cs` | Server→client VFX message (40 B fixed) |
| `HabmFighter = 0x80` | `src/Allegiance.Core/Data/DataHullTypeIGC.cs` | Fighter-class split (`igc.h:733`) |
| `ExplosionPreset` / `ExplosionPresets` | `src/Allegiance.ClientCore/State/ExplosionPreset.cs` | The native `AddExplosion` table, per category |
| `ExplosionSampler` | `src/Allegiance.ClientCore/State/ExplosionSampler.cs` | Pure elapsed→sprite/shock-wave timing (`ExplosionGeo`) |
| `ExplosionEntity` + world wiring | `src/Allegiance.ClientCore/State/ExplosionEntity.cs`, `ClientWorld.cs`, `Session/ConnectionSession.cs` | Ingests 207, prunes by real duration |
| `ExplosionAssets` | `.../MonoGame/Rendering/ExplosionAssets.cs` | Decodes + slices the real `.mdl` strips into frames |
| `ExplosionRenderer` | `.../MonoGame/Rendering/ExplosionRenderer.cs` | Draws fireballs + expanding shock wave additively |
| Server emission (4 sites) | `src/Allegiance.Server/Server.cs` | Broadcasts 207 from KillShip / MissileDestroyed / MineDestroyed / StationDestroyed |
| Tests | `tests/.../ClientWorldExplosionTests.cs` (+25), `ExplosionLiveTests.cs` (+1 live), `.../MonoGame.Tests/ExplosionAssetFramesTests.cs` (+7) | 33 new |

## Native citations (the exact categories/sizes/durations)

`ExplosionType` constants — `src/Igc/igc.h:36-45`:

| Category | id | count | radiusExplosion | radiusWave | tint | shock wave | sprite film(s) |
|---|---|---|---|---|---|---|---|
| Mine | 1 | 4 | 25 | 50 | (154,130,190) | yes | exp24 |
| Missile/Projectile | 2 | 6 | 1 *(hack)* | — | — | no | exp20 |
| SmallShip/Probe | 3 | 12 | 35 | 70 | (200,130,50) | yes | exp25, exp22 |
| BigShip | 4 | 16 | 165 | 250 | (200,130,50) | yes | exp25, exp22 |
| SmallStation | 5 | 16 | 100 | 800 | (220,130,50) | yes | exp25, exp23 |
| Asteroid | 6 | 5 | 150 | — | — | no | exp24 |
| LargeStation | 7 | 24 | 350 | 1000 | (200,130,50) | yes | exp25, exp23 |

(The count/radius/colour columns are the `AddExplosion` switch,
`trekigc.cpp:95-177`; the sprite-film assignments are `trekigc.cpp:2530-2560`;
the film → frame counts come from the real strips via `AnimatedImage` slicing,
`engine/image.cpp:588-625`.)

**Duration** — native `ExplosionGeo` (`effect/explosion.cpp`): decal *i* starts
at `i * 0.25`s (`:113`) and animates through the frames of
`films[i % films.Length]` at 20 fps (`frame = (int)(time * 20.0f)`, `:183`),
removed when `frame >= GetCount()` (`:185`). The shock wave fades quadratically
(`bright = 1 − t²`, `:155`) and expands linearly (`scale = t * radiusWave`,
`:158`) over its 1 s life. Computed totals (see the preset): Mine 1.55 s,
Missile 2.25 s, SmallShip 4.25 s, BigShip 5.25 s, SmallStation 5.65 s,
Asteroid 1.80 s, LargeStation 7.65 s.

## How it is triggered (the P97 tie-in)

The P97 blast sim (`Sector.ResolveExplosion`) already drives the destruction
events this port broadcasts: KILL_SHIP(105), MISSILE_DESTROYED(83),
MINE_DESTROYED(84), STATION_DESTROYED(59). Those broadcasts are the 
server-authoritative replacement for native's `IclusterSite::AddExplosion`
callback (`clusterIGC.cpp:407`). This stage adds `SendExplosionEvent` at each of
the four existing broadcast handlers, deriving the category exactly as native
derives it at the corresponding client wire site:

- **Ship kill** → `BroadcastKillShip`: `victim.HullType.HasCapability(HabmFighter)
  ? SmallShip(3) : BigShip(4)` — native `appmsg.cpp:2853-2857`.
- **Missile** → `BroadcastMissileDestroyed`: `Missile(2)` — native `missileigc.cpp:531`.
- **Mine** → `BroadcastMineDestroyed`: `Mine(1)`, scale literal 1.5 — native `shipIGC.h:2024`.
- **Station** → `BroadcastStationDestroyed`:
  `station.Type.HasCapability(SabmFlag) ? LargeStation(7) : SmallStation(5)` —
  native `appmsg.cpp:525-528`.

Because the ship/station category needs the hull/station capability (data that
lives server-side, and which the thin client has no static-core access to), the
category is derived **server-side** and carried on the wire — the faithful
split of native's single-client `AddExplosion` across this port's
server-authoritative architecture.

## Real asset vs procedural

**Real assets, used faithfully.** `exp20..exp25bmp.mdl` + `fx18bmp.mdl` are read
from `ALLEGIANCE_ARTWORK_PATH` at runtime (the established `MissileModelSource`
convention), decoded via `MdlTextureDecoder`, sliced into their square frames
exactly as native's `AnimatedImage(nRows=0)` does (`engine/image.cpp:588-625`),
and drawn as **additive** billboards (SrcAlpha/One) so the sprites' black
surround vanishes and only the glowing fireball reads — the same reason native
renders them as additive decals. A MonoGame test (`ExplosionAssetFramesTests`)
pins the declared frame counts to the real asset output.

The one documented rendering divergence: native orients the shock-wave ring in
the emitter's plane (`forward`/`right`); this port draws it camera-facing
(billboarded) so it always reads as an expanding ring — therefore the wire omits
forward/right (see `Wire/ExplosionEvent.cs` remarks).

## Verification

- **Whole-solution build**: `dotnet build AllegianceCore.slnx -c Debug` — 0
  warnings, 0 errors.
- **Tests**: ClientCore 116 (+26), MonoGame 331 (+7), Core unchanged. Full suites
  run twice; only the pre-existing live-network flakes appear (see below).
- **Live proof (required)** — real server + real `ConnectionSession` + real sim
  kill, in `.verify_p263/`:
  - The server log (`server_live_run.log`) shows the real kill → real 207 path:
    `[combat] ship 8096 killed by (none) … — KILL_SHIP(105)` then
    `[explosion] type 3 cluster 1051 scale 2 at (750,760,940) -> EXPLOSION_EVENT(207) -> 1 peer(s)`,
    and the peer's `world.Explosions` ingests it (type 3 = a fighter hull death →
    `c_etSmallShip`, the "ship death" category).
  - `real_wire_shipdeath_appear/expand/late/cleared.png` — the real captured
    blast at ages 0.05 s (a single ember, just started) → mid (a glowing,
    animating fireball) → near-end → past its 4.25 s duration (**pure black —
    cleared**, not lingering). The VFX demonstrably *appears, animates/expands,
    and clears* over its real duration.
  - `bigship_appear/expand/late/cleared.png` — the `c_etBigShip`(4) tier over
    its 5.25 s duration, for a second category.
  - `scale_contrast.png` — `SmallShip`(3) and `BigShip`(4) at the SAME elapsed
    time, side by side: the BigShip is ~3–4× the footprint of the SmallShip —
    the two blast categories are **visually distinct in scale**, exactly the
    native tier table.
  - The 33 new tests include a discriminating **live test** (`ExplosionLiveTests`)
    that boots a real server + session and asserts a real kill delivers a real
    207 into the peer's world; disabling the ship-kill `SendExplosionEvent`
    makes it go red (`no EXPLOSION_EVENT arrived over the wire`) — proven.

## Exclusions / scope boundary

- **No second impact-spark system.** Trivial impact feedback was folded into the
  same mechanism only where it IS the same mechanism (a missile/round blast uses
  the missile/projectile category); no separate weapon-spark system was invented.
- **Shield effects** — native has none (gauge-only, confirmed this session), so
  nothing to build; untouched.
- **Native reference** (`~/Development/Allegiance`) read-only; only read, never
  modified.
- **No explosion audio** — native also plays large/medium/small explosion sounds
  keyed on the same `scale` tier; the `ExplosionEvent` carries `scale` so a
  future audio stage can key off it, but sound is out of this stage's visual
  scope.
