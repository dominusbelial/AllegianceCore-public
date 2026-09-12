# P116 — Wire the real land-on-carrier trigger (detection only)

Branch `master`, repo `/home/domingo/Development/AllegianceCore`.

Goal: wire P114's isolated `Ship.InGarage` / `HullLandingBay.Reconstruct` into a
real per-tick `Sector` pass that detects a genuine carrier landing against real
corpus `.cvh` geometry — and deliberately stop before the native's actual effect
(resupply / loadout / launch-slot reposition / `RELAUNCH_SHIP` broadcast). This
is this arc's P112: real trigger, proven detection, nothing else changed.

---

## 1. The atomic-function / no-latch finding — CONFIRMED by reading native code

Task demanded I confirm the summary myself, not on faith. Reading
`src/FedSrv/FedSrv.CPP:12814-13031` (`FedSrvSiteBase::LandOnCarrierEvent`) in
full:

```
12814  bool FedSrvSiteBase::LandOnCarrierEvent(...)
...
12822  // refill free, partially-gone expendables (RefillPart backwards)
12827  if (pfsShip->IsPlayer()) {
12829    ptDesired = pfsShip->GetPlayer()->GetDesiredLoadout();   // <-- preference system
...
12867    CreateAndAddPart / Upgrade (GetSuccessorPartType)          // auto-loadout + upgrade
...
12990  pship->SetAmmo(SHRT_MAX); pship->SetFuel(FLT_MAX);
12991  pship->SetEnergy(max);      pship->SetFraction(1.0f); shield full
...
12999  const Orientation& orientation = pshipCarrier->GetOrientation();
13001  float vLaunch = ... c_fcidExitStationSpeed;
13003  position = pshipCarrier->GetPosition(); velocity = carrier velocity (+ jitter)
...
13009  if (phtCarrier->GetLaunchSlots() == 0) { position -= back*(r+r+vLaunch*0.5f) ... }
13010  else { slot = GetLaunchSlot(); position += GetLaunchPosition(slot)*orientation;
13012         forward = GetLaunchDirection(slot)*orientation; position += forward*(r+vLaunch*0.5f);
13013         velocity += forward*vLaunch; TurnTo(forward); }
13018  pship->SetPosition(position); pship->SetVelocity(velocity); pship->SetOrientation(...);
13021  RecalculateCollisions(tCollision, pship, NULL);
13023  BEGIN_PFM_CREATE(... RELAUNCH_SHIP) ... pfmLaunch->shipID/carrierID/position/velocity/orientation/cookie
13030  SendMessages(GetGroupSectorFlying(...))
```

Confirmation points:
- It is ONE atomic function: detect happens BEFORE the call (the `InGarage` gate
  in `HandleCollision`); the function body then does resupply → loadout →
  reposition → broadcast in the same call and returns. There is **no persistent
  "ship is docked at carrier" state anywhere** — grep the whole function: no
  latch, no flag, no member that records "landed".
- It ends by **moving the ship out of the bay** (`SetPosition` to a launch slot /
  behind the carrier). That is exactly why a stationary lander re-fires each tick
  in this detect-only slice (nothing moves it yet) and why P117 naturally ends
  the condition: relaunch moves the ship out, so next tick `InGarage` is false.
- The reposition consumes P114's decoded geometry directly:
  `GetLaunchSlots()`, `GetLaunchPosition(slot)`, `GetLaunchDirection(slot)`
  (`FedSrv.CPP:13009-13013`) — the very fields `HullLandingBay.LaunchSlots`
  reconstructs from `launchN` frames. So P117 inherits the slot geometry P114
  decoded but P116 does not use.
- `RELAUNCH_SHIP` is message id 192, sector-wide via `GetGroupSectorFlying`.

**Testing the finding (case 5):** `CarrierLandingTriggerTests.Stationary_lander_is_detected_every_consecutive_tick_no_latch`
holds a real lander stationary inside a real bay across 25 consecutive `Sector.Update`s
and asserts the drained list is non-empty on **every** tick (25/25), with no latch
or cooldown suppressing repeat detection. This is the no-latch consequence of the
atomic-function finding, made explicit and tested rather than assumed.

---

## 2. Scope implemented

1. **Production `HullLandingBay` cache/resolver** — `src/Allegiance.Core/Collision/HullLandingBayCache.cs`.
   Keyed by hull `ModelName`; parses the carrier's `.cvh` once (via
   `CvhReader.Parse` + `HullLandingBay.Reconstruct`, P114) and caches the
   reconstructed `HullLandingBayGeometry` per distinct carrier hull, so frames are
   never reparsed/re-scanned per tick — the native `HullTypeIGC::Initialize` +
   `HitTest::Load(modelName)` once-per-hull-type convention
   (`hullTypeIGC.cpp:56,158-212`). No lock: runs on the sim thread only.
2. **Per-tick pass** — `Sector.ResolveCarrierLandings()`, called from
   `Sector.Update` right after the P115 rescue pass. Same-shape as the dock/rescue
   passes (per-tick `Sector` predicate sweep, same-side gate), but runs the **real
   geometric `Ship.InGarage`** against the cached bay — not a sphere proximity
   shortcut (InGarage is the whole point). Detection-only: each true pair is
   accumulated and drained (`Sector.DrainCarrierLandings()`), and the host logs it
   (`[carrier] LAND-ON-CARRIER detected: …`) with this project's tagged style
   (`[rescue]`/`[ripcord]` precedent). No ship state change, no resupply, no
   reposition, no broadcast.
3. **Server wiring** — `Server.InstantiateGames` builds one shared
   `HullLandingBayCache` whose loader resolves `<artPath>/<modelName>.cvh` (the
   server's configured asset root — see §3) and attaches it to every sector;
   `Server.SimPass` drains `DrainCarrierLandings()` per sector and logs each
   detection (no wire — Core never sends wire; the real `RELAUNCH_SHIP` send is
   P117).

Verified by **6 unit tests** in
`tests/Allegiance.Core.Tests/CarrierLandingTriggerTests.cs`, all against real
corpus data (real `faohshcar` hull record + real `faohshcar.cvh` bay, real
`dreghpod` lander, P114's hand-derived inside/outside coordinates):

| Case | Result |
|---|---|
| 1. Real lander inside real bay | detection fires; **no ship state changed** (position/velocity/energy/ammo/fraction/loadout/parts/dock-latch snapshots identical) |
| 2. Same pair, opposite sides | no detection (same-side gate) |
| 3. Lander missing `c_habmLandOnCarrier` | no detection (capability gate) |
| 4. Lander outside every bay half-space (below deck / out of y-band / far in x) | no detection (real geometry, not proximity) |
| 5. Stationary lander, 25 consecutive ticks | detected **every** tick (no-latch finding) |
| + Production lookup path | the exact `artPath`-root loader (Server.cs) resolves real faohshcar bay and fires |

---

## 3. Which production `.cvh` lookup path I used, and why

`ServerConfig.ArtPath` (default `/home/domingo/Development/Artwork`,
`ServerConfig.cs:43`) — the same asset-root convention the server already uses to
load its static core and maps (`Path.Combine(_config.ArtPath, _config.StaticCore)`,
`Server.cs:245`; `Path.Combine(_config.ArtPath, g.Map)`, `Server.cs:336`). All
corpus `.cvh` files sit flat at that root (verified: 0 nested under Artwork), and
`faohshcar.cvh` resolves there.

I confirmed the test-only `CvhCorpus.Files` (`tests/.../CvhCorpus.cs`) is **not** a
production path: it re-scans two developer trees (`/home/domingo/Development/Artwork`
and `/home/domingo/Development/Allegiance`) on every access and lives only in the
test project. Grep confirms **no `.cvh` is loaded anywhere in `src/`** before this
change, so there was no pre-existing production corpus loader to reuse; the only
established production file-resolution convention is `ArtPath`. The cache therefore
takes an injectable loader (`Func<string, CvhFile?>`); the host supplies the
ArtPath one, tests supply the real-corpus `CvhCorpus` twin or the verbatim
ArtPath loader (both are exercised). This keeps Core I/O-free (consistent with the
rest of the Core, which never does file IO) while giving production real,
cached-once geometry.

Also confirmed: `RELAUNCH_SHIP`(192) has **no wire codec** in `src/Allegiance.Core/Wire/`
— it appears only as a message-name label in `FedMessageDumper.cs:47`. Left alone,
as scoped, for whichever task sends it.

---

## 4. Out of scope (unchanged)

- `LandOnCarrierEvent`'s real resupply / auto-loadout / part-upgrade and the
  `CFSPlayer::GetDesiredLoadout()` preference system — confirmed a real, separate
  concern (`FedSrv.CPP:12829,12850-12900`); not modelled in this port.
- Launch-slot repositioning + `RELAUNCH_SHIP`(192) broadcast/codec — no codec
  exists yet (confirmed above); P117's job.
- Any latch/cooldown/debounce — the atomic-function finding (§1) shows none is
  wanted at this stage; case 5 tests that repeat detection is correct and expected.
- `RescueShipEvent`/`PLAYER_RESCUED` (P115) and P110-P115 ripcord/teleport/dock
  code — read only for pattern precedent.

---

## 5. Next task (this slice's P113)

**P117 — `LandOnCarrierEvent`'s real effect.** Consume
`Sector.DrainCarrierLandings()` and perform the native function's body:
(1) refill depleted expendables; (2) auto-buy/upgrade the player's desired loadout
(a new `GetDesiredLoadout()` preference system); (3) reposition the lander at a
launch slot using `HullLandingBay.LaunchSlots` (already decoded by P114, unused
until now) — `GetLaunchSlots`/`GetLaunchPosition`/`GetLaunchDirection`
(`FedSrv.CPP:13009-13013`), scaling launch dirs with the carrier's orientation,
exit-speed margin `c_fcidExitStationSpeed`; (4) reset ammo/fuel/energy/fraction;
(5) broadcast `RELAUNCH_SHIP`(192) sector-wide (needs the new wire codec). This is
what naturally ends P116's repeat detection: relaunching moves the ship out of the
bay, so `InGarage` goes false the following tick.

---

## 6. Build / test / commit

- Verified 3 consecutive clean full-suite runs: **2511 passed, 0 failed** each
  (baseline 2505+ going in, per P115; +6 from this task = 2511). The known
  pre-existing `LiveChatSectorWireTests` timing flake fired once and was rerun
  clean; `LiveCombatWireTests` did not fire. Neither is a regression.
- Committed to `master` and pushed to `origin/master` (this repo's own convention).

## 7. Session stats

Logged via `python3 ~/Development/allegiance-tools/session_stats.py <session_id>`.
