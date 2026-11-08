# P958 — deterministic pod load investigation

Status: complete. All P958 checks pass; the single Fast gate has the unrelated
failures/timing violations documented below. Changes remain in the working tree; no commits,
pushes or deployments. No operational/game hosts, rig, mini or Mac were contacted.
This report was written before running the gate.

## Scope and method

Seed mission simulation randomness while retaining the production default. Reproduce
both P699b traffic cases across at least 30 seeds, trace failures, compare the
responsible rules with native, then pin and validate a native-honest regression.
Raw local output is retained under `.scratch/p958/` during this task. Base is
`692c84d4`; qualification uses the rig's code defaults (snapshot + encode-once,
parallel build, pool transport, native ship-update model and native GotoPlan),
with server GC explicitly enabled. No performance optimization or rig A/B is
claimed; wall time is test-run evidence only.

Setup exception: the first automatic restore attempted online NuGet vulnerability
audit metadata and failed on a read-only user cache. The brief's no-public-host
restriction should have been applied to that SDK behavior too. A writable
worktree HTTP cache was used during initial setup; subsequent build/qualification
gates restrict restore sources to the existing local package cache and disable
NuGet audit. No operational/game host was contacted.

## Entropy inventory

Production defaults are unchanged: `Mission.Random` is `Random.Shared`, and the
server's optional `SimulationRandom` is null. A supplied generator is installed
before assembly and shared by server ejection, warp exits and mission consumers.
Standalone `Mission.Assemble(..., random: ...)` also supports injection. Existing
explicit weapon/chaff/launch RNG overrides still take precedence. Map generation
keeps its existing seeded abstraction; explicit simulation injection supplies a
child seed only where no map seed/rotation receipt exists. No new production config/env var; test-driver knobs are documented below.

| Draw | Port consumer | Native source (re-grepped) |
| --- | --- | --- |
| Coincident collision centres | `Collision/Separate.cs`, all three `Sector` bounce paths | `Igc/shipIGC.cpp:523-533`; unit direction `zlib/vector.cpp:37-46` |
| Cargo/treasure drop direction | `Sim/Ship.cs` `RandomDirection` | `FedSrv/FedSrv.CPP:4324-4337` |
| Chaff spoof choice | `Sim/Ship.cs` `DeployChaff` | `FedSrv/FedSrv.CPP:5680-5700` |
| Complaint backoff | `Sim/Ship.cs` `ComplainMiningCrowded` / `ComplainIdle` | `Igc/shipIGC.h:1723-1734`, multiply by uniform [1.5,2.5) |
| Weapon dispersion | `Sim/Weapon.cs` | `Igc/weaponIGC.cpp:259-260` |
| Probe initial attitude / dispersion | `Sim/Probe.cs` | `Igc/probeigc.cpp:123-128,483-484` |
| Station launch jitter and fallback direction | `Sim/Station.cs` `Launch` | `Igc/stationIGC.cpp:299-382` |
| Treasure selection, placement, lifespan and regeneration | `Model/Mission.Treasure.cs` | `Igc/missionigc.cpp:4125-4214`, `FedSrv/fsmission.cpp:2516-2552` |
| Survivor burst | `Server.Combat.cs` `EjectSurvivor` / server `_rand` | `FedSrv/FedSrv.CPP:11944-12006` |
| Aleph exit wobble, roll and offset | `ServerHost.WarpExitRandom` / `AlephExit` | `FedSrv/fsship.cpp:90-132` |
| File-map asteroid top-up | existing `MapRandomFactory` via server assembly | `Igc/missionigc.cpp:3977-4055`, `Igc/common.cpp:2844-2893,3020-3079` |

Native's shared generator is C `rand()` (`zlib/zmath.h:114-129`) seeded at
`FedSrv/FedSrv.CPP:10245` with `srand(GetTickCount() + time(NULL))`. This change
preserves the port's distributions and draw formulas; it does not claim .NET's
seed sequence equals a Windows CRT sequence. The seed is a reproducibility hook,
not a new gameplay rule.

The initial mission-only injection was insufficient. Complete telemetry still
differed; injection after boot left assembly random. Server injection before
assembly still exposed unseeded file-map asteroid top-ups. Seeding that existing
stream made both complete 200-pod runs repeat exactly: the first determinism run
passed **4 / 0 / 0** (Passed / Failed / Skipped), including both traffic cases and
the two small RNG checks. Failed exploratory runs are kept separately and are
not mixed into the definitive seed sweep.

The harness advances `_simNow += EffectiveSimPeriod` in
`Server.Broadcast.cs:129`; `TickSim` has no cadence sleep. Collision detection
sorts by collision time and registration ordinals (`CollisionDetector.cs:94-120`),
so worker scheduling is not a source of collision event order. Map/civilization/
goal policy streams and discovery nonces remain separate; this fixture uses
fixed civilizations and file-map topology. The pre-existing deterministic mine
placement hash is unchanged. Placed probes previously had an independent `new
Random()` source; their attitude/dispersion now use the mission stream too, with
the original private generator retained as the no-mission fallback.

## Seed sweep and failure traces

The original controller was swept across seeds **0–29 in each case** (60 runs,
12,000 actual survivor trips). Ordinary launch traffic: **16 pass / 14 fail**
(**46.7%** zero-expiry failures), 130 expiries total, worst **50** at seed 23.
Rescue-capable launch traffic: **28 pass / 2 fail** (**6.7%**), 58 expiries total,
worst **36** at seed 23. Aggregate **44 / 16 / 0**, or **26.7%** failing runs.
Thus the aggregate is in the brief's 25–75% band; the rescue case is substantially
less failure-prone in this sample than the four-run dispatch observation. No
missing/skipped artwork cases are counted as passes. All original-controller
expiries had `RadiusAt=null`.

`D/R/O` means docked/rescued/oxygen; `H` is number ever observed in the home
sector. P95 is **successful trips only** (the same original test statistic);
catastrophic seed 23's low P95 is survivor bias, not fast returns for expired
pods. `E0/E1` is the original repeated enter-frame counter, not queue length.
The home-re-entry count is pods that departed home at least once, not the total
number of transit events.
[Complete legacy measurements](P958_assets/legacy-sweep.csv) and
[JSON](P958_assets/legacy-sweep.json) include completed bay counts and re-entries.

### Ordinary launch traffic, original controller

| Seed | D/R/O | H | P95 (s) | E0/E1 | Pods re-entering home |
| --- | --- | --- | --- | --- | --- |
| 0 | 200/0/0 | 200 | 288.70 | 1472/5042 | 1 |
| 1 | 199/0/1 | 200 | 286.25 | 1558/5143 | 1 |
| 2 | 200/0/0 | 200 | 289.55 | 1524/5415 | 0 |
| 3 | 197/0/3 | 200 | 287.10 | 1551/4996 | 1 |
| 4 | 200/0/0 | 200 | 284.00 | 1538/5305 | 1 |
| 5 | 198/0/2 | 200 | 285.60 | 1491/5132 | 0 |
| 6 | 199/0/1 | 200 | 288.90 | 1582/5088 | 0 |
| 7 | 199/0/1 | 200 | 287.70 | 1503/5230 | 1 |
| 8 | 198/0/2 | 200 | 291.50 | 1510/4994 | 2 |
| 9 | 196/0/4 | 200 | 287.35 | 1568/5148 | 2 |
| 10 | 197/0/3 | 200 | 292.40 | 1586/4948 | 0 |
| 11 | 197/0/3 | 200 | 286.60 | 1563/5043 | 1 |
| 12 | 190/0/10 | 200 | 293.75 | 1483/5113 | 3 |
| 13 | 200/0/0 | 200 | 287.90 | 1606/5385 | 0 |
| 14 | 200/0/0 | 200 | 287.80 | 1615/5105 | 0 |
| 15 | 200/0/0 | 200 | 287.25 | 1473/5058 | 0 |
| 16 | 200/0/0 | 200 | 286.90 | 1630/5069 | 0 |
| 17 | 200/0/0 | 200 | 288.50 | 1554/5147 | 2 |
| 18 | 200/0/0 | 200 | 287.90 | 1496/4955 | 2 |
| 19 | 199/0/1 | 200 | 288.60 | 1526/5269 | 0 |
| 20 | 200/0/0 | 200 | 287.95 | 1580/5092 | 1 |
| 21 | 200/0/0 | 200 | 287.80 | 1488/5141 | 0 |
| 22 | 200/0/0 | 200 | 284.90 | 1574/4903 | 0 |
| 23 | 150/0/50 | 200 | 229.90 | 1536/3167 | 2 |
| 24 | 200/0/0 | 200 | 287.40 | 1466/5168 | 2 |
| 25 | 200/0/0 | 200 | 288.05 | 1552/5120 | 0 |
| 26 | 200/0/0 | 200 | 289.65 | 1512/5144 | 0 |
| 27 | 200/0/0 | 200 | 289.20 | 1545/5099 | 0 |
| 28 | 197/0/3 | 200 | 294.00 | 1590/5019 | 1 |
| 29 | 154/0/46 | 200 | 223.40 | 1579/3423 | 2 |

### Rescue-capable launch traffic, original controller

| Seed | D/R/O | H | P95 (s) | E0/E1 | Pods re-entering home |
| --- | --- | --- | --- | --- | --- |
| 0 | 142/58/0 | 148 | 224.90 | 1546/2762 | 0 |
| 1 | 138/62/0 | 148 | 221.85 | 1543/2702 | 0 |
| 2 | 132/68/0 | 150 | 221.35 | 1584/2510 | 0 |
| 3 | 137/63/0 | 148 | 222.00 | 1581/2663 | 0 |
| 4 | 199/1/0 | 199 | 286.30 | 1541/5297 | 0 |
| 5 | 143/ 57/0 | 153 | 228.60 | 1534/2812 | 0 |
| 6 | 139/61/0 | 150 | 234.05 | 1491/2828 | 0 |
| 7 | 150/50/0 | 151 | 221.70 | 1555/3157 | 0 |
| 8 | 137/63/0 | 148 | 221.95 | 1513/2618 | 0 |
| 9 | 139/61/0 | 149 | 222.30 | 1566/2832 | 0 |
| 10 | 168/32/0 | 177 | 291.60 | 1581/3853 | 0 |
| 11 | 142/58/0 | 150 | 232.10 | 1550/2841 | 0 |
| 12 | 175/25/0 | 188 | 293.80 | 1528/4352 | 0 |
| 13 | 140/60/0 | 148 | 256.80 | 1589/2818 | 0 |
| 14 | 141/59/0 | 147 | 222.00 | 1541/2987 | 0 |
| 15 | 138/62/0 | 149 | 224.25 | 1550/2858 | 0 |
| 16 | 142/58/0 | 154 | 226.05 | 1543/2824 | 0 |
| 17 | 139/61/0 | 149 | 221.70 | 1529/2765 | 0 |
| 18 | 136/64/0 | 149 | 222.25 | 1544/2757 | 0 |
| 19 | 138/62/0 | 147 | 223.35 | 1534/2890 | 0 |
| 20 | 139/61/0 | 149 | 222.35 | 1510/2706 | 0 |
| 21 | 168/32/0 | 175 | 282.65 | 1518/3846 | 0 |
| 22 | 141/59/0 | 147 | 233.80 | 1526/2803 | 0 |
| 23 | 135/29/36 | 186 | 266.35 | 1571/2626 | 0 |
| 24 | 139/61/0 | 150 | 222.80 | 1495/2747 | 0 |
| 25 | 137/63/0 | 149 | 222.90 | 1616/2718 | 0 |
| 26 | 135/65/0 | 148 | 221.95 | 1563/2585 | 0 |
| 27 | 146/54/0 | 149 | 225.35 | 1531/3022 | 0 |
| 28 | 169/31/0 | 177 | 293.30 | 1610/3821 | 0 |
| 29 | 146/32/22 | 180 | 239.25 | 1613/3152 | 0 |

### Failure traces

Three complete failing-seed traces (1, 3, 8) capture every expired pod's final
60 s at 0.5 s resolution, every resolved ship/rock/station impulse, steering
branch and aleph transit. All complete per-pod results exactly matched their
sweep results despite tracing and different host load. There were **zero applied
coincident-centre random normals** in these selected traces. No station ram was
responsible for the observed home re-entry. [Compact pose/impulse excerpts](P958_assets/trace-excerpts.json)
retain five-second poses, every impact/transit and final pre-expiry samples;
raw half-second traces remain in `.scratch/p958/trace-seed{1,3,8}.trx`.

| Seed / pod | Home at (absolute sim s) | Last 60 s | Outcome at 420 s |
| --- | --- | --- | --- |
| 1 / 20177 | 343.05 | Dock branch throughout; no further collision. Distance to station 3879.03 → 433.18 at final sampled pose. | Oxygen, never inside station radius |
| 3 / 20185 | 354.65 | Dock branch throughout; 3980.82 → 475.66, no further collision. | Oxygen |
| 3 / 20187 | 365.70 | Route branch until home, then dock; launch ships 25099 and 25103 hit at 363.50 and 371.05. | Oxygen, still 1586.26 from station at final sample |
| 3 / 20199 | 358.95 | Dock branch throughout, 4174.03 → 703.92, no further collision. | Oxygen |
| 8 / 20189 | 355.00 | Launch ship 25099 hit at 360.85, rock 3357 hit at 402.40; 4009.06 → 795.77. | Oxygen |
| 8 / 20197 | 349.40 | Rock 3357 hit at 392.65; 3662.10 → 430.98. | Oxygen |

Seed 1 also traces non-expired pod **20089**, the clean home re-entry example:

- Home arrival **203.30**; outgoing ship **25034** hit it at **205.60** while the
  harness stayed in the dock branch (`DodgeShips`, mask 128, bay 1).
- Before: velocity `(39.146,-38.201,-3.238)`. Native collision response delta:
  `(-188.603,+187.136,+15.661)`, after `(-149.457,+148.934,+12.423)`.
  Separation displacement was only `(-0.458,+0.436,+0.039)`; this was a velocity
  reversal by launch traffic, not a random normal or a large separation jump.
- At **206.00** the pod was 70.47 from the arrival aleph centre and travelling
  back toward it. It crossed **1050 → 1059 at 206.45**, 3.15 s after arrival,
  legitimately rearmed by the accepted-update handshake. It returned at 225.00.
  There was **no `NextWarp` call in the home branch**.

Seed 1's expired pod 20177 also hit launch ship **25091** just after arriving
home (**343.25**). Its X velocity changed from **−54.10 to +116.99**, pushing it
away from the station. By 348 s it was **4422.60** from the station; the remaining
72 s was insufficient. This explains why a pod can reach home but still expire
without any subsequent transit.

**Bimodal failure reproduced, seed 23:** 50 ordinary-case expiries are every odd
side-1 pod, i.e. the entire 1062 return mouth. Additional diagnostic traces sample
20101/20151/20199. They repeatedly ram generated rock **3610 in sector 1062**
(**22 / 29 / 13** resolved impacts), because original pursuit kept aiming through
it without native dodge/diversion. Pod 20101 first hit at **156.25**, last hit
that rock at **204.75**, and reached home only at **262.40** (ejected at 0).
Pod 20151 was delayed from **235.75 to 297.90**, reached home at **357.05**
(ejected at 80), and expired at 380 still **2969.78** from the station. Pod 20199
reached home at **366.70**, expired at 420 **1339.68** from the station. These
pods were **not waiting for a bay slot**. Home-sector telemetry alone had made
that interpretation tempting. Random file-map top-up controls whether a rock
obstructs this long leg, hence the old bimodal all-or-part pattern.

**Worst-seed counterfactual:** seed **23** now passes both cases with **zero
expiries**: ordinary **200 docked**, rescue traffic **159 docked + 41 rescued**.
This is the worst original failure in both cases, so it is the permanent pinned
regression. **No assertion is loosened:** zero expiries, no oxygen expiry with
`RadiusAt`, every pod's 300 s allowance, ordinary-case home/radius/bay arrival,
successful completion before 300 s, sufficient launch traffic, all 200 outcomes,
and zero sim-pass failures are all retained. New tests also exercise the sampled
home approach and the obstructing seed-23 rock directly.

The remaining corrected-controller sweep is evidence about broader native limits,
not an attempt to select an easy passing seed. Some other seeds still expire:
source comparisons show native provides avoidance and collision rules, not an
arbitrary-world completion guarantee. A future multi-seed soak should distinguish
native timer/garage invariants from fixture-specific loss budgets; a sampled
upper bound is not a universal native rule.
It also retains the original `RadiusAt` guard, which is stronger than “currently
in the station sphere”: the telemetry remembers **any historical visit**. This
is a pinned-fixture regression check, not a native promise that every location
inside a model's bounding sphere is a landing bay. Native `InGarage` tests bay
planes and oriented hull extremes (`Igc/stationIGC.cpp:384-474`).


**Expired home-re-entry case, seed 23 / pod 20137:** the additional exact trace
reproduces the brief's **1051 → 1062 → 1051** signature. Ejected at 40.00, it
reached home at **287.75**. Outbound launch ship **25071** hit it at **291.45**
while docking; velocity changed from `(-50.118,+12.100,-4.190)` to
`(+201.593,-52.341,+16.940)`, delta `(+251.711,-64.441,+21.130)`.
The applied separation was only `(+0.447,-0.104,+0.038)`, with unequal centres.
It left home at **292.75**, returned at **310.35**, and expired at **340.00**
without entering the station radius. Home-sector steering was always the dock
branch, never `NextWarp`. With the complete native controller, this **same pod,
seed and world docks in 282.10 s** with route **1052 → 1062 → 1051**, no re-entry.
Its complete original telemetry also matches the sweep exactly; excerpts are in
the shared trace JSON and raw evidence is
`.scratch/p958/qualification/legacy-seed23-pod20137.trx`.

### Corrected native-controller sweep (same seeds 0–29)

The diagnostic sweep retains the original strict zero-expiry assertions, so its
pass/fail column means “zero expiries”, not “all native rules violated/preserved”.
Ordinary traffic: **9 pass / 21 fail**, **92 expiries total**, maximum **7**
(vs original 130 total, maximum 50). Rescue traffic: **30 pass / 0 fail**, zero
expiries (vs original 58 total, maximum 36). This is a rules correction, not a
throughput optimization: native crowd avoidance creates more small ordinary-case
delays even as it removes the catastrophic rock-ramming loss. Production NPCs
already use this controller; no new NPC steering behavior is introduced. The
maximum-loss regression seed 23 passes both cases.
[Complete native-controller CSV](P958_assets/native-sweep.csv) and
[JSON](P958_assets/native-sweep.json) preserve all observations. Zero home-sector
re-entries were observed across the corrected sweep.

#### Ordinary traffic, corrected controller

| Seed | D/R/O | H | P95 (s) | E0/E1 | Historical-radius expiries |
| --- | --- | --- | --- | --- | --- |
| 0 | 193/0/7 | 200 | 291.75 | 987/2862 | 0 |
| 1 | 198/0/2 | 200 | 293.65 | 1154/3385 | 0 |
| 2 | 196/0/4 | 200 | 290.65 | 1091/3086 | 1 |
| 3 | 194/0/6 | 200 | 291.95 | 979/3289 | 0 |
| 4 | 196/0/4 | 200 | 292.60 | 944/3422 | 0 |
| 5 | 196/0/4 | 200 | 290.95 | 870/2945 | 2 |
| 6 | 200/0/0 | 200 | 287.80 | 1118/2969 | 0 |
| 7 | 196/0/4 | 200 | 291.35 | 1210/2929 | 0 |
| 8 | 198/0/2 | 200 | 292.40 | 794/2886 | 0 |
| 9 | 193/0/7 | 200 | 293.05 | 1200/2788 | 1 |
| 10 | 198/0/2 | 200 | 290.35 | 1082/2949 | 0 |
| 11 | 196/0/4 | 200 | 294.90 | 899/2969 | 1 |
| 12 | 200/0/0 | 200 | 288.30 | 1077/3157 | 0 |
| 13 | 196/0/4 | 200 | 290.75 | 1157/3234 | 0 |
| 14 | 195/0/5 | 200 | 293.70 | 812/3133 | 0 |
| 15 | 195/0/5 | 200 | 293.80 | 688/3007 | 2 |
| 16 | 200/0/0 | 200 | 293.15 | 926/3167 | 0 |
| 17 | 195/0/5 | 200 | 291.75 | 995/2553 | 0 |
| 18 | 195/0/5 | 200 | 291.25 | 1090/3116 | 0 |
| 19 | 196/0/4 | 200 | 293.35 | 1032/2711 | 1 |
| 20 | 200/0/0 | 200 | 287.50 | 1116/2794 | 0 |
| 21 | 199/0/1 | 200 | 290.75 | 877/2783 | 1 |
| 22 | 193/0/7 | 200 | 291.75 | 924/3269 | 1 |
| 23 | 200/0/0 | 200 | 290.00 | 793/3004 | 0 |
| 24 | 200/0/0 | 200 | 289.25 | 902/2839 | 0 |
| 25 | 196/0/4 | 200 | 291.40 | 1055/3016 | 0 |
| 26 | 200/0/0 | 200 | 289.40 | 1254/2996 | 0 |
| 27 | 194/0/6 | 200 | 291.90 | 976/2850 | 0 |
| 28 | 200/0/0 | 200 | 287.95 | 1006/2904 | 0 |
| 29 | 200/0/0 | 200 | 293.35 | 978/2966 | 0 |

#### Rescue-capable traffic, corrected controller

| Seed | D/R/O | H | P95 (s) | E0/E1 | Historical-radius expiries |
| --- | --- | --- | --- | --- | --- |
| 0 | 143/ 57/0 | 149 | 226.05 | 966/1676 | 0 |
| 1 | 144/56/0 | 148 | 227.50 | 970/1941 | 0 |
| 2 | 140/60/0 | 150 | 227.90 | 1161/1729 | 0 |
| 3 | 136/64/0 | 147 | 227.60 | 1184/1442 | 0 |
| 4 | 178/22/0 | 182 | 278.70 | 958/2588 | 0 |
| 5 | 141/59/0 | 149 | 223.20 | 918/1690 | 0 |
| 6 | 157/43/0 | 172 | 276.70 | 1116/2056 | 0 |
| 7 | 148/52/0 | 151 | 226.10 | 919/2113 | 0 |
| 8 | 149/51/0 | 158 | 274.95 | 849/1653 | 0 |
| 9 | 146/54/0 | 157 | 276.20 | 1112/1941 | 0 |
| 10 | 169/31/0 | 183 | 282.95 | 952/2336 | 0 |
| 11 | 139/61/0 | 148 | 228.05 | 878/1781 | 0 |
| 12 | 166/34/0 | 179 | 277.15 | 935/2338 | 0 |
| 13 | 137/63/0 | 149 | 225.70 | 898/1625 | 0 |
| 14 | 139/61/0 | 150 | 228.70 | 1206/1756 | 0 |
| 15 | 140/60/0 | 150 | 226.80 | 942/1525 | 0 |
| 16 | 146/54/0 | 152 | 225.15 | 1030/1813 | 0 |
| 17 | 146/54/0 | 150 | 227.65 | 887/1808 | 0 |
| 18 | 148/52/0 | 151 | 226.20 | 981/1813 | 0 |
| 19 | 137/63/0 | 150 | 228.30 | 926/1711 | 0 |
| 20 | 163/37/0 | 170 | 277.00 | 1199/2257 | 0 |
| 21 | 159/41/0 | 167 | 276.95 | 1118/2247 | 0 |
| 22 | 139/61/0 | 149 | 228.90 | 902/1840 | 0 |
| 23 | 159/41/0 | 163 | 277.75 | 1017/2202 | 0 |
| 24 | 156/44/0 | 170 | 276.80 | 853/2214 | 0 |
| 25 | 145/55/0 | 151 | 227.20 | 988/1660 | 0 |
| 26 | 172/28/0 | 183 | 277.35 | 1054/2071 | 0 |
| 27 | 146/54/0 | 148 | 227.35 | 826/2031 | 0 |
| 28 | 161/39/0 | 179 | 278.25 | 967/2061 | 0 |
| 29 | 160/40/0 | 170 | 277.15 | 925/2278 | 0 |

The measured prospective loss budgets for a broader seed soak are at most **7/200
ordinary pods** and **0/200 rescue-traffic pods** in this 30-seed sample. They are
sample-specific, not native constants. Some ordinary seeds record a historical
station-sphere visit before later expiry, so the existing `RadiusAt` assertion
also cannot be represented as a universal native promise. It is still preserved
for the permanent seed-23 fixture. No broadening of that assertion is made here.

A complete additional native-controller seed-0 trace covers all seven expired
pods' final 60 s. None had an applied collision during that interval; they were
in the docking seek/coast/pivot/enter state machine with ordinary crowd avoidance.
Their last sampled station distances were **437.39, 413.72, 406.01, 433.66, 408.15,
420.76, 500.85**, versus contact radius **402.5**. These are finite-budget approach
delays, not occupancy-slot waits or hidden oxygen extensions. The native trace
also exactly matches its seed-sweep per-pod telemetry, including every route,
terminal pose and time. Excerpts are in the shared trace JSON; raw evidence is
`.scratch/p958/trace-native-seed0.trx`.

## Native comparison and correction

The failure is physical traffic/obstacle interference plus a finite travel
budget, not a route planner requesting a home-sector warp. There is also a
**harness bug**: P699b extracted `DockApproach` masks and steering but omitted
native GotoPlan's first-priority `Dodge` and stationary-obstacle tangent diversion
(`Igc/common.cpp:2136-2226`). Its transit branch used MCP's pursuit
controller, and only the docking branch rewrote RCS state. That mixed controller
cannot certify native autopilot guarantees.

The corrected harness uses the already-ported `NativeGotoPlan.Execute` for both
warp and station targets and the production `Ship.FillNativeObstacles` packing,
including native `Ignore` rules (friendly rescue hulls must remain eligible to
pick pods up). Controls and movement buttons are replaced together every frame.
`P958_LEGACY_STEERING=1` is a test-only diagnostic replay of the original controls;
it is not a server deployment setting. The routing policy, ejection helper,
collision bodies, real launch frames, update handshake and oxygen allowance stay
in place.

Re-grepped native rules:

- `Igc/common.cpp:1999-2020` runs Dodge **before** seek/coast/pivot unless
  `NoDodge` is set. `:1693-1790` predicts contact and thrusts away; `:1639-1690`
  filters rescue-compatible traffic. `:2309` writes directional RCS on turns.
- `Igc/shipIGC.cpp:523-533,603-615` separates actual collision bodies with a
  0.5-unit clearance and randomizes the normal only for exactly equal centres.
  The traced impacts have **unequal** centres. Their velocity response is the
  native mass-weighted inelastic/elastic blend (`:1084-1124`), elasticity 0.75;
  a fast launch hull can impart a large velocity to a light pod. Friendly pods
  avoid impact damage, not impact motion (`:1166-1168`).
- `Igc/shipIGC.cpp:629-636` calls `HitWarpEvent` on contact without checking
  intended destination or whether this is the pod's home. Player re-entry is
  permitted after accepted-update rearming (`FedSrv/fsship.cpp:74-82,1185-1191`).
  No non-native re-entry immunity is enabled by this change.
- `Igc/missionigc.cpp:2724,2775` sets 300 s endurance; `Igc/shipIGC.cpp:1407-1408`
  arms it and `:361-364` expires it. There is no home-sector extension, throughput
  promise, or collision-delay compensation.

**Measured counterfactual, same seed 1:** originally pod 20089 went
`1052 → 1059 → 1050 → 1059 → 1050` and docked in **177.40 s**. Adding only the
missing home-approach Dodge removed the home re-entry and docked it in
**164.65 s**; the complete native controller docked it in **168.70 s**, also
without re-entry. Original expired pod 20177 completed in **288.85 s** under the
complete controller. Nevertheless that controller's seed-1 run still had two
other oxygen expiries outside the station radius. Thus fixing the harness bug
cannot establish an all-seed zero-expiry guarantee.

The strict zero-expiry assertion is retained for pinned seed 23, which reproduced
the largest old failures and now completes both cases. The broader sweep exposes
native crowd/avoidance delays, including historical sphere visits without garage
entry; those observations are reported rather than hidden by a blanket “all
native pods return within 300 s” claim.

**Bay skew:** `bays=1:... ,0:...` counts repeated `Enter` *frames*, not berth
reservations or dockings. Seed 0's original controller recorded **5042:1472**
frames but completed **side 0: 50 bay-0 + 50 bay-1; side 1: 100 bay-1** dockings.
The combined completed-docking ratio is therefore 150:50. The two civilizations'
station geometries and approach errors account for it. Native selects the least
rest-position error (`Igc/common.cpp:1172-1244`) and only changes winner on strict
`<` (`:1246`); it does not balance bay usage. The legacy port's ID-rotated exact
error ties are a non-native extension, but did not cause the measured skew. The
corrected controller uses native first-tie order. `InGarage` has no berth-occupancy
reservation; the 100-pods/one-bay/one-tick test remains intact and passed in the
initial native-controller cohort.

## Reproduction

With the existing artwork environment exported from `local.env` and the test
project built, run:

```bash
DOTNET_gcServer=1 python3 tools/p958-pod-load.py --legacy-steering \
  --seeds 0:30 --parallel 2 --output .scratch/p958/replay-legacy
DOTNET_gcServer=1 python3 tools/p958-pod-load.py \
  --seeds 0:30 --parallel 2 --output .scratch/p958/replay-native
```

The output directory must be new. The helper keeps complete TRX/logs, a CSV/JSON
summary, actual test status, separate `zeroExpiryOutcome`, and host load. Test
status still uses the strict unchanged assertions; the seed-23 default passes
both native-controller cases. Add `--trace-failures 3` to rerun expired cases with
all relevant pod IDs traced, or set test-only `P958_TRACE=20137`, `P958_SEED=23`,
`P958_LEGACY_STEERING=1` on the exact ordinary P699b test for the expired
1051→1062→1051 example. No production server configuration or deployment is needed.

## Validation

Initial and final solution builds: **0 warnings / 0 errors**. All **120 tool projects** built
with zero warnings/errors. Linux-x64 self-contained server **Native AOT publish
completed**, with no IL2026/IL3050 diagnostics. Restores for these gates were
restricted to the existing local NuGet package cache. The final solution build
includes the finalized fixture and regression tests.

Affected RNG/physics/navigation cohort: **220 / 0 / 0**, repeated serially
**three times**, **660 / 0 / 0 total**. The cohort includes separation/cache
identity, convex-hull/ship/asteroid/station collisions, station launches, chaff,
weapons and side-GA firing, armed probes, treasure placement/expiry, drone
complaints and economy drone AI, plus all four new small P958 regression cases.
All were run with artwork and server GC. The complete class filter and TRX are
in `.scratch/p958/affected-filter.txt` and `.scratch/p958/qualification/`.

Pinned seed-23 P699b (both traffic cases plus one-bay proof): **3 / 0 / 0**,
repeated **three times**, **9 / 0 / 0 total**. Every pod's complete telemetry is
identical across all three invocations. Maximum successful ordinary trip:
**292.55 s**; rescue-traffic trip: **279.65 s**, with the 300 s assertions intact.

Same/different-seed determinism: **2 / 0 / 0**, repeated **three times**,
**6 / 0 / 0 total**. Each invocation runs seed 23 twice and seed 24 once for each
traffic case and compares every complete per-pod telemetry field (including terminal poses) and bay counts.
All six same-seed comparisons match exactly; different seeds differ. This is
18 complete 200-pod scenarios, not a mock of the random generator.

Showcase never-parks, exact normal-home method, five alternating original/current
pairs: **base 5 / 0 / 0; current 5 / 0 / 0**. All ten runs observed real combat and
reached at least 24/30 bots. Neither the parking limit nor any bot/flight policy
was changed. Raw receipts are retained; unseeded observation durations are not a
performance A/B.

| Pair | Base duration (s) | Current duration (s) | Base longest stopped (s) | Current longest stopped (s) |
| --- | --- | --- | --- | --- |
| 1 | 199.175 | 202.152 | 2.007 | 2.015 |
| 2 | 197.367 | 405.637 | 2.105 | 2.005 |
| 3 | 178.173 | 272.065 | 1.503 | 0.607 |
| 4 | 197.190 | 202.697 | 3.007 | 1.906 |
| 5 | 199.204 | 368.713 | 1.403 | 1.313 |

Bounded-parallel pod load: **two concurrent test processes**, each running both
pinned traffic cases, **4 / 0 / 0 total**. Host one-minute load at completion was
**9.23–9.60**. Every complete pod telemetry record exactly matches the isolated
reference in both processes; no remaining load sensitivity was observed. No
synthetic CPU load was generated. Hashes and count receipts are in
[validation JSON](P958_assets/validation.json).

One complete Fast-tier run: **13,485 passed / 6 failed / 3 skipped**,
**13,494 cases** in **594.13 s**. All **9 infrastructure checks pass**. The runner
reports **20 timing violations across 15 method/theory families** at the calibrated
10 s limit. This Fast gate is **FAIL** and is not relabeled green by isolation.
The three skips are the existing optional private `MeshyPackTests`; artwork is
configured and **all P958 / affected / pinned / determinism / parallel / showcase
checks have zero skips**. Complete [Fast summary](P958_assets/fast-summary.json).

The six assertion failures are five `PlayerBundlePathsTests` cases and one
`MissileLockLiveTests` case. The former reproduce the existing parent-`local.env`
fixture obstruction under the required in-worktree `TMPDIR` (same signature as
the P916/P952 ledger). The latter again reaches full lock 1.000 without the
required partial-tip receipt; its isolated repeats are **pass/fail/pass**, the
same already-open intermittent signature. These subsystems are unchanged.

Every failed method/theory family and every timing-offender family was repeated
**three times alone**: **19 families / 57 invocations**, yielding **65 passed /
16 failed / 0 skipped** across the included theory cases. Twelve timing families pass all three repetitions below 10 s. Warp-exit
sampling passes assertions but remains over budget in two isolated runs
(**10.288 / 10.052 / 9.789 s**); treasure-offset corpus decoding remains over
budget in all three (**11.997 / 10.695 / 10.643 s**). These unchanged,
non-task timing failures remain open. Persistent bundle-path failures and
the intermittent missile receipt are recorded in `docs/TEST_FLAKES.md`; no unrelated
source, assertion, timing budget or tier was changed. Detailed
[isolation JSON](P958_assets/non-task-isolation.json) and
[compact summary](P958_assets/non-task-isolation-summary.json).

| Non-task method / theory family | Three isolated invocations | P/F/S | Max case (s) | Disposition |
| --- | --- | --- | --- | --- |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | pass / pass / pass | 3/0/0 | 5.587 | Assertions and timing pass in all isolated repeats |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | pass / fail / pass | 2/1/0 | 38.779 | Known missing partial-tip receipt; remains intermittent |
| `P704TechLoadoutTests.Dock_local_masks_are_never_combined_and_finale_uses_the_same_bomber_selection` | pass / pass / pass | 3/0/0 | 4.228 | Assertions and timing pass in all isolated repeats |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | pass / pass / pass | 9/0/0 | 7.897 | Assertions and timing pass in all isolated repeats |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | pass / pass / pass | 3/0/0 | 3.663 | Assertions and timing pass in all isolated repeats |
| `P832bProsperityTests.No_goal_decision_trace_matches_dispatch_base` | pass / pass / pass | 3/0/0 | 2.055 | Assertions and timing pass in all isolated repeats |
| `P832bProsperityTests.Observed_He3_grows_miners_first_and_losses_trigger_replacement` | pass / pass / pass | 3/0/0 | 4.298 | Assertions and timing pass in all isolated repeats |
| `P832bProsperityTests.Other_goals_keep_the_same_commander_trace_as_absent_goals` | pass / pass / pass | 3/0/0 | 8.437 | Assertions and timing pass in all isolated repeats |
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | pass / pass / pass | 3/0/0 | 1.800 | Assertions and timing pass in all isolated repeats |
| `P942ResearchCommanderTests.Generated_commander_funds_buyable_research_before_optional_production` | pass / pass / pass | 9/0/0 | 3.226 | Assertions and timing pass in all isolated repeats |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | pass / pass / pass | 6/0/0 | 4.633 | Assertions and timing pass in all isolated repeats |
| `SC3CloakPolicyTests.Research_loadouts_offer_real_legal_cloaks_without_unlock_bypass` | pass / pass / pass | 3/0/0 | 1.497 | Assertions and timing pass in all isolated repeats |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass / pass / pass | 3/0/0 | 10.288 | Assertions pass 3/3; 2 isolated case executions still exceed 10 s |
| `P817NativeMinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell` | pass / pass / pass | 3/0/0 | 7.088 | Assertions and timing pass in all isolated repeats |
| `PayloadDecodeTests.TreasureSet_offsets_cross_reference_same_core` | pass / pass / pass | 3/0/0 | 11.997 | Assertions pass 3/3; 3 isolated case executions still exceed 10 s |
| `PlayerBundlePathsTests.LocalConfigWinsOverBundleWithBlankEnvironment` | fail / fail / fail | 0/3/0 | 0.015 | Known ancestor-local.env fixture obstruction |
| `PlayerBundlePathsTests.MissingDirectoryHasNoDefault` | fail / fail / fail | 3/6/0 | 0.005 | Known ancestor-local.env fixture obstruction |
| `PlayerBundlePathsTests.NativeSourceNeverGuessesAnInstallSubdirectory` | fail / fail / fail | 0/3/0 | 0.005 | Known ancestor-local.env fixture obstruction |
| `PlayerBundlePathsTests.UsesExecutableDirectoryOutsideRepository` | fail / fail / fail | 3/3/0 | 0.005 | Known ancestor-local.env fixture obstruction |

Final ancillary checks: glossary links, default parity matrices plus the P958
supplemental CSV, LF-only CSVs, `git diff --check`, and a scan of new/changed
source for personal home paths, private IPs and email literals all pass.

Raw build/test/trace evidence is retained locally in
[the task scratch directory](../../.scratch/p958/), including `qualification/`,
`fast-tier/`, and `fast-isolation/`. Committed-sized review artifacts total less
than 2 MB in `P958_assets/`. Unused generated assembly copies are removed; there
are no new media assets, commits, pushes or deployment changes.

## Native intent

The omitted Dodge/diversion is a **harness bug**. The large seed-23 loss was
repeated obstacle ramming during transit, not a native bay queue promise.
Physical pile-up, impacts and the finite 300 s budget are native **design / era constraints**, not evidence of a load-driven
simulation clock or an oxygen bug. Bay error minimization and unrestricted bay
throughput are **design**; combined enter-frame counters exaggerate the apparent
imbalance. The legacy exact-tie rotation is a port extension and the corrected
native controller does not use it. Formula-level/source comparisons are evidence
for these rules; this task does not claim a Windows native-binary 200-pod replay.

## Modern approach

A seedable mission simulation RNG makes stochastic interactions reproducible while
leaving production randomness unseeded.
