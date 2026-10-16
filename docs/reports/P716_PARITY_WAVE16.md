# P716 — Parity wave 16: simulation SOUNDS, fourth bundle (combat & physics feedback)

## Summary

Ported **14 `sounds` rows** in the combat & physics feedback area — everything
the player hears when fighting, dying, launching and warping. Three are
genuinely new (the own ship's warp-transit `jumpSound` cue, covering the native
departure and arrival call sites, plus a new ClientWorld collision-wire test);
eleven were already substantially wired by earlier waves but sat stale
`partial`/`missing` in the matrix (`evidence: inferred`), so this wave verifies
them against the running code, adds the missing trigger-level tests, upgrades
the evidence to `tested`, and corrects the matrix — the P714-style
reconciliation, extended to the combat-feedback cluster. **No new server event
was required** (STATION_DESTROYED(59), COLLISION_EVENT(216) and TELEPORT_ACK(102)
are all already broadcast by this port's server; the sector-jump cue is derived
client-side from the own ship's existing SET_CLUSTER) and **no flight/simulation
behaviour changes** (guarded by the P700 regression gate).

Scorecard, `sounds` area:

| metric | before | after |
|---|---|---|
| weighted | 239/594 (40.24%) | **282/594 (47.47%)** |
| unweighted | 86/228 (37.72%) | **100/228 (43.86%)** |

## Why these rows

All 14 rows are one cluster — **combat & physics feedback one-shots** — sharing
the native `PlaySoundEffect` combat region and this port's
`ClientSoundEvent → Game1.Audio → SoundManager` one-shot path (plus the
positional hit/collision/explosion path built in P463/P463c/P574):

- **Impacts** — `collisionSound` (4 branches, `Igc/shipIGC.cpp:834,1004,1189,1193`)
  and the station-destroyed cues (`WinTrek/trekigc.cpp:4335,4337`,
  `clintlib/appmsg.cpp:2896`);
- **Launch** — `GetLaunchSound` (missile, `clintlib/clintlib.cpp:3103`);
- **System/combat messages** — `newCriticalMsgSound`/`newNonCriticalMsgSound`
  (`WinTrek/trekigc.cpp:3009,3014,3075,3080`);
- **Sector transition** — `jumpSound` (`WinTrek/trekigc.cpp:3998,4415`,
  `clintlib/appmsg.cpp:1057`).

They are one-shots (no loops, no state machine), so they cannot perturb flight,
and they are testable at the `ClientWorld` wire level — the established
`ClientWorldSoundEventTests` pattern.

## Implementation

### New: the own ship's warp-transit `jumpSound` (SIM-0394, SIM-0398)

Native plays the `jumpSound` cue both when the player's ship leaves a cluster
(`WinTrek/trekigc.cpp:3998`, `pship->SetCluster`) and when it emerges beside the
destination aleph (`WinTrek/trekigc.cpp:4415`). This pure-renderer client
observes a warp transit as the own ship's `SET_CLUSTER` landing in a **new**
sector beside its aleph — the exact P585 aleph-emergence edge already computed in
`ClientWorld.Apply(SetCluster)` (`ownSectorChanged` + `NearestWarpInSector`).
At that point the client now records a new `ClientSoundEventKind.SectorJump`,
which `Game1.Audio.UpdateSoundEvents` dispatches to `SoundManager.PlayJump`
(the `jumpin` wave native's jumpSound uses). Because departure and arrival are
the same wave, the cue fires **once per warp crossing**. Guarded to the own
ship, and only when actually emerging from a warp (never on a same-sector
re-seat, a launch into an empty sector, or a remote ship's arrival).

- `ClientSoundEvent.cs` — new `SectorJump` kind (doc-cited to
  trekigc.cpp:3998,4415).
- `ClientWorld.cs:2191-2200` — record `SectorJump` inside the P585
  aleph-emergence gate.
- `Game1.Audio.cs:518` — dispatch `SectorJump` → `PlayJump`.

### New trigger test: the collision wire (SIM-0244..0247)

The four native `collisionSound` branches (`Igc/shipIGC.cpp:834,1004,1189,1193`)
are served by the P463c positional collision cue: `COLLISION_EVENT(216)` →
`ClientWorld.Apply(CollisionEvent)` buffers a `CollisionEntity`, drained by
`Game1.Audio.UpdateCollisionSounds` → `PlayCollisionSound`. The trigger had no
dedicated ClientWorld-level test; P716 adds
`CollisionWire_IsBufferedForTheCollisionSoundPass`. All four native branches
collapse to the single per-impact cue (documented generalization — the port's
collision pass is distance-attenuated and not participant-branch-specific).

### Reconciliation (11 rows, already implemented — matrix corrected)

| row | cue | wiring | evidence |
|---|---|---|---|
| SIM-0396 | station destroyed (friendly) | `Apply(StationDestroyed)` → `StationDestroyed` → `PlayStationEvent` (appmsg.cpp:548-551) | `ClientWorldSoundEventTests.OwnStationDestroyed_...` |
| SIM-0397 | station destroyed (enemy) | same path → `StationEnemyDestroyed` | `ClientWorldSoundEventTests.EnemyStationDestroyed_...` |
| SIM-0434 | station destroyed (KILL_SHIP branch) | same `StationDestroyed` path (appmsg.cpp:2896 plays the same per-type destroyedSound) | `ClientWorldSoundEventTests.OwnStationDestroyed_...` |
| SIM-0442 | missile launch | `UpdateMissileSounds` local-launch detection → `PlayMissileLaunch` (clintlib.cpp:3103) | P236 MissileSoundPlanner (already tested) |
| SIM-0390..0393 | critical/non-critical message cues | `Game1.Feedback.UpdateFeedback` → `PlayCriticalMessage`/`PlayNonCriticalMessage` (trekigc.cpp:3009/3014/3075/3080) | wiring audit (`verified`) |
| SIM-0427 | teleport jump | `Apply(TeleportAck)` → `TeleportArrived` → `PlayJump` (appmsg.cpp:1901/1057) | `P638FeedbackWireTests.TeleportAck_Accepted_QueuesArrivalJump` |

All were `partial`/`missing` in the matrix despite the code being live; each row
is now `implemented` with the wiring cited in `port_cite_or_reason` and
`evidence` upgraded.

## Failing-first

The genuinely-new `SectorJump` path is failing-first: `ClientSoundEventKind.SectorJump`
did not exist before P716, so the new tests (`OwnShipWarpsToNewSectorBesideAleph_RecordsSectorJump`
etc.) cannot compile on the pre-change tree (CS0104-style missing-member compile
error, the same failing-first mechanism P711 documented); after the change all
pass. The collision-wire test likewise exercises a buffer that predates P716 but
had no ClientWorld-level assertion; it passes now. The 11 reconciliation rows were
already working code (station destroyed, missile launch, teleport, feedback) —
verified by the existing tests + a wiring audit, not by a new failing-first test
(the P714 precedent).

## Tests

`tests/.../ClientWorldSoundEventTests.cs` (+4):
- `OwnShipWarpsToNewSectorBesideAleph_RecordsSectorJump`
- `OwnShipSameSectorReSeat_NoSectorJump`
- `RemoteShipSectorArrival_NoSectorJump`
- `CollisionWire_IsBufferedForTheCollisionSoundPass`

35/35 `ClientWorldSoundEventTests` pass; 67/67 across the affected classes
(`ClientWorldSoundEventTests`, `ClientWorldAlephTransitTests` (SET_CLUSTER),
`P638FeedbackWireTests`, `FedMsgDispatchCoverageTests`, `ClientWorldHullHitTests`).

## Verification

- Whole-solution build (`dotnet build AllegianceCore.slnx -c Debug`):
  **0 warnings, 0 errors** (also 0/0 in Release).
- Failing-first tests: see above (new `SectorJump` kind is a pre-change compile
  error).
- Regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **10/10 pass (Release, 0 failures)**. The change is client-side sound-event
  recording/gating only and cannot alter bot flight.
- Full suite `scripts/run-tests.sh`, exactly 2 runs (recorded per run below).

### Suite run 1 (recorded as it happened)
- Lobby 19/19.
- MonoGame 2242 pass + 3 skip, 0 fail.
- ClientCore 1117 pass + 1 skip, 0 fail.
- Core 4707 pass, 0 fail.
- Quarantine: ClientCore 3 + Core 18 + Lobby 19, all green.
- `run-tests.sh` exit **0** (fully clean run).

### Suite run 2 (recorded as it happened)
- Lobby 19/19.
- MonoGame 2242 pass + 3 skip, 0 fail.
- ClientCore 1117 pass + 1 skip, 0 fail.
- Core 4706 pass + **1 fail** —
  `MapGenRingLiveProofTests.GeneratedHiLo_PeerFliesThroughWarp_ToTracedNeighbour`
  (live-net map-gen ring/warp-trace proof; **5/6 in isolation** here — an
  intermittent load flake exacerbated by concurrent Codex suite runs on this
  host; unrelated to P716 — code not touched; added to `docs/TEST_FLAKES.md`).
- Quarantine: ClientCore 3 + Core 18, all green.
- `run-tests.sh` exit 1 (driven solely by the documented flake above).

Across both runs P716's code contributed **zero failures**; the single run-2
failure is a pre-existing, documented-in-isolation live-net load flake in code
P716 does not touch.

## Files

- `src/Allegiance.ClientCore/State/ClientSoundEvent.cs` — new `SectorJump` kind.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `Apply(SetCluster)` records
  `SectorJump` on own-sector aleph emergence.
- `src/Allegiance.ClientCore.Game/Game1.Audio.cs` — `SectorJump` dispatch → `PlayJump`.
- `tests/.../ClientWorldSoundEventTests.cs` — 4 new tests.
- `docs/parity/sim-rules-matrix.csv` — 14 rows → `implemented` (LF endings kept).
- `docs/TEST_FLAKES.md` — documents the run-2 live-net load flake.
- `docs/reports/P716_PARITY_WAVE16.md` — this report.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| warp-transit jumpSound | trekigc.cpp:3998,4415 | design | once-per-crossing on SET_CLUSTER aleph emergence (arrival side; departure is the same wave) |
| collision sound | shipIGC.cpp:834,1004,1189,1193 | design | one distance-attenuated cue per COLLISION_EVENT(216) (branches collapsed) |
| station destroyed/captured VO | appmsg.cpp:548-551; trekigc.cpp:4335,4337; appmsg.cpp:2896 | design | per-type station voice on FM_S_STATION_DESTROYED(59) |
| missile launch cue | clintlib.cpp:3103 | design | local-launch detection → PlayMissileLaunch (fixed stem vs per-magazine core launch SoundID) |
| critical/non-critical msg cues | trekigc.cpp:3009,3014,3075,3080 | design | played for the wired feedback-line tip sites (subset of native PostText call sites) |
| teleport jump | appmsg.cpp:1901/1057 | design | TELEPORT_ACK → PlayJump (P638) |

## Modern alternative considered

Native computes collisions and station deaths in-process and plays their cues
against its IGC. This pure-renderer client instead consumes the server's already-
broadcast `COLLISION_EVENT(216)` / `STATION_DESTROYED(59)` and derives the warp-
transit cue from the existing SET_CLUSTER — no client-side physics sim and zero
extra wire, player-identical for a cockpit/own-ship announce. The four native
collision branches (ship↔ship, ship↔asteroid, ship↔station) are intentionally
collapsed to one positional cue per impact point; a modern engine would do the
same with a single impact-sound emitter. The fixed missile-launch stem
(substituting a per-magazine core SoundID) matches how this port already voices
missiles and is documented.

## Limitations

- SIM-0394/0398 fire the jump cue on the **arrival** half of the transit (the
  SET_CLUSTER emergence), representing both native halves with the one shared
  wave; a pure departure-side-only cue is not separately observable in this
  client (the client learns of a warp by arriving).
- SIM-0390..0393 cover the tip-line feedback sites the renderer actually renders
  (P578 eject/death/rescue, P594 queued orders), not every native `PostText`
  call site; the remaining native producers have no analogue in this renderer.
- The critical/non-critical reconciliation is a wiring audit (`verified`
  evidence) — the cue is MonoGame-level, so there is no ClientWorld-level
  trigger test for those four rows.
