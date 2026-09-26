# P396 — wire the Leaders and Wing chat targets

Status: landed on `master` (unpushed — Domingo pushes).

## Summary

The chat dispatcher's `SelectChatTargets` switch (`src/Allegiance.Server/Server.Lobby.cs`)
now routes two previously-deferred `ChatTarget`s whose real prerequisites had
already landed:

- **`Leaders` (1)** — reaches **every side's** current leader (a cross-team
  "leaders channel"), matching native `FedSrv.CPP:3467-3480` (`case CHAT_LEADERS`).
- **`Wing` (8)** — reaches every **same-side** peer ship whose `WingId` matches
  the message's `OidRecipient`, matching native `ForwardChatMessageToWing`
  (`FedSrv.CPP:3327-3345`, direct player-chat call at `FedSrv.CPP:3594`).

`Allies` and `Admin` remain deliberately un-implemented (see the out-of-scope
rationale in the brief / README), and `Ship` remains a separate unresolved
prerequisite. `Group`/`GroupNoEcho` are untouched (they alias `Individual`).

## Leader-lookup mechanism

`Leaders` reuses the **P183 `Side.LeaderShipId` storage** directly rather than
writing a new `O(peers)` scan:

```csharp
case (int)ChatTarget.Leaders:
{
    if (_peerShips.TryGetValue(from, out var leadersSender))
    {
        var mission = leadersSender.Ship.Mission;
        if (mission is not null)
            return mission.Sides
                .Select(s => LeaderShip(mission, s.Id))   // P183 storage
                .OfType<Ship>()
                .Select(s => ConnOf(s))                   // ship -> connection
                .Where(c => c is not null)
                .Cast<ConnectionId>()
                .Distinct()
                .ToArray();
    }
    return Array.Empty<ConnectionId>();
}
```

`LeaderShip(mission, sideId)` (`Server.Lobby.cs:961`) resolves a side's
`LeaderShipId` owner to its live `Ship`, and `ConnOf(ship)`
(`Server.Lobby.cs:951`) maps that ship to its peer `ConnectionId`. These two
existing accessors are exactly the "given a side, find its leader's connection"
reverse-lookup the brief asked for — no new scan, no duplicated leader tracking.

Semantics preserved from native:

- **Every side**, not just the sender's — `mission.Sides` is swept wholesale,
  mirroring `while (iSide-- > 0) ... GetLeader(iSide)`.
- **Sender inclusion** follows native's every-side sweep: a side's leader is
  included even on the sender's own side (so a leader addressing `Leaders`
  reaches itself), matching this port's `Everyone` self-relay convention.
- The `commandID == c_cidNone` guard is trivially satisfied — a plain player
  chat message carries `c_cidNone`; the non-`c_cidNone` form is the separate
  drone-order chat-buoy dispatch which never flows through this case. Confirmed,
  no special handling needed.

## Wing routing

```csharp
case (int)ChatTarget.Wing:
    return _peerShips
        .Where(kv => kv.Value.Ship.SideId == senderSideId)
        .Where(kv => kv.Value.Ship.WingId == chat.OidRecipient)
        .Select(kv => kv.Key)
        .ToArray();
```

Mirrors native exactly: same-side ships only, `WingId == pcd->oidRecipient`
(the target wing id carried in the message's `OidRecipient`), no skip parameter
(the direct player-chat call passes `pshipSkip = NULL`). Native iterates
`GetSide()->GetShips()`, **not** sector-restricted — so a same-side wingmate
in another sector is reached (and the live test proves exactly that).

## WingId-assignability finding (brief point 3)

`Ship.WingId` (`Ship.cs:647`, `short`, default `0`) is **genuinely assignable
by a real, reachable game mechanism** — it is not a settable-but-never-set
field. `SET_WINGID` is a real inbound wire message: `Server.ShipCommands.cs`
handles it (`FM_CS_SETWINGID`), validates the range `[0, WidMax=10)` against
the native bound `FedSrv.CPP:5319` (`c_widMax = 10`, `igc.h:505`), enforces
self-targeting (native `FedSrv.CPP:5323`), corrects `bCommanded=false`
(`FedSrv.CPP:5334`), broadcasts side-wide, and mutates `ship.WingId =
req.WingId` (`Server.ShipCommands.cs:941`) — the port of native
`pfsShip->GetIGCShip()->SetWingID(...)` (`FedSrv.CPP:5338`). The port's own
`FltBot.SendSetWingId` exercises the real codec.

So wing chat is **fully reachable today**: a client can assign a wing and then
address that wing over chat, end-to-end, with no gap. (The finding is not
speculative — the live test assigns wings over the real `SET_WINGID` wire path
and then routes wing chat over the real `CHATMESSAGE` path.)

## Out-of-scope reconfirmation

- **`Allies`** — not implemented. Native's `ForwardChatMessageToAllies` was
  commented out by native's own team after a crash (`FedSrv.CPP:3352`,`:3452`,
  "imago fixed crash 7/13/09 removing FwdChatMsgToAllies"); never functional in
  the shipped server. Porting would invent behavior, not restore it.
- **`Admin`** — not implemented. Literally `//NYI` in native (`FedSrv.CPP:3485`).
- **`Ship`** (parent/child routing) — not implemented; separate prerequisite
  not re-verified this pass.
- **Wing-assignment UI** — not needed as a side effect; the mechanism already
  exists (see above).

## Live multi-peer verification (real wire, both targets)

Proof: `tests/Allegiance.Core.Tests/LiveChatLeadersWingWireTests.cs`
(`[Collection("LiveNetwork")]`), the same real-bot-over-loopback-TCP harness
the P33 sector test uses, on `XXV_simple.igc` (Ceto 1053 / Farout 1056,
sides Alpha/Bravo). Five real `FltBot`s; leadership established in the spawner
via P183 `Side.LeaderShipId` (first Alpha → Alpha leader, first Bravo → Bravo
leader); wings assigned by the bots over the **real `SET_WINGID` wire path**.

Layout (role resolution order-independent, keyed off the authoritative
`Server.Ships` table and the spawner's captured ship ids):

    ship 8098  Alpha Ceto    ALPHA LEADER, wing 1
    ship 8099  Bravo Ceto    BRAVO LEADER
    ship 8100  Alpha Farout  (sender)        wing 2
    ship 8101  Alpha Farout                  wing 1
    ship 8102  Bravo Farout

Server log (verbatim from the passing run):

    [setwingid] ship 8101: wingID -> 1   [setwingid] ship 8100: wingID -> 2   [setwingid] ship 8098: wingID -> 1
    [chat] ship 8100 side 0 'LEADERS CALL' target 1 -> 2 peer(s)
    [chat] ship 8098 side 0 'LEADERS SELF' target 1 -> 2 peer(s)
    [chat] ship 8100 side 0 'WING SPEAK' target 8 -> 2 peer(s)

Asserted outcomes:

- **Leaders, sender = non-leader (8100):** reached both leaders 8098 (Alpha)
  and 8099 (Bravo) — the sender's own side's leader *and* the opposing side's
  leader (cross-team reach) — and **no** non-leader (sender 8100, other Alpha
  8101, other Bravo 8102). RESULT PASS.
- **Leaders, sender = the Alpha leader (8098):** reached 8098 (self-relay, the
  native every-side sweep) and 8099 (Bravo leader), no non-leader. RESULT PASS.
- **Wing (target wing 1) from 8100 (wing 2):** reached 8098 (Alpha, wing 1,
  **different sector** Ceto — proves wing routing is not sector-restricted)
  and 8101 (Alpha, wing 1, same sector), and **neither** the sender 8100 (same
  side, wing 2 — different wing excluded) **nor** the Braves 8099/8102
  (opposite side excluded). RESULT PASS.

The whole-solution build is clean (**0 warnings, 0 errors**), and the new test
passes live. The full test suite is run twice with only the pre-existing
documented live-wire timing flakes expected.

## Files changed

- `src/Allegiance.Server/Server.Lobby.cs` — `SelectChatTargets` switch gains
  the `Leaders` and `Wing` cases; XML doc for the method updated.
- `src/Allegiance.Bot/Bot.cs` — `SendChat` XML doc updated (Leaders/Wing now
  wired).
- `tests/Allegiance.Core.Tests/LiveChatLeadersWingWireTests.cs` — new live
  multi-peer proof for both targets (plus the real `SET_WINGID` wing assignment).
- `README.md` — the chat-targets sections updated to reflect `Leaders`/`Wing`
  landing and the real `WingId` mechanism.
- This report.
