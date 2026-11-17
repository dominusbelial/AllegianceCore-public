# P1048 — Browser slice 3: game-server WebSocket transport + browser join (P1045 "WSS endpoint beside game server")

Report name: `docs/reports/BROWSER_WS_TRANSPORT_P1048.md`.

## Context
Read first: `docs/reports/BROWSER_CLIENT_P1045.md` (**§ Networking and security, § Public admission and the R1/R1b
boundary, § Lobby entry and identity / Login → list → join** — this brief implements its preferred path),
`BROWSER_PREVIEW_P1046.md`, `BROWSER_SEAMS_P1047.md` (the browser build: `src/Allegiance.ClientCore.Game.Browser`,
`src/Allegiance.Core.Browser`, KNI), `docs/reports/R1_REPORT.md`, `R1B_CARBON_REPORT.md`, `AGENTS.md`, `GLOSSARY.md`.
Browsers cannot open the game's TCP/UDP transports. P1045 chose a **WebSocket (`wss`) listener inside the game server**
implementing `IServerTransport`, carrying unchanged FedPackets, over a bridge/gateway (which it says must never be
published). Today the browser preview stops at the menu with "not available in the browser".

**Write the report FIRST** (skeleton + a status line per item) and keep it current. Priority order is the item order:
items 1–5 are the must-have; 6–7 are the playable slice.

## Items
1. **Generalize the server transport seams before adding a third listener** (P1045: "Generalize these seams before
   adding a third enum string"). Audit and replace concrete TCP/UDP/DualTransport casts with interfaces carrying
   per-peer facts — authenticated external source address, is-loopback/is-local, human/bot, protocol name — at least in
   `Server.Privileges.cs` (loopback trust), `Server.CredentialLimits.cs` (per-source throttles), `Server.Egress.cs`,
   `Server.SchedulingMetrics.cs`, `Server.Lifecycle.cs` listener construction, `DualTransport.cs` peer-id bit layout
   (collision-free multiplexer for a third listener), and `ConnectionSession.cs:31` (honest protocol label). Behaviour
   for TCP/UDP peers must be **identical** (prove with the existing server/transport suites + new unit tests).
2. **`WebSocketServerTransport : IServerTransport`** in `src/Allegiance.Core/Transport/` (or the server project if
   Kestrel must live there — state why). Subprotocol `allegiance-fed-v1`; one binary WS message = one full FedPacket
   (CRC included); reassemble fragments to a capped message **before** the existing decoder; bounded per-peer send
   queues (entries AND bytes AND oldest-guaranteed age), zero-key order preserved, the two keyed latest-wins APIs
   (`ITransport.cs:78`/`:93`), peer-joined-before-receive ordering (`:105`), drained at tick boundaries — no receive task
   mutates simulation. Single writer per socket. Off by default: a server config key (e.g. `WebSocket:Enabled`,
   `WebSocket:Port`/path, `WebSocket:AllowedOrigins`) — no listener unless configured.
3. **Admission and limits** (P1045 bullet list, all of it): Origin allowlist enforced on upgrade, binary-only,
   unknown subprotocol rejected, TLS expected in production (`ws` only on loopback for dev/tests), per-IP and global
   connection + handshake-attempt caps, absolute handshake/auth deadline and fragment-progress deadline, reassembled
   client message cap = the existing 64 KiB inbound cap (`TcpTransport.cs:171`) with a smaller pre-logon cap,
   fragment-count/cumulative-byte caps even without EndOfMessage, native CRC/cbmsg/message-count checks kept, reuse of
   ingress reservations and per-tick fairness (`ServerHost.cs`), slow-consumer disconnect, **WS peers never treated as
   local/loopback for privileges**, credential throttles grouped by the real source, greeting deadline + pre-logon
   retirement as TCP. Identity: the ordinary `LOGONREQ` with the join ticket (`Server.Identity.cs`, `JoinTokens.cs` —
   signature/issuer/audience/time/single use unchanged); no type-byte greeting copied from the spike bridge; nothing
   trusts a forwarded header or role bit from the browser. Behind a trusted reverse proxy the source comes from the
   existing trusted-proxy configuration only (state how), never from an untrusted header.
4. **Security tests** (P1045's list): fragmentation (incl. never-ending fragments), oversize pre/post logon, close during
   auth, bad/expired/replayed ticket, wrong Origin, wrong subprotocol, text frames, connection flood / handshake flood
   caps, slow consumer, proxy-provenance (a WS peer from 127.0.0.1 via proxy is not "local"), credential-throttle
   grouping, plus the relevant R1/R1b regression suites run against a WS peer (state which). No synthetic CPU load —
   bounded, deterministic tests.
5. **Packaging:** the server publishes Native AOT (`Allegiance.Server.csproj:12`) into a runtime-deps image
   (`src/Allegiance.Server/Dockerfile:85`). Prove the WS listener survives a real AOT publish + trim (0 new trim/AOT
   warnings, the listener starts and passes a WS join test from the published binary). If embedding Kestrel fails AOT,
   stop and report the measured reason and the alternative instead of shipping a JIT-only path. `docker compose config`
   if compose/env changes (document the new keys in `.env.example`/docs; no secrets).
6. **Client transport:** a client `ITransport` over WebSocket — desktop (`ClientWebSocket`, used by tests and a headless
   join proof) and browser (JS `WebSocket` interop in the KNI build). Extend `TransportFactory` to accept a validated
   `wss://`/`ws://`(loopback only) endpoint URI. A headless test: a WS client logs on with a ticket, picks a side,
   launches and exchanges ship input/updates with a local server — same messages as a TCP client (compare).
7. **Browser join slice (stretch):** in the browser preview, a server advertising a web route can be joined over `ws`
   on loopback: login → list → join → side select → launch. Flight rendering parity is NOT required (P1047 says it is
   not claimed); reaching the in-game state with the server confirming the player, and any rendering the build already
   supports, is enough. Keep unsupported paths explicit ("not available in the browser"), never half-enabled. Record
   the ticket-timing rule (fetch the join ticket after staging assets) and the gesture rule (fresh gesture for
   audio/pointer). If time runs short, stop after item 6 and report exactly where 7 stopped.

## Gate
Build 0/0 + `tools/build-tools.sh`; new tests 3× alone; one `tools/run-tests.sh --tier fast` (non-task failures 3× alone
+ `docs/TEST_FLAKES.md`); the existing server/transport/identity/credential suites green; **TCP/UDP behaviour unchanged**
(state the suites and counts that prove it); AOT publish proof (item 5); browser build still builds and the P1047 browser
proof still passes (`node tools/p1047-browser-proof.cjs` setup per its report, loopback only). A short loopback latency
note: WS vs TCP round-trip and per-message overhead on the same machine (measured, not estimated). Report Native
intent / Modern approach footer.

## Not in scope
Public hosting, CDN, lobby `/play/` static-serving changes, CSP changes, WebTransport/WebRTC, pointer lock/replay/HDR
effects, flight rendering parity, any change to game rules, wire message layouts or server simulation. No deployment to
the rig, VPS, mini, Mac or any public host; no contact with them. Do not publish or keep the P1045 throwaway bridge.

---
## Common rules
- Behaviour parity: native rules unchanged; this is a transport. Never weaken an existing limit, throttle or privilege
  check; if a seam needs a behaviour decision, stop and report it.
- Lane environment: MS-A2 Debian 13 container, own clone; your worktree is the current directory (own branch). SDK
  `~/.dotnet/dotnet`. Artwork `/srv/corpus/Artwork`, native reference `/srv/corpus/Allegiance/src/` (read-only;
  `local.env`). Docker is not installed (state `docker compose config` as not run if so). **Do not leave the brief file
  in the change set** (check `git status`).
- **NuGet in your sandbox (first attempt stopped on this — do not stop on it again):** `~/.nuget/packages` and
  `~/.local/share/NuGet` are read-only inside your sandbox. Every package the solution and all `tools/` projects need
  is already in `~/.nuget/packages` (pre-restored by Claude). Before any build/test/restore run
  `export NUGET_HTTP_CACHE_PATH=$PWD/.scratch/p1048/nuget-http` (P1047 did the same). If a NEW package is genuinely
  needed (e.g. for the WS listener), first prefer the shared framework (`Microsoft.AspNetCore.App` is a framework
  reference, not a NuGet package); otherwise restore it into `NUGET_PACKAGES=$PWD/.scratch/p1048/nuget-packages` with
  `~/.nuget/packages` as a fallback folder, and state it in the report. A restore/sandbox problem is something to work
  around, never a reason to end the task early.
- **Never `git stash`**, never `pkill -f`/`pgrep -f`, no synthetic CPU load, scratch in `.scratch/p1048/`, do not commit
  or push, do not contact the rig, mini, Mac, VPS or any public host; network only on loopback. Public-repo hygiene: no
  secrets, no personal paths/IPs/hostnames in committed files. Every process you start must end before you finish.
  Build-hang workaround: `--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`.
- **Time:** 8 hours max; report first and kept current.
- **Report:** what changed (file:line), seam audit table (cast → interface), limits table (value, source, test), tests
  (Passed/Failed/Skipped), AOT result, latency note, anything unfinished.
