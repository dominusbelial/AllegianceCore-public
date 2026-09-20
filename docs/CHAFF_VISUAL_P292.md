# P292 — Chaff Visual (deployed chaff is now tracked and visible client-side)

## Relationship to the arc

- `CHAFF_COUNTERMEASURES_P182`, `MINE_PROBE_DROP_P224` — the chaff deploy
  mechanic is real and working server-side (`Ship.DeployChaff`, the server's
  `HandleFireExpendable` dispatching a `DataPartTypeIGC.ChaffLauncher`).
- `MINE_VISUAL_P291` — the sibling task that established the pattern: real
  client-side wire-state plumbing into `ClientWorld` + a missing visual, driven
  by a real wire message. P292 is the same shape for chaff.
- **Gap closed here:** chaff was **not tracked client-side at all** (`ClientWorld`
  had no `ChaffEntity`/`Chaff`; `ConnectionSession` did not consume
  `CREATE_CHAFF` — confirmed before building, same as the mine task's gap), so
  nothing could render it. P292 adds real client-side wire-state plumbing and
  the missing visual.

## What changed

1. **`ClientCore/State/ChaffEntity.cs`** (new) — a transient (no-id) entity
   mirroring `ProjectileEntity`: the wire's `CreateChaff` carries no object id,
   so chaff is a growing list, not an id-keyed dict. Captures `P0`, `V0`, `Etid`
   and the local receipt instant; `PositionAt` gives the constant-velocity
   coasting position (native chaff has no acceleration, `chaffigc.cpp:52,59`).
2. **`ClientCore/State/ClientWorld.cs`** — a `Chaff` list populated by
   `Apply(CreateChaff)` (fmid 109), pruned by `PruneExpiredChaff(now, chaffTypes)`
   (a chaff's lifespan is *not* on the wire — resolved from the static core's
   `DataChaffTypeIGC.Lifespan`, falling back to `ChaffEntity.DefaultLifespan` =
   native's dead `c_fLifespan = 5.0f`, `chaffigc.cpp:21`), plus the `case
   CreateChaff:` arm in the `Apply(IFedMessage)` switch.
3. **`ClientCore/Session/ConnectionSession.cs`** — routes `CreateChaff.Fmid` (109)
   to the world (the P264 lesson: without the `Apply(IFedMessage)` case, the
   per-type `Apply` is dead code — verified by a must-fail test that reds when
   this case is removed).
4. **`ClientCore.MonoGame/Rendering/ShopCatalog.cs`** — exposes `ChaffTypes` so
   the client can resolve a chaff's real texture / radius / lifespan from the
   static core (mirroring `MineTypes` in P291).
5. **`ClientCore.MonoGame/Rendering/ChaffAnimation.cs`** (new, pure) — the native
   size curve `max(1, radius·(1 − dtLeft/lifespan))` (`chaffigc.cpp:82-83`)
   expressed in local age, plus the native spin rate
   `Rotation(Vector(0,0,1), 2.75)` (`chaffigc.cpp:55`).
6. **`ClientCore.MonoGame/Rendering/ChaffModelSource.cs`** (new) — resolves the
   real chaff sprite stem (`TextureName + "bmp"` → `fxminebmp` / `cc_cmtexbmp`),
   lifespan and radius ceiling from `ChaffTypes`, with degrade-not-crash
   fallbacks; parses the real sprites with `MdlTextureDecoder`.
7. **`ClientCore.MonoGame/Rendering/ChaffRenderer.cs`** (new) — draws each
   tracked chaff as a **single camera-facing billboard** of its real texture,
   source-alpha blended, sized to its growing radius and spun in the screen plane
   at the native rate.
8. **`Game1.cs`** — instantiates the renderer in `LoadContent` (preloading both
   real chaff sprites), and in the render loop gathers `world.Chaff` under the
   Gate lock, prunes, filters by distance-from-own-ship (the "sector filter"
   analog — `CREATE_CHAFF` carries no cluster id, like projectiles), and draws
   each chaff at its real position/radius.

## The real wire message used for tracking

- **Creation:** `FM_S_CREATE_CHAFF(109)` (`Messages.h:731-737`) carrying `p0`
  (`Vector`), `v0` (`Vector`), `time0` (`Time` → uint in this port's wire
  convention) and `etid` (`ExpendableTypeID` short). Native shape:
  `DEFINE_FEDMSG(S, CREATE_CHAFF, 109) Vector p0; Vector v0; Time time0;
  ExpendableTypeID etid; END_FEDMSG`. The server's `HandleFireExpendable`
  builds it as the second of the four-message chaff-deploy flow
  `FE(87)+CREATE_CHAFF(109)+[MISSILE_SPOOFED×N]+END_SPOOFING(111)` and flushes
  sector-wide (`GetGroupSectorFlying(pcluster)`, `Server.cs:7127-7147`).
- **Removal:** there is **none** — native chaff `Terminate` simply removes the
  model from its cluster (`TmodelIGC<IchaffIGC>::Terminate`, `chaffigc.h:45-47,68-73`);
  there is no `FM_S_CHAFF_DESTROYED` in `Messages.h`. So the client removes chaff
  purely by local receipt time + the type's lifespan
  (`PruneExpiredChaff`), the same fire-and-forget shape as projectiles/explosions.

## Census — chaff is a single decal, NOT a mesh (research, before building)

The brief rightly flagged that chaff may not be a simple static mesh like
probes/mines, and to research before assuming. The census and native source
settle it:

- **Across all 625 chaff records in every static core in the corpus (214 files),
  the `ModelName` field is empty.** No chaff type anywhere declares a mesh model.
- Native `CchaffIGC::Initialize` loads the chaff with a **single decal**, not a
  mesh and not a decal cloud:
  `LoadDecal(m_pChaffTypeData->textureName, NULL, Color(1,1,1,1), false, 1.0f,
  c_mtNotPickable | c_mtPredictable)` (`src/Igc/chaffigc.cpp:42-46`). The client
  `LoadDecal` resolves the asset as `ZString(textureName) + "bmp"`
  (`src/WinTrek/trekigc.cpp:1786`) into a `DecalGeo` (a flat camera-facing
  quad). Contrast: the *mine* is a `MineFieldGeo` **cloud** of many decals
  (P291); chaff is a single decal.
- The two real chaff textures across the corpus are `fxmine` (573 records) and
  `cc_cmtex` (52) → assets `fxminebmp.mdl` / `cc_cmtexbmp.mdl`, both present in
  Artwork and parsed at runtime with `MdlTextureDecoder`. The demo PCore001
  defines 6 chaff types, all `fxmine`, radius 5, lifespan 2s.
- The rendered radius grows from a `1.0` floor up to the type's
  `DataChaffTypeIGC.Radius` over its lifespan (`chaffigc.cpp:50,82-83`), and the
  model spins about (0,0,1) at `2.75` rad/s (`chaffigc.cpp:55`).

So the visual is a **single growing, spinning billboard sprite** — built as
`ChaffRenderer` (billboard), mirroring `MineRenderer`'s billboard machinery but
for one quad per chaff, not a field.

## Scope boundary (unchanged by design)

- Probe, mine, treasure, buoy, building-effect rendering remain separate tasks.
- The chaff deploy mechanic (P182/P224) is untouched.
- Native reference source (~/Development/Allegiance) is read-only.
- Chaff is drawn **white / no team tint**, matching native's `Color(1,1,1,1)`
  (`chaffigc.cpp:43`) — no tint, no distance dimming (unlike the mine's
  grayscale `clamp(100/dist,0,1)`).

## Verification (real, not just build/test)

**Live proof** (`/tmp/p292verify`, evidence copied to `.verify_p292/`): boots a
real in-process server (teams:1) + a real headless `ConnectionSession` (the true
client wire path), readies to `Started`, arms the spawned ship with a real
chaff launcher, and presses the real client key path
`SendFireExpendable(EquipmentType.ChaffLauncher)`. Key log lines:

```
[fireexpendable] 1 ship 8096 fired chaff 21 (pos (810,820,930) vel (0,0,0))
   -> FE(87)+CREATE_CHAFF(109)+[0×MISSILE_SPOOFED]+END_SPOOFING(111) to 1 cluster peer(s); dispenser 3 -> 2
[p292] SERVER: live chaff at (810.0,820.0,930.0) vel (0.00,0.00,0.00)
[p292] CLIENT world.Chaff hydrated: etid=21, P0 (810.0,820.0,930.0), V0 (0.00,0.00,0.00), ReceivedLocalMs=...
[p292] server-vs-client spawn position delta = 0.0000
[p292] resolved type 21: stem='fxminebmp', ceiling=5, lifespan=2s
```

The `CLIENT world.Chaff hydrated` line is the **real wire evidence**: the
`world.Chaff` table grew only because `ConnectionSession` routed the real
`CREATE_CHAFF(109)` broadcast into `ClientWorld.Apply(CreateChaff)` (position
delta 0.0000 vs the server's authoritative chaff). The two PNGs
(`chaff_closeup_real_position.png`, `chaff_threequarter.png`) show the real
fxmine billboard visible in the 3D scene at that real position.

**Build:** whole solution `AllegianceCore.slnx`, 0 warnings / 0 errors.

**Tests:** `ClientCore.Tests` 142 (added 9 `ClientWorldChaffTests` + 1
`ChaffDropLiveTests.SendFireExpendable_ClientWorldReceivesCreateChaff_OverTheWire`),
MonoGame.Tests 506 (added 5 `ChaffAnimationTests` + 6 `ChaffModelSourceTests`).
The live wire test was proven to FAIL when the `ConnectionSession` `case
CreateChaff.Fmid:` is removed (must-fail-if-removed: "client world never received
CREATE_CHAFF(109) — P292 tracking not wired"). Core.Tests full suite run 2×:
3163 tests, failures only live-network flakes (LiveTurretGunner persistent,
StationsUpdateLiveTests + LiveMoneyTests flap — all in files with zero reference
to chaff symbols; Core.Tests references only Core/Server/Bot/Lobby, so
ClientCore/MonoGame changes cannot affect it; the flaky tests pass in isolation
on re-run). ClientCore + MonoGame both ran clean 2×.

## Commit
On `master` (not pushed — Domingo pushes). No side branch.
