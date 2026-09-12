# P112 — Ripcord countdown driver + ContinueRipcord revalidation (no teleport yet)

**Branch:** `master` (task started at `357d3dd`, P111)
**Date:** 2026-09-08
**Scope:** the per-tick countdown driver and `ContinueRipcord` revalidation
that P111 deliberately deferred ("neither has anything to attach to without a
per-tick countdown driver"). **It stops before the teleport.** Reaching zero
clamps the countdown and does nothing observable yet — the actual relocation
on zero (`UseRipcord`) is the separate P113 follow-up, because station-anchor
vs ship-anchor relocation are a genuinely different concern from "does the
countdown keep ticking and does it correctly abort when the anchor goes bad."

---

## 1. The per-tick driver — where it lives and how it is shaped

The native drives the countdown inside the ship's own `Update`
(`shipIGC.cpp:270-293`), gated on `m_pshipParent == NULL`, calling the site's
`ContinueRipcord` and decrementing `m_dtRipcordCountdown`. For this port the
task chose to **centralize it as a sector pass** rather than scatter it into
each ship's `Update`: `Sector.ResolveRipcordCountdowns`
(`src/Allegiance.Core/Model/Sector.cs:229`), called from `Sector.Update`
(`Sector.cs:209`) in the same spot the other per-tick passes
(`ResolveMining`, `ResolveWarpCollisions`, …) sit.

For every ship in the sector's model list with an active ripcord
(`IsRipcordActive`) and **no parent** (`ParentShip is null` — the same
not-boarded gate P111's request handler already uses, `Server.cs:2500`, and
the native's `m_pshipParent == NULL`, `shipIGC.cpp:271`):
- anchor legal → `Ship.AdvanceRipcordCountdown(dt)`
  (`Ship.cs:346`): decrement and clamp at zero (`Ship.cs:348-352`).
- anchor illegal → clear the ripcord state (`SetRipcordModel(null)`, the same
  call P111's cancel path uses) and record the ship in
  `_pendingRipcordAborts` (`Sector.cs:90,242-244`) for the host to broadcast.

The `dt` is the sector's per-chunk length (the same `(float)dt` the combat and
mining passes receive). On the 20 upd/s live tests that is 0.05 s per tick, so
a 30 s seed ticks 0.05 s per pump — the observable decrement the tests assert.

### The abort broadcast crosses the sim/wire boundary

`ContinueRipcord`'s failure broadcast is wire work, so the Core sector pass
does not send. It accumulates the aborted ships and the host drains them right
after the mission tick: `Server.SimPass` iterates each sector's
`DrainRipcordAborts()` (`Sector.cs:253`) and emits `RIPCORD_ABORTED(187)`
sector-wide to everyone flying in the ripper's own sector via P111's existing
`SendRipcordSectorWide` helper (`Server.cs:398-413`, helper at
`Server.cs:2605`) — the port's `GetGroupSectorFlying` broadcast
(`FedSrv.CPP:13427-13434`). Running on the sim thread, it may send without
locking exactly like the request handler.

## 2. `ContinueRipcord` revalidation — the "regular" (same-side) branch only

`RipcordAnchorStillLegal` (`Sector.cs:286`) is the port of
`FedSrvSiteBase::ContinueRipcord` (`FedSrv.CPP:13379-13437`).

**The `bAllowAlliedRip` simplification — stated plainly.** The native's two
parallel branches are gated on `MissionDef.misparms.bAllowAlliedRip`
(`FedSrv.CPP:13383`): the allied-allowance branch permits same-side **or
allied** anchors; the "regular" branch is same-side only. **This port does not
decode `misparms.bAllowAlliedRip` anywhere, and it has no
alliance/`AlliedSides` concept at all** — the same class of gap P107
documented for `bEjectPods` and that P111 flagged for `bAllowAlliedViz`. So,
following the P107 precedent, the allied `if` is **dropped unconditionally and
the "regular" branch is implemented**: same-side only, no allied-sides
allowance. Documented in the method's doc comment (`Sector.cs:261-285`), not
silently dropped.

The three gates of the regular branch, each confirmed in the native source:

| Native gate | Native line | Port |
|---|---|---|
| `pship->GetFlag() != NA` (flag carrier) | `FedSrv.CPP:13385` | **collapses to always-passing** — this port tracks no flags, the same precedent P111's request gate used (`Server.cs:2515-2516`) |
| `pship->GetSide() != pmodel->GetSide()` (same-side) | `FedSrv.CPP:13387` | anchor's *current* side read off the resolved model each tick, not cached — native dereferences the pointer (`Sector.cs:302,316`) |
| `pmodel->GetCluster() == NULL` (anchor left its cluster) | `FedSrv.CPP:13388` | anchor no longer present in its `RipcordTarget.ClusterId` sector (`Sector.cs:294-297,305-310,315`) |

The anchor is resolved from the stored `RipcordTarget` `(Ot, Oid, ClusterId)`
against the mission graph (`ship.Mission`), so the check is mission-wide even
though the pass runs on the ripper's sector:
- **Station anchor:** still in the `_stations` list of the requested cluster
  (a captured/destroyed station leaves it) **and** same-side (`Sector.cs:299-303`).
- **Ship anchor:** still present in the requested cluster's model list (a dead
  ship is removed, a warped ship changes sector) **and** same-side
  (`Sector.cs:305-317`).

For a **ship** anchor the identical hull-capability re-check that
`FindRipcordModel` Tier 4 already implements (`Server.cs:FindRipcordModel`,
`shipIGC.cpp:3900-3906`) is performed — a **second read** of the same check
(ripper's `c_habmCanLtRipcord` selects `c_habmIsRipcordTarget` or that plus
`c_habmIsLtRipcordTarget`; the anchor must carry it), not a reimplementation
(`Sector.cs:318-322`). A station anchor always passes (`FedSrv.CPP:13396`).

## 3. What reaching zero does — deliberately nothing observable

`AdvanceRipcordCountdown` clamps at zero and leaves the ripcord **active**
(`Ship.cs:346-352`). This mirrors the native's "no teleport landed" no-op
shape: when `UseRipcord` (the teleport) would run at zero it is not ported
(P113), so the native's `else m_dtRipcordCountdown = 0.0f` arm
(`shipIGC.cpp:284-288`) is exactly what the clamp reproduces — the countdown
stops at zero, the ripcord stays active, no relocation, no abort (the anchor
is still legal). Out of scope by task design: no `UseRipcord`, no
position change, no `bAllowAlliedRip`, no probe anchors, no change to
`FindRipcordModel`'s tier selection, no change to P111's handlers.

**Real test evidence that reaching zero does nothing observable:** the
loopback `Countdown_reaching_zero_does_nothing_yet` test
(`tests/Allegiance.Core.Tests/LiveRipcordTests.cs`) activates a real station
ripcord, nudges the countdown to 0.031 s, pumps six real ticks, then asserts:
`RipcordTimeLeft == 0f`, `IsRipcordActive` still true, `RipcordModel` not null,
**position unchanged** (no teleport), and **no `RIPCORD_ABORTED`** to either
bot. It passed across three consecutive full-suite runs (2492 tests each).

## 4. The two P111 assertion changes (why they were necessary)

Adding a live countdown driver necessarily invalidates P111's two *static*
`Assert.Equal(RipcordSpeed, RipcordTimeLeft)` checks: by the time the
activation message reaches the loopback bot (a few pumped ticks of async
delivery — measured 30 s seed ≈ 28.2 s), the countdown has genuinely ticked
down. A per-tick decrement is the entire point of P112, so those two assertions
were updated to assert the countdown is *seeded-but-ticking* (`> 0` and
`<= RipcordSpeed`), with an explanatory comment. This is the only change to
P111's tests; everything else (the four codecs, the broadcast scopes, the
silent gates, the deny path) passes unmodified.

## 5. Verification

- **Loopback live tests** (`LiveRipcordTests.cs`, `[Collection("LiveNetwork")]`,
  real `Bot` vs in-process `Server` on XXV_simple):
  1. `Countdown_ticks_down_over_real_ticks` — a station-anchored ripcord falls
     0.050 s per pumped tick (measured 28.150 → 27.650 over 10 ticks), still
     active — a live driver, not a one-shot set.
  2. `Countdown_reaching_zero_does_nothing_yet` — zero clamps, ripcord stays
     active, position unchanged (no teleport), no abort. **The one behavior
     this task must NOT add, asserted explicitly.**
  3. `Mid_countdown_anchor_side_change_aborts_sector_wide` — a real ship anchor
     switching side mid-countdown clears the ripcord (`IsRipcordActive` false)
     and broadcasts `RIPCORD_ABORTED(187)` to everyone flying in the ripper's
     sector (ripper + bystander both receive it; the anchor ship in the target
     sector receives nothing) — matching P111's cancel-path assertion style.
     (Connects the three bots one-at-a-time so spawn order — hence which bot is
     the 1054 anchor — is deterministic.)
  4. `Boarded_ship_ripcord_does_not_continue_ticking` — a ripcord armed before
     boarding is frozen once boarded (the `m_pshipParent == NULL` gate): the
     countdown stops advancing and no abort fires.
  5. `Inactive_ship_unaffected_by_ripcord_pass` — a never-requested ship keeps
     `RipcordTimeLeft == 0`, inactive, no messages — no regression from the new
     pass.
- **Full suite, three consecutive runs:** 2492 passing (baseline 2487 + 5 new),
  0 failures each run. The known pre-existing `LiveChatSectorWireTests` timing
  flake (~1-in-8) did not fire on any run.

## 6. New / changed files

- `src/Allegiance.Core/Sim/Ship.cs` — `AdvanceRipcordCountdown` (per-chunk
  decrement + zero-clamp).
- `src/Allegiance.Core/Model/Sector.cs` — `ResolveRipcordCountdowns` per-tick
  pass (+ wired into `Update`), `RipcordAnchorStillLegal` revalidation,
  `DrainRipcordAborts`, `_pendingRipcordAborts`.
- `src/Allegiance.Server/Server.cs` — `SimPass` drains per-sector aborts and
  broadcasts `RIPCORD_ABORTED(187)` via `SendRipcordSectorWide`.
- `tests/Allegiance.Core.Tests/LiveRipcordTests.cs` — 5 new P112 scenarios;
  2 P111 countdown assertions updated (seeded-but-ticking, see §4).

## 7. The immediate next follow-up

**P113 — the actual teleport on `RipcordTimeLeft <= 0`: `UseRipcord`
(`FedSrv.CPP:13440+`).** The countdown driver and revalidation now exist, so
the on-zero trigger has something to attach to. Station-anchor vs ship-anchor
are two distinct relocation mechanics (launch at the station vs consume the
target's energy and relocate), so porting `UseRipcord`'s two branches — plus
its interplay with `AdvanceRipcordCountdown` clearing the model on success —
is the clean next task. The revalidation here already aims abort correctly, so
P113 needs only the successful-relocation arm.
