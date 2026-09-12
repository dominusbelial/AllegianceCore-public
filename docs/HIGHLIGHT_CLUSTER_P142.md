# HIGHLIGHT_CLUSTER_P142 — relay FM_CS_HIGHLIGHT_CLUSTER(199)

Date: 2026-09-10 · Branch: `master` · Repo: `AllegianceCore` (server-side wire
relay; no native `Allegiance` change, no Wopr/Windows VM involvement).

## What this does

Teaches `AllegianceCore`'s headless server to decode and relay
`FM_CS_HIGHLIGHT_CLUSTER`(199) — a commander marking a sector on the map for
their own teammates (Xynth #208) — as a **pure relay**: decode, resolve the
sender's `SideId`, forward the original bytes to every peer on that side.
Previously any inbound 199 fell through to the catch-all
`[wire] from {from}: unsupported fmid {fmid} — ignored at headless milestone`
(`src/Allegiance.Server/Server.cs:2263`) and was silently dropped.

## The native handler is genuinely this simple — confirmed

`Inc/Messages.h:1033-1036` (quoted in full):

```cpp
DEFINE_FEDMSG(CS, HIGHLIGHT_CLUSTER, 199)  //Xynth #208 Notify clients of sector highlight
    SectorID            clusterID;
	bool				highlight;
END_FEDMSG
```

`FedSrv/FedSrv.CPP:7369-7375` (the entire handler — no more logic than this):

```cpp
	//Xynth #208 8/2010
	case FM_CS_HIGHLIGHT_CLUSTER:
	{
		CASTPFM(pfmHC, CS, HIGHLIGHT_CLUSTER, pfm);

		g.fm.ForwardMessage(CFSSide::FromIGC(pfsPlayer->GetSide())->GetGroup(),pfmHC, FM_GUARANTEED);
	}
	break;
```

Confirmed: **no `STAGE_STARTED` gate, no cluster-existence check, no
sender-side check, no validation** — the case body is only the cast + a
forward of the exact same bytes to the sender's own side group
(`CFSSide::FromIGC(pfsPlayer->GetSide())->GetGroup()`). There is no missing
validation step to port; this port adds none.

## Relay-pattern reuse (the port)

Rather than any new relay infrastructure, the handler reuses the exact
established relay shape of the `CHATBUOY`/`CHATMESSAGE` branches of
`Server.Drain` (`src/Allegiance.Server/Server.cs:1867-1943`):
_decode → `_peerShips.TryGetValue(from, …)` to resolve the sender's ship and
its `SideId` → `FedPacket.Pack(new[]{ m })` to re-pack the **original bytes**
verbatim → `SendPeer` to the selected peers_ (CHATBUOY branch,
`Server.cs:1920-1937`). The one difference is the target selector: instead of
`SelectChatTargets`, the branch uses the **same** `_peerShips.Where(kv =>
kv.Value.Ship.SideId == senderSideId)` predicate the `Team` chat target uses
inside `SelectChatTargets` (`Server.cs:2370-2374`) — the faithful equivalent
of native `GetGroup()` (the sender's side), so every same-side peer (including
the sender, as the native group includes it) gets the relay, and no
different-side peer does.

New `HighlightCluster` codec (`src/Allegiance.Core/Wire/HighlightCluster.cs`)
mirrors the sibling short+bool `TELEPORT_ACK(102)` shape (`TeleportAck.cs:9-14`):
`cbmsg`(2) + `fmid=199`(2) + `clusterID` short(2) + `highlight` C++ bool(1) = 7
B fixed, no var items.

## Files

- `src/Allegiance.Core/Wire/HighlightCluster.cs` — codec (new).
- `src/Allegiance.Server/Server.cs` — relay branch inserted after the CHATBUOY
  branch (before `Docked`, ~line 1944).
- `src/Allegiance.Bot/Bot.cs` — `SendHighlightCluster`,
  `Highlights`/`ResetHighlightsReceived`, and the inbound decode case.
- `tests/.../Wire/HighlightClusterWireTests.cs` — codec unit tests.
- `tests/.../LiveHighlightClusterWireTests.cs` — loopback live test.

## Verification

- **Loopback live test** (`LiveHighlightClusterWireTests`, the same
  three-bots-two-sides arrangement as `LiveChatBuoyWireTests`): the sender
  highlights cluster 13 (`highlight=true`); the same-side teammate receives it
  **byte-exact** (decoded message re-encodes to the sender's original bytes,
  `ClusterId==13 && Highlight==true`) and the opposite-side bot does **not**
  receive it. A second phase confirms `highlight=false` relays the bool value
  itself (clear), still same-side-only. Both phases:
  `LIVE-HIGHLIGHT: … — RESULT PASS`.
- **Full suite clean, 3 consecutive runs**: 2711 tests (2703 baseline + 8
  new), 0 failures, three runs in a row (B2/B3/C1). The intermittent failures
  seen across the earlier batches were the documented live-network timing
  flakes (`LiveCombatWireTests`, `LiveSuicideShipDeleteTests` — the task names
  `LiveCombatWireTests`; verified passing 16/16 in isolation), unaffected by
  this additive change.
