# BOOSTER_SOUND_GATE_P1003 — the afterburner loop is a state loop, not a button echo

**Task:** v0.1.6 public-tester feedback (2026-11-13, veteran native player):
*"press shift in a pod and you hear the boost, no actual boost."*

**Classification:** **port bug** (behaviour parity). Native gates the afterburner loop on a mounted
`ET_Afterburner` part *and* that part's power ramp; the port played it whenever the booster button bit was
set in the local ship's `StateM`. Not a native design choice and not an era constraint — the native rule is
in the same `UpdateSounds` pass the port already ports.

**Base:** `a87b00f3` (v0.1.6 + P1002). Branch `hermes/p1003`. No commit, no push: changes are in the
working tree.

---

## 1. Finding

| Case (tester) | Native | Port before P1003 | Verdict |
|---|---|---|---|
| Pod, Shift held | Silent (no parts → `GetMountedPart` NULL → sound id `NA`, `AfterburnerPower()` 0) | Played the `iafter` boost loop | **bug** |
| Fighter, tank empty, Shift held | Silent (activation requires `fuel != 0`; only the "out of fuel" VO fires) | Played the boost loop | **bug** |
| Fighter burning | Loops while `GetPower() > 0`, decaying to silence on release | Loop from the button bit; a fixed `iafter` asset for every part | **bug** (asset wrong for most parts) |
| Turret gunner | Carrier's beds (interior + exterior) follow the carrier's power | Nothing is played for a gunner at all — no wrong cue, a **gap** (§5) | gap recorded |
| Other ships' exterior boost | Audible 3-D `xafter` per ship (`UpdateSounds` runs per sound object) | No per-ship afterburner bed exists | gap recorded (§5) |

## 2. Native intent (re-grepped, read-only `/srv/corpus/Allegiance/src`)

```cpp
// WinTrek/trekigc.cpp:820-825
float AfterburnerPower()
{
    IafterburnerIGC* pafterburner =
        (IafterburnerIGC*)(m_pship->GetMountedPart(ET_Afterburner, 0));
    return pafterburner ? pafterburner->GetPower() : 0.0f;
}

// WinTrek/trekigc.cpp:1115-1130
SoundID GetAfterburnerSoundID(bool fInterior)
{
    IafterburnerIGC* pafterburner =
        (IafterburnerIGC*)(m_pship->GetMountedPart(ET_Afterburner, 0));
    if (pafterburner)
        return fInterior ? pafterburner->GetInteriorSound()
                         : pafterburner->GetExteriorSound();
    return NA;
}

// WinTrek/trekigc.cpp:1320-1322 (interior, inside the bIsPlayersShip block)
PlaySoundIf(m_pAfterburnerInteriorSound,
            GetAfterburnerSoundID(true),
            m_psourceEngine, AfterburnerPower() > 0.0f);

// WinTrek/trekigc.cpp:1344-1347 (exterior, for every ship)
PlaySoundIf(m_pAfterburnerExteriorSound,
            GetAfterburnerSoundID(false),
            m_psourceEngine, AfterburnerPower() > 0.0f);
```

- `PlaySoundIf` (`trekigc.cpp:930-951`) is a **state loop**: start if the slot is empty, stop and clear it
  otherwise — no restart while held.
- The power ramp itself: `CafterburnerIGC::IncrementalUpdate` (`Igc/afterburnerIGC.cpp:103-164`);
  activation needs *button held AND `fuel != 0`* (`:129`); `m_power` rises at `onRate` (`:137`), decays at
  `offRate` (`:143`), saturates at 1, and deactivates at 0 (`:156-158` "out of gas"). `GetPower()` is
  `m_power` (`Igc/afterburnerIGC.h:130-133`, field `:177`). So power > 0 already encodes mount gate, tank
  and ramp; the button bit alone encodes none of them.
- Per-mount sound ids: `DataAfterburnerTypeIGC`'s `InteriorSound` / `ExteriorSound` (`Igc/igc.h:1865`).
  sounddef (`Artwork/sounddef.mdl:1323-1327,2178-2180`): `afterburner1InSound` = 680 (`iafter`),
  `afterburner1OutSound` = 700 (`xafter`), plus the civilizations' own boost waves
  `dn_Aft{Ion,Lgt,Crs,Hvy,Hpr}In/OutSound` = 514/515/516/518/519 in, 529 out.
- Camera view gates only the **volume bus**: `SetInternalVolume` / `SetExternalVolume`
  (`trekigc.cpp:887-927`) and the camera switch at `:1359-1372` (`-100` dB on the wrong bus). Both loops keep
  running across a camera change.
- The empty tank case is explicitly a VO, not a loop: `TryingToFireAfterburnersWithoutFuel`
  (`trekigc.cpp:866-874`) drives `salNoFuelSound` (`:829-845`), which the port already has (SIM-0364/0366,
  `ClientWorld.UpdateOwnStatusAlarms`).

## 3. Root cause (port, file:line)

1. **The gate.** `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs:1172-1176` (pre-change) played the
   bed on `afterburnerActive`, and the only caller
   `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:2907` built it as
   `(mineUp.StateM & ShipControlState.AfterburnerButton) != 0` — the button bit. Pods have no parts at all
   since P998 (`docs/reports/DEAD_SHIP_PARTS_P998.md`), so `GetMountedPart(ET_Afterburner, 0)` is NULL for a
   pod, and nothing in the chain knew it.
2. **The asset.** `SoundManager.Load` created one fixed `_afterburner = Loop("Sounds/iafter")`
   (`:156`, `:930`), i.e. the retail *generic* interior wave. PCore001's fourteen afterburner parts carry six
   **different** interior ids (514/515/516/518/519/680 — see §4), so every civilization but one boosted with
   another civ's wave, and the chase view heard the interior asset because there was no exterior instance.
3. **The port's `AfterburnerPower()`.** The client's own ship is simulated locally
   (`src/Allegiance.ClientCore/Flight/OwnShipSimulator.cs`; `StepAfterburner` `:140-162` implements exactly
   `CafterburnerIGC::IncrementalUpdate`'s mount/on/off/fuel rules) and already exposes that ramp as
   `OwnShipSimulator.Power` (`:120-124`) — the same value native's client reads from its own `m_pship`.
   The server copy lives in `src/Allegiance.Core/Sim/Ship.cs` `UpdateAfterburner` (`:9493-9554`, field
   `_afterburnerPower` `:9426`). Nothing on the sound path could reach either.
   *Separate, recorded divergence (not fixed here):* native ships export the mounted part's power in the
   ship update's `power` byte (`Igc/shipIGC.h:1136-1138` `SetOVTP`) and import it
   (`Igc/shipIGC.cpp:3076-3081` `GetOVTP`); the port hardcodes `power = 1.0` in
   `src/Allegiance.Core/Wire/ServerShipUpdate.cs:176`. No consumer needed it for this fix (the own-ship
   source is the local sim), and changing the wire byte would move every captured goldens' byte 46 — left
   for its own task (§7).

## 4. The fix

**`src/Allegiance.ClientCore.Game/Audio/AfterburnerLoop.cs` (new)** — the native loop state as pure data:
`Mounted`, `Power`, `InteriorSoundId`, `ExteriorSoundId`; `InteriorId`/`ExteriorId` are native's
`GetAfterburnerSoundID` (`NA` without a part) and `Plays` is `Mounted && Power > 0f`. Doc block carries the
native cites; no MonoGame types, so it is testable without a device.

**`SoundManager.Update`** (`src/Allegiance.ClientCore.Game/Audio/SoundManager.cs:1183-1196`) takes the
`AfterburnerLoop` instead of `bool afterburnerActive` and maintains **two** `PlaySoundIf` slots — native's
`m_pAfterburnerInteriorSound` / `m_pAfterburnerExteriorSound` (`trekigc.cpp:755,762`) — starting each with
the **mounted part's own SoundID** through the existing numeric dispatch (`StartSound` → sounddef template →
ASR loop range → bundled wave), and muting the wrong bus by camera view (`SetSlotVolume`, `:2915-2921`,
native `SetInternalVolume`/`SetExternalVolume`). The fixed `iafter` instance is gone (`:937`), and both slots
are reset on dispose.

**Caller** (`src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:2903-2922`):
`Mounted = OwnShipLoadoutHasAfterburner(mine)` (granted loadout, lifepod hull always false — the P998 rule),
`Power = _ownSim.Power` (the port's `AfterburnerPower()`), and the part's `InteriorSound`/`ExteriorSound` via
`_ownBoostType`, kept by `ConfigureOwnBooster`
(`src/Allegiance.ClientCore.Game/Game1.Prediction.cs:38-45,51-60`) — it runs every frame before the sound
pass, from the same loadout scan that configures the sim.

**Content.** The six waves those ids name were not bundled (only `iafter` was), so the corrected beds would
have been silent: `xafter`, `dn_ptaftion`, `dn_ptaftlgt`, `dn_ptaftcrs`, `dn_ptafthvy`, `dn_ptafthpr` were
copied from the artwork corpus into `src/Allegiance.ClientCore.MonoGame/Content/Sounds/` and registered in
`Content.mgcb` (264 KB total, same provenance as the 656 waves already bundled; **new media — needs
Domingo's review like any new asset**). The content pipeline builds them for both the game and the test
output (`Content/bin/DesktopGL/Content/Sounds/*.xnb`).

Retail core mapping proven by the tests (PCore001 = PCore002 = PCore003, 14 afterburner parts):

| part interior id | template | wave | exterior id | template | wave |
|---|---|---|---|---|---|
| 514 | dn_AftIonInSound | dn_ptaftion | 700 | afterburner1OutSound | xafter |
| 515 | dn_AftLgtInSound | dn_ptaftlgt | 529 | dn_AftHprOutSound | dn_ptafthpr |
| 516 | dn_AftCrsInSound | dn_ptaftcrs | | | |
| 518 | dn_AftHvyInSound | dn_ptafthvy | | | |
| 519 | dn_AftHprInSound | dn_ptafthpr | | | |
| 680 | afterburner1InSound | iafter | | | |

## 5. The rest of the audit

- **Fuel empty on a fighter** — fixed by the same gate: the ramp never leaves 0 while the tank is empty
  (`afterburnerIGC.cpp:129`), so both beds stay stopped and the existing `salNoFuel` VO is the only cue.
  Covered by `Fighter_with_empty_tank_and_button_held_plays_no_loop`.
- **Turret gunner — gap, not a wrong cue.** Native's `UpdateSounds` runs per ship; for a gunner
  `bIsPlayersShip` is true for the CARRIER's sound object (`trekigc.cpp:1218,1235`), so the carrier's
  interior/exterior boost beds follow the carrier's power. The port skips the whole own-ship sound pass for
  a boarded gunner (`Game1.FlightInput.cs:2746-2760` → the gunner takes the carrier-pose branch), and the
  carrier's power is not on the wire (hardcoded `power = 1.0`, §3.3). Playing something there would repeat the
  same mistake (button bit without power), so it stays silent and is recorded as a gap for the wire-power
  task.
- **Exterior / other-ship boost — gap.** The own ship's exterior bed is now correct (per-part `ExteriorSound`,
  chase view only). The port has no per-ship sound objects for remote ships, so native's 3-D `xafter` from
  other ships is not played at all. Again a missing bed, not a spurious one; recorded in SIM-0381's notes.
- **Sidethrust bed — correct as ported.** Native's predicate is the button state itself
  (`PlaySidethrust()` = `backward|left|right`, `trekigc.cpp:953-956`), and
  `Game1.FlightInput.cs:2871-2874` passes exactly those bits. No change.
- **Engine / main-thruster bed — natively shaped, not a button bit.** Native gates on
  `m_fThrustSoundLevel > 0.01f` with `ForwardThrustFraction()` = throttle (plus full value while the booster
  button is held) (`trekigc.cpp:778-790,1135-1150`); the port's `EngineSoundCurve.Mix(throttle, speed, max)`
  is throttle/speed-driven in the same spirit, with a documented 0.10 idle floor (a stated port adaptation,
  `Audio/EngineSoundCurve.cs`) and no button-bit gate. Left as is; no false "boost" cue.
- **Missile-lock bed — correct.** `world.MissileLock.IsFullLock` is `IsReady && LockFraction >= 1`
  (`src/Allegiance.ClientCore/State/MissileLockState.cs:39`) = native's `HasMissileLock() == GetLock()==1.0`
  (`trekigc.cpp:876-883`). No change.

## 6. Tests

New: `tests/Allegiance.ClientCore.MonoGame.Tests/P1003BoosterSoundGateTests.cs` (11 cases; Fast tier, no
device needed — the power comes from the production `OwnShipSimulator`, the gate from `AfterburnerLoop`).

- **pod + booster held → no loop** (`Pod_with_booster_held_plays_no_afterburner_loop`): no part mounted,
  `OwnShipSimulator.Power == 0`, `Plays == false`, both ids `NA`.
- **fighter with empty tank → no loop** (`Fighter_with_empty_tank_and_button_held_plays_no_loop`): part
  mounted, power stays 0, ids resolve but `Plays == false`.
- **fighter burning → loop** (`Fighter_burning_plays_the_loop_with_the_parts_own_sounds`): power > 0, ids
  680/700 → `iafter`/`xafter`.
- `Gate_requires_mounted_part_and_positive_power` (stale power without a part; any positive ramp plays),
  `Released_boost_decays_to_silence`, `Loop_slots_start_only_while_the_part_burns_and_stop_on_release`
  (native `PlaySoundIf` lifecycle), `Production_call_site_passes_mounted_state_and_real_power` (source pin:
  `Mounted: ownAfterburner` + `Power: _ownSim.Power`, and no `afterburnerActive:` left),
  `Retail_core_afterburner_parts_resolve_to_their_own_bundled_beds` (real PCore001: the six ids, each
  playable/loopable/bundled, and a 518 part playing `dn_ptafthvy` + `xafter`).
- Touched existing: `P902SoundTemplateTests` (its inert-device smoke loop now passes a real `AfterburnerLoop`);
  `P721ShipLoopBedsTests` and `SoundAssetManifestTests` re-run unchanged.

Counts (each filter, 3× alone, `dotnet test --no-build`):

| run | P1003BoosterSoundGateTests | P902+P721+SoundAssetManifest |
|---|---|---|
| 1 | Passed 11 / Failed 0 / Skipped 0 | Passed 50 / Failed 0 / Skipped 0 |
| 2 | Passed 11 / Failed 0 / Skipped 0 | Passed 50 / Failed 0 / Skipped 0 |
| 3 | Passed 11 / Failed 0 / Skipped 0 | Passed 50 / Failed 0 / Skipped 0 |

No HUD/UI element changed (audio only), so no Classic/Modern captures are required; `HudFrame.Power` is
untouched.

## 7. Gate

- `dotnet build AllegianceCore.slnx` (`--disable-build-servers -nodeReuse:false
  -p:UseSharedCompilation=false`): **0 warnings, 0 errors**.
- `tools/build-tools.sh`: **PASS** — all 137 tool projects built (after updating the one
  `tools/p902-audio-proof` smoke call that passed the old `bool` argument).
- One `tools/run-tests.sh --tier fast --no-build` (results:
  `~/allegiance-evidence/P1003/fast-20261113/`): **14,179 passed / 8 failed / 20 skipped** of
  14,207 cases in 543.7 s; 9/9 infrastructure checks and both quarantine lanes passed. The
  runner reports FAIL **only** on ledgered, load-driven families (host load 12.9–16.9 on the
  shared 24-CPU host; P744 calibration factor 1.156 → 11.559 s Fast limit) and four
  timing-only offenders. Every failed case was re-run 3× alone: six pass 3/3
  (`P902` allocation counter, `WorldPopulation`, `WarpTransit`, `GameOverPlayersWire`,
  `BallotVoting`, `AsteroidMined`), and `P739TimerLiveTests` keeps its pre-existing
  one-ULP float-boundary signature (`7.0000000000000009` outside `(6.9, 7]`). Nothing on
  the afterburner sound path failed. Full ledger: `docs/TEST_FLAKES.md`
  (“2026-11-13 — P1003 Fast qualification”).
- Parity: `docs/parity/sim-rules-matrix.csv` SIM-0375 (interior) and SIM-0381 (exterior) rewritten;
  `scripts/parity-scorecard.py` passes (860 sim rows validated). `tools/check-glossary-links.py` passes.

**Fast-suite numbers for this change's own tests** (each filter, 3× alone): P1003
11 passed / 0 failed / 0 skipped every run (85–92 ms); the touched
`P902`+`P721`+`SoundAssetManifest` filters 50 passed / 0 failed / 0 skipped every run.

**Known gap left open (needs its own task, recorded):** the wire's `ServerShipUpdate.power` byte is still a
constant 1.0 while native exports/imports the mounted afterburner's `GetPower()`
(`Igc/shipIGC.h:1136-1138`, `Igc/shipIGC.cpp:3076-3081`). Consumers that read it today are the remote
dead-reckoner (`ShipDeadReckoner.Seed`, `AfterburnerPower: update.Power`), the P449b cockpit jiggle
(`Game1.Rendering.cs:509-512` still documents the "no ramp exists" premise, now false — native uses
`afterburner->GetPower()`, `WinTrek/WinTrek.cpp:390`) and the HUD power gauge
(`Game1.Rendering.cs:3423`). Fixing the byte would make the gunner carrier and remote beds possible and the
jiggle/gauge native-exact; it was out of this bug's path and would move every wire golden's byte 46.

---

**Native intent.** The boost sound is a *state* loop of the mounted afterburner part: it plays only while that
part has power above zero (which itself requires a mounted part, a non-empty tank and the held button), uses
the part type's own interior/exterior SoundIDs, and is muted — never stopped — by the camera bus.

**Modern approach.** The same rule lives in one small pure record (`AfterburnerLoop`) fed by the client's own
flight simulation (`OwnShipSimulator.Power`), driven through the already-ported native `PlaySoundIf` slot
machinery and the numeric SoundID dispatch, with the six retail boost waves bundled so every civilization
hears its own booster.
