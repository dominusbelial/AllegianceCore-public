# P746 — kill credit on ejection

Date: 2026-10-19. Resumed dispatcher snapshot: `dbdab447358a97b` (the existing
P746 WIP; no commit or push was made during this continuation). Pre-P746 base:
`b7e410e4`.

## Result

`BroadcastKillShip` awards eligible kills before the victim becomes a lifepod.
For a nonzero event against a non-lifepod on another side, the credited player
receives one kill, combat-rating adjustment and the existing class-score credit;
the ship receives its applicable kill bonus, and the credited side gains one kill.
The kills-win calculation runs before the eject/death branch. Ejection still
increments `AddEjection` without a death; a true death still increments death
counters and resets the victim's kill bonus.

One `KILL_SHIP(105)` reaches every peer in the victim's mission, including
off-sector peers, before `EJECT(77)`. An ejection sets `bDeathCredit=false`;
`bKillCredit` reflects eligibility independently. EJECT and combat effects keep
their sector audience. Station, probe, mine and ship/turret credit preserve the
object type, id and side, and increment the side exactly once. Zero-amount events
send no KILL_SHIP and award no kill.

The client already distinguishes ejection from death using `bDeathCredit`:
ejections preserve the live entity and show ejection text. Non-ship credit now
also checks object type before resolving a ship name, claiming a personal kill,
or requesting the port's ship-only camera focus;
overlapping station/probe/mine and ship ids cannot produce incorrect
ship-attributed feed text.

The resumed tests exposed a necessary ordering fix: the port immediately rebuilds
and migrates a mission inside its game-over callback. Calling it in the scoring
block removed KILL_SHIP/EJECT recipients and finalized scores before AddEjection.
Victory is now calculated there, with its callback published after the ship event
finishes (including all early-return death paths). Winning ejection/death tests
assert message order and the final player scoreboard. Finale and siege code were
not changed.

## Native evidence

Paths below are relative to `~/Development/Allegiance/src/`, inspected locally.

| Rule | Native source |
| --- | --- |
| Decide true death versus ejection; reset experience only on true death | `FedSrv/FedSrv.CPP:11772-11788` |
| Only nonzero events construct KILL_SHIP; death flag equals `bKilled` | `FedSrv/FedSrv.CPP:11791-11798` |
| Credit requires a side, non-lifepod victim and different sides | `FedSrv/FedSrv.CPP:11805-11814` |
| Ship kill, combat rating, experience and damage-share scoring precede ejection | `FedSrv/FedSrv.CPP:11816-11861` |
| Ship AddKill increments both player and side counters | `FedSrv/fsship.h:44-48`, `Igc/shipIGC.h:1778-1782` |
| Non-ship credit calls the side directly; then check kills victory | `FedSrv/FedSrv.CPP:11867-11880` |
| Preserve credited object identity even when no kill is awarded | `FedSrv/FedSrv.CPP:11908-11912` |
| Send KILL_SHIP to `GetGroupMission()` with guaranteed delivery and flush, including ejections | `FedSrv/FedSrv.CPP:11914-11915`; this is inside the nonzero-event block and before the eject branch |
| Ejection counts separately, then EJECT goes to sector flying peers | `FedSrv/FedSrv.CPP:11918-11921,12020` |
| True death announces exit, then increments player deaths | `FedSrv/FedSrv.CPP:12023-12032` |
| GameOver records a winner; the mission update processes it later | `FedSrv/fsmission.cpp:2691-2695,2508-2511` |
| A clock expiry can choose a winner by kills/deaths with active sides; an exact tie draws | `FedSrv/fsmission.cpp:2468-2502` |
| Feed selects “ejected”/“was killed” and self/killer forms from `bDeathCredit` | `clintlib/appmsg.cpp:2796-2801,2818-2829` |

The port's existing peer-only rating storage and single-contributor class-score
approximation are preserved. Native distributes partial credit over damage buckets
(`FedSrv/FedSrv.CPP:11826-11861`); this change moves the existing whole-credit
approximation ahead of the eject gate rather than implementing other scoring rules.
The existing missing-eject-data fallback is decided before encoding KILL_SHIP so
its death flag agrees with its score and deletion behavior.

## Verification

Final results and reproducible commands are recorded in
[verification.json](P746_assets/verification.json).

- Affected tests: **44 methods, three separate exact-method invocations each;
  183 executed cases passed, zero failures/skips**. This includes all five P746
  methods (12 cases per repetition), existing live scoring/kill-bonus/respawn
  coverage, client feed cases, and real client-session combat.
- Required local showcase never-parks proof: **5/5 isolated passes** on the final
  implementation and **5/5** on untouched base `b7e410e4`, run sequentially with
  the same environment. Both observed failure rates were 0/5. Maximum stationary
  intervals with a target were **2.006 s final / 2.015 s base**, below 5 s.
- Whole-solution build, including the final camera and test-helper edits:
  **0 warnings / 0 errors** (`-nr:false -p:UseSharedCompilation=false` avoids
  cross-sandbox compiler-server reuse). The first post-sweep rebuild hit an
  inherited compiler-server write-permission error before fast tests started;
  no test execution from that preflight is counted.
- Required Release/linux-x64 self-contained AOT publish: **passed**, native ELF
  executable produced; no IL2026/IL3050 diagnostics.

The single unfiltered Core sweep completed **4,899 pass / 6 fail / 0 skip**.
Five failures were path-fixture context, not scoring: temporary install directories
under this checkout inherited the actual repository's `local.env`. All nine
`PlayerBundlePathsTests` cases passed a diagnostic invocation using a `/proc`
file-descriptor alias to the same physical scratch directory, which removes that
lexical ancestor. This changes no fixture assertion, resolver or storage location.
The fast tier uses that alias; all failed path-fixture methods subsequently
passed three separate isolated invocations. The temporary shell setup in ignored
`local.env` has been restored byte for byte.

The sixth Core failure was the inherited TR-3 parity matrix: descriptions on rows
376–377 contained unquoted commas. Only those two CSV cells were quoted; their
content, completion statuses and weights are unchanged. Both repository matrices
now validate (665 and 473 rows). The existing scorecard test passed 3/3 alone;
the Core sweep is not repeated.

Single unfiltered sweeps and the one actual fast-tier run:

| Run | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4,899 | 6 | 0 |
| ClientCore, including opt-in local showcase rotation | 1,471 | 3 | 0 |
| MonoGame | 2,495 | 0 | 3 |
| Fast tier, all four projects | 8,741 | 1 | 3 |

The MonoGame skips require the unavailable private Meshy pack. Fast infrastructure
checks passed; the calibrated per-case limit was 10.811 s. Fast Core passed
4,831/4,831 and ClientCore 1,415/1,415 after the path-context and CSV corrections.
The sole fast failure was
`MiscKeyBindingLiveTests.Live_own_ship_is_real_and_center_ranking_selects_a_real_ship`
(target candidates were warps rather than ships); it subsequently passed 3/3
alone. The ClientCore reconnect dictionary-enumeration race also passed 3/3
alone. Both are recorded in [TEST_FLAKES.md](../TEST_FLAKES.md); their assertions,
timeouts and production behavior were unchanged. No full sweep is repeated.

The local live siege run independently demonstrated the requested scoring fix:
its deadline logged **side 0: 49 kills / 11 deaths; side 1: 39 kills / 14 deaths**,
with **88 plain player `kill +1` lines = 49 + 39 side kills**, and chose side 0 by
kills. That test separately failed its station-elimination assertion: a station
was destroyed, but no entire side was eliminated before the native deadline.
The positive scoring evidence does not count as a passing siege test; the
assertion correction and its independent qualification are described below.

The old siege postcondition was tested four times alone: **fail/pass/pass/fail**.
Both failures destroyed a station and reached a native deadline win; the passes
ended by side elimination. This did not meet the three-pass load-flake rule.
The test now validates both native endings: its actual station-destruction and
Stage Over requirements remain mandatory; eliminated sides must have no restart
stations; otherwise earned kills must be positive and the recorded deadline
reason must match an independent descending-kills/ascending-deaths ranking
(including exact ties). The corrected test passed **3/3 isolated invocations**.
The first corrected qualification ended by elimination;
the second passed at the deadline with **41 kills / 14 deaths versus 33 / 10**,
and the third with **44 / 14 versus 37 / 15**. Both independently verified Alpha
as the winner. Production finale/siege code,
forces, research settings and time limits
are unchanged. All four old outcomes remain in the ledger.

An affected P703 constructor-lifecycle test still expected no off-sector
KILL_SHIP. Its nonzero-damage case now asserts one mission-wide message with
death credit, no killer credit, and no remote explosions. Its zero-amount case
continues to assert no KILL_SHIP. This follows the same native audience rule as
the ejection proof, rather than treating a feed message as a sector effect.
Both theory cases now pass **3/3 exact-method invocations**. The final camera
identity guard and existing ship-killer camera behavior also pass **3/3**.

An audit rejected 24 preliminary empty-filter invocations: the scoring test file
contains `PlayerScoreCombatRatingTests` and `LiveCombatScoringTests`, rather than a
class named after the file. All eight methods were then executed three times
under their real names; empty runs are excluded from the passing totals above.

The interrupted snapshot needed a missing `Allegiance.Core` import and fixture
repairs: valid side names, positions inside the sector boundary, prompt side
selection, and stable references to the finished mission/ships across rotation.
These were implementation/fixture defects, not load flakes.

The sandbox permits writes only in this checkout and `/tmp`; the brief prohibits
scratch in `/tmp`. Validation therefore used ignored `.verify-P746/` in the
checkout, including `TMPDIR`, NuGet HTTP cache and test-runner lock. The preferred
`~/allegiance-scratch/P746/` and `~/allegiance-evidence/P746/` are outside the
writable sandbox. Raw TRX/logs, the AOT binary and baseline build were removed
after evidence extraction; only compact evidence remains under `P746_assets/`,
below 2 MB.

The initial full sweep also exposed seven legacy test helpers writing directly
to fixed `/tmp` paths despite `TMPDIR`. Their file-output destinations now use
`Path.GetTempPath()`; siege log names are unique. These output changes preserve
simulation settings; the separate native-ending assertion correction is
described above. These supporting changes make the brief's scratch rule
effective for subsequent verification. All affected output-helper methods passed
3/3 alone. The exact matching initial siege log was recovered into task scratch;
ambiguous shared fixed-name outputs from other
lanes are preserved. The verification ledger records recovery details.

## Rig check for Claude

Run the rig/221/200v200 checks in Claude's lane. For one match, count only the
plain player-kill line (the separate `class kill +1` line must not be double-counted):

```bash
rg -c '\[scoring\] killer ship [0-9-]+ kill \+1 \(player peer\)' rig.log
```

Expect this count to approximate eligible player-credited kills, including victims
that eject. Side totals additionally include eligible unowned ship and non-ship
credit. Lifepod kills, same-side kills, environmental deaths and zero-amount
events contribute no killer credit. Count within the same match because rotation
resets live counters. The deadline's `by=kills` values should now include these
earned kills; eligible combat must no longer produce hundreds of deaths with zero
kills. Verify observer “X ejected” and own/killer forced-ejection text, a live pod,
and exactly one mission-wide KILL_SHIP with death credit false for each ejection.
