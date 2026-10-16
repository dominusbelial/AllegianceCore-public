# P704 — loadouts from current side techs

Implemented in `codex/p704`, left in the working tree without commits or pushes.

## Selection and progression

`TechLoadoutSelector` owns role classification and selection for both ordinary
bots and the finale. It calls `ResearchTree.Unlocks` with the current dock's
station type, then checks human eligibility, successors and each exact mount.
There are no civ-ID, hull-ID or name branches in the policy.

Roles are harness policy over native data: carriers and non-fighter capitals
are heavy; small station threats are bombers; turreted fighters are heavy;
long sensor reach relative to speed marks scouts; no-ripcord or short-range,
small-magazine craft are interceptors; remaining small combat craft are fighters.
The thresholds and all ranking/tie rules are explicit in the selector. Renaming
hulls cannot change their role. A requested role stays on its bot even while a
locked role temporarily uses an available fighter, scout or interceptor.

Fighters favor armor and fixed hardpoints, scouts sensor reach times speed,
and interceptors armor times speed. Weapons favor effective anti-ship DPS;
interceptors also favor reach. Bombers maximize damage against the observed
station's armor/shield pools, then DPS. Their fixed aim gun may do its damage
to ships while the selected missile supplies station damage. The gun search
respects shared energy/ammo and price budgets. Support chooses fitting shields,
boosters, role-appropriate mines/probes, chaff and cloaks. Launcher quantities
use the shared native-capacity clamp. Automated pilots fit fixed guns; unused
turret mounts stay empty. Stable core IDs resolve ties. Selection
logs contain the role, shop, tech count, score/rule, price and every mount.

Scenario composition now seeds roles, rather than advancing synthetic tech
levels. Scenario `tech_a`, `tech_b` and fleet `tech` remain accepted compatibility
fields but no longer affect purchases. MCP summaries describe this and report
actual grants. Explicit `spawn_bot` initial requests and catalogue/matrix
helpers retain their explicit-loadout interface; combat relaunches re-pick their
capability-derived role from current techs.

Each combat relaunch, including next-match launch and true-death restart,
reads its purchasing session's development techs, owned station effects, dock,
civ and wallet. Unknown observations defer purchase. No tech/station notification
starts a purchase while a ship is flying. A temporary scout/fighter keeps the
intended role for later purchases but takes station-attack duty only after the
client has actually received a bomber hull. This prevents a locked bomber slot
from parking a harmless scout in front of a base. Natural deaths spread upgrades, and
one manager additionally paces relaunch requests to 20/second without catching
up in a burst after a hitch. Retries retain the existing five-second interval
and three-request bound.

A refusal triggers a fresh legality/affordability check of the previous fully
accepted loadout. If it is obsolete, incompatible, unaffordable or has lost
prerequisites, the bot selects another legal cheaper option or stays docked.
The planner conservatively ignores potential refunds. As with any asynchronous
client, an authoritative wallet or ownership change after the last received
snapshot can still cause a refusal; the loopback injects exactly that race to
exercise fallback. No tech bypass or server purchase gate was changed.

## Native rules checked locally

Paths below are relative to `~/Development/Allegiance/src/`.

| Rule | Native file:line |
| --- | --- |
| Hull capability flags | `Igc/igc.h:726-741` |
| Side techs combine development and all owned building effects; lost buildings recompute the union | `Igc/sideigc.h:133-158` |
| Purchases require the side-tech union plus this individual station's local techs | `Igc/stationIGC.h:383-390` |
| Obsolescence and furthest buyable successor | `Igc/stationIGC.h:393-433` |
| Human shop hides negative hull groups and obsolete hulls | `WinTrek/load.cpp:1603-1606` |
| Part shop excludes hidden negative groups and applies buyability/obsolescence | `WinTrek/load.cpp:1715-1718` |
| Weapon mounts use individual hardpoints; other equipment uses mount 0; mask containment determines fit | `Igc/hullTypeIGC.h:163-176,199-212` |
| Dock required; hull/part purchases and automatic successor substitution | `Igc/shipIGC.h:992-1004,1028-1039` |
| Station damage uses separate armor/shield damage-matrix entries | `Igc/stationIGC.cpp:138-159` |
| Launcher quantity is capacity divided by expendable size, minimum one | `Igc/launchertypeigc.h:130-142` |
| Packs have zero purchase value | `Igc/packigc.h:80-83` |
| Launcher/dispenser value includes quantity | `Igc/magazineigc.h:85-88`, `Igc/dispenserigc.h:86-89` |
| Docked, sided, unboarded purchase preconditions; debit is new value minus old value | `FedSrv/FedSrv.CPP:6153-6172,6183-6207` |

Role ranking, temporary role fallback, conservative budget and pacing are
explicit harness policies, rather than additional claims about native AI.

## Verification

The [final solution build](P704_assets/build.log) passed with zero warnings and
zero errors. Exact commands, environments, timings and TRX hashes are recorded
in [the invocation ledger](P704_assets/verification.json); the
[summary](P704_assets/summary.json) indexes all affected methods and suite totals.
The final focused set passed **53 methods / 93 cases, each method 3/3 alone**:
**279 passing case executions**, with no focused failures on the final controller.
All three complete suites passed on their first final invocation:

| Suite | Passed | Failed | Skipped | Invocation wall time |
| --- | ---: | ---: | ---: | ---: |
| Core | 4,575 | 0 | 0 | 9m 27s |
| ClientCore | 1,073 | 0 | 0 | 32m 24s |
| ClientCore.MonoGame | 2,208 | 0 | 3 | 15m 33s |

ClientCore includes all five additionally enabled local capture/baseline/transit
drivers. MonoGame includes the real 605-second flight soak; its three skips are
the existing optional Meshy content-pack checks. There were no full-suite
failures, so no load-flake retries or `docs/TEST_FLAKES.md` entries were needed.
The solution is built with `NUGET_HTTP_CACHE_PATH=/tmp/p704-nuget-cache`;
individual filters and TRX hashes are in the ledger. Full runs explicitly enable
the local P542 projectile proof and P660 15v15 migration proof. ClientCore also
enables the non-rig capture, 20/10 Hz combat-baseline and acquisition/transit
drivers (`ALLEGIANCE_LIVE_CAPTURE_PROOF`, `ALLEGIANCE_LIVE_P539_BASELINE`,
`ALLEGIANCE_LIVE_P539C_REPRO`), using their standard windows.

New proofs cover seven PCore001 civs (25, 3, 1, 2, 4, 35, 101), starting/middle/final
research states, station-local isolation, name-independent roles, exact mount
legality, deterministic selection, shared finale selection, real research over
TCP, newly owned station effects, refusal fallback, and simultaneous relaunches
of 60 bots with tech gates enabled and no purchase refusals.
Selected receipts are indexed in [proofs.json](P704_assets/proofs.json), with
complete compressed TRX files alongside it.

The loopback funds an actual development bucket and waits for its completion
and `SIDE_TECH_CHANGE`; it then transfers a fixture station through the real
capture notification, kills the ships, rescues the pods through the server,
and explicitly drives the production lifecycle/relaunch methods against real
TCP sessions. The fixture controls positioning, starting wallets and station
capture setup. It verifies every purchase's
full ACK and the server hull, not just a client intention.

Initial diagnostic fixtures were corrected: parked ships must remain inside
the native universe boundary (`Igc/shipIGC.cpp:368-408`), zero budget is not the
same as no available free equipment, and station-local tests must satisfy the
remaining civ prerequisites. These are deterministic fixture corrections,
not load flakes. The existing MCP lifecycle fixture now expects the current
legal role loadout instead of the original bypassed hull/gun; its full grant,
launch and real damage checks remain. This expected-behavior update is also
not a load flake. The P700 observation fixture also assumed no fighter could
have a defense deployment while passing through station-less Alderan. Three
isolated diagnostics instead showed a real recall to the owned home station
in Organia (1051), while the fighter was moving at 158–177 m/s in Alderan
(1052), without a combat target. The fixture now requires any such recall to
name a different sector containing an owned station. Its five-second park
bound, zero idle-with-target metrics, travel, firing and damage checks are
unchanged. This is a deterministic fixture correction, not a load flake;
defense production code was not changed.

The longer P700 observation then exposed a real controller issue: a requested
bomber role could park its temporarily selected scout against a station. The
station-attack branch now checks the actually granted hull's capability-derived
role. A two-case controller regression proves that the fallback scout targets
an enemy ship while the granted bomber targets the station, with the intended
role preserved. The synthetic finale-strike fixture now exports its granted
bomber identity before asserting flight behavior. The five-second park bound
was retained. The corrected diagnostic reached 26/30 objectives, fired
3,453 shots, registered 1,741 hits and 14 kills, and observed at most 1.703 s
stationary with a target. Final repetitions use the same unshortened window.

The search also now admits valid early missile bombers with
an anti-ship aim gun. For the all-tech garrison fixture it now selects Stealth
Bomber 23 with a 5,000/5,000 volley, beating the previous Heavy Bomber 69
combination at 4,500/4,500. The live fixture retains exact hull/part identities,
proves the score improvement and buys the complete selection from the server.
Requiring every gun to damage station armor had excluded
the stock early bomber even when it could buy station-damaging missiles.
The catalogue-only local-mask adapter was also corrected to include supplied
local bits in its unlock query before applying the exact final gate; live
queries continue to use the actual single dock station.

No rig/221/200v200 runs, commits or pushes were performed.
