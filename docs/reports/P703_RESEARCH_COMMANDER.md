# P703 — research commander with native wallets

Implemented in the `codex/p703` working tree. No commits, pushes, rig/221/200v200
runs, bot loadout changes, or rig configuration changes.

## Execution and configuration

The MCP process automatically runs one cached `EnsureCommander` peer per actual
mission side when the server supplies a selected P702 plan. A separate 4 Hz
controller thread keeps connection setup and research out of the combat decision
loop. In a process pool, worker zero owns the research commanders. Side IDs and
loaded civilization/core data drive selection; the implementation contains no
race, station, development or hull identity constants.

P702's `games[].researchPlanDirectory` and `games[].researchPlans` remain the
selection interface. Two read-only port-extension messages expose the actual
selected plan/settings/mission epoch (992), and exact production ship/drone/base
identities (993). The latter distinguishes drones sharing a hull. Neither message
spends money, grants tech or orders a unit. Plans use the existing source-generated
JSON contract inside a bounded compressed briefing. Actual actions use normal
`BUCKET_DONATE`, `AUTODONATE`, and individual drone quick-chat orders.

The new server configuration is:

```json
"showcase": {
  "incomeMultiplier": 1,
  "loopResearchPlans": false
}
```

`incomeMultiplier` accepts finite values from 1 through 10000. It multiplies the
periodic base-plus-owned-station income before treasury distribution. At 1 the
native income formula and payday cadence apply. Native starting money and mining
sales use their existing flows. The multiplier does not accelerate bucket timers,
constructor flight or construction. `loopResearchPlans` repeats the selected plan
after completion; the default leaves a completed commander idle.

Each commander keeps its own player wallet and opts out of auto-donating its
payday share to another leader. It donates up to the smaller of its wallet and
the bucket's outstanding cost, then waits for authoritative debit/funding updates.
There is no synthetic team wallet, direct money assignment or tech-gate bypass.
The `research-cmd` name avoids the usual `mcp-` harness grant prefix. Native-economy
runs require the existing global test-unlock/harness grants to be disabled; both
are explicitly disabled in the loopback proofs, including `IgnoreTechGate=false`.
The manual `invest` tool now accepts development buckets and observes
`DEV_COMPLETED` as well as production completion.

- **Develop:** fund the real, creation-time civ-scaled bucket; advance on observed
  completion or a genuinely obsolete, already-known tech-only development.
- **Station:** reserve a currently available compatible rock before funding;
  adopt the exact constructor produced for that type; use native goto navigation
  for another sector, then BUILD. Advance only when the owned station or its
  successor appears. Active construction is not interrupted by repeated orders.
- **Drone:** fund the exact type within the live native default of four per type;
  completion requires observed new production, independently of elapsed time.
- **Wait:** require both the specified sim-time duration and current tech mask.

Lost/taken rocks release reservations and select another target. A killed
constructor causes another paid purchase. Missing earlier bases are rebuilt,
using an available successor when appropriate, while preserving interrupted step
state. A missing resource, prerequisite, unit-cap slot or wallet balance produces
a wait, never a grant. Reconnect/join state includes development history and live
production identities; mission epochs discard old plan state. Research-only peers
also ready their fresh/rotated seats through the normal lobby messages.

## Native rules and necessary runtime support

All native paths below are relative to `~/Development/Allegiance/src/`.
Commander planning, reservations, scripts, telemetry, and the income multiplier
are explicitly port extensions. The underlying game rules are:

| Rule | Native source | Implementation |
|---|---|---|
| Divide treasury by players; credit own wallet or auto-donate recipient; retain remainder | `FedSrv/fsmission.cpp:2294-2324` | Existing per-player payday flow; commander keeps its own share |
| Base income plus income of currently owned stations | `FedSrv/fsmission.cpp:2514-2546` | Multiplier applied to that periodic accrual before normal payout |
| Player chooses/clears donation target and may gift wallet money | `FedSrv/FedSrv.CPP:5516-5550`, `FedSrv/fsship.cpp:1293-1352` | Normal AUTODONATE opt-out; no direct wallet edits |
| Donation requires a nonnegative affordable amount and current CanBuy; debit actual spend | `FedSrv/FedSrv.CPP:7137-7208`, `Igc/bucketigc.h:281-292` | Existing server donation path and bounded controller donations |
| Development prerequisites are global; production can use one station's local techs; obsolete station successors cannot be bought | `Igc/sideigc.h:237-280`, `Igc/stationtypeigc.h:185-195` | Re-query live development/station state for every action |
| Development cost/time frozen with civ attributes at bucket creation | `Igc/bucketigc.cpp:66-74` | Catalog reconstruction uses civ base GA, rather than researched GA |
| Money-proportional build time; money AND time required for completion | `Igc/bucketigc.h:40-74` | Existing bucket timer; progress/ETA estimates never prove completion |
| Station/drone production buckets reset after completion | `Igc/bucketigc.h:180-239` | Added repeatable production reset and funding sync, needed for replacements |
| Lost prerequisites empty funded buckets; obsolete stations transfer investment to a successor; obsolete research completes without effects | `Igc/sideigc.h:611-675`, `Igc/bucketigc.h:274-300` | Reconcile on tech changes before bucket time advances; no fabricated refund or duplicate development effects |
| Owned stations supply building techs; deleting a base recomputes those bits | `Igc/sideigc.h:133-169,274-287` | Existing live ownership queries; commander rebuilds missing prerequisites |
| Native investment UI checks the live drone type cap; default is four | `WinTrek/loadout.cpp:1108-1120`, `Igc/igc.h:1331` | Exact drone-type identities and live counts, including initial miners |
| Funded station bucket produces a waiting constructor, not a finished station | `FedSrv/FedSrv.CPP:11343-11380` | Existing production path followed by ordinary orders |
| Builder base data supplies rock capability; drill only on arrival | `Igc/shipIGC.h:2051-2057`, `Igc/shipIGC.cpp:2810-2827` | Capability selection, goto/BUILD and physical travel/construction |
| Goto/waypoint navigation supports movement to the command target | `Igc/shipIGC.cpp:2797-2827,4000-4026` | Existing native goto order path before a cross-sector BUILD |
| Constructed station resolves successor, consumes the rock, and exports to its owner | `FedSrv/FedSrv.CPP:11380,11431-11462` | Added completion notification with native asteroid removal and station export |
| Build completion announces its sound/text and quietly consumes the builder | `Igc/buildingeffectigc.h:315-343`, `FedSrv/FedSrv.CPP:12025` | Native completion chat plus SHIP_DELETE/SDR_TERMINATE; no combat death credit |
| True ship deletion reaches the mission even when no player is in the victim sector | `FedSrv/fsship.cpp:196-223`, `FedSrv/FedSrv.CPP:12025` | Corrected SHIP_DELETE routing; death-before-first-export is retained without phantom entities |
| Client removes destroyed/consumed asteroids and construction effects | `clintlib/appmsg.cpp:1692-1715` | Added missing receive dispatch; runtime and late-join asteroid catalogs agree |

These runtime fixes are needed for genuine replacement purchases and for an
existing client to observe completion. Previously, production was one-shot,
funded research could outlive a lost prerequisite, constructed stations were only
learned on a later join, and consumed rocks/builders remained client ghosts.
The P702 bucket ID/catalog compatibility contract is retained.

## Telemetry and proof boundaries

`get_perf_stats.research` and periodic `[research]` lines report each side's plan,
step/action, status, wallet, progress, current-step ETA, completed developments,
owned stations, constructor and reservation. Team chat announces starts,
completions, rebuilding and final idle. Development history is separate from the
bounded sound-event queue; existing native research completion messages/sounds
continue through their normal handlers.

Overall progress counts completed steps plus the current step's estimated stage
progress. ETA uses observed wallet credits and bucket funding/time. Constructor
travel, combat, missing prerequisites/resources and unknown income have an
explicitly unknown ETA. Neither an estimate nor funding alone advances a step.

The live proof uses generated map topology plus fixture asteroids whose capability
bits come from each actual selected plan. This is map content, not a money/tech
or constructor grant. An initial map-only run correctly reached Supremacy and
waited: the current map generator emits generic special rocks (0x10), whereas
these core branch bases need specific capability bits such as 0x40. The commander
does not invent missing resources. P705 must provide an appropriately resourced
showcase map. Full-plan reachability still depends on surviving bases, funds and
available compatible rocks, as P702's downstream contract specifies.

The socket-free tests cover all step types, donation acknowledgement, live caps,
three-side reservations, cross-sector orders, rock loss, killed constructors,
prerequisite rebuilding, wait conditions, completion/looping and wire reset.
A separate loopback proof kills or quietly consumes a constructor in a sector
with no player and requires its home-side observer to learn the death without
receiving remote combat effects. The combined recovery test kills a paused
constructor and removes another prerequisite, then requires its reservation to
be released so the prerequisite can use the only compatible rock.
Economy tests cover native/default and multiplied income, repeat production,
asteroid join-catalog lifetime, prerequisite loss, successor investment migration
and obsolete-development completion without reapplying effects.

The loopback test runs both authored 25/3 selections (`plan_a` / `plan_b`) and
generated 1/2 selections, with multiplier 500, test unlock off, harness off and
`IgnoreTechGate=false`. Both sides must physically build their first branch and
observe it through the commander wire world. It does not claim full-plan or
rig-scale completion. The fixture advances normal 20 UPS simulation ticks faster
than wall time; bucket timers, flight and construction retain their simulation
durations. Reported wall times therefore are test durations, not showcase ETAs.

The existing obsolete-development live fixture previously expected a
`DEV_COMPLETED` event for research whose effects were already known. Native
`Igc/sideigc.h:629-633` only fills that bucket, without completion effects. The
fixture now requires full money/time `BUCKET_STATUS` and no completion/tech/GA
events, retaining its original 400-tick observation budget.

## Verification

`dotnet build AllegianceCore.slnx` completed with **0 warnings / 0 errors**;
see [build.txt](P703_assets/build.txt). The focused phase ran each new/affected
method three times alone, including the constructor, production, asteroid,
payday, tech-gate, development, economic-victory, P702-plan and JSON-default
regressions. It completed 361 invocations matching tests, with 415 passing case
executions and one intermittent first-branch timeout. The failed exact method
then passed three isolated reruns, both matchup cases each time, with unchanged
assertions and deadline. This is recorded in [TEST_FLAKES.md](../TEST_FLAKES.md).
All 20 cases in the dedicated P703 test classes passed at least three isolated
runs and their full suite; see the [case-by-case repetitions](P703_assets/new-test-repetitions.json).

The [regression-red proof](P703_assets/regression-red.json) temporarily disabled
the new production reset and prerequisite reconciliation: five of the eight new
economy cases failed as expected. Sources were restored before the final build
and verification; no regression-disable hooks remain.

The [invocation ledger](P703_assets/verification.json) records project, exact
method filter, outcome, counters, timing and raw TRX/log basenames. The nine
early zero-match helper probes are excluded from the focused invocation count.
[Loopback proofs](P703_assets/loopback-proofs.json) retain each side's real final
telemetry, station inventory, simulation time and wall duration. Raw logs/TRX
remain in `/tmp/p703-final-results/` and sampled live state in
`/tmp/p703-final-proof/`.

Unfiltered suites ran sequentially after the clean build:

| Suite | Passed | Failed in full run | Skipped | Process wall time |
|---|---:|---:|---:|---:|
| Core | 4,581 | 2 | 0 | 9m 38s |
| ClientCore | 1,073 | 0 | 0 | 35m 10s |
| MonoGame | 2,208 | 0 | 3 | 15m 34s |
| Lobby | 19 | 0 | 0 | 1s |

Core's mission-rotation empty-receipt failure and transport raw-peer join timeout
each passed **3/3 exact-method isolated reruns**, without code, assertion or
deadline changes. Both repeat existing P702 observations and are recorded in
[TEST_FLAKES.md](../TEST_FLAKES.md). No deterministic failures remain. The full
ClientCore run passed both P703 matchup cases and both remote-constructor death
cases again. MonoGame completed its 605-second human-flight soak; its three
existing optional `MeshyPackTests` skips reflect the unconfigured private Meshy
asset pack. Their exact names are retained in the invocation ledger.

The full runs use `dotnet test tests/<project> --no-build` without a lane or
corpus filter, with artwork/native paths loaded from `local.env`. Local opt-in
proofs are enabled with `ALLEGIANCE_LIVE_P542_PROOF`,
`ALLEGIANCE_LIVE_P660_SHOWCASE`, `ALLEGIANCE_LIVE_CAPTURE_PROOF`,
`ALLEGIANCE_LIVE_P539_BASELINE` and `ALLEGIANCE_LIVE_P539C_REPRO`, each set to 1.
No duration/size overrides are applied. The explicitly 221/rig/200v200 drivers
remain gated under the brief's exception.
