# P29 — FM_S_MISSIONDEF(38) + FM_S_JOINED_MISSION(95): make the client load its static core

Branch `master`, on top of `7df4573` (P28). Date 2026-08-30.

## Summary

Ported `FM_S_MISSIONDEF(38)` and `FM_S_JOINED_MISSION(95)` codecs into
`Wire/` and send both (in that order, **before** EXPORT/SET_CLUSTER/
BUY_LOADOUT_ACK) to every validated direct-connect peer, so the client's
`JOINED_MISSION` handler triggers its static-core load (`ResetStaticData` →
`LoadIGCStaticCore`) — the step that is supposed to populate the mission's
hull/part/station registries that T14-T16 proved were missing.

**Live outcome — honest negative, but with a definitive, partially-positive
result.** The client now decodes MISSIONDEF perfectly, the `JOINED_MISSION`
version gate **passes** (the previous "core files out of sync" disconnect is
gone), and **T16's exact crash is GONE**: `GetMyMission()->GetHullType(1)` now
executes cleanly instead of raising the access violation T16 pinned
(`hullTypeIGC`/`GetIbaseIGC` walk, `missionigc.h:255-258`, `igc.h:5182-5197`).
But the native `CmissionIGC::Import` faults while importing **this specific
`PCore001.igc`** at hull record #463, so the hull tables never fill,
`GetHullType(1)` returns null, the hull is never adopted, and the observer's
`ship.GetPosition()` keeps NRE-ing. **No real WOPR movement — 0/5 trials.**
The milestone is blocked by a native client's core-**import** fault on
`PCore001.igc`, which is a separate, client-side concern (see §5), not the
wire codec/ordering this task owns.

## 1. Empirically-confirmed layout (MSVC probe, not guessed)

A throwaway probe (`/tmp/layoutprobe_p29.cpp`, never added to any project)
included the real `igc.h`/`Messages.h` and used the P28
undefined-template-instantiation trick to leak `sizeof`/`offsetof` through
deliberate compile errors, compiled on the Windows VM (v145, ClintLib include
dirs + defines from `VS2022/ClintLib.vcxproj:99-100`). Verbatim compiler output:

```
error C2079: 'ProbeA::m_MP_SIZEOF' uses undefined struct 'PrintSize<252>'          // sizeof(MissionParams) = 252
error C2079: 'ProbeA::m_MP_szIGCStaticFile' uses undefined struct 'PrintSize<65>'   // offsetof(MissionParams, szIGCStaticFile)
error C2079: 'ProbeA::m_MP_szCustomMapFile' uses undefined struct 'PrintSize<78>'
error C2079: 'ProbeA::m_MP_strGamePassword' uses undefined struct 'PrintSize<91>'
error C2079: 'ProbeA::m_MP_verIGCcore' uses undefined struct 'PrintSize<120>'       // offsetof(MissionParams, verIGCcore)
error C2079: 'ProbeA::m_MP_fGoalTeamMoney' uses undefined struct 'PrintSize<116>'
error C2079: 'ProbeA::m_MP_timeStart' uses undefined struct 'PrintSize<156>'
error C2079: 'ProbeA::m_MP_fStartCountdown' uses undefined struct 'PrintSize<160>'
error C2079: 'ProbeA::m_MP_rgCivID' uses undefined struct 'PrintSize<168>'
error C2079: 'ProbeA::m_MP_nTeams' uses undefined struct 'PrintSize<218>'
error C2079: 'ProbeA::m_MP_iMinRank' uses undefined struct 'PrintSize<220>'
error C2079: 'ProbeA::m_MP_iMaxRank' uses undefined struct 'PrintSize<222>'
error C2079: 'ProbeA::m_MP_fStartingMoney' uses undefined struct 'PrintSize<200>'
error C2079: 'ProbeA::m_MP_iLives' uses undefined struct 'PrintSize<204>'
error C2079: 'ProbeA::m_MP_mmMapType' uses undefined struct 'PrintSize<206>'
error C2079: 'ProbeA::m_MP_nInvitationListID' uses undefined struct 'PrintSize<224>'
error C2079: 'ProbeA::m_MP_iMaxImbalance' uses undefined struct 'PrintSize<228>'
error C2079: 'ProbeA::m_MP_nPlayerSectorAsteroids' uses undefined struct 'PrintSize<230>'
error C2079: 'ProbeA::m_MP_nTotalMaxPlayersPerGame' uses undefined struct 'PrintSize<250>'

error C2079: 'ProbeB::m_MD_SIZEOF' uses undefined struct 'PrintSize<864>'           // sizeof(FMD_S_MISSIONDEF) = 864
error C2079: 'ProbeB::m_MD_misparms' uses undefined struct 'PrintSize<4>'          // offsetof(misparms) = 4
error C2079: 'ProbeB::m_MD_szDescription' uses undefined struct 'PrintSize<256>'
error C2079: 'ProbeB::m_MD_dwCookie' uses undefined struct 'PrintSize<460>'
error C2079: 'ProbeB::m_MD_iSideMissionOwner' uses undefined struct 'PrintSize<464>'
error C2079: 'ProbeB::m_MD_stage' uses undefined struct 'PrintSize<468>'
error C2079: 'ProbeB::m_MD_szServerName' uses undefined struct 'PrintSize<472>'
error C2079: 'ProbeB::m_MD_szServerAddr' uses undefined struct 'PrintSize<497>'
error C2079: 'ProbeB::m_MD_rgszName' uses undefined struct 'PrintSize<561>'

error C2079: 'ProbeC::m_JM_SIZEOF' uses undefined struct 'PrintSize<20>'           // sizeof(FMD_S_JOINED_MISSION) = 20
error C2079: 'ProbeC::m_JM_shipID' uses undefined struct 'PrintSize<4>'
error C2079: 'ProbeC::m_JM_dtime' uses undefined struct 'PrintSize<8>'
error C2079: 'ProbeC::m_JM_cookie' uses undefined struct 'PrintSize<12>'
error C2079: 'ProbeC::m_JM_dwCookie' uses undefined struct 'PrintSize<16>'
static_assert failed: 'P29 layout probe: read the PrintSize instantiations above'
```

Probe fully reverted (it lived only in `/tmp`); `git status`/`git diff` in
`~/Development/Allegiance` are clean at the end (only the pre-existing
untracked `BOOT_PATH.md` and the `TRANSPORT_T16.md` write-up). No tracked
changes to the C++ repo — every probe edit was applied to the sandbox VM
extract, never to `~/Development/Allegiance`.

## 2. Which fields matter, and why (grep-verified, not guessed)

`MissionParams` is a 252-byte struct (`igc.h:1109-1218`) with dozens of
`bool:1` game-rule bitfields plus floats/shorts/arrays. The join path reads
essentially none of the simulation-policy fields. Determined by tracing every
consumer reachable from the messages this task sends:

- `misparms.szIGCStaticFile` — read by `GetIGCStaticFile()`
  (`clintlib.h:608-609`), the whole point of the message. **Must be the
  extensionless base name `"PCore001"`**, NOT `"PCore001.igc"`: the client's
  `LoadIGCStaticCore` calls `UTL::getFile(name, ".igc", ...)`
  (`missionigc.cpp:2410`) which appends `.igc` (`_Utility/Utility.cpp:105-123`),
  and `UTL::SetArtPath` only guarantees a trailing `\`
  (`Utility.cpp:91-102`). Sending `"PCore001.igc"` makes the client look for
  `C:\Source\git\Artwork\PCore001.igc.igc` — caught live by the probe
  (`filename=[...PCore001.igc.igc]`, `hr=0x80004005`, `fopen=null`). **This is
  an empirical correction to the task brief**, which said the field must match
  `"PCore001.igc"`.
- `misparms.verIGCcore` — read by `GetIGCStaticVer()` (`clintlib.h:610-611`);
  `ResetStaticData` bails (and the client disconnects) if it ≠ the version word
  read from the file (`clintlib.cpp:4212-4218`). We drive it from
  `_missions[0].Core.Version` (the first int32 of the loaded core, 990051329).
- `MISSIONDEF.dwCookie` — consumed by the `MissionInfo` constructor
  (`appmsg.cpp:2416`); 0 is fine (no lobby identity needed).
- `MISSIONDEF.iSideMissionOwner`, `stage`, `szDescription`, server-name arrays,
  and all other `MissionParams` fields — zeroed. The sole
  `GetMissionParams()` consumer elsewhere in clintlib is `appmsg.cpp:2492`
  (DELPOSITIONREQ, a lobby path unreachable for a direct-connect bot). Grep
  `appmsg.cpp:2150-2428` confirms the MISSIONDEF/JOINED_MISSION flow reads only
  `szIGCStaticFile`, `verIGCcore`, `shipID`, `dwCookie`.
- `JOINED_MISSION.shipID` — used to tag the player's info (`appmsg.cpp:2198/2207`);
  set to the assigned ship id. `dtime`/`cookie` are not read in this flow
  (zeroed); `dwCookie` is the assert (escaped by `|| !m_fmLobby.IsConnected()`).

## 3. Wire codecs + where/how they're sent

- `src/Allegiance.Core/Wire/MissionDef.cs` — `FM_S_MISSIONDEF(38)`, 864 B fixed
  (header 4 + MissionParams 252 + the MISSIONDEF trailing fields), named offsets
  from the probe above. Encode zero-fills then writes `szIGCStaticFile`
  (offset 69), `verIGCcore` (offset 124), `dwCookie` (460), `iSideMissionOwner`
  (464), `stage` (468), stamps `cbmsg=864` last, and self-checks the two
  essential placements by re-reading them.
- `src/Allegiance.Core/Wire/JoinedMission.cs` — `FM_S_JOINED_MISSION(95)`,
  20 B fixed (header 4 + shipID 4 + pad 6 + dtime 8 + cookie 12 + pad 13 +
  dwCookie 16).
- Send: `src/Allegiance.Server/Server.cs`, inside the existing `if (validated)`
  block, at the very top (before EXPORT), via the central `SendPeer` to the
  owning peer only. Two messages packed in one `FedPacket.Pack`.

**Ordering rationale** (why MISSIONDEF+JOINED_MISSION must precede
EXPORT/SET_CLUSTER/BUY_LOADOUT_ACK): the `JOINED_MISSION` handler calls
`ResetStaticData(...)` (`appmsg.cpp:2162-2163`), which — because
`&m_pCoreIGC == ppStaticIGC` — runs `FlushGameState()` then `CreateMission()` +
`Initialize()` (`clintlib.cpp:4196-4200`). `FlushGameState`
(`clintlib.cpp:1253-1291`) terminates **every ship** in the current mission
(incl. any EXPORTed one) and destroys the mission. So an EXPORT/SET_CLUSTER/
BUY_LOADOUT_ACK sent before the join would be wiped by the join. MISSIONDEF
must come first because JOINED_MISSION `assert(m_pMissionInfo != NULL)`
(`appmsg.cpp:2154`) and `MissionInfo` is only created by the MISSIONDEF handler
(`appmsg.cpp:2416`). This mirrors the real game (lobby → join → ship-assign).

## 4. Test evidence

`tests/Allegiance.Core.Tests/Wire/MissionDefJoinedMissionWireTests.cs` — 11
tests: declared sizes; round-trips; **byte-exact placements at the
compiler-confirmed offsets** (header `cbmsg=864/fmid=38`; `szIGCStaticFile` at
69 = `"PCore001.igc\0"` and the real base name `"PCore001"`; `verIGCcore` at 124
= `01 FC 02 3B` for 990051329; `dwCookie` at 460; and a whole-buffer scan
proving everything else is zero — guarding against a silent pad slip); the
over-long-name truncation (keeps the NUL); rejection of wrong fmid/length;
JOINED_MISSION byte-exact 20-byte buffer + non-zero field landing + rejection.

`dotnet build`: **0 warnings / 0 errors**. `dotnet test`: **1558 passing**
(+11; was 1547).

## 5. Live proof — honest result (5 trials)

Real server (fixed code, `PCore001` extensionless), real WOPR build
(`Wopr.exe -d 192.168.0.140 6073`). Server log confirms the full join burst on
every peer, e.g.:
```
[join] peer 9 't14Bot@BOT' -> MISSIONDEF core 'PCore001' v990051329 + JOINED_MISSION ship 8104 (static core load + hull-table hydration)
```
VM-side instrumented client (diagnostic fprintf, sandbox only — see note) shows
the client decode is **perfect** and the load is correctly triggered:
```
P29JM ship=8097 staticFile=[PCore001] ver=990051329 dwCookie=0
P29RSD enter file=[PCore001] curStatic=[] missionVer=990051329
P29RSD bEncrypt(zClub)=0
P29LS  name=[PCore001] filename=[C:\Source\git\Artwork\PCore001.igc] hr=0x0 file=...
P29RSD fileVer=990051329 missionVer=990051329
P29RSD version OK -> proceeding to load
P29LF  munge=00000000 iDatasize=648110
P29IMP iter=463 type=29 size=540 datasize=520592     <- last record processed; type 29 = OT_hullType (igc.h:161)
P29BLA decodedHull=1 core=... hullCount=0 GetHullType=00000000 (T16 AV should be gone)
```

**Five trials, verbatim result — 0/5 moved:**

| trial | `Ship assigned` | `max 1s-position-delta` | T16 `GetHullType` AV |
|-------|-----------------|--------------------------|----------------------|
| 1 | yes | `0.0 units. -> no significant movement` | gone (returns null, no fault) |
| 2 | yes | `0.0 units. -> no significant movement` | gone |
| 3 | yes | `0.0 units. -> no significant movement` | gone |
| 4 | yes | `0.0 units. -> no significant movement` | gone |
| 5 | yes | `0.0 units. -> no significant movement` | gone |

In every trial `position read skipped: Object reference not set...` fills the
30 s window (the observer's `ship.GetPosition()` NREs because the native ship
never adopts a hull).

**Precise new diagnosis.** The static-core **import** is where it now stops:
`LoadIGCFile(FILE*...)` (`missionigc.cpp:94-111`) reads `iDatasize=648110`
(= 648118 − 8, consistent with the file), calls
`pMission->Import(now, -1, pData, 648110)`, and `Import`
(`missionigc.cpp:3269-3297`) walks the record stream cleanly through 463
records (types 22/23/26/24/25/30 = civs/other types, developments) and then
faults at record **#463, `type=29 = OT_hullType` (igc.h:161), size 540** — the
trace stops there, `hullCount=0`, and the load aborts (the exception is
contained by the client's `NativeClient::ExceptionFilter`, so the process
survives and the later ACK is still processed). Consequence:
`GetMyMission()->GetHullType(1)` is now reachable and executes without the T16
AV, but returns **null** because no hull was ever registered, so
`BUY_LOADOUT_ACK`'s `SetBaseHullType(null)` never gives the dummy ship a hull
and movement never happens.

This is a **native-client `ChullTypeIGC::Initialize`-side incompatibility with
`PCore001.igc`'s hull records** (the file's field layout is evidently not what
this checkout's hull-type importer expects), i.e. a client-side core-import
problem **not caused by, and not fixable from, the C# server's MISSIONDEF /
JOINED_MISSION codecs**. Per the task's explicit stop-condition ("if the native
client ALSO needs a code change to make this work — stop and report precisely,
don't force it"), P29 stops here: the wire/ordering deliverables are complete
and verified, and the milestone is blocked by this distinct, precisely-located
native import fault.

## Files changed

- `src/Allegiance.Core/Wire/MissionDef.cs` — new, FM_S_MISSIONDEF(38) codec
- `src/Allegiance.Core/Wire/JoinedMission.cs` — new, FM_S_JOINED_MISSION(95) codec
- `src/Allegiance.Server/Server.cs` — send MISSIONDEF + JOINED_MISSION first in the validated block
- `tests/Allegiance.Core.Tests/Wire/MissionDefJoinedMissionWireTests.cs` — new, 11 tests
- `README.md` — document both messages + ordering
- `P29_MISSIONDEF_JOINED_MISSION.md` — this writeup

Build clean (0 warnings / 0 errors); **1558** tests pass (+11).

**No changes to `~/Development/Allegiance` (C++):** the empirical probe was
fully reverted and the Linux repo's `git status`/`git diff` are clean (only the
pre-existing untracked `BOOT_PATH.md` plus the `TRANSPORT_T16.md` write-up).
All native-client diagnosis was done on the sandbox VM extract
(`C:\Users\domingo\Allegiance`), which retains P29 `fprintf` instrumentation;
it is not the deliverable and not a git repo. **Test-environment note:** the
brief assumed the client already had a local `PCore001.igc`; it did not, so for
the live test I deployed the server's `PCore001.igc` (byte-identical,
648118 bytes, md5 `40d28b2324824e53fbe6f7c81f563cb7`) to the VM client's
configured ArtPath (`C:\Source\git\Artwork\`) — a data-file deployment, not a
source change.
