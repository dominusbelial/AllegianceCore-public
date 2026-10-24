# P803 (PAR-8) — turret gunner and observer HUD, end to end

A player boarded on a teammate's ship as a turret gunner now sees what native shows, and **the turret actually fires**.
Proven in a real two-client window run (pilot + gunner, Classic and Modern) and by loopback/sim tests.

Work: Pi (GLM-5.3 Flash) built the HUD consumers, the turret-state relay and the tests; Claude finished it (the
two-client proof, two client crashes, the turret-gunner binding bug, the Modern label overlap, row classification).

## 1. Native rules ported (verify with the cites)

| Behaviour | Native | Port |
|---|---|---|
| Boarding | `FedSrv/FedSrv.CPP:2322-2345` `BoardShip`: mission started; boarder AND carrier **docked** (or moving within the same carrier); carrier is a **player** ship; seat cycles `GetMaxFixedWeapons()..GetMaxWeapons()-1`, no turret seat → observer | unchanged server rule (`Server.Docking.cs` `PerformBoard`) — the proof exercises it |
| Gunner's source ship | `WinTrek/indicator.cpp:130,181-204` `GetSourceShip()` / `GetParentShip()` | velocity marker and target-in-range read the carrier (`Game1.Camera.cs` `TrySourcePose`) |
| Equipment / inventory rows | `consoledata.cpp:684,1080,1122`, `inventoryimage.cpp:65,141-169,289-291` | assigned weapon, active/selected state, per-turret occupant rows, observers (`HudInventoryPane.cs`) |
| Board-state text | `dialog.mdl:2104-2112,5431-5433` (BoardTextImage / InTurretPaneImage) | "Turret of X" / "Observer of X" + IN TURRET badge on the loadout screen (`DockedScreenRenderer.DrawBoardText`) |
| Turret fire | `Igc/weaponIGC.h:236-254` `fIsWeaponSelected` (turret = `m_pshipGunner && gunner stateM & weaponsMaskIGC`), `shipIGC.cpp:326-341` | unchanged; see §2.3 for the binding bug |
| Weapon mounted on an occupied seat | `Igc/weaponIGC.cpp:126-135` (`SetMountID`: "Is there a turret gunner in this position") | **new** `Ship.BindTurretGunner`, called at every weapon-mount site |
| Other players' turret state | `FedSrv.CPP:3662-3693` `SendShipUpdates` | relayed turret facing/firing latch on the boarded child (`Server.Broadcast.cs`, `ClientWorld`) |

`TK_ViewTurret` → **n/a**: native's handler is commented out (`WinTrek.cpp:9856-9864`, "This camera mode is not
actually handled by the viewing code") and its key registration is off (`trekinput.cpp:196`); the port's rebindable
gunner camera toggle is a documented extension.

## 2. Bugs found by the real two-client proof (all fixed here)

1. **Client crash — hangar preview of a boarded player.** Boarding empties the gunner's own loadout; the hangar's 3-D
   preview fell back to the procedurally built stand-in model whose mesh has no parent bone →
   `NullReferenceException` at `ShipRenderScale.RenderRadius` (`mesh.ParentBone.Transform`). Fixed: a boarded
   player's preview shows the **carrier's** hull (native source ship); a missing bone is the identity transform.
   Likely also the "client silently quits after ~1 minute" P786b observed.
2. **Client crash — board-state text.** `DockedScreenRenderer.DrawBoardText` drew outside `SpriteBatch.Begin/End`.
   Fixed: it opens its own batch like the renderer's other passes.
3. **The turret never fired (simulation).** The gunner was bound to the turret weapon at boarding, but the carrier's
   launch `BUY_LOADOUT` remounts every part as new `Weapon` objects; the port lacked native's rebind on mount
   (`weaponIGC.cpp:126-135`), so the gunner's trigger pointed at a discarded weapon: **280 `ACTIVE_TURRET_UPDATE`s,
   0 projectiles**. Fixed in Core (`Ship.BindTurretGunner`); this path is also what bot gunners (P814) use.
4. **The gunner heard no shots.** The local "weapon fire" cue plays when a projectile spawns at the listener's ship;
   a boarded gunner's own record keeps its docked pose, so its turret's shots never counted (gunner 0 vs pilot 148
   cues). Fixed: a boarded player listens from the carrier (native source ship) — proof: gunner **147**, pilot 147,
   149 projectiles.
5. **Pilot quits → the gunner was dropped** (seen live by Domingo flying the turret: "it decoupled from the ship").
   Native `RemovePlayerFromSide` promotes the first turret gunner to pilot (`fsmission.cpp:895-913`,
   `pship->Promote()` + `FM_S_PROMOTE` to the sector, or the side group when docked); the port's disconnect path only
   detached the children. Fixed: `Server.PromoteHeirOnPilotExit` runs before the ship is retired — the gunner takes
   the hull, parts, ammo and pose (`Ship.Promote`), gets PROMOTE(189) (its client already switches to pilot), and the
   departed pilot is unseated. Test: `LiveBoardShipTests.Pilot_quitting_promotes_the_turret_gunner_to_pilot`.
6. **The gunner flew a detached copy of its ship** (seen live: "it decoupled from the ship", "I still have control",
   "I'm just very far away"). While boarded, the client still ran the own-ship flight simulation for the gunner's
   unseated record, so the gunner's inputs flew it away and the turret cockpit view (eye = own scene pose) followed it.
   Native: a gunner has no ship to fly — controls only aim the turret, the view rides the source ship. Fixed: while
   boarded the own-ship sim is skipped, the gunner's scene pose is the carrier's smoothed render pose, and the carrier
   hull is hidden in the turret cockpit view like the own hull (`Game1.FlightInput.GatherRenderPoses`, `_turretCarrierId`).
7. **Modern layout:** Modern's larger font let the sector name run into "Turret of:" — the label now clears the name
   (visual only; Classic keeps the native -75 anchor).

## 3. Proof (real windows, local server, `m2xspiderw.igc`)

Harness additions (live-proof only): `--auto-accept` (the leader turns native auto-accept on and re-asserts it),
`--auto-launch-when-boarded N` (launch once N children ride), `--auto-launch-after` now really delays the launch, a
docked pre-buy of the launch hull (native `BUY_LOADOUT` fLaunch=false), `--auto-gunner-fire` (hold the turret
trigger once airborne), `--auto-turret-yaw DEG`. The launch clock starts only when airborne for boarding flows. Server `--verbose true` and a
flush wait before shutdown keep the board/turret lines.

Sequence (both styles): PILOT seats on side 0, buys a **Gunship** (1 fixed + 2 turret mounts) while docked;
GUNNER joins side 0 and boards → server `ship 8099 boarded 8098 as turret gunner slot 1`; the carrier launches with
the gunner aboard; the gunner holds fire.

| Style | boarded | ACTIVE_TURRET_UPDATE sent | turret projectiles (server PROJ_INST from the carrier) | client crashes |
|---|---|---|---|---|
| Modern | yes | 278 | **149** | 0 |
| Classic | yes | 280 | **149** | 0 |

Final run on the fixed build (camera rides the carrier, gunner audio): Modern 149 projectiles / 146 gunner fire
sounds, Classic 149 / 146, both 5 screenshots. One earlier Classic run crashed in `MemoryStream.set_Capacity`
(AccessViolation while loading a sky texture right after launch — native heap corruption surfacing in managed code,
not the turret path); it did not reproduce with crash dumps enabled and is tracked separately.

Visible fire: the gunner swings the turret 50° off the carrier's axis (`--auto-turret-yaw 50`) so tracers cross the
screen instead of receding down the line of sight; the pilot shoots from the chase camera. The turret draws on the
carrier's shared ammo pool (native), so the pilot's AMMO gauge drops while only the gunner fires (Modern: 88 %).

- `P803_assets/gunner-in-space-classic.webp` — native red TURRET reticle, "Turret of: PILOT", the Gunship inventory
  with the GUNNER occupant row (native turret icon) on the turret weapon.
- `P803_assets/gunner-in-space-modern.webp` — same in Modern (carrier speed, GUNNER row).
- `P803_assets/pilot-chase-turret-fire-classic.webp`, `…-modern.webp` — the carrier from the pilot's chase camera with
  the turret's red SkyCap tracer leaving to the left (Modern: AMMO 88 %).

## 4. Tests

- `TurretGunnerFiringTests.A_weapon_mounted_on_an_occupied_turret_picks_up_its_gunner_and_fires` (new, fails without
  `BindTurretGunner`) — 3× green with the existing turret tests (4/4).
- P803 HUD/relay/loopback tests (Pi): 12/12, incl. `Live_boarded_gunner_huds_name_the_right_turret_on_both_clients`.

## 5. Parity

HUD rows: Target in weapon range, Velocity direction marker, Observer and turret board state, Equipment part identity
and key, Equipment selected and active state, Inventory turret gunner identity → **done**; `TK_ViewTurret` → **n/a**;
SIM-0470 (ship status deltas and turret updates) updated. HUD 77.06 % → **82.57 %**, interface 93.39 % → **94.58 %**.

## 6. Out of scope / follow-ups

Bot gunners (P814: native-faithful, default on in skirmish/showcase), the auto-turret experiment (P815).
