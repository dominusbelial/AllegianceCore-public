# P25 — `FM_CS_CHATMESSAGE(23)` wire codec + live relay (Everyone / Team)

Chat was on the README "not yet done" list and is small: the wire message is a
variable-length `Message` string plus a fixed `ChatData` struct plus an
`oidTarget`/`otTarget` pair — no physics, no collision, no economy. It is
bidirectional (`CS`): a client sends it, the server relays it back out.

This slice wires **exactly two** of the fourteen `AGCChatTarget` modes, in this
codebase's established "explicit, documented simplification" style (cf. P21's
friendly-fire-always-off, P23's no-`PartMask`-checking):

- **`Everyone` (0)** — broadcast to every connected peer.
- **`Team` (5)** — broadcast only to peers whose `Ship.SideId` matches the
  sender's (trivial now that P21 gave ships a real `SideId` — reused, not
  reinvented).

**Every other `ChatTarget` value is logged plainly and dropped** — never
broadcast as a leniency fallback (that would be a real behavior bug disguised
as leniency) and never thrown (an unimplemented target from a client must not
crash the server). That matches the README server-does-section's "any inbound
packet that isn't well-formed is reported and ignored."

---

## 1. The wire layout as ported

Reference source (read-only): `/home/domingo/Development/Allegiance/src/`.

```c
// src/Inc/Messages.h:204-214 — the fixed chat payload
struct ChatData {
  ShipID        sidSender;        // = ObjectID = short            (igc.h:477,480)
  ChatTarget    chatTarget;       // = int (C++ enum, default int)  (igc.h:2729)
  ObjectID      oidRecipient;     // = short                        (igc.h:477)
  SoundID       voiceOver;        // = short                        (igc.h:453)
  CommandID     commandID;        // = char (signed)                (igc.h:897)
  bool          bObjectModel;     // = bool (1 byte)
};
// src/Inc/Messages.h:216-223
DEFINE_FEDMSG(CS, CHATMESSAGE, 23)
  FM_VAR_ITEM(Message);           // ib/cb pair                     (messagecore.h:693)
  ChatData      cd;               // above
  ObjectID      oidTarget;        // = short
  ObjectType    otTarget;         // = short                        (igc.h:115)
END_FEDMSG
```

Under the MSVC-default packing rule recorded in `FEDMSG.md` §2 (member aligned
to its own size, struct aligned to its largest member; `ib`/`cb` are `unsigned
short` pairs placed first), the compiler-packed fixed size — asserted by the C++
server as `ibMessage == sizeof(FMD_CS_CHATMESSAGE)` at **`FedSrv.CPP:4922`** —
computes to **28 bytes**:

```
offset  0: ushort cbmsg
offset  2: ushort fmid = 23
offset  4: ushort ibMessage
offset  6: ushort cbMessage        (length includes the trailing NUL)
offset  8: ChatData cd:
 offset  8:   short  sidSender
 offset 10:   2 pad bytes                       (align int chatTarget to 4)
 offset 12:   int    chatTarget
 offset 16:   short  oidRecipient
 offset 18:   short  voiceOver
 offset 20:   sbyte  commandID
 offset 21:   bool   bObjectModel
           = 14 → padded to 16 (4-align)        (next member at 24)
offset 24: short  oidTarget
offset 26: short  otTarget
= 28 bytes fixed, 4-aligned
blob: Message (CB_ZTS) at offset 28 → cbmsg = 28 + blobLen
```

`Message` is a CB_ZTS string: trailing spaces trimmed, one trailing NUL, `cb`
includes the NUL — exactly what the client's `FM_VAR_PARM(pszText, CB_ZTS)`
produces (**`clintlib.cpp:2444-2445`**). The C++ client discards a chat whose
`sidSender` is its own ship (**`appmsg.cpp:2644`**); the port reproduces the
relay, not that client-side discard.

Note the width of `chatTarget` on the wire: a plain C++ `enum` is `int` (4
bytes). The sim-layer `Sim.ChatTarget` enum in `IgcEnums.cs:12` is declared
`short` (a deliberate sim-abstraction narrowing for the `IIgcSite` signatures),
which is **not** the wire width — the codec therefore keeps the field as a raw
`int` (`ChatMessage.ChatTarget`).

## 2. What was built (file:line in `AllegianceCore`)

| Piece | Location |
|---|---|
| Codec `FM_CS_CHATMESSAGE(23)`, FixedSize=28, round-trip + CB_ZTS blob | `src/Allegiance.Core/Wire/ChatMessage.cs` |
| Server relay case in `ProcessDatagram`'s `fmid` chain | `src/Allegiance.Server/Server.cs` (the `else if (fmid == ChatMessage.Fmid)` block) |
| Target selector: Everyone = all `_peerShips`; Team = matching `SideId`; else log+drop | `src/Allegiance.Server/Server.cs::SelectChatTargets` |
| `Bot.SendChat(...)` + `Bot.Chats` + `Bot.ResetChatReceived()` + decode case | `src/Allegiance.Bot/Bot.cs` |
| Wire codec tests (round-trip + byte-exact buffer) | `tests/Allegiance.Core.Tests/Wire/ChatMessageWireTests.cs` |
| Live-socket proof (`[Collection("LiveNetwork")]`) | `tests/Allegiance.Core.Tests/LiveChatWireTests.cs` |
| Standalone demo tool (registered in `AllegianceCore.slnx`) | `tools/chatdemo/Program.cs` |

## 3. Verbatim proof output

### 3a. `tools/chatdemo` — three real bots over loopback TCP

Three bots seat round-robin onto two sides: ships 8096 and 8098 on side 0
(Alpha), ship 8097 on side 1 (Bravo). Sender ship 8096 sends three real
`CHATMESSAGE(23)`s. The server relay lines and each bot's verbatim reception:

```
[demo] sender ship 8096
[chat] 1 ship 8096 side 0 'ALL HELLO' target 0 -> 3 peer(s)      # Everyone
[chat] 1 ship 8096 side 0 'TEAM HI'  target 5 -> 2 peer(s)       # Team
[chat] target 8 not implemented in this slice — message from 1 dropped
[chat] 1 ship 8096 side 0 'WING ONLY' target 8 -> 0 peer(s)      # unimplemented → dropped
[demo] === CHAT RECEPTION (verbatim, per real bot) ===
[demo] ship 8096 received: ALL HELLO | TEAM HI
[demo] ship 8097 received: ALL HELLO
[demo] ship 8098 received: ALL HELLO | TEAM HI
[demo] Everyone reached 3/3: True; Team reached 2/3 (one declined): True; Wing reached 0/3: True
[demo] RESULT PASS
```

Interpretation: `ALL HELLO` reached all three ships (Everyone); `TEAM HI`
reached ships 8096 and 8098 — the two on the sender's side 0 — and not 8097
(side 1, the opponent); `WING ONLY` reached nobody and was reported-and-dropped.

### 3b. `LiveChatWireTests` — the same scenario, as an automated `[Collection("LiveNetwork")]` test

```
[chat] 1 ship 8096 side 0 'ALL HELLO' target 0 -> 3 peer(s)
[chat] 1 ship 8096 side 0 'TEAM HI'  target 5 -> 2 peer(s)
LIVE-CHAT: sender ship 8096 side 0
LIVE-CHAT: bot ship 8096 side 0 everyone=[ALL HELLO] team=[TEAM HI]
LIVE-CHAT: bot ship 8097 side 1 everyone=[ALL HELLO] team=[]
LIVE-CHAT: bot ship 8098 side 0 everyone=[ALL HELLO] team=[TEAM HI]
[chat] target 8 not implemented in this slice — message from 1 dropped
[chat] 1 ship 8096 side 0 'WING ONLY' target 8 -> 0 peer(s)
LIVE-CHAT: Wing target dropped for all 3 — RESULT PASS
  Passed Allegiance.Core.Tests.LiveChatWireTests.Real_bots_exchange_chat_Everyone_and_Team_over_the_wire
```

Same result: Everyone reached all 3 (ALL HELLO on every bot incl. the side-1
opponent), Team reached exactly the two side-0 bots, Wing reached none.

## 4. Green

Full suite run three times after adding the `LiveNetwork`-tagged test
(P20 standard); each run green, 0 failed:

```
Passed!  - Failed: 0, Passed: 1527, Total: 1527  (run 1)
Passed!  - Failed: 0, Passed: 1527, Total: 1527  (run 2)
Passed!  - Failed: 0, Passed: 1527, Total: 1527  (run 3)
```

(1521 baseline before this task + 5 codec tests + 1 live test = 1527.)

## 5. Scope notes / discrepancies

- **`ChatTarget` width**: the C++ wire field is an `int` enum; the existing
  `Sim.ChatTarget` is `short`. The codec keeps the wire field as `int`
  (`ChatMessage.ChatTarget`) and the server switches on that raw int — trusted
  over the sim enum's narrower width, since the latter is not the wire format.
- **Team excludes vs includes the sender**: the C++ forwards `CHAT_TEAM` to the
  whole side group (`FedSrv.CPP:3524`), which includes the sender; the client
  discards its own echo (`appmsg.cpp:2644`). The port therefore relays Team to
  all matching peers (sender included) and lets the consumer filter — the
  faithful reproduction, uniform with `Everyone` = all `_peerShips`.
- No change to `Weapon.cs`, `Ship.cs` combat/damage, `KillShip.cs`,
  `Sector.cs`'s collision pass, or any existing test/tool except README/docs.
