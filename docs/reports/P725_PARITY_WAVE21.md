# P725 — Parity wave 21: simulation SOUNDS, ninth bundle (drone/constructor in-flight chatter)

## Summary

Ported the **drone/constructor in-flight chatter** voice family (the last
tractable slice of the station/economy sounds cluster). **4 `sounds` rows → `implemented`**
(SIM-0249 constructorRunning, SIM-0250 droneTooMuchDamage, SIM-0252
droneComingHomeEmpty, SIM-0253 droneTooCrowded), and **10 more documented**
(2 refined to `partial` with a concrete reason, 8 clarified) rather than
silently skipped. The server-side AI edges for those four rows **already
existed** in the port's sim (`Ship.SendDroneChat` at the native runaway /
coming-home-empty / too-crowded moments), but they were **silently broken**:
they broadcast SoundIDs that were **+1001 off** the true native ordinals
(Ship.cs local literals 1327/1329/1342/1343 vs. the P723-verified 326/328/341/342)
and routed through the un-overridden `IIgcSite.SendChat` no-op — so the client
could never resolve them and nothing was audible. P725 fixes the ordinals to
P723's rigorous `DroneChatterCue` values, routes the chatter through a **new
`IIgcSite.DroneChatEvent` seam** that the server host converts into a TEAM-chat
broadcast carrying the SoundID as the wire `VoiceOver` (the exact pipe P723's
where-to pleas use), and verifies end-to-end with a **live seated-peer wire
test**. **No flight/simulation behaviour changes** (chatter is emitted on
pre-existing AI decisions; only the id + delivery path changed), guarded by the
P700 regression gate.

Scorecard, `sounds` area:

| metric | before | after |
|---|---|---|
| weighted | 391/592 (66.05%) | **404/592 (68.24%)** |
| unweighted | 143/227 (63.00%) | **147/227 (64.76%)** |

## Why these rows

The task lists the remaining sounds families as weapons/ordnance, station/economy,
UI/screens and ambient/positional loops (UI/screens stays with Codex's interface
waves, and the per-launcher weapons reload/deplete rows need per-magazine counts
the 47-byte ship update does not stream — P723's documented blocker). Within
station/economy, the largest coherent remaining cluster is the **drone/constructor
in-flight chatter**: P723 already built the complete client side (the
`DroneChatterCue` SoundID→clip resolver + all nine `.ogg`), and the sim already
ran the AI edges — the only missing pieces were (a) the wrong SoundIDs and
(b) a host that forwards the chatter to peers. This wave is exactly the
"server-touching follow-up" P723's limitations section called for ("a
server-touching follow-up can enable them without further client work"). It is
one coherent family sharing one new seam (`IIgcSite.DroneChatEvent`) and one
broadcast path (`Server.BroadcastDroneChat` → `BroadcastTeamChat`).

## Implementation

### The bug: wrong ordinals + a mute seam

`Ship.cs` emitted the four chatters with local constants `DroneTooMuchDamageSound=1327`,
`DroneComingHomeEmptySound=1329`, `DroneTooCrowdedSound=1342`,
`ConstructorRunningSound=1343` — every one **+1001** above the true sounds.h
ordinal that P723 recomputed rigorously (326/328/341/342, `DroneChatterCue`,
sounds.h:380/382/395/396). Worse, `SendDroneChat` called `Mission.Site.SendChat`
(the IIgcSite no-op default — `ServerHost` never overrode it), so the chatter
died at the seam. Net effect: four wired sim edges producing **silent** chatter.

### Fix: verified ordinals + a real broadcast seam (SIM-0249, 0250, 0252, 0253)

- **`Ship.cs`** — the four constants now **alias `DroneChatterCue`** (the P723-verified
  values), eliminating the duplicated-wrong-literal hazard; `SendDroneChat`
  routes through the new `IIgcSite.DroneChatEvent(this, side, sound, message)`.
- **`IIgcSite.DroneChatEvent`** — new C#-only seam (no-op default), the concrete-`Ship`
  analogue of native `GetIgcSite()->SendChat(this, CHAT_TEAM, ..., sound, text)`
  (`shipIGC.cpp:1895-1901,2171,2199`), following the established
  `ProjectileCreatedEvent`/`MineCreatedEvent` pattern.
- **`ServerHost.DroneChat`** hook + override — raises the seam to the owning `Server`
  (sim-thread, no lock), exactly like the other broadcast hooks.
- **`Server.Lifecycle`** — `_host.DroneChat = BroadcastDroneChat;`
- **`Server.Economy.BroadcastDroneChat`** — converts the seam into the side's TEAM
  chat via the existing `BroadcastTeamChat`, carrying the SoundID verbatim as
  the wire `VoiceOver` (same pipe as P723's where-to pleas). The renderer
  resolves it via `Game1.Session.ProcessIncomingQuickChatVoices` →
  `DroneChatterCue.Resolve` (P723).

### Rows documented, not silently skipped

| row | native | disposition | reason |
|---|---|---|---|
| SIM-0251 | droneEnemyOnScope, shipIGC.cpp:1901 | `partial` (was `missing`) | the native non-damage "enemy on scope" retreat branch is unreachable in the port: the sim's runaway is damage-driven (hull/shield, Ship.cs:7394-7397), and upstream the native enemy-scope heuristic is commented out; porting it needs a new enemy-driven escape = a flight-behaviour change, out of scope |
| SIM-0255 | GetConstructorNeedRockSound, shipIGC.cpp:2319 | `partial` | per-station-type core SoundID (builder's `ConstructorNeedRockSound`) — resolving id→wave needs the core sound-def/VO table, the separate SIM-0399 task |
| SIM-0236 | GetCompletingConstruction, buildingeffectigc.h:320 | `partial` | per-station-type completion SoundID (same SIM-0399 core-table dependency); the sim has no completing-construction chat edge |
| SIM-0258–0262, 0269–0270 | droneInTransitSound ×7, shipIGC.cpp:2386/2401/2561/2692/2779, shipIGC.h:2408/2449 | `missing`→documented | native wingman combat-chatter edges ("Attacking … to defend …", "Returning to defend …", "Switching target to …"); the port's wingman AI does not emit these per-site in-transit chats — adding them is a server-AI follow-up, not a sound-path change |

All are constant or core-data drone voices P723's resolver is already prepared
for (droneInTransit→`vo_miner_intransit`), so enabling them later is client-free.

## Failing-first

The genuinely-new pieces are failing-first: `IIgcSite.DroneChatEvent`,
`ServerHost.DroneChat` and `RecordingIgcSite.DroneChatEvent` did not exist before
P725, so the new tests cannot **compile** on the pre-change tree (CS1061 — the
demonstrated mechanism, verified by stashing the production changes and rebuilding:
`error CS1061: 'RecordingIgcSite' does not contain a definition for 'DroneChatEvent'`).
Semantically, the live wire test asserts a seated teammate receives `VoiceOver==341`;
pre-change the chatter broadcast 1342 through the mute `SendChat` no-op, so no such
chat could arrive.

## Tests

- `tests/.../P725DroneChatterSeamTests.cs` (new, Core, 3 cases): pins the four
  native ordinals to the verified values **and** that each resolves in
  `DroneChatterCue` (the +1001 bug guard); a real crowded-miner edge routes
  `DroneTooCrowdedSound` (341) through `RecordingIgcSite.DroneChatEvent`;
  `RecordingIgcSite` observes the seam.
- `tests/.../P725DroneChatterWireLiveTests.cs` (new, Core `LiveNetwork`): boots a
  real local server + seated bot, invokes the crowded-miner chatter edge, and
  asserts the teammate receives a TEAM chat with `VoiceOver==341` that the client
  can resolve — the wire-level proof of the new seam.

New tests **3× clean** (4/4 each).

Targeted existing tests, **3× clean**: drone/bucket/constructor/live-chat/site
classes 23/23 each (`DroneBucketSpawnsDroneLiveTests`,
`StationBucketSpawnsConstructorLiveTests`, `IgcSiteTests`, `LiveChatWireTests`,
`BuildingConstructionLiveTests`, `DirectNpcBuildOrderLiveTests`);
`ClientWorldSoundEventTests` 46/46 each; MonoGame P723 drone-chatter/asset
classes 14/14 each.

## Verification

- Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings, 0 errors**
  in both Debug and Release.
- Failing-first: see above (new seam member is a pre-change compile error).
- Regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **5/5 pass (Release)** — 1m34s, 1m37s, 1m33s, 1m33s, 1m37s. The change emits an
  informational team chat on pre-existing AI edges; it cannot alter bot flight.
- **Full suite (`scripts/run-tests.sh`): not run this session — blocked
  environmentally.** Multiple concurrent Codex agents (P727–P734) are actively
  running heavy live suites on this shared box right now (P734's `verify.py suites`
  is live; load average 7.85). This is the documented shared-box condition under
  which `run-tests.sh` hangs in its live-network tests (session memory;
  P723's wave 20 report documented the identical block and precedent: "don't
  attempt the 2× suite while they run"). P725's change is a small, additive,
  informational team-chat broadcast on pre-existing AI edges + an ID fix; it
  cannot cause a server-side sim/live-network failure. All affected code paths
  are covered by the targeted tests above (3× clean) and the live wire test.

## Files

- `src/Allegiance.Core/Sim/Ship.cs` — 4 chatter constants alias `DroneChatterCue`
  (fixes the +1001 ordinals); `SendDroneChat` → `IIgcSite.DroneChatEvent`.
- `src/Allegiance.Core/Sim/IIgcSite.cs` — new `DroneChatEvent` seam.
- `src/Allegiance.Core/Sim/RecordingIgcSite.cs` — records `DroneChatEvent`.
- `src/Allegiance.Server/ServerHost.cs` — `DroneChat` hook + override.
- `src/Allegiance.Server/Server.Lifecycle.cs` — wire `_host.DroneChat`.
- `src/Allegiance.Server/Server.Economy.cs` — `BroadcastDroneChat` → `BroadcastTeamChat`.
- `tests/.../P725DroneChatterSeamTests.cs`, `tests/.../P725DroneChatterWireLiveTests.cs` (new).
- `docs/parity/sim-rules-matrix.csv` — 4 rows → `implemented`, 2 refined to `partial`,
  8 clarified; **LF endings kept** (validated by `scripts/parity-scorecard.py`).
- `docs/reports/P725_PARITY_WAVE21.md` — this report.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| constructorRunning / droneTooMuchDamage retreat chatter | shipIGC.cpp:1895,1898 | design | same AI edge (Ship runaway), now broadcasts the corrected SoundID via the DroneChatEvent seam |
| droneComingHomeEmpty (displaced miner) | shipIGC.cpp:2171 | design | same AI edge (RedirectDisplacedMiner), corrected id (328) |
| droneTooCrowded (contested rock) | shipIGC.cpp:2199 | design | same AI edge (ComplainMiningCrowded), corrected id (341) |
| droneEnemyOnScope non-damage retreat | shipIGC.cpp:1901 | design | partial — sim runaway is damage-driven; enemy-scope retreat not modelled |
| per-station-type completion/need-rock voices | buildingeffectigc.h:320, shipIGC.cpp:2319 | design | partial — need the core sound-def/VO table (SIM-0399) |
| wingman in-transit combat chatter | shipIGC.cpp:2386..2779, shipIGC.h:2408/2449 | design | documented — sim wingman AI doesn't emit it (server-AI follow-up) |

## Modern alternative considered

Native selects the drone-chatter SoundID in-process and `SendChat`s it to the
side; the client resolves the id locally. The port already follows the modern
"send an event id, resolve the asset on the receiver" split (P723's where-to
pleas). This wave merely (a) fixes the ids and (b) gives the sim's existing
chatter a real delivery seam — the modern "raise a typed event, broadcast it"
pattern already used everywhere else in `ServerHost`. No audio-engine upgrade
warranted. The one honest deviation: the previous code shipped +1001-inflated
ordinals (a transcription defect, not a design choice) — restored to the
P723-verified values rather than propagated.

## Limitations

- The **full-suite run is the one outstanding verification item**, blocked this
  session by the concurrent Codex agents (P734 `verify.py suites` live; load 7.85)
  — the documented shared-box condition (see Verification). P725's change is
  additive/informational and cannot cause the server-side live-network hangs.
- SIM-0251 / 0255 / 0236 / 0258–0262 / 0269–0270 remain `partial`/`missing`
  **with the reasons above** (AI behaviour not modelled / core sound-def table),
  never silently skipped. The `DroneChatterCue` resolver + assets already cover
  every one of these voices, so a later wave can enable them client-free once the
  sim emits (or the core supplies) the trigger.
