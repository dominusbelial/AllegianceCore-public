# P847 — Certify the event-driven mission rules: site docking, ripcord, warp, loadout, treasure, payday, chat, building

Batch (site docking / ripcord / warp / loadout / treasure / payday / chat / building), 30 rows,
weight 62: SIM-0662 (AlliedRearm) · SIM-0663 (LandOnCarrierEvent) · SIM-0664 (RescueShipEvent) ·
SIM-0665 (CaptureStationEvent) · SIM-0666 (DockWithStationEvent) · SIM-0667 (ChangeStation) ·
SIM-0669 (RequestRipcord) · SIM-0670 (ContinueRipcord) · SIM-0671 (UseRipcord) ·
SIM-0672 (HitWarpEvent) · SIM-0673 (ChangeCluster) · SIM-0674 (LoadoutChangeEvent) ·
SIM-0675 (HitTreasureEvent) · SIM-0676 (PaydayEvent) · SIM-0677 (SendChat) ·
SIM-0678 (ClusterUpdateEvent) · SIM-0679 (CommandChangedEvent) · SIM-0680 (CreateBuildingEffect) ·
SIM-0681 (LayExpendable) · SIM-0682 (RespawnFlag) · SIM-0771 (LeaveShip) · SIM-0772 (BoardShip) ·
SIM-0775/SIM-0776/SIM-0838 (the three CreateTreasure overloads) · SIM-0777/SIM-0778 (DisplaceProbe) ·
SIM-0780 (RefillPart) · SIM-0781 (ErasePart) · plus **SIM-0512 (GiveSideMoney + callers)** from the
P841 review.

Repo: public AllegianceCore worktree at master (`P847_BRIEF.md` present). Native reference read-only:
`~/Development/Allegiance/src/`. Every native line number was re-grepped at dispatch time
(`FedSrv.CPP` = 14 126 lines, `fsmission.cpp` = 6 199 lines).

**Result: 25 of 30 rows certified `implemented`, 5 remain `partial` with the exact missing branch named
below.** Family weighted completion rises **25.93 % (153/590) → 34.58 % (204/590)**
(unweighted **25.00 % (63/252) → 34.92 % (88/252)**).

Six behaviour fixes landed in production code, each with a fail-first test (7 of the 9 new tests were
red before the fixes, verified by reverting `src/` and re-running):

| # | Fix | Rows | Test |
|---|---|---|---|
| 1 | NPC cash pickup now runs `GiveSideMoney` = accrue **+ immediate `DoPayday`** | SIM-0512, SIM-0675 | `Npc_cash_pickup_pays_the_side_out_immediately` |
| 2 | Team-lobby quit pays the leaver's money out **immediately**, and after the leaver leaves the side (native order) | SIM-0512 | `Side_leave_pays_the_leavers_money_out_immediately` |
| 3 | The auto-donate re-point block is **stage-gated** (`Started`/`Starting`) | SIM-0512 | `Lobby_time_leader_exit_leaves_donations_untouched` |
| 4 | A **non-leader** leaver's donors are **cleared**, not handed to the sitting leader (`pfsNewLeader == NULL`) | SIM-0512 | `Started_non_leader_exit_clears_donations_instead_of_repointing` |
| 5 | Old-player score cache key is **case-insensitive on insert** (one entry per name, native `_stricmp`) | SIM-0512 family | `Old_player_cache_keeps_one_entry_per_name_ignoring_case` |
| 6 | `CreateTreasure(part)` **amount switch** (magazine/dispenser/pack only, NULL on empty, 0 otherwise) + DROP_PART gate | SIM-0776, SIM-0838 | `Dropped_weapon_treasure_carries_native_zero_amount`, `Dropped_empty_launcher_creates_no_treasure` |
| 7 | Dock restock's desired-loadout match uses native **`MatchPT`** (successor chain), so an upgraded part no longer gets a duplicate base part | SIM-0781 | `Dock_restock_does_not_re_buy_a_desired_type_already_carried_as_its_successor` |

Two further branches were closed with one-line-level changes visible in the same code:
`GiveSideMoney` is now a single ported method shared by every native caller (`Server.Economy.cs:1187`),
and the treasure pickup pass enforces the native `c_ptPlayer` pilot-type gate (`treasureIGC.h:86`)
now that `Ship.PilotType` carries real drone pilot types — an NPC miner no longer collects loot.

---

## Per-row certification

Legend: **EQUAL** = branch reproduced; **DIFF** = port behaves differently; **MISS** = absent;
**n/a** = era/config/data constraint (reason given). Every row's native body was read in full.

### SIM-0512 — `CFSMission::GiveSideMoney` + its three callers (fsmission.cpp:1777-1787) — IMPLEMENTED

Native branches: `assert(money >= 0)`; `if (money != 0)`; `m_rgMoney[sideID] += money`; `DoPayday(pside)`
(np == 0 guard, `delta = money/np`, PAYDAY to the side group, `money -= delta*np`, per-player credit to the
autodonate target else self, fsmission.cpp:2294-2326).
Callers: `PaydayEvent` (FedSrv.CPP:13647-13650), NPC cash treasure pickup (FedSrv.CPP:13612-13615),
`RemovePlayerFromSide`'s side-leave payday (fsmission.cpp:1142-1145).

| native branch | port | verdict |
|---|---|---|
| `if (money != 0)` + accrue + `DoPayday` | `Server.Economy.cs:1187 GiveSideMoney` → `:1195 OnPayday` | **EQUAL** (new single body) |
| PaydayEvent caller | `ServerHost.PaydayEvent` (:300) → `GiveSideMoney` | EQUAL |
| NPC cash caller | `Server.Docking.cs:1020 PerformTreasurePickup` NPC branch → `GiveSideMoney` | **DIFF → fixed** (was treasury-only) |
| side-leave caller (departure) | `Server.Lifecycle.cs:2239 ApplyDeparturePaydayAndOldPlayer` (wallet + full hull value → old side) | EQUAL (already immediate; now via `GiveSideMoney`) |
| side-leave caller (team lobby) | `Server.Lobby.cs MoveToTeamLobby` | **DIFF → fixed** (treasury-only, and paid before the leaver left the side so `DoPayday` split it one extra way) |
| `DoPayday` body | `OnPayday` | EQUAL |
| — donation retarget inside `RemovePlayerFromSide` (:1103-1115) | see below | **DIFF → fixed** |
| — old-player save (:2624-2680) | see below | **DIFF → fixed** |

Also fixed in the same native body's scope: the whole auto-donate re-point block sits inside
`if (STAGE_STARTED == stage || STAGE_STARTING == stage)` (fsmission.cpp:1103) — the port ran it
unconditionally in **both** places it exists (`Server.Economy.cs RetargetDonorsOffLeaver` for the leader
path, `Server.Lifecycle.cs RePointDonationsAfterDeparture` for departure). And native's re-point target is
`pfsNewLeader`, which is **NULL unless the leaver was the side's leader** (it is assigned only inside the
`rgShipIDLeaders[sideidOld] == shipid` block, :918-990) — so a departing *member*'s donors get
`SetAutoDonate(NULL)`, not the sitting leader; the port re-pointed them at the leader (redirecting real
income). Both now match, with tests.
Old-player cache: native `SaveAsOldPlayer` updates the entry `GetOldPlayerLink` finds with
`_stricmp` (fsmission.h:390-398), i.e. **one entry per name ignoring case**; the port's dictionary used
the default tuple comparer, so a save under a different casing would have created a duplicate and a later
rejoin restored whichever entry the scan met first. The key now uses a case-insensitive comparer.

### SIM-0662 — `FedSrvSiteBase::AlliedRearm` (FedSrv.CPP:12655-12812) — PARTIAL

Branches: parts refilled backwards via `RefillPart` (:12662-12675); desired-loadout copy (:12678-12680);
`ErasePart` over Magazine/Dispenser/ChaffLauncher + cargo slots (:12683-12688); fuel/ammo pack
resolution (:12690-12708); fill empty cargo slots from the desired list, else buy fuel/ammo packs
(:12710-12750); `SetAmmo(SHRT_MAX)/SetFuel(FLT_MAX)/SetEnergy(max)/SetFraction(1)/shield 1`
(:12752-12763); `BUY_LOADOUT_ACK{fBoughtEverything, fLaunch}` to the owner (:12766-12778);
`SaveDesiredLoadout` (:12780); `QueueLoadoutChange` + per-child ship-status state/send (:12782-12802);
`g.fm.PurgeOutBox()`; when `bLaunch`, ship + children `SetStation(NULL)` (:12804-12812).

Port: `Server.Docking.cs:1745 RestockOnDock` implements the **own-side dock** equivalent of the body
(RefillPart successor/amount walk :1745-1810, desired-loadout re-buy with the native `MatchPT` rule
:1702/:1780, scalar-pool reset :1837-1842, P441-style value differential). **MISS:** the allied-base entry
point. `AlliedRearm` is only reachable from `ChangeStation`'s allied branch
(FedSrv.CPP:13295-13310), and `Sector.ResolveDockCollisions` rejects every non-own-side dock that does not
capture — the port has no allied-base dock at all. Consequently the `bLaunch` leg
(`BUY_LOADOUT_ACK.fLaunch` + `SetStation(NULL)` for ship and children) has no caller. Reachability stays
`production` only for the own-side restock half.

### SIM-0663 — `FedSrvSiteBase::LandOnCarrierEvent` (FedSrv.CPP:12814-13031) — PARTIAL

Branches: `RefillPart` loop for every lander (:12822-12834); player-only desired-loadout walk, depleted
launcher remount (:12836-12900); scalar resets incl. `SetFraction(1)` + shield fraction (:12855-12861);
launch-slot transform or the no-slot backward launch (:12963-13005); `RecalculateCollisions` (:13006);
`RELAUNCH_SHIP` with the exported loadout, cookie, position/velocity/orientation (:13008-13022); autopilot
Goto→Defend re-task (:13026-13030); always returns true.
Port: `ServerHost.cs:1383` → `Server.Docking.cs:1390 PerformRelaunch`: placement both branches, jitter,
`ExitStationSpeed`, round-robin slot via `Ship.GetLaunchSlot`, `RELAUNCH_SHIP(192)` sector-wide, return
true. **MISS:** the per-part `RefillPart` successor/amount walk, the player desired-loadout re-buy +
depleted-launcher remount, `SetFuel(FLT_MAX)`, hull/shield fraction reset (only Energy/Ammo are reset), and
the autopilot command re-task (the port has no `c_cid*` command model for NPC autopilot). `n/a`:
`RecalculateCollisions` (no rigid-body pass) and the cookie (single-byte 0, established port convention).

### SIM-0664 — `FedSrvSiteBase::RescueShipEvent` (FedSrv.CPP:13033-13077) — IMPLEMENTED

Branches: `if (pshipRescuer)`: rescuer `AddRescue` (:13039), Steam awards (:13041-13052, n/a — no Steam),
`PLAYER_RESCUED` to the rescuer's connection then the rescuee's (:13055-13065); `ResetWarpState`
(:13060); `FindTarget(… c_sabmRestart)` nearest friendly station + `GetBase(own side)` fallback
(:13063-13071); `return pstation ? DockWithStationEvent(...) : false` (:13073-13075).
Port: `ServerHost.cs:1410` → `Server.Docking.cs:735 PerformRescue` (score, both-connection wire, station
search, direct dock-and-latch; no station → no dock, wire already sent). DIFF, documented substitutions:
(i) Steam = era plumbing; (ii) the trailing `DockWithStationEvent` is performed as the port's direct dock
latch rather than through the dock event's own body (announce-exit / ship-status / flag / tech legs) — the
same substitution the P110 teleport path documents, and the client resolves the docked state from the next
`SHIP_STATUS(90)` (StationID + `c_ssDocked`), so the net client-visible effect is equal;
(iii) `FindFriendlyStation` is the port's own-side restart search rather than a spatial nearest search (a
documented stand-in that always returns an own-side station, so native's fallback condition is inherently
satisfied).

### SIM-0665 — `FedSrvSiteBase::CaptureStationEvent` (FedSrv.CPP:13080-13084) — IMPLEMENTED

Native body is one line into `CFSShip::CaptureStation` (fsship.cpp:577-641): `CaptureBase(true)` score,
`SetSide(capturingSide)`, `psideNew->AddBaseCapture()`, `STATION_CAPTURE` to `GetGroupRealSides()`.
Port: `ServerHost.cs:1027` → `Server.Combat.cs:1439 BroadcastStationCapture` (score `CaptureBase(true)`,
`Side.BaseCaptures++`, `STATION_CAPTURE(58)` with sidOld/sidNew/shipIDCredit) driven by the sector's
re-side in `ResolveDockCollisions`. EQUAL. No `VacateStation` on capture (confirmed in native).

### SIM-0666 — `FedSrvSiteBase::DockWithStationEvent` (FedSrv.CPP:13087-13177) — PARTIAL

Branches: `OkToWarp()` gate (:13093); capture when sides differ ∧ board hull ∧ (`!AlliedSides` ∨
`bAllowFriendlyFire`) (:13100-13105); `AnnounceExit(cluster, SDR_DOCKED)` (:13107); `ShipStatusDocked`
(:13110); own-side flag/artifact secure → score `AddFlag`/`AddArtifact`, `Side.SetFlags/SetArtifacts`,
two secure chats, `SetFlag(NA)`, `GAIN_FLAG(NA)` whole mission, `CheckForVictoryByFlags` + `GameOver`,
`RespawnFlag` (:13112-13161); `SetStation` for the ship and every child (:13166-13178).
Port: `ServerHost.cs:1171 DockWithStationEvent` + `Server.Combat.cs:2842 OnShipDocked` (flag secure with
the `pside == psideOld` gate, both secure chats, `GAIN_FLAG(NA)`, flags/artifact victory, `RespawnFlag`,
`SHIP_DELETE(SDR_DOCKED)` to the sector, `FM_S_DOCKED(61)` to the owner) and the `OkToWarp` gate in
`ResolveDockCollisions`. **MISS (2):** (a) the allied exemption in the capture gate — the port captures any
board-hull ship at a non-own station whose shields are down, ignoring `AlliedSides`/`bAllowFriendlyFire`;
(b) children are not given the station (`psl->data()->SetStation(pstation)`), so a boarded gunner keeps
`StationId` NA on its turret status row. Both hinge on the missing allied-dock entry (SIM-0662/0667).

### SIM-0667 — `FedSrvSiteBase::ChangeStation` (FedSrv.CPP:13179-13311) — PARTIAL

Branches: launch branch (`RepairAndRefuel` + `Launch`, gated `STAGE_STARTED` ∧ unboarded, :13185-13191);
first dock at own side → tech recovery over the parts, per-new-tech `GAIN_FLAG{bIsTreasureDocked}`,
`ApplyDevelopmentTechs` + `AddTechsRecovered` + Steam (:13195-13230); dock gates — lifepod at an enemy
station, turrets at a non-own station (:13233-13238); `UpgradeShip` for NPCs (:13240);
**allied branch** (:13242-13310): player at an allied base → repair/refuel + relaunch, with the
lifepod case deleting the ship and re-homing it at the last own-side station + `ForceLoadoutChange`.
Port: `ServerHost.cs:1677 ChangeStation` covers the first four (tech recovery, `GAIN_FLAG`, both dock gates,
NPC upgrade, launch repair/refuel). **MISS:** the entire allied branch (two sub-cases). Unreachable today
because the sector dock gate never admits a non-own-side non-capture dock.

### SIM-0669 — `FedSrvSiteBase::RequestRipcord` (FedSrv.CPP:13319-13378) — IMPLEMENTED

Branches: `pcluster == NULL` cancel with an active model → clear + `RIPCORD_ABORTED` to the requester's
sector (:13327-13339); already anchored in the requested cluster → no-op (:13341); `FindRipcordModel`
→ set model + `ResetRipcordTimeLeft` + `RIPCORD_ACTIVATE` sector-wide on first activation / requester-only
on re-target (:13344-13368); no anchor → `RIPCORD_DENIED` requester-only (:13370-13376).
Port: `Server.Docking.cs:410 RequestRipcordCore` (plus the `FM_C_RIPCORD_REQUEST` gate
`HandleRipcordRequest`, FedSrv.CPP:7717-7721). EQUAL. `SetStateBits(droneRipMaskIGC, 0)` n/a — the port
keeps ripcord state in `Ship.RipcordModel` only, cleared on the same paths.

### SIM-0670 — `FedSrvSiteBase::ContinueRipcord` (FedSrv.CPP:13379-13438) — IMPLEMENTED

Branches: both arms (`bAllowAlliedRip` and regular) with flag-carrier refusal, side/allied anchor,
anchor-left-cluster, ship anchors requiring `c_habmIsRipcordTarget` (or the Lt pair when the ripper has
`c_habmCanLtRipcord`), any other anchor type legal; false → mask clear + `RIPCORD_ABORTED` to the sector.
Port: `RipcordRules.AnchorStillLegal` (Core, shared by the interface default and `ServerHost.cs:1444`),
with the abort broadcast in the sector's false branch. EQUAL.

### SIM-0671 — `FedSrvSiteBase::UseRipcord` (FedSrv.CPP:13440-13505) — IMPLEMENTED

Branches: station → `station->Launch(pship)` (:13441-13443); ship → energy gate on the TARGET against the
RIPPER's `RipcordCost`, `RIPCORD_ABORTED` + `return true` when short (:13446-13460); `r = target.Radius +
ripper.Radius + 25`, random direction, `ExitStationSpeed`, zeroed yaw/pitch/roll, `SetCluster`
(:13461-13476); probe falls into the same non-station placement with no cost; always returns true
(:13504); `ShipStatusSpotted` (:13499).
Port: `ServerHost.cs:1459` → `Server.Docking.cs:552 PerformRipcordTeleport` (station launch core, ship
energy + placement, probe placement, cluster move, abort broadcast, attempt always consumed). EQUAL;
`ShipStatusSpotted` has no port wire trigger (stated, not silent).

### SIM-0672 — `FedSrvSiteBase::HitWarpEvent` (FedSrv.CPP:13506-13511) — IMPLEMENTED

Native body: `pfsShip->HitWarp(pwarp)` — the whole rule is `CFSShip::HitWarp` (fsship.cpp:71-137).
Port: `ServerHost.cs:1542 HitWarpEvent` (WarpReady gate, Andon mass limit, `AlephExit` frame/velocity/roll,
`LastArrivalWarpId`, sector list move, `PlayerWarpState = NoUpdate`, `SET_CLUSTER` via `ShipWarped`,
`ChangeCluster`). EQUAL. Design: the aleph spin about `dataWarp->rotation` is not modelled; no rigid-body
`RecalculateCollisions`.

### SIM-0673 — `FedSrvSiteBase::ChangeCluster` (FedSrv.CPP:13513-13528) — IMPLEMENTED

Native: announce the exit with `SDR_LEFTSECTOR` only for a real old→new cluster move, then `SetCluster`.
Port: `ServerHost.cs:1651 ChangeCluster` → `BroadcastShipDeparted(SDR_LEFTSECTOR)` to the old sector's
peers + peer snapshot update; dock/death/leave keep their own `SHIP_DELETE` reasons. EQUAL.

### SIM-0674 — `FedSrvSiteBase::LoadoutChangeEvent` (FedSrv.CPP:13530-13556) — IMPLEMENTED

Native: `lc == c_lcAdded ∧ ET_Weapon ∧ blast radius > 0` → re-send `CommandChangedEvent(c_cmdCurrent,
target, GetCommandID(c_cmdCurrent))`; the trailing `ShipLoadoutChange` block is commented out in native.
Port: `ServerHost.cs:602 LoadoutChangeEvent` (relay + the same AoE re-send using the ship's real current
command id) → `Server.Combat.cs:459 BroadcastLoadoutChange`. EQUAL.

### SIM-0675 — `FedSrvSiteBase::HitTreasureEvent` (FedSrv.CPP:13558-13645) — IMPLEMENTED

Branches: `c_tcFlag` refusal (already carrying / own flag) (:13562-13568); `GAIN_FLAG` whole mission +
`SetTreasureID(NA)` (:13570-13578); part → nothing (the offer handles it); cash → player: autodonate target
else self + side-wide `MONEY_CHANGE`, NPC: `GiveSideMoney` (:13589-13616); other codes →
`ship->HitTreasure` (:13619); player tail: buffer the part offer only when the buffer is empty, then
`ACQUIRE_TREASURE` to the picker's own connection (:13622-13641); always true.
Port: `ServerHost.cs:1425 HitTreasureEvent` (flag gates) → `Server.Docking.cs:1020 PerformTreasurePickup`
(all four code branches + `ACQUIRE_TREASURE` + `DESTROY_TREASURE`), trigger gate in
`Sector.ResolveTreasurePickups`. DIFF → **fixed**: the NPC cash arm now includes the immediate `DoPayday`;
and the pickup pass now enforces the native `c_ptPlayer` pilot-type gate (`treasureIGC.h:86`) instead of
the stale "always true" claim. n/a: the `c_tcDevelopment` arm of `HitTreasure` — PCore001's treasure sets
carry no Development entries (`DataTreasureSetIGC.cs` corpus note), so no such treasure can exist; an
unrecognised code stays in place rather than being credited as cash.

### SIM-0676 — `FedSrvSiteBase::PaydayEvent` (FedSrv.CPP:13647-13650) — IMPLEMENTED

Native: `m_pfsMission->GiveSideMoney(pside, Money(money))`. Port: `ServerHost.cs:300` → `GiveSideMoney` →
`OnPayday` (player count, `delta = money/np`, `PAYDAY(114)` to the side group, `money -= delta*np`,
per-player credit into the autodonate target else self). EQUAL, and now literally one shared body.

### SIM-0677 — `FedSrvSiteBase::SendChat` (FedSrv.CPP:13652-13692) — IMPLEMENTED

Native body: build the `CHATMESSAGE` allocation from `sidSender/chatTarget/oidRecipient/commandID/
voiceOver/bObjectModel/otTarget/oidTarget`, then `ForwardChatMessage(...)` and free it. Port:
`ServerHost.cs:581 SendChat` builds the same `ChatMessage` (including `bObjectModel`, `otTarget`,
`oidTarget`), drops `CHAT_ADMIN` (NYI in native itself, FedSrv.CPP:3473-3477) and raises `ChatRequested` to
`Server.Callbacks.cs SendSiteChat`. EQUAL — the body carries no routing policy; the target-selection rules
are `ForwardChatMessage` (SIM-0774, another lane).

### SIM-0678 — `FedSrvSiteBase::ClusterUpdateEvent` (FedSrv.CPP:13694-13697) — IMPLEMENTED

Native body is **empty** — there is no rule to port. The port's `ServerHost.cs:317 ClusterUpdateEvent`
raises a hook because the port's construction/export split needs a per-sector-tick completion signal; that
hook performs SIM-0680's broadcast half, not a hidden ClusterUpdate rule.

### SIM-0679 — `FedSrvSiteBase::CommandChangedEvent` (FedSrv.CPP:13699-13732) — IMPLEMENTED

Branches: `c_cmdAccepted` always; `c_cmdCurrent` only when pilot type ≥ player ∧ in a cluster ∧
`IsUsingAreaOfEffectWeapon()`; `OT_invalid`/NA for a NULL target; audience side group (guaranteed) vs
sector-flying (NOT guaranteed). Port: `ServerHost.cs:1759` → `Server.Combat.cs:2714 OnCommandChanged`.
EQUAL.

### SIM-0680 — `FedSrvSiteBase::CreateBuildingEffect` (FedSrv.CPP:13733-13767) — IMPLEMENTED

Branches: create the effect envelope (start behind the builder, asteroid/builder radii, target position);
`GetSeenSides(builder, …, asteroid)` plus `GetAsteroidCurrentEye` plus the builder's own side decide who
gets it; per informed side `SetBuilderSeenSide` + `ExportObj(OT_buildingEffect)` + guaranteed send.
Port: `ServerHost.cs:322 CreateBuildingEffect` → `Sector.cs:560 StartBuildingEffect` (envelope + the same
informed-side set) → `Server.Docking.cs:1318 BroadcastBuildingEffectCreated`. EQUAL.

### SIM-0681 — `FedSrvSiteBase::LayExpendable` (FedSrv.CPP:13769-13840) — IMPLEMENTED

Branches: mine type → `DataMineIGC` at the layer's position with the side as launcher; probe type →
`DataProbeIGC` likewise with no target; parts terminated first (no treasure), `SetAmmo(0)`,
`SetFuel(0)`, `KillShipEvent(now, layer, NULL, 0, pos, zero)`.
Port: `Ship.cs:4765 PerformLayerLay` (mine/probe branch, `ClearLoadout` before the kill, `Ammo/Fuel = 0`,
`KillShipEvent(launcher null, 0)`). EQUAL, with one documented design substitution: the created
Mine/Probe keeps the layer ship as its `Launcher` because that reference is the port's route to the
mine/probe broadcast seam (native sets `exportF = false` and relies on sector sync).

### SIM-0682 — `FedSrvSiteBase::RespawnFlag` (FedSrv.CPP:13842-13944) — IMPLEMENTED

Branches: `sidFlag == NA` no-op; `SIDE_TEAMLOBBY` → `GenerateTreasure(now, randomNeutralCluster, -2)`
(:13847-13868); real side → pedestal stations of the flag's own side with a 300-unit "spitting distance"
existing-flag exclusion (:13873-13905), no station → `GameOver` for the capturer when supplied
(:13906-13920), else spawn the flag treasure above the pedestal (z + radius + `c_fFlagOffset`, 10-day
lifespan, createNow false, :13921-13941).
Port: `Server.Combat.cs:2230 RespawnCarriedFlag` (+ `Mission.RespawnArtifact`). EQUAL.

### SIM-0771 — `LeaveShip` (FedSrv.CPP:2277-2320) — IMPLEMENTED

Branches: only when the carrier has no cluster (:2285); `SetParentShip(NULL)`; restore the side civ's
lifepod hull (:2301-2302); ship status → docked/parent NA/lifepod hull, state time; `LEAVE_SHIP` to the
side group (:2312-2316).
Port: `Server.Docking.cs:227 PerformLeave` (docked-carrier gate, parent/turret clear, lifepod hull,
`LEAVE_SHIP(94)` side-wide). The ship-status fields are derived per tick by `MakeOwnShipStatus` — the
port's standing status contract rather than a write here. EQUAL.

### SIM-0772 — `BoardShip` (FedSrv.CPP:2322-2477) — IMPLEMENTED

Branches: self-exclusion; carrier is a player; both docked (or an existing child moving within the same
carrier); turret walk over `[MaxFixedWeapons, MaxWeapons)` requiring a FREE and EQUIPPED slot (Turkey
#145), observer fallback; `(turret != NA) || CanCheat()`; detach the boarder's children; hardware refund +
side-wide `MONEY_CHANGE`; `SetParentShip`/`SetTurretID`; ship-status state/parent/hull; station inherit;
`QueueLoadoutChange` on the carrier; sector vs side-group flush; true with no ack.
Port: `Server.Docking.cs:94 PerformBoard`. EQUAL (the `CanCheat` observer gate collapses to always-true —
the port has no cheat concept, so observers are allowed, the established precedent).

### SIM-0775 — `CreateTreasure(DataTreasureIGC*)` builder (FedSrv.CPP:4324-4355) — IMPLEMENTED

Branches: `GenerateNewTreasureID`; `p0 = position + (shipRadius + 10)·randomDirection`;
`v0 = direction·dv + ship velocity`; cluster id; time0; `CreateObject(OT_treasure)`; release.
Port: the shared shape in `Ship.cs:4289 CreateDropTreasure(type, amount, …)` and `:4316
CreateFlagTreasure`. EQUAL (assert-only branches are not behaviour; the pointer-taking overload becomes
typed overloads).

### SIM-0776 — `CreateTreasure(IpartIGC*, IpartTypeIGC*)` (FedSrv.CPP:4374-4396) — IMPLEMENTED (fixed)

Branches: `OT_magazine/OT_dispenser/OT_pack` → `amount = GetAmount()`, and **return NULL when 0**;
`default` → `amount = 0`; then the amount overload.
Port: `Ship.cs:4244 CreateDropTreasure(MountedPart, …)` now applies exactly that switch, and the
`HandleDropPart` gate mirrors `IlauncherIGC::IsLauncher` = magazine|dispenser only, so a dropped chaff
launcher still drops (amount 0) while an empty mine/dispenser launcher does not.

### SIM-0777 / SIM-0778 — `DisplaceProbe` (FedSrv.CPP:4668-4682 / 4684-4710) — IMPLEMENTED

Per-model predicate: `dxy = position − model.Position`, `rOffset = model.Radius + r`, displace only when
`l2 < rOffset²` ∧ `l2 != 0`, new position `center + dxy·(rOffset/√l2)`. Cluster sweep: stations first,
then warps, stopping at the first hit; the asteroid loop is commented out in native itself.
Port: `Ship.Equipment.cs FireDispenser`'s probe branch (local `Displace`, stations then warps, no
asteroids, zero-separation no-op). EQUAL.

### SIM-0780 — `RefillPart` (FedSrv.CPP:12589-12621) — PARTIAL

Branches: base part's required techs ⊆ side techs; walk to the furthest successor whose techs are ⊆;
`newAmount = price == 0 ? SHRT_MAX : GetAmount()`; same type → `SetAmount`, else `Terminate` +
`CreateAndAddPart(successor, oldMount, newAmount)`.
Port: `Server.Docking.cs:1745 RestockOnDock` reproduces the tech gate, the successor walk, the
free-part amount reset and replace-in-place. **DIFF (1):** the port gates the successor upgrade on the
player's wallet (value differential) and keeps the base part when unaffordable, where native upgrades
unconditionally and never charges — native assumes the player already owns the tech. Also `RefillPart` is
applied at station dock only, so a carrier landing does not run it (SIM-0663).

### SIM-0781 — `ErasePart` (FedSrv.CPP:12637-12653) — IMPLEMENTED (fixed)

Branches: look up the mounted part, then consume the first desired-list entry that `MatchPT` accepts
(same type or any successor).
Port: `Server.Docking.cs:1702 DesiredPartPresent` (successor-chain match) consumed by `RestockOnDock`'s
desired-loadout re-buy. Previously an exact `PartId` comparison, so an upgraded part left the base desired
entry "missing" and a duplicate base part was mounted at the next dock.

### SIM-0838 — `CreateTreasure(amount, IpartTypeIGC*)` (FedSrv.CPP:4357-4371) — IMPLEMENTED

Branches: `treasureCode = c_tcPart`, `treasureID = ppt->GetObjectID()`, `lifespan` (default 600),
`amount`, `createNow = false` → shared builder.
Port: `Ship.cs:4289 CreateDropTreasure(type, amount, now, time0, dv, lifespan)`; every caller passes the
native dv/lifespan pair (DROP_PART 25/120, treasure-ack decline 25/120, kill drops 100/60 and 100/30).
EQUAL.

---

## Family score

| metric | before | after |
|---|---|---|
| weighted (credit/weight) | 25.93 % (153/590) | **34.58 % (204/590)** |
| unweighted (complete/eligible) | 25.00 % (63/252) | **34.92 % (88/252)** |

This batch: 25 rows `implemented` (weight 51), 5 rows `partial` (weight 11: SIM-0662, SIM-0663,
SIM-0666, SIM-0667, SIM-0780). `reachability` stays `production` for all 30 rows (every one is reachable
from a real client/drone path on a configured server; the partial rows' missing branches are named above).
`python3 scripts/parity-scorecard.py` validates (843 sim-rules rows); `tools/p832a-mission-rules-audit.py
--native-root ~/Development/Allegiance/src --check` validates (351 bodies); `tools/check-glossary-links.py`
OK.

## What changed

Production code (8 files, all in `src/`):
- `src/Allegiance.Server/Server.Economy.cs` — new `GiveSideMoney` (fsmission.cpp:1777-1787: `if (money !=
  0)` accrue + immediate `DoPayday`) and the donation-retarget stage gate in `RetargetDonorsOffLeaver`
  (fsmission.cpp:1103).
- `src/Allegiance.Server/Server.Docking.cs` — NPC cash pickup now calls `GiveSideMoney` (immediate payday);
  new `DesiredPartPresent` (`MatchPT` successor-chain match, FedSrv.CPP:12617-12653) feeding the dock
  restock's desired-loadout re-buy.
- `src/Allegiance.Server/Server.Lifecycle.cs` — departure payday via `GiveSideMoney`; `RePointDonations…`
  stage-gated and clearing donors of a departing non-leader (`pfsNewLeader == NULL`).
- `src/Allegiance.Server/Server.Lobby.cs` — team-lobby quit: payday moved after the side change and paid
  out immediately; old-player cache lookup is a single case-insensitive take.
- `src/Allegiance.Server/Server.cs` — `_oldPlayerScores` keyed with a case-insensitive name comparer
  (native `_stricmp`, fsmission.h:390-398).
- `src/Allegiance.Server/Server.ShipCommands.cs` — DROP_PART gate honours `IlauncherIGC::IsLauncher`
  (magazine|dispenser) and the NULL-treasure case.
- `src/Allegiance.Core/Sim/Ship.cs` — `CreateDropTreasure(MountedPart, …)` applies native's amount switch
  and returns `null` for an empty consumable.
- `src/Allegiance.Core/Model/Sector.cs` — the treasure pickup pass enforces the native `c_ptPlayer`
  pilot-type gate (`treasureIGC.h:86`).

Tests:
- `tests/Allegiance.Core.Tests/P847MissionRulesCertifyTests.cs` (new, 10 tests — 7 fail-first verified).
- `tests/Allegiance.ClientCore.Tests/TreasureDropLiveTests.cs` and
  `tests/Allegiance.Core.Tests/DropPartTests.cs` — the dropped-weapon treasure amount assertions updated
  from the old spliced part quantity (20) to native's `amount = 0` (FedSrv.CPP:4392).
- `docs/TEST_FLAKES.md` — P847 observation (pre-existing `P784ComposeTests` failure + the two updated
  expectations).

CSV: 30 rows updated in `docs/parity/sim-rules-matrix.csv` (only those lines; LF preserved,
`grep -c $'\r'` = 0).

## Light gate

- Whole-solution build (`~/.dotnet/dotnet build AllegianceCore.slnx`): **0 warnings / 0 errors**
  (log: `~/allegiance-evidence/P847/build.log`).
- `bash tools/build-tools.sh`: **all 91 tool projects built**, 0 warnings / 0 errors
  (`~/allegiance-evidence/P847/build-tools.log`).
- AOT publish of `src/Allegiance.Server` (Release, linux-x64, self-contained, `PublishAot=true`): **clean,
  no IL2026/IL3050** (`~/allegiance-evidence/P847/aot-publish.log`) — required because both
  `Allegiance.Server` and `Allegiance.Core` changed.
- New tests 3× alone: `P847MissionRulesCertifyTests` **10/10 ×3** (0.7 s each);
  `TreasureDropLiveTests` 1/1 ×3; `DropPartTests` 3/3 ×3.
- Fail-first proof: with every `src/` fix reverted (patch saved, `git checkout -- src/`, rebuilt), **7 of the
  9 branch tests fail** (`Npc_cash_pickup…`, `Lobby_time_leader_exit…`, `Started_non_leader_exit…`,
  `Side_leave_pays…`, `Dropped_weapon_treasure…`, `Dropped_empty_launcher…`,
  `Old_player_cache_keeps_one_entry…`, plus `Dock_restock_does_not_re_buy…` on its targeted revert); the two
  that pass in both states are the main-contract pins (`Started_leader_exit_repoints…`,
  `Rejoining_player_under_a_different_case…`). The fixes were then re-applied and everything re-ran green.
- Affected existing suites re-run after the fixes: `P841MissionRulesCertifyTests`, `PaydayTests`,
  `PlayerMoneyAutoDonateTests`, `LiveMoneyTests`, `LiveTeamLeadershipTests`, `DockAutoRestockLiveTests`,
  `P551DockOreSaleTests`, `CargoBayReloadTests`, `LiveTreasurePickupTests`, `LiveGainFlagTests`,
  `KillShipLootDropTests`, `ClientWalletLiveTests`, `P641FlightPartsLiveTests` — **120 tests, all green**.
- ONE `tools/run-tests.sh --tier fast`: **11 082 tests in 307.7 s**, two failures
  (`~/allegiance-evidence/P847/fast-tier.log`):
  1. `Allegiance.Core.Tests.DropPartTests.DropPart_drops_weapon_sectorwide_with_treasure_then_empty_pack_skips`
     — the second test that pinned the old spliced treasure amount (expected 20, native is 0). Updated to
     `amount = 0` (FedSrv.CPP:4392) and re-run **3/3 alone, green**. This was the only failure caused by
     this batch.
  2. `Allegiance.ClientCore.MonoGame.Tests.P784ComposeTests.Fanout_issues_one_order_message_per_recipient_on_the_wire`
     — **pre-existing on master**: reverting every P847 `src/` change still fails the identical assertion
     (mate ship `8100` missing from the wing-0 recipients) **3/3 alone**. No fanout/order-compose code is
     touched by this batch. Ledgered in `docs/TEST_FLAKES.md` (2026-10-27) as needing an owner.
- Showcase never-parks 5× (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`,
  the run that matters because the loot-pickup gate and payday distribution changed bot/sim behaviour):
  **5/5 green** (4 m 34 s, 4 m 31 s, 5 m 56 s, 7 m 12 s, 5 m 23 s).
- `python3 scripts/parity-scorecard.py`: validates (843 sim-rules rows).
  `tools/p832a-mission-rules-audit.py --native-root ~/Development/Allegiance/src --check`: validates (351
  bodies). `tools/check-glossary-links.py`: OK (212 paths).

## Native-intent classification & modern/better-approach note

- **Design** (deliberate port architecture, not a gap): the site seam split (sector detects / host acts),
  per-tick `SHIP_STATUS` as the docked-state carrier instead of a dedicated write in `LeaveShip`/
  `DockWithStationEvent`, typed overloads instead of the pointer-taking `CreateTreasure` builder, and the
  layer ship as the Mine/Probe launcher reference (the port's route to the broadcast seam, which native
  avoids by not exporting laid mines at all).
- **Era constraint (n/a):** Steam achievements in `RescueShipEvent`/`ChangeStation`; `CHAT_ADMIN` (NYI in
  native); rigid-body `RecalculateCollisions`; the aleph spin; the `c_tcDevelopment` treasure arm (absent
  from the PCore001 corpus).
- **Native bugs kept:** `BoardShip`'s `assert`-only turret invariants (the port validates instead, which is
  strictly safer); native's crash-prone `IshipIGC*` cast of a station turret launcher in the allied
  station alert (already documented by P846); the `GameOver("… no stations left")` in `RespawnFlag` is
  kept verbatim, including firing only when a capturer is supplied.
- **Not kept (documented, tested):** nothing silent — every divergence above is either fixed with a test,
  or named as the exact remaining branch of a `partial` row.
- **Modern/better approach:** the money path now has ONE `GiveSideMoney` body shared by every native
  caller, so "accrue then pay out" cannot drift between the payday, the NPC pickup, the departure and the
  team-lobby quit; the old-player cache uses a semantic key comparer rather than a linear `_stricmp` scan
  (same contract, O(1)); and the treasure amount/type rules live in one place in the sim instead of being
  re-derived at each DROP_PART caller.
