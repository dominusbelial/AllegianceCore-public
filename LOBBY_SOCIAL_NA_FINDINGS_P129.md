# P129 — MUTE(117) / STATIC_MAP_INFO(11) / ICQ_CHAT_ACK(107): disposition under this port's lobby-free, database-free, standalone-shaped architecture

Date: 2026-09-10
Repo: `/home/domingo/Development/AllegianceCore` (branch `master`, HEAD `d3d0fc8` P128)
Task: confirm, with real citations re-read this session (not by trusting this prompt), the
disposition of three Phase 6 fmids flagged as likely N/A for this port's standalone,
direct-connect architecture (no lobby/mission browser, no account database).

---

## Bottom line

**All three are N/A — a pure finding, no code.** `MUTE`(117) is a purely client-local chat
filter the server never sends or receives; `STATIC_MAP_INFO`(11) is the lobby's SQL-backed
map/mission-template catalog broadcast, and this port has no lobby or map catalog anywhere;
`ICQ_CHAT_ACK`(107) is gated in native behind `#if !defined(ALLSRV_STANDALONE)` — the
reference code itself turns it off for exactly this build shape (standalone, no lobby, no
SQL account DB) — which is precisely this port's shape. Native citations were verified
directly against the sibling native checkout this session (`/home/domingo/Development/Allegiance/`,
per FEDMSG.md's provenance note), not taken from the brief. Reported in full below, the same
way P122 (PLAYER_MONEY_TRANSFER_FINDINGS_P122.md) and P124 (SIDE_TECH_POSTER_FINDINGS_P124.md)
closed out their Phase 4/5 trios, and `SHIP_RESET`(183) before them.

---

## 1. The two load-bearing architectural facts (both re-verified fresh)

**(a) This port has no lobby / mission-browser / map-catalog concept anywhere.**
`STATIC_MAP_INFO`'s sole purpose is to hand the client the lobby's browsable list of playable
maps/missions. In this port the only textual presence of anything lobby-like in the whole
tree is the fmid *name* `LOCK_LOBBY`(43) inside the read-only catalog table
(FedMessageDumper.cs:25) — an id, not code; `LOCK_LOBBY` is absent from the ported set and
from the dispatch chain. There is no `JoinSquad`/`JoinMission`/game-creation path, no
`Igc`/`Pigs` mission-authoring tree, and the port's greeting/connection path is pure
direct-connect (LOGONREQ→LOGONACK; the dispatch special-cases only `LogonRequest`,
Server.cs:1827 region). The native standalone build reads maps from a file instead of SQL
(this session, `FedSrv.CPP:189-230`: the `#else` branch of `LoadStaticMapInfo` loads
`<artwork>\maps.txt`) — but even that file-backed catalogue is a lobby-facing feature this
port has no counterpart for: there is no lobby, no game browser, no `maps.txt` reader, no
`StaticMapInfo` struct, no `MapInfo` message.

**(b) This port has no database / SQL / account layer at all.** Both `STATIC_MAP_INFO`
(native SQL queries `GetStaticMapCount`/`GetStaticMaps`, `FedSrv.CPP:191-209`) and
`ICQ_CHAT_ACK` (native SQL `GetICQID` account lookup, `FedSrv.CPP:5267-5276`) are SQL-backed
in native. A fresh whole-repo sweep of this port's `src/` for any database primitive
(`SqlConnection|Npgsql|MySql|System.Data|SQLSTRCPY|account|database`) returns **zero**
matches; the only `Db` identifier in the tree is `ChatBuoy.Db` (a data-record property,
Server.cs:1845), which is a chat-buoy record struct, not a database. Combined with (a),
this is the standalone-equivalent shape native's own `ALLSRV_STANDALONE` preprocessor guards
exist to disable.

---

## 2. MUTE(117) — N/A (pure client-local chat filter; server never touches it)

Native definition (verified in-repo this session):
```cpp
// src/Inc/Messages.h:762-765
DEFINE_FEDMSG(C, MUTE, 117)
    ShipID  shipID;
    bool    bMute;
END_FEDMSG
```
Port definition: fmid name only, FedMessageDumper.cs:39 (`[117]="MUTE"`).

Native disposition (verified): **zero send/receive sites in `FedSrv.CPP`** — a fresh grep
of the entire native `src/` for `MUTE` returns only the `Messages.h:762` definition and the
WOPR auto-generated interop plumbing (`Wopr/AllegianceInterop/AllMessages.h:5664-5681`,
`Wopr/.../MessageType.cs:104`, `MessageReceiver.cs:834-842`) — all C→S message-type
registrations, none in the server itself. `MUTE` is a **client→server declaration** ("please
mute ship X in my UI") that, in native, the server dispatch never acts on; the muting is a
purely client-local chat-UI filter the server never needs to know about. Native's chat code
confirms muting is a client-side concern: `FedSrv.CPP`'s `ForwardChatMessage` has no mute
gate (there is no server mute list), and WOPR merely reflects the frame for logging.

Port disposition: **N/A.** Re-verified exhaustively on this repo:
- **No codec class.** The `Wire/` directory (listed in full this session, 86 files) has no
  `Mute.cs` — unlike every ported fmid, which has one.
- **Not in the ported set.** `FedMessageCatalog.Ported` is
  `[1,2,4,5,7,24,78,94,99,100,103,178]` (FedMessageDumper.cs:54-56); 117 absent.
- **No `Fmid = 117` constant, no send/receive site.** Whole-repo grep for
  `MUTE|bMute|FM_C_MUTE` yields only the catalog name row plus two *comments* in the chat
  router that affirm the absence: Server.cs:2232 ("the 'NoFilter' distinction is about
  bypassing mute/ignore lists") and Server.cs:2267 ("a distinction that can't exist yet (no
  mute/ignore lists)") — i.e. this port's chat machinery explicitly documents there are no
  mute/ignore lists to act on.
- Even if a client sent a `MUTE` frame, there is nothing for the server to do: no server-side
  filter exists, and forwarding it back to the sender is meaningless. It drops harmlessly
  into the terminal report-and-ignore catch-all (§5).

Dead-end confirmed — the "feature" is the client's UI state, invisible to this standalone
server by construction. Nothing to do.

---

## 3. STATIC_MAP_INFO(11) — N/A (lobby's map/mission-template catalog; no lobby here)

Native definition (verified in-repo):
```cpp
// src/Inc/Messages.h:127-130
DEFINE_FEDMSG(S, STATIC_MAP_INFO, 11)
    FM_VAR_ITEM(maps)
END_FEDMSG
```
Port definition: fmid name only, FedMessageDumper.cs:18 (`[11]="STATIC_MAP_INFO"`).

Native disposition (verified): `LoadStaticMapInfo` (`FedSrv.CPP:189`) populates
`g.vStaticMapInfo[]` from a **SQL-backed map database** — `GetStaticMapCount`/
`GetStaticMaps` cursors filling `cbIGCFile`/`cbFriendlyName`/`nNumTeams` per row via
`SQLSTRCPY`/`GetStaticMaps_FileName`/`GetStaticMaps_MapName`/`GetStaticMaps_NumTeams`
(`FedSrv.CPP:191-209`; the standalone `#else` reads `<artwork>\maps.txt`, `:211-230`). It is
called once during server bring-up (`FedSrv.CPP:9350`), and the resulting catalog is handed
to a connecting player on the join path via `BEGIN_PFM_CREATE(g.fm, pfmStaticMapInfo, S,
STATIC_MAP_INFO)` (`FedSrv.CPP:993`) — i.e. it is the **mission-creation lobby browser's
map list**, delivered so the client can populate its "choose a game/map to play" screen.

Port disposition: **N/A.** Re-verified exhaustively on this repo:
- **No codec class, no data struct.** `Wire/` has no `StaticMapInfo.cs`; there is no
  `StaticMapInfo` struct anywhere (whole-tree grep for
  `STATIC_MAP_INFO|LoadStaticMapInfo|GetStaticMaps|StaticMapInfo` hits only the catalog row).
- **Not in the ported set** (FedMessageDumper.cs:54-56).
- **No lobby / mission browser to feed** (§1a): the only lobby-ish text in the whole port is
  the `LOCK_LOBBY`(43) *name* in the read-only catalog (FedMessageDumper.cs:25); there is no
  map catalog, no `maps.txt` reader, no `JoinSquad`/game-creation path, and no map/mission
  database (§1b) to seed the catalog from. This port's connection model is direct-connect to
  a single already-running mission — there is no "pick a map from the lobby" step at all.

Building a decoder for a message with no producer (no lobby server to compile a map list), no
consumer (no mission-browser UI in a standalone direct-connect port), and no data source (no
map DB / file catalogue) would be dead weight duplicating the report-and-ignore catch-all.
Out of scope per the brief, and architecturally impossible to make meaningful here. Dead-end
confirmed — nothing to do.

---

## 4. ICQ_CHAT_ACK(107) — N/A (native itself disables it for this build shape)

Native definition (verified in-repo):
```cpp
// src/Inc/Messages.h:710-714
DEFINE_FEDMSG(S, ICQ_CHAT_ACK, 107)   // mirrored back by server with icqid when chat starts with "icq,"
    FM_VAR_ITEM(Message)
    int         icqid;
END_FEDMSG
```
Port definition: fmid name only, FedMessageDumper.cs:36 (`[107]="ICQ_CHAT_ACK"`).

Native handler (verified in-repo, read in full this session, `FedSrv.CPP:5250-5276`): inside
the chat forward path, a block detects messages beginning `"icq,playername:"`, looks up the
recipient's numeric ICQ id via the **SQL account query** `GetICQID`
(`FedSrv.CPP:5267-5276`: `lstrcpyn(GetICQID_CharName, szName…)`, `SQL_GO(GetICQID)`,
`SQL_GETROW(GetICQID)`, `GetICQID_ID`) and, on success, replies with a `ICQ_CHAT_ACK`
carrying the resolved `icqid`. Critically, the **entire block is wrapped in
`#if !defined(ALLSRV_STANDALONE)`** (`FedSrv.CPP:5255`), matching the brief: native itself
compiles this feature out of the standalone server build. The feature is doubly unavailable
to a standalone build — compiled out *and* dependent on an SQL account DB it wouldn't have.

Port disposition: **N/A.** Re-verified exhaustively on this repo:
- **No codec class.** `Wire/` has no `IcqChatAck.cs`; not in the ported set
  (FedMessageDumper.cs:54-56). Whole-tree grep for `ICQ_CHAT_ACK|GetICQID|icqid|ICQ` hits only
  the catalog row.
- **No database layer** (§1b) to resolve an ICQ id against — the SQL `GetICQID` table that
  this feature exists to read simply cannot exist in this port.
- **This port is the ALLSRV_STANDALONE-equivalent case.** Standalone native servers are the
  direct-connect, no-lobby, no-SQL-account variant (native sets `PublicLobby(false)` in
  standalone mode, `FedSrv.CPP:9327-9340`) — the exact shape this port is (§1). The reference
  code's own `#if !defined(ALLSRV_STANDALONE)` is the strongest native-sourced N/A signal in
  this arc: native disables ICQ chat exactly for this build shape.

Even in a full-build native the feature is cosmetic (old-style ICQ chat interop), and in this
managed port there is no codec, no send site, no account DB, and — per the guard — the
reference architecture intentionally omits it here. Dead-end confirmed — nothing to do.

---

## 5. Why no code was written

The brief's conditional ("build only if a genuinely separate, small, server-relevant angle
exists") does not fire for any of the three:

- **MUTE:** the "angle" is a client-side UI preference with no server counterpart; there is
  literally nothing for a standalone server to do with it, and the port's chat router already
  documents "no mute/ignore lists" (Server.cs:2232,2267).
- **STATIC_MAP_INFO:** there is no lobby, no map catalog, and no data source to seed one; the
  only angle would be building the entire lobby/map-browser subsystem, which is explicitly
  out of scope and confirmed architecturally absent (§1).
- **ICQ_CHAT_ACK:** native compiles it out for this build shape and it depends on an account
  DB this port lacks; no small honest piece of code is justified.

All three share the established unimplemented-fmid posture: an inbound 117/11/107 frame drops
into the terminal `else` — `"[wire] from {from}: unsupported fmid {fmid} — ignored at
headless milestone"` (Server.cs:2152) — which reports and discards **by fmid, before any
payload is read** (the same catch-all every prior findings task recorded; cf. the
special-cased-`LogonRequest` path noted in §1a).

Zero code changes ⇒ no new runtime behavior exists to test ⇒ **the existing suite passing
unmodified is the verification** (the SHIP_RESET / P122 / P124 precedent). Run this session:
`dotnet test tests/Allegiance.Core.Tests` → **Passed! — Failed: 0, Passed: 2633, Skipped: 0,
Total: 2633.** No behavior changed; this commit adds only this findings note.

---

## 6. Phase 6 — a genuinely tractable next item

Closing out the three lobby/social fmids surfaces the natural successor in the same native
join-path block: **`RANK_INFO`(13)**, the S→C broadcast the native server sends immediately
after `STATIC_MAP_INFO` in the connection burst (`FedSrv.CPP:993-1001` builds the map-info
frame, then the rank-info frame in the same block). This is the concrete, genuinely
tractable next Phase 6 item this session's native reads surface — with one honest hook:

**Why it is tractable (unlike 11/107):**
- **Standalone-enabled in native.** `LoadRankInfo` is the *companion* function to
  `LoadStaticMapInfo` but, crucially, it is **not** compiled out under `ALLSRV_STANDALONE` —
  it has a real `#else` branch that loads `<artwork>\ranks.txt` when SQL is unavailable
  (`FedSrv.CPP:103-150`: SQL `GetRankCount`/`GetRanks` path at `:106-128`, `ranks.txt`
  fallback at `:130-148`). Compare ICQ_CHAT_ACK, whose block is wrapped in
  `#if !defined(ALLSRV_STANDALONE)` with no `#else` (`FedSrv.CPP:5255`) — i.e. ICQ is fully
  removed, while rank info is deliberately preserved for exactly this port's build shape.
- **Static config, not live game state.** `RANK_INFO`'s payload is an array of `RankInfo`
  records (`civ`, `rank`, `requiredRanking`, `RankName` — `Messages.h:139-146`) read from a
  boot-time table, not derived from live per-side simulation state (contrast SIDE_TECH_
  CHANGE 75/76, which P124 closed as N/A precisely because it needs live tracked state).
  Structurally it matches the port's proven small codec+broadcast pattern — the same shape
  as `SET_BRIEFING_TEXT`(121), P121 — with no wing model, no team-leader election, and no
  alliance system required.
- **No database, no lobby.** Like maps, rank lists are served to the standalone build from a
  flat file, so porting it needs no SQL layer and no lobby browser — it is a per-connecting-
  player send in the join burst.

**The one honest hook (must be resolved at task time):** this port's boot path currently
*stubs* rank-list loading — `Server.cs:272` prints "(stub) cores.txt / ranks.txt / maps.txt
NOT loaded — download & rank lists are out of scope". So porting `RANK_INFO` is genuinely
tractable *provided* the task is scoped to include seeding a minimal rank table (analogous to
how the port already embeds/loads a static core) rather than only the codec. That decision —
make `RANK_INFO` real with a small embedded/loaded rank set, versus formally declare it N/A
like 11/107 — is exactly the kind of well-bounded call this arc has handled cleanly before. It
is the strongest genuinely-open Phase 6 candidate my research surfaced: the direct sibling of
the three just closed, preserved-by-native for this exact build shape, and free of every
infra blocker that makes 11/107 N/A.

(The remaining chat-social targets — Leaders / Ship / Allies / Wing — are *not* tractable
without building team-leader election, a docking/carrier model, an alliance system, or a wing
model respectively; the README's deferred list at README.md:892-926 states each reason from
native `ForwardChatMessage`. Purchase/loadout costing remains open too, but it is an
economy-cluster item, not Phase 6.)

Nothing in this task alters P103–P128 code; all of it was read for context only.

---

## 7. Citations (file:line actually read this session)

Port tree (`/home/domingo/Development/AllegianceCore`):
- `src/Allegiance.Core/Wire/FedMessageDumper.cs:18` — fmid table `[11]="STATIC_MAP_INFO"`;
  `:36` — `[107]="ICQ_CHAT_ACK"`; `:39` — `[117]="MUTE"`; `:54-56` — `Ported` set
  (11/107/117 absent); `:25` — `[43]="LOCK_LOBBY"` (name only).
- `src/Allegiance.Core/Wire/` — full directory listing this session (86 files): no
  `Mute.cs`, no `StaticMapInfo.cs`, no `IcqChatAck.cs`; codec presence confirmed for every
  ported fmid.
- `src/Allegiance.Server/Server.cs:2152` — terminal report-and-ignore catch-all for
  unsupported fmids; `:2232,:2267` — chat router comments explicitly state there are
  "no mute/ignore lists"; `:1845` — `ChatBuoy.Db` is a data-record property, not a database;
  dispatch special-cases only `LogonRequest` (§1a).
- Fresh whole-repo sweeps (all `src/`): `MUTE|bMute` → catalog row + 2 chat-router comments
  only; `STATIC_MAP_INFO|LoadStaticMapInfo|GetStaticMaps|StaticMapInfo` → catalog row only;
  `ICQ_CHAT_ACK|GetICQID|icqid|ICQ` → catalog row only; database primitives
  (`SqlConnection|Npgsql|MySql|System.Data|SQLSTRCPY|account|database`) → **zero** matches.

Native tree (verified directly this session at `/home/domingo/Development/Allegiance/`,
provenance per FEDMSG.md — vendored read-only sibling checkout, not just the brief):
- `src/Inc/Messages.h:762-765` — `DEFINE_FEDMSG(C, MUTE, 117)` (`shipID`,`bMute`);
  `:710-714` — `DEFINE_FEDMSG(S, ICQ_CHAT_ACK, 107)` (`Message`,`icqid`);
  `:127-130` — `DEFINE_FEDMSG(S, STATIC_MAP_INFO, 11)` (`FM_VAR_ITEM(maps)`).
- `src/FedSrv/FedSrv.CPP:189-230` — `LoadStaticMapInfo` (SQL `GetStaticMapCount`/
  `GetStaticMaps` at :191-209; standalone `maps.txt` `#else` at :211-230);
  `:993` — `STATIC_MAP_INFO` send on join path; `:9350` — called at bring-up;
  `:997-1001` — `RANK_INFO` built in same join-path block; `:9347` — `LoadRankInfo()` runs
  unconditionally; `:9327-9340` — standalone sets `PublicLobby(false)`.
- `src/FedSrv/FedSrv.CPP:5250-5276` — ICQ block read in full; `:5255` —
  `#if !defined(ALLSRV_STANDALONE)` wrapping the entire `"icq,"` detection +
  `GetICQID` SQL lookup + `ICQ_CHAT_ACK` reply.
- Native MUTE sweep: zero `FedSrv.CPP` sites; only `Messages.h:762` + WOPR interop
  (`Wopr/AllegianceInterop/AllMessages.h:5664-5681`, `MessageType.cs:104`,
  `MessageReceiver.cs:834-842`).
- `src/FedSrv/FedSrv.CPP:103-150` — `LoadRankInfo` standalone `#else` branch (reads
  `<artwork>\ranks.txt`) vs SQL path — **not** compiled out under `ALLSRV_STANDALONE`;
  `FedSrv.CPP:993-1001` — `STATIC_MAP_INFO` then `RANK_INFO` built in the same join-path block;
  `src/Inc/Messages.h:139-146` — `RankInfo` struct (`civ`/`rank`/`requiredRanking`/`RankName`);
  `Messages.h:148-150` — `DEFINE_FEDMSG(S, RANK_INFO, 13)`.
- `src/Allegiance.Server/Server.cs:269-274` — boot stub: "cores.txt / ranks.txt / maps.txt
  NOT loaded — download & rank lists are out of scope" (the RANK_INFO data-source hook, §6).
- `README.md:892-926` — deferred `ChatTarget`s (Leaders/Ship/Allies/Wing) each blocked on
  absent infra (team-leader election, docking model, alliance system, wing model);
  `docs/wire.md:62` — "lobby / encryption / CD-key scrambling — out of scope by design".
- Cross-read: PLAYER_MONEY_TRANSFER_FINDINGS_P122.md (P122 trio precedent);
  SIDE_TECH_POSTER_FINDINGS_P124.md:13-23,71-177 (P124 trio + N/A precedent &
  `Ported`-set method); TELEPORT_ACK_P110.md:112-114 (SHIP_RESET N/A precedent);
  SET_BRIEFING_TEXT_P121.md:104-121 (the small-codec+broadcast precedent RANK_INFO would
  follow; its forward-look flagged SET_WINGID/GAIN_FLAG, both of which this session's
  README read shows are blocked on absent wing/flag models).

---

Session stats logged via
`python3 ~/Development/allegiance-tools/session_stats.py 20260910_013310_b0ff41`.
No code changed; this commit adds only this findings note (P122/SHIP_RESET report-only precedent).
