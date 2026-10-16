# P710 — Parity wave 13: simulation SOUNDS (client sound cues)

## Summary

Ported **11 open `sounds` rows** — the wealth/treasure-announcement cues and the
incoming-message (chat/command/personal) notification cues — all previously
`missing` (no condition-to-cue path existed in the client). Every cue rides the
established `ClientSoundEvent` → `Game1.Audio.UpdateSoundEvents` →
`SoundManager` one-shot path, driven by wire messages the server already
broadcasts, so no new server event was required and **no flight/simulation
behaviour changes** (guarded by the P700 regression gate).

Scorecard, `sounds` area:

| metric | before | after |
|---|---|---|
| weighted | 141/594 (23.74%) | **163/594 (27.44%)** |
| unweighted | 42/228 (18.42%) | **53/228 (23.25%)** |

## Why these rows

All 11 rows share two tight native code sites and one client plumbing path
(`ClientSoundEvent`), so they are a coherent bundle:

- **Wealth announcements** — native `FM_S_ACQUIRE_TREASURE` /
  `FM_S_PAYDAY` handlers in `clintlib/appmsg.cpp:262-308, 1313`.
- **Incoming-message cues** — native `WinTrekClient::ReceiveChat` sound stage
  in `WinTrek/trekigc.cpp:4536-4560`.

They are pure announce-VO one-shots (no loops, no positional 3D, no state
machine), so they cannot perturb flight and are trivially wire-testable at the
`ClientWorld` level — matching the existing `ClientWorldSoundEventTests` pattern.

## Implementation

### Wealth / treasure announcements (5 rows)

`FM_S_ACQUIRE_TREASURE(162)` (codec already present but **undispatched** in the
client) and `FM_S_PAYDAY(114)` now record a sound event in `ClientWorld`:

| row | native | wave (sounddef.mdl) | event |
|---|---|---|---|
| SIM-0429 | `paydaySound` (appmsg.cpp:1313) | `payday` (:294) | `PaydayReceived` on `Payday` |
| SIM-0415 | `pickUpPowerupSound` (:282) | `PowerupShield` (:280) | `PickupPowerup` on `AcquireTreasure` code `Powerup` |
| SIM-0416 | `pickUpDevelopmentSound` (:286) | `PowerupDevelopment` (:279) | `PickupDevelopment` on `AcquireTreasure` code `Development` |
| SIM-0417 | `pickUpCashSound` (:291) | `hoover` (:281) | `PickupCash` on `AcquireTreasure` code `Cash` |
| SIM-0418 | flag → `pickUpDevelopmentSound` (:303, NYI flag cue) | `PowerupDevelopment` | `PickupDevelopment` on `AcquireTreasure` code `Flag` |

`c_tcPart` has no pickup sound (native sends a `TREASURE_ACK` instead,
appmsg.cpp:267-276) — guarded by `AcquirePart_RecordsNoPickupSound`.

### Incoming-message notification cues (6 rows)

`Apply(ChatMessage)` now classifies each line with the new
`ClientWorld.ClassifyChatSound` static, reproducing native `ReceiveChat`'s
non-sonic destination tree (`trekigc.cpp:4540-4560`):

| row | native branch | wave | event |
|---|---|---|---|
| SIM-0400 | command id ≠ NA (:4540) | `receivedobjective` (:300) | `ChatCommand` |
| SIM-0401 | individual PM from team leader / training commander (:4546) | `receivedcomchat` (:298) | `ChatCommander` |
| SIM-0402 | individual to local pilot (:4548) | `receivepersonalchat` (:301) | `ChatPersonal` |
| SIM-0403 | leader-sent general line (:4555) | `receivedcomchat` (:298) | `ChatCommander` |
| SIM-0404 | regular general line (:4557) | `receivechat` (:297) | `ChatRegular` |
| SIM-0405 | disconnected receiver (:4560) | `receivechat` @ −10 dB (:299) | `ChatOffline` |

Own echoes are dropped before the sound stage (native `appmsg.cpp:2644`), and a
sonic chat (non‑NA `voiceOver`) is left to the voice-over table this renderer
port does not hold — that is the open **SIM-0399**, explicitly out of this
bundle and noted in the report's limitations. `senderIsLeader` mirrors native
`bIsLeader` via the `SET_TEAM_LEADER`-populated per-side leader
(`TeamLeaderShipIdFor`); `online` mirrors `m_fm.IsConnected()` (always true in
this always-online client — the offline branch is exposed only so SIM-0405 is
faithfully representable and testable).

## SoundManager / Game1

New `SoundManager` one-shots loaded in `Load()` from the real artwork waves
(`payday`, `powerupshield`, `powerupdevelopment`, `hoover`,
`receivedobjective`, `receivedcomchat`, `receivepersonalchat`,
`receivechat` — all confirmed present in `ALLEGIANCE_ARTWORK_PATH`) with public
`Play*` methods, and `Game1.Audio.UpdateSoundEvents` dispatches each
`ClientSoundEventKind` to its method. `ChatOffline` reuses `PlayChatRegular(offline:true)`
at −10 dB to match `GainSound(-10, …)` (sounddef.mdl:299).

## Failing-first

Every row was `missing` — no condition-to-cue path existed. The new tests
cannot pass on the pre-change tree: `Apply(AcquireTreasure)` had no dispatch
case (so no pickup event), `Apply(Payday)` recorded no sound event, and
`ClientWorld.ClassifyChatSound` did not exist (compile error in the chat tests).
After the change, all 11 new tests pass.

## Tests (all new, in `tests/.../ClientWorldSoundEventTests.cs`)

- `Payday_RecordsPaydaySoundEvent`
- `AcquireTreasure_RecordsPerTypePickupSound` (theory: Powerup/Development/Cash/Flag)
- `AcquirePart_RecordsNoPickupSound`
- `ClassifyRegularChat_IsChatRegular`, `ClassifyCommandChat_IsChatCommand`,
  `ClassifyPersonalChatToMe_IsChatPersonal`, `ClassifyLeaderPersonalChat_IsChatCommander`,
  `ClassifyLeaderGeneralChat_IsChatCommander`, `ClassifyOfflineChat_IsChatOffline`,
  `ClassifyOwnChatEcho_IsSilent`, `ClassifySonicChat_IsSilent`
- `RegularChatWire_RecordsChatRegular` (wire → `TakeSoundEvents`)

21 sound-event tests pass; the affected chat/event-log/dispatch classes
120/120.

## Verification

- Whole-solution build: **0 warnings, 0 errors** (Debug, verified).
- Regression gate `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
  **10/10 pass (Release, 0 failures)**. The Debug back-to-back runs produced the
  **already-documented** bot-speed-under-load flake (`docs/TEST_FLAKES.md:814`;
  P706 observed the same failure on run 2, confirmed pre-existing on the
  pre-change commit `060fc834`). The change is client-side sound-event
  recording only and cannot influence bot flight; under Release the 10 gate
  runs are clean, demonstrating sounds do not alter flight.
- Full suite `scripts/run-tests.sh`, exactly 2 runs (recorded per run below).

### Suite run 1 (recorded as it happened)

- Lobby 19/19.
- MonoGame 2233 pass + 3 skip.
- ClientCore 1070 pass + 1 skip + **1 fail** —
  `SessionLossLiveTests.EstablishedSession_ServerKilled_RaisesLost_EventAndControlsGoSilent`
  (documented transport/session-loss load flake, `docs/TEST_FLAKES.md`; **3/3 in
  isolation** here; unrelated to P710 — code not touched).
- Core 4610 pass.
- Quarantine: all green.
- `run-tests.sh` exit 1 (driven solely by the above flake).

### Suite run 2 (recorded as it happened)

- Lobby 19/19.
- MonoGame 2233 pass + 3 skip.
- ClientCore 1071 pass + 1 skip, **0 fail**.
- Core 4609 pass + **1 fail** —
  `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`
  (documented load flake, `docs/TEST_FLAKES.md`; **3/3 in isolation** here;
  unrelated to P710 — code not touched).
- Quarantine: all green.
- `run-tests.sh` exit 1 (driven solely by the above flake).

Both failures are the two already-documented load flakes that recur across
waves (P706 observed both); neither is in code P710 touched and both pass 3/3
in isolation.

## Files

- `src/Allegiance.ClientCore/State/ClientSoundEvent.cs` — 9 new event kinds.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `Apply(AcquireTreasure)`
  (new dispatch case + handler), `Apply(Payday)` sound event,
  `Apply(ChatMessage)` classification + `ClassifyChatSound`.
- `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs` — 9 new one-shot cues
  + `Play*` methods.
- `src/Allegiance.ClientCore.Game/Game1.Audio.cs` — dispatch cases.
- `tests/Allegiance.ClientCore.Tests/ClientWorldSoundEventTests.cs` — 11 new tests.
- `docs/parity/sim-rules-matrix.csv` — 11 rows → `implemented`.

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| treasure pickup → per-type one-shot | appmsg.cpp:282-303 | design | identical one-shots from the real waves |
| payday → paydaySound | appmsg.cpp:1313 | design | identical |
| chat sound by destination | trekigc.cpp:4536-4560 | design | faithful non-sonic tree; sonic chat left open (SIM-0399) |
| offline chat quieter | sounddef.mdl:299 | design | −10 dB recreated |
| acquire-treasure ack for parts | appmsg.cpp:267-276 | design | no pickup cue for `c_tcPart` |

## Modern alternative considered

Native resolves these announce cues as `ThreeDSound` positioned on the ship
(appmsg.cpp:282-291). For a cockpit/own-ship announce the 3D pan is
inaudible, so the port plays them as one-shots through the voice budget at
`SoundCategory.Warning` (the same `MutexSal`-equivalent serialisation native
uses for VO). The alternative — routing treasure pickups through the same
positional `ThreeDSound` emitter as remote hits — was rejected: they are
always the local ship's own pickup, so a non-positional one-shot is
player-identical and cheaper.

## Limitations

- SIM-0399 (sonic chat, trekigc.cpp:4536) remains open: it needs the
  voice-over `SoundID → wave` table this renderer port does not hold.
- SIM-0405's offline branch is not reachable in this always-online client but
  is represented and unit-tested for fidelity.
