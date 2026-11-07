# P944 — Lobby Join links

Implemented in the working tree. No commits, pushes, deployment, remote-machine
contact, lobby API changes or transport changes. The initial report was written
before gating; dispatch base is `14b74a24`. This report records the resulting implementation and evidence.

## Link format and connection flow

Version 1 is `allegiance://join?lobby=<encoded lobby URL>&id=<listing id>&v=1`.
Omitting `v` selects version 1. Unknown actions and versions fail closed. An empty
path normalized by an OS launcher (`allegiance://join/?…`) is accepted too; GIO
actually produces that form. The pure shared parser and launch isolation are
`src/Allegiance.ClientCore/Session/JoinLink.cs`.

The desktop entry point strips every argument before training, calibration,
benchmark, automation, asset-check and update parsing whenever a link is present,
even for a malformed link. It skips update bootstrap on these argv launches.
Neither query values nor OS events are split into arguments or executed by a shell.

`JoinLinkConfirmation` requests the absolute `/api/servers/{id}` endpoint on the
link's lobby. Production resolution disables redirects and cookies, has a six-second
deadline and a 256 KiB response limit, and supplies no saved credentials. The
returned address, TCP port, mission cookies, UDP port and player-hosted identity
are the connection inputs. Servers with multiple missions offer Up/Down selection
using the browser's existing `ServerListing.ExpandMissions` projection. Minimum
client-version filtering is retained.

Both HUD styles use the same confirmation model and screen. It displays the lobby
host (ASCII IDN form, wrapped in full), different-lobby notice, server name, mission, verified/unverified status,
dedicated/player-hosted status, player count and password edit. Tab edits the
existing 15-character browser password buffer; Enter finishes editing before a
second Enter confirms. A fresh key release after HTTP completion is required,
so Enter held at startup or while waiting cannot accept the confirmation.
Escape cancels to the normal intro, including while HTTP is pending.

Confirmation calls the same `BeginConnect` used by the browser, with the resolved
`listingTarget`. A transient `listingLobby` pins player-hosted introductions and
survives retry/resume without changing `_lobbyUrl` or `player.json`. Dedicated
transport selection also consumes that resolved listing, including its UDP port.
The existing direct-network failure result and punch/session implementation are
unchanged. A saved-lobby identity ticket is never sent on a different-lobby join;
unverified explicit listings do not borrow a cached verified listing's identity.

## Threat list and failure handling

| Input / failure | Handling and evidence |
|---|---|
| Wrong scheme, action, authority/path or unknown version | Exact join action, optional empty `/` path, `v=1` only; parser tests. |
| HTTP to a non-loopback host; localhost lookalikes | HTTPS only, except literal localhost or an IP that is loopback; DNS names are never resolved to establish that exception. |
| Userinfo, query, fragment, backslash or nested escaping in lobby | Refused. The lobby URL is a base URL, not a credential or command carrier. |
| Empty/odd/oversized IDs | 1–128 ASCII letters/digits/underscore/hyphen, first character alphanumeric; total URL at most 4,096 characters. |
| Spaces, quotes, controls, newlines, malformed percent escapes or double-encoded IDs | Raw and once-decoded query text checked; malformed escapes refused; residual `%` is illegal in IDs/lobby URLs. |
| Injected `--flags`, connection parameters or duplicate fields | No re-splitting; every other launch argument is removed. Unknown parameters are ignored; duplicate recognized parameters are rejected. URL host/port/cookie extras cannot override the resolved listing. |
| Multiple link arguments | Refused, with all launch switches still suppressed. |
| Hostile listing names | Plain bounded display text, with control/non-ASCII characters replaced; web rendering uses DOM textContent, never markup strings. |
| Listing lookup redirects, huge/invalid/unsupported responses | No redirects; bounded buffer, cancellable deadline, JSON/listing validation, recoverable message. |
| Unknown/expired listing; unreachable lobby | Missing/expired message or lookup error; Escape returns to intro. In-process lobby and actual refused-loopback tests cover these. |
| Implicit connection, stale Enter or cancelled dialog | Resolution has no connection callback. Only explicit confirmation can consume the listing, once; cancellation and held-key tests cover both HUD styles. |
| Different lobby / saved identity confusion | Plain notice, transient introduction origin, no preference save and no cross-lobby identity ticket. Real-window proof compares player.json bytes before and after each join. |

## Platform registration and what was verified

**Linux:** both desktop templates declare `MimeType=x-scheme-handler/allegiance;`
and a single `%u` argument. The per-user installer invokes `xdg-mime default`.
Actual GIO testing found two details that a static desktop validator misses:
GIO normalizes the empty URL path to `/`, and it probes the executable before
expanding `%%`. The generated entry uses `/usr/bin/env` with the escaped absolute
launcher path as one argument, so percent/quote characters in a bundle path do not
break executable detection. Quote escaping was corrected through both Desktop
Entry decoding layers. The launcher still executes its argv via `"$@"`.

Verified on this Linux host: `desktop-file-validate`; the real installer and
`xdg-mime` association/query in isolated XDG directories; GIO launching a recording
executable with exactly one URL argument; hostile bundle path characters (spaces,
percent, dollar, quote, backtick); shell/AppImage argument forwarding. No real user
association or configuration was modified. AppImage integrations must retain the
bundled MIME/Exec fields and register their generated desktop entry; this lane did
not produce or mount a full new AppImage.

**Windows:** Inno Setup registers only `HKCU\Software\Classes\allegiance`, with
`URL Protocol` and the quoted command `"{app}\Allegiance.exe" "%1"`;
`uninsdeletekey` removes it. No administrator registration is required. `play.bat`
preserves first-run configuration and forwards the original quoted arguments
directly to the native executable, avoiding a cmd-to-PowerShell reparse.
`play.ps1` keeps its argv-array forwarding. Registry/quoting/source assertions
passed; no Windows installer compilation, installation or runtime was performed.
The registry handler invokes the native EXE directly, without either shell wrapper.

**macOS:** `CFBundleURLTypes` declares `allegiance` with Viewer role. This is wired,
not a plist-only promise: the shipped MonoGame SDL package is 2.32.10.2. Static
inspection of its actual ARM64 dylib shows
`-[SDLAppDelegate handleURLEvent:withReplyEvent:]` reading the Apple Event string
and calling `SDL_SendDropFile(NULL, url)` then `SDL_SendDropComplete(NULL)`.
MonoGame 3.8.5.1's decompiled `SdlGamePlatform` accepts DropFile only when
`Drop.WindowId == _view.Id`, so Cocoa's window-ID-zero URL does not reach FileDrop.
`MacJoinLinkEvents` installs a chained, rooted SDL event filter before window
construction, copies the bounded URL, consumes/frees that drop payload and queues
it to the main-thread confirmation. It uses MonoGame's actual loaded SDL handle,
restores the prior filter/drop state on exit and never re-splits the Apple Event.
The pinned SDL SendDrop/PushEvent disassembly was also checked for payload ownership.
A zero-window SDL event fixture and macOS bundle/plist/launcher tests passed on
Linux. Real Finder/LaunchServices delivery, cold start and registration remain
unverified on macOS; no Mac was contacted. Static findings are retained in scratch.

## Real-window evidence

The Release client was launched with one normalized Join URL against a real local
lobby and dedicated server started with `scripts/dev-stack.sh --local --name p944`
using a task-local server config (XXV_simple map, port 59444, loopback lobby 8080).
Each launch also supplied `--training --exit-after 1` to exercise switch suppression.
The client remained at confirmation until input; X11 key events then drove the
production side/ready/launch controls and entered the actual game's cockpit. Separate scratch profiles
selected Classic and Modern, and remembered a different loopback lobby.
`player.json` stayed byte-for-byte unchanged before confirmation and after joining.
The proof stack and clients were stopped by their exact recorded PIDs.

All four PNGs were opened and visually inspected. Confirmation shows the explicit
different-lobby notice, unverified dedicated server, mission and Tab password field;
the count is the fixture lobby's advertised 0/0. Both joined captures show the
actual Ceto sector, asteroids, cockpit instruments and minimap: Classic has the
native arc instruments; Modern has its teal percentage rings and speed readout.
The larger Classic cockpit capture uses a 128-color palette to meet the evidence
budget, retaining the full frame. Each PNG is below 300 KB; all task assets together are below 2 MB.

| Style | Confirmation | Joined game |
|---|---|---|
| Classic | [confirmation](P944_assets/confirmation-classic.png) | [cockpit](P944_assets/joined-classic.png) |
| Modern | [confirmation](P944_assets/confirmation-modern.png) | [cockpit](P944_assets/joined-modern.png) |

These new media files still require Domingo's review before any push. Temporary
build/test directories and proof signing keys/database were removed; referenced
logs, traces, proof scripts and receipts remain in ignored task scratch.
Player-hosted listing resolution and transient-lobby UDP selection were tested,
including both HUD styles; this task did not repeat a full NAT/punch window proof.
Existing P936 browser transport/password regressions passed in the affected cohort.

## Tests and gate

- Solution build: **0 warnings / 0 errors**. Release client and lobby builds also
  passed 0/0. No Server/Core simulation source changed, so the additional server
  AOT-publish gate is not applicable.
- `tools/build-tools.sh --drop-bins`: **all 118 tool projects built**; no warnings.
  The final source was checked again after review. NuGet restores used only the
  existing local package cache; no public host was contacted.
- New/affected cohorts, each **three isolated runs**: parser 47/47, UI + existing
  P936 browser tests 14/14, lobby resolution + browser page 14/14. Total **225 C#
  passes, 0 failed, 0 skipped** across those nine processes.
- Packaging suite: **17/17 ×3** via `python3 packaging/test_packaging.py -q`, including
  smoke module assertions and the real GIO hostile-path launcher check. The required
  `python3 -m pytest packaging/test_packaging.py` was attempted, but this environment
  has no pytest module or offline cached pytest distribution. No public download
  was attempted. The identical unittest suite ran successfully; the exact pytest
  invocation remains unavailable in this lane.
- `node tools/p944-lobby-page-test.js`: **passed ×3**, executing app.js against a
  minimal DOM with hostile names/IDs for dedicated and player-hosted rows. Run
  against `git show HEAD:…/app.js`, it fails before the change (8 columns versus the
  new Join column); the changed file passes and round-trips only lobby/id/v fields.
- Glossary: 617 linked paths valid. P944 parity CSV validates. Desktop templates
  validate; `git diff --check` is clean.
- `local.env` is present and loaded for tests. Exactly one Fast-tier sweep ran:
  **13,370 passed / 9 failed / 3 skipped**, 13,382 executions in **597.2 s**. All
  nine infrastructure checks passed. Eight executions exceeded the calibrated
  ten-second Fast limit. All P944 tests passed; the sweep remains **FAIL**.
  The three skips are optional Meshy-pack tests. Isolated controls are recorded
  below; no second Fast sweep ran.
- Final review preserved standalone `--lobby=<url>` switches and made the full
  ASCII/IDN lobby host wrap instead of truncate. Those final refinements were
  built 0/0 and tested in the final three affected cohorts after the single Fast
  sweep; the sweep does not cover these last two refinements.

Raw local evidence is in the ignored `.scratch/p944/` directory (build logs,
`affected/*.trx`, parser/UI/lobby/packaging logs, `page-before.log`, SDL/MonoGame
inspection, `window-proof.py`, `window-proof.log`, per-style client logs,
`window-receipts.json`, `desktop-proof/receipt.json`, `isolation/summary.json`). Dev-stack logs are under
`.scratch/dev-stack/p944.logs/`. This sandbox permits writes only inside this
worktree and `/tmp`; the lane forbids scratch in `/tmp`, so these raw logs remain
in ignored task scratch instead of an external evidence directory.

## Fast sweep controls

The fifteen failed/slow methods each ran three times in separate filtered
processes after the sweep, with no overlapping test process from this lane.
Parameterized methods include every case. Load averages and per-case outcomes
are retained in `.scratch/p944/isolation/summary.json` and the matching TRX/logs.

| Control | Three isolated results | Maximum case duration in each run |
|---|---|---|
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | pass/pass/pass | 5.349/5.451/6.212 s |
| `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump` | pass/fail/pass | 4.446/4.511/4.525 s |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | pass/pass/pass | 5.425/5.410/5.341 s |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | pass/pass/pass | 3.245/3.193/3.283 s |
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | pass/pass/pass | 1.656/1.620/1.663 s |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | pass/pass/pass | 4.192/4.146/4.131 s |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | pass/pass/pass | 1.021/1.116/1.046 s |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail/fail/fail | 1.239/1.186/1.236 s |
| `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` | pass/pass/pass | 1.722/1.675/1.812 s |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass/pass/pass | 9.389/9.340/9.525 s |
| `P942ResearchOrderingTests.Generated_research_never_waits_for_a_drone_or_utility_base_while_a_development_is_buyable` | pass/pass/pass | 1.064/1.009/1.017 s |
| `PlayerBundlePathsTests.LocalConfigWinsOverBundleWithBlankEnvironment` | fail/fail/fail | 0.015/0.015/0.015 s |
| `PlayerBundlePathsTests.MissingDirectoryHasNoDefault` | fail/fail/fail | 0.004/0.004/0.004 s |
| `PlayerBundlePathsTests.NativeSourceNeverGuessesAnInstallSubdirectory` | fail/fail/fail | 0.004/0.004/0.003 s |
| `PlayerBundlePathsTests.UsesExecutableDirectoryOutsideRepository` | fail/fail/fail | 0.004/0.004/0.005 s |

The four path methods account for the same **five failing cases per run**:
executable Artwork fallback, missing Artwork/NativeSource defaults, local-config
precedence, and native install-subdirectory guessing. The mandatory in-worktree
TMPDIR makes these fixtures discover ancestor `local.env`; P942 and earlier
ledger entries reproduce this on unchanged source. `ExternalPaths` and these
fixtures were not changed.

The NPC defender's expected-ship/actual-null assertion remains persistent, as
already documented under P942/P928. Side auto-seat saw 31.75 simulated seconds
against [28,31] in the sweep and passed three times alone. The countdown's off
case saw 14.65 against [14.94,15.06] in the sweep and 14.85 in the second isolated
run; it remains intermittent alone. The mine wake test's 31.704-second loopback
wait passed three times alone in 1.620–1.663 seconds. The seven other timing-only
controls all passed three times, with every isolated case below ten seconds.
No assertions, timeouts, tiers, simulation policy or test inclusion were changed.
See the P944 entry in `docs/TEST_FLAKES.md`. The original Fast result stays FAIL.

## Running instances

An argv link launch may start a second client instance. Forwarding between
processes is out of scope. macOS may deliver another URL to its running process;
the adapter queues it for the intro and never interrupts an active match to join.

## Native intent

**Modern port addition, not a new native rule.** Re-grep of the read-only native
source found no `URL Protocol` or `allegiance://` registration in the C++/header/
registration resources searched. Native `WinTrek/introscreen.cpp:1812-1829` has
`OnButtonZoneClub`/`OnButtonInternet` enter `ScreenIDZoneClubScreen` after checking
network drivers. This work supplies the web/OS handoff to that lobby-join intent,
then reuses the port's existing wire and player-hosted introduction flow. No new
native behavior is claimed. `docs/parity/p944-join-links.csv` records this as an
`n/a` modern port addition, with no native completion credit.
