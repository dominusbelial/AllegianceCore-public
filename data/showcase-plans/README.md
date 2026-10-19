# Research plans

Plans are data files under `<core>/civ<id>-<name>.json`. IDs and exact names must
match the loaded core. `coreVersion` is the core header version. Every file for
the selected core is validated at boot when `games[].researchPlanDirectory` is
configured. A mismatch names its file and the offending one-based step.

Configure a game without changing its matchup:

```json
{
  "researchPlanDirectory": "data/showcase-plans",
  "researchPlans": { "0": "auto", "1": "plan_b" },
  "allowShipyardPath": true,
  "allowExpansionPath": true,
  "allowTacticalPath": true,
  "allowSupremacyPath": true,
  "initialMinersPerTeam": 1
}
```

Selections are keyed by **side id**; the civ comes from the actual mission side.
Missing selections default to `auto`, which selects the first authored plan by name, or produces a generated fallback and a clear log message.
`generated` explicitly requests the fallback. An unknown explicit plan fails.
The optional plan-directory setting leaves execution and showcase configuration
to P703/P705. Selected, validated plans are in `mission.ResearchPlans[sideId]`.

Schema version 1 is represented by `ResearchPlan` and `ResearchStep` in Core.
Each step has `action`, core `id`, exact `name`, and `expectedUnlocks` (typed
identities with kind/id/name/role). Actions:

- `develop`: fund the development bucket, await completion.
- `station`: fund the type's constructor bucket, order construction on an
  unoccupied asteroid matching `asteroidClass` (a **single capability bit**),
  await the built station. This also exercises the associated constructor type.
- `drone`: fund the drone bucket and await spawn. Builder pilots use `station`.
- `wait`: wait `waitSeconds`; `requiredTechs` is a 50-byte mask (JSON base64).
  Its condition must already be satisfiable at this point in the script.

IDs for `wait` are unused. Empty expected-unlock lists are allowed for steps
that change attributes, build infrastructure or exercise a drone without adding
a purchase. All expected unlock identities must be buyable after the step.
The validator assumes retention of earlier stations and unlimited suitable
rocks/funding. It rejects repeated/obsolete steps. It does not execute actions,
predict combat losses, reserve rocks, or simulate income. P703 must fund real
buckets, wait for real completion, choose available rocks and react to losses.

The civ-25 and civ-3 plans place the native tactical/stealth branch early so
scouts and bombers can purchase a mounted cloak within a showcase match. Civ 25
reaches the branch with Tactical → Upgrade Tactical Lab → Tactical (Adv) →
Bomber → Stealth Bomber; civ 3 uses Tactical → [P]alisade → Upgrade Tactical
Lab → Tactical (Adv) → Stealth Fighter → Adv Stl Fighter → Bomber → Stealth
Bomber. Sig Cloak 1 is already buyable for both civs; their starting combat
hulls cannot mount it. These are ordinary funded station/development steps, with
no technology grants. The branch is placed just after the dev-dense opening
(~15 steps): the opening must stay development-heavy because construction is
not scaled by the research multiplier, and the scale-eight opening research
metric (P733ResearchTests) requires it. Final positions (plan_a): civ 25 cloak
scout at step 18 and cloak bomber at step 20; civ 3 cloak scout at step 19 and
cloak bomber at step 23. This still reaches a mounted cloak in roughly 5-7
minutes of sim at the rig's ×8 research scale — far inside a 45-minute match and
far earlier than the stock price-order plans (which reached it at step ~144+).
The branch insertion is curated: `author` generates generic price order, so
preserve the branch and recompute expected unlocks when regenerating these four
files.

After that prefix, `plan_a` retains its cheapest-first order and `plan_b` its
most-expensive-first order. Both supplied race plans have been inspected for
all four branches, advanced stations, garrison successors and non-builder drone
types. All reachable attribute developments and tech prerequisites are included;
obsolete equivalent alternatives are represented in the tree's closure.

Run from the repository root (replace `$CORE` with a core `.igc` path):

```sh
dotnet run --project tools/tech-tree -- "$CORE" dump
dotnet run --project tools/tech-tree -- "$CORE" validate data/showcase-plans
dotnet run --project tools/tech-tree -- "$CORE" sweep data/showcase-plans all
# Explicit tuples; the same interface accepts triples:
dotnet run --project tools/tech-tree -- "$CORE" sweep data/showcase-plans '25,3;1,2;4,35,101'
# Author additional plans; this writes files:
dotnet run --project tools/tech-tree -- "$CORE" author data/showcase-plans '1,2'
```

The sweep boots one generated mission per tuple, iterates its actual sides,
selects and validates both/all plans, and writes one Markdown table. It reports
funding, serial bucket time, each station completion's serial timestamp,
branches, hull/part counts and links to exact purchase catalogs. These times are
**not** economy time-to-tier predictions: constructor flight/build effects,
income, simultaneous funding, combat and mining are not simulated.

## Showcase tech matches (P705)

Set top-level `"showcase": { "techMode": "research" }` to select plans for every
actual side and activate P703's commanders when MCP connects. The server ships
these authored plans beside its executable; `researchPlanDirectory` may be
omitted. `auto` uses a generated fallback for every other civilization. Use
`--civs 25,3` (one configured two-side game) to change the matchup for a run.

Research mode disables test unlock and harness grants/bypasses. Explicit grant
or bypass settings that conflict with research fail boot and name the key.
`incomeMultiplier` remains optional (native pacing at 1). `loopResearchPlans`
stays false unless requested.

`showcase.researchTimeScale` defaults to **1**, accepts finite values **1–64**,
and accelerates funded bucket research/production timers and the constructor's
30-second station transformation timer. For example, `8` makes 80 seconds of
bucket work take 10 simulation seconds. Costs, purchase gates, income, constructor
travel and authored plan waits remain unchanged. This is a **showcase/benchmark
accelerator, not native gameplay**. It is applied only with `techMode: "research"`;
unlocked mode and ordinary servers retain native timers even if a scale is set.
The server prints `[boot] showcase.researchTimeScale = N`; commander ETA estimates
use the effective scale. Missing JSON keys preserve the native default.

A research finale tries the best legal affordable bomber first, then the P704
station-damage ranking across available player hulls. If no legal weapon can damage
the station, it launches an available combat loadout. At the actual maturity trigger,
MCP arms an epoch-scoped server deadline. `showcase.finaleTimeoutSeconds` defaults to
**300**, accepts finite values **1–3600**, and bounds the remaining simulation time.
Natural victory can end the match earlier; expiry calls the existing game-over,
results, reseating and automatic-next-match path with an explicit showcase deadline
reason. The triggering strike side wins (the first participating selected side if
`finale_side` is `both`). This bounded outcome is showcase orchestration, not a native
station kill or technology grant. Duplicate requests cannot extend the deadline;
stale epochs, another side's requests and unlocked servers are ignored. A matching
MCP build is required. Unlocked finales retain their existing behavior.

Research mode aligns the starting home station with the selected civilization's
initial station type when a file map bakes a different home type. It also fills
missing home-sector build-rock capabilities from each
selected plan, with one reserve per capability. This is showcase map content;
normal purchases, constructor movement and construction still apply. The same
resource preparation runs when the mission resets. `techMode: "unlocked"` is
the default and preserves the existing configuration and map.

See [P705's report](../../docs/reports/P705_SHOWCASE_TECH_MODE.md) for validation,
local timelines and Claude's rig handoff. See also [P733](../../docs/reports/P733_TECH_FINALE_AND_SPEED.md) for the accelerator measurements and updated rig delta.
