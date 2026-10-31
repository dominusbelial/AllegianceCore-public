# P901 — Host LAN game

Codex lane, 2026-10-31. Changes left in the working tree; no commits, pushes,
remote-machine access, or host migration. Local raw receipts are under
`.scratch/p901/`; small inspected captures are under `P901_assets/`.

## Ownership and native investigation

`SkirmishHost` owns an in-process `Allegiance.Server.Server`, a simulation thread,
cancellation and synchronization, an external `allegiance-mcp` supervisor and its
workers, a process-lifetime guard, private stdin/stdout control, health monitoring,
and per-match temporary config/log files. The playing pilot is an ordinary
`ConnectionSession` connected to loopback. The release bundle already publishes
the server and SinglePlayer assemblies plus the self-contained bot supervisor
(`packaging/package.py:246-269`); hosting requires no new executable, runtime,
service, download, or packaging dependency. Artwork/maps remain the existing
bundle's requirements.

Native source was read locally and every cited region re-read before citation:

- `FedSrv/AdminServer.cpp:511-532` creates a private standalone LAN session using
  `FEDSRV_STANDALONE_PRIVATE_GUID`, separate from lobby/public sessions.
- `clintlib/clintlib.cpp:1561-1569` enumerates that GUID;
  `WinTrek/introscreen.cpp:897-905` calls it from the LAN list, with comments about
  synchronous enumeration hanging the UI. `clintlib/clintlib.cpp:1309-1321`
  joins an enumerated session instance or a private/public named session.
- `FedSrv/AdminGames.cpp:47-69` limits a standalone server to one game and divides
  its player capacity among teams.
- `FedSrv/FedSrv.CPP:648-661` checks the game password and replies
  `DPR_BadPassword`; `Igc/igc.h:1120` stores it in mission parameters.
- `FedSrv/fsmission.cpp:4612-4642` applies numeric balance after NotStarted,
  preserving two extra low-rank seats; `:4509-4515` makes auto-pick prefer the
  smaller side when the rank choice would exceed numeric imbalance.
- `FedSrv/fsmission.cpp:275-283` removes remaining players with
  `QSR_ServerShutdown` when destroying a mission.
- `clintlib/clintlib.cpp:3672-3715` confines the offline wingman ammunition
  resupply latch to the disconnected branch; it is disabled for LAN guests.

The supplied native tree has **no active WinTrek player-host launch path** in
WinTrek/clintlib. Its former training standalone cleanup is explicitly commented
out with the VS2008 Express/ATL explanation at `WinTrek/WinTrek.cpp:3471-3474`;
current training creates a local universe at
`training/trainingmission.cpp:153-163`. Thus the private standalone/LAN intent is
real, but claiming an active native in-client LAN-server implementation would be
unsupported. P901 adds a deliberate cross-platform ownership/UI design using the
port's already-shipped embedded server. Tracking: `docs/parity/p901-lan-host.csv`.

## Built behavior

The main menu has **Host LAN game** in both HUD styles. The shared setup model
edits name, TCP/UDP game port, optional masked password, Small/Medium fleet,
difficulty, and civilizations. LAN hosts require research tech and normal speed.

`SkirmishOptions.HostLan` is explicit. Its defaults bind IPv4 all interfaces at
6073, start TCP **and** UDP, and require discovery on UDP game-port + 1.
Game-port range is 1024–65534; a TCP, UDP or discovery collision produces a clear
startup error and cleans partial listeners. Dedicated server defaults are
unchanged, including best-effort discovery; strict discovery and transport
selection apply only to embedded LAN hosts. The desktop host ignores dedicated
transport-protocol/UDP-port environment overrides so both promised listeners
remain available.

The host joins normally at loopback. The top status line lists active local IPv4
interface endpoints and current seated players (humans + bots), refreshed once
per second. It only reads interfaces; it does not probe firewall/NAT, select a
preferred route, or open ports automatically. Friends select the LAN listing or
type an endpoint in Direct Connect. **Tab** edits the join password on either
screen. Passwords are not stored in player preferences; the existing temporary
server config contains the launch password, like a dedicated server config.
The password is an access gate on the existing unencrypted game transport.

Bots keep their seats. Small starts with four combat bots beside the host on
side 0 and five on side 1; Medium uses fourteen/fifteen. Research commanders and worker probe pilots also occupy ordinary team seats.
Capacity is 11 per side for Small and 21 per side for Medium, including all
humans, bots and helpers; the precise remaining human slots depend on helper
placement. Numeric balance is set to 1, including the
native two-seat low-rank allowance; manual joins retain that native allowance,
and auto-pick uses the smaller team. No bot despawn, new remote control endpoint,
or guest privilege is involved. Existing ready/start, intermission, research,
legal purchases and match migration paths are reused. The final process proof
plays a native 45-second countdown goal through game over and the next match,
without an admin or forced game-over call. No synthetic showcase
finale can be armed in LAN-host mode; matches end through ordinary mission rules.

Host pause and private spectator/seat-exchange controls are unavailable.
Ctrl+F10, the menu's **End LAN game for everyone**, hangar exit, game-over exit,
and app disposal end the host. Guests receive native guaranteed
`QUIT_MISSION / ServerShutdown`; the host then closes discovery and transports
and reaps its supervisor/workers. A crashed host has the ordinary transport-loss
path rather than a shutdown message. Guest drop/rejoin uses the dedicated-server
path; optional grace/resume is not enabled implicitly.

## Guest-safety audit

| Setting / path | Offline skirmish | LAN hosting |
|---|---|---|
| Bind/port/discovery | Loopback, ephemeral, off | All IPv4, selected TCP+UDP, discovery required |
| `TestUnlock` | Enabled only for unlocked mode | Disabled; unlocked mode refused before listening |
| `TestHarness` / IgnoreTechGate | Available for unlocked tests | Both disabled; guest names and loopback do not grant it |
| `AdminCheats` | Benchmark seed can enable allowlisted pilot | Absent; benchmark seed refused |
| `Seed`, benchmark metrics | Supported locally | Seed and sizing/calibration overrides refused; inherited bot benchmark metrics removed |
| `ResearchSpeed`, Fast/Showcase, goal rotation | Local orchestration choices | Research multiplier 1; non-Normal speed and rotation overrides refused |
| `TimeDilation` / `TimeScale` | Automatic; local pause supported | Dedicated default automatic dilation; no fixed scale/admin command; pause unavailable |
| `DebugMode` / lifepod override | Defaults | Dedicated defaults; debug off, no lifetime override |
| Offline wingman ammo resupply | Enabled | Disabled: online guests use ordinary supply rules |
| Local observer and bot seat exchange | Owned in-process controls | Host flies normally; private observer/seat UI and exchanges disabled |
| Showcase finale wire request | Existing local showcase orchestration | Ignored in LAN-host mode, including from owned bots |
| Bot/admin authentication | Developer shell may supply bot bearer | Inherited privilege bearer removed; no BotAuthenticationSecret configured |
| Connection/frame/chat/idle limits | Elevated local bot IP budget | 128 connections per transport, 64 per IP; other limits match dedicated server; both TCP and UDP use existing enforcement |
| Lobby, identity, file/version gates | Local defaults | No lobby publication or identity requirement; ordinary wire/version checks, no new required extension |
| Map reveal, difficulty, gunners, goals | Skirmish options | Existing legal server/bot options, no guest cheat route |

Refusing unlocked and measurement modes keeps the advertised LAN game predictable:
research and purchases mean the same thing for host, guests and bots. Automatic
time dilation remains because it is also the dedicated server default and keeps
all clients on the same simulation clock under overload.

## Evidence and verification

The process proof uses four independent client processes and the production
`ConnectionSession`, discovery engine, UDP/TCP transports and wire handlers.
It verifies actual transport selection (no hidden UDP-to-TCP fallback), flight,
password refusal, an impersonated harness-name prefix without grants, rejected
admin time scale, ignored synthetic finale, drop/rejoin, native timed game over,
next-match migration, shutdown reason, process exit and rebinding all listeners.

Final receipts: [validation.json](P901_assets/validation.json). Raw logs/TRX are
retained in `.scratch/p901/` (including `results/` and `fast/`).

| Gate | Final result |
|---|---|
| Solution build | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **100/100 projects**, 0 warnings / errors |
| Server AOT publish, Release/linux-x64/self-contained | **Clean**, no IL2026/IL3050 warnings |
| New P901 tests | **10/10 ×3**, no skips; played lifecycle case about 106.5 s per run |
| Offline-skirmish option regressions | **29/29 ×3** |
| Affected intro/layout/career tests | **30/30 ×3**, including the corrected Career-slot assertion |
| Showcase never-parks | **5/5 pass**, serial with artwork and server GC; 206.9 / 217.0 / 270.2 / 220.3 / 466.5 s |
| One Fast sweep | **12,691 pass / 11 fail / 3 optional skips**, 544.3 s; nine infrastructure checks pass; two passing cases over 10 s |
| Glossary links / parity scorecard / Compose config / whitespace | **Pass** |

**The single Fast sweep is not a passing gate.** One failure was an outdated
Career-last assertion, now fixed and covered by the 30-case ×3 selection. The
other failures and both timing offenders have three-run isolation results in
[the P901 flake-ledger entry](../TEST_FLAKES.md#p901-verification-2026-10-31-utc).
The persistent defender baseline remains fail/fail/fail; side auto-seat and
warp-bomb floating-point assertions remain intermittent. The other gameplay
failure and both timing offenders pass 3/3 alone. Profile and temporary-path
obstructions have passing writable/topology controls. No unrelated gameplay,
timing bounds or test expectations were relaxed, and no second Fast sweep ran.

The build lane uses a writable `NUGET_HTTP_CACHE_PATH` and worktree-local
`TMPDIR`; the final AOT command also uses `-m:1`. The outside-repository path
fixture control uses a `/proc` descriptor alias, while actual files remain in
the worktree. This avoids writing into the real profile or outside scratch.

Actual Game1 window captures, opened and visually inspected:

- [Classic setup](P901_assets/host-classic.png)
- [Modern setup](P901_assets/host-modern.png)

Both visibly show name, TCP+UDP port 6073, an empty optional-password row, fleet and
race choices, the start/back actions, and firewall/internet/host-ending guidance.
The intro form intentionally shares its presentation in both style settings.
Actual running-host captures were also opened and inspected at
`.scratch/p901/live-Classic.png` and `.scratch/p901/live-Modern.png`; these show
the ordinary host pilot docked in its scout, a normal wallet, the live join
endpoints and total player count at the top. They stay ignored because their
local interface addresses are private.
No private interface addresses appear in the published setup captures. Password
masking is covered by the model tests. New media still needs
Domingo's normal review before a later push by the dispatcher.

## Platform limits and handoff

Run here: local Linux client/server processes and real Game1 windows on the local
display. Guest processes use the production client session headlessly, rather
than a second release-bundle GUI. No Windows or macOS hardware, physical second machine, real firewall
prompt, Tailscale installation/path, router forwarding, blocked firewall, Wi-Fi,
or release-bundle installation was exercised. No rig, mini, Mac, or other remote
host was contacted. The old-port-client proof omits current optional logon
extensions; it is not a native DirectPlay interoperability claim.

Claude should check with two real machines:

1. Install the normal player bundle; host with each style. Confirm one address
   belongs to the friends' LAN and the live human+bot count changes on joins.
2. Discover and directly join over explicit TCP and UDP; check default Auto
   selection, a custom port via Direct Connect/favorite, and wrong/right password.
3. Seat two friends beside bots, launch, play to natural conquest, and stay
   through intermission into another match; exercise the native low-rank balance
   allowance and capacity refusals.
4. Disconnect/rejoin a guest; end from hangar, game-over, menu, Ctrl+F10 and window
   close. Guests should see a shutdown explanation and the port should be reusable.
5. Check Windows private-network prompts, macOS incoming-app permission, and
   Linux firewall rules. A blocked firewall means missing listings/timeouts;
   nothing here detects or fixes it automatically.
6. Use a real Tailscale IPv4 endpoint through Direct Connect, and separately
   forwarded TCP+UDP game port for internet friends. No relay/NAT traversal is
   implemented; discovery is optional for a known direct address.

## Native-intent footer

**Design:** explicit player-owned LAN lifetime, fixed bots beside joining humans,
research-only safety policy, and no host migration. **Era constraint:** DirectPlay
private-session enumeration and old ATL/COM hosting give way to the existing
cross-platform embedded server, bounded discovery and ordinary client sockets.
**Bug fixes:** no native gameplay bug is intentionally changed; native balance,
password refusal and shutdown reason are retained. The modern better approach is
one visible host action with explicit exposure, clear join information and
owned cleanup, using the same server rules as dedicated play.
