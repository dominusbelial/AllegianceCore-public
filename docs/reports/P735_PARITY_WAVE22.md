# P735 — Parity wave 22: simulation SOUNDS, tenth bundle (own-ship state notification one-shots)

## Summary

Ported **3 open `sounds` rows → `implemented`** on the established
`ClientSoundEvent → Game1.Audio → SoundManager` one-shot path: **SIM-0248
cloak-engage** (`salCloakEngageSound`), **SIM-0268 part-pickup**
(`pickUpPartSound`) and a **reconciliation of SIM-0275 personal-jump**
(served by the existing P638 `TeleportArrived → jumpin` path). A fourth change
is a **suite-hygiene fix**: `P652EconomyDroneAiTests.Enemy_lockout_complains_with_native_voice_id_after_initial_backoff`
still asserted the pre-P725 `SendChat`+`1342` path; P725 corrected the ordinal
to `341` and routed the chatter through the new `DroneChatEvent` seam, leaving
that Core test red on master. It is a genuine P725 regression (not caused by
this wave — P735 touches no `Allegiance.Core` code) and is corrected here.

The remaining open rows are **documented, never silently skipped** (below):
every family has a concrete blocker recorded in the matrix. The biggest one —
the **salutation/notification sal\* family** — has had its **asset-inventory
blocker lifted**: every wave it needs exists in `~/Development/Artwork`
(`vo_sal_mia`, `vo_sal_traitor`, `vo_sal_recruitsarrived`, `vo_sal_enemyjoiners`,
`vo_sal_enemyleaves`, `vo_sal_shipisrepaired`, `vo_sal_reloading*`,
`vo_sal_chaffdepleted`, `vo_sal_missilesdepleted`, `vo_sal_dispenserempty`), so
enabling it later is asset-free; only the client **wire/trigger** (a client
`QUIT_MISSION` handler + server broadcasts of every quit reason; per-magazine
count streaming) and the **core sound-def/VO table (SIM-0399)** remain.

Scorecard, `sounds` area:

| metric | before | after |
|---|---|---|
| weighted | 404/592 (68.24%) | **410/592 (69.26%)** |
| unweighted | 147/227 (64.76%) | **150/227 (66.08%)** |

## Why these rows

The task lists the remaining sounds families as weapons/ordnance,
station/economy, UI/screens and ambient/positional loops. Reconnaissance of the
**80 open rows** (scorecard) shows the tractable surface is narrower than the
family names suggest, and almost every large cluster is blocked by a specific,
recorded constraint:

- **UI/screens** (SIM-0317–0338) — explicitly **out of scope** (Codex's
  interface waves P718/P720/P722).
- **Weapons/armament reload** (SIM-0263–0266, 0443–0453) — need **per-magazine
  counts** the 47-byte ship update does not stream (P717/P723 documented
  blocker); assets now available in Artwork.
- **Presence/side-change salutations** (SIM-0454–0465) — native fires them in
  the **client's** `clintlib ReceiveChat` from player-quit/join side events;
  the port's server broadcasts `QUIT_MISSION` **only** on the LinkDead and
  switching-sides paths (the other quit reasons are not broadcast), and the
  client has **no `QuitMission`/join handler** — a client wire handler + full
  server quit-reason broadcasts are required (a separate, server-touching
  follow-up). Assets now available in Artwork.
- **Drone in-transit chatter** (SIM-0258–0262, 0269–0270) — sim wingman AI
  doesn't emit those per-site chats (P725 documented; server-AI follow-up).
- **Command ack VOs** (SIM-0294/0295/0298) — played **to the sender** as an
  individual chat; "automatic command acknowledgement trigger not traced"
  (peer-command flow).
- **Station completion / need-rock** (SIM-0236, SIM-0255) — per-station core
  SoundID → the SIM-0399 core sound-def/VO table (separate task).
- **Ambient positional loops** (SIM-0345/0347/0348/0411/0412) — partly wired
  (`partial`); generic dynamic ambient-id coverage and per-mount
  continuous-charge/manned-turret-rotation state incomplete.

Within that constrained surface, the genuinely **asset-present, fully
client-side, low-risk** rows that share the `ClientSoundEvent` one-shot path are
the **own-ship state / acquisition notification** cluster — exactly the three
here plus the pre-existing P638 jump path. This is the "station/economy +
weapons/ordnance feedback" family's most faithful, self-contained slice.

## Implementation

### SIM-0248 — cloak engage (new client one-shot)

Native plays `salCloakEngageSound` server-side when a ship's `CloakActive`
state bit goes false→true (`Igc/shipIGC.cpp:1608`). The port mirrors it as a
client one-shot on the **local ship's** own streamed `StateM`
(`ShipControlState.CloakActive = 0x800`):
- `ClientWorld.UpdateOwnStatusAlarms` gains a `_prevOwnCloaked` edge detector →
  records `ClientSoundEventKind.CloakEngaged` exactly once per engagement.
- `Game1.UpdateSoundEvents` → `SoundManager.PlayCloakEngage()`.
- Wave `vo_sal_cloakengage` (sounddef.mdl:807) copied from Artwork into
  `Content/Sounds` + registered in `Content.mgcb`.

### SIM-0268 — part pickup (new client one-shot)

Native plays `pickUpPartSound` (wave `hoover`, sounddef.mdl:278) at the
acquiring ship **server-side** (`Igc/shipIGC.h:1307`); its client
`FM_S_ACQUIRE_TREASURE` handler is intentionally silent for `c_tcPart`
(appmsg.cpp:266–275 — it only sends a `TREASURE_ACK`). The port reproduces the
**player-facing** cue on the same local `c_tcPart` pickup:
- `ClientWorld.Apply(AcquireTreasure)`: `TreasureCode.Part → ClientSoundEventKind.PickupPart`
  (was previously "no sound here").
- `Game1.UpdateSoundEvents` → `SoundManager.PlayPickupPart()` (wave `hoover`,
  already bundled).

### SIM-0275 — personal jump (reconciled)

Native plays `personalJumpSound` (wave `jumpin`) in two overlapping places: the
teleport-dock relocation (`appmsg.cpp:1901`) and the post-relocation camera
transition (`WinTrek.cpp:6567`). The port already serves the teleport-relocation
cue via **P638** (`ClientSoundEventKind.TeleportArrived` → `SoundManager.PlayJump`,
Game1.Audio.cs:428–432). That path plays the same `jumpin` personal-jump sound,
so SIM-0275 is **reconciled to `implemented`** (the WinTrek.cpp:6567 trigger is
the redundant camera half of the same relocation cue).

### Suite hygiene — P652 stale assertion (P725 regression)

`Enemy_lockout_complains_with_native_voice_id_after_initial_backoff` asserted
`site.Calls` contained `SendChat(...)` with the **pre-P725** inflated ordinal
`1342`. P725 corrected the crowd-complaint ordinal to `341` and moved drone
chatter onto the `IIgcSite.DroneChatEvent` seam. Updated the three assertions to
`DroneChatEvent(...)` + `341` (the P725-verified values), preserving the test's
intent (two locked-out miners voice the crowd complaint after the initial
backoff cooldown, before it).

## Failing-first

The genuinely-new pieces are failing-first: `ClientSoundEventKind.CloakEngaged`
and `PickupPart` did not exist before P735, so the updated/new world tests
**cannot compile** on the pre-change tree — demonstrated by stashing the
production changes and rebuilding:
`error CS0117: 'ClientSoundEventKind' does not contain a definition for 'PickupPart'`
and the same for `'CloakEngaged'`. The new `P735SoundAssetTests` also fails
pre-change because `vo_sal_cloakengage.ogg` was absent. The P652 test fix is the
reverse: it **failed** on today's code (Expected 2, Actual 0 — no `SendChat`
with `1342` because P725's seam emits `DroneChatEvent`+`341`) and passes after.

## Tests

- `tests/.../ClientWorldSoundEventTests.cs` — 3 new cloak-edge cases
  (engage records `CloakEngaged`; already-active does not refire; uncloak is
  silent) and the `AcquirePart` case rewritten from "no sound" to
  `PickupPart` (SIM-0268). 49/49.
- `tests/.../P735SoundAssetTests.cs` (new, MonoGame) — `vo_sal_cloakengage`
  present in `Content/Sounds` + registered in `Content.mgcb`; `hoover`
  (SIM-0268) bundled. 1/1.
- `tests/.../P652EconomyDroneAiTests.cs` — stale P725 assertion corrected.

New/affected tests **3× clean** (49/49, 1/1, and P652 64/64 each).

Targeted existing tests, **3× clean**: `ShipStatusAlarmSoundTests` 12/12,
`LiveTreasurePickupTests` 4/4, `P714AnnounceSoundTests` + `P719StationEconomySoundTests`
+ `P717DeploySoundTests` 3/3.

## Verification

- Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings, 0
  errors** in both Debug and Release.
- Failing-first: see above (new event kinds are pre-change compile errors; new
  asset pre-change fails the asset test; the P652 test failed pre-fix).
- Regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **5/5 pass (Release)** — 1m37s, 1m36s, 1m33s, 1m35s, 1m34s. The change records
  client-side one-shot events only; it cannot alter bot flight.
- **Full suite (`scripts/run-tests.sh`), exactly 1 run** (the lighter gate),
  recorded as it happened:

  | pass | result |
  |---|---|
  | Lobby MAIN | 19/19, 0 fail |
  | MonoGame MAIN | 2319/2322 (3 skip), 0 fail |
  | ClientCore MAIN | 1179/1180 (1 skip), 0 fail |
  | Core MAIN | 4755/4757, **2 fail** |
  | Quarantine | ClientCore 3/3; Core 1 flake (passes in isolation) |

  The two Core failures, both outside this change:
  - `TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`
    — **documented live-wire load flake** (TEST_FLAKES.md has numerous entries);
    passes **3/3 in isolation** here.
  - `P652EconomyDroneAiTests.Enemy_lockout_complains_with_native_voice_id_after_initial_backoff`
    — **P725 stale assertion** (see above), failed 1/64 consistently in
    isolation; corrected and now passes **3/3**.

  P735's change is confined to the client sound path and `Content`; it cannot
  cause a server/sim/live-network failure, and the suite finished (exit 1 solely
  from the two attributed failures — one documented flake, one now fixed).

## Files

- `src/Allegiance.ClientCore/State/ClientSoundEvent.cs` — new `CloakEngaged`,
  `PickupPart` kinds.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — cloak edge in
  `UpdateOwnStatusAlarms` (SIM-0248); `c_tcPart` → `PickupPart` in
  `Apply(AcquireTreasure)` (SIM-0268).
- `src/Allegiance.ClientCore.Game/Game1.Audio.cs` — `CloakEngaged`, `PickupPart`
  cases in `UpdateSoundEvents`.
- `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs` — `_cloakEngage`,
  `_pickupPart` fields + `Load()` + `PlayCloakEngage`, `PlayPickupPart` (+dispose).
- `src/Allegiance.ClientCore.MonoGame/Content/Sounds/vo_sal_cloakengage.ogg`
  (new, from Artwork) + `Content/Content.mgcb` registration.
- `tests/.../ClientWorldSoundEventTests.cs`, `tests/.../P735SoundAssetTests.cs`
  (new), `tests/.../P652EconomyDroneAiTests.cs` (stale-assertion fix).
- `docs/parity/sim-rules-matrix.csv` — 3 rows → `implemented` (SIM-0248, 0268,
  0275), evidence `verified`; **LF endings kept** (validated by
  `scripts/parity-scorecard.py`).
- `docs/reports/P735_PARITY_WAVE22.md` — this report.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| cloak-engage VO | shipIGC.cpp:1608 | design | local one-shot on the own ship's CloakActive stateM edge false→true |
| part-pickup "hoover" | shipIGC.h:1307 (server); appmsg.cpp:266-275 (client handler silent) | design | reproduces the player-facing cue on the same local `c_tcPart` pickup |
| personal-jump "jumpin" | WinTrek.cpp:6567 + appmsg.cpp:1901 | design | reconciled — served by P638 TeleportArrived → PlayJump (jumpin) |
| pre-P725 +1001 ordinals / SendChat seam | (P725 regression in a test) | bug | corrected to DroneChatEvent + 341 (P725-verified), matching native sounds.h ordinals |

## Modern alternative considered

Native fires these as server-side `PlayNotificationSound` calls whose buffers
are played at a `SoundSite`; the port sends a typed event id
(`ClientSoundEventKind`) and resolves the wave on the receiver — the modern
"emit an event, resolve the asset client-side" split already used across the
port. No audio-engine upgrade warranted; the two new one-shots are plain
cockpit-range cues on the existing voice budget path (SerializedSound / MutexSal
group), so they respect the voice budget without extra work. The honest
deviation is the part-pickup cue: native's client `ACQUIRE_TREASURE` handler is
silent for parts because native plays it via the server-side notification; the
port lacks that positional-notification path, so it reproduces the same
player-facing cue on the pickup event — player-identical, smaller.

## Limitations

- **SIM-0248's disengage** (`salCloakDisengageSound`, sounddef.mdl:808) is not a
  separate row and native only voices the engage; not added.
- **Large families remain `partial`/`missing` WITH reasons** (never silently
  skipped): presence/side-change salutations need a client `QUIT_MISSION`/join
  handler + full server quit-reason broadcasts; reload/armament need per-magazine
  counts streamed; drone in-transit chatter needs server wingman AI; command ack
  VOs need the peer command-ack flow; station completion/need-rock need the
  SIM-0399 core sound-def table; UI/screen one-shots stay with Codex's interface
  waves. **All of the sal\* assets the first three need are now present in
  `~/Development/Artwork`**, so a server-touching follow-up can enable them
  client-free on assets.
- Full suite completed in **one** run (per the lighter gate). No second run was
  attempted. P735's change is client-only and additive; the two attributed Core
  failures are a documented load flake and a corrected P725 stale assertion.

## P738 merge onto master

Integrated the `master` merge target (P735–P737) into `integ/batch2` with
file edits only. Cloak-engage and part-pickup producers, event kinds, playback
cases and waves survive alongside the earlier sound waves. New eager loads use
P732's `LoadSound` registration so available native serialized templates join
the existing mutex/priority path.

The incoming P652 test's `341` assertion is superseded by the already verified
P732 native enum correction. Retained the stronger existing `DroneChatEvent`
assertions with SoundID **1342** and exactly two crowded-miner complaints.
Native `~/Development/Allegiance/src/Igc/igc.h:456-464` starts the included
sound enum after 1000; `~/Development/Allegiance/src/Igc/sounds.h:395` names
`droneTooCrowded`. The P725 delivery seam is preserved.

Solution Release build: **0 warnings, 0 errors**. Incoming/affected coverage:
**223 methods, each in three fresh isolated processes**, with
**three passes after the last failure for every method**. Showcase: **5/5 passed**.
Full measured suite results, flake qualifications and the combined scorecard
are recorded in [P737's integration notes](P737_PARITY_WAVE24.md#p738-merge-onto-master)
and `P738_master_assets/summary.json`.
