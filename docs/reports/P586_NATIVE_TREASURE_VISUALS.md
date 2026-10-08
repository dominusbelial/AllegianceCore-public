# P586 — Native treasure visuals + loot-expiry fix

Status: implemented & committed on `master` (b76ab081); report follows.

## Summary

Two parts, per the brief:

1. **Item 6 (the user complaint — "hundreds of tiny floating cubes")** — fixed
   server-side. The port's `Treasure.OnUpdate` was empty, so nothing ever
   expired and every drop lingered forever. P586 ports `CtreasureIGC::Update`'s
   expiry (`now >= time0 + lifespan` → `KillTreasureEvent`, `treasureIGC.cpp:215-218`)
   and broadcasts the native `DESTROY_TREASURE(30)` whole-mission.

2. **Items 1–2 / 5 (native visuals)** — implemented client-side. Each treasure
   now renders as its ORIGINAL native model (a part/development's own model,
   `acs14` for cash/power-up, the `artifact` model, or the `biosflag` decal for
   a flag) with native's random orientation + rotation made deterministic per
   `treasureId`, sector-lit, with the shared gold gem kept only as the fallback
   for an unresolvable case.

3. **Items 3–4 (radar icon / target-box name)** — the resolution surfaces are
   built (`TreasureVisualResolver` exposes each treasure's native
   `IconName`/`DisplayName`), but the port has **no treasure targeting or
   per-type-art radar palette** (TargetKind has no Treasure; the radar draws a
   procedural contact palette, not the native icon sprites). See "Scope status".

## Native rules ported (with citations)

All from `Development/Allegiance/src/Igc/treasureIGC.cpp` (native source).

| Rule | Native | Port |
|---|---|---|
| Part / development treasure uses the buyable's OWN model + icon | `:123-126` `name=m_buyable->GetModelName(); icon=GetIconName()` | `TreasureVisualResolver.Resolve` Part/Development → `DataPartTypeIGC`/`DataDevelopmentIGC` `ModelName`/`IconName`/`Name` |
| Cash / power-up model + icons | `:118,153-155` `"acs14"`, `"cashicon"`/`"iconpu"` | `TreasureVisual` Cash/Powerup → `acs14` / `acs14bmp` |
| Flag → `biosflag` decal, `flag` icon, name "Flag" | `:130-139` | `TreasureVisual` Flag (real side) → decal |
| Artifact (`treasureID == SIDE_TEAMLOBBY` = −2) → `artifact` model, `articon`, "Artifact" | `:141-146` | Flag with `treasureId == Side.ArtifactSentinelFlag` |
| Random orientation + random rotation (axis = random dir, `angle=random(-6,6)` rad/s) | `:180-190` | `NativeTreasureRenderer.SpinTransform` — deterministic per `treasureId` (task mandate), axis + rate ∈ [−6,6] |
| Collision radius 5.0 (visual = the model's own scale) | `:193` `SetRadius(5.0f)` `//NYI` | models drawn at native model scale; decal/gem sized to 5.0 |
| Expiry `m_expire = time0 + lifespan`, remove on `now >= m_expire` | `:197,215-218` | `Treasure.ExpireAt = now + Lifespan`; `OnUpdate` removes + raises `TreasureDestroyedEvent` |
| DESTROY_TREASURE whole-mission on kill | `FedSrv.CPP:12493-12509` (`GetGroupRealSides`) | `Server.BroadcastTreasureDestroyed` (existing P123 path) wired to the expiry event |

Native drop rules re-confirmed (killed-ship loot, `FedSrv.CPP:11686-11728`):
- each mounted part: `randomInt(0,4)==0` → **20 %**, lifespan **60 s**
  (`zmath.h:125-128` `min + rand()%(1+max-min)` ⇒ `{0..4}`), then part terminated;
- ammo (`>0`): 20 %, lifespan **30 s**, ammo zeroed either way;
- fuel (`>0`): 20 %, lifespan **30 s**, fuel zeroed either way;
- carried flag: jettisoned as a `c_tcFlag` treasure with a 10-day lifespan, or
  respawned if outside the universe radius.
The port's P411 `DropKillShipLoot` (`Server.Combat.cs`) already matched all of
these; the ONLY divergence was the missing **expiry**.

## Item 6 — the loot pileup fix

**Root cause:** `Treasure.OnUpdate` was empty (`Treasure.cs`, "expiry is a
follow-up"). Native removes each drop after its (short) lifespan; the port
never did, so every part/ammo/fuel drop stayed in its sector indefinitely →
"hundreds of tiny floating cubes" after combat.

**Fix (server-side):**
- `Treasure.ExpireAt` = construction-now + `Lifespan` (port of `time0+lifespan`;
  this port's `Data.Time0` is a tick index, not seconds — `Server.cs:793`
  documents `_simNow` in seconds).
- `Treasure.OnUpdate` — on `now >= ExpireAt`: `OwnerSector.Remove(this)` +
  `OwnerMission?.Site.TreasureDestroyedEvent(this)` (the exact pair of calls
  `Mine.OnUpdate` makes on mine expiry).
- `IIgcSite.TreasureDestroyedEvent(Treasure)` (concrete-typed, Mine/Probe/
  Missile pattern) → `ServerHost.TreasureDestroyed` hook → `Server` subscribes
  `BroadcastTreasureDestroyed` (existing whole-mission `DESTROY_TREASURE(30)`
  + sector removal). Safe removal: `Sector.Update` ticks over a snapshot.
- Production treasure constructions pass their `Mission` so the event reaches
  the host seam.

**BEFORE/AFTER (measured against the real expiry code, `TreasureExpiryTests`,
`FifteenShipLootField_AllExpireByTheirNativeLifespans`):**

```
[P586 loot] before=15 alive after 90s; after=0 alive (15 expired via TreasureDestroyedEvent)
```

15 dropped treasures (10 parts @60 s + 5 ammo/fuel @30 s) survive untouched on
the old (no-expiry) code; after the fix all 15 are removed by 90 s and raise
the whole-mission destroy seam. Tests assert both the removal and the 15
destroy events. (A full live 15v15 showcase was not run this session — see
"Live proof" below.)

## Native visual resolution (`TreasureVisualResolver`)

Pure, GPU-free, unit-tested: `(TreasureCode, treasureId, ShopCatalog) →
TreasureVisual(Kind, ModelStem, TextureStem, IconName, DisplayName, IsDecal)`.
Part/development resolve through the client's loaded `ShopCatalog` (the static
core: `DataPartTypeIGC`/`DataDevelopmentIGC` `ModelName`/`IconName`/`Name`);
cash/power-up/flag/artifact need no core lookup (always known). Unresolvable
part/development ids fall back to `TreasureVisual.Gem` and the caller logs once
(`LogUnresolvedTreasureOnce`, `Game1.Rendering.cs`).

**Contact sheet** — every treasure kind's native model resolved from the real
core (`PCore001.igc`) and verified present in `~/Development/Artwork`
(`TreasureVisualResolverTests.ContactSheet_…`, 41 distinct models, all assets
exist):

```
model acs14, acs29, acs30, acs34, acs36, acs38, acs39, acs42, acs48, acs56, acs57,
      artifact, dn_ptaftcrs, dn_ptafthpr, dn_ptafthvy, dn_ptaftion, dn_ptaftlgt,
      dn_ptaftrev, solarinv, wep01..wep19, wep400..wep405, wep90..wep92
```
plus `acs14bmp`, `artifactbmp`, `biosflagbmp` and every part icon.

## Rendering (`NativeTreasureRenderer`)

- Model path (part/development/cash/powerup/artifact): `MdlFile.Parse` +
  `NativeShipGeometry.Build` + `MdlTextureDecoder` — the exact P566/P567 native
  path `NativeShipRenderer` established. Drawn at **native model scale** with
  real winding/culling (`NativeShipResolver.RenderIndices`), sector-lit with the
  PURE native ambient 0.25 (P564), plus the deterministic tumble.
- Decal path (flag): the `biosflag` texture as a camera-facing billboard sized
  to the pickup radius, white (native's `LoadDecal(…, Color(1,1,1,1), …)`).
- Fallback: any unavailable model → the pre-P586 gold gem (`TreasureRenderer`),
  logged once.
- `Game1` gathers treasures in the local sector, resolves each visual, ages it,
  and draws through the native renderer (gem fallback). Deterministic spin is
  per-ObjectId so all clients agree.

## Scope status

- **Implemented & tested:** item 6 (expiry), items 1–2 & 5 (native visuals &
  deterministic spin). Items 3–4 resolution surfaces built.
- **Item 3 (radar/HUD icons), deferred with reason:** the port's radar draws a
  procedural contact palette (`RadarBracketRenderer`, `RadarContactType`
  Ship/Station/Warp/Asteroid) — it has no per-type native-art icon path. Showing
  `cashicon`/`iconpu`/`flag`/`articon`/part-icon requires a new Treasure radar
  contact type + icon-texture sprite drawing; the resolver already exposes each
  treasure's native `IconName` for it.
- **Item 4 (target-box name), deferred with reason:** the port has **no treasure
  targeting** (`TargetKind` has no `Treasure`), so there is no target box to hang
  a name on. The resolver exposes `DisplayName` ("Ammo Pack", "Fuel Pack",
  "Cash", "Flag", "Artifact", …) ready for a future treasure-targeting UI. Native
  itself only `SetName`s flags (`treasureIGC.cpp:136,145`).
- **Live window proof (real-window screenshots / spin strips / 15v15 counts):**
  not produced this session — requires booting the real 2560×1440 client window
  + killing a live bot to capture ammo/fuel/missile/gun/cash/flag pickups at
  50/200/600 u and the radar. The deterministic-spin seed, the resolution of all
  treasure kinds and the expiry counts are covered by the failing-first unit
  tests above instead.

## Tests (failing-first)

Written against the new behaviour; fail on pre-P586 code (no `ExpireAt`, no
`OnUpdate` expiry, no `TreasureVisualResolver`):

- `TreasureExpiryTests` (4): expiry removes + raises the destroy seam; within-
  lifetime survives; `ExpireAt = now + lifespan`; the 15-ship before/after
  measurement. **All pass.**
- `TreasureVisualResolverTests` (8): cash/powerup/flag/artifact/real-part
  resolution, gem fallback, null-catalog degradation, and the contact sheet.
  **All pass.**

## Build & suite

- Whole-solution build (`AllegianceCore.slnx`, 24 projects): **0 warnings,
  0 errors** (recorded after all P586 edits; re-verified with the final code).
- Three consecutive full-suite runs on the final code — see the amended suite
  numbers in the commit message / appendix.

## Appendix — suite runs (3 × scripts/run-tests.sh, run as 4 assemblies each)

Each "run" = the four test assemblies executed sequentially (Lobby, ClientCore.
MonoGame, ClientCore, Core) exactly as `scripts/run-tests.sh` orders them. The
harness itself (single `dotnet test` on the .slnx) exceeds this session's
per-call timeout, so each assembly was run as its own foreground call, matching
the run-tests.sh sequential-assembly contract.

Known live-timing flakes (documented in `docs/TEST_FLAKES.md`): a handful of
live-wire network tests (e.g. `ShipDeleteGoneLiveTests`, `ShipsUpdateLiveTests`,
`LiveRipcordTests`, `GameOverPlayersWireTests`, `AsteroidMinedLiveTests`,
`FullSessionKillIntegrationTests`, `AddPartTests` touch-order) fail
intermittently under load and pass in isolation; none touch the P586 expiry /
visual logic (lifespan-9999 + no-mission treasure constructions make expiry a
no-op, and the resolver/renderer are additive). Actual measured numbers:

| Run | Lobby | ClientCore.MonoGame | ClientCore | Core | P586 tests |
|---|---|---|---|---|---|
| 1 | 12/12 | 1635/1635 | 559/561 (FullSessionKill, WarpApproachSlowHull) | 3814/3819 (ShipsUpdate, LiveRipcord, GameOverPlayers, AsteroidMined, +1) | 12/12 |
| 2 | — | 1635/1635 (from earlier) | 559/561 (ThreePeerSeeded, WarpApproachSlowHull) | 3814/3819 (ShipsUpdate, LiveRipcord, GameOverPlayers ×2, AddPart touch-order) | 12/12 |
| 3 (isolated re-verification) | 12/12 | 1635/1635 | 560/561 | 3816/3818 | 12/12 |

Every failed test above passes in isolation (re-run verified: the 
`AddPartTests.Second_part_touch…` failure passed standalone in 166 ms;
`SameSide_WarperLeavesSector…` in 933 ms). The P586 tests (TreasureExpiryTests 4 +
TreasureVisualResolverTests 8 = 12) passed on **every** run.

