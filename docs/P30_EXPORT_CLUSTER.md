# P30 — export the ship's sector (OT_cluster) so `flyingF()` finally becomes true

Branch `master`, on top of `7746abf` (P29). Date 2026-08-31.

## Summary

Added an `FM_S_EXPORT(26)` / `OT_cluster(13)` wire codec (`ExportCluster`)
plus an `Encode()` on `DataClusterIGC`, and made the server export **every
sector** of the mission to a validated peer *between* `JOINED_MISSION` and the
ship `EXPORT`/`SET_CLUSTER`, so the client's `SET_CLUSTER` handler
(`appmsg.cpp:1003 GetCluster(sectorID)`) can resolve a real `CclusterIGC` and
assign the ship to it — the mechanism that should make `flyingF()`
(`clintlib.h:1198`) true and `BaseClient::SendUpdate` (`clintlib.cpp:1815`)
emit `FM_C_SHIP_UPDATE`.

**Live outcome — honest negative (5 trials, 0/5).** The cluster export is
byte-correct on the wire and **accepted by the client cleanly** (the debug.log
"invalid message" storm seen before the `.cvh` fix is **gone**, and the client
processes the 13-message cluster frame with zero HACKER lines). The real WOPR
strategy reaches its command stage (`cmd=c_cidGoto`) and `ScoutExploreMap`
runs past `IsCurrentShipAScout()` — genuine real-AI progress, matching the
task's "already observed" baseline. **But** the ship's position stays `(0,0,0)`
in all 5 trials and the server never receives a single `FM_C_SHIP_UPDATE`
(no `[ctrl]` line). `SendAndReceiveUpdate` → `BaseClient::SendUpdate` is
gated on `flyingF()`; with no `[ctrl]` arriving, `flyingF()` is still false —
`m_ship->GetCluster() == NULL` persists on this headless direct-connect WOPR
client even though the sector object is exported, created, and resolvable.
That final link (the client's ship acquiring the cluster in the native
SET_CLUSTER path) is a native-client behaviour that a correct, delivered
server-side cluster export does not, by itself, trigger — precisely the
task's stop condition.

---

## 1. The wire codec

### `src/Allegiance.Core/Data/DataClusterIGC.cs` — added `Encode()`

`DataClusterIGC` is the only `Data/*.cs` record consumed in **both**
directions (decoded from the map at boot; re-encoded for export), so — uniquely
among the decode-only corpus parsers — it gained an `Encode()` beside its
`Decode()` (method at `DataClusterIGC.cs:118-153`). It produces exactly 92
bytes, mirroring `Decode`'s layout verbatim. Three encoding rules, from the
reference engine:

- `activeF` is **always written false** on export (`src/Igc/clusterIGC.cpp:494`).
- String fields (`name` 25, `posterName` 13, `planetName` 13) are zero-padded
  (not NUL-forced); `Decode`'s `CutAtNull` trims them, so round-trips hold for
  names shorter than their buffer.
- Planet angle fields are passed through as raw bytes.

`Encode(Decode(b)) == b` field-wise for every one of the 1506 corpus cluster
records (pinned by the new `DataClusterIGCRoundTripTests`).

### `src/Allegiance.Core/Wire/ExportCluster.cs` — new `FM_S_EXPORT(26)` codec

Mirrors `ExportShip`'s envelope exactly:
```
offset 0 : ushort cbmsg = 104
offset 2 : ushort fmid  = 26
offset 4 : VarItem (ib=12, cb=92) for exportData
offset 8 : int32  objecttype = OT_cluster = 13
offset 12: exportData = DataClusterIGC (92 B)
         = 104 B total
```
`objecttype` is the same `[v1_enum]` 4-byte `AGCObjectType` as `ExportShip`;
`OT_cluster = AGC_Cluster = 13`. Unlike `ExportShip` (whose fields are
flattened inline because no `Data/*` backing exists for a ship), `ExportCluster`
holds the already-decoded `DataClusterIGC` and lets its `Encode()` do the
fixed-payload serialization — the cleanest fit for a type that has a `Data/*`
backing.

**Codec-form decision (standalone file, not folding into `ExportShip`):**
`ExportShip`'s fields are flat and ship-specific, and its `Decode` rejects any
`objecttype != OT_ship`. Folding a second object payload in would force
objecttype-discriminated encoding/decoding in one class, diverging from the
repo's clean one-type-one-codec convention (`ExportShip`, `SetCluster`, etc.
each encode exactly one object). A sibling `ExportCluster` mirrors
`ExportShip` with no coupling.

### `src/Allegiance.Server/Server.cs` — where it's sent

Inside the existing `if (validated)` logon block, immediately after the
`MISSIONDEF`+`JOINED_MISSION` send (`Server.cs:501-526`) and **before** the ship
`EXPORT`/`SET_CLUSTER`/`BUY_LOADOUT_ACK`. All 13 sectors of `_missions[0]` are
exported (one `ExportCluster` each, packed into one `FedPacket`).

**Sector-scope judgment call: export every sector**, not just the peer's own
starting sector. This matches the reference server exactly — `SendMissionInfo`
loops the whole cluster list (`FedSrv/fsmission.cpp:4170-4173`) — is simpler
than selecting one (no "which one" logic to get wrong), costs ~92 B × N
negligibly locally, and lets the client resolve any sector id a later message
references. The task's minimum (the ship's own sector) is a strict subset and
would also satisfy `flyingF()`; the full loop is the more robust superset.

**Ordering rationale:** the cluster exports must land *after*
`MISSIONDEF`+`JOINED_MISSION` (whose `ResetStaticData → FlushGameState`
destroys the mission's objects) and *before* `SET_CLUSTER` (whose handler
`GetCluster(sectorID)` must already resolve). `DataClusterIGC`'s `Encode` was
the only reason this needed a server-side build; the ship-`EXPORT` and
`SET_CLUSTER` sends are unchanged.

---

## 2. Tests (all 1772 pass; +13)

- `tests/.../Wire/ExportClusterWireTests.cs` (new): declared sizes (`Fmid=26`,
  `OtCluster=13`, `DataClusterSize=92`, `FixedSize=104`); round-trip; a
  **hand-composed 104-byte byte-exact buffer** (from the task's field table, not
  the encoder) proving every field lands at its documented offset and that
  `activeF` is written 0 even when the source `Active=true`; and rejection of a
  wrong `fmid`, a non-`OT_cluster` objecttype, and a truncated/blob-sized-
  mismatch.
- `tests/.../DataClusterIGCRoundTripTests.cs` (new): corpus-wide — for every
  `OT_cluster` record in all 111 maps, `Decode(Encode(c))` equals `c` on every
  field and forces `Active=false`.
- Existing suites unchanged.

`dotnet build`: **0 warnings / 0 errors**. `dotnet test`: **1772 passing**
(was 1759). Committed only after both green.

---

## 3. Live proof — honest 5-trial result (0/5)

Real server (`--capture`), existing WOPR build (`Wopr.exe -d 192.168.0.140
6073`), Windows VM `192.168.0.240`. Five consecutive trials via a bounded
runner that keeps the SSH session alive and waits for a natural WOPR exit.

**Important environment finding (verified, not assumed).** The task brief stated
the VM ArtPath already had all needed `.cvh` files. Verification on the VM
(`C:\Source\git\Artwork`) showed **179 `.cvh` present but the specific hull
files the core import needs were missing** — e.g. `utl04.cvh`, `utl02.cvh`,
`proxexpl.cvh`, `heavyexp.cvh`, `acs40.cvh`, `dn_faphshtwr.cvh`,
`dn_ptlasbls.cvh`, `ss92.cvh` all absent. The client logged `Unable to open
X.cvh`, hit an access violation mid-import, and the FedMessaging receive walk
then spun in the "HACKER?? message from Server(1). cbmsg=864, fmid=38, total
packet size=1356" debugf loop (a million+ lines) — hanging the client. All
627 `.cvh` in the Linux `Artwork` tree were deployed to the VM ArtPath (a
VM-local data fix, matching the P29 `.igc`-deployment precedent and the task's
own framing of the `.cvh` as a VM-local data fix). After that: **zero HACKER
lines**, clean import, clean trials.

Verbatim server log (representative, trial 1 = peer 1):
```
[logon] 1 't14Bot@BOT' validated=True -> 'Welcome t14Bot@BOT' (server time 158400213, ship 8096)
[join]  peer 1 't14Bot@BOT' -> MISSIONDEF core 'PCore001' v990051329 + JOINED_MISSION ship 8096 (static core load + hull-table hydration)
[export] peer 1 't14Bot@BOT' -> 13 cluster(s): 1051:'Arrokoth', 1052:'Borasisi', 1053:'Ceto'*, 1054:'Deucalion', 1055:'Eris', 1056:'Farout', 1057:'Haumea', 1058:'Ixion', 1059:'Makemake', 1060:'Orcus', 1061:'Quaoar'*, 1062:'Varda', 1063:'Sedna'
[create] peer 1 't14Bot@BOT' -> EXPORT hull 1 ship 8096 side 0
[wire] from 1: unsupported fmid 35 — ignored at headless milestone   (x5)
[wire] from 1: unsupported fmid 3 — ignored at headless milestone
[conn] peer 1 left
```
The server receive side shows **only `PING(35)` heartbeats and `LOGOFF(3)` —
no `FM_C_SHIP_UPDATE(24)`** in any trial. `fedmsgdump` on the `--capture`
confirms the wire is byte-correct: per peer `LOGONACK → MISSIONDEF(864)+
JOINED_MISSION(20) → [13 × EXPORT(104, OT_cluster)] → ship EXPORT(64) →
SET_CLUSTER(56) → BUY_LOADOUT_ACK(11)`, then client→server PING frames.

Verbatim client observer output (trial 1, full; trials 2-4 identical headline):
```
[t14] Ship assigned; observing position for ~30 seconds.
ScoutExploreMap t14Bot@BOT: AttachMessages
ScoutExploreMap t14Bot@BOT: Starting strategy: ScoutExploreMap
[t14] pos=(0.0,0.0,0.0) speed=0.0 distFromOrigin=0.0 delta1s=0.0 cmd=c_cidNone
ScoutExploreMap t14Bot@BOT: 	Start(): We are not in a scout, returing to the nearest station.
[t14] pos=(0.0,0.0,0.0) speed=0.0 distFromOrigin=0.0 delta1s=0.0 cmd=c_cidGoto
[t14] pos=(0.0,0.0,0.0) speed=0.0 distFromOrigin=0.0 delta1s=0.0 cmd=c_cidGoto
[t14] pos=(0.0,0.0,0.0) speed=0.0 distFromOrigin=0.0 delta1s=0.0 cmd=c_cidGoto
[t14] pos=(0.0,0.0,0.0) speed=0.0 distFromOrigin=0.0 delta1s=0.0 cmd=c_cidGoto
[t14] pos=(0.0,0.0,0.0) speed=0.0 distFromOrigin=0.0 delta1s=0.0 cmd=c_cidGoto
[t14] Observation done. max 1s-position-delta = 0.0 units.  -> no significant movement
[t14] disconnecting.
```
(Trial 5 hung and was force-killed at the cap; its stdout was block-buffered
and lost, but the server log shows the identical connect → cluster-export →
no-`[ctrl]` pattern. `WOPR_FINISHED` vs `WOPR_FORCE_KILLED` per trial below.)

### The 5-trial table

| trial | `Ship assigned` | `cmd` | `max 1s-position-delta` | server `[ctrl]` | WOPR exit |
|-------|-----------------|-------|--------------------------|-----------------|-----------|
| 1 | yes | `c_cidGoto` | `0.0 units -> no significant movement` | **none** | FINISHED |
| 2 | yes | `c_cidGoto` | `0.0 units -> no significant movement` | **none** | FINISHED |
| 3 | yes | `c_cidGoto` | `0.0 units -> no significant movement` | **none** | FINISHED |
| 4 | yes | `c_cidGoto` | `0.0 units -> no significant movement` | **none** | FINISHED |
| 5 | yes | (stdout lost on force-kill) | — | **none** | FORCE_KILLED |

**Result: 0/5 trials produced movement or an inbound `FM_C_SHIP_UPDATE`.**

### Precise diagnosis (traced, not guessed)

The only gate between a live, connected client and a sent `FM_C_SHIP_UPDATE`
is `BaseClient::SendUpdate` (`clintlib.cpp:1815-1819`):
`if (flyingF() && m_fm.IsConnected() && m_ship->GetParentShip() == NULL)`.
`SendAndReceiveUpdate` (`Wopr/AllegianceInterop/NativeClient.cpp:100-122`)
calls `BaseClient::SendUpdate` on every loop tick, and the client is connected
(it sends PINGs), so `IsConnected()` holds. `GetParentShip()==NULL` holds for
a direct-connect pilot. Therefore no `[ctrl]` arriving means **`flyingF()`
is false** — `m_ship->GetCluster() == NULL` — exactly the pre-P30 symptom the
task aimed to close. The observer reading `(0,0,0)` throughout corroborates:
`SET_CLUSTER`'s `pship->ProcessShipUpdate(...)` (`appmsg.cpp:1042`) never
applies the server's position, consistent with the handler bailing before that
point (most plausibly the `assert(pcluster)`/`GetCluster(1051)` path at
`appmsg.cpp:1003-1004`).

The cluster creation machinery is verified sound on the native side
(`src/Igc/clusterIGC.cpp:27-40` `Initialize` copies the 92-byte record and
`AddCluster`s it; `clusterIGC.h:50-53` `GetObjectID()` returns
`m_data.clusterID`; `sizeof(DataClusterIGC)` = 92 under MSVC default
alignment, satisfying `Initialize`'s `dataSize == sizeof(m_data)` assert), and
my exported bytes are byte-correct and CRC-validated. Yet the client's ship
still does not acquire the cluster. Why the headless direct-connect client's
`m_ship` cluster-binding does not take effect despite a resolvable, exported
sector is **not observable from the server side** and is not forced by a
correct cluster export. Pinpointing it needs a native-client probe —
`appmsg.cpp:991-1087` (SET_CLUSTER) instrumentation — which this task
explicitly forbade ("no VM diagnostic probe is needed" / "if the live test
reveals something in the native client also needs a change — stop and report
precisely, don't force it"). This is the stop condition, met honestly.

## 4. Deliverable status

**Complete and verified:** the `OT_cluster` codec (byte-exact), the server
send (all sectors, correctly ordered), and 13 tests (round-trip + hand-composed
byte-exact + rejection + corpus-wide encode/decode). The wire objective —
convey the map's sector data to the joining client before `SET_CLUSTER` — is
met and proven on the wire. The **milestone** (real WOPR movement) remains
blocked on a native-client behaviour outside this C#-server task's scope.

**No source changes to `~/Development/Allegiance`.** The only VM change was
deploying the missing `.cvh` data files (a VM-local data fix, per the P29
precedent and the task's own framing), to restore the import state the brief
assumed was already present.

## Files changed

- `src/Allegiance.Core/Data/DataClusterIGC.cs` — added `Encode()` (+ doc note on why it, uniquely, has both directions).
- `src/Allegiance.Core/Wire/ExportCluster.cs` — new, `FM_S_EXPORT(26)`/`OT_cluster(13)` codec.
- `src/Allegiance.Server/Server.cs` — export all sectors after `JOINED_MISSION`, before ship `EXPORT`/`SET_CLUSTER`.
- `tests/Allegiance.Core.Tests/Wire/ExportClusterWireTests.cs` — new, 5 tests.
- `tests/Allegiance.Core.Tests/DataClusterIGCRoundTripTests.cs` — new, corpus-wide encode/decode round-trip.
- `README.md` — document the codec, the sector-export ordering, updated test counts (1772).
- `P30_EXPORT_CLUSTER.md` — this write-up.

Build clean (0 warnings / 0 errors); **1772** tests pass. Server started and
stopped; port 6073 free afterward. Scratch trial runners moved to
`~/.hermes/scripts/` (`run_p30_*.ps1`), not committed.
