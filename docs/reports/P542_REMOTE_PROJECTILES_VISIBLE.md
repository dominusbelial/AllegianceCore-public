# P542 — other ships' projectiles are invisible in the MonoGame client: root cause found and fixed

Date: 2026-10-04. Branch: `master` (not pushed).

## Symptom

Domingo (2026-10-04, 5v5 MCP bots on 221): the bots maneuvered and tried to
dogfight, "looks like they have no weapons/ammo?" — **no bot tracers visible**,
even though the 221 server log proved the bots really fire (528
`[projectile] ship N fired -> PROJ_INST(12) type 85 …` lines, the Adv Scout's
default `PW A-B Omega 2` gun) and the server really emits `PROJECTILE_INSTANCE`.
Domingo flew the same hull and default gun as the bots.

## Pipeline and where the shots were lost

The intended hops (all verified live):

1. **Server send** — `src/Allegiance.Server/Server.Combat.cs`
   `BroadcastProjectileInstance` (≈:48-105): shooter's own peer + same-side
   teammates holding the shooter's ExportShip + cross-side viewers (P375),
   excluding cloak-hidden shooters. Logs `-> n peer(s)`.
2. **Client receive** — `src/Allegiance.ClientCore/Session/ConnectionSession.cs:816-819`
   → `ClientWorld.Apply(ProjectileInstance)` (`State/ClientWorld.cs:2144`).
   ⚠️ **P462 own-echo drop** (`Game1.FlightInput.cs:2222,2301-2302`,
   `Flight/OwnShotEcho.cs:315`).
3. **Client draw** — `Game1.Rendering.cs:~756-790`: distance visibility
   (5900), per-type style (`ProjectileStyle.For`, P465b), near-camera cull.

The **loss happened at hop 2** — the P462 own-echo de-dup.

## Root cause

`ClientWorld.Apply(ProjectileInstance)` consulted the own-echo drop predicate
**for every incoming `PROJECTILE_INSTANCE`, regardless of who fired it**, and the
predicate (`OwnShotEcho.ShouldDropServerEcho`, `OwnShotEcho.cs:315`) matches on:

1. the round's projectile **type** equals one we recently emitted,
2. it spawned within `FireOriginRadius` (**150 u**) of our server position,
3. it arrived within `EchoWindowMs` (400 ms) of that local round.

Nothing in that rule checks **who fired**. When the local player flies the same
hull + default gun as a nearby enemy and is *also* firing (so a same-type local
round exists in the echo window), every enemy/teammate round of that type spawned
within 150 u is dropped as "our own echo" — the bots' shots vanish before they
reach the renderer. That is exactly Domingo's 221 scenario (Adv Scout ×
`PW A-B Omega 2`, dogfighting at point-blank range).

Contributing gap: the `PROJECTILE_INSTANCE` wire message carried **no
shooter/launcher id** (`src/Allegiance.Core/Wire/ProjectileInstance.cs`), so the
client had no way to tell "our echo" from "another ship's round of the same type".

## Native citation

Native Allegiance fires on the client in the shared IGC sim
(`CshipIGC::Update → CweaponIGC::FireWeapon`, `src/Igc/shipIGC.cpp:312-340` /
`weaponIGC.cpp:142-322`): the client's OWN trigger creates the projectile object
locally at the trigger frame. De-duplication of the server echo is **per the
locally-created projectile**, never "any round of the same type near me" — the
P462 report itself claimed (wrongly) that the type+radius+window rule "never
mis-de-duped" a foreign round, which P542 corrects. This port resolves fire
server-side and simulates no client projectile, so the faithful realization of
"de-dup per own shot" is to carry the launching ship id on the wire and gate the
predicate on it.

## Fix

1. **`Wire/ProjectileInstance.cs`** — add `short LauncherId` (the launching
   `Ship.Id`, stamped in `FromProjectile` from `Projectile.Launcher`; `-1` when
   the launcher is not a ship). Wire width 52 → 54 B.
2. **`Server.Combat.cs`** — `BroadcastProjectileInstance` now emits the launcher
   id via `FromProjectile` (server side unchanged in audience logic).
3. **`ClientWorld.Apply(ProjectileInstance)`** (`ClientWorld.cs:2144`) — classify
   the round's launcher (own / teammate / enemy via
   `ClassifyLauncher`, using `MyShipId`/`MySideId`/`ShipEntity.SideId`) and consult
   the drop predicate **only for OUR OWN shot** (`rel == Own`). A foreign round —
   even same-type at point-blank — is never dropped; it renders. The P462
   "own echo dropped / unmatched falls back" behavior for our own shots is
   preserved unchanged.
4. **Diagnostics** (the task's measurement): `ClientWorld.ProjectileDiagnostics`
   (`Diag`) counts, split by shooter relation, each received
   `PROJECTILE_INSTANCE` as it passes received → dropped-as-own-echo →
   radius/camera-culled → drawn. `DiagnoseDraw` is the shared draw-decision pass
   (identical radius + near-camera cull + style rules, factored into a new
   `State/ProjectileVisibility.cs` used by both the renderer and the headless
   path; `ProjectileCameraCull` and `Game1`'s constants now delegate to it). The
   MonoGame client emits a once-per-second `[proj-diag]` line
   (`ALLEGIANCE_PROJ_DIAG=1`, only while active) via `Game1.Rendering.cs`.

Files: `src/Allegiance.Core/Wire/ProjectileInstance.cs`,
`src/Allegiance.ClientCore/State/ClientWorld.cs`,
`src/Allegiance.ClientCore/State/ProjectileEntity.cs` (+`EverDrawn`),
`src/Allegiance.ClientCore/State/ProjectileVisibility.cs` (new),
`src/Allegiance.ClientCore.Game/Game1.Rendering.cs`,
`src/Allegiance.ClientCore.Game/Game1.cs`,
`src/Allegiance.ClientCore.Game/Rendering/ProjectileCameraCull.cs`,
`tests/Allegiance.ClientCore.Tests/ClientWorldProjectileTests.cs`,
`tests/Allegiance.Core.Tests/ProjectileBroadcastTests.cs`,
`tests/Allegiance.ClientCore.Tests/P542RemoteProjectilesLiveTests.cs` (new, live-gated).

## Tests

**Failing-first.** `ClientWorldProjectileTests.EnemyRound_OfSameType_AdjacentToUs_IsKept`
(and the rewritten `OwnEchoPredicate_DropsOwnEcho_KeepsForeignEvenSameType`)
assert that an enemy round of OUR projectile type spawned right next to our ship
is **kept**, while our own echo is still dropped. Verified failing against the
pre-fix `Apply` (`bool own = true`): `Assert.Single() Failure: collection was
empty` (the enemy round was dropped); passes with the fix. Codec round-trip
updated to 54 B incl. `LauncherId`.

## Live proof (in-process server + real TCP + real ConnectionSession/ClientWorld)

Driver: `P542RemoteProjectilesLiveTests` (gated `ALLEGIANCE_LIVE_P542_PROOF=1`).
Same conditions both runs: the local player (Alpha) and an enemy shooter (Bravo)
fly the same hull + same dispersion-0 gun (same projectile type 85), are
co-located 120 u apart in one sector (inside `FireOriginRadius`), both auto-fire,
and Alpha actively fires too (always-true own-echo predicate models its live
local tracer). Pairing the server's own broadcast lines with the client's
`[proj-diag]` counters:

| hop | BEFORE (pre-fix) | AFTER (P542) |
|---|---|---|
| Server broadcasts Bravo's shots | 182 `[projectile] … type 85 … -> 2 peer(s)` | 180 |
| Client RECEIVED (enemy) | **182** | **180** |
| Client DROPPED as own echo (enemy) | **182** | **0** |
| Client DRAWN (enemy, unique) | **0** | **180** |

Quoted lines (`after_client_diag.txt`, `before_after_counts.txt` in `.verify_p542/`):

```
BEFORE: serverShots(bravo)=182 receivedEnemy=182 droppedEnemy=182 drawnEnemy=0
        diagLine: received own=182 tm=0 en=182 dropped-as-own own=182 tm=0 en=182
                   radius-culled=0 camera-culled=0 drawn own=0 tm=0 en=0
AFTER:  serverShots(bravo)=180 receivedEnemy=180 droppedEnemy=0  drawnEnemy=180
        diagLine: received own=180 tm=0 en=180 dropped-as-own own=180 tm=0 en=0
                   radius-culled=0 camera-culled=3747 drawn own=0 tm=0 en=180
```

Server broadcast ↔ client receipt match 1:1 (181 ≈ 181/182). Before the fix every
enemy round was swallowed as "our echo" (`droppedEnemy=182`, `drawnEnemy=0`);
after the fix none are dropped and all are drawn. Alpha's OWN shots are still
correctly dropped as its own echo (`droppedOwn=180`), preserving the P462
single-render of our own fire.

The MonoGame client's `[proj-diag]` line (same counters, emitted once per second
when active) is identical in structure; both are fed by the shared `DiagnoseDraw`.

## Build

Whole-solution build with `/home/domingo/.dotnet/dotnet build`: **0 warnings,
0 errors**.

## Suite (3 consecutive runs on the final code, `scripts/run-tests.sh` P471)

See the amended `git log` below and `docs/TEST_FLAKES.md` for any cited flake.
