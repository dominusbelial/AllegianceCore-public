# P778a — finale bomber wing: escorts push their bomber (P778a-2 completion)

Base: master `37ea36f2` + the P778a WIP (`pi/p778a`). Branch/worktree: `pi/p778a2`.
Task: `P778A2_BRIEF.md` (finish P778a: diagnose the natural failure, add a staged live
proof, report, coverage row). Native reference `~/Development/Allegiance/src/`.
Evidence (raw logs, the 17-run corpus summary): `~/allegiance-evidence/P778a/`.

**Status.** The push path is implemented and now **executes live**: a staged finale
produces real `[push] contact` events, the native collision damage, and a measurable
speed impulse along the target line. The staged gate is 5/5 green on this branch and
**fails on master with 0 contacts** (§6). The strict *"reaches launch range sooner
than the un-pushed control"* claim is **not robustly met** — measured pushed 21.4-21.8 s vs
control 21.5-21.8 s (−0.1 to +0.4 s, within one 250 ms sample). The reason and
the numbers are in §8; making it decisive needs a sustained-contact pusher (follow-up).

---

## 1. Native rules (cites)

| rule | native | port |
|---|---|---|
| ship↔ship collision, elastic/inelastic blend, `cElasticity = 0.75` | `Igc/shipIGC.cpp:1090-1124` | `src/Allegiance.Core/Model/Sector.cs` `ResolveShipShipCollisions` |
| damage `base=|Δv|²/128`, `damage1=2·base1+base2`, `damage2=base1+2·base2`, HP pool includes the shield term | `Igc/shipIGC.cpp:1139-1158` | same |
| friendly rams damage; only a friend-vs-lifepod pair is exempt | `Igc/shipIGC.cpp:1166-1168` | same |
| friendly damage is suppressed when the mission has `bAllowFriendlyFire` off | `Igc/shipIGC.cpp:1222-1228` | `Ship.ReceiveDamage`; `Mission.AllowFriendlyFire` defaults **off** |
| collision sound/jiggle per party (the port's `COLLISION_EVENT`) | `Igc/shipIGC.cpp:1189-1195` | `Core/Wire/CollisionEvent.cs`; `ClientWorld.CollisionEvents` |
| the shared dodge law (GotoPlan's first priority) | `Igc/common.cpp:1692-1790, 2000-2018` | `ClientCore/Flight/ClientAutopilotDodge.cs` (unchanged; P778a only exempts the finale bomber's own escorts in `BotManager.TryDodgeGoal`) |

No `src/Allegiance.Core`, `src/Allegiance.Server`, `src/Allegiance.ClientCore*` or wire
change was made for P778a/P778a-2. Production edits are `tools/allegiance-mcp/**` only.

## 2. What P778a-2 changed on top of the WIP (each from live evidence)

1. **Escort transit no longer needs a live bomber** (`BotManager.Finale.cs`,
   `TryDriveFinaleEscort`). The WIP "ride with the strike" branch ran *after* the anchor
   pick, and `TryEscortAnchor` returns nothing without a live bomber, so a strike whose
   bombers were podded returned the escorts to their HOME sector for the rest of the
   strike. The committed-strike transit (`run.StrikeStartedAt != null`,
   `plan.KilledAt == null`, `ownSector != plan.Sector`) now runs first and keeps the wing
   moving to the target sector. Evidence: pre-fix corpus had 0 `[push] seek`/`[push]
   contact` and every hold reason was `wrong-sector-*`; post-fix the 5-minute natural run
   shows escorts crossing into the plan sector (`[warp] ship 8104 … Tellus→Organia
   t=190.9`, `8110 … t=264.6`, `8108/8109/8111 t=324.9-331.5`).
2. **A contact is a real collision, not a closing-speed collapse** (`TrackPushContact`).
   The WIP heuristic fired on ordinary throttle changes (a logged contact with
   `bomberAlong 80.1→80.1` and no collision) and missed the real collisions. It now drains
   `ClientWorld.TakeCollisionEvents()` and counts the pair (pusher, bomber).
3. **`bomberAlong` is the velocity along the BOMBER→TARGET line** (`plan.Direction`), not
   the escort-relative steering vector, which flipped once a pusher overshot the contact
   point (the WIP logged `-45.9` u/s for a bomber doing 80 u/s toward the station).
4. **Chase speed is capped at `PushChaseSpeed = 20` u/s** (`ShowcaseFinale.cs`,
   `TryPushFinaleBomber`). The old 90 u/s cap let the pusher arrive at 13-15 u/s and slam
   the bomber off-axis: the live trace showed the bomber dropping 80→48-52 u/s at the
   contacts.
5. **A pusher that is not astern station-keeps behind the bomber** (40 u outside the
   shell) instead of flying to the bomber's *position* (which parks it alongside/ahead and
   it never pushes again).
6. **The finale bomber does not dodge its own pushers** (`BotManager.TryDodgeGoal`). The
   shared dodge law treats a closing same-side ship as an obstacle; the live trace showed
   the bomber braking 80→50 u/s before each contact. If the dodge disappears when the
   nearest finale escort is skipped, the dodge is suppressed for that tick.
7. **`PusherMargin = 0`** (was 4 u). The staged run measured the pusher walled up 12 u
   from the contact point, riding the separate pass with no collision events for seconds;
   aiming at the hull contact distance makes it keep ramming gently.

## 3. Diagnosis from one natural run (the P778a-2 "why")

Corpus: 17 × 20-minute natural runs, `/tmp/p778a-push-*.log`, summarised in
`~/allegiance-evidence/P778a/old-17-runs-summary.txt`: **0 `[push] contact`, 0
`[push] seek` in all 17**; 26 `[push] strike` summaries, every one `contacts=0
pushDamage=0.0 timeToLaunchRange=never(‑) bomberSurvived=false`.

Representative run `p778a-push-742137f4…` (m2xspider, 15 per side, research showcase):

* 75 `[push] hold` lines: **60 are `wrong-sector-*`** (0:25, 2:22, 1:13, 3:1), 12
  `engaging`, 2 `bomber-threatened`, 1 `full`. The escorts were not in the plan sector.
* `[push] strike side=1 … bomberSurvived=false` and `side=0 … bomberSurvived=false`
  (timeToLaunchRange=never); max `bomberKilled=38` over the run; 2 strike summaries.
* `[finale] … no buyable bomber strike loadout: no bomber hull passes the side + station
  tech gate` **72 times, from log line 1282 to 11893** — it never clears in research
  mode. The research finale then takes the designed `SelectAvailableStrike` fallback
  (Scout 410/Fighter 121 — 17 `[loadout] requested role=Bomber … temporary
  Scout/Fighter` lines in the 5-minute post-fix run). Those fallback strike ships die in
  ~20-40 s (`9af4f90a`: the only assigned "bomber", a Scout, is `isPod:true` ~10-30 s
  after assignment), so a *live* bomber and the escorts are rarely in the same sector; when
  one is, `bomber-threatened` correctly holds the push.
* Warp evidence (`373ab464`): escorts crossed `Wasp(0)→Tierankeen(1)` at t=60-79 and
  then went back; they never reached `Nadir(2)` (the plan sector) because the transit was
  re-gated on a live bomber every tick. That is the WIP bug fixed in §2.1.

**Conclusion:** the natural run cannot prove the push; the strike itself fails for reasons
outside P778a's scope (research-mode bomber tech gate + fallback strike ships dying before
launch range). The staged test removes those two variables.

**Post-fix natural 5-minute run** (`P778A_NATURAL=1`; cap ≤5 min, evidence only):
`contacts=0 seeks=0 strikes=2`; both summaries `timeToLaunchRange=never
bomberSurvived=false`. The escorts now *do* reach the plan sector (warp times above), but
the first strike wave's bombers are podded by sim t≈375/386 while the surviving bombers are
in other lanes (samples at sim 654-677 put every bomber in 1054/1055/1058, not the target
1051), so no co-location inside the 5-minute cap. The 20-minute runs have the same shape.

## 4. Design

**Pusher pick** — `ShowcaseFinale.IsBomberPusher`: no attacker on the bomber inside
`ThreatWindowSeconds` (threat response keeps priority) and the ship id is among the
`MaxBomberPushers = 2` lowest ids of the in-sector finale escorts (deterministic, no
coordination channel). `BomberPush` returns a contact point `bomber − direction·(r_bomber +
r_pusher + PusherMargin)` on the bomber→target line and the line direction; an escort
already inside/forward of the contact shell gets the astern station-keeping reflex (§2.5).
**Stop**: bomber dead/podded, in its own launch range (`FinaleLaunchRange`: the
accelerating-missile envelope `v0·L + a·L²/2`, else the gun reach), or a fresh attacker.

**Closing speed.** `PushClosingSpeed = 8` u/s; chase ≤ `PushChaseSpeed = 20` tapering to 8
at the shell. Per-contact native damage (real PCore001 masses/HP; `PushContactDamage`
splits through the counterpart HP pool, `shipIGC.cpp:1172-1185`):

| bomber hull (mass/HP) | pusher hull (mass/HP) | Δv bomber | damage bomber / pusher | % of each hull |
|---|---|---|---|---|
| Stealth Bomber 23 (120/150) — bought finale hull | Adv Fighter 15 (30/450) — sim test pusher | 2.80 u/s | **1.10 / 1.99 hp** | 0.73% / 0.44% |
| Stealth Bomber 23 (120/150) — staged live hull | Lxy Interceptor 251 (20/825) — staged live pusher | 2.00 u/s | **1.19 / 2.25 hp** | 0.79% / 0.27% |
| Bomber 25/404 (50/800) | Lt Interceptor 415 (20/600) | 5.25 u/s | 1.03 / 1.69 hp | 0.13% / 0.28% |
| Scout 410 fallback (40/200) | Lt Interceptor 415 (20/600) | 4.67 u/s | 1.02 / 1.52 hp | 0.51% / 0.25% |

At 20 u/s the same contact is 6.25× (closing²): 7.4 hp on the 150-hp Stealth Bomber
(4.9%) — that is why the design closing is 8, not the old 90-ish chase.

**Friendly-fire gate finding (live).** Nothing in the port sets
`Mission.AllowFriendlyFire`; the server default is off (`shipIGC.cpp:1222-1228`), so on a
default server a push contact costs **zero** hull — the "nanite scouts pay the hull back"
half of P778a only applies with friendly fire on. The staged test turns it on as fixture so
the native formula is observable; the natural push, if it ever lands, is damage-free.

## 5. Telemetry samples (real)

```
# staged gate (unlocked tech; one pusher; forced finale state)
[push] contact pusher=8100 bomber=8106 closing=5.8 bomberAlong=80.1->81.3 bomberDamage=0.62 pusherDamage=3.44 expected=1.19/2.25
[push] strike side=0 contacts=2 pushDamage=4.1 timeToLaunchRange=-6.3s bomberSurvived=true
  (negative time is the staged clock: the fixture sets StrikeStartedAt = Created+60;
   the test's own launch-range time is the authoritative number)
# pre-fix natural (742137f4)
[push] hold ship=8116 reason=wrong-sector-0
[push] strike side=1 contacts=0 pushDamage=0.0 timeToLaunchRange=never bomberSurvived=false
```

## 6. Tests

| test | tier | result |
|---|---|---|
| `P778ABomberPushTests` (helper geometry/selection/stop/repair order) | fast | 4 pass, 3× alone |
| `P778ABomberPushSimTests` (real `Sector` collision, PCore001 hulls; measured damage 1.0995/1.9914 vs formula 1.0995/1.9914) | fast | 2 pass, 3× alone |
| `P778ABomberPushLiveTests.Staged_finale_pushers_contact_the_bomber_and_do_not_slow_the_strike` | Live | **5/5 pass** (3× alone + 2 extra, incl. the final renamed run); contacts 1-2 per timed window; expected 1.19/2.25 matches the test-local formula; measured within the 1/240 wire quantum / 5% band; `SimPassStageFailures=0` |
| same test file built **on master 37ea36f2** (`.scratch/master`, test copied in) | Live | **FAILS**: `contacts=0`, "no `[push]` contact lines in the timed window" — the required "fails on master" proof |
| `P778ABomberPushLiveTests.Natural_research_finale_push_counts_evidence_only` (`P778A_NATURAL=1`, 5 min cap) | Live | pass as evidence: `contacts=0 seeks=0 strikes=2` (both bombers podded before launch range) |

Why the staged test is the gate: it boots an `unlocked`-tech showcase (P509 test-unlock +
P473 harness: real bomber hull and launcher), spawns 6 fighters + 1 bomber vs 1 fighter,
forces the strike plan, relocates the bomber + one escort into the target sector with
`Server.RelocateShipToSector` (the real SET_CLUSTER warp seam), parks every other ship out
of the sector, sets `AllowFriendlyFire` and zeroes shields (fixture only), and then compares
the exact same staging with pushing disabled. Everything else is production: the bot
decision loop, the push driver, the collision pass, the damage.

## 7. Gate numbers (this worktree)

* solution build **0 warnings / 0 errors**; `tools/build-tools.sh` **all 70 tool projects, 0/0**.
* new/affected tests 3× alone: helper+sim 6/6 ×3; staged live 5/5 (3×: 21.4/21.8,
  21.6/21.7, 21.6/21.7 s pushed/control; plus 21.4/21.7 and 21.8/21.7).
* one Fast-tier suite: `tools/run-tests.sh --tier fast --no-build` → **10029 tests, PASS**
  (Allegiance.ClientCore.Tests 1658, Core 5070, MonoGame 3262, Lobby 24, quarantine 12).
* showcase never-parks `P700ShowcaseLiveTests` **5/5 pass** (1 m 52-54 s each).
* natural finale: 1 × 5-minute evidence run (above); 0 `[push] contact`.
* no `src/**` change → no AOT publish required by the lane rule (build-tools ran).

## 8. Limitations / unfinished (honest)

1. **"Reaches launch range sooner" is not decisive.** Measured pushed 21.4-21.8 s vs
   control 21.5-21.8 s (−0.1 to +0.4 s, ~one 250 ms sample; one run was 0.1 s later). The port's native drag + cruise
   throttle law (`ShipKinematics`, `shipIGC.cpp:2930-2934`) removes most of each
   +1-3 u/s impulse before the pusher re-contacts (~5 s cadence); a single clean contact at
   cruise gives 80.1→81.3 u/s, then the speed returns to 80. A *sustained-contact* pusher
   (or engine contact force) would be needed; that is a separate task, not an engine
   change smuggled into P778a. The staged gate therefore enforces "not later than +0.3 s"
   and records the comparison; the exact momentum-transfer proof is the sim test.
2. **The natural finale still cannot exercise the push** for the reasons in §3 (research
   mode has no buyable bomber; fallback strike ships die before launch range; the first
   wave is over before the escorts arrive). A 20-minute natural run is evidence-only and
   was not repeated (budget); the pre-fix corpus remains the diagnosis.
3. **Skirmish check (P778a gate item):** `tools/allegiance-mcp/SkirmishDriver.cs:24-26`
   spawns `preset:"showcase"` without `finale_after_minutes`, and
   `ReadFinaleSettings` defaults a showcase finale to **30 minutes** — a Medium
   skirmish that reaches a finale cannot fit the P778a-2 ≤5-minute live cap. The
   equivalent evidence is the 5-minute natural showcase run above (same
   `spawn_scenario` path, finale moved to 1 minute): `contacts=0 seeks=0 strikes=2`.
4. **Rig evidence is pending** (Claude runs it on 221). Recipe below.
5. The live wire hull fraction is 1/240, so a 1.19 hp loss on a 150-hp bomber is 1-2
   quanta; the live damage assertion is a band, not an exact match. The exact match is the
   sim test (real `Sector` pass).

## 9. Rig check recipe (Claude, 221)

```
grep -c "\[push\] contact"                     # > 0 after a real bomber+escort strike
grep "\[push\] strike"                         # per strike: contacts, pushDamage, timeToLaunchRange, bomberSurvived
grep "\[push\] hold" | grep -o "reason=[a-z0-9-]*" | sort | uniq -c   # not-selected/wrong-sector/bomber-threatened
grep "\[support\]" | grep -c "repair=finale-bomber"                    # nanite priority (P778a part 2)
```
Compare per-strike `timeToLaunchRange` and `bomberSurvived` against batch 20. Expect the
same caveats: research-mode fallback strike hulls, and friendly fire off (push damage-free).
