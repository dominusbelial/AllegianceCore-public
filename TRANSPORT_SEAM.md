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
