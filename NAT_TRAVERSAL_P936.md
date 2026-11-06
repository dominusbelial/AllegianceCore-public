# P936 — client-hosted internet games

Implemented on `codex/p936`, based on `bf1978d7d5c7b8602bfd581478d5e77b0c1b08f0`.
No commits or pushes. All network proofs use loopback; no public STUN service or
remote rig/mini/Mac was contacted. This report was written before the final gate;
its validation table is updated with the actual gate results below.

## Design as built

- **Lobby-owned discovery:** `src/Allegiance.Lobby/NatBindingResponder.cs:14`
  binds two UDP observers only when `Lobby:PlayerHostedGames` is enabled (default
  off). Defaults are 3478 and 3479, configurable through the settings and Compose.
  It reuses fixed receive/reply buffers and drops oversized/malformed packets.
  `src/Allegiance.Core/Transport/StunBinding.cs:11` implements the Binding subset
  without a package dependency. The format follows [RFC 5389 sections 6 and
  15.2](https://www.rfc-editor.org/rfc/rfc5389).
- **Endpoint reception proof:** `src/Allegiance.Lobby/NatObservationStore.cs:16`
  issues a random 128-bit receipt only in the UDP response to a padded game
  request. It records the source endpoint, observing port and 30-second expiry.
  `NatObservationStore.cs:44` requires a primary-port receipt and the same HTTP
  source address. `src/Allegiance.Lobby/LobbyEndpoints.cs:50` overwrites the
  player listing address/UDP port from that observation. Existing protocol,
  source-address and approved-server checks still run. An arbitrary claimed
  address or port cannot become a player-hosted UDP target.
- **One game socket:** `src/Allegiance.Core/Transport/UdpTransport.Traversal.cs:21`
  reserves the joiner's UDP game socket before discovery. `DiscoverAsync` (:34)
  and `PunchAsync` (:65) send through that socket; normal `UdpTransport.Join`
  reuses it. `UdpTransport.cs:263` demultiplexes discovery and probes before
  parsing game records. Hosts use their already-bound game listener.
- **Player host lifecycle:** `src/Allegiance.SinglePlayer/SkirmishOptions.cs:23`
  adds `HostInternet` and `InternetLobbyUrl`, with HTTPS validation at :143.
  Internet hosting retains the multiplayer host rules.
  `src/Allegiance.Core/Transport/DualTransport.cs:13` gives UDP an optional
  dual-stack bind while preserving the IPv4 TCP listener for the host/bots;
  ordinary LAN remains IPv4 and ordinary skirmish remains loopback.
  `SkirmishHost.cs:127` starts the publisher after the owned bots are ready.
  `src/Allegiance.SinglePlayer/InternetHostPublisher.cs:43` refreshes both STUN
  observations and the listing every five seconds. Its dedicated one-second
  poll (:56) avoids changing dedicated-server heartbeat behavior. Dispose
  cancels polling/probes and attempts a bounded deregistration. Player listings
  disappear within 20 seconds without a heartbeat (`ServerRegistry.cs:19`),
  including from list, individual lookup, stats and core catalog. A shorter
  administrator-configured directory age wins consistently for lookups and
  heartbeats. The listing advertises one occupied game slot so the existing
  create-game relay cannot queue new matches to this desktop publisher.
- **Shared client flow:** `src/Allegiance.SinglePlayer/HostLanScreenModel.cs:9`
  configures the existing shared Classic/Modern host form. The new main-menu
  entry chooses internet mode. The online browser identifies player listings and
  supports masked optional-password entry through the rebindable
  `LobbyPassword` action (Tab by default) and `LobbyJoin` (Enter by default).
  Completing password entry consumes the finish key before a later join. `Game1.Session.cs:1166`
  retains the selected listing ID, rather than guessing from IP/TCP port (two
  hosts behind one public IP may advertise the same TCP port). Manual connects
  clear the traversal choice. `ConnectionSession.cs:398` freezes the internet
  target for connection/resume and uses the normal session after establishment.
- **Bounded introduction:** `src/Allegiance.Lobby/PunchSignaling.cs:13` creates
  matching 128-bit probe secrets and ten-second introductions containing only
  lobby-observed endpoints. Host polling requires listing ownership and matching
  HTTP source. The joiner probes, then performs the unchanged UDP cookie
  handshake and native game logon. Internet joins have no silent TCP fallback.
- **Failure and family selection:** `NatTraversalClient.cs:60` compares mappings
  from the two lobby UDP destinations. Changed mappings and UDP/probe timeout
  report “Direct connection not possible on this network” plus port-forward/
  Tailscale advice. HTTP and STUN choose IPv4 first consistently (:12), avoiding
  HTTP Happy Eyeballs selecting IPv6 while UDP selects IPv4. HTTPS is mandatory
  except loopback; proxies/redirects are disabled for this direct signaling path.

No new app, store volume, service process or package is needed. Compose and the
lobby Dockerfile expose/document both UDP ports; `docker/README.md` documents all
four new operator environment settings and the unchanged default-off behavior.
`docs/GUIDE.md` and `GLOSSARY.md` describe the player flow and implementation.

## Wire formats and API

### STUN subset

Network byte order: 20-byte RFC header (Binding request `0x0001`, success
`0x0101`, body length, magic cookie `0x2112a442`, random 96-bit transaction ID).
Only UDP Binding request/success is implemented. Length, padding, transaction,
cookie, attribute bounds and address family are validated. Unsupported required
attributes are dropped. Maximum accepted datagram: 256 bytes. Standard empty
20-byte Binding requests receive XOR-MAPPED-ADDRESS only (32 bytes IPv4, 44 IPv6).
They cannot authorize a listing or introduction.

The game sends 64-byte requests: header plus optional PADDING attribute `0x8026`
with 40 zero bytes. Success contains XOR-MAPPED-ADDRESS `0x0020` (family, XOR port,
XOR IPv4/IPv6 address), then private optional attribute `0xc936`, length 16, with
the opaque reception receipt. Responses are 52 bytes IPv4 / 64 IPv6, no larger
than these padded requests. Port XOR uses the high cookie bits; address XOR uses
the cookie followed by transaction ID for IPv6. Extra STUN services are not used
for listing addresses; this version adds no public STUN diagnostics dependency.

### Probe

Exactly 32 bytes: little-endian magic `0x36333950` (`P936`) at 0, version 1 at 4,
kind 1=request / 2=reply at 5, zero reserved bytes 6–7, 16-byte introduction secret
in .NET Guid byte order at 8–23, zero tail 24–31. The JSON secret is its 32-hex
`N` representation. Only the exact introduced endpoint/secret is accepted.
Each side sends at most 25 requests at 100 ms intervals (2.5 seconds), stops
within the introduction deadline, and sends at most 32 equal-size replies per
introduction. There are no reply chains. A successful probe does not replace
normal UDP address validation or the mission's password/team admission rules.

New remote handshakes on an internet host require a received authorized probe.
Trusted loopback host/bot clients retain access. Existing connected peers use
normal UDP tokens after the introduction expires. Dedicated/LAN hosts do not
opt in to this admission gate. The private NAT-fixture routing hooks are internal
and never enabled in production.

### HTTP JSON (camelCase)

| Endpoint | Request / result |
|---|---|
| `GET /api/nat/config` | `{ playerHostedGames, primaryPort, secondaryPort }`; ports are zero when disabled |
| `POST /api/servers/register` | Existing request plus `playerHosted:true, receipt`; address/UDP port are observed, response remains `{ id, token }` |
| `POST /api/servers/{id}/punch` | `{ receipt }` → `{ endpoint:{address,port}, secret, expiresUtc }` for the host |
| `POST /api/servers/{id}/punch/poll` | `{ token }` → `{ introductions:[...] }` containing guest endpoints, same secrets/deadlines; drain-on-poll |
| `GET /api/servers[/id]` | Existing listing plus `playerHosted:true`; false is omitted for unchanged dedicated-server JSON |
| `DELETE /api/servers/{id}?token=...` | Existing ownership-based deregistration |

Player hosting off returns 403; absent/expired/non-player listings return 404;
invalid or expired observation receipts return 400; wrong polling ownership
returns 401; introduction budgets return 429. Requests cannot supply a probe
endpoint. Player-hosted registration fixes `currentGames=maxGames=1`: the embedded match
occupies its sole game slot, so the existing create-game relay refuses additional
mission requests instead of leaving them unclaimed. All new Core JSON contracts
use source-generated metadata for AOT.

## Threat model and limits

Limit: symmetric-NAT detection compares the mappings seen at two lobby ports on
one lobby IP, so a NAT whose mapping depends only on the destination address
(not the port) is not detected; it fails later as an unanswered punch. A
second observer IP would close this. STUN replies are never larger than their
request (a bare 20-byte Binding gets no answer), so the responder cannot amplify.

The lobby is trusted signaling infrastructure, not a relay. Native UDP gameplay
remains unencrypted; path capabilities do not provide payload confidentiality.
HTTPS protects
receipts and probe secrets; public reads expose no ownership token, receipt or
introduction secret. Trusted reverse proxies must preserve the real HTTP source,
and UDP must reach the lobby directly. A spoofed UDP request alone cannot claim
an endpoint: the attacker must receive its unguessable receipt and have the same
HTTP source IP. Shared NAT addresses are permitted, but address equality alone
is insufficient. The existing approved-server filter still applies.

STUN limits: 16 responses/source IP/second and 512 globally/second, at most 4096
source windows and 8192 receipt records, expiring after 30 seconds. Standard
unpadded STUN responses have bounded 1.6× IPv4 / 2.2× IPv6 amplification; the
game's padded receipt usage does not amplify. Malformed input creates no
observations. Valid outstanding transaction/source matching precedes client
endpoint/receipt allocation.

Introductions: 3/requester IP and 6/listing per ten seconds, 128 globally/second,
bounded limiter/pending tables, at most 32 pending probes per game transport.
Host polling is ownership checked and capped at 90/IP/minute, within the
existing lobby global 120/IP/minute policy. An attacker sharing an IP can consume
its budget; authentication/relay quotas are future work. STUN receipts, game
cookies and probe secrets are capabilities, not player identity. The existing
mission password and native logon gates still decide admission. Do not log
request bodies or ownership tokens in public operator logs.

Two destination ports detect endpoint-dependent mappings that differ for those
destinations. They cannot prove the absence of address-dependent mapping,
carrier-grade NAT, restrictive firewalls, hairpin limitations or packet loss.
A bounded failed punch still gives the same direct-connection advice. Established
paths use normal UDP keepalive; listed hosts refresh discovery every five seconds.
A host IP change requires expiry/re-registration. System clocks must be reasonably
synchronized for the absolute ten-second introduction deadline.

IPv4 is preferred. Global IPv6 is allowed through the same discovery/probe path
when the lobby URL resolves only to usable global v6 and both peer endpoints are
global v6; the internet host binds dual-stack. Private/link-local IPv6 and mixed
families fail explicitly. There is no candidate gathering or automatic IPv4-to-v6
fallback. Tests exercise IPv6 XOR encoding; real v6 routing remains a hardware
check. LAN discovery remains IPv4; internet guests use Play Online. The local
TCP listener remains for host/bots and P901 compatibility; the introduction gate
applies to UDP, not native TCP admission.

## Simulated NAT evidence

Fixtures in `tests/_shared/P936NatShim.cs` use actual UDP sockets: full cone uses
one external mapping and accepts all sources; port restricted uses one mapping
and accepts only destinations already contacted; symmetric uses a distinct
external socket per destination and endpoint filtering. Internal gateways retain
the logical destination via test-only transport hooks. No result is based on
mocked packet delivery.

Development proofs have passed with **zero skipped tests** (local.env present):

| Network | Proven behavior |
|---|---|
| Full cone, both peers | Same-socket discovery/punch/reliable bidirectional records; real logon, side seating, ready/start, launch and at least five distinct pose timestamps |
| Port restricted, both peers | Same complete transport and real-session sequence through both endpoint filters |
| Symmetric host or joiner | Different mappings on the two observers; explicit failure/advice |
| Full cone before introduction | Unsolicited game Hello and unknown probes reach the host transport and get no reply/application callback |
| Expired/unknown signaling | Refused or drained without probing stale endpoints; requester and listing budgets tested |
| After introduction expiry | Real session remains connected, receives updates, refreshes its listing, then deregisters |

Real-session fixtures exercise the production lobby responder, observation
store, HTTP endpoints, host publisher, UDP transport, `ConnectionSession`, and
server/native game protocol. The simulated-NAT fixtures use in-process HTTP and actual loopback UDP. An
additional fixture runs real loopback Kestrel, the actual embedded SkirmishHost
and owned bot processes, and an ordinary InternetJoin guest through seating and
deregistration; this also exercises production HTTP address-family selection.
Existing P901 LAN/TCP tests are retained. Final repetition counts are below.

## UI evidence

Actual Game1 captures, opened for visual inspection:

- [Host form](docs/reports/P936_assets/host.png)
- [Online browser](docs/reports/P936_assets/browser.png)
- [Direct-connection failure](docs/reports/P936_assets/failure.png)

Classic and Modern captures were byte-identical for all three forms (the intro
forms share rendering across styles), so one capture of each is kept.

The host form shows the internet title, name, TCP+UDP listener port, optional
password, bot/race choices, start/back actions, configured example lobby,
firewall guidance and direct-connect fallback advice. The intro form intentionally
shares rendering across styles (the host captures are byte-identical). Browser
captures show a documentation-only example listing and masked password entry.
Failure captures show the complete direct-connection message, port-forward/
Tailscale advice, and retry/abort keys within the viewport. The real embedded-host
test initially exposed an IPv6-only TCP bind that broke
local bot startup; the UDP-only bind override fixes it and the complete embedded
HTTP/UDP/bot lifecycle now passes.
No native MDL/2-D coordinate convention or HUD composition was changed.
New media remains for the dispatcher's normal pre-push review.

## Real two-network check (dispatcher / Domingo)

1. Opt in to player hosting; retain the approved-server policy. Expose both lobby
   UDP observers without source-rewriting proxies; terminate HTTPS correctly.
2. Host from the client on one network and join from another, including a phone
   hotspot. Confirm the selected listing, password refusal/success, balanced seat,
   ready/start/launch and continuous native updates in Classic and Modern.
3. Confirm the UDP source port stays stable while listed and during game traffic;
   verify both endpoints probe. Repeat with two hosts behind the same public IP.
4. Test a blocked/symmetric network and verify explicit failure/advice; do not
   promise universal success from the two-port mapping check.
5. Stop heartbeat / kill the client and verify listing disappearance within
   20 seconds, owned bot cleanup, game shutdown and listener release. Also test
   cancellation and resume on each desktop platform.
6. Repeat P901's outstanding real-hardware/firewall checklist. If testing IPv6,
   use global v6 on the lobby and both networks; verify consistent HTTP/UDP source
   selection and firewall behavior. No real-router result is claimed here.

## Relay seam

`NatTraversalClient.ConnectAsync` is the establishment boundary: it receives the
reserved game transport and an `InternetJoin` target, establishes a direct path,
and returns a normal UDP session. `DirectConnectionException` has one actionable
failure message. A later relay policy can replace/extend establishment there,
with its own authenticated allocation and quotas, before handing off to the
unchanged game session. No TURN allocation, public relay, ICE library or automatic
router port mapping has been added. The lobby's observed-endpoint ownership
rules must continue to apply to direct candidates.

## Validation

Final lobby review also covers an administrator's shorter directory expiry and
the one-occupied-slot metadata above. These affect lobby lookup/creation policy;
the serial native-gameplay showcase comparison uses unchanged gameplay modules.

Recorded results:

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | PASS, 0 warnings / 0 errors (`gate-build-handoff.log`) |
| `tools/build-tools.sh` | PASS, all 116 projects, 0 warnings (`gate-tools-handoff.log`) |
| Linux x64 self-contained AOT publish | PASS, no IL2026/IL3050 warnings (`gate-aot-complete.log`) |
| `docker compose config --quiet` | PASS (`gate-compose.log`) |
| Glossary links / parity matrices / diff whitespace | PASS |
| Affected transport, lobby, P901 LAN and client UI tests, 3 repetitions | PASS, 0 skipped; includes P901's separate TCP/UDP guests, rotation and shutdown |
| Final-code P936 + affected selections, 3 isolated repetitions | PASS: 1,005/1,005 each combined repetition, 0 skipped; all 30 P936 cases ×3 |
| One fast-tier run | **FAIL**: 13,194 passed / 11 failed / 3 optional skips; task guard fixed and unrelated failures isolated below |
| Showcase never-parks, base 5× vs changed 5× | PASS: base 5/5, changed 5/5, zero skips |

The final combined repetition contains 13 Core traversal cases, 68 lobby cases,
2 client host-model cases and 922 MonoGame input/transport cases. The final Core
codec checks include an independent IPv4 XOR wire vector (`E112A643` for
`192.0.2.1`), alongside the IPv4/IPv6 roundtrips. The earlier broader regression
selection also passed three times, including P901's separate TCP/UDP guests,
password refusal, rotation, port conflicts, host shutdown and process cleanup.

The serial showcase comparison used the archived base above, then the changed
tree, with snapshot/encode-once/parallel-build enabled, pool TCP, native ship
updates, native GotoPlan and server GC. Interest tiers, native broadcast order
and receive decoupling remained off. Maximum observed low-speed-with-target
period was **2.726 s on base / 2.018 s changed**, below the unchanged five-second
failure condition. All ten runs observed real scouting and combat, with at
least 24/30 ships reaching their objectives. Wall time varies with the scout
opening; this is a health proof, not a throughput or speed-improvement claim.
Shared-host one-minute load rose above 30 during the last case; that case still
passed. Load receipts remain with the raw evidence. No synthetic load was used.
The final lobby-only expiry/capacity follow-ups do not change these gameplay
modules; their dedicated three-pass regressions passed after the comparison.

The extended host lifecycle test was added during validation and exposed the
IPv6-only TCP issue described above; final-code repetitions cover that fix.
NuGet's HTTP cache was redirected into scratch after an initial read-only cache
error; no package vulnerability checks were disabled. The single Fast sweep exposed the new password handler's raw-key guard
violation (corrected through the action map and followed by isolated tests),
plus ten non-task failures in already-ledgered path/profile, timer and NPC/seat
fixtures. Two existing Fast timing checks also exceeded ten seconds. The sweep
was not repeated. Three isolated outcomes per non-task method/family are
recorded in `docs/TEST_FLAKES.md`; no unrelated implementation, limit or assertion
is relaxed.

Raw logs/TRX and reproduction scripts
stay under `.scratch/p936/` inside this worktree because this lane cannot write
to the external evidence archive. Small screenshots and a final summary stay
under `docs/reports/P936_assets/`. The external `local.env` is present and ignored;
the recorded skipped count is **0 for P936 / 3 optional Meshy-pack cases in Fast**.
The large temporary baseline checkout and AOT binaries were removed after
validation; their build/publish logs and the exact base revision remain.
Small machine-readable evidence: `.scratch/p936/validation.json` (not published).

## Native-intent footer

**Era constraint / modern port extension**, not a native gameplay rule or a
native bug correction. Re-grepped native references: `_Utility/DirectPlayTransport.cpp:208`
selects the DirectPlay TCP/IP provider; :215 supplies the hosting port;
:295 and :306 supply the enumeration destination port/hostname.
`lobby/server.cpp:95-98` retains the advertised/default game port;
:125 obtains the real connection address, and :131-132 applies loopback/
approved-server policy. `WinTrek/CreateGameDialog.h:18-20` supplies the retained
full-server capacity predicate. A case-insensitive search for `stun`, `upnp`, `nat.pmp`
and `hole.punch` in the native lobby tree and this DirectPlay source returned no
matches. This does not establish what an opaque Windows DirectPlay runtime
might do internally; it establishes that this project has no equivalent
project-level traversal/signaling implementation in those paths.

The retained source/approved-server and capacity policies, plus the extension,
are tracked in `docs/parity/p936-nat-traversal.csv`; the extension is excluded from native
completion credit. A modern same-socket STUN/introduction approach improves
cross-platform reachability. A relay remains the better fallback for networks
where direct UDP is impossible; that is deliberately the next task.
