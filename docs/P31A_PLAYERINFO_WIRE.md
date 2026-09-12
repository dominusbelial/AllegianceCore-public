# P31a — FM_S_PLAYERINFO(10) on the wire: codec + send, empirically byte-verified, one live trial

Date: 2026-08-31. Branch: `master`. Base: `07d20fb` (P30). Status: **done** —
the wire codec is in, the message is sent, and one live trial proved the
client receives and decodes it (better than the minimum the brief allowed: the
side it carries was actually assigned to the client's ship). A follow-on
framing symptom (`HACKER??` loop) is recorded at the bottom and handed to
P31b along with the assert-ambiguity question.

> **Scope note / prior-work reconciliation.** On arrival the working tree
> already contained uncommitted P31-era work (a PLAYERINFO codec, Server.cs
> wiring, a MissionDef `nTeams`/`rgCivID` extension, a PING echo, and a second
> MISSIONDEF), and the *native* repo
> (`~/Development/Allegiance`) was dirtied with P31 instrumentation plus a
> `HandleMsgUnderLock` change. The brief forbids ALL native-side changes, so I
> **fully reverted the native repo** to clean `HEAD` (only the pre-existing
> untracked `BOOT_PATH.md`/`TRANSPORT_T16.md`/`TRANSPORT_T17.md` remain, per
> precedent) and trimmed the C# tree to the brief's narrow scope: the PING
> echo is **excluded** (not part of the wire codec; connection-liveness is a
> separate concern). The empirical layout was harvested from the **existing
> WOPR build** the brief told me to run — its PLAYERINFO handler already had
> `sizeof`/`offsetof` probe debugf baked in — so no new native changes were
> needed to obtain real-compiler byte evidence.

---

## 1. The empirically-confirmed layout, and how I got it

I did **not** hand-derive offsets from the struct declaration. Three
independent confirmations agree:

**(a) The real running build printed its compile-time offsets.** The one live
trial (section 5) ran the *existing* WOPR build on the Windows VM
(`C:\Users\domingo\Allegiance\src\Wopr\Wopr\bin\x86\FZDebug-NoYellowComm`),
whose `FM_S_PLAYERINFO` handler already prints `sizeof`/`offsetof` via debugf
(a prior P31 probe, left in that build; the native repo itself is reverted).
When the client processed our message it emitted, verbatim into
`debug.log`:

```
P31 sizeof(FMD_S_PLAYERINFO)=68 fixedRegion=60
P31 offsets cchName=8 money=36 nKills=42 nEject=44 nDeaths=46 exp=48 iSide=52 shipID=54 rank=56 drone=58 pilotType=60 abm=62 base=64
```

**(b) I re-derived the same offsets arithmetically** against the macro-generated
struct (`messagecore.h:760-777`; `FMD_... : FEDMESSAGE` + `FM_VAR_ITEM` =
two shorts `ib`/`cb` + fixed fields) and the typedef widths in `igc.h`
(`Money`=int `:840`, `PilotType`=`unsigned char` `:685`, `SideID`/`ShipID`/
`DroneTypeID`/`ObjectID` = `ObjectID`=`short` `:477,480,485,491`, `RankID`=
`short` `:843`, `AbilityBitMask`=`short` `:723`), under MSVC-default packing:

| region | offset | field | width |
|---|---|---|---|
| FEDMSG header | 0 | `cbmsg`, `fmid` | 4 |
| var item | 4 | `ib`, `cb` (`rgPersistPlayerScores`) | 4 |
| fixed region | 8 | `CharacterName[25]` | 25 |
| | 36 | `money` (int; 3 pad 33-35) | 4 |
| | 40 | `fReady/fTeamLeader/fMissionOwner` 3×`bool:1` | 1 |
| | 42 | `nKills`,`nEjections`,`nDeaths` (shorts; 1 pad 41) | 6 |
| | 48 | `fExperience` (float) | 4 |
| | 52 | `iSide` (short) | 2 |
| | 54 | `shipID` (short) | 2 |
| | 56 | `rankID` (short) | 2 |
| | 58 | `dtidDrone` (short) | 2 |
| | 60 | `pilotType` (uchar) | 1 |
| | 62 | `abmOrders` (short; 1 pad 61) | 2 |
| | 64 | `baseObjectID` (short) | 2 |
| | 66-67 | trailing pad to struct 4-alignment | 2 |

So `sizeof(FMD_S_PLAYERINFO) = 68` = 4 (header) + 4 (var item) + 60 (fixed
region after the var item) — exactly the running build's printed values, and
exactly what the codec encodes.

**(c) The byte-exact test** composes the 68 bytes by hand from these offsets
and asserts the encoder reproduces them bit-for-bit (section 4).

Net: `cbmsg=68`, `fmid=10`, var-item `ib=68`/`cb=0` (empty score list), and the
fixed region carries `CharacterName[25]`@8, `money`@36, the flags byte@40,
kills/ejections/deaths@42/44/46, `fExperience`@48, **`iSide`@52**,
**`shipID`@54**, `rankID`@56, `dtidDrone`@58, `pilotType`@60, `abmOrders`@62,
`baseObjectID`@64, trailing pad 66-67.

---

## 2. The codec — `src/Allegiance.Core/Wire/PlayerInfo.cs`

- `class PlayerInfo : IFedMessage`, `Fmid = 10` (`PlayerInfo.cs:78`), the same
  Encode/Decode shape as `JoinedMission`/`MissionDef`.
- **Declared widths** (real-compiler-consistent, cross-checked arithmetically):
  `MessageSize = 68` (`:81`), `FixedSize = 68 − 4 − 4 = 60` (`:84`),
  `NameBufferSize = 25` (`:105` = `c_cbName`), `PilotTypePlayer = 10`
  (`:108` = `c_ptPlayer`, `igc.h:685`).
- **Named absolute wire offsets** `:89-102`, all matching the probe above
  (e.g. `ISideOffset=52`, `ShipIdOffset=54`).
- **Fields populated on send**: `CharacterName`, `Money`(=0, `clintlib.cpp:4112`
  asserts `GetMoney()==0`), `ISide` (the peer's real `Ship.SideId`), `ShipId`
  (the assigned ship), `PilotType`=10. Everything else is zeroed
  (`:124-134`, `:157-166`) — stats/bitflags/rank/drone/orders/base are not
  read by the join/side path (grep of the handler `appmsg.cpp:2217-2316`
  shows only name/side/shipID/pilot/stats are consumed).
- `Encode()` (`:138-173`) writes the var item as absent (`cb=0`), then the fixed
  region with an internal-drift guard (`:169-170`) and `cbmsg` patched last.
- `Decode()` (`:175-213`) rejects a wrong length, wrong `fmid`, or a
  non-empty score blob (`cb != 0`), and reads the fixed region by offset.

---

## 3. Send placement and reasoning — `src/Allegiance.Server/Server.cs`

Sent inside the `if (validated)` block (`Server.cs:660-671`), positioned:

```
[join]    MISSIONDEF(38) + JOINED_MISSION(95)   <- static core load + hull hydration
[export]  EXPORT(26) 13× OT_cluster             <- sectors, so GetCluster(sectorID) resolves
[create]  EXPORT(26)  ship (OT_ship)            <- real, hulled ship object (side 0)
[sides]   MISSIONDEF(38) nTeams=2 civIds[1,3]   <- SECOND one; creates SIDE_TEAMLOBBY + real sides
[playerinfo] PLAYERINFO(10) iSide=0 ship=8096   <- THIS message  (Server.cs:660-671)
[then]    SET_CLUSTER(7)                        <- whose handler used to fault for a null Side
[then]    BUY_LOADOUT_ACK(5)
```

Reasoning for each placement decision:

- **After JOINED_MISSION** — `JOINED_MISSION`'s `ResetStaticData`
  (`appmsg.cpp:2162-2163` → `clintlib.cpp:4193-4224`) destroys and rebuilds
  `m_pCoreIGC`; anything the PLAYERINFO handler built before that would be
  flushed away.
- **After the ship EXPORT** — so the handler's
  `CreateObject(OT_ship,&ds,...)` at `appmsg.cpp:2280` fails on the already-
  present id and it takes the `GetShip(shipID)` else-branch (`:2293`),
  reusing the real hulled ship from EXPORT instead of building the
  `hullID=NA` dummy. Proven in the trial: `P31 step5 post-CreateObject
  pship=0` (CreateObject returned NULL → else-branch taken).
- **Immediately after the second MISSIONDEF** — that second MISSIONDEF
  (`Server.cs:618-630`) drives the client's `UpdateSides`
  (`m_pMissionInfo != NULL` branch, `appmsg.cpp:2419-2425`) to create the real
  sides and, via `CmissionIGC::AddSide`, **`SIDE_TEAMLOBBY`** — the exact
  precondition the PLAYERINFO handler's `AddPlayerToMission`
  (`GetSide(SIDE_TEAMLOBBY)`, `clintlib.cpp:4088`) needs, else it
  `SetSide(NULL)`s and faults. Proven in the trial:
  `P31 MDEF before UpdateSides nTeams=2 sides=0 lobbyside=0` →
  `after UpdateSides: sides=2 lobbyside=1`, then
  `P31 AddToMission ENTRY lobbyside=1`.
- **Before SET_CLUSTER** — T17 located the crash at `appmsg.cpp:1034`
  (`GetShip()->GetSide()->GetColor()`) because `m_ship->GetSide()` was NULL.
  PLAYERINFO is the only dispatched path that reaches `AddPlayerToSide`
  (`clintlib.cpp:4103`, the `pship->SetSide` at `:4124`); the trial confirms
  it now runs and sets the side.
- **vs. BUY_LOADOUT_ACK** — PLAYERINFO lands before it (it must precede
  SET_CLUSTER; the loadout ack is a ship-hull adoption that belongs after
  SET_CLUSTER).

---

## 4. Test evidence — `tests/Allegiance.Core.Tests/Wire/PlayerInfoWireTests.cs`

`dotnet build` and `dotnet test` are clean (`Build succeeded, 0 warnings`;
**1780 passed, 0 failed**, incl. the 8 new `PlayerInfo*` tests). Coverage:

- `FixedSizes_AreDeclared` — `Fmid=10`, `FixedSize=60`, `MessageSize=68`,
  `NameBufferSize=25`, `PilotTypePlayer=10`.
- `RoundTrips_ThroughWire` — name/side/shipID/pilot round-trip.
- `EmitsExactDeclaredBytes_ForASideAssignment` — hand-composes the 68 bytes
  (header, var item, padded name, money@36, flags@40, iSide@52, shipID@54=0x1FA7,
  pilotType@60) and asserts `Equal(expected, Encode())`.
- `NonZeroSide_SurvivesRoundTrip`, `EmptyName_IsAllZerosAndPreserved`,
  `LongName_TruncatesTo24AndNullTerminates`.
- `ZerosEveryCell_ExceptThePopulatedOnes`.
- `RejectsWrongFmid_NonZeroScoreBlob_AndWrongLength` — bad fmid, 67- and
  70-byte buffers, and a `cb!=0` score var item are all rejected.

---

## 5. The one live trial — delivery confirmed on the wire and at the client

- Server: this host `192.168.0.140:6073`, `dotnet run --project
  src/Allegiance.Server` at P31a `07d20fb+mine`, one game (PCore001 +
  XXV_simple, 2 sides), `--capture /tmp/p31a.cap`.
- Client: the existing WOPR build on VM `192.168.0.240`
  (`Wopr.exe -d 192.168.0.140 6073`), ArtPath `C:\Source\git\Artwork` with all
  627 `.cvh`. One trial, ~250s bounded, observer `-d` DirectTest; killed at the
  script cap (as designed), server then cleanly stopped (`[shutdown] clean:
  6811 ticks, 0 overruns`, port released).

**Server send-side (verbatim from server log)** — the message went out with the
peer's real side and assigned ship:

```
[join] peer 1 't14Bot@BOT' -> MISSIONDEF core 'PCore001' v990051329 + JOINED_MISSION ship 8096 ...
[export] peer 1 't14Bot@BOT' -> 13 cluster(s): 1051:'Arrokoth', ... 1063:'Sedna'
[create] peer 1 't14Bot@BOT' -> EXPORT hull 1 ship 8096 side 0
[sides]  peer 1 't14Bot@BOT' -> MISSIONDEF(38) nTeams 2 civIds [1, 3] ...
[playerinfo] peer 1 't14Bot@BOT' -> PLAYERINFO(10) ship 8096 iSide 0 (side assignment for the direct-connect ship)
[conn] peer 1 left
```

**Client-side symptom (verbatim from client `debug.log`)** — it received and
decoded the message, and — importantly, beyond the brief's minimum — the side
was actually assigned:

```
P31 dispatch fmid=10
Got Player Info for t14Bot@BOT
P31 sizeof(FMD_S_PLAYERINFO)=68 fixedRegion=60        <- byte layout, from the real build
P31 offsets cchName=8 ... iSide=52 shipID=54 ... base=64
P31 captured-iSide=0 (sent value)
P31 step5 post-CreateObject pship=0                    <- reuses the EXPORT'd ship
P31 step7 pre AddPlayerToMission
P31 AddToMission ENTRY lobbyside=1                     <- SIDE_TEAMLOBBY exists (2nd MISSIONDEF worked)
P31 after AddPlayerToMission SideID()=-2 (expect -2 TEAMLOBBY)
P31 before AddPlayerToSide SideID()=-2, calling with sideID=0
P31 AddPlayerToSide ENTRY SideID()=-2 (assert expects -2 TEAMLOBBY); arg sideID=0
P31 after AddPlayerToSide SideID()=0 GetSide-is-set=1
PlayerInfo for t14Bot@BOT, ship=8096, side=0
```

The code-derived **assert-ambiguity prediction held empirically**: a single
PLAYERINFO with the real `iSide` is sufficient — `AddPlayerToMission`
(called before `AddPlayerToSide`) sets `SetSideID(SIDE_TEAMLOBBY)`
(`clintlib.cpp:4085`), so `AddPlayerToSide`'s precondition assert
`SideID()==SIDE_TEAMLOBBY` (`clintlib.cpp:4107`) sees `-2` and passes. The
client's ship now has `GetSide()!=NULL`, and the SET_CLUSTER crash point is
gone. One message, no two-message dance.

---

## 6. Follow-on (hands to P31b)

The brief's stated *acceptable* outcome (T17's `GetSide()`-still-null crash
recurring) did **not** occur — it was superseded by success (side assigned).
Two things remain and are explicitly **not** resolved by this task:

1. **A `HACKER??` loop begins after PLAYERINFO completes** (client
   `debug.log`): `Messages.cpp:947`/`:973` rejects the message because the
   packet frame it walks reports `cbmsg=68, fmid=10, total packet size=15` and
   `cbmsg(68) > PacketSize(15)` fails the `cbmsg <= PacketSize()` sanity check,
   so the packet walk loops in the else-branch. This is a downstream framing /
   message-cursor symptom — preceded by a swallowed AV (`P31 EAddr=7313BD57`)
   inside the PLAYERINFO handler between the probe print and step2. Net
   effect: the ship's side is set but no clean flight results and the
   observer still sees no movement. Rooting out the AV + restoring packet
   framing so the message-queue cursor stays intact is the natural P31b work.

2. **The AddPlayerToSide assert ambiguity** — this trial empirically resolved
   the specific "one vs two messages" question (one suffices; assert passed),
   so P31b should **redirect** its investigation to the swallowed AV in the
   PLAYERINFO handler (`EAddr=7313BD57`/`...BD57` near the `P31 offsets`
   print) and the resulting `HACKER??` packet-desync, rather than re-litigating
   the assert. Instrument/guard the PLAYERINFO handler body (or the inbound
   packet walk) on the native side — out of scope here per the brief.
