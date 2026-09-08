# P103 — SUICIDE + SHIP_DELETE (death-only slice)

Repo: `AllegianceCore` branch `master`, on top of `8baf38b` (P102).
Scope: the two messages that mean "a ship stops existing for a peer" —
`FM_C_SUICIDE` (80) and `FM_S_SHIP_DELETE` (20) — **restricted to the
combat-death overlap between them**. This is deliberately a narrower slice
than SHIP_DELETE's full native scope. No Windows VM needed.

Two deliberate, prominently-flagged simplifications; both stated here and
again in the code where they live (neither is a silent drop):

1. SUICIDE's child-ship/cheat gate is relaxed.
2. SHIP_DELETE emits only `SDR_KILLED` — the other six reasons are out of
   scope and require a visibility-culling system this port does not have.

---

## What was built

### `src/Allegiance.Core/Wire/Suicide.cs` — `FM_C_SUICIDE(80)`

Confirmed genuinely empty: `Messages.h:590-591` is `DEFINE_FEDMSG(C,
SUICIDE, 80)` with no fields between the macros (`FMD_C_SUICIDE` is
header-only, `cbmsg = sizeof = 4`). Codec: `Encode()` writes `{cbmsg=4,
fmid=80}`, `Decode()` validates the header and returns the empty object.

Server-side handler (`Server.cs`, `case Suicide.Fmid` in the inbound
dispatch, plus `HandleSuicide`), a faithful port of
`FedSrv.CPP:6335-6350`:

- **Gate: mission started.** `FedSrv.CPP:6342` refuses unless
  `pfsMission->GetStage() == STAGE_STARTED`. Kept: the ship's
  `Mission` must exist and be `MissionStage.Started`.
- **Gate: the ship is actually flying.** `FedSrv.CPP:6346` refuses unless
  `GetCluster()` is non-null. In this port a placed-but-docked ship
  **keeps** `Ship.Sector` (the dock seam sets `StationId` but does not
  clear `Sector`), so "has a cluster" faithfully means placed **and** not
  docked: `Sector != null && StationId == null`. A docked/off-sector
  suicide is a verified no-op.
- **Relaxed child-ship/cheat gate (documented simplification).**
  `FedSrv.CPP:6338` only allows suicide for a *child ship*
  (`GetParentShip() != NULL` — a boarded/turret gunner, Phase 3 cluster E,
  not yet built here) **or** an admin/cheat-enabled player (`CanCheat()` —
  no admin/cheat concept exists in this port at all). Neither prerequisite
  exists, so the gate as native wrote it would make SUICIDE permanently
  unusable here. Following the exact precedent P100 set for probe
  friendly-fire (a gate collapses to always-true when its own prerequisite
  doesn't exist yet), **any placed ship may suicide**. Revisit only when
  boarding lands.
- **Kill path reused, not rebuilt.** The trigger is the exact seam a combat
  death's lethal branch raises (`Ship.ReceiveDamage` →
  `Mission.Site.KillShipEvent`, `Ship.cs:650`) — a direct
  `KillShipEvent(now, ship, NULL launcher, 1.0f, ship.Position,
  Vector::GetZero())`, precisely what the native suicide does
  (`FedSrv.CPP:6347-6348`). No parallel kill mechanism; the site seam is
  wired once to the broadcast. As in native, suicide does not drain the
  hull — the death notice is what a suicide produces.

### `src/Allegiance.Core/Wire/ShipDelete.cs` — `FM_S_SHIP_DELETE(20)`

Message declared at `Messages.h:196-199`: `ShipID shipID; ShipDeleteReason
sdr;`. Wire width of each field, verified from the native sources (not
guessed):

- `ShipID` is a `short`: `typedef ObjectID ShipID;` (`igc.h:480`) and
  `typedef short ObjectID;` (`igc.h:477`). The header is 4 bytes: `CB
  cbmsg` (`messagecore.h:59`, `unsigned short`) then `FEDMSGID fmid`
  (`messagecore.h:43`, `unsigned short`), so the fixed part begins at
  offset 4.
- `ShipDeleteReason` is a **plain C++ `enum` with no underlying type**
  (`Messages.h:188-195`), so MSVC sizes and aligns it as a full `int`
  (4 bytes) — the same convention this port already applies to other
  plain enums (`ChatData.cs:15`, `IChatRoutingTarget.cs:17` treat the
  4-byte C++ `ChatTarget` enum as an `int32`). That inserts **2 padding
  bytes** after the `short shipID`, giving:

```
offset  0: ushort cbmsg
offset  2: ushort fmid = 20
offset  4: short  shipID        (ShipID = ObjectID = short)
offset  6: 2 pad bytes (0)      — enum 4-align
offset  8: int32  sdr           (ShipDeleteReason, plain enum = 4 bytes)
= 12 B fixed, no var items → cbmsg = 12
```

So `FixedSize = 12`, and `Encode` writes the two pad bytes explicitly
(the native struct is cast straight to the wire — `CASTPFM`,
`appmsg.cpp:457` — so the padding is real and present on the wire).

**Only `sdr = SDR_KILLED` is constructible here.** `SDR_KILLED` is ordinal 1,
the second enumerator (`Messages.h:188-190`). Exposed as
`ShipDelete.SdrKilled`. The other six reasons (`SDR_OUTOFRANGE`,
`SDR_LEFTSECTOR`, `SDR_DOCKED`, `SDR_SUPERCEDED`, `SDR_LOGGEDOFF`,
`SDR_TERMINATE`) are not constructed anywhere by this slice — see the
prerequisite note below.

### Broadcast on ship death (both trigger paths)

In `Server.BroadcastKillShip` (`Server.cs`, the `_host.ShipKilled`
handler wired at `Server.cs:266`), a second parallel message is sent
alongside the existing `KILL_SHIP(105)`: `ShipDelete.ForKilledShip(victim.Id)`
→ `SHIP_DELETE(20, SDR_KILLED)`, to **the exact same `targets` array**
(peers whose ship shares the victim's sector), reusing the loop, not
inventing an audience. Because this port's existing `KILL_SHIP` already
reaches everyone who needs to know a ship died, `SHIP_DELETE` is genuinely
redundant information by the native's own design — belt-and-suspenders,
since a real native client uses `SHIP_DELETE` to *remove* the client-side
object (`appmsg.cpp:470-476`: SDR_KILLED/TERMINATE → `SetCluster(NULL)`)
and `KILL_SHIP` to play the death effect. Both are ported; `SHIP_DELETE`
is not skipped as "already covered."

This fires for **both** a combat death (the pre-existing path, unchanged —
`ResolveCombatCollisions` → `Ship.ReceiveDamage` → `KillShipEvent`) and a
suicide death (same seam, reached from the new `HandleSuicide`).

## Out of scope (explicitly not attempted)

- `SDR_OUTOFRANGE` / `SDR_LEFTSECTOR` / `SDR_DOCKED` / `SDR_SUPERCEDED` —
  all need a real per-side fog-of-war / range visibility-culling system.
- `SDR_LOGGEDOFF` / `SDR_TERMINATE` — disconnect/reconnect handling, a
  separate concern.
- Restoring SUICIDE's native child-ship/cheat gate — explicitly relaxed
  (above), revisit only once Phase 3 cluster E (boarding) exists.
- Any change to the existing `KILL_SHIP(105)` broadcast itself.

### The real prerequisite for the other 6 `ShipDeleteReason` values

The native sender, `CFSShip::AnnounceExit(IclusterIGC*, ShipDeleteReason)`
(`FedSrv/fsship.cpp:196`), fires whenever a ship leaves a peer's
*visibility*; most of its reasons are driven by a **per-side visibility /
range-culling system** the sender uses to tell peers to forget a ship
(moving it to the null sector, `appmsg.cpp:479-489`). This port has no such
system: its teammate-visibility broadcast is a **one-shot, never-revoked**
EXPORT — `Server.cs:70` `_teammateExportSent` + callers (P58/P62/P63) — so
once a peer is told about a teammate's ship, this port never tells it to
forget it, and there is no concept of a ship becoming invisible again.
Building the full 7-reason SHIP_DELETE faithfully means building that whole
visibility-culling system first. That is a much later, separate
undertaking — not this task's problem.

One honesty note on audience: the native `AnnounceExit` for the
non-DOCKED/LEFTSECTOR reasons (including SDR_KILLED) actually addresses the
**whole mission group** (`GetGroupMission()`, `fsship.cpp:213-219`), which
is broader than the sector-scoped audience of this port's `KILL_SHIP`. Per
the task's explicit instruction ("broadcast SHIP_DELETE to the same
audience KILL_SHIP(105) already reaches — don't invent a new one"),
SHIP_DELETE here matches KILL_SHIP's sector audience. Noted as a deliberate
choice, not an oversight.

---

## Verification (all green, 3 consecutive full-suite runs)

Each run: `dotnet test tests/Allegiance.Core.Tests` → **2403 passed, 0
failed** (baseline was ~2395+ at the P102 checkpoint; net +8 new test
cases, consistent).

New tests in `tests/Allegiance.Core.Tests/LiveSuicideShipDeleteTests.cs`
(all in the `LiveNetwork` sequential collection):

| Test | Asserts |
|---|---|
| `Suicide_message_round_trips` | FM_C_SUICIDE(80) codec is header-only: emits exactly `{04-00-50-00}` (cbmsg=4, fmid=80), round-trips. |
| `ShipDelete_message_round_trips` | SHIP_DELETE(20) codec round-trips shipID + SDR_KILLED. |
| `ShipDelete_message_emits_exact_declared_bytes` | Hand-composed 12-byte buffer (cbmsg=12, fmid=20, shipID short, 2 pad bytes, sdr=1) matches the encoder exactly. |
| `Kill_seam_raises_kill_ship_exactly_once_either_way` (Theory, ×2) | The suicide call (`Site.KillShipEvent`, NULL launcher) is the same seam as a combat lethal `ReceiveDamage`; each raises KillShipEvent exactly once via a `RecordingIgcSite`. |
| `Suiciding_ship_dies_and_both_death_messages_arrive` (live) | A flying placed ship sends SUICIDE → real KILL_SHIP(105) + SHIP_DELETE(20,SDR_KILLED) naming its own id, on its own client. |
| `Suiciding_docked_ship_is_a_no_op` (live) | Docked ship's SUICIDE is refused (gate: GetCluster(), FedSrv.CPP:6338): nothing dies, no KILL_SHIP, no SHIP_DELETE, still docked after 120 ticks. |
| `Combat_death_produces_ship_delete_to_same_audience` (live) | Two armed bots fight; the victim dies and **both** clients receive SHIP_DELETE(SDR_KILLED) plus KILL_SHIP for the same victim id. |

### Live-loopback evidence (captured during the filtered run)

```
[combat] ship 8097 killed by ship 8096 ... KILL_SHIP(105) -> 2 peer(s)
[combat] ship 8097 — SHIP_DELETE(20) SDR_KILLED -> 2 peer(s)
COMBAT-DELETE-LIVE: KILL_SHIP(105)  shipID=8097 idCredit=8096 = 0E-00-69-00-A1-1F-00-00-A0-1F-FF-FF-00-01
COMBAT-DELETE-LIVE: SHIP_DELETE(20) shipID=8097 sdr=1        = 0C-00-14-00-A1-1F-00-00-01-00-00-00
[suicide] 1 ship 8096 — re-triggering the combat kill path (KillShipEvent, launcher NULL)
[combat] ship 8096 killed by (none) ... KILL_SHIP(105) -> 1 peer(s)
[combat] ship 8096 — SHIP_DELETE(20) SDR_KILLED -> 1 peer(s)
SUICIDE-LIVE: KILL_SHIP(105)  shipID=8096 idCredit=-1 = 0E-00-69-00-A0-1F-00-00-FF-FF-FF-FF-00-01
SUICIDE-LIVE: SHIP_DELETE(20) shipID=8096 sdr=1       = 0C-00-14-00-A0-1F-00-00-01-00-00-00
[suicide] 1 ship 8096: docked — refused (gate: GetCluster(), FedSrv.CPP:6338)
SUICIDE-LIVE: docked ship 8096 refused (no kill/no broadcast after 120 ticks)
```

The suicide KILL_SHIP carries `idCredit=-1` (`killerId` NA, launcher NULL)
exactly like the native suicide — same shape as a combat death, differing
only in the (correctly) absent killer. The docked refusal emits no death
messages at all.

### Regression

The full existing combat-death suites
(`CombatDamageTests`, `MissileCombatTests`, `ProbeCombatTests`,
`AsteroidCombatTests`, `LiveCombatWireTests`, `CombatDeathTests`, and every
other class) pass unmodified — nothing changed in the `KILL_SHIP` path or
the collision/damage machinery.

## Native sources consulted

- `src/Inc/Messages.h:188-199` — `enum ShipDeleteReason` + `DEFINE_FEDMSG(S, SHIP_DELETE, 20)`
- `src/Inc/Messages.h:590-591` — `DEFINE_FEDMSG(C, SUICIDE, 80)` (empty)
- `src/Inc/messagecore.h:43,59,76` — `FEDMSGID`/`CB` (ushort), `FEDMESSAGE`
- `src/Igc/igc.h:477,480` — `typedef short ObjectID; typedef ObjectID ShipID`
- `src/FedSrv/FedSrv.CPP:6335-6350` — `case FM_C_SUICIDE`
- `src/FedSrv/fsship.cpp:196-225` — `CFSShip::AnnounceExit`
- `src/clintlib/appmsg.cpp:455-489` — client SHIP_DELETE handler (SDR_KILLED → `SetCluster(NULL)`)
- `src/Wopr/AllegianceInterop/AllMessages.h:1358-1403` — managed SHIP_DELETE wrapper (confirms raw-struct layout)
