# P832d — artifact bots

Artifact games now use legal player pilots to search neutral sectors under fog,
share sightings within their side, collect artifacts, return them to an own
station and intercept visible enemy carriers. The `artifacts` scenario preset
requires `MissionInfo.Goals.Artifacts > 0`; ordinary showcase and skirmish fleets
also detect that goal automatically. Artifact games suppress the bomber finale.
There are no commits or pushes. No remote host was contacted.

## Implementation and policy

`BotManager.Artifacts.cs` adds search and collection policy to the existing
`BotManager.Flags.cs` controller. Return flight, station selection, docking,
escort, visible-carrier interception, navigation and death recovery remain
shared. The flags-only planner branch is retained. When neither objective is
selected, the new entry points return before changing state, consuming random
numbers or issuing controls/orders.

The shared offense quota is the P832c quota: 1 carrier for a tiny fleet, up to
3 offensive pilots for a small fleet, then 10% capped at 8. Denial is 10%,
capped at 12 (zero for a single pilot). Actual carriers return first. The
nearest capable available pilot collects a known artifact; without a sighting,
a scout is preferred. At eight or more eligible pilots one offensive slot
escorts the collector/carrier. Remaining offensive slots search, except that a
combined flags/artifacts game reserves an offensive slot for a flag raid.
Nearest free observing fighters intercept visible enemy carriers; unused denial
slots retain normal duties. Every deployment removes its pilot from the shared
candidate list, so a pilot never has two objective duties. Existing carriers
are always brought home, including unusual incidental pickups beyond the
ordinary offensive quota, as in P832c.

At 200 eligible pilots, at most 8 search/collection/raid/escort slots and 12
interceptors are selected; without a visible carrier only 8 are selected.
Thus at least 90% of the fleet keeps normal clash/strike behavior, usually 96%.
The quotas operate on the eligible fighter/scout roster, so the mixed showcase
fleet can allocate fewer. `ScenarioCoordination.cs` runs the same planner over
all workers, with one budget per mission cookie and side.

Searchers use global native topology/home-sector flags to choose neutral
clusters, then the existing discovered-mouth travel and sector sweep. They
complete a center-and-ring search and keep cycling neutral sectors after an
empty sweep. Each completed map lap expands the ring by one scanner range,
using overlapping bands up to the core's flight boundary minus a safety margin,
then cycles those bands again. Native treasure placement increases its radius
after rejected positions (`Igc/missionigc.cpp:4158-4187`), so a fixed exploration
ring is insufficient for every respawn. They resume after a pickup/drop/secure instead of assuming the
respawn position. Any assigned capable searcher opportunistically collects the
nearest known drop. Only a bot's native client treasure table supplies a
position. `ArtifactSighting` extends the existing `SideIntel` digest with
same-side observations, monotonic timestamps, pickup tombstones and 180-second
retention. Tombstones prevent delayed worker digests resurrecting an old
pickup; a fresh respawn ID needs a fresh observation. An observer remaining in
a sector invalidates absent sightings whose last observation is over two seconds old.
Enemy carrier identity alone is insufficient: a current, visible client pose
is required for interception. Other sides' worlds and server object positions
are never inputs to bot decisions.

## Re-grepped native contracts

All citations refer to the local read-only native source tree named in the
brief; these line numbers were checked during this task.

| Contract | Native source | Result / difference from real-side flags |
|---|---|---|
| Mission-start artifact placement | `Igc/common.cpp:3089-3097`; loaded population guard `Igc/missionigc.cpp:4048-4051` | One artifact per populated neutral cluster, not one per goal count. `nGoalArtifactsCount` is the winning score. Existing port population reused. |
| Artifact type and random placement | `Igc/missionigc.cpp:4146-4207` | `c_tcFlag`, `SIDE_TEAMLOBBY=-2`, random squashed-disk position. No side-owned pedestal. |
| Secure respawn | `FedSrv/FedSrv.CPP:13842-13870`; call at `:13161` | Random neutral cluster, new treasure/position; unlike flags, no return to a pedestal and no eligible-pedestal win. Existing artifact respawn reused. |
| Pickup capability and one cargo slot | `Igc/treasureIGC.h:81-92`; `FedSrv/FedSrv.CPP:13563-13580` | Player pilot plus Rescue hull capability. Any real side may pick up -2; the own-flag refusal compares the payload to the pilot's real side. A carried flag OR artifact blocks a second pickup. Pickup suppresses respawn. |
| Own-station secure | `FedSrv/FedSrv.CPP:13113-13149` | Same own-side dock gate; increments artifacts and player `AddArtifact`, not flags. Team chat says “has secured an artifact.” with `artifactSecuredSound` (1362; `Igc/sounds.h:419-420`). Clears cargo and publishes `GAIN_FLAG(NA)` before victory. |
| Score threshold and reason | `FedSrv/fsmission.cpp:3861-3899`; `FedSrv/FedSrv.CPP:13149-13158` | Artifact count checked at secure. Port now emits `<team> wins with the artifact recovered by <pilot>`. See the native-source typo qualification below. |
| Death and side departure | `FedSrv/FedSrv.CPP:11731-11757`; `FedSrv/fsmission.cpp:867-893` | Same drop-at-current-position / outside-universe respawn rule as flags, carrying -2. Existing P841 `Server.Lifecycle.cs` departure path reused; bots reacquire the dropped treasure. |
| Placement clearance around mines | `Igc/missionigc.cpp:4171-4187`; `Igc/mineigc.cpp:103-106`; `Igc/minetypeigc.h:104-106`; `Igc/igc.h:1990-1996` | Native uses the model's runtime radius, which for a mine is the derived tail `radius` (`MineRadius` in the port), not the shadowed and uninitialized base field. |
| Carrier visibility | `Igc/shipIGC.h:348-369` | `GetFlag()!=NA` includes -2: bypasses range/signature/occlusion when a side scanner is present in that sector. P832c scanner rule reused; identity does not reveal a pose in another sector. |
| What the observing client knows | `FedSrv/FedSrv.CPP:10750-10778`; `FedSrv/fsmission.cpp:4224-4231`; `FedSrv/FedSrv.CPP:13571-13576`; `WinTrek/radarimage.cpp:637-645` | Treasure export follows side visibility; pickup identity goes to real sides; radar highlights flag treasures and distinguishes artifact-carrying ships with `c_maskArtifact`. No new radar/HUD behavior. |

Three narrow native contract gaps were fixed and recorded in SIM-0844–0846. Artifact
secure previously used the generic prosperity/artifact victory message before
clearing cargo and omitted the team chat/cue. It now follows the native secure
notification/order and the requested artifact-specific reason. New generated
and dropped artifact broadcasts previously bypassed side visibility; they now
wait for `SeenBySide`, after which the existing discovery sweep exports them.
The core's artifact-placement clearance also now uses `DataMineTypeIGC.MineRadius`, the
derived native `radius` field used by `mineigc.cpp:103-106` and
`minetypeigc.h:104-106`. The inherited `DataObjectIGC.radius` is shadowed in
`igc.h:1990-1996` and contains `0xCCCCCCCC` in this core. Squaring that unused
negative value previously forced respawns hundreds of millions of units away.
The regression uses the observed poison pattern, infinity and NaN to prove
that this unused field cannot affect a legal, seeded artifact placement; it
also verifies the real mine clearance. This correction is restricted to the
artifact generation leg to preserve other goal games' existing treasure
behavior in this slice. No native spawn-radius clamp was added.

The TCP negative test uses a following visible treasure as a stream barrier,
then proves that revealing the side permits exactly one export. No new service,
port, environment variable, artwork or HUD change was introduced.

**Native-source qualification:** `FedSrv.CPP:13154` tests `sidFlag != NA` when
choosing between its flag and artifact reason strings. Because an artifact's
value is -2, that literal condition incorrectly selects flag text for an
artifact too. The brief explicitly requests the artifact-recovered reason;
this implementation selects the artifact string at `:13156` for -2. This is a
native-intent correction, not a claim of byte-for-byte parity with that typo.

## Qualification

Final receipts are linked in [verification.json](P832D_assets/verification.json).

| Check | Result |
|---|---|
| Final solution build | 0 warnings / 0 errors |
| `tools/build-tools.sh --drop-bins` | All 91 projects pass, 0 warnings / 0 errors |
| Linux x64 server AOT publish | Pass; no IL2026/IL3050 diagnostics |
| Artifact policy, mixed-goal/worker quota, fog memory, capability, hidden pose, no-goal traces | 10/10 × 3, zero skips |
| Treasure generation, including poisoned mine-radius regression | 18/18 × 3 |
| Dispatch decision inventory | 167/167 on base; 167/167 × 3 on final source, identical named outcomes |
| Affected native server rules | 32/32 × 3; P835 visibility class separately 5/5 × 3 |
| Artifact bot games | 9/9 native artifact wins; pickup, secure, score, winner and exact reason verified |
| Existing P832c flags-only bot games | 9/9 pass across the unchanged three cases × 3 |
| One Fast tier | 11,081 passed / 1 known baseline failure / 3 optional skips; 309.0 s; 9 infrastructure checks pass; no timing offenders |
| Glossary / native inventory / parity | 215 linked paths valid; 351 native bodies/cases reconciled; CSV valid with LF |
| Compose / working-tree hygiene | `docker compose config --quiet` passes; no new media, personal code paths/emails or secret-pattern hits |
| Never-parks 5× base / 5× changed | Not run under the brief's measured busy-host exception; see receipt below |

| Artifact topology / goal | Three final wall times | Result |
|---|---|---|
| Brawl / 1 | 74.4, 94.6, 22.0 s | 3/3 artifact wins |
| Brawl / 2 | 92.7, 47.8, 59.2 s | 3/3; below-goal respawn and closing interception required |
| SingleRing / 1 | 50.3, 46.6, 38.5 s | 3/3; real cross-sector travel |

The sole Fast failure is the already-ledgered
`P775PlayerInfoMomentsTests.Mid_game_sighting_of_a_joined_enemy_sends_no_playerinfo`:
its announcement counter expects 1 and receives 2. It fails 3/3 exact-method
runs on the changed tree and 3/3 with the captured dispatch product DLLs. The
P775 test source is byte-identical to HEAD; the baseline reproduction used the
same unchanged test method in the current test assembly with the captured
`Core`, `Server`, `ClientCore`, `Bot` and `Update` DLLs. The receipt records
those DLL hashes. This matches the existing P841 baseline-red ledger entry;
no PLAYERINFO code, test assertion or native rule was changed for this task.
The one broad Fast run remains **failed** and was not repeated or relabelled
passing.

Fast used a `/proc/<parent>/fd/<directory>` lexical alias to workspace-owned
scratch and task-local `XDG_CONFIG_HOME`, as in the prior lane receipt. This
keeps temporary path tests outside repository ancestry and profile writes
inside the permitted workspace without changing product/tests or writing
large temporary results into `/tmp`. All nine path cases and the profile
editor pass in the single broad run.

The loopback games use normal research, starting money, purchase/tech gates and
fog. They do not place artifacts, teleport pilots, set possession or force a
winner. Server-object reads in the proof are evidence collection only, guarded
against concurrent mutation. The ticker advances the ordinary fixed-step
server every 20 ms, approximately 2.4 times normal simulation speed; times are
accelerated loopback wall times with a 360-second bound per game. Brawl/2 also
requires a below-goal respawn and a moving defender closing more than 50 units
on a visible enemy artifact carrier. SingleRing exercises real warp travel.

The initial proof had an evidence-only collection enumeration race (two
failures, one SingleRing win); a simulation lock fixed it before the repeated
qualification. An initial policy probe also tried to send an order through its
fake connected transport; that probe now checks the assigned intercept anchor,
and the TCP game verifies actual flight. These were test-fixture corrections;
no production flight/physics law or timing threshold was relaxed.

An intermediate fixed-ring implementation won its first three games, but a
later Brawl/2 repeat reached the 360-second bound after one secure. Inspection also found
a search coverage gap: the ordinary exploration ring never expands, whereas
native treasure placement can expand its spawn radius. The final policy
adds overlapping outward search bands within the core's legal flight boundary,
and the coverage test includes a location beyond the original scanner band.
The failed run is retained in `.scratch/p832d/pre-expansion/`.

A later diagnostic repeat made the underlying native-rule error concrete:
after the first secure, artifact 5607 spawned at approximately
`(-147059360, 417081380, -6543547.5)`. The static-core inspection showed the
unused inherited mine radius was `0xCCCCCCCC`, while native explicitly uses the
derived mine radius. The clearance-field correction above fixes that source
of unreachable artifacts; expanding the search alone could not recover it.
Final qualification uses both the corrected native clearance and the expanded
search, with all failed intermediate receipts retained.

One combined 37-case run also hit the existing P835 process-global
spotter-cache isolation race: both stage-2 scanner identities were 9601.
The exact case passes 3/3 alone. The P835 and P832c visibility classes both toggle the same global without a
shared collection; final server/P832c and P835 visibility groups run separately, with no product or assertion changes. This receipt is recorded in
`docs/TEST_FLAKES.md`; the original combined failure remains in the raw logs.

The busy-host exception in the brief applies to the five-by-five never-parks
comparison. With our solution build finished and before live games/tools/AOT
work, ten three-second samples showed 1-minute load **23.85–27.43 on 24 CPUs**
and **28.14–87.86% CPU idle**, mean **58.49%**. The corresponding 5-/15-minute
loads were about 15.2–15.6 / 7.5–7.8. This is not an idle-host window; the
comparison was not started or retried blindly. The baseline test binaries are
preserved for Claude's quiet-host comparison. No new parking flake is claimed.

Raw TRX/logs, host samples and dispatch-baseline binaries remain in the ignored
workspace directory `.scratch/p832d/`, since this sandbox cannot write the
normal external evidence directory. Only small JSON/configuration receipts are
public report assets. The baseline is `9caa4049cb8586811e443d45ec44c9edf1f74fb9`.

## Local / 221 recipe for Claude

Use [showcase-artifacts.json](P832D_assets/showcase-artifacts.json), a normal
research Brawl with fog, seed 832 and artifacts=2. Preserve the rig's existing
artwork, approved bind/port and hosting settings when applying the game block.
The existing transport capacity is 1024 connections/per-IP and the game permits
220 players per side. The seed pins map geometry; artifact placement/respawn
retains runtime randomness, so coordinates and win times vary between runs.
No host or configuration on 221 was accessed.

From the repository root, the local server command is:

```sh
~/.dotnet/dotnet run --project src/Allegiance.Server --no-build -- \
  --config docs/reports/P832D_assets/showcase-artifacts.json
```

Connect the usual MCP driver and call:

```json
{"tool":"spawn_scenario","args":{"preset":"artifacts","count_a":6,"count_b":6,"loadout_mode":"best"}}
```

For Claude's 221 qualification, use `count_a:200,count_b:200` with the existing
`--workers 4` supervisor. The single offense/denial budget remains 8/12 across
workers. The shared controller logs `[flags] duty=Search/Collect`, discovery,
`[flag] ... picked up side -2`, `duty=Return`, opposing `duty=Intercept`, then
`artifacts=1` and a fresh discovery/pickup, finally artifacts=2 and GAME_OVER.
Check the winning side and exact artifact-recovered reason over the wire.

The final accelerated 6v6-requested-fleet games won artifacts=2 in
**47.8, 59.2 and 92.7 wall seconds**. At normal simulation cadence this suggests
roughly **2–5 minutes** for the small recipe, including ordinary startup; this
is a cadence-based estimate, not a separate normal-clock measurement. For
200v200 on 221, budget **15 minutes** as an initial qualification window and
expect roughly **5–15 minutes**; that large-fleet estimate is unmeasured. Record
the actual time, winner, reason and score on 221. No remote or 200v200 win is
claimed.

## Native-intent classification

**Native contracts restored:** artifact secure team chat/cue and ordering,
artifact-specific victory text (the explicitly requested correction to the
native ternary typo), side-visible artifact creation/drop export, and
artifact placement clearance using the mine's actual native radius.
**Bot policy added:** search allocation, expiring side reports, nearest known
collection, shared offense/denial shares, escort and reacquisition. Native does
not supply this fleet AI; it implements the native artifact game's intent
using native-visible information and legal player actions. The rules for
pickup, initial count, random respawn, drop, carrier visibility and scoring
threshold remain native rules, not new tactical advantages.

A modern/better approach could reserve distinct observed artifacts per hunter,
choose extraction routes from observed threats and send interceptors toward a
visible carrier's likely exit. That policy could improve coordination at large
fleet sizes while retaining native visibility, cargo limits, flight and scoring.
