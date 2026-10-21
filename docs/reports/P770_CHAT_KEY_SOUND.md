# P770 — SIM-0317/0318/0319: chat-key sound gates + pinning tests

Base: `master` @ `e96b994322b0edadb5b688289c49c416ac15a750`  
Branch: `pi/p770`

## Native rule

`WinTrek/console.cpp` defines the compose-state enum order (lines 524–529):

```cpp
c_csNotComposing        = 0,
c_csComposeMouseCommand = 1,
c_csComposeChat         = 2,
c_csComposeCommand      = 3,
c_csComposeShell        = 4,
c_csComposeQuick        = 5
```

The three call sites for `chatKeySound` are gated as follows:

| Row | Native site | Native gate | File:line |
|-----|-------------|-------------|-----------|
| SIM-0317 | `ConsoleImage::OnVerbButton` | `m_csComposeState != c_csComposeShell` | `WinTrek/console.cpp:1266` |
| SIM-0318 | `ConsoleImage::OnChar` | `m_csComposeState > c_csComposeMouseCommand` | `WinTrek/console.cpp:2337` |
| SIM-0319 | `ConsoleImage::StartChat` | unconditional | `WinTrek/console.cpp:2423` |

## Port change

Added a pure static decision class `Allegiance.ClientCore.MonoGame.ChatKeySound` in:

- `src/Allegiance.ClientCore.Game/ChatKeySound.cs`

Each production site now calls the matching decision before playing the sound:

| Row | Production site | Decision used | File:line |
|-----|-----------------|---------------|-----------|
| SIM-0317 | Command-bar verb press | `ChatKeySound.OnVerb(_composeVisualState)` | `Game1.Commanding.cs:364` |
| SIM-0318 | Text-input append | `ChatKeySound.OnChar(_composeVisualState)` | `Game1.Session.cs:1192` |
| SIM-0319 | Enter / StartChat | `ChatKeySound.OnStartChat()` | `Game1.FlightInput.cs:1026` |

Claude review fix: the decisions use `LiveComposeState` (Game1.ConsoleModes.cs), derived from `_chatActive`/`_qcMenuOpen` at the moment of the call. `_composeVisualState` was one frame late (it updates only in `UpdateConsoleOverlay`, before `HandleChatCompose` sets `_chatActive`), so the first typed character could miss its click, and it stays `Chat` while the pane slides out.

### Reachability note

In this port `_composeVisualState` is only ever set to `Chat`, `Quick`, or `NotComposing`. `Shell` and `MouseCommand` are not currently reachable production states. The gates are kept anyway so the port matches native semantics if those states are ever wired.

## Tests

New test class: `tests/Allegiance.ClientCore.MonoGame.Tests/P770ChatKeySoundTests.cs`

- **Decision-function coverage** — every `NativeComposeState` value is exercised for `OnVerb` and `OnChar`; `OnStartChat` is asserted to always return `true`.
- **Source guards** — each production method body is extracted and verified to contain the matching `ChatKeySound.*` call, with the call occurring before the raw `_sound?.PlayChatKey()` invocation.

The source guards fail on the pre-change code: when the three production files were temporarily reverted to `HEAD`, the tests reported `missing ChatKeySound.OnVerb call`, `missing ChatKeySound.OnChar call`, and `missing ChatKeySound.OnStartChat call` (see raw log).

## Verification results

| Check | Result |
|-------|--------|
| Whole-solution build | **0 warnings, 0 errors** |
| `P770ChatKeySoundTests` new tests ×3 | **16/16 passed each run** |
| `tools/run-tests.sh --tier fast` | **9594 tests PASS (276.5 s)** |

The fast-tier run showed 3 `NotExecuted` tests in `Allegiance.ClientCore.MonoGame.Tests` (corpus-skipped tests unrelated to this change).

## Evidence

- Small in-repo summary: `docs/reports/P770_assets/summary.json`
- Raw logs (outside the repo): `~/allegiance-evidence/P770/`
  - `before-tests.log` — source-guard failures against the unmodified production files
  - `fast-tier.log` — full fast-tier output
  - `fast-summary.json` — runner summary JSON

## Matrix update

Updated only the three target rows in `docs/parity/sim-rules-matrix.csv`:

- `SIM-0317` → `implemented`, evidence `tested`, cites corrected
- `SIM-0318` → `implemented`, evidence `tested`, cites corrected
- `SIM-0319` → `implemented`, evidence `tested`, cites corrected

CSV column count verified as 16; line endings remain LF.
