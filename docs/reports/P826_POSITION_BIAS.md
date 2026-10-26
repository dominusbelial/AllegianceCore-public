# P826 — showcase position-bias audit

The copied logs confirm the recent **side-1 win streak**, but do not establish a
single position defect to fix. The most consistent measured pattern is a
**civilization-dependent change in kill production over the research timeline**:
Ga’Taraan leads the pre-finale kill count in five of six post-P819 completed
matches, including when it occupies side 0; Iron Coalition wins the finale-window
kill count in all six, including when it occupies side 1. Four matches end with
Ga’Taraan’s earlier lead intact; Iron Coalition wins the other two. Alderan accounts for most of the decisive
margins. This is evidence of different fleet/tech trajectories, not proof of
intrinsic civilization balance or a complete causal explanation of the streak.

**No gameplay changes are justified by this evidence.** The measured home-to-clash
commute favors side 0, the main siege route is reciprocal, and the simultaneous
finale does not consistently increase side 1’s lead. No proven side-index server
parity bug or race-independent bot control defect explains these results.
Only the existing analysis tools, their tests, glossary and report evidence were
changed. No commits, pushes or remote-host access were performed.

Working base: public `ac932657`, with the supplied partial WIP `1ccd1ced` at HEAD.
The analysis-only verification branch of the brief applies; no local gameplay
A/B is claimed or needed for an unchanged bot/server. Small local matches would
not resolve a persistent 5–10% margin in this population.

## WIP review, boundaries and scoring authority

The WIP’s strongest component was correct: reconstructing `KILL_SHIP(105)` flags
instead of counting the final `killed by` messages. I retained that method and
re-ran every capture. **All 13 completed deadline matches reconcile exactly on
both sides’ kills and deaths.** Ejections award kills without deaths; destroying
a pod or expiring its oxygen adds a death without an opposing kill. Native:
`FedSrv/FedSrv.CPP:11770-11798,11811-11819,11918-11921,12025-12032`;
port: `src/Allegiance.Server/Server.Combat.cs:1676-1706,1747,1771-1776,1798,1841`.
This distinction matters: a pod death is not another point in the winner’s kill
margin. Two-side attribution uses the opposite of the victim’s side **only when
the kill-credit flag is true**. Every recorded credited kill also has a mapped
player-killer receipt in these captures.

I corrected or made explicit these WIP limitations:

* Docker timestamps are stripped while streaming; EOF never fabricates a result.
  Each round now has `deadline`, `ended-without-deadline`, or `censored` status.
  The previous CLI incorrectly called every countdown a completed match.
* b29 contains **seven** deadline results, side 0 winning **5/7**, then an empty
  eighth opening. The brief’s 4/5 describes a shorter selection; the supplied
  file supports the full seven-match contrast below. It is not the separate
  13-match P819 capture, which is not relabeled as b29 here.
* b32 has three deadlines and an empty opening; b33A/B each have one deadline
  plus a shutdown round with **one peer**, zero scored events and no deadline.
  Their logged Stage Over winners are excluded from competitive win tallies.
* b34 has one deadline and an unfinished second match. The supplied server file
  ends at **2026-10-26 02:09:47 UTC**, with only **one** mission `#-1` rotation
  timing receipt. `--stop-after 2` is enforced but the second receipt is absent.
  Thus no b35 opening was observed, and b34B remains censored. It has no finale
  arm or deadline. Its partial score is usable as observation, never a win.
* Ejection sectors come directly from the ejection receipt; pod-death sectors
  now use the preceding trip’s `EndSector`. Undocks reset last sector to the
  known station or explicit unknown, avoiding stale pre-rescue sectors. Other
  true deaths use last logged warp/home; no unlogged movement is invented.
* Worker initial purchases are aligned to each bot’s own reseating episode,
  since loadout selection precedes its re-ready receipt. Successful commute
  samples now also identify original-home launches. They measure from the
  preceding simulation ping, so durations are **upper estimates**, not exact
  launch times. Missing arrivals are censored. No medians are pooled as samples.
* Added attacker-hull attribution, retaining a `currentlyPod` flag. A delayed
  missile/mine can score after its launcher ejects; the last purchased hull is
  not necessarily the launch hull or damage weapon. This is never labeled
  “pod gunfire.” Victim hull/kind and true death remain separate dimensions.

The full per-match cubes, worker summaries and limits are [b29](P826_assets/b29.json),
[b32](P826_assets/b32.json), [b33A](P826_assets/b33A.json),
[b33B](P826_assets/b33B.json), and [b34](P826_assets/b34.json).
[Summary](P826_assets/summary.json) retains the sector, phase, victim-hull/kind,
true-death, economy, recovery and station-kill breakdowns for every match.
The cubes also retain all attacker-hull counts, purchases, developments, route
entries and commute distributions. Input byte counts and SHA-256 fingerprints
identify exactly which local captures were analyzed.

## Where the kill margins arise

All pairs below are **side 0 / side 1**. “Pre” ends at the server finale arm;
“finale” is the following 900 simulation seconds. These are counts, not rates;
pre-finale durations differ. Civs are Iron Coalition / Ga’Taraan except b33B,
which is Ga’Taraan / Iron Coalition. Match labels are local countdown ordinals.

| Match | Deadline kills | True deaths | Pre kills | Finale kills | Winner |
|---|---:|---:|---:|---:|---:|
| b29.1 | 701 / 671 | 596 / 577 | 563 / 585 | 138 / 86 | 0 |
| b29.2 | 945 / 863 | 736 / 646 | 743 / 682 | 202 / 181 | 0 |
| b29.3 | 874 / 649 | 525 / 740 | 644 / 529 | 230 / 120 | 0 |
| b29.4 | 816 / 735 | 614 / 663 | 542 / 548 | 274 / 187 | 0 |
| b29.5 | 761 / 914 | 726 / 610 | 621 / 727 | 140 / 187 | 1 |
| b29.6 | 884 / 893 | 731 / 690 | 642 / 793 | 242 / 100 | 1 |
| b29.7 | 843 / 750 | 621 / 722 | 646 / 560 | 197 / 190 | 0 |
| b32.1 | 887 / 954 | 661 / 722 | 619 / 707 | 268 / 247 | 1 |
| b32.2 | 1059 / 846 | 666 / 726 | 781 / 688 | 278 / 158 | 0 |
| b32.3 | 719 / 783 | 603 / 552 | 554 / 656 | 165 / 127 | 1 |
| b33A.1 | 804 / 873 | 640 / 560 | 562 / 704 | 242 / 169 | 1 |
| b33B.1, civ swap | 684 / 762 | 628 / 482 | 606 / 569 | 78 / 193 | 1 |
| b34A, countdown 1 | 731 / 763 | 640 / 610 | 444 / 550 | 287 / 213 | 1 |
| b34B, **censored** | 2356 / 2070 observed | 1937 / 2176 observed | 2356 / 2070 | not armed | none |

The six post-P819 deadline results reproduce **5/6 for side 1, four straight**.
The pre-finale/finale changes in the five unswapped matches are side-1 leads of
88, −93, 102, 142 and 106, reduced by side-0 finale gains of 21, 120, 38, 73
and 74. b33B instead has a **37-kill side-0 lead before the finale**, overturned
by **115 more kills for side 1 during it**. A civ swap demonstrates that either
civ can win from Organia; one such comparison cannot isolate a persistent
position effect from fleet trajectories and match variance.

The following decomposition exhausts scored kills in each post-P819 deadline
match. “Corridor” means every sector except the two homes and Alderan.

| Match / phase | Alderan 1052 | Ganymede 1050 | Organia 1051 | Corridors |
|---|---:|---:|---:|---:|
| b32.1 pre | 509 / 574 | 54 / 14 | 23 / 79 | 33 / 40 |
| b32.1 finale | 204 / 181 | 38 / 2 | 9 / 63 | 17 / 1 |
| b32.2 pre | 705 / 575 | 23 / 11 | 27 / 68 | 26 / 34 |
| b32.2 finale | 197 / 95 | 41 / 0 | 0 / 37 | 40 / 26 |
| b32.3 pre | 506 / 572 | 0 / 0 | 22 / 57 | 26 / 27 |
| b32.3 finale | 141 / 63 | 12 / 2 | 3 / 49 | 9 / 13 |
| b33A pre | 435 / 584 | 54 / 5 | 26 / 68 | 47 / 47 |
| b33A finale | 179 / 110 | 59 / 4 | 1 / 53 | 3 / 2 |
| b33B pre | 435 / 366 | 56 / 11 | 11 / 68 | 104 / 124 |
| b33B finale | 29 / 132 | 40 / 1 | 4 / 60 | 5 / 0 |
| b34A pre | 319 / 449 | 51 / 8 | 23 / 48 | 51 / 45 |
| b34A finale | 217 / 134 | 60 / 0 | 5 / 77 | 5 / 2 |

For example, b34A’s **106-kill pre-finale deficit** is Alderan −130,
Ganymede +43, Organia −25, corridors +6. The finale recovers 74: Alderan +83,
Ganymede +60, Organia −72, corridors +3. The net home-sector advantage for
side 1 is just 12 in that window; it does not create the pre-existing 106-kill
lead. b33B’s decisive finale reversal is likewise principally Alderan (29/132).

Pod deaths do not inflate these scored-kill tables. In b34A the full true-death
sector counts are pre: Alderan **394/181**, Ganymede **28/35**, Organia **33/102**,
corridors **17/35**; finale: Alderan **88/141**, Ganymede **50/22**, Organia
**24/87**, corridors **6/7**. These sum to the phase deaths 472/353 and 168/257.
They include oxygen expiry wherever the pod actually expired, not necessarily
where its combat hull was destroyed. Equivalent complete phase/sector death
breakdowns for b29–b34 are retained in the summary.

## Hulls, roles and research trajectory

Server first in-place hull swaps per ship/countdown show the following opening
selections. Scout counts **include support**; this is a purchase census, not
continuous fleet exposure or the requested role labels.

| Build/capture | Side 0 first hulls | Side 1 first hulls |
|---|---|---|
| b29 and each b32 match | 160 Scout, 35 Lt Interceptor | 82 Scout, 113 Fighter |
| b33A, P821 | 111 Scout, 84 Lt Interceptor | 120 Scout, 75 Fighter |
| b33B, P821 swapped | 120 Scout, 75 Fighter | 111 Scout, 84 Lt Interceptor |
| b34A and B, P821b | 82 Scout, 113 Lt Interceptor | 82 Scout, 113 Fighter |

b34’s surviving worker logs independently show equal requested roles per side:
**81 Fighter, 35 Interceptor, 35 Scout, 24 Bomber, 20 Support**. Selected roles
are **113 Interceptor / 62 Scout / 20 Support** for Iron Coalition and
**113 Fighter / 62 Scout / 20 Support** for Ga’Taraan. Thus P821b removed the
large opening combat-versus-scout fallback disparity, while the legal starting
combat hulls still differ. Earlier worker logs are unavailable; their requested
role intent and commander-wallet distributions cannot be recovered from the
server’s hull names. The repeated b34B hull swaps confirm the same opening
fleet, without inventing another set of worker `initial:` receipts.

The kill-credit victim census separates bombers and miners explicitly:

| Match | Pre bomber victims | Finale bomber victims | Miner victims, whole match |
|---|---:|---:|---:|
| b32.1 | 32 / 50 | 47 / 41 | 1 / 1 |
| b32.2 | 9 / 41 | 46 / 28 | 1 / 0 |
| b32.3 | 0 / 25 | 17 / 30 | 1 / 0 |
| b33A | 38 / 44 | 45 / 39 | 1 / 1 |
| b33B | 43 / 43 | 37 / 44 | 1 / 0 |
| b34A | 30 / 34 | 45 / 48 | 1 / 1 |

These pairs count **kills earned**, so the first number is an enemy victim,
not a side-0 loss. No pod victim earns kill credit. The remaining points come
from other hulls; the cubes retain every hull, including unlogged hulls and
NPC builders. The full b29 miner-victim pairs are 2/0, 2/0, 1/0, 1/0, 2/1,
1/0 and 2/0. Mining raids are real, but contribute too few direct points to
explain the recent margins.

b34A shows the changing combat fleet directly. Before the finale, side 0 loses
**248 Lt Interceptors and 143 Scouts** to scored kills, while side 1 loses
**176 Fighters and 199 Scouts**. Killer receipts attribute **365** of side 1’s
550 pre-finale kills to its last purchased Fighter hull; side 0’s leading hulls
are Lt Interceptor **210** and Enh Fighter **113**. During the finale, side 0’s
Enh Fighter / Adv Stl Fighter / Interceptor hulls contribute **153 / 55 / 54**
of its 287 kills. In the swapped match those same Iron Coalition hulls, now
on side 1, contribute **90 / 42 / 29** of 193 finale kills. Counts include
post-ejection delayed ordnance and are not weapon attribution or per-hull
kill efficiency; there is no continuous exposure denominator.

Server development UTCs provide actual, comparable wall-clock milestones.
From Stage Started, Bomber completes at the following seconds:

| Match | Side 0 | Side 1 |
|---|---:|---:|
| b32.1 | 756 | 846 |
| b32.2 | 831 | 1195 |
| b32.3 | 886 | 1439 |
| b33A | 766 | 865 |
| b33B | 893 | 790 |
| b34A | 812 | 991 |

Exact timestamps for **all** developments remain in the cubes. Different civ
plans unlock different work at the same step. For b34A the worker’s first
observed research steps ≥25/50/75/90 occur at **1032/1748/2225/2580** seconds
for side 0 and **1052/2023/2620/2944** for side 1, relative to its first research
status. These are adjacent worker-clock estimates, include startup, and are
not server simulation seconds. The commander reaches steps **108/157** and
**98/181** by that match’s end. Its sampled wallet medians are **25,176 / 101,060**
(319 observations each). Side 1 is not visibly commander-cash-starved.
There is no earlier worker-wallet telemetry to compare with b32 or b33.

The pattern is compatible with starting Fighters producing early kills and
Iron Coalition’s upgraded combat fleet producing later kills. Geometry,
tech availability, loadout policy, support, casualties and survivor selection
all interact. This is the measured mechanism of the phase reversal; it does
**not** identify a new incorrect purchase gate or justify changing legal hull
scores, civ tech or research plans.

## Mining, recovery and relaunch

Each match with scored events logs one initial miner and one subsequently
built Miner per side. Lost-miner counts below are from actual deaths, not a guessed live
snapshot; an extra unlogged deletion cannot be ruled out by spawn-minus-loss.

| Match | Ore sold | Miners lost | Oxygen expirations | Ship rescues | Relaunch skill events |
|---|---:|---:|---:|---:|---:|
| b29.1 | 945.0 / 90.0 | 0 / 2 | 205 / 201 | 60 / 58 | 946 / 877 |
| b29.2 | 934.6 / 90.0 | 0 / 2 | 229 / 93 | 87 / 127 | 1124 / 1128 |
| b29.3 | 742.5 / 270.0 | 0 / 1 | 184 / 332 | 80 / 87 | 869 / 1032 |
| b29.4 | 871.2 / 270.0 | 0 / 1 | 228 / 242 | 74 / 119 | 985 / 990 |
| b29.5 | 675.0 / 0 | 1 / 2 | 182 / 243 | 108 / 118 | 1141 / 936 |
| b29.6 | 877.5 / 270.0 | 0 / 1 | 201 / 183 | 101 / 119 | 1145 / 1046 |
| b29.7 | 817.3 / 0 | 0 / 2 | 157 / 228 | 85 / 91 | 997 / 1010 |
| b32.1 | 405.0 / 270.0 | 1 / 1 | 186 / 196 | 93 / 132 | 1137 / 1061 |
| b32.2 | 873.8 / 180.0 | 0 / 1 | 153 / 104 | 145 / 158 | 1078 / 1229 |
| b32.3 | 816.0 / 270.0 | 0 / 1 | 193 / 211 | 114 / 136 | 995 / 896 |
| b33A | 472.5 / 0 | 1 / 1 | 167 / 91 | 97 / 118 | 1093 / 987 |
| b33B | 494.2 / 472.5 | 0 / 1 | 224 / 195 | 102 / 116 | 940 / 897 |
| b34A | 405.0 / 270.0 | 1 / 1 | 248 / 195 | 61 / 82 | 952 / 895 |
| b34B, censored | 2767.5 / 810.0 | 0 / 2 | 339 / 218 | 249 / 409 | no final skills report |

Side 0’s mining advantage persists through losing matches. b33B’s nearly equal
sales also accompany a side-1 win. Sold ore is neither potential extraction nor
commander income; no income ledger supports converting it into a cash deficit.
Per-match observation spans differ, so totals across the long b34B match are
not compared as equal-duration rates.

Recovery is material but does not present a uniform side-1 launch-delay defect:

| Match | Successful dock trip median, sim seconds (n) | Ship-rescue trip median, sim seconds (n) |
|---|---|---|
| b32.1 | 187.34 (88) / 206.19 (18) | 108.53 (93) / 134.73 (132) |
| b32.2 | 228.53 (58) / 164.77 (164) | 100.75 (145) / 27.83 (158) |
| b32.3 | 221.97 (70) / 247.71 (12) | 70.20 (114) / 85.81 (136) |
| b33A | 207.90 (100) / 204.71 (124) | 91.82 (97) / 100.65 (118) |
| b33B | 187.90 (11) / 239.63 (89) | 85.81 (102) / 94.11 (116) |
| b34A | 205.45 (42) / 195.38 (11) | 52.59 (61) / 72.78 (82) |

Oxygen endings are approximately 300 simulation seconds for both sides.
b32.1 additionally has **93** side-0 probe rescues, b33A **54**, and none for
side 1 in those matches; these remain distinct from ship rescues. b34A’s
matched dock-end-to-undock median ping brackets are **0.049–0.050 s (42)** and
**0.049–0.081 s (11)**; ship rescues are **0.047–0.049 s (49)** and
**0.049–0.087 s (68)**. Oxygen-to-undock brackets are **0.049–0.050 s (190)**
and **0.705–0.705 s (186)**. Unmatched recovery events are censored, and true
combat pod deaths are not treated as oxygen or successful dock returns.
These subsecond launch differences do not explain the much larger travel
and 300-second oxygen durations. All outcomes and quantiles, including b29,
b34B and Alderan-only ejections, remain in the evidence.

## Geometry, travel and simultaneous finale

[Geometry](P826_assets/geometry.json) was re-extracted from the local native
`m2xspiderw.igc`, with its own SHA-256. Coordinates agree with P819 within
0.001 unit. These are straight flight legs between authored station/aleph
positions; they exclude turning, warp exit clearance, obstacles and combat.

| Home → Alderan entry route | Home leg | Corridor leg | Total to entry | Total to origin reference |
|---|---:|---:|---:|---:|
| Ganymede → Rigel → Alderan | 4,013 | 3,979 | **7,993** | 13,987 |
| Ganymede → Plato → Alderan | 5,794 | 6,559 | 12,353 | 17,824 |
| Organia → Juniper → Alderan | 6,308 | 6,163 | **12,471** | 18,365 |
| Organia → Gaia → Alderan | 4,264 | 11,948 | 16,213 | 20,629 |

The actual dominant clash lanes are Rigel and Juniper: b34A pre-finale
non-pod, non-bomber entry counts are **589** via 1059→1052 for side 0 and
**418** via 1061→1052 for side 1. These are repeated life crossings, not
unique pilots. Other entries remain in the cube.

Successful **original-home launch → first non-pod Alderan entry** samples:

| Match | Scout median sim seconds (n), side 0 / 1 | Starting combat hull median (n), side 0 / 1 |
|---|---|---|
| b33A | 78.33 (200) / 165.90 (225) | Lt Interceptor 189.00 (141) / Fighter 199.23 (95) |
| b33B swapped | 73.00 (192) / 191.35 (163) | Fighter 97.86 (98) / Lt Interceptor 240.26 (106) |
| b34A | 85.96 (167) / 173.66 (176) | Lt Interceptor 162.36 (215) / Fighter 208.20 (141) |

The earlier-ping launch estimate slightly overstates travel duration. Samples
include diversions, exploration and combat; ships killed before arrival are
absent. They therefore measure observed successful journeys, not controlled
maximum-speed tests. Both geometry and these observations oppose the proposed
shorter side-1 reinforcement commute. They cannot determine whether faster
side-0 reinforcement also feeds more losses into a concentrated enemy fleet:
that requires continuous occupancy and damage/exposure data.

The main siege lane is Ganymede → Bug Nebula → Tellus → Organia and its
reverse. Station-to-station distance is **26,356 in either direction**. Four
legs are **3,434 + 7,350 + 7,992 + 7,580** for side 0, reversed for side 1.
Estimated flight to the 3,500-unit target standoff is **22,856 / 22,922**.
The brief’s **8,330 / 4,184** old-hold remainder is attacker-indexed: 8,330
approaches **Organia**, 4,184 approaches **Ganymede**. It is not a measurement
showing Organia’s defender has a 4,184-unit route. Moreover, the old aleph
hold was superseded by P819, so it is not the current assembly point.
Native reads authored warp positions (`Igc/warpIGC.cpp:24-41`); map asymmetry
is legitimate content, not a port defect.

Only b34 retains worker logs, so earlier per-worker selections and commits
cannot be reconstructed from server `armedBy` alone. b34A has:

| Measurement | Side 0 | Side 1 |
|---|---:|---:|
| Worker selections | both workers | both workers |
| Logged flying-strike promotions | 19 | 19 |
| Finale commits, worker 1 | 2 | 3 |
| Finale commits, worker 2 | 3 | 1 |
| Total massed / staging-timeout commits | 4 / 1 | 4 / 0 |
| All logged Bomber target-home crossings during finale | 46 | 38 |
| Named-promotion Bomber target-sector arrivals, once per life | 38 | 33 |
| Median server-arm → named arrival, sim seconds | 385.94 | 384.92 |
| Named Bomber en-route ejections | 2 | 4 |

Both workers explicitly log `trigger sides=[0,1]` at registration age 1800s.
Worker peaks assembled are 4/3 and 3/3. Each side has one no-buyable-bomber
warning per worker. Selection opportunities are now symmetric; commits are
real on both sides. Arrival and ejection counts in the named subset are not
a fleet-wide survival rate or a continuous reservation census.
[Transits](P826_assets/b34-transits.json) show Bug Nebula crossing-to-crossing
median residence **85.07 / 85.36 s** and Tellus **92.26 / 93.56 s**. This subset
includes holding/combat, excludes legal non-Bomber fallbacks and does not
measure final physical standoff assembly. It supplies no large one-direction
corridor-transit deficit.

The station-damage receipts contradict the brief’s “rare station kills” premise
for these captures. In b34A **six** stations are destroyed: side 0 destroys
five in Organia (Teleport, Tactical, Teleport, Palisade, Expansion), side 1 one
Tactical in Ganymede. All occur before the finale arm. In b33B side 1 destroys
six stations, including Ganymede’s Garrison at **sim 1574.383**, and side 0
one; other restart bases keep the match running. b33A has one side-0 Tactical
kill. These are real `killed=True` receipts, preserved verbatim with IDs and
UTCs; a missile’s current launcher hull may already be a Lifepod. Destroying
one station is not automatically a mission victory. All competitive results
here still come from deadline kills.

## Ordered loops, worker assignment and the unfinished rotation

`armedBy=0` appears in all six completed post-P819 matches, whose winners
include both sides. It only records which valid mission/epoch request armed
the one shared deadline; it does not select an attacker or winner
(`Server.Showcase.cs:15-37`). The worker picks its first selected-side bot in a research-mode mission
when sending that request (`BotManager.Finale.cs:438-442`), then reserves
bombers **for every selected side** (`:443-460`). This explains the repeated
label without treating it as an advantage grant.

There is no all-side-0-then-all-side-1 combat tick: the side loop updates each
side’s economy/research, then sectors update their model lists
(`Mission.cs:809-815,877-892`; `Sector.cs:954-962`). Native likewise walks sides,
then clusters and insertion-ordered models (`Igc/missionigc.cpp:3138-3146,
3168-3175`; `Igc/clusterIGC.cpp:241-253`). Those serial traversal orders alone
are not evidence of an incorrect port or of an order-induced kill advantage.
No ship-order replay with inverted insertion order exists in the copied logs,
so an order effect cannot be completely ruled out.

Workers split the plan independently per side (`WorkerPartition.cs:3-22`);
b34 trigger telemetry reports `combat=98` per side on worker 1 and `combat=97`
on worker 2, with research commanders on worker 1. This readiness counter
includes participating support pilots (`BotManager.Finale.cs:360-363`); it is
not a hull-role census. Both positions therefore share
each worker’s scheduling load. In b34A side 1’s first undock actually precedes
side 0’s by about **50 ms**, but side 0 reaches Alderan about **90 s earlier**.
The swapped b33B retains side 0’s earlier clash entry. Startup latency is not
an observed side-1 first-arrival mechanism. Equal slot counts do not prove
identical per-client CPU or network latency; no such full trace is available.

b34B repeats both opening fleets and continues research, but neither worker
logs a new finale trigger or `re-armed` receipt. The server never arms its
deadline. At EOF side 0 leads **2356/2070**, with **2284/1944** kills in Alderan
and **72/126** in Juniper. This is **not a side-0 victory** and cannot join the
5/6 tally. It is useful evidence that the position does not force side-1
kill superiority in every observed trajectory. Both research plans eventually
reach their last steps in the worker capture, which is still not a match result.

Missing finale re-arm is an observed bot orchestration failure, separate from
the completed-match position hypothesis. `RearmRotatedFinales` waits on every
participating bot’s reset, objective resolution, generation and world readiness
(`BotManager.Finale.cs:316-343`). The logs do not expose which member/gate
blocked the transition. Changing that gate speculatively could reuse stale
station objectives. I leave this known censored-match issue explicitly
unfixed here; it cannot explain the completed first-match streak and is not
a proven cause of position bias.

## Decision and rig-side follow-up proposal

No changes to bot selection, finale policy, combat, economy, native timing,
civilizations or human-server rules were made. The evidence supports locating
the margins and rejecting several simple explanations; it does not support
claiming a uniquely proven position-bias cause or a validated fairness fix.
In particular, simultaneous home attacks do **not** systematically benefit
side 1 in the measured window, and the fastest authored clash route favors
side 0. The fleet/tech-phase pattern follows the civ in the one swapped match.

For Claude/Domingo’s decision, use paired **home assignments** on the same
native map, with civ tuple and bot settings held fixed, and cross those pairs
with the reverse civ tuple. Swapping civs alone does not swap home geometry.
P825’s creation-time matchup planning is a suitable future place to add an
explicit, logged home permutation and replay seed; none is implemented or
configured by this task. Preserve authored positions and ordinary game rules,
then assign teams to the alternate home at assembly. If that operational
change is undesirable, select a more symmetric native map or alternate
original/mirrored map content after review. No map was altered here.

Until the separate re-arm failure is diagnosed, fresh one-match launches avoid
counting long undelimited rotations as normal trials. Retain native deadline
scores and phase counters; treat station destructions, commander funds and
successful arrivals separately. A balanced matched design is more informative
than adding more consecutive runs with only one civ/home arrangement. A few
small accelerated local matches can check orchestration but cannot establish
or exclude a 5–10% 200v200 margin. No rig action was taken from this lane.

## Verification and reproducibility

Analysis-only result: no production C# or gameplay behavior changes, so the
brief’s analysis-only gate was used instead of the solution/tool build,
fast-tier, never-parks and gameplay A/B gates. The existing audit/summarizer/
transit toolchain ran end-to-end on all supplied captures; no raw logs or media
were added. Six focused Python evidence-integrity tests pass **three isolated
runs**: ejection-versus-death semantics, phase scoring/deadline reconciliation,
pod end-sector correction, EOF/admin-round censoring, interleaved per-bot
reseating, and synthetic native-map routes/wrapper validation. The synthetic
route case checks reciprocal siege distance alongside unequal clash commutes.

Raw inputs remain as supplied in ignored `.scratch/rig/`; scratch validation
outputs are under `.scratch/p826/`. Public artifacts are small JSON only,
under the task’s 2 MB limit. See [validation](P826_assets/validation.json) for
reconciliation, coordinate cross-check, tests and hygiene results.

From the repository root:

```sh
for batch in b29 b32 b33A b33B; do
  python3 tools/p819-side-bias-proof/audit.py --per-match \
    --server .scratch/rig/$batch/server.log \
    --output docs/reports/P826_assets/$batch.json
done
python3 tools/p819-side-bias-proof/audit.py --per-match --stop-after 2 \
  --server .scratch/rig/b34/server.log \
  --bots .scratch/rig/b34/bots/err.worker-1.log .scratch/rig/b34/bots/err.worker-2.log \
  --output docs/reports/P826_assets/b34.json
python3 tools/p819-side-bias-proof/transits.py --stop-after 2 \
  --server .scratch/rig/b34/server.log \
  --bots .scratch/rig/b34/bots/err.worker-1.log .scratch/rig/b34/bots/err.worker-2.log \
  --output docs/reports/P826_assets/b34-transits.json
. ./local.env
python3 tools/p819-side-bias-proof/summarize.py \
  --map "$ALLEGIANCE_ARTWORK_PATH/m2xspiderw.igc" \
  --output docs/reports/P826_assets/geometry.json
python3 tools/p819-side-bias-proof/summarize.py --position \
  docs/reports/P826_assets/b29.json docs/reports/P826_assets/b32.json \
  docs/reports/P826_assets/b33A.json docs/reports/P826_assets/b33B.json \
  docs/reports/P826_assets/b34.json --output docs/reports/P826_assets/summary.json
python3 -m unittest discover -s tools/p819-side-bias-proof -p 'test_*audit.py' -v
```

## Native behaviour classified

| Finding | Classification | Treatment |
|---|---|---|
| Kill credit on combat hull loss, distinct pod death/ejection counters | Native design: `FedSrv/FedSrv.CPP:11770-11819,11918-11921,12025-12032` | Preserve; audit the actual flags and reconcile scores. |
| Kill-count deadline and deaths tiebreak | Native design: `FedSrv/fsmission.cpp:2468-2498` | Preserve; every competitive result here is a kill-count decision. |
| Authored non-mirror map positions and unequal clash commutes | Native design: `Igc/warpIGC.cpp:24-41` | Measure and retain; propose operational home/map alternation for review. |
| Civ-dependent legal hull/tech trajectory | Native content/design; bot role classification is policy | Retain; no balance or research-plan changes. Counts do not prove efficiency. |
| Ordered side/cluster/model updates | Native serial implementation, an era constraint: `Igc/missionigc.cpp:3138-3175`, `Igc/clusterIGC.cpp:241-253` | No demonstrated incorrect port or quantified order effect; no speculative reordering. |
| Both-side finale selection and first requester’s `armedBy` | Port-only showcase orchestration | No native fairness rule to port; b34 selections/commits are symmetric enough to reject the old opportunity-count explanation. |
| b34B does not re-arm | Observed port/bot failure, blocked gate not identified | Explicitly censored and not patched as a position-bias fix. Needs member/gate evidence before changing the barrier. |
| WIP completion labels, stale sectors and worker purchase alignment | Analysis-tool defects | Corrected and covered by focused evidence-integrity tests; no gameplay changes. |

## Modern alternative considered

Use a compact structured match ledger keyed by mission epoch, life generation,
side, hull and simulation timestamp, recording kill flags, current sector,
research milestones and finale reservation/reset-gate transitions. Sample active
fleet exposure and entry-to-engagement distances so hull efficiency and arrival
attrition have actual denominators. This would replace fragile cross-log
inference while retaining native combat and map intent. Pair that telemetry
with seeded home/civ permutations and enough completed, comparable matches to
estimate uncertainty. Parallelizing the legacy simulation or introducing
handicaps without an order/exposure experiment would not be justified by these
captures. Neither telemetry expansion nor experimental gameplay was added here.
