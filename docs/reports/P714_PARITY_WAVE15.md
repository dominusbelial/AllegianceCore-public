# P714 — Parity wave 15: simulation SOUNDS, third bundle

## Summary

Ported **13 `sounds` rows** in the leadership / investment / objective /
autonomy announce area. Five are new wire-driven objective & status announce
VOs (commander, investor, and the three flag/artifact steal cues); eight are
already-built cues that sat stale `missing` in the matrix and are reconciled to
`implemented` with evidence and asset tests (autopilot engage/disengage, rescue
and teleport jump). Every cue rides the established `ClientSoundEvent` →
`Game1.Audio.UpdateSoundEvents` → `SoundManager` one-shot path. **No new server
event was required** (SET_TEAM_LEADER(121), AUTODONATE(116) and GAIN_FLAG(156)
are all already broadcast by this port's server) and **no flight/simulation
behaviour changes** (guarded by the P700 regression gate).

Scorecard, `sounds` area:

| metric | before | after |
|---|---|---|
| weighted | 206/594 (34.68%) | **239/594 (40.24%)** |
| unweighted | 73/228 (32.02%) | **86/228 (37.72%)** |

## Why these rows

All five new rows share one native region — the announce-VO call sites in
`clintlib/appmsg.cpp` (`FM_S_SET_TEAM_LEADER:2965`, `FM_S_AUTODONATE:1238`,
`FM_S_GAIN_FLAG:3149,3156,3169`) — and one client plumbing path
(`ClientSoundEvent`), so they are a coherent bundle matching the P710 announce
pattern. The eight reconciliation rows were audited to be genuinely already
implemented (autopilot engage/disengage via the single `EngageAutopilotToward` /
`DisengageAutopilot` chokepoints; rescue/teleport jump via `PlayJump`) — the
P711-style matrix correction (cf. SIM-0410).

## Implementation

### New objective & status announce VOs (5 rows)

| row | native | wave (sounddef.mdl) | event |
|---|---|---|---|
| SIM-0435 | commanderSound (appmsg.cpp:2965) | vo_sal_madecommander (:779) | `CommanderElected` on SET_TEAM_LEADER announcing own ship |
| SIM-0428 | investorSound (:1238) | vo_sal_madeinvestor (:780) | `InvestorReceived` on AUTODONATE 0→1 donor count |
| SIM-0436 | enemyFlagLostSound (:3149) | vo_stolen_enemyflag (:773) | `EnemyFlagStolen` on GAIN_FLAG (teammate steals enemy flag) |
| SIM-0437 | artifactFoundSound (:3156) | vo_sal_artifact (:776) | `ArtifactFound` on GAIN_FLAG (teammate finds artifact) |
| SIM-0438 | flagLostSound (:3169) | vo_stolen_flag (:771) | `OurFlagStolen` on GAIN_FLAG (enemy carries our flag) |

`ClientWorld` changes:

- `Apply(SetTeamLeader)` now records `CommanderElected` when the announced leader
  is the local ship (native `pplayer == MyPlayerInfo()`, appmsg.cpp:2965).
- `Apply(AutoDonateUpdate)` tracks the previous donate target (native `pshipOld`,
  appmsg.cpp:1202), and when a teammate (not ourselves) starts donating to us and
  the donor count becomes exactly 1, records `InvestorReceived` (native
  appmsg.cpp:1227-1238).
- New `Apply(GainFlag)` + dispatch case (GAIN_FLAG was previously not applied by
  the client). Classification is faithful to native appmsg.cpp:3137-3170:
  recipient-on-our-side + flag origin ≠ lobby(-2 sentinel) → EnemyFlagStolen;
  origin == lobby → ArtifactFound; otherwise origin == our side → OurFlagStolen.
  Needs the recipient ship's side (native `pship->GetSide()`); when the recipient
  is not in our entity table the cue is skipped (documented limitation — the
  whole-mission announce can name carriers outside our visible sector).

`SoundManager`: five new one-shots loaded from the real artwork waves and public
`PlayMadeCommander` / `PlayMadeInvestor` / `PlayStolenEnemyFlag` /
`PlayArtifactFound` / `PlayStolenFlag`. `Game1.Audio` dispatches the five new
`ClientSoundEventKind`s. The 5 `.ogg` were copied from the artwork tree into
`Content/Sounds` and registered in `Content.mgcb`.

### Reconciliation (8 rows, already implemented — matrix corrected)

| row | cue | wiring |
|---|---|---|
| SIM-0276, SIM-0292 | salAutopilotDisengageSound | `DisengageAutopilot` → `PlayAutopilotDisengage` |
| SIM-0293, SIM-0297, SIM-0406, SIM-0414 | salAutopilotEngageSound | `EngageAutopilotToward` → `PlayAutopilotEngage` (incl. order-execute, target and eject-rescue paths) |
| SIM-0420 | jumpSound (rescued) | PLAYER_RESCUED → `Game1.Feedback.PlayJump` (appmsg.cpp:339) |
| SIM-0431 | personalJumpSound (teleport) | TELEPORT_ACK → `TeleportArrived` → `PlayJump` (appmsg.cpp:1894) |

All were `missing` in the matrix despite the code being live; each row is now
`implemented` with the wiring cited in `port_cite_or_reason` and `evidence=verified`.

## Failing-first

The 5 new rows were genuinely `missing` (GAIN_FLAG was not even dispatched, and
`Apply(SetTeamLeader)`/`Apply(AutoDonateUpdate)` recorded no sound event): the new
tests cannot pass on the pre-change tree (the new `ClientSoundEventKind` members
did not exist — a compile error in `ClientWorldSoundEventTests`, and `Apply(GainFlag)`
did not exist). After the change all tests pass. The 8 reconciliation rows were
already working code (like P711's SIM-0410); they are verified by wiring audits +
the new asset-bundling test, not by a failing-first test.

## Tests

`tests/Allegiance.ClientCore.Tests/ClientWorldSoundEventTests.cs` (10 new):
- `BecomingTeamLeader_RecordsCommanderElected`, `AnotherShipBecomesLeader_RecordsNoSound`
- `FirstDonorBeginsDonatingToMe_RecordsInvestor`, `SecondDonorDoesNotRefireInvestor`,
  `DonatingMyself_NoInvestor`, `StoppingDonation_NoInvestor`
- `TeammateStealsEnemyFlag_RecordsEnemyFlagStolen`, `TeammateFindsArtifact_RecordsArtifactFound`,
  `EnemyStealsOurFlag_RecordsOurFlagStolen`, `SecureOrUnclassifiedGainFlag_NoSound`

`tests/Allegiance.ClientCore.MonoGame.Tests/P714AnnounceSoundTests.cs` (new):
- `AnnounceAndAutonomyWaves_AreBundledForTheRealSoundPlayer` — all 5 new + 3
  reconciled `.ogg` present in `Content/Sounds` and registered in `Content.mgcb`.

31/31 `ClientWorldSoundEventTests` pass; 931/931 non-live ClientCore pass;
the new MonoGame asset test passes.

## Verification

- Whole-solution build: **0 warnings, 0 errors** (Debug, verified).
- Regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **10/10 pass (Release, 0 failures)**. Sounds are client-side announce VOs and
  cannot alter bot flight.
- Full suite `scripts/run-tests.sh`, exactly 2 runs (recorded per run below).

### Suite run 1 (recorded as it happened)
- Lobby 19/19.
- MonoGame 2242 pass + 3 skip, 0 fail.
- ClientCore 1113 pass + 1 skip, 0 fail.
- Core 4706 pass + **1 fail** —
  `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy`
  (documented load flake, `docs/TEST_FLAKES.md`; **3/3 in isolation** here; unrelated
  to P714 — code not touched).
- Quarantine: ClientCore 3 pass; Core quarantine clean (LiveTeamLobby flake noted benign).
- `run-tests.sh` exit 1 (driven solely by the Core flake).

### Suite run 2 (recorded as it happened)
- Lobby 19/19.
- MonoGame 2241 pass + 3 skip + **1 fail** —
  `ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy`
  (documented live-chat load flake, `docs/TEST_FLAKES.md`; **3/3 in isolation**
  here; unrelated to P714 — code not touched).
- ClientCore 1113 pass + 1 skip, **0 fail**.
- Core 4706 pass + **1 fail** —
  `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`
  (documented load flake, `docs/TEST_FLAKES.md`; **3/3 in isolation** here;
  unrelated to P714 — code not touched).
- Quarantine: ClientCore 3 + Core 18, all green.
- `run-tests.sh` exit 1 (driven solely by the two documented flakes).

Across both runs P714's code contributed **zero failures**; every failure is a
pre-existing, documented load flake in code P714 does not touch, each verified
**3/3 in isolation**.

## Files

- `src/Allegiance.ClientCore/State/ClientSoundEvent.cs` — 5 new event kinds.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `Apply(SetTeamLeader)` commander
  cue, `Apply(AutoDonateUpdate)` investor cue, new `Apply(GainFlag)` + dispatch case.
- `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs` — 5 new one-shots + Play methods.
- `src/Allegiance.ClientCore.Game/Game1.Audio.cs` — dispatch cases.
- `src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb` + 5 new `.ogg` waves.
- `tests/.../ClientWorldSoundEventTests.cs` (10 new tests), `P714AnnounceSoundTests.cs` (new).
- `docs/parity/sim-rules-matrix.csv` — 13 rows → `implemented`.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| become-leader VO | appmsg.cpp:2965 | design | identical one-shot on SET_TEAM_LEADER for own ship |
| first-investor VO | appmsg.cpp:1227-1238 | design | identical 0→1 donor one-shot |
| flag steal/found/lost VOs | appmsg.cpp:3149-3169 | design | faithful GAIN_FLAG classification (sender-side known); skips when recipient side unknowable |
| autopilot engage/disengage | WinTrek.cpp:8315,10365,10372,10692; trekigc.cpp:4882; appmsg.cpp:237 | design | already wired via Engage/DisengageAutopilot chokepoints |
| rescue/teleport jump | appmsg.cpp:339,1894 | design | already wired via PlayJump |

## Modern alternative considered

Same as P710/P711: these are local announce VOs native resolves as `ThreeDSound`
positioned on the ship; the port plays them as one-shots through the voice budget
(`SoundCategory.Warning`) — player-identical for cockpit/own-ship announces and
cheaper. For GAIN_FLAG the one genuinely novel choice is recipient-side
resolution: native reads the full IGC (always has the carrier's side), whereas
this pure-renderer client knows only ships in its entity table, so an out-of-view
carrier's cue is skipped rather than guessed. No modern audio engine upgrade was
warranted for a handful of one-shot announce VOs.

## Limitations

- GAIN_FLAG classification requires the recipient ship's side from the client
  entity table; a whole-mission announce naming a carrier outside the visible
  sector produces no cue (safe under-classification, never a wrong cue).
- Flags/artifact gameplay is opt-in (`FlagsGame`); the cues are wired to the wire
  message and fire whenever it arrives, independent of the config.
- The 8 reconciliation rows were already implemented by earlier waves (P501/P524
  autopilot, P578/P638 jump); this wave corrects the stale matrix entries with
  evidence, it does not add new behaviour for them.
