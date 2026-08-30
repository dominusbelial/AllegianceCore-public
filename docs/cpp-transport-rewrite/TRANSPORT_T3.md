# TRANSPORT_T3 — wire the receive path (`FMTransportSite`'s five methods)

Date: 2026-08-30
Branch: `port-transport` (repo `/home/domingo/Development/Allegiance`)
Base: `969bc874 T2: wire FedMessaging onto ITransport (finish T2 attempt; fix A-D)`
HEAD: `13b5f8e9 T3: ...`
Scope: **`src/_Utility/Messages.cpp` only.** `AllegianceCore/` untouched,
`messagecore.h`/`DirectPlayTransport.*` untouched, the out-of-scope
`DPN_MSGID_CREATE_GROUP` branch, `DPN_MSGID_SEND_COMPLETE` branch, and
`CFMConnection`/`CFMGroup` friend methods untouched. The three payload structs
were placed in `Messages.cpp` (not a public header) above the site methods, as
the task permitted ("keep them out of any public header").

---

## 1. What was wired

All file:line refer to the current `Messages.cpp`.

### The three payload structs (`Messages.cpp:250-252`)

```cpp
struct FMPeerJoinedMsg  { DPID dpid; char name[64]; };
struct FMGroupChangeMsg { DPID dpnidGroup; DPID dpnidPlayer; };
struct FMReceiveMsg     { DPID dpnidSender; BYTE* pReceiveData; DWORD dwReceiveDataSize; };
```
Placed in the .cpp, not in `messagecore.h`, so no unrelated translation unit
sees them. Field names mirror their real DirectPlay counterparts on purpose.

### The five producers (was `Messages.cpp:249-276`, now `:254-313`)

Each: build one owned payload, push a `DPlayMsg` onto `m_msgList` under
`m_csMsgList`, return. None of them touch connection/group state or call
`m_pfmSite->On...` — the threading discipline the task mandates (see §5).

| Site method | Payload | dwType |
|---|---|---|
| `OnPeerJoined(id,name)` | `new FMPeerJoinedMsg()`; bounded `strncpy` of name + explicit null terminator | `DPN_MSGID_CREATE_PLAYER` |
| `OnPeerLeft(id)` | `new DPID(id)` | `DPN_MSGID_DESTROY_PLAYER` |
| `OnGroupChanged(group,peer,fAdded)` | `new FMGroupChangeMsg{group,peer}` | `ADD_PLAYER_TO_GROUP` or `REMOVE_PLAYER_FROM_GROUP` |
| `OnSessionLost()` | `NULL` (nothing consumed downstream) | `DPN_MSGID_TERMINATE_SESSION` |
| `OnReceive(from,pData,cb)` | `new BYTE[cb?cb:1]` deep copy + `new FMReceiveMsg{from,copy,cb}` | `DPN_MSGID_RECEIVE` |

### Consumer-side cast changes

- **CREATE_PLAYER branch (`:720-737`)** — cast `msg.pData` to `FMPeerJoinedMsg*`
  instead of `DPNMSG_CREATE_PLAYER*`, call `CreateConnection(lp->name, lp->dpid)`
  replacing the T2 `"<peer-joined>"` placeholder-name stub **and its comment**.
  **Deleted** the `pvPlayerContext==(void*)1` server-player special case
  (was `:689-696`) per T1 ambiguity #6 — the transport already filters "self"
  (DirectPlayTransport.cpp:674-678) before raising `OnPeerJoined`, so this event
  only ever carries remote peers.
- **DESTROY_PLAYER branch (`:742-769`)** — cast to `DPID*`, deref for the id
  (`GetConnectionFromDpid(*lp)`). The `m_pcnxnMe`/`m_pcnxnServer` death check →
  `OnSessionLost`+`Shutdown`, else `DeleteConnection`, unchanged.
- **ADD/REMOVE_PLAYER_TO_GROUP (`:812-841`)** — both cast to `FMGroupChangeMsg*`;
  field names match, so `GetGroupFromDpid`/`GetConnectionFromDpid`/`PlayerAdded`/
  `PlayerDeleted` are unchanged.
- **TERMINATE_SESSION (`:842-845`)** — no consumer change needed (reads no
  payload fields).

### Buffer-ownership fix in `ReceiveMessages` (`:900-998`)

Cast at the RECEIVE branch (`:907`) to `FMReceiveMsg*` — field names
(`dpnidSender`/`pReceiveData`/`dwReceiveDataSize`) match, so the CRC check, the
`FEDMESSAGE` walk, `OnAppMessage`/`OnBadCRC` need no other change. Added
`delete[] p_dpMsg->pReceiveData;` at the **same level as `m_timeMsgLast =
Time::Now();`** (`:993-994`), i.e. NOT nested inside the `if (pcnxnFrom != 0)`
block, so the copy is freed whether or not the sender resolved. The original's
comment about returning the DPlay buffer was rewritten to reflect the new owner
(the transport already returned the buffer inside `OnDPlayMessage`; the message
layer frees the copy *we* made).

---

## 2. Verbatim build evidence (real Windows build)

Host `192.168.0.240`, SSH key auth (no ICMP — SSH only). `build_t3.ps1` scp'd
over and run with `powershell.exe -ExecutionPolicy Bypass`. Repo tar synced
(`--exclude=.git --exclude=Debug --exclude=Release`); prior
`C:\Users\domingo\Allegiance` removed before extracting. Retargeted the
extracted copy v143->v145 (**28 vcxproj files**) because the host has no v143
toolset — identical host accommodation T2 documented. MSBuild 18.9.1 via
vswhere. Tail of the run:

```
RETARGETED_V143_TO_V145_FILES=28
MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe
CWD=C:\Users\domingo\Allegiance\VS2022
=== Build Utility;ClintLib ===
...
  Messages.cpp
  DirectPlayTransport.cpp
  Utility.cpp
  Generating Code...
  Utility.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\_Utility\Utility.lib
  pch.cpp
  appmsg.cpp
  AutoDownload.cpp
  badwords.cpp
  clintlib.cpp
  Generating Code...
  ClintLib.vcxproj -> C:\Users\domingo\Allegiance\objsv145\FZDebug-NoYellowComm\ClintLib\ClintLib.lib
LASTEXITCODE=0
RESULT_OK
```

**Zero errors.** Grep for `error C\d+` across the log: no matches. Only compiler
diagnostic in the whole run is the pre-existing warning, unrelated to this
change:
`src\zlib\ztime.cpp(104,1): warning C4172: returning address of local variable
or temporary : timeString [ZLibrary.vcxproj]` — present in T1/T2 as well; my
changes contribute zero warnings. `Messages.cpp` compiled (listed in the
`Generating Code...` run) and both `.lib` targets were emitted, proving no
regression downstream in `ClintLib`.

Definition of green (Utility + ClintLib build with zero new errors) is met.
There is still no live two-party test (no `TcpTransport` yet, build-only recipe),
so verification = clean build + the careful reading in §5.

---

## 3. Ambiguity status (confirmed, not reopened)

- **#1 (no `OnSessionFound` on `ITransportSite`)** — STAYS DROPPED. No
  `OnSessionFound` event added. WOPR/bots are pure client-half direct-connect;
  `EnumSessions`/`EnumHostsInternal` remain unused by the bot path. Untouched.
- **#4 (`SEND_COMPLETE`→NAK)** — STAYS DEFERRED. No `FMTransportSite` method
  maps to "send failed"; `OnSysMessage`'s `DPN_MSGID_SEND_COMPLETE` branch
  (`:770-811`) has no producer and remains dead code. No NAK hook added. Next
  candidate is a real `TcpTransport` ("disconnect = NAK", TRANSPORT_SEAM.md
  §5.3).
- **#5 (receive buffer lifetime)** — NOW RESOLVED in code, not just documented.
  The transport returns the DirectPlay buffer immediately after calling
  `OnReceive` (DirectPlayTransport.cpp:756-759); the site deep-copies on the
  callback thread and the message layer frees the copy after draining. The
  lifetime hazard the synchronous contract created is closed by the ownership
  transfer: copy is owned by the message layer from queue-time onward.
- **#6 (CREATE_PLAYER server-player special case)** — NOW RESOLVED in code,
  not just documented. The `pvPlayerContext==1` branch is deleted from
  `OnSysMessage`; self identity is handled exclusively by
  `HostSession`/`JoinSession`'s `m_pcnxnMe`/`m_pcnxnServer` construction (T2).
  The transport filters "self" before `OnPeerJoined`.

---

## 4. Things that surprised me / worth noting

1. **TRANSPORT_SEAM.md is not present in this repo.** I searched the whole tree
   (working tree and `git log --all` for it); it is referenced everywhere
   (`AllegianceCore/` per the prompt, and by T1/T2) but no file named
   `TRANSPORT_SEAM.md` exists anywhere in the repo. This task's prompt quotes
   the §5.3 bits I needed ("harder half", "disconnect = NAK"), so I proceeded on
   that. If it should exist, it's missing from this branch — flagging so nobody
   assumes it's here.

2. **Confirmed the thread-safety premise from reading, as the task anticipated:**
   `CreateConnection` (`Messages.cpp:1349-1360`), `GetConnectionFromDpid`
   (`:1292-1311`), `GetGroupFromDpid` (`:1314-1321`), and `DeleteConnection`
   (header `messagecore.h:446`) have **no locking of their own** — they mutate/
   read `m_listCnxns`/`m_cnxnsMap`/`m_groupMap`/`m_pgrpEveryone` with no critical
   section. This confirms the single-owner discipline is mandatory: the site
   methods must not call them from the transport thread. My site methods don't —
   they only lock `m_csMsgList` and push onto `m_msgList`. The consumer
   functions run only on the game thread, so the no-locking maps stay safe.

3. **One small divergence from the prompt's line-number guesses** (allowed by the
   rules): the prompt cited the CREATE_PLAYER special case at `:689-696` and the
   branch at `:683-710` — accurate at write time, but T2's edits (and my +37-line
   struct insertion) shifted line numbers. I trusted the code and patched by
   content, not by the cited offsets.

4. **Ownership accounting for the RECEIVE path**, traced end-to-end: `new
   BYTE[]` in `OnReceive` (`:305`) → consumed inside the branch → `delete[]
   p_dpMsg->pReceiveData` (`:993`) → `delete msg.pData` frees the outer
   `FMReceiveMsg` (`:997`). Exactly one `new[]`, one `delete[]`, one `new`, one
   `delete` per message. For the non-RECEIVE messages the outer `delete
   msg.pData` covers the `FMPeerJoinedMsg`/`DPID`/`FMGroupChangeMsg`/`NULL`
   payloads (trivially destructible).

5. **No self-contradiction in this prompt's scope rules** (contrast with T2's
   fix-D). "Do not touch CREATE_GROUP / SEND_COMPLETE / friend methods /
   AllegianceCore" does not collide with any change this task required. I
   deliberately left `DPN_MSGID_SEND_COMPLETE` (`:770-811`) and `DPN_MSGID_CREATE_GROUP`
   (`:738-741`) intact.

---

## 5. Threading discipline honored

- Site callbacks: thread = transport's own; work limited to allocating a payload,
  locking `m_csMsgList`, pushing to `m_msgList`, unlocking. The callback thread
  never enters the connection/group maps.
- Consumers (`OnSysMessage`/`ReceiveMessages`): thread = game; they do the
  actual `CreateConnection`/`DeleteConnection`/`GetConnectionFromDpid`/group
  work, which (per §4 item 2) relies on being called from a single thread.

`AllegianceCore/` untouched. Committed only after the real Windows build
confirmed green (§2).
