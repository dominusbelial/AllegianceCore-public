# TRANSPORT_T1 — ITransport seam + DirectPlayTransport wrapper (additive only)

Date: 2026-08-29
Branch: `port-transport` (repo `/home/domingo/Development/Allegiance`)
Scope: **additive only** — two new source files, one .vcxproj edit. Nothing wired
in, `Messages.cpp`/`messagecore.h` untouched, `AllegianceCore/` untouched.

---

## 1. What was produced

| New file | Role |
|---|---|
| `src/_Utility/ITransport.h` | Ports §2 of TRANSPORT_SEAM.md exactly: `ITransportSite` (OnPeerJoined / OnPeerLeft / OnGroupChanged / OnSessionLost / OnReceive) and `ITransport` (Host / Join / Shutdown / IsConnected / GetLocalPlayerId / Send / GetSendQueueInfo). Every method carries a doc comment citing the `Messages.cpp` file:line it replaces. |
| `src/_Utility/DirectPlayTransport.h` / `.cpp` | Concrete `DirectPlayTransport : ITransport` that moves the DirectPlay-8-touching logic from `Messages.cpp` into a class speaking `ITransportSite`. Constructor takes `ITransportSite*` and stores it; nothing constructs it yet (wiring is the next stage). |
| `VS2022/Utility.vcxproj` | Added `<ClCompile>` for `DirectPlayTransport.cpp` and `<ClInclude>` for both headers, matching existing style. |

---

## 2. What moved (file:line -> new location)

All original locations are `Messages.cpp`.

Lifecycle:
- `InitDPlayClient` (1236-1272) -> `DirectPlayTransport::InitDPlayClient` (.cpp:155)
- `InitDPlayServer` (1282-1320) -> `DirectPlayTransport::InitDPlayServer` (.cpp:198)
- `HostSession` (1341-1423, DirectPlay parts) -> `DirectPlayTransport::Host` (.cpp:220)
- `JoinSession` (1490-1533) -> `DirectPlayTransport::Join` (.cpp:431)
- `JoinSessionInstance` x2 (1548-1608, 1613-1617) -> `DirectPlayTransport::JoinSessionInstance` (private, .cpp:392)
- `Shutdown` DirectPlay parts (1437-1448) -> `DirectPlayTransport::Shutdown` (.cpp:483)

Send:
- the `Send`/`SendTo` call inside `GenericSend` (547-555) + flag selection (513-519)
  -> `DirectPlayTransport::Send(target, pv, cb, fGuaranteed)` (.cpp:529)

Enumeration / discovery:
- `EnumSessions` (1722-1738) -> folded into `EnumHostsInternal` (see §4 ambiguity 3)
- `EnumHostsInternal` (1740-1897) -> `DirectPlayTransport::EnumHostsInternal` (private, .cpp:247)
- `EnumHostsCallback` (193-234) -> `DirectPlayTransport::EnumHostsCallback` (.cpp:312)

Introspection:
- `GetIPAddress` (1652-1692) -> `DirectPlayTransport::GetIPAddress(DPID, char[64])` (.cpp:575)
- `GetListeningPort` (1694-1720) -> `DirectPlayTransport::GetListeningPort` (.cpp:612)
- `GetSendQueue` (2137-2145) -> `DirectPlayTransport::GetSendQueueInfo` (.cpp:565)

Receive-side trampoline:
- static `DPlayMsgHandler` (245-250) -> `DirectPlayTransport::DPlayMsgHandler`
  (static member thunk, .cpp:643), dispatching into `OnDPlayMessage` (.cpp:650).
- the per-`DPN_MSGID_*` dispatch pattern from `MsgHandler` (890) and
  `OnSysMessage` (686), translated to `ITransportSite` calls (not
  `m_msgList`/`m_csMsgList`, which stays FedMessaging-owned):
  - CREATE_PLAYER (non-host) -> `OnPeerJoined` (after a faithful `GetClientInfo`
    name fetch, .cpp:671-685)
  - DESTROY_PLAYER -> `OnPeerLeft` (.cpp:692-700)
  - ADD/REMOVE_PLAYER_TO_GROUP -> `OnGroupChanged(group, peer, added)` (.cpp:703-715)
  - TERMINATE_SESSION -> `OnSessionLost` (.cpp:717-719)
  - RECEIVE -> `OnReceive(from, data, cb)` (.cpp:726-749)

TU-local helpers that are `static` inside `Messages.cpp` were reproduced
(file-local) in `DirectPlayTransport.cpp` because that TU is off-limits this
round: `SafeReleaser` (31-50), `constChar2Wchar` (99-121), `wChar2ConstChar`
(133-155), and the `static GUID g_guidApplication` enum filter (16, 1840).
`FetchPlayerName` is a new file-local helper reproducing the `GetClientInfo`
two-pass sizing dance (723-756).

---

## 3. What was deliberately left behind, and why

- **`Messages.cpp` / `messagecore.h`** entirely untouched this round (task
  scope; the originals stay until the next stage rewires `FedMessaging` onto
  `ITransport*` and deletes the moved code).
- **`FedMessaging`'s message queue** (`m_msgList` + `m_csMsgList`) — stays
  owned by FedMessaging; the next stage wires the site/transport into it.
- **`PFedMsgCreate`, `PfmGetNext`, `QueueExistingMsg`, `SendMessages` /
  `SendToDefault`, the CRC in `GenericSend`, out-box/in-box byte machinery,
  `CFMConnection`/`CFMGroup`/`CFMRecipient`, `m_cnxnsMap`/`m_groupMap`** — pure
  message-layer/bookkeeping, belongs to `FedMessaging` permanently (task scope).
- **`GenericSend`'s CRC and framing (529-538)** — message layer; `Send()` moves
  only the DirectPlay send call.
- **"Everyone" group creation in `HostSession` (1416)** and **`ResetOutBuffer`**
  — group/bookkeeping, message layer.
- **`CFMConnection::Delete`, `CFMGroup::{AddConnection,DeleteConnection,Ctor,
  Delete}`** (2015-2066) and the group-dpid hand-back at `MsgHandler`
  (h:916-917) — group/message layer (§5.1 next stage).
- **`KillSvr` (1175), `Connect`/`ConnectToDPAddress` (1138, 1112 — already
  `assert(0)` dead)** — deleted/absent in the transport; NOT reproduced.
- **`GetConnectionInfo`, `GetReceiveQueue`, `GetConnectionSendQueue`,
  `GetLinkDetails`, `SetSessionDetails`, `GetCountConnections`** — admin/server
  diagnostics, mostly degenerate today (spec §5.1); not carried into the
  transport, they stay in FedMessaging for the next stage to forward/stub.
- **`CFMConnection`/the client-side `m_pcnxnMe`/`m_pcnxnServer` construction**
  (1603, 1606) — message layer; the transport reports the two ids via
  `Join`'s out-params instead, preserving 0 / 1.

---

## 4. Flagged for the next stage (explicit, not guessed)

1. **`ITransportSite` has no `OnSessionFound`** (enum discovery). §2's interface
   omits it, yet `EnumHostsCallback` (Messages.cpp:231-232) fired it under
   `m_fSessionCallback`. Resolution chosen: `DirectPlayTransport` records the
   discovered instance GUID + host/device addresses privately
   (`m_guidInstance`, `m_pHostAddress`, `m_pDeviceAddress`, readable via
   `HasDiscoveredHost`/`GetDiscoveredHostAddress`/`GetDiscoveredDeviceAddress`);
   no site call. Next stage decides whether to extend `ITransportSite` with an
   `OnSessionFound`-like event or drop enumeration for the bot (spec §3.4
   recommends dropping it on the connect path).

2. **`ITransport::Join` carries no application GUID**, but DirectPlay's connect
   path needs it (JoinSessionInstance fills `dpnAppDesc.guidApplication`, 1563-1564;
   the enum filter needs it, 1840). DirectPlayTransport requires the site to call
   `SetApplicationGuid()` before `Join`; otherwise `Join` returns `E_FAIL`
   (guarded). A `TcpTransport` given address:port can skip discovery and won't
   need this (spec §3.4). This is an interface-fit gap, not a coding shortcut.

3. **`EnumSessions`** (1722) only differs from `EnumHostsInternal` by setting
   `m_fSessionCallback=true` (1735) so the callback fires `OnSessionFound`.
   Since there is no on-site discovery event (ambiguity 1), the browser-UX
   distinction is currently inert; enumeration is folded into
   `EnumHostsInternal`. `m_fSessionCallback` is retained as a member for
   fidelity but has no consumer yet.

4. **`DPN_MSGID_SEND_COMPLETE` -> NAK is not forwarded.** The original maps the
   async handle through `m_handleMap` (a FedMessaging member, 611-612) to
   `OnMessageNAK` (805-846). `ITransportSite` has no NAK hook and the transport
   doesn't own `m_handleMap`. T1 drops it; next stage picks "disconnect = NAK"
   for TCP (spec §5.3) or retains a NAK hook on this transport.

5. **RECEIVE buffer lifetime divergence.** Original deep-copies the
   `DPNMSG_RECEIVE` into `m_msgList` (943), holds the DPlay buffer until the
   message layer drains it (returning `DPNSUCCESS_PENDING` at 944, `ReturnBuffer`
   at 1080-1090). DirectPlayTransport instead calls `site->OnReceive(from,
   data, cb)` **synchronously** and returns the buffer immediately after — the
   site must copy during the call. This removes the async buffering the next
   stage will need; the wiring must restore the original ordering (or accept the
   synchronous-copy contract) when it brings up the receive queue. Explicitly
   NOT presumed correct for the final design — flagged so it is revisited.

6. **CREAT_PLAYER / DESTROY_PLAYER peer-lifecycle trimming.** The original
   CREATE_PLAYER handling (700-774) also created `CFMConnection`s and, on the
   server, treated `pvPlayerContext==1` as its own "me" (710-712) and fetched
   names via GetClientInfo. In the transport, `pvPlayerContext==1` sets
   `m_localPlayerId` (no site event), other server players raise `OnPeerJoined`,
   and a DirectPlay **client** never raises `OnPeerJoined` (its me/server ids are
   fabricated, matching the original's `assert(m_pDirectPlayServer)` at 716).
   The DESTROY_PLAYER me/server-dies branch (793-797) fired `OnSessionLost` +
   `Shutdown` using message-layer CFM objects; here it reduces to `OnPeerLeft`
   and the site (next stage) applies the me/server interpretation. If the client
   actually receives a real CREATE_PLAYER for the server player that the original
   mishandles (assert), that quirk is preserved by not surfacing it — no guess
   made about its intended mapping.

7. **`GetIPAddress`/`GetListeningPort` not in §2's `ITransport`** even though
   §5.1 lists them as "rewritten to go through ITransport". Resolved by exposing
   them as non-interface methods on `DirectPlayTransport` (note: they take a
   `DPID`, not a `CFMConnection&`, per §2's no-CFM-objects rule). Next stage
   promotes them into `ITransport` or stubs them.

8. **`OnMessageSent` / `OnPreCreate` / `OnMessageBox`** (IFedMessagingSite hooks)
   have no `ITransportSite` equivalent and are dropped; failing HRESULTs are
   returned instead of popping dialogs. Errors are surfaced as return codes, not
   UI.

---

## 5. Verification

Definition of green: `Utility` builds with the two new files compiled in, zero
new errors, and `ClintLib` (depends on `Utility`) still builds clean. Build
recipe (proven settings): host 192.168.0.240, toolset v145, platform Win32,
`Allegiance.sln /t:Utility /t:ClintLib /p:Configuration=Debug
/p:Platform=Win32 /p:PlatformToolset=v145 /m /v:minimal /nologo`.

Result: **GREEN — 0 errors.** Neither target emitted any `error C####` line.
The only compiler diagnostic in the whole log is a **pre-existing** warning,
unrelated to this change:
`src\zlib\ztime.cpp(104,1): warning C4172: returning address of local variable
or temporary : timeString` (ZLibrary.vcxproj) — present before T1; my files
contribute zero warnings.

Key compile evidence (verbatim from the run):

```
  pch.cpp
  allegdb.cpp
  ...
  Messages.cpp
  DirectPlayTransport.cpp     <-- new file compiled clean
  Utility.cpp
  Generating Code...
  Utility.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\_Utility\Utility.lib

RESULT_Utility_BUILD=PASS
=== Build ClintLib ===
  ClintLib.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\ClintLib\ClintLib.lib

RESULT_ClintLib_BUILD=PASS
=== ALL DONE ===
```

(The effective configuration MSBuild selected for Win32 under this invocation is
`FZDebug-NoYellowComm`; that is the static-library configuration Utility exports,
matching the prior day's proven run.) Both `.lib` targets were emitted, proving
`DirectPlayTransport.cpp` compiled and linked with no regressions in the
downstream `ClintLib`.

No commit was made until the real Windows build confirmed green.
