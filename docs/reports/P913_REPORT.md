# P913 — local server console for AllSrv administration

Implemented on branch `codex/p913`; changes remain in the working tree, with no
commit or push. No remote machine was contacted. The existing stdin reader is
the only entry point: no listener, port, service, environment variable or new
credential was added. Operator and Docker instructions are in
[GUIDE.md](../GUIDE.md#local-server-console).

## Row verdicts

Native source paths below are relative to the read-only native `src/` tree.
Line numbers were rechecked against that checkout. Port paths are relative to
the repository. All five named tests live in
`tests/Allegiance.ClientCore.Tests/P913ServerConsoleTests.cs`.

| Row | Verdict | Native citation | Production port | Executable evidence |
|---|---|---|---|---|
| ADM-0002 (3) | implemented | `AllSrvUI/PagePlayers.cpp:77-120,504-524` | `Server.Console.cs:136-150`: live name/game/team/sector/state and ship/user IDs, including side selection | `Stdin_roster_status_and_boot_follow_live_membership_including_side_select` |
| ADM-0004 (3) | implemented | `AllSrvUI/PagePlayers.cpp:392-418`; `PageChat.cpp:515-520` | `Server.Console.cs:175-202`: system `say` across every hosted game; `whisper` to the selected user | `Say_reaches_all_games_whisper_only_selected_user_and_history_never_sends_to_clients` |
| ADM-0010 (2) | implemented | `AllSrvUI/AllSrvUISheet.cpp:155-204`; `FedSrv/counters.h:4-35` | `Server.Console.cs:155-159`: connection/player/mission counts, uptime, tick/overrun counts, cumulative output/timeout counters and each stage | `Stdin_roster_status_and_boot_follow_live_membership_including_side_select` |
| ADM-0012 (2) | implemented, design-replaced UI | `AllSrvUI/PageChat.cpp:91-183,244-327,426-439,485-528` | `Server.Console.cs:108-122,175-225`; `Server.Dispatch.cs:564,697`; `Server.Callbacks.cs:74`: game-tagged chat view, bounded history and system sends | `Ring_wraps_preferences_gate_capture_and_native_admin_chat_triggers_alert_only_on_server`; routing/read-only test above |
| ADM-0013 (2) | implemented, design-replaced preferences | `AllSrvUI/DlgChatPreferences.cpp:95-148,190-205`; `PageChat.cpp:443-469` | `Server.Console.cs:108-125,212-225`: independent `chat` and `alerts` gates, bounded history | `Ring_wraps_preferences_gate_capture_and_native_admin_chat_triggers_alert_only_on_server` |
| ADM-0016 (2) | implemented, design-replaced alert presentation | `FedSrv/FedSrv.CPP:3129-3130,3163-3171,3481-3487,3599`; `AllSrvUI/AdminPageThreadWnd.cpp:181-215` | `Server.Console.cs:212-225`; `Server.Dispatch.cs:564,697`: timestamped ADMIN alerts, no client recipients | `Ring_wraps_preferences_gate_capture_and_native_admin_chat_triggers_alert_only_on_server` |

`Server.*` above means `src/Allegiance.Server/Server.*`. Only the six requested
matrix rows changed. Their native bodies/rules and weights were preserved;
completion rests on new executable production paths and regression tests.
The three deliberate UI replacements use `native_class=design-replaced` and
explicit `REPLACED:` reasons instead of claiming full Win32/registry parity.

Boot also reuses `Server.Moderation.cs:69-74`, extracted from the existing
ADM-0001 moderator branch. Native `CAdminUser::Boot` removes a user with
`QSR_AdminBooted` (`FedSrv/AdminUser.cpp:168-175`); the console uses that exact
reason, sends the guaranteed departure notice, and runs the existing cleanup.
The player's transport connection may remain for the lobby, as in native.
System chat also preserves native's object-model flag, NA sender/target IDs,
no command and no voice-over (`FedSrv/AdminServer.cpp:122-132`;
`FedSrv/AdminUser.cpp:114-123`); routing tests assert those wire fields.
ADM-0001 and ADM-0003 matrix rows were not changed.

## Native behavior inventory and explicit replacements

- **Player page:** login/logout adds/removes users; sector, team and team-name
  events update the three columns (`PagePlayers.cpp:77-120,532-651`). Selection
  enables boot and send (`:336-352`); all selected broadcasts to the game,
  otherwise selected users receive privately (`:392-418`). Boot's confirmation
  and COM user dispatch are at `:776-829`. The console queries authoritative
  live state, takes a ship/user ID as selection, and treats the entered `boot`
  command as the operator's action. No GUI selection/confirmation window is
  needed.
- **Chat page:** ChatMessage/AdminPage events are filtered to the selected
  game, with a `TEST` preference-preview exception (`PageChat.cpp:91-129`).
  Sender/target/message are appended; scrolling follows only when already at
  the end; old entries are trimmed (`:132-183,426-439`). Sending trims blank
  input, updates a bounded send-MRU, and chooses current-game or server-wide
  delivery (`:485-528`). Registry column order/width, MRU and all-games choice
  persist (`:244-423`). The console combines all hosted games with an explicit
  game tag, always makes `say` server-wide, and leaves visual scrolling to the
  terminal. The fixed 200-entry received-history ring replaces the GUI list;
  registry column state and persisted send-MRU are intentionally absent in
  this stdin implementation. The native clear-list button (`:720-726`) is
  replaced by bounded retention and reset at boot; no clear-list command was
  requested. There is no selected-game or `TEST` UI state.
- **Preferences:** native loads/saves chat enable, ADMIN list inclusion,
  message-box and shortcut options plus unlimited/limited history
  (`DlgChatPreferences.cpp:95-148`). Unlimited means limit zero; an absent
  shortcut disables execution (`:190-205`). The subscription is gated by these
  choices (`PageChat.cpp:443-469`); native test creates a synthetic AdminPage
  (`DlgChatPreferences.cpp:271-303`). P913 explicitly requires bounded history,
  so this implementation fixes the limit at 200, starts chat capture off, keeps
  alerts on, and resets preferences/history at boot. `chat n` chooses how much
  history to display, not a new retention limit. `alerts off` suppresses the
  standalone alert while `chat on` can still display/retain ADMIN chat.
- **Alert trigger and actions:** `CHAT_ADMIN` selects `EventID_AdminPage`
  (`FedSrv.CPP:3129-3130`); its forwarding branch selects no clients
  (`:3481-3487`) and the COM event still fires (`:3599`). Ordinary chat is
  `EventID_ChatMessage`. The native page checks preferences and posts the
  alert to dedicated worker windows (`PageChat.cpp:171-177,531-616`). The
  worker formats sender/time/message into a warning message box or executes
  `AdminAlert.lnk` with date/sender/message (`AdminPageThreadWnd.cpp:181-215`).
  Both actions, and the brief's Event Viewer action, become one console/log
  line. There is no Event Viewer launch in this native file; it runs the
  configured shortcut. The cited “bias” is timezone conversion
  (`AdminPageThreadWnd.cpp:133-175`), **not an alert debounce interval**. No
  invented join/leave/error alerts or debounce were added.
- **Status:** the sheet polls session health and renders the current game stage
  (`AllSrvUISheet.cpp:155-204,645-649`); `FedSrv/counters.h:4-35` names activity
  counters. The console displays real counts, all mission stages and the
  already implemented cumulative guaranteed-egress/timeout counters without
  draining profiler windows. It does not fabricate the other native counters.

## Scheduling, safety and cost

`Server.Lifecycle.cs:2574` uses the existing background stdin reader;
`Server.Console.cs:47-83` bounds input to 1024 characters and 64 queued commands.
Only nonblocking `TryWrite` is used; a full queue rejects new submissions.
At most eight commands execute at `Server.Broadcast.cs:100` on the simulation
thread immediately before the existing skills query. EOF ends only the input
reader, including Docker's unattached stdin. The production process already
installs `ConsoleLog`'s bounded asynchronous output sink; commands do not wait
for stdout. Output can be dropped by that sink when full, as existing logs can.

`players`, `status`, `chat`, preference toggles and alerts never send anything
to a client. Only explicit send/boot commands do. Received chat remains on the
existing routing/budget path; the observer is attached after command handlers
and budget admission. CHATMESSAGE, CHATBUOY and ordinary site chat are observed.
Chat capture off skips formatting, timestamps, roster enumeration and storage;
ADMIN alerts are independently gated. The fixed ring allocates no per-tick
storage. Security handling redacts configured moderator, cheat, bot and harness
credentials before truncation and again after removing control/format characters.
Existing logon/launch diagnostics use the same handling so a hostile logon name
cannot expose a configured credential. Credential fields, tickets and request
objects are never enumerated by the console. Malformed commands produce one
usage line and never repeat the supplied text.

The performance proof uses the existing 394-peer broadcast fixture and measures
only the added disabled observer. It is an allocation/branch proof, **not** a
394-real-client rig benchmark; the separate loopback tests use real transport
clients and two real hosted missions. No synthetic CPU load was generated.

## Scorecard

Before: AllSrvUI **68.18% (30/44)** weighted; **10/16** complete.
After: AllSrvUI **100.00% (44/44)** weighted; **16/16** complete, of which
6 weight points are explicitly design-replaced. All unrelated scorecard totals
are unchanged. Before/after raw output is retained in
`.scratch/p913/scorecard-before.txt` and `.scratch/p913/scorecard-after.txt`.

## Validation

Gate results:

- `dotnet build AllegianceCore.slnx`: **0 warnings, 0 errors**.
- `tools/build-tools.sh`: **107/107 projects**, no warnings/errors.
- Linux x64 self-contained server Native AOT publish: **clean**, no IL2026/IL3050.
  The final repeat initially hit NU1900 because NuGet's default HTTP audit
  cache is read-only in this sandbox. Setting `NUGET_HTTP_CACHE_PATH` to task
  scratch resolved it; audit and warning enforcement stayed enabled.
- Each new test run alone three times: **15 passed, 0 failed, 0 skipped**.
- Affected P762 moderation + P760 callbacks cohort three times: **81 passed,
  0 failed, 0 skipped** (27 per run).
- AOT executable smoke: real process stdin commands, malformed usage without
  echo, EOF liveness, and clean SIGTERM shutdown **passed**.
- Disabled observer, 394-peer fixture: **0 bytes** allocated in each of three
  runs; **4.5 / 4.2 / 4.6 ns per call** in the isolated microcheck. This tiny
  sample is not an end-to-end tick benchmark or a rig A/B.
- `docker compose config --quiet`, glossary links, parity validation and
  `git diff --check`: **passed**.

Showcase never-parks: **5/5 passed, 0 skipped**, serially. Durations:
**387.934 / 436.559 / 221.535 / 237.196 / 420.422 s**. Every run reached 24/30
objectives with real combat hits and asserted no simulation-stage failures;
maximum stopped-with-target interval **1.913 s**. This matches the saved P908
baseline's **5/5** pass rate. No end-to-end performance A/B is claimed.
Boot output confirms snapshot/encode-once, parallel native broadcast, pool
transport, native GotoPlan and server/concurrent GC.

The single Fast sweep completed **12,889 cases in 600.132 s**:
**12,878 passed / 8 failed / 3 optional private-pack skips**. All **nine
infrastructure checks passed**; eight executions exceeded the ten-second Fast
limit. The result remains **FAIL** and the full tier was not rerun.

Every failed or slow method was repeated three times alone: **13 methods,
48 case executions, 42 passed / 6 failed / 0 skipped**. The defender's existing
null-target failure persists 3/3. Dock status remains intermittent
(pass/fail/pass), as does auto-seat receipt timing (fail/pass/fail). The other
failures and all timing-only methods passed their isolated repetitions.
The standby-wingman displacement and lethal-asteroid receipt failures were
sweep-only; no NPC, flight, collision or damage rule changed. Exact signatures,
durations and all three results are recorded in
[TEST_FLAKES.md](../TEST_FLAKES.md#p913-verification-2026-11-02-utc).

Small public receipts: [validation.json](P913_assets/validation.json).
The final AOT executable also passed the real-process stdin/EOF/SIGTERM smoke
check after the cache-path correction.
Local artwork was available via the existing ignored `local.env`.
Raw gate logs/TRX remain in this worktree's ignored `.scratch/p913/` directory;
no raw logs or build artifacts are added as public report assets.

## Native-intent footer

**Design:** local operators can inspect players, remove a selected user,
broadcast/private-message, inspect status, monitor chat and receive an explicit
ADMIN page without exposing a new admin network surface. **Era constraints:**
MFC lists/dialogs, COM worker windows, registry column/MRU preferences and shell
shortcuts served a Windows desktop operator. P913 replaces those with bounded
stdin commands, game-tagged console output and the existing log pipeline.
**Bug/source correction:** the old matrix described timezone bias as alert
throttling and a generic Event Viewer action; the actual code converts UTC to
local time and executes a configurable shortcut. No new gameplay rule is
inferred from those descriptions. **Modern better approach:** keep bounded,
nonblocking structured operational output and host/container access control;
a separately authorized authenticated admin service could later add durable
preferences, filtering and audit records, but is outside this task.
