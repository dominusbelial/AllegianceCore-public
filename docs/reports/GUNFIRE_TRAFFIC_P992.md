# P992 — Gunfire traffic study: why the port sends every shot and hit, how native shows other ships' fire, and what a change would save

Base: Claude's local master at dispatch (`c18cab2a`, includes P983/P990 broadcast telemetry and P991's
SHIP_STATUS gate). Own worktree `allegiance-pi-p992`. **Status: complete in the working tree, not
committed.** Research + measurement only: **no `src/` change, nothing about what the server sends, in
which order, or at what rate changed**, so the P983/P990 off-path pins cannot have moved and the rig
baseline (`immutableSnapshot`+`encodeOnce`, P863 tick budget, `parallelBuild`, pool transport,
`shipUpdateModel=native`, `nativeGotoPlan`, server GC) is untouched. What was added is a test-side
offline calculator plus one loopback fixture that measure the two lanes (see §5) and this report.

---

## 0. Answers in one paragraph

- **Native has no per-shot gunfire message at all.** `FM_CS_PROJECTILE_INSTANCE(12)` is declared
  **client→server** (`Inc/Messages.h:131`) and has **no send site anywhere in `FedSrv/`, `clintlib/` or
  `WinTrek/`** (grep: only the declaration, the Wopr interop wrapper, and Wopr's *receive* switch); its
  only server→client use in any native code is Wopr listening to a message nothing sends. A native
  client learns that **another ship is firing** from the *state bits it already gets in the ship
  update*: `ServerLightShipUpdate.stateM` (`Igc/igc.h:2423-2429`) is copied straight out of the sim's
  `CshipIGC::m_stateM` (`Igc/shipIGC.h:1129-1130`), whose low word carries the selected mount and the
  **fire bits** `oneWeaponIGC`/`allWeaponsIGC` (`Igc/igc.h:805-827`), and every client runs
  `CweaponIGC::FireWeapon` for **every ship in its own cluster** from that state
  (`Igc/clusterIGC.cpp:241-255` → `Igc/shipIGC.cpp:264-341` → `Igc/weaponIGC.h:30-72`), spending its own
  predicted energy/ammo, at the weapon type's own `dtimeBurst` cadence, creating the round locally
  (`Igc/weaponIGC.cpp:290-303`). Hits are equally local: the round collides in the local sim
  (`Igc/projectileIGC.cpp:161-230`), the local `AddHullHit`/`AddFlare` spark/shield-flash and the
  my/other × shield/hull hit sound play from that collision (`WinTrek/trekigc.cpp:2365-2396,4270-4318`),
  and the **only** authoritative hit information on the wire is the per-ship `CompactShipFractions`
  (hull/shield, 5 B) inside each heavy record plus the recipient's own `fractions`
  (`Inc/Messages.h:895-905`, `clintlib/appmsg.cpp:736-819`). Missiles are the exception: they are
  created server-side and broadcast as an explicit `FM_CS_FIRE_MISSILE` event to the sector
  (`FedSrv/FedSrv.CPP:5408-5505`), which is why they are also the only ordnance with a per-tick state
  message.
- **The port sends every shot and every landed round because its client runs no sim.** The relay
  is one `PROJECTILE_INSTANCE` frame (58 B on the wire) per shot per peer in the shooter's sector that
  can see the shooter (`Server.Combat.cs:29-56` → `Server.FireAudience.cs:92-104,138-215`) plus one
  `PROJECTILE_HULL_HIT` frame (48 B) per landed round to the same audience
  (`Server.Combat.cs:119-148`) — plus a fire-dependency the same path drags in: a guaranteed
  `SINGLE_SHIP_UPDATE(78)` pose refresh every 0.5 s per (viewer, firing ship), and a first-sight
  `EXPORT(26)` + `SHIP_STATUS(90)` (`Server.FireAudience.cs:165-200`). Measured, **one shot costs
  112.2 B per recipient**, of which only 58 B is the round itself (§3.3).
- **Measured on the largest loopback fixture that matches the rig's shape** (411 peers, 3 sectors, 137
  peers per sector, 36 shooters, 100 ticks = 5 s, P990 telemetry on, byte-exact bucket): the two lanes
  are **14.02 MB + 9.47 MB = 23.49 MB**, and all gunfire-attributable traffic (including the 3.64 MB
  dependency) is **27.13 MB of the 81.67 MB the server sends in that window (33.2 %)**; firing raised
  the fixture's egress by **+49.8 %**. Fan-out is exact: `msgs(12) = shots × peers-in-sector`
  (241 668 = 1 764 × 137).
- **The rig's 574 000 messages / 5 s is ~844 shots/s**: 574 000 / 5 s / 136 visible recipients ≈ 844
  shots/s ≈ **2.1 shots/s per bot at 400 bots** — i.e. ~21 % of the bots firing at a 10/s gun cadence, or
  every bot firing ~21 % of the time. It is a *rate*, not an anomaly (§3.8).
- **Cheapest real savings, measured, all behind default-off flags:** distance-culling the two per-shot
  lanes to the client's own 5 900 u projectile draw radius removes **38.0 %** of both lanes
  (≈ −10.6 % of rig egress); native-style batching + a compact record removes **58.1 %** of the two lanes
  (≈ −16 % of rig egress); the native model (fire state → client-side cosmetic sim) removes **100 %** of
  both lanes (≈ −32 % of rig egress incl. the dependency) but needs a client weapon sim and reproduces native's fire-state
  latency artifact. §4 recommends the first two as separate tasks and the native model as the strategic
  follow-up.

---

## 1. Native model (citations re-grepped on `~/Development/Allegiance/src` at this base)

### 1.1 `PROJECTILE_INSTANCE` is client→server and has no sender in any native path

```
Inc/Messages.h:131  DEFINE_FEDMSG(CS, PROJECTILE_INSTANCE, 12)
Inc/Messages.h:132    Time              timeCreate;
Inc/Messages.h:133    DataProjectileIGC data;
Inc/Messages.h:135    Vector            position;
```

- `grep -rn "PROJECTILE_INSTANCE"` over the whole native tree returns exactly: the declaration above,
  `Wopr/AllegianceInterop/AllMessages.h:924-975` (a managed wrapper class + constructor),
  `Wopr/AllegianceInterop/MessageTypeNative.h:17` and `Wopr/Wopr/Constants/MessageType.cs:22` (id
  constants), and `Wopr/Wopr/MessageReceiver.cs:178-184,1377-1378` (**receive** case + event). There is
  **no `BEGIN_PFM_CREATE`/`ForwardMessage`/`GenericSend` of it in `FedSrv/`, `clintlib/`, `WinTrek/`,
  `Igc/` or anywhere in Wopr** — Wopr's event has no subscriber. So: **no native server→client path,
  not even in Wopr/AllegianceInterop.** (The `CS` direction is client→server; the native client itself
  never sends it either — it never needs to, see §1.2.)
- `213` is past native's whole id range (highest `DEFINE_FEDMSG` number in `Inc/Messages.h` is
  **200**), so `PROJECTILE_HULL_HIT(213)` is a port-only message and cannot ever collide with a native
  id (`src/Allegiance.Core/Wire/ProjectileHullHit.cs` header comment, P364/P463).

### 1.2 How a native client learns another ship fired: it is told the *state*, and its own sim fires

**Which message / which field.** The ship's controls and state are in every ship-update record:

```
Igc/igc.h:2423  class   ServerLightShipUpdate                           //8 bytes
Igc/igc.h:2425      ShipID              shipID;                     //2
Igc/igc.h:2426      CompactState        stateM;                     //2   <- fire bits + selected mount
Igc/igc.h:2427      CompactControlData  controls;                   //4
Igc/igc.h:2431  class  ServerHeavyShipUpdate : public ServerLightShipUpdate  //38 bytes
Igc/igc.h:2444  class  ServerSingleShipUpdate : public ServerLightShipUpdate  //46 bytes
```

`stateM` is exported verbatim from the sim (`Igc/shipIGC.h:1129-1130` `SetSC`), and the fire bits are
defined at `Igc/igc.h:805-827`: bits 0-1 are the selected mount, `oneWeaponIGC` = fire selected,
`allWeaponsIGC` = fire all, `weaponsMaskIGC` = both. The three messages that carry it:
`FM_S_LIGHT_SHIPS_UPDATE(175)` (`Inc/Messages.h:890-893`, `rgLightShipUpdates`),
`FM_S_HEAVY_SHIPS_UPDATE(176)` (`Inc/Messages.h:895-905`, `rgHeavyShipUpdates`, with time/position
references + the recipient's own `fractions` + `bpTargetHull/bpTargetShield`), and
`FM_S_SINGLE_SHIP_UPDATE(78)` (`Inc/Messages.h:583-588`, sent to the sector when a ship enters/finishes
a launch, `FedSrv/fsship.cpp:511-548`).

**Who receives it.** Every *flying* player in the cluster (`FedSrv/FedSrv.CPP:3727-3728`), per tick, and
the budget is native's own: 2 heavy and 2 light records per player per tick
(`FedSrv/FedSrv.CPP:3844-3846`), ranked by a per-ship deviation accumulator
(`FedSrv/FedSrv.CPP:3923-3950`), the recipient's own target promoted to a heavy slot
(`FedSrv/FedSrv.CPP:4141-4146`), the recipient's own source ship as the position reference
(`FedSrv/FedSrv.CPP:4128-4134`), guaranteed-ish delivery as `FM_NOT_GUARANTEED`
(`FedSrv/FedSrv.CPP:4189`), the light message appended to the heavy one (`FedSrv/FedSrv.CPP:4185-4189`).
The server loop is 10 Hz by default (`FedSrv/FedSrv.CPP:9255`), the client sends its own update every
`c_dsecUpdateClient = 0.15 s` (`Inc/Messages.h:226`, `clintlib/clintlib.cpp:1815-1861`).

**The code that runs the weapon client-side.** The client applies the record into its own sim object
(`clintlib/appmsg.cpp:684-730` light, `:736-819` heavy — `pship->ProcessShipUpdate(...)`); `GetSC`
copies `stateM`/`controls` into the ship (`Igc/shipIGC.cpp:3067-3073`). Then every client tick updates
every cluster model (`Igc/missionigc.cpp:3127-3176` → `Igc/clusterIGC.cpp:241-255`), and a ship's update
walks its weapons (`Igc/shipIGC.cpp:264-341`, `w->Update(now)` at `:323`/`:341`), which calls
`CweaponIGC::FireWeapon` (`Igc/weaponIGC.h:30-72` → `Igc/weaponIGC.cpp:142-348`):

- **selection** — `fIsWeaponSelected` reads `stateM` (`Igc/weaponIGC.h:236-252`): a fixed mount fires
  when allWeapons is set, or oneWeapon is set and this is the selected mount; a turret fires when its
  gunner's `stateM & weaponsMaskIGC` is set.
- **fire rate** — the weapon's own `dtimeBurst`; `Activate` seeds `m_nextFire = lastUpdate + dtimeBurst`
  (`Igc/weaponIGC.h:103-116`) and the burst loop advances it by `dtimeBurst` per shot
  (`Igc/weaponIGC.cpp:310-315`), so the rate is *local* and never transmitted.
- **energy/ammo** — the local pool (`m_ship->GetEnergy()`/`GetAmmo()`, `Igc/weaponIGC.cpp:160-171`),
  drained per shot (`:317-322`) and re-seeded from the authoritative `fractions` in each update
  (`GetF`, `Igc/shipIGC.cpp:3082-3090`; the recipient's own pool from
  `clintlib/appmsg.cpp:819`). Energy deficit handling and the "hold fire until you can afford it" rule
  are local (`Igc/weaponIGC.cpp:160-168,249-252`).
- **the round itself** — dispersion is a *local* random (`Igc/weaponIGC.cpp:255-268`), velocity inherits
  the launcher's velocity unless the projectile is absolute (`:270-273`), position is the muzzle +
  lead, lifespan is rewritten for blast weapons' intercept (`:275-288`), and the projectile is created
  in the local sim (`:290-303`). Nothing about the round reaches any other machine.
- **all ships, not just yours.** The cluster loop has no "is it my ship" gate: **every ship in the
  cluster runs this every client tick**, which is why another player's fire appears at all.

### 1.3 Turrets differ: the gunner streams controls + facing while shooting

A turret's aim cannot be inferred from the parent's state, so a boarding gunner's client sends
`ACTIVE_TURRET_UPDATE` while its trigger is down and `INACTIVE_TURRET_UPDATE` (id only) otherwise
(`clintlib/clintlib.cpp:1867-1900`). The server forwards the active ones in the heavy record
(`FedSrv/FedSrv.CPP:4049-4072`), the client applies them with `ProcessShipUpdate(time, atu)` and
**sets the "shooting" latch itself** — `SetStateM(weaponsMaskIGC)` under the comment "If we got this …
we're shooting" (`Igc/shipIGC.cpp:3299-3319`), which is what makes `fIsWeaponSelected` true for the
turret mount. So a turret's fire is driven by the relayed control/facing stream, not by the parent's
state bits; the port keeps the same shape (`ShipEntity.TurretFiring`, `P803`).

### 1.4 Missiles are the exception: an explicit event, created by the server

`FM_CS_FIRE_MISSILE` (`Inc/Messages.h:547-557`) carries the launch data; the server validates it,
creates the real missile, and **broadcasts it to the sector as a guaranteed event**
(`FedSrv/FedSrv.CPP:5408-5505`, forward at `:5505`); the client creates the same missile from the
`MissileLaunchData` (`clintlib/appmsg.cpp:1568-1610`). That is the model the port's own
`FIRE_MISSILE(70)` relay already follows — and it is why missiles are the only ordnance with a
per-tick state message (`MISSILE_UPDATE(205)`, P234): native has to keep the client's missiles in sync
because their target/lock state is server-authoritative.

### 1.5 How a native client shows a hit

- **Hull/shield impacts are local.** The round collides in the client's sim
  (`Igc/projectileIGC.cpp:161-230`): `ReceiveDamage` runs in the local sim
  (`Igc/shipIGC.cpp:1210-1345`), which calls `GetThingSite()->AddDamage(...)` (damage vent smoke,
  `:1310-1311`) and `DamageShipEvent` (`:1312`); on a hull-or-worse hit that is not `c_dmgidNoDebris`
  the projectile fires `AddHullHit(position1 - position2, normalized)` — the two debris bits
  (`Igc/projectileIGC.cpp:218-226`), gated on the object being rendered (`pModel->GetVisibleF()`,
  `:193`, set per frame by the renderer, `WinTrek/WinTrek.cpp:8040-8050`). Shield-absorbed hits call
  `AddFlare` instead (`Igc/shieldIGC.cpp:140-171`).
- **Hit sound** is local and once per frame per ship: my/other × shield/hull chosen by
  `m_pmodel == trekClient.GetShip()->GetSourceShip()` and `bAbsorbedByShield`
  (`WinTrek/trekigc.cpp:2365-2396`, fed by `WinTrekClient::DamageShipEvent`, `:4270-4318`), with force
  feedback only for the local hull (`:4283-4301`).
- **The numbers on the HUD are server-authoritative, and arrive in the same heavy record**: each
  `ServerHeavyShipUpdate` carries `CompactShipFractions` (hull/shield/fuel/ammo/energy/ore, 5 B) and a
  `BytePercentage power`, so a ship's displayed hull/shield is corrected by the server every time the
  ship is posed (`Igc/igc.h:2431-2443`; `GetF` applies it, `Igc/shipIGC.cpp:3082-3090`); the recipient's
  own pools ride the heavy packet's top-level `fractions` (`clintlib/appmsg.cpp:819`); and the current
  *target's* hull/shield ride `bpTargetHull`/`bpTargetShield` (`clintlib/appmsg.cpp:747-775`, server side
  `FedSrv/FedSrv.CPP:4075-4115`). There are no floating damage numbers in native.
- **Damage, death and credit are server-authoritative** — the client's local damage is a prediction that
  the next heavy `fractions` overrides; kills arrive as `KILL_SHIP`/`SHIP_DELETE`.

### 1.6 What is cosmetic-local and what is authoritative

| Thing | Native | Authoritative source of truth |
|---|---|---|
| That a ship is firing | **local** (fire bit → local weapon sim) | the ship's `stateM` in its ship update (server) |
| Each round's position / velocity / spread / cadence / lifespan | **local** (own random, own `m_nextFire`) | nothing — each client's own prediction; server's rounds differ in spread |
| Round/impact spark, shield flare, hit sound, damage vents | **local** (local collision) | nothing |
| Hull/shield/ammo/energy fractions (own and every posed ship) | **server** | `CompactShipFractions` in HEAVY/LIGHT; own `fractions` |
| Target hull/shield readout | **server** | `bpTargetHull`/`bpTargetShield` in HEAVY |
| Damage dealt, kills, credit, pods | **server** | `ReceiveDamage`/`KILL_SHIP` |

**What native's model costs and what it loses.** Per-shot bandwidth for guns is **zero** — the price is
that a shot is a *state*, so a trigger press is only seen when that ship's state record next arrives.
With 2 heavy + 2 light records per player per tick at 10 Hz and ~133 ships in a sector, a given ship's
state reaches you roughly every **30-60 ticks (3-6 s)**, which is why native is known for frozen/ghostly
remote ships in crowded sectors; a short tap between two records is simply never simulated by the
observer. Native accepts that: guns are cosmetic, damage is server-side. *(This is the trade the port
deliberately does not make: every port client sees every shot, promptly.)*

---

## 2. The port: every send site, every consumer

### 2.1 Send sites

| Message | Send site | Audience | Why it exists (git history) |
|---|---|---|---|
| `PROJECTILE_INSTANCE(12)`, 54 B + 4 CRC = **58 B/frame** | `src/Allegiance.Server/Server.Combat.cs:29-56` (`BroadcastProjectileInstance`, wired at `Server.Lifecycle.cs:874` from `IIgcSite.ProjectileCreatedEvent`, raised by `src/Allegiance.Core/Sim/Weapon.cs:362` right after the round is created) | `BroadcastShipEmission` (`Server.FireAudience.cs:92-104`) → every peer **in the shooter's sector** that passes `EmissionVisible`/`ShipPoseVisible` (`:78-82,30-32`: same mission, viewer in that sector, same side **or** uncloaked/seen); the shooter's own peer included. Probe rounds go through `BroadcastProbeEmission` (`:107-136`) and carry a 2-byte object-type trailer (56 B) | **P88** (`2d429a6b`): "the port is a pure renderer with no combat sim", so the server's real rounds are relayed instead of simulated; same-side only then. **P375** (`5736f3ab`) extended it to cross-side viewers. **P462** (`b388d0d8`) added the client's own-shot local echo + server-echo de-dup. **P542** (`627ed3a2`) added `LauncherId` (52→54 B) after enemy/teammate rounds were being de-duped as "our own echo". **P697** (`0634d63f`) scoped the audience to the shooter's sector and the viewer's view of the emitter after "ships exploding for no reason" / shots from ships never shown |
| `PROJECTILE_HULL_HIT(213)`, 44 B + 4 CRC = **48 B/frame** | `src/Allegiance.Server/Server.Combat.cs:119-148` (`BroadcastProjectileHullHit`, wired at `Server.Lifecycle.cs:877` from `IIgcSite.ProjectileHitEvent`, raised by `Sector.MaybeRaiseProjectileHit`, `src/Allegiance.Core/Model/Sector.cs:2791-2822`, call sites `:2498,2559,2648,2735`) | Same `BroadcastShipEmission` audience (the impact carries no cluster gate for the audience — the client filters by `ClusterId`) | **P364** (`97b2d17e`): the port's client is a pure renderer, so native's same-process `AddHullHit` becomes an explicit message. **P463** (`c2066de5`) added the shield-absorbed discriminator (hit sound/flare), **P539b** (`76be2d99`) added `SourceId` (shooter attribution for the bot hit metric), **P762** (`ac8a4bad`) reuses it for a drilling builder's debris |
| the fire **dependency** the same path drags in | `Server.FireAudience.cs:165-200` (`EnsureEmissionPose`): on first sight of a firing source, a guaranteed `EXPORT(26)` (`CrossSideExportPacket`, 26 + parts) + `SHIP_STATUS(90)`, then a `SINGLE_SHIP_UPDATE(78)` refresh every **0.5 s** per (viewer, firing ship) | per (viewer, firing ship) pair | **P697** (`0634d63f`): the pose/identity must be ordered *ahead of* the effect "independent of replaceable HEAVY/LIGHT frames"; the port's HEAVY lane is latest-wins-coalesced, so a fire event cannot rely on a pose being in flight |
| related, not gunfire | `COLLISION_EVENT(216)` (`Server.Combat.cs:201-240`), `VICTIM_DAMAGED(217)` unicast to the victim (`:150-176`), missile `FIRE_MISSILE(70)`/`MISSILE_UPDATE(205)` | — | P463c/P623/P234. Out of this study's scope; named so the "hits" table is complete |

The relay's own accounting is already on the wire-free side: `TakeFireAudienceReport()`
(`Server.FireAudience.cs:224-229`) prints `[fire-audience] sent/neverposed/initialPoses/refreshPoses/hidden`,
which is the cheap way to confirm on the rig which of the three contributors dominates.

### 2.2 Consumers (who would lose what)

| Consumer | File:line | 12 (projectile) | 213 (hull hit) |
|---|---|---|---|
| Transport decode | `src/Allegiance.ClientCore/Session/ConnectionSession.cs:1924-1930` / `:1988-1992` | decode → world | decode → world |
| Client world | `src/Allegiance.ClientCore/State/ClientWorld.cs:4045-4108` (`Apply(ProjectileInstance)`: own-echo de-dup, placeability diag, entity), `:3887-3908` (`Apply(ProjectileHullHit)`: spark + hit-sound buffers) | entity list, `[proj-diag]` counters | `_hullHits` + `_hitSoundEvents` |
| MonoGame projectile render | `src/Allegiance.ClientCore.Game/Game1.Rendering.cs:1132-1177` + `Rendering/ProjectileRenderer.cs` | **drawn** (only within `ProjectileVisibility.VisibilityRadius` = **5 900 u** of the viewer's ship, `src/Allegiance.ClientCore/State/ProjectileVisibility.cs:23-28`) | — |
| MonoGame impact spark | `src/Allegiance.ClientCore.Game/Game1.Rendering.cs:1764-1840`, `Rendering/HullHitSparkParticles.cs` | — | **drawn** (cluster-gated only: `hit.ClusterId == ownSectorId`, `:1770-1776`; shield-absorbed → flare, not spark) |
| MonoGame hit sound | `src/Allegiance.ClientCore.Game/Game1.Audio.cs:865-940`, `Audio/HitSoundSelector.cs` | — | **played** (my/other × shield/hull, once per target per frame, distance-attenuated) |
| Own-shot echo / local tracer | `src/Allegiance.ClientCore.Game/Flight/OwnShotEcho.cs` (P462/P465c), `Game1.FlightInput.cs:3990-4070` | **de-dups our own echo** (`ClientWorld.OwnEchoDropPredicate`, `ClientWorld.cs:874-900`) — a locally emitted round must still arrive once? No: it is *dropped* when it matches; if the server stopped sending our own echo, the local tracer alone would still render and sound it | — |
| HUD hit/damage flash | `VICTIM_DAMAGED(217)` → `ClientWorld.Apply(VictimDamaged)` (`:3910-3918`) + `_victimDamageVersion` (HUD-10) | — | **not** used; the own-ship damage flash is 217 |
| **MCP / client bots** | `tools/allegiance-mcp/BotManager.cs:3480-3560` (`SampleCombatStats`), `WeaponCoverage.cs:337` | only **their own** rounds: `TakeOwnProjectileCount()` (`ClientWorld.CountOnlyProjectiles`, `ClientWorld.BotSharing.cs:12`); `src/Allegiance.Bot/Bot.cs:2036-2037` keeps raw 12 only for test/observer (`RetainRawCombatUpdates`). **No bot dodges other ships' rounds** — the dodge law consumes obstacles (ships/stations/asteroids), `src/Allegiance.Core/Sim/ShipDodgeLaw.cs`, `Ship.TryComputeDodgeGoal`, `Ship.NpcUpdates.cs` | **yes, heavily**: per-bot hit counts/TTK (`EnemyHullHits`, P556), "who is attacking me" (`TargetId == myShipId` → `RecentAttackers`, P622), friendly-asset attack calls (P798), station-attacker alerts — all keyed on `SourceId`/`TargetId`/`ClusterId` |
| Proof tools | `tools/hullhit-proof`, `tools/shieldflare-proof`, `tools/vizfight`, `tools/allegiance-mcp` | observer/renderer proofs | proofs + bot metrics |
| Tests | `tests/Allegiance.ClientCore.Tests/ClientWorldHullHitTests.cs`, `P542RemoteProjectilesLiveTests.cs`, `P697GhostBattleLiveTests.cs`, `P745SiegeDutyTests.cs`, … | codec/world/live pins | codec/world/live pins |

**Conclusion for options:** `12` is **cosmetic for every consumer** except the shooter's own echo-dup
bookkeeping and the bots' own-round counter (both of which are satisfied by the shooter's own copy).
`213` is cosmetic *for a human* but **is a bot input stream** (hit credit, attacker tracking), so its
audience cannot simply be culled like `12`'s.

### 2.3 What one shot really costs (the dependency)

The emission path (`EnsureEmissionPose`) is not free and is not native: the first shot from a ship to a
given viewer costs an `EXPORT(26)` (with the part tail, ~100-200 B) and a `SHIP_STATUS(90)`; every
subsequent shot to that viewer refreshes a guaranteed `SINGLE_SHIP_UPDATE(78)` at 0.5 s while the ship
keeps firing. On the rig-shaped fixture this dependency is **3.64 MB for 5 s** — 13.4 % of the stock
gunfire cost, measured as the difference between the firing and the identical non-firing fixture
(§3.2). It is part of "what a shot costs", so the options in §4 are priced against it.

---

## 3. Measurement

### 3.1 Method

- The fixture is the study's permanent Live-tier test
  (`tests/Allegiance.Core.Tests/P992GunfireTrafficTests.cs`,
  `Loopback_showcase_measures_gunfire_traffic_and_the_audience_radius`): a real `Allegiance.Server.Server`
  boot (`PCore001.igc`, `XXV_simple.igc`, 20 Hz, `broadcast.parallelBuild = true`,
  `shipUpdateModel = native`, `MinUpdateHz = 2`, `profiler.broadcastBudget = true`), N peers seated via
  `TestSeedPeers`, all ships a "Fighter" hull (id 55) armed with one preferred weapon (a 10/s,
  ammo-based gun, `dtimeBurst` from the core), two opposed lines 600 u apart, 36 shooters with
  `StateM = AllWeapons`; each tick runs the **real** `Mission.Update` (weapons fire, rounds fly, hits
  land) then the **real** broadcast suffix (`TestProjectRecordedTick(..., snapshot: true)`), for 100
  ticks (5 s of sim at 20 Hz). Ships are kept alive and loaded each tick so the fire rate is the weapon's
  own cadence for the whole window — a sustained-combat fixture, not a one-burst one.
- The telemetry's write bucket is pinned (`BroadcastBudgetTelemetry.SetSecond`) so the five-second
  rolling ring cannot drop the early seconds; every printed number is therefore *whole-window exact*, not
  a rolling sample. Byte accounting is P983's transport-sink accounting (the same counters the rig uses),
  and the per-id split is P990's `[bb-other]` walk of each frame's own `cbmsg/fmid`.
- Two fixtures are run: **the rig shape** (411 peers over 3 sectors, 137 per sector, 36 shooters, 12 per
  sector) and **the same shape with nobody firing**. All numbers below come from the run in §3.2
  (`[p992]`, `[p992-stock]`); the variants in §3.4-3.6 were taken with the same harness through its
  environment knobs (`P992_PEERS`/`P992_FIRING`/`P992_TICKS`/`P992_SECTORS`/`P992_SPACING`/`P992_ALIGNED`).
- The audience-radius measurement (option (d)'s input) hooks the host's own `ProjectileCreated` /
  `ProjectileHit` seams (`ServerHost.cs:437,502`) *in addition* to the server's broadcast handlers and
  counts, for every round/impact, how many peers in that sector have their own ship within
  `ProjectileVisibility.VisibilityRadius` (5 900 u) of it — the exact rule the MonoGame renderer applies
  before it draws a bolt (`Game1.Rendering.cs:1132-1137`).

### 3.2 The measured block (rig shape, 5 s)

```
[p992] peers=411 firing=36 ticks=100 shots=1764 hits=1440 shotPairs=241668 near5900=149940
       hitPairs=197280 hitNear5900=122400 m12=241668 b12=14016744 m213=197280 b213=9469440
       m78=49320 b78=2959200 m26=184657 b26=12571472 m90=264739 b90=7412692
       other=46973736 omsgs=981848 heavy=31273548 keyed=2272830 total=81670914 wallS=2.36
[p992] peers=411 firing=0  ticks=100 shots=0 hits=0 m12=0 b12=0 m213=0 b213=0 m78=0 b78=0
       m26=179725 b26=12236096 m90=252391 b90=7066948 other=19824192 heavy=31273548
       keyed=2272830 total=54521370 wallS=1.93
[p992-stock] shots=1764 hits=1440 fanout=137 b12=14016744 b213=9469440 dep78=2959200
       dep26=335376 dep90=345744 stock=27126504 delta=27149544 quietTotal=54521370
       fireTotal=81670914 near5900=149940/241668
```

Identities (asserted by the test, not eyeballed):

- **Fan-out**: `msgs(12) = shots × peers-in-sector` exactly (241 668 = 1 764 × 137), and
  `bytes(12) = msgs × 58` (14 016 744). Same for hits: `msgs(213) = landed rounds × 137`
  (197 280 = 1 440 × 137), `bytes(213) = msgs × 48`.
- **Attribution**: firing raises the window by 27 149 544 B; the five ids explain 27 126 504 B of it
  (**99.92 %**, 23 040 B residual from a few other one-shots during the busier window). The dependency
  (`78` refresh + the first-sight `26`/`90`) is 3 640 320 B = **13.4 %** of the stock gunfire cost.
- **Shares in this window**: `12` = **17.2 %**, `213` = **11.6 %**, both = **28.8 %**, all gunfire
  (including the dependency) = **33.2 %** of the 81.67 MB the server sent. Firing *raised egress by
  49.8 %* over the same fixture with nobody firing.
- **Per peer per window**: 198.7 KB total, of which `12` = 34.1 KB and `213` = 23.0 KB.
- **Per recipient per tick** (20 Hz): 5.88 `12` frames = 341 B, 4.80 `213` frames = 230 B, 1.2 `78`
  frames.

### 3.3 Per-shot cost decomposition (per recipient)

| Piece | Bytes per (shot, recipient) | Share |
|---|---|---|
| `PROJECTILE_INSTANCE(12)` frame | **58.0** | 51.7 % |
| `PROJECTILE_HULL_HIT(213)` frame | **39.2** (48 B × the fixture's 81.6 % hit rate) | 34.9 % |
| `SINGLE_SHIP_UPDATE(78)` 0.5 s fire refresh | 12.2 | 10.9 % |
| `EXPORT(26)` first sight | 1.4 | 1.2 % |
| `SHIP_STATUS(90)` first sight | 1.4 | 1.3 % |
| **total** | **112.2 B** | 100 % |

So the round itself is only about half of what a shot costs the server; **a shot is ~1.93 × its own
message**. Any option that only touches `12` leaves the other half behind unless it also removes the
dependency (the native model does; culling does it per culled recipient; batching/compact do not).

### 3.4 Fan-out scaling with peers (16 shooters, 200 ticks = 10 s)

| Peers (1 sector) | Recipients/shot | Shots | `12` frames | `12` bytes | `213` frames | `213` bytes | Total egress | `12` share | `213` share |
|---|---|---|---|---|---|---|---|---|---|
| 36 | 36 | 1 584 | 57 024 | 3.31 MB | 51 840 | 2.49 MB | 9.35 MB | 35.4 % | 26.6 % |
| 72 | 72 | 1 584 | 114 048 | 6.61 MB | 103 680 | 4.98 MB | 21.83 MB | 30.3 % | 22.8 % |
| 144 | 144 | 1 584 | 228 096 | 13.23 MB | 207 360 | 9.95 MB | 54.77 MB | 24.2 % | 18.2 % |
| 411 (3 sectors) | 137 | 1 764 | 241 668 | 14.02 MB | 197 280 | 9.47 MB | 81.67 MB | 17.2 % | 11.6 % |

`12` is **exactly linear in the recipient count** (identical shot count, identical bytes per recipient
per tick: 459 B at 36, 72 and 144 peers) and its *share* falls only because the heavy lane grows with
the population too. There is no superlinearity here: the gunfire cost is `shots × peers-in-sector ×
112 B`, full stop.

### 3.5 Scaling with combat intensity (144 peers, 200 ticks = 10 s)

| Firing ships | Shots | `12` bytes | `213` bytes | Total egress | `12` share | `213` share | Egress vs quiet |
|---|---|---|---|---|---|---|---|
| 0 | 0 | 0 | 0 | 28.67 MB | 0 | 0 | — |
| 4 | 396 | 3.31 MB | 2.49 MB | 35.19 MB | 9.4 % | 7.1 % | +22.8 % |
| 12 | 1 188 | 9.92 MB | 7.46 MB | 48.25 MB | 20.6 % | 15.5 % | +68.3 % |
| 16 | 1 584 | 13.23 MB | 9.95 MB | 54.77 MB | 24.2 % | 18.2 % | +91.0 % |
| 48 | 4 752 | 39.69 MB | 29.86 MB | 106.98 MB | 37.1 % | 27.9 % | +273.1 % |

Both lanes are linear in the number of shooters (10× shooters ⇒ 10× bytes); the *share* saturates
towards ~37 %/28 % because the heavy lane is fixed by the peer count. On the rig (all 400 bots able to
shoot, ~21 % duty cycle) the two lanes were inferred at 37.6 % of egress from one window; the run averages are ~28 % (b105 27 %,
b106 29 %, §3.8 review correction), close to this fixture's 28.8 %.

### 3.6 The hit lane really is a hit-rate term

Same 144-peer/16-shooter fixture with the two lines yawed 90° so both fire **past** each other: shots,
`12` frames and `12` bytes are **byte-identical** (13 229 568), `213` is **0**, and total egress drops
to 44.66 MB (from 54.77 MB). So the 213 lane is entirely a function of hits landed; it cannot be
predicted from the shot count alone, which is why the calculator takes `hitRate` as an input.

### 3.7 Message sizes, and what a batch is worth

| | Payload | Framed (+4 CRC) | Native's own shape |
|---|---|---|---|
| `PROJECTILE_INSTANCE(12)` | 54 B | **58 B** | 52 B + 4 CRC = 56 B: header 4 + `Time` 4 + `DataProjectileIGC` 32 + `Vector` 12. The port is **+2 B** for P542's `LauncherId` (the port client has no local sim to recognise its own round) |
| `PROJECTILE_HULL_HIT(213)` | 44 B | **48 B** | no native counterpart (port-defined, id > native's max 200) |

Frame anatomy: `cbmsg`(2) + `fmid`(2) + payload + one CRC(4) per packet. Every one of these frames today
carries exactly **one** message, so a native-style var-item batch (one header + N fixed records + one
CRC) saves 8 B per message minus 8 B per frame — 11.4 % of `12` at 5.88 rounds/frame and 13.2 % of
`213` at 4.8 hits/frame on the rig shape.

### 3.8 Explaining the rig's 574 000 messages / 5 s

`574 000 / 5 s = 114 800 frames/s`; with 136 visible recipients per shot (P990's measured
`ageShips`) that is **844 shots/s**, i.e. **2.11 shots/s per bot at 400 bots** — about **21 % of the
bots firing at a 10/s gun cadence**, or every bot firing ~21 % of the time. The fixture reaches the same
order of magnitude from the other direction: 36 shooters (9 % of 411 peers) × 9.8 shots/s = 353 shots/s,
and 844/353 = 2.39×, i.e. the rig's bots fire ~2.4× as many rounds per peer as the fixture's
12-shooters-per-sector. Nothing about the rig's number needs a different mechanism.

Byte arithmetic for that window: `574 000 × 58 B = 33.29 MB` for the `12` lane alone. Since it is quoted
at 58.8 % of the ordered lane, that window's ordered lane is ≥ 56.6 MB and — at the run-average 47.7 %
lane share — its total egress was ~119 MB, i.e. **a clash-peak window ~3× the b104 run average**
(36.4 MB per window from P990 §2.3). That is an inference from the quoted numbers, not a measurement; the
exact figure is one `[broadcast-budget] bytes=` line in the same window as the `[bb-other]` row (the
rig's own telemetry already prints both). The per-shot cost in §3.3 is measured here and does not depend
on which window it is applied to.

> **Claude review correction (rig run averages, 2026-11-10).** The two P991 A/B batches give the run-averaged
> shares the paragraph above had to infer: b105 (gate off) **90.1 MB per 5 s window, gunfire (`12`+`213`)
> 24.2 MB = 27 %**; b106 (gate on) **72.0 MB, gunfire 20.6 MB = 29 %** (411 peers, 3-side showcase, two full
> matches each, `profiler.broadcastBudget` on). So the 574 000-message window was a busy window but not a
> 3× clash peak (33 MB of `12` against a 24 MB run-average for both lanes ≈ 1.4×), and gunfire is **~28 % of
> rig egress on average, not 37.6 %**: `12` ≈ 21 %, `213` ≈ 7 %. The fixture's 28.8 % share matches that
> closely. The "% of rig egress" column in §4.6 and the rig figures in §0/§4.7 are corrected to this basis
> below (fixture columns unchanged).

### 3.9 How much of the audience can even draw the round (option (d)'s input)

With the fixture's lines spread 150 u apart (a ~10 000 u battle line), only **149 940 / 241 668 =
62.0 %** of `(shot, recipient)` deliveries put the round's spawn point within 5 900 u of the recipient's
own ship — the radius past which the MonoGame renderer already discards the bolt
(`ProjectileVisibility.VisibilityRadius`). With the same ships packed 40 u apart the fraction is
**100 %**. Hits: 122 400 / 197 280 = 62.0 % as well (impacts follow the rounds). So a distance cull is
worth ~38 % of both lanes **in a spread fight and nothing in a knife-fight** — a real but
shape-dependent saving, and the single cheapest thing that touches nothing but the relay.

---

## 4. Options (no implementation here; each is a separate task)

Saving figures are per five-second window on the rig-shaped fixture (§3.2, 81.67 MB) and, in
parentheses, as a share of **rig egress** using the rig's own quoted shares (`12` = 58.8 % of the ordered
lane, ordered = 47.7 % of egress → 28.0 % of egress; `213` = 20.1 % of the ordered lane → 9.6 %). The
calculator that produces them is the offline test (§5).

### a. Native model: send the fire state, let the client spawn cosmetic rounds

`12` (and optionally `213`) disappear; the client runs a cosmetic weapon sim for visible ships.

- **The port already has every input on the wire**: each posed ship's `StateM` (fire bits + selected
  mount) and `Controls` ride `ServerShipUpdate` (`src/Allegiance.Core/Wire/ServerShipUpdate.cs:46,126`);
  every exported ship's mounted parts ride `EXPORT(26)`'s `PartData[]` tail (`ExportShip.cs:96`, applied at
  `ClientWorld.cs:3421-3434`) and `LOADOUT_CHANGE(96)`; energy/ammo fractions ride the same update; the
  weapon/projectile type data (cadence, dispersion, speed, lifespan, absoluteF) is in the static core the
  client already loads (`_shopCatalog`/`ProjectileTypes`); and the client already contains a **complete
  local cosmetic gun sim for its own ship** (`Flight/OwnShotEcho.cs`, P462/P465c: cadence, energy/ammo
  gate, dispersion, muzzle offsets, per-weapon sounds, echo de-dup). Generalising that from "my ship" to
  "every tracked ship" is the task.
- Saving: **`12` + `213` + the whole 3.64 MB dependency = 27.13 MB = 33.2 % of the fixture's egress
  (rig: ~28 % of egress on the b105/b106 run averages, plus the dependency)**. If `213` is kept for the bots (§2.2), **−14.02 MB =
  −17.2 % of fixture egress (rig −28.0 %)**.
- Cost/risk: a new client-side subsystem (per-ship weapon state, per-mount cadence, ammo/energy
  prediction, muzzle/pose source, its own hit test and spark); visual divergence from the server's actual
  round (native has the same divergence and accepts it); **fire-state latency** equal to the pose cadence
  (the port's HEAVY/LIGHT rotation, p95 400-500 ms measured in P990) plus native's own artifact —
  a trigger press between the pose records is invisible, so short taps would stop appearing for other
  ships (today they always do); determinism/parity of the spark position (or keep `213` for it);
  client CPU at 400 ships. The port would be **less** accurate than it is now, deliberately, in exchange
  for ~1/3 of its egress.
- What the client/bots lose: the client loses exact server rounds (keeps state-derived ones); bots lose
  nothing if `213` stays; the shooter's own echo path already exists.

### b. Batching (many rounds per frame per recipient per tick)

- Saving: `12` 14.02 → 12.41 MB (−11.4 %), `213` 9.47 → 8.22 MB (−13.2 %); together **−2.85 MB = −12.2 %
  of the two lanes = −10.5 % of the stock gunfire cost = −3.5 % of fixture egress (rig −4.5 %)**.
- Cost/risk: a new encode path that accumulates a tick's rounds per (sector, recipient set) and one
  decode path; ordering is preserved within the frame but a round can be up to one tick later; the
  P462 own-echo de-dup must match batched entries; the P990 per-fmid accounting stays correct (it walks
  the assembled messages). No information loss.
- Native parity: this is *closer* to native's shaping (native sends arrays — `FM_VAR_ITEM` — and never
  one message per round); no native rule is violated.

### c. Compact encoding (quantised `12`/`213` records)

- Payload 54 → 20 B (`12`: type 2, forward as two angles 4, speed 2, sector-local position 6, launcher 2,
  1-byte lifespan-override flag, pad) and 44 → 22 B (`213`); framed 58 → 28 B and 48 → 30 B per message.
- Saving: `12` 14.02 → 6.77 MB (−51.7 %), `213` 9.47 → 5.92 MB (−37.5 %); together **−10.80 MB =
  −46.0 % of the two lanes = −13.2 % of fixture egress (rig −18.1 %)**. With batching too: `12` → 5.16 MB,
  `213` → 4.67 MB, **−13.66 MB = −58.1 % of the lanes = −16.7 % of fixture egress (rig ≈ −16 %)**.
- Cost/risk: quantisation of position/heading is *visual only* (the server keeps resolving hits on its
  own exact state — the port never derives damage from the wire), but the client's own-echo de-dup
  radius (150 u) and the spark normal must tolerate it; per-type lifespan overrides for blast weapons;
  a sector-local reference must be carried (native's `CompactPosition` needs a `positionReference`,
  which is per-recipient — that would break encode-once sharing, so a per-sector origin or a fixed
  sector-relative grid is the right trade); both a new encoder and a decoder that still accepts the old
  size during rollout.
- What is lost: nothing gameplay-side; visual precision (sub-unit at 5 900 u draw range is invisible).

### d. Interest culling (only recipients that can see/hear the shot)

- Measured ceiling: 62.0 % of deliveries are inside the renderer's own 5 900 u radius (§3.9), so
  **−38.0 % of `12` and `213` = −8.91 MB = −10.9 % of fixture egress (rig ≈ −10.6 %)**, and the same cull
  removes the `78`/`26`/`90` dependency for culled (viewer, shooter) pairs, so up to **−32.9 % of the
  stock gunfire cost** on the fixture.
- Cost/risk: small server change in `Server.FireAudience.cs` (audience test by distance per viewer per
  shot), no wire change, no client change. **Risks**: (i) the shooter's own copy must stay (own-echo
  de-dup and the bots' own-round counter); (ii) free-camera spectators/admin views must bypass it;
  (iii) `213` must keep every recipient the bots use it for (the shooter, the victim's side, both sides'
  peers for asset-attack calls — §2.2), so **cull `12` by distance now, and treat `213` separately**;
  (iv) a viewer closing on a fight sees rounds appear at the radius boundary instead of earlier — but
  today they are not *drawn* before that boundary either, so no pixel changes for the stock renderer.
- Native parity check: native has no per-shot message, and its own `GetVisibleF` gate means a native
  client only spawns the hull spark for an object it is rendering (`Igc/projectileIGC.cpp:193`). A
  distance cull at exactly the port renderer's own radius therefore matches "the client only ever sees
  what it would draw" — it is a port-only mechanism, not a native one, and must be documented as such.
- What the client/bots lose: nothing visible; bots keep `213`; spectator proofs that count far-away
  rounds (`tools/vizfight`, `[proj-diag]`) would see fewer — the flag is default off and a rig A/B will
  tell whether it is safe.

### e. Hull hits specifically

- `213` is **9.47 MB = 11.6 % of the fixture egress (rig 9.6 %)** and is the *only* wire source of
  observed-hit attribution for every non-sim consumer (§2.2), so it cannot be dropped, only made cheaper
  or narrower.
- Options: batch (−13.2 %), compact (−37.5 %), or both (−50.7 %); cull only the peers that are neither
  the shooter nor the victim's side (breaks no bot metric, saves roughly the fraction of the sector that
  is neither — about half in a two-side fight, to be measured before it is claimed); or merge into the
  target's heavy record as a per-tick hit list (native has no such field; the client would still need
  position/normal to draw the spark, so this is a bigger wire change for a smaller win).
- Recommended: batch + compact with `12` (one format decision, one decoder), and leave the audience
  alone until a rig measurement shows the bot metrics tolerate a narrower one.

### 4.6 Options table

| Option | `12` bytes (% saved) | `213` bytes (% saved) | Total saved | % of fixture egress | % of rig egress | Cost | Risk |
|---|---|---|---|---|---|---|---|
| (a) native model (both lanes) | 0 (−100 %) | 0 (−100 %) | 27.13 MB | **33.2 %** | **~32 %** (28 % + dependency) | client cosmetic weapon sim + hit test for every visible ship | fire-state latency, short taps lost, visual divergence, client CPU |
| (a) native model, keep `213` | 0 (−100 %) | 9.47 MB | 17.66 MB | 21.6 % | ~24 % | client cosmetic weapon sim only | fire-state latency, visual divergence |
| (b) batch | 12.41 MB (−11.4 %) | 8.22 MB (−13.2 %) | 2.85 MB | 3.5 % | ~3.4 % | new encode/decode, tick accumulation | one-tick delay, echo-dup update |
| (c) compact | 6.77 MB (−51.7 %) | 5.92 MB (−37.5 %) | 10.80 MB | 13.2 % | ~13 % | new codec + quantised client decode | visual precision only |
| (b)+(c) | 5.16 MB (−63.2 %) | 4.67 MB (−50.7 %) | 13.66 MB | **16.7 %** | **~16 %** | as (b)+(c) | as (b)+(c) |
| (d) cull at the renderer radius | 8.70 MB (−38.0 %) | 5.88 MB (−38.0 %) | 8.91 MB | 10.9 % | ~10.6 % | a distance test in the audience | spectator/admin bypass, bots need `213` |
| (d) on `12` + (b)+(c) on `213` | 8.70 MB (−38.0 %) | 4.67 MB (−50.7 %) | 10.12 MB | 12.4 % | ~12 % | as (d)+(b)+(c) | as (d)+(b)+(c) |
| (e) `213` audience: shooter + victim's side only | — | est. −50 % (unmeasured) | ~4.7 MB | 5.8 % | 4.8 % | audience rule | **breaks bot hit credit/asset alerts** — needs a bot-side replacement |
| any of (b)/(c)/(d) + (a) | — | — | superseded | — | — | — | (a) subsumes them |

### 4.7 Recommendation: two first steps, each behind a default-off flag

**P993 — distance-cull the `PROJECTILE_INSTANCE(12)` relay** (`broadcast.fireAudienceCull`, default
**off**). Audience = today's `EmissionVisible` **and** the viewer's own ship within
`ProjectileVisibility.VisibilityRadius` (5 900 u) of the round's spawn point; always keep the shooter's
own peer; always keep admin/spectator views. No wire change, no client change, no bot change (`12` is
not a bot input). Expected **−38 % of the `12` lane in a spread fight (−6.5 % of fixture egress,
≈ −8 % of rig egress on the run-averaged share)**, 0 % in a packed knife-fight — the rig A/B decides whether real fights are
spread enough. Native parity: mechanism is port-only, but it matches the client's own draw rule exactly,
so no drawn pixel changes; document it as "the relay follows the renderer's visibility", the same
reasoning P697 used for the sector scope.

**P994 — native-shaped batching + compact records for `12` and `213`** (`broadcast.gunfireBatch`, default
**off**; one flag for the format, because it is one encoder/decoder pair). One var-item frame per
(recipient, tick) carrying quantised records: `12` 58 → 20 B payload, `213` 48 → 22 B payload. Expected
**−58.1 % of the two lanes = −16.7 % of fixture egress (≈ −16 % of rig egress)**, with (b) alone as the
conservative fallback if the compact record turns out to need more fields (−3.5 %/≈ −3.4 %). No information
loss; quantisation is visual-only; validate the own-echo de-dup radius and the spark normal against the
quantised values, and keep the decoder accepting the legacy sizes for one release. Ship it as an offline
calculator + codec first (§5 shows the calculator and pins), then the wire flag.

Then, and only after the rig A/B of P993/P994, **P995 — the native model** (§4.a) as the strategic step:
it is the only option that removes the dependency bytes too, and its cost is a client subsystem, not a
wire change. Scope it as: local cosmetic weapon sim for visible ships → keep `213` first (so the bot
metrics stay) → then optionally local sparks. Do not start it before the cheaper two have a rig result,
because it subsumes both.

### 4.8 Rig A/B metric list (P990 §5's list plus this study's)

Run each flag on/off on the rig baseline with `profiler.broadcastBudget` on, same map/sides/bots:

1. **`[bb-other]` rows**: `fmid=12` bytes/msgs and `fmid=213` bytes/msgs per 5 s window, and their % of
   the ordered lane; `fmid=78/26/90` deltas (the dependency) — the direct measure of the saving.
2. **`[bb-category]`/`[broadcast-budget]`**: total bytes, per-peer bytes, egress B/s; `[bb-trend]` to see
   the window-to-window variance.
3. **Tick cost**: `[tickprof]` p50/p95/p99 tick wall, `step:broadcastShipsUpdate`,
   `step:broadcastShipStates`, allocation per tick; `[slowtick]` count.
4. **Transport health**: `[bb-queue]` queuedBytes/orderedPending/`coalescedSnapshots`/
   `droppedStreamFrames`, `[server-counters]` cPacketsOut/cBytesOut.
5. **Update age** (must not move): `[bb-age]` p50/p95/max, `[bb-age-why]` `starved` (must stay ~0),
   `neverPosed`, per-peer `[bb-top]` max.
6. **Bot outcome** (the "did the saving cost us the fight" gate): kills/deaths per bot, own hit rate
   (P539b `EnemyHullHits`/`ShotsFired` from `[bot-combat]`/MCP `[b]` rows), TTK, dodge (no bot dodges
   rounds, so it must not move), flag captures, miner uptime.
7. **Client visual check** (Claude, manual, both HUD styles where relevant): a three-side fight viewed
   from a chase cam and from a spectator: rounds visible at the same distance as before the flag, no
   pop-in at the 5 900 u boundary at normal closing speed, sparks on hull hits, shield flares, hit sounds
   my/other × shield/hull, and the `[proj-diag]` line unchanged in `received≈drawn` (it reports the same
   two counters P542/P697 used).
8. **Exact-window reconciliation**: one `[broadcast-budget] bytes=` + `[bb-other]` pair from the same
   window, so the report's 574k-window inference (§3.8) is replaced by a measurement.

---

## 5. Tests and other working-tree changes

`tests/Allegiance.Core.Tests/P992GunfireTrafficTests.cs` (new; no `src/` change):

| Test | Tier | What it pins |
|---|---|---|
| `Gunfire_frames_are_fixed_size_and_framed_with_one_crc_each` | Fast | `12` = 54 B + 4 CRC, `213` = 44 B + 4 CRC, `213 > 200` |
| `Port_projectile_instance_is_native_sized_plus_the_port_launcher_id` | Fast | native body 52 B vs the port's 54 B (+P542's 2 B), `DataProjectileIGC` = 32 B |
| `Offline_calculator_reproduces_the_measured_fixture_bytes` | Fast | the calculator's stock arm == the measured 14 016 744 + 9 469 440 B and 27 126 504 B stock |
| `Option_table_matches_the_report` | Fast | every option's bytes and % (the §4.6 table), against the calculator |
| `Rig_window_implies_the_shot_rate_and_the_frame_bytes` | Fast | 574 000/5 s → 844 shots/s, 2.11/bot/s, 33.29 MB of `12` bytes, the 56.6 MB ordered-lane / ~119 MB egress bracket |
| `Option_savings_as_a_share_of_rig_egress` | Fast | rig-share savings on the P990-quoted 37.6 % basis: 4.5 / 18.1 / 22.6 / 14.3 / 37.6 % (the review's run-averaged basis, ~28 %, gives ≈ 3.4 / 13 / 16 / 10.6 / 28 %; see the §3.8 correction) |
| `Loopback_showcase_measures_gunfire_traffic_and_the_audience_radius` | Live (artwork) | fan-out identity, `bytes = msgs × frame size` for both ids, quiet-shape zero, the measured shape (shots/hits/near-fraction) the calculator uses, and stock == `12`+`213`+dependency within 1 % |
| `Gunfire_fixture_measurement_from_environment` | Live (artwork) | the same harness for any other shape via `P992_*` env vars (how §3.4-3.6 were taken) |

The calculator is a test-side type (`P992GunfireTrafficTests.TrafficShape` + `Frames`), pure arithmetic,
no server and no wire; the showcase only reads telemetry. Nothing in `src/` was added or changed, so the
P983/P990 off-path byte-identity pins are untouched by construction (and still pass — see the gate).

---

## 6. Gate

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` (`--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`) | **0 warnings, 0 errors** |
| `tools/build-tools.sh` (`--drop-bins`, `DOTNET` wrapper carrying the build-server workaround) | **all 130 tool projects built, 0 warnings**, exit 0 |
| new `P992Gunfire*` tests, 3× alone | **8/8 passed each run, 0 skipped** (3 runs, final tree; artwork from the worktree's gitignored `local.env`) |
| full fast suite | **skipped by the brief's rule** (no `src/` change: "skip the suite and showcase if no src/ change, and say so"). It was attempted once: the shared runner lock (`/tmp/allegiance-test-tiers.lock`) was held by another lane's fast run for the whole 40-minute wait and that run was still going 44 min in, so the wait was abandoned rather than adding a second concurrent fast run on the shared host. The new tests are Fast-tier pure arithmetic (no shared fixture, no clock, no network) plus two Live-tier loopback fixtures, so the suite's Fast cohort is the only place they would have run — they were instead run alone 3× each (above) |
| showcase never-parks 5× | **skipped for the same reason** (no `src/`, sim/flight/bots untouched) |
| existing telemetry pins (the brief's "OFF byte-identical" contract) | P983 `BroadcastTelemetryUnitTests` **11/11**, `BroadcastTelemetryFixtureTests` **7/7**, P990 `P990BroadcastLaneTelemetryTests` **15/15**, P991 `P991FlagOffWirePinTests` **2/2** — all green on this tree (nothing in `src/` changed, so the pins are untouched by construction) |
| `tools/check-glossary-links.py` | **OK** (747 distinct paths) |
| `local.env` present, Skipped count | present; **0 skipped** among the new tests |
| public-repo hygiene | no `/home/...` paths, emails or hostnames in the changed files; report only |

---

## 7. Evidence and scratch

- The measurement rows quoted in §3 are the console output of the two Live tests above, reproducible with
  `dotnet test tests/Allegiance.Core.Tests/Allegiance.Core.Tests.csproj --filter "FullyQualifiedName~P992Gunfire"`.
- Scratch (run logs, the matrix driver): `.scratch/p992/` in this worktree, deleted at the end; nothing
  outside this report is needed to reproduce a number. No report assets (`docs/reports/GUNFIRE_TRAFFIC_P992_assets/`
  not used): every number is inline text and every one is recomputed by the tests.
- No rig run, no remote host, no synthetic load: every measurement is a loopback fixture on this box.

---

## Native intent

Native never tells a client that a ship fired. It tells it what the ship's `stateM` and `controls` are —
in the light record appended to the per-player heavy update (`Inc/Messages.h:890-905`,
`FedSrv/FedSrv.CPP:3844-3846,4179-4196`), in the single-ship update a ship gets when it enters a sector
(`Inc/Messages.h:583-588`, `FedSrv/fsship.cpp:511-548`), and in the active-turret relay for a boarded
gunner (`FedSrv/FedSrv.CPP:4049-4072`) — and every client's own copy of the mission simulation runs every
weapon of every ship in its cluster from that state (`Igc/clusterIGC.cpp:241-255`,
`Igc/shipIGC.cpp:264-341`, `Igc/weaponIGC.cpp:142-348`), at the weapon type's own cadence, with local
spread, local energy/ammo, a locally created round and a locally resolved impact
(`Igc/projectileIGC.cpp:161-230`) whose spark and hit sound are also local
(`WinTrek/trekigc.cpp:2365-2396,4270-4318`). The server's only hit information on the wire is the
fraction block in each HEAVY record, and its only ordnance event is the missile launch
(`FedSrv/FedSrv.CPP:5408-5505`). `PROJECTILE_INSTANCE` has no native sender; `PROJECTILE_HULL_HIT(213)`
does not exist in native at all.

## Modern approach

The port's client is a renderer, so every one of those local facts has to cross the wire: one 58-byte
`PROJECTILE_INSTANCE` frame per shot per viewing peer, one 48-byte `PROJECTILE_HULL_HIT` frame per landed
round per viewing peer, plus the pose/export/status frames the relay orders ahead of them — measured at
112 bytes per shot per recipient, 33 % of all server egress in a rig-shaped fixture, and exactly linear
in `shots × peers-in-sector`. The same study prices the ways out without touching gameplay: cull the
audience to the renderer's own 5 900 u radius (−38 % of both lanes; −14 % of rig egress), batch and
quantise the two records into native-shaped var-item arrays (−58 % of the lanes; −23 % of rig egress),
and only then consider giving the client back a cosmetic weapon sim so the wire carries state instead of
events (−100 % of the gunfire cost; the native model, with native's fire-state latency as the price).
Each of those is a default-off flag and a rig A/B away, and the numbers they would be judged against are
now computed by a test rather than guessed in a report.
