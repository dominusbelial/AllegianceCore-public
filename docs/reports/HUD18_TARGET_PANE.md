# HUD-18 — target pane fidelity: weapon-range flag, LOCATION: sector fallback, time to intercept

Base: master `5f4c2f82` (HUD-16 tip; contains HUD-3 and HUD-13). Worktree
`allegiance-pi-hud18`, changes left uncommitted.
Model: DeepSeek V4.1 Flash, vision on.

Native citations are the checked-out tree at `~/Development/Allegiance/src/`; artwork
citations are `~/Development/Artwork/`.

All three items are **client-only**, exactly as in native: native computed every one
of them on the client (`WinTrek/indicator.cpp`, `WinTrek/consoledata.cpp`,
`Artwork/dialog.mdl`), and the port's client already had the data. No server, sim or
wire change was made — `git diff src/Allegiance.Core src/Allegiance.Server` is empty.

## 1. Native → port rule table

| Rule | Native | Port |
| --- | --- | --- |
| In-range flag exists per frame, for the current command target, **any** model kind | `WinTrek/indicator.cpp:186` (`bIisInRange = false`), `:188-192` (target is `GetCommandTarget(c_cmdCurrent)`, any `ImodelIGC`, same cluster, `CanSee`) | `Game1.ComputeTargetInRange` (Game1.Camera.cs) gathers for ship/station/asteroid/aleph; `TargetPaneModel.SameCluster` is the port's cluster+visible stand-in (the native per-target scanner part of `CanSee`, `Igc/shipIGC.h:380-389`, is not modelled — same documented approximation the pane's range/hull/shield rules already use) |
| Shooter/weapon: parent + turret part when boarded, else own ship + **selected** mount weapon | `indicator.cpp:194-212`; `clintlib/clintlib.cpp:1585-1588` (`GetMountedPart(ET_Weapon, m_selectedWeapon)`) | Own ship when `ParentId < 0`; the selected mount via `SelectedMountWeapon.Resolve(loadout, parts, _selectedMount)` (new pure helper). A boarded player returns **false** (turret weapon is out of scope, HUD-18b) |
| Healing/friendly gate | `indicator.cpp:214-218`: requires `w`; chase/missile camera **or** `(targetSide == ownSide \|\| AlliedSides) == (power < 0)`; a side-less target is not friendly | `TargetWeaponRange.IsInRange` input `ChaseCamera` (`CameraMode.Chase`/`ExternalMissile`) and `FriendlyOrAllied` (ship with real identity / station side, same side or `ClientWorld.AreAllied`; asteroid/aleph false) vs `ProjectilePower < 0` |
| Ammo speed GA | `Igc/common.cpp:141`: `speed = pt->GetSpeed() * (AmmoPerShot ? c_gaSpeedAmmo : 1)` | `ProjectileSpeed * (AmmoPerShot ? GlobalAttributesFor(ownSide).Get(SpeedAmmo) : 1)` |
| Energy lifespan GA | `Igc/weaponIGC.h:165-171`: `ls *= c_gaLifespanEnergy` when `energyPerShot != 0` | `ProjectileLifespan * (EnergyWeapon ? Get(LifespanEnergy) : 1)` |
| Solve + gate | `solveForLead` (`common.cpp:121-149`): `deltaP = hisPos − (myPos + weapon->GetPosition()*myOrientation)`, `deltaV = AbsoluteF ? hisVel : hisVel − myVel`, `solveForImpact(deltaP, deltaV, speed, target->GetRadius())`; in range when `t <= w->GetLifespan()` (`indicator.cpp:221-228`) | `TargetWeaponRange.IsInRange` calls the faithful `NavMath.SolveForImpact` with the own-shot misc pose, the hardpoint muzzle (`MuzzleOffsetsFor`), target pose/velocity (`TargetPaneModel.TryTargetPose`) and radius (0.5 × hull Length; station/asteroid/aleph exported `Radius`). No-solution sentinel `FLT_MAX/500000000` (`common.cpp:86`) is far above any lifespan → out of range |
| Lead capability gate | `indicator.cpp:286-320` only decides whether the **reticle** is drawn; `SetInRange(bIisInRange)` runs unconditionally at `:407` | **No capability input** in `TargetWeaponRange`; the server lead message (`LeadIndicatorState`) is no longer read by the pane. `LeadIndicatorView` (the reticle) is untouched and stays server-driven |
| LOCATION: fallback | `Artwork/dialog.mdl:4053-4060`: `SwitchString(GetRange(Target), "RANGE: "+NumberString(range), [(-1, "LOCATION:"+GetSectorName(Target))])` — note **no space** after `LOCATION:` | `TargetPaneModel.Build` resolves `SectorName` only when `Distance` is null; both skins draw `HudStringCache.Location(name)` = `"LOCATION:"+name` at the RANGE anchor (`HudRenderer.cs:1962-1974`, `ModernHudRenderer.cs:287-293`) |
| Range -1 | `consoledata.cpp:182-199`: distance only when the target's cluster is the player's cluster and `CanSee` | `TargetPaneModel`'s existing same-cluster distance (unchanged) |
| GetSectorName | `consoledata.cpp:70-87`: cluster name when visible; else a ship's `PlayerInfo::LastSeenSector()` (the last `FM_S_SHIP_STATUS` sector, `clintlib/clintlib.h:433`) when not NA; else `"unknown"` | visible/same-sector → RANGE (no sector text); ship → `ShipEntity.SectorId` **only when `StatusSectorKnown`**; station/asteroid/aleph → their exported `ClusterId`; otherwise `unknown` |
| TTI formula | `dialog.mdl:1280-1325`: `allseconds = GetRange/(GetSpeed(Me) − GetSpeed(Target))`; `GetSpeed` is `|velocity|`, 0 static, −1 unseen (`consoledata.cpp:136-150`); `abs`, `fmod(abs,60)`, `minutes=(abs−mod)/60`, `seconds=min(mod,59)` | `TargetTti.Text(range, mySpeed, targetSpeed)` — same float sequence, `NumberString = (int)(v+0.5)` (`modeler.cpp:2006-2009`), `SwitchString` exact-match semantics (`modeler.cpp:1369-1465`) |
| TTI strings | `Max(9.4999, seconds) == 9.4999` → `":0"+NumberString(seconds)`, else `":"+…`; `Max(0, allseconds) == 0` wraps in `( … )`; `|t| ≥ 1800` or an exactly-zero speed difference → `"infinite"` | identical branches in `TargetTti.Text`; golden cases below |
| TTI visibility | `dialog.mdl:1327-1355` `GHLargeTargetTTI`, entire group hidden while docked (`GetPercentHitPoints(MyStation) != 0`, `:1328-1330`) | `TargetPaneModel.Build` sets `TimeToIntercept = null` when `ShipEntity.StationId >= 0` (the port's "docked at MyStation", the same flag `HudContext.Docked` uses) |
| TTI skin | native GlassHUD-only (the classic `DefaultNormalTargetDisplay`, `dialog.mdl:4217-4242`, has none) | drawn **only** in the Modern pane (`ModernHudRenderer.DrawTargetPane`, under SPEED); classic deliberately draws none |

### Behaviour that changed

* `HudTargetPane.InRange` is now the **local** solve (`bool inRange` passed to
  `TargetPaneModel.Build`), not `lead.HasLead && lead.InRange`. The old flag was wrong
  for every non-ship target, every hull without `c_habmLeadIndicator`/relay, healing
  weapons, and it read the first weapon rather than the selected mount.
* `HudTargetPane` gained `SectorName` and `TimeToIntercept` (appended optional record
  parameters, so the existing positional constructors in `HudModelTests` still compile).
* `HudElements`: "Target range or sector" is now available when **either** RANGE or
  LOCATION text exists; "Target time to intercept" gained the placement
  `HudPlacement(Bottom, 242, 10, y-up)`, directly under the SPEED line.
* `ModernHudRenderer`'s in-range diamond no longer allocates `new[] { -1, 1 }` per
  frame (static `DiamondCorners` array).

### Per-frame allocation discipline

`Game1` caches the resolved weapon spec keyed by (catalogue reference, hull id,
`ShipEntity.Loadout` reference, `_selectedMount`); a cache hit is reference compares and
a struct copy. `TargetTti.Text` caches the m:ss strings by
`(rounded minutes, rounded seconds, pad-zero, parenthesised)` and returns the
`"infinite"` constant. `HudStringCache.Location`/`Tti` cache the prefixed strings.
`HudStringCache` is a renderer-only cache, like the existing entries.

## 2. GA / healing cases (the decision table)

Ported and pinned by `Hud18TargetPaneTests` (pure `TargetWeaponRange`):

| Case | Expected |
| --- | --- |
| damaging weapon, non-friend / side-less (asteroid, aleph), t ≤ lifespan | **true** (non-ship targets: today false) |
| damaging weapon, friend, t ≤ lifespan | false |
| damaging weapon, friend, chase camera | true (`indicator.cpp:214` skips the power test) |
| healing weapon (`Power < 0`), friend/ally | true when the solve is in range |
| healing weapon, enemy | false |
| healing weapon, enemy, chase camera | true |
| ammo weapon at 700 m, 600 m/s, 1.0 s vs `SpeedAmmo = 1.5` | false → **true** (speed 900 m/s) |
| energy weapon at 700 m, 1200 m/s, 0.5 s vs `LifespanEnergy = 1.2` | false → **true** (0.6 s) |
| ammo weapon with `LifespanEnergy = 10` / energy weapon with `SpeedAmmo = 10` | no effect (each GA is tied to its own weapon family) |
| `AbsoluteF` true: own 500 m/s toward a static target at 1000 m, 600 m/s, 1 s | false (t = 1.667); `AbsoluteF` false → true (t = 0.909); target velocity only enters the absolute branch |
| no-solution sentinel (`speed = 0`, `deltaV = 0`, `a == 0`, `common.cpp:86`) | false (sentinel ≈ 6.8e29 ≫ every lifespan) |
| selected mount 1 rather than mount 0 | mount 1's projectile is used (`SelectedMountWeapon.Resolve`) |

`TargetTti.Text` golden cases (all hand-computed in the test comments):
`diff 0 → infinite`, `|t| ≥ 1800 → infinite`, `75 s → 1:15`, `5 s → 0:05`,
`9.6 s → 0:10`, receding 75 s → `(1:15)`, range `-1` → `(0:00)`/`0:00` (native's tiny
value), target speed `-1` at 500 m and own 100 → `0:05`, `59.7 s → 0:59`,
`1799 s → 29:59`.

## 3. The `SectorId = 0` default finding

`ClientWorld.Upsert` creates `ShipEntity { ShipId = id }` with the C# default
`SectorId = 0`, and `0` **is** a real cluster id in some missions. An `EXPORT(26)`-only
ship (identity/hull/side, no `SHIP_STATUS(90)`, no `SET_CLUSTER`) therefore used to look
like it was in sector 0. HUD-18 adds `ShipEntity.StatusSectorKnown`, set by
`Apply(ShipStatusMessage)` (the native last-seen sector source), `SET_CLUSTER`, an
in-cluster `HEAVY_SHIPS_UPDATE` (every 176 entry is in our own cluster),
`RelaunchShip` (the carrier's sector) and `Promote` (the parent's), and the LOCATION
resolver requires both the flag and `SectorId >= 0` (native's `sid != NA` test).
`Ship_never_statused_reports_unknown_not_the_default_sector_zero` pins it against a
world where cluster 0 is named.

**Conclusion:** the matrix note "no last-seen sector on the wire" was wrong and is
corrected: the cross-side `SHIP_STATUS(90)` does carry the last-known sector
(`Server.Broadcast.cs:2098-2113`; `ShipEntity.SectorId` documented as
`Status.GetSectorID()`), and the flag only guards the *never-statused* case.

**Documented approximation:** for a station/asteroid/aleph outside the player's cluster
the port resolves the sector name from the object's exported `ClusterId`. Native's
`GetSectorName` first branch needs `CanSee`; native returns `"unknown"` for an
out-of-cluster model whose side visibility is unknown, while a friendly/known station
does satisfy `CanSee` (`Igc/shipIGC.h:380-389`). The port has no per-object side
visibility, so it treats an exported static as known — this is recorded here rather
than hidden. Ships (the interesting LOCATION: case) follow the native status-sector rule
exactly.

## 4. Real-client screenshots (1920×1080)

Captured with `tools/hud18-proof` (new): a real local `Allegiance.Server`, `m2scatter.igc`,
2 sides, a real `Game1` over TCP, production autoshot backbuffer path, one run per skin.
The proof places a **real engine-ticked enemy ship** (SpawnDefaultShip, `IsNpc=false`,
no peer, pinned position) 550 m ahead, a wire-exported in-sector enemy station 350 m
ahead, a real exported asteroid 300 m ahead, the real enemy Garrison (sector `Fejfar`)
as the out-of-sector target, and sets the own sim speed to 100 m/s for the TTI shot.
The own hull is `Adv Scout` (hull 13), **without** `c_habmLeadIndicator`
(`HabmCapabilities & 0x800 == 0`), and its selected mount 0 carries `EW Pulse Laser`
(native projectile 66: 1250 m/s, 0.5 s, power +5, absolute). The proof logs
`leadMsg(HasLead=False,InRange=False)` at every shot — the old pane value would have
been false in all of them.

Evidence assets (JPEG, ≤ 2 MB total for the task): `docs/reports/HUD18_assets/`.
Raw full-size PNGs and run logs: `~/allegiance-evidence/HUD18/raw/` and
`~/allegiance-evidence/HUD18/*.log` (outside the repo, per the evidence-size rule).

| Shot | Asset | What is actually visible (opened with the image reader) |
| --- | --- | --- |
| (a) enemy ship, no lead capability — Classic | `hud18-a-enemy-in-range-classic.jpg`, `zoom-a-icon-classic.jpg` | Cockpit, classic HUD. Bottom target pane: `Ship 8097: Adv Scout`, `SPEED: 0`, `RANGE: 550`, ARMOR/SHIELD gauges; the native green turret-range glyph (the `turretrangebmp` art) sits above `ARMOR`. The zoom shows the green glyph clearly. |
| (a) enemy ship — Modern | `hud18-a-enemy-in-range-modern.jpg` | Modern skin: `Ship 8097: Adv Scout`, `SPEED: 0`, `RANGE: 550`; the in-range marker (X of four diagonals) left of `ARMOR`; `TTI: infinite` (own and target speed both 0 — native's zero-difference branch). |
| (b) station in range — Classic | `hud18-b-station-in-range-classic.jpg` | Pane `Garrison: Garrison`, `SPEED: 0`, `RANGE: 350`, and the green turret-range glyph above `ARMOR` (this is a non-ship target; the old ship-only server flag was always false here). |
| (b) station in range — Modern | `hud18-b-station-in-range-modern.jpg` | Same station in the Modern pane: `Garrison: Garrison`, `SPEED: 0`, `RANGE: 350`, in-range X marker, `TTI: infinite`. |
| (b) asteroid in range — Classic | `hud18-b-asteroid-in-range-classic.jpg` | Pane `unknown: asteroid`, `SPEED: 0`, `RANGE: 300`, green turret-range glyph above `ARMOR`. (The exported test rock has no name; the type resolves to `asteroid`.) |
| (b) asteroid in range — Modern | `hud18-b-asteroid-in-range-modern.jpg` | Same rock in the Modern pane: `unknown: asteroid`, `RANGE: 300`, in-range X marker, `TTI: infinite`. |
| (c) other sector — Classic | `hud18-c-location-classic.jpg` | Pane `Garrison: Garrison`, `SPEED: 0`, and **`LOCATION:Fejfar`** where RANGE normally sits (no space after the colon; the target is the real enemy Garrison in sector 1057 `Fejfar`, while the player is in 1054 `Monmar`). No turret-range glyph — out of sector. |
| (c) other sector — Modern | `hud18-c-location-modern.jpg` | Same: `Garrison: Garrison`, `SPEED: 0`, **`LOCATION:Fejfar`**, `TTI: infinite` (native's zero speed difference), no in-range marker. |
| (d) TTI — Modern | `hud18-d-tti-modern.jpg`, `zoom-d-tti-modern.jpg` | Enemy at `RANGE: 500`, own sim speed 100 m/s (centre readout `100`), target speed 0; under `SPEED: 0` the pane shows **`TTI: 0:05`** (500 / (100 − 0) = 5 s → `min(mod,59)` pad branch `0:05`). The zoom shows `SPEED: 0`, `RANGE: 500`, `TTI: 0:05` and the in-range X. |
| (d) TTI — Classic (control) | `hud18-d-tti-classic-no-tti.jpg` | The same closing geometry in the classic skin: `RANGE: 500` but **no TTI line**, matching native (TTI exists only in GlassHUD; classic has none). |

Also logged at every shot (`~/allegiance-evidence/HUD18/classic-run.log`,
`modern-run.log`): `own ship=8096 hull=13 'Adv Scout' leadCapability=False
loadout=[part129@m0] selectedMount=0 leadMsg(HasLead=False,InRange=False)`.

The in-sector enemy station and the asteroid are proof-only *wire* objects (a fresh
station id exported with `ClusterId` = the own sector; an `ExportAsteroid` record sent
sector-wide) so the client has a non-ship target at a controlled range; the real
server-side station was not moved.

## 5. Tests

New file `tests/Allegiance.ClientCore.MonoGame.Tests/Hud18TargetPaneTests.cs` (23 tests):
the `TargetWeaponRange` decision table, the `TargetTti` golden cases, the
`TargetPaneModel` sector/TTI tests (other-sector station, hidden ship last status,
never-statused ship, same-cluster target, docked TTI null, element rows), the
selected-mount helper, and a 1000-frame allocation assertion
(`GC.GetAllocatedBytesForCurrentThread() == 0` for the in-range decision and
`TargetTti.Text` on steady inputs, after cache warm-up).

**The cached-spec path** inside `Game1` (hull/loadout/mount key) is not instantiable
without a `Game1`/`GraphicsDevice`, so it has no headless allocation test; the pure
`SelectedMountWeapon.Resolve` half is tested, and the cache itself is reference
compares + a struct return with no allocation by construction.

**Red-before-green evidence.** I stubbed the new members to today's behaviour
(`TargetWeaponRange.IsInRange` → `false`, `TargetTti.Text` → `"infinite"`,
`TargetPaneModel` → lead-message `InRange`, `SectorName`/`TimeToIntercept` → null,
`HudElements` rows reverted, `SelectedMountWeapon.Resolve` → the first weapon) and ran
the suite: **21 of 23 tests failed**, including every test with a positive assertion.
The only two survivors were the theory rows that assert `false` (a blanket-`false` stub
cannot fail them) — noted honestly. The stub run log is
`~/allegiance-evidence/HUD18/stub-red-run.log`; the production files were then restored
from a backup and the suite re-run green.

Verification:

* `dotnet build AllegianceCore.slnx -c Release` — **0 warnings, 0 errors**;
  `dotnet build tools/hud18-proof/hud18-proof.csproj -c Release` also **0/0** (proof
  tools are not part of the solution, matching `hud13-proof`/`hud16-proof`).
* New tests alone, 3×: **23/23 pass** each run.
* `tools/run-tests.sh --tier fast` — **PASS**, 9658 tests, 270.5 s
  (`~/allegiance-evidence/HUD18/fast-tier.log`; the only `NotExecuted` are the three
  pre-existing `MeshyPackTests` skips). No Core/Server change, so no AOT publish was
  required by the light gate.

## 6. Matrix

Only the three named rows were edited as text (LF endings kept, parsed back as exactly
6 columns):

* **404 "Target range or sector" → `done`.** RANGE and `LOCATION:` are drawn in both
  skins; ships use the last `SHIP_STATUS` sector (guarded by `StatusSectorKnown`),
  statics use their `ClusterId`, `unknown` when no cluster is known.
* **408 "Target in weapon range" → stays `partial`.** The client-side solve now matches
  native for any ship/station/asteroid/aleph, the selected mount, GA scaling, the
  healing/friendly gate and no capability gate. **Remaining:** the turret gunner weapon
  (`indicator.cpp:196-204`) — a boarded player gets `false` until HUD-18b, because the
  client keeps only `ParentId` (`ClientWorld.cs:3844-3856`); and probe/treasure/cluster
  targets still have no resolved cluster/pose.
* **410 "Target time to intercept" → `done`.** Note added: "native GlassHUD-only
  (dialog.mdl:1280-1355); port shows it in the Modern pane; classic has none natively".

Stale cites were replaced with real line numbers (`HudRenderer.cs:1962-1974/1995-1997`,
`ModernHudRenderer.cs:287-293/318-325/328-333`); `HudRenderer.cs:1144` and
`HudFrame.cs:85` no longer appear in these rows.

## 7. Unfinished / out of scope

* **Turret gunner weapon (HUD-18b).** `PassengerData.TurretId` arrives in
  `LOADOUT_CHANGE`, but the passenger loop (`ClientWorld.cs:3844-3856`) keeps only
  `ParentId`; the parent's loadout availability on a gunner client is unverified.
  Today a boarded player's in-range flag is `false`.
* Probe/treasure/other targets: `TargetPaneModel` does not resolve their cluster, so
  they stay out of range and their LOCATION text is `unknown`.
* Server `BroadcastLeadIndicator` gating and the lead reticle were not touched (the
  server still only computes its message for ship targets; the pane no longer consumes
  it).
* No GlassHUD skin, classic pane layout change, fonts, kill bonus, missile camera or
  any server/sim/wire change.
