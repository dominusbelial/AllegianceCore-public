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
