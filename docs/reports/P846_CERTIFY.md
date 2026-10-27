# P846 — certify the event-driven mission rules: site events (kill, damage, mining)

Base: `master` at dispatch (`ed1894bd`, "P836: HUD level screenshots …").
Native reference: `~/Development/Allegiance/src/` (read-only, every line number re-grepped for this report).
Changes are left in the working tree; nothing was committed or pushed, no remote host was contacted.

Batch: 17 rows of `docs/parity/sim-rules-matrix.csv`, family **FedSrv mission rules (event-driven)**,
weight 37 — `SIM-0646…0661` (`TerminateModelEvent`, `KillShipEvent`, `DamageShipEvent`,
`DamageStationEvent`, `KillStationEvent`, the three `KillAsteroidEvent` overloads,
`DrainAsteroidEvent`, `MineAsteroidEvent`, `KillProbeEvent`, `KillBuildingEffectEvent`,
`KillTreasureEvent`, `KillMissileEvent`, `KillMineEvent`, `WarpBombEvent`) plus `SIM-0768` (`ExportObj`).

## Result

| | before | after |
|---|---|---|
| family weighted | **1.52 %** (9/592) | **5.74 %** (34/592) |
| family unweighted | 1.18 % (3/254) | 5.51 % (14/254) |
| batch (weight 37) | 0 implemented | **25/37 weight implemented** (11 of 17 rows), 12/37 remain `partial` |
| TOTAL sim-rules weighted | 67.92 % | 69.25 % |
| TOTAL sim-rules unweighted | 61.29 % | 62.90 % |

`python3 scripts/parity-scorecard.py` validates (840 rows, no shifted fields, every reachability legal).

Six rows stay `partial`, each with the exact remaining branch in `notes`. No row was marked `n/a`: every
one of these bodies is reachable from the mission site (the one exception, `DrainAsteroidEvent`, has an
*unreachable native body* and is certified as such, not waved through as `n/a`).

### Behaviour fixes landed by this batch

1. **`KillShipEvent` partial-kill shares** (`SIM-0647`). Native walks *every* enemy bucket of the
   victim's damage track and calls `KillShip(pship, bucketTotal/totalDamage)` (`FedSrv.CPP:11826-11861`);
   the port credited only the top dealer with a flat `1.0` and left every helper contributor at `0`.
   Now every enemy player bucket earns its damage share (`DamageTrack.Buckets`, `AwardPartialKillShares`).
2. **`DamageStationEvent` allied leg** (`SIM-0649`). Native's `#ALLY` branches were missing entirely:
   the `(Ally)` prefix, `CHAT_ALLIES` addressing/audience (`FedSrv.CPP:12196`), and the allied player's
   private *"Watch your fire"* (`:12222-12228`). All three are now produced.
3. **`DamageShipEvent` production alliance source** (`SIM-0648`). The allied gate read only the
   skirmish/test `Mission.AlliedSidePairs`; a real lobby populates `Side.AllyGroup` (and native
   `pside->AlliedSides(...)` reads that group). The gate now uses `AlliedSides(...)` (AllyGroup) first,
   so allied FF warnings work in a real game, not only in a test that injects pairs.
4. **`KillTreasureEvent` `STAGE_STARTED` gate** (`SIM-0658`). Native announces `DESTROY_TREASURE` and
   respawns a `c_tcFlag` **only** while the mission is `STAGE_STARTED`; `ptreasure->Terminate()` is
   outside the gate (`FedSrv.CPP:12495-12509`). The port announced unconditionally, so a treasure that
   expired during the pre-start countdown (flag/artifact setup, `MissionStage.Starting`, where the port's
   sectors already tick) leaked a destruction message for an object no client had. The gate is now exact;
   the terminate still always runs.

## Per-row certification

Legend: **equal** = the native branch is reproduced. **different** = reproduced with a stated deviation.
**missing** = native branch has no port counterpart. **n/a** = does not apply (era/architecture/native dead
code), with the reason.

### SIM-0646 — `FedSrvSiteBase::TerminateModelEvent` (`FedSrv.CPP:11630-11632`) — implemented

Native body is literally `{ }`: the server site performs no work when a model terminates (`CmodelIGC::Terminate`,
`Igc/modelIGC.cpp:55-61`, calls the site then detaches the model).

| native branch | port site | verdict |
|---|---|---|
| entire body (empty) | `IIgcSite.cs:147` default no-op; `ServerHost.cs:1358-1361` optional `ModelTerminated` hook (wired by nobody); `Sector.cs:500-505` raises the seam once per real termination | equal |

Fix: none needed. Test: `P846SiteEventTests.Terminating_a_model_notifies_the_site_once_and_nothing_else`
(one seam call, no kill/asteroid announcement).

### SIM-0647 — `FedSrvSiteBase::KillShipEvent` (`FedSrv.CPP:11634-12087`) — implemented

| native branch | port site | verdict |
|---|---|---|
| credit transfer to the top damage bucket, same mission (`:11644-11655`) | `Ship.ReceiveDamage` picks `DamageTrack.TopDealer` (`Sim/Ship.cs:5098-5110`) | equal |
| assist: first non-allied enemy **player** ship after the top bucket (`:11656-11675`) | `Server.Combat.cs:1755-1780` (`DealersAfterTop`, peer-owned only) | equal |
| child ships die recursively, `pdmglink`/loot only for `pshipParent == NULL` (`:11646,11677-11683`) | `victim.ParentShip is null` loot guard `Server.Combat.cs:1690-1691`; no turret parenting in production | n/a (design: no child ships) |
| per part: `randomInt(0,4)==0` → part treasure (dv 100, 60 s), then `Terminate` (`:11686-11696`) | `DropKillShipLoot` `Server.Combat.cs:2088-2120` | equal |
| ammo > 0: 1-in-5 ammo-pack treasure (30 s), then ammo=0 (`:11699-11713`) | `:2122-2148` | equal |
| fuel > 0: 1-in-5 fuel-pack treasure (30 s), then fuel=0 either way (`:11714-11728`) | `:2150-2165` | equal |
| carried flag: `SetFlag(NA)`; outside universe (`x²+y²+z²/lm² > (1.5·radius)²`) → `RespawnFlag`, else 10-day `c_tcFlag` treasure; whole-mission `GAIN_FLAG(NA, ship)` (`:11730-11762`) | `:2168-2192` + `IsOutsideUniverse` `:2200-2212` + `BroadcastGainFlag` | equal |
| `bLifepod = parent==NULL && c_habmLifepod`; player `bKilled = !bEjectPods \|\| bLifepod \|\| amount<0`; non-player always `bKilled` (`:11770-11785`) | `Server.Combat.cs:1715-1716` (`owner is null \|\| bLifepod \|\| amount < 0`); `bEjectPods` is not a decoded port mission param, eject pods are always on | different (config surface absent — documented since P107) |
| `SetExperience(0)` when killed (`:11783-11787`) | `victim.ResetKillBonus()` `:1733` | equal |
| `amount != 0` → `KILL_SHIP`: shipID, `bKillCredit`, `bDeathCredit`, `sideidKiller`, `typeCredit`/`idCredit`, to `GetGroupMission` (`:11789-11810,11906-11915`) | `:1831-1855` | equal |
| credit block: `AddKill` + `AdjustCombatRating` + non-allied `AddExperience`, else side `AddKill` (`:11812-11824,11863-11870`) | `:1773-1813` (`AddKill`, `AdjustCombatRating`, `AddKillBonus`, `killerSide.Kills++`) | equal |
| partial kills: for each enemy ship bucket, `KillShip(pship, share)`, plus parent divisor (`:11826-11861`) | `AwardPartialKillShares` `:2050-2094` (called at `:1807`) (**fixed this task**), parent divisor n/a | equal / n/a (no turrets) |
| `CheckForVictoryByKills` → `GameOver(reason)` (`:11872-11887`) | `RecomputeFlagsAndKills` + `OnFlagsKillsComputed` → `DeclareGameOver` (`:1822-1830`) | equal (reason text is the generic P178 string — presentation) |
| Steam achievements, >4 players per side, non-pod (`Xynth #8`, `:11890-11905`) | — | n/a (no Steam) |
| eject: `AddEjection`, retarget away missiles, lifepod hull, random escape vector biased away from the launcher, `EJECT` to sector-flying (`:11917-12024`) | `EjectSurvivor` `:2472-2540` | equal |
| death: `AnnounceExit(SDR_KILLED/SDR_TERMINATE)`, `AddDeath`, nearest `c_sabmRestart` station else side base, `SetCluster(NULL)`, lifepod hull, `ShipStatusRestart`+`ENTER_LIFEPOD` (own peer), `SetStation`+docked, `SetCluster`, ghost-side deactivate; non-player: delete drone (`:12026-12086`) | `:1850-1995` (`BroadcastShipDeparted`, `AddDeath`, `FindFriendlyStation`, hull change, `EnterLifepod`, `DeactivateSide`, `FinalizeDeadShip`) | equal |

Fix: partial-kill shares (see above). Tests: `P846SiteEventTests.KillShip_splits_victim_class_credit_by_damage_share`
(fails on the pre-fix flat 1.0), plus the affected-feed suites (`LivePlayerScoringFeedsTests`, `P746`, `LiveCombatScoringTests`,
`KillShipLootDropTests`, `P752`).

### SIM-0648 — `FedSrvSiteBase::DamageShipEvent` (`FedSrv.CPP:12089-12181`) — implemented

| native branch | port site | verdict |
|---|---|---|
| gates: `amount >= 1`, launcher non-null, `!(type & c_dmgidNoWarn)`, launcher side (`:12091-12097`) | `Server.Combat.cs:1242-1247` | equal |
| 10 s per-victim throttle, NPC stamps always / player only when whispered (`:12103,12172,12183`) | `:1263-1290` | equal |
| NPC voice by pilot type: carrier / miner (<50 % → critical) / builder (station type's constructor sound) / layer+default (`:12106-12130`) | `SendNpcUnderAttackAlert` `:1341-1390` | equal |
| `(Ally)` prefix + team "Under attack by … in …" (`:12131-12145`) | `:1370-1395` | equal (alliance source **fixed**: `AlliedSides`/`Side.AllyGroup` now, not only `AlliedSidePairs`) |
| same-side or allied player launcher → private "Watch your fire" (`:12131-12145,12161-12171`) | `SendWatchYourFire` `:1315-1337` | equal |
| player victim: only allied cross-side whispers; no team alert (`:12174-12185`) | `:1283-1292` | equal |

Fix: production alliance source (above). Tests: the six `ShipDamageAlertTests` cases (incl. the allied
prefix + whisper pair that would go silent for a real lobby before the fix).

### SIM-0649 — `FedSrvSiteBase::DamageStationEvent` (`FedSrv.CPP:12183-12235`) — implemented

| native branch | port site | verdict |
|---|---|---|
| gates: `amount >= 1`, launcher, no `c_dmgidNoWarn`, launcher side (`:12185-12189`) | `Server.Combat.cs:1119-1126` | equal (non-`Ship` launcher ignored — see note) |
| 10 s station throttle, stamped only after a passing block (`:12190,12231`) | `:1130-1133,1191` | equal |
| cross-side: critical (<0.30 pre-hit) vs normal text with station/sector/launcher names and type voice ids (`:12199-12212`) | `:1146-1188` | equal |
| allied launcher: `(Ally)` prefix, `ct = GetAllies()!=NA ? CHAT_ALLIES : CHAT_TEAM`, audience = defending side (+ allies) (`:12192-12198`) | `:1144,1158-1175`, `StationSideHasAllies` `:1223-1225` (**fixed this task**) | equal |
| allied player launcher → private "Watch your fire" (sender NULL) (`:12222-12228`) | `SendStationWatchYourFire` `:1200-1219` (**fixed this task**) | equal |
| same-side player launcher → private "Watch your fire" (`:12221-12228`) | same helper | equal |
| native casts **any** launcher to `IshipIGC` before `IsPlayer` (`:12223`), UB for a missile/mine launcher | port requires a `Ship` launcher; its ordnance paths always pass the firing ship | n/a (native bug, unsafe cast) |

Fix: allied leg (above). Tests: `StationUnderAttackAlertLiveTests` (3 existing + the new
`Allied_cross_side_station_hit_adds_ally_prefix_and_watch_your_fire`).

### SIM-0650 — `FedSrvSiteBase::KillStationEvent` (`FedSrv.CPP:12237-12409`) — partial

| native branch | port site | verdict |
|---|---|---|
| `VacateStation(pstation)` first (`:12241`) | `BroadcastStationDestroyed` `Server.Combat.cs:1007-1067` (undock / ghost re-dock, fsmission.cpp:4783-4849) | equal |
| ship launcher: `KillBase(true)`, Steam base-kill, children `KillBase(false)` (`:12245-12267`) | `:975-985`; children n/a (no turrets); Steam n/a | equal / n/a |
| `psideLauncher->AddBaseKill()` for any launcher with a side (`:12269-12273`) | `:988-997` (`Side.BaseKills++`) | equal |
| `STATION_DESTROYED(stationID, launcher ship id or NA)` to `GetGroupRealSides` (`:12275-12279`) | `:1069-1085` | equal |
| AGC `StationDeleted` event + commented-out escort event (`:12281-12327`) | — | n/a (no AGC/COM) |
| `CheckForVictoryByStationKill` → `GameOver`; launcher-side check incl. `AlliedSides`; allied win-reason name list (`:12329-12358`) | `Sector.cs:2607-2613` recompute + `OnVictoryByStationsComputed` `:1501-1530` | different: winner/declare is equal, the reason string is the generic P178 text (allied name list not built) |
| **else** (no win) `SendChatf(NULL, CHAT_EVERYONE, NA, NA, "%s has destroyed %s's %s.", …)` (`:12360-12363`) | — | **missing** |
| #121 stale rock for sides that cannot see the station (`:12366-12372`) | `ServerHost.cs:929-936` + `Side.StaleRockSides` | equal |
| `pstation->Terminate()` (`:12374`) | `Sector.RemoveStation` before the raise (`Sector.cs:2601`) | equal |
| `UpdateTerritory()` for every side (`:12376-12378`) | `RecomputeConquestAndTerritory` (`Sector.cs:2610`) | equal |

Remaining (why partial): the non-victory "X has destroyed Y's Z" chat and the allied win-reason name list.
The port's conquest/territory recompute runs **after** the `KillStationEvent` callback, so the handler
cannot know whether the kill ended the game; emitting the chat unconditionally would double-announce a
game-ending kill, and reordering would put `GAME_STATE(155)` before `STATION_DESTROYED(59)`.

### SIM-0651 — `FedSrvSiteBase::KillAsteroidEvent(IasteroidIGC*, bool)` (`FedSrv.CPP:12411-12421`) — implemented

| native branch | port site | verdict |
|---|---|---|
| `ASTEROID_DESTROYED(clusterID, asteroidID, explodeF)` to `GetGroupRealSides` (`:12413-12418`) | `ServerHost.cs:890-895` → `Server.Combat.cs:917-933` | equal |
| `pasteroid->Terminate()` (`:12420`) | `AsteroidKilled` handler calls `Sector.RemoveAsteroid` (`ServerHost.cs:894`) | equal |

Test: `AsteroidCombatLiveTests.Lethal_hit_kills_asteroid_removes_from_mineable_and_broadcasts_destroy_to_whole_mission`.

### SIM-0652 — `FedSrvSiteBase::KillAsteroidEvent(IasteroidIGC*, IsideIGC*, bool)` (`FedSrv.CPP:12423-12431`) — partial

| native branch | port site | verdict |
|---|---|---|
| side-scoped `ASTEROID_DESTROYED` to `CFSSide::FromIGC(side)->GetGroup()`, **no terminate** (`:12425-12430`) | no `IIgcSite` overload; receipt produced by `Server.Research.cs:40-63 OnStationConstructed` (retirement packet per peer) + `ExportDiscoveredStations` (`:14-37`) + `Side.StaleRockSides` | different: same wire/fields and no terminate, but the port's retirement audience is broader (every non-owner, non-stale peer, including sides that never saw the rock) and the overload itself is unmodelled |

Only caller: `BuildStation` (`:11432,:11447`). Test: `P747ConstructionTests.Stale_rock_discovery_is_side_scoped_ship_only_and_emitted_once`.

### SIM-0653 — `FedSrvSiteBase::KillAsteroidEvent(AsteroidID, SectorID, IsideIGC*)` (`FedSrv.CPP:12433-12441`) — implemented

| native branch | port site | verdict |
|---|---|---|
| `ASTEROID_DESTROYED(soid, roid, explodeF=false)` to one side's group, no terminate (`:12435-12440`) | `ServerHost.cs:346-349` → `Server.Combat.cs:908-916`; raised at `Sector.cs:5436` and in the station-kill sweep `ServerHost.cs:929-936` | equal |

Test: `P747ConstructionTests.Stale_rock_discovery_is_side_scoped_ship_only_and_emitted_once`.

### SIM-0654 — `FedSrvSiteBase::DrainAsteroidEvent` (`FedSrv.CPP:12443-12452`) — implemented

| native branch | port site | verdict |
|---|---|---|
| whole-mission `ASTEROID_DRAINED` (`:12445-12450`) | — | n/a: **unreachable**. Its only caller, `CasteroidIGC::MineOre`, has the call commented out (`Igc/asteroidigc.h:228-231`, *"NYI -- re-enable if He3 does not regenerate"*) |
| `pasteroid->SetOre(0.0f)` (`:12451`) | `Asteroid.MineOre` zeroes `OreRemaining` when the request exceeds the pool (`Sim/Asteroid.cs:195-198`) | equal (same state, no event) |

Test: `P846SiteEventTests.Tapping_the_last_ore_zeroes_the_rock_without_DrainAsteroidEvent` (rock tapped,
no `DrainAsteroidEvent`, sibling `MineAsteroidEvent` still fires).

### SIM-0655 — `FedSrvSiteBase::MineAsteroidEvent` (`FedSrv.CPP:12454-12472`) — partial

| native branch | port site | verdict |
|---|---|---|
| per side with `GetAsteroidCurrentEye` → side-scoped `ASTEROID_MINED(clusterID, asteroidID, bpOreFraction)` (`:12456-12469`) | `ServerHost.cs:1135-1136` → `Server.Combat.cs:2610-2640 OnMine` (whole mission) | different: audience is `GetGroupRealSides`-wide, not eye-gated (the port has no per-side `ASTEROIDS_UPDATE` eye channel; deliberate, and certified by `AsteroidMinedLiveTests`) |
| `pasteroid->SetOreWithFraction(newOre, false)` (`:12470`) | `Asteroid.MineOre` already wrote the same ore (`Sim/Asteroid.cs:195-205`); native's call recomputes `fraction*oreMax` — the identity of the fraction it was handed — plus per-side `oreSeenBySide` bookkeeping, which the port models through `Asteroid.OreSeenBySide`/`ASTEROIDS_UPDATE` | equal |
| `> 3.0` reporting threshold (`Igc/asteroidigc.h:246-251`) | `Asteroid.MineOre` `_lastReportedOre` gate (`:236-251`) | equal |

Remaining: the per-side eye audience. Tests: `AsteroidMinedLiveTests` (3) + `MiningTests.Near_rock_…`.

### SIM-0656 — `FedSrvSiteBase::KillProbeEvent` (`FedSrv.CPP:12474-12483`) — partial

| native branch | port site | verdict |
|---|---|---|
| `PROBE_DESTROYED(clusterID, probeID)` to `GetGroupRealSides` (`:12476-12481`) | `ServerHost.cs:817-818` → `Server.Combat.cs:829-840` (owner side + recorded viewers) | different: audience narrower than native's whole mission (deliberate same-side ordnance-visibility model, P99/P260) |
| `pprobe->Terminate()` (`:12482`) | probe removal + `ProbeDestroyedEvent` raise (`Sim/Probe.cs:268,570,695`) | equal |

Tests: `ProbeBroadcastTests`, `ProbeCombatLiveTests.Lethal_hit_kills_probe_…`.

### SIM-0657 — `FedSrvSiteBase::KillBuildingEffectEvent` (`FedSrv.CPP:12484-12492`) — implemented

| native branch | port site | verdict |
|---|---|---|
| `BUILDINGEFFECT_DESTROYED(asteroidID)` to `GetGroupRealSides` (`:12486-12491`) | `Server.Docking.cs:1266-1280` (whole mission), fed by the interruption drain `Sector.cs`/`Server.Broadcast.cs:298-303` | equal |
| `pbe->Terminate()` (`:12491`) | `ServerHost.cs:339-342` → `Sector.TerminateBuildingEffect` (`Sector.cs:5280-5295`) | equal (callback terminates first, broadcast drains in the same tick — identical message set/audience) |

Test: `LiveBuildingEffectWireTests.Interrupted_build_broadcasts_BuildingEffectDestroyed_whole_mission`.

### SIM-0658 — `FedSrvSiteBase::KillTreasureEvent` (`FedSrv.CPP:12493-12510`) — implemented

| native branch | port site | verdict |
|---|---|---|
| gate: `m_pfsMission && stage == STAGE_STARTED` (`:12495`) | `Server.Docking.cs:1200-1212` (**fixed this task**; mission from `OwnerMission`, falling back to the sector's mission) | equal |
| `DESTROY_TREASURE(treasureID, sectorID)` to `GetGroupRealSides`, **not guaranteed** (`:12498-12503`) | `:1213-1225` | equal |
| `c_tcFlag` → `RespawnFlag(treasureID, NULL)` (`:12504-12506`) | `Server.Lifecycle.cs:835-843` (gated the same way — **fixed**) | equal |
| `ptreasure->Terminate()` **outside** the gate (`:12509`) | `sector.Terminate(treasure)` before the gate (`:1203`) | equal |

Tests: `P846SiteEventTests.Treasure_expiry_before_STAGE_STARTED_terminates_silently` (fails before the gate),
`TreasureExpiryTests`, `LiveGainFlagTests` (flag pickup/secure), `LiveTreasurePickupTests`.

### SIM-0659 — `FedSrvSiteBase::KillMissileEvent` (`FedSrv.CPP:12512-12529`) — partial

| native branch | port site | verdict |
|---|---|---|
| `MISSILE_DESTROYED(clusterID, missileID, position)` to `GetGroupRealSides` (`:12514-12521`) | `ServerHost.cs:478` → `Server.Combat.cs:342-402` (recorded viewers, same- and cross-side) | different: audience narrower than native's whole mission (deliberate model) |
| `if (position != Vector::GetZero()) pmissile->Explode(position)` — the real blast (`:12523-12528`) | `Sim/Missile.cs:146-157` (non-zero position → `ResolveExplosion`), `:192-199` (expiry), aleph/consumer paths `Sector.cs:4398-4487` | equal |
| `pmissile->Terminate()` | `Sector.Terminate` before the raise (`Missile.cs:152`) | equal |

Tests: `MissileBroadcastTests`, `WarpBombTests` (3 domain + 1 live).

### SIM-0660 — `FedSrvSiteBase::KillMineEvent` (`FedSrv.CPP:12531-12540`) — partial

| native branch | port site | verdict |
|---|---|---|
| `MINE_DESTROYED(clusterID, mineID)` to `GetGroupRealSides` (`:12533-12538`) | `ServerHost.cs:703` → `Server.Combat.cs:659-700` (owner side + cross-side export latch) | different: audience narrower than native (deliberate model) |
| `pmine->Terminate()` (`:12539`) | mine removal + `MineDestroyedEvent` (`Sim/Mine.cs:272-284`) | equal |

Tests: `MineBroadcastTests`, `MineCrossSideVisibilityLiveTests`.

### SIM-0661 — `FedSrvSiteBase::WarpBombEvent` (`FedSrv.CPP:12543-12587`) — implemented

| native branch | port site | verdict |
|---|---|---|
| `timeExplosion = warp.GetLastUpdate() + c_fcidWarpBombDelay` (loaded slot 32) (`:12555`) | `Sector.cs:4444-4446` (`now + EffectiveWarpBombDelay`), captured on `Missile.WarpBombTime`; `Server.Combat.cs:865-882` uses it verbatim | equal |
| `WARP_BOMB(timeExplosion, warpidBombed, expendableidMissile)` to `GetGroupRealSides` (`:12556-12558`) | `Server.Combat.cs:869-895` (whole mission, ms) | equal |
| `pwarp->AddBomb(timeExplosion, pmt)` (`:12560`) | `Sector.ScheduleWarpBomb` (`Sector.cs:4515-4521`) + detonation at `:4495-4513` | equal |
| commented-out immediate double explosion (`:12562-12586`) | — | n/a (native dead code) |

Tests: `WarpBombTests.Live_whole_mission_warp_bomb_reaches_the_cross_side_peer_and_the_delayed_blast_lands`,
`WarpBombTests.Domain_missile_in_warp_radius_…`, `Domain_warp_bomb_missile_hitting_a_ship_deals_no_damage`.

### SIM-0768 — `ExportObj` (`FedSrv.CPP:2022-2043`) — implemented

| native branch | port site | verdict |
|---|---|---|
| `cbExport = pIGC->Export(NULL)`, allocate `FM_S_EXPORT` with a var blob, set `objecttype`, serialize the blob (`:2024-2042`) | shared `IFedMessage` envelope in `Wire/ExportShip.cs` and the per-type codecs `ExportStation/ExportProbe/ExportMine/ExportBuildingEffect/ExportAsteroid/ExportWarp/ExportCluster` (objecttype + serialized blob, same `FM_S_EXPORT(26)`) | equal |
| `ppfmExport` out-parameter (hand the allocation back to the caller) vs send in place (`:2026-2043`) | — | n/a (C++ ownership plumbing; the port returns the message object and the caller packs it) |
| callers: runtime object exports from `ShowObject`/join sync (`:10778,10787,10828`, `fsmission.cpp:4206-4212`, `FedSrv.CPP:13763`) | `Server.Lobby.cs:4170-4265` join exports, `Server.Research.cs:14-37` discovery export, `Broadcast*` creation paths | equal |
| static catalog exports: station/projectile/part/hull/development/drone/civilization types (`:4417-4473`) | — | n/a: this port's client loads the same local core artifact (`PCore001.igc`), so type objects never cross the wire |

Tests: `AsteroidExportTests`, `CrossSideExportLiveTests`, `LiveBuildingEffectStartWireTests`,
`P591dMinefieldExportAudienceTests`.

## Verification

All commands run from the worktree, `~/.dotnet/dotnet`, no `git stash`, no remote host.

- **Build:** `dotnet build AllegianceCore.slnx` → **0 warnings, 0 errors**.
- **Tools:** `tools/build-tools.sh` → all 90 tool projects, 0 warnings.
- **AOT (Server + Core changed):** `dotnet publish src/Allegiance.Server/… -c Release -r linux-x64 --self-contained -p:PublishAot=true`
  (output under `.scratch/aot`, deleted afterwards) → exit 0, **no IL2026/IL3050**, zero warning lines.
- **New/affected tests 3× alone** (artwork corpus present):
  - `P846SiteEventTests` + `StationUnderAttackAlertLiveTests` + `LivePlayerScoringFeedsTests` + `LiveTreasurePickupTests`
    → **19/19 passed, 3 consecutive runs** (≈4 s each).
  - Regression sweep over `Treasure|Kill|Scoring|DamageTrack|Assist|Asteroid|Mining|StationUnderAttack|ShipDamageAlert`
    → **320/320 passed**.
- **One fast-tier run:** `tools/run-tests.sh --tier fast` (results dir under the worktree's gitignored `.scratch/`, deleted afterwards) →
  **PASS**, 11,013 tests in 305 s: `Allegiance.Update.Tests` 28, `Allegiance.Lobby.Tests` 82,
  `Allegiance.ClientCore.MonoGame.Tests` 3602 (+3 NotExecuted), `Allegiance.ClientCore.Tests` 1849 + 2 quarantine,
  `Allegiance.Core.Tests` 5437 + 10 quarantine; infrastructure checks PASS; no fast test exceeded the 10 s limit.
- **Showcase never-parks 5×** (`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`,
  idle host, load ≈0.14/CPU): **5/5 passed** — 4 m 05 s, 4 m 08 s, 4 m 06 s, 4 m 11 s, 3 m 11 s (the documented
  base rate is 5/5; the test fails if either match parks, so no parking regression).
- These tests fail on the pre-fix code: the kill-share test (flat `1.0`/`0`), the treasure stage test
  (announcement while `STAGE_STARTED` was false), and the allied station test (no `CHAT_ALLIES`/prefix/whisper).
  `LivePlayerScoringFeedsTests.Killing_a_mining_hull_feeds_the_miner_kill_counter` was updated to build the real
  damage-track bucket the native loop needs (it previously drove `BroadcastKillShip` directly with no track,
  which native credits with `AddKill` but no class-kill share).

## Native-intent classification

- **Design (ported as-is):** the whole `KillShipEvent` structure (top-damage credit, assists, loot rolls,
  eject/death branches, ghost elimination), the `Damage*` gates/throttles/voice lines, the three
  `KillAsteroidEvent` overloads, `WarpBombEvent`'s delayed double-detonation, `ExportObj`'s per-object export.
- **Era/architecture constraints (n/a or adapted, stated above):** Steam achievements, AGC/COM events,
  DirectPlay/SQL plumbing, `bEjectPods` as an undecoded mission param, the static type-catalog exports
  (shared local core), the `ppfmExport` allocation out-parameter, and the narrower same-side audiences for
  probe/mine/missile destruction (the port's ordnance-visibility model).
- **Native bugs / quirks (kept or deliberately improved, with the reason):**
  - `DamageStationEvent` casts *any* launcher to `IshipIGC` before `IsPlayer` (`FedSrv.CPP:12223`) — unsafe
    for missile/mine launchers; the port requires a `Ship` launcher instead of reproducing the UB.
  - `KillShipEvent` lets a *non-ship* top damage bucket (a missile) become `pcredit`, which suppresses all
    player kill credit (`FedSrv.CPP:11812-11826`); the port's track records Ship launchers only, so the
    firing ship keeps its credit — the native comment itself says the intent was "Get the player credited".
  - `DrainAsteroidEvent`'s only caller is commented out (`asteroidigc.h:228-231`) — native dead code; the
    port matches the *reachable* behaviour and pins it with a test.
  - Native's allied win-reason and the non-victory destroy announcement are presentation text; the port's
    missing branch is tracked rather than invented (SIM-0650 partial).

## Modern / better-approach note

The port keeps the native rule where it matters and replaces native plumbing with typed seams:
`DamageTrack.Buckets` is a read-only snapshot (no aliasing into the live track), the destroy/damage
broadcasts are explicit task-queued steps on the sim thread (`Server.Combat`/`Server.Docking`) instead of
inline `FedMessaging` sends with `FM_FLUSH`, and the treasure stage gate reads the authoritative
`Mission.Stage` rather than a mission-params pointer. The two audience deviations from `GetGroupRealSides`
(probe/mine/missile destruction) are the one place where the port deliberately improves on native: native
reveals the destruction of an object a side never saw, while the port's per-side visibility model does not.
If exact native wire parity is preferred there, the change is mechanical (drop the viewer filters and send
to all mission peers) but it contradicts the project's certified same-side ordnance-visibility rows, so it
was left as `partial` rather than changed unilaterally.

## Unfinished / residual risk

1. `SIM-0650`, `SIM-0652`, `SIM-0655`, `SIM-0656`, `SIM-0659`, `SIM-0660` remain `partial`; the remaining
   branch and the reason are in each row's `notes` (two are presentation/ordering, four are audience).
2. `BroadcastStationDamaged` now emits `CHAT_ALLIES` and a non-zero `oidRecipient` on the `CHAT_TEAM`
   variant (native passes the defending side id). Clients render both; the live loopback test decodes them.
