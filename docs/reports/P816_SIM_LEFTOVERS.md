# P816 — last sim-rule leftovers: ripcord-time constant, part-mounted and cargo-full sounds

Branch `pi/p816`, base public master `041e23a0`. Native reference `~/Development/Allegiance/src/`
(all cites re-verified against that tree). Artwork corpus `~/Development/Artwork` (78 static cores
found by the `IgcWrapper.VersionAndDataSize` probe the server's `LoadCore` uses,
`src/Allegiance.Server/Server.Lifecycle.cs:727`).

**Scorecard after this task** (`python3 scripts/parity-scorecard.py`): sim-rules `sounds` 100 %
(550/550 weighted, 212/212 rows), `constants` and `updates` totals as printed by the tool; the
three task rows SIM-0014 / SIM-0239 / SIM-0267 all leave `partial`.

## 1. SIM-0014 `c_fcidRipcordTime` — closed by measurement, no code change

Native chain:

- `Igc/igc.h:73` — `c_fcidRipcordTime = 13`; default `c_dtRipcord = 10.0f`
  (`Igc/missionigc.cpp:2700`, assigned at `:2754`).
- `FedSrv/FedSrv.CPP:1676` (inside `LoadDbCache`, `:1335`):
  `pdht->ripcordSpeed = ShipTypes_ripcordSpeed * g.trekCore->GetFloatConstant(c_fcidRipcordTime)`.
- `Igc/shipIGC.cpp:4664-4666` — `GetRipcordSpeed()` divides the hull field by
  `GA[c_gaRipcordTime]` only — **no second slot-13 multiply at game time**.

The decisive question was what the multiply feeds. `LoadDbCache` is not an in-game loader: it is
the **core builder**. `BuildIGCFiles` (`FedSrv.CPP:9700-9725`) calls `LoadDbCache()` and then
`DumpIGCStaticCore(..., g.trekCore, c_maskStaticTypes, ...)` — and `c_maskStaticTypes`
(`Igc/igc.h:175-188`) includes `OT_hullType` **and** `OT_constants`. So the shipped `.igc` core's
`dataHullTypeIGC.ripcordSpeed` values **are** the `ShipTypes_ripcordSpeed × FloatConstants[13]`
products, and the constants record carrying slot 13 itself is baked alongside them. At game time
both server and client load hulls straight from that core file
(`LoadIGCStaticCore` → `LoadIGCFile` → `SetStaticCore`, `Igc/missionigc.cpp:2389-2426`;
`ExportStaticIGCObjs`, the alternative DB-fed export path, is explicitly marked
"**Not being used**", `FedSrv.CPP:4398-4404`), and `GetRipcordSpeed()` never re-applies slot 13.

### Measurement

A scratch tool (`.scratch/p816measure`, deleted; summary kept) parsed every static core in the
artwork corpus through the port's own `IgcReader`/`StaticCore.Load`:

- **All 78 static cores carry `FloatConstants[13] = 10.0`** (the `c_dtRipcord` default; full
  per-core table in `P816_SIM_LEFTOVERS_assets/core_slot13_ripcord.csv`).
- Hull ripcord values are the familiar effective durations, e.g. PCore001 (447 hulls):
  fighters 10 / 8.5, scouts/bombers/interceptors 10, stealth 15, TT/destroyer/cruiser 20,
  battlecruiser/battleship 25, miners/constructors 30, carriers 60 s.

### Verdict

The port's `DataHullTypeIGC.RipcordSpeed` (`DataHullTypeIGC.cs:372`) reads the baked product and
`Ship.RipcordSpeed` divides by the side GA only (`src/Allegiance.Core/Sim/Ship.cs:1437`), which is
exactly native's in-game computation. **No code change**; applying `FloatConstants[13]` at runtime
would double-apply the build-time bake. Regression guard added:
`P816SimLeftoversTests.Ships_core_ripcord_slot_stays_at_the_native_asset_bake_default`
(`tests/Allegiance.Core.Tests/P816SimLeftoversTests.cs`). Classification: **design** (the bake is
the authored pipeline, not a bug or era constraint).

## 2. SIM-0239 part-mounted cue — magazine completion raised on the existing ready seam

Native plays `mountedSound` (+ `"%s ready."` text) when a part's `m_mountedFraction` reaches 1:
`weaponIGC.h:44-46`, `cloakigc.cpp:75-76`, `shieldIGC.cpp:82-83`, `dispenserigc.cpp:98-99`,
`afterburnerIGC.cpp:119-120` and **`magazineigc.cpp:96-97`** — all the same shape. The port already
runs one pipeline for the others (P676/P690): `Ship.AdvanceFlightMounts` raises
`IIgcSite.WeaponReady` at the completion edge → `ServerHost.WeaponReady` →
`BroadcastWeaponReady` CHATMESSAGE `VoiceOver = WeaponReadyCue.SoundId` (1023 = native
`mountedSound`, `Igc/sounds.h:34`) → client `ChatVoiceOver` → `PlayVoice` on the bundled
`unmount.ogg` (`sounddef.mdl:287`; `NativeVoiceCatalog.Generated.cs:104,661`). The magazine was the
only ready producer missing from the `AdvanceFlightMounts` predicate.

**Client-visible signal** (the P651/P674 finding): the mount-complete edge *is* observable — the
server raises `RaiseLoadoutChange(part, Mounted)` on the same completion tick (the loadout tail
crosses `FractionMounted 0 → 240`), and the ready cue rides the same edge. Change:

- `src/Allegiance.Core/Sim/Ship.cs` — `AdvanceFlightMounts` now includes
  `EquipmentType.Magazine` in the `WeaponReady` condition (cited `magazineigc.cpp:96-97`).
  Still exactly one cue per mount cycle (the loop skips parts already at 1), still suppressed for
  docked/boarded/cargo (shipIGC.cpp:266-271), and the native cloak forced-ready oddity
  (`cloakigc.cpp:68`) stays intentionally uncopied (SIM-0058).

Test: `P816SimLeftoversTests.Magazine_ready_notification_occurs_once_on_float_completion_and_again_after_remount`
(no cue before completion, exactly one at the float boundary, exactly one more after a remount) —
mirrors the P676 weapon precedent. Both HUD styles are unaffected (audio cue, no HUD surface).
Classification: **design**.

## 3. SIM-0267 cargo-full cue — pickup leg raised at the session mount picker

Native: the client answers an offered part treasure with
`pfmAck->mountID = m_ship->HitTreasure(c_tcPart, …)` (`clintlib/appmsg.cpp:270-275`); when the
`HitTreasure` search (mount pass → cargo pass, `Igc/shipIGC.h:1257-1301`) finds no room at all it
plays `salCargoFullSound` **instead of** `pickUpPartSound` (which lives on the success path only,
`shipIGC.h:1307`) and returns `c_mountNA` (`shipIGC.h:1302-1304`) — the server then drops the part
back into space as a fresh treasure (`FedSrv.CPP:6125-6130`). The swap leg
(`WinTrek.cpp:9114`) was already ported by P719 (`Game1.FlightParts.cs:239`).

The port's `HitTreasure` search is `TreasureMountPicker.PickMount` (P758), called from
`ConnectionSession.PartOfferMount` in the receive loop. Changes:

- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — `PartOfferMount` now reports whether
  the search itself ran and found nothing (`mount == MountNA` with hull/part/ship resolved;
  unresolvable-offer declines stay silent); on that outcome it calls
  `ClientWorld.RecordPartOfferCargoFull()` **before** applying the same `ACQUIRE_TREASURE`, then
  still answers `c_mountNA` so the server re-creates the treasure.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `RecordPartOfferCargoFull()` records
  `ClientSoundEventKind.CargoFull` (new enum member, wave `vo_sal_cargofull`, sounddef.mdl:810)
  and one-shot-suppresses the P735 `PickupPart` cue for the immediately following part offer
  (native plays cargo-full *instead of* the pickup cue; the flag is consumed by the next apply
  regardless of code, so it cannot leak).
- `src/Allegiance.ClientCore.Game/Game1.Audio.cs` — drains `CargoFull` →
  `SoundManager.PlayCargoFull()` (P719, already loaded from the bundled
  `Sounds/vo_sal_cargofull.ogg`).

Asset note: the P787b row's `ogg_present=false` was stale — P719 already bundled the wave and
registered it in `Content/Content.mgcb` (`#begin Sounds/vo_sal_cargofull.ogg`); the bundled file is
byte-identical to `~/Development/Artwork/vo_sal_cargofull.ogg`
(md5 `3d6bf0e63176e1c084a3c1a9693e9522`), so nothing was copied in this task. No media review
needed (no new media).

Tests (`tests/Allegiance.ClientCore.Tests/P816CargoFullCueTests.cs`, 3× alone green):
declined offer records `CargoFull` (ship-id-tagged) and **not** `PickupPart`; accepted offer keeps
`PickupPart` with no `CargoFull`; the decline flag cannot leak past a non-part offer. The picker's
full-ship decline itself remains covered by
`TreasureMountPickerTests.No_mount_and_full_cargo_declines_with_MountNA`. Classification:
**design**.

## Parity rows

- `docs/parity/sim-rules-matrix.csv` — SIM-0014 `partial → implemented` (measurement-closed, no
  code; evidence table linked), SIM-0239 `partial → implemented`, SIM-0267
  `partial → implemented`. `docs/parity/p650-core-constants.csv` row 13 note updated to match.
  LF endings preserved (`grep -c $'\r'` = 0); `python3 scripts/parity-scorecard.py` validates.

## Verification

- `~/.dotnet/dotnet build AllegianceCore.slnx` — 0 warnings / 0 errors.
- `tools/build-tools.sh` — all 76 tool projects built, 0/0.
- New/affected tests 3× alone: `P816SimLeftoversTests` (2), `P816CargoFullCueTests` (3),
  and the affected classes `P676SimRulesTests`+`P690EquipmentReadinessTests`+
  `P651EquipmentRulesTests` (77) and `P758AcquireTreasureClientTests`/`P758 wire`+
  `ClientWorldSoundEventTests`+`TreasureMountPickerTests`+`P735*` (99) — all passed 3× each; none
  failed on master (SIM-0239's magazine cue and SIM-0267's pickup cue did not exist there).
- ONE fast-tier run: `tools/run-tests.sh --tier fast` — 10356 tests. Two failures, both outside this
  task's paths and both ledgered load flakes: `P802TeamCivLiveTests.Random_change_...`
  (TEST_FLAKES.md row 2745) and
  `DockedStationUpdateLiveTests.Docked_player_receives_seen_own_station_update_within_native_cadence`
  (row 2743) — each passed 3/3 alone immediately after the run; the flagged SLOW FAST
  `P750NativeAsteroidTypesTests` (10.574 s) is row 2744. No task test failed anywhere.
- Raw measurement log kept out of the repo (evidence rule); only the 79-line summary CSV is
  committed under `docs/reports/P816_SIM_LEFTOVERS_assets/`.
