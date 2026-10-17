# P733 — bounded tech finales and showcase research speed

## Implementation

Research finales now reserve pilots even when their requested bomber role is
currently flying a legal scout/fighter fallback. On a normal docked relaunch they
try the legal affordable bomber selection, then P704's station-damage search over
available player hulls. A verified station-damaging fallback receives the finale's
station-attack duty. If no available loadout damages stations, they can launch a
legal ordinary combat loadout; the reason is logged explicitly. Unlocked mode
retains its bomber-only selection, reservation order and duty behavior.

The rig's initial tech masks for civs 25 and 3 provide no legal station-damaging
strike option. Therefore substituting a starting fighter alone cannot guarantee
that a finale ends. The actual maturity trigger also sends port-extension
`SHOWCASE_FINALE` (994), scoped to the current research epoch and the selected
strike side. The server accepts it only in research mode from a seated peer on
that side in a started mission. It arms a simulation-time deadline once; retries
cannot postpone it. Stale epochs, another side's requests and unlocked mode are
ignored. Natural game over can win the race. At expiry, the server logs
`Research showcase finale deadline: best available legal loadouts did not end the match`
and enters the existing `DeclareGameOver` path, including `Stage Over`, results,
reseating, tech reset and automatic restart. This is an explicit showcase outcome,
not a fabricated station kill. The triggering strike side wins; with `both`, the
first participating selected side supplies the request.

`showcase.finaleTimeoutSeconds` defaults to **300**, with finite range **1–3600**.
The bound starts when the finale actually fires, after the existing age, relaunch
share and recent-kill gates. It does not remove those maturity requirements.
A matching MCP executable must be deployed with the server.

`showcase.researchTimeScale` defaults to **1**, with finite range **1–64**.
It multiplies elapsed bucket research/production work and divides the constructor
station-transformation duration. Static core definitions, bucket prices and
`TimeToBuild` values stay intact. Money ceilings, purchase/tech gates, constructor
flight and authored plan waits remain unchanged. Only `techMode: "research"`
applies the scale; unlocked mode and ordinary servers use 1. Boot prints
`[boot] showcase.researchTimeScale = N`. The effective scale travels in the
read-only research briefing so commander progress estimates and ETAs agree.
Setters preserve omitted source-generated JSON defaults; P686's recursive guard
is included in validation. This is a showcase/benchmark accelerator, not native
gameplay.

## Native rule references

All native paths below are relative to `~/Development/Allegiance/src/` and were
read from the local native checkout. Mode selection, research plans, loadout
ranking, timer acceleration and the bounded finale outcome are port extensions.

| Native rule preserved underneath the extension | Native file:line |
|---|---|
| Funded bucket work accrues elapsed time, needs money and time to complete, and is capped by money's proportional share | `Igc/bucketigc.h:40-74` |
| Constructor effect transforms the asteroid at the shrink boundary, 30 seconds after start | `Igc/buildingeffectigc.h:19-22,50-54,312-316` |
| Constructor begins construction only after compatible-asteroid arrival | `Igc/shipIGC.cpp:2795-2815` |
| Completed construction consumes the constructor and builds the station | `Igc/buildingeffectigc.h:328-348` |
| Weapon and equipment mount masks come from the hull hardpoint or equipment slot | `Igc/hullTypeIGC.h:163-175` |
| Hull/part purchases use station-local plus side tech and respect successors | `Igc/stationIGC.h:383-433` |
| Match start seeds the treasury, performs the initial payday and anchors the next payday at start + the core payday interval | `FedSrv/fsmission.cpp:1992-2004,2253,2278` |
| Payday divides treasury money among actual players and credits wallets/donation recipients | `FedSrv/fsmission.cpp:2294-2324` |
| Explicit `iMaxImbalance = 0x7fff` disables the running-match team-balance limit | `FedSrv/fsmission.cpp:4612-4623` |
| Game-over results carry the reason and winning side | `FedSrv/fsmission.cpp:3063-3085` |
| Game-over resets the mission; automatic restart sends a fresh start time | `FedSrv/fsmission.cpp:3524-3527,3539-3546` |

The P705 handoff's `m2xspiderw.igc` map also bakes a garrison identity different from the selected
civilization's initial garrison. The commander was treating that as a missing
prerequisite and rebuilding its initial base before starting the plan. Research
map preparation now aligns the first restart station on each side with the
selected civ's initial station identity, matching the plan's starting assumption.
This is research-only showcase map preparation; unlocked file maps retain their
station identities. Native generated maps choose the civ's initial station at
`Igc/mapmakerigc.cpp:926-932`; aligning a preauthored showcase map is a port extension.

## Measurements and validation

The whole-solution [build](P733_assets/build.log) passes with **0 warnings / 0 errors**.
The [invocation ledger](P733_assets/verification.json) records the final focused,
gate and full-suite commands and outcomes; raw logs/TRX are in
`/tmp/p733-verification/`. The [summary](P733_assets/summary.json) indexes the
measurements and suite/gate results.

The timer/economy benchmark uses the rig matchup **civs 25 vs 3**, authored
`plan_a` on both sides, `m2xspiderw.igc`, normal player-ready/countdown startup,
20 UPS and dilation off. Two real research peers and twenty docked fleet peers
provide a quiet 10v10 population. The P703 controller receives authoritative
snapshots every 0.25 simulation seconds; donations and constructor orders go
through real loopback messages and server handlers. Constructors still fly and
build in the simulator. No money, technology, completed station or production
ship is injected.

To model the rig's smaller per-player payday without opening 212+ sockets, the
11-wallet-per-side benchmark uses `incomeMultiplier = 5000 × 11 / 107`
(**514.018691588785**). This conservatively matches a commander sharing the rig
payday with 105 combat pilots plus control peers; the other rig side has one
fewer control peer. The initial payday is the ordinary 22-seat startup payday.
These are quiet headless expectations, not a 105v105 combat certificate;
constructor deaths, repairs and battlefield interference can reduce progress.

All **three isolated repetitions** produced the same completed-plan-step counts:

| Research scale | Civ 25 steps / 157 | Civ 3 steps / 181 | Civ 25/3 end wallets (approx.) |
|---:|---:|---:|---:|
| 1 | 15 | 15 | 665k / 773k |
| 4 | 43 | 43 | 564k / 656k |
| 8 | 74 | 71 | 410k / 559k |
| 16 | 109 | 115 | 166k / 343k |

The [raw observations](P733_assets/measurements/) retain completion percentages,
current steps, stations, developments, wallets and constructor state. Counts are
the floor of overall plan progress times plan length, excluding a partially
completed step. Increasing the scale from 8 to 16 gives less than twice the progress because
constructor flight is unchanged. The substantial remaining wallets confirm that the measured
end state is not money-starved. The rig delta below uses **8**; **16** is available
if Claude prefers faster progression for both sides.

The required **≥5×** test compares a funded bucket-heavy opening: normal startup,
60 seconds of native payday warmup, then 90 seconds of research at the same
150-second match age. Scale 1 completes **1/1** steps; scale 8 completes **7/7**,
a **7×** gain on both sides in **3/3 isolated runs**. The 30-minute table includes
unscaled constructor travel and is reported separately: its scale-8 whole-plan
gains are **4.93× / 4.73×**, rather than 7×. The server unit tests
also verify unchanged timer definitions, exact elapsed work at scale 1 versus 8,
and constructor durations of 30 versus 3.75 seconds.

Exploratory fixtures that set `Started` directly or enumerated build rocks without
the live commander's own-home-sector/asteroid-ID priority were excluded. The
measurements above use normal startup and the same rock ordering as the live
controller. Every new or affected selected method has
three passing isolated invocations: **189 invocations / 384 passing case executions / zero failures**. This
includes P686 defaults, P705 mode/pair selection, P703 economy/commander,
P704 selection and real purchase bursts, existing unlocked finale behavior,
request guards and the new two-match no-bomber finale proof.

The new [no-bomber finale proof](P733_assets/no-bomber-finales.json) passed **3/3**,
each with two `Stage Over` events and automatic subsequent starts. P705's
unchanged civ 25/3 10v10 proof passed **3/3**, with both complete research/upgrade
matches reaching game over each time:
[run 1](P733_assets/p705-civ25-3-1-timeline.json),
[run 2](P733_assets/p705-civ25-3-2-timeline.json),
[run 3](P733_assets/p705-civ25-3-3-timeline.json).

The unchanged never-parks gate passed **10/10**. Its largest low-speed-with-target
interval was **2.114 seconds**, below the five-second assertion;
every run observed real shots, hits and kills. The [per-run metrics](P733_assets/never-parks-metrics.json)
are retained. No gate failure required a base-rate comparison.

Exactly two full suite passes are scheduled, without lane/corpus filters, in
Core → ClientCore → MonoGame → Lobby order. The existing `local.env` supplies
the artwork and native checkout paths. Local live proofs use
`ALLEGIANCE_LIVE_P542_PROOF=1`, `ALLEGIANCE_LIVE_P660_SHOWCASE=1`,
`ALLEGIANCE_LIVE_CAPTURE_PROOF=1`, `ALLEGIANCE_LIVE_P539_BASELINE=1` and
`ALLEGIANCE_LIVE_P539C_REPRO=1`; rig-only proof gates remain off. Build uses
`NUGET_HTTP_CACHE_PATH=/tmp/p733-nuget-http` to keep restore cache writes inside
the sandbox.

The first Core assembly completed **4,785 pass / 0 fail / 0 skip**. The initial
ClientCore assembly attempt exited without a TRX or console completion summary.
No assertion failure or cgroup OOM event was recorded; its cause is unestablished.
Its partial logs/proofs are archived under `/tmp/p733-verification/interrupted-client1/`
and the ledger labels it `interrupted-suite-1-Allegiance.ClientCore.Tests`, not a
completed suite pass. Its completed retry used normal console progress,
blame diagnostics and an exit-status record written before ledger processing.
This interrupted attempt is separate from the two required complete suite sweeps.
The retry completed with **1,190 pass / 2 fail**: both P496 capture-proof methods
failed, and each failed **3/3** alone. These were deterministic fixture failures,
not load flakes. The capture scenario intentionally seats defenders before
attackers, but its default running-match team balancing moved a defender onto
the attacking side, leaving target-station lookup on the wrong home sector.
The fixture now explicitly sets `MaxTeamImbalance = 0x7fff`, using the native
balance-off setting cited above. Scenario size, timeout and assertions are
unchanged. The whole solution was rebuilt with **0 warnings / 0 errors** after
this correction. Final isolated and full-suite results follow when those runs finish.

## Exact rig config delta — Claude deployment

Update the rig's existing showcase object with these fields; preserve unrelated
configuration and its existing civ 25/3 matchup, 105v105 fleet and automatic next
match settings. A field-level RFC 6902 patch for an existing `showcase` object is
also available [here](P733_assets/rig-showcase-delta.json):

```json
{
  "showcase": {
    "techMode": "research",
    "incomeMultiplier": 5000,
    "researchTimeScale": 8,
    "finaleTimeoutSeconds": 300,
    "loopResearchPlans": false
  }
}
```

Keep the existing MCP scenario request:

```json
{"preset":"showcase","size_scale":7,"staging":false,"refill":true,"finale_after_minutes":30,"finale_side":"0"}
```

Deploy matching rebuilt server and MCP binaries. The new boot scale line should
show `8`. The ordinary P705 grant/bypass settings remain disabled. After the
maturity trigger, expect the server's `[finale] ... bounded finale armed` line.
If natural victory has not happened after another 300 simulation seconds, expect
the explicit deadline reason, `Stage Over`, intermission and the automatic next
start. The same sequence must happen in match two. Simulation-time bounds can
take longer in wall time when dilation is active.

No rig access, commit or push is part of this lane. Changes remain in the working
tree for the dispatcher.
