# P771 — SIM-0276: autopilot-disengage sound only on native's two cancel transitions

## Native rule

Native plays `salAutopilotDisengageSound` at exactly two client-side call sites,
both already gated on "autopilot was engaged":

| Transition | Native file:line | Sound? |
| --- | --- | --- |
| Manual flight input cancels autopilot | `WinTrek/WinTrek.cpp:8272-8316` (sound at `:8315`) | **Yes** |
| Toggle autopilot key turns it off | `WinTrek/WinTrek.cpp:10359-10366` (sound at `:10365`) | **Yes** |
| Accepted-order autopilot cancel | `WinTrek/WinTrek.cpp:8877-8898` (sound at `:8895`) | **No** — the block is commented out |
| Board as passenger / other `SetAutoPilot(false)` callers | `clintlib/appmsg.cpp:2045`, `BaseClient::SetAutoPilot` (`clintlib/clintlib.cpp:1590-1593`) | **No** |
| TK_TargetNothing clear | `WinTrek/WinTrek.cpp:10446-10449` | **No** — only `SetTarget(NULL, c_cidNone)` |

Every other autopilot-off transition in native is silent.

## Port change

`src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` now declares an internal
`AutopilotDisengageReason` enum (`:3470`) with one value per real transition,
and the decision rule is narrowed to the two native sound cases
(`ShouldPlayAutopilotDisengageCue` at `:3489`):

```csharp
internal static bool ShouldPlayAutopilotDisengageCue(bool wasEngaged, AutopilotDisengageReason reason)
    => wasEngaged && reason is AutopilotDisengageReason.ToggleOff or AutopilotDisengageReason.ManualInput;
```

`DisengageAutopilot` now takes `AutopilotDisengageReason` (`:3495`) and all 10
production callers pass the matching enum value:

| Caller | File:line | Reason | Native sound? |
| --- | --- | --- | --- |
| `ToggleAutopilotForTarget` | `Game1.FlightInput.cs:181` | `ToggleOff` | Yes |
| Manual-input check | `Game1.FlightInput.cs:2149` | `ManualInput` | Yes |
| Target unresolvable | `Game1.FlightInput.cs:2191` | `TargetLost` | No |
| Cluster arrival | `Game1.FlightInput.cs:2674` | `Arrived` | No |
| Docked (station approach) | `Game1.FlightInput.cs:2726` | `Docked` | No |
| Rescued (pod pickup) | `Game1.FlightInput.cs:2753` | `Rescued` | No |
| Same-sector arrival | `Game1.FlightInput.cs:2771` | `Arrived` | No |
| Enter docked hangar | `Game1.Panes.cs:94` | `Docked` | No |
| Enter local spectator | `Game1.SkirmishObserver.cs:48` | `Spectating` | No |
| TK_TargetNothing / clear target | `Game1.Targeting.cs:1400` | `TargetNothing` | No |

`SoundManager.PlayAutopilotDisengage()` (`Audio/SoundManager.cs:1066-1071`) is
untouched and remains the single playback site.

## Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/AutopilotDisengageSoundTests.cs`
was rewritten to pin the rule and the wiring:

- `[Theory]` over every enum value × `wasEngaged` (16 cases). Only
  `ToggleOff`/`ManualInput` with `wasEngaged == true` expect `true`.
- Source-guard test verifying `DisengageAutopilot` calls
  `ShouldPlayAutopilotDisengageCue(wasEngaged, reason)` and is the only caller
  of `PlayAutopilotDisengage(` in `src/Allegiance.ClientCore.Game`.
- Source-guard tests that the manual-input branch passes `ManualInput` and the
  toggle branch passes `ToggleOff`.
- Source-guard test that no `DisengageAutopilot("...")` string-reason calls
  remain.

### Measured failing count on the old identity rule

Temporarily restoring the old `=> wasEngaged` rule made the theory test fail
for every silent reason with `wasEngaged == true`:

```
Failed: 6, Passed: 14, Total: 20
```

The six failing cases are the silent transitions `TargetLost`, `Arrived`,
`Docked`, `Rescued`, `Spectating`, and `TargetNothing` when the autopilot had
been engaged. Log: `~/allegiance-evidence/P771/autopilot_identity_rule_test.log`.

## Verification

- Whole-solution build: **0 warnings, 0 errors**.
- `AutopilotDisengageSoundTests` run alone: **3/3 passed** (20 tests each).
- Fast-tier suite: **9596 tests passed**, 3 skipped corpus-backed `MeshyPackTests`;
  full log at `~/allegiance-evidence/P771/fast-tier.log`.

No server/core code changed, so no AOT publish was required.

## SIM-0276 matrix row

Updated `docs/parity/sim-rules-matrix.csv` row `SIM-0276` to `implemented`
with current port cites (`ShouldPlayAutopilotDisengageCue :3489`,
`DisengageAutopilot :3495`, manual-input caller `:2149`, toggle-off caller
`:181`) and the new test names as evidence.
