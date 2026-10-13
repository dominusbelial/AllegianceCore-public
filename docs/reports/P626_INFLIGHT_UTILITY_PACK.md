# P626 — parity wave 1: in-flight utility pack

part/cargo cycling (NextCargo / NextPart / PrevPart), drop cargo, reload,
chat scroll, vote keys — ported with native semantics.

## Summary

P617 items 9, 16, 17. Nine TK commands wired into the port's action map
(`InputActionCatalog.cs`) with native keys and native names
(`trekinput.cpp:1459`), their in-flight handlers (`Game1.FlightInput.cs` /
`Game1.InFlightUtility.cs`), the help overlay (auto-generated from the live
map via `KeyBindingTable`), 11 parity-matrix rows closed, unit tests, a
loopback wire test for drop-cargo and vote, real-window frames, and the full
suite.

### Parity scorecard (refreshed after P626, from `docs/parity/parity-matrix.csv`)

438 command rows: **done 280 (63.9%)**, partial 83, missing 47, na 28
(weighted by severity: done 1082 / partial 293 / missing 107 / na 63).
P626 moved 11 rows from missing→done and partial→done.

## Commands ported

| Command | Native cite (handler) | Port implementation |
|---|---|---|
| TK_NextCommMsg / TK_PrevCommMsg | igc.h:227-228 "scroll focus to next/previous chat message" | `Rendering/ChatScroll.cs` (pure offset model) + `Game1.FlightInput.cs` (keys) + `Rendering/ChatRenderer.cs` (offset window + "(OLDER)" indicator) |
| TK_ChatPageUp / TK_ChatPageDown | igc.h:327-329; native default keys PageUp/PageDown (VK 0x21/0x22) | `Rendering/ChatScroll.cs` + `Game1.FlightInput.cs` |
| TK_NextPart / TK_PrevPart | igc.h:232-233 "select next/previous part from inventory" (native declares, binds nothing, no case handler) | registered unbound (F6-rebindable) over the cargo selection: `Rendering/CargoSelector.cs` + `Game1.InFlightUtility.cs` |
| TK_NextCargo | igc.h:367 "rotate the selected cargo item"; handler `WinTrek.cpp:9640-9648` → `NextCargoPart` `trekigc.cpp:2620-2642`; native key "," (VK 0xBC) | `Rendering/CargoSelector.cs` (circular selection) + `Game1.InFlightUtility.cs` (`CycleSelectedCargo`) |
| TK_DropCargo | igc.h:372; handler `WinTrek.cpp:9698-9730` (drops the selected cargo part; if none, `NextCargoPart`); native key Ctrl+"," | client `SendDropCargo` (Wire/DropPart.cs) + server cargo-mount DROP_PART (`Server.ShipCommands.cs` `HandleDropPart`, `Ship.TryRemoveCargoPart`) → part treasure |
| TK_Reload | igc.h:373; handler `WinTrek.cpp:9674-9695` → `BaseClient::Reload` `clintlib.cpp:3425+`; native key R | client `SendReload` (Wire/Reload.cs) `Game1.InFlightUtility.cs`; P192 already has the server handler |
| TK_VoteYes / TK_VoteNo | igc.h:442-443; handler `WinTrek.cpp:10377-10387` `trekClient.Vote(bAgree)`; native keys Y / N | `ClientWorld.Apply(Ballot)` (latch ActiveBallot + system line) + `Session/ConnectionSession.SendVote` (Wire/Vote.cs) + `Game1.InFlightUtility.CastVote` |

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Chat buffer scroll offset model | igc.h:227-228, 327-329 | design | Bottom-anchored offset over the log; clamped; new line snaps to live edge |
| Cargo selection is the selected cargo-bay mount | trekigc.cpp:2603-2642 | design | Circular selector over occupied negative mounts (-1..-5) |
| DropCargo drops the selected cargo part → treasure in space | WinTrek.cpp:9698-9730; FedSrv.CPP:6296-6330 | design | Negative-mount DROP_PART → `Ship.TryRemoveCargoPart` → `CreateDropTreasure` |
| Reload tops pools/launchers from cargo | WinTrek.cpp:9674-9695; FedSrv.CPP:6504-6653 | design | Client computes NA per cargo slot; P192 server handler applies |
| VoteYes/No cast on the active ballot | WinTrek.cpp:10377-10387; Messages.h:185-188 | design | FM_C_VOTE(19) against `ClientWorld.ActiveBallot` id |
| Native binds NextPart/PrevPart to nothing, no handler | defaultinputmap.mdl; WinTrek.cpp | design (native leaves them unbound) | Registered as unbound-but-rebindable commands over the cargo selection |
| Malformed RELOAD entry refuses the rest, no broadcast | FedSrv.CPP:6531-6648 (softened) | bug/era | Kept the existing pinned P192 softened-refuse behaviour (a client entry per cargo slot is well-formed for reloadable slots) |
| Native hard-boots a connection on malformed reload/drop | FedSrv.CPP TrapHackBoot | bug/era | Port's established soften-to-log-and-refuse (never disconnect) |
| Native computes reload amounts client-side from live pool levels | clintlib.cpp:3425+ | era (client sim knows its pools) | Port has no in-flight pool/type metadata; the client sends NA (consume whole item) per slot and the server validates — server-authoritative, no fabrication |

## Modern alternative considered

Native transmits reload entries as client-computed pack transfers and trusts
them; the port instead keeps the server authoritative (`Ship.TryApplyReloadEntry`
validates mount range, type and amount, FedSrv.CPP:6531) so a misbehaving or
stale client cannot overfill a pool or corrupt the hold — the modern "thin
client, authoritative server" posture, with the player-visible behaviour
native-faithful (top up pools from cargo). Chat scroll uses a single clamped
integer offset over an append-only log rather than native's widget-level
cursor, which is cheaper and O(1) per keypress. Cargo is delivered to the
owner inside the existing BUY_LOADOUT_ACK (native mixes mounted + cargo into
one loadout tail) rather than a new message type, keeping the wire surface flat.

## Key binding decisions (native wins where possible)

- ChatPageUp=PageUp, ChatPageDown=PageDown (native defaults; CycleCommandee
  moved to Shift+PageDown — relocated, P624 precedent).
- NextCommMsg/PrevCommMsg = Ctrl+PageUp / Ctrl+PageDown (native leaves these
  unbound; free chords in the PageUp/Down family).
- NextCargo="," , DropCargo=Ctrl+"," (native defaults; "," is only the starmap
  sector-nav key while the map is expanded, a different context).
- VoteYes=Y (native; ready-check Y is a different, pre-flight screen).
- VoteNo=Ctrl+N — divergence: native N is the port's DropMine (P224), and
  native's chaff key Z is taken by P415 enemy-base targeting, so a full native
  re-alignment is impossible (documented).
- Reload=Shift+R — divergence: native R is the port's QuickChat (P217); manual
  reload is a convenience (P192 auto-reloads on empty), so Shift+R rides the
  R family.
- PrevPart/NextPart = unbound (native also binds them to nothing); F6-rebindable.

All keys resolve through the live action map, so the help overlay and any
rebind stay consistent (`InputActionCatalog.cs` is the single source).

## Tests

`P626InFlightUtilityPackTests.cs` (MonoGame, all pure): ChatScroll clamp /
step / page / start-index / can-scroll; CargoSelector mount encode/decode,
occupancy sort, next/prev cyclic, empty-hold; catalogue registers all 9 new
commands with native TK names + keys; native-wins key choices; help-overlay
appearance; no-duplicate-keys within each help group.

`ClientWorldBallotTests.cs` (ClientCore): a decoded BALLOT(17) sets
`ActiveBallot` and posts a system line; a newer ballot replaces it.

`P626InFlightUtilityLiveTests.cs` (Core, loopback over real bots/TCP):
- **DropCargo** — a negative-mount DROP_PART drops a cargo pack, empties the
  slot and relays the negative-mount DROP_PART sector-wide with the filled
  shipID (the server also creates the part treasure — observed in the server
  log, which the dropper instantly re-collects).
- **ExportInventoryForAck** — the BUY_LOADOUT_ACK tail now carries the cargo
  hold as negative-mount entries.
- **Vote** — a real FM_C_VOTE(19) against the active ballot id tallies and
  produces the observable effect (passing #mutiny transfers leadership).

## Verification

Whole-solution build: **0 warnings, 0 errors** (both Debug and Release).
Full suite via `scripts/run-tests.sh`: **2 consecutive runs on the final code** —
see the numbers below (remaining failures are known ledgered live-network
load-flakes in `docs/TEST_FLAKES.md`, none P626-caused; see "Native behaviour
classified" / the flake ledger).
Real-window frames (real backbuffer 2560×1440, `scripts/live-proof.sh` +
`ALLEGIANCE_INFLIGHT_PROOF=1`):
`docs/reports/P626_assets/*.png`:
- `help_overlay.png` / `help_overlay_2.png` — the H overlay showing every new
  P626 command and the two new help categories (INVENTORY / CARGO, VOTE /
  COMMAND), incl. NextCargo `,`, DropCargo `Ctrl+,`, Reload `Shift+R`, Vote
  YES `Y`, Vote NO `Ctrl+N`, PrevCommMsg/NextCommMsg `Ctrl+PgUp/PgDn`.
- `chat_paged_history.png` — the chat pane scrolled back (history visible with
  the "(OLDER)" scroll state).
- `chat_paged_and_cargo.png` — scrolled chat + the in-flight cargo HUD readout.
Live bot check (local server, `scripts/live-proof.sh --bots
'spawn_scenario {"size_scale":1,"target_sector":"Alderan"}'`): bots leave home
and reach the target — server logs `[warp] ship 8097 crossed from 'Plato' to
'Alderan' via warp 2184`; no disconnect/error introduced (the only rejects are
the normal per-cookie-epoch ship-update drift, and the single ReadErrorOrEof is
the client exiting at the deadline). Scale was small (2 NPC miners) because
this change touches only two rare message handlers (DROP_PART cargo mounts,
BUY_LOADOUT_ACK cargo tail), not the tick / broadcast / transport / bot path,
so the 200v200 combat-baseline measurement is not triggered.

### Suite runs (final code)

Two consecutive `scripts/run-tests.sh` runs on the final committed code, as
measured (reporting each run as it happened):

| Run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1949/1949 (3 skipped) | 680/680 | 4012/4012 |
| 2 | 12/12 | 1949/1949 (3 skipped) | 679/680 | 4011/4012 |

Run 2's two failures are both **known ledgered live-network load-flakes** in
`docs/TEST_FLAKES.md`, unrelated to P626 (they are live-wire timing tests in
other subsystems that flake intermittently under full-suite socket load, and
both passed in run 1):
- `ClientCore…CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return` — TEST_FLAKES.md line 396.
- `Core…GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` — TEST_FLAKES.md line 432.

Per the task's reporting rule ("report each run as it happened; don't re-run a
failing suite until clean"), no further re-runs were made.

## Files changed

- src: `InputActionCatalog.cs`, `ChatScroll.cs`, `CargoSelector.cs`,
  `ChatRenderer.cs`, `Game1.cs`, `Game1.FlightInput.cs`, `Game1.Session.cs`,
  `Game1.Rendering.cs`, `Game1.InFlightUtility.cs`, `Game1.InFlightProof.cs`,
  `Session/ConnectionSession.cs`, `State/ClientWorld.cs`, `State/ShipEntity.cs`,
  `Sim/Ship.cs`, `Server/Server.ShipCommands.cs`
- tests: `P626InFlightUtilityPackTests.cs`, `ClientWorldBallotTests.cs`,
  `P626InFlightUtilityLiveTests.cs`, plus the two help-overlay count tests
  updated for the two new categories (`P625KeyMapTests.cs`,
  `HelpOverlayTests.cs`)
- docs: `parity-matrix.csv` (11 rows closed), `P626_INFLIGHT_UTILITY_PACK.md`,
  `P626_assets/` (real-window frames)
