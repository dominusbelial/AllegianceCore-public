# P124 — SIDE_TECH_CHANGE(75) / SIDE_ATTRIBUTE_CHANGE(76) / POSTER(27): disposition under this port's tech-tree-absent, no-server-trigger reality

Date: 2026-09-09
Repo: `/home/domingo/Development/AllegianceCore` (branch `master`, HEAD `96e85f8` P123)
Task: confirm, with real evidence re-read this session (not by trusting this prompt), the
disposition of the last three Phase 4 fmids this session's earlier research flagged as likely
blocked or dead — the tech-tree pair 75/76 and the server-never-sends-it `POSTER` — and close
them out cleanly one way or another.

---

## Bottom line

**All three are N/A — a pure finding, no code.** 75/76 are, by construction, broadcasts of a
side's *acquired* tech tree and its *aggregated* global attribute set; this port has no live
per-side tech/attribute tracking of any kind (only the static ICE-record blobs it parses for
corpus/IGC-dump purposes, and a `Side` runtime object that owns money and buckets but no tech
state). `POSTER`(27) is the "defined and receivable (in the native client) but never sent by
the server" flavor of dead code — and in this port there is no codec, no send site, and no
mission-authoring editor (there is no `Igc/`/`Pigs/` tree here at all) to generate posters off
the live path. There is no small, real, honest piece of code any of the three justifies.
Reported in full below, the same way the P122 trio was closed out (PLAYER_MONEY_TRANSFER_
FINDINGS_P122.md) and `SHIP_RESET`(183) before it (TELEPORT_ACK_P110.md:112-114).

---

## 1. The two load-bearing facts (both re-verified fresh)

**(a) This port has no runtime per-side tech-tree / global-attribute tracking.** The only
occurrences of `TechTreeBitMask` / `GlobalAttributeSet` in the whole `src` tree are *static
data blobs* inside the ICE-record decoders — the definitional payloads parsed for corpus
validation and IGC dumping, never interpreted, aggregated, or mutated, and never feeding any
broadcast:

- `DataCivilizationIGC` (OT_civilization, static core record): carries
  `TtbmBaseTechs` / `TtbmNoDevTechs` (the civ's two `TechTreeBitMask` blobs) and
  `GasBaseAttributes` (a 25-float `GlobalAttributeSet`) — Data/DataCivilizationIGC.cs:17-19,
  26-27, 52-54.
- `DataDevelopmentIGC` (OT_development, static core record): its `gas` field is a 25-float
  `GlobalAttributeSet` — Data/DataDevelopmentIGC.cs:9-11, 30-31.
- `Wire/CreateBuckets.cs:31-33` states outright that "the `TechTreeBitMask` / tech-tree
  infrastructure is **confirmed absent** from this port", and its codec ships the two mask
  fields as opaque bytes (MaskBytes=50, :45-47) that the join burst zero-fills (the P119
  finding, BUCKET_CONSTRUCTION_FUNDING_P119.md).
- `Sim/Ship.cs:532-536,551-555` and `Sim/Weapon.cs:26-28` explicitly record that
  `GetGlobalAttributeSet()` is *unimplemented* and the per-side `GlobalAttributeSet`-scaled
  capacities are omitted by design.

Meanwhile the runtime `Side` object — the thing native's `SIDE_TECH_CHANGE`/`SIDE_ATTRIBUTE_
CHANGE` would read (side's `GetDevelopmentTechs()`, its aggregated `GetGlobalAttributeSet()`)
— owns only `Id`, `Name`, `Civilization` (the static record), `Buckets`, `UpdateCount`,
`LastUpdate`, and the pooled `Money`: Model/Side.cs:24-87. **No tech-acquisition state, no
attribute-set accumulator.** An inbound fmid-75/76 frame therefore has nowhere to come from
(no send site — see §2) and nowhere to land (the terminal catch-all, §3). This is the same
finding this session recorded three times before — P117 (`RELAUNCH_SHIP` desired-loadout /
successor-upgrade gap), P119 (`CREATE_BUCKETS`'s zeroed tech-bitmask fields), and the
bucket-completion investigation's `OT_development`/`IbuyableIGC` entanglement — and it is now
independently re-confirmed here with the same evidence shape.

**(b) This port ships no mission-authoring editor.** The `tools/` tree is exclusively
analyzers / dumpers / observers — `cvhdump`, `igcdump`, `missiondump`, `fedmsgdump`,
`verify_{map,map_layout,part_layout,treasure_layout}.py`, `loopback`, `vizfight`,
`livecombat`, `chatdemo`, `corpusvalidate`. There is **no `Igc/` and no `Pigs/` tree in this
repo at all** (repo root holds only `src/{Allegiance.Bot,Allegiance.Core,Allegiance.Server}`,
`tests/`, `tools/`, `docs/`). So the native mission-authoring editor that might generate
posters outside the live `FedSrv` runtime has no analogue here; the only "poster" strings in
`tools/` are the static `PosterName` map field printed by `missiondump/Program.cs:96,116`
(`posters={cl.PosterName}`) and described in `verify_map_layout.py:6` (`poster@59-71`) — both
reading the cluster data record, neither emitting a `POSTER` message.

---

## 2. SIDE_TECH_CHANGE(75) / SIDE_ATTRIBUTE_CHANGE(76) — N/A

Native definitions (`src/Inc/Messages.h`, as cited in the brief; fmids confirmed at
FedMessageDumper.cs:30):
```cpp
DEFINE_FEDMSG(S, SIDE_TECH_CHANGE, 75)
    SideID          sideID;
    TechTreeBitMask ttbmDevelopments;
END_FEDMSG

DEFINE_FEDMSG(S, SIDE_ATTRIBUTE_CHANGE, 76)
    SideID              sideID;
    GlobalAttributeSet  gasAttributes;
END_FEDMSG
```
Native send site per the brief: `FedSrv/FedSrv.CPP:11291-11294` (both fields read from the
side's live development/attribute state when a development completes). Those native paths are
**not vendored in this repo** — only cited in the P-docs — so their handler detail rests on the
documented excerpt, not an in-repo re-read (same caveat P122 flagged at §2).

Port disposition: **N/A.** Both messages are S→C broadcasts of live per-side progression state
that this port does not track (§1a). Confirmed exhaustively this session:

- **No codec classes.** The `Wire/` directory (listed in full this session) contains no
  `SideTechChange.cs`, no `SideAttributeChange.cs` — unlike every ported fmid, which has one.
- **Not in the ported set.** `FedMessageCatalog.Ported` is `[1,2,4,5,7,24,78,94,99,100,103,178]`
  (FedMessageDumper.cs:54-55); 75/76 are absent, so they appear only as names in the read-only
  fmid table (FedMessageDumper.cs:30) and are dumped raw/unported.
- **No `Fmid = 75` / `Fmid = 76` constants and no send site.** A fresh `src` grep for
  `SideTechChange|SideAttributeChange|SIDE_TECH_CHANGE|SIDE_ATTRIBUTE_CHANGE|sfmSideTechChange|
  sfmSideAttributeChange` returns a single hit — the catalog name row. No code path constructs
  or sends either message.
- **Nothing to read from.** The runtime `Side` (Model/Side.cs:24-87) exposes no
  `GetDevelopmentTechs()` / `GetGlobalAttributeSet()` analogue; the only `DevelopmentTechs` /
  `InitialTechs` mentions are a comment in the join burst about native's `fsmission.cpp`
  (Server.cs:1284), not code. (Fresh grep: the only `GetDevelopmentTechs|GetInitialTechs|Add
  Development|TrackDevelopment` matches are that comment, plus `CreateBuckets.cs` referencing
  them as the native source of the bucketed mask.)

Rebuilding the tech-tree infra to make them meaningful is explicitly out of scope (stated in
the brief; consistent with the three prior confirmations). Dead-end confirmed — nothing to do.

---

## 3. POSTER(27) — N/A

Native definition (fmids confirmed FedMessageDumper.cs:22):
```cpp
DEFINE_FEDMSG(S, POSTER, 27)
  char      textureName[c_cbFileName];
  float     latitude;
  float     longitude;
  float     radius;
  SectorID  sectorID;
END_FEDMSG
```
Background from this session's own earlier research (restated, then re-verified): the native
**client** (`clintlib/appmsg.cpp`) and WOPR's C# `MessageReceiver.cs` both still carry real
receive-handling for `POSTER`, but this session's native-tree grep found **zero send sites** in
`FedSrv.CPP` — the "defined and receivable, but the server never actually sends it" flavor of
dead code (distinct from `THREAT`(131)'s fully-commented-out definition or `DrainAsteroidEvent`'s
`/* NYI */`-commented call, same practical conclusion).

Port disposition: **N/A.** Re-verified exhaustively on this repo:

- **No codec class.** `Wire/` has no `Poster.cs` (full directory listing this session). Not in
  the ported set (FedMessageDumper.cs:54-55).
- **No send symbol anywhere.** Fresh whole-repo grep for
  `pfmPoster|SendPoster|sfmPoster|Poster\.cs` returns **zero** matches; the only `POSTER` text
  in `src` is the catalog name at FedMessageDumper.cs:22.
- **No mission-authoring generator.** Per §1b, no `Igc/`/`Pigs/` editor exists in this port to
  emit posters off the live path; the only "poster" strings in `tools/` are the static
  `PosterName` map field (missiondump/Program.cs:96,116; verify_map_layout.py:6).
- The `PosterName` field on the cluster data record (Data/DataClusterIGC.cs:41,81,130) is the
  lobby's poster-texture name for a sector — a static ICE-string field the mission dump prints,
  not a `POSTER(27)` message send.

Unlike 75/76 (blocked by absent infra), `POSTER` is blocked by *absence of any server-side
trigger* — in the native game it appears the server never emits it (client + WOPR handlers are
orphaned receivers), and in this port there is no codec, no send site, and no editor that could
generate one. Both routes independently conclude N/A.

---

## 4. Why no code was written

The brief's conditional ("build only if a real, different angle justifies a small handler")
does not fire for any of the three:

- **75/76:** the only "angle" is the tech-tree / global-attribute infrastructure itself, which
  is explicitly out of scope and has now been confirmed absent four times. Building decoders
  that nothing sends or reads would be dead weight duplicating the report-and-ignore catch-all.
- **POSTER:** there is no live trigger anywhere (no send site, no editor); building a codec with
  no producer and, in this managed port, no consumer is dead weight. The native client/WOPR
  receivers are outside this repo's tree entirely.
- All three share the established unimplemented-fmid posture: an inbound 75/76/27 frame drops
  into the terminal `else` — `"[wire] from {from}: unsupported fmid {fmid} — ignored at headless
  milestone"` (Server.cs:2023-2026) — which reports and discards **by fmid, before any payload
  is read** (the same catch-all P122/SHIP_RESET record for every unported id; cf.
  Server.cs:934-944,946 where `ProcessDatagram` special-cases only `LogonRequest`).

Zero code changes ⇒ no new runtime behavior exists to test ⇒ **the existing suite passing
unmodified is the verification** (the SHIP_RESET / P122 precedent). Run this session:
`dotnet test tests/Allegiance.Core.Tests` → **Passed! — Failed: 0, Passed: 2596, Skipped: 0,
Total: 2596.** No behavior changed; this commit adds only this findings note.

---

## 5. Phase 4 — what remains genuinely open after P124

Closing out the tech-tree pair and the orphaned `POSTER`:

- **Closed N/A here (P124):** `SIDE_TECH_CHANGE`(75), `SIDE_ATTRIBUTE_CHANGE`(76) —
  no live per-side tech/attribute tracking to broadcast (infra absent, out of scope);
  `POSTER`(27) — no server-side trigger anywhere (no send site, no mission-editor in this port).
- **Still open, already scoped this session as needing their own multi-task passes:**

  1. **`STATIONS_UPDATE`(57) / `STATION_CAPTURE`(58) / `STATION_DESTROYED`(59)** — the station
     state-broadcast + capture mechanic cluster (named in P118's and P122's forward-look,
     MONEY_CHANGE_SET_MONEY_P118.md:163-175; PLAYER_MONEY_TRANSFER_FINDINGS_P122.md:198-201).
     Requires stations that can be captured/destroyed and a side-state broadcaster — a dedicated
     multi-task scope of its own, not a small task.
  2. **Bucket-completion effect** — the payoff of P119's funding/buildup: firing the completed
     bucket's consequence (ship/station construction) on the `OT_development`/`IbuyableIGC`
     object model. Found this session to be too entangled to scope as a small task (recorded in
     the bucket-completion investigation and echoed at P122:199-201 for the related
     `BUILDINGEFFECT_DESTROYED`(181)).
  3. **Purchase / loadout costing** — inherited from P118/P122 forward-look: give the buy path a
     real price and `MONEY_CHANGE` its negative-`dMoney` site (P122:195-197). Still open.

Nothing in this task alters P103–P123 code; all of it was read for context only.

---

## 6. Citations (file:line actually read this session)

- `src/Allegiance.Core/Wire/FedMessageDumper.cs:22` — fmid table `[27]="POSTER"`;
  `:30` — `[75]="SIDE_TECH_CHANGE", [76]="SIDE_ATTRIBUTE_CHANGE"`;
  `:54-55` — `Ported` set (75/76/27 absent).
- `src/Allegiance.Core/Wire/` — full directory listing this session: no `SideTechChange.cs`,
  no `SideAttributeChange.cs`, no `Poster.cs`.
- `src/Allegiance.Core/Model/Side.cs:24-87` — runtime `Side` fields: Id/Name/Civilization/
  Buckets/UpdateCount/LastUpdate/Money; **no tech-acquisition or attribute-set state**.
- `src/Allegiance.Core/Data/DataCivilizationIGC.cs:17-19,26-27,52-54` — static civ record's
  `TtbmBaseTechs`/`TtbmNoDevTechs`/`GasBaseAttributes` (opaque ICE blobs, not runtime state).
- `src/Allegiance.Core/Data/DataDevelopmentIGC.cs:9-11,30-31` — OT_development's `gas`
  GlobalAttributeSet (static record).
- `src/Allegiance.Core/Wire/CreateBuckets.cs:31-33` — "tech-tree infrastructure is confirmed
  absent" (P119); `:45-47` — `MaskBytes = 50`.
- `src/Allegiance.Core/Sim/Ship.cs:532-536,551-555`; `Sim/Weapon.cs:26-28` —
  `GetGlobalAttributeSet()` unimplemented; per-side scaled capacities omitted.
- `src/Allegiance.Core/Data/DataClusterIGC.cs:41,81,130` — `PosterName` static cluster field.
- `src/Allegiance.Server/Server.cs:2023-2026` — terminal report-and-ignore catch-all for
  unsupported fmids; `Server.cs:934-944,946` — `ProcessDatagram` special-cases only LOGONREQ;
  `Server.cs:1284` — the only `GetDevelopmentTechs()/GetInitialTechs()` mention (commentary).
- `tools/missiondump/Program.cs:96,116` — prints static `posters={cl.PosterName}` (analyzer);
  `tools/verify_map_layout.py:6` — `poster@59-71` layout (verifier). No editor/generator.
- Fresh `src` greps: `SideTechChange|SideAttributeChange|SIDE_TECH_CHANGE|SIDE_ATTRIBUTE_CHANGE|
  sfm*` → only the catalog row; `pfmPoster|SendPoster|sfmPoster|Poster\.cs` → zero; no
  `Fmid = 27/75/76` constants.
- Cross-read: PLAYER_MONEY_TRANSFER_FINDINGS_P122.md:198-201 (station cluster + bucket-completion
  forward-look); MONEY_CHANGE_SET_MONEY_P118.md:163-175 (same); TELEPORT_ACK_P110.md:112-114
  (SHIP_RESET-style N/A precedent).
- Native `FedSrv/FedSrv.CPP` / `Inc/Messages.h` are **not vendored in this repo**; the 75/76
  send-site line (:11291-11294) is taken from the brief's cited excerpt, flagged in §2, not
  asserted as an in-repo re-read.

---

Session stats logged via
`python3 ~/Development/allegiance-tools/session_stats.py 20260909_224517_449762`.
No code changed; this commit adds only this findings note (P122/SHIP_RESET report-only precedent).
