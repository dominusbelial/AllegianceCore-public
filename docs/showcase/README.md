# Showcase skill telemetry (SC-1)

`SKILLS.csv` defines 32 observation IDs, their native training/player sources, exact detection semantics, and provisional per-side minimums for a 107v107, 45-minute match. These are observation floors, not balance targets or statistical estimates. A smaller/shorter run is compared with the same floors and must be identified as such.

Every server world receives a fresh counter set at assembly, including automatic mission rotation. Counters include setup purchases/launches and all entities on that side (combat peers, research commanders, and native NPCs). Client-only missions have telemetry disabled. Successful events increment fixed arrays on the simulation thread without event logs or JSON serialization. Damage and effective repair hits also retain the raw damage-type byte; type counts are hit counts, not health points. Captures, administrative destruction, passive regeneration, scenario object insertion, refused purchases/orders, and ineffective repairs are not evidence of weapon damage/repair skills.

The shared game-over path freezes and emits one final block per side before mission rotation. The lethal station hit is counted before the victory callback. The four lines per side contain the snapshot/final marker, counters, coverage, and nonzero damage-type counters. Coverage is the number of skills meeting their minimum divided by all 32 catalogue skills; the sides are evaluated independently.

Type `skills` followed by Enter on the dedicated server's standard input for a snapshot. Embedded console hosts can call `Server.RequestSkills()` from any thread; the next simulation pass emits the snapshot. Requests coalesce, never reset counters, and work during a single-player pause. Network/MCP clients are not given an unauthenticated administrative query. A console query satisfies the SC-1 on-demand requirement.

```sh
python3 scripts/showcase-skills.py /path/to/server.log
python3 scripts/showcase-skills.py /path/to/server.log --json
```

The report tool accepts timestamp prefixes, preserves match and side identity, and uses the final block or latest snapshot, without summing repeated snapshots. Missing counters are explicitly listed as incomplete and remain in the denominator. Input should be from one server process: match numbers are process-local. JSON output includes every count and every zero/under-minimum entry.

For a reproducible, bounded local proof after the solution build:

```sh
set -a
source local.env
set +a
python3 scripts/sc1-showcase-local.py --output /tmp/allegiance-evidence/SC1/local
python3 scripts/showcase-skills.py /tmp/allegiance-evidence/SC1/local/server.log
```

The proof uses port 18471 (override with `--port`), civs 25/3, research ×16, income multiplier 5000, a four-sector SingleRing, 15 combat bots per side plus commanders/NPCs, and a 600-simulation-second countdown. Existing finale orchestration may end it earlier; the driver waits for the final block and terminates only its own processes. It requests snapshots every minute. Raw server/MCP output and the exact configuration stay in the chosen external output directory.

Flight counts each moving ship identity once, including pods. Autopilot is the server-steering proxy: client autopilot intent is not transmitted. Scouting counts acquisition of current scanner contacts, including rocks and station scanners; it does not prove a scout intentionally explored. Guns include nanite rounds; nanite coverage separately requires effective negative damage. Commander order counts require an accepted, supported order to an NPC, and do not include autonomous plans or player-to-player orders. A repair order by itself cannot satisfy repair coverage. Voice counts native quick voice IDs and drone speech, not microphone audio. Dock excludes initial seating and direct rescue placement; relaunch includes the first physical launch. Drone-build counts completed station construction, while mines/probes count deployment by layers or dispensers.

Keep the fixed C# catalogue in `SkillTelemetry.cs` and `SKILLS.csv` IDs/minimums aligned. `scripts/test_showcase_skills.py` checks their parity and the parser. See `docs/reports/SC1_SHOWCASE_SKILLS.md` for evidence and follow-up diagnoses.


## Nanite support pilots (SC-2)

The MCP showcase assigns `ceil(fleet size × share)` support slots independently per side (two of fifteen at the default share). Set `ALLEGIANCE_BOT_SUPPORT_SHARE` on the **allegiance-mcp process** to a fraction in `[0,1]`; default `0.10`, `0` disables support. Invalid/nonfinite values use the default. Direct bot spawns can request `role: "support"` internally; human purchases are unchanged.

Support pilots buy an affordable, nonobsolete player hull and a negative-power gun through the ordinary observed side-tech, individual dock-local tech, mount, energy/ammo and wallet gates. The gun may occupy any compatible fixed socket (PCore001 Ga'Taraan scouts use socket 1). Support purchases carry no damaging guns or dispensers. When repair equipment is locked, the pilot flies a legal combat fallback and checks for a reachable legal refit shop every five seconds. A refit uses a physical return to dock, ordinary purchase, and launch acknowledgement; an unaffordable refit can retain native payday income.

A repair-equipped pilot prioritizes damaged own stations, then visible damaged friendly ships in its sector. It recalls across normal warp routes to damaged own stations and otherwise accompanies the strike group or nearby combat peers. Pods, dead/hidden/docked/turreted ships, enemies and full hulls are excluded. It leads moving targets and fires within the native repair angle/range gate; dodging suppresses fire. Finale orchestration preserves its support role. Active-bot telemetry reports `Role: "support"`, and `[support]` lines identify target changes. These intent traces are separate from SC-1's effective server repair counters.

Native references (relative to `~/Development/Allegiance/src/`): `training/mission8.cpp:194,221,278`; `Igc/shipIGC.cpp:1222-1267,2608-2676`; `Igc/stationIGC.cpp:147-156`; purchase/mount gates `Igc/stationIGC.h:383-433`, `Igc/hullTypeIGC.h:163-213`; voluntary payday opt-out `FedSrv/FedSrv.CPP:5535-5544`. The share, ranking, escort and target priorities are showcase policy.

After building the solution, run the bounded local research ×16 qualification with:

```sh
dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter FullyQualifiedName~Research_x16_showcase_support_repairs_ships_and_stations_on_both_sides
```

It uses civs 25/3, ordinary research and purchases, income ×5000, 45 combat peers per side (five support pilots each) and the existing siege finale. It requires both nanite counters to reach 10 on **each** side within 1,200 wall seconds. `SC2_LIVE_LOG=/writable/disk/path/showcase.log` retains detailed diagnostics. A separate training-style loopback test places/damages fixtures and proves that one legally purchased support pilot actually restores both target kinds. See [the SC-2 report](../reports/SC2_NANITE_SUPPORT.md) for counts, traces and validation.

## Per-match goal coverage (P832g)

Opt in with `showcase.goalRotation` in the server config; see
[`tools/p832g-goal-rotation.json`](../../tools/p832g-goal-rotation.json).
`{"mode":"cycle"}` uses conquest 100%, flags 1, artifacts 1, team kills 20,
prosperity ×1, territory 70%, countdown 1800 s, then flags 1 + artifacts 1.
Override `entries` with objects containing a unique `name` and a native `goals`
object. At least two distinct enabled goal sets are required. Fixed `games[].goals`
or a nonzero `gameLengthSeconds` conflict with rotation. All entries are validated
for every configured game before boot (territory needs a sufficiently large map;
prosperity needs developments). The authored spider map supports the full list.

`random` chooses from every entry except the preceding one, with a separate RNG
seeded from the logged matchup seed (`games[].civSeed` pins the sequence).
Replay a random choice using the same ordered entries, `seed` and
`previousGoalIndex` on `[matchup]`; cycle uses the per-game `goalMatch` ordinal.
The log also includes exact goal values and the selected zero-based `goalIndex`.
Multi-game servers keep independent rotation positions. A restart begins a fresh
sequence. An omitted rotation preserves the existing fixed-goal path and log form.

Spawn the ordinary `showcase` bot fleet with `finale_after_minutes:0`; it reads
all selected objectives from the native-visible mission info after each migration.
Rotation disables the server's synthetic research-finale deadline. Normal native
survival/elimination rules still apply. `SkirmishOptions.GoalRotation` exposes the
same opt-in preset to embedded hosts without changing setup-screen defaults.

The skill report's Goals column/JSON `goals` field joins `[matchup]` to `[skills]`
by match number, so each match's observed mechanisms retain its actual goal set.
Old logs show `fixed/unspecified`. Selecting a goal does not itself certify play;
see the measured two-match proof and the remaining rig recipe in
[`P832G_GOAL_ROTATION.md`](../reports/P832G_GOAL_ROTATION.md).
