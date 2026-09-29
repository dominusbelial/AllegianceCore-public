# P416 — Turret-Seat Keys (Promote / Eject / Seat-Switch)

## TL;DR

This task started as a **survey**, not a known gap: is there a real, player-facing
way to enter / switch / leave a turret seat in this port, and are the native
turret-seat keys (`TK_SwapTurret1-4`, `TK_PromoteTurret1-4`, `TK_Suicide`-as-eject)
bound to anything? The investigation corrected the brief's central assumption and
found one genuine, in-scope client gap, which this task fixes:

- **The real turret/passenger machinery is already complete and player-reachable**
  on both server and client — seat *entry* is the docked TeleportPane "board ship
  as gunner" flow (P273), which sits behind the **docked-only** boarding gates the
  server faithfully ports from native (`FedSrv.CPP:2342-2345`).
- **The brief misreads native's `TK_SwapTurret1-4`.** Those keys are *weapon-swap*
  keys (swap the gun mounted in turret slot N of your OWN hull, `WinTrek.cpp:9134`
  → `SwapPart(ET_Weapon, id+nFixedHardpoints)`), **not** "take a specific turret
  seat". The real in-flight seat-move key is `TK_OccupyNextTurret` — which depends
  on a native boarding branch ("Moving within the same ship",
  `FedSrv.CPP:2338-2339`) that **this port's `PerformBoard` does not port**, so an
  in-flight seat-switch key would hit the both-docked gate and always `BOARD_NACK`.
- **Eject-from-turret is already satisfied by the existing `K` (suicide) key.**
  Native's "Eject From Turret" *is* the suicide command on a boarded child
  (`FedSrv.CPP:6335-6350`); the port's `K` already sends `SendSuicide`. The
  "(Press 3 times)" label is an anti-accident UI timer, judged **over-specified**
  for this port's single-press `K` (P222) and deliberately **not ported**.
- **The one real, in-scope gap: `PromoteTurret1-4`.** The server's promote is
  complete and live-tested (P140 / `HandlePromote`), and it is the one turret-seat
  command **not** docked-gated (a commander can promote a boarded gunner in
  flight) — but the MonoGame client had **no `SendPromote` at all**, so a real
  player could never promote. **This task adds it.**

---

## 1. The real turret/passenger flow (traced)

### Server (complete, native-faithful, live-tested)

- **Board / leave** — `FM_C_BOARD_SHIP(103)` →
  `Server.Dispatch.cs:1474` → `HandleBoardShip` (`Server.ShipCommands.cs:141`)
  → `PerformBoard` (`Server.Docking.cs:74`) / `PerformLeave` (`Server.Docking.cs:205`).
  Port of `FedSrv.CPP:6385-6418` (`case FM_C_BOARD_SHIP`) and the internal
  `BoardShip`/`LeaveShip` (`FedSrv.CPP:2322-2475, 2277-2320`).
  - **Docked-only gate:** `PerformBoard` refuses unless *both* ships are docked
    (`Server.Docking.cs:91-95`, native `FedSrv.CPP:2342-2345`); `PerformLeave`
    refuses unless the carrier is docked (`Server.Docking.cs:207-211`).
  - Slot selection: `ChooseTurretSlot` (`Server.Docking.cs:1263`, `FedSrv.CPP:2347-2402`);
    gunner binding via `Weapon.SetGunner` (`Server.Docking.cs:147-153`, P109).
- **Promote** — `FM_C_PROMOTE(190)` → `Server.Dispatch.cs:1502` → `HandlePromote`
  (`Server.ShipCommands.cs:283`, P140) → `Ship.Promote` (`Ship.cs`, port of
  `CshipIGC::Promote`, `shipIGC.cpp:3509-3591`); broadcasts `FM_S_PROMOTE(189)`.
  Gates: mission started + the *requesting ship's own* boarded child at the mount.
  **Not docked-gated** — works in flight.
- **Turret firing/aim** — `FM_C_ACTIVE_TURRET_UPDATE(99)` /
  `FM_C_INACTIVE_TURRET_UPDATE(100)` → `HandleActiveTurretUpdate`
  (`Server.ShipCommands.cs`, P109), gated on the sender being a boarded gunner
  (`Server.ShipCommands.cs`; native `FedSrv.CPP:4873-4875`, `shipIGC.cpp:3349-3392`).

The state model is real: `Ship.ParentShip` (nullable), `Ship.TurretId`
(`short?`, in `[MaxFixedWeapons, MaxWeapons)`), `Ship.ChildShips`
(`Ship.cs:1841-1861`), and the passenger blob is serialized on the wire
(`PassengerData.cs`, `LoadoutChange.cs`, `Server.Combat.cs:381-391`).

### Client (complete for board, absent for promote)

- `SendBoardShip(short sidParent)` — `ConnectionSession.cs:345`; the docked
  TeleportPane ("board ship as gunner") drives it (`Game1.Panes.cs:311-314`, P273).
- `SendSuicide()` — `ConnectionSession.cs:291`; bound to `K` (`Game1.FlightInput.cs:409-415`, P222).
- `SendActiveTurret` / `SendInactiveTurret` — defined (`ConnectionSession.cs:383,391`)
  but **never called** by the MonoGame client (gunning is not wired client-side; out
  of this task's "seat entry/control only, no turret-firing" scope).
- `SendPromote` — **did not exist**. ✅ Added (this task).

---

## 2. What native's turret-seat keys really do

| Native key | Native behavior | Port status |
|---|---|---|
| `TK_SwapTurret1-4` | **Swap the WEAPON** in turret hardpoint N of your own hull (`WinTrek.cpp:9134` `SwapTurret` → `SwapPart(ET_Weapon, id+nFixedHardpoints)`), only when not boarded. **Not a seat key.** | Out of "seat-entry/control" scope; the brief's "take a specific turret seat" reading is incorrect. |
| `TK_OccupyNextTurret` | Re-board the CURRENT parent to grab the next free turret seat (`WinTrek.cpp:10453-10463` → `BOARD_SHIP(parent)`). | Blocked in-flight: native's "Moving within the same ship" branch (`FedSrv.CPP:2338-2339`) is **not** ported in `PerformBoard`, so re-boarding in flight hits the both-docked gate → `BOARD_NACK`. Bigger feature (boarding-seam change). |
| `TK_PromoteTurret1-4` | Captain hand-off: promote the boarded gunner in seat N of the flown ship (`WinTrek.cpp:9583-9601`, `mountidPromoted = (key−TK_PromoteTurret1)+MaxFixedWeapons`). | Server complete (P140); **client had no sender — fixed here.** |
| `TK_Suicide` | Doubles as **Eject From Turret**: suicide is only allowed on a boarded child (or cheater), `FedSrv.CPP:6335-6350`; the 3-press is an anti-accident UI timer. | Already satisfied: the port's `K` → `SendSuicide` → `KillShipEvent` on the boarded child = native's eject. 3-press not ported (over-specified for the single-press `K`). |

### Why the in-flight seat-switch/eject keys can't work here (yet)

Because both docked boarding is a faithful port, and native's in-flight reseat
branch (`pshipBoard == pshipOldParent`, `FedSrv.CPP:2338-2339`) is absent from
`PerformBoard`, *any* in-flight `BOARD_SHIP` (seat-switch or eject-leave) is
rejected by the docked gate. Enabling it would be a **server boarding-seam change**
(add the same-parent reseat exemption + an in-flight leave path) — larger than a
key binding and adjacent to the boarding/docking seams. It is scoped out here and
flagged as a candidate follow-up.

---

## 3. What was built (the real, in-scope fix)

**Goal:** make the `PromoteTurret1-4` keys work for a real MonoGame player —
the one turret-seat command the server supports in flight but the client had no
sender for.

1. **`ConnectionSession.SendPromote(short mountidPromoted)`**
   (`src/Allegiance.ClientCore/Session/ConnectionSession.cs`) — new sender wrapping
   `FM_C_PROMOTE(190)`, mirroring `SendBoardShip`. Previously the server feature
   (P140) was only reachable from tests / other clients.

2. **`Flight.PromoteTurretKey.MountForSeat(int seat, short maxFixed, short maxWeapons)`**
   (new pure resolver, `src/.../Flight/PromoteTurretKey.cs`) — the native
   `mount = (key − TK_PromoteTurret1) + GetMaxFixedWeapons()` mapping, refusing
   seats outside the flown hull's turret range (native `WinTrek.cpp:9593` guard).
   Headless-unit-testable, matching the `RipcordKey` (P414) convention.

3. **D1–D4 key bindings** (`src/.../Game1.FlightInput.cs`) — edge-triggered,
   composing-gated; each resolves the own hull's `MaxFixedWeapons`/`MaxWeapons`
   from the shop catalogue and sends `SendPromote(MountForSeat(seat, …))`. A seat
   the flown hull doesn't own is refused client-side (no wire message) with a
   system-line hint — mirroring native's guard. Digits 1–4 are unclaimed in
   flight (verified in the P257 binding-table audit).

4. **`KeyBindingTable.cs`** — documented `1-4` (Promote seat N to command) and the
   `K` eject annotation (pure-ASCII, group count kept at 8 so the H-overlay layout
   contract is unchanged).

No combat / damage / turret-firing logic was touched (out of scope).

---

## 4. Verification (real proof)

**Whole-solution build:** `dotnet build AllegianceCore.slnx -c Release` →
**0 warnings, 0 errors**.

**New tests:**
- `PromoteTurretKeyTests` (8 pure unit): wire encode round-trip of `PromoteRequest`
  (mount fidelity + fmid rejection), and the seat→mount rule (seat 1 = first turret
  mount; seat N = `MaxFixedWeapons+N−1`; out-of-range and turret-less hulls refused).
- `PromoteTurretKeyLiveTests` (1 live, `[Collection("LiveNetwork")]`): a real
  2-side server spawns a 792 turret-capable carrier + a 504 gunner; the gunner
  boards via the production `SendBoardShip`; the commander's **production
  `SendPromote`** (the exact method the D1-4 keys call) promotes the gunner over
  the real wire. Server state verified: gunner freed on the carrier's hull (792),
  old commander demoted into the exact vacated mount, gunner now carries the
  commander as a child.

**Full test suite (≥2×):**
- `Allegiance.ClientCore.MonoGame.Tests`: **803/803 pass** twice (includes the 9 new
  tests above). Green.
- `Allegiance.Core.Tests`: 3497 total; only the **pre-existing rotating live-wire
  timing flakes** (7 failures on run 1, 3 on run 2 — different tests each time:
  `LiveTurretGunnerBindingTests`, `GameOverPlayersWireTests`, `AddPartTests`, …).
  None are in code touched here (Core.Tests does not reference the changed
  ClientCore/MonoGame assemblies).
- `Allegiance.ClientCore.Tests`: 227 total; **two deterministic failures
  (`ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
  `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`) that I proved are
  pre-existing** — they fail identically with my changes stashed on clean `master`,
  and `SendPromote` is a purely additive method with no effect on their paths.

---

## 5. Decisions

- **Promote (built):** the one real, in-scope turret-seat key. The port *does* have
  a captain/gunner distinction (`Ship.ChildShips` + `TurretId` + the complete P140
  role-swap), and promote is the one such command the server allows in flight, so
  the missing client sender + keys are the clean, verifiable fix.
- **Eject (documented, not coded):** the existing `K` already performs native's
  eject-from-turret (suicide on the boarded child). The native "(Press 3 times)"
  confirmation is an anti-accident UI timer; this port's `K` is single-press
  (established P222 shape), so porting a 3-press-with-3-second-window would be
  over-specification. Recorded in the binding table.
- **Seat-switch / in-flight board (scoped out):** requires porting native's
  "Moving within the same ship" reseat branch into the boarding seam (a
  `PerformBoard`/leave change, bigger than a key binding). Flagged as a follow-up;
  documented here with exact native citations.
- **`TK_SwapTurret1-4` (corrected):** a weapon-swap on your own turret mounts, not
  a seat key — outside "seat entry/control" scope and the brief's reading was
  incorrect.

Files changed: `ConnectionSession.cs`, `Game1.FlightInput.cs`, `Game1.cs`,
`KeyBindingTable.cs`, new `Flight/PromoteTurretKey.cs`,
`PromoteTurretKeyTests.cs`, `PromoteTurretKeyLiveTests.cs`, this report.
Committed on `master` (not pushed).
