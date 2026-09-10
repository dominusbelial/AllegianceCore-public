# P130 — RANK_INFO(13): join-sync rank table (real `ranks.txt`, standalone-faithful)

Date: 2026-09-10
Repo: `/home/domingo/Development/AllegianceCore` (branch `master`, HEAD `20221cd` P129 at start)
Task: port `RANK_INFO`(13) — the per-player join-sync rank-name-table message — loading the **real**
`ranks.txt` corpus and sending it once per joining player at the port's join-sync burst.

---

## Bottom line

`RANK_INFO`(13) is now a real, byte-faithful join-sync message in this port. The server loads the
**actual** `/home/domingo/Development/Artwork/ranks.txt` corpus at boot (via the same single configured
`ArtPath` root the static core, maps, and P116's `.cvh` corpus already use) and sends it once to each
joining player's own connection in the same join-sync burst as `SET_MONEY`/`CREATE_BUCKETS`/
`SET_BRIEFING_TEXT`. Every entry carries `civ = -1` and `requiredRanking = 0` — **not an invented
simplification**: that is exactly native's own standalone `LoadRankInfo` `#else` branch
(`FedSrv.CPP:140-141`). `cores.txt`/`maps.txt` remain stubbed (out of scope, named below). Test count
2633 → **2645** (+12), 3 consecutive clean full-suite runs after rerunning past the one documented
pre-existing timing flake.

---

## 1. The real file, read directly (not assumed)

`/home/domingo/Development/Artwork/ranks.txt` (489 bytes, read in full): line 1 is the count header
`40`, then `<rank>,<name>` lines, one per rank. Verified: the file has **41 newlines / 42 splitlines**,
i.e. 41 entry lines (ranks 0..40) after the `40` header — and native reads **exactly the header count**
(`fscanf("%d\n",&nbranks)` then `for i<nbranks`), so the final `40,Master 9` line (a duplicate of rank
39's name, with no trailing newline) is **dead data native never consumes**. This port reproduces that
exactly: `FromRanksFile` (Wire/RankInfo.cs:134) loops `count` times and stops, so the loaded table is
the 40 real entries ranks 0..39, ending `39,Master 9`. The parser also reproduces native's
malformed-line tolerance (`continue` on a line without a comma or an unparsable rank) and its missing-
file fallback (`else g.cRankInfo = 0`, FedSrv.CPP:145-146 → empty table).

Confirmed format match against the cited native parse (`FedSrv.CPP:131-142`): count header, then
`<rank>,<name>` per line — yes, byte-for-byte the file's shape.

## 2. Native citation (re-read this session)

- **Message**: `src/Inc/Messages.h:148-150` — `DEFINE_FEDMSG(S, RANK_INFO, 13) FM_VAR_ITEM(ranks);
  short cRanks;`. Element struct `Messages.h:139-145`: `int requiredRanking; CivID civ; RankID rank;
  char RankName[c_cbName];`.
- **Types**: `CivID` = `short` (ClubMessages.h:21), `RankID` = `short` (ClubMessages.h:25),
  `c_cbName` = `c_cbNameDB + 1` = `24 + 1` = **25** (`_Utility/Utility.h:60,64`).
- **Standalone branch** (the exact build shape this port matches — **not** compiled out under
  `ALLSRV_STANDALONE`): `FedSrv.CPP:125-148`, the `#else` of `LoadRankInfo` — reads
  `<artwork>/ranks.txt` via `UTL::getFile("ranks",".txt",…)`, `fscanf("%d\n",&nbranks)`, then per line
  `fscanf("%d,%[^,\n]\n",&rank, name)` with **`g.vRankInfo[i].requiredRanking = 0;`
  `g.vRankInfo[i].civ = -1;`** (FedSrv.CPP:140-141), `else g.cRankInfo = 0;` (:145-146).
- **Send site / targeting**: the frame is built right after `LOGONACK` on the valid-connect path
  (`FedSrv.CPP:998-1001`: `FM_VAR_PARM(g.vRankInfo, sizeof(RankInfo)*g.cRankInfo)` then
  `pfmRankInfo->cRanks = g.cRankInfo`), `LoadRankInfo()` having run unconditionally at bring-up
  (`FedSrv.CPP:9347`). Client decode memcpy's `cRanks * sizeof(RankInfo)` bytes from the var item
  (`clintlib/appmsg.cpp:3377-3381`).
- **`sizeof(RankInfo)`**: RankInfo (Messages.h:139-145) is **outside** the file's `#pragma pack(1)`
  regions (those are Messages.h:636 and :917), so default MSVC packing applies: 4+2+2+25 = 33 field
  bytes, aligned up to the leading `int`'s 4 → **sizeof = 36** (3 trailing pad bytes per element).
  This stride is authoritative: both native's send (`sizeof(RankInfo)*cRankInfo`) and the client's
  memcpy depend on it, so the codec pins `RankInfoEntry.Size = 36`.

## 3. Port implementation

- **Codec** `src/Allegiance.Core/Wire/RankInfo.cs` — `RankInfoEntry` (the 36-byte record:
  `i32 requiredRanking @0, i16 civ @4, i16 rank @6, char RankName[25] @8, 3 pad @33`) and `RankInfo`
  (the message: `fmid 13`, fixed region 10 B = header(4)+ib/cb var pair(4)+`cRanks`(2), blob of
  `cRanks × 36` appended — the identical FM_VAR_ITEM-array shape `TreasureSets` already ports,
  TreasureSets.cs:96-174). Plus `RankInfo.FromRanksFile(path)` (Wire/RankInfo.cs:134) for the faithful
  standalone parser, and `Encode`/`Decode`.
- **Boot load** `src/Allegiance.Server/Server.cs` — new field `_rankTable` (:39), loaded in `Boot`
  right after `StubFileLists()` (:237) via `LoadRankTable()` (:289) using the **existing** art-root
  path convention (`Path.Combine(_config.ArtPath, "ranks.txt")` — the same single configured `ArtPath`
  the static core `:251`, `InstantiateGames` `.cvh`/map `:344/:356`, and P116 use; no new
  path-resolution mechanism invented).
- **Stub message updated** `Server.cs:275-276` — now names only `cores.txt / maps.txt` as stubbed
  (still deferred), and states the rank list **is** loaded from `ranks.txt`. `cores.txt`/`maps.txt`
  are untouched and remain explicitly out of scope (per the brief), as does any SQL/database rank path
  (confirmed N/A for this standalone shape by P129) and `STATIC_MAP_INFO`(11)/`ICQ_CHAT_ACK`(107)/
  `MUTE`(117).
- **Send at join-sync** `Server.cs:1478-1486` — placed with the join-sync burst but **outside** the
  side-scoped block (ranks are server-global static boot config, not side-scoped; same treatment as
  `TREASURE_SETS`, which sits outside the side block for the same reason — cf. `Server.cs:1454-1470`).
  One `RANK_INFO` is sent **once per joining player, to that player's own connection only** — not
  broadcast repeatedly, not whole-mission-wide.

### Join-sync hook point (confirmed)

The same per-player join-sync point P118/P119/P121 hook — the logon/assign handler region around
`Server.cs:1364-1450` (the `if (live.Ship.SideId is { } setMoneySideId)` block for `SET_MONEY`/`
CREATE_BUCKETS`/`BUCKET_STATUS`/`SET_BRIEFING_TEXT`) — with `RANK_INFO` emitted alongside it
(immediately before the `TREASURE_SETS` block) as the global payload. Confirmed by the live test: each
joiner gets exactly one.

## 4. Verification

- **Codec tests** `tests/Allegiance.Core.Tests/Wire/RankInfoWireTests.cs` (11): round-trip real-corpus
  shape, empty table, byte-exact single-entry frame (46 B = 10 fixed + 36 blob, `ib=10 cb=36 cRanks=1`,
  `civ=FFFF`, 25-wide NUL-padded name + 3 pad), 25-char name truncation, wrong-fmid / short-buffer /
  cbmsg-mismatch rejection, loader fidelity (temp file with a header+dead-line exactly like the real
  file), missing-file → empty, unparseable header → empty.
- **Live loopback test** `tests/Allegiance.Core.Tests/LiveRankTests.cs` (1): real in-process server +
  2 real TCP bots, each on a different side. Each bot receives **exactly one** `RANK_INFO` (per-player,
  not broadcast). The table is compared against the real file read independently: **40 entries** in
  file order, rank 0 `Newbie` first, rank 39 `Master 9` last, and `civ = -1` / `requiredRanking = 0`
  asserted for **every** entry — matching native's standalone branch, not a simplification.
- **Suite**: 2633 (P129 baseline) → **2645** (+12). Runs: #1 had 1 failure — the **pre-existing**
  `LiveChatSectorWireTests` timing flake (documented in memory and the brief; unrelated to P130 — my 12
  new tests were all among the 2644 that passed); reran to **3 consecutive clean full-suite runs:
  2645/2645, 2645/2645, 2645/2645**.

## 5. Next Phase 6 item (surfaced by this task's reads)

The immediate Messages.h neighbor after `RANK_INFO` is **`SET_START_TIME`(14)** (`Messages.h:153-155`,
a single `Time timeStart` field, `Time` = 8-byte double-backed class, `zlib/ztime.h:20`) — a trivial
fixed codec. **Honest hook, same shape as P129's:** its native emissions are the **auto-restart /
victory-timing** path (`fsmission.cpp:3544`: forwarded game-wide when an auto-restartable game resets
its start time), not the per-player join burst — so implementing it faithfully requires knowing whether
this port models mission start-time / auto-restart state, which (like the tech-tree finding P117) it
most likely does not. It is the concrete next numeric sibling, tractable only if that start/restart
lifecycle is scoped in. The chat-social Phase 6 targets (Leaders/Ship/Allies/Wing) remain blocked on
absent infra (team-leader election, docking model, alliance system, wing model — README.md:892-926),
per the P129 findings.

## 6. Citations (file:line read this session)

Port tree (`/home/domingo/Development/AllegianceCore`):
- `src/Allegiance.Core/Wire/RankInfo.cs:41` — `RankInfoEntry.Size = 36`; `:107` — `Fmid = 13`;
  `:111` — `FixedSize = 10`; `:134` — `FromRanksFile`; `:67-105` — layout docs.
- `src/Allegiance.Server/Server.cs:39` — `_rankTable` field; `:236-237` — Boot loads it; `:275-276` —
  stub message (cores/maps only); `:289-294` — `LoadRankTable` (art-path convention, cf. `:251/:344/:356`);
  `:1478-1486` — join-sync send (once per joining player); `:1454-1470` — `TREASURE_SETS` outside-side-
  block precedent.
- `src/Allegiance.Bot/Bot.cs` — `RankInfos` capture (field `:70`, property, dispatch case after
  `SetBriefingText.Fmid`).
- `tests/.../Wire/RankInfoWireTests.cs`, `tests/.../LiveRankTests.cs` — new tests (12).
- `src/Allegiance.Core/Wire/FedMessageDumper.cs:19` — `[13]="RANK_INFO"` (name already present);
  `:54-56` — `Ported` left unchanged (consistent with the recent ported P-messages, e.g. `SET_BRIEFING_TEXT`,
  not being added to `Ported`).

Native tree (verified at `/home/domingo/Development/Allegiance/`, vendored read-only sibling per
FEDMSG.md):
- `src/Inc/Messages.h:139-145` — `struct RankInfo`; `:148-150` — `DEFINE_FEDMSG(S, RANK_INFO, 13)`;
  `:153-155` — `SET_START_TIME(14)`; `:636/:917` — the file's only `#pragma pack(1)` regions (RankInfo
  outside both).
- `_Utility/Utility.h:60,64` — `c_cbNameDB = 24`, `c_cbName = 25`.
- `Inc/ClubMessages.h:21` — `typedef short CivID`; `:25` — `typedef short RankID`.
- `src/FedSrv/FedSrv.CPP:103-148` — `LoadRankInfo`; `:125-148` — standalone `#else` (ranks.txt);
  `:140-141` — `requiredRanking=0`, `civ=-1`; `:145-146` — missing-file → `cRankInfo=0`; `:9347` —
  `LoadRankInfo()` unconditional; `:998-1001` — `RANK_INFO` built after `LOGONACK` (sizeof×count blob +
  `cRanks`).
- `clintlib/appmsg.cpp:3377-3381` — client memcpy decode (`cRanks * sizeof(RankInfo)`).
- `zlib/ztime.h:20` — `class Time` (8-byte double-backed; for the SET_START_TIME note).
- `FedSrv/fsmission.cpp:3544` — `SET_START_TIME` sent on the auto-restart/victory timing path (why it is
  not a clean per-player join item).

`/home/domingo/Development/Artwork/ranks.txt` — read in full (489 B, 41 newlines/42 lines): header
`40`, entries 0..40 (rank 39 `Master 9`, trailing dead duplicate `40,Master 9`), count-header-governed
loader loads 40.

Session stats logged via
`python3 ~/Development/allegiance-tools/session_stats.py <session_id>` (logged at commit).
