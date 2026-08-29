# FEDMSG — the Allegiance message wire format (framing + logon handshake)

Reference C++ lives in the sibling checkout at
`/home/domingo/Development/Allegiance/` (read-only here). Line numbers below are
against that tree as of 2026-08-29. Everything is derived from the C++; the C++
wins where anything disagrees.

Scope of this file + the accompanying `src/Allegiance.Core/Wire/` code: the
**framing** and the **logon handshake** only — enough for a client to connect
and be acknowledged. Of the 136 `DEFINE_FEDMSG` entries in `Messages.h`, we port
the framing plus **two** messages (`LOGONREQ` #1, `LOGONACK` #2). Everything
else (DirectPlay transport, lobby, encryption, game-state, chat, ship updates)
is out of scope and deliberately not touched. If we were to start porting
message #7, that would be scope creep — we stop at #2.

Gate: `MSGVER = 216` (`MessageVersion.h:21`).

---

## 1. The macro and the `FEDMESSAGE` base — what every message carries

`DEFINE_FEDMSG(TYPE, SHORTNAME, NUMBER)` expands to a struct
`FMD_<TYPE>_<SHORTNAME> : public FEDMESSAGE` plus a `CFEDMSGID`
`FM_<TYPE>_<SHORTNAME> = NUMBER` (the message id constant).

- `messagecore.h:35-46` — the base `FEDMESSAGE` is exactly four bytes:

  ```
  offset 0: CB     cbmsg   (unsigned short)   // total message size incl. itself
  offset 2: FEDMSGID fmid  (unsigned short)   // message id (1 = LOGONREQ ...)
  ```

  `CB` is `unsigned short` (`messagecore.h:28`). `FEDMSGID` is `unsigned short`
  (`messagecore.h:21`). Everything after these four bytes is message-specific.

- The header comment at `messagecore.h:32-46` is normative:
  *"A FedMessage by itself is never used. It is merely the start of every
  message."*  `cbmsg` = sum of the fixed-size struct *plus* all variable-length
  blobs. A message is not stack-allocatable precisely because `cbmsg` varies.

- So: **every message on the wire is preceded by 4 bytes — `cbmsg`, then
  `fmid`, little-endian** (native byte order; see §5).

---

## 2. `FM_VAR_ITEM` — how variable-length data is laid out and located

This is the crux. Definitions:

- `messagecore.h:27` — `IB` (index of bytes) and `CB` (count of bytes) are
  **both `unsigned short`**. Comment: *"there is code that depends on these
  being the same size."*
- `messagecore.h:693` — the field pair generator:
  `#define FM_VAR_ITEM(NAME) IB ib##NAME; CB cb##NAME`
  i.e. each variable-length item is represented by **two consecutive
  little-endian shorts**: `ib` (an **offset** from the start of the message to
  the item's bytes) and `cb` (the item's **length** in bytes).
- `messagecore.h:701-706` — ordering rule:
  *"For each message structure, the variable length items MUST go first, then
  the fixed length items."* So the `ib/cb` pairs sit immediately after the
  4-byte `FEDMESSAGE`, then the fixed fields, then the blobs.
- `messagecore.h:696` — dereference:
  `#define FM_VAR_REF(PFM, NAME) ((PFM)->cb##NAME ? (char*)(PFM)+(PFM)->ib##NAME : NULL)`
  i.e. a `cb` of 0 means "absent", and the item lives at `message_start + ib`.

### How a message is composed on the wire (`_Utility/Messages.cpp:338-439`, `PFedMsgCreate`)

Reading that function tells us the exact layout an encoder must reproduce:

1. `pbFM` = start of the message. `((FEDMESSAGE*)pbFM)->fmid = fmid` is written
   at offset 2 first.
2. `pBlobDst = pbFM + cbfm` where `cbfm = sizeof(FMD_*)` is the **compiler-packed
   size of the fixed struct** (header + all `ib/cb` pairs + all fixed fields).
   Variable **blobs are appended after the entire fixed region.**
3. `pib = (IB*)(pbFM + sizeof(FEDMESSAGE))` — the first `ib/cb` pair is written
   right after the 4-byte header.
4. For each `FM_VAR_PARM(ptr,len)`:
   - `*pib = pBlobDst - pbFM` (store the blob's offset from message start)
   - `*pcb = len` (or the trailing-space-trimmed length for `CB_ZTS` strings)
   - the blob bytes are copied to `pBlobDst`, advancing `pBlobDst`.
   - `pib += 2` moves to the next `ib/cb` pair.
5. Finally `((FEDMESSAGE*)pbFM)->cbmsg = pBlobDst - pbFM` — `cbmsg` is the
   distance from the start of the message to the end of the last blob.

### Zero-terminated-string convention

`messagecore.h:618` — `#define CB_ZTS 0xffff` marks a variable param as a
null-terminated string during *creation*. `Messages.cpp:372-398`: for `CB_ZTS`,
the writer measures `lstrlen`, **trims trailing spaces**, copies the string,
**appends a single `\0`**, and stores `cb = len+1` (length *includes* the NUL).
So a variable string item on the wire is `length = strLen + 1`, with a trailing
NUL byte, and `ib`/`cb` pointing at it. Our encoder must reproduce the
trailing-space trimming and the included NUL.

### Canonical LOGONREQ wire layout (declared spec)

`Messages.h:36-51`:

```
offset 0: ushort cbmsg
offset 2: ushort fmid = 1
offset 4: ushort ib CharacterName
offset 6: ushort cb CharacterName
offset 8: ushort ib ZoneTicket
offset10: ushort cb ZoneTicket
offset12: ushort ib CDKey
offset14: ushort cb CDKey
offset16: ushort ib MissionPassword
offset18: ushort cb MissionPassword
offset20: ushort fedsrvVer
offset22: (pad to 4-align for Time)
offset24: uint32  time                       (Time = uint32 wrapper, ztime.h:22)
offset28: uint32  dwCookie                   (DWORD)
offset32: int32   crcFileList                (int)
   #ifndef NO_STEAM  (modern builds DO define steam)
offset36: int8    steamAuthTicket[1024]
offset1060:uint32 steamAuthTicketLength
offset1068:int64   steamID
offset1076:char    drmHash[50]
offset1126: ---- struct ends; padded to 1128 (8-align) ----
```

- Fixed struct size with STEAM = **1128** (MSVC default packing; the largest
  member is `uint64_t steamID`, so struct alignment is 8 and `sizeof` rounds
  1126 up to 1128). `Time` is 4 bytes (`ztime.h:22` `uint32_t m_dwTime`).
- The five blobs start at message offset 1128 and continue until `cbmsg`.

⚠️ **Known upstream discrepancy — read before trusting byte-exact tests:**

The *declared* struct has **four** var items, but the client
(`clintlib.cpp:1339-1345`) passes **five** `FM_VAR_PARM`s, the fifth being a
stale `drmHash` (marked `// BT - STEAM // TODO: I believe this is a bug and
should be removed`). `PFedMsgCreate` writes an `ib/cb` pair for it anyway at
offset 36+, i.e. **over the first four bytes of `steamAuthTicket`**. So a real
client's `LOGONREQ` is not the clean 1128-byte struct: it carries an extra
`ib/cb` pair that aliases `steamAuthTicket[0..3]`. Consequences:

- We cannot hand-assert the *exact* client bytes without choosing sides on this
  bug and on the `NO_STEAM` macro. **We therefore commit to the round-trip
  property and to structural invariants, and to an independently-computed CRC —
  NOT to a fabricated "expected" client buffer.** This is the honest reading of
  the task's escape hatch. Details in §"Byte-level verification".
- Our port implements the **declared spec** (4 var items + STEAM fixed fields),
  which is what the server validation path (`FedSrv.CPP:5824`) consumes. If we
  later need to accept a real modern client verbatim, we must tolerate the 5th
  `ib/cb` pair aliasing `steamAuthTicket` — noted in README's "still needs".

`LOGONACK` (`Messages.h:53-60`) has **one** var item and no steam fields, so it
is fully deterministic:

```
offset 0: ushort cbmsg
offset 2: ushort fmid = 2
offset 4: ushort ib CharName_OR_FailureReason
offset 6: ushort cb CharName_OR_FailureReason
offset 8: uint32 timeServer                 (Time)
offset12: int16  shipID                     (ShipID = ObjectID = short, igc.h:477,480)
offset14: bool   fValidated                 (1 byte)
offset15: bool   fRetry                     (1 byte)
offset16: byte   cookie                     (Cookie = BYTE, igc.h:516)
offset17: ---- ends; struct padded to 20 (4-align) ----
```
Fixed size **20**. Blob (the char-name-or-failure-reason string) starts at 20.

---

## 3. Framing — the packet level, and where the CRC lives

A **packet** is a flat concatenation of messages, walked via `PfmGetNext`
(`Messages.cpp:451-471`): each message starts with `cbmsg` (so `next =
this + cbmsg`), up to `PacketSize()`.

- **Length prefix:** each message is self-describing via its `cbmsg` — there is
  no outer packet length; the socket/transport supplies the packet length.
- **Message-id:** `fmid`, the second ushort of the message header.
- **CRC:** a single **trailing `int` (4 bytes) per packet**, after the last
  message, covering **only the message bytes** (not itself).

  Send path — `Messages.cpp:529-537`:
  ```c
  crcbuff = copy of messages;
  int crc = MemoryCRC(crcbuff, cb);       // over the messages only
  *(int*)(crcbuff + cb) = crc;            // appended LE int
  pv = crcbuff; cb += sizeof(crc);        // packet size grows by 4
  ```
  Receive path — `Messages.cpp:1015-1018`:
  ```c
  int crc = MemoryCRC(m_rgbbuffInPacket, PacketSize());       // PacketSize = received_len - 4
  if (crc == *(int*)(m_rgbbuffInPacket + PacketSize())) { ... }
  ```
  `PacketSize()` is `m_dwcbPacket - sizeof(int)` (`messagecore.h:386-393`), and
  `GetBuffOutSize()` similarly subtracts `sizeof(int)` for the CRC slot. So the
  CRC is unambiguous: **compute CRC-32 (variant below) over every message byte;
  the result is the last 4 bytes of the packet**, compared for equality.

### The exact CRC variant — it is NOT the standard CRC-32 finalize

`_Utility/CRC.cpp`:
- Table is the standard reflected IEEE table (`CRC.cpp:11-92`, first values
  `0x00000000, 0x77073096, 0xee0e612c, ...`).
- Per-byte step (`CRC.cpp:98-102`):
  ```c
  return CRC32_TABLE[(BYTE)(Crc) ^ Data] ^ (Crc >> 8);
  ```
  seeded with `0xFFFFFFFF`.
- `MemoryCRC` (`CRC.cpp:162-176`) seeds `0xFFFFFFFF`, runs the step per byte,
  then **returns the raw running value — it does NOT xor with `0xFFFFFFFF` at
  the end** (despite the PKZIP/ZMODEM comment at line 107 saying it "should"
  finish by inverting). And it maps a result of `0` to `1` (never-zero guard).
- Therefore the wire CRC = `crc32_no_final_xor(data)` with `init = 0xFFFFFFFF`
  and the 0→1 guard. This is reproducible independently and we test it against
  a separately-written reference (§verification). ⚠️ Summaries/vendors that
  describe this as "plain CRC-32" are wrong about the final inversion; the C++
  wins.

---

## 4. `MSGVER 216` — where it is checked, and the mismatch behaviour

- Defined: `MessageVersion.h:21` `const int MSGVER = 216` (a full `int`).
- Client writes it into `LOGONREQ.fedsrvVer` at logon (`clintlib.cpp:1346`);
  field type is `USHORT` (`Messages.h:41`).
- Server checks on `FM_C_LOGONREQ` at **`FedSrv.CPP:5824`**:
  ```c
  if (pfmLogon->fedsrvVer == MSGVER)  { /* accepted; do auth lookup */ }
  else {
     cLoginsFailed++;
     // build reason string depending on < or > MSGVER  (lines 6057-6071)
     BEGIN_PFM_CREATE(g.fm, pfmLogonAck, S, LOGONACK)
       FM_VAR_PARM(szVerError, CB_ZTS)   // failure reason
       FM_VAR_PARM(NULL, CB_ZTS)         // second (empty) var slot
     END_PFM_CREATE
     pfmLogonAck->fValidated = false;
     pfmLogonAck->fRetry = false;
     g.fm.SendMessages(&cnxnFrom, FM_GUARANTEED, FM_FLUSH);
  }
  ```
  So a mismatch is **not** a socket drop — the server replies with a `LOGONACK`
  carrying `fValidated = false` and the failure reason string. (Note: the two
  var params here again exercise the same var-item-count laxness seen on the
  client; the second `NULL` param produces `cb = 0`, so no blob.)
- Field comparison: `fedsrvVer == 216`. Our rejection test must reproduce: a
  request with `fedsrvVer != 216` yields a LOGONACK with `fValidated = false`.
  (`fedsrvVer` is `USHORT`, so values outside 0..65535 simply cannot be encoded.)

---

## 5. Endianness and packing

- **Little-endian** everywhere. The reference is x86/x64 (`_WIN64`/`__GNUC__`
  paths in `CRC.cpp:97`). `cbmsg`, `fmid`, every `ib`/`cb` short, and every
  wider integer are little-endian. On any other host the port must byte-swap to
  LE explicitly.
- **No `#pragma pack`** on the message structs themselves. A grep of
  `src/Inc` finds `#pragma pack(push,1)` only around two inner helper structs
  (`ProbeState` `Messages.h:636`, `AsteroidState` `Messages.h:917`) that are
  not part of our framing. The message structs rely on MSVC default packing:
  each member aligned to its own size, struct aligned to its largest member.
  All offsets in §2 follow that rule.
- The two unrelated pack regions are listed here to prove we accounted for the
  only pack directives in the header — none affect the logon messages.

---

## What this port actually ships (`src/Allegiance.Core/Wire/`)

Byte-level encode/decode only — no sockets, no DirectPlay, no async, no DI,
no reflection, no new NuGet packages.

- `FedMessageHeader` — the 4-byte `cbmsg`/`fmid` base.
- `VarItem` — the `ib`/`cb` pair model.
- `WireWriter`/`WireReader` — little-endian cursor primitives + the blob
  placement algorithm that mirrors `PFedMsgCreate`.
- `FedMsgCrc` — CRC-32 with no final XOR (init `0xFFFFFFFF`, 0→1 guard).
- `LogonRequest` / `LogonAck` — concrete round-trippable messages.
- Tests under `tests/Allegiance.Core.Tests/Wire/`.

See README ("The FEDMSG wire") for the summary, what's ported, and what a bot
connection still needs.
