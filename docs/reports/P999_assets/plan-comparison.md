# P999 — offline plans versus today's actual generated schedule

Seed 999; opponents [18, 4, 5]. Baseline is Generate(prioritizeResearch: true), the P942 MCP execution order; the serialized server fallback still uses canonical Generate(). No live behavior is changed.

Times below are cumulative serial bucket seconds at scale 1, prices are cumulative bucket funding. Unlimited funding/rocks, no travel/building effect, no rebuy/losses. A station bucket's completion does not imply its station is already built in a match. Candidate slices are exact-replayed; conservative unsliced prefixes are labeled in the tool JSON. Offline scores are dimensionless hypotheses, not expected win rates.

## 18 — Gigacorp

First 40 steps:

| Step | Today research-first | Skirmish | Siege | Economy |
|---|---|---|---|---|
| 1 | develop 222 Weedcore 03 | develop 37 Bomber | develop 37 Bomber | develop 37 Bomber |
| 2 | develop 75 EW Disruptor 1 | develop 22 Patroller | develop 22 Patroller | develop 22 Patroller |
| 3 | develop 84 PW SkyCap 1 | develop 87 ER Nanite 2 | develop 87 ER Nanite 2 | develop 87 ER Nanite 2 |
| 4 | develop 90 MRM Seeker 2 | develop 75 EW Disruptor 1 | develop 75 EW Disruptor 1 | develop 75 EW Disruptor 1 |
| 5 | develop 109 Med Shield 2 | drone 41 .TowerAC | drone 41 .TowerAC | drone 41 .TowerAC |
| 6 | develop 114 Booster 1 | station 14 Refinery (Lt) | station 14 Refinery (Lt) | station 14 Refinery (Lt) |
| 7 | develop 54 Retro Booster | develop 222 Weedcore 03 | develop 222 Weedcore 03 | develop 222 Weedcore 03 |
| 8 | develop 116 Lt Booster 1 | drone 200 .Caltrop | drone 200 .Caltrop | drone 200 .Caltrop |
| 9 | develop 119 Sig Cloak 1 | station 47 Teleport (Lt) | station 47 Teleport (Lt) | station 47 Teleport (Lt) |
| 10 | develop 131 Counter 2 | develop 84 PW SkyCap 1 | develop 84 PW SkyCap 1 | develop 84 PW SkyCap 1 |
| 11 | develop 134 Hvy Counter 1 | develop 90 MRM Seeker 2 | develop 90 MRM Seeker 2 | develop 90 MRM Seeker 2 |
| 12 | develop 235 MRM Quickfire 1 | develop 109 Med Shield 2 | develop 109 Med Shield 2 | develop 109 Med Shield 2 |
| 13 | develop 236 MRM Quickfire 2 | develop 114 Booster 1 | develop 114 Booster 1 | develop 114 Booster 1 |
| 14 | develop 22 Patroller | develop 54 Retro Booster | develop 54 Retro Booster | develop 54 Retro Booster |
| 15 | develop 87 ER Nanite 2 | develop 116 Lt Booster 1 | develop 116 Lt Booster 1 | develop 116 Lt Booster 1 |
| 16 | develop 97 SRM Seismic | develop 119 Sig Cloak 1 | develop 119 Sig Cloak 1 | develop 119 Sig Cloak 1 |
| 17 | develop 121 Hvy Sig Cloak 1 | develop 131 Counter 2 | develop 131 Counter 2 | develop 131 Counter 2 |
| 18 | develop 294 Std Carrier Drone | develop 134 Hvy Counter 1 | develop 134 Hvy Counter 1 | develop 134 Hvy Counter 1 |
| 19 | develop 37 Bomber | develop 235 MRM Quickfire 1 | develop 235 MRM Quickfire 1 | develop 235 MRM Quickfire 1 |
| 20 | develop 171 Upgrade Garrison | develop 236 MRM Quickfire 2 | develop 236 MRM Quickfire 2 | develop 236 MRM Quickfire 2 |
| 21 | develop 187 SpecMine (Ca) | drone 32 .Miner | drone 32 .Miner | drone 32 .Miner |
| 22 | develop 188 SpecMine (Ur) | drone 225 .Carrier | drone 225 .Carrier | drone 225 .Carrier |
| 23 | develop 189 SpecMine (Si) | station 2 Outpost (Lt) | station 2 Outpost (Lt) | station 2 Outpost (Lt) |
| 24 | station 2 Outpost (Lt) | develop 97 SRM Seismic | develop 97 SRM Seismic | develop 97 SRM Seismic |
| 25 | station 11 Supremacy | develop 121 Hvy Sig Cloak 1 | develop 121 Hvy Sig Cloak 1 | develop 121 Hvy Sig Cloak 1 |
| 26 | develop 151 GA: Missile Track 1 | develop 294 Std Carrier Drone | develop 294 Std Carrier Drone | develop 294 Std Carrier Drone |
| 27 | develop 155 GA: Ripcord 1 | develop 171 Upgrade Garrison | develop 171 Upgrade Garrison | develop 171 Upgrade Garrison |
| 28 | develop 157 GA: Ship Sensors 1 | develop 187 SpecMine (Ca) | develop 187 SpecMine (Ca) | develop 187 SpecMine (Ca) |
| 29 | develop 159 GA: Ship Hull 1 | station 22 SpecMine (Ca) | station 22 SpecMine (Ca) | station 22 SpecMine (Ca) |
| 30 | develop 161 GA: Ship Shield 1 | develop 188 SpecMine (Ur) | develop 188 SpecMine (Ur) | develop 188 SpecMine (Ur) |
| 31 | develop 53 Cruise Booster | station 21 SpecMine (Ur) | station 21 SpecMine (Ur) | station 21 SpecMine (Ur) |
| 32 | develop 71 PW Gattling Gun 2 | develop 189 SpecMine (Si) | develop 189 SpecMine (Si) | develop 189 SpecMine (Si) |
| 33 | develop 74 EW Disruptor 2 | station 23 SpecMine (Si) | station 23 SpecMine (Si) | station 23 SpecMine (Si) |
| 34 | develop 81 PW Auto-Cannon 2 | station 11 Supremacy | station 11 Supremacy | station 11 Supremacy |
| 35 | develop 88 SRM Dumbfire 2 | develop 20 Enh Fighter | develop 20 Enh Fighter | develop 20 Enh Fighter |
| 36 | develop 96 SRM Anti-Base 2 | develop 74 EW Disruptor 2 | develop 74 EW Disruptor 2 | develop 74 EW Disruptor 2 |
| 37 | develop 107 Sm Shield 2 | develop 71 PW Gattling Gun 2 | develop 71 PW Gattling Gun 2 | develop 71 PW Gattling Gun 2 |
| 38 | develop 113 Booster 2 | develop 107 Sm Shield 2 | develop 107 Sm Shield 2 | develop 157 GA: Ship Sensors 1 |
| 39 | develop 129 Mine Pack 1 | develop 88 SRM Dumbfire 2 | develop 88 SRM Dumbfire 2 | develop 107 Sm Shield 2 |
| 40 | develop 123 EWS Probe 2 | develop 157 GA: Ship Sensors 1 | develop 157 GA: Ship Sensors 1 | develop 88 SRM Dumbfire 2 |


Opening opportunity ranking (top six per profile):

| Profile | Target | Score | Route steps | Cost | Seconds | Exact-valid slice |
|---|---|---|---|---|---|---|
| skirmish | Bomber | 0.099 | 1 | 10000 | 120 | True |
| skirmish | Patroller | 0.049 | 1 | 5000 | 120 | True |
| skirmish | Upgrade Light Class | 0.049 | 5 | 70000 | 600 | True |
| skirmish | Upgrade Heavy Class | 0.047 | 7 | 105000 | 840 | True |
| skirmish | Upgrade Medium Class | 0.041 | 6 | 85000 | 720 | True |
| skirmish | Fighter/Bomber | 0.036 | 10 | 85000 | 1200 | True |
| siege | Bomber | 0.216 | 1 | 10000 | 120 | True |
| siege | Hvy Bomber | 0.055 | 8 | 110000 | 960 | True |
| siege | Stealth Bomber | 0.053 | 6 | 70000 | 720 | True |
| siege | Fighter/Bomber | 0.05 | 10 | 85000 | 1200 | True |
| siege | Upgrade Heavy Class | 0.05 | 7 | 105000 | 840 | True |
| siege | Patroller | 0.025 | 1 | 5000 | 120 | True |
| economy | Bomber | 0.072 | 1 | 10000 | 120 | True |
| economy | SpecMine (Si) | 0.05 | 8 | 108000 | 930 | True |
| economy | SpecMine (Ca) | 0.039 | 8 | 106000 | 930 | True |
| economy | SpecMine (Ur) | 0.039 | 8 | 106000 | 930 | True |
| economy | Upgrade Light Class | 0.03 | 5 | 70000 | 600 | True |
| economy | Upgrade Heavy Class | 0.03 | 7 | 105000 | 840 | True |


Key completions: step / serial seconds / cumulative cost:

| Key | Today | Skirmish | Siege | Economy |
|---|---|---|---|---|
| Patroller | 14 / 1570 / 35010 | 2 / 240 / 15000 | 2 / 240 / 15000 | 2 / 240 / 15000 |
| Bomber | 19 / 2170 / 67510 | 1 / 120 / 10000 | 1 / 120 / 10000 | 1 / 120 / 10000 |
| Outpost (Lt) | 24 / 2740 / 111260 | 23 / 2470 / 68010 | 23 / 2470 / 68010 | 23 / 2470 / 68010 |
| Supremacy | 25 / 2860 / 121260 | 34 / 3700 / 155510 | 34 / 3700 / 155510 | 34 / 3700 / 155510 |
| GA: Ship Hull 1 | 29 / 3340 / 129260 | 41 / 4540 / 177010 | 41 / 4540 / 177010 | 41 / 4540 / 177010 |
| PW Gattling Gun 2 | 32 / 3700 / 136260 | 37 / 4060 / 168010 | 37 / 4060 / 168010 | 37 / 4060 / 168010 |
| EW Disruptor 2 | 33 / 3820 / 138760 | 36 / 3940 / 165510 | 36 / 3940 / 165510 | 36 / 3940 / 165510 |
| Enh Fighter | 42 / 4900 / 171260 | 35 / 3820 / 163010 | 35 / 3820 / 163010 | 35 / 3820 / 163010 |
| Upgrade Supremacy | 43 / 5020 / 196260 | 99 / 11470 / 452510 | 99 / 11470 / 452510 | 99 / 11470 / 452510 |
| Supremacy (Adv) | 44 / 5140 / 206260 | 100 / 11590 / 462510 | 100 / 11590 / 462510 | 100 / 11590 / 462510 |
| Adv Fighter | 62 / 7300 / 263760 | 101 / 11710 / 472510 | 101 / 11710 / 472510 | 101 / 11710 / 472510 |
| Expansion | 64 / 7540 / 278760 | 52 / 5860 / 215510 | 52 / 5860 / 215510 | 52 / 5860 / 215510 |
| GA: PW&EW Damage 1 | 66 / 7780 / 282760 | 54 / 6100 / 225010 | 56 / 6340 / 229010 | 57 / 6460 / 231010 |
| Interceptor | 79 / 9340 / 332260 | 53 / 5980 / 223010 | 53 / 5980 / 223010 | 53 / 5980 / 223010 |
| Expansion (Adv) | 81 / 9580 / 367260 | 151 / 17710 / 772510 | 151 / 17710 / 772510 | 151 / 17710 / 772510 |
| Tactical | 99 / 11740 / 459760 | 68 / 7780 / 279010 | 68 / 7780 / 279010 | 68 / 7780 / 279010 |
| Stealth Fighter | 112 / 13300 / 514260 | 71 / 8140 / 292510 | 71 / 8140 / 292510 | 71 / 8140 / 292510 |
| Tactical (Adv) | 114 / 13540 / 549260 | 135 / 15790 / 670010 | 135 / 15790 / 670010 | 135 / 15790 / 670010 |
| Shipyard | 129 / 15340 / 631760 | 121 / 14110 / 545010 | 121 / 14110 / 545010 | 121 / 14110 / 545010 |
| Upgrade Medium Class | 140 / 16660 / 701760 | 123 / 14350 / 570010 | 123 / 14350 / 570010 | 123 / 14350 / 570010 |
| Garrison (Str) | 142 / 16900 / 741760 | 83 / 9550 / 355010 | 83 / 9550 / 355010 | 83 / 9550 / 355010 |
| Hvy Bomber | 156 / 18580 / 799260 | 84 / 9670 / 365010 | 84 / 9670 / 365010 | 84 / 9670 / 365010 |
| Refinery (Lt) | 162 / 19180 / 825510 | 6 / 660 / 25750 | 6 / 660 / 25750 | 6 / 660 / 25750 |
| Teleport (Lt) | 163 / 19270 / 827760 | 9 / 850 / 29510 | 9 / 850 / 29510 | 9 / 850 / 29510 |
| SpecMine (Ur) | 166 / 19570 / 841010 | 31 / 3370 / 127510 | 31 / 3370 / 127510 | 31 / 3370 / 127510 |
| SpecMine (Ca) | 167 / 19660 / 847010 | 29 / 3160 / 111510 | 29 / 3160 / 111510 | 29 / 3160 / 111510 |
| SpecMine (Si) | 168 / 19750 / 855010 | 33 / 3580 / 145510 | 33 / 3580 / 145510 | 33 / 3580 / 145510 |


All hull unlocks (including autonomous hulls), for reproducible timing:

| Plan | Kind | ID | Name | Step | Seconds | Funding |
|---|---|---|---|---|---|---|
| today | hull | 210 | Scout | 0 | 0 | 0 |
| today | hull | 217 | Lg Enh Constructor | 0 | 0 | 0 |
| today | hull | 237 | Enh Miner | 0 | 0 | 0 |
| today | hull | 244 | Lifepod | 0 | 0 | 0 |
| today | hull | 248 | Lt Carrier [SR] | 0 | 0 | 0 |
| today | hull | 271 | Sm Enh Constructor | 0 | 0 | 0 |
| today | hull | 215 | Patroller | 14 | 1570 | 35010 |
| today | hull | 225 | Carrier [SR] | 18 | 2050 | 57510 |
| today | hull | 204 | Bomber | 19 | 2170 | 67510 |
| today | hull | 214 | Enh Fighter | 42 | 4900 | 171260 |
| today | hull | 258 | Lxy Fighter | 62 | 7300 | 263760 |
| today | hull | 288 | Adv Fighter | 62 | 7300 | 263760 |
| today | hull | 127 | Fighter/Bomber | 63 | 7420 | 268760 |
| today | hull | 212 | Interceptor | 79 | 9340 | 332260 |
| today | hull | 295 | Troop Transport | 94 | 11140 | 414760 |
| today | hull | 216 | Lg Adv Constructor | 95 | 11260 | 422260 |
| today | hull | 272 | Sm Adv Constructor | 95 | 11260 | 422260 |
| today | hull | 236 | Adv Miner | 96 | 11380 | 429760 |
| today | hull | 251 | Lxy Interceptor | 97 | 11500 | 439760 |
| today | hull | 281 | Hvy Interceptor | 97 | 11500 | 439760 |
| today | hull | 293 | Hvy Troop Transport | 98 | 11620 | 449760 |
| today | hull | 294 | Lxy Hvy Troop Transport | 98 | 11620 | 449760 |
| today | hull | 211 | Stealth Fighter | 112 | 13300 | 514260 |
| today | hull | 148 | Stealth Bomber [NG] | 127 | 15100 | 606760 |
| today | hull | 220 | Stealth Bomber | 127 | 15100 | 606760 |
| today | hull | 256 | Lxy Stl Fighter | 128 | 15220 | 616760 |
| today | hull | 286 | Adv Stl Fighter | 128 | 15220 | 616760 |
| today | hull | 34 | Corvette | 139 | 16540 | 686760 |
| today | hull | 78 | Attack Carrier [SR] | 139 | 16540 | 686760 |
| today | hull | 221 | Lt Cruiser | 140 | 16660 | 701760 |
| today | hull | 287 | Assault Ship | 140 | 16660 | 701760 |
| today | hull | 291 | Destroyer | 140 | 16660 | 701760 |
| today | hull | 62 | Lxy Cruiser | 141 | 16780 | 721760 |
| today | hull | 222 | Devastator | 141 | 16780 | 721760 |
| today | hull | 285 | Adv Scout | 155 | 18460 | 789260 |
| today | hull | 289 | Hvy Bomber | 156 | 18580 | 799260 |
| today | hull | 299 | Lxy Hvy Bomber | 156 | 18580 | 799260 |
| today | hull | 290 | Gunship | 157 | 18700 | 809260 |
| today | hull | 85 | Hvy Scout | 158 | 18820 | 819260 |
| today | hull | 92 | Lxy Scout | 158 | 18820 | 819260 |
| skirmish | hull | 210 | Scout | 0 | 0 | 0 |
| skirmish | hull | 217 | Lg Enh Constructor | 0 | 0 | 0 |
| skirmish | hull | 237 | Enh Miner | 0 | 0 | 0 |
| skirmish | hull | 244 | Lifepod | 0 | 0 | 0 |
| skirmish | hull | 248 | Lt Carrier [SR] | 0 | 0 | 0 |
| skirmish | hull | 271 | Sm Enh Constructor | 0 | 0 | 0 |
| skirmish | hull | 204 | Bomber | 1 | 120 | 10000 |
| skirmish | hull | 215 | Patroller | 2 | 240 | 15000 |
| skirmish | hull | 225 | Carrier [SR] | 26 | 2830 | 85510 |
| skirmish | hull | 214 | Enh Fighter | 35 | 3820 | 163010 |
| skirmish | hull | 212 | Interceptor | 53 | 5980 | 223010 |
| skirmish | hull | 211 | Stealth Fighter | 71 | 8140 | 292510 |
| skirmish | hull | 289 | Hvy Bomber | 84 | 9670 | 365010 |
| skirmish | hull | 299 | Lxy Hvy Bomber | 84 | 9670 | 365010 |
| skirmish | hull | 290 | Gunship | 85 | 9790 | 375010 |
| skirmish | hull | 285 | Adv Scout | 86 | 9910 | 382510 |
| skirmish | hull | 85 | Hvy Scout | 87 | 10030 | 392510 |
| skirmish | hull | 92 | Lxy Scout | 87 | 10030 | 392510 |
| skirmish | hull | 258 | Lxy Fighter | 101 | 11710 | 472510 |
| skirmish | hull | 288 | Adv Fighter | 101 | 11710 | 472510 |
| skirmish | hull | 127 | Fighter/Bomber | 105 | 12190 | 485010 |
| skirmish | hull | 34 | Corvette | 122 | 14230 | 555010 |
| skirmish | hull | 78 | Attack Carrier [SR] | 122 | 14230 | 555010 |
| skirmish | hull | 221 | Lt Cruiser | 123 | 14350 | 570010 |
| skirmish | hull | 287 | Assault Ship | 123 | 14350 | 570010 |
| skirmish | hull | 291 | Destroyer | 123 | 14350 | 570010 |
| skirmish | hull | 62 | Lxy Cruiser | 124 | 14470 | 590010 |
| skirmish | hull | 222 | Devastator | 124 | 14470 | 590010 |
| skirmish | hull | 220 | Stealth Bomber | 136 | 15910 | 677510 |
| skirmish | hull | 256 | Lxy Stl Fighter | 137 | 16030 | 687510 |
| skirmish | hull | 286 | Adv Stl Fighter | 137 | 16030 | 687510 |
| skirmish | hull | 148 | Stealth Bomber [NG] | 141 | 16510 | 702510 |
| skirmish | hull | 251 | Lxy Interceptor | 152 | 17830 | 782510 |
| skirmish | hull | 281 | Hvy Interceptor | 152 | 17830 | 782510 |
| skirmish | hull | 295 | Troop Transport | 165 | 19390 | 830010 |
| skirmish | hull | 216 | Lg Adv Constructor | 166 | 19510 | 837510 |
| skirmish | hull | 272 | Sm Adv Constructor | 166 | 19510 | 837510 |
| skirmish | hull | 236 | Adv Miner | 167 | 19630 | 845010 |
| skirmish | hull | 293 | Hvy Troop Transport | 168 | 19750 | 855010 |
| skirmish | hull | 294 | Lxy Hvy Troop Transport | 168 | 19750 | 855010 |
| siege | hull | 210 | Scout | 0 | 0 | 0 |
| siege | hull | 217 | Lg Enh Constructor | 0 | 0 | 0 |
| siege | hull | 237 | Enh Miner | 0 | 0 | 0 |
| siege | hull | 244 | Lifepod | 0 | 0 | 0 |
| siege | hull | 248 | Lt Carrier [SR] | 0 | 0 | 0 |
| siege | hull | 271 | Sm Enh Constructor | 0 | 0 | 0 |
| siege | hull | 204 | Bomber | 1 | 120 | 10000 |
| siege | hull | 215 | Patroller | 2 | 240 | 15000 |
| siege | hull | 225 | Carrier [SR] | 26 | 2830 | 85510 |
| siege | hull | 214 | Enh Fighter | 35 | 3820 | 163010 |
| siege | hull | 212 | Interceptor | 53 | 5980 | 223010 |
| siege | hull | 211 | Stealth Fighter | 71 | 8140 | 292510 |
| siege | hull | 289 | Hvy Bomber | 84 | 9670 | 365010 |
| siege | hull | 299 | Lxy Hvy Bomber | 84 | 9670 | 365010 |
| siege | hull | 290 | Gunship | 85 | 9790 | 375010 |
| siege | hull | 285 | Adv Scout | 86 | 9910 | 382510 |
| siege | hull | 85 | Hvy Scout | 87 | 10030 | 392510 |
| siege | hull | 92 | Lxy Scout | 87 | 10030 | 392510 |
| siege | hull | 258 | Lxy Fighter | 101 | 11710 | 472510 |
| siege | hull | 288 | Adv Fighter | 101 | 11710 | 472510 |
| siege | hull | 127 | Fighter/Bomber | 105 | 12190 | 485010 |
| siege | hull | 34 | Corvette | 122 | 14230 | 555010 |
| siege | hull | 78 | Attack Carrier [SR] | 122 | 14230 | 555010 |
| siege | hull | 221 | Lt Cruiser | 123 | 14350 | 570010 |
| siege | hull | 287 | Assault Ship | 123 | 14350 | 570010 |
| siege | hull | 291 | Destroyer | 123 | 14350 | 570010 |
| siege | hull | 62 | Lxy Cruiser | 124 | 14470 | 590010 |
| siege | hull | 222 | Devastator | 124 | 14470 | 590010 |
| siege | hull | 220 | Stealth Bomber | 136 | 15910 | 677510 |
| siege | hull | 256 | Lxy Stl Fighter | 137 | 16030 | 687510 |
| siege | hull | 286 | Adv Stl Fighter | 137 | 16030 | 687510 |
| siege | hull | 148 | Stealth Bomber [NG] | 141 | 16510 | 702510 |
| siege | hull | 251 | Lxy Interceptor | 152 | 17830 | 782510 |
| siege | hull | 281 | Hvy Interceptor | 152 | 17830 | 782510 |
| siege | hull | 295 | Troop Transport | 165 | 19390 | 830010 |
| siege | hull | 216 | Lg Adv Constructor | 166 | 19510 | 837510 |
| siege | hull | 272 | Sm Adv Constructor | 166 | 19510 | 837510 |
| siege | hull | 236 | Adv Miner | 167 | 19630 | 845010 |
| siege | hull | 293 | Hvy Troop Transport | 168 | 19750 | 855010 |
| siege | hull | 294 | Lxy Hvy Troop Transport | 168 | 19750 | 855010 |
| economy | hull | 210 | Scout | 0 | 0 | 0 |
| economy | hull | 217 | Lg Enh Constructor | 0 | 0 | 0 |
| economy | hull | 237 | Enh Miner | 0 | 0 | 0 |
| economy | hull | 244 | Lifepod | 0 | 0 | 0 |
| economy | hull | 248 | Lt Carrier [SR] | 0 | 0 | 0 |
| economy | hull | 271 | Sm Enh Constructor | 0 | 0 | 0 |
| economy | hull | 204 | Bomber | 1 | 120 | 10000 |
| economy | hull | 215 | Patroller | 2 | 240 | 15000 |
| economy | hull | 225 | Carrier [SR] | 26 | 2830 | 85510 |
| economy | hull | 214 | Enh Fighter | 35 | 3820 | 163010 |
| economy | hull | 212 | Interceptor | 53 | 5980 | 223010 |
| economy | hull | 211 | Stealth Fighter | 71 | 8140 | 292510 |
| economy | hull | 289 | Hvy Bomber | 84 | 9670 | 365010 |
| economy | hull | 299 | Lxy Hvy Bomber | 84 | 9670 | 365010 |
| economy | hull | 285 | Adv Scout | 85 | 9790 | 372510 |
| economy | hull | 85 | Hvy Scout | 86 | 9910 | 382510 |
| economy | hull | 92 | Lxy Scout | 86 | 9910 | 382510 |
| economy | hull | 290 | Gunship | 87 | 10030 | 392510 |
| economy | hull | 258 | Lxy Fighter | 101 | 11710 | 472510 |
| economy | hull | 288 | Adv Fighter | 101 | 11710 | 472510 |
| economy | hull | 127 | Fighter/Bomber | 105 | 12190 | 485010 |
| economy | hull | 34 | Corvette | 122 | 14230 | 555010 |
| economy | hull | 78 | Attack Carrier [SR] | 122 | 14230 | 555010 |
| economy | hull | 221 | Lt Cruiser | 123 | 14350 | 570010 |
| economy | hull | 287 | Assault Ship | 123 | 14350 | 570010 |
| economy | hull | 291 | Destroyer | 123 | 14350 | 570010 |
| economy | hull | 62 | Lxy Cruiser | 124 | 14470 | 590010 |
| economy | hull | 222 | Devastator | 124 | 14470 | 590010 |
| economy | hull | 220 | Stealth Bomber | 136 | 15910 | 677510 |
| economy | hull | 256 | Lxy Stl Fighter | 137 | 16030 | 687510 |
| economy | hull | 286 | Adv Stl Fighter | 137 | 16030 | 687510 |
| economy | hull | 148 | Stealth Bomber [NG] | 141 | 16510 | 702510 |
| economy | hull | 251 | Lxy Interceptor | 154 | 18070 | 787510 |
| economy | hull | 281 | Hvy Interceptor | 154 | 18070 | 787510 |
| economy | hull | 295 | Troop Transport | 165 | 19390 | 830010 |
| economy | hull | 216 | Lg Adv Constructor | 166 | 19510 | 837510 |
| economy | hull | 272 | Sm Adv Constructor | 166 | 19510 | 837510 |
| economy | hull | 236 | Adv Miner | 167 | 19630 | 845010 |
| economy | hull | 293 | Hvy Troop Transport | 168 | 19750 | 855010 |
| economy | hull | 294 | Lxy Hvy Troop Transport | 168 | 19750 | 855010 |


## 4 — Phoenix

First 40 steps:

| Step | Today research-first | Skirmish | Siege | Economy |
|---|---|---|---|---|
| 1 | develop 222 Weedcore 03 | station 68 Shipyard | develop 37 Bomber | station 68 Shipyard |
| 2 | develop 54 Retro Booster | develop 296 Upgrade Light Class | station 71 Refinery | develop 296 Upgrade Light Class |
| 3 | develop 90 MRM Seeker 2 | develop 297 Upgrade Medium Class | station 72 Teleport | develop 297 Upgrade Medium Class |
| 4 | develop 109 Med Shield 2 | develop 298 Upgrade Heavy Class | develop 87 ER Nanite 2 | develop 298 Upgrade Heavy Class |
| 5 | develop 116 Lt Booster 1 | develop 37 Bomber | drone 34 .TowerAC | develop 37 Bomber |
| 6 | develop 131 Counter 2 | station 69 Shipyard (Dry) | develop 222 Weedcore 03 | station 69 Shipyard (Dry) |
| 7 | develop 236 MRM Quickfire 2 | develop 101 SRM Tactical Nuke 2 | drone 200 .Caltrop | develop 101 SRM Tactical Nuke 2 |
| 8 | develop 58 Enh Constructor | station 72 Teleport | develop 54 Retro Booster | station 71 Refinery |
| 9 | develop 64 Enh Miner | station 71 Refinery | develop 90 MRM Seeker 2 | station 72 Teleport |
| 10 | develop 87 ER Nanite 2 | station 73 [T]ransceiver | develop 109 Med Shield 2 | station 73 [T]ransceiver |
| 11 | develop 97 SRM Seismic | develop 203 [T] SmallRip Drone | develop 116 Lt Booster 1 | develop 203 [T] SmallRip Drone |
| 12 | develop 121 Hvy Sig Cloak 1 | develop 295 [T] Recon Drone | develop 131 Counter 2 | develop 295 [T] Recon Drone |
| 13 | develop 223 Enh Carrier Drone | develop 87 ER Nanite 2 | develop 236 MRM Quickfire 2 | develop 87 ER Nanite 2 |
| 14 | develop 37 Bomber | drone 29 .Recon | drone 33 .Miner | drone 29 .Recon |
| 15 | develop 171 Upgrade Garrison | develop 222 Weedcore 03 | drone 37 .Carrier | develop 222 Weedcore 03 |
| 16 | station 70 Outpost | drone 34 .TowerAC | develop 58 Enh Constructor | drone 34 .TowerAC |
| 17 | station 62 Supremacy | drone 28 .SmallRip | develop 64 Enh Miner | drone 28 .SmallRip |
| 18 | develop 151 GA: Missile Track 1 | drone 200 .Caltrop | develop 97 SRM Seismic | drone 200 .Caltrop |
| 19 | develop 155 GA: Ripcord 1 | develop 54 Retro Booster | develop 121 Hvy Sig Cloak 1 | develop 54 Retro Booster |
| 20 | develop 157 GA: Ship Sensors 1 | develop 90 MRM Seeker 2 | station 70 Outpost | develop 90 MRM Seeker 2 |
| 21 | develop 159 GA: Ship Hull 1 | develop 109 Med Shield 2 | develop 223 Enh Carrier Drone | develop 109 Med Shield 2 |
| 22 | develop 161 GA: Ship Shield 1 | develop 116 Lt Booster 1 | develop 171 Upgrade Garrison | develop 116 Lt Booster 1 |
| 23 | develop 53 Cruise Booster | develop 131 Counter 2 | station 61 Garrison (Str) | develop 131 Counter 2 |
| 24 | develop 71 PW Gattling Gun 2 | develop 236 MRM Quickfire 2 | develop 173 LRM Aleph Res 1 | develop 236 MRM Quickfire 2 |
| 25 | develop 74 EW Disruptor 2 | drone 33 .Miner | develop 233 LRM Aleph Res 2 | drone 33 .Miner |
| 26 | develop 81 PW Auto-Cannon 2 | drone 37 .Carrier | develop 234 LRM Aleph Res 3 | drone 37 .Carrier |
| 27 | develop 88 SRM Dumbfire 2 | develop 50 XRM Torpedo 1 | develop 125 Teleport Probe 1 | develop 50 XRM Torpedo 1 |
| 28 | develop 96 SRM Anti-Base 2 | develop 51 XRM Torpedo 2 | develop 15 Adv Scout | develop 51 XRM Torpedo 2 |
| 29 | develop 107 Sm Shield 2 | develop 58 Enh Constructor | develop 130 Counter 3 | develop 58 Enh Constructor |
| 30 | develop 113 Booster 2 | develop 64 Enh Miner | develop 115 Lt Booster 2 | develop 64 Enh Miner |
| 31 | develop 129 Mine Pack 1 | develop 83 PW SkyCap 2 | develop 108 Med Shield 3 | develop 83 PW SkyCap 2 |
| 32 | develop 123 EWS Probe 2 | develop 82 PW SkyCap 3 | develop 91 MRM Seeker 3 | develop 82 PW SkyCap 3 |
| 33 | develop 253 XRM Anti-Base 1 | develop 97 SRM Seismic | develop 39 Gunship | develop 97 SRM Seismic |
| 34 | develop 20 Enh Fighter | develop 100 XRM Hunter Killer 2 | develop 120 Hvy Sig Cloak 2 | develop 100 XRM Hunter Killer 2 |
| 35 | develop 2 Upgrade Supremacy | develop 99 XRM Hunter Killer 3 | develop 237 MRM Quickfire 3 | develop 99 XRM Hunter Killer 3 |
| 36 | station 63 Supremacy (Adv) | develop 104 XRM Cruise 1 | develop 35 Hvy Bomber | develop 104 XRM Cruise 1 |
| 37 | develop 70 PW Gattling Gun 3 | develop 103 XRM Cruise 2 | station 73 [T]ransceiver | develop 103 XRM Cruise 2 |
| 38 | develop 72 EW Galvonic Blaster | develop 105 MRM Seismic | develop 295 [T] Recon Drone | develop 105 MRM Seismic |
| 39 | develop 73 EW Disruptor 3 | develop 111 Large Shield 2 | develop 203 [T] SmallRip Drone | develop 111 Large Shield 2 |
| 40 | develop 80 PW Auto-Cannon 3 | develop 110 Large Shield 3 | drone 29 .Recon | develop 110 Large Shield 3 |


Opening opportunity ranking (top six per profile):

| Profile | Target | Score | Route steps | Cost | Seconds | Exact-valid slice |
|---|---|---|---|---|---|---|
| skirmish | Upgrade Medium Class | 0.097 | 3 | 40000 | 480 | True |
| skirmish | Upgrade Heavy Class | 0.07 | 4 | 60000 | 660 | True |
| skirmish | Upgrade Light Class | 0.068 | 2 | 25000 | 300 | True |
| skirmish | LRM Aleph Res 1 | 0.064 | 8 | 107500 | 1260 | True |
| skirmish | Bomber | 0.064 | 1 | 10000 | 180 | True |
| skirmish | Shipyard (Dry) | 0.062 | 5 | 75000 | 780 | True |
| siege | Bomber | 0.159 | 1 | 10000 | 180 | True |
| siege | Upgrade Medium Class | 0.149 | 3 | 40000 | 480 | True |
| siege | Upgrade Heavy Class | 0.107 | 4 | 60000 | 660 | True |
| siege | Shipyard (Dry) | 0.098 | 5 | 75000 | 780 | True |
| siege | SRM Tactical Nuke 2 | 0.098 | 6 | 80000 | 960 | True |
| siege | Large Shield 3 | 0.089 | 7 | 85000 | 1140 | True |
| economy | Upgrade Medium Class | 0.068 | 3 | 40000 | 480 | True |
| economy | Upgrade Heavy Class | 0.049 | 4 | 60000 | 660 | True |
| economy | Bomber | 0.048 | 1 | 10000 | 180 | True |
| economy | LRM Aleph Res 1 | 0.043 | 8 | 107500 | 1260 | True |
| economy | Upgrade Light Class | 0.042 | 2 | 25000 | 300 | True |
| economy | Shipyard (Dry) | 0.041 | 5 | 75000 | 780 | True |


Key completions: step / serial seconds / cumulative cost:

| Key | Today | Skirmish | Siege | Economy |
|---|---|---|---|---|
| Bomber | 14 / 2355 / 57510 | 5 / 840 / 70000 | 1 / 180 / 10000 | 5 / 840 / 70000 |
| Outpost | 16 / 2625 / 72510 | 46 / 7215 / 248510 | 20 / 2835 / 71010 | 46 / 7215 / 248510 |
| Supremacy | 17 / 2745 / 82510 | 63 / 10155 / 353510 | 42 / 6495 / 198510 | 63 / 10155 / 353510 |
| GA: Ship Hull 1 | 21 / 3465 / 90510 | 73 / 11955 / 403010 | 52 / 8295 / 248010 | 74 / 12135 / 405010 |
| PW Gattling Gun 2 | 24 / 4005 / 97510 | 69 / 11235 / 393510 | 48 / 7575 / 238510 | 69 / 11235 / 393510 |
| EW Disruptor 2 | 25 / 4185 / 100010 | 70 / 11415 / 396010 | 49 / 7755 / 241010 | 70 / 11415 / 396010 |
| Enh Fighter | 34 / 5805 / 132510 | 68 / 11055 / 391010 | 47 / 7395 / 236010 | 68 / 11055 / 391010 |
| Upgrade Supremacy | 35 / 5985 / 157510 | 120 / 20145 / 596510 | 120 / 20145 / 596510 | 120 / 20145 / 596510 |
| Supremacy (Adv) | 36 / 6105 / 167510 | 121 / 20265 / 606510 | 121 / 20265 / 606510 | 121 / 20265 / 606510 |
| Adv Fighter | 54 / 9345 / 225010 | 123 / 20625 / 621510 | 123 / 20625 / 621510 | 123 / 20625 / 621510 |
| Expansion | 56 / 9645 / 240010 | 86 / 14175 / 444510 | 65 / 10515 / 289510 | 86 / 14175 / 444510 |
| GA: PW&EW Damage 1 | 58 / 10005 / 244010 | 87 / 14355 / 446510 | 66 / 10695 / 291510 | 90 / 14895 / 452510 |
| Interceptor | 71 / 12345 / 293510 | 101 / 16875 / 498010 | 80 / 13215 / 343010 | 101 / 16875 / 498010 |
| Expansion (Adv) | 73 / 12645 / 328510 | 166 / 28125 / 832010 | 166 / 28125 / 832010 | 166 / 28125 / 832010 |
| Tactical | 91 / 15825 / 421010 | 102 / 16995 / 508010 | 81 / 13335 / 353010 | 102 / 16995 / 508010 |
| Stealth Fighter | 104 / 18165 / 475510 | 119 / 19965 / 571510 | 98 / 16305 / 416510 | 119 / 19965 / 571510 |
| Tactical (Adv) | 106 / 18465 / 510510 | 146 / 24645 / 718010 | 146 / 24645 / 718010 | 146 / 24645 / 718010 |
| [T]ransceiver | 121 / 21105 / 588010 | 10 / 1440 / 106000 | 37 / 5775 / 176010 | 10 / 1440 / 106000 |
| Shipyard | 133 / 23205 / 643010 | 1 / 120 / 15000 | 99 / 16425 / 431510 | 1 / 120 / 15000 |
| Upgrade Medium Class | 143 / 25005 / 708010 | 3 / 480 / 40000 | 101 / 16785 / 456510 | 3 / 480 / 40000 |
| Shipyard (Dry) | 145 / 25305 / 743010 | 6 / 960 / 85000 | 103 / 17085 / 491510 | 6 / 960 / 85000 |
| Garrison (Str) | 154 / 26865 / 803010 | 49 / 7695 / 286010 | 23 / 3315 / 108510 | 49 / 7695 / 286010 |
| Hvy Bomber | 168 / 29385 / 860510 | 62 / 10035 / 343510 | 36 / 5655 / 166010 | 62 / 10035 / 343510 |
| Refinery | 183 / 31365 / 913510 | 9 / 1320 / 96000 | 2 / 270 / 13000 | 8 / 1230 / 93000 |
| Teleport | 184 / 31455 / 916510 | 8 / 1230 / 93000 | 3 / 360 / 16000 | 9 / 1320 / 96000 |


All hull unlocks (including autonomous hulls), for reproducible timing:

| Plan | Kind | ID | Name | Step | Seconds | Funding |
|---|---|---|---|---|---|---|
| today | hull | 77 | Lifepod | 0 | 0 | 0 |
| today | hull | 157 | Miner | 0 | 0 | 0 |
| today | hull | 179 | Sm Std Constructor | 0 | 0 | 0 |
| today | hull | 182 | Lg Std Constructor | 0 | 0 | 0 |
| today | hull | 185 | Carrier [SR] | 0 | 0 | 0 |
| today | hull | 187 | Scout | 0 | 0 | 0 |
| today | hull | 190 | Fighter | 0 | 0 | 0 |
| today | hull | 229 | SmallRip [SR] | 0 | 0 | 0 |
| today | hull | 230 | Recon Drone | 0 | 0 | 0 |
| today | hull | 180 | Sm Enh Constructor | 8 | 1275 | 20010 |
| today | hull | 183 | Lg Enh Constructor | 8 | 1275 | 20010 |
| today | hull | 177 | Enh Miner | 9 | 1455 | 25010 |
| today | hull | 186 | Enh Carrier [SR] | 13 | 2175 | 47510 |
| today | hull | 195 | Bomber | 14 | 2355 | 57510 |
| today | hull | 191 | Enh Fighter | 34 | 5805 | 132510 |
| today | hull | 192 | Adv Fighter | 54 | 9345 | 225010 |
| today | hull | 241 | Adv Fighter [DR] | 54 | 9345 | 225010 |
| today | hull | 205 | Fighter/Bomber | 55 | 9525 | 230010 |
| today | hull | 198 | Interceptor | 71 | 12345 | 293510 |
| today | hull | 202 | Troop Transport | 86 | 14985 | 376010 |
| today | hull | 181 | Sm Adv Constructor | 87 | 15165 | 383510 |
| today | hull | 184 | Lg Adv Constructor | 87 | 15165 | 383510 |
| today | hull | 232 | Nova Bomb Layer [LS] | 87 | 15165 | 383510 |
| today | hull | 178 | Adv Miner | 88 | 15345 | 391010 |
| today | hull | 199 | Hvy Interceptor | 89 | 15525 | 401010 |
| today | hull | 243 | Hvy Interceptor [DR] | 89 | 15525 | 401010 |
| today | hull | 203 | Hvy Troop Transport | 90 | 15705 | 411010 |
| today | hull | 200 | Stealth Fighter | 104 | 18165 | 475510 |
| today | hull | 206 | Stealth Bomber | 119 | 20805 | 568010 |
| today | hull | 207 | Stealth Bomber [NG] | 119 | 20805 | 568010 |
| today | hull | 201 | Adv Stl Fighter | 120 | 20985 | 578010 |
| today | hull | 242 | Adv Stl Fighter [DR] | 120 | 20985 | 578010 |
| today | hull | 209 | Corvette | 142 | 24825 | 693010 |
| today | hull | 213 | Attack Carrier [SR] | 142 | 24825 | 693010 |
| today | hull | 219 | Assault Ship | 143 | 25005 | 708010 |
| today | hull | 224 | Destroyer | 143 | 25005 | 708010 |
| today | hull | 247 | Frigate | 143 | 25005 | 708010 |
| today | hull | 226 | Devastator | 145 | 25305 | 743010 |
| today | hull | 227 | Cruiser | 145 | 25305 | 743010 |
| today | hull | 188 | Adv Scout | 167 | 29205 | 850510 |
| today | hull | 196 | Hvy Bomber | 168 | 29385 | 860510 |
| today | hull | 197 | Gunship | 169 | 29565 | 870510 |
| today | hull | 189 | Hvy Scout | 171 | 29925 | 883010 |
| today | hull | 240 | Hvy Scout [DR] | 171 | 29925 | 883010 |
| today | hull | 246 | Vanguard | 173 | 30285 | 898010 |
| skirmish | hull | 77 | Lifepod | 0 | 0 | 0 |
| skirmish | hull | 157 | Miner | 0 | 0 | 0 |
| skirmish | hull | 179 | Sm Std Constructor | 0 | 0 | 0 |
| skirmish | hull | 182 | Lg Std Constructor | 0 | 0 | 0 |
| skirmish | hull | 185 | Carrier [SR] | 0 | 0 | 0 |
| skirmish | hull | 187 | Scout | 0 | 0 | 0 |
| skirmish | hull | 190 | Fighter | 0 | 0 | 0 |
| skirmish | hull | 229 | SmallRip [SR] | 0 | 0 | 0 |
| skirmish | hull | 230 | Recon Drone | 0 | 0 | 0 |
| skirmish | hull | 209 | Corvette | 2 | 300 | 25000 |
| skirmish | hull | 213 | Attack Carrier [SR] | 2 | 300 | 25000 |
| skirmish | hull | 219 | Assault Ship | 3 | 480 | 40000 |
| skirmish | hull | 224 | Destroyer | 3 | 480 | 40000 |
| skirmish | hull | 247 | Frigate | 3 | 480 | 40000 |
| skirmish | hull | 195 | Bomber | 5 | 840 | 70000 |
| skirmish | hull | 226 | Devastator | 6 | 960 | 85000 |
| skirmish | hull | 227 | Cruiser | 6 | 960 | 85000 |
| skirmish | hull | 180 | Sm Enh Constructor | 29 | 4245 | 163510 |
| skirmish | hull | 183 | Lg Enh Constructor | 29 | 4245 | 163510 |
| skirmish | hull | 177 | Enh Miner | 30 | 4425 | 168510 |
| skirmish | hull | 186 | Enh Carrier [SR] | 47 | 7395 | 256010 |
| skirmish | hull | 188 | Adv Scout | 53 | 8415 | 301010 |
| skirmish | hull | 197 | Gunship | 56 | 8955 | 318510 |
| skirmish | hull | 196 | Hvy Bomber | 62 | 10035 | 343510 |
| skirmish | hull | 189 | Hvy Scout | 64 | 10335 | 363510 |
| skirmish | hull | 240 | Hvy Scout [DR] | 64 | 10335 | 363510 |
| skirmish | hull | 246 | Vanguard | 67 | 10875 | 383510 |
| skirmish | hull | 191 | Enh Fighter | 68 | 11055 | 391010 |
| skirmish | hull | 198 | Interceptor | 101 | 16875 | 498010 |
| skirmish | hull | 200 | Stealth Fighter | 119 | 19965 | 571510 |
| skirmish | hull | 192 | Adv Fighter | 123 | 20625 | 621510 |
| skirmish | hull | 241 | Adv Fighter [DR] | 123 | 20625 | 621510 |
| skirmish | hull | 205 | Fighter/Bomber | 130 | 21885 | 644010 |
| skirmish | hull | 206 | Stealth Bomber | 147 | 24825 | 725510 |
| skirmish | hull | 201 | Adv Stl Fighter | 150 | 25365 | 743010 |
| skirmish | hull | 242 | Adv Stl Fighter [DR] | 150 | 25365 | 743010 |
| skirmish | hull | 207 | Stealth Bomber [NG] | 163 | 27645 | 792010 |
| skirmish | hull | 202 | Troop Transport | 180 | 30645 | 882010 |
| skirmish | hull | 181 | Sm Adv Constructor | 181 | 30825 | 889510 |
| skirmish | hull | 184 | Lg Adv Constructor | 181 | 30825 | 889510 |
| skirmish | hull | 232 | Nova Bomb Layer [LS] | 181 | 30825 | 889510 |
| skirmish | hull | 178 | Adv Miner | 182 | 31005 | 897010 |
| skirmish | hull | 199 | Hvy Interceptor | 183 | 31185 | 907010 |
| skirmish | hull | 243 | Hvy Interceptor [DR] | 183 | 31185 | 907010 |
| skirmish | hull | 203 | Hvy Troop Transport | 186 | 31665 | 924010 |
| siege | hull | 77 | Lifepod | 0 | 0 | 0 |
| siege | hull | 157 | Miner | 0 | 0 | 0 |
| siege | hull | 179 | Sm Std Constructor | 0 | 0 | 0 |
| siege | hull | 182 | Lg Std Constructor | 0 | 0 | 0 |
| siege | hull | 185 | Carrier [SR] | 0 | 0 | 0 |
| siege | hull | 187 | Scout | 0 | 0 | 0 |
| siege | hull | 190 | Fighter | 0 | 0 | 0 |
| siege | hull | 229 | SmallRip [SR] | 0 | 0 | 0 |
| siege | hull | 230 | Recon Drone | 0 | 0 | 0 |
| siege | hull | 195 | Bomber | 1 | 180 | 10000 |
| siege | hull | 180 | Sm Enh Constructor | 16 | 2205 | 51010 |
| siege | hull | 183 | Lg Enh Constructor | 16 | 2205 | 51010 |
| siege | hull | 177 | Enh Miner | 17 | 2385 | 56010 |
| siege | hull | 186 | Enh Carrier [SR] | 21 | 3015 | 78510 |
| siege | hull | 188 | Adv Scout | 28 | 4215 | 128510 |
| siege | hull | 197 | Gunship | 33 | 5115 | 148510 |
| siege | hull | 196 | Hvy Bomber | 36 | 5655 | 166010 |
| siege | hull | 189 | Hvy Scout | 44 | 6855 | 213510 |
| siege | hull | 240 | Hvy Scout [DR] | 44 | 6855 | 213510 |
| siege | hull | 246 | Vanguard | 45 | 7035 | 223510 |
| siege | hull | 191 | Enh Fighter | 47 | 7395 | 236010 |
| siege | hull | 198 | Interceptor | 80 | 13215 | 343010 |
| siege | hull | 200 | Stealth Fighter | 98 | 16305 | 416510 |
| siege | hull | 209 | Corvette | 100 | 16605 | 441510 |
| siege | hull | 213 | Attack Carrier [SR] | 100 | 16605 | 441510 |
| siege | hull | 219 | Assault Ship | 101 | 16785 | 456510 |
| siege | hull | 224 | Destroyer | 101 | 16785 | 456510 |
| siege | hull | 247 | Frigate | 101 | 16785 | 456510 |
| siege | hull | 226 | Devastator | 103 | 17085 | 491510 |
| siege | hull | 227 | Cruiser | 103 | 17085 | 491510 |
| siege | hull | 192 | Adv Fighter | 123 | 20625 | 621510 |
| siege | hull | 241 | Adv Fighter [DR] | 123 | 20625 | 621510 |
| siege | hull | 205 | Fighter/Bomber | 130 | 21885 | 644010 |
| siege | hull | 206 | Stealth Bomber | 147 | 24825 | 725510 |
| siege | hull | 201 | Adv Stl Fighter | 150 | 25365 | 743010 |
| siege | hull | 242 | Adv Stl Fighter [DR] | 150 | 25365 | 743010 |
| siege | hull | 207 | Stealth Bomber [NG] | 163 | 27645 | 792010 |
| siege | hull | 202 | Troop Transport | 180 | 30645 | 882010 |
| siege | hull | 181 | Sm Adv Constructor | 181 | 30825 | 889510 |
| siege | hull | 184 | Lg Adv Constructor | 181 | 30825 | 889510 |
| siege | hull | 232 | Nova Bomb Layer [LS] | 181 | 30825 | 889510 |
| siege | hull | 178 | Adv Miner | 182 | 31005 | 897010 |
| siege | hull | 199 | Hvy Interceptor | 183 | 31185 | 907010 |
| siege | hull | 243 | Hvy Interceptor [DR] | 183 | 31185 | 907010 |
| siege | hull | 203 | Hvy Troop Transport | 186 | 31665 | 924010 |
| economy | hull | 77 | Lifepod | 0 | 0 | 0 |
| economy | hull | 157 | Miner | 0 | 0 | 0 |
| economy | hull | 179 | Sm Std Constructor | 0 | 0 | 0 |
| economy | hull | 182 | Lg Std Constructor | 0 | 0 | 0 |
| economy | hull | 185 | Carrier [SR] | 0 | 0 | 0 |
| economy | hull | 187 | Scout | 0 | 0 | 0 |
| economy | hull | 190 | Fighter | 0 | 0 | 0 |
| economy | hull | 229 | SmallRip [SR] | 0 | 0 | 0 |
| economy | hull | 230 | Recon Drone | 0 | 0 | 0 |
| economy | hull | 209 | Corvette | 2 | 300 | 25000 |
| economy | hull | 213 | Attack Carrier [SR] | 2 | 300 | 25000 |
| economy | hull | 219 | Assault Ship | 3 | 480 | 40000 |
| economy | hull | 224 | Destroyer | 3 | 480 | 40000 |
| economy | hull | 247 | Frigate | 3 | 480 | 40000 |
| economy | hull | 195 | Bomber | 5 | 840 | 70000 |
| economy | hull | 226 | Devastator | 6 | 960 | 85000 |
| economy | hull | 227 | Cruiser | 6 | 960 | 85000 |
| economy | hull | 180 | Sm Enh Constructor | 29 | 4245 | 163510 |
| economy | hull | 183 | Lg Enh Constructor | 29 | 4245 | 163510 |
| economy | hull | 177 | Enh Miner | 30 | 4425 | 168510 |
| economy | hull | 186 | Enh Carrier [SR] | 47 | 7395 | 256010 |
| economy | hull | 188 | Adv Scout | 53 | 8415 | 301010 |
| economy | hull | 197 | Gunship | 59 | 9495 | 326010 |
| economy | hull | 196 | Hvy Bomber | 62 | 10035 | 343510 |
| economy | hull | 246 | Vanguard | 64 | 10335 | 363510 |
| economy | hull | 189 | Hvy Scout | 65 | 10515 | 373510 |
| economy | hull | 240 | Hvy Scout [DR] | 65 | 10515 | 373510 |
| economy | hull | 191 | Enh Fighter | 68 | 11055 | 391010 |
| economy | hull | 198 | Interceptor | 101 | 16875 | 498010 |
| economy | hull | 200 | Stealth Fighter | 119 | 19965 | 571510 |
| economy | hull | 192 | Adv Fighter | 123 | 20625 | 621510 |
| economy | hull | 241 | Adv Fighter [DR] | 123 | 20625 | 621510 |
| economy | hull | 205 | Fighter/Bomber | 131 | 22065 | 646510 |
| economy | hull | 206 | Stealth Bomber | 147 | 24825 | 725510 |
| economy | hull | 201 | Adv Stl Fighter | 149 | 25185 | 740510 |
| economy | hull | 242 | Adv Stl Fighter [DR] | 149 | 25185 | 740510 |
| economy | hull | 207 | Stealth Bomber [NG] | 163 | 27645 | 792010 |
| economy | hull | 202 | Troop Transport | 180 | 30645 | 882010 |
| economy | hull | 181 | Sm Adv Constructor | 181 | 30825 | 889510 |
| economy | hull | 184 | Lg Adv Constructor | 181 | 30825 | 889510 |
| economy | hull | 232 | Nova Bomb Layer [LS] | 181 | 30825 | 889510 |
| economy | hull | 178 | Adv Miner | 182 | 31005 | 897010 |
| economy | hull | 199 | Hvy Interceptor | 183 | 31185 | 907010 |
| economy | hull | 243 | Hvy Interceptor [DR] | 183 | 31185 | 907010 |
| economy | hull | 203 | Hvy Troop Transport | 186 | 31665 | 924010 |


## 5 — Omicron

First 40 steps:

| Step | Today research-first | Skirmish | Siege | Economy |
|---|---|---|---|---|
| 1 | develop 222 Weedcore 03 | station 703 Shipyard | develop 37 Bomber | station 703 Shipyard |
| 2 | develop 54 Retro Booster | develop 311 Upgrade Medium Class | station 711 Supremacy | develop 311 Upgrade Medium Class |
| 3 | develop 90 MRM Seeker 2 | develop 37 Bomber | develop 71 PW Gattling Gun 2 | develop 37 Bomber |
| 4 | develop 109 Med Shield 2 | station 711 Supremacy | develop 74 EW Disruptor 2 | station 711 Supremacy |
| 5 | develop 116 Lt Booster 1 | develop 312 Upgrade Heavy Class | develop 159 GA: Ship Hull 1 | develop 312 Upgrade Heavy Class |
| 6 | develop 131 Counter 2 | station 701 Shipyard (Dry) | develop 107 Sm Shield 2 | station 701 Shipyard (Dry) |
| 7 | develop 236 MRM Quickfire 2 | develop 306 PW A-B Omega 2 | develop 88 SRM Dumbfire 2 | develop 306 PW A-B Omega 2 |
| 8 | develop 58 Enh Constructor | develop 71 PW Gattling Gun 2 | develop 157 GA: Ship Sensors 1 | develop 71 PW Gattling Gun 2 |
| 9 | develop 64 Enh Miner | develop 74 EW Disruptor 2 | develop 161 GA: Ship Shield 1 | develop 74 EW Disruptor 2 |
| 10 | develop 87 ER Nanite 2 | develop 88 SRM Dumbfire 2 | develop 155 GA: Ripcord 1 | develop 157 GA: Ship Sensors 1 |
| 11 | develop 121 Hvy Sig Cloak 1 | develop 159 GA: Ship Hull 1 | develop 151 GA: Missile Track 1 | develop 159 GA: Ship Hull 1 |
| 12 | develop 309 EW Seismic Cannon | develop 107 Sm Shield 2 | develop 129 Mine Pack 1 | develop 107 Sm Shield 2 |
| 13 | develop 223 Enh Carrier Drone | develop 157 GA: Ship Sensors 1 | develop 113 Booster 2 | develop 88 SRM Dumbfire 2 |
| 14 | develop 37 Bomber | develop 161 GA: Ship Shield 1 | develop 81 PW Auto-Cannon 2 | develop 161 GA: Ship Shield 1 |
| 15 | develop 303 Upgrade Garrison | develop 81 PW Auto-Cannon 2 | develop 300 PW A-B Hammer 2 | develop 151 GA: Missile Track 1 |
| 16 | station 702 Outpost | develop 151 GA: Missile Track 1 | develop 123 EWS Probe 2 | develop 155 GA: Ripcord 1 |
| 17 | station 703 Shipyard | develop 155 GA: Ripcord 1 | develop 57 XRP Anti-Base 1 | develop 129 Mine Pack 1 |
| 18 | develop 83 PW SkyCap 2 | develop 300 PW A-B Hammer 2 | develop 302 Upgrade Supremacy | develop 113 Booster 2 |
| 19 | develop 111 Large Shield 2 | develop 113 Booster 2 | station 707 Supremacy (Adv) | develop 300 PW A-B Hammer 2 |
| 20 | develop 133 Hvy Counter 2 | develop 129 Mine Pack 1 | develop 70 PW Gattling Gun 3 | develop 81 PW Auto-Cannon 2 |
| 21 | develop 311 Upgrade Medium Class | develop 57 XRP Anti-Base 1 | develop 73 EW Disruptor 3 | develop 123 EWS Probe 2 |
| 22 | station 711 Supremacy | develop 123 EWS Probe 2 | develop 72 EW Galvonic Blaster | develop 57 XRP Anti-Base 1 |
| 23 | develop 151 GA: Missile Track 1 | develop 302 Upgrade Supremacy | develop 175 Fighter/Bomber | develop 302 Upgrade Supremacy |
| 24 | develop 155 GA: Ripcord 1 | station 707 Supremacy (Adv) | develop 160 GA: Ship Hull 2 | station 707 Supremacy (Adv) |
| 25 | develop 157 GA: Ship Sensors 1 | develop 70 PW Gattling Gun 3 | develop 106 Sm Shield 3 | develop 70 PW Gattling Gun 3 |
| 26 | develop 159 GA: Ship Hull 1 | develop 72 EW Galvonic Blaster | develop 89 SRM Dumbfire 3 | develop 72 EW Galvonic Blaster |
| 27 | develop 161 GA: Ship Shield 1 | develop 175 Fighter/Bomber | develop 158 GA: Ship Sensors 2 | develop 175 Fighter/Bomber |
| 28 | develop 53 Cruise Booster | develop 73 EW Disruptor 3 | develop 162 GA: Ship Shield 2 | develop 73 EW Disruptor 3 |
| 29 | develop 71 PW Gattling Gun 2 | develop 89 SRM Dumbfire 3 | develop 156 GA: Ripcord 2 | develop 160 GA: Ship Hull 2 |
| 30 | develop 74 EW Disruptor 2 | develop 160 GA: Ship Hull 2 | develop 152 GA: Missile Track 2 | develop 89 SRM Dumbfire 3 |
| 31 | develop 81 PW Auto-Cannon 2 | develop 106 Sm Shield 3 | develop 128 Mine Pack 2 | develop 158 GA: Ship Sensors 2 |
| 32 | develop 88 SRM Dumbfire 2 | develop 158 GA: Ship Sensors 2 | develop 112 Booster 3 | develop 106 Sm Shield 3 |
| 33 | develop 107 Sm Shield 2 | develop 162 GA: Ship Shield 2 | develop 80 PW Auto-Cannon 3 | develop 162 GA: Ship Shield 2 |
| 34 | develop 113 Booster 2 | develop 80 PW Auto-Cannon 3 | develop 301 PW A-B Hammer 3 | develop 152 GA: Missile Track 2 |
| 35 | develop 129 Mine Pack 1 | develop 152 GA: Missile Track 2 | develop 122 EWS Probe 3 | develop 156 GA: Ripcord 2 |
| 36 | develop 300 PW A-B Hammer 2 | develop 156 GA: Ripcord 2 | develop 193 XRP Anti-Base 2 | develop 128 Mine Pack 2 |
| 37 | develop 57 XRP Anti-Base 1 | develop 301 PW A-B Hammer 3 | develop 87 ER Nanite 2 | develop 112 Booster 3 |
| 38 | develop 123 EWS Probe 2 | develop 112 Booster 3 | station 747 TeleRefinery | develop 301 PW A-B Hammer 3 |
| 39 | develop 312 Upgrade Heavy Class | develop 128 Mine Pack 2 | drone 51 .TowerAC | develop 80 PW Auto-Cannon 3 |
| 40 | develop 302 Upgrade Supremacy | develop 193 XRP Anti-Base 2 | develop 222 Weedcore 03 | develop 193 XRP Anti-Base 2 |


Opening opportunity ranking (top six per profile):

| Profile | Target | Score | Route steps | Cost | Seconds | Exact-valid slice |
|---|---|---|---|---|---|---|
| skirmish | Upgrade Medium Class | 0.076 | 2 | 25000 | 600 | True |
| skirmish | Shipyard (Dry) | 0.055 | 5 | 65000 | 1500 | True |
| skirmish | PW A-B Omega 2 | 0.053 | 6 | 69000 | 1800 | True |
| skirmish | Bomber | 0.048 | 1 | 10000 | 300 | True |
| skirmish | Large Shield 3 | 0.048 | 7 | 75000 | 2100 | True |
| skirmish | PW SkyCap 3 | 0.045 | 7 | 75000 | 2100 | True |
| siege | Bomber | 0.12 | 1 | 10000 | 300 | True |
| siege | PW A-B Omega 2 | 0.085 | 6 | 69000 | 1800 | True |
| siege | Shipyard (Dry) | 0.082 | 5 | 65000 | 1500 | True |
| siege | Large Shield 3 | 0.072 | 7 | 75000 | 2100 | True |
| siege | Hvy Counter 3 | 0.068 | 7 | 75000 | 2100 | True |
| siege | PW SkyCap 3 | 0.067 | 7 | 75000 | 2100 | True |
| economy | Upgrade Medium Class | 0.049 | 2 | 25000 | 600 | True |
| economy | PW A-B Omega 2 | 0.038 | 6 | 69000 | 1800 | True |
| economy | Shipyard (Dry) | 0.037 | 5 | 65000 | 1500 | True |
| economy | Bomber | 0.036 | 1 | 10000 | 300 | True |
| economy | Large Shield 3 | 0.033 | 7 | 75000 | 2100 | True |
| economy | Hvy Counter 3 | 0.03 | 7 | 75000 | 2100 | True |


Key completions: step / serial seconds / cumulative cost:

| Key | Today | Skirmish | Siege | Economy |
|---|---|---|---|---|
| Bomber | 14 / 3925 / 57510 | 3 / 900 / 35000 | 1 / 300 / 10000 | 3 / 900 / 35000 |
| Outpost | 16 / 4315 / 72510 | 67 / 18595 / 302810 | 56 / 15295 / 213810 | 67 / 18595 / 302810 |
| Shipyard | 17 / 4615 / 82510 | 1 / 300 / 10000 | 76 / 21295 / 338810 | 1 / 300 / 10000 |
| Upgrade Medium Class | 21 / 5815 / 112510 | 2 / 600 / 25000 | 77 / 21595 / 353810 | 2 / 600 / 25000 |
| Supremacy | 22 / 6115 / 122510 | 4 / 1200 / 45000 | 2 / 600 / 20000 | 4 / 1200 / 45000 |
| GA: Ship Hull 1 | 26 / 7315 / 130510 | 11 / 3300 / 88500 | 5 / 1500 / 27000 | 11 / 3300 / 88000 |
| PW Gattling Gun 2 | 29 / 8215 / 137510 | 8 / 2400 / 81500 | 3 / 900 / 22500 | 8 / 2400 / 81500 |
| EW Disruptor 2 | 30 / 8515 / 140010 | 9 / 2700 / 84000 | 4 / 1200 / 25000 | 9 / 2700 / 84000 |
| Upgrade Supremacy | 40 / 11515 / 215010 | 23 / 6900 / 149000 | 18 / 5400 / 90000 | 23 / 6900 / 149000 |
| Shipyard (Dry) | 41 / 11815 / 225010 | 6 / 1800 / 75000 | 79 / 22195 / 383810 | 6 / 1800 / 75000 |
| Supremacy (Adv) | 46 / 13315 / 254010 | 24 / 7200 / 159000 | 19 / 5700 / 100000 | 24 / 7200 / 159000 |
| Expansion | 65 / 19015 / 316510 | 87 / 24595 / 427810 | 87 / 24595 / 427810 | 87 / 24595 / 427810 |
| GA: PW&EW Damage 1 | 67 / 19615 / 320510 | 89 / 25195 / 434810 | 89 / 25195 / 434810 | 92 / 26095 / 440810 |
| Expansion (Adv) | 80 / 23515 / 397510 | 131 / 37585 / 664810 | 131 / 37585 / 664810 | 131 / 37585 / 664810 |
| Tactical | 97 / 28615 / 480010 | 101 / 28795 / 478810 | 101 / 28795 / 478810 | 101 / 28795 / 478810 |
| Tactical (Adv) | 111 / 32815 / 567010 | 116 / 33085 / 567310 | 116 / 33085 / 567310 | 116 / 33085 / 567310 |
| Garrison (Str) | 125 / 37015 / 644510 | 70 / 19495 / 340310 | 59 / 16195 / 251310 | 70 / 19495 / 340310 |
| Hvy Bomber | 139 / 41215 / 702010 | 71 / 19795 / 350310 | 60 / 16495 / 261310 | 71 / 19795 / 350310 |
| TeleRefinery | 146 / 42265 / 733310 | 43 / 12690 / 217800 | 38 / 11190 / 158800 | 43 / 12690 / 217800 |


All hull unlocks (including autonomous hulls), for reproducible timing:

| Plan | Kind | ID | Name | Step | Seconds | Funding |
|---|---|---|---|---|---|---|
| today | hull | 710 | Scout | 0 | 0 | 0 |
| today | hull | 715 | Fighter | 0 | 0 | 0 |
| today | hull | 719 | Lg Std Constructor | 0 | 0 | 0 |
| today | hull | 725 | Carrier [SR] | 0 | 0 | 0 |
| today | hull | 738 | Miner | 0 | 0 | 0 |
| today | hull | 744 | Lifepod | 0 | 0 | 0 |
| today | hull | 770 | Sm Std Constructor | 0 | 0 | 0 |
| today | hull | 717 | Lg Enh Constructor | 8 | 2125 | 20010 |
| today | hull | 771 | Sm Enh Constructor | 8 | 2125 | 20010 |
| today | hull | 737 | Enh Miner | 9 | 2425 | 25010 |
| today | hull | 726 | Enh Carrier [SR] | 13 | 3625 | 47510 |
| today | hull | 704 | Bomber | 14 | 3925 | 57510 |
| today | hull | 703 | Corvette | 21 | 5815 | 112510 |
| today | hull | 727 | Attack Carrier [SR] | 21 | 5815 | 112510 |
| today | hull | 787 | Assault Ship | 21 | 5815 | 112510 |
| today | hull | 791 | Devastator | 21 | 5815 | 112510 |
| today | hull | 714 | Enh Fighter | 22 | 6115 | 122510 |
| today | hull | 722 | Cruiser | 41 | 11815 | 225010 |
| today | hull | 792 | Adv Devastator | 41 | 11815 | 225010 |
| today | hull | 788 | Adv Fighter | 46 | 13315 | 254010 |
| today | hull | 732 | Fighter/Bomber | 63 | 18415 | 301510 |
| today | hull | 712 | Interceptor | 65 | 19015 | 316510 |
| today | hull | 781 | Hvy Interceptor | 80 | 23515 | 397510 |
| today | hull | 795 | Troop Transport | 93 | 27415 | 445010 |
| today | hull | 716 | Lg Adv Constructor | 94 | 27715 | 452510 |
| today | hull | 772 | Sm Adv Constructor | 94 | 27715 | 452510 |
| today | hull | 736 | Adv Miner | 95 | 28015 | 460010 |
| today | hull | 793 | Hvy Troop Transport | 96 | 28315 | 470010 |
| today | hull | 711 | Stealth Fighter | 97 | 28615 | 480010 |
| today | hull | 786 | Adv Stl Fighter | 111 | 32815 | 567010 |
| today | hull | 720 | Stealth Bomber | 124 | 36715 | 624510 |
| today | hull | 734 | Stealth Bomber [NG] | 124 | 36715 | 624510 |
| today | hull | 785 | Adv Scout | 138 | 40915 | 692010 |
| today | hull | 789 | Hvy Bomber | 139 | 41215 | 702010 |
| today | hull | 790 | Gunship | 140 | 41515 | 712010 |
| today | hull | 733 | Hvy Scout | 141 | 41815 | 722010 |
| skirmish | hull | 710 | Scout | 0 | 0 | 0 |
| skirmish | hull | 715 | Fighter | 0 | 0 | 0 |
| skirmish | hull | 719 | Lg Std Constructor | 0 | 0 | 0 |
| skirmish | hull | 725 | Carrier [SR] | 0 | 0 | 0 |
| skirmish | hull | 738 | Miner | 0 | 0 | 0 |
| skirmish | hull | 744 | Lifepod | 0 | 0 | 0 |
| skirmish | hull | 770 | Sm Std Constructor | 0 | 0 | 0 |
| skirmish | hull | 703 | Corvette | 2 | 600 | 25000 |
| skirmish | hull | 727 | Attack Carrier [SR] | 2 | 600 | 25000 |
| skirmish | hull | 787 | Assault Ship | 2 | 600 | 25000 |
| skirmish | hull | 791 | Devastator | 2 | 600 | 25000 |
| skirmish | hull | 704 | Bomber | 3 | 900 | 35000 |
| skirmish | hull | 714 | Enh Fighter | 4 | 1200 | 45000 |
| skirmish | hull | 722 | Cruiser | 6 | 1800 | 75000 |
| skirmish | hull | 792 | Adv Devastator | 6 | 1800 | 75000 |
| skirmish | hull | 788 | Adv Fighter | 24 | 7200 | 159000 |
| skirmish | hull | 732 | Fighter/Bomber | 27 | 8100 | 169000 |
| skirmish | hull | 717 | Lg Enh Constructor | 57 | 15805 | 252810 |
| skirmish | hull | 771 | Sm Enh Constructor | 57 | 15805 | 252810 |
| skirmish | hull | 737 | Enh Miner | 58 | 16105 | 257810 |
| skirmish | hull | 726 | Enh Carrier [SR] | 68 | 18895 | 310310 |
| skirmish | hull | 789 | Hvy Bomber | 71 | 19795 | 350310 |
| skirmish | hull | 785 | Adv Scout | 72 | 20095 | 357810 |
| skirmish | hull | 790 | Gunship | 73 | 20395 | 367810 |
| skirmish | hull | 733 | Hvy Scout | 74 | 20695 | 377810 |
| skirmish | hull | 712 | Interceptor | 87 | 24595 | 427810 |
| skirmish | hull | 711 | Stealth Fighter | 101 | 28795 | 478810 |
| skirmish | hull | 786 | Adv Stl Fighter | 116 | 33085 | 567310 |
| skirmish | hull | 720 | Stealth Bomber | 117 | 33385 | 574810 |
| skirmish | hull | 734 | Stealth Bomber [NG] | 123 | 35185 | 599810 |
| skirmish | hull | 781 | Hvy Interceptor | 131 | 37585 | 664810 |
| skirmish | hull | 795 | Troop Transport | 134 | 38485 | 679810 |
| skirmish | hull | 793 | Hvy Troop Transport | 135 | 38785 | 689810 |
| skirmish | hull | 736 | Adv Miner | 145 | 41785 | 724810 |
| skirmish | hull | 716 | Lg Adv Constructor | 146 | 42085 | 732310 |
| skirmish | hull | 772 | Sm Adv Constructor | 146 | 42085 | 732310 |
| siege | hull | 710 | Scout | 0 | 0 | 0 |
| siege | hull | 715 | Fighter | 0 | 0 | 0 |
| siege | hull | 719 | Lg Std Constructor | 0 | 0 | 0 |
| siege | hull | 725 | Carrier [SR] | 0 | 0 | 0 |
| siege | hull | 738 | Miner | 0 | 0 | 0 |
| siege | hull | 744 | Lifepod | 0 | 0 | 0 |
| siege | hull | 770 | Sm Std Constructor | 0 | 0 | 0 |
| siege | hull | 704 | Bomber | 1 | 300 | 10000 |
| siege | hull | 714 | Enh Fighter | 2 | 600 | 20000 |
| siege | hull | 788 | Adv Fighter | 19 | 5700 | 100000 |
| siege | hull | 732 | Fighter/Bomber | 23 | 6900 | 112500 |
| siege | hull | 717 | Lg Enh Constructor | 52 | 14305 | 193810 |
| siege | hull | 771 | Sm Enh Constructor | 52 | 14305 | 193810 |
| siege | hull | 737 | Enh Miner | 53 | 14605 | 198810 |
| siege | hull | 726 | Enh Carrier [SR] | 57 | 15595 | 221310 |
| siege | hull | 789 | Hvy Bomber | 60 | 16495 | 261310 |
| siege | hull | 785 | Adv Scout | 61 | 16795 | 268810 |
| siege | hull | 790 | Gunship | 62 | 17095 | 278810 |
| siege | hull | 733 | Hvy Scout | 63 | 17395 | 288810 |
| siege | hull | 703 | Corvette | 76 | 21295 | 338810 |
| siege | hull | 727 | Attack Carrier [SR] | 76 | 21295 | 338810 |
| siege | hull | 787 | Assault Ship | 77 | 21595 | 353810 |
| siege | hull | 791 | Devastator | 77 | 21595 | 353810 |
| siege | hull | 722 | Cruiser | 79 | 22195 | 383810 |
| siege | hull | 792 | Adv Devastator | 79 | 22195 | 383810 |
| siege | hull | 712 | Interceptor | 87 | 24595 | 427810 |
| siege | hull | 711 | Stealth Fighter | 101 | 28795 | 478810 |
| siege | hull | 786 | Adv Stl Fighter | 116 | 33085 | 567310 |
| siege | hull | 720 | Stealth Bomber | 117 | 33385 | 574810 |
| siege | hull | 734 | Stealth Bomber [NG] | 123 | 35185 | 599810 |
| siege | hull | 781 | Hvy Interceptor | 131 | 37585 | 664810 |
| siege | hull | 795 | Troop Transport | 132 | 37885 | 672310 |
| siege | hull | 793 | Hvy Troop Transport | 133 | 38185 | 682310 |
| siege | hull | 736 | Adv Miner | 146 | 42085 | 729810 |
| siege | hull | 716 | Lg Adv Constructor | 147 | 42385 | 737310 |
| siege | hull | 772 | Sm Adv Constructor | 147 | 42385 | 737310 |
| economy | hull | 710 | Scout | 0 | 0 | 0 |
| economy | hull | 715 | Fighter | 0 | 0 | 0 |
| economy | hull | 719 | Lg Std Constructor | 0 | 0 | 0 |
| economy | hull | 725 | Carrier [SR] | 0 | 0 | 0 |
| economy | hull | 738 | Miner | 0 | 0 | 0 |
| economy | hull | 744 | Lifepod | 0 | 0 | 0 |
| economy | hull | 770 | Sm Std Constructor | 0 | 0 | 0 |
| economy | hull | 703 | Corvette | 2 | 600 | 25000 |
| economy | hull | 727 | Attack Carrier [SR] | 2 | 600 | 25000 |
| economy | hull | 787 | Assault Ship | 2 | 600 | 25000 |
| economy | hull | 791 | Devastator | 2 | 600 | 25000 |
| economy | hull | 704 | Bomber | 3 | 900 | 35000 |
| economy | hull | 714 | Enh Fighter | 4 | 1200 | 45000 |
| economy | hull | 722 | Cruiser | 6 | 1800 | 75000 |
| economy | hull | 792 | Adv Devastator | 6 | 1800 | 75000 |
| economy | hull | 788 | Adv Fighter | 24 | 7200 | 159000 |
| economy | hull | 732 | Fighter/Bomber | 27 | 8100 | 169000 |
| economy | hull | 717 | Lg Enh Constructor | 57 | 15805 | 252810 |
| economy | hull | 771 | Sm Enh Constructor | 57 | 15805 | 252810 |
| economy | hull | 737 | Enh Miner | 58 | 16105 | 257810 |
| economy | hull | 726 | Enh Carrier [SR] | 68 | 18895 | 310310 |
| economy | hull | 789 | Hvy Bomber | 71 | 19795 | 350310 |
| economy | hull | 785 | Adv Scout | 72 | 20095 | 357810 |
| economy | hull | 733 | Hvy Scout | 73 | 20395 | 367810 |
| economy | hull | 790 | Gunship | 74 | 20695 | 377810 |
| economy | hull | 712 | Interceptor | 87 | 24595 | 427810 |
| economy | hull | 711 | Stealth Fighter | 101 | 28795 | 478810 |
| economy | hull | 786 | Adv Stl Fighter | 116 | 33085 | 567310 |
| economy | hull | 720 | Stealth Bomber | 117 | 33385 | 574810 |
| economy | hull | 734 | Stealth Bomber [NG] | 122 | 34885 | 594810 |
| economy | hull | 781 | Hvy Interceptor | 131 | 37585 | 664810 |
| economy | hull | 795 | Troop Transport | 136 | 39085 | 684810 |
| economy | hull | 793 | Hvy Troop Transport | 137 | 39385 | 694810 |
| economy | hull | 716 | Lg Adv Constructor | 146 | 42085 | 729810 |
| economy | hull | 772 | Sm Adv Constructor | 146 | 42085 | 729810 |
| economy | hull | 736 | Adv Miner | 147 | 42385 | 737310 |

