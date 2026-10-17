# P731 — The Native Intro Screen: Single Player or Multiplayer at Start

Author: Hermes Agent · Date: 2026-10-16 · Branch: `master` (commit `766999c6`)
Status: implemented, build 0/0, new tests green, real-client screenshot proof captured.

## Why

Domingo: *"Display the choice at start to either go for single or multiplayer
(you get to search or type a server address etc.)."*

Before P731 the client went straight in from command-line arguments
(`server port pilot`, or an empty server → lobby), and `player.json`
(P644/P726) remembered values. Native had exactly this screen —
`WinTrek/introscreen.cpp` — with `m_pbuttonPlayInt` (internet/lobby),
`m_pbuttonPlayLan` + `FindServerPopup` (:708-880), `m_pbuttonTrainingBig`,
Options, Help, Credits, Exit. The port had no intro screen.

## What was built

### 1. The intro screen (faithful port of `introscreen.cpp`)

A new `ClientState.Intro` renders a main menu with the same slots as native,
plus a pilot-name field, using the port's 2D UI idiom (the `_hudSmall` /
`_hudBig` sprite-font menus already used by the lobby/connect screens). It
lives in `Game1.IntroScreen.cs` with a pure, unit-testable model
(`IntroScreenModel`, `IntroLayout`) so every path is testable headless.

| Native element | Native cite | Port |
|---|---|---|
| Single Player | `trainBigButtonPane` :1221, `OnButtonTraining` :1558 | Opens the **Single Player / Skirmish** view — the hook P727's skirmish plugs into (a stub today; training missions are a later track, shown as coming-later, mirroring native's commented-out training slot :579/:1220) |
| Play Online | `playIntButtonPane` :1213, `OnButtonInternet` :1822 (→ lobby / Zone Club) | Goes to the port's **lobby** server browser (`ClientState.Lobby`) |
| Play LAN / Find Server | `playLanButtonPane` :1212, `OnButtonLAN` :1832 (opens `FindServerPopup`) | Opens the merged **Find Server** list (see §2) |
| Direct Connect | *(modern addition; native had no direct-IP field — see the `ALLEGIANCE_DIRECT_SERVER` note at :1630-1656)* | `host[:port]` field (hostname / IPv4 / IPv6 / MagicDNS) |
| Options | `optionsButtonPane` :1223, `OnButtonOptions` :1847 (`ShowOptionsMenu`) | Opens the existing settings (the Ctrl+F6 key-bindings/settings popup, `ToggleKeyBindings`) |
| Credits | `creditsButtonPane` :1225, `OnButtonCredits` :1853 | Static credits view |
| Exit | `exitButtonPane` :1238, `OnButtonExit` :1799 (`GetWindow()->StartClose()`) | `Exit()` |
| Pilot name | `trekClient.SaveCharacterName` :1607 | Pilot-name field, remembered in `player.json` |

Navigation: UP/DOWN select, ENTER choose, TAB to edit the pilot field, ESC
back/quits. Command-line arguments (`[host] [port] [name]`, `--capture`,
`--inspect`) still bypass the intro entirely — no behaviour change for rigs,
tests, bots or the future `--benchmark` work. With **no** command-line
arguments the client opens the intro (what the P644/P726 desktop shortcut
runs); the intro is bundle-ready and P726 wires the shortcut.

### 2. Find Server — multi-source discovery (Domingo, 2026-10-16)

> "Play LAN / Find Server must find servers over VPNs and Tailscale too.
> Broadcast and multicast do not cross VPNs, and Tailscale does not carry them
> between nodes."

The find-server list merges five **discovery sources** into one list, each
entry showing its **source** and **ping** (`DiscoveryCoordinator`,
`DiscoveryEngine`, `DiscoveryTargets` in `Allegiance.ClientCore.Game/Discovery/`):

1. **LAN** — UDP broadcast (directed + 255.255.255.255) and IPv4 multicast to
   the responder, on native's `PollForServers` 1.0 s cadence
   (introscreen.cpp:824), made async so the UI never hangs (the era constraint
   native worked around at :897-901).
2. **Tailscale** — `tailscale status --json`; each **online** peer is
   unicast-probed on its Tailscale IP and shown with its **MagicDNS** name.
   Parallel, bounded, fast timeouts. Absence of Tailscale is silent.
3. **VPN / other subnets** — a bounded unicast sweep of each active
   non-loopback interface's subnet (WireGuard/OpenVPN/ZeroTier included),
   capped (<= /24), rate-limited, cancellable. Off for subnets larger than /24.
4. **Favorites / recent** — remembered addresses/hostnames from `player.json`,
   refreshed by the same unicast query.
5. **Lobby** — the online `Play Online` list, merged so a lobby reachable over
   the VPN also works.

Entries are de-duplicated by `host:port` (first-wins by source order) and each
carries source + ping. `FormatRow()` produces e.g.
`Test Mission  192.168.1.5:2326  3/100 players  [LAN] 2ms  XXV_simple.igc`.

**Direct connect** is a modern addition: a `type address[:port]` field
(hostname, IPv4/IPv6, MagicDNS) — native had no direct-IP screen.

**Server side — the discovery responder** (`Discovery/DiscoveryResponder.cs`):
a small UDP responder on a configurable port (default **game port + 1**) that
answers **broadcast and unicast** queries with `name, map, players/max,
version, game port`. It is rate-limited (token-bucket per second), carries no
secrets, and can be disabled. Config keys (P686-style guarded JSON defaults,
validated, documented in `server.example.json`):

```json
"discovery": {
  "enabled": true,          // false = no socket, no replies
  "port": 6074,             // null = game port + 1 (6074 for a 6073 game)
  "rateLimitPerSecond": 20,
  "multicastGroup": "239.255.7.31"
}
```

The client's well-known probe port is 6074 (`DiscoveryDefaults.Port`), which
equals the server's default `game port + 1` for a default 6073 game. An admin
who moves the game port should pin `discovery.port` to 6074 (or tell players
the port) so clients can still find the server — documented in the config
comment. The shared `DiscoveryProtocol` DTO (request/reply JSON with a nonce
the responder echoes) lives in `Allegiance.Core` so the client and server
cannot drift; both projects reference Core.

### 3. Wire / player.json

- `player.json` lives at `%AppData%/AllegianceCore/player.json`
  (`IntroPreferences`), outside the install. Field names match the P726
  bundle shape (`server/port/pilot/lobby`) plus a `recent[]` list. Remembers
  the last server + pilot; a failed join returns to the intro
  (`_returnToIntroOnFailure`).
- New shared discovery DTO in `Allegiance.Core/Discovery/DiscoveryProtocol.cs`.
- Command-line bypass preserved; `opts.Intro = true` when no game-starting
  args (Program.cs), `ClientState.Intro` booted in the Game1 constructor.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| LAN server poll cadence | `PollForServers` every 1.0 s (:824); background polling disabled (:901) | era constraint | Keeps the 1.0 s cadence, but async (native froze the GUI; the port must not) |
| DPLAY8 duplicate stripping | :916-931 | bug workaround | Port de-dupes by `host:port` (modern, cleaner) |
| "Searching…" sentinel | :983-987 | design | Port shows "Searching…" while scanning |
| Single-player/zone filtering | :952-953 `nMaxPlayers > 1 && < 1000` | design | Port separates skirmish into its own path; LAN list shows real servers |
| Rate limit / no secrets on responder | *(no native analogue — native's enumeration was unfiltered)* | hardening | Responder is rate-limited and carries only public facts |
| Direct-IP connect | `ALLEGIANCE_DIRECT_SERVER` :1642-1656 (diagnostic only in native) | design | Modern addition — a real address[:port] field |

## Modern alternative considered

Native sends fixed-modem-era broadcast enumeration with a synchronous GUI-freezing
poll. The port instead uses a tiny JSON-over-UDP request/reply protocol with
per-request nonces (matching replies to a socket multiplexing many probes),
bounded/cancellable parallel probing, and five merged discovery sources
(LAN / Tailscale / VPN-subnet / favorites / lobby). Broadcast+multicast still
serve the LAN, while unicast probing makes Tailscale/VPN discovery work — the
task's explicit requirement. Chosen: the JSON UDP protocol over a bespoke
binary format for debuggability and because both sides already treat their
counterpart DTOs as documented JSON contracts; the DTO is declared once in
`Allegiance.Core` so they can't drift.

## Tests

- `IntroScreenModelTests` — each menu path's command (single-player hook, lobby,
  find-server, direct connect, options, credits, exit), navigation wrap,
  direct-connect parsing (hostname/IPv4/[IPv6]:port/bare IPv6/MagicDNS), invalid
  rejects.
- `IntroPreferencesTests` — player.json round-trip, tolerant load, `Remember`
  (newest-first, deduped, capped), path outside install.
- `IntroLayoutTests` — main-menu geometry fits the viewport and avoids the pilot
  field/footer at 720p / 1080p / 1440p.
- `DiscoveryTests` — LAN endpoint enumeration (directed/limited broadcast +
  multicast), subnet-scan cap (/16 skipped, /24 = 253 hosts, own address
  excluded), Tailscale `status --json` parsing (online-only), end-to-end
  **unicast probe against a real loopback responder**, **loopback broadcast
  discovery**, coordinator merge/dedupe with a mocked `tailscale status`, and
  silence when Tailscale is absent.
- `DiscoveryResponderTests` — request/reply round-trip + nonce echo, reply's
  public facts + game port, malformed/foreign datagram rejection, rate-limit
  drop, config default (enabled) vs explicit disable.
- `DiscoveryResponderServerBootTests` — a **real booted server** answers a UDP
  discovery query on game port + 1 with its live mission name/map/game port
  (requires artwork).

All new tests pass (37 new tests: 32 in MonoGame.Tests + 5 in Core.Tests, plus
1 server-boot test).

## Proof

- Whole-solution build: **0 warnings, 0 errors** (verified after all changes).
- New tests: all green. 37 tests added for P731, all passing:
  - MonoGame.Tests: 32 (intro model 9, prefs 5, layout 6, discovery 12).
  - Core.Tests: 5 responder/protocol + 1 real-booted-server discovery query.
- **Showcase never-parks 10×** (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`,
  run in isolation): **10/10 passed**, never parked, no failures
  (each run ~1 m 35 s).
- **Real client screenshot** (real backbuffer via `ALLEGIANCE_AUTOSHOT`, window
  2560x1440): `docs/reports/P731_assets/intro_main_2560x1440.png` — shows the
  ALLEGIANCE title, all seven menu buttons ("Single Player" highlighted), the
  Pilot field, and the control hints. Confirmed visually (not an offscreen
  render).
- **Real booted server** answers a UDP discovery query on game port + 1 with
  its live mission name/map/game port (automated test; requires artwork).

### Addendum — full suite (`scripts/run-tests.sh`)

The mandated 2× full-suite run **could not be completed**: the suite hangs in
the pre-existing live-network `Allegiance.ClientCore.MonoGame.Tests` assembly
(a live test wedged on a socket at ~0–6 % CPU). This is **not** a P731
regression — the evidence:

- The **pre-P731 baseline** (`d0acd9ec`, temp worktree) hangs identically in
  the same MonoGame.Tests assembly.
- The concurrently-running **Codex agents'** suites (the P726 bundle worktree
  and a P732 worktree) hang identically right now, in the same assembly, at
  ~0 % CPU — three agents' heavy live suites all saturate the shared machine's
  live-network tests simultaneously (load average 2–4 sustained).
- `docs/TEST_FLAKES.md` documents this live-network load-flake class
  extensively; Domingo's own guidance (P639) notes clean runs are rare
  (~1/17) precisely because of live-network load flakes.

Attempted full-suite runs of the final code all reached
`MAIN MonoGame.Tests` and stalled there; each was stopped after a bounded wait.
Measured during these runs: `Allegiance.Lobby.Tests` passes 19/19. I stopped
re-running once it was clear the hang is systemic (four genuine attempts; three
were after the code fix). Per the P639 rule ("never re-run the full suite
hoping for a clean run") and the session rule, I did not keep burning runs.

All P731-specific verification that is not dependent on the contended full
suite is complete and green (build 0/0, 37 new tests, showcase 10/10, real
client screenshot, real-booted-server discovery). The full-suite 2× should be
re-run on a quiet machine (no concurrent agents) to record the final numbers.

## Deliverables

- Commits on `master` (not pushed): see `git log`.
- Report: this file.

## Notes

- The intro screen's Find Server scan and lobby merge are bounded and
  cancellable; leaving the view stops the background scanner and disposes its
  socket.
- `player.json` is shared with the P726 bundle shape; bundle-specific shortcut
  wiring is left to P726 (the intro is bundle-ready: no-arg launch → intro).
