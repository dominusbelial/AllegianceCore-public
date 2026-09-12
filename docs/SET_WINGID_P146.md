# P146 — SET_WINGID(106): wing-group assignment

Port of `FM_CS_SET_WINGID` (id 106), the wing-group-membership tag on a ship,
relayed side-wide. This task was split out of the `allegiance-next-phase-plan`
grouping that lumped it with `ORDER_CHANGE`(93) — direct investigation this
session showed `ORDER_CHANGE` needs a whole drone-AI foundation (`FindTarget`,
`SetCommand`, `PickDefaultOrder`, per-ship command queues, drone voice-line
chat) that this port lacks, whereas `SET_WINGID` is small and self-contained.

## Scope delivered

1. `src/Allegiance.Core/Wire/SetWingId.cs` — the codec (`FM_CS_SET_WINGID`,
   `Messages.h:705-708`).
2. `Ship.WingId` — new mutable property (`Ship.cs`).
3. `Server.cs` dispatch + `HandleSetWingId`.
4. `Allegiance.Bot`: inbound decode + `SendSetWingId`.
5. `FedMessageDumper`: `FormatSetWingId` (debug-tooling completeness).
6. `tests/Allegiance.Core.Tests/SetWingIdTests.cs` — 4 tests.

## Broadcast audience: side-wide, NOT sector-wide (confirmed)

The native handler forwards the corrected message to
`CFSSide::FromIGC(pside)->GetGroup()` — the **sending side's peer group**
(`FedSrv.CPP:5337`), not `GetGroupSectorFlying(pcluster)` (the sector audience
`SWAP_PART`/`DROP_PART`/`PROMOTE` use, e.g. `FedSrv.CPP:6312`). This port's
side-group analogue is the `BroadcastMoneyChangeSideWide` shape
(`Server.cs:3626-3637`): filter `_peerShips` by matching `Ship.SideId`, pack
once, send to each target. `HandleSetWingId` reuses that exact shape.

The live test isolates this distinction precisely: all three bots spawn in the
**same** starter sector (asserted `SubjectSector == EnemySector == ObserverSector`),
yet after the subject tags its own ship, the same-side observer **and** the
subject's own ship receive `SET_WINGID` while the cross-side enemy does **not**.
That cross-side-enemy-in-the-same-sector gap is what proves side-wide, not
sector-wide — a different audience shape than `SwapPart`/`DropPart`/`PROMOTE`.

## Self-targeting-only simplification (deliberate, cited)

Native has two paths (`FedSrv.CPP:5323-5335`):

- `pfsShip == pfsPlayer` (self-assignment) → `bCommanded = false`
  (`FedSrv.CPP:5334-5335`);
- `pfsShip != pfsPlayer` on the same side **and the sender is the side's
  leader** (`pfsPlayer == pfsMission->GetLeader(pside->GetObjectID())`,
  `FedSrv.CPP:5323-5330`) → `bCommanded = true`.

A team-leader concept does not exist in this port: `GetLeader(sid)`
(native `fsmission.h:264`) has no analogue anywhere in `Server.cs`/`Ship.cs`/
`Model/Mission.cs` (zero hits), consistent with `SET_TEAM_LEADER`(121) already
being classified Tier-4 lobby-only N/A in `allegiance-next-phase-plan`. With no
leader system, the leader-retags-another-ship branch is unreachable, so this
port applies the documented simplification: **`shipID` must resolve to the
sender's own ship; any other target is a silent no-op (no broadcast, no state
change)**, and `bCommanded` is always server-corrected to `false`. Stated
plainly here (same discipline as the pooled-money / no-alliance precedents this
session has used), not silently dropped. The rejection is live-tested:
attempting to set a different ship's wing produces no `SET_WINGID` on any peer
and no `WingId` mutation.

## WingID type + bound — FOUND FOR REAL (corrects the task's byte hypothesis)

The task's spec suggested `WingID` was "very likely a `char`/byte-width type"
but explicitly warned **don't assume**. A deeper grep of the native source
found it:

- `Igc/igc.h:504` — `typedef ObjectID WingID;`
- `Igc/igc.h:477` — `typedef short ObjectID;`
  → **`WingID` is a `short` (2 bytes), not a byte.** It was hidden behind a
  macro-generated/typedef chain (the earlier session's grep pattern only caught
  *usages*, not the `typedef`). The codec therefore encodes `wingID` as a
  `short` at offset 6, giving a 10-byte fixed layout — identical size/shape to
  `DROP_PART`.
- `Igc/igc.h:505` — `const WingID c_widMax = 10;` (the bound, now a codec
  constant `SetWingId.WidMax = 10`).
- Cross-check: the ten named wings in `c_pszWingName[c_widMax]`
  (`Igc/common.cpp:23-35`): `command, attack, defend, escort, search, alpha,
  bravo, charlie, delta, echo`; the native client validates `0 ≤ Val ≤ 9`
  before sending (`PigSrv/PigShip.cpp:234-236`).

So no applied-default needed — the type and bound are both confirmed facts.

### Ship.WingId default (applied, stated explicitly)

Native constructs a ship with `m_wingID(NA)` = −1 (`shipIGC.cpp:47`), and the
join path places a team leader at wing 0 (`fsmission.cpp:634`, `SetWingID(0)`).
With no leader system and the wire carrying only `[0, WidMax)`, `Ship.WingId`
defaults to a plain `0` ("command") — an applied port choice, documented in the
property XML, not inferred from the handler.

## Handler gates — mission-present only (confirmed)

The native handler's sole gate is `if (!pfsMission) break` (`FedSrv.CPP:5313`).
There is **genuinely no** `STAGE_STARTED` check in the whole
`case FM_CS_SET_WINGID` (no `GetStage() == STAGE_STARTED` line), unlike most
other in-mission handlers (`SWAP_PART`/`DROP_PART`/`PROMOTE`/`TREASURE_ACK` all
gate on `STAGE_STARTED`). The port matches: `HandleSetWingId` checks only
`mission is null`. Bounds check is `wingID < 0 || wingID >= c_widMax` (native
`TrapHackBoot(pfmSW->wingID < c_widMax)`, `FedSrv.CPP:5319`; negatives = `NA`
also rejected) as a silent no-op.

## Explicit exclusions

- **`ORDER_CHANGE`(93)** — the real drone/AI command-tasking system — is NOT
  touched. Confirmed this session to need `FindTarget`, `SetCommand`,
  `PickDefaultOrder`, and a drone-piloted-ship concept this port has no
  foundation for; remains its own deferred Tier-3 item.
- **Leader-retags-another-ship** — simplified to self-targeting-only (no
  team-leader system, see above).
- **Wing display names** (`c_pszWingName`) — a UI-facing string table, not
  wire-protocol scope; only the numeric bound (`c_widMax`) matters and is
  ported.
- No change to P103–P145 code beyond this one handler.

## Verification

- No VM needed.
- 4 new `SetWingIdTests` (codec layout, `c_widMax`, side-wide live relay +
  mutation, foreign-target silent rejection) pass.
- Full existing suite: 3 consecutive clean runs required before commit (see
  commit notes).
