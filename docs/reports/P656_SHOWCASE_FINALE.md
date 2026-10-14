# P656 — mature showcase bomber finale

Implemented on `codex/p656`. Changes are confined to `tools/allegiance-mcp`,
socket-free ClientCore tests, and this report. No commits or pushes.

## Behaviour and options

`spawn_scenario` now accepts:

| Input | Default | Meaning |
| --- | --- | --- |
| `finale_after_minutes` | 30 for showcase, 0 otherwise | Minimum age; 0 disables registration and assignment entirely |
| `finale_side` | `"leader"` | Most scenario-bot kills; ties use the smaller side ID. Also accepts integer 0/1 or `"both"` |
| `finale_min_relaunch_share` | 0.5 | Required completed death → pod docking → launch cycles, on **each** side |
| `finale_min_kills_last_5_minutes` | 5 | Minimum credited scenario-bot kills in the rolling 300-second window |
| `finale_bombers` | 12 | Existing bomber slots to reserve per selected side |
| `finale_escort_share` | 0.1 | Fraction of fighter/interceptor slots, rounded down |
| `finale_escort_cap` | 12 | Maximum escorts per selected side |
| `finale_escort_radius` | 1500 | Approaching-enemy detection radius around the escorted bomber |
| `finale_escort_leash` | 2500 | Maximum chase separation; must be at least the radius |

The clock starts after scenario spawning finishes. Time, both sides' completed
pod-cycle shares, and recent kills must all pass. Leader selection uses all kills
of the scenario's bots, including their opening combat during spawning. Recent
kills are recorded once by the credited killer's own client, rather than once
per observer. Disconnected bots leave the live maturity denominator.

Before the trigger, the finale records observations without holding ships,
changing roles, changing loadouts, redirecting routes, or taking fighters out of
the rendezvous battle. Tests compare the resulting decisions with the finale
reference removed, both before the time gate and after time with immature bots.
The normal march, route split, staging and combat controller remain in use.

At the trigger, reserve up to `finale_bombers` existing bomber slots, ordered by
ship ID. Each buys the strike loadout on its **next docked pod launch** using the
existing `BUY_LOADOUT(fLaunch:true)` path. Unselected bombers keep their original
loadouts. Escorts likewise join only on a docked pod launch; engaged fighters
cannot be recruited. The rest of the fleet keeps its rendezvous objective.

This implementation chooses the existing-slot alternative in the brief: it
does not manufacture spare slots or convert fighters into bombers. Small fleets
can therefore field fewer than 12 bombers, explicitly reported as
`bomberShortfall`. A 200-per-side mixed showcase has enough existing bomber
slots for the default wing. Purchases and subsequent deaths use normal relaunch
and ACK handling; a refused/partial purchase is reported, not bypassed.

## Loadout selection and the real PCore001 result

`ShowcaseFinale.SelectLoadout` searches player-buyable bomber-role hulls in the
loaded catalogue. The existing role classifier includes native
`HabmThreatToStation` hulls, even when their name is not “Bomber”. AllTech uses
the core's required-tech union. Obsolete hulls and parts are excluded using the
same `SuccessorUpgrade` rules the shop applies, so the requested selection is
stable through purchase.

For every fixed hardpoint, use its actual part mask. A Pareto search considers
gun combinations within the hull's shared energy and ammunition budget, then
combines those with fitting missile magazines at their real carrying capacity.
Rank complete volleys by reciprocal full-station equivalent volley count:
`1 / (maxArmor / hullDamage + maxShield / shieldDamage)`, omitting the shield
term for shield-bypassing damage. Nominal refire damage breaks ties, followed by
deterministic catalogue order. Equip the strongest fitting, nonobsolete shield
and afterburner where those sockets exist. Uncrewed turret sockets are excluded.

Damage is `projectile.Power × DamageConstants[damageType][stationDefenseType]`,
separately for hull and shield, with the station simulation's nonnegative matrix
clamp and neutral out-of-range fallback. Missiles use their direct `Power` in
the same calculation. **Blast power is deliberately excluded:** the current
`Sector.ResolveExplosion` visits ships and mines, not stations; the direct
ordnance/station collision is what damages a base. No server code was changed.

Against real `Garrison (Str)` type **10**, hull defence **7**, shield defence
**10**, the selected bomber-role hull is **73 — Flagship.4 [AsCr]**:

| Part ID | Part | Mount / quantity |
| --- | --- | --- |
| 216 | PW A-B Omega 2 | Fixed gun mounts 0 and 1, one each |
| 133 | EW SkyRip LE | Fixed gun mounts 2 and 3, one each |
| 144 | LRM Anti-Base | Magazine mount 0, 13 missiles |
| 46 | Lrg Shield 3 | Shield mount 0 |

This hull has no fitting afterburner in the selection. The purchase value is
**10,000**, before credit for the previous inventory, using the server's pricing
model. It is an ordinary priced player loadout under the showcase's existing
test unlock; no relocation, damage, wallet or destruction cheat is introduced.

Nominal complete volley: **8,020 vs hull / 8,040 vs shield**. The default native
loadout of representative **Stealth Bomber 23** gives **4,000 / 4,000** against
the same target. These are separate defence responses for landed rounds, not
simultaneous damage applied to both pools. The target has 25,000 armour, 15,000
shield, and regeneration of 50/150 respectively. Selection is performed against
each side's observed enemy home station, so the tool reports the actual target
type and resolved selection rather than assuming type 10.

The reported volley/refire figures assume hits, identity combat attributes and
available ammunition/energy. They do not predict aim, obstruction, defence,
resource exhaustion or a guaranteed kill time. Exact ACK verification checks
the hull, **each mount** (including duplicate weapon IDs), missile quantity and
`FBoughtEverything`; pending/partial grants do not count as armed bombers.

## Escort, defence and lifecycle

Escorts follow a live strike bomber visible in their own client, use normal
warp routing and exit clearance, and wait when no bomber is available. They
prefer attackers from real hit events received by the bomber's own client over
nearer idle enemies. Visible enemies moving toward the bomber inside the escort
radius also qualify. Both the escort and its chase target must stay inside the
leash; otherwise the escort clears its chase and returns to the bomber.
Membership and received attacker IDs are shared within the wing; enemy
coordinates always come from the escort's own visible world.

Defence duties select station-threatening bombers/capitals before other defence
targets **before** either P622 ordinary-choice branch. This fixes the legacy
nearest bypass for base defenders and in-sector defending fighters, including
when the finale is disabled. It is the requested defence-priority correction;
ordinary rendezvous target choice remains unchanged. Aggressive escape target
switching cannot override escort, defence or active strike duties. Strike
bombers keep firing at their designated garrison while under attack.

`spawn_scenario` returns a `finale` section including the loadouts and initial
maturity conditions. `get_perf_stats.finale` supplies live gates, counts,
budgets, selected sides, grants, and timing. Times are seconds since scenario
registration: trigger, first actual strike relaunch, target destruction, and
server game over. Target time-to-kill runs from first strike relaunch to the
observed station removal in the same mission. Winner and the server's game-over
reason are recorded; this does not assume every ending was conquest.

Escort cohesion is the running mean distance to the followed bomber, sampled
once per second **only when co-located**. `bomberProtected` counts received-hit
episodes survived for the existing threat window; it describes survival, not
proof that an escort caused it. `bomberKilled` counts strike-bomber deaths,
including collision losses. Neither outcome is scripted.

On mission rotation, restore the original hull/loadout, role, arena flag,
objective and route index. Clear finale duties, pod maturity and targeting via
the existing new-life reset. Existing objective discovery and `ReReadyBot`
then resolve the new map and send `PLAYER_READY(true)`. The archived finale
continues reporting reset, ready, active, pending and idle bot counts for the
next mission. The completed run is not re-triggered in that mission.

## Validation and remaining live work

- `dotnet build AllegianceCore.slnx --no-restore -m:1`: **0 warnings, 0 errors**.
  This fresh worktree first needed a dependency restore, completed using cached
  packages with `-m:1` and NuGet audit disabled for that restore.
- **165 socket-free xUnit cases passed, 0 failed, 0 skipped**, with the normal
  environment, and **165 passed again** with
  `ALLEGIANCE_P622_LEGACY_NEAREST=1`.
- Coverage includes the new P656 cases and existing BotFixRegressionTests,
  showcase composition, catalogue resolution, P622 selection, P635 routing,
  P637 bomber routing/staging, and game-over/back planning tests.
- New checks cover gates and leader/tie/both selection; real-core damage and
  mount legality; station-simulation damage agreement; obsolete purchase
  filtering; rolling kill expiry and duplicate-observer prevention; unchanged
  pre-trigger decisions; existing bomber/fresh escort budgets; real relaunch
  requests and exact/partial ACKs; a second strike purchase after death;
  attacker-first escorting, approach detection and leash return; defence and
  station priority in both P622 modes; cohesion/protection/death status; and
  game over → mission reset → re-ready → resumed routing.

The standard `dotnet test` launcher was attempted but its VSTest communication
socket is prohibited in this sandbox (`SocketException (13): Permission
denied`). Tests were instead discovered and executed with xUnit's actual
in-process `XunitTestFramework` executor, serially, using a temporary runner at
`/tmp/p656-runner`. Logs are `/tmp/p656-build.log`, `/tmp/p656-tests.log` and
`/tmp/p656-legacy-tests.log`. No replacement assertion framework or socket
permissions were used.

The live 200v200 conquest and automatic mission rotation are **not claimed as
observed** here: Claude runs that exercise afterwards, as requested. Inspect
`get_perf_stats.finale` for both maturity shares and the recent-kill gate, full
grants, co-located escort samples, station kill/game-over times, then all bots
reset/ready with no pending or idle bots on the new map. Defenders can defeat
the strike, and a bomber that never dies must wait for its next normal relaunch.
