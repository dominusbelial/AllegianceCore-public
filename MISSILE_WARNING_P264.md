# MISSILE_WARNING_P264 — missile-lock-ON-YOU warning sound

Stage: P264. Scope: a real audio cue when an **in-flight enemy missile's target is
the local player's ship** — the reverse of P230 (which covered your OWN missile
locking onto a target).

## Relationship to native

Native's real mechanism is purely a sound cue, driven client-side by walking the
player's cluster. `WinTrek/trekigc.cpp:5025-5071` (inside the client's per-frame
sound update):

- For every model in the player's cluster (`pcluster = GetCluster()`, iterating
  `pcluster->GetModels()`), if `GetObjectType() == OT_missile` and
  `pmissile->GetTarget() == trekClient.GetShip()->GetSourceShip()` → set
  `bPlayMissileWarning = true`, and keep `fBestLock = max(GetLock())` among those
  missiles (5043-5048).
- If `bPlayMissileWarning && player is in a cluster` (5054): if
  `m_psoundMissileWarning` is not yet allocated, start it (5056-5063), then every
  frame `SetPitch(0.75 + fBestLock/2)` (5065) — so the intensity dimension is the
  **pitch**, ramping 0.75 → 1.25 as the best lock goes 0 → 1.
- Otherwise (no in-cluster missile targeting the player, or the player is not in a
  cluster) stop + NULL the sound (5067-5071). It is likewise cleared when docked at
  a station in the combat screen (5088-5093) and when not in a cluster (5104-5109).
- The member `m_psoundMissileWarning` is initialised to NULL at `trekigc.cpp:2587`.
- The sound is `missileLockSound = GainSound(SFXGain, LoopingSound(ImportWave("missilelockonme")))`
  (`Artwork/sounddef.mdl:364`) — i.e. the **`missilelockonme`** looping wave
  (distinct from the own-lock `missilelock` tone P230 used at sounddef.mdl:365).
  Both `.ogg` assets exist in the artwork tree.

So the warning is **binary** (armed while any in-cluster missile targets you,
disarmed when none / you leave the cluster) plus a **continuous pitch ramp** from
the best lock among those missiles. It is never re-fired per frame — the loop is
started once on arm and only re-pitched each frame.

## Was a server-side addition needed? Yes.

The port's server-side `Sim.Missile` carries the real `Target` and `LockFraction`
(P179/P181), so the server already knows everything the warning needs. But the
client cannot derive "this missile's target is me" from data it already receives:

- The missile wire the client consumes — `FIRE_MISSILE(70)` (identity/type/cluster/
  spawn pose), `MISSILE_UPDATE(205)` (per-tick position/heading), `MISSILE_DESTROYED(83)`
  (id) — carries **no target** (`State/MissileEntity.cs` has `MissileId/TypeId/
  ClusterId/Position/Forward` only).
- The MonoGame client is a pure renderer of server-authoritative state with **no**
  static core, so it cannot infer a missile's lock/target locally the way native's
  client (which ran its own IGC sim) did.

Therefore a small server-side addition is required, following the established
owner-only per-tick extension precedent (P230's `MissileLockUpdate` 204, P255's
`OwnShipMiningUpdate` 206): the server computes the armed state + best lock and
flags "this missile's target is me" explicitly to the target's owner.

## Implementation

- **`src/Allegiance.Core/Wire/MissileTrackWarningUpdate.cs`** (FMID 208, above
  native's real max 200 and the port's prior extensions 204-207):
  `{cbmsg,u16}{fmid=208,u16}{bTracked@4,byte}{bestLockPercent@5,byte}` = 6 B fixed;
  `From(bool tracked, float bestLock)` quantizes the lock to div-240; round-trip by
  fields (OwnShipMiningUpdate style). No native `DEFINE_FEDMSG` to be byte-faithful
  to (native's client never transmitted this).
- **`src/Allegiance.Server/Server.cs` → `BroadcastMissileTrackWarning()`** (per-tick,
  sim thread): for each seated peer, iterate `ship.Sector.Models` for
  `Missile mis && mis.Target == ship`, take max `LockFraction`, and send
  `MissileTrackWarningUpdate.From(tracked, bestLock)` to **that target's connection
  only** (owner-only, NOT same-side-gated — the warning goes to the target's owner
  regardless of the launcher's side). Native's in-cluster walk maps to the port's
  sector membership (a missile's target is only ever in the missile's own sector —
  homing drops it otherwise, `Missile.OnUpdate`).
- **`src/Allegiance.ClientCore/State/MissileTrackState.cs`**: `readonly record
  struct { bool IsTracked; float BestLock }`, `Clear = (false, 0)`.
- **`ClientWorld`**: field + `MissileTrack` property + `Apply(MissileTrackWarningUpdate)`,
  routed through the single `Apply(IFedMessage)` dispatch hub (see trap below).
- **`ConnectionSession`**: receive case 208.
- **MonoGame**:
  - `Audio/MissileWarningController.cs` — pure edge tracker: `Arm` on the
    not-tracked→tracked edge (once), `Pitch` each armed frame, `Disarm` on the
    tracked→not-tracked edge; `PitchFor(b) = 0.75 + clamp(b,0,1)·0.5` (native's
    SetPitch).
  - `Audio/SoundManager.cs` — `_missileWarning` loop instance for
    `Sounds/missilelockonme`; `UpdateMissileWarning(cmd)` performs Arm (EnsurePlaying
    + pitch + volume 0.6) / Pitch / Disarm; disposed in `Dispose()`.
  - `Game1.cs` — `UpdateMissileWarning()` reads `world.MissileTrack`, runs the
    controller, replays the command against SoundManager, and logs
    `[monogame] audio: incoming missile warning ARMED / CLEARED`.
  - `Content/Sounds/missilelockonme.ogg` copied from Artwork + `Content.mgcb` entry.

## Scope boundary

- Your OWN missile-lock feedback (tone/text/target-box) — P230, untouched.
- No new visual warning: native's mechanism is purely the sound cue per the citation
  above, so no UI element was added.
- Native source `~/Development/Allegiance` — read-only, not modified.

## Architecture trap uncovered (reusable)

`ClientWorld.Apply(IFedMessage)` is the **one** dispatch hub — a `switch` on the
concrete decoded type that routes to each per-type `private Apply(X)`.
`ConnectionSession`'s `_world.Apply(X.Decode(m))` binds to `Apply(IFedMessage)`
(every codec is an `IFedMessage`), **not** to a per-type overload. Adding only a
`private Apply(MissileTrackWarningUpdate)` is therefore dead code unless you also add
a `case X: Apply(x);` inside the hub's switch — otherwise the message is silently
dropped (observed: the decode fired, the instrumentation in the per-type Apply never
ran, and the world never updated). Both the per-type `Apply` and the hub routing case
were added here.

## Verification

### Live proof (required)

1. **Live wire test** (`MissileTrackWarningLiveTests`, ClientCore +1): a real
   in-process server places an ENEMY attacker that fires a homing missile at the
   local player's ship (`attacker.FireMissile(type, mission.LastUpdate, target:
   placedLocal, lockFraction: 0.6f)`); the real `ConnectionSession` for the target
   receives `MISSILE_TRACK_WARNING(208)` → `ClientWorld.MissileTrack` arms with
   `BestLock == 0.6`; destroying the missile (`missile.OwnerSector.Remove`) clears it.
   A 40 ms pump keeps the receive thread in pace so the armed frame is observed
   (P230 lesson). Must-fail verified on both the server broadcast call and the
   client hub routing case.

2. **Live audio proof** (real device): a /tmp MiniGame harness (real in-process
   server + real `ConnectionSession` target + the real `SoundManager` loaded via
   Content on the live OpenAL device) drives the real `MissileTrackWarningController`
   from the real `world.MissileTrack` and reflects the private `_missileWarning`
   `SoundEffectInstance`. Result:
   - `SoundManager.IsLoaded=True`
   - after the enemy missile homes on the local ship: `world.IsTracked=True | loop
     state=Playing pitch=1.150 vol=0.60` — pitch **1.150 == 0.75 + 0.8/2** (native's
     SetPitch with fBestLock 0.8), volume 0.6 = MissileWarningVolume;
   - after the missile is destroyed (sector remove): `loop state=Stopped` — cleared.
   This is the exact "warning armed at the right moment and cleared when the missile
   was destroyed" the brief requires.

### Whole-solution build

`dotnet build AllegianceCore.slnx -c Debug`: **0 warnings, 0 errors** across all 18
projects (incl. the new `missilelockonme.ogg` content).

### Full test suite (2 runs)

| Suite | Run 1 | Run 2 | Delta |
|---|---|---|---|
| Allegiance.Core.Tests | 3099 / 3100 | 3099 / 3100 | +6 wire (only `LiveTurretGunnerBindingTests` flake) |
| Allegiance.ClientCore.Tests | 117 / 117 | 117 / 117 | +1 live |
| Allegiance.ClientCore.MonoGame.Tests | 338 / 338 | 338 / 338 | +7 controller |

Baseline (pre-P264): Core 3094, ClientCore 116, MonoGame 331. The single Core
failure in both runs is the documented pre-existing `LiveTurretGunnerBindingTests`
timing flake (its file references none of this stage's symbols; documented in the
port workflow skill). New tests: `MissileTrackWarningUpdateWireTests` (6),
`MissileWarningControllerTests` (7), `MissileTrackWarningLiveTests` (1).

## Commits

Landed on `master` (not pushed; Domingo pushes).
