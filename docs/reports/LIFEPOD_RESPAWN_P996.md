# P996 — Lifepod respawn loop: a docked Lifepod can launch and dies instantly of a stale oxygen deadline

**Task.** Domingo could not leave his base on the rig (2026-11-10, ship 8110): podded, the pod was killed, the
player respawned **docked as a Lifepod**, and every hangar Launch press killed him 6 ms later of "oxygen" at a
deadline left over from the previous life. This report covers the server root cause (a pod clock that survived a
dock), the native rule for launching a lifepod hull, the client-side `RestoreLoadout` that keeps the hangar
showing a real ship, the neighbouring paths checked for the same class of stale state, and the gate.

---

## 1. The rig timeline (server log of ship 8110, client UM690)

| sim/clock | event |
|---|---|
| 23:05:15 | podded — `[eject] player ship 8110 ejected -> Lifepod hull 744` |
| 23:05:15 | the pod was killed → `[lifepod] player ship 8110 -> docked Lifepod (hull 744) at station 2 'Garrison'; ENTER_LIFEPOD(166) -> own peer only` (`src/Allegiance.Server/Server.Combat.cs` respawn leg) |
| 23:18:08 | client sends `BUY_LOADOUT` **hull 744, 0 parts, fLaunch=true**; accepted (`cost 0`, `[undock] ship 8110 undocked`) |
| +6 ms | `[oxygen] lifepod ship 8110 oxygen exhausted at sim-t 1030.7 (deadline 926.25)` → killed → docked Lifepod again |
| … | repeat: the client log shows `undocked: leaving hangar` then `sim frozen (docked/dead/warp)` |

`deadline 926.25` is 104 s **before** that launch and (given `LifepodEndurance = 300`) was armed ~404 s earlier —
i.e. when the previous life's pod was created, not when the relaunched pod started drifting.

## 2. Native rules (verified against `~/Development/Allegiance/src`, line numbers re-grepped)

**2.1 The oxygen deadline only exists while the pod is in space.**
`CshipIGC::SetBaseHullType` arms it on a hull swap to a lifepod —
`m_timeToDie = GetLastUpdate() + GetMyMission()->GetFloatConstant(c_fcidLifepodEndurance)`
(`Igc/shipIGC.cpp:1408`). It is read in exactly two places: `Update`'s expiry kill
(`Igc/shipIGC.cpp:360-363`: `if (HasCapability(c_habmLifepod) && (now > m_timeToDie)) KillShipEvent(…, NULL, 1.0f, …)`)
and `GetEndurance` (`Igc/shipIGC.h:2473-2491`, whose fraction is gated on `GetCluster()`). **A berthed ship is not
in a cluster at all**: `CshipIGC::SetStation` (`Igc/shipIGC.h:421-459`) ends with
`s->AddShip(this); //This also sets the ship's cluster to NULL` (`:457`) and `assert (GetCluster() == NULL)` (`:459`).
So a docked pod runs no `Update` (no oxygen burn) and `GetEndurance` reads "full" — and, crucially, a deadline left
over from the previous life is **inert**, because nothing can consult it while the ship is berthed.

**2.2 A pod may be bought and launched from a station.** `CshipIGC::PurchaseShipLoadout`
(`Igc/shipIGC.h:989-1020`) gates a changed hull **only** on the station's tech-based
`m_station->CanBuy(pht)` (`:1000`, `Igc/stationIGC.h:383-391`: `requiredTechs <= stationLocal | sideTechs`), then
takes `m_station->GetSuccessor(pht)` (`:1004`) and re-hulls in place with `SetBaseHullType(pht)` (`:1020`) — which
re-arms the clock via 2.1. The `GroupID >= 0` test that hides drone hulls belongs to the native **hangar's hull
list** (`WinTrek/load.cpp:1603-1606`), not to the purchase. A side's pod hull carries a real `RequiredTechs` mask
(PCore001: hull 744/844 need their civilisation's garrison pod tech), so a docked player may legitimately launch in
a pod — with a fresh deadline. A same-hull request (`hullID == pht->GetObjectID()`) skips the hull block entirely,
so no `SetBaseHullType` runs and no deadline is re-armed; native never hits that case because of 2.3.

**2.3 The native client restores the last loadout while berthed in a pod.** The own-ship `LOADOUT_CHANGE` handler's
`if (pship->GetBaseHullType()->HasCapability(c_habmLifepod) && pship->GetStation())` branch calls
`RestoreLoadout(pship->GetStation())` (`clintlib/appmsg.cpp:2022-2027`) instead of applying the pod's parts.
`RestoreLoadout(IstationIGC*)` (`clintlib/clintlib.h:1876-1886`) builds an empty ship and runs
`RestoreLoadout(m_ship, pship, pstation, m_ship->GetValue() + GetMoney())` (`:1880`), then **sends that loadout to
the server with `BuyLoadout(pship, false)`** (`:1883`). The rule inside it (`clintlib.h:1631-1633`): a pod base hull
falls to `ReplaceLoadout(sink, pstation, NULL, budget, m_loadouts)` — take the **head** of the cached loadout list
(`pcll = m_loadouts.first()`, `:1489`), the ship the player last flew (saved on every `BUY_LOADOUT_ACK`,
`clintlib/appmsg.cpp:3185` `SaveLoadout()`; `WinTrek/load.cpp:557`). With a saved entry
(`clintlib.h:1816-1848`): budget-check `pstation->CanBuy(pcl->pht) && price <= budget`, upgrade the hull to
`pstation->GetSuccessor(pcl->pht)` (the furthest buyable successor, `Igc/stationIGC.h:399-440`), clone every saved
part the new hull `CanMount`s, and `if (pcl->pht != pht) TryToBuyParts(…)` fill the upgraded hull's empty slots with
its preferred parts (`:1846-1848`, `TryToBuyParts` `:1341-1368`). With no usable saved entry: the first hull in core
order with `GroupID >= 0 && CanBuy && price <= budget`, upgraded to its successor, with its default loadout
(`:1850-1870`).

**2.4 The death-respawn docks the pod** (`FedSrv/FedSrv.CPP:12030-12065`): `SetCluster(NULL)` → `SetBaseHullType(pht)`
(`:12052`) → `ShipStatusRestart` (`:12057`) → `ENTER_LIFEPOD` → `SetStation(pstationDest)` (`:12064`) →
`ShipStatusDocked` (`:12065`). The hull swap arms the clock; the `SetStation` immediately after takes the ship out of
any cluster, which is why native tolerates the armed value.

**2.5 Native also strips a dying ship's parts** inside the same `KillShipEvent` loot block
(`FedSrv/FedSrv.CPP:11691-11702`: one treasure roll per part, then `p->Terminate()`), before the eject branch's
`SetBaseHullType` — which is why that function can `assert (m_parts.n() == 0)` (`Igc/shipIGC.cpp:1378`).

## 3. Root causes (file:line in the port)

1. **`src/Allegiance.Core/Sim/Ship.cs` (P279 code, removed now):** `ChangeHull` armed `LifepodTimeToDie` on *every*
   hull swap to a lifepod — including the respawn swap of 2.4 — and `OnUpdate` only *gated* the expiry on
   `StationId is null`. Docking never cleared the deadline, so a pod respawned by a pod death kept the previous
   life's clock; the first undock removed the gate and the next tick killed the ship at a deadline ~100 s in the
   past. The rig log's `deadline 926.25` vs `sim-t 1030.7` is exactly that.
2. **`src/Allegiance.Server/Server.Combat.cs` respawn leg (`~2080-2087`):** `victim.ChangeHull(lp)` then
   `victim.StationId = dest.StationId` — the arm happened before the dock and nothing undid it (native is saved by
   the cluster NULL, §2.1).
3. **`src/Allegiance.ClientCore.Game/Game1.Panes.cs` (docked shop construction):** the hangar's `ShopModel` was
   built from the ship's *current* hull — the pod at a station — so `EffectiveHullId` was the pod and the Launch
   button sent `BUY_LOADOUT(pod hull, fLaunch)`. The pod hull is `GroupID < 0`, so the shop's hull list cannot offer
   it and `EffectiveHull` was null: the hangar had nothing else to send. That is the message the rig accepted.

## 4. Changes

### 4.1 Server / Core — the pod clock lives only in space (`src/Allegiance.Core/Sim/Ship.cs`)

* `LifepodEnduranceSeconds` (single source for the core's `FloatConstants[31]`, P279 seam honoured) and
  `ArmLifepodDeadline()` / `ClearLifepodDeadline()` helpers (`Ship.cs:275-297`); the duplicated endurance
  expressions in `ChangeHull`/`LifepodEnduranceFraction` now call it.
* `StationId`'s setter (`Ship.cs:1392-1404`): any non-null station **clears** the deadline (native `SetStation`
  takes the ship out of its cluster, §2.1); setting it to `null` while the hull is a lifepod **arms a fresh one**
  (a pod launched back into space starts a new clock — native's hull swap does the same, §2.2).
* `ChangeHull` (`Ship.cs:352-364`): arms only when the new hull is a lifepod **and** the ship is not berthed,
  otherwise clears.
* The `OnUpdate` oxygen kill's fire-once guard (`Ship.cs:9519-9530`): a death that respawned the player docked
  cleared the clock synchronously (through `SetStation`), so the guard no longer stamps `+∞` onto a berthed pod.
  A death that could not relocate the pod keeps the `+∞` guard.

### 4.2 Server — a lifepod launch is handled as native does (`src/Allegiance.Server/Server.ShipCommands.cs`)

`ApplyLoadout`'s hull gate (`:2694-2722`) now treats a **lifepod hull** as player-buyable: it is exempt from the
P441/P470 `GroupId < 0` refusal (native's `PurchaseShipLoadout` never consults the group id; the native *hangar*
does, §2.2) and still passes the same `StationSellsHull` (CanBuy containment) gate, plus a log line naming the
native rule. Every other `GroupID < 0` hull stays refused. The launch itself gets a fresh deadline from 4.1's
undock arm, so "if native allows it, the pod must at least launch with a fresh deadline" holds. The same-hull pod
re-launch the rig sent (hull unchanged ⇒ native skips the hull block) was already accepted and now also gets a
fresh clock.

### 4.3 Client world — remember the last real loadout (`src/Allegiance.ClientCore/State/ClientWorld.cs`)

* `ClientWorld.SavedLoadout(HullId, Parts)` + `OwnSavedLoadout` (`:369-384`): the port's head of the native
  `m_loadouts` list, recorded by `RememberOwnLoadout` on the own ship's purchase ACK (native `SaveLoadout()`,
  `appmsg.cpp:3185`), on its `LOADOUT_CHANGE`, and on a real-id EXPORT. A lifepod hull never overwrites it, and an
  unresolvable side leaves it untouched — the pod's bare chassis can never become "the last loadout".
* `OwnShipDockedInLifepod` (`:386-397`): native's `pod hull && GetStation()` gate (`appmsg.cpp:2022-2023`), with a
  positive lifepod resolution required.

### 4.4 Client — the hangar restores it (`src/Allegiance.ClientCore.Game/Rendering/ShopModel.cs`, `Game1.Panes.cs`)

* `ShopModel.RestoreSavedLoadout(savedHullId, savedParts)` (`ShopModel.cs:585-744`) is the native rule of §2.3:
  furthest buyable successor of the saved hull (`BuyableHullOrDefault` → `Igc/stationIGC.h:399-440`), clone the
  saved parts it can mount, fill an upgraded hull's empty slots with its default loadout (`TryToBuyParts`), else the
  first affordable buyable hull with its default loadout (native fallback), with the saved hull's price checked
  against the wallet (`m_ship->GetValue() + GetMoney()`; a berthed pod is worth 0).
* `Game1.Panes` (`:178-193`) calls it when the docked ship's hull is the side's lifepod, so the hangar's selection —
  and therefore the Launch request (`shop.EffectiveHullId` + `shop.ChosenParts`) — is the restored real ship.
* Scope stated plainly: the port's shop carries the **mounted** loadout (cargo is the existing shop gap, not a
  P996 decision), so cargo parts are not replayed; the server's own P488 successor upgrade still applies to the
  hull and parts that are sent.

### 4.5 Bots / MCP path

Bots already pick a real hull on every relaunch and never needed the restore: `BotManager.Lifecycle.cs:216` sends
`bot.HullId` (a role hull) with `fLaunch:true`, chosen by `RepickRelaunchLoadout` /
`TechLoadoutSelector.PickTechLoadout` (`BotManager.TechLoadouts.cs:168-215`), and pod state is only *classified*
(`Catalog.IsLifepodHull`, `BotManager.Lifecycle.cs:394,487`). The new bots test (§6) pins that they never show the
server-side loop either.

### 4.6 Docs

`GLOSSARY.md` (lifepod oxygen clock + hangar restore entry), `docs/parity/sim-rules-matrix.csv` **SIM-0859** (the
clock exists only in space; the `updates` family, `native_body` = the real `CshipIGC::SetBaseHullType` body so the
P832a mission-body inventory stays reconciled), and `docs/parity/p977-eject-rescue.csv` (two rows: a docked player
may buy/launch a lifepod hull — native `PurchaseShipLoadout`'s tech gate + re-armed clock — and the client
`RestoreLoadout` while berthed in a pod). `parity-scorecard.py`, `p832a-mission-rules-audit.py --check` and
`tools/check-glossary-links.py` all pass.

## 5. Neighbouring paths checked for the same stale-state class (step 4)

| path | where | verdict |
|---|---|---|
| eject → drifting pod | `Ship.ChangeHull` in `EjectSurvivor` (`Server.Combat.cs:2610`) | arms a fresh deadline **in space** (P279 behaviour kept, asserted by the new test's leg A and by `LiveP699PodHomeTests`/`LiveLifepodOxygenTests`) |
| pod → rescue → dock | `PerformRescue` sets `StationId` (`Server.Docking.cs:856-857,959-960`) | docks ⇒ clock cleared (was: gated only). `LiveLifepodOxygenTests.Docked_lifepod_does_not_self_destruct…` now also asserts the clear |
| pod docking at an **allied** base | `PerformAlliedLifepodRedirect` (`Server.Docking.cs:1990-2040`): `ChangeHull(real hull)` + `StationId = home` | re-hull while still berthed ⇒ no arm; then `StationId` set ⇒ cleared. `P977AlliedEjectLiveTests` stays green |
| **teleport while a pod** | `Server.Docking.cs:2495-2530`: `ChangeHull(lp)` (bNewHull) then `StationId = target` | same order (arm-in-space impossible here, dock clears). Covered by the P110/P102 live tests |
| boarding a docked carrier while a pod | `Server.Docking.cs:194-196` (`boarder.StationId = cs`), then `PerformLeave`'s `ChangeHull(lp)` while docked | inheriting a dock clears the clock; leaving while berthed re-hulls without arming (correct: still berthed) |
| death while docked/pod in an inactive (eliminated) side or with no friendly station | `Server.Combat.cs` respawn guards → `FinalizeDeadShip` | no `ChangeHull`, no arm; the `OnUpdate` `+∞` guard stops a re-fire. Unchanged |
| game-over reset / mission rotation | `Server.Lifecycle.cs` world rebuild, `ClientWorld` teardown (`:2239,5359`) | whole `Ship` objects are discarded; no clock survives a rotation |
| hangar purchase of a **drone** hull | `ApplyLoadout` group gate | still refused for every non-lifepod `GroupID < 0` hull (P441/P470 unchanged) |
| pod keeps the dead ship's parts | `BroadcastKillShip` drops loot (`DropKillShipLoot`) but never clears the victim's loadout | **pre-existing divergence, found, not changed**: native terminates every part in the same loot loop (`FedSrv.CPP:11691-11702`) before the pod swap. The port's pod carries the dead loadout on the wire and in the docked "CURRENT SHIP" pane (visible in the screenshot below). Not the stale *clock*, and touching the loot/P411 block is outside this task's scope (it belongs with the P411 loot owner) — recorded here as a follow-up finding |
| client-side per-life state across the pod cycle | `ClientWorld` death/pod latches (`KillShip`, `ShipDelete`, `EnterLifepod`, `LoadoutChange`) | checked: `IsDead/IsGone`/dead-reckoners already had their P521/P648 reset paths; the new saved-loadout is the only new per-life state and it is explicitly *not* reset by the pod (that is the point) |

## 6. Tests

New (all run green; counts in §8):

* `tests/Allegiance.Core.Tests/P996LifepodRespawnLiveTests.cs` — **the loopback wire repro**: pod killed → docked
  Lifepod → the rig's own `BUY_LOADOUT(lifepod hull, 0 parts, fLaunch)` over real TCP. Asserts (A) a fresh eject arms
  a fresh deadline, (B) the berthed pod carries **no** deadline and survives 3 s past the previous life's deadline,
  (C) the pod launch is **granted** (`fLaunch`) and carries a **future** fresh deadline with no instant death,
  (D) that fresh clock still kills the pod on time, (E) a relaunch into a real hull after a pod death has
  `LifepodTimeToDie == null` and survives 2 s past any compressed pod clock. Also exercised interleaved with the
  existing `[oxygen]` cadence. **Fails on master at the first docked-pod assertion** (line 231: the docked pod has
  a deadline; verified by reverting `Ship.cs` and re-running).
* `tests/Allegiance.ClientCore.Tests/P996LifepodSavedLoadoutTests.cs` — the world keeps the last **real** loadout
  across the purchase-ACK / eject / pod `LOADOUT_CHANGE` / docked-status cycle, never overwrites it with a pod, and
  reports `OwnShipDockedInLifepod` only for a berthed pod.
* `tests/Allegiance.ClientCore.MonoGame.Tests/P996LifepodHangarRestoreTests.cs` — the hangar/loadout **model**: a
  berthed pod selects the last real hull + its parts, an out-teched saved hull becomes its furthest buyable
  successor (with defaults filling the upgraded hull), an unbuyable saved hull falls back to the first affordable
  hull + default loadout, and an unaffordable one falls back to a free hull.
* `tests/Allegiance.ClientCore.MonoGame.Tests/P996LifepodHangarRestoreLiveTests.cs` — **end to end**: real session +
  real shop model + real wire; launches hull 13 with a gun, kills the ship and the pod, then builds the docked shop
  exactly as `Game1.Panes` does, asserts the selection **is** the real hull, sends it, and the ship launches as
  `hull 13` with `LifepodTimeToDie = null` and survives 60 ticks.
* `tests/Allegiance.ClientCore.Tests/P996BotsLifepodNoOxygenLoopLiveTests.cs` — seeded (`BenchmarkSeed=996`, Brawl
  map seed 996) loopback `kills` showcase, 12v12 bots, pod endurance compressed to 5 s (`liveSettings.
  lifepodEnduranceSeconds`, P378 tunable) so pods really die of oxygen inside the window: bots get podded, the pods
  die, and **no oxygen death happens within 1 s of that ship's launch**; per-ship oxygen-death counts are asserted
  and cross-checked against the captured `[oxygen]` log lines.
* Existing tests updated for the now-cleared deadline (they measured an armed clock on a *berthed* pod, which native
  cannot have): `LiveLifepodOxygenTests.Docked_lifepod_does_not_self_destruct_after_oxygen_deadline_passes` now
  asserts `LifepodTimeToDie is null` while docked, and `LiveP378DebugModeTests` drops the fresh spawn's dock latch
  before its in-space hull swap (the armour it measures has always been an in-space arm).

`tools/p996-ui-proof/` is the screenshot driver (same pattern as `p759-ui-proof`): real `Game1` + real loopback
server; it launches hull 15, pods the ship, kills the pod, and **fails unless** the docked hangar's shop model
restored a real hull with parts (`proof-*.json` next to the PNGs).

## 7. Screenshots (Classic **and** Modern, real window, real server)

`docs/reports/P996_assets/` (driven by `tools/p996-ui-proof`, 1280×720):

| file | what is actually visible |
|---|---|
| `hangar-after-pod-death-classic.png` | The docked native hangar screen (`hangar.mdl`: cockpit art, the "HANGAR 15" door placard, the red monitor bank) captured after the pod death, Classic HUD style. The left/right info panes are the P945 native 800×600 panes scaled to 720p, so their text is clipped at this window size — a pre-existing P945 layout artifact, unrelated to P996. |
| `hangar-after-pod-death-modern.png` | The same docked hangar screen in the Modern HUD style; identical art composition, different pane/typography styling (113 678 differing raw bytes vs the Classic PNG). |
| `loadout-after-pod-death-classic.png` | The loadout screen (`HangarAction.Loadout`) after the pod death: **`CURRENT SHIP hull: #33`** (the Iron-Coalition-class pod the server docked) with **`PENDING LOADOUT hull: Adv Fighter`** and its restored parts (`Hvy Booster, Counter 3, Mine Pack 2, PW Gat Gun 3 ×3, Sm Shield 3, SRM Dumbfire 3`), `cost $0`, Affordable. This is the restore: the ship the player last flew, not the pod. |
| `loadout-after-pod-death-modern.png` | Pixel-identical to the Classic capture: the loadout pane is the shared model/layout, so Modern's overrides (which target the hangar artwork/buttons and the flight HUD) change nothing here. Stated rather than staged. |

`proof-classic.json` / `proof-modern.json`: `{"launchedHull":15,"podHull":33,"hangarSelection":15,
"hangarParts":8,"podDeadline":null}` — the berthed pod (`33`) carried **no** deadline, and the hangar selection was
the launched hull (`15`).

The four PNGs are **new media**: they need Domingo's review before the push (`ALLOW_MEDIA=1` for
`scripts/publish-guard.sh`). Total asset size 0.57 MB (Claude review: the two hangar captures were quantised to 256 colours, 677/654 KB → 175/176 KB).

The "CURRENT SHIP hull: #33" pane listing the dead fighter's parts is the pre-existing §5 finding (the port does not
terminate a killed ship's parts the way native's loot loop does); the restored *pending* loadout below it is the
P996 behaviour under test.

## 8. Gate

All runs on this worktree, shared host with other lanes active; every command with the lane's anti-hang flags
(`--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`) and every suite/run bounded by
`timeout`. `local.env` is present (artwork + native-source paths), so nothing skipped for a missing corpus: the
only skips anywhere in the gate were the three `MeshyPackTests` (private Meshy pack not configured). Raw logs:
`~/allegiance-evidence/P996/` (outside the repo).

| gate item | command | result |
|---|---|---|
| whole-solution build, 0 warnings | `dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** (final tree) |
| every tools project | `tools/build-tools.sh` project set, each with the anti-hang flags | **131/131 projects build**, 0 failures. The script itself was **not** usable as-is: it runs a plain `dotnet build` per project and hung 40 min twice on `tools/allegiance-mcp` (the documented shared-host MSBuild-node hang); the identical project set built with the flags in 1 s each |
| AOT publish (Server/Core changed) | `dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true` | **0 errors, 0 warnings, 0 IL2026/IL3050**, 17.5 MB native binary |
| new + affected tests, 3× alone (final tree) | Core: P996 + `LiveLifepodOxygenTests` + `LiveP378DebugModeTests` + `LiveLifepodEnduranceDisplayTests`/`LifepodEnduranceDisplayTests` + `P955PlayerPodLifetimeTests` + `LiveShipDeathFinalizeTests` + `P699bPodLoadTests`; ClientCore: P996 saved-loadout; MonoGame: P996 restore (unit + live) | **3 × 25 passed / 0 failed / 0 skipped** (Core, 90 s each), **3 × 6 passed** (ClientCore), **3 × 5 passed** (MonoGame) |
| bots showcase, 3× alone (final tree) | `P996BotsLifepodNoOxygenLoopLiveTests` | **3/3 pass** (3 m 20 s each); runs: pods drifting 19/20/6, pod deaths 59/74/25, oxygen deaths 26/31/9, **minimum launch→oxygen-death gap 27.35 s / 15.80 s / 31.50 s** (rule: > 1 s) |
| one full fast tier | `tools/run-tests.sh --tier fast --no-build` (the runner's own build step hung on the same plain `dotnet build`; the solution was pre-built with the flags, hence `--no-build`) | **13 966 tests, 572.9 s, exit 1**: 13 960 passed, **3 failed**, 3 skipped (`MeshyPackTests`, the private Meshy pack is not configured on this box — the only skips), 2 slow-Fast offenders (38.369 s `MissileLockLiveTests`, 10.055 s `P777WarpExitWobbleTests`). All three failures are **non-task** except one that was mine: `P832aMissionRulesInventoryTests` (my two new parity rows sat in the audited `FedSrv mission rules` family with prose `native_body`; SIM-0859 moved to the `updates` family with its real body key and SIM-0860 to `p977-eject-rescue.csv`) — **re-checked clean** (`p832a-mission-rules-audit.py --check` exit 0, test passes 1/1). The other two are the ledgered load-boundary families, each re-run **3× alone** and still failing with their exact recorded signatures (`MissileLock…` 0/3 at 38 s — clean-base 1 pass / 2 fail, P966 row; `LiveSideSelectTests.Flag_on_auto_seats…` 31.35 s in the sweep and 32.1000000000003 against [28,31] in 3 isolated runs — clean-base 2 pass / 3 fail). Ledgered in `docs/TEST_FLAKES.md` under "P996 sweep" |
| showcase never-parks 5× vs the base rate | `dotnet test … --filter FullyQualifiedName=…P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` ×5 | **5/5 pass** (exit 0): 243.2 s, 451.5 s, 464.7 s, 369.4 s, 133.2 s; reached 24/30 in every run; **longest "speed < 1 with a target" 0.206 / 1.804 / 2.007 / 1.914 / 1.913 s** (limit 5 s) and `idleWithTarget.currentCount == botsLastMinute == 0` asserted in each. Base rate from the ledger: 5 pass / 1 fail per 6-run arm with **0 park violations in every run** (the one failure was the arrival-time flake at the end of the fixed 480 s window) — so P996 is at or above the base rate, with no parking regression. (These five runs used the build from before the last refinement, the `StationId` idempotence guard that mirrors native `SetStation`'s `if (pstationOld != s)`; that guard only affects repeated assignment of the same station value and is a no-op for non-pod hulls, and every test above was re-run 3× *after* it.) |
| docs/tooling checks | `scripts/parity-scorecard.py`, `tools/p832a-mission-rules-audit.py --check`, `tools/check-glossary-links.py`, `grep -c $'\r'` on the rewritten CSVs | all pass (859 sim-rules rows validated, 351 native bodies/cases represented, 754 glossary paths, 0 CR bytes) |

**Second full fast tier (after the parity-row fix).** Re-ran `tools/run-tests.sh --tier fast --no-build` on the
final tree: **13 966 tests, 575.6 s, exit 1** — 13 964 passed, **2 failed**, 3 skipped, 1 slow-Fast offender
(10.477 s `P777WarpExitWobbleTests`). Both failures are the already-ledgered families above and neither is mine:
`P902SoundTemplateTests.Asr_and_cache_hit_mix_math_allocate_nothing_after_warmup` (`Assert.Equal` 0 vs 1 264 bytes —
ledgered allocation intermittency) re-ran **3/3 pass** alone (11-14 ms each), and
`LiveSideSelectTests.Flag_on_auto_seats…` at 32.45 s (the same ledgered auto-seat clock boundary). No P996 test
failed in either sweep, and `P832aMissionRulesInventoryTests` passed in this one. Ledgered in
`docs/TEST_FLAKES.md` under "P996 sweep 2".

**Repro proof.** With `src/Allegiance.Core/Sim/Ship.cs` reverted to `HEAD` and everything else in place, the new
loopback test fails at its first docked-pod assertion (`P996LifepodRespawnLiveTests` line 231: the berthed pod
*does* carry a deadline on master); with the fix it passes 3/3. The deadline it kept is exactly the rig's
`deadline 926.25` vs `sim-t 1030.7` relationship.

**Enumeration of the failing case on this host.** The two ledgered non-task failures fail 3/3 *alone* here (the
host is running other lanes), which is why they were classified from `docs/TEST_FLAKES.md`'s recorded clean-base
rates rather than from a single rerun. No P996 test failed or exceeded the fast-tier timing limit in any run.

---

## Native intent

A lifepod's oxygen clock is a property of *drifting in space*, not of the player: native arms it only on the hull
swap into a pod (`Igc/shipIGC.cpp:1408`) and can only ever read it from `Update`/`GetEndurance`
(`Igc/shipIGC.cpp:360-363`, `Igc/shipIGC.h:2473-2491`), and docking removes the ship from its cluster altogether
(`Igc/shipIGC.h:457-459`). A berthed pod is therefore safe by construction, and the *client* is what keeps the
player flying: while it is berthed in a pod the client replays the last real loadout
(`clintlib/appmsg.cpp:2022-2027` → `clintlib/clintlib.h:1876-1886`) and sends it to the server, so the hangar and
its Launch button always act on a real ship. Native is even permissive about what the server would accept — a
`BUY_LOADOUT` naming the pod hull passes the station's tech-based `CanBuy` gate
(`Igc/shipIGC.h:996-1000`) and re-arms the clock (`:1019-1020`) — precisely because nothing in native relies on
refusing it for the game to be playable.

## Modern approach

The port keeps the native state machine and makes the invariant explicit instead of incidental: `Ship.StationId` is
the single dock latch, so it is where the clock is cleared (dock) and re-armed (launch of a pod hull), with
`ChangeHull` arming only for a pod that is already in space; the stale deadline the rig died of is now
unrepresentable. The server accepts a lifepod hull exactly where native's `PurchaseShipLoadout` would (tech gate,
no group id) and hands it a fresh clock, and the MonoGame/ClientCore client keeps the native client's cached
loadout head (`ClientWorld.OwnSavedLoadout`, recorded from the same wire events native's `SaveLoadout` uses) and
plays it back through `ShopModel.RestoreSavedLoadout`, including native's successor upgrade, its
`TryToBuyParts` fill for an upgraded hull and its free-default fallback. The result is the rig's loop replaced by
native's outcome: a pod death leaves the player berthed, the hangar shows the ship he last flew, and launching it
carries no oxygen clock at all.

## Claude review (2026-11-11)

* Native citations re-grepped (`shipIGC.cpp:1408`, `:360-363`; `shipIGC.h:989-1020`, `:457-459`; `appmsg.cpp:2022-2023`;
  `clintlib.h:1815-1820`, `:1876-1886`): accurate.
* Rebased on master d0734dfa (P993/P994): one append conflict in `docs/TEST_FLAKES.md` (both ledgers kept); build 0/0;
  the P996 + lifepod/rescue/P279/P699/P977 set 3× alone on the merged tree: Core 96/0/0, ClientCore 9/0/0,
  MonoGame 8/0/0 each run.
* Hangar captures quantised (1.5 MB → 0.57 MB of assets).
* Known small deviation, not changed: `ShopModel.RestoreSavedLoadout` checks the budget against the successor hull's
  price and does not budget-check the cloned parts; native checks the saved hull's price (`clintlib.h:1815`) and clones
  parts within the remaining budget. The server's funds gate still decides the purchase.
* Follow-up (from §5): the port keeps the dead ship's parts on the pod (native terminates them in the kill loot loop,
  `FedSrv.CPP:11691-11702`); visible as the pod's "CURRENT SHIP" list in the loadout capture.
