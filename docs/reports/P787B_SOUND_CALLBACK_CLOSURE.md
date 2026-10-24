# P787b (PAR-6b) — sound and callback closure: real gaps implemented, the rest reclassified with evidence

Branch `pi/p787b`, base master `d4464dc0` (public `AllegianceCore-public` worktree). Native reference
`~/Development/Allegiance/src/` (re-grepped 2026-10-24; all cites re-verified against that tree).
Asset inventory in `src/Allegiance.ClientCore.MonoGame/Content/sounddef.mdl` (the bundled copy of the
shipped `Artwork/sounddef.mdl`).

**Scorecard after this task** (`python3 scripts/parity-scorecard.py`): callbacks 98.46 % (192/195 weighted; 64/65 rows complete), sounds 98.91 % (544/550 weighted; 209/212 rows complete — the 3 open rows are the P787a launcher-reload rows and the P785 slideshow row, out of scope here, plus the buildingEffect ambient id named in §1). All 15 rows below end `done` / `implemented` / classified remainder.

---

## 1. Implemented cues (group 1)

### SIM-0321 / SIM-0327 — loadout mouseover (load.cpp:236, loadout.cpp:1178) — **done**

Native rule. The custom-loadout slot images play `mouseoverSound` on every `MouseEnter`
(`WinTrek/load.cpp:233-237`: `m_ploadout->SlotEntered(this); trekClient.PlaySoundEffect(mouseoverSound);`).
The hangar/loadout screen's part list plays it when the hovered row changes
(`WinTrek/loadout.cpp:1173-1190`): `OnListHover(nSel)` plays only
`if (m_nLastHoverIndex != nSel)`, and `m_nLastHoverIndex = nSel` runs on **every** call — including
the `-1` "nothing hovered" call (loadout.cpp:1186) — so leaving the list and re-entering a row cues again.

Port. New pure rule `Rendering/ShopHover.Hover` (`src/Allegiance.ClientCore.Game/Rendering/ShopHover.cs`):
cue iff the hovered row is a real row and differs from the last hover; the last-hover index resets to
−1 on "nothing". `Game1.HandleShopMouseHover` (`Game1.Panes.cs`) maps the free docked-screen pointer
onto the shop's row bands with the same geometry the touch taps use
(`DockedTouchZones.RowIndex`), moves the shop cursor onto the hovered row, and applies the rule via
`Game1.RunShopHover` (the P764b split pattern), which calls `UiSounds?.PlayMouseOver()` — the
`IUiSoundSink` seam (`SoundManager.PlayMouseOver`, the bundled `mouseover` wave, sounddef.mdl:322).
One cue covers both native surfaces: the port's slot-driven shop rows stand in for the loadout slot
images (load.cpp) and the part list rows (loadout.cpp) alike; for discrete rows the change-based rule
is audibly identical to native's enter/leave pairs (off-list-and-back = one cue each side).
Classified `design`; the redesigned docked screen is a documented visual difference (P519).

How the data reaches the client: all client-side (pointer position + shop model state); no wire data.
Tests: `P787BShopHoverAndPickTests` — the pure rule table, the leave/re-enter rule, and the
`RunShopHover` call-site pin (`mouseover` once per change; cursor follows the hover).

### SIM-0332 — sector-map plain right-click PickCluster (sectormap.cpp:1586) — **done**

Native rule. A plain right-button hit on a cluster plays `mouseclickSound` and hands the cluster to
the console (`WinTrek/sectormap.cpp:1583-1587`:
`trekClient.PlaySoundEffect(mouseclickSound); pWindow->GetConsoleImage()->GetConsoleData()->PickCluster(pClusterFound, button);`).
`Console::PickCluster` (`WinTrek/console.cpp:1739-1791`): **not composing** →
`StartComposing(c_csComposeChat)` with `SetRecipient(CHAT_FRIENDLY_SECTOR, clusterID, pcluster)` — a
chat compose addressed to the clicked sector; **already composing** → the compose becomes the command
compose (`c_csComposeCommand`) and PickCluster issues a `c_cidGoto` order with a cluster buoy for the
clicked sector, sent to the current compose recipient. (A builder-ship individual recipient gets a
build-order special case via `FindTarget`, console.cpp:1758-1777.) The Ctrl+right-click highlight
mark (sectormap.cpp:1491-1510) plays NO sound — unchanged, still silent in the port.

Port. `Game1.HandleStarmapClick` now routes a plain right-click press through
`Game1.RunConsolePickCluster(cluster)` (`Game1.StarmapClick.cs`): `UiSounds?.PlayMapClick()` (the
sectormap.cpp:1586 cue), then the compose branch — `SetComposeState(Chat)`,
`_chatTarget = FriendlySector`, and the new `_chatSectorRecipient` override; or, while composing,
`_composeState = Command` plus the `c_cidGoto` cluster-buoy send (`SendChatBuoy("goto", …)`), the
same transport `IssueStarmapGoto` uses. The send path (`Game1.Session.SendChatLine`) resolves the
FriendlySector/AllSector recipient as `_chatSectorRecipient ?? world.ViewedClusterId ?? own sector` —
native's `BaseClient::SendChat` NA fallback is `GetChatCluster()` = viewed cluster, else the own
cluster (clintlib.cpp:2352-2366, clintlib.h:998-1009), which also **fixes** the port's previous
own-sector-only default. The recipient persists across compose sessions like `_chatTarget` (native's
`m_chsChat` is a member, console.cpp:644). Classified remainder: the builder sub-branch
(console.cpp:1758-1777) is not ported — the port's compose recipient model cannot address a builder
ship (no FindTarget over sector asteroids client-side); era-constraint (native 2010 builder UI).

How the data reaches the client: the picked cluster is client input; the goto order rides the
existing CHATBUOY(108) message native's `SendChat` uses for buoy orders (clintlib.cpp:2460-2469).
Tests: `P787BShopHoverAndPickTests` — click cue + compose state; empty-map no-op; composing branch;
and a full loopback asserting the composed line reaches the wire as
`CHATMESSAGE FriendlySector OidRecipient=7` (the right-clicked cluster, not the own sector).

### SIM-0273 / SIM-0274 — sonic chat voices (WinTrek.cpp:1108/1110) — **done (closed with cites, no code)**

Native rule. `WinTrek::PlaySonicChat(id, voice)` (WinTrek/WinTrek.cpp:1093-1114): the chosen voice's
sound template via `StartSound(pvoice->GetSound())` (WinTrek.cpp:1108), else
`trekClient.PlaySoundEffect(id)` (WinTrek.cpp:1110). It is called with `voice = 0` only, from the
chat receive path: a null-text chat whose id is a sonic chat index → `PlaySonicChat(idSonicChat, 0)`
(trekigc.cpp:4530-4532); a chat with a **direct SoundID** (text present) → `PlaySoundEffect(idSonicChat)`
(trekigc.cpp:4534-4536).

Port. The executable path already exists end to end (P732/P738):
`ClientWorld.ReceiveChat(ChatMessage/ChatBuoy)` resolves the cue via
`NativeVoiceCatalog.Resolve(id, text, messageIsAbsent)` (`src/Allegiance.Core/Audio/NativeVoiceCatalog.cs:14-25`)
— `messageIsAbsent` → the sonic chat's voice cue (`SonicChats[id].Cue`, the generated
`NativeVoiceCatalog.Generated.cs` table, one cue per shipped `quickchat.mdl` sonic chat — the
`GetNth(0)` voice) — and records a `ChatVoiceOver` sound event that `Game1.Audio.cs:420-421` plays
via `SoundManager.PlayVoice` (the serialized-voice chain with native mutexes/gains). A direct
SoundID resolves through `Sounds[id]` — the trekigc.cpp:4536 branch. The caption-compatibility leg
(text == caption) covers the port's P217 sender format. Pinned by `P732VoiceOverTests` and
`P709ClientCallbacksTests.Chat_callback_observes_sound_and_notification_once_on_wire_and_direct_delivery`.

Classification. The fallback `PlaySoundEffect(id)` (WinTrek.cpp:1110) fires only when the sonic
chat's voice list is EMPTY (`pvoice == null` at voice index 0). Every shipped sonic chat has ≥ 1
voice (quickchat.mdl / the generated catalog), so the branch is dead in practice — and `id` there is
the **sonic-chat index** used as a SoundID, i.e. a native bug (it would play an unrelated numbered
sound if it ever fired). Modern better approach: skip the cue (the port's behaviour: an unresolvable
id stays silent — `Resolve` returns null and no event is recorded), which the port does. Both rows
`done`: SIM-0273 = the voice-template path (SonicChats cue), SIM-0274 = the direct-SoundID path
(`Sounds[id]`); the native fallback leg is classified `bug`, intentionally not reproduced.

### SIM-0347 — weapon activate loop (trekigc.cpp:984-986) — **done**

Native rule. Per fixed-weapon mount:
`PlaySoundIf(m_vpActiveSound[mount], pweapon ? pweapon->GetActivateSound() : NA, m_psounceWeaponMount[mount], pweapon && pweapon->fActive())`
(WinTrek/trekigc.cpp:984-986). `PlaySoundIf` (trekigc.cpp:930-950) starts the loop when not playing
and stops it when the predicate goes false; the loops are also stopped when the mounted weapon's
type changes (trekigc.cpp:960-975).

Port. The id → wave table is derived exactly like P465's: sounddef.mdl's `soundList` maps the id to
an `*ActivationSound` template (`(81, antiUtility1ActivationSound)` …, sounddef.mdl:1948-2129) and
each template's `ImportWave` names the wave — `WeaponSoundMap.ActivationStemBySoundId`
(`src/Allegiance.ClientCore.Game/Audio/WeaponSoundMap.cs`). All 44 distinct `activateSound` ids of
the real PCore001 core resolve (corpus dump: 81-142, 530/533/550/553; 28 distinct waves, bundled in
`Content/Sounds/*pwrup.ogg`, see §5). `SoundManager.StartWeaponActivate/StopWeaponActivate/
StopAllWeaponActivates` keep one looping instance per mount (native's `m_vpActiveSound[mount]`).
The predicate: native's `fActive()` is the client's own Core sim state; the port's client-visible
proxy is the same shot/burst state the P465 burst loops use — a mount that fired this frame or is in
sustained fire (`EmitOwnShots`, `Game1.FlightInput.cs`: `_activateLoopMounts`) starts its loop if
`w.ActivateSound != 0`; the fire-released block stops every loop (trekigc.cpp:942-950's false leg).
Named predicate gap, closed: **none** — mounts without an activateSound (id 0/NA/unlisted) stay
silent exactly like native's `StartSound(NA)`. Weapon-changed stop (trekigc.cpp:960-975) is not
mirrored: the port's mounted weapon type cannot change in flight (loadout edits happen docked, and
the burst loops have the same pre-existing limitation) — classified era/design remainder.
Tests: the activation table resolution test (all 44 ids resolve; 0/999 → silence) in
`P787BShopHoverAndPickTests`; the loop lifecycle rides the P465 own-shot echo tests (unchanged shape).

### SIM-0348 — turret turn sound (trekigc.cpp:988-996) — **done (local-gunner half)**

Native rule.
`PlaySoundIf(m_vpTurretSound[mount], m_pship->GetHullType()->GetHardpointData(mount).turnSound, …,
pweapon && pweapon->GetGunner() && (pweapon->fActive() || pweapon->GetGunner() == trekClient.GetShip()) && TurnRate(pweapon->GetGunner()) > 0.01)`
(WinTrek/trekigc.cpp:988-996), where `TurnRate(ship)` = yaw²+pitch²+roll² of the gunner's control
axes (trekigc.cpp:807-813). Native's own comment (:987) concedes remote gunners' turret motion is
only known while firing.

Port. The turn-sound ids resolve via `WeaponSoundMap.TurnStemBySoundId` —
`(120, smallTurretSound)` → `sturret`, `(121, mediumTurretSound)` → `mturret`, `(122, largeTurretSound)`
→ `lturret` (sounddef.mdl:1975-1977); the real PCore001 core uses exactly ids 120/121/122 (corpus
dump). Waves bundled (`sturret/mturret/lturret.ogg`, §5). The pure rules live in
`Audio/TurretTurnCue` (`ShouldLoop` = the `TurnRate > 0.01` predicate; `FirstTurretTurnSound` =
the carrier's first non-fixed hardpoint with a turnSound — gunners man turrets, the non-fixed
hardpoints; the boarding wire carries no mount index, and on most shipped hulls all turret
hardpoints share one id — the 14 hulls mixing 121/122 may pick the neighbouring tier: classified
asset-level remainder). The loop (`SoundManager.SetTurretTurnLoop`) is wired in
`Game1.UpdateTurretGunner` (the LOCAL-gunner branch native's predicate always admits:
`pweapon->GetGunner() == trekClient.GetShip()`), keyed to the gunner's look axes; leaving the turret
stops it. **The remote-gunner half mirrors native's own limit**: the port's boarding wire carries no
remote turret gunner/active state, so other players' turrets stay silent here — the same information
limit native documents, not a behaviour gap beyond it. Classified `design`.

Tests: `Turret_turn_predicate_is_native_turn_rate_squared_above_0_01` (0.0075 → silent, 0.03 → loop)
and `First_turret_turn_sound_is_the_first_non_fixed_hardpoint_with_one` (fixed/no-sound hardpoints
skipped), in `P787BShopHoverAndPickTests`.

### SIM-0345 / SIM-0411 — ambient (trekigc.cpp:714, trekigc.cpp:5085) — **done**

Native rule. `ThingSiteImpl` attaches an `AmbientSoundSite(pmodel, id)` per object type
(trekigc.cpp:1426-1468); `AmbientSoundSite::UpdateSounds` starts the loop once and keeps it
(trekigc.cpp:711-718), resetting on cluster change (trekigc.cpp:721-725). The docked station's
interior ambient is separate: while docked on the combat screen,
`newAmbientSound = GetShip()->GetStation()->GetInteriorSound()` and
`if (!m_psoundAmbient || m_idAmbient != newAmbientSound) { stop; StartSound(m_idAmbient, NULL); }`
(trekigc.cpp:5076-5087); undocked kills it (trekigc.cpp:5093-5098).

**Native's ambient id inventory, and the port's proof per id:**

| Native ambient id (model) | id space / template | Port path | Status |
|---|---|---|---|
| warp (trekigc.cpp:1436-1438) | `alephSound` → `alephloop` | P237 warp ambient loop (`_warpAmbients`, per WarpId) | done (pre-existing) |
| projectile (trekigc.cpp:1453-1461) | per-type `GetAmbientSound()` | P232 projectile ambient loop (`erbeam` default; per-type ids not client-visible — the port's noted approximation) | done-visual (single default wave) |
| missile (trekigc.cpp:1449-1452) | per-type `GetAmbientSound()` | P236 missile ambient loop (`mmissile` default) | done-visual (single default wave) |
| probe (trekigc.cpp:1444-1447) | per-type `GetAmbientSound()` | probe ambient default (`erbeam` scanning loop; per-type unreachable client-side, P260) | done-visual (single default wave) |
| buildingEffect (trekigc.cpp:1440-1442) | `buildSound` | the build effect renders visually, but no per-model `buildSound` loop cue is wired | the one remaining ambient id without a cue — named remainder (small, queued; the construction trigger already exists client-side) |
| asteroid (trekigc.cpp:1432-1434) | `asteroidSound` = `PitchSound(0.35, ThreeDSound(200, LoopingSound(shipout)))` (sounddef.mdl:382; DEFSOUND(asteroid), sounds.h:140) | **NEW**: per-asteroid `shipout` loop at pitch −0.35, 200-unit radius, distance-attenuated (`AsteroidAmbientSoundCurve`, `SoundManager.Start/Update/StopAsteroidAmbient`, driver `Game1.UpdateAsteroidSounds` + `_asteroidSoundPlanner`) | done (this wave) |
| station (trekigc.cpp:1465-1468) | per-type `GetExteriorSound()` (default `shipout`, sounddef id 380) | P267 station exterior ambient loop, distance curve | done (pre-existing) |
| docked station interior (trekigc.cpp:5076-5087) | per-station-type `interiorSound` — the real PCore001 station types use ids 420/425/430/435/440/445/450/451/452 (corpus dump), one per civ family; each template is `PairedSound(PitchSound(0.5, LoopingSound(shipint)), intermittent random bed)` (sounddef.mdl:1030-1150) | **NEW**: `SoundManager.SetStationInteriorAmbient` — loops the bundled `shipint` wave at pitch −0.5 while docked, restarts when the id changes, stops when undocked; the switch decision is the pure `AmbientSwitchRule` (start/none/switch/stop = native trekigc.cpp:5076-5087 verbatim). Wired per frame from the own-ship audio block (`Game1.FlightInput.cs`, resolves the docked station's type → interiorSound via the shop catalogue) | done (this wave) |

Note on buildingEffect: it is the one remaining ambient id without a cue (named remainder above);
every other row of the table is proven.

SIM-0345's row is `done` on the strength of the table (the per-model ambient rule is implemented for
every model type except buildingEffect, named above); SIM-0411's row is `done` with the interior
switch implemented. Tests: `Ambient_switch_follows_native_trekigc_5076_5087` (the 6-case switch
table) and `Asteroid_ambient_curve_is_native_authored_200_unit_falloff` in
`P787BShopHoverAndPickTests`.

## 2. Caller audit — the IIgcSite site methods (SIM-0176/0177/0178/0180) — **done**

The current port `IIgcSite` (`src/Allegiance.Core/Sim/IIgcSite.cs`) declares **no**
`PlaySoundEffect` / `PlayNotificationSound` / `PlayVisualEffect` (P760b deleted the dead
declarations; the CSV rows still carried the stale "declared but no override" reason). These are
architecture differences, not behaviour gaps: native delivers these site calls through the client's
own IGC instance; the port plays sound/effects through wire-event planners. Every native caller, and
the port path that produces the same cue under the same condition:

### SIM-0176 / SIM-0177 — `IgcSite::PlaySoundEffect(SoundID, model[, offset])` (igc.h:4830/4831)

| Native caller | Native cue | Port path (same condition) |
|---|---|---|
| `asteroidigc.h:154-155` (asteroid damaged) | `rockHitSound` (`hitrock`, sounddef.mdl:308/266) | `PROJECTILE_HULL_HIT(213)` with asteroid target → `HitSoundEntity` targetId −1 → `HitSoundSelector` → `HitSoundKind.Rock` → `SoundManager.PlayRockHitSound` (P463c), distance-attenuated, own-sector gated (`Game1.Audio.UpdateHitSounds`, Game1.Audio.cs:712-766) |
| `shieldIGC.cpp:92-94` (shield recharges above 5 %) | shield type `activateSound` (`shieldup`) | local ship's shield 0.05-crossing edge → `ClientSoundEventKind.ShieldActivate` (SIM-0241, ClientWorld.cs:759-772) → `Game1.Audio.cs:575` → `PlayShieldActivate` (P741) |
| `shieldIGC.cpp:182-185` (overload knocks the shield down) | shield type `deactivateSound` (`noshld`) | local ship's online→≈0 edge → `ShieldDeactivate` (SIM-0242, ClientWorld.cs:748-757) → `Game1.Audio.cs:581` → `PlayShieldDeactivate` (P741) |
| `shipIGC.cpp:834/1004/1189/1193` (ship collision, both parties) | `collisionSound` (positional, once per party) | server `COLLISION_EVENT(216)` → `ClientWorld.Apply(CollisionEvent)` → `Game1.Audio.UpdateCollisionSounds` (Game1.Audio.cs:774-831) → `PlayCollisionSound(volume)` per party (`CollisionFeedback.SelectSoundSources`) |

Tests: `CollisionFeedbackTests`, `P463/P463c` hit/collision pins (existing). Both rows `done`
(SIM-0177's offset overload is the same collision path — native passes the impact offset for the
positional source; the port's planner computes the source from the collision position).

### SIM-0178 — `IgcSite::PlayNotificationSound(SoundID, model)` (igc.h:4832)

| Native caller | Native cue | Port path (same condition) |
|---|---|---|
| `afterburnerIGC.cpp:117-119`, `cloakigc.cpp:73-75`, `dispenserigc.cpp:96-98`, `magazineigc.cpp:94-96`, `shieldIGC.cpp:80-82`, `weaponIGC.h:44-46` (a part finishes mounting) | `mountedSound` (id 1023 → wave `unmount`, sounddef.mdl:287) + `PostNotificationText("%s ready.")` | Core sim raises `IIgcSite.WeaponReady` at the same moments (`Ship.cs:3253`, `Ship.cs:8881`) → `ServerHost.WeaponReady` → `Server.Combat.BroadcastWeaponReady` (Server.Combat.cs:421-450): a `CHATMESSAGE` with `VoiceOver = WeaponReadyCue.SoundId` to the owner (sound+text) and sound-only to boarded crew → client `ReceiveChat` resolves the id via the catalog → `ChatVoiceOver` event → `PlayVoice` (P707 seam) |
| `shieldIGC.cpp:186` (shield offline) | `salShieldsOfflineSound` (`vo_sal_shieldsoffline`) | same shield-down edge as above → `ShieldsOffline` (SIM-0243) → `Game1.Audio.cs:568-571` → `PlayShieldsOffline` (P717) |
| `shipIGC.cpp:1601-1609` (cloak toggles) | `salCloakEngageSound` / `salCloakDisengageSound` | own cloak engage edge → `PlayCloakEngage/Disengage` (SIM-0248, ClientWorld.cs:769-772 → Game1.Audio.cs:588-590; P735) |
| `shipIGC.h:616-617` (ammo auto-reload succeeds) | `salReloadingAmmoSound` + `startReloadSound` | own ship's `RELOAD(129)` relay, ammo-pack entry → `SalReloadingAmmo` + `StartReload` events (P737, ClientWorld.cs:3180-3200) → `Game1.Audio` players |
| `shipIGC.h:646-647` (fuel auto-reload succeeds) | `salReloadingFuelSound` + `startReloadSound` | same relay, fuel-pack entry → `SalReloadingFuel` + `StartReload` (P737) |

Tests: `P719StationEconomySoundTests`, `P664StationEventSoundTests`, `P735/P737/P741/P717` wave pins
(existing). Row `done` — the WeaponReady seam **is** the notification-audio seam (the P751b note's
"uses the separate IIgcSite.WeaponReady seam", now with the full caller table).

### SIM-0180 — `IgcSite::PlayVisualEffect(VisualEffectID, model, intensity)` (igc.h:4834)

All four native callers are the collision jiggle: `shipIGC.cpp:836/1006/1191/1195`
(`PlayVisualEffect(effectJiggle, this/pModel, …)` beside each `PlaySoundEffect(collisionSound)`).
Port path: the same `COLLISION_EVENT` events that drive the collision sounds feed the jiggle — when
the local ship is one of the two parties, the event's intensity goes to
`Rendering.FlightJiggle.SetJiggle` (Game1.Audio.cs:774-786 doc; `CollisionFeedback.JiggleForOwnShip`
returns 0 for a witnessed collision, so an observer's camera never shakes — the own-ship-only rule).
The FF `effectBounce` force-feedback rows are controller rumble — no port equivalent (hardware
surface, classified n/a). Row `done` for the executable visual rule.

## 3. Callback rows settled (group 3)

### SIM-0172 — `ReceiveChat` (igc.h:4792) — **done**

The IgcSite virtual's only executable override in the shipped tree is the CLIENT's
(`WinTrekClient::ReceiveChat`, trekigc.cpp:4447+; FedSrv never overrides it — the server keeps the
no-op default, which the port's `RecordingIgcSite`/ServerHost mirror). The port's client callback is
`ClientWorld.ReceiveChat(ChatMessage/ChatBuoy)` (CHATMESSAGE/CHATBUOY dispatch → event log +
captions + sound events + `ChatReceived`, P709/P738), pinned by `P709ClientCallbacksTests`.

The remainder was the native filter set (trekigc.cpp:4491-4496), defaults at
trekigc.cpp:2462-2465 + WinTrek.cpp:3314:

| Filter | Native default | Port |
|---|---|---|
| pilot mute (`ppi->GetMute()`) | — (shell `!mute`) | `IsPilotMuted` (pre-existing) |
| `m_bFilterChatsToAll` | **false** | no-op at defaults — matches |
| `m_bFilterQuickComms` | **false** | no-op at defaults — matches |
| `m_dwFilterLobbyChats` | 0 / 3 → both mean **no filtering** (the condition needs mode 1 or 2, trekigc.cpp:4494) | no-op — matches |
| `m_bFilterUnknownChats` | **true** | **implemented this wave**: a HUMAN sender's null-text sonic chat whose caption is "Unknown chat" is dropped before the event log/sound/callback (ClientWorld.ReceiveChat, both chat and buoy paths) — the `ppi->IsHuman()` gate uses the ship's `PlayerInfo.IsHuman` |

Test: `P787BChatAndReloadTextTests` (human drop, drone pass, known caption pass). The three
default-off toggles remain options-UI surface (native's registry-backed preference toggles) —
classified era/UI remainder, not a behaviour gap at defaults.

### SIM-0195 — `PostNotificationText` (igc.h:4849) — **done**

Native producers (all posted to the model's own ship only — the client shows the text solely for the
local ship, trekigc.cpp:2994-3018):
"<part> ready." (afterburner/cloak/dispenser/magazine/shield/weapon finish mounting —
afterburnerIGC.cpp:120, cloakigc.cpp:76, dispenserigc.cpp:99, magazineigc.cpp:97, shieldIGC.cpp:83,
weaponIGC.h:47) and the pool texts "Ammo depleted." / "Reloading ammo..." / "Fuel depleted." /
"Reloading fuel..." (`SetAmmo`/`SetFuel` zero transitions + auto-reload outcome,
`Igc/shipIGC.h:597-650`).

Port. The "<part> ready." producers are routed through the `IIgcSite.WeaponReady` seam and the
`BroadcastWeaponReady` transport (P707 — see the SIM-0178 table; the boarded-crew sound-only rule is
the trekigc.cpp:4975-4982 leg). The pool texts are now derived client-side from the same
client-observable state native's client sim used (`Ship.cs`'s P412/P361 outcome trackers exist but
the pure-renderer client never saw them): the own ship's wire ammo/fuel fraction positive→≈0 crossing
is the `SetAmmo(0)/SetFuel(0)` transition (shipIGC.h:609/638); a recovery above the epsilon inside
the 0.35 s grace posts "Reloading ammo..."/"Reloading fuel..." (the synchronous server reload
refills the pool — native's success leg, shipIGC.h:614-619/644-649); the grace elapsing still-empty
posts "Ammo depleted."/"Fuel depleted." (the failure leg, shipIGC.h:611-613/640-642). Implemented in
`ClientWorld.WatchPoolDepletion`/`TickPoolDepletion` (ClientWorld.Callbacks.cs), driven per frame
from `UpdateOwnStatusAlarms` under the gate. Classified `design`: native computes these on the
client's own IGC sim; the port computes them from the wire fractions per the parity rule ("if native
computes a value on the client from IGC state, compute it on the client"). The 0.35 s grace is
WeaponReloadTracker's constant — P361's reload is instant server-side, so the window never
mislabels. Tests: `P787BChatAndReloadTextTests` (depletion after the grace; recovery → reload text;
fuel variants; first-sample guard).

## 4. Explicitly untouched (per brief)

SIM-0445/0452/0453, SIM-0239, SIM-0267 (launcher reload — P787a) and SIM-0272 (slideshow sound —
P785) were not touched. Music, mixing, voice-catalog regeneration: out of scope, untouched.

## 5. Assets added (all copied verbatim from the local Artwork checkout, `~/Development/Artwork/*.ogg`)

25 weapon `pwrup` activation waves (cannon1/cannon2, dn_ptlascan1/2, dn_ptvulcan1/2, gauss,
machinegun1/2/3, minigun1/2/3, plasmarifle1/2/3, sniperlaser1/2, tf, turretcannon1/2/3,
turretgun1/2/3), the 3 turret turn tiers (sturret, mturret, lturret), and `shipint` (the station
interior loop component) — 29 files under `src/Allegiance.ClientCore.MonoGame/Content/Sounds/`,
registered in `Content/Content.mgcb`. No mixing changes; every file is the shipped wave native's own
sounddef references.

## 6. Scorecard / parity rows updated

`docs/parity/sim-rules-matrix.csv` — 15 rows updated (statuses + port cites + notes, LF endings
verified): SIM-0172/0176/0177/0178/0180/0195 (callbacks), SIM-0273/0274/0321/0327/0332/0345/0347/0348/0411
(sounds). `python3 scripts/parity-scorecard.py` validates: callbacks 98.46 % (192/195), sounds
98.91 % (544/550), TOTAL 97.74 % (1255/1284).

## 7. Verification

- Whole-solution build 0 warnings / 0 errors (Debug and Release).
- `tools/build-tools.sh`: all 73 tool projects, 0/0.
- New tests: `P787BShopHoverAndPickTests` (22, MonoGame.Tests),
  `P787BChatAndReloadTextTests` (7, ClientCore.Tests) — each **3× alone: pass 3/3**.
- Affected existing tests 3× alone: `P764UiSoundSeamTests` + `WeaponSoundMapTests` +
  `P761StartChatComposeTests` (38), `P709ClientCallbacksTests` + `P732VoiceOverTests` (45),
  `OwnShotEchoTests` + `StationAmbientSoundCurveTests` + `P664StationEventSoundTests` (24) —
  pass 3/3 each.
- One Fast-tier run (`tools/run-tests.sh --tier fast`): 10207 tests — main tiers all pass
  (MonoGame 3324, ClientCore 1755, Core 5089, Lobby 24); the only failure was the already-known
  quarantine Live flake `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return`
  (10 s limit exceeded under load, passed the tier's retry and 3/3 alone — recorded in
  `docs/TEST_FLAKES.md`; P787b touches no combat-visibility path).
