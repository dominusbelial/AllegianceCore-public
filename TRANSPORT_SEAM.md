# Transport Seam Analysis — FEDMSG over DirectPlay 8 → pluggable transport + TCP

Analysis only. No code produced. Reference: `Allegiance/src/_Utility/Messages.cpp`
(2,172 lines) and `Allegiance/src/Inc/messagecore.h` (743 lines). WOPR surface:
`Allegiance/src/Wopr/AllegianceInterop/`. C# server: `AllegianceCore/`.

Scope note on DirectPlay reach: 38 lines in Messages.cpp mention `DPID`/`dpnid`
directly, 126 mention an `IDirectPlay8*` / `DPN_*` / `DPlay` token (overlapping),
plus 6 more in the CFMGroup/CFMConnection friends and the header. Consistent
with the "~128 references, spread everywhere, no single seam" measurement. The
seam does not sit at one call site — it sits at the **member/concurrency level**:
everything DirectPlay does is funneled through five chokepoints (init, sys-msg
handler, receive pump, send, shutdown) plus three identity primitives. Detailed
below.

---

## 1. Inventory — every DirectPlay-touching `FedMessaging` member, grouped by concern

Legend for `file:line`: `Messages.cpp` unless prefixed `h:` (= messagecore.h).

### 1.1 Session lifecycle (host / join / connect / shutdown)

| Member | What DirectPlay gives it | file:line |
|---|---|---|
| `InitDPlayServer()` | COM-creates `IDirectPlay8Server` (via `CLSID_DirectPlay8Server`), registers the static callback `DPlayMsgHandler` | 1293–1295, 1308 |
| `InitDPlayClient()` | COM-creates `IDirectPlay8Client`, registers same callback | 1247–1249, 1261 |
| `HostSession(guid, fKeepAlive, hEventServer, fProtocol, dwPort)` | Builds `DPN_APPLICATION_DESC`, creates + configures the local address object, calls `m_pDirectPlayServer->Host(...)` which assigns host player + instance GUID | 1365, 1370–1372, 1383, 1392, 1400–1405, 1409 |
| `JoinSession(guid, szServer, szName, dwPort)` | `InitDPlayClient` then `EnumHostsInternal` to discover the instance; blocks polling `m_guidInstance`; forwards to `JoinSessionInstance`; `CancelAsyncOperation(DPNCANCEL_ENUM)` on failure | 1496, 1500, 1507–1516, 1522, 1528 |
| `JoinSessionInstance(guidApp, guidInst, addr, device, name)` | `SetClientInfo` to publish name; synchronous `Connect(...)`; afterwards fabricates the local "me" and "server" connections with hard-coded dpids | 1576, 1582–1589, 1603, 1606 |
| `JoinSessionInstance(guidInst, name)` overload | Thin forwarding wrapper (no DirectPlay of its own) | 1613–1617 |
| `Shutdown()` | `Close(0)` + `Release()` on both interfaces; wipes lists/maps/queue/guids | 1437–1448, 1465–1474 |
| `Connect(szAddress)` | **Dead** — `assert(0)` at 1140, body commented out | 1140 |
| `ConnectToDPAddress(pvAddress)` | **Dead** — `assert(0)` at 1114 | 1114 |
| `KillSvr()` | Launches `KillSvr.exe`, waits on it — process/daemon management tied to the DirectPlay `dplaysvr` service | 1184–1206 |
| `GetListeningPort(dwPort)` | `GetLocalHostAddresses` + read `DPNA_KEY_PORT` component | 1706, 1714 |

Notes:
- `HostSession` unconditionally pins the service provider to TCP/IP:
  `pDP8AddressLocal->SetSP(&CLSID_DP8SP_TCPIP)` (1383) and the listen port
  component (1392). So DirectPlay's only in-play SP today is **TCP**. The
  transport swap does not lose any alternate-SP capability we actually use.
- `Connect` and `ConnectToDPAddress` are dead scaffolding from the old
  DirectPlay 4 days; they contribute nothing to the active surface.

### 1.2 Sending

| Member | What DirectPlay gives it | file:line |
|---|---|---|
| `GenericSend(precip, pv, cb, fmg)` | Chooses `DPNSEND_GUARANTEED\|PRIORITY_HIGH\|COALESCE` vs `DPNSEND_NOCOMPLETE\|NONSEQUENTIAL` flags, wraps payload in `DPN_BUFFER_DESC`, gets a `DPNHANDLE`, then client `Send()` **or** server `SendTo(precip->GetID(), ...)` (the `GetID()` here is the DPID / group DPID) | 513–519, 540–543, 547–553 |
| `SendMessages(precip, fmg, fmf)` | Batches the outgoing packet, then calls `GenericSend` | 631 |
| `SendToDefault(fmf)` | Wrapper resolving the default recipient/guarantee to `SendMessages` | 584–587 |

### 1.3 Receiving

| Member | What DirectPlay gives it | file:line |
|---|---|---|
| `DPlayMsgHandler(pvCtx, id, buf)` (static) | The DirectPlay callback; forwards to `MsgHandler` | 245–250 |
| `MsgHandler(id, buf)` | Switch on `DPN_MSGID_*`; deep-copies DirectPlay structs into a serialized `m_msgList` (locked by `m_csMsgList`); handles `ENUM_HOSTS_RESPONSE` inline | 890–966 |
| `ReceiveMessages()` | Pulls one `DPlayMsg` off the queue; non-RECEIVE → `OnSysMessage`; RECEIVE → resolve sender by `dpnidSender`, verify CRC, walk `FEDMESSAGE`s, `ReturnBuffer` the DirectPlay buffer | 1000, 1006–1012, 1027–1064, 1080–1090 |
| `PfmGetNext(pfm)` | Bounds iteration by the receive packet size | 451–471 |

### 1.4 System/status messages

| Member | What DirectPlay gives it | file:line |
|---|---|---|
| `OnSysMessage(msg)` | Dispatches DirectPlay system events: `CREATE_PLAYER` → `GetClientInfo` twice to learn the player name; `DESTROY_PLAYER`; `SEND_COMPLETE` → resolves the target from `m_handleMap`; group membership notifications; `TERMINATE_SESSION` | 700–774, 779–804, 805–846, 847–872, 873–876 |
| `GetSendQueue(pcMsgs, pcBytes)` | `GetSendQueueInfo` on the client object | 2140 |
| `GetConnectionSendQueue(pcnxn, ...)` | `GetSendQueueInfo` per player on the server object | 2158 |
| `GetReceiveQueue(...)` | DirectPlay 8 has no receive queue — returns 0, no DirectPlay call | 2147–2153 |
| `GetConnectionInfo(pcnxn, ...)` | `GetConnectionInfo` on the server object | 2169 |
| `GetLinkDetails(...)` | The whole body is commented out; returns hard zeros — **no live DirectPlay call** | 1970–2001 |
| `SetSessionDetails(szDetails)` | `GetApplicationDesc` / `SetApplicationDesc` to change the advertised session-name string | 1921, 1931, 1937 |
| `GetCountConnections()` | `GetApplicationDesc` → `dwCurrentPlayers` | 1949, 1959, 1961 |
| `GetIPAddress(cnxn, out)` | `GetClientAddress` (server) / `GetServerAddress` (client), then `DPNA_KEY_HOSTNAME` component | 1663, 1669, 1678 |

### 1.5 Enumeration / discovery

| Member | What DirectPlay gives it | file:line |
|---|---|---|
| `EnumSessions(guid, szServer)` | `InitDPlayClient` + delegates to `EnumHostsInternal`; sets `m_fSessionCallback` so a found host raises `OnSessionFound` | 1729, 1735, 1737 |
| `EnumHostsInternal(guid, szServer, dwPort)` | `InitDPlayClient`, builds host/local `IDirectPlay8Address` objects (COM + `SetSP(CLSID_DP8SP_TCPIP)` + port/hostname components), sets the `g_guidApplication` global, calls `EnumHosts` (sync for private GUID, async-with-infinite-retries otherwise) | 1772–1804, 1807, 1819–1829, 1840, 1846–1849, 1853–1856 |
| `EnumHostsCallback(resp)` | Reads `DPNMSG_ENUM_HOSTS_RESPONSE`; keeps host+device addresses; records `guidInstance`; fires `OnSessionFound` | 213–233 |

### 1.6 Connection identity (`DPID`/`dpnid`) — the private/public seam

| Member | What DirectPlay gives it | file:line |
|---|---|---|
| `GetConnectionFromDpid(dpid)` | Maps a DirectPlay player id to a `CFMConnection` | 1620–1639 |
| `GetGroupFromDpid(dpid)` | Maps a DirectPlay group id to a `CFMGroup` | 1642–1649 |
| `CreateConnection(name, dpid)` | Makes a `CFMConnection` from a DirectPlay `dpnidPlayer`, inserts into `m_cnxnsMap` | 1899–1910 |
| `CFMConnection::Delete(pfm)` | Calls `GetDPlayServer()->DestroyClient(GetDPID())` | 2022 |
| `CFMGroup::AddConnection(pfm, cnxn)` | Registers membership with `AddPlayerToGroup(dp, cp)` so `SendTo(groupDPID)` fans out | 2035 |
| `CFMGroup::DeleteConnection(pfm, cnxn)` | `RemovePlayerFromGroup(dp, cp)` | 2043 |
| `CFMGroup::CFMGroup(pfm, name)` | `CreateGroup` returns a DirectPlay-owned group DPID; the assigned `dpnidGroup` is written back at MsgHandler | 2055, h:916–917 |
| `CFMGroup::Delete(pfm)` | `DestroyGroup(dp)` | 2066 |

The decisive observation for §3: **`DPID` is `typedef DWORD` (messagecore.h:23)** and
it is consumed everywhere upstream as an opaque integer key (`GetID()`,
`GetConnectionFromId(id)`, the two maps). Only the identity of *who assigns it*
(and how) is DirectPlay-specific; the *kind* of thing it is (a DWORD key) is not.

Counts backing the sections above:
- Best-effort (`FM_NOT_GUARANTEED`) send sites in the whole tree: **2** (§3.2).
- Multicast sends via `SendTo(<group>)`: all server-side; every `GenericSend`
  routes through one call (§3.3).
- Functions with **no** DirectPlay touch at all (pure message layer — reusable
  unchanged): `PFedMsgCreate`, `PfmGetNext`'s framing logic, `QueueExistingMsg`,
  `BuffOut/BuffIn/PacketSize/CbFreeSpaceInOutbox` helpers, `CheckOdometer`,
  `Set/GetDefaultRecipient`, `PurgeOutBox`, `VerifyMessage` (§5).

---

## 2. The minimal `ITransport`

 The honest seam is drawn by asking: *what does FedMessaging's public API need the
 transport to give it?* It does not need "a DirectPlay". It needs:

 1. a way to stand up / tear down a session (host & join),
 2. a stable **integer connection id** per remote peer in both directions,
 3. a send primitive addressed by that id, with a reliable/unreliable bit,
 4. a receive + peer-lifecycle event channel,
 5. a handful of telemetry/introspection calls (queue depth, latency, ip).

 Everything else in `Messages.cpp` (frame composition via `PFedMsgCreate`,
 the non-standard CRC appended in `GenericSend` 529–538, packet walking, the
 `OnSysMessage` state machine, the connection/group maps) sits **above** the
 transport and is transport-agnostic as long as the id stays a `DWORD`.

 Two deliberate simplifications that keep the interface small:
 - **Multicast is left OUT of the transport.** `Send()` is unicast; group fan-out
 is a loop over the group's member ids in the message layer. This makes the TCP
 impl trivially correct and keeps `ITransport` at ~8 methods. (Contrast: baking
 multicast into the interface forces every transport to re-implement group
 membership.) Trade-off: server-side broadcast cost scales O(members) instead of
 O(1) DirectPlay multicast — acceptable (see §3.3).
 - **Peer identity is assigned by the transport** as a plain `DPID` (which stays
 `DWORD`, messagecore.h:23). No `CFM*` objects leak into the transport — it
 deals only in ids and bytes, so the transport is reusable/compilable on the
 C#-adjacent layer without pulling in the whole IGC/CLI stack.

 Proposed interface (concrete signatures; adapts the existing DirectPlay callback
 handling, so `OnSysMessage`/`ReceiveMessages` need the least churn):

 ```cpp
 // Events the transport raises, driven onto the SAME serialized queue that
 // FedMessaging already uses (m_csMsgList). Mirrors the DPN_MSGID_* that
 // MsgHandler/OnSysMessage consume today, renamed away from DirectPlay.
 class ITransportSite {
 public:
 virtual void OnPeerJoined  (DPID id, const char* name) = 0; // CREATE_PLAYER + GetClientInfo
 virtual void OnPeerLeft    (DPID id)                      = 0; // DESTROY_PLAYER
 virtual void OnGroupChanged(DPID group, DPID peer, bool fAdded) = 0; // ADD/REMOVE_PLAYER_TO_FROM_GROUP
 virtual void OnSessionLost ()                             = 0; // TERMINATE_SESSION / host death
 virtual void OnReceive     (DPID from, const BYTE* pData, DWORD cb) = 0; // RECEIVE (framed datagram)
 };

 class ITransport {
 public:
 // ---- lifecycle (replaces HostSession/JoinSession/InitDPlay* internals) ----
 virtual HRESULT Host (const GUID& appGuid, DWORD port) = 0;
 virtual HRESULT Join (const char* address, DWORD port,
                       const char* playerName, DPID& outSelf, DPID& outServer) = 0;
 virtual HRESULT Shutdown() = 0;
 virtual bool    IsConnected() const = 0;

 // ---- identity ----
 // Server side: transport calls site->OnPeerJoined after allocating the id;
 // FedMessaging never invents ids itself (today the client side fakes 0/1:
 // Messages.cpp:1603,1606 — the transport absorbs that fiction).
 virtual DPID  GetLocalPlayerId() const = 0;

 // ---- send ----
 // fGuaranteed true == reliable. Target is a peer id OR a group id
 // (message layer pre-expands groups to members before calling, or the TCP
 // transport expands; see §3.3 for the chosen side).
 virtual HRESULT Send(DPID target, const void* pv, CB cb, bool fGuaranteed) = 0;

 // ---- telemetry (thin stubs if not meaningful) ----
 virtual HRESULT GetSendQueueInfo(DWORD* pcMsgs, DWORD* pcBytes) = 0;
 };

 // Dispatch: a static trampoline analogous to DPlayMsgHandler (Messages.cpp:245)
 // funnels socket-thread events into site->On* which enqueue DPlayMsg structs;
 // ReceiveMessages() drains them unchanged.
 ```

 Why this is minimal and sufficient:

 - Every current DirectPlay member in §1 maps to a `ITransport` call or a
 `ITransportSite` event. The FIFO `m_msgList` + `m_csMsgList` serializer
 (MsgHandler 961–963 / ReceiveMessages 984–991) is DirectPlay-independent and
 survives as-is; only the *producers* change (socket thread instead of DPlay
 COM callback).
 - `GetSendQueueInfo`, `GetListenPort`, `GetIPAddress`, `GetConnectionInfo` are
 consumers of transport-specific knowledge and either forward to `ITransport`
 or degrade to the same 0/FALSE stubs DirectPlay already provokes
 (`GetLinkDetails` returns all-zeros today, Messages.cpp:1996–2000; the
 non-queue introspection is already effectively fake).

 Things deliberately *not* in the interface (kept in the message layer): frame
 composition, CRC, packet walking, recipient/group bookkeeping, default-recipient
 logic. They are shared with the wire codec already in `AllegianceCore` and do
 not belong to a transport.

---

## 3. What DirectPlay semantics leak upward?

 ### 3.1 `DPID`/`dpnid` — connection identity leaks *moderately*, but only as an opaque DWORD key

 `DPID` is `typedef DWORD` (messagecore.h:23) and `CFMRecipient` stores it as
 `m_dpid` (messagecore.h:112), exposing it publicly as `GetID()` (messagecore.h:85).
 Every `CFMConnection` and every `CFMGroup` carries one, and two maps are keyed
 by it: `m_cnxnsMap` (DWORD→CFMConnection*, messagecore.h:303, 605) and
 `m_groupMap` (DWORD→CFMGroup*, messagecore.h:304, 606). Routing by id is the
 only use:

 - Incoming: `ReceiveMessages` resolves the sender from the datagram id
 (`p_dpMsg->dpnidSender`) via `GetConnectionFromDpid` (Messages.cpp:1006,
 1620–1639); unknown sender → drop (1008).
 - Outgoing: server-side `GenericSend` targets `precip->GetID()` in
 `m_pDirectPlayServer->SendTo(...)` (553); client-side `Send()` ignores it.
 - Completion/NAK: `SEND_COMPLETE` maps `DPNHANDLE`→`DPID` through `m_handleMap`
 (611, 817–823) to attribute a failed send to a recipient → `OnMessageNAK`.

 So within `FedMessaging` the id is genuinely structural (identity + addressing +
 attribution). **The leak stops at the boundary**: the values are DirectPlay
 assigned, but every consumer treats the id as an opaque integer. Beyond
 `FedMessaging`, `GetID()`/`GetConnectionFromId(DWORD)` (messagecore.h:423–426)
 pass it onward as a plain `DWORD` — e.g. the server maps game ships to
 connections by this integer. Nothing reconstructs DPIDs from meaning; nobody
 depends on the specific number sequence except three places that hard-code tiny
 values, all interior to FedMessaging:

 - Client "me" connection is invented with dpnid **0** (Messages.cpp:1603).
 - Client "server" connection is invented with dpnid **1** (Messages.cpp:1606).
 - Server recognizes its own host-player marker via
 `lp->pvPlayerContext == (void*)1` (Messages.cpp:706) and the Everyone group is
 assigned its dpid by `CreateGroup` (h:916–917).

 Verdict: **low-risk.** Replace "DirectPlay assigns it" with "transport assigns it"
 (a `FedMessaging`-internal counter on the server; 0=me, 1=server, on the client
 either as today or moved into the transport). No upstream caller cares how the
 integer was minted, only that it is stable and unique per peer. The one subtlety:
 the transport must assign the id *before* raising `OnPeerJoined` so that
 `CreateConnection` (1899–1910) and the site's `OnNewConnection` see a consistent
 id, exactly as `CREATE_PLAYER` delivers `dpnidPlayer` first (700–774).

 ### 3.2 Guaranteed vs best-effort — quantify it: **2 call sites**, both conditional, neither on the bot path

 `FMGuaranteed` (messagecore.h:252–256) flows into `GenericSend` which sets the
 DirectPlay reliability/flags (Messages.cpp:513–519). Best-effort (`FM_NOT_GUARANTEED`)
 is requested in exactly **two** places in the whole tree, and both are guarded by
 a runtime bool (so they are usually reliable anyway):

 | Site | What it is | When it picks best-effort | file:line |
 |---|---|---|---|
 | `Pig.cpp:1469` | Upload/download a file (resource/binary transfer) | `bGuaranteed` false | src/Pigs/PigSrv/Pig.cpp:1469 |
 | `clintlib.cpp:2081` | `FM_CS_PING` — the client/server ping | `bGuaranteed = m_cUnansweredPings > g_cPingsTimeout` (mostly reliable; best-effort only at the start of a lag spike) | src/clintlib/clintlib.cpp:2081 |

 Every other send resolves to the `FM_GUARANTEED` default
 (`m_fmGuaranteedDefault = FM_GUARANTEED`, ctor Messages.cpp:263) — the entire
 game-state, position, wing-set, chat, logon traffic is reliable.

 If TCP makes everything reliable, the concrete losses are:
 1. **Ping loss statistics.** The ping was sent best-effort specifically so
  unanswered-ping counting reflects *packet loss*, driving
  `OnSessionLost("Pings aren't coming back.")` and server-lag heuristics
  (clintlib.cpp:2065–2087). On a reliable channel the counter measures only
  reachability/timeout, not loss — a behavior change, but only for the lag/DC
  detector, not for message correctness.
 2. **Large file transfers** lose the option to fire-and-forget (Pig.cpp:1469);
  they'd pay reliable-path overhead. Cosmetic.

 For the WOPR **bot path** this is moot: the bot's only sends are
 `GenericSend(..., FM_GUARANTEED)` (ClientConnection.cpp:237) and
 `BEGIN_PFM_CREATE(...)` (default). **Making all sends reliable changes nothing
 for a bot.**

 ### 3.3 Groups / multicast — the one real semantic hole, but only on the server

 Yes, FEDMSG leans on DirectPlay group/multicast sends:

 - `HostSession` creates the **Everyone** group (Messages.cpp:1416); every
 new connection auto-joins it (`CreateConnection` → `Everyone()->AddConnection`,
 1906).
 - `CFMGroup` is a thin wrapper over DirectPlay groups: `CreateGroup` (2055),
 `AddPlayerToGroup` (2035), `RemovePlayerFromGroup` (2043), `DestroyGroup`
 (2066). `CFMGroup::GetID()` is a group DPID.
 - Server broadcasts go through `GenericSend` → `SendTo(precip->GetID())` where
 the id is a **group** id (553). That is DirectPlay multicast: one call, O(1),
 delivered to every member.

 TCP has no multicast. Two viable replacements:

 - **(a) Fan-out in the message layer:** `GenericSend` detects a group id and
 loops `transport->Send(memberId, ...)` for each member. Keeps `ITransport`
 unicast-only (my §2 recommendation). Cost: server O(members) send syscalls
 per broadcast; for typical Allegiance squad/team sizes this is fine, but
 "Everyone" (all ~60–150 players) plus high-frequency state could be a throughput
 factor worth measuring.
 - **(b) Fan-out in the TCP transport:** transport keeps a group→member table
 and writes each member socket once. Same O(members) on the wire, but avoids
 N socket writes from the caller and centralises the copy loop on one thread.

 Either way the client (and therefore the **bot**) sees identical behaviour — it
 never sends to a group; it only *receives* group-addressed broadcasts, which a
 unicast-per-member re-broadcast satisfies transparently. The multicast hole is
 entirely a server-host concern, and since the eventual host is the C# server
 implementing the TCP transport, the fan-out belongs there.

 Also worth noting: today's "groups" on the client side are paper-thin — the
 client is a DirectPlay **client**, and DirectPlay client objects cannot create
 groups; the fake `m_pcnxnServer` (dpid 1) is not a real group at all. The only
 real groups exist server-side. So the seam introduces **no new group machinery**
 for the client path.

 ### 3.4 Enumeration / discovery and keepalive

 **Enumeration is on the connect path today, but droppable for TCP.**
 `JoinSession` (Messages.cpp:1490–1533) performs `EnumHostsInternal` (1500) and
 blocks polling `m_guidInstance` (1507–1516) purely to obtain the instance GUID
 and the host/device addresses before `JoinSessionInstance` (1522). A TCP transport
 given an explicit `address:port` can skip discovery entirely: connect, exchange a
 hello, read the instance GUID from the server announcement, and proceed. So
 `EnumHosts`/`EnumHostsCallback`/`EnumSessions` (the server-browser feature) are
 **droppable for a bot**, kept only for a future "game list" UX — they do not gate
 connecting to a known host.

 **Keepalive/dead-peer detection** is presently DirectPlay-internal (host drops
 players after timeouts, reflected in `m_cAbsentCount` roll-call counters on
 CFMConnection, messagecore.h:129–147, 193–202). TCP offers transport-level
 keepalive (SO_KEEPALIVE / idle probes) and, more usefully, an explicit heartbeat.
 For WOPR, `NativeClient::SendAndReceiveUpdate` already drives the loop that would
 send those heartbeats (NativeClient.cpp:99–146). Verdict: drop DirectPlay's
 keepalive; rely on TCP + the existing app-level ping. Not a bot blocker.

 Summary of leaks that force work: id assignment (easy), one best-effort nuance
 cosmetic, multicast fan-out (server-side, owned by the future C# host), and
 enumeration removal on connect. None of these block a loopback proof.

---

## 4. What does WOPR actually require? — the bot path is a *client-half* of FedMessaging

 WOPR reaches the wire exclusively through two C++/CLI assemblies:
 `NativeClient` (native C++ : `BaseClient` : `IFedMessagingSite`, with native
 `__event` hooks) and `ClientConnection` (the C++/CLI bridge that plumbed the
 native events out to managed delegates). None of them touches DirectPlay
 directly — they only use `FedMessaging`'s public API. The full call chain:

 ```
 ClientConnection::ConnectToLobby / ConnectToServer        (ClientConnection.cpp:85,155)
 └─ BaseClient::ConnectToLobby / ConnectToServer
      └─ m_fmLobby.JoinSession(...)                      (clintlib.cpp:1411)  [lobby]
         m_fm.JoinSession(...)                           (clintlib.cpp:1317,1320)
         m_fm.JoinSessionInstance(...)                   (clintlib.cpp:1312)
 ClientConnection::SendAndReceiveUpdate                    (ClientConnection.cpp:198)
 └─ NativeClient::SendAndReceiveUpdate                   (NativeClient.cpp:99–146)
      ├─ BaseClient::ReceiveMessages()  →  OnAppMessage → OnAppMessageEvent → managed
      └─ BaseClient::SendMessages()
 ClientConnection::SendMessage                             (ClientConnection.cpp:214–241)
 └─ fm->GenericSend(fm->GetServerConnection(), bytes, cb, FM_GUARANTEED)
      fm = GetFMLobby() or &m_fm                        (ClientConnection.cpp:221,225)
 ```

 What the **bot path** actually exercises (vs the full client):

 | Bot-needs-this | File:line | Used for |
 |---|---|---|
 | `m_fmLobby` and `m_fm` — **two** FedMessaging clients, serialized | clintlib.h:760–761 | Lobby handshake then game-server handshake |
 | `JoinSession` / `JoinSessionInstance` (client connect) | clintlib.cpp:1312–1320, 1411 | Both connects (Server: guid-known→Instance, else enum-Join; Lobby: JoinSession) |
 | `GetServerConnection()` — sends target dpid 1 | ClientConnection.cpp:237 | Every outbound message targets the server connection only |
 | `GenericSend(..., FM_GUARANTEED)` — unicast, reliable | ClientConnection.cpp:237 | Raw bytes for lobby and game FEDMSGs |
 | `ReceiveMessages()` + `OnAppMessage` | NativeClient.cpp:105, 149–186 | Drives BaseClient::HandleMsg → relays every FEDMESSAGE to managed |
 | `IsConnected()` | NativeClient.cpp:108, 127; ClientConnection.cpp:236 | Gates the update tick and sends |
 | `OnLogonLobbyAck` / `OnLogonAck` (site events) | NativeClient.cpp:47–55 | Logged/fired to managed (ClientConnection.cpp:256–264) |
 | `SendAndReceiveUpdate` loop calling `BaseClient::SendMessages`/`ReceiveMessages` | NativeClient.cpp:99–146 | One "frame" per tick (called externally by the bot loop) |

 Surface the bot **does not** exercise:

 - `HostSession`, `InitDPlayServer`, `KillSvr`, `GetListeningPort` — server-only.
 - `CFMGroup` creation — a DirectPlay client can't create groups anyway
 (see §3.3); the bot never sends to a group.
 - `GetIPAddress`, `GetLinkDetails`, `SetSessionDetails`, `GetCountConnections`,
 `GetConnectionSendQueue`, `GetSendQueue`, `GetConnectionInfo` — admin/server
 diagnostics; the bot path hits none of them.
 - `EnumSessions` / `OnSessionFound` — the server browser UX; not a bot feature.

 Two observations that materially simplify the TCP work:

 1. **Every bot send is a unicast, reliable send to the server connection.**
  `GenericSend(fm->GetServerConnection(), ..., FM_GUARANTEED)` (ClientConnection.cpp:237).
  So for the bot, §3.2's "make everything reliable" is a non-change and §3.3's
  multicast is irrelevant — the bot never multicasts. The TCP transport only has
  to do reliable unicast to the server.
 2. **The bot shares `BaseClient`'s two FM instances.** `ConnectToServer` chooses
  `JoinSessionInstance` (known guid) or `JoinSession` (enum-to-find)
  (clintlib.cpp:1312–1320). On TCP this collapses to "join address:port".
  Whether the client-side `m_pcnxnMe`=0 / `m_pcnxnServer`=1 fabrication
  (Messages.cpp:1603,1606) moves into the transport or stays is invisible to
  the bot — it only ever uses `GetServerConnection()`.

 Honest caveat on the "easier than the full client" framing: the bot is still a
 **full game client** (it runs the whole IGC/clintlib message-processing engine
 via `BaseClient::HandleMsg`), so the *wire messages* it handles are the same as a
 human client's. But the *transport surface* it touches is dramatically narrower —
 pure client-half of FedMessaging, all reliable, all unicast to the server. Any
 transport seam that keeps the FedMessaging public API intact will work for WOPR
 without touching `NativeClient`/`ClientConnection`/`clintlib` at all.

---

## 5. Sizing — how many call sites change, what stays, swap or rewrite?

 ### 5.1 Functions in Messages.cpp that must change (touch DirectPlay today)

 From the §1 inventory, the concrete edit list. Rough counts, all `Messages.cpp`
 unless noted:

 **Rewritten to go through `ITransport` (≈15):**
 `GenericSend` (505), `MsgHandler` (890), `ReceiveMessages` (974),
 `OnSysMessage` (686), `HostSession` (1341), `JoinSession` (1490),
 `JoinSessionInstance` ×2 (1548, 1613), `Shutdown` (1433), `InitDPlayClient`
 (1236), `InitDPlayServer` (1282), `EnumSessions` (1722), `EnumHostsInternal`
 (1740), `EnumHostsCallback` (193), `GetIPAddress` (1652), `GetListeningPort`
 (1694).

 **Friend/group methods rewritten (≈6):** `CFMConnection::Delete` (2015),
 `CFMGroup::AddConnection` (2032), `CFMGroup::DeleteConnection` (2039),
 `CFMGroup::CFMGroup` (2047), `CFMGroup::Delete` (2063), plus the group-dpid
 hand-back at `MsgHandler` (h:916–917).

 **Introspection re-pointed or stubbed (≈7):** `SetSessionDetails` (1915),
 `GetCountConnections` (1943), `GetSendQueue` (2137), `GetConnectionSendQueue`
 (2155), `GetReceiveQueue` (2147), `GetConnectionInfo` (2165), `GetLinkDetails`
 (1970). Most already degenerate to zeros/`S_FALSE` today (e.g. GetLinkDetails
 1996–2000, GetReceiveQueue 2150–2152), so several shrink to a `return` forward
 or disappear.

 **Deleted outright (≈4):** `KillSvr` (1175) and `Connect`/`ConnectToDPAddress`
 (1138, 1112 — already `assert(0)` dead code).

 **Untouched, pure message layer (10+):** `PFedMsgCreate` (338), `PfmGetNext`
 (451), `QueueExistingMsg` (483), `SendMessages` (609 — only its one call to
 `GenericSend` at 631 changes), `SendToDefault` (584), `CheckOdometer` (660),
 `SetDefaultRecipient` (297), `GetDefaultRecipient` (315), `PurgeOutBox`,
 the entire out-box/in-box byte machinery (`BuffOut/BuffIn/PacketSize/
 CbFreeSpaceInOutbox/CbUsedSpaceInOutbox/ResetOutBuffer`, header 370–406, 541–546),
 `VerifyMessage` (169), and `FMSessionDesc` parsing (2092, independent of the
 transport — it just cracks a name/value string).

 Net: **≈30 member functions edited/removed out of ~44 total definitions** (≈70%),
 but the mass is concentrated: the message framing + recipient bookkeeping
 (which is what would hurt to rebuild) is 100% intact. What actually changes is
 the transport-coupled shell: init/host/join/shutdown, the sys-event reducer, the
 send sink, the receive pump. That is a *cohesive subsystem*, not a scattered
 rename.

 ### 5.2 Header (messagecore.h)

 The DirectPlay exposure is small and well-localised: `#include <dplay8.h>`
 (line 3), the `DPID` typedef (23), the two interface pointers
 `m_pDirectPlayClient`/`m_pDirectPlayServer` (594–595), the two address pointers
 (591–592), `HandleIDMap m_handleMap` (611–612, only needed for `SEND_COMPLETE`),
 `GetDPlayServer()` (507–512), `DPlayMsg` (289–293), and `InitDPlay*`/
 `ConnectToDPAddress`/`EnumHostsCallback` declarations (558–563). Under the swap
 these become `ITransport* m_transport` (+ the static dispatch trampoline) and a
 `DPlayMsg` queue becomes transport-neutral. Everything else in the header — the
 recipient hierarchy, out-box, macros, IFedMessagingSite — is untouched.

 ### 5.3 Swap or rewrite? — Honest answer: **a directed rewrite of the transport-coupled ~⅓, not a bolt-on swap, and not a ground-up rewrite**

 Why it is **not** a clean swap: there is no single call site where "give me a
 TCP-backed DirectPlay" plugs in. The DirectPlay callback model is fused with
 FedMessaging's threading (`DPlayMsgHandler` COM callback → `m_csMsgList`
 serializer, MsgHandler 961–963 / ReceiveMessages 984–991), and the
 `DPNMSG_*` structs travel all the way into `OnSysMessage`'s logic (CREATE_PLAYER →
 name fetch at 723–773). You cannot swap only `GenericSend`; you must also teach
 the receive side where datagrams/peer events come from now. So a naive
 "replace one pointer with another" does not work — hence the task's warning.

 Why it is **not** a rewrite either: the expensive, error-prone, protocol-bearing
 parts (frame composition `PFedMsgCreate`, packet walking `PfmGetNext`, the
 non-standard CRC, recipient/group/data structures, the entire FEDMESSAGE
 wire-codec surface that `AllegianceCore` mirrors) are untouched. The IGC wiring,
 `BEGIN_PFM_CREATE`, `CFMRecipient/CFMConnection/CFMGroup`, `IFedMessagingSite`,
 and all of WOPR's `NativeClient`/`ClientConnection`/clintlib call sites compile
 against an unchanged public API.

 **Sizing verdict: moderate-to-hard, but cleanly separable.** The work is a
 well-bounded rewrite of the transport-coupled shell behind `ITransport`, reusing
 the majority of the file. Estimated footprint: the ~30 functions + header member
 change above. The two genuinely fiddly bits are (a) preserving the single-threaded
 dispatch semantics (`OnAppMessage` may delete its own connection — `g_fConnectionDeleted`
 Messages.cpp:24, 1022–1027) when the socket thread and the `ReceiveMessages`
 thread differ, and (b) mapping `SEND_COMPLETE`-driven `OnMessageNAK`/roll-call
 onto reliable TCP (can be reduced to "disconnect = NAK").

 Backward-compatibility note that de-risks this: because `ITransport*` is an
 injected pointer, the existing DirectPlay code can be wrapped in a
 `DirectPlayTransport : ITransport` and kept for legacy deployments while the
 `TcpTransport` is brought up alongside. That makes the effort additive rather
 than destructive. A parallel `FedMessagingTcp` subclass is *not* the good route:
 the members to swap (`m_msgList`, out-box, non-virtual `GenericSend`/
 `ReceiveMessages`, the maps) are private and non-virtual, so subclassing would
 either duplicate the whole framing layer or require the same base edits anyway —
 strictly worse than in-place refactor.

---

## Recommendation

 **Approach.** Introduce a small `ITransport`/`ITransportSite` seam (Section 2) and
 do an **in-place, directed rewrite of the transport-coupled shell** of
 `Messages.cpp` (≈30 functions per §5.1) behind an injected `ITransport*`, leaving
 the message framing, CRC, out-box, recipient/group objects, and the whole public
 API (`SendMessages`/`GenericSend`/`ReceiveMessages`/`Begin...` macros/
 `IFedMessagingSite`) signature-identical so WOPR (`NativeClient`,
 `ClientConnection`, clintlib) compiles untouched. Ship two transports behind the
 same interface — wrap the existing DirectPlay calls as `DirectPlayTransport` for
 legacy (this is exactly the "we were never going to keep running DirectPlay",
 but keeping it stubs out risk during bring-up), and the real deliverable
 `TcpTransport`. The C# server already owns the wire codec and the multicast
 fan-out belongs on that (server) side per §3.3.

 **What the TCP transport must supply** (the binding constraints):
 1. Assign connection ids (serve as the peer-identity primitive, §3.1) and raise
  `OnPeerJoined` *before* the app sees the peer.
 2. Reliable **unicast** send to the server connection — the only send the bot
  path ever issues (§4).
 3. On the C# server side, fan out group-addressed sends per member (§3.3) and
  surface the instance GUID in the connect hello so the client's enum-on-join
  can be skipped (§3.4).

 **Honest difficulty: moderate-to-hard, cleanly separable — a rewrite of the
 transport-coupled third, not a swap, not a ground-up rewrite.** The hard parts
 are threading (preserving `FedMessaging`'s single-owner dispatch when a socket
 thread feeds the queue) and translating `SEND_COMPLETE`/`OnMessageNAK` and
 best-effort semantics onto reliable TCP. Both are tractable and largely
 *removable* for the bot path.

 **What I am unsure about:**
 - The cost of server-side fan-out at full population (Everyone broadcast ×
 high-frequency game state). Needs a quick benchmark before committing to a
 per-member loop.
 - Exactly how roll-call (`m_cAbsentCount`, `SetLastComplete`) and the ping-loss
 heuristic degrade on a reliable channel — safe defaults exist ("disconnect =
 NAK"), but server-tuning constants may need revisiting.
 - Thread-affinity of the C++/CLI `NativeClient` update tick vs. a dedicated
 socket thread — the existing `m_csMsgList` serializer must be preserved.

 **Smallest first step that proves it.** A two-process **loopback proof**, no
 `Allegiance` edits, exercising only the wire + a stand-in transport:
 1. Bring up a minimal `TcpTransport` (C#-side host) that binds a port and speaks
  the FEDMSG framing `AllegianceCore` already encodes/decodes.
 2. A second process plays the WOPR/bot role: open a TCP connection, perform the
  logon handshake, send `FM_C_LOGONREQ`/receive `FM_S_LOGONACK` (or
  `FM_C_LOGONLOBBIREQ`/`FM_S_LOGONLOBBYACK`) **as raw frames over the socket**,
  using the existing `AllegianceCore` codec for framing/CRC/var-items.
 3. Prove the opposite direction too — the C# server consuming one frame the
  *client* sent.

 That validates end-to-end encoding + transport framing + id/lifecycle assignment
 with zero dependence on the ~30-function C++ refactor, and it is the cheapest
 possible falsification: if a logon request/ack cannot round-trip over TCP with
 the existing codec, the whole plan is dead before any C++ work. Once that passes,
 the in-place `Messages.cpp` seam (Section 2) is the next milestone.
