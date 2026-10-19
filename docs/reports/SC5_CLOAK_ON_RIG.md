# SC-5 — legal cloak purchases for the rig civilizations

The break is hull selection, compounded by late stealth hull research. Both rig civilizations have native cloak parts. Their starting combat hulls cannot mount them, and the previous scout/bomber rankings never chose a cloak-bearing loadout even after either authored plan completed. SC-3's use policy therefore had no cloak to activate. This change brings the native stealth branch forward in both plans and prefers an actually mounted, legal cloak for scout and bomber purchases.

Base: `a00e842fe424787e4bd0a2b7319fc386b41324b9`, branch `codex/sc5`. Changes remain in the working tree. No commits, pushes, rig/221 tests or 200v200 runs were made.

## Core data and native rules

The catalogue is decoded from `/home/domingo/Development/Artwork/PCore001.igc`, version **990051329**, SHA-256 `f73aa7bbce7414140f6691366f8829ea58829727660a8a86c114b29a83a55de8`. [Decoded records](SC5_assets/native-catalogue.json) preserve IDs, prerequisite/effect bits, hull equipment masks and cloak properties; [plan evidence](SC5_assets/plans.json) preserves the before/after chains. These are data records, not rules inferred from ship names. Native record definitions are `~/Development/Allegiance/src/Igc/igc.h:1753-1764` (buyable prerequisites/effects), `:1788-1809` (hulls), `:1822-1830,1855-1863` (parts/cloaks), and `:2691-2696` (developments).

A purchase needs the required bits to be a subset of **side techs plus that particular station's local techs** (`~/Development/Allegiance/src/Igc/stationIGC.h:383-391`). Side techs combine development and building effects; development buckets require global prerequisites, while station/drone purchases may use an individual station's local mask (`~/Development/Allegiance/src/Igc/sideigc.h:233-280`). Native successor/obsolescence checks remain authoritative (`~/Development/Allegiance/src/Igc/stationIGC.h:393-433`). Nonweapon equipment uses mount 0, and the hull mask must contain the part mask (`~/Development/Allegiance/src/Igc/hullTypeIGC.h:163-176,199-213`). Native ammunition/dispenser capacities are in `:178-196`; the human purchase catalogue checks group, buyability and obsolescence (`~/Development/Allegiance/src/WinTrek/load.cpp:1603-1606`). The server purchases the requested loadout and returns the actual grant/launch acknowledgement (`~/Development/Allegiance/src/FedSrv/FedSrv.CPP:6189-6190,6247-6259`).

| Cloak part | Mask | Availability for civs 25 / 3 |
| --- | ---: | --- |
| 38 Sig Cloak 1 | 1 | Starting bit 45 on both sides; no cloak development needed |
| 39 Sig Cloak 2 | 1 | Tactical grants 224/243; development 118 grants 46 |
| 34 Sig Cloak 3 | 1 | Advanced Tactical grants 7; development 117 requires 7/46 and grants 220; purchase also requires 224 |
| 41 Hvy Cloak 1 | 2 | Development 121 requires 6/17/45 and grants 47 |
| 35 Hvy Cloak 2 | 2 | Development 120 requires 6/18/47 and grants 221; bit 18 comes from upgraded Garrison |
| 135 Lt Cloak | 1 | Civ 3 only: [R]esearch station 43 grants 262; Tactical grants 243; development 269 grants 158 |

Civ 3 also has part 139 Solar Inverter in the Cloak equipment class, unlocked by development 270 plus Research/Tactical. Its **MaxCloaking is −5**; it is not a positive-strength stealth device and is never used as cloak proof. These part/development prerequisites are in the linked decoded records, interpreted using the native purchase rules above. Civ 25 cannot reach the civ-3-specific 135/139 prerequisites through its own economy.

The complete reachable compatible hull records are below. Mask 3 accepts both light (1) and heavy (2) cloak parts. Compatibility alone does not mean a hull is a legal, nonobsolete combat purchase at every research stage.

| Civ | Hull records | Cloak mask |
| --- | --- | ---: |
| 25 Iron Coalition | 411 Stealth Fighter; 486 Adv Stl Fighter; 420 Stealth Bomber | 3 |
| 25 | 421 Frigate; 487 Assault Ship | 2 |
| 25 | 150 Stealth Bomber [NG] | 1 |
| 3 Ga'Taraan | 131 Stealth Fighter; 132 Adv Stl Fighter; 133 Stealth Bomber | 3 |
| 3 | 140 Assault Ship; 142 Cruiser; 144 Devastator; 146 Frigate | 2 |
| 3 | 155 Stealth Bomber [NG] | 1 |

The remaining hull unlocks also follow the recorded masks: civ 25 gets fighter 411 from normal Tactical (147 is initially known), and 421/487 from Shipyard 103 plus development 225 Upgrade Medium Class (142/143). Civ 3 gets fighter 131 from development 26; 140/146 require Shipyard 49, development 241 Upgrade Light Class (143), then 228 Upgrade Medium Class (142). Its 142/144 additionally need development 280 Upgrade Heavy Class (141) and Dry Shipyard 50 (185). The capital purchases require Shipyard local bit 240. The [NG] variants add development 195 SRM Nerve Gas (204) to the stealth-bomber prerequisites; the combat policy excludes their gunless loadouts. The same native global/local prerequisite and mount rules cited above interpret every one of these records.

Starting combat hulls 410/415 (civ 25) and 114/121 (civ 3) accept none of these positive-strength cloaks. The bot combat selector requires a damaging fixed gun, so the gunless [NG] variants are not a workaround. SC-3 is pilot policy for the **Scout/Bomber classifications**, not for capital ships or every hull named “Stealth Fighter.” The eligible advanced stealth fighters classify as Scouts from their scanner/speed characteristics. These classification and ranking choices are harness policy, not new native rules.

## Exact research and purchase chain

Civ 25 starts with the civ/weapon prerequisites, bit 147 and advanced-stealth prerequisite 182. Build **113 Tactical** (effects include 243), fund **development 10 Upgrade Tactical Lab** (grants 178/152), then build **109 Tactical (Adv)** (grants 7). This makes **486 Adv Stl Fighter** buyable as a scout. Fund **37 Bomber** (grants 36), then **33 Stealth Bomber** (grants 56) to buy **420 Stealth Bomber**. Part **38 Sig Cloak 1** fits both at mount 0.

Civ 3 builds **55 Tactical** (grants 243) and **42 [P]alisade** (grants 263), funds **271 Upgrade Tactical Lab** (requires 243/263, grants 178), and builds **56 Tactical (Adv)** (grants 7). **Development 26 Stealth Fighter** grants 147; **24 Adv Stl Fighter** grants 182, unlocking scout **132**. Developments **37** then **33** unlock bomber **133**. Both mount the already-buyable part **38**. Exact masks, costs and native civ-scaled development durations are retained in the decoded records; the scaling rule is `~/Development/Allegiance/src/Igc/bucketigc.cpp:70-74`.

Both authored plans already contained these branches. Before this change their milestones were:

| Civ / plan | Cloak-bearing scout | Cloak-bearing bomber |
| --- | --- | --- |
| 25 / plan_a (auto default) | step 144, 2,285 s | step 157, 2,480 s |
| 25 / plan_b | step 18, 420 s | step 21, 465 s |
| 3 / plan_a (auto default) | step 131, 1,925 s | step 130, 1,910 s |
| 3 / plan_b | step 29, 435 s | step 32, 480 s |

Times are **serial bucket work divided by the rig's research scale 8**, excluding constructor travel, station transformation, funding waits, commander observation delays and losses. The default civ-25 plan spends 41m20s on buckets before the stealth bomber, leaving under four minutes of a 45-minute match for all other delays. The plans can reach the branch in prerequisite terms; their completion times do not guarantee reaching it during the rig match. The supplied rig excerpt has no per-step timestamps, so it cannot establish which last development actually completed there.

The decisive break is independent of that uncertainty: replaying **every step**, making **26,684** scout/bomber queries at every owned dock against the opposing initial Garrison and successor target types, produced **zero cloak-bearing selections on both default plan_a paths** and on civ 3 plan_b. Civ 25 plan_b does briefly select stealth bomber 420 during steps 21–90 (465–1,500 bucket seconds at ×8), then development 175 Fighter/Bomber at step 91 makes noncloaking 158 win. Neither civ ever selects a cloaked scout in either original plan. After full research all four paths choose noncloaking Hvy Scouts (87/120), Fighter/Bomber 158 on civ 25 and Hvy Bomber 125 on civ 3. Scout scanner×speed and bomber station-damage ranking beat stealth hulls. Thus plan_b offers a transient exception for civ 25, not reliable two-sided cloak coverage. Selecting the strongest compatible support part after picking an incompatible hull cannot fix that.

The revised branch placement puts civ 25's cloak scout at step **18** and cloak bomber at step **20**, and civ 3's cloak scout at step **19** and cloak bomber at step **23** (plan_a). Bucket work at ×8 to the cloak bomber is **316 s** (civ 25) and **316 s** (civ 3) on plan_a, plus the real construction/travel/funding delays — roughly 5-7 minutes of sim, far inside a 45-minute match. Both plan variants retain exactly their original 157/181 completion identities; all expected-unlock lists were recomputed and validated. The branch is placed **just after the dev-dense opening (~15 steps)** and not at the very front: an initial attempt that put the two tactical stations at the top of the plan collapsed the scale-eight opening research metric in `P733ResearchTests.Headless_10v10_scale_eight_completes_at_least_five_times_native_steps` (station construction is **not** scaled by the research multiplier, so front-loading stations drops the measured ×8 speedup from ~8× toward ~2.8×). Keeping the stock development-heavy opening first and the cloak branch after it preserves that test (it passes 3/3) while still reaching a mounted cloak well inside the match.

`TechLoadoutSelector` now ranks an actually purchased positive-strength cloak first for scouts and real bomber duty, then uses its existing hull/weapon ranking. Dedicated mine-layer purchases (`preferMines`) retain their dispenser-capable scout ranking; a stealth hull without a mine socket cannot displace that duty. It budgets cloak before optional support equipment on those loadouts. The available-strike fallback across other combat roles retains station-damage ranking. The existing research, local dock, mount, amount, successor, human-hull and wallet checks remain in place; natural purchases/refits still obtain server acknowledgements. No simulator, equipment grant, counter, core data or SC-3 control policy changed. The existing first-branch live test now waits for all its original asserted evidence, since a station-first plan may complete its branch before its first development; no assertion or deadline was relaxed.

## Local proof and validation

Qualification uses actual civs **25 vs 3**, research **×8**, income multiplier **5000**, a generated four-sector SingleRing, **15 combat bots per side**, neutral rendezvous sector 1 and ordinary research commanders/NPCs. Finale and automatic-next-match are disabled so the proof stops at qualification. The ticker calls ordinary `TickSim` every 5 ms to accelerate wall time. Test unlocks and ignored tech gates remain disabled. There is no direct equipment, control, money, damage or counter injection.

SC-1 increments for a real inactive-to-active emitter transition. Native activation requires mount 0, mounted readiness and the cloak request (`~/Development/Allegiance/src/Igc/cloakigc.cpp:62,68-83`); energy throttling and fade remain native (`:89-121`). The proof observes those server emitters and existing skill counters.

Three final isolated rig-civ proof invocations passed on the repositioned plans (civs 25 vs 3, research ×8). [Structured snapshots and actual emitter witnesses](SC5_assets/live-summary.json) and [SC-1 skill lines](SC5_assets/skills.txt) retain the observations.

| Run | Test duration | Sim time at qualification | Civ 25 cloak / minimum | Civ 3 cloak / minimum |
| --- | --- | ---: | ---: | ---: |
| 1 | 00:02:15 | 1058.0 s | 17 / 5 | 5 / 5 |
| 2 | 00:02:07 | (qualified) | 14 / 5 | 5 / 5 |
| 3 | 00:02:11 | (qualified) | 22 / 5 | 5 / 5 |

Whole-solution build: **0 warnings / 0 errors**. AOT publish was not triggered: no `src/Allegiance.Core` or `src/Allegiance.Server` source changed. Four new plan cases and sixteen affected catalogue/selector/policy cases passed **3/3 separate exact-case invocations each** (60 passes). They cover initial locked hulls, early and full research, real mount legality, deterministic selection, wallet bounds, preserved dedicated mine purchases, damaging bomber strikes, station-local prerequisites, successor handling, equipment readiness, energy, flight eligibility and decloaking for actions. The live proof is tagged `Tier=Live`.

Parking: baseline **5/5 passed**, longest stopped-with-target intervals **1.919, 2.006, 2.007, 1.905, 2.005 s**; final code **5/5 passed**, **2.006, 1.905, 1.920, 2.005, 1.920 s**. The unchanged limit is five seconds. [Parking evidence](SC5_assets/parking-summary.json).

The existing 60v60 SC-4 research showcase passed **3/3** with both sides above the unchanged chaff/probe/mine minimums and actual models at defended alephs and contested sectors. [Dispenser evidence](SC5_assets/dispenser-summary.json). This independently checks that early stealth research and the dedicated mine-layer exception preserve those skills.

Seven affected live/lifecycle cases passed **3/3 isolated invocations each** (21 passes): SC-3 cloak-to-strike, one-peer and 60-peer research repurchases, stale-wallet refusal fallback, authored and generated first-branch research, and the rig-pair two-match upgrade/reset fixture. The all-civilization authored/generated plan validator passed **3/3** as well. All original assertions and deadlines remain in place. The 60-peer repurchase test observes full server grants without refusals; the separate refusal fixture deliberately injects one stale-wallet race and verifies recovery. No full Live/Perf tier was run.

One full fast-tier run on the final merged code (`tools/run-tests.sh --tier fast`): **8856 tests PASS** (Allegiance.Lobby 19, ClientCore.MonoGame 2498 (+3 NotExecuted, pre-existing), ClientCore 1472, Core 4852, plus quarantine 12), 271 s. Live/perf tiers run on Claude's mini test runner per merged batch.

New/affected tests on the final code, each 3× alone unless noted: `SC5CloakResearchTests` 4/4 ×3; `SC5CloakLiveTests` (Live) 1/1 ×3, qualifying both sides ≥5 (17/14/22 vs 5/5/5); the modified `P703ResearchCommanderLiveTests` 4/4 ×3; `SC3CloakLiveTests` 1/1 ×3; the all-civ plan validator `P702ResearchTests` 8/8 ×3; the scale-eight opening metric `P733ResearchTests.Headless_10v10_scale_eight…` 1/1 ×3; the selector/support/finale/policy fast classes (P704, P656, SC3Policy, SC4Dispenser, SC2Nanite) 37/37 ×3; and the showcase-never-parks test `P700ShowcaseLiveTests.Local_showcase_never_parks…` **5/5**. Failing-first: reverting the selector change and the plan branch makes `SC5CloakResearchTests` fail 4/4 (no mounted cloak) and pass with the fix; the repositioning specifically restores `P733ResearchTests` from fail (front-loaded stations) to pass (dev-dense opening + cloak branch after).

The five-activation minimum in `docs/showcase/SKILLS.csv` is unchanged. These are local qualification runs at the rig civilizations/research scale, not a claim of a new 107v107 rig result. Preferring stealth can select a bomber with less raw station damage or a scout with less scanner reach; those are intentional pilot-policy tradeoffs, while damaging guns and legal strike damage remain required.

Raw evidence is retained in [the ignored workspace evidence directory](/home/domingo/allegiance-codex-sc5/.verify-sc5-evidence/). The prescribed `~/allegiance-scratch/SC5/` and `~/allegiance-evidence/SC5/` could not be created: both returned “Read-only file system” under this sandbox. All scratch therefore used an ignored NVMe workspace directory, never `/tmp`. A `/proc/<runner-pid>/fd/<directory-fd>` TMPDIR alias keeps temporary fixture paths from inheriting the checkout's `local.env`; all writes still land in workspace scratch. NuGet's HTTP cache was also redirected there. Base and changed builds used separate output directories to preserve the parking baseline ([binary hashes](SC5_assets/binaries.json)); after baseline completion the final code was also rebuilt in standard solution output paths for the fast tier. Temporary scratch/build outputs are removed after verification; only report-linked evidence is retained. Small tracked evidence stays below the 2 MB limit.
