# P127 — FM_CS_LOGOFF(3) — graceful client-initiated disconnect

Repo: `AllegianceCore` branch `master`, on top of `a0dc934` (P126).

Scope: `FM_CS_LOGOFF`(3), the zero-field `CS` (client-or-server) message a
client sends when it wants to log off. As close to the cheapest possible
fmid in the port as `SUICIDE`(80)/P103 — same class, and the smallest item
in Phase 6.

## The connection-cleanup finding (answered plainly)

Does an explicit `LOGOFF` need to do anything beyond echoing, or does
native's `delete pfsPlayer` do something meaningfully different that this
port must replicate?

**Finding: native's `delete pfsPlayer` maps exactly onto this port's
existing disconnect-cleanup path, so LOGOFF reuses it — there is nothing
meaningfully different to replicate, and building a second cleanup
mechanism would be wrong.**

Why. The port's architecture separates two things that native's
`pfsPlayer` bundles into one object:

- the *peer → commanded-ship* association (`Server._peerShips`,
  `Server.cs:57`, plus `_peerNames` for the remembered name at
  `Server.cs:68`), which is what "a player commands a ship / is in the
  broadcast set" means here; and
- the *ship object* itself, owned by the mission and placed in a sector.

An abrupt TCP drop is already cleaned up by the transport raising
`OnPeerLeft` (`TcpTransport.cs:207`, in the read-loop `finally`) →
`PeerLeftEvt` → `Server.HandleTransportEvent` (`Server.cs:913`), which
removes the peer from `_peerShips`/`_peerNames`. The ship object stays in
the sector and keeps flying under `Mission.Update` — that is this port's
defined "commander departed" behaviour (the comment at `Server.cs:914-918`
states it explicitly: "the ship itself stays in the sector and keeps
flying under Mission.Update — that is what 'a bot disconnect leaves the
server ticking' exercises").

Native's `delete pfsPlayer` deletes the player object immediately on
LOGOFF (`FedSrv.CPP:5787-5802`). The port's equivalent "immediate" effect
of deleting the player is dropping the peer from the command/broadcast
sets — which is precisely the two-line cleanup the disconnect path already
performs. So the honest, non-duplicating implementation is to reuse that
path. The ship-object-stays behaviour is a deliberate, already-documented
port simplification and is intentionally not changed here (it is identical
to a disconnect). Therefore: **LOGOFF = echo back + run the existing
disconnect-cleanup path. No new mechanism.**

(The one nuance: a real client that sends LOGOFF then closes its socket
will ALSO trigger the transport's `OnPeerLeft`, so `ReleasePeer` runs
twice. Both removes are idempotent `Dictionary.Remove` no-ops — safe.)

## What changed

1. `src/Allegiance.Core/Wire/Logoff.cs` — the zero-field codec, mirroring
   `Suicide.cs`'s shape exactly (header-only, `cbmsg = 4`, fmid 3; `CS`
   direction marker vs `C`). Round-trips and rejects a foreign fmid / wrong
   length. Direction-agnostic by construction, which is what lets native
   `ForwardMessage` the same bytes straight back.

2. `src/Allegiance.Server/Server.cs`
   - Factored the disconnect cleanup into one reusable method
     `ReleasePeer(ConnectionId)` (`Server.cs:940`), now called by both
     `PeerLeftEvt` (`Server.cs:919`) and the new LOGOFF handler — the reuse
     the task asked for, not duplication.
   - Added the `FM_CS_LOGOFF(3)` inbound handler (`Server.cs:1928`):
     `Logoff.Decode(m)` validates the frame, then echoes the exact same
     (empty) message back to the same connection via
     `SendPeer(from, FedPacket.Pack([m]))` (guaranteed — `SendPeer`
     forwards `guaranteed: true`, `Server.cs:315-320`, matching native's
     `ForwardMessage(..., FM_GUARANTEED)`), then `ReleasePeer(from)`.
     No ship gate (unlike SUICIDE): LOGOFF acts on the connection itself,
     so it holds even for a peer that never validated. Malformed frames
     are reported and dropped like every other inbound handler.

## Verification

- `tests/Allegiance.Core.Tests/LiveLogoffTests.cs` — 4 tests, all real:
  - `Logoff_message_round_trips`: exact bytes `{4, 0, 3, 0}` + round-trip.
  - `Logoff_echo_re_decodes_from_the_client_side`: direction-agnostic.
  - `Logoff_echoes_and_releases_the_peer_via_the_disconnect_path` (live
    loopback): a connected player sends LOGOFF; the client receives the
    Logoff echo; the peer vanishes from `server.Ships` (=
    `_peerShips.Values`, the broadcast/command set, exactly what a
    disconnect empties); and the ship *object* still occupies the sector
    (`server.Missions[0].Sectors...OfType<Ship>()`) — proving the reused
    path, not a ship destroy.
  - `Logoff_cleanup_matches_an_abrupt_disconnect_footprint` (live
    loopback): a graceful LOGOFF leaves the exact same observable
    footprint as an abrupt socket drop — both peers released from `Ships`,
    both ship objects still in the sector. There is no second, parallel
    mechanism.
- Full suite: 2623 → **2627** (+4). Three consecutive clean runs, 2627
  passed each; neither the `LiveChatSectorWireTests` nor the
  `LiveCombatWireTests` timing flake fired.

## Notes / out of scope (confirmed)

- Steam auth session cleanup — N/A, absent from this port (fed through
  native's `FedSrv.CPP` Steam branch).
- P103–P126 code untouched beyond extracting the two-line cleanup into the
  shared `ReleasePeer` (behaviourally identical for the disconnect path).
- `FedMessageCatalog.Ported`/dumper left alone: that list is a maintained
  decode registry and already omits many implemented ids (20/105/80/90/…);
  adding LOGOFF there would be cosmetic and out of this task's scope.

## Natural next Phase 6 item (surfaced by this task's research)

**Retire the orphaned ship when its commander leaves.** Working LOGOFF made
the port's defined simplification concrete: a logged-off (or dropped)
player's ship object stays in the sector and flies forever under
`Mission.Update` — each logoff/drop leaks one zombie ship that nothing ever
reaps. Native `delete pfsPlayer` removes the player from the mission
entirely; the port deliberately defers that. A small, tractable next item
is a per-tick sweep that retires a ship whose commanding peer is no longer
in `_peerShips` (a genuine `SHIP_DELETE`/despawn via the existing
destroyed-ship machinery), so the sector doesn't accumulate ghosts. It is
Phase-6-sized (one sweep + the existing despawn reuse), needs no VM, and is
orthogonal to the large visibility-system items blocking Phases 4/5.
