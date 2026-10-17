# P705 — showcase tech mode

## Implementation

`showcase.techMode` accepts `unlocked` (default) and `research`. `unlocked`
preserves the existing configuration, including independently configured P703
plans. It does not silently enable a new grant.

`research` resolves the dependent settings before booting runtime subsystems:

- Test unlock disabled, `allTech=false`; harness grants disabled,
  `ignoreTechGate=false`, including the harness's normally-true omitted default.
- Explicit `enabled=true` on either grant, explicit `allTech=true` or
  `ignoreTechGate=true`, and `allowDevelopments=false` fail boot with the
  conflicting key. This applies equally to JSON and configs constructed in code.
- Each actual side selects and validates its own civilization's plan. Missing
  selections mean `auto`: authored plans first, generated fallback otherwise.
  Missing developments, unknown civs, unavailable named plans and invalid plans
  produce errors naming game, side and civ. Research plan preflight runs before
  opening the listener.
- Plans ship beside the server under `showcase-plans/`, including in the Docker
  image. An optional `games[].researchPlanDirectory` overrides that location.
  Plan briefings activate the existing P703 MCP research commanders; a connected
  MCP process is still required to execute the commanders.
- `incomeMultiplier` remains an explicit pacing parameter, default 1, validated
  in [1, 10000]. `loopResearchPlans` remains false by default. Research mode
  neither fabricates wallet money nor accelerates development/build timers.

The existing file map has asteroid capabilities 1 and 8, while plans also need
16, 32 and 64. Research mode therefore prepares **showcase map content** before
mission assembly: each side's home sector receives any missing capability rocks
needed for its selected station steps, plus one replacement reserve per
capability. Existing resources count toward that supply. For a given input map and plans, the added layout and IDs are deterministic.
Preparation runs on every match rotation. These rocks contain no
added ore. There is no grant of developments, stations, constructors or wallets.
`[research-map]` logs distinguish this opt-in map extension from native rules.
This addresses P703's documented resource gap without rewriting P703/P704.

## Matchup run parameter

Use the ordinary ordered `games[].civIds` array, or override one configured
2-side game for a run:

```sh
dotnet run --project src/Allegiance.Server -- --config local-tech.json --civs 25,3
# Same config, different matchup:
dotnet run --project src/Allegiance.Server -- --config local-tech.json --civs 4,35
```

`--civs` validates two integer IDs and one two-side game. Plan selection and
resource preparation iterate side IDs in dictionaries; extending the CLI's
side-count validation later does not require race-specific branches. No new
3+ side run interface was implemented.

The loaded PCore001 civilization list is: 1 Dreghklar, 2 Technoflux, 3 Ga'Taraan,
4 Phoenix, 5 Omicron, 18 Gigacorp, 25 Iron Coalition, 35 Bios, 101 Belters,
201 Rixian. All **45 unordered pairs** boot with valid plans. Civs 25 and 3 use
packaged `plan_a` by default; the other eight use generated plans. The matrix
also asserts every selected construction capability has sufficient home-sector
resources. Explicit side selections remain available and an unavailable named
plan is an error rather than an unnoticed substitution.

## Native rule references

Paths below are relative to `~/Development/Allegiance/src/`. Mode resolution,
plan orchestration, resource layouts, proof acceleration and the income
multiplier are port extensions. The native rules exercised underneath are:

| Rule | Native source |
|---|---|
| Starting techs use the actual civilization, path toggles and development policy | `Igc/missionigc.cpp:3869-3883` |
| Treasury payday is divided among real players and credited to wallets or their donation recipient | `FedSrv/fsmission.cpp:2294-2324` |
| Periodic base and station income precedes payday | `FedSrv/fsmission.cpp:2514-2546` |
| Bucket donations require affordable nonnegative money and live buyability | `FedSrv/FedSrv.CPP:7142-7158` |
| Bucket completion needs money and elapsed build time | `Igc/bucketigc.h:40-74` |
| Developments use global tech; production uses a real station's local tech; obsolete station successors cannot be bought | `Igc/sideigc.h:237-280` |
| Hull/part purchase requires dock-local plus side tech and honors successors | `Igc/stationIGC.h:383-433` |
| Construction requires a compatible asteroid and physical arrival | `Igc/shipIGC.h:2051-2057`, `Igc/shipIGC.cpp:2810-2827` |
| Completed construction consumes its asteroid and exports the station | `FedSrv/FedSrv.CPP:11431-11462` |
| Loss of the last restart station deactivates a side and checks victory | `FedSrv/fsmission.cpp:5318-5427` |
| Game-over publishes results and resets the mission | `FedSrv/fsmission.cpp:3063-3085,3435,3527` |
| Automatic restart sends a fresh start time | `FedSrv/fsmission.cpp:3539-3546` |
| Joining uses the configured player cap and imbalance policy | `FedSrv/fsmission.cpp:4593-4626` |

## Local proof and verification

The [solution build](P705_assets/build.log) passed with **0 warnings / 0 errors**.
The [invocation ledger](P705_assets/verification.json) retains exact project,
method/filter, command, elapsed time, TRX counters and failure messages. The
[summary](P705_assets/summary.json) indexes focused checks and suite outcomes.
Raw TRX/log files remain under `/tmp/p705-results/`.

The core focused phase completed **60 isolated invocations / 138 passing case
executions / 0 failures**. It includes each new configuration test, the recursive
P686 source-generated-default guard, existing config limits/crowding regressions,
P702 selection/boot, and P703 income behavior. The 45-pair/resource matrix passed 3/3 again against the corrected build.
The corrected-build focused phase completed another **39 isolated invocations /
87 passing case executions / 0 failures**, covering all new config tests and the
profiler/transport/checkout regressions. Unknown civs and
unavailable named plans fail before listening, with side/civ context.

The new loopback proof runs real MCP combat bots and P703 research commanders:
25/3 at 10v10, 1/2 at 2v2, 4/35 at 3v3, and 101/1 at 5v5. All use
`techMode=research`, multiplier 500, grants/bypasses off, and automatic next match.
Every combat peer must actually launch. Each side must observe real development
completions and obtain a full successful server purchase/launch acknowledgement
for a loadout that fails legality against that side's starting tech and dock.
The fixture also compares the purchased hull/part identities to the server ship.
After each game over it checks fresh development masks, empty completed buckets,
new mission identity, reseating and the new research epoch; match two repeats the
research and upgraded-purchase assertions.

The fixture drives normal 20 UPS ticks in batches faster than wall time. Combat
pilots are spaced for deterministic research observation. Once the bomber role
is available it triggers native damage/ejection and station rescue, allowing
P704 to choose and buy the upgrade. After both upgrade proofs, lethal fixture
damage to the opposing restart stations exercises the ordinary station death,
side elimination, wire GAME_OVER, mission rebuild and automatic restart paths.
No development, bucket funding, wallet, completed station or constructor is
injected. These are bounded integration matches; the rig handoff exercises the
unattended combat/finale at full requested load.

Times below are **observed simulation seconds after each match's observed
start**, sampled from wire state. Completion counts and times are side 0/side 1.
Each linked JSON records every observed development ID/name/completion time,
first upgraded hull/parts/launch, game over and reset epoch. Wall observations
are included there and are accelerated test durations, rather than rig ETAs.

| Civ pair / full timeline | Load | Match | Development completions | First development (s) | First upgraded launch (s) | Game over (s) |
|---|---|---:|---:|---:|---:|---:|
| [25-3](P705_assets/25-3-timeline.json) | 10v10 | 1 | 14/14 | 55.2/55.2 | 2669.8/2454.3 | 2669.8 |
| [25-3](P705_assets/25-3-timeline.json) | 10v10 | 2 | 14/14 | 54.3/54.3 | 2614.8/2431.0 | 2614.8 |
| [1-2](P705_assets/1-2-timeline.json) | 2v2 | 1 | 40/12 | 24.0/48.2 | 2630.8/5463.0 | 5463.0 |
| [1-2](P705_assets/1-2-timeline.json) | 2v2 | 2 | 42/12 | 26.5/47.5 | 2633.0/5674.7 | 5674.7 |
| [4-35](P705_assets/4-35-timeline.json) | 3v3 | 1 | 64/24 | 36.0/70.2 | 3810.5/15325.2 | 15325.2 |
| [4-35](P705_assets/4-35-timeline.json) | 3v3 | 2 | 64/24 | 31.5/64.5 | 3817.5/15459.2 | 15459.2 |
| [101-1](P705_assets/101-1-timeline.json) | 5v5 | 1 | 11/28 | 43.5/43.5 | 3783.3/2412.3 | 3783.3 |
| [101-1](P705_assets/101-1-timeline.json) | 5v5 | 2 | 11/28 | 49.0/49.0 | 3775.0/2436.5 | 3775.0 |

| Full suite | Passed | Failed in full run | Skipped | Wall seconds |
|---|---:|---:|---:|---:|
| full-Core | 4600 | 6 | 0 | 619.6 |
| full-ClientCore | 1090 | 0 | 0 | 3199.3 |
| full-ClientCore.MonoGame | 2207 | 1 | 3 | 932.7 |
| fixed-full-Core | 4605 | 1 | 0 | 722.8 |
| fixed-full-ClientCore | 1090 | 0 | 0 | 3169.1 |
| fixed-full-ClientCore.MonoGame | 2208 | 0 | 3 | 933.5 |

An initial diagnostic Core run exposed a failed-boot ordering bug: rejected
research plans left profiler callbacks attached before a host existed. Four later
profiler tests then saw a null host. Plan preflight now runs before runtime
initialization. The strengthened rejection test forces a profiler window; both
cases fail against the earlier binary and pass three isolated runs against the
fix. See [the regression-red proof](P705_assets/preflight-regression-red.json).
No P703/P704 implementation was rewritten.

The same diagnostic Core run included a checkout guard failure because the
isolated artifact root initially lacked its read-only Git link. The link was
corrected and the exact guard passed 3/3. A separate transport shutdown
ThreadStateException passed 3/3 exact-method isolated reruns and is recorded as a
load flake. The fresh full suites use the corrected artifact-root setup.
The diagnostic MonoGame run also found that its real-GL subprocess expects the
conventional `tools/fsr-proof/bin/Debug/net10.0` layout. A link within the isolated
artifact root points that location to the built probe; the real GL test then
passed 3/3 unchanged isolated runs. See
[GPU isolation results](P705_assets/gpu-isolation-summary.json).

One 101/1 focused match timed out while its paid Refinery constructor remained
in physical approach. The next two scheduled repetitions and three additional
unchanged isolated reruns passed, both matches each time. This meets the brief's
load-flake rule; its steering cause is not established. See
[TEST_FLAKES.md](../TEST_FLAKES.md), the
[timeout snapshot](P705_assets/constructor-approach-timeout.json), the
[partial timeline](P705_assets/101-1-timeout-timeline.json), and the
[three isolated results](P705_assets/focused-isolation-summary.json).

All three required full suites completed against the corrected build:
**Core 4,605 pass / 1 receipt flake / 0 skip; ClientCore 1,090 pass / 0 fail /
0 skip; MonoGame 2,208 pass / 0 fail / 3 existing optional Meshy-pack skips**.
The Core mine-visibility receipt failure passed **3/3 unchanged exact-method
isolated reruns**; see [its isolation results](P705_assets/fixed-core-isolation-summary.json).
No deterministic failures remain. Each new case has at least three passing
isolated executions; the corrected failed-boot regression also passed 3/3.

Full suites ran sequentially without lane/corpus filters. Artwork/native paths
came from the existing `local.env`. Local live proofs were enabled with
`ALLEGIANCE_LIVE_P542_PROOF`, `ALLEGIANCE_LIVE_P660_SHOWCASE`,
`ALLEGIANCE_LIVE_CAPTURE_PROOF`, `ALLEGIANCE_LIVE_P539_BASELINE`, and
`ALLEGIANCE_LIVE_P539C_REPRO`, all set to 1. Existing durations and sizes were
unchanged; the new P705 matches use the explicit reduced loads above. Rig-only
proof gates stayed off.

The filesystem profile required isolated build/cache locations:
`NUGET_HTTP_CACHE_PATH=/tmp/p705-nuget-http` and
`dotnet build AllegianceCore.slnx --artifacts-path /tmp/p705-fixed-artifacts`.
That artifact root links to the checkout marker/source/data directories and
read-only Git metadata so existing repository guards resolve normally. Its
conventional FSR probe path links to the SDK's relocated output. Exact test
commands are retained in the ledger. Raw final logs/TRX remain in
`/tmp/p705-results/`; per-run proof files are in `/tmp/p705-fixed-proof/`
and `/tmp/p705-final-proof/`.

## Rig handoff — Claude only

No rig connection, rig deployment, commit or push was performed. Apply this
**field delta** to the rig's existing `compose.server.json`; preserve its artwork,
networking, transport and profiler settings. `games[0]` below means edit the
existing first game, rather than append another game. The same edits are
available as an [exact RFC 6902 patch](P705_assets/compose.server.tech-mode.patch.json),
validated against the checked-in compose template locally:

```json
{
  "showcase": {
    "techMode": "research",
    "incomeMultiplier": 500,
    "loopResearchPlans": false
  },
  "liveSettings": {
    "debugMode": false,
    "timeDilation": "auto",
    "autoNextMatch": { "enabled": true, "intermissionSeconds": 30 },
    "testUnlock": { "enabled": false, "allTech": false, "money": 0 },
    "testHarness": { "enabled": false, "ignoreTechGate": false, "money": 0 }
  }
}
```

Set these fields on `games[0]`:

```json
{
  "map": "m2xspiderw.igc",
  "mapGen": null,
  "teams": 2,
  "civIds": [25, 3],
  "sideNames": ["Alpha", "Bravo"],
  "maxPlayersPerTeam": 128,
  "maxTeamImbalance": 32767,
  "allowDevelopments": true,
  "allowShipyardPath": true,
  "allowExpansionPath": true,
  "allowTacticalPath": true,
  "allowSupremacyPath": true,
  "initialMinersPerTeam": 1,
  "researchPlanDirectory": null,
  "researchPlans": {}
}
```

Rebuild the server image so packaged plans and mode code are both present. The
server does not spawn combat bots from its config. With the matching MCP build,
use the existing `spawn_scenario` tool:

```json
{"preset":"showcase","size_scale":7,"staging":false,"refill":true,"finale_after_minutes":30,"finale_side":"0"}
```

This requests 105 combat bots per side, plus the P703 commander peers and probe;
capacity 128 leaves room for those control peers. The 30-minute mature-battle finale uses the normal researched bomber selection,
including waiting for a legal loadout. Observe **two** full `Stage Over` events with
a successful automatic second start between them; then stop the run. The next
match policy continues indefinitely until stopped; it is not a two-match cap.

Expected logs:

- Server boot: `[boot] showcase.techMode = research; incomeMultiplier=500; researchCommander=on`.
- Both sides: `[research-plan] side 0: civ 25 ... plan_a`,
  `[research-plan] side 1: civ 3 ... plan_a`.
- `[research-map] side=... capability=... available=... planned=... added=...`.
  These repeat after each game-over rebuild.
- MCP: `[research] side=... plan=plan_a ... techs=[...]` grows on both sides;
  completed developments, owned stations, real wallets and constructor waits
  appear in `get_perf_stats.research`.
- MCP `[loadout]` selection reasons change with tech after death/docking; server
  `BUY_LOADOUT_ACK ... fbought True fLaunch True` proves purchase and launch.
  `SIDE_TECH_CHANGE` alone does not repurchase an in-flight ship.
- Server `[gameover] mission #0 -> Stage Over`, rebuilt
  `STAGE_NOTSTARTED`, then `[intermission] ... next match in 30 sim seconds`;
  the second match starts and produces its own `Stage Over`.
- MCP `[p682] ... bought and launched next-match hull ...` after reseating;
  research starts from fresh epoch/starting masks rather than match-one tech.

The test-unlock and harness grant-active banners should be absent. A contradictory
old grant setting now produces a boot error instead of silently degrading the run.
