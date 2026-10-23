# P790 — pod rescue on contact (a friendly pod touching a rescue-capable ship is rescued, natively, with no extra centre-distance gate)

**Base:** `master` @ `19b32562` (`client: --borderless (frameless window at the top-left) for full-height recordings`),
worktree `allegiance-pi-p790`, branch `pi/p790`. Changes left in the working tree (not committed).
**Model:** `deepseek/deepseek-flash` (thinking high).
**Scope:** sim only — `src/Allegiance.Core/Model/Sector.cs` (plus a comment-only accuracy fix in
`src/Allegiance.Server/Server.Docking.cs`). No wire, client or bot change.

---

## 1. The native rule (what "rescue" actually is)

Native resolves a ship↔ship collision in `CshipIGC::HandleCollision`'s `case OT_ship`
(`Igc/shipIGC.cpp:1023`), and the **first** thing it does for a friendly pair is the rescue test:

| native | code |
|---|---|
| `shipIGC.cpp:1027` | `bool bFriendly = (GetSide() == pModel->GetSide()) \|\| IsideIGC::AlliedSides(...)` |
| `shipIGC.cpp:1028-1036` | `if (habmHim & c_habmLifepod) { if ((habmMe & c_habmRescue) && igcsite->RescueShipEvent((IshipIGC*)pModel, this)) break; }` |
| `shipIGC.cpp:1038-1041` | `else if (habmMe & c_habmLifepod) { if ((habmHim & c_habmRescue) && igcsite->RescueShipEvent(this, (IshipIGC*)pModel)) break; }` |
| caps | `c_habmRescue = 0x02` (`Igc/igc.h:727`), `c_habmLifepod = 0x04` (`Igc/igc.h:728`) |
| accepted | the `break` leaves the switch: **no** position/velocity correction, **no** damage, **no** sound/jiggle |
| refused | no `break` → falls through to the ordinary bounce; the friendly-lifepod damage exemption (`shipIGC.cpp:1166-1168`) keeps both hulls unharmed |
| site | `FedSrvSiteBase::RescueShipEvent` (`FedSrv/FedSrv.CPP:13033`): awards the rescue, sends `PLAYER_RESCUED`, then `FindTarget(... c_sabmRestart)` (`:13066-13068`) and `return pstation ? DockWithStationEvent(...) : false;` (`:13076`) |

The trigger is the **collision itself**. There is no distance test anywhere in the branch: the hull
contact is the event, and the accept/refuse answer is entirely the site's (a friendly restart station
exists or it does not).

## 2. What the port did before (root cause)

Two independent mechanisms, neither of them native's:

1. **`Sector.ResolveRescueEvents`** (the P115 proximity pass, `Sector.cs:~1290` serial + `~1370`
   parallel) queued a rescue only for a friendly `c_habmLifepod` ↔ `c_habmRescue` pair whose
   **centres** were within `0.5·LengthA + 0.5·LengthB`. Crucially it sampled **tick positions**.
2. **`Sector.ResolveShipShipCollision`** (the real convex-hull physics pass, `Sector.cs:~3165`)
   skipped physics only when `IsRescueEligiblePair` matched — the *same* predicate **including the
   same centre-distance clause**, evaluated at the collision-time positions.

Measured consequences (real `PCore001` hulls, real `.cvh` collision hulls, 20 updates/s):

| hull pair | geometry | old threshold (`0.5·lenA+0.5·lenB`) | centre distance at the **hull contact** | centre distance at the **contact tick's positions** |
|---|---|---|---|---|
| 15 Adv Fighter (`quizfigv2`, len 18.9) → 33 Lifepod (`dreghpod`, len 4) | head-on, 100 m/s | **11.45 u** | **10.404 u** (t = 0.046 s into the step) | **20 u** |
| same | head-on, 80 / 150 / 200 / 300 m/s | 11.45 u | 10.24 / 10.23 / 10.44 / 10.38 u | 16 / 25 / 30 / 40 u |
| same | 6 u glancing, 100 / 150 m/s | 11.45 u | 9.54 / 9.47 u | 16.2 / 11.7 u |

So the hulls touch in the **middle of the swept step**, and at that tick's own positions the centres are
still 15–20 u apart — outside the 11.45 u radius. The rescue therefore was **not queued on the contact
tick**; it landed a tick or more later, only if a tick-position sample happened to fall inside the
sphere. A tick lattice can straddle a narrow sphere window (a glancing pass' window is narrower than
head-on, and the step grows with speed), in which case the sample is skipped entirely — that is the
mechanism by which the pre-P790 path could miss a touched pod. And a pair whose site *refused* never
bounced at all (the physics pass skipped every pair inside the radius, so the two ships ghosted
through each other instead of the native fall-through bounce) — measured, test 3.

Two extra defects rode along in the same code:

* the **serial** proximity loop never re-checked the outer ship after a rescue, so one pod could be
  booked by two rescuers in one tick, while the **parallel** path (P667) booked it once — a real
  serial/parallel divergence;
* the physics pass re-checked a **distance-gated** eligibility predicate rather than the native
  capability/side pair, so "is this a rescue pair" was answered by geometry.

## 3. What changed (after)

**`src/Allegiance.Core/Model/Sector.cs`**

* `ResolveShipShipCollision` now calls **`TryRescueOnContact(ship1, ship2)` first** — before the
  zero-relative-velocity early-out and before `Separate`/physics. The port of `shipIGC.cpp:1023-1044`:
  capability/side pair → site's `RescueShipEvent(pod, rescuer)`; accepted → `QueueRescue(...)` and
  `return` (no bounce, no damage, no `CollisionEvent`); refused → `return false` and the ordinary
  elastic bounce runs, with the existing friendly-lifepod damage exemption.
* `IsRescueEligiblePair(a, b, pa, pb)` → **`IsRescueCapablePair(a, b, out rescuee, out rescuer)`**: the
  native side + capability test and **nothing else — no distance clause**.
* New `QueueRescue(rescuee, rescuer)`: appends to `_pendingRescues`, latches both ships in the new
  shared `_shipRescueDone`, and never appends the same pair twice (the contact path and the proximity
  path can both see it inside one `Mission.Update`'s chunks).
* `_shipRescueDone` is cleared at the top of `Sector.Update`, immediately before the ship-ship pass
  (the first of the two rescue producers, the proximity pass being the last) — one rescue per ship per
  tick, shared by both producers, reused (no per-tick allocation).
* `ResolveRescueEvents` (serial **and** parallel) is kept as the **fallback** for pairs that overlap
  without a detected hull collision; it now uses `IsRescueCapablePair`, the shared latch and
  `QueueRescue`, and the serial loop got the missing `if (done.Contains(a)) break;` so a pod can never
  be booked twice in one tick. The parallel path still qualifies read-only in parallel and applies the
  latch + queue serially in the exact `(i,j)` scan order ⇒ identical result for any thread count.
* All comments that cited the old "in proximity" rule / `shipIGC.cpp:1027-1042` were rewritten
  (`ResolveShipShipCollisions` remarks, `ResolveRescueEvents` remarks, `Update`'s pass comment,
  `DrainRescues`, `_pendingRescues`).

**`src/Allegiance.Server/Server.Docking.cs`** — comment only: `PerformRescue`'s doc now says the pair is
produced by the hull-contact trigger or the proximity fallback, and cites `FedSrv.CPP:13033-13076`.

Not changed (out of scope, as the brief says): station rescue (`ResolveStationRescueEvents`), rescue
probes (P779b/P780), bot pod behaviour, nanite/repair, allied sides, and every collision
physics/damage constant.

## 4. Tests

New file `tests/Allegiance.Core.Tests/P790PodRescueOnContactTests.cs` (5 tests, real `PCore001` hulls,
real `.cvh` collision hulls via `CollisionHulls`, real production site gate
`ServerHost.RescueShipEvent` with `RescueDestination`):

| # | test | what it proves | on the **unfixed** code |
|---|---|---|---|
| 1 | `Adv_fighter_head_on_contact_rescues_the_pod_that_tick_with_no_bounce_or_damage` | hull 15 at 100 m/s head-on into hull 33: the pair is rescued **on the contact tick**, fighter/pod velocities untouched, `LastShipShipResolved == 0`, both fractions 1. Prints "centres 20 u at the contact tick > old threshold 11.45 u; actual hull-contact distance 10.404 u" | **FAILS** (`Assert.Single(rescues)` — empty: the proximity pass cannot see them at that tick) |
| 2 | `Adv_fighter_glancing_contact_rescues_the_pod_that_tick` | 6 u glancing contact: rescued on the contact tick, velocity `(0,100)` intact, pod not launched, no damage | passes (regression guard: the proximity pass still caught it a tick later) |
| 3 | `Refused_rescue_falls_through_to_the_bounce_with_no_damage_to_either` | site answers native's `false` (no friendly restart station, `FedSrv.CPP:13076`): nothing queued, physics **resolves** a bounce (fighter vz 100 → 56.25), both fractions stay 1 | **FAILS** ("a refused rescue must fall through to the ordinary collision resolution") |
| 4 | `Enemy_pod_is_never_rescued_and_collides_normally` | different-side pod: 0 rescues, physics resolves, the pod takes real damage/dies (no friendly exemption) | passes (guard) |
| 5 | `Serial_and_parallel_produce_identical_rescue_sets_for_any_thread_count` | 6 groups in one sector — 2 head-on contacts, 1 glancing contact, 1 proximity-only pair (11 u, hulls not touching), 1 enemy pod, and **1 pod with two rescue-capable ships touching it** — over 20 ticks the drained rescue set (5 pairs, one per pod, order included) is identical with `ParallelSectorSim` off and on at 1/2/4/8 workers | **FAILS** (serial path books the two-rescuer pod twice: 6 rescues instead of 5 — the serial/parallel divergence) |

Test 1's helper rebuilds the physics pass's own `CollisionDetector` step and reports the exact
contact-time centre distance, so the report's 10.404 u number is produced by the test itself.

Existing tests I had to edit: **none**. `ShipShipCollisionTests` (incl. its same-side rescue-pair
scenario 4), `P754CallbackTests`, `P667ParallelSectorSimDeterminismTests`, `LiveRescueTests`,
`LiveStationRescueTests`, `LiveInSpaceEjectPodAliveTests`, `P590PodRescueTests`,
`GhostShipSectorMembershipTests` all pass unchanged (see §5).

## 5. Verification (light gate)

| gate | result |
|---|---|
| whole-solution build (`dotnet build AllegianceCore.slnx`) | **0 warnings, 0 errors** |
| `tools/build-tools.sh --drop-bins` | **all 70 tool projects built**, 0 warnings / 0 errors |
| AOT publish `Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true` | **succeeded**, no IL2026/IL3050 |
| new tests 3× alone (`P790PodRescueOnContactTests`) | **5/5 passed ×3** |
| affected Fast tests 3× alone (`ShipShipCollisionTests`, `P754CallbackTests`) | **8/8 ×3 each** |
| affected Live-loopback rescue tests 3× alone (`LiveRescueTests`, `LiveStationRescueTests`, `LiveInSpaceEjectPodAliveTests`, `P590PodRescueTests`, `GhostShipSectorMembershipTests`) | **17/17 ×3** |
| **one** Fast-tier suite (`tools/run-tests.sh --tier fast --no-build`) | **PASS — 9971 tests, 280.9 s** (Lobby 24, MonoGame 3214 + 3 not-executed, ClientCore 1650 + 2 quarantine, Core 5068 + 10 quarantine); re-run on the final tree after the last test-only edits: **PASS — 9971 tests, 281.8 s** |
| showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`) 5× | see below |

Showcase 5× (alone, `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`):
**5/5 passed** — 1 m 55 s, 1 m 50 s, 1 m 50 s, 1 m 50 s, 1 m 46 s (exit 0 each). One extra detailed
run printed the base-rate comparison the test itself asserts against:
`P700 local 15v15: 109.0s, samples=1089, reached=29/30, longest speed<1 with target=2.004s, shots=2428,
hits=1210, kills=11` — no bot parked (`longest` 2.0 s vs the 5 s failure threshold), `idleWithTarget`
0 (asserted), 29/30 reached, real gunfire/damage observed. Note the machine also carried two other
agents' long Live runs during this window (`allegiance-pi-p778a` `P778ABomberPushLiveTests`, 1000 s+),
which is the load the test is sensitive to — it still passed every run.

Flakes: **none observed**; nothing added to `docs/TEST_FLAKES.md`. (The first showcase attempt was
abandoned by me mid-run — I killed it to avoid rebuilding Core while it was executing — not a test
flake, and it is excluded from the 5 runs above.)

## 6. Rig recipe for Claude (221, before/after)

The rig is the only place that can confirm the session symptom (`scratchpad/hero/
session-classic-volumetric-171954/server.out`, 2026-10-22: `[pod-trip]` windows with `rescued=0` and
~72 pods active). Nothing was run against 221 from this lane.

1. **Before** (current master build, i.e. without this working tree): start the 90v90 Alderan arena as
   in that session, fly a hull with `c_habmRescue` (Adv Fighter, hull 15) into a cluster of friendly
   ejected pods at 80–300 m/s, and record
   * every `[pod-trip] window … rescued=… docked=… active=…` line,
   * every `[rescue]` line (`Server.PerformRescue`: "ship N (lifepod) rescued by ship M -> PLAYER_RESCUED(16) … placed docked at station …"),
   * the pod ids and the `[pod-trip]` terminal outcome for the pods that were touched.
2. **After** (this working tree — `Sector.cs` contact trigger): repeat the same flight and the same
   capture:
   * expect the pods to be `rescued` in the **same** window (same tick) in which the fighter's hull
     touches them, with `[rescue] … rescued by ship M` arriving at most one server tick after contact;
   * expect `[pod-trip]` `rescued=` counts to climb out of 0 for the flown-into pods;
   * a pod whose side has **no** friendly restart station must now *bounce* (and show no rescue); if
     the after-run still reports `rescued=0`, look for those refusals first — `ServerHost.
     RescueShipEvent` → `Server.FindFriendlyStation` returning null is the other half of the native
     rule (`FedSrv.CPP:13066-13076`) and is deliberately still the production gate.
3. If the after-run shows rescues but the pods do not end up docked, check the drain step
   (`Server.Broadcast.cs` `Step("rescues")` → `Server.PerformRescue`), which this task did not touch.

## 7. Open points / what I could not confirm

* The brief's diagnosis says the pre-P790 physics pass judged the pair "not eligible" and **bounced**
  the ship off the pod. In the measured geometry that specific sequence does not happen: the
  detector's sphere gate opens *before* the hulls touch, so at the contact instant the centres
  (10.2–10.4 u) are still inside the old 11.45 u radius and the old code skipped physics. What the old
  code did instead — measured — is deliver the rescue **late** (a tick or more, because the proximity
  pass samples tick positions that are 15–40 u away at the contact tick), with a constructed path to
  never at all when the tick lattice straddles the sphere window; and the **refused** case ghosted
  through instead of bouncing. Both of those are what this change fixes; the production `rescued=0` may
  additionally involve the site gate (no friendly restart station found for the pod's side), which is
  why §6.2 says to look for refusals first.
* `ResolveRescueEvents` remains as the proximity fallback (the brief asks for it): in these hulls
  there is a thin shell (centres 10.4–11.45 u) where the pods are rescued by proximity without the
  hulls touching. That is *earlier* than native, by design of the fallback; the contact path is now the
  trigger for everything the hulls actually touch.
* Station rescue and probe rescue are untouched and still fire independently (a pod can be rescued by
  a station/probe in the same tick it is rescued by a ship — the shared latch is only for the
  ship↔ship producers, as before).

## 8. Evidence

Raw logs (outside the repo):
`~/allegiance-evidence/P790/` — `fast-tier.log`, `fast-tier-final.log`, `build-tools.log`, `aot-publish.log`,
`showcase-1..5.log`, `showcase-detailed.log`, `showcase-summary.txt`, `prefix-build.log` +
`prefix-tests.log` (the same 5 tests against the **unfixed** `Sector.cs`: 2 passed, **3 failed** —
head-on, refused, determinism), `tests-current.log`.

Files changed:
* `src/Allegiance.Core/Model/Sector.cs` (production)
* `src/Allegiance.Server/Server.Docking.cs` (comment only)
* `tests/Allegiance.Core.Tests/P790PodRescueOnContactTests.cs` (new)
* `docs/reports/P790_POD_RESCUE_ON_CONTACT.md` (this report)
