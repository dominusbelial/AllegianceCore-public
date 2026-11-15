# P1027 — MISSIONPARAMS accepts NaN / Infinity floats (R1 §4 lead)

Status: **complete**. Fix + tests in the working tree; no commits made.

## Root cause

`src/Allegiance.Core/Wire/MissionParamsMsg.cs` — `Invalid(bool bFromClient)` (pre-fix `:451-560`;
the port of native `MissionParams::Invalid`, `Igc/igc.h:1339-1506`). Every float guard is a pure
`<`/`>` comparison — `FGoalTeamMoney < 0f || FGoalTeamMoney > 9f` (pre-fix `:492`, now `:532`),
`FHe3Density` (now `:530`), `FStartingMoney` (now `:534`), both treasure rates (now `:514-517`,
`:528-529`). **NaN returns false for every comparison, so it passes all of them**, and ±Infinity
passes wherever a bound is open. Three floats had **no check at all**: `DtGameLength`,
`FStartCountdown`, `FRestartCountdown`.

The message carries **eight `float` fields and no doubles** (declarations `MissionParamsMsg.cs:192-216`;
native struct fields `Igc/igc.h:1158,1160-1163,1170-1171,1184`):
`FGoalTeamMoney`, `NPlayerSectorTreasureRate`, `NNeutralSectorTreasureRate`, `DtGameLength`,
`FHe3Density`, `FStartCountdown`, `FRestartCountdown`, `FStartingMoney`.

Exploit path (confirmed by test before the fix): the mission owner sends FM_CS_MISSIONPARAMS(113) →
`src/Allegiance.Server/Server.MissionParams.cs` `HandleMissionParams`, the `msg.Invalid(bFromClient: true)`
gate at `:169` (native `TrapHackBoot(missionparams.Invalid(true) == NULL)`, `FedSrv.CPP:7534`) returns
null → `SetMissionParams` writes the goals into the mission (`:329-337`, `GameLengthSeconds = msg.DtGameLength`)
→ the server re-broadcasts the whole MissionDef (`SendMissionDef`, `:230`) → the poisoned values are
persisted into the lobby result report (`src/Allegiance.Server/Server.Lobby.cs:1537`
`GoalCountdownLength`, `:1539` `GoalProsperityMoney`). NaN also corrupts the winning-condition logic:
`IsCountdownGame => DtGameLength != 0f` / `IsProsperityGame => FGoalTeamMoney != 0f`
(`MissionParamsMsg.cs:585,588`) treat NaN as "goal set", so even the no-winning-conditions guard does
not fire.

**Pre-fix confirmation run** (test file written first, executed against unfixed code):
`Failed: 27, Passed: 10, Skipped: 0`. `Invalid(true)` returned **null** (accepted) for NaN in all 8
fields, for −Infinity in the 5 fields with no lower bound (both treasure rates, `DtGameLength`, both
countdowns) and for +Infinity in the 3 fields with no upper bound (`DtGameLength`, both countdowns);
the remaining ±Infinity cases were rejected only incidentally by native's range messages. The
server-level test showed the NaN edit adopted and **MISSIONDEF(38) re-advertised to both seated peers**.

## Native citation + verdict

- Native `MissionParams::Invalid(bool bFromClient) const` — `/srv/corpus/Allegiance/src/Igc/igc.h:1339-1506`
  ("do some data validation... should do this is a cleaner way", `:1341`).
- Its float guards: `nNeutralSectorTreasureRate > 1.0f/60.0f` / `nPlayerSectorTreasureRate > 1.0f/60.0f`
  (`igc.h:1384-1390`), `fHe3Density < 0.0f || > 9.0f` (`:1416-1419`), `fGoalTeamMoney` (`:1420-1423`),
  `fStartingMoney` (`:1424-1427`), `treasure rates > 0.01` (`:1490-1493`).
- `dtGameLength` (`igc.h:1162`, "Seconds till end of game, 0 == no limit"), `fStartCountdown`/`fRestartCountdown`
  (`igc.h:1170-1171`) are **never range-checked** in native `Invalid`; Reset defaults are
  `dtGameLength = 0.0f` (`:1305`), `fStartCountdown = 15.0f` (`:1273`), `fRestartCountdown = 60.0f` (`:1274`).
- **Native does with NaN exactly what the port did**: NaN fails every `<`/`>` comparison, so native
  `Invalid()` accepts it, and the native server (same `FedSrv.CPP:7534` gate) stores and re-broadcasts it.

**Verdict: deliberate port hardening.** Rejecting non-finite floats and bounding `DtGameLength` has no
native counterpart; native has the same comparisons and the same hole. Recorded as a design divergence
in the parity rows (below), not as a parity break.

## The fix

`src/Allegiance.Core/Wire/MissionParamsMsg.cs`, top of the `if (bFromClient)` leg of `Invalid`
(`:456-492`), before any goal logic reads the floats:

1. `!float.IsFinite(...)` → per-field refusal in the existing message style (`:459-483`):
   `"GoalTeamMoney must be a finite number."`, likewise StartingMoney, He3Density,
   PlayerSectorTreasureRate, NeutralSectorTreasureRate, GameLength, StartCountdown, RestartCountdown.
2. `DtGameLength < 0f || DtGameLength > 86400f` → `"GameLength must be between 0 and 86400."` (`:491-492`).

Range rationale (nothing a real producer makes is rejected): 0 disables the countdown (`igc.h:1162`);
the host-edit UI steps `CountdownTimeValues` = [0, 10, 20, 30, 60, 120, 180, 240, 300, 360] minutes →
**0..21600 s** (`src/Allegiance.ClientCore/State/MissionSettingsForm.cs:72,193`, the native list
`artwork/newgamescreen.mdl:329` via `WinTrek/newgamescreen.cpp:1010`); the port's countdown preset is
1800 s (`MissionGoals.Preset`, `src/Allegiance.Core/Model/MissionGoals.cs:41`); native's training
mission uses 600 s (`clintlib/clintlib.cpp:755`); the longest match the port itself hosts is a day
(`ServerConfig.cs:35`, `MatchCapSeconds ≤ 86400`). 86400 leaves 4× headroom over the UI maximum.

`Invalid(bFromClient: true)` is the single trust boundary — every production caller passes `true`:
`Server.MissionParams.cs:169`, `PlayerGameProvisioning.cs:110`, `GameCreationStore.cs:93,126` (Lobby),
client-side `CreateGameWizard.cs:153` and `MissionSettingsForm.cs:265`.

## Tests

New file `tests/Allegiance.Core.Tests/P1027MissionParamsNonFiniteTests.cs` — 37 cases:
NaN/+Inf/−Inf × all 8 float fields rejected with a "finite" reason (24 theory cases); exact per-field
messages (1); `DtGameLength` producer values 0/600/1800/3600/21600/86400 stay valid (6), −60/86460
rejected (1), every host-edit countdown preset valid (1); regression guards — valid baseline (1), every
`MissionGoals.Preset` stays valid, "none" refused only by native's no-winning-condition rule (1),
native `Reset` defaults stay valid (1); server level through the real dispatcher: legal countdown edit
relays MISSIONDEF, then owner NaN edit → refused, **nothing re-broadcast**, stored params and mission
goals unchanged and finite (1, artwork-gated, ran unskipped).

| Command (lane SDK) | Passed | Failed | Skipped |
| --- | --- | --- | --- |
| `dotnet test tests/Allegiance.Core.Tests --no-build --filter "FullyQualifiedName~P1027"` ×3 | 37 / 37 / 37 | 0 | 0 |
| same project, `--filter "FullyQualifiedName~MissionParams"` (existing + new) | 48 | 0 | 0 |
| `tests/Allegiance.ClientCore.Tests`, `--filter "FullyQualifiedName~MissionParams"` | 2 | 0 | 0 |
| pre-fix confirmation run of the P1027 filter | 10 | **27** | 0 |

Adjacent regressions (other `Invalid(true)` callers): `P853HostEditServerTests` 13/0/0,
`P853bHostEditUiTests` 13/0/0, `P871WizardChatAndFileListTests` 6/0/0.
Full `dotnet build AllegianceCore.slnx` (brief flags): **0 warnings / 0 errors**.
`scripts/parity-scorecard.py` validates `sim-rules-matrix.csv` and the full default run exits 0.

## Parity rows updated

- `docs/parity/p867-onappmessage.csv` — `FM_CS_MISSIONPARAMS` row: hardening recorded in
  `classification_or_difference`, the 8 new tests appended to `tests`.
- `docs/parity/sim-rules-matrix.csv` — `SIM-0628` row: same additions (`notes` / `evidence`).

## Unfinished / observations

- None required by the brief. Noted for the orchestrator:
  - The **common leg** (`Invalid(false)`) still accepts non-finite floats; the brief scoped the fix to
    the client leg, and no production caller uses the common leg alone.
  - Bare server config `gameLengthSeconds` is validated only finite/≥0 (`ServerConfig.cs:86-92`), so a
    config > 86400 s would seed fine but the owner's client-side echo-edit would now be refused by the
    new range. No shipped config, preset or script uses a value above 21600 s.
- Changed files: `src/Allegiance.Core/Wire/MissionParamsMsg.cs`, the two parity CSVs, the new test
  file, this report. `P1027_BRIEF.md` removed from the working tree (must not ride in the change set).

---

**Native intent:** native's `Invalid()` was a settings-screen data-validation convenience ("do some
data validation... should do this is a cleaner way", `igc.h:1341`) written for honest in-range floats
from the host UI; it never contemplated IEEE special values — NaN silently satisfies "in range" for
every `<`/`>` comparison, and native stores and re-broadcasts it just like the port did. The intent to
preserve is *"the server adopts only sane mission params from the owner"*.

**Modern approach:** treat the wire as adversarial and close the hole at the single trust boundary:
every production caller passes `Invalid(bFromClient: true)`, so the client leg now rejects non-finite
floats per field with a clear reason string, and bounds the one float native never checked
(`dtGameLength`) to the union of what real producers emit (host-UI presets ≤ 21600 s, port presets,
showcase cap ≤ 86400 s) — all legitimate values keep working, and the divergence from native is
recorded as deliberate hardening in the parity matrix.
