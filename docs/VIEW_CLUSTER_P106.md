# VIEW_CLUSTER — P106

**Slice:** `FM_C_VIEW_CLUSTER(104)` / `FM_S_VIEW_CLUSTER(177)` — the docked
spectator/map-view request/reply. Adds two wire codecs and one server handler,
fully verified by real loopback `Bot`/in-process-server live tests plus
hand-composed exact-byte codec tests.

Commit: builds clean, full suite (2420 → 2427 tests) passed 3 consecutive
runs.

---

## 1. What the native handler does

Quoted in the task (`FedSrv/FedSrv.CPP:6420-6454`, confirmed verbatim at
`/home/domingo/Development/Allegiance/src/FedSrv/FedSrv.CPP:6420-6457`):

- Gated on the player's ship being **docked** (`GetIGCShip()->GetStation()`,
  native `FedSrv.CPP:6422`). This port's direct equivalent is
  `Ship.StationId is not null` (the same convention `PerformUndock` uses,
  `Server.cs:714,1474`).
- `clusterID != NA` (‑1) gates the whole reply; when it is NA the server sends
  nothing (native `FedSrv.CPP:6427`).
- Otherwise it resolves the cluster, echoes the cluster id, sets
  `bUsePosition = false` by default, and only if a target is named
  (`otTarget != NA`), the target resolves, `ptarget->GetCluster() == pcluster`
  **and** `ptarget->SeenBySide(pfsPlayer->GetSide())` does it set
  `bUsePosition = true` and echo `ptarget->GetPosition()` (native
  `FedSrv.CPP:6439-6446`).
- The reply goes **only to the requesting player** via
  `g.fm.SendMessages(pfsPlayer->GetConnection(), ...)` (native
  `FedSrv.CPP:6449`) — never a sector/mission broadcast. Ported as
  `SendPeer(from, …)`.

## 2. Finding: is there a real native consumer of the per-player viewed-cluster state?

**No.** The native's trailing `pfsPlayer->SetCluster(pcluster, true)` (native
`FedSrv.CPP:6454`) calls `CFSPlayer::SetCluster` (`fsship.cpp:742`), which
delegates to `CFSShip::SetCluster` (`fsship.cpp:511`) with `bViewOnly = true`.
In that path the only real work is **display-packet group routing**: the
docked viewer's connection is switched onto the viewed sector's (station's)
DP group so it *receives that sector's display messages while docked*
(`fsship.cpp:932-939` — the `else if (bViewOnly)` branch, which sets
`SetDPGroup(pfsCluster, false)` from the docked station's cluster). It is an
immediate routing side-effect, **not** a persisted "currently viewed cluster"
that any handler reads back — no call site in `FedSrv.CPP` queries a player's
viewed cluster after this write.

Conclusion: this port implements `VIEW_CLUSTER` as a pure request/reply with
**no new server-side per-player state**. The port also has no DP-group routing
concept (replies go straight to the peer's connection), so even the routing
half of the native side-effect has nothing to map onto. The wire protocol is
stateless with respect to the viewer — the server never needs to remember what
a client is currently looking at.

## 3. Visibility gate reused

Native `SeenBySide(side)` reflects fog-of-war/scanning. This port has no
fog-of-war/scanner, so its established existing visibility gate is the
**same-side predicate** the P58 teammate broadcast uses:

- `src/Allegiance.Server/Server.cs:521` — `if (tv.Value.Ship.SideId != recipientSide) continue;  // same-side only`

Reused unchanged as the target-visibility predicate in `HandleViewCluster`
(`Server.cs:1901`), factored as `SeenByRequesterSide` (`Server.cs:1974`): an
object with no side (a neutral world object) is visible; a side-carrying object
is visible only to its own side. This is the faithful stand-in for native
`SeenBySide`, not an invented gate. (Documented in the handler doc comment,
`Server.cs:1876-1875`.)

## 4. Wire layouts

`Wire/ViewClusterRequest.cs` — `FM_C_VIEW_CLUSTER`(104), 10 B fixed:
`cbmsg(2) fmid=104(2) clusterID short(2) otTarget short(2) oidTarget short(2)`.
All ids are native `short` typedefs (`igc.h:114-115,477`), NA = ‑1.

`Wire/ViewClusterReply.cs` — `FM_S_VIEW_CLUSTER`(177), 19 B fixed:
`cbmsg(2) fmid=177(2) Vector position(12) clusterID short(2) bUsePosition byte(1)`.
Vector 4-aligned at 4-15 (same as `MissileDestroyed.cs`); the bool is 1 byte
(the same convention `KillShip.cs:13-14` establishes, live-verified). No
alignment padding anywhere — this codebase's standing assumption (proof in
`SetCluster.cs`'s doc comment, P36).

Both codecs round-trip and hand-compose byte-exact (test
`ViewClusterRequest_emits_exact_declared_bytes` /
`ViewClusterReply_emits_exact_declared_bytes`).

## 5. Handler

`src/Allegiance.Server/Server.cs`:

- dispatch branch `else if (fmid == ViewClusterRequest.Fmid)` — docks gate
  (`StationId is not null`), falls through to `HandleViewCluster`
  (`Server.cs:1594-1623`).
- `HandleViewCluster` (`Server.cs:1876`): NA-cluster silent, else resolve
  cluster, resolve+validate target, reply to requester only.
- `TryResolveViewTarget` (`Server.cs:1943`): the port's `GetModel(ot, oid)` —
  scans peer ships and sector-attached stations.
- `SeenByRequesterSide` (`Server.cs:1974`): the reused same-side gate.

No ship position/cluster/sim state is touched — strictly a read-only preview
(scope requirement). No docking/undocking interaction.

## 6. Verification (all real, loopback in-process server over TCP)

From `tests/Allegiance.Core.Tests/LiveViewClusterTests.cs`, 7 new tests:

| Scenario | Result |
|---|---|
| docked ship, real cluster, `otTarget=NA` | reply names cluster, `bUsePosition=false` (observed `cluster 1053`, bytes `13-00-B1-00-...00-1D-04-00`) |
| docked ship, real same-side object in it | `bUsePosition=true`, position `(5000,-5000,2000)` == the object's real position |
| object real but in a different cluster | `bUsePosition=false`, cluster still echoed (`1051`) |
| object real but enemy (not visible) | `bUsePosition=false`, cluster `1053`, position zero |
| flying (not docked) ship request | **no reply** (observed after 120 ticks) |
| both codecs | round-trip + byte-exact vs hand-built buffers |

Observed live output (from the passing run):

```
VIEW-LIVE: docked ship 8096 -> cluster 1053 target ship 8097 bUsePosition=true position (5000,-5000,2000) (expected (5000,-5000,2000))
VIEW-LIVE: docked ship 8096 bare-cluster request -> reply cluster 1053 bUsePosition=false (19B, 13-00-B1-00-00-00-00-00-00-00-00-00-00-00-00-00-1D-04-00)
VIEW-LIVE: docked ship 8096 wrong-cluster target -> reply 1051 bUsePosition=false; enemy target -> 1053 bUsePosition=false
VIEW-LIVE: flying ship 8096 no reply after 120 ticks (gate GetStation()!=NULL)
```

Full suite: 2420 (baseline, P102 checkpoint reference) → **2427** with the 7
new tests, green across **3 consecutive** `dotnet test` runs (details below).

---

*Phase 3 cluster D — P106.*
