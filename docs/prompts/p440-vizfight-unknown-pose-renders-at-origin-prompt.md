# P440 — vizfight attach renders known-sector, unknown-pose ships at [0,0,0]

## Why this task exists

This is an open item carried from P433, whose root cause was confirmed then
but deliberately not fixed because P433 was editing the same file. In
`vizfight attach` mode, a ship whose **sector is known but whose position
is not** gets drawn at the sector origin `[0,0,0]`. That is a phantom ship
sitting at a spot where nothing real is.

The exact chain, by direct source read:

1. `ClientWorld.Apply(ShipStatusMessage)`
   (`src/Allegiance.ClientCore/State/ClientWorld.cs:930-940`) upserts the
   ship entity and sets `e.SectorId = status.SectorId`. `SHIP_STATUS(90)`
   carries no pose, so for a teammate the spectator has never received a
   pose update for (not in its own cluster, so no `HeavyShipsUpdate`/
   `SingleShipUpdate`), `ShipEntity.Latest` stays **null** while
   `SectorId` is real. The only `Latest` writes are `ClientWorld.cs:841`
   (SET_CLUSTER), `:918` and `:985` (pose updates).
2. `SpectatorFrame.BuildTickFrame`
   (`src/Allegiance.ClientCore/State/SpectatorFrame.cs:~70-90`) emits every
   `world.Ships` entry with `pos = Vec(latest?.Position)` and
   `realSector = e.SectorId`. The helper `Vec`
   (`SpectatorFrame.cs:~190`) maps `null` → `[0f, 0f, 0f]`, so the frame
   says "this ship is at the origin of sector N."
3. The frontend (`tools/vizfight/wwwroot/index.html`) believes it.
   `shipMatchesFocus` (`:364-367`) puts the mesh in the scene when
   `realSector === focusId`, and `updateShipVisual` (`:488-489`) sets
   `mesh.position` from `s.pos`. `window.__vstate` (`:815-825`) also reads
   `s.pos` directly.

A related variant to check, **not assume**: a ship that warped keeps its
last `Latest` pose from the *previous* sector while `SHIP_STATUS` updates
`SectorId` to the new one. It would then render at a stale, wrong-sector
coordinate in the new sector. Determine whether this really happens. P370's
`IsGone`/`IsNotVisible` handling may already cover it.

## Scope

1. **Tell the frontend when a pose is genuinely known.** In
   `SpectatorFrame.BuildTickFrame`, add a `hasPose` field to each ship entry
   in the tick frame's ships array. It is `false` exactly when there is no
   real pose (`Latest` null), and always `true` for `ExtraShip` entries,
   which always carry a real position. Keep `pos` in the schema: it is
   additive, and classic/explore modes and existing tests rely on the
   shape. Don't change `Vec`'s behaviour for projectiles and other models.
2. **Frontend: a ship with `hasPose === false` is tracked but not
   rendered.** Keep it in `shipState` (and in the `sectorCensus` count,
   which is computed server-side in `tools/vizfight/Program.cs` ~2440-2532
   and must stay unchanged). Do not add its mesh to the scene. Leave it out
   of camera framing/follow (`shipInFocusedSector`, `followShips`,
   `reframeCamera`) and skip `mesh.position` updates for it. When a later
   tick brings `hasPose: true`, it appears normally through the existing
   per-tick scene-membership re-check (`index.html:~675-685`). A missing
   `hasPose` field (classic/explore frames) must mean "has pose", so those
   modes keep working exactly as before.
3. **Investigate the warp stale-pose variant** from the section above. If
   it is real and small (for example, treat a pose as unknown when
   `Latest`'s sector disagrees with `SectorId`, if such a signal exists),
   fold it into the same `hasPose` rule. If it isn't small, document it as
   a named follow-up and don't fix it here.

## Explicitly out of scope

- Any server-side change: sending poses for more ships, or changing
  SHIP_STATUS/HeavyShipsUpdate fan-out. Unknown pose is a real state and
  the fix is not to fake a position.
- The MonoGame client. Check whether its renderer has the same null-Latest
  fallback and note it in the report, but don't change it here.
- vizfight's bot-driving code (`DriveEnemyContingent`, `EngageEnemy`,
  `AttachEnemyFlyTo`) and the allegiance-mcp split. See the
  vizfight/MCP architecture decision; this is a pure visualizer fix.
- Other in-flight tasks: P438 (client JOIN_SIDE handler) and P439 (mission
  rotation after Over).

## Verification

- Unit test in `tests/Allegiance.ClientCore.Tests` (same style as the
  existing SpectatorFrame tests). A world with a ship that has `SectorId`
  set via a real `ShipStatusMessage` apply and no pose → the tick frame
  entry has `hasPose: false`. After a pose update → `hasPose: true` with
  the real `pos`. `ExtraShip` → `hasPose: true`.
- Frontend: if the existing live/headless vizfight tests drive `__vstate`,
  extend them to assert a no-pose ship is `inScene: false`. Otherwise, run
  a real `vizfight attach` against a local server and show from
  `__vstate`, or the served frame JSON, that no ship sits at `[0,0,0]`
  in the scene while its pose is unknown. Record the evidence in the report.
- Build `/home/domingo/.dotnet/dotnet build -c Release tools/vizfight`
  **directly**, plus a whole-solution build. Both must have 0 warnings and
  0 errors. Bare `dotnet` is a broken snap stub on this box, so always use
  the full path.
- Full suite with `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean
  runs. Only the pre-existing documented live-wire timing flakes are
  acceptable, and each one seen must be named.

## Deliverables

1. The fix and tests, committed on **`master`**, not pushed (Domingo
   pushes). Check `git branch --show-current` before committing.
2. Report doc `docs/reports/P440_VIZFIGHT_UNKNOWN_POSE_ORIGIN.md`. Cover the
   confirmed chain, the `hasPose` contract, the finding on the warp
   stale-pose variant, and the MonoGame check.
