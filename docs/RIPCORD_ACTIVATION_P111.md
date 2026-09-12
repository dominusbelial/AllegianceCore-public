# P111 — Ship Ripcord: request / activate / deny / abort handshake

**Branch:** `master` (task started at `28413e3`)
**Date:** 2026-09-08
**Scope:** the client `FM_C_RIPCORD_REQUEST`(184) → server `RequestRipcord`
state machine → `RIPCORD_ACTIVATE`(185) / `RIPCORD_DENIED`(186) /
`RIPCORD_ABORTED`(187) handshake, plus the countdown/target-model state on
`Ship`. The real teleport when the countdown reaches zero, and
`ContinueRipcord`'s per-tick revalidation, are follow-ups — argued below.

---

## 1. What the native handler actually does

`FedSrv.CPP:7715-7728` `case FM_C_RIPCORD_REQUEST` gates the request
(each sub-condition read in the source, `FedSrv.CPP:7717-7721`):

| Native condition | Meaning | Ported gate |
|---|---|---|
| `pfsMission->GetStage() != STAGE_STARTED` | mission not running | `Mission.Stage != Started` |
| `GetSide()->GetObjectID() < 0` | no real side | `SideId < 0` |
| `GetParentShip()` | boarded ship | `ParentShip != null` |
| `GetCluster() == NULL` | not flying | `Sector == null \|\| StationId != null` |
| `GetFlag() != NA` | flag carrier | gate collapses (no flags tracked) |
| `GetBaseHullType()->HasCapability(c_habmNoRipcord)` | no-ripcord hull | `HullType.HasCapability(HabmNoRipcord)` |

Each failing gate is a **silent** `break` — there is no denial message for a
gate violation; `RIPCORD_DENIED` only comes later, from inside
`RequestRipcord`, when the request reached it but found no anchor.

Notes on the two gates the task flagged as potentially dependent on
underdeveloped systems:

- **Boarded ship** (`GetParentShip()`, `FedSrv.CPP:7718`): this gate is live in
  native regardless of cluster-E progress — it does not depend on E having
  "landed". E (boarding) *has* landed in this port (P108/P109), so the gate is
  real here, not a collapse.
- **Flag carrier** (`GetFlag() != NA`, `FedSrv.CPP:7720`): this port tracks no
  flags, so, exactly as the task anticipated, the gate collapses to
  always-passing (same precedent P100/P107 used). Documented in
  `HandleRipcordRequest`, not silently dropped.
- **Docked ship** (`GetCluster() == NULL`, `FedSrv.CPP:7719`): confirmed in the
  native that docking clears the cluster — `SetStation` → `s->AddShip(this)`
  "also sets the ship's cluster to NULL" with `assert (GetCluster() == NULL)`
  (`shipIGC.h:457,459`). So a docked ship genuinely has `GetCluster() == NULL`
  and the gate is real, not a consequence of E. In this port a docked ship
  retains `Sector` but has `StationId != null` (the P103/P106 convention), so
  "flying" = `Sector != null && StationId == null`.
- **`c_habmNoRipcord`** = `0x20` (`igc.h:731`), read with the same discipline
  P107 read `c_habmLifepod = 0x04` (`igc.h:728`). Verified: the lowest-hull-id
  hull (1) actually carries this bit on PCore001, which is why the tests select
  a non-no-ripcord hull for the happy paths.

`RequestRipcord` (`FedSrv.CPP:13319-13378`) is the real state machine:

```
pcluster == NULL  →  if active: SetRipcordModel(NULL), send RIPCORD_ABORTED
                     to GetGroupSectorFlying(pclusterShip)
else if (active == NULL || active.GetCluster() != pcluster):
    pmodelNew = FindRipcordModel(pcluster)
    if pmodelNew:
        if pmodelNew != pmodelOld: SetRipcordModel + ResetRipcordTimeLeft
        send RIPCORD_ACTIVATE (185) to
             (pmodelOld==NULL ? GetGroupSectorFlying(pclusterShip)
                              : requester's connection)
             with shipid, ot/oid = pmodelNew, sid = pcluster->GetObjectID()
    else if player:
        send RIPCORD_DENIED (186) to requester's connection only
```

Faithful ports of the three branches, the broadcast scoping, and the
`sidRipcord = requested-cluster-id` subtlety (the anchor may physically sit in
a different sector under warp-adjacency, but `sidRipcord` is always the
<b>requested</b> cluster, `FedSrv.CPP:13364`) are in
`Server.cs:HandleRipcordRequest`/`RequestRipcord`/`SendRipcordSectorWide`.
A request naming an *unknown* sector id resolves to a null cluster natively
(`GetCluster(sidRipcord)` → NULL), which `RequestRipcord` treats exactly like
`sidRipcord == NA` — i.e. a bogus sector id behaves as a cancel, mirrored in the
handler (`Server.cs:HandleRipcordRequest`).

### The state added to `Ship`

Mirroring `shipIGC.h` exactly:
- `RipcordTarget` = `(Ot, Oid, ClusterId)` — the port's stand-in for the native
  `m_pmodelRipcord` pointer. Only three attributes of that pointer are read by
  the wire/state machine: object type/id (for `RIPCORD_ACTIVATE`,
  `FedSrv.CPP:13362-13363`) and its owning cluster (for the re-target
  `GetCluster()` comparison, `FedSrv.CPP:13341-13342`).
- `RipcordModel` ⇐ `GetRipcordModel()` (`shipIGC.h:1832-1835`)
- `IsRipcordActive` ⇐ `fRipcordActive()` (`shipIGC.h:1525-1528`)
- `SetRipcordModel()` ⇐ `SetRipcordModel()` (`shipIGC.h:1836-1849`); the
  native's cloak-off and ripcord-debt adjustments are skipped (port has neither)
- `RipcordTimeLeft` ⇐ `GetRipcordTimeLeft()` (`shipIGC.h:1530-1535`)
- `ResetRipcordTimeLeft()` ⇐ `ResetRipcordTimeLeft()` (`shipIGC.h:1536-1540`),
  seeding the countdown from the hull's `RipcordSpeed`
- `RipcordSpeed` ⇐ `GetRipcordSpeed()` (`shipIGC.h:103`)

(The movement model in `Ship.ExecuteMove` already references the native
`if (!m_pmodelRipcord)` blocks at `shipIGC.cpp:2885,2962`; because this port has
no ripcord path those blocks always ran — that remains true, and is noted.)

---

## 2. `FindRipcordModel` — the real anchor-qualification logic

Read fully at `shipIGC.cpp:3718-3973`. Native anchor priority in a requested
cluster `pcluster`:

1. **Command-target probe/ship** (`shipIGC.cpp:3802-3833`) — if the ship has a
   command target that is a probe (with `GetCanRipcord`) or a ship (whose hull
   is a ripcord target) and it sits in `pcluster`.
2. **Friendly station** via `FindTarget(this, …, c_ttFriendly|c_ttStation, …,
   c_sabmRipcord)` (`shipIGC.cpp:3835-3838`) — a same-side (or allied, when
   `bAllowAlliedRip`) station whose station type has `c_sabmRipcord`
   (`igc.h:747`).
3. **Friendly probe** in `pcluster` (`shipIGC.cpp:3841-3896`), most-recent
   first.
4. **Friendly ship** in `pcluster` (`shipIGC.cpp:3898-3930`), lowest ripcord
   debt first (or closest to the command-target position). A ship is a valid
   target only if its hull is a ripcord target: `c_habmIsRipcordTarget`, or
   additionally `c_habmIsLtRipcordTarget` when the *ripper's* hull has
   `c_habmCanLtRipcord` (`shipIGC.cpp:3900-3906`).
5. **Warp-adjacent-cluster expansion** (`shipIGC.cpp:3936-3972`) — push the
   destinations of warps the ship can see, recurse, and return whatever anchor
   the neighbour yields.

Answer to the task's explicit question: **a warp is not an anchor** — warps are
only traversed to reach another cluster (then that cluster's station/probe/ship
is the anchor). A friendly `c_sabmRipcord` **station** is the canonical anchor;
probes and ships also qualify; the only "other" model tier is the command-target
one.

### What the port implements, and why

`Server.cs:FindRipcordModel` implements tiers 2 (station) and 4 (ship), scoped
to the requested sector, and documents the rest:

- **Tier 2 (station):** a same-side station whose `StationTypeIGC` has
  `c_sabmRipcord` (`Server.cs`), the canonical native anchor.
- **Tier 4 (ship):** a same-side peer ship in the requested sector whose hull is
  a ripcord target, reproducing the `c_habmCanLtRipcord` light-ripcord
  refinement (`Server.cs:FindRipcordModel`).

**Deliberately deferred, and why:**

- **Command-target tier** (`shipIGC.cpp:3802-3833`) and **probe tier**
  (`shipIGC.cpp:3841-3896`): this map/core has no deployed probes and this port
  has no ripcord-to-command-target UI flow; there is no probe list on `Sector`
  to search and nothing drives `CommandTarget` for a ripcord. Adding the tier
  would require inventing probe inventory semantics that do not exist yet.
- **Warp-adjacent expansion** (`shipIGC.cpp:3936-3972`): it depends on per-side
  `CanSee(w)` visibility (no fog-of-war in this port → everything visible) and
  on ripcord-debt ordering the port does not model. More decisively, XXV_simple
  is a dense 13-sector / 38-warp web in which the real side-0 garrison is
  reachable from nearly every sector, so native expansion (with the port's
  "everything visible" stance) would grant an anchor almost anywhere — making
  the `RIPCORD_DENIED` path unreachable in practice. Scoping the anchor search
  to the requested sector keeps the deny path real and deterministic.
  Revisit alongside the countdown/teleport follow-up.

These scoping choices are documented in `Server.cs:FindRipcordModel`'s doc
comment, not silently dropped.

---

## 3. `ContinueRipcord` — not ported; the reasoning

Read in full: `FedSrv.CPP:13379-13438`, and its only caller is the ship's
per-tick `Update` at `shipIGC.cpp:277-294`:

```
if (m_pmodelRipcord) {
    if (pigc->ContinueRipcord(this, m_pmodelRipcord)) {      // re-validate
        m_dtRipcordCountdown -= dt;                           // countdown
        if (m_dtRipcordCountdown <= 0.0f) {                   // timer hit zero
            if (pigc->UseRipcord(this, m_pmodelRipcord))      // THE TELEPORT
                SetRipcordModel(NULL);
            else
                m_dtRipcordCountdown = 0.0f;
        }
    } else
        SetRipcordModel(NULL);                                // became illegal → abort
}
```

`ContinueRipcord`'s revalidation is therefore inseparable from the per-tick
countdown driver and the teleport-on-zero — the pieces this task explicitly
excludes. It re-checks ally-ripcord legality (`MissionDef.misparms.bAllowAlliedRip`),
the flag-carrier gate, that the target model still has a real cluster, and the
ship-target hull capability, broadcasting `RIPCORD_ABORTED` sector-wide and
clearing the model when they fail (`FedSrv.CPP:13427-13434`).

**Decision: it is NOT ported here**, because without a per-tick countdown driver
or a teleport there is no "mid-countdown" in this task's observable behavior for
it to police: nothing decrements the timer, so nothing can illegally lapse, and
the abort-on-legality-failure it would emit is the province of the teleport
follow-up that introduces the driver. Porting `ContinueRipcord` now would bolt a
validator onto a state machine with no revalidation trigger. It is named as part
of the next follow-up (below) so the active-ripcord-becomes-illegal auto-abort
is not forgotten.

---

## 4. Verification

- **Loopback live tests** (`tests/…/LiveRipcordTests.cs`, `[Collection("LiveNetwork")]`),
  real `Bot` against the in-process `Server` on the 2-team XXV_simple map:
  1. A flying ship requests ripcord to a sector containing a real,
     side-0, `c_sabmRipcord` anchor → `RIPCORD_ACTIVATE`(185) broadcast to
     **everyone flying in the requester's sector** (both bots), naming the
     anchor's object type (`OT_station`), id, and the requested target sector;
     server-side `IsRipcordActive` true, `RipcordModel` = the anchor, countdown
     re-seeded to the hull's ripcord speed.
  2. The same ship re-requests a *different* target sector while active →
     `RIPCORD_ACTIVATE` reaches the **requester only** (the bystander in the
     same sector receives nothing) — the first-vs-re-target broadcast
     distinction asserted explicitly.
  3. A request to an anchor-less sector (1056 Farout — no station and no
     friendly ship in it) → `RIPCORD_DENIED`(186) to the requester only.
  4. A cancel (`sidRipcord = NA`) while active → `RIPCORD_ABORTED`(187)
     broadcast sector-wide naming the correct ship; server clears the model.
  5. Silent gates: a **docked** ship, a **boarded** ship, and a ship with a
     **`c_habmNoRipcord`** hull each send a request and receive no ripcord
     message at all (native `break`, `FedSrv.CPP:7717-7721`).
- **Codec tests** (`tests/…/Wire/RipcordWireTests.cs`): all four codecs
  round-trip and emit the exact declared bytes (cbmsg 6/12/4/6; layouts from
  `Messages.h:951-967`, typedefs `igc.h:115,477,487`).
- Full suite: **2487 passing** (baseline 2467 + 20 new), no failures across the
  run(s). The pre-existing `LiveChatSectorWireTests` timing flake (memory: ~1-in-8)
  did not fire on the commit runs.

---

## 5. New / changed files

- `src/Allegiance.Core/Wire/RipcordRequest.cs` (new codec, 184)
- `src/Allegiance.Core/Wire/RipcordActivate.cs` (new codec, 185)
- `src/Allegiance.Core/Wire/RipcordDenied.cs` (new codec, 186)
- `src/Allegiance.Core/Wire/RipcordAborted.cs` (new codec, 187)
- `src/Allegiance.Core/Sim/Ship.cs` (ripcord target model + countdown state)
- `src/Allegiance.Core/Data/DataHullTypeIGC.cs` (`c_habmNoRipcord` 0x20,
  `c_habmIsRipcordTarget` 0x40, `c_habmIsLtRipcordTarget` 0x1000,
  `c_habmCanLtRipcord` 0x2000 — `igc.h:731-739`)
- `src/Allegiance.Core/Data/DataStationTypeIGC.cs` (`c_sabmRipcord` 0x08 —
  `igc.h:747`)
- `src/Allegiance.Server/Server.cs` (dispatch + `HandleRipcordRequest` +
  `RequestRipcord` + `SendRipcordSectorWide` + `FindRipcordModel`)
- `src/Allegiance.Bot/Bot.cs` (collectors + `SendRipcordRequest`)
- `tests/…/LiveRipcordTests.cs`, `tests/…/Wire/RipcordWireTests.cs` (new)

---

## 6. The next follow-up (named, not guessed)

**The actual teleport when the countdown reaches zero** — including the per-tick
driver (`shipIGC.cpp:277-294`) and `UseRipcord` (`FedSrv.CPP:13440+`): a
station-anchored ripcord launches the ship at that station
(`Launch`/`stationIGC.cpp`); a ship-anchored ripcord consumes the target's
energy (deducting the ripper's `RipcordCost`) and relocates the ship. That is
the piece that also brings in `ContinueRipcord` (which revalidates the anchor
*during* that same countdown and aborts it if it becomes illegal) — so the two
go together in one follow-up: introduce the per-tick decrement, port
`ContinueRipcord`'s legality checks as the trigger for the mid-flight abort, and
implement the relocation on zero. Not necessarily the *next* task — that is the
user's call.
