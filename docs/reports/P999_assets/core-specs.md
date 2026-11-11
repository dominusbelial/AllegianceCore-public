# P999 — reproducible PCore001 specification census

Core version 990051329; SHA-256 `F73AA7BBCE7414140F6691366F8829EA58829727660A8A86C114B29A83A55DE8`. All 10 loaded civilizations are below the lobby's special-civ cutoff; none is omitted.

Raw hull values are separated from civilization GA multipliers. Shield is a mounted part, not a hull field. Initial shield capacity below is the largest compatible initially buyable shield multiplied by the civ GA; mounted masses and research are excluded. All human hulls have the engine's five cargo mounts (-5 through -1); magazine/dispenser/chaff capacities are distinct size budgets, not cargo slots. Equipment masks are listed in engine order (chaff, gun, magazine, dispenser, shield, cloak, pack, afterburner).

Times are serial bucket seconds at researchTimeScale=1 with unlimited funding and legal rocks; station travel/building effects, losses, and concurrent funding are excluded. These are not measured match times. Base tech bit numbers use the engine's MSB-first bit convention.

## 1 — Dreghklar

Starting tech bits: 6,23,31,34,37,45,59,73,93,98,102,107,108,110,126,127,144,145,146,154,165,168,169,170,187,208,209,355. All four enabled path-switch bits (1,2,3,4) are added by ResearchSettings. No-development bits: 5,6,7,8,13,14,15,17,18,23,28,32,34,36,37,38,45,46,51,52,57,59,72,87,88,89,98,100,102,103,108,110,111,114,116,125,126,130,132,144,145,146,154,157,163,165,166,168,170,177,180,182,183,186,187,190,194,195,196,197,202,206,207,210,211,212,213,216,220,224,225,226,243,355.

Home 26 Garrison: armor 20000, shield 10000, income 0; grants [17], local [99,239,241,255]. Base payday fraction +0.125; starting-money addition +0.15; pod 33. Negative additions are retained.

Nonidentity base GA multipliers:

| Index | Attribute | Factor |
|---|---|---|
| 0 | speed | 1.2 |
| 2 | turn rate | 1.25 |
| 3 | turn torque | 1.35 |
| 8 | ship armor | 0.9 |
| 11 | scan | 0.9 |
| 12 | signature resistance | 0.95 |
| 13 | energy | 1.15 |
| 16 | missile turn | 1.25 |
| 17 | mining rate | 0.65 |
| 18 | mining yield | 1.35 |
| 19 | mining capacity | 0.95 |
| 22 | missile damage | 1.1 |
| 23 | development cost | 1.25 |
| 24 | development time | 0.5 |

Starting hulls (including autonomous types):

| ID | Hull | Human | HP raw/effective | Shield max initial | Speed raw/effective | Thrust | Mass | Signature | Guns fixed/total | Ammo | Magazine/dispenser/chaff |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 3 | Miner | False | 1056/950.4 | 300 | 100/120 | 1000 | 100 | 1.5 | 0/0 | 0 | 0/0/0 |
| 6 | Carrier [SR] | False | 2000/1800 | 0 | 50/60 | 500000 | 50000 | 2 | 0/0 | 1600 | 60/8/8 |
| 9 | Lg Std Constructor | False | 2880/2592 | 300 | 60/72 | 500000 | 50000 | 1.5 | 0/0 | 0 | 0/0/0 |
| 12 | Sm Std Constructor | False | 1440/1296 | 300 | 60/72 | 2000 | 200 | 1.5 | 0/0 | 0 | 0/0/0 |
| 14 | Scout | True | 200/180 | 150 | 160/192 | 1200 | 40 | 0.75 | 1/1 | 800 | 10/2/4 |
| 21 | Lt Interceptor | True | 600/540 | 0 | 75/90 | 400 | 20 | 1 | 2/2 | 450 | 0/0/4 |
| 33 | Lifepod | False | 25/22.5 | 0 | 60/72 | 150 | 10 | 0.5 | 0/0 | 0 | 0/0/0 |

Starting human hardpoints/equipment and purchasable weapons:

| Hull | Hardpoints (mask; fixed) | Equipment masks | Initial fitting guns | Initial fitting afterburners |
|---|---|---|---|---|
| 14 | 3;True | 1,0,129,7169,1,0,0,16 | 33 PW Gat Gun 1; 91 ER Nanite 1; 92 EW Disruptor 1 |  |
| 21 | 24;True; 24;True | 1,0,0,0,0,0,0,33 | 120 PW Mini-Gun 1 | 125 Afterburner 1 |

Reachable human hulls (raw; cumulative baseline unlock time is in the plan appendix):

| ID | Hull | HP | Speed | Thrust/mass | Signature | Fixed/total | Magazine/cargo slots | Equipment masks |
|---|---|---|---|---|---|---|---|---|
| 13 | Adv Scout | 225 | 180 | 1200/40 | 0.5 | 1/1 | 15/5 | 1,0,129,7169,1,0,0,16 |
| 14 | Scout | 200 | 160 | 1200/40 | 0.75 | 1/1 | 10/5 | 1,0,129,7169,1,0,0,16 |
| 15 | Adv Fighter | 450 | 120 | 750/30 | 1 | 3/3 | 25/5 | 1,0,129,1024,1,0,0,15 |
| 16 | Enh Fighter | 350 | 100 | 750/30 | 1 | 3/3 | 20/5 | 1,0,129,1024,1,0,0,7 |
| 17 | Adv Stl Fighter | 200 | 160 | 2160/60 | 0.5 | 2/2 | 30/5 | 1,0,259,4096,1,3,0,0 |
| 18 | Stealth Fighter | 150 | 140 | 2160/60 | 0.5 | 2/2 | 20/5 | 1,0,259,4096,1,3,0,0 |
| 19 | Hvy Interceptor | 750 | 90 | 400/20 | 1 | 3/3 | 0/5 | 1,0,0,2,0,0,0,33 |
| 20 | Interceptor | 700 | 80 | 400/20 | 1 | 2/2 | 0/5 | 1,0,0,2,0,0,0,33 |
| 21 | Lt Interceptor | 600 | 75 | 400/20 | 1 | 2/2 | 0/5 | 1,0,0,0,0,0,0,33 |
| 22 | Gunship | 800 | 80 | 1000/50 | 2 | 1/3 | 40/5 | 1,0,259,0,2,0,0,0 |
| 23 | Stealth Bomber | 150 | 80 | 2160/120 | 1 | 2/2 | 30/5 | 1,0,262,0,1,3,0,0 |
| 24 | Hvy Bomber | 1200 | 75 | 750/50 | 2 | 2/4 | 75/5 | 1,0,1830,0,2,0,0,0 |
| 25 | Bomber | 800 | 60 | 750/50 | 2 | 2/4 | 60/5 | 1,0,294,0,2,0,0,0 |
| 26 | Hvy Troop Transport | 975 | 75 | 1350/90 | 1 | 0/0 | 45/5 | 1,0,32,0,2,0,0,0 |
| 27 | Troop Transport | 750 | 60 | 675/45 | 2 | 0/0 | 15/5 | 1,0,32,0,2,0,0,0 |
| 28 | Destroyer | 1500 | 100 | 25000/2500 | 3 | 1/1 | 120/5 | 2,0,529,0,4,0,0,0 |
| 29 | Devastator | 3500 | 75 | 25000/2500 | 3.5 | 1/5 | 0/5 | 2,0,0,0,4,0,0,0 |
| 30 | Cruiser | 4000 | 70 | 50000/5000 | 4 | 1/5 | 160/5 | 2,0,520,0,4,0,0,0 |
| 31 | Frigate | 3500 | 75 | 25000/2500 | 3 | 1/4 | 120/5 | 2,0,520,0,4,2,0,0 |
| 32 | Assault Ship | 3000 | 120 | 37500/2500 | 1 | 1/3 | 0/5 | 2,0,512,3073,4,2,0,0 |
| 50 | Corvette | 2000 | 80 | 12500/625 | 2.5 | 2/4 | 80/5 | 2,0,771,2064,4,0,0,0 |
| 83 | Attack Carrier [SR] | 3000 | 75 | 50000/5000 | 1.5 | 1/3 | 60/5 | 2,0,2,3073,4,0,0,0 |
| 90 | Hvy Scout | 250 | 180 | 1440/40 | 0.5 | 1/1 | 20/5 | 1,0,4225,7169,1,0,0,16 |
| 153 | Stealth Bomber [NG] | 150 | 80 | 2160/120 | 1.5 | 2/2 | 30/5 | 1,0,64,0,1,1,0,0 |
| 161 | Fighter/Bomber | 450 | 70 | 1000/40 | 1.5 | 3/3 | 25/5 | 1,0,133,1024,1,0,0,5 |

Early and late weapons (side-buyable; a fitting local hull/mount is still required). Listed DPS is raw direct projectile power / burst time, before damage matrix and GA; missile cycle is ready + lock, not an observed launch interval:

| Part | Weapon | Initially buyable | Raw DPS / missile cycle | Damage type | Energy/ammo per shot |
|---|---|---|---|---|---|
| 5 | LRM Aleph Res 1 | False | 0.25 | 6 | magazine |
| 10 | XRM Cruise 1 | False | 0.25 | 8 | magazine |
| 11 | XRM Cruise 2 | False | 0.25 | 8 | magazine |
| 18 | SRM Seismic | False | 0.25 | 9 | magazine |
| 20 | LRM Killer 1 | False | 0.25 | 3 | magazine |
| 30 | LRM Killer 2 | False | 0.25 | 3 | magazine |
| 31 | LRM Hunter 3 | False | 1.25 | 4 | magazine |
| 32 | XRM Hunter Klr 3 | False | 1.75 | 4 | magazine |
| 33 | PW Gat Gun 1 | True | 32 | 2 | 0/1 |
| 50 | EW Utl Can 1 | True | 27.545 | 1 | 20/0 |
| 51 | EW Utl Can 2 | False | 31.138 | 1 | 20/0 |
| 52 | EW Utl Can 3 | False | 38.922 | 1 | 20/0 |
| 60 | EW Sniper 1 | False | 28 | 4 | 8/0 |
| 61 | EW Sniper 2 | False | 36 | 4 | 7/0 |
| 67 | EW SkyRip | False | 149.254 | 15 | 8/0 |
| 71 | PW Gat Gun 2 | False | 40 | 2 | 0/1 |
| 72 | PW Gat Gun 3 | False | 50 | 2 | 0/1 |
| 87 | PW Mini-AC 1 | True | 90.361 | 11 | 0/1 |
| 88 | PW Mini-AC 2 | False | 99.398 | 11 | 0/1 |
| 89 | PW Mini-AC 3 | False | 109.337 | 11 | 0/1 |
| 90 | LRM Aleph Res 2 | False | 0.25 | 6 | magazine |
| 91 | ER Nanite 1 | True | -90 | 10 | 30/0 |
| 92 | EW Disruptor 1 | True | 12.8 | 3 | 7.5/0 |
| 93 | EW Disruptor 2 | False | 16 | 3 | 7.5/0 |
| 94 | EW Disruptor 3 | False | 20 | 3 | 7.5/0 |
| 95 | LRM Killer Swarm 1 | False | 0.25 | 3 | magazine |
| 96 | LRM Killer Swarm 2 | False | 0.25 | 3 | magazine |
| 97 | LRM Aleph Res 3 | False | 0.25 | 6 | magazine |
| 98 | MRM Seek 1 QL | False | 2.25 | 4 | magazine |
| 99 | MRM Seek 2 QL | False | 1.75 | 4 | magazine |
| 100 | PW EMP Can 1 | False | 36 | 13 | 0/2 |
| 101 | PW EMP Can 2 | False | 45 | 13 | 0/2 |
| 102 | MRM Seek 3 QL | False | 1.25 | 4 | magazine |
| 103 | MRM Quickfire 1 | True | 0.5 | 14 | magazine |
| 104 | MRM Quickfire 2 | False | 0.45 | 14 | magazine |
| 105 | MRM Quickfire 3 | False | 0.4 | 14 | magazine |
| 106 | SRM Dumb 1 QL | False | 0.25 | 4 | magazine |
| 107 | SRM Dumb 2 QL | False | 0.25 | 4 | magazine |
| 108 | SRM Dumb 3 QL | False | 0.25 | 4 | magazine |
| 111 | EW Galv Blstr | False | 28 | 7 | 12/0 |
| 115 | SRM Nerve Gas | False | 0.25 | 12 | magazine |
| 120 | PW Mini-Gun 1 | True | 50 | 5 | 0/1 |
| 121 | PW Mini-Gun 2 | False | 64.103 | 5 | 0/1 |
| 122 | PW Mini-Gun 3 | False | 78.125 | 5 | 0/1 |
| 126 | XRM Anti-Base 1 | False | 0.25 | 8 | magazine |
| 127 | XRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 130 | PW AutoCan 1 | True | 117.952 | 11 | 0/1 |
| 131 | PW AutoCan 2 | False | 149.277 | 11 | 0/1 |
| 132 | PW AutoCan 3 | False | 186.627 | 11 | 0/1 |
| 140 | PW SkyCap 1 | True | 60 | 11 | 0/4 |
| 141 | PW SkyCap 2 | False | 75 | 11 | 0/4 |
| 142 | PW SkyCap 3 | False | 93.75 | 11 | 0/4 |
| 150 | SRM Dumbfire 1 | True | 0.25 | 4 | magazine |
| 151 | SRM Dumbfire 2 | False | 0.25 | 4 | magazine |
| 152 | SRM Dumbfire 3 | False | 0.25 | 4 | magazine |
| 153 | MRM Seeker 1 | True | 2.25 | 4 | magazine |
| 154 | MRM Seeker 2 | False | 1.75 | 4 | magazine |
| 155 | LRM Hunter 1 | True | 2.25 | 4 | magazine |
| 156 | MRM Seeker 3 | False | 1.25 | 4 | magazine |
| 157 | SRM Anti-Base 1 | True | 0.25 | 8 | magazine |
| 158 | SRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 159 | LRM Hunter 2 | False | 1.75 | 4 | magazine |
| 160 | SRM Tactical Nuke 1 | False | 0.25 | 8 | magazine |
| 161 | SRM Tactical Nuke 2 | False | 0.25 | 8 | magazine |
| 162 | MRM Seismic | False | 0.25 | 9 | magazine |
| 163 | XRM Hunter Klr 1 | False | 2.25 | 4 | magazine |
| 164 | XRM Hunter Klr 2 | False | 2.25 | 4 | magazine |
| 190 | ER Nanite 2 | False | -120 | 10 | 30/0 |
| 192 | SRM EMP | False | 0.25 | 12 | magazine |
| 193 | XRM Torpedo 1 | False | 0.25 | 4 | magazine |
| 194 | XRM Torpedo 2 | False | 0.25 | 4 | magazine |
| 223 | ER Nanite 3 | False | -150 | 10 | 30/0 |
| 601 | SRM Anti-Base 3 | False | 0.25 | 8 | magazine |

Station branches and baseline serial position (constructor bucket only, excluding travel):

| Station | Required/effect/local bits | Direct cost/seconds | Baseline step/seconds/cost |
|---|---|---|---|
| 4 | Outpost [23,255]/[12]/[99,255] | 6250/90 | 17/995/103137 |
| 19 | Supremacy [4,23,239]/[13,175,216,225]/[99,255] | 12500/120 | 18/1115/115637 |
| 6 | Supremacy (Adv) [4,23,179,239]/[13,14,174,175,216,217,225,292]/[99,255] | 12500/120 | 37/2315/221887 |
| 20 | Expansion [2,23,239]/[8,175,216,226]/[99,255] | 12500/120 | 57/3575/312512 |
| 16 | Expansion (Adv) [2,23,119,239]/[8,15,174,175,216,217,226,290]/[99,255] | 12500/120 | 74/4655/423137 |
| 24 | Tactical [3,23,239]/[216,224,243]/[99,255] | 12500/120 | 92/5795/538762 |
| 17 | Tactical (Adv) [3,23,178,239]/[7,216,217,224,243,291]/[99,255] | 12500/120 | 107/6755/650637 |
| 5 | Shipyard [1,23,216,239]/[11,227]/[99,240,255] | 18750/120 | 122/7715/753762 |
| 1 | Shipyard (Dry) [1,23,141,216,239]/[11,185,227]/[99,240,255] | 18750/120 | 134/8495/878762 |
| 18 | Garrison (Str) [23,183,255]/[17,18,293]/[92,99,239,255] | 25000/120 | 143/9095/953762 |
| 25 | Refinery [23,255]/[]/[] | 3750/90 | 164/10505/1061887 |
| 28 | Teleport [23,255]/[]/[] | 3750/90 | 165/10595/1065637 |
| 27 | SpecMine (He) [23,239,361]/[]/[] | 7000/90 | 167/10805/1076637 |


Reachable GA developments (each completed factor multiplies the base GA; bucket cost/time were frozen at creation):

| Development | Cost | Seconds | Factors |
|---|---|---|---|
| GA: Ship Acceleration 1 | 2500 | 60 | thrust x1.1 |
| GA: Ship Acceleration 2 | 3125 | 60 | thrust x1.1 |
| GA: Ship Agility 1 | 2500 | 60 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Agility 2 | 3125 | 60 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Energy 1 | 2500 | 60 | energy x1.1 |
| GA: Ship Energy 2 | 3125 | 60 | energy x1.1 |
| GA: EW Range 1 | 2500 | 60 | energy lifespan x1.1 |
| GA: EW Range 2 | 3125 | 60 | energy lifespan x1.1 |
| GA: PW&EW Damage 1 | 2500 | 60 | gun damage x1.1 |
| GA: PW&EW Damage 2 | 3125 | 60 | gun damage x1.1 |
| GA: Faster He3 1 | 2500 | 60 | mining rate x1.1 |
| GA: Faster He3 2 | 3125 | 60 | mining rate x1.1 |
| GA: He3 Yield 1 | 2500 | 60 | mining yield x1.1 |
| GA: He3 Yield 2 | 3125 | 60 | mining yield x1.1 |
| GA: Missile Damage 1 | 2500 | 60 | missile damage x1.1 |
| GA: Missile Damage 2 | 3125 | 60 | missile damage x1.1 |
| GA: Missile Track 1 | 2500 | 60 | missile turn x1.1 |
| GA: Missile Track 2 | 3125 | 60 | missile turn x1.1 |
| GA: PW Range 1 | 2500 | 60 | ammo speed x1.1 |
| GA: PW Range 2 | 3125 | 60 | ammo speed x1.1 |
| GA: Ripcord 1 | 2500 | 60 | ripcord speed x1.15 |
| GA: Ripcord 2 | 3125 | 60 | ripcord speed x1.15 |
| GA: Ship Sensors 1 | 2500 | 60 | scan x1.1 |
| GA: Ship Sensors 2 | 3125 | 60 | scan x1.1 |
| GA: Ship Hull 1 | 2500 | 60 | ship armor x1.1 |
| GA: Ship Hull 2 | 3125 | 60 | ship armor x1.1 |
| GA: Ship Shield 1 | 2500 | 60 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Shield 2 | 3125 | 60 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Signature 1 | 2500 | 60 | signature resistance x1.1 |
| GA: Ship Signature 2 | 3125 | 60 | signature resistance x1.1 |
| GA: Ship Speed 1 | 5000 | 60 | speed x1.1 |
| GA: Ship Speed 2 | 6250 | 60 | speed x1.1 |
| GA: Station Hull 1 | 2500 | 60 | station armor x1.1; station armor regen x1.1 |
| GA: Station Hull 2 | 3125 | 60 | station armor x1.1; station armor regen x1.1 |
| GA: Station Shield 1 | 2500 | 60 | station shield x1.1; station shield regen x1.1 |
| GA: Station Shield 2 | 3125 | 60 | station shield x1.1; station shield regen x1.1 |


## 2 — Technoflux

Starting tech bits: 6,19,31,34,37,45,59,73,93,98,102,107,108,110,127,144,145,146,154,168,169,171,187,208,209,320,321,328,329,330,331,332,333,334,335,339,340,341,342,343,357. All four enabled path-switch bits (1,2,3,4) are added by ResearchSettings. No-development bits: 5,6,7,8,13,14,15,17,18,19,28,32,34,36,37,38,45,46,51,52,57,59,72,87,88,89,98,100,102,103,108,110,111,114,116,130,132,144,145,146,154,157,163,166,168,171,177,180,182,183,186,187,190,194,195,196,197,202,206,207,210,211,212,213,216,220,224,225,226,243,321,328,329,331,335,339,340,342,348,357.

Home 40 Garrison: armor 20000, shield 10000, income 0; grants [17], local [99,239,241,255]. Base payday fraction +-0.15; starting-money addition +-0.2; pod 64. Negative additions are retained.

Nonidentity base GA multipliers:

| Index | Attribute | Factor |
|---|---|---|
| 0 | speed | 0.9 |
| 2 | turn rate | 1.1 |
| 3 | turn torque | 1.2 |
| 4 | station armor | 1.1 |
| 6 | station shield | 1.1 |
| 10 | ship shield regen | 1.3 |
| 11 | scan | 1.1 |
| 15 | energy lifespan | 1.2 |
| 17 | mining rate | 2 |
| 19 | mining capacity | 0.55 |
| 20 | ripcord speed | 1.3 |
| 23 | development cost | 0.75 |
| 24 | development time | 3 |

Starting hulls (including autonomous types):

| ID | Hull | Human | HP raw/effective | Shield max initial | Speed raw/effective | Thrust | Mass | Signature | Guns fixed/total | Ammo | Magazine/dispenser/chaff |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 41 | Miner [SR] | False | 1056/1056 | 300 | 100/90 | 1000 | 80 | 1.5 | 0/0 | 0 | 0/0/0 |
| 45 | Lg Std Constructor | False | 2880/2880 | 300 | 60/54 | 500000 | 40000 | 1.5 | 0/0 | 0 | 0/0/0 |
| 48 | Sm Std Constructor | False | 1440/1440 | 300 | 60/54 | 2000 | 160 | 1.5 | 0/0 | 0 | 0/0/0 |
| 51 | Scout | True | 200/200 | 150 | 160/144 | 1200 | 32 | 0.75 | 2/2 | 600 | 0/2/4 |
| 64 | Lifepod | False | 25/25 | 0 | 60/54 | 150 | 8 | 0.5 | 0/0 | 0 | 0/0/0 |
| 65 | Carrier [SR] | False | 2000/2000 | 0 | 50/45 | 500000 | 50000 | 2 | 0/0 | 1600 | 60/8/8 |

Starting human hardpoints/equipment and purchasable weapons:

| Hull | Hardpoints (mask; fixed) | Equipment masks | Initial fitting guns | Initial fitting afterburners |
|---|---|---|---|---|
| 51 | 3;True; 1;True | 1,0,0,7169,1,0,0,17 | 3 PE Gat Gun 1; 91 ER Nanite 1; 92 EW Disruptor 1 |  |

Reachable human hulls (raw; cumulative baseline unlock time is in the plan appendix):

| ID | Hull | HP | Speed | Thrust/mass | Signature | Fixed/total | Magazine/cargo slots | Equipment masks |
|---|---|---|---|---|---|---|---|---|
| 49 | Adv Scout | 225 | 180 | 1200/32 | 0.5 | 2/2 | 0/5 | 1,0,0,7169,1,0,0,17 |
| 51 | Scout | 200 | 160 | 1200/32 | 0.75 | 2/2 | 0/5 | 1,0,0,7169,1,0,0,17 |
| 52 | Adv Fighter | 450 | 120 | 750/28.8 | 1 | 4/4 | 0/5 | 1,0,0,3072,1,0,0,15 |
| 53 | Enh Fighter | 350 | 100 | 750/28.8 | 1 | 3/3 | 0/5 | 1,0,0,3072,1,0,0,7 |
| 54 | Corvette | 2000 | 80 | 12500/625 | 2.5 | 2/4 | 80/5 | 2,0,768,3088,4,0,0,0 |
| 55 | Fighter | 300 | 100 | 750/28.8 | 1 | 3/3 | 0/5 | 1,0,0,3072,1,0,0,3 |
| 56 | Adv Stl Fighter | 200 | 160 | 2160/48 | 0.5 | 3/3 | 30/5 | 1,0,256,3073,1,3,0,0 |
| 58 | Stealth Fighter | 150 | 140 | 2160/48 | 0.5 | 2/2 | 20/5 | 1,0,256,3073,1,3,0,0 |
| 59 | Lt Stl Fighter | 100 | 120 | 2160/48 | 0.6 | 2/2 | 10/5 | 1,0,256,3073,1,3,0,0 |
| 60 | Hvy Interceptor | 750 | 90 | 400/16 | 1 | 3/3 | 0/5 | 1,0,0,2,0,0,0,33 |
| 61 | Interceptor | 700 | 80 | 400/16 | 1 | 2/2 | 0/5 | 1,0,0,2,0,0,0,33 |
| 63 | Lt Interceptor | 600 | 75 | 400/16 | 1 | 2/2 | 0/5 | 1,0,0,0,0,0,0,33 |
| 66 | Gunship | 800 | 80 | 1000/40 | 2 | 1/3 | 40/5 | 1,0,256,0,2,0,0,0 |
| 68 | Stealth Bomber | 150 | 80 | 2160/96 | 1 | 3/3 | 0/5 | 1,0,0,0,1,3,0,0 |
| 69 | Hvy Bomber | 1200 | 75 | 750/40 | 2 | 2/4 | 75/5 | 1,0,1536,0,2,0,0,0 |
| 70 | Bomber | 800 | 60 | 750/40 | 2 | 2/4 | 0/5 | 1,0,0,0,2,0,0,0 |
| 71 | Hvy Troop Transport | 975 | 75 | 1350/72 | 1 | 1/1 | 0/5 | 1,0,0,0,2,0,0,0 |
| 72 | Troop Transport | 750 | 60 | 675/36 | 2 | 1/1 | 0/5 | 1,0,0,0,2,0,0,0 |
| 73 | Flagship.4 [AsCr] | 5000 | 70 | 50000/5600 | 5 | 4/8 | 200/5 | 2,0,1536,3073,4,2,0,0 |
| 74 | Flagship.3 [AtCr] | 4000 | 70 | 50000/4000 | 4 | 2/6 | 160/5 | 2,0,1536,3073,4,2,0,0 |
| 75 | Flagship.2 [AtDv] | 3500 | 75 | 50000/2000 | 3.5 | 2/6 | 120/5 | 2,0,1536,3073,4,2,0,0 |
| 76 | Flagship.1 [AtFr] | 3500 | 75 | 50000/2000 | 3 | 2/5 | 120/5 | 2,0,1536,3073,4,2,0,0 |
| 84 | Attack Carrier [SR] | 3000 | 75 | 50000/5000 | 1.5 | 1/3 | 60/5 | 2,0,0,3073,4,0,0,0 |
| 91 | Hvy Scout | 250 | 180 | 1440/32 | 0.5 | 3/3 | 0/5 | 1,0,0,7169,1,0,0,17 |
| 154 | Stealth Bomber [NG] | 150 | 80 | 2160/96 | 1.5 | 2/2 | 30/5 | 1,0,64,0,1,1,0,0 |
| 162 | Fighter/Bomber | 450 | 70 | 1000/32 | 1.5 | 3/3 | 25/5 | 1,0,0,1024,1,0,0,5 |

Early and late weapons (side-buyable; a fitting local hull/mount is still required). Listed DPS is raw direct projectile power / burst time, before damage matrix and GA; missile cycle is ready + lock, not an observed launch interval:

| Part | Weapon | Initially buyable | Raw DPS / missile cycle | Damage type | Energy/ammo per shot |
|---|---|---|---|---|---|
| 3 | PE Gat Gun 1 | True | 32 | 2 | 4.59/1 |
| 4 | PE Mini-Gun 1 | True | 50 | 5 | 4.56/1 |
| 5 | LRM Aleph Res 1 | False | 0.25 | 6 | magazine |
| 6 | PE Gat Gun 2 | False | 40 | 2 | 4.131/1 |
| 7 | PE Gat Gun 3 | False | 50 | 2 | 3.672/1 |
| 8 | PE Mini-Gun 2 | False | 64.103 | 5 | 4.41/1 |
| 9 | PE Mini-Gun 3 | False | 76.22 | 5 | 4.26/1 |
| 12 | PE SkyCap 1 | True | 50 | 11 | 9.27/4 |
| 36 | PE SkyCap 2 | False | 62.5 | 11 | 8.72/4 |
| 37 | PE SkyCap 3 | False | 78.125 | 11 | 8.18/4 |
| 47 | PE AutoCan 1 | True | 98.293 | 11 | 5.23/1 |
| 49 | PE AutoCan 2 | False | 124.398 | 11 | 4.92/1 |
| 50 | EW Utl Can 1 | True | 27.545 | 1 | 20/0 |
| 51 | EW Utl Can 2 | False | 31.138 | 1 | 20/0 |
| 52 | EW Utl Can 3 | False | 38.922 | 1 | 20/0 |
| 55 | PE AutoCan 3 | False | 155.522 | 11 | 4.61/1 |
| 56 | PE EMP Can 1 | False | 30 | 13 | 11.47/2 |
| 60 | EW Sniper 1 | False | 28 | 4 | 8/0 |
| 61 | EW Sniper 2 | False | 36 | 4 | 7/0 |
| 62 | PE EMP Can 2 | False | 37.5 | 13 | 10.75/2 |
| 63 | PE SkyRip | False | 124.378 | 15 | 12.02/1 |
| 74 | EW A-B Can 1 | True | 66.667 | 8 | 98.75/0 |
| 75 | EW A-B Can 2 | False | 83.333 | 8 | 93.81/0 |
| 76 | EW A-B Can 3 | False | 100 | 8 | 68.63/0 |
| 77 | EW A-BS Can | False | 1750 | 12 | 263.33/0 |
| 78 | PW A-B Ion Bls 1 | True | 146.667 | 8 | 0/80 |
| 79 | PW A-B Ion Bls 2 | False | 173.333 | 8 | 0/80 |
| 82 | EW Hvy A-B Can 1 | False | 133.333 | 8 | 137.5/0 |
| 83 | EW Hvy A-B Can 2 | False | 166.667 | 8 | 130.63/0 |
| 90 | LRM Aleph Res 2 | False | 0.25 | 6 | magazine |
| 91 | ER Nanite 1 | True | -90 | 10 | 30/0 |
| 92 | EW Disruptor 1 | True | 12.8 | 3 | 7.5/0 |
| 93 | EW Disruptor 2 | False | 16 | 3 | 7.5/0 |
| 94 | EW Disruptor 3 | False | 20 | 3 | 7.5/0 |
| 97 | LRM Aleph Res 3 | False | 0.25 | 6 | magazine |
| 111 | EW Galv Blstr | False | 28 | 7 | 12/0 |
| 115 | SRM Nerve Gas | False | 0.25 | 12 | magazine |
| 116 | SRP Killer 1 | False | 0.25 | 3 | magazine |
| 117 | SRP Killer 2 | False | 0.25 | 3 | magazine |
| 118 | PE Mini-AC 1 | True | 75.301 | 11 | 5.98/1 |
| 119 | PE Mini-AC 2 | False | 82.831 | 11 | 5.61/1 |
| 123 | PE Mini-AC 3 | False | 91.114 | 11 | 5.23/1 |
| 165 | XRP Anti-Base 1 | False | 0.25 | 8 | magazine |
| 173 | XRP Anti-Base 2 | False | 0.25 | 8 | magazine |
| 190 | ER Nanite 2 | False | -120 | 10 | 30/0 |
| 222 | EW Seismic Cannon | False | 40 | 9 | 10/0 |
| 223 | ER Nanite 3 | False | -150 | 10 | 30/0 |

Station branches and baseline serial position (constructor bucket only, excluding travel):

| Station | Required/effect/local bits | Direct cost/seconds | Baseline step/seconds/cost |
|---|---|---|---|
| 30 | Outpost [19,255]/[12]/[99,255] | 5000/90 | 14/4440/51882 |
| 36 | Supremacy [4,19,239]/[13,175,216,225]/[99,255] | 10000/120 | 15/4560/61882 |
| 32 | Supremacy (Adv) [4,19,179,239]/[13,14,174,175,216,217,225,292]/[99,255] | 10000/120 | 32/10440/132257 |
| 37 | Expansion [2,19,239]/[8,175,216,226]/[99,255] | 10000/120 | 50/16680/192882 |
| 33 | Expansion (Adv) [2,19,119,239]/[8,15,174,175,216,217,226,290]/[99,255] | 10000/120 | 67/22560/261757 |
| 38 | Tactical [3,19,239]/[216,224,243]/[99,255] | 10000/120 | 85/28800/333632 |
| 34 | Tactical (Adv) [3,19,178,239]/[7,216,217,224,243,291]/[99,255] | 10000/120 | 96/32520/390507 |
| 31 | Shipyard [1,19,216,239]/[11,227]/[99,240,255] | 15000/120 | 109/36960/450507 |
| 29 | Shipyard (Dry) [1,19,142,216,239]/[11,185,227]/[99,240,255] | 15000/120 | 117/39600/514257 |
| 35 | Garrison (Str) [19,183,255]/[17,18,293]/[92,99,239,255] | 17500/120 | 123/41520/565507 |
| 39 | Refinery [19,255]/[]/[] | 3000/90 | 140/46830/625882 |
| 41 | Teleport [19,255]/[]/[] | 3000/90 | 141/46920/628882 |


Reachable GA developments (each completed factor multiplies the base GA; bucket cost/time were frozen at creation):

| Development | Cost | Seconds | Factors |
|---|---|---|---|
| GA: Ship Acceleration 1 | 1500 | 360 | thrust x1.1 |
| GA: Ship Acceleration 2 | 1875 | 360 | thrust x1.1 |
| GA: Ship Agility 1 | 1500 | 360 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Agility 2 | 1875 | 360 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Energy 1 | 1500 | 360 | energy x1.1 |
| GA: Ship Energy 2 | 1875 | 360 | energy x1.1 |
| GA: EW Range 1 | 1500 | 360 | energy lifespan x1.1 |
| GA: EW Range 2 | 1875 | 360 | energy lifespan x1.1 |
| GA: PW&EW Damage 1 | 1500 | 360 | gun damage x1.1 |
| GA: PW&EW Damage 2 | 1875 | 360 | gun damage x1.1 |
| GA: Faster He3 1 | 1500 | 360 | mining rate x1.1 |
| GA: Faster He3 2 | 1875 | 360 | mining rate x1.1 |
| GA: He3 Yield 1 | 1500 | 360 | mining yield x1.1 |
| GA: He3 Yield 2 | 1875 | 360 | mining yield x1.1 |
| GA: PW Range 1 | 1500 | 360 | ammo speed x1.1 |
| GA: PW Range 2 | 1875 | 360 | ammo speed x1.1 |
| GA: Ripcord 1 | 1500 | 360 | ripcord speed x1.15 |
| GA: Ripcord 2 | 1875 | 360 | ripcord speed x1.15 |
| GA: Ship Sensors 1 | 1500 | 360 | scan x1.1 |
| GA: Ship Sensors 2 | 1875 | 360 | scan x1.1 |
| GA: Ship Hull 1 | 1500 | 360 | ship armor x1.1 |
| GA: Ship Hull 2 | 1875 | 360 | ship armor x1.1 |
| GA: Ship Shield 1 | 1500 | 360 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Shield 2 | 1875 | 360 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Signature 1 | 1500 | 360 | signature resistance x1.1 |
| GA: Ship Signature 2 | 1875 | 360 | signature resistance x1.1 |
| GA: Ship Speed 1 | 3000 | 360 | speed x1.1 |
| GA: Ship Speed 2 | 3750 | 360 | speed x1.1 |
| GA: Station Hull 1 | 1500 | 360 | station armor x1.1; station armor regen x1.1 |
| GA: Station Hull 2 | 1875 | 360 | station armor x1.1; station armor regen x1.1 |
| GA: Station Shield 1 | 1500 | 360 | station shield x1.1; station shield regen x1.1 |
| GA: Station Shield 2 | 1875 | 360 | station shield x1.1; station shield regen x1.1 |


## 3 — Ga'Taraan

Starting tech bits: 6,10,31,34,37,45,59,73,93,98,102,107,108,110,126,127,145,146,154,165,168,169,170,187,208,209,229,355. All four enabled path-switch bits (1,2,3,4) are added by ResearchSettings. No-development bits: 5,6,7,8,10,13,14,15,17,18,28,32,33,34,36,37,38,45,46,51,52,57,59,72,87,88,89,91,98,100,102,103,108,110,111,114,116,125,126,130,132,146,154,157,163,165,166,168,170,177,180,182,183,186,187,190,194,195,196,197,202,206,207,210,211,212,213,216,220,224,225,226,243,259,260,261,262,263,265,268,278,281,284,355.

Home 44 Garrison: armor 20000, shield 10000, income 0; grants [17], local [92,99,239,255]. Base payday fraction +0; starting-money addition +0; pod 113. Negative additions are retained.

Nonidentity base GA multipliers:

| Index | Attribute | Factor |
|---|---|---|
| 4 | station armor | 1.1 |
| 6 | station shield | 1.1 |
| 9 | ship shield | 1.1 |
| 16 | missile turn | 0.9 |
| 17 | mining rate | 0.8 |
| 20 | ripcord speed | 0.9 |
| 23 | development cost | 0.8 |

Starting hulls (including autonomous types):

| ID | Hull | Human | HP raw/effective | Shield max initial | Speed raw/effective | Thrust | Mass | Signature | Guns fixed/total | Ammo | Magazine/dispenser/chaff |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 97 | Sm Std Constructor | False | 1440/1440 | 330 | 60/60 | 2000 | 200 | 1.5 | 0/0 | 0 | 0/0/0 |
| 107 | Miner | False | 1056/1056 | 330 | 100/100 | 1000 | 100 | 1.5 | 0/0 | 0 | 0/0/0 |
| 110 | Lg Std Constructor | False | 2880/2880 | 330 | 60/60 | 500000 | 50000 | 1.5 | 0/0 | 0 | 0/0/0 |
| 113 | Lifepod | False | 25/25 | 0 | 60/60 | 150 | 10 | 0.5 | 0/0 | 0 | 0/0/0 |
| 114 | Scout | True | 200/200 | 165 | 160/160 | 1200 | 40 | 0.75 | 2/2 | 960 | 10/2/4 |
| 116 | Carrier [SR] | False | 2000/2000 | 0 | 50/50 | 500000 | 50000 | 2 | 0/0 | 1600 | 60/8/8 |
| 121 | Fighter | True | 300/300 | 165 | 100/100 | 750 | 30 | 1 | 2/2 | 600 | 20/1/4 |

Starting human hardpoints/equipment and purchasable weapons:

| Hull | Hardpoints (mask; fixed) | Equipment masks | Initial fitting guns | Initial fitting afterburners |
|---|---|---|---|---|
| 114 | 1;True; 2;True | 1,0,129,7169,1,0,0,16 | 33 PW Gat Gun 1; 91 ER Nanite 1; 92 EW Disruptor 1 |  |
| 121 | 1;True; 1;True | 1,0,129,1024,1,0,0,3 | 33 PW Gat Gun 1; 92 EW Disruptor 1 | 23 Booster 1 |

Reachable human hulls (raw; cumulative baseline unlock time is in the plan appendix):

| ID | Hull | HP | Speed | Thrust/mass | Signature | Fixed/total | Magazine/cargo slots | Equipment masks |
|---|---|---|---|---|---|---|---|---|
| 57 | Corvette | 2000 | 80 | 12500/625 | 2.5 | 2/4 | 80/5 | 2,0,771,2064,4,0,0,0 |
| 114 | Scout | 200 | 160 | 1200/40 | 0.75 | 2/2 | 10/5 | 1,0,129,7169,1,0,0,16 |
| 118 | Attack Carrier [SR] | 3000 | 75 | 50000/5000 | 1.5 | 1/3 | 60/5 | 2,0,2,3073,4,0,0,0 |
| 119 | Adv Scout | 225 | 180 | 1200/40 | 0.5 | 2/2 | 15/5 | 1,0,129,7169,1,0,0,16 |
| 120 | Hvy Scout | 250 | 180 | 1440/40 | 0.5 | 2/2 | 20/5 | 1,0,4225,7169,1,0,0,16 |
| 121 | Fighter | 300 | 100 | 750/30 | 1 | 2/2 | 20/5 | 1,0,129,1024,1,0,0,3 |
| 122 | Enh Fighter | 350 | 100 | 750/30 | 1 | 3/3 | 20/5 | 1,0,129,1024,1,0,0,7 |
| 123 | Adv Fighter | 450 | 120 | 750/30 | 1 | 3/3 | 25/5 | 1,0,129,1024,1,0,0,15 |
| 124 | Bomber | 800 | 60 | 750/50 | 2 | 2/4 | 60/5 | 1,0,1318,0,2,0,0,0 |
| 125 | Hvy Bomber | 1200 | 75 | 750/50 | 2 | 2/4 | 75/5 | 1,0,1830,0,2,0,0,0 |
| 126 | Guardian | 1600 | 30 | 200/40 | 0.5 | 1/1 | 0/5 | 1,0,0,0,0,0,0,2 |
| 128 | PT Bomber | 600 | 120 | 1250/50 | 1.5 | 1/2 | 24/5 | 1,0,11265,0,2,0,0,0 |
| 129 | Mustang | 800 | 120 | 1300/43 | 1.5 | 1/2 | 30/5 | 1,0,259,0,2,0,0,1 |
| 131 | Stealth Fighter | 150 | 140 | 2160/60 | 0.5 | 2/2 | 20/5 | 1,0,259,4096,1,3,0,0 |
| 132 | Adv Stl Fighter | 200 | 160 | 2160/60 | 0.5 | 2/2 | 30/5 | 1,0,259,4096,1,3,0,0 |
| 133 | Stealth Bomber | 150 | 80 | 2160/120 | 1 | 2/2 | 30/5 | 1,0,262,0,1,3,0,0 |
| 134 | Interceptor | 700 | 80 | 400/20 | 1 | 2/2 | 0/5 | 1,0,0,2,0,0,0,33 |
| 135 | Hvy Interceptor | 750 | 90 | 400/20 | 1 | 3/3 | 0/5 | 1,0,0,2,0,0,0,33 |
| 136 | Troop Transport | 750 | 60 | 675/45 | 2 | 0/0 | 15/5 | 1,0,32,0,2,0,0,0 |
| 137 | Hvy Troop Transport | 975 | 75 | 1350/90 | 1 | 0/0 | 45/5 | 1,0,32,0,2,0,0,0 |
| 138 | Hvy Troop Tran [AP] | 1170 | 75 | 2700/180 | 1 | 0/0 | 45/5 | 1,0,32,0,2,0,0,0 |
| 140 | Assault Ship | 3000 | 120 | 37500/2500 | 1 | 1/3 | 0/5 | 2,0,512,3073,4,2,0,0 |
| 142 | Cruiser | 4000 | 70 | 50000/5000 | 4 | 1/5 | 160/5 | 2,0,520,0,4,2,0,0 |
| 143 | Destroyer | 1500 | 100 | 25000/2500 | 3 | 1/1 | 120/5 | 2,0,529,0,4,0,0,0 |
| 144 | Devastator | 3500 | 75 | 25000/2500 | 3.5 | 1/5 | 0/5 | 2,0,0,0,4,2,0,0 |
| 145 | Harbinger of Doom | 3000 | 60 | 25000/2500 | 4 | 0/0 | 120/5 | 2,0,-32256,0,4,0,0,0 |
| 146 | Frigate | 3500 | 75 | 25000/2500 | 3 | 1/4 | 120/5 | 2,0,520,0,4,2,0,0 |
| 147 | Assault Carrier | 4000 | 100 | 50000/5000 | 1 | 1/3 | 60/5 | 2,0,514,3073,4,0,0,0 |
| 155 | Stealth Bomber [NG] | 150 | 80 | 2160/120 | 1.5 | 2/2 | 30/5 | 1,0,64,0,1,1,0,0 |

Early and late weapons (side-buyable; a fitting local hull/mount is still required). Listed DPS is raw direct projectile power / burst time, before damage matrix and GA; missile cycle is ready + lock, not an observed launch interval:

| Part | Weapon | Initially buyable | Raw DPS / missile cycle | Damage type | Energy/ammo per shot |
|---|---|---|---|---|---|
| 5 | LRM Aleph Res 1 | False | 0.25 | 6 | magazine |
| 10 | XRM Cruise 1 | False | 0.25 | 8 | magazine |
| 11 | XRM Cruise 2 | False | 0.25 | 8 | magazine |
| 18 | SRM Seismic | False | 0.25 | 9 | magazine |
| 20 | LRM Killer 1 | False | 0.25 | 3 | magazine |
| 30 | LRM Killer 2 | False | 0.25 | 3 | magazine |
| 31 | LRM Hunter 3 | False | 1.25 | 4 | magazine |
| 32 | XRM Hunter Klr 3 | False | 1.75 | 4 | magazine |
| 33 | PW Gat Gun 1 | True | 32 | 2 | 0/1 |
| 50 | EW Utl Can 1 | True | 27.545 | 1 | 20/0 |
| 51 | EW Utl Can 2 | False | 31.138 | 1 | 20/0 |
| 52 | EW Utl Can 3 | False | 38.922 | 1 | 20/0 |
| 60 | EW Sniper 1 | False | 28 | 4 | 8/0 |
| 61 | EW Sniper 2 | False | 36 | 4 | 7/0 |
| 67 | EW SkyRip | False | 149.254 | 15 | 8/0 |
| 71 | PW Gat Gun 2 | False | 40 | 2 | 0/1 |
| 72 | PW Gat Gun 3 | False | 50 | 2 | 0/1 |
| 87 | PW Mini-AC 1 | True | 90.361 | 11 | 0/1 |
| 88 | PW Mini-AC 2 | False | 99.398 | 11 | 0/1 |
| 89 | PW Mini-AC 3 | False | 109.337 | 11 | 0/1 |
| 90 | LRM Aleph Res 2 | False | 0.25 | 6 | magazine |
| 91 | ER Nanite 1 | True | -90 | 10 | 30/0 |
| 92 | EW Disruptor 1 | True | 12.8 | 3 | 7.5/0 |
| 93 | EW Disruptor 2 | False | 16 | 3 | 7.5/0 |
| 94 | EW Disruptor 3 | False | 20 | 3 | 7.5/0 |
| 95 | LRM Killer Swarm 1 | False | 0.25 | 3 | magazine |
| 96 | LRM Killer Swarm 2 | False | 0.25 | 3 | magazine |
| 97 | LRM Aleph Res 3 | False | 0.25 | 6 | magazine |
| 98 | MRM Seek 1 QL | False | 2.25 | 4 | magazine |
| 99 | MRM Seek 2 QL | False | 1.75 | 4 | magazine |
| 100 | PW EMP Can 1 | False | 36 | 13 | 0/2 |
| 101 | PW EMP Can 2 | False | 45 | 13 | 0/2 |
| 102 | MRM Seek 3 QL | False | 1.25 | 4 | magazine |
| 106 | SRM Dumb 1 QL | False | 0.25 | 4 | magazine |
| 107 | SRM Dumb 2 QL | False | 0.25 | 4 | magazine |
| 108 | SRM Dumb 3 QL | False | 0.25 | 4 | magazine |
| 111 | EW Galv Blstr | False | 28 | 7 | 12/0 |
| 115 | SRM Nerve Gas | False | 0.25 | 12 | magazine |
| 120 | PW Mini-Gun 1 | True | 50 | 5 | 0/1 |
| 121 | PW Mini-Gun 2 | False | 64.103 | 5 | 0/1 |
| 122 | PW Mini-Gun 3 | False | 78.125 | 5 | 0/1 |
| 128 | EW Gauss Gun | False | 0 | 4 | 192/0 |
| 129 | EW Pulse Laser | False | 50 | 5 | 6.5/0 |
| 130 | PW AutoCan 1 | True | 117.952 | 11 | 0/1 |
| 131 | PW AutoCan 2 | False | 149.277 | 11 | 0/1 |
| 132 | PW AutoCan 3 | False | 186.627 | 11 | 0/1 |
| 133 | EW SkyRip LE | False | 149.254 | 15 | 7/0 |
| 136 | PW Mini-Dis | False | 0 | 3 | 0/1 |
| 137 | PE Striker | False | 0 | 18 | 30/15 |
| 138 | Rocket Targeting | False | 0 | 15 | 0/0 |
| 140 | PW SkyCap 1 | True | 60 | 11 | 0/4 |
| 141 | PW SkyCap 2 | False | 75 | 11 | 0/4 |
| 142 | PW SkyCap 3 | False | 93.75 | 11 | 0/4 |
| 144 | LRM Anti-Base | False | 0.25 | 8 | magazine |
| 145 | LRR Thunder | False | 0.25 | 3 | magazine |
| 146 | MRM Zeus | False | 1 | 4 | magazine |
| 147 | MRR Lightning 1 | False | 0.25 | 14 | magazine |
| 148 | MRR Lightning 2 | False | 0.25 | 14 | magazine |
| 149 | SRM Doomsday Res | False | 0.25 | 8 | magazine |
| 150 | SRM Dumbfire 1 | True | 0.25 | 4 | magazine |
| 151 | SRM Dumbfire 2 | False | 0.25 | 4 | magazine |
| 152 | SRM Dumbfire 3 | False | 0.25 | 4 | magazine |
| 153 | MRM Seeker 1 | True | 2.25 | 4 | magazine |
| 154 | MRM Seeker 2 | False | 1.75 | 4 | magazine |
| 155 | LRM Hunter 1 | True | 2.25 | 4 | magazine |
| 156 | MRM Seeker 3 | False | 1.25 | 4 | magazine |
| 157 | SRM Anti-Base 1 | True | 0.25 | 8 | magazine |
| 158 | SRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 159 | LRM Hunter 2 | False | 1.75 | 4 | magazine |
| 160 | SRM Tactical Nuke 1 | False | 0.25 | 8 | magazine |
| 161 | SRM Tactical Nuke 2 | False | 0.25 | 8 | magazine |
| 162 | MRM Seismic | False | 0.25 | 9 | magazine |
| 163 | XRM Hunter Klr 1 | False | 2.25 | 4 | magazine |
| 164 | XRM Hunter Klr 2 | False | 2.25 | 4 | magazine |
| 190 | ER Nanite 2 | False | -120 | 10 | 30/0 |
| 192 | SRM EMP | False | 0.25 | 12 | magazine |
| 193 | XRM Torpedo 1 | False | 0.25 | 4 | magazine |
| 194 | XRM Torpedo 2 | False | 0.25 | 4 | magazine |
| 209 | MRT Anti-Base | False | 0.25 | 8 | magazine |
| 223 | ER Nanite 3 | False | -150 | 10 | 30/0 |
| 601 | SRM Anti-Base 3 | False | 0.25 | 8 | magazine |

Station branches and baseline serial position (constructor bucket only, excluding travel):

| Station | Required/effect/local bits | Direct cost/seconds | Baseline step/seconds/cost |
|---|---|---|---|
| 46 | Outpost [10,255]/[12]/[99,255] | 5000/90 | 15/1660/59008 |
| 42 | [P]alisade [6,10,239]/[263]/[99,239,255] | 10000/120 | 16/1780/69008 |
| 43 | [R]esearch [6,10,239]/[262]/[99,255] | 10000/120 | 17/1900/79008 |
| 51 | Supremacy [4,10,239]/[13,175,216,225]/[99,255] | 10000/120 | 24/2740/122008 |
| 52 | Supremacy (Adv) [4,10,179,239]/[13,14,174,175,216,217,225,292]/[99,255] | 10000/120 | 42/4900/188008 |
| 53 | Expansion [2,10,239]/[8,175,216,226]/[99,255] | 10000/120 | 63/7420/248008 |
| 54 | Expansion (Adv) [2,10,119,239]/[8,15,174,175,216,217,226,290]/[99,255] | 10000/120 | 81/9580/322808 |
| 55 | Tactical [3,10,239]/[216,224,243]/[99,255] | 10000/120 | 101/11980/406808 |
| 56 | Tactical (Adv) [3,10,178,239]/[7,216,217,224,243,291]/[99,255] | 10000/120 | 118/14020/488408 |
| 49 | Shipyard [1,10,216,239]/[11,227]/[99,240,255] | 15000/120 | 134/15940/561408 |
| 50 | Shipyard (Dry) [1,10,141,216,239]/[11,185,227]/[99,240,255] | 15000/120 | 147/17500/657408 |
| 45 | Garrison (Str) [10,183,255]/[17,18,293]/[92,99,239,255] | 25000/120 | 158/18820/720408 |
| 48 | Teleport [10,255]/[]/[] | 3000/90 | 179/21190/799408 |


Reachable GA developments (each completed factor multiplies the base GA; bucket cost/time were frozen at creation):

| Development | Cost | Seconds | Factors |
|---|---|---|---|
| GA: Ship Acceleration 1 | 1600 | 120 | thrust x1.1 |
| GA: Ship Acceleration 2 | 2000 | 120 | thrust x1.1 |
| GA: Ship Agility 1 | 1600 | 120 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Agility 2 | 2000 | 120 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Energy 1 | 1600 | 120 | energy x1.1 |
| GA: Ship Energy 2 | 2000 | 120 | energy x1.1 |
| GA: EW Range 1 | 1600 | 120 | energy lifespan x1.1 |
| GA: EW Range 2 | 2000 | 120 | energy lifespan x1.1 |
| GA: PW&EW Damage 1 | 1600 | 120 | gun damage x1.1 |
| GA: PW&EW Damage 2 | 2000 | 120 | gun damage x1.1 |
| GA: Faster He3 1 | 1600 | 120 | mining rate x1.1 |
| GA: Faster He3 2 | 2000 | 120 | mining rate x1.1 |
| GA: He3 Yield 1 | 1600 | 120 | mining yield x1.1 |
| GA: He3 Yield 2 | 2000 | 120 | mining yield x1.1 |
| GA: Missile Damage 1 | 1600 | 120 | missile damage x1.1 |
| GA: Missile Damage 2 | 2000 | 120 | missile damage x1.1 |
| GA: Missile Track 1 | 1600 | 120 | missile turn x1.1 |
| GA: Missile Track 2 | 2000 | 120 | missile turn x1.1 |
| GA: PW Range 1 | 1600 | 120 | ammo speed x1.1 |
| GA: PW Range 2 | 2000 | 120 | ammo speed x1.1 |
| GA: Ripcord 1 | 1600 | 120 | ripcord speed x1.15 |
| GA: Ripcord 2 | 2000 | 120 | ripcord speed x1.15 |
| GA: Ship Sensors 1 | 1600 | 120 | scan x1.1 |
| GA: Ship Sensors 2 | 2000 | 120 | scan x1.1 |
| GA: Ship Hull 1 | 1600 | 120 | ship armor x1.1 |
| GA: Ship Hull 2 | 2000 | 120 | ship armor x1.1 |
| GA: Ship Shield 1 | 1600 | 120 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Shield 2 | 2000 | 120 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Signature 1 | 1600 | 120 | signature resistance x1.1 |
| GA: Ship Signature 2 | 2000 | 120 | signature resistance x1.1 |
| GA: Ship Speed 1 | 3200 | 120 | speed x1.1 |
| GA: Ship Speed 2 | 4000 | 120 | speed x1.1 |
| GA: Station Hull 1 | 1600 | 120 | station armor x1.1; station armor regen x1.1 |
| GA: Station Hull 2 | 2000 | 120 | station armor x1.1; station armor regen x1.1 |
| GA: Station Shield 1 | 1600 | 120 | station shield x1.1; station shield regen x1.1 |
| GA: Station Shield 2 | 2000 | 120 | station shield x1.1; station shield regen x1.1 |


## 4 — Phoenix

Starting tech bits: 6,9,31,34,37,45,59,73,93,98,102,107,108,110,126,127,144,146,154,165,168,169,170,187,208,209,355. All four enabled path-switch bits (1,2,3,4) are added by ResearchSettings. No-development bits: 5,6,7,8,9,13,14,15,17,18,28,32,34,36,37,38,45,46,51,52,57,59,72,87,88,89,94,95,98,100,102,103,108,110,111,114,115,116,118,125,126,130,132,144,146,154,155,157,163,165,166,168,170,177,180,182,183,186,187,190,194,195,196,197,202,205,206,207,210,211,212,213,216,220,224,225,226,243,290,291,292,294,355.

Home 60 Garrison: armor 20000, shield 10000, income 0; grants [17], local [99,239,241,255]. Base payday fraction +0; starting-money addition +0; pod 77. Negative additions are retained.

Nonidentity base GA multipliers:

| Index | Attribute | Factor |
|---|---|---|
| 3 | turn torque | 1.1 |
| 4 | station armor | 0.9 |
| 6 | station shield | 1.25 |
| 8 | ship armor | 0.9 |
| 9 | ship shield | 1.25 |
| 11 | scan | 1.1 |
| 18 | mining yield | 1.1 |
| 22 | missile damage | 0.95 |
| 24 | development time | 1.5 |

Starting hulls (including autonomous types):

| ID | Hull | Human | HP raw/effective | Shield max initial | Speed raw/effective | Thrust | Mass | Signature | Guns fixed/total | Ammo | Magazine/dispenser/chaff |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 77 | Lifepod | False | 25/22.5 | 0 | 60/60 | 150 | 10 | 0.5 | 0/0 | 0 | 0/0/0 |
| 157 | Miner | False | 1056/950.4 | 375 | 100/100 | 1000 | 100 | 1.5 | 0/0 | 0 | 0/0/0 |
| 179 | Sm Std Constructor | False | 1440/1296 | 375 | 60/60 | 2000 | 200 | 1.5 | 0/0 | 0 | 0/0/0 |
| 182 | Lg Std Constructor | False | 2880/2592 | 375 | 60/60 | 500000 | 50000 | 1.5 | 0/0 | 0 | 0/0/0 |
| 185 | Carrier [SR] | False | 2000/1800 | 0 | 50/50 | 500000 | 50000 | 2 | 0/0 | 1600 | 60/8/8 |
| 187 | Scout | True | 200/180 | 187.5 | 160/160 | 1200 | 40 | 0.75 | 1/1 | 800 | 15/2/4 |
| 190 | Fighter | True | 300/270 | 187.5 | 110/110 | 750 | 30 | 1 | 2/2 | 600 | 25/1/4 |
| 229 | SmallRip [SR] | False | 1000/900 | 0 | 80/80 | 750 | 50 | 1.25 | 0/0 | 0 | 0/0/0 |
| 230 | Recon Drone | False | 200/180 | 0 | 100/100 | 750 | 30 | 0.5 | 0/0 | 0 | 0/0/0 |

Starting human hardpoints/equipment and purchasable weapons:

| Hull | Hardpoints (mask; fixed) | Equipment masks | Initial fitting guns | Initial fitting afterburners |
|---|---|---|---|---|
| 187 | 3;True | 1,0,129,15361,1,0,0,16 | 33 PW Gat Gun 1; 91 ER Nanite 1; 92 EW Disruptor 1 |  |
| 190 | 1;True; 1;True | 1,0,129,1024,1,0,0,3 | 33 PW Gat Gun 1; 92 EW Disruptor 1 | 23 Booster 1 |

Reachable human hulls (raw; cumulative baseline unlock time is in the plan appendix):

| ID | Hull | HP | Speed | Thrust/mass | Signature | Fixed/total | Magazine/cargo slots | Equipment masks |
|---|---|---|---|---|---|---|---|---|
| 187 | Scout | 200 | 160 | 1200/40 | 0.75 | 1/1 | 15/5 | 1,0,129,15361,1,0,0,16 |
| 188 | Adv Scout | 225 | 180 | 1200/40 | 0.5 | 1/1 | 20/5 | 1,0,129,15361,1,0,0,16 |
| 189 | Hvy Scout | 250 | 180 | 1440/40 | 0.5 | 1/1 | 30/5 | 1,0,4225,15361,1,0,0,16 |
| 190 | Fighter | 300 | 110 | 750/30 | 1 | 2/2 | 25/5 | 1,0,129,1024,1,0,0,3 |
| 191 | Enh Fighter | 350 | 110 | 750/30 | 1 | 3/3 | 25/5 | 1,0,129,1024,1,0,0,7 |
| 192 | Adv Fighter | 450 | 130 | 750/30 | 1 | 3/3 | 30/5 | 1,0,129,1024,1,0,0,15 |
| 195 | Bomber | 800 | 60 | 750/50 | 2 | 2/4 | 70/5 | 1,0,294,0,18,0,0,0 |
| 196 | Hvy Bomber | 1200 | 75 | 750/50 | 2 | 2/5 | 85/5 | 1,0,1830,0,18,0,0,0 |
| 197 | Gunship | 800 | 80 | 1000/50 | 2 | 1/3 | 50/5 | 1,0,259,0,2,0,0,0 |
| 198 | Interceptor | 700 | 80 | 400/20 | 1 | 2/2 | 0/5 | 1,0,0,2,0,0,0,33 |
| 199 | Hvy Interceptor | 750 | 90 | 400/20 | 1 | 3/3 | 0/5 | 1,0,0,2,0,0,0,33 |
| 200 | Stealth Fighter | 150 | 140 | 2160/60 | 0.5 | 2/2 | 30/5 | 1,0,259,12288,1,3,0,0 |
| 201 | Adv Stl Fighter | 200 | 160 | 2160/60 | 0.5 | 2/2 | 40/5 | 1,0,259,12288,1,3,0,0 |
| 202 | Troop Transport | 750 | 60 | 675/45 | 2 | 0/1 | 15/5 | 1,0,32,0,18,0,0,0 |
| 203 | Hvy Troop Transport | 975 | 75 | 1350/90 | 1 | 0/2 | 50/5 | 1,0,32,0,18,0,0,0 |
| 205 | Fighter/Bomber | 450 | 70 | 1200/40 | 1.5 | 3/3 | 25/5 | 1,0,133,1024,1,0,0,5 |
| 206 | Stealth Bomber | 150 | 80 | 2160/120 | 1 | 2/2 | 40/5 | 1,0,262,0,1,3,0,0 |
| 207 | Stealth Bomber [NG] | 150 | 80 | 2160/120 | 1.5 | 2/2 | 40/5 | 1,0,64,0,1,1,0,0 |
| 209 | Corvette | 2000 | 80 | 12500/625 | 2.5 | 2/4 | 90/5 | 2,0,771,3088,4,0,0,0 |
| 213 | Attack Carrier [SR] | 3000 | 75 | 50000/5000 | 1.5 | 1/3 | 70/5 | 2,0,2,3073,4,0,0,0 |
| 219 | Assault Ship | 3000 | 120 | 37500/2500 | 1 | 1/3 | 0/5 | 2,0,512,3073,4,2,0,0 |
| 224 | Destroyer | 1500 | 100 | 25000/2500 | 3 | 1/1 | 130/5 | 2,0,529,0,4,0,0,0 |
| 226 | Devastator | 3500 | 75 | 25000/2500 | 3.5 | 1/5 | 0/5 | 2,0,0,0,4,0,0,0 |
| 227 | Cruiser | 4000 | 70 | 50000/5000 | 4 | 1/5 | 200/5 | 2,0,520,0,4,0,0,0 |
| 246 | Vanguard | 900 | 80 | 1200/40 | 1.5 | 2/2 | 20/5 | 1,0,0,12288,0,0,0,-32765 |
| 247 | Frigate | 3500 | 75 | 25000/2500 | 3 | 1/4 | 160/5 | 2,0,520,0,4,2,0,0 |

Early and late weapons (side-buyable; a fitting local hull/mount is still required). Listed DPS is raw direct projectile power / burst time, before damage matrix and GA; missile cycle is ready + lock, not an observed launch interval:

| Part | Weapon | Initially buyable | Raw DPS / missile cycle | Damage type | Energy/ammo per shot |
|---|---|---|---|---|---|
| 5 | LRM Aleph Res 1 | False | 0.25 | 6 | magazine |
| 10 | XRM Cruise 1 | False | 0.25 | 8 | magazine |
| 11 | XRM Cruise 2 | False | 0.25 | 8 | magazine |
| 18 | SRM Seismic | False | 0.25 | 9 | magazine |
| 20 | LRM Killer 1 | False | 0.25 | 3 | magazine |
| 30 | LRM Killer 2 | False | 0.25 | 3 | magazine |
| 31 | LRM Hunter 3 | False | 1.25 | 4 | magazine |
| 32 | XRM Hunter Klr 3 | False | 1.75 | 4 | magazine |
| 33 | PW Gat Gun 1 | True | 32 | 2 | 0/1 |
| 50 | EW Utl Can 1 | True | 27.545 | 1 | 20/0 |
| 51 | EW Utl Can 2 | False | 31.138 | 1 | 20/0 |
| 52 | EW Utl Can 3 | False | 38.922 | 1 | 20/0 |
| 60 | EW Sniper 1 | False | 28 | 4 | 8/0 |
| 61 | EW Sniper 2 | False | 36 | 4 | 7/0 |
| 67 | EW SkyRip | False | 149.254 | 15 | 8/0 |
| 71 | PW Gat Gun 2 | False | 40 | 2 | 0/1 |
| 72 | PW Gat Gun 3 | False | 50 | 2 | 0/1 |
| 87 | PW Mini-AC 1 | True | 90.361 | 11 | 0/1 |
| 88 | PW Mini-AC 2 | False | 99.398 | 11 | 0/1 |
| 89 | PW Mini-AC 3 | False | 109.337 | 11 | 0/1 |
| 90 | LRM Aleph Res 2 | False | 0.25 | 6 | magazine |
| 91 | ER Nanite 1 | True | -90 | 10 | 30/0 |
| 92 | EW Disruptor 1 | True | 12.8 | 3 | 7.5/0 |
| 93 | EW Disruptor 2 | False | 16 | 3 | 7.5/0 |
| 94 | EW Disruptor 3 | False | 20 | 3 | 7.5/0 |
| 95 | LRM Killer Swarm 1 | False | 0.25 | 3 | magazine |
| 96 | LRM Killer Swarm 2 | False | 0.25 | 3 | magazine |
| 97 | LRM Aleph Res 3 | False | 0.25 | 6 | magazine |
| 98 | MRM Seek 1 QL | False | 2.25 | 4 | magazine |
| 99 | MRM Seek 2 QL | False | 1.75 | 4 | magazine |
| 100 | PW EMP Can 1 | False | 36 | 13 | 0/2 |
| 101 | PW EMP Can 2 | False | 45 | 13 | 0/2 |
| 102 | MRM Seek 3 QL | False | 1.25 | 4 | magazine |
| 103 | MRM Quickfire 1 | True | 0.5 | 14 | magazine |
| 104 | MRM Quickfire 2 | False | 0.45 | 14 | magazine |
| 105 | MRM Quickfire 3 | False | 0.4 | 14 | magazine |
| 106 | SRM Dumb 1 QL | False | 0.25 | 4 | magazine |
| 107 | SRM Dumb 2 QL | False | 0.25 | 4 | magazine |
| 108 | SRM Dumb 3 QL | False | 0.25 | 4 | magazine |
| 111 | EW Galv Blstr | False | 28 | 7 | 12/0 |
| 115 | SRM Nerve Gas | False | 0.25 | 12 | magazine |
| 120 | PW Mini-Gun 1 | True | 50 | 5 | 0/1 |
| 121 | PW Mini-Gun 2 | False | 64.103 | 5 | 0/1 |
| 122 | PW Mini-Gun 3 | False | 78.125 | 5 | 0/1 |
| 126 | XRM Anti-Base 1 | False | 0.25 | 8 | magazine |
| 127 | XRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 130 | PW AutoCan 1 | True | 117.952 | 11 | 0/1 |
| 131 | PW AutoCan 2 | False | 149.277 | 11 | 0/1 |
| 132 | PW AutoCan 3 | False | 186.627 | 11 | 0/1 |
| 140 | PW SkyCap 1 | True | 60 | 11 | 0/4 |
| 141 | PW SkyCap 2 | False | 75 | 11 | 0/4 |
| 142 | PW SkyCap 3 | False | 93.75 | 11 | 0/4 |
| 150 | SRM Dumbfire 1 | True | 0.25 | 4 | magazine |
| 151 | SRM Dumbfire 2 | False | 0.25 | 4 | magazine |
| 152 | SRM Dumbfire 3 | False | 0.25 | 4 | magazine |
| 153 | MRM Seeker 1 | True | 2.25 | 4 | magazine |
| 154 | MRM Seeker 2 | False | 1.75 | 4 | magazine |
| 155 | LRM Hunter 1 | True | 2.25 | 4 | magazine |
| 156 | MRM Seeker 3 | False | 1.25 | 4 | magazine |
| 157 | SRM Anti-Base 1 | True | 0.25 | 8 | magazine |
| 158 | SRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 159 | LRM Hunter 2 | False | 1.75 | 4 | magazine |
| 160 | SRM Tactical Nuke 1 | False | 0.25 | 8 | magazine |
| 161 | SRM Tactical Nuke 2 | False | 0.25 | 8 | magazine |
| 162 | MRM Seismic | False | 0.25 | 9 | magazine |
| 163 | XRM Hunter Klr 1 | False | 2.25 | 4 | magazine |
| 164 | XRM Hunter Klr 2 | False | 2.25 | 4 | magazine |
| 190 | ER Nanite 2 | False | -120 | 10 | 30/0 |
| 192 | SRM EMP | False | 0.25 | 12 | magazine |
| 193 | XRM Torpedo 1 | False | 0.25 | 4 | magazine |
| 194 | XRM Torpedo 2 | False | 0.25 | 4 | magazine |
| 196 | PE Vul Can 1 | False | 60 | 4 | 5.03/2 |
| 197 | PE Vul Can 2 | False | 80 | 4 | 4.75/2 |
| 198 | EW Lsr Can 1 | False | 117.952 | 4 | 5.4/0 |
| 202 | EW Lsr Can 2 | False | 149.277 | 4 | 4.86/0 |
| 223 | ER Nanite 3 | False | -150 | 10 | 30/0 |
| 601 | SRM Anti-Base 3 | False | 0.25 | 8 | magazine |

Station branches and baseline serial position (constructor bucket only, excluding travel):

| Station | Required/effect/local bits | Direct cost/seconds | Baseline step/seconds/cost |
|---|---|---|---|
| 70 | Outpost [9,255]/[12]/[99,255] | 5000/90 | 16/2625/72510 |
| 62 | Supremacy [4,9,239]/[13,175,216,225,295]/[99,255] | 10000/120 | 17/2745/82510 |
| 63 | Supremacy (Adv) [4,9,179,239]/[13,14,174,175,216,217,225,292,295]/[99,255] | 10000/120 | 36/6105/167510 |
| 64 | Expansion [2,9,239]/[8,175,216,226,295]/[99,255] | 10000/120 | 56/9645/240010 |
| 65 | Expansion (Adv) [2,9,119,239]/[8,15,174,175,216,217,226,290,295]/[99,255] | 10000/120 | 73/12645/328510 |
| 66 | Tactical [3,9,239]/[216,224,243,295]/[99,255] | 10000/120 | 91/15825/421010 |
| 67 | Tactical (Adv) [3,9,178,239]/[7,216,217,224,243,291,295]/[99,255] | 10000/120 | 106/18465/510510 |
| 73 | [T]ransceiver [9,239,295]/[294]/[] | 10000/120 | 121/21105/588010 |
| 68 | Shipyard [1,9,239]/[11,227,295]/[99,240,255] | 15000/120 | 133/23205/643010 |
| 69 | Shipyard (Dry) [1,9,141,239]/[11,185,227,295]/[99,240,255] | 15000/120 | 145/25305/743010 |
| 61 | Garrison (Str) [9,183,255]/[17,18,293,295]/[92,99,239,255] | 20000/120 | 154/26865/803010 |
| 71 | Refinery [9,255]/[]/[] | 3000/90 | 183/31365/913510 |
| 72 | Teleport [9,255]/[]/[] | 3000/90 | 184/31455/916510 |


Reachable GA developments (each completed factor multiplies the base GA; bucket cost/time were frozen at creation):

| Development | Cost | Seconds | Factors |
|---|---|---|---|
| GA: Ship Acceleration 1 | 2000 | 180 | thrust x1.1 |
| GA: Ship Acceleration 2 | 2500 | 180 | thrust x1.1 |
| GA: Ship Agility 1 | 2000 | 180 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Agility 2 | 2500 | 180 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Energy 1 | 2000 | 180 | energy x1.1 |
| GA: Ship Energy 2 | 2500 | 180 | energy x1.1 |
| GA: EW Range 1 | 2000 | 180 | energy lifespan x1.1 |
| GA: EW Range 2 | 2500 | 180 | energy lifespan x1.1 |
| GA: PW&EW Damage 1 | 2000 | 180 | gun damage x1.1 |
| GA: PW&EW Damage 2 | 2500 | 180 | gun damage x1.1 |
| GA: Faster He3 1 | 2000 | 180 | mining rate x1.1 |
| GA: Faster He3 2 | 2500 | 180 | mining rate x1.1 |
| GA: He3 Yield 1 | 2000 | 180 | mining yield x1.1 |
| GA: He3 Yield 2 | 2500 | 180 | mining yield x1.1 |
| GA: Missile Damage 1 | 2000 | 180 | missile damage x1.1 |
| GA: Missile Damage 2 | 2500 | 180 | missile damage x1.1 |
| GA: Missile Track 1 | 2000 | 180 | missile turn x1.1 |
| GA: Missile Track 2 | 2500 | 180 | missile turn x1.1 |
| GA: PW Range 1 | 2000 | 180 | ammo speed x1.1 |
| GA: PW Range 2 | 2500 | 180 | ammo speed x1.1 |
| GA: Ripcord 1 | 2000 | 180 | ripcord speed x1.15 |
| GA: Ripcord 2 | 2500 | 180 | ripcord speed x1.15 |
| GA: Ship Sensors 1 | 2000 | 180 | scan x1.1 |
| GA: Ship Sensors 2 | 2500 | 180 | scan x1.1 |
| GA: Ship Hull 1 | 2000 | 180 | ship armor x1.1 |
| GA: Ship Hull 2 | 2500 | 180 | ship armor x1.1 |
| GA: Ship Shield 1 | 2000 | 180 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Shield 2 | 2500 | 180 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Signature 1 | 2000 | 180 | signature resistance x1.1 |
| GA: Ship Signature 2 | 2500 | 180 | signature resistance x1.1 |
| GA: Ship Speed 1 | 4000 | 180 | speed x1.1 |
| GA: Ship Speed 2 | 5000 | 180 | speed x1.1 |
| GA: Station Hull 1 | 2000 | 180 | station armor x1.1; station armor regen x1.1 |
| GA: Station Hull 2 | 2500 | 180 | station armor x1.1; station armor regen x1.1 |
| GA: Station Shield 1 | 2000 | 180 | station shield x1.1; station shield regen x1.1 |
| GA: Station Shield 2 | 2500 | 180 | station shield x1.1; station shield regen x1.1 |


## 5 — Omicron

Starting tech bits: 6,16,31,34,37,45,59,73,87,88,93,98,102,107,108,110,126,127,144,145,146,147,154,163,166,168,170,171,182,187,208,209,355. All four enabled path-switch bits (1,2,3,4) are added by ResearchSettings. No-development bits: 5,6,7,8,13,14,15,16,17,18,28,32,34,36,37,38,45,46,51,52,57,59,72,87,88,89,98,100,102,103,108,110,111,114,116,125,126,130,132,144,145,146,147,154,157,163,166,168,170,171,177,180,182,183,186,187,190,194,195,196,197,202,206,207,210,211,212,213,216,220,224,225,226,243,355.

Home 715 Garrison: armor 20000, shield 10000, income 0; grants [17], local [99,239,241,255]. Base payday fraction +0; starting-money addition +0; pod 744. Negative additions are retained.

Nonidentity base GA multipliers:

| Index | Attribute | Factor |
|---|---|---|
| 2 | turn rate | 1.2 |
| 3 | turn torque | 1.3 |
| 6 | station shield | 0.95 |
| 7 | station shield regen | 0.9 |
| 9 | ship shield | 0.95 |
| 10 | ship shield regen | 0.9 |
| 11 | scan | 1.1 |
| 12 | signature resistance | 1.05 |
| 13 | energy | 0.95 |
| 14 | ammo speed | 1.1 |
| 15 | energy lifespan | 0.9 |
| 19 | mining capacity | 0.85 |
| 21 | gun damage | 1.05 |
| 24 | development time | 2.5 |

Starting hulls (including autonomous types):

| ID | Hull | Human | HP raw/effective | Shield max initial | Speed raw/effective | Thrust | Mass | Signature | Guns fixed/total | Ammo | Magazine/dispenser/chaff |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 710 | Scout | True | 200/200 | 142.5 | 160/160 | 1200 | 40 | 0.75 | 1/1 | 960 | 10/2/4 |
| 715 | Fighter | True | 300/300 | 142.5 | 100/100 | 750 | 30 | 1 | 2/2 | 720 | 20/1/4 |
| 719 | Lg Std Constructor | False | 2880/2880 | 285 | 60/60 | 500000 | 50000 | 1.5 | 0/0 | 0 | 0/0/0 |
| 725 | Carrier [SR] | False | 2000/2000 | 0 | 50/50 | 500000 | 50000 | 2 | 0/0 | 1600 | 60/8/8 |
| 738 | Miner | False | 1056/1056 | 285 | 100/100 | 1000 | 100 | 1.5 | 0/0 | 0 | 0/0/0 |
| 744 | Lifepod | False | 25/25 | 0 | 60/60 | 150 | 10 | 0.5 | 0/0 | 0 | 0/0/0 |
| 770 | Sm Std Constructor | False | 1440/1440 | 285 | 60/60 | 2000 | 200 | 1.5 | 0/0 | 0 | 0/0/0 |

Starting human hardpoints/equipment and purchasable weapons:

| Hull | Hardpoints (mask; fixed) | Equipment masks | Initial fitting guns | Initial fitting afterburners |
|---|---|---|---|---|
| 710 | 3;True | 1,0,129,7169,1,0,0,16 | 33 PW Gat Gun 1; 91 ER Nanite 1; 92 EW Disruptor 1 |  |
| 715 | 1;True; 1;True | 1,0,129,1024,1,0,0,3 | 33 PW Gat Gun 1; 92 EW Disruptor 1 | 23 Booster 1 |

Reachable human hulls (raw; cumulative baseline unlock time is in the plan appendix):

| ID | Hull | HP | Speed | Thrust/mass | Signature | Fixed/total | Magazine/cargo slots | Equipment masks |
|---|---|---|---|---|---|---|---|---|
| 703 | Corvette | 2000 | 80 | 12500/625 | 2.5 | 2/4 | 80/5 | 2,0,771,2064,4,0,0,0 |
| 704 | Bomber | 800 | 60 | 750/50 | 2 | 2/4 | 60/5 | 1,0,258,2048,2,0,0,0 |
| 710 | Scout | 200 | 160 | 1200/40 | 0.75 | 1/1 | 10/5 | 1,0,129,7169,1,0,0,16 |
| 711 | Stealth Fighter | 150 | 140 | 2160/60 | 0.5 | 2/2 | 20/5 | 1,0,259,4096,1,3,0,0 |
| 712 | Interceptor | 700 | 80 | 400/20 | 1 | 2/2 | 0/5 | 1,0,0,2,0,0,0,33 |
| 714 | Enh Fighter | 350 | 100 | 750/30 | 1 | 3/3 | 20/5 | 1,0,129,1024,1,0,0,7 |
| 715 | Fighter | 300 | 100 | 750/30 | 1 | 2/2 | 20/5 | 1,0,129,1024,1,0,0,3 |
| 720 | Stealth Bomber | 150 | 80 | 2160/120 | 1 | 2/2 | 30/5 | 1,0,258,0,1,3,0,0 |
| 722 | Cruiser | 4000 | 70 | 50000/5000 | 4 | 1/5 | 160/5 | 2,0,512,0,4,0,0,0 |
| 727 | Attack Carrier [SR] | 3000 | 75 | 50000/5000 | 1.5 | 1/3 | 60/5 | 2,0,2,3073,4,0,0,0 |
| 732 | Fighter/Bomber | 450 | 70 | 1000/40 | 1.5 | 3/3 | 25/5 | 1,0,129,1024,1,0,0,5 |
| 733 | Hvy Scout | 250 | 180 | 1440/40 | 0.5 | 1/1 | 20/5 | 1,0,4225,7169,1,0,0,16 |
| 734 | Stealth Bomber [NG] | 150 | 80 | 2160/120 | 1.5 | 2/2 | 30/5 | 1,0,64,0,1,1,0,0 |
| 781 | Hvy Interceptor | 750 | 90 | 400/20 | 1 | 3/3 | 0/5 | 1,0,0,2,0,0,0,33 |
| 785 | Adv Scout | 225 | 180 | 1200/40 | 0.5 | 1/1 | 15/5 | 1,0,129,7169,1,0,0,16 |
| 786 | Adv Stl Fighter | 200 | 160 | 2160/60 | 0.5 | 2/2 | 30/5 | 1,0,259,4096,1,3,0,0 |
| 787 | Assault Ship | 3000 | 120 | 37500/2500 | 1 | 1/3 | 60/5 | 2,0,512,3073,4,2,0,0 |
| 788 | Adv Fighter | 450 | 120 | 750/30 | 1 | 3/3 | 25/5 | 1,0,129,1024,1,0,0,15 |
| 789 | Hvy Bomber | 1200 | 75 | 750/50 | 2 | 2/4 | 75/5 | 1,0,1794,2048,2,0,0,0 |
| 790 | Gunship | 800 | 80 | 1000/50 | 2 | 1/3 | 40/5 | 1,0,259,0,2,0,0,0 |
| 791 | Devastator | 3500 | 75 | 25000/2500 | 3.5 | 1/5 | 0/5 | 2,0,0,0,4,0,0,0 |
| 792 | Adv Devastator | 4000 | 75 | 50000/3000 | 4 | 2/6 | 160/5 | 2,0,512,0,4,0,0,0 |
| 793 | Hvy Troop Transport | 975 | 75 | 1350/90 | 1 | 1/2 | 45/5 | 1,0,0,0,2,0,0,0 |
| 795 | Troop Transport | 750 | 60 | 675/45 | 2 | 1/1 | 15/5 | 1,0,0,0,2,0,0,0 |

Early and late weapons (side-buyable; a fitting local hull/mount is still required). Listed DPS is raw direct projectile power / burst time, before damage matrix and GA; missile cycle is ready + lock, not an observed launch interval:

| Part | Weapon | Initially buyable | Raw DPS / missile cycle | Damage type | Energy/ammo per shot |
|---|---|---|---|---|---|
| 5 | LRM Aleph Res 1 | False | 0.25 | 6 | magazine |
| 20 | LRM Killer 1 | False | 0.25 | 3 | magazine |
| 30 | LRM Killer 2 | False | 0.25 | 3 | magazine |
| 31 | LRM Hunter 3 | False | 1.25 | 4 | magazine |
| 33 | PW Gat Gun 1 | True | 32 | 2 | 0/1 |
| 50 | EW Utl Can 1 | True | 27.545 | 1 | 20/0 |
| 51 | EW Utl Can 2 | False | 31.138 | 1 | 20/0 |
| 52 | EW Utl Can 3 | False | 38.922 | 1 | 20/0 |
| 60 | EW Sniper 1 | False | 28 | 4 | 8/0 |
| 61 | EW Sniper 2 | False | 36 | 4 | 7/0 |
| 67 | EW SkyRip | False | 149.254 | 15 | 8/0 |
| 71 | PW Gat Gun 2 | False | 40 | 2 | 0/1 |
| 72 | PW Gat Gun 3 | False | 50 | 2 | 0/1 |
| 87 | PW Mini-AC 1 | True | 90.361 | 11 | 0/1 |
| 88 | PW Mini-AC 2 | False | 99.398 | 11 | 0/1 |
| 89 | PW Mini-AC 3 | False | 109.337 | 11 | 0/1 |
| 90 | LRM Aleph Res 2 | False | 0.25 | 6 | magazine |
| 91 | ER Nanite 1 | True | -90 | 10 | 30/0 |
| 92 | EW Disruptor 1 | True | 12.8 | 3 | 7.5/0 |
| 93 | EW Disruptor 2 | False | 16 | 3 | 7.5/0 |
| 94 | EW Disruptor 3 | False | 20 | 3 | 7.5/0 |
| 97 | LRM Aleph Res 3 | False | 0.25 | 6 | magazine |
| 98 | MRM Seek 1 QL | False | 2.25 | 4 | magazine |
| 99 | MRM Seek 2 QL | False | 1.75 | 4 | magazine |
| 100 | PW EMP Can 1 | False | 36 | 13 | 0/2 |
| 101 | PW EMP Can 2 | False | 45 | 13 | 0/2 |
| 102 | MRM Seek 3 QL | False | 1.25 | 4 | magazine |
| 103 | MRM Quickfire 1 | True | 0.5 | 14 | magazine |
| 104 | MRM Quickfire 2 | False | 0.45 | 14 | magazine |
| 105 | MRM Quickfire 3 | False | 0.4 | 14 | magazine |
| 106 | SRM Dumb 1 QL | False | 0.25 | 4 | magazine |
| 107 | SRM Dumb 2 QL | False | 0.25 | 4 | magazine |
| 108 | SRM Dumb 3 QL | False | 0.25 | 4 | magazine |
| 111 | EW Galv Blstr | False | 28 | 7 | 12/0 |
| 115 | SRM Nerve Gas | False | 0.25 | 12 | magazine |
| 120 | PW Mini-Gun 1 | True | 50 | 5 | 0/1 |
| 121 | PW Mini-Gun 2 | False | 64.103 | 5 | 0/1 |
| 122 | PW Mini-Gun 3 | False | 78.125 | 5 | 0/1 |
| 126 | XRM Anti-Base 1 | False | 0.25 | 8 | magazine |
| 127 | XRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 130 | PW AutoCan 1 | True | 117.952 | 11 | 0/1 |
| 131 | PW AutoCan 2 | False | 149.277 | 11 | 0/1 |
| 132 | PW AutoCan 3 | False | 186.627 | 11 | 0/1 |
| 140 | PW SkyCap 1 | True | 60 | 11 | 0/4 |
| 141 | PW SkyCap 2 | False | 75 | 11 | 0/4 |
| 142 | PW SkyCap 3 | False | 93.75 | 11 | 0/4 |
| 150 | SRM Dumbfire 1 | True | 0.25 | 4 | magazine |
| 151 | SRM Dumbfire 2 | False | 0.25 | 4 | magazine |
| 152 | SRM Dumbfire 3 | False | 0.25 | 4 | magazine |
| 153 | MRM Seeker 1 | True | 2.25 | 4 | magazine |
| 154 | MRM Seeker 2 | False | 1.75 | 4 | magazine |
| 155 | LRM Hunter 1 | True | 2.25 | 4 | magazine |
| 156 | MRM Seeker 3 | False | 1.25 | 4 | magazine |
| 159 | LRM Hunter 2 | False | 1.75 | 4 | magazine |
| 165 | XRP Anti-Base 1 | False | 0.25 | 8 | magazine |
| 173 | XRP Anti-Base 2 | False | 0.25 | 8 | magazine |
| 190 | ER Nanite 2 | False | -120 | 10 | 30/0 |
| 212 | PW A-B Hammer 1 | True | 57.6 | 8 | 0/8 |
| 213 | PW A-B Hammer 2 | False | 76.8 | 8 | 0/8 |
| 214 | PW A-B Hammer 3 | False | 96 | 8 | 0/8 |
| 215 | PW A-B Omega 1 | False | 200 | 8 | 0/100 |
| 216 | PW A-B Omega 2 | False | 300 | 8 | 0/100 |
| 222 | EW Seismic Cannon | False | 40 | 9 | 10/0 |
| 223 | ER Nanite 3 | False | -150 | 10 | 30/0 |

Station branches and baseline serial position (constructor bucket only, excluding travel):

| Station | Required/effect/local bits | Direct cost/seconds | Baseline step/seconds/cost |
|---|---|---|---|
| 702 | Outpost [16,255]/[12]/[99,255] | 5000/90 | 16/4315/72510 |
| 703 | Shipyard [1,16,239]/[11,227]/[99,240,255] | 10000/300 | 17/4615/82510 |
| 711 | Supremacy [4,16,239]/[13,143,175,216,225]/[99,255] | 10000/300 | 22/6115/122510 |
| 701 | Shipyard (Dry) [1,16,141,239]/[11,185,227]/[99,240,255] | 10000/300 | 41/11815/225010 |
| 707 | Supremacy (Adv) [4,16,179,239]/[13,14,143,174,175,216,217,225,292]/[99,255] | 10000/300 | 46/13315/254010 |
| 712 | Expansion [2,16,239]/[8,143,175,216,226]/[99,255] | 10000/300 | 65/19015/316510 |
| 708 | Expansion (Adv) [2,16,119,239]/[8,15,143,174,175,216,217,226,290]/[99,255] | 10000/300 | 80/23515/397510 |
| 713 | Tactical [3,16,239]/[143,216,224,243]/[99,255] | 10000/300 | 97/28615/480010 |
| 709 | Tactical (Adv) [3,16,178,239]/[7,143,216,217,224,243,291]/[99,255] | 10000/300 | 111/32815/567010 |
| 710 | Garrison (Str) [16,183,255]/[17,18,293]/[92,99,239,255] | 20000/300 | 125/37015/644510 |
| 747 | TeleRefinery [16,255]/[]/[] | 3800/90 | 146/42265/733310 |


Reachable GA developments (each completed factor multiplies the base GA; bucket cost/time were frozen at creation):

| Development | Cost | Seconds | Factors |
|---|---|---|---|
| GA: Ship Acceleration 1 | 2000 | 300 | thrust x1.1 |
| GA: Ship Acceleration 2 | 2500 | 300 | thrust x1.1 |
| GA: Ship Agility 1 | 2000 | 300 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Agility 2 | 2500 | 300 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Energy 1 | 2000 | 300 | energy x1.1 |
| GA: Ship Energy 2 | 2500 | 300 | energy x1.1 |
| GA: EW Range 1 | 2000 | 300 | energy lifespan x1.1 |
| GA: EW Range 2 | 2500 | 300 | energy lifespan x1.1 |
| GA: PW&EW Damage 1 | 2000 | 300 | gun damage x1.1 |
| GA: PW&EW Damage 2 | 2500 | 300 | gun damage x1.1 |
| GA: Faster He3 1 | 2000 | 300 | mining rate x1.1 |
| GA: Faster He3 2 | 2500 | 300 | mining rate x1.1 |
| GA: He3 Yield 1 | 2000 | 300 | mining yield x1.1 |
| GA: He3 Yield 2 | 2500 | 300 | mining yield x1.1 |
| GA: Missile Damage 1 | 2000 | 300 | missile damage x1.1 |
| GA: Missile Damage 2 | 2500 | 300 | missile damage x1.1 |
| GA: Missile Track 1 | 2000 | 300 | missile turn x1.1 |
| GA: Missile Track 2 | 2500 | 300 | missile turn x1.1 |
| GA: PW Range 1 | 2000 | 300 | ammo speed x1.1 |
| GA: PW Range 2 | 2500 | 300 | ammo speed x1.1 |
| GA: Ripcord 1 | 2000 | 300 | ripcord speed x1.15 |
| GA: Ripcord 2 | 2500 | 300 | ripcord speed x1.15 |
| GA: Ship Sensors 1 | 2000 | 300 | scan x1.1 |
| GA: Ship Sensors 2 | 2500 | 300 | scan x1.1 |
| GA: Ship Hull 1 | 2000 | 300 | ship armor x1.1 |
| GA: Ship Hull 2 | 2500 | 300 | ship armor x1.1 |
| GA: Ship Shield 1 | 2000 | 300 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Shield 2 | 2500 | 300 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Signature 1 | 2000 | 300 | signature resistance x1.1 |
| GA: Ship Signature 2 | 2500 | 300 | signature resistance x1.1 |
| GA: Ship Speed 1 | 4000 | 300 | speed x1.1 |
| GA: Ship Speed 2 | 5000 | 300 | speed x1.1 |
| GA: Station Hull 1 | 2000 | 300 | station armor x1.1; station armor regen x1.1 |
| GA: Station Hull 2 | 2500 | 300 | station armor x1.1; station armor regen x1.1 |
| GA: Station Shield 1 | 2000 | 300 | station shield x1.1; station shield regen x1.1 |
| GA: Station Shield 2 | 2500 | 300 | station shield x1.1; station shield regen x1.1 |


## 18 — Gigacorp

Starting tech bits: 6,20,34,59,80,86,98,102,108,110,127,144,145,146,154,165,169,170,187,208,355. All four enabled path-switch bits (1,2,3,4) are added by ResearchSettings. No-development bits: 5,6,7,8,13,14,15,17,18,20,28,32,34,36,37,38,45,46,51,52,57,59,72,87,88,89,98,100,102,103,108,110,111,114,116,125,126,130,132,144,145,146,154,157,163,165,166,169,170,177,180,182,183,186,187,190,194,195,196,197,202,206,207,210,211,212,213,216,220,224,225,226,243,355.

Home 15 Garrison: armor 20000, shield 10000, income 0; grants [17], local [99,239,241,255]. Base payday fraction +0.25; starting-money addition +0.25; pod 244. Negative additions are retained.

Nonidentity base GA multipliers:

| Index | Attribute | Factor |
|---|---|---|
| 0 | speed | 1.1 |
| 1 | thrust | 0.9 |
| 3 | turn torque | 0.8 |
| 8 | ship armor | 0.85 |
| 9 | ship shield | 1.1 |
| 19 | mining capacity | 1.25 |

Starting hulls (including autonomous types):

| ID | Hull | Human | HP raw/effective | Shield max initial | Speed raw/effective | Thrust | Mass | Signature | Guns fixed/total | Ammo | Magazine/dispenser/chaff |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 210 | Scout | True | 200/170 | 165 | 160/176 | 1200 | 40 | 0.75 | 1/1 | 800 | 10/2/4 |
| 217 | Lg Enh Constructor | False | 3600/3060 | 330 | 80/88 | 500000 | 50000 | 1.25 | 0/0 | 0 | 0/0/0 |
| 237 | Enh Miner | False | 1320/1122 | 330 | 110/121 | 1000 | 100 | 1.25 | 0/0 | 0 | 0/0/0 |
| 244 | Lifepod | False | 25/21.25 | 0 | 60/66 | 150 | 10 | 0.5 | 0/0 | 0 | 0/0/0 |
| 248 | Lt Carrier [SR] | False | 1500/1275 | 0 | 50/55 | 500000 | 50000 | 2 | 0/0 | 1600 | 60/8/8 |
| 271 | Sm Enh Constructor | False | 1800/1530 | 330 | 80/88 | 2000 | 200 | 1.25 | 0/0 | 0 | 0/0/0 |

Starting human hardpoints/equipment and purchasable weapons:

| Hull | Hardpoints (mask; fixed) | Equipment masks | Initial fitting guns | Initial fitting afterburners |
|---|---|---|---|---|
| 210 | 3;True | 1,0,129,7169,1,0,0,16 | 33 PW Gat Gun 1; 91 ER Nanite 1 |  |

Reachable human hulls (raw; cumulative baseline unlock time is in the plan appendix):

| ID | Hull | HP | Speed | Thrust/mass | Signature | Fixed/total | Magazine/cargo slots | Equipment masks |
|---|---|---|---|---|---|---|---|---|
| 34 | Corvette | 2000 | 80 | 12500/625 | 2.5 | 2/4 | 80/5 | 2,0,771,2064,4,0,0,0 |
| 62 | Lxy Cruiser | 4000 | 70 | 50000/5000 | 6 | 1/5 | 200/5 | 2,0,520,0,4,0,0,0 |
| 78 | Attack Carrier [SR] | 3000 | 75 | 50000/5000 | 1.5 | 1/3 | 60/5 | 2,0,2,3073,4,0,0,0 |
| 85 | Hvy Scout | 250 | 180 | 1440/40 | 0.5 | 1/1 | 20/5 | 1,0,4225,7169,1,0,0,16 |
| 92 | Lxy Scout | 275 | 180 | 1440/40 | 0.6 | 1/1 | 20/5 | 1,0,4225,7169,1,0,0,16 |
| 127 | Fighter/Bomber | 450 | 70 | 1000/40 | 1.5 | 3/3 | 25/5 | 1,0,133,1024,1,0,0,5 |
| 148 | Stealth Bomber [NG] | 150 | 80 | 2160/120 | 1.5 | 2/2 | 30/5 | 1,0,64,0,1,1,0,0 |
| 204 | Bomber | 800 | 60 | 750/50 | 2 | 2/4 | 60/5 | 1,0,294,0,2,0,0,0 |
| 210 | Scout | 200 | 160 | 1200/40 | 0.75 | 1/1 | 10/5 | 1,0,129,7169,1,0,0,16 |
| 211 | Stealth Fighter | 150 | 140 | 2160/60 | 0.5 | 2/2 | 20/5 | 1,0,259,4096,1,3,0,0 |
| 212 | Interceptor | 700 | 80 | 400/20 | 1 | 2/2 | 0/5 | 1,0,0,2,0,0,0,33 |
| 214 | Enh Fighter | 350 | 100 | 750/30 | 1 | 3/3 | 20/5 | 1,0,129,1024,1,0,0,7 |
| 215 | Patroller | 300 | 140 | 1080/36 | 1 | 2/2 | 15/5 | 1,0,129,7169,1,0,0,17 |
| 220 | Stealth Bomber | 150 | 80 | 2160/120 | 1 | 2/2 | 30/5 | 1,0,262,0,1,3,0,0 |
| 221 | Lt Cruiser | 3500 | 75 | 25000/2500 | 4 | 1/4 | 120/5 | 2,0,520,0,4,0,0,0 |
| 222 | Devastator | 3500 | 75 | 25000/2500 | 3.5 | 1/5 | 0/5 | 2,0,0,0,4,0,0,0 |
| 251 | Lxy Interceptor | 825 | 90 | 400/20 | 1 | 3/3 | 0/5 | 1,0,0,2,0,0,0,33 |
| 256 | Lxy Stl Fighter | 220 | 160 | 2160/60 | 0.5 | 2/2 | 40/5 | 1,0,259,4096,1,3,0,0 |
| 258 | Lxy Fighter | 495 | 120 | 750/30 | 1 | 4/4 | 25/5 | 1,0,129,7169,1,0,0,15 |
| 281 | Hvy Interceptor | 750 | 90 | 400/20 | 1 | 3/3 | 0/5 | 1,0,0,2,0,0,0,33 |
| 285 | Adv Scout | 225 | 180 | 1200/40 | 0.5 | 1/1 | 15/5 | 1,0,129,7169,1,0,0,16 |
| 286 | Adv Stl Fighter | 200 | 160 | 2160/60 | 0.5 | 2/2 | 30/5 | 1,0,259,4096,1,3,0,0 |
| 287 | Assault Ship | 3000 | 120 | 37500/2500 | 1 | 1/3 | 0/5 | 2,0,512,3073,4,2,0,0 |
| 288 | Adv Fighter | 450 | 120 | 750/30 | 1 | 3/3 | 25/5 | 1,0,129,1024,1,0,0,15 |
| 289 | Hvy Bomber | 1200 | 75 | 750/50 | 2 | 2/4 | 75/5 | 1,0,1830,0,2,0,0,0 |
| 290 | Gunship | 800 | 80 | 1000/50 | 2 | 1/3 | 40/5 | 1,0,259,0,2,0,0,0 |
| 291 | Destroyer | 1500 | 100 | 25000/2500 | 3 | 1/1 | 120/5 | 2,0,529,0,4,0,0,0 |
| 293 | Hvy Troop Transport | 975 | 75 | 1350/90 | 1 | 0/0 | 45/5 | 1,0,32,0,2,0,0,0 |
| 294 | Lxy Hvy Troop Transport | 1073 | 75 | 1800/120 | 1 | 0/0 | 60/5 | 1,0,32,0,2,0,0,0 |
| 295 | Troop Transport | 750 | 60 | 675/45 | 2 | 0/0 | 15/5 | 1,0,32,0,2,0,0,0 |
| 299 | Lxy Hvy Bomber | 1320 | 75 | 750/50 | 2 | 2/4 | 90/5 | 1,0,1830,0,2,0,0,0 |

Early and late weapons (side-buyable; a fitting local hull/mount is still required). Listed DPS is raw direct projectile power / burst time, before damage matrix and GA; missile cycle is ready + lock, not an observed launch interval:

| Part | Weapon | Initially buyable | Raw DPS / missile cycle | Damage type | Energy/ammo per shot |
|---|---|---|---|---|---|
| 5 | LRM Aleph Res 1 | False | 0.25 | 6 | magazine |
| 10 | XRM Cruise 1 | False | 0.25 | 8 | magazine |
| 18 | SRM Seismic | False | 0.25 | 9 | magazine |
| 20 | LRM Killer 1 | False | 0.25 | 3 | magazine |
| 30 | LRM Killer 2 | False | 0.25 | 3 | magazine |
| 31 | LRM Hunter 3 | False | 1.25 | 4 | magazine |
| 33 | PW Gat Gun 1 | True | 32 | 2 | 0/1 |
| 50 | EW Utl Can 1 | True | 27.545 | 1 | 20/0 |
| 51 | EW Utl Can 2 | False | 31.138 | 1 | 20/0 |
| 52 | EW Utl Can 3 | False | 38.922 | 1 | 20/0 |
| 60 | EW Sniper 1 | False | 28 | 4 | 8/0 |
| 61 | EW Sniper 2 | False | 36 | 4 | 7/0 |
| 67 | EW SkyRip | False | 149.254 | 15 | 8/0 |
| 71 | PW Gat Gun 2 | False | 40 | 2 | 0/1 |
| 72 | PW Gat Gun 3 | False | 50 | 2 | 0/1 |
| 87 | PW Mini-AC 1 | True | 90.361 | 11 | 0/1 |
| 88 | PW Mini-AC 2 | False | 99.398 | 11 | 0/1 |
| 89 | PW Mini-AC 3 | False | 109.337 | 11 | 0/1 |
| 90 | LRM Aleph Res 2 | False | 0.25 | 6 | magazine |
| 91 | ER Nanite 1 | True | -90 | 10 | 30/0 |
| 92 | EW Disruptor 1 | False | 12.8 | 3 | 7.5/0 |
| 93 | EW Disruptor 2 | False | 16 | 3 | 7.5/0 |
| 94 | EW Disruptor 3 | False | 20 | 3 | 7.5/0 |
| 95 | LRM Killer Swarm 1 | False | 0.25 | 3 | magazine |
| 97 | LRM Aleph Res 3 | False | 0.25 | 6 | magazine |
| 98 | MRM Seek 1 QL | False | 2.25 | 4 | magazine |
| 99 | MRM Seek 2 QL | False | 1.75 | 4 | magazine |
| 100 | PW EMP Can 1 | False | 36 | 13 | 0/2 |
| 101 | PW EMP Can 2 | False | 45 | 13 | 0/2 |
| 102 | MRM Seek 3 QL | False | 1.25 | 4 | magazine |
| 103 | MRM Quickfire 1 | False | 0.5 | 14 | magazine |
| 104 | MRM Quickfire 2 | False | 0.45 | 14 | magazine |
| 105 | MRM Quickfire 3 | False | 0.4 | 14 | magazine |
| 106 | SRM Dumb 1 QL | False | 0.25 | 4 | magazine |
| 107 | SRM Dumb 2 QL | False | 0.25 | 4 | magazine |
| 108 | SRM Dumb 3 QL | False | 0.25 | 4 | magazine |
| 111 | EW Galv Blstr | False | 28 | 7 | 12/0 |
| 115 | SRM Nerve Gas | False | 0.25 | 12 | magazine |
| 120 | PW Mini-Gun 1 | True | 50 | 5 | 0/1 |
| 121 | PW Mini-Gun 2 | False | 64.103 | 5 | 0/1 |
| 122 | PW Mini-Gun 3 | False | 78.125 | 5 | 0/1 |
| 126 | XRM Anti-Base 1 | False | 0.25 | 8 | magazine |
| 127 | XRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 130 | PW AutoCan 1 | True | 117.952 | 11 | 0/1 |
| 131 | PW AutoCan 2 | False | 149.277 | 11 | 0/1 |
| 132 | PW AutoCan 3 | False | 186.627 | 11 | 0/1 |
| 140 | PW SkyCap 1 | False | 60 | 11 | 0/4 |
| 141 | PW SkyCap 2 | False | 75 | 11 | 0/4 |
| 150 | SRM Dumbfire 1 | True | 0.25 | 4 | magazine |
| 151 | SRM Dumbfire 2 | False | 0.25 | 4 | magazine |
| 152 | SRM Dumbfire 3 | False | 0.25 | 4 | magazine |
| 153 | MRM Seeker 1 | True | 2.25 | 4 | magazine |
| 154 | MRM Seeker 2 | False | 1.75 | 4 | magazine |
| 155 | LRM Hunter 1 | True | 2.25 | 4 | magazine |
| 156 | MRM Seeker 3 | False | 1.25 | 4 | magazine |
| 157 | SRM Anti-Base 1 | True | 0.25 | 8 | magazine |
| 158 | SRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 159 | LRM Hunter 2 | False | 1.75 | 4 | magazine |
| 160 | SRM Tactical Nuke 1 | False | 0.25 | 8 | magazine |
| 162 | MRM Seismic | False | 0.25 | 9 | magazine |
| 163 | XRM Hunter Klr 1 | False | 2.25 | 4 | magazine |
| 164 | XRM Hunter Klr 2 | False | 2.25 | 4 | magazine |
| 190 | ER Nanite 2 | False | -120 | 10 | 30/0 |
| 192 | SRM EMP | False | 0.25 | 12 | magazine |
| 193 | XRM Torpedo 1 | False | 0.25 | 4 | magazine |
| 223 | ER Nanite 3 | False | -150 | 10 | 30/0 |
| 601 | SRM Anti-Base 3 | False | 0.25 | 8 | magazine |

Station branches and baseline serial position (constructor bucket only, excluding travel):

| Station | Required/effect/local bits | Direct cost/seconds | Baseline step/seconds/cost |
|---|---|---|---|
| 2 | Outpost (Lt) [20,255]/[12]/[99,255] | 3750/90 | 24/2740/111260 |
| 11 | Supremacy [4,20,239]/[13,175,216,225]/[99,255] | 10000/120 | 25/2860/121260 |
| 7 | Supremacy (Adv) [4,20,179,239]/[13,14,174,175,216,217,225,292]/[99,255] | 10000/120 | 44/5140/206260 |
| 12 | Expansion [2,20,239]/[8,175,216,226]/[99,255] | 10000/120 | 64/7540/278760 |
| 8 | Expansion (Adv) [2,20,119,239]/[8,15,174,175,216,217,226,290]/[99,255] | 10000/120 | 81/9580/367260 |
| 13 | Tactical [3,20,239]/[216,224,243]/[99,255] | 10000/120 | 99/11740/459760 |
| 9 | Tactical (Adv) [3,20,178,239]/[7,216,217,224,243,291]/[99,255] | 10000/120 | 114/13540/549260 |
| 3 | Shipyard [1,20,217,239]/[11,227]/[99,240,255] | 15000/120 | 129/15340/631760 |
| 10 | Garrison (Str) [20,183,255]/[17,18,293]/[92,99,239,255] | 20000/120 | 142/16900/741760 |
| 14 | Refinery (Lt) [20,255]/[]/[] | 2250/90 | 162/19180/825510 |
| 47 | Teleport (Lt) [20,255]/[]/[] | 2250/90 | 163/19270/827760 |
| 21 | SpecMine (Ur) [20,239,250]/[]/[] | 6000/90 | 166/19570/841010 |
| 22 | SpecMine (Ca) [20,239,248]/[]/[] | 6000/90 | 167/19660/847010 |
| 23 | SpecMine (Si) [20,239,249]/[]/[] | 8000/90 | 168/19750/855010 |


Reachable GA developments (each completed factor multiplies the base GA; bucket cost/time were frozen at creation):

| Development | Cost | Seconds | Factors |
|---|---|---|---|
| GA: Ship Acceleration 1 | 2000 | 120 | thrust x1.1 |
| GA: Ship Acceleration 2 | 2500 | 120 | thrust x1.1 |
| GA: Ship Agility 1 | 2000 | 120 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Agility 2 | 2500 | 120 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Energy 1 | 2000 | 120 | energy x1.1 |
| GA: Ship Energy 2 | 2500 | 120 | energy x1.1 |
| GA: EW Range 1 | 2000 | 120 | energy lifespan x1.1 |
| GA: EW Range 2 | 2500 | 120 | energy lifespan x1.1 |
| GA: PW&EW Damage 1 | 2000 | 120 | gun damage x1.1 |
| GA: PW&EW Damage 2 | 2500 | 120 | gun damage x1.1 |
| GA: Faster He3 1 | 2000 | 120 | mining rate x1.1 |
| GA: Faster He3 2 | 2500 | 120 | mining rate x1.1 |
| GA: He3 Yield 1 | 2000 | 120 | mining yield x1.1 |
| GA: He3 Yield 2 | 2500 | 120 | mining yield x1.1 |
| GA: Missile Damage 1 | 2000 | 120 | missile damage x1.1 |
| GA: Missile Damage 2 | 2500 | 120 | missile damage x1.1 |
| GA: Missile Track 1 | 2000 | 120 | missile turn x1.1 |
| GA: Missile Track 2 | 2500 | 120 | missile turn x1.1 |
| GA: PW Range 1 | 2000 | 120 | ammo speed x1.1 |
| GA: PW Range 2 | 2500 | 120 | ammo speed x1.1 |
| GA: Ripcord 1 | 2000 | 120 | ripcord speed x1.15 |
| GA: Ripcord 2 | 2500 | 120 | ripcord speed x1.15 |
| GA: Ship Sensors 1 | 2000 | 120 | scan x1.1 |
| GA: Ship Sensors 2 | 2500 | 120 | scan x1.1 |
| GA: Ship Hull 1 | 2000 | 120 | ship armor x1.1 |
| GA: Ship Hull 2 | 2500 | 120 | ship armor x1.1 |
| GA: Ship Shield 1 | 2000 | 120 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Shield 2 | 2500 | 120 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Signature 1 | 2000 | 120 | signature resistance x1.1 |
| GA: Ship Signature 2 | 2500 | 120 | signature resistance x1.1 |
| GA: Ship Speed 1 | 4000 | 120 | speed x1.1 |
| GA: Ship Speed 2 | 5000 | 120 | speed x1.1 |
| GA: Station Hull 1 | 2000 | 120 | station armor x1.1; station armor regen x1.1 |
| GA: Station Hull 2 | 2500 | 120 | station armor x1.1; station armor regen x1.1 |
| GA: Station Shield 1 | 2000 | 120 | station shield x1.1; station shield regen x1.1 |
| GA: Station Shield 2 | 2500 | 120 | station shield x1.1; station shield regen x1.1 |


## 25 — Iron Coalition

Starting tech bits: 6,21,31,34,37,45,59,73,87,88,93,98,102,107,108,110,126,127,144,145,146,147,154,163,165,166,168,170,182,187,208,209,355. All four enabled path-switch bits (1,2,3,4) are added by ResearchSettings. No-development bits: 5,6,7,8,13,14,15,17,18,21,28,32,34,36,37,38,45,46,51,52,57,59,72,87,88,89,98,100,102,103,108,110,111,114,116,125,126,130,132,144,145,146,147,154,157,163,165,166,168,170,177,180,182,183,186,187,190,194,195,196,197,202,206,210,211,212,213,216,220,224,225,226,243,355.

Home 115 Garrison: armor 20000, shield 10000, income 0; grants [17], local [99,239,241,255]. Base payday fraction +-0.125; starting-money addition +-0.125; pod 444. Negative additions are retained.

Nonidentity base GA multipliers:

| Index | Attribute | Factor |
|---|---|---|
| 4 | station armor | 1.15 |
| 6 | station shield | 1.15 |
| 8 | ship armor | 0.95 |
| 12 | signature resistance | 0.85 |
| 13 | energy | 1.2 |
| 17 | mining rate | 0.85 |
| 19 | mining capacity | 0.75 |
| 21 | gun damage | 1.1 |
| 22 | missile damage | 1.1 |

Starting hulls (including autonomous types):

| ID | Hull | Human | HP raw/effective | Shield max initial | Speed raw/effective | Thrust | Mass | Signature | Guns fixed/total | Ammo | Magazine/dispenser/chaff |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 410 | Scout | True | 200/190 | 150 | 160/160 | 1200 | 40 | 0.75 | 1/1 | 960 | 10/2/4 |
| 415 | Lt Interceptor | True | 600/570 | 0 | 75/75 | 400 | 20 | 1 | 2/2 | 540 | 0/0/4 |
| 419 | Lg Std Constructor | False | 2880/2736 | 300 | 60/60 | 500000 | 50000 | 1.5 | 0/0 | 0 | 0/0/0 |
| 425 | Carrier [SR] | False | 2000/1900 | 0 | 50/50 | 500000 | 50000 | 2 | 0/0 | 1600 | 60/8/8 |
| 438 | Miner | False | 1056/1003.2 | 300 | 100/100 | 1000 | 100 | 1.5 | 0/0 | 0 | 0/0/0 |
| 444 | Lifepod | False | 25/23.75 | 0 | 60/60 | 150 | 10 | 0.5 | 0/0 | 0 | 0/0/0 |
| 470 | Sm Std Constructor | False | 1440/1368 | 300 | 60/60 | 2000 | 200 | 1.5 | 0/0 | 0 | 0/0/0 |

Starting human hardpoints/equipment and purchasable weapons:

| Hull | Hardpoints (mask; fixed) | Equipment masks | Initial fitting guns | Initial fitting afterburners |
|---|---|---|---|---|
| 410 | 3;True | 1,0,129,7169,1,0,0,16 | 33 PW Gat Gun 1; 91 ER Nanite 1; 92 EW Disruptor 1 |  |
| 415 | 24;True; 24;True | 1,0,0,0,0,0,0,33 | 120 PW Mini-Gun 1 | 125 Afterburner 1 |

Reachable human hulls (raw; cumulative baseline unlock time is in the plan appendix):

| ID | Hull | HP | Speed | Thrust/mass | Signature | Fixed/total | Magazine/cargo slots | Equipment masks |
|---|---|---|---|---|---|---|---|---|
| 36 | Corvette | 2000 | 80 | 12500/625 | 2.5 | 2/4 | 80/5 | 2,0,771,2064,4,0,0,0 |
| 80 | Attack Carrier [SR] | 3000 | 75 | 50000/5000 | 1.5 | 1/3 | 60/5 | 2,0,2,3073,4,0,0,0 |
| 87 | Hvy Scout | 250 | 180 | 1440/40 | 0.5 | 1/1 | 20/5 | 1,0,4225,7169,1,0,0,16 |
| 150 | Stealth Bomber [NG] | 150 | 80 | 2160/120 | 1.5 | 2/2 | 30/5 | 1,0,64,0,1,1,0,0 |
| 158 | Fighter/Bomber | 450 | 70 | 1000/40 | 1.5 | 3/3 | 25/5 | 1,0,133,1024,1,0,0,5 |
| 404 | Bomber | 800 | 60 | 750/50 | 2 | 2/4 | 60/5 | 1,0,294,0,2,0,0,0 |
| 410 | Scout | 200 | 160 | 1200/40 | 0.75 | 1/1 | 10/5 | 1,0,129,7169,1,0,0,16 |
| 411 | Stealth Fighter | 150 | 140 | 2160/60 | 0.5 | 2/2 | 20/5 | 1,0,259,4096,1,3,0,0 |
| 412 | Interceptor | 700 | 80 | 400/20 | 1 | 2/2 | 0/5 | 1,0,0,2,0,0,0,33 |
| 414 | Enh Fighter | 350 | 100 | 750/30 | 1 | 3/3 | 20/5 | 1,0,129,1024,1,0,0,7 |
| 415 | Lt Interceptor | 600 | 75 | 400/20 | 1 | 2/2 | 0/5 | 1,0,0,0,0,0,0,33 |
| 420 | Stealth Bomber | 150 | 80 | 2160/120 | 1 | 2/2 | 30/5 | 1,0,262,0,1,3,0,0 |
| 421 | Frigate | 3500 | 75 | 25000/2500 | 3 | 1/4 | 120/5 | 2,0,536,0,4,2,0,0 |
| 422 | Cruiser | 4000 | 70 | 50000/5000 | 4 | 1/5 | 160/5 | 2,0,536,0,4,0,0,0 |
| 481 | Hvy Interceptor | 750 | 90 | 400/20 | 1 | 3/3 | 0/5 | 1,0,0,2,0,0,0,33 |
| 485 | Adv Scout | 225 | 180 | 1200/40 | 0.5 | 1/1 | 15/5 | 1,0,129,7169,1,0,0,16 |
| 486 | Adv Stl Fighter | 200 | 160 | 2160/60 | 0.5 | 2/2 | 30/5 | 1,0,259,4096,1,3,0,0 |
| 487 | Assault Ship | 3000 | 120 | 37500/2500 | 1 | 1/3 | 0/5 | 2,0,512,3073,4,2,0,0 |
| 488 | Adv Fighter | 450 | 120 | 750/30 | 1 | 3/3 | 25/5 | 1,0,129,1024,1,0,0,15 |
| 489 | Hvy Bomber | 1200 | 75 | 750/50 | 2 | 2/4 | 75/5 | 1,0,1830,0,2,0,0,0 |
| 490 | Gunship | 800 | 80 | 1000/50 | 2 | 1/3 | 40/5 | 1,0,259,0,2,0,0,0 |
| 491 | Devastator | 3500 | 75 | 25000/2500 | 3.5 | 1/5 | 0/5 | 2,0,0,0,4,0,0,0 |
| 493 | Hvy Troop Transport | 975 | 75 | 1350/90 | 1 | 0/0 | 45/5 | 1,0,32,0,2,0,0,0 |
| 495 | Troop Transport | 750 | 60 | 675/45 | 2 | 0/0 | 15/5 | 1,0,32,0,2,0,0,0 |

Early and late weapons (side-buyable; a fitting local hull/mount is still required). Listed DPS is raw direct projectile power / burst time, before damage matrix and GA; missile cycle is ready + lock, not an observed launch interval:

| Part | Weapon | Initially buyable | Raw DPS / missile cycle | Damage type | Energy/ammo per shot |
|---|---|---|---|---|---|
| 5 | LRM Aleph Res 1 | False | 0.25 | 6 | magazine |
| 10 | XRM Cruise 1 | False | 0.25 | 8 | magazine |
| 11 | XRM Cruise 2 | False | 0.25 | 8 | magazine |
| 18 | SRM Seismic | False | 0.25 | 9 | magazine |
| 20 | LRM Killer 1 | False | 0.25 | 3 | magazine |
| 30 | LRM Killer 2 | False | 0.25 | 3 | magazine |
| 31 | LRM Hunter 3 | False | 1.25 | 4 | magazine |
| 32 | XRM Hunter Klr 3 | False | 1.75 | 4 | magazine |
| 33 | PW Gat Gun 1 | True | 32 | 2 | 0/1 |
| 50 | EW Utl Can 1 | True | 27.545 | 1 | 20/0 |
| 51 | EW Utl Can 2 | False | 31.138 | 1 | 20/0 |
| 52 | EW Utl Can 3 | False | 38.922 | 1 | 20/0 |
| 60 | EW Sniper 1 | False | 28 | 4 | 8/0 |
| 61 | EW Sniper 2 | False | 36 | 4 | 7/0 |
| 67 | EW SkyRip | False | 149.254 | 15 | 8/0 |
| 71 | PW Gat Gun 2 | False | 40 | 2 | 0/1 |
| 72 | PW Gat Gun 3 | False | 50 | 2 | 0/1 |
| 87 | PW Mini-AC 1 | True | 90.361 | 11 | 0/1 |
| 88 | PW Mini-AC 2 | False | 99.398 | 11 | 0/1 |
| 89 | PW Mini-AC 3 | False | 109.337 | 11 | 0/1 |
| 90 | LRM Aleph Res 2 | False | 0.25 | 6 | magazine |
| 91 | ER Nanite 1 | True | -90 | 10 | 30/0 |
| 92 | EW Disruptor 1 | True | 12.8 | 3 | 7.5/0 |
| 93 | EW Disruptor 2 | False | 16 | 3 | 7.5/0 |
| 94 | EW Disruptor 3 | False | 20 | 3 | 7.5/0 |
| 95 | LRM Killer Swarm 1 | False | 0.25 | 3 | magazine |
| 96 | LRM Killer Swarm 2 | False | 0.25 | 3 | magazine |
| 97 | LRM Aleph Res 3 | False | 0.25 | 6 | magazine |
| 98 | MRM Seek 1 QL | False | 2.25 | 4 | magazine |
| 99 | MRM Seek 2 QL | False | 1.75 | 4 | magazine |
| 100 | PW EMP Can 1 | False | 36 | 13 | 0/2 |
| 101 | PW EMP Can 2 | False | 45 | 13 | 0/2 |
| 102 | MRM Seek 3 QL | False | 1.25 | 4 | magazine |
| 103 | MRM Quickfire 1 | True | 0.5 | 14 | magazine |
| 104 | MRM Quickfire 2 | False | 0.45 | 14 | magazine |
| 105 | MRM Quickfire 3 | False | 0.4 | 14 | magazine |
| 106 | SRM Dumb 1 QL | False | 0.25 | 4 | magazine |
| 107 | SRM Dumb 2 QL | False | 0.25 | 4 | magazine |
| 108 | SRM Dumb 3 QL | False | 0.25 | 4 | magazine |
| 111 | EW Galv Blstr | False | 28 | 7 | 12/0 |
| 115 | SRM Nerve Gas | False | 0.25 | 12 | magazine |
| 120 | PW Mini-Gun 1 | True | 50 | 5 | 0/1 |
| 121 | PW Mini-Gun 2 | False | 64.103 | 5 | 0/1 |
| 122 | PW Mini-Gun 3 | False | 78.125 | 5 | 0/1 |
| 126 | XRM Anti-Base 1 | False | 0.25 | 8 | magazine |
| 127 | XRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 130 | PW AutoCan 1 | True | 117.952 | 11 | 0/1 |
| 131 | PW AutoCan 2 | False | 149.277 | 11 | 0/1 |
| 132 | PW AutoCan 3 | False | 186.627 | 11 | 0/1 |
| 140 | PW SkyCap 1 | True | 60 | 11 | 0/4 |
| 141 | PW SkyCap 2 | False | 75 | 11 | 0/4 |
| 142 | PW SkyCap 3 | False | 93.75 | 11 | 0/4 |
| 150 | SRM Dumbfire 1 | True | 0.25 | 4 | magazine |
| 151 | SRM Dumbfire 2 | False | 0.25 | 4 | magazine |
| 152 | SRM Dumbfire 3 | False | 0.25 | 4 | magazine |
| 153 | MRM Seeker 1 | True | 2.25 | 4 | magazine |
| 154 | MRM Seeker 2 | False | 1.75 | 4 | magazine |
| 155 | LRM Hunter 1 | True | 2.25 | 4 | magazine |
| 156 | MRM Seeker 3 | False | 1.25 | 4 | magazine |
| 157 | SRM Anti-Base 1 | True | 0.25 | 8 | magazine |
| 158 | SRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 159 | LRM Hunter 2 | False | 1.75 | 4 | magazine |
| 160 | SRM Tactical Nuke 1 | False | 0.25 | 8 | magazine |
| 161 | SRM Tactical Nuke 2 | False | 0.25 | 8 | magazine |
| 162 | MRM Seismic | False | 0.25 | 9 | magazine |
| 163 | XRM Hunter Klr 1 | False | 2.25 | 4 | magazine |
| 164 | XRM Hunter Klr 2 | False | 2.25 | 4 | magazine |
| 190 | ER Nanite 2 | False | -120 | 10 | 30/0 |
| 192 | SRM EMP | False | 0.25 | 12 | magazine |
| 193 | XRM Torpedo 1 | False | 0.25 | 4 | magazine |
| 194 | XRM Torpedo 2 | False | 0.25 | 4 | magazine |
| 223 | ER Nanite 3 | False | -150 | 10 | 30/0 |
| 601 | SRM Anti-Base 3 | False | 0.25 | 8 | magazine |

Station branches and baseline serial position (constructor bucket only, excluding travel):

| Station | Required/effect/local bits | Direct cost/seconds | Baseline step/seconds/cost |
|---|---|---|---|
| 102 | Outpost (Hvy) [21,255]/[12]/[99,255] | 5000/120 | 16/1870/74510 |
| 111 | Supremacy [4,21,239]/[13,143,175,216,225]/[99,255] | 10000/240 | 17/2110/84510 |
| 107 | Supremacy (Adv) [4,21,179,239]/[13,14,143,174,175,216,217,225,292]/[99,255] | 10000/240 | 35/4450/167010 |
| 112 | Expansion [2,21,239]/[8,143,175,216,226]/[99,255] | 10000/240 | 54/6850/229510 |
| 108 | Expansion (Adv) [2,21,119,239]/[8,15,143,174,175,216,217,226,290]/[99,255] | 10000/240 | 70/8950/315510 |
| 113 | Tactical [3,21,239]/[143,216,224,243]/[99,255] | 10000/240 | 87/11110/398010 |
| 109 | Tactical (Adv) [3,21,178,239]/[7,143,216,217,224,243,291]/[99,255] | 10000/240 | 101/12970/485010 |
| 103 | Shipyard [1,21,239]/[11,227]/[99,240,255] | 15000/120 | 115/14650/557510 |
| 101 | Shipyard (Dry) [1,21,141,239]/[11,185,227]/[99,240,255] | 15000/120 | 126/16090/654510 |
| 110 | Garrison (Str) [21,183,255]/[17,18,293]/[92,99,239,255] | 20000/240 | 135/17290/714510 |
| 147 | Teleport (Hvy) [21,255]/[]/[] | 3000/120 | 155/19600/799010 |


Reachable GA developments (each completed factor multiplies the base GA; bucket cost/time were frozen at creation):

| Development | Cost | Seconds | Factors |
|---|---|---|---|
| GA: Ship Acceleration 1 | 2000 | 120 | thrust x1.1 |
| GA: Ship Acceleration 2 | 2500 | 120 | thrust x1.1 |
| GA: Ship Agility 1 | 2000 | 120 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Agility 2 | 2500 | 120 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Energy 1 | 2000 | 120 | energy x1.1 |
| GA: Ship Energy 2 | 2500 | 120 | energy x1.1 |
| GA: EW Range 1 | 2000 | 120 | energy lifespan x1.1 |
| GA: EW Range 2 | 2500 | 120 | energy lifespan x1.1 |
| GA: PW&EW Damage 1 | 2000 | 120 | gun damage x1.1 |
| GA: PW&EW Damage 2 | 2500 | 120 | gun damage x1.1 |
| GA: Faster He3 1 | 2000 | 120 | mining rate x1.1 |
| GA: Faster He3 2 | 2500 | 120 | mining rate x1.1 |
| GA: He3 Yield 1 | 2000 | 120 | mining yield x1.1 |
| GA: He3 Yield 2 | 2500 | 120 | mining yield x1.1 |
| GA: Missile Damage 1 | 2000 | 120 | missile damage x1.1 |
| GA: Missile Damage 2 | 2500 | 120 | missile damage x1.1 |
| GA: Missile Track 1 | 2000 | 120 | missile turn x1.1 |
| GA: Missile Track 2 | 2500 | 120 | missile turn x1.1 |
| GA: PW Range 1 | 2000 | 120 | ammo speed x1.1 |
| GA: PW Range 2 | 2500 | 120 | ammo speed x1.1 |
| GA: Ripcord 1 | 2000 | 120 | ripcord speed x1.15 |
| GA: Ripcord 2 | 2500 | 120 | ripcord speed x1.15 |
| GA: Ship Sensors 1 | 2000 | 120 | scan x1.1 |
| GA: Ship Sensors 2 | 2500 | 120 | scan x1.1 |
| GA: Ship Hull 1 | 2000 | 120 | ship armor x1.1 |
| GA: Ship Hull 2 | 2500 | 120 | ship armor x1.1 |
| GA: Ship Shield 1 | 2000 | 120 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Shield 2 | 2500 | 120 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Signature 1 | 2000 | 120 | signature resistance x1.1 |
| GA: Ship Signature 2 | 2500 | 120 | signature resistance x1.1 |
| GA: Ship Speed 1 | 4000 | 120 | speed x1.1 |
| GA: Ship Speed 2 | 5000 | 120 | speed x1.1 |
| GA: Station Hull 1 | 2000 | 120 | station armor x1.1; station armor regen x1.1 |
| GA: Station Hull 2 | 2500 | 120 | station armor x1.1; station armor regen x1.1 |
| GA: Station Shield 1 | 2000 | 120 | station shield x1.1; station shield regen x1.1 |
| GA: Station Shield 2 | 2500 | 120 | station shield x1.1; station shield regen x1.1 |


## 35 — Bios

Starting tech bits: 6,31,34,37,45,47,59,73,93,98,102,107,108,110,126,127,143,144,145,146,154,165,168,169,170,188,208,209,210,355. All four enabled path-switch bits (1,2,3,4) are added by ResearchSettings. No-development bits: 5,6,7,8,13,14,15,17,18,28,32,34,36,37,38,45,46,47,51,52,57,59,72,87,88,89,98,100,102,103,108,110,111,114,116,125,126,130,132,144,145,146,154,157,163,165,166,168,169,170,177,180,182,183,186,188,190,194,195,196,197,202,206,207,210,211,212,213,216,220,224,225,226,243,355.

Home 210 Garrison (Str): armor 25000, shield 15000, income 0; grants [17,18], local [92,99,239,255]. Base payday fraction +0; starting-money addition +0; pod 344. Negative additions are retained.

Nonidentity base GA multipliers:

| Index | Attribute | Factor |
|---|---|---|
| 2 | turn rate | 1.2 |
| 3 | turn torque | 1.3 |
| 4 | station armor | 0.9 |
| 5 | station armor regen | 1.25 |
| 6 | station shield | 0.9 |
| 7 | station shield regen | 1.25 |
| 8 | ship armor | 0.9 |
| 9 | ship shield | 0.9 |
| 10 | ship shield regen | 1.25 |
| 11 | scan | 1.15 |
| 12 | signature resistance | 1.15 |
| 23 | development cost | 0.5 |
| 24 | development time | 5 |

Starting hulls (including autonomous types):

| ID | Hull | Human | HP raw/effective | Shield max initial | Speed raw/effective | Thrust | Mass | Signature | Guns fixed/total | Ammo | Magazine/dispenser/chaff |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 310 | Scout | True | 200/180 | 135 | 160/160 | 1200 | 40 | 0.75 | 1/1 | 800 | 10/2/4 |
| 315 | Fighter | True | 300/270 | 135 | 100/100 | 750 | 30 | 1 | 2/2 | 600 | 20/2/4 |
| 319 | Lg Std Constructor | False | 2880/2592 | 270 | 60/60 | 500000 | 50000 | 1.5 | 0/0 | 0 | 0/0/0 |
| 325 | Carrier [SR] | False | 2000/1800 | 0 | 50/50 | 500000 | 50000 | 2 | 0/0 | 1600 | 60/8/8 |
| 338 | Miner | False | 1056/950.4 | 270 | 100/100 | 1000 | 100 | 1.5 | 0/0 | 0 | 0/0/0 |
| 344 | Lifepod | False | 25/22.5 | 0 | 50/50 | 150 | 10 | 1 | 0/0 | 0 | 0/0/0 |
| 370 | Sm Std Constructor | False | 1440/1296 | 270 | 60/60 | 2000 | 200 | 1.5 | 0/0 | 0 | 0/0/0 |

Starting human hardpoints/equipment and purchasable weapons:

| Hull | Hardpoints (mask; fixed) | Equipment masks | Initial fitting guns | Initial fitting afterburners |
|---|---|---|---|---|
| 310 | 3;True | 1,0,129,7169,1,2,0,16 | 33 PW Gat Gun 1; 91 ER Nanite 1; 92 EW Disruptor 1 |  |
| 315 | 1;True; 1;True | 1,0,129,3072,1,2,0,3 | 33 PW Gat Gun 1; 92 EW Disruptor 1 | 23 Booster 1 |

Reachable human hulls (raw; cumulative baseline unlock time is in the plan appendix):

| ID | Hull | HP | Speed | Thrust/mass | Signature | Fixed/total | Magazine/cargo slots | Equipment masks |
|---|---|---|---|---|---|---|---|---|
| 35 | Corvette | 2000 | 80 | 12500/625 | 2.5 | 2/4 | 80/5 | 2,0,771,3088,4,2,0,0 |
| 79 | Attack Carrier [SR] | 3000 | 75 | 50000/5000 | 1.5 | 1/3 | 60/5 | 2,0,2,3073,4,2,0,0 |
| 86 | Hvy Scout | 250 | 180 | 1440/40 | 0.5 | 1/1 | 20/5 | 1,0,4225,7169,1,2,0,16 |
| 149 | Stealth Bomber [NG] | 150 | 80 | 2160/120 | 1.5 | 2/2 | 30/5 | 1,0,64,0,1,1,0,0 |
| 156 | Fighter/Bomber | 450 | 70 | 1000/34 | 1.5 | 3/3 | 25/5 | 1,0,133,1024,1,2,0,5 |
| 304 | Bomber | 800 | 60 | 750/50 | 2 | 2/4 | 60/5 | 1,0,294,0,2,2,0,0 |
| 310 | Scout | 200 | 160 | 1200/40 | 0.75 | 1/1 | 10/5 | 1,0,129,7169,1,2,0,16 |
| 311 | Stealth Fighter | 150 | 140 | 2160/52 | 0.5 | 2/2 | 20/5 | 1,0,259,4096,1,3,0,0 |
| 312 | Interceptor | 700 | 80 | 400/20 | 1 | 2/2 | 0/5 | 1,0,0,2,0,2,0,33 |
| 313 | Lt Stl Fighter | 140 | 120 | 2160/56 | 0.6 | 2/2 | 20/5 | 1,0,259,4096,1,3,0,0 |
| 314 | Enh Fighter | 350 | 100 | 750/30 | 1 | 3/3 | 20/5 | 1,0,129,3072,1,2,0,7 |
| 315 | Fighter | 300 | 100 | 750/30 | 1 | 2/2 | 20/5 | 1,0,129,3072,1,2,0,3 |
| 320 | Stealth Bomber | 150 | 80 | 2160/120 | 1 | 2/2 | 30/5 | 1,0,262,0,1,3,0,0 |
| 321 | Devastator | 3500 | 75 | 25000/2500 | 3.5 | 1/5 | 0/5 | 2,0,0,0,4,2,0,0 |
| 322 | Cruiser | 4000 | 70 | 50000/5000 | 4 | 1/5 | 160/5 | 2,0,520,0,4,2,0,0 |
| 324 | Lt Interceptor | 600 | 75 | 400/20 | 1 | 2/2 | 0/5 | 1,0,0,0,0,2,0,33 |
| 381 | Hvy Interceptor | 750 | 90 | 400/20 | 1 | 3/3 | 0/5 | 1,0,0,2,0,2,0,33 |
| 385 | Adv Scout | 225 | 180 | 1200/40 | 0.5 | 1/1 | 15/5 | 1,0,129,7169,1,2,0,16 |
| 386 | Adv Stl Fighter | 200 | 160 | 2160/48 | 0.5 | 2/2 | 30/5 | 1,0,259,4096,1,3,0,0 |
| 387 | Assault Ship | 3000 | 120 | 37500/2500 | 1 | 1/3 | 0/5 | 2,0,512,3073,4,2,0,0 |
| 388 | Adv Fighter | 450 | 120 | 750/30 | 1 | 3/3 | 25/5 | 1,0,129,3072,1,2,0,15 |
| 389 | Hvy Bomber | 1200 | 75 | 750/50 | 2 | 2/4 | 75/5 | 1,0,1830,0,2,2,0,0 |
| 390 | Gunship | 800 | 80 | 1000/50 | 2 | 1/3 | 40/5 | 1,0,259,0,2,2,0,0 |
| 391 | Destroyer | 1500 | 100 | 25000/2500 | 3 | 1/1 | 120/5 | 2,0,529,0,4,2,0,0 |
| 393 | Hvy Troop Transport | 975 | 75 | 1350/90 | 1 | 0/0 | 45/5 | 1,0,32,0,2,2,0,0 |
| 395 | Troop Transport | 750 | 60 | 675/45 | 2 | 0/0 | 15/5 | 1,0,32,0,2,2,0,0 |

Early and late weapons (side-buyable; a fitting local hull/mount is still required). Listed DPS is raw direct projectile power / burst time, before damage matrix and GA; missile cycle is ready + lock, not an observed launch interval:

| Part | Weapon | Initially buyable | Raw DPS / missile cycle | Damage type | Energy/ammo per shot |
|---|---|---|---|---|---|
| 5 | LRM Aleph Res 1 | False | 0.25 | 6 | magazine |
| 10 | XRM Cruise 1 | False | 0.25 | 8 | magazine |
| 11 | XRM Cruise 2 | False | 0.25 | 8 | magazine |
| 18 | SRM Seismic | False | 0.25 | 9 | magazine |
| 20 | LRM Killer 1 | False | 0.25 | 3 | magazine |
| 30 | LRM Killer 2 | False | 0.25 | 3 | magazine |
| 31 | LRM Hunter 3 | False | 1.25 | 4 | magazine |
| 32 | XRM Hunter Klr 3 | False | 1.75 | 4 | magazine |
| 33 | PW Gat Gun 1 | True | 32 | 2 | 0/1 |
| 50 | EW Utl Can 1 | True | 27.545 | 1 | 20/0 |
| 51 | EW Utl Can 2 | False | 31.138 | 1 | 20/0 |
| 52 | EW Utl Can 3 | False | 38.922 | 1 | 20/0 |
| 60 | EW Sniper 1 | False | 28 | 4 | 8/0 |
| 61 | EW Sniper 2 | False | 36 | 4 | 7/0 |
| 67 | EW SkyRip | False | 149.254 | 15 | 8/0 |
| 71 | PW Gat Gun 2 | False | 40 | 2 | 0/1 |
| 72 | PW Gat Gun 3 | False | 50 | 2 | 0/1 |
| 87 | PW Mini-AC 1 | True | 90.361 | 11 | 0/1 |
| 88 | PW Mini-AC 2 | False | 99.398 | 11 | 0/1 |
| 89 | PW Mini-AC 3 | False | 109.337 | 11 | 0/1 |
| 90 | LRM Aleph Res 2 | False | 0.25 | 6 | magazine |
| 91 | ER Nanite 1 | True | -90 | 10 | 30/0 |
| 92 | EW Disruptor 1 | True | 12.8 | 3 | 7.5/0 |
| 93 | EW Disruptor 2 | False | 16 | 3 | 7.5/0 |
| 94 | EW Disruptor 3 | False | 20 | 3 | 7.5/0 |
| 95 | LRM Killer Swarm 1 | False | 0.25 | 3 | magazine |
| 96 | LRM Killer Swarm 2 | False | 0.25 | 3 | magazine |
| 97 | LRM Aleph Res 3 | False | 0.25 | 6 | magazine |
| 98 | MRM Seek 1 QL | False | 2.25 | 4 | magazine |
| 99 | MRM Seek 2 QL | False | 1.75 | 4 | magazine |
| 100 | PW EMP Can 1 | False | 36 | 13 | 0/2 |
| 101 | PW EMP Can 2 | False | 45 | 13 | 0/2 |
| 102 | MRM Seek 3 QL | False | 1.25 | 4 | magazine |
| 103 | MRM Quickfire 1 | True | 0.5 | 14 | magazine |
| 104 | MRM Quickfire 2 | False | 0.45 | 14 | magazine |
| 105 | MRM Quickfire 3 | False | 0.4 | 14 | magazine |
| 106 | SRM Dumb 1 QL | False | 0.25 | 4 | magazine |
| 107 | SRM Dumb 2 QL | False | 0.25 | 4 | magazine |
| 108 | SRM Dumb 3 QL | False | 0.25 | 4 | magazine |
| 111 | EW Galv Blstr | False | 28 | 7 | 12/0 |
| 115 | SRM Nerve Gas | False | 0.25 | 12 | magazine |
| 120 | PW Mini-Gun 1 | True | 50 | 5 | 0/1 |
| 121 | PW Mini-Gun 2 | False | 64.103 | 5 | 0/1 |
| 122 | PW Mini-Gun 3 | False | 78.125 | 5 | 0/1 |
| 126 | XRM Anti-Base 1 | False | 0.25 | 8 | magazine |
| 127 | XRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 130 | PW AutoCan 1 | True | 117.952 | 11 | 0/1 |
| 131 | PW AutoCan 2 | False | 149.277 | 11 | 0/1 |
| 132 | PW AutoCan 3 | False | 186.627 | 11 | 0/1 |
| 140 | PW SkyCap 1 | True | 60 | 11 | 0/4 |
| 141 | PW SkyCap 2 | False | 75 | 11 | 0/4 |
| 142 | PW SkyCap 3 | False | 93.75 | 11 | 0/4 |
| 150 | SRM Dumbfire 1 | True | 0.25 | 4 | magazine |
| 151 | SRM Dumbfire 2 | False | 0.25 | 4 | magazine |
| 152 | SRM Dumbfire 3 | False | 0.25 | 4 | magazine |
| 153 | MRM Seeker 1 | True | 2.25 | 4 | magazine |
| 154 | MRM Seeker 2 | False | 1.75 | 4 | magazine |
| 155 | LRM Hunter 1 | True | 2.25 | 4 | magazine |
| 156 | MRM Seeker 3 | False | 1.25 | 4 | magazine |
| 157 | SRM Anti-Base 1 | True | 0.25 | 8 | magazine |
| 158 | SRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 159 | LRM Hunter 2 | False | 1.75 | 4 | magazine |
| 160 | SRM Tactical Nuke 1 | False | 0.25 | 8 | magazine |
| 161 | SRM Tactical Nuke 2 | False | 0.25 | 8 | magazine |
| 162 | MRM Seismic | False | 0.25 | 9 | magazine |
| 163 | XRM Hunter Klr 1 | False | 2.25 | 4 | magazine |
| 164 | XRM Hunter Klr 2 | False | 2.25 | 4 | magazine |
| 190 | ER Nanite 2 | False | -120 | 10 | 30/0 |
| 192 | SRM EMP | False | 0.25 | 12 | magazine |
| 193 | XRM Torpedo 1 | False | 0.25 | 4 | magazine |
| 194 | XRM Torpedo 2 | False | 0.25 | 4 | magazine |
| 223 | ER Nanite 3 | False | -150 | 10 | 30/0 |
| 601 | SRM Anti-Base 3 | False | 0.25 | 8 | magazine |

Station branches and baseline serial position (constructor bucket only, excluding travel):

| Station | Required/effect/local bits | Direct cost/seconds | Baseline step/seconds/cost |
|---|---|---|---|
| 202 | Outpost [188,255]/[12]/[99,255] | 5000/60 | 27/15110/60005 |
| 207 | Supremacy (Adv) [4,188,239]/[13,14,174,175,216,217,225]/[99,255] | 15000/60 | 28/15170/75005 |
| 208 | Expansion (Adv) [2,188,239]/[8,15,174,175,216,217,226]/[99,255] | 15000/60 | 67/38030/155005 |
| 209 | Tactical (Adv) [3,188,239]/[7,216,217,224,243]/[99,255] | 15000/60 | 100/57290/238005 |
| 201 | Shipyard (Dry) [1,188,216,239]/[11,185,227]/[99,240,255] | 20000/60 | 128/73550/319005 |
| 214 | Refinery [188,255]/[]/[] | 3000/60 | 149/84080/376005 |
| 247 | Teleport [188,255]/[]/[] | 3000/60 | 150/84140/379005 |


Reachable GA developments (each completed factor multiplies the base GA; bucket cost/time were frozen at creation):

| Development | Cost | Seconds | Factors |
|---|---|---|---|
| GA: Ship Acceleration 1 | 1000 | 600 | thrust x1.1 |
| GA: Ship Acceleration 2 | 1250 | 600 | thrust x1.1 |
| GA: Ship Agility 1 | 1000 | 600 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Agility 2 | 1250 | 600 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Energy 1 | 1000 | 600 | energy x1.1 |
| GA: Ship Energy 2 | 1250 | 600 | energy x1.1 |
| GA: EW Range 1 | 1000 | 600 | energy lifespan x1.1 |
| GA: EW Range 2 | 1250 | 600 | energy lifespan x1.1 |
| GA: PW&EW Damage 1 | 1000 | 600 | gun damage x1.1 |
| GA: PW&EW Damage 2 | 1250 | 600 | gun damage x1.1 |
| GA: Faster He3 1 | 1000 | 600 | mining rate x1.1 |
| GA: Faster He3 2 | 1250 | 600 | mining rate x1.1 |
| GA: He3 Yield 1 | 1000 | 600 | mining yield x1.1 |
| GA: He3 Yield 2 | 1250 | 600 | mining yield x1.1 |
| GA: Missile Damage 1 | 1000 | 600 | missile damage x1.1 |
| GA: Missile Damage 2 | 1250 | 600 | missile damage x1.1 |
| GA: Missile Track 1 | 1000 | 600 | missile turn x1.1 |
| GA: Missile Track 2 | 1250 | 600 | missile turn x1.1 |
| GA: PW Range 1 | 1000 | 600 | ammo speed x1.1 |
| GA: PW Range 2 | 1250 | 600 | ammo speed x1.1 |
| GA: Ripcord 1 | 1000 | 600 | ripcord speed x1.15 |
| GA: Ripcord 2 | 1250 | 600 | ripcord speed x1.15 |
| GA: Ship Sensors 1 | 1000 | 600 | scan x1.1 |
| GA: Ship Sensors 2 | 1250 | 600 | scan x1.1 |
| GA: Ship Hull 1 | 1000 | 600 | ship armor x1.1 |
| GA: Ship Hull 2 | 1250 | 600 | ship armor x1.1 |
| GA: Ship Shield 1 | 1000 | 600 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Shield 2 | 1250 | 600 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Signature 1 | 1000 | 600 | signature resistance x1.1 |
| GA: Ship Signature 2 | 1250 | 600 | signature resistance x1.1 |
| GA: Ship Speed 1 | 2000 | 600 | speed x1.1 |
| GA: Ship Speed 2 | 2500 | 600 | speed x1.1 |
| GA: Station Hull 1 | 1000 | 600 | station armor x1.1; station armor regen x1.1 |
| GA: Station Hull 2 | 1250 | 600 | station armor x1.1; station armor regen x1.1 |
| GA: Station Shield 1 | 1000 | 600 | station shield x1.1; station shield regen x1.1 |
| GA: Station Shield 2 | 1250 | 600 | station shield x1.1; station shield regen x1.1 |


## 101 — Belters

Starting tech bits: 6,31,34,37,45,59,73,93,97,102,107,108,110,126,127,143,144,145,154,165,168,169,170,187,208,209,224,225,226,227,339,355. All four enabled path-switch bits (1,2,3,4) are added by ResearchSettings. No-development bits: 5,6,7,8,13,14,15,17,18,28,32,34,36,37,38,45,46,51,52,57,59,72,87,88,89,97,98,100,102,103,108,110,111,114,116,125,126,132,144,145,154,157,163,165,166,167,168,169,170,177,180,182,183,186,187,190,194,195,196,197,202,206,207,210,211,212,213,216,217,220,224,225,226,243,339,355.

Home 315 Garrison: armor 20000, shield 10000, income 0; grants [17], local [99,239,241,255]. Base payday fraction +-0.25; starting-money addition +0; pod 544. Negative additions are retained.

Nonidentity base GA multipliers:

| Index | Attribute | Factor |
|---|---|---|
| 0 | speed | 1.15 |
| 2 | turn rate | 1.05 |
| 3 | turn torque | 1.1 |
| 8 | ship armor | 1.05 |
| 17 | mining rate | 0.6 |
| 18 | mining yield | 1.25 |
| 19 | mining capacity | 0.8 |
| 23 | development cost | 1.25 |
| 24 | development time | 2 |

Starting hulls (including autonomous types):

| ID | Hull | Human | HP raw/effective | Shield max initial | Speed raw/effective | Thrust | Mass | Signature | Guns fixed/total | Ammo | Magazine/dispenser/chaff |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 510 | Scout | True | 400/420 | 0 | 160/184 | 1200 | 60 | 0.9 | 1/1 | 800 | 10/2/4 |
| 515 | Fighter | True | 600/630 | 0 | 100/115 | 910 | 50 | 1.2 | 2/2 | 600 | 20/1/4 |
| 519 | Lg Std Constructor | False | 2880/3024 | 300 | 60/69 | 500000 | 50000 | 1.5 | 0/0 | 0 | 0/0/0 |
| 525 | Carrier [SR] | False | 2000/2100 | 0 | 50/57.5 | 500000 | 50000 | 2 | 0/0 | 1600 | 60/8/8 |
| 538 | Miner | False | 1056/1108.8 | 300 | 100/115 | 1000 | 100 | 1.5 | 0/0 | 0 | 0/0/0 |
| 544 | Lifepod | False | 25/26.25 | 0 | 60/69 | 150 | 15 | 0.5 | 0/0 | 0 | 0/0/0 |
| 570 | Sm Std Constructor | False | 1440/1512 | 300 | 60/69 | 2000 | 200 | 1.5 | 0/0 | 0 | 0/0/0 |

Starting human hardpoints/equipment and purchasable weapons:

| Hull | Hardpoints (mask; fixed) | Equipment masks | Initial fitting guns | Initial fitting afterburners |
|---|---|---|---|---|
| 510 | 3;True | 1,0,129,7169,0,0,0,19 | 33 PW Gat Gun 1; 91 ER Nanite 1; 92 EW Disruptor 1 | 23 Booster 1 |
| 515 | 3;True; 25;True | 1,0,129,5120,0,0,0,3 | 33 PW Gat Gun 1; 91 ER Nanite 1; 92 EW Disruptor 1; 120 PW Mini-Gun 1 | 23 Booster 1 |

Reachable human hulls (raw; cumulative baseline unlock time is in the plan appendix):

| ID | Hull | HP | Speed | Thrust/mass | Signature | Fixed/total | Magazine/cargo slots | Equipment masks |
|---|---|---|---|---|---|---|---|---|
| 37 | Corvette | 2000 | 80 | 12500/625 | 2.5 | 2/4 | 80/5 | 2,0,771,2064,4,0,0,0 |
| 67 | Omni Fighter | 750 | 120 | 750/34 | 0.9 | 3/3 | 30/5 | 1,0,387,7168,0,1,0,11 |
| 81 | Attack Carrier [SR] | 3000 | 75 | 50000/5000 | 1.5 | 1/3 | 60/5 | 2,0,2,3073,4,0,0,0 |
| 88 | Hvy Scout | 550 | 180 | 1440/60 | 0.6 | 1/1 | 20/5 | 1,0,4225,7169,0,0,0,19 |
| 151 | Stealth Bomber [NG] | 300 | 80 | 2160/180 | 1.8 | 2/2 | 30/5 | 1,0,64,0,0,1,0,3 |
| 159 | Fighter/Bomber | 750 | 70 | 1137/50 | 1.8 | 3/3 | 25/5 | 1,0,133,1024,0,0,0,5 |
| 504 | Bomber | 800 | 60 | 750/50 | 2 | 2/4 | 60/5 | 1,0,294,0,2,0,0,0 |
| 510 | Scout | 400 | 160 | 1200/60 | 0.9 | 1/1 | 10/5 | 1,0,129,7169,0,0,0,19 |
| 511 | Stealth Fighter | 300 | 140 | 2160/90 | 0.6 | 2/2 | 20/5 | 1,0,259,4096,0,3,0,3 |
| 512 | Interceptor | 700 | 80 | 400/34 | 1 | 2/2 | 10/5 | 1,0,128,2,0,0,0,33 |
| 514 | Enh Fighter | 650 | 100 | 910/50 | 1.2 | 3/3 | 20/5 | 1,0,129,5120,0,0,0,7 |
| 515 | Fighter | 600 | 100 | 910/50 | 1.2 | 2/2 | 20/5 | 1,0,129,5120,0,0,0,3 |
| 520 | Stealth Bomber | 300 | 80 | 2160/180 | 1.2 | 2/2 | 30/5 | 1,0,294,0,0,3,0,3 |
| 521 | Frigate | 3500 | 75 | 25000/2500 | 3 | 1/4 | 120/5 | 2,0,520,0,4,2,0,0 |
| 522 | Cruiser | 4000 | 70 | 50000/5000 | 4 | 1/5 | 160/5 | 2,0,8744,0,4,0,0,0 |
| 581 | Hvy Interceptor | 750 | 90 | 400/34 | 1 | 3/3 | 10/5 | 1,0,128,2,0,0,0,33 |
| 585 | Adv Scout | 525 | 180 | 1200/60 | 0.6 | 1/1 | 15/5 | 1,0,129,7169,0,0,0,19 |
| 586 | Adv Stl Fighter | 400 | 160 | 2160/90 | 0.6 | 2/2 | 30/5 | 1,0,259,4096,0,3,0,3 |
| 587 | Assault Ship | 3000 | 120 | 37500/2500 | 1 | 1/3 | 90/5 | 2,0,771,3073,4,2,0,0 |
| 588 | Adv Fighter | 750 | 120 | 910/50 | 1.2 | 3/3 | 25/5 | 1,0,129,5120,0,0,0,15 |
| 589 | Hvy Bomber | 1200 | 75 | 750/50 | 2 | 2/4 | 75/5 | 1,0,1830,0,2,0,0,0 |
| 590 | Gunship | 800 | 80 | 1000/50 | 2 | 1/3 | 40/5 | 1,0,259,0,2,0,0,0 |
| 591 | Destroyer | 1500 | 100 | 25000/2500 | 3 | 1/1 | 120/5 | 2,0,529,0,4,0,0,0 |
| 593 | Hvy Troop Transport | 975 | 75 | 1350/90 | 1 | 0/0 | 45/5 | 1,0,32,0,2,0,0,0 |
| 595 | Troop Transport | 750 | 60 | 675/45 | 2 | 0/0 | 15/5 | 1,0,32,0,2,0,0,0 |

Early and late weapons (side-buyable; a fitting local hull/mount is still required). Listed DPS is raw direct projectile power / burst time, before damage matrix and GA; missile cycle is ready + lock, not an observed launch interval:

| Part | Weapon | Initially buyable | Raw DPS / missile cycle | Damage type | Energy/ammo per shot |
|---|---|---|---|---|---|
| 5 | LRM Aleph Res 1 | False | 0.25 | 6 | magazine |
| 10 | XRM Cruise 1 | False | 0.25 | 8 | magazine |
| 11 | XRM Cruise 2 | False | 0.25 | 8 | magazine |
| 18 | SRM Seismic | False | 0.25 | 9 | magazine |
| 20 | LRM Killer 1 | False | 0.25 | 3 | magazine |
| 30 | LRM Killer 2 | False | 0.25 | 3 | magazine |
| 31 | LRM Hunter 3 | False | 1.25 | 4 | magazine |
| 32 | XRM Hunter Klr 3 | False | 1.75 | 4 | magazine |
| 33 | PW Gat Gun 1 | True | 32 | 2 | 0/1 |
| 48 | MRM Nerve Gas | False | 0.25 | 12 | magazine |
| 50 | EW Utl Can 1 | True | 27.545 | 1 | 20/0 |
| 51 | EW Utl Can 2 | False | 31.138 | 1 | 20/0 |
| 52 | EW Utl Can 3 | False | 38.922 | 1 | 20/0 |
| 60 | EW Sniper 1 | False | 28 | 4 | 8/0 |
| 61 | EW Sniper 2 | False | 36 | 4 | 7/0 |
| 63 | PE SkyRip | False | 124.378 | 15 | 12.02/1 |
| 71 | PW Gat Gun 2 | False | 40 | 2 | 0/1 |
| 72 | PW Gat Gun 3 | False | 50 | 2 | 0/1 |
| 87 | PW Mini-AC 1 | True | 90.361 | 11 | 0/1 |
| 88 | PW Mini-AC 2 | False | 99.398 | 11 | 0/1 |
| 89 | PW Mini-AC 3 | False | 109.337 | 11 | 0/1 |
| 90 | LRM Aleph Res 2 | False | 0.25 | 6 | magazine |
| 91 | ER Nanite 1 | True | -90 | 10 | 30/0 |
| 92 | EW Disruptor 1 | True | 12.8 | 3 | 7.5/0 |
| 93 | EW Disruptor 2 | False | 16 | 3 | 7.5/0 |
| 94 | EW Disruptor 3 | False | 20 | 3 | 7.5/0 |
| 95 | LRM Killer Swarm 1 | False | 0.25 | 3 | magazine |
| 96 | LRM Killer Swarm 2 | False | 0.25 | 3 | magazine |
| 97 | LRM Aleph Res 3 | False | 0.25 | 6 | magazine |
| 98 | MRM Seek 1 QL | False | 2.25 | 4 | magazine |
| 99 | MRM Seek 2 QL | False | 1.75 | 4 | magazine |
| 100 | PW EMP Can 1 | False | 36 | 13 | 0/2 |
| 101 | PW EMP Can 2 | False | 45 | 13 | 0/2 |
| 102 | MRM Seek 3 QL | False | 1.25 | 4 | magazine |
| 103 | MRM Quickfire 1 | True | 0.5 | 14 | magazine |
| 104 | MRM Quickfire 2 | False | 0.45 | 14 | magazine |
| 105 | MRM Quickfire 3 | False | 0.4 | 14 | magazine |
| 106 | SRM Dumb 1 QL | False | 0.25 | 4 | magazine |
| 107 | SRM Dumb 2 QL | False | 0.25 | 4 | magazine |
| 108 | SRM Dumb 3 QL | False | 0.25 | 4 | magazine |
| 111 | EW Galv Blstr | False | 28 | 7 | 12/0 |
| 115 | SRM Nerve Gas | False | 0.25 | 12 | magazine |
| 120 | PW Mini-Gun 1 | True | 50 | 5 | 0/1 |
| 121 | PW Mini-Gun 2 | False | 64.103 | 5 | 0/1 |
| 122 | PW Mini-Gun 3 | False | 78.125 | 5 | 0/1 |
| 126 | XRM Anti-Base 1 | False | 0.25 | 8 | magazine |
| 127 | XRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 130 | PW AutoCan 1 | True | 117.952 | 11 | 0/1 |
| 131 | PW AutoCan 2 | False | 149.277 | 11 | 0/1 |
| 132 | PW AutoCan 3 | False | 186.627 | 11 | 0/1 |
| 140 | PW SkyCap 1 | True | 60 | 11 | 0/4 |
| 141 | PW SkyCap 2 | False | 75 | 11 | 0/4 |
| 142 | PW SkyCap 3 | False | 93.75 | 11 | 0/4 |
| 150 | SRM Dumbfire 1 | True | 0.25 | 4 | magazine |
| 151 | SRM Dumbfire 2 | False | 0.25 | 4 | magazine |
| 152 | SRM Dumbfire 3 | False | 0.25 | 4 | magazine |
| 153 | MRM Seeker 1 | True | 2.25 | 4 | magazine |
| 154 | MRM Seeker 2 | False | 1.75 | 4 | magazine |
| 155 | LRM Hunter 1 | True | 2.25 | 4 | magazine |
| 156 | MRM Seeker 3 | False | 1.25 | 4 | magazine |
| 157 | SRM Anti-Base 1 | True | 0.25 | 8 | magazine |
| 158 | SRM Anti-Base 2 | False | 0.25 | 8 | magazine |
| 159 | LRM Hunter 2 | False | 1.75 | 4 | magazine |
| 160 | SRM Tactical Nuke 1 | False | 0.25 | 8 | magazine |
| 161 | SRM Tactical Nuke 2 | False | 0.25 | 8 | magazine |
| 162 | MRM Seismic | False | 0.25 | 9 | magazine |
| 163 | XRM Hunter Klr 1 | False | 2.25 | 4 | magazine |
| 164 | XRM Hunter Klr 2 | False | 2.25 | 4 | magazine |
| 190 | ER Nanite 2 | False | -120 | 10 | 30/0 |
| 192 | SRM EMP | False | 0.25 | 12 | magazine |
| 193 | XRM Torpedo 1 | False | 0.25 | 4 | magazine |
| 194 | XRM Torpedo 2 | False | 0.25 | 4 | magazine |
| 223 | ER Nanite 3 | False | -150 | 10 | 30/0 |
| 601 | SRM Anti-Base 3 | False | 0.25 | 8 | magazine |

Station branches and baseline serial position (constructor bucket only, excluding travel):

| Station | Required/effect/local bits | Direct cost/seconds | Baseline step/seconds/cost |
|---|---|---|---|
| 302 | Outpost [97,255]/[12]/[99,255] | 4000/90 | 16/3470/66699 |
| 311 | Supremacy [4,97,239]/[13,175,216,225]/[99,255] | 8000/120 | 17/3590/74699 |
| 307 | Supremacy (Adv) [4,97,179,239]/[13,14,174,175,216,217,225,292]/[99,255] | 8000/120 | 35/7790/157386 |
| 312 | Expansion [2,97,239]/[8,175,216,226]/[99,255] | 8000/120 | 54/12230/231011 |
| 308 | Expansion (Adv) [2,97,119,239]/[8,15,174,175,216,217,226,290]/[99,255] | 8000/120 | 71/16190/321198 |
| 313 | Tactical [3,97,239]/[216,224,243]/[99,255] | 8000/120 | 89/20390/405759 |
| 309 | Tactical (Adv) [3,97,178,239]/[7,216,217,224,243,291]/[99,255] | 8000/120 | 104/23870/497196 |
| 303 | Shipyard [1,97,216,239]/[11,227]/[99,240,255] | 12000/120 | 119/27350/582633 |
| 301 | Shipyard (Dry) [1,97,141,216,239]/[11,185,227]/[99,240,255] | 12000/120 | 130/29870/666508 |
| 310 | Garrison (Str) [97,183,255]/[17,18,293]/[92,99,239,255] | 16000/120 | 139/31910/732508 |
| 314 | Refinery [97,255]/[]/[] | 2400/90 | 160/36350/818595 |
| 347 | Teleport [97,255]/[]/[] | 2400/90 | 161/36440/820995 |


Reachable GA developments (each completed factor multiplies the base GA; bucket cost/time were frozen at creation):

| Development | Cost | Seconds | Factors |
|---|---|---|---|
| GA: Ship Acceleration 1 | 2500 | 240 | thrust x1.1 |
| GA: Ship Acceleration 2 | 3125 | 240 | thrust x1.1 |
| GA: Ship Agility 1 | 2500 | 240 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Agility 2 | 3125 | 240 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Energy 1 | 2500 | 240 | energy x1.1 |
| GA: Ship Energy 2 | 3125 | 240 | energy x1.1 |
| GA: EW Range 1 | 2500 | 240 | energy lifespan x1.1 |
| GA: EW Range 2 | 3125 | 240 | energy lifespan x1.1 |
| GA: PW&EW Damage 1 | 2500 | 240 | gun damage x1.1 |
| GA: PW&EW Damage 2 | 3125 | 240 | gun damage x1.1 |
| GA: Faster He3 1 | 2500 | 240 | mining rate x1.1 |
| GA: Faster He3 2 | 3125 | 240 | mining rate x1.1 |
| GA: He3 Yield 1 | 2500 | 240 | mining yield x1.1 |
| GA: He3 Yield 2 | 3125 | 240 | mining yield x1.1 |
| GA: Missile Damage 1 | 2500 | 240 | missile damage x1.1 |
| GA: Missile Damage 2 | 3125 | 240 | missile damage x1.1 |
| GA: Missile Track 1 | 2500 | 240 | missile turn x1.1 |
| GA: Missile Track 2 | 3125 | 240 | missile turn x1.1 |
| GA: PW Range 1 | 2500 | 240 | ammo speed x1.1 |
| GA: PW Range 2 | 3125 | 240 | ammo speed x1.1 |
| GA: Ripcord 1 | 2500 | 240 | ripcord speed x1.15 |
| GA: Ripcord 2 | 3125 | 240 | ripcord speed x1.15 |
| GA: Ship Sensors 1 | 2500 | 240 | scan x1.1 |
| GA: Ship Sensors 2 | 3125 | 240 | scan x1.1 |
| GA: Ship Hull 1 | 2500 | 240 | ship armor x1.1 |
| GA: Ship Hull 2 | 3125 | 240 | ship armor x1.1 |
| GA: Ship Shield 1 | 2500 | 240 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Shield 2 | 3125 | 240 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Signature 1 | 2500 | 240 | signature resistance x1.1 |
| GA: Ship Signature 2 | 3125 | 240 | signature resistance x1.1 |
| GA: Ship Speed 1 | 5000 | 240 | speed x1.1 |
| GA: Ship Speed 2 | 6250 | 240 | speed x1.1 |
| GA: Station Hull 1 | 2500 | 240 | station armor x1.1; station armor regen x1.1 |
| GA: Station Hull 2 | 3125 | 240 | station armor x1.1; station armor regen x1.1 |
| GA: Station Shield 1 | 2500 | 240 | station shield x1.1; station shield regen x1.1 |
| GA: Station Shield 2 | 3125 | 240 | station shield x1.1; station shield regen x1.1 |


## 201 — Rixian

Starting tech bits: 6,22,31,34,37,45,59,73,93,98,102,107,108,110,127,144,145,146,154,168,169,171,187,208,209,355. All four enabled path-switch bits (1,2,3,4) are added by ResearchSettings. No-development bits: 5,6,7,8,13,14,15,17,18,22,28,32,34,36,37,38,45,46,51,52,57,59,72,87,88,89,98,100,102,103,108,110,111,114,116,130,132,144,145,146,154,157,163,166,168,169,171,177,180,182,183,186,187,190,194,195,196,197,202,206,207,210,211,212,213,216,220,224,225,226,243,247,355.

Home 415 Garrison: armor 20000, shield 10000, income 0; grants [17], local [99,239,241,255]. Base payday fraction +0; starting-money addition +0; pod 644. Negative additions are retained.

Nonidentity base GA multipliers:

| Index | Attribute | Factor |
|---|---|---|
| 8 | ship armor | 0.9 |
| 14 | ammo speed | 1.2 |
| 15 | energy lifespan | 1.2 |
| 17 | mining rate | 0.9 |
| 21 | gun damage | 1.15 |
| 24 | development time | 0.1 |

Starting hulls (including autonomous types):

| ID | Hull | Human | HP raw/effective | Shield max initial | Speed raw/effective | Thrust | Mass | Signature | Guns fixed/total | Ammo | Magazine/dispenser/chaff |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 610 | Scout [SR] | True | 200/180 | 150 | 160/160 | 1200 | 40 | 0.9 | 1/1 | 800 | 0/3/4 |
| 615 | Fighter | True | 300/270 | 150 | 110/110 | 750 | 30 | 1 | 3/3 | 600 | 0/1/4 |
| 619 | Lg Std Constructor | False | 2880/2592 | 300 | 60/60 | 500000 | 50000 | 1.5 | 0/0 | 0 | 0/0/0 |
| 625 | Carrier [SR] | False | 2000/1800 | 0 | 50/50 | 500000 | 50000 | 2 | 0/0 | 1600 | 60/8/8 |
| 638 | Miner | False | 1056/950.4 | 300 | 100/100 | 1000 | 100 | 1.5 | 0/0 | 0 | 0/0/0 |
| 644 | Lifepod | False | 25/22.5 | 0 | 60/60 | 150 | 10 | 0.5 | 0/0 | 0 | 0/0/0 |
| 670 | Sm Std Constructor | False | 1440/1296 | 300 | 60/60 | 2000 | 200 | 1.5 | 0/0 | 0 | 0/0/0 |

Starting human hardpoints/equipment and purchasable weapons:

| Hull | Hardpoints (mask; fixed) | Equipment masks | Initial fitting guns | Initial fitting afterburners |
|---|---|---|---|---|
| 610 | 3;True | 1,0,0,7169,1,0,0,16 | 33 PW Gat Gun 1; 91 ER Nanite 1; 92 EW Disruptor 1 |  |
| 615 | 1;True; 1;True; 1;True | 1,0,0,1024,1,0,0,3 | 33 PW Gat Gun 1; 92 EW Disruptor 1 | 23 Booster 1 |

Reachable human hulls (raw; cumulative baseline unlock time is in the plan appendix):

| ID | Hull | HP | Speed | Thrust/mass | Signature | Fixed/total | Magazine/cargo slots | Equipment masks |
|---|---|---|---|---|---|---|---|---|
| 38 | Corvette | 2000 | 80 | 12500/625 | 2.5 | 1/3 | 80/5 | 2,0,512,2068,4,0,0,0 |
| 82 | Attack Carrier [SR] | 3000 | 75 | 50000/5000 | 1.5 | 1/3 | 60/5 | 2,0,0,3073,4,0,0,0 |
| 89 | Hvy Scout | 250 | 180 | 1440/40 | 0.7 | 1/1 | 0/5 | 1,0,0,7169,1,0,0,16 |
| 152 | Stealth Bomber [NG] | 150 | 80 | 2160/120 | 1.5 | 2/2 | 30/5 | 1,0,64,0,1,1,0,0 |
| 160 | Fighter/Bomber | 450 | 70 | 1200/40 | 1.5 | 3/3 | 25/5 | 1,0,0,1024,1,0,0,5 |
| 604 | Bomber | 800 | 60 | 750/50 | 2 | 2/4 | 0/5 | 1,0,0,0,2,0,0,0 |
| 610 | Scout [SR] | 200 | 160 | 1200/40 | 0.9 | 1/1 | 0/5 | 1,0,0,7169,1,0,0,16 |
| 611 | Stealth Fighter | 150 | 140 | 2160/60 | 0.7 | 2/2 | 0/5 | 1,0,0,7173,1,3,0,0 |
| 612 | Interceptor | 700 | 80 | 400/20 | 1 | 2/2 | 0/5 | 1,0,0,2,0,0,0,33 |
| 614 | Enh Fighter | 350 | 110 | 750/30 | 1 | 3/3 | 0/5 | 1,0,0,1024,1,0,0,7 |
| 615 | Fighter | 300 | 110 | 750/30 | 1 | 3/3 | 0/5 | 1,0,0,1024,1,0,0,3 |
| 620 | Stealth Bomber | 150 | 80 | 2160/120 | 1 | 2/2 | 0/5 | 1,0,0,1037,1,3,0,0 |
| 621 | Frigate | 3500 | 75 | 25000/2500 | 3 | 1/4 | 120/5 | 2,0,512,56,4,2,0,0 |
| 622 | Cruiser | 4000 | 70 | 50000/5000 | 4 | 1/5 | 160/5 | 2,0,512,56,4,0,0,0 |
| 634 | Hvy Scout [SR] | 250 | 180 | 1440/40 | 0.7 | 1/1 | 0/5 | 1,0,0,7169,1,0,0,16 |
| 681 | Hvy Interceptor | 750 | 90 | 400/20 | 1 | 3/3 | 0/5 | 1,0,0,2,0,0,0,33 |
| 685 | Adv Scout [SR] | 225 | 180 | 1200/40 | 0.8 | 1/1 | 0/5 | 1,0,0,7169,1,0,0,16 |
| 686 | Adv Stl Fighter | 200 | 160 | 2160/60 | 0.6 | 2/2 | 0/5 | 1,0,0,7173,1,3,0,0 |
| 687 | Assault Ship | 3000 | 120 | 37500/2500 | 1 | 1/3 | 0/5 | 2,0,512,3073,4,2,0,0 |
| 688 | Adv Fighter | 450 | 130 | 750/30 | 1 | 3/3 | 0/5 | 1,0,0,1024,1,0,0,15 |
| 689 | Hvy Bomber | 1200 | 75 | 750/50 | 2 | 2/4 | 75/5 | 1,0,1536,0,2,0,0,0 |
| 690 | Gunship | 800 | 80 | 1000/50 | 2 | 1/3 | 0/5 | 1,0,0,0,2,0,0,0 |
| 691 | Destroyer | 1500 | 100 | 25000/2500 | 3 | 1/1 | 0/5 | 2,0,0,0,4,0,0,0 |
| 693 | Hvy Troop Transport | 975 | 75 | 1350/90 | 1 | 1/1 | 0/5 | 1,0,0,0,2,0,0,0 |
| 695 | Troop Transport | 750 | 60 | 675/45 | 2 | 1/1 | 0/5 | 1,0,0,0,2,0,0,0 |

Early and late weapons (side-buyable; a fitting local hull/mount is still required). Listed DPS is raw direct projectile power / burst time, before damage matrix and GA; missile cycle is ready + lock, not an observed launch interval:

| Part | Weapon | Initially buyable | Raw DPS / missile cycle | Damage type | Energy/ammo per shot |
|---|---|---|---|---|---|
| 5 | LRM Aleph Res 1 | False | 0.25 | 6 | magazine |
| 33 | PW Gat Gun 1 | True | 32 | 2 | 0/1 |
| 50 | EW Utl Can 1 | True | 27.545 | 1 | 20/0 |
| 51 | EW Utl Can 2 | False | 31.138 | 1 | 20/0 |
| 52 | EW Utl Can 3 | False | 38.922 | 1 | 20/0 |
| 60 | EW Sniper 1 | False | 28 | 4 | 8/0 |
| 61 | EW Sniper 2 | False | 36 | 4 | 7/0 |
| 67 | EW SkyRip | False | 149.254 | 15 | 8/0 |
| 71 | PW Gat Gun 2 | False | 40 | 2 | 0/1 |
| 72 | PW Gat Gun 3 | False | 50 | 2 | 0/1 |
| 87 | PW Mini-AC 1 | True | 90.361 | 11 | 0/1 |
| 88 | PW Mini-AC 2 | False | 99.398 | 11 | 0/1 |
| 89 | PW Mini-AC 3 | False | 109.337 | 11 | 0/1 |
| 90 | LRM Aleph Res 2 | False | 0.25 | 6 | magazine |
| 91 | ER Nanite 1 | True | -90 | 10 | 30/0 |
| 92 | EW Disruptor 1 | True | 12.8 | 3 | 7.5/0 |
| 93 | EW Disruptor 2 | False | 16 | 3 | 7.5/0 |
| 94 | EW Disruptor 3 | False | 20 | 3 | 7.5/0 |
| 97 | LRM Aleph Res 3 | False | 0.25 | 6 | magazine |
| 100 | PW EMP Can 1 | False | 36 | 13 | 0/2 |
| 101 | PW EMP Can 2 | False | 45 | 13 | 0/2 |
| 111 | EW Galv Blstr | False | 28 | 7 | 12/0 |
| 115 | SRM Nerve Gas | False | 0.25 | 12 | magazine |
| 120 | PW Mini-Gun 1 | True | 50 | 5 | 0/1 |
| 121 | PW Mini-Gun 2 | False | 64.103 | 5 | 0/1 |
| 122 | PW Mini-Gun 3 | False | 78.125 | 5 | 0/1 |
| 130 | PW AutoCan 1 | True | 117.952 | 11 | 0/1 |
| 131 | PW AutoCan 2 | False | 149.277 | 11 | 0/1 |
| 132 | PW AutoCan 3 | False | 186.627 | 11 | 0/1 |
| 140 | PW SkyCap 1 | True | 60 | 11 | 0/4 |
| 141 | PW SkyCap 2 | False | 75 | 11 | 0/4 |
| 142 | PW SkyCap 3 | False | 93.75 | 11 | 0/4 |
| 165 | XRP Anti-Base 1 | False | 0.25 | 8 | magazine |
| 173 | XRP Anti-Base 2 | False | 0.25 | 8 | magazine |
| 190 | ER Nanite 2 | False | -120 | 10 | 30/0 |
| 200 | EW A-B Stinger 1 | True | 48 | 17 | 10/0 |
| 201 | EW A-B Stinger 2 | False | 80 | 17 | 9/0 |
| 222 | EW Seismic Cannon | False | 40 | 9 | 10/0 |
| 223 | ER Nanite 3 | False | -150 | 10 | 30/0 |
| 1003 | PW Lancer 1 | True | 0 | 18 | 0/15 |
| 1004 | PW Lancer 2 | False | 0 | 18 | 0/15 |
| 1005 | PW Lancer 3 | False | 0 | 18 | 0/13 |

Station branches and baseline serial position (constructor bucket only, excluding travel):

| Station | Required/effect/local bits | Direct cost/seconds | Baseline step/seconds/cost |
|---|---|---|---|
| 402 | Outpost [22,255]/[12]/[99,255] | 5000/90 | 14/235/67510 |
| 411 | Supremacy [4,22,239]/[13,175,216,225]/[99,255] | 10000/120 | 15/355/77510 |
| 407 | Supremacy (Adv) [4,22,179,239]/[13,14,174,175,216,217,225,292]/[99,255] | 10000/120 | 32/667/160510 |
| 412 | Expansion [2,22,239]/[8,175,216,226]/[99,255] | 10000/120 | 49/979/225510 |
| 408 | Expansion (Adv) [2,22,119,239]/[8,15,174,175,216,217,226,290]/[99,255] | 10000/120 | 65/1279/309010 |
| 413 | Tactical [3,22,239]/[216,224,243]/[99,255] | 10000/120 | 83/1603/401510 |
| 409 | Tactical (Adv) [3,22,178,239]/[7,216,217,224,243,291]/[99,255] | 10000/120 | 97/1879/489010 |
| 403 | Shipyard [1,22,216,239]/[11,227]/[99,240,255] | 15000/120 | 112/2167/574010 |
| 401 | Shipyard (Dry) [1,22,141,216,239]/[11,185,227]/[99,240,255] | 15000/120 | 120/2371/654010 |
| 410 | Garrison (Str) [22,183,255]/[17,18,293]/[92,99,239,255] | 20000/120 | 126/2551/699010 |
| 414 | Refinery [22,255]/[]/[] | 3000/90 | 144/3079/778510 |
| 447 | Teleport [22,255]/[]/[] | 3000/90 | 145/3169/781510 |


Reachable GA developments (each completed factor multiplies the base GA; bucket cost/time were frozen at creation):

| Development | Cost | Seconds | Factors |
|---|---|---|---|
| GA: Ship Acceleration 1 | 2000 | 12 | thrust x1.1 |
| GA: Ship Acceleration 2 | 2500 | 12 | thrust x1.1 |
| GA: Ship Agility 1 | 2000 | 12 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Agility 2 | 2500 | 12 | turn rate x1.1; turn torque x1.21 |
| GA: Ship Energy 1 | 2000 | 12 | energy x1.1 |
| GA: Ship Energy 2 | 2500 | 12 | energy x1.1 |
| GA: EW Range 1 | 2000 | 12 | energy lifespan x1.1 |
| GA: EW Range 2 | 2500 | 12 | energy lifespan x1.1 |
| GA: PW&EW Damage 1 | 2000 | 12 | gun damage x1.1 |
| GA: PW&EW Damage 2 | 2500 | 12 | gun damage x1.1 |
| GA: Faster He3 1 | 2000 | 12 | mining rate x1.1 |
| GA: Faster He3 2 | 2500 | 12 | mining rate x1.1 |
| GA: He3 Yield 1 | 2000 | 12 | mining yield x1.1 |
| GA: He3 Yield 2 | 2500 | 12 | mining yield x1.1 |
| GA: PW Range 1 | 2000 | 12 | ammo speed x1.1 |
| GA: PW Range 2 | 2500 | 12 | ammo speed x1.1 |
| GA: Ripcord 1 | 2000 | 12 | ripcord speed x1.15 |
| GA: Ripcord 2 | 2500 | 12 | ripcord speed x1.15 |
| GA: Ship Sensors 1 | 2000 | 12 | scan x1.1 |
| GA: Ship Sensors 2 | 2500 | 12 | scan x1.1 |
| GA: Ship Hull 1 | 2000 | 12 | ship armor x1.1 |
| GA: Ship Hull 2 | 2500 | 12 | ship armor x1.1 |
| GA: Ship Shield 1 | 2000 | 12 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Shield 2 | 2500 | 12 | ship shield x1.1; ship shield regen x1.1 |
| GA: Ship Signature 1 | 2000 | 12 | signature resistance x1.1 |
| GA: Ship Signature 2 | 2500 | 12 | signature resistance x1.1 |
| GA: Ship Speed 1 | 4000 | 12 | speed x1.1 |
| GA: Ship Speed 2 | 5000 | 12 | speed x1.1 |
| GA: Station Hull 1 | 2000 | 12 | station armor x1.1; station armor regen x1.1 |
| GA: Station Hull 2 | 2500 | 12 | station armor x1.1; station armor regen x1.1 |
| GA: Station Shield 1 | 2000 | 12 | station shield x1.1; station shield regen x1.1 |
| GA: Station Shield 2 | 2500 | 12 | station shield x1.1; station shield regen x1.1 |

