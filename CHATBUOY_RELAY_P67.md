# CHATBUOY_RELAY_P67 — relay FM_CS_CHATBUOY(108)

Date: 2026-09-03 · Branch: `master` · Repo: `AllegianceCore` (server-side wire
relay only; no native `Allegiance` change, no Wopr/Windows VM involvement).

## What this does

Teaches `AllegianceCore`'s headless server to decode and relay
`FM_CS_CHATBUOY`(108) exactly the way it already relays `FM_CS_CHATMESSAGE`(23).
Previously any inbound 108 fell through to the catch-all
`[wire] from {from}: unsupported fmid 108 — ignored at headless milestone`,
silently dropping it.

That message is the one the real dispatch actually uses: a commander's
`SendChat(..., otTarget: buoy.GetObjectType(), oidTarget: buoy.GetObjectID(),
buoy, ...)` (`CommanderResearchAndExpand.cs:1615-1618`, triggered by the real
decision at `:1610`, `"Kicking loafing miner .Miner1@BOT to enemy cluster
Alderan."`) targets a buoy, and `BaseClient::SendChat` picks `CHATBUOY`(108)
whenever `otTarget == OT_buoy` (`clintlib/clintlib.cpp:2432-2444`). So the
dispatch order was reaching this server and being dropped — this task is the
server-side fix; the native receive side (`Miner.cs`'s own handler) is P68 and
still outstanding (see below).

## Where DataBuoyIGC's real layout was found

In the native source, straightforwardly: **`src/Igc/igc.h:1913-1922`** in the
sibling checkout at `/home/domingo/Development/Allegiance/`. It was not in a
buoy-specific header; it sits alongside every other `Data*IGC` struct in
`igc.h`. (An earlier quick search missed it only because the first member is a
`Vector` rather than the usual `signature`, so the search terms that found
siblings didn't match.)

```cpp
struct DataBuoyIGC                       // src/Igc/igc.h:1913-1922
{
    Vector     position;   // offset 0  — float x,y,z (src/zlib/vector.h:14-20)
    SectorID   clusterID;  // offset 12 — ObjectID = short (agcidl.idl:229, igc.h:487)
    BuoyType   type;       // offset 14 — char (igc.h:552)
    BuoyID     buoyID;     // offset 16 — ObjectID = short (igc.h:481); 1 pad byte at 15
    bool       visible;    // offset 18 — 1 byte; 1 pad byte at 19 → struct aligns to 4
};
```

Under MSVC default packing (FEDMSG.md §2: each member aligned to its own size,
struct aligned to its largest member) this is **20 bytes**: 12 (Vector) + 2
(clusterID) + 1 (type) + 1 pad + 2 (buoyID) + 1 (visible) + 1 pad. It's the
payload behind `FM_CS_CHATBUOY`'s `DataBuoyIGC db` member
(`src/Inc/Messages.h:716-722`).

**Lifecycle finding (confirmed, not assumed):** buoys have **no separate
server-side object lifecycle** — they are pure client-side ephemeral target
markers. They are created client-side via `ClientConnection.CreateBuoy(...)`
(`Wopr/.../ClientConnection.h`; used e.g. in `WinTrek/console.cpp`) and carried
inside this chat message as its own export payload. The native client builds
the `db` block by `pmodelTarget->Export(&(pfmChatBuoy->db))`
(`clintlib.cpp:2468`), and `IbouyIGC::Export` writes exactly the five fields
above in order (`src/Igc/buoyigc.cpp:87-98`). Unlike ships/asteroids/warps/
stations, this server never exports a buoy as a synced object; it only ever
sees the 20-byte block relayed verbatim inside CHATBUOY. Hence the codec treats
it as a message-payload fragment, with no object-store side effects.

## Wire shape

```text
DEFINE_FEDMSG(CS, CHATBUOY, 108)          // Messages.h:716-722
  FM_VAR_ITEM(Message);
  ChatData      cd;                       // identical to CHATMESSAGE's (Messages.h:204-214)
  DataBuoyIGC   db;                       // 20 B, replaces CHATMESSAGE's oidTarget/otTarget
END_FEDMSG
```

```text
offset 0: ushort cbmsg
offset 2: ushort fmid = 108
offset 4: ushort ibMessage
offset 6: ushort cbMessage
offset 8: ChatData cd        (16 B — identical block to CHATMESSAGE)
offset 24: DataBuoyIGC db    (20 B — position 12, clusterID 2, type 1, buoyID 2, visible 1)
= 44 B fixed
blob: Message (CB_ZTS) at offset 44 → cbmsg = 44 + blobLen
```

So versus CHATMESSAGE(23) the only difference is that the trailing `oidTarget`
(2) + `otTarget` (2) pair is replaced by the whole embedded `DataBuoyIGC db`
(20 B): fixed size 28 → 44.

## Implementation

**Reuse over duplication** (the codebase's stated preference, and this shape
genuinely overlaps — both messages embed the identical `ChatData cd`):

- `src/Allegiance.Core/Wire/ChatData.cs` — new shared `ChatData` struct (16 B,
  offsets documented). `ChatMessage` now embeds `Cd` and exposes the six chat
  fields as forwarding accessors onto it (its public API is unchanged, so
  `Bot.SendChat`, `tools/chatdemo` and the wire tests compiled untouched), and
  `ChatBuoy` embeds the same `Cd`.
- `src/Allegiance.Core/Wire/ZtsBlob.cs` — new shared CB_ZTS blob helper,
  factored out of `ChatMessage`'s former private helpers; both codecs use it
  for the trailing `Message` blob.
- `src/Allegiance.Core/Data/DataBuoyIGC.cs` — new 20-byte payload record
  (decode/encode), grounded in `igc.h:1913-1922` as above.
- `src/Allegiance.Core/Wire/ChatBuoy.cs` — the 108 codec, structurally
  mirroring `ChatMessage.cs`, reusing `ChatData`, `ZtsBlob`, `DataBuoyIGC`.
- `src/Allegiance.Core/Wire/IChatRoutingTarget.cs` — new minimal interface
  (`int ChatTarget`, `short OidRecipient`) implemented by both `ChatMessage`
  and `ChatBuoy`. `Server.SelectChatTargets` now takes it instead of a concrete
  `ChatMessage`, so the CHATBUOY handler calls the **exact same routing
  method** — no duplicated switch, no narrowed copy. `SelectChatTargets`
  itself is untouched (out of scope).

**Server wiring** (`src/Allegiance.Server/Server.cs`, new branch inserted after
the CHATMESSAGE branch): decodes, resolves the sender's
ship/SideId, calls the shared `SelectChatTargets`, logs
`[chatbuoy] {from} ship {id} side {side} '{msg}' target {ct} buoy {db.BuoyId}
-> N peer(s)` (mirroring the unconditional `[chat]` line at `Server.cs:1277`),
then relays the **original raw bytes** via `FedPacket.Pack(new[] { m })` to each
selected peer — the same verbatim-forwarding pattern the existing ChatMessage
relay (`Server.cs:1281`) and CS_PING echo (`Server.cs:1352`) use. No
reinterpretation; the server only routes.

**Bot support** (`src/Allegiance.Bot/Bot.cs`): `SendChatBuoy(...)` +
receive-side `ChatBuoys` accumulation + a `ChatBuoy.Fmid` dispatch case, so a
loopback test bot can send and observe real 108 messages.

## Verification

- Baseline re-confirmed first: `dotnet test` → 2284 passed, 0 failed.
- New tests: `tests/.../Wire/ChatBuoyWireTests.cs` (codec round-trip +
  byte-exact 53-byte buffer hand-composed from the packing rule + wrong-fmid
  rejection) and `tests/.../LiveChatBuoyWireTests.cs` (loopback `Bot` test,
  modeled on `LiveChatWireTests`): three bots seat round-robin onto two sides;
  the same-side pair exchanges a real CHATBUOY — Individual addressed to the
  teammate is delivered byte-exactly to only that ship (asserted via
  `sent.Encode() == received.Encode()` since the server forwards verbatim),
  Team reaches both teammates and not the opponent, Everyone reaches all three,
  and cross-side Individual is dropped for everyone.
- Three consecutive clean runs, all **2290 passed / 0 failed** (2284 baseline
  + 6 new: 5 codec + 1 live). All 2290 pass on every run.

## Outstanding (P68) before the live payoff

P67 makes the server **relay** CHATBUOY to a docked miner, but observing a real
miner receiving and acting on a dispatch order also requires the native
receive side — `Miner.cs`'s own handler for fmid 108 — which is a **separate,
still-open task (P68)** and deliberately out of scope here (no native repo
changes in this task, per the mandate). Until P68 lands, the relayed 108 has
no consuming handler, so no end-to-end dispatch is observable yet. This task
is complete at the server relay layer.

## Files

- Added: `src/Allegiance.Core/Wire/ChatBuoy.cs`, `ChatData.cs`,
  `ZtsBlob.cs`, `IChatRoutingTarget.cs`, `src/Allegiance.Core/Data/DataBuoyIGC.cs`
- Modified: `src/Allegiance.Core/Wire/ChatMessage.cs` (embed `ChatData`, share
  `ZtsBlob`, keep API), `src/Allegiance.Server/Server.cs` (108 branch +
  `SelectChatTargets` signature widened), `src/Allegiance.Bot/Bot.cs`
  (SendChatBuoy + receive case)
- Tests: `tests/.../Wire/ChatBuoyWireTests.cs`,
  `tests/.../LiveChatBuoyWireTests.cs`
