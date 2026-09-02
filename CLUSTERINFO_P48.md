# P48 — diagnose (and, if confirmed, fix) why FM_S_CLUSTERINFO never reaches ScoutExploreMap despite being sent correctly

**Repo:** `AllegianceCore` (branch `master`), top commit `2e41094` (P47). Native
`Allegiance` repo read-only (this task is diagnosis only; no native edits, no
`sudo`). Date: 2026-09-01.

---

## TL;DR conclusion

**The specific hypothesis this task carried — that `FM_S_CLUSTERINFO`'s fixed
4204-byte encoding is truncated by the native message-core's 2048-byte
`m_rgbbuffAlloc` buffer — is REFUTED by reading the source precisely.** That
buffer is on the **outgoing-construction** path only, is not on the receive
path, and even where it is asserted (construction), the *actual* allocation is
`cbmsg`-sized, not bounded by 2048. The native client **receive** path
provably carries a 4204-byte CLUSTERINFO intact, end-to-end, straight into the
managed `FMD_S_CLUSTERINFO` constructor and then into `StrategyBase.cs`'s
handler.

Because the hypothesized cause is refuted, I do **not** implement the proposed
compact encoder. It was scoped as a remedy to a truncation that the diagnosis
does not support — applying it anyway would repeat P40's exact mistake
(changing server output to fix an unpinned, wrongly-guessed cause). The root
cause of the observed non-delivery is **genuinely open** from source reading
alone and needs the live VM pass. Concrete, ordered things for Claude Code's
pass to check are at the bottom.

Confidence: **high** on the refutation (the receive path is unambiguous),
**honestly inconclusive** on the ultimate why-the-handler-didn't-fire.

---

## What I actually read (every claim cites file:line)

### 1. The server send site — C#, 4204 bytes, no native involvement

`src/Allegiance.Server/Server.cs:797-799`:

```
var clusterInfo = ClusterInfo.FromSectors(_missions[0].Sectors);
SendPeer(from, FedPacket.Pack(new[] { clusterInfo.Encode() }));
Console.WriteLine($"[clusterinfo] peer {from} ... -> CLUSTERINFO(200) ...");
```

`ClusterInfo.Encode()` always emits the fixed **4204-byte** message
(`src/Allegiance.Core/Wire/ClusterInfo.cs:116-150`, buffer `new byte[FixedSize]`,
layout block `:12-20`). The server is C#; it sends through its **own** managed
transport. Neither the AllegianceCore server send nor its client loopback Bot
touches the native message core at all. So `m_rgbbuffAlloc` (which lives only
in the native C++ repo) cannot truncate the C# server's send.

### 2. Native receive path — buffer sized to the actual frame everywhere

I traced the native client path from TCP arrival to the managed constructor.
It is clean for a 4204-byte message:

- **TCP read:** `src/_Utility/TcpTransport.cpp:443` allocates
  `body = new BYTE[lenRaw]` — exactly the frame body length. The only gate is
  `lenRaw > c_dwMaximumFrame` → fail-close (**never truncate**), and
  `c_dwMaximumFrame = 1 << 24` ≈ **16 MiB** (`TcpTransport.cpp:52`). 4204 is
  nowhere near it. Delivered via `m_pSite->OnReceive(c_dpidServer, body, cb)`
  (`TcpTransport.cpp:351`).

- **Deep copy at the site:** `src/_Utility/Messages.cpp:313-328`
  (`FMTransportSite::OnReceive`) does `copy = new BYTE[cb]; memcpy(copy,pData,cb)`
  — heap copy of exactly the frame bytes — into `FMReceiveMsg{pReceiveData=copy,
  dwReceiveDataSize=cb}` (`:267`), queued to `m_msgList` under
  `EnterCriticalSection(&m_csMsgList)`.

- **Dispatch/drain:** `FedMessaging::ReceiveMessages()` (`Messages.cpp:887-1034`)
  pops it, `pfm = (FEDMESSAGE*)p_dpMsg->pReceiveData` (`:929`), sets
  `m_rgbbuffInPacket = pReceiveData` and `m_dwcbPacket = dwReceiveDataSize`
  (`:928,930`). The sanity gate at `:947` is
  `pfm->fmid > 0 && pfm->cbmsg >= sizeof(FEDMESSAGE) && pfm->cbmsg <= PacketSize()`
  with `PacketSize()` = `m_dwcbPacket` (`messagecore.h:431-433`). For a sole
  CLUSTERINFO packet, `cbmsg(4204) <= PacketSize()` **passes** (whether or not
  `dwReceiveDataSize` counts the trailing CRC). No "HACKER??" drop. Forwards via
  `m_pfmSite->OnAppMessage(this, *pcnxnFrom, pfm)` (`:966`). The T18 infinite-loop
  fix (`:981-993`) concerns malformed packets and does not affect a well-formed
  4204-byte message.

- **Native→managed bridge:** `src/Wopr/AllegianceInterop/ClientConnection.cpp:252-259`:
  ```
  void ClientConnection::OnAppMessageEvent(..., FEDMESSAGE * pfm) {
      array<Byte> ^ bytes = gcnew array<Byte>(pfm->cbmsg);   // = 4204
      Marshal::Copy((IntPtr)pfm, bytes, 0, pfm->cbmsg);        // 4204 bytes
      OnAppMessage(this, bytes);
  }
  ```
  So `messageData` handed to the C# `MessageReceiver.OnAppMessage` is **exactly
  4204 bytes long**.

### 3. `ManagedObject<T>` base constructor — aliases, never copies, never truncates

`src/Wopr/AllegianceInterop/ManagedObject.h:54-64`:

```
ManagedObject(array<System::Byte> ^ bytes) {
  ...
  m_pinnedBytes = GCHandle::Alloc(bytes, GCHandleType::Pinned);
  m_Instance = (T *)m_pinnedBytes.AddrOfPinnedObject().ToPointer();
}
```

It **pins the caller's array and casts its pointer** directly to `T*`. There is
**no copy into a `sizeof(T)`-sized buffer** and **no length validation**. So
`m_Instance` is a pointer into whatever array was handed in; the decode is safe
only if that array is long enough. Here it is (4204). The question the task
posed — "what happens with a bytes array shorter than `sizeof(T)`?" — is moot
here because the array is *not* short, but for completeness: a short array would
**not** throw; the C++/CLI would read out-of-bounds past the pinned GC array
(UB/garbage), with no automatic bounds check, because `m_Instance` is a raw
`T*`. No guarded read exists. (Worth stating because it means "shorter input" is
silent, not loud.)

### 4. `FMD_S_CLUSTERINFO` constructor — walks only to the `-1` sentinel, in-bounds

`src/Wopr/AllegianceInterop/AllMessages.h:10172-10198`: the managed ctor calls
`ManagedObject(bytes)`, then loops `clusterIDs[i]` up to `-1` and `homeSectors[i]`
at the same index (`:10176-10178`), and warp edges pairing `warpFrom`/`warpTo`
(`:10181-10197`). With 13 clusters / 38 warps this reads only offsets ~4-68 /
100-176 / 2152-2226 — all far inside the pinned 4204-byte array. Safe. It
assigns `message.Clusters` → handler sets `GameInfo.Clusters` (`StrategyBase.cs:172-174`).

### 5. Dispatch + the swallowing catch

`src/Wopr/Wopr/MessageReceiver.cs:1125-1132` has the `FM_S_CLUSTERINFO` case;
the whole `switch` sits inside `try { ... }` (`:27`) with a **broad
`catch (Exception ex)`** (`:1311-1314`) that writes the exception to
`c:\1\logs\{player}_OnAppMessageExceptions.txt`. So if construction or the
`.Invoke` threw, the handler's diagnostic print would never fire **and the
exception would be logged to that file** — which the live pass should read first.

---

## The buffer hypothesis, precisely refuted

`BYTE m_rgbbuffAlloc[2<<10]; // 2K this is a temp buffer for alloc'd messages`
(`src/Inc/messagecore.h:636`).

Its only consumer in the message core is the **outgoing-construction** branch
of the blob-building routine (`src/_Utility/Messages.cpp:472-478`):

```
else // need to allocate space
{
  // allocate a new blob to return the message in
  assert (((FEDMESSAGE *)pbFM)->cbmsg <= sizeof(m_rgbbuffAlloc));
  BYTE * pbMsg = (BYTE *) GlobalAllocPtr(GMEM_MOVEABLE, ((FEDMESSAGE *)pbFM)->cbmsg);
  CopyMemory(pbMsg, pbFM, ((FEDMESSAGE *)pbFM)->cbmsg);
  pbFM = pbMsg;
}
```

Three independent reasons it cannot be the cause of the observed symptom:

1. **It is not on the receive path.** Received data is carried by
   `m_rgbbuffInPacket` (a `BYTE*`, `messagecore.h:633`), set to the heap deep
   copy whose size is the actual frame length (`Messages.cpp:928,320-323`). The
   2048-byte `m_rgbbuffAlloc` and the two out-packet buffers (`m_rgbbuffOutPacket
   [16K]`, `m_rgbbuffSecondaryOutPacket[1K]`, `messagecore.h:634-635`) are the
   *outgoing* pool. Incoming bytes never pass through it.

2. **Even on the construction side it is only an `assert`**, which is compiled
   out in release (`/NDEBUG`) builds — the shipped Wopr.exe is a release build
   (FZDebug aside, asserts are off). The real allocation at `:476` is
   `GlobalAllocPtr(GMEM_MOVEABLE, cbmsg)` — **`cbmsg`-sized, not 2048-bounded**.
   So nothing actually truncates even an oversized constructed message.

3. **CLUSTERINFO is not constructed on the client at all.** It is *received*
   and decoded in the managed constructor. The `BEGIN_PFM_CREATE_ALLOC`/
   `m_rgbbuffAlloc` path is for *outgoing* messages the client builds to send
   (e.g. `FMD_LS_LOBBYMISSIONINFO` at `AllMessages.h:8680-8683`). CLUSTERINFO is
   server→client; the client never builds it.

Separately, the C# server send does not involve native buffers at all
(`Server.cs:797-798` via the managed transport).

⇒ **The 2048-byte buffer cannot truncate this message on either end.** Refuted.

## Why the loopback pass passed but the native live pass didn't (asymmetry)

P47's loopback `Bot` (`src/Allegiance.Bot/Bot.cs`) is a **pure C# managed
`TcpTransport` client** (`Bot.cs:140-141` `new TcpTransport(this); Join(...)`,
decoding via `FedPacket.Unpack`, `Bot.cs:25,18-20`) speaking the ported messages
over a TCP socket. It does **not** traverse the native C++/CLI
`AllegianceInterop.ClientConnection` / `FedMessaging::ReceiveMessages` /
`ClientConnection::OnAppMessageEvent` path at all. So the loopback test proves
the **C# server's `Encode()` and the C# `Decode()` agree** — nothing about
whether the native client's receive/dispatch actually delivers the message.
That is exactly the gap the native live pass exposed. This asymmetry should be
stated plainly so nobody reads P47's loopback as proof of native arrival.

---

## What's confirmed vs. refuted vs. open

**Confirmed (by source reading):**
- The native client receive path (TCP → heap frame copy → `FedMessaging::
  ReceiveMessages` → `ClientConnection::OnAppMessageEvent` → pinned managed
  array) carries a 4204-byte CLUSTERINFO intact and in-bounds straight to the
  `FMD_S_CLUSTERINFO` constructor and handler.
- `PacketSize()` gate and the TCP frame cap both comfortably accept 4204 bytes.
- `ManagedObject(bytes)` aliases (pins) the exact input array; it neither copies
  nor truncates. (Short-input would silently over-read, not throw.)
- `MessageReceiver.cs` has a broad `catch(Exception)` at `:1311` that would
  swallow a constructor/Invoke failure and record it in
  `c:\1\logs\<player>_OnAppMessageExceptions.txt`.

**Refuted:**
- The 2048-byte `m_rgbbuffAlloc` truncation hypothesis (three independent
  reasons above).

**Open (root cause of the observed non-delivery not pinned from source alone):**
- Something between the server log line and the handler's diagnostic print drops
  the message or fails the handler silently. Since source reading shows the
  receive should deliver it, and the swallow-catch is the only sink that could
  absorb a failure without the diagnostic firing, the highest-probability
  residue is a failure swallowed at `MessageReceiver.cs:1311` (recorded in
  `_OnAppMessageExceptions.txt`), OR an arrival/ordering issue before the
  switch. Neither can be discriminated without the live VM.

## What I deliberately did NOT do

- **Did not implement the compact encoder.** It was scoped as the remedy to a
  truncation this diagnosis refutes. Applying it now would be changing server
  output to fix an unpinned, wrongly-attributed cause — precisely P40's mistake
  this task exists to avoid. If the live pass later proves the native receive
  genuinely mishandles large messages (it shouldn't, but if it does), the compact
  encoder becomes a defensible mitigation and can be added then, with tests.
- **No native edits** (read-only), **no `sudo`**, **no wiki/Obsidian writes**.
- **No code changes ⇒ no build/test run, no commit.** Per the task's §2 "stop
  here" branch. Working tree unchanged (verified below).

## Next for Claude Code's live pass (ordered)

1. **Read `c:\1\logs\<player>_OnAppMessageExceptions.txt`** (and the `_messages.txt`
   log) for any swallowed exception around the CLUSTERINFO arrival. This is the
   single most likely place the real cause is recorded today, and it costs
   nothing.
2. **Instrument `ClientConnection::OnAppMessageEvent`**
   (`src/Wopr/AllegianceInterop/ClientConnection.cpp:252-259`) to print
   `pfm->cbmsg` and `pfm->fmid` — confirms the native side even raises
   OnAppMessage for a 4204-byte message and settles arrival vs. construction.
3. **Instrument the switch itself** (`MessageReceiver.cs:1125-1132`) before the
   `new FMD_S_CLUSTERINFO(messageData)` to confirm the case is reached with
   `messageData.Length == 4204`.
4. Re-check `GameInfo.Clusters` afterward (a print at the top of
   `GetUnexploredClusters`, `GameInfo.cs:69`) rather than only the handler print,
   to rule out a later wipe (unlikely; nothing else writes `GameInfo.Clusters`).

If (2) shows `pfm->cbmsg` < 4204 on a release build, THEN revisit the compact
encoding with the receipt-side short-buffer tolerance properly characterized
(the `-1` sentinel found before whatever was copied is the only safe
justification, per the task's own load-bearing-precondition caveat).

## Session stats

Logged via `python3 ~/Development/allegiance-tools/session_stats.py <session_id>`
(see "Verification" below for the actual run).

## Verification

- Branch: `master`, top commit `2e41094` (P47) — confirmed at start.
- No files modified during this task (diagnosis only): `git status --porcelain`
  in `AllegianceCore` shows no changes, so there is nothing to build, test, or
  commit.

```
(reproduced in the shell as part of finishing; see the agent's final message
for the session_stats output)
```

---

*Honesty note:* I could not run the native client (no Windows VM), so the
non-delivery itself remains unfixed and the definitive root cause remains for a
live-instrumented pass — exactly this project's established precedent (P33's
four-bug hunt) for settling this class of question.
