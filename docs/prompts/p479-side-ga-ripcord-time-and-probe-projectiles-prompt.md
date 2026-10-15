# P479 — side GA scales ripcord time, and probe projectile speed and lifespan

## Why this task exists

The parity pass found side global attributes (GA) computed and broadcast but
never applied. P446 (`d857c0b4`), P467 (`d9541bdb`) and P467b wired flight,
weapon output and ship durability. The remaining unwired `c_ga…` consumers
are listed in `docs/reports/P446_SIDE_GA_FLIGHT_SCALING.md` §"Still-unwired
GA consumers" and `docs/reports/P467_SIDE_GA_SCALE_WEAPON_OUTPUT.md` §3.
Native is at `~/Development/Allegiance/src/`.

**Reuse the established pattern, and don't invent a second one.** P446
added named index constants to `src/Allegiance.Core/Model/GlobalAttributeSet.cs`
(`MaxSpeed = 0` … following `igc.h:585-610`) and `Effective…()` accessors on
`Ship` that multiply the raw data value by `SideGlobalAttributes().Get(index)`
(port of native `GetSide()->GetGlobalAttributeSet().GetAttribute(c_ga…)`).
Add the index constants this task needs in the same style, with an `igc.h`
line citation each, and route every consumer through an equivalent
accessor. If the consumer is owned by the server or the client instead of
the sim, use the same `Get(index)` on the right side's set. The client has
the side's GA from P446's `SIDE_ATTRIBUTE_CHANGE(76)` handling
(`ClientWorld`).

## Scope

1. `c_gaRipcordTime` (20), `shipIGC.cpp:4665`. Apply it to the port's
   ripcord countdown (P454's teleport-probe tier included), on the server,
   and to any client countdown display.
2. The armed/probe projectile path: `c_gaSpeedAmmo` at `probeigc.cpp:367`
   and `c_gaLifespanEnergy` at `probeigc.h:226`. P467 left these because a
   probe fires its own projectile with its own speed, so it isn't a one-line
   change through the shared weapon path. **Armed probes (gun towers) may
   still not fire in the port** (a parity-pass finding, `Sim/Probe.cs:39-45`).
   If they don't, wire the GA into the probe projectile constructor so it's
   correct once they do, test it at that seam, and record "armed probes
   don't fire" as the still-open named follow-up. Don't build probe firing
   here.

## Explicitly out of scope

- Any GA index not listed in this task's Scope. The other remaining ones
  are P475–P479, so don't pre-empt them.
- How developments complete or GA accumulates (already correct, P189).

## Verification

- Per consumer: a test that sets a synthetic non-1.0 multiplier on the
  side's GA and asserts the scaled value. **Run it against unfixed code
  first** and record the failure in the report.
- One end-to-end check through a **real `PCore001` development** that
  changes one of this task's attributes, if one exists. Scan the core and
  list which developments and civs touch each attribute, as P446's report
  did. If none exists for an attribute, say so, and the synthetic test
  suffices.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full `/home/domingo/.dotnet/dotnet test`, 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` (P471) for any remaining
  flake.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P479_SIDE_GA_RIPCORD_PROBES.md`. Include the updated remaining-GA
   ledger, meaning what is still unwired after this task.
