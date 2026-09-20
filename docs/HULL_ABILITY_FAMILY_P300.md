# P300 — `c_habmCaptureThreat` + `c_habmMiner`: the last two missing `HullAbilityBitMask` bits

Follow-on to **P299** (which landed `c_habmThreatToStation`, the 15th bit).
This closes the final two gaps found in the round-4 family-completeness pass
(`allegiance-missing-arcs-round2.md`): `c_habmCaptureThreat` (0x08) and
`c_habmMiner` (0x4000). Both had **zero references** in the port before this
task.

**Result in one sentence:** both bits are real, both are genuinely populated on
real hulls in the shipped corpus, both have real native consumers — but in both
cases that consumer is a system this port does not have yet, so **neither gets
behavioral wiring**; each is decoded as a named constant for family completeness
and its native consumer is named as a separate future prerequisite. No
fabricated integration point.

---

## Native's real consumer for each bit (cited, not guessed)

Native's canonical 16-bit `HullAbilityBitMask` list is enumerated by its own
debug CSV-dump routine (`Igc/missionigc.cpp:5078-5108`). Both bits are *set*
during hull decode from the hull's `Ability` capability strings
(`Igc/missionigc.cpp:226` for `CanCapture` → `c_habmCaptureThreat`;
`Igc/missionigc.cpp:248` for `IsMiner` → `c_habmMiner`).

### `c_habmCaptureThreat = 0x08` (`Igc/igc.h:729`)

Its **single direct consumer** is the asset-mask classifier `GetAssetMask`
(`Igc/igc.h:5361-5412`). For a hostile ship whose pilot type is not one of the
special-cased layer/builder/miner/carrier types, it reads the hull's
capabilities (`igc.h:5382-5384`):

```cpp
HullAbilityBitMask habm = pht->GetCapabilities();
if (habm & (c_habmBoard | c_habmCaptureThreat))
    am = (habm & c_habmFighter)
         ? (c_amEnemyFighter | c_amEnemyAPC)
         : (c_amEnemyCapital | c_amEnemyAPC);
else if (habm & c_habmThreatToStation)
    am = (habm & c_habmFighter)
         ? (c_amEnemyFighter | c_amEnemyBomber)
         : (c_amEnemyCapital | c_amEnemyBomber);
...
```

So a hull carrying `c_habmCaptureThreat` is branded an **`c_amEnemyAPC`**
(station-assault / troop-transport class) in the asset-mask universe. That mask
then feeds **two downstream systems**:

1. **Server-side high-value-target spotting scoring** — `FedSrv.CPP:10957`
   computes `GetAssetMask(pSpottedShip, …, false)` when a side first sees a
   ship, and if the mask overlaps the high-value mask (which includes
   `c_amEnemyAPC`) it awards `AddTargetSpot()` score / Steam achievements to the
   spotting player/probe-launcher.
2. **Client-side cluster-warning system** — each cluster aggregates an asset
   mask over its contents (`WinTrek/trekigc.cpp:410`), and `GetClusterWarning`
   (`Igc/common.cpp:2490`) turns `c_amEnemyAPC` + a station (when stations are
   vulnerable) into `c_cwStationCaptureThreat` (igc.h:4383), surfaced in the
   sector-map HUD (`WinTrek/sectormap.cpp:523,1056,1449`) and as text/sound
   alerts (`WinTrek/trekigc.cpp:333-359`).

### `c_habmMiner = 0x4000` (`Igc/igc.h:740`)

Its real consumers are **exclusively the client's target-selection commands**
(`TrekFindTarget` with `c_habmMiner` in the filter mask):

- `WinTrek/WinTrek.cpp:10182` — `TK_TargetEnemyMinerNearest` →
  `TrekFindTarget(..., c_habmMiner)` (“target nearest enemy miner”).
- `WinTrek/WinTrek.cpp:10315` — `TK_TargetFriendlyMinerNearest` →
  `TrekFindTarget(..., c_habmMiner)` (“target nearest friendly miner”).
- `WinTrek/WinTrek.cpp:10122,10158` — helper masks `c_habmRescue |
  c_habmMiner | c_habmBuilder` for enemy target cycling.
- `WinTrek/WinTrek.cpp:8096` and `WinTrek/trekigc.cpp:2958` — the miner/builder
  pair used as a default-target preference when a rescue-filtered search finds
  nothing.

This is a *hull*-level capability flag, deliberately distinct from the
`c_ptMiner` **pilot** type (which round-2 of the research already confirmed is
correctly handled at `Ship.cs:558-575`). Native never gates mining eligibility
on `c_habmMiner` — the grep shows no server-side mining gate reads it at all.

---

## Wiring decision: both SKIPPED (decoded, not wired) — and why

Per the task's scope rule, wiring happens only where this port already has (or
plausibly should have) an equivalent system. Neither consumer system exists
here:

| Bit | Native consumer | Port-side equivalent? | Verdict |
|---|---|---|---|
| `c_habmCaptureThreat` | asset-mask → spotting score (`FedSrv.CPP:10957`) + cluster-warning display (`common.cpp:2490`, sector map) | **None.** No `AssetMask`/`GetAssetMask`/`GetClusterWarning`/`AddTargetSpot`/achievement anywhere. The port's visibility sweep `Sector.ResolveCrossSideVisibility` is a pure per-object flag-flipper whose doc comment explicitly declares these out of scope (`Sector.cs:2677-2682`, “no ShowObject/HideObject effects … no broadcasts”). | **Skipped** |
| `c_habmMiner` | client targeting-command system (`TrekFindTarget` “target nearest enemy/friendly miner”) | **None.** The port's only `FindTarget` is `Ship.cs` NPC-AI combat-target selection (P149), a different mechanic that does not expose player “target the miner” commands. | **Skipped** |

Both are therefore named as **separate future prerequisites** (building the
port-side asset-mask → cluster-warning / high-value-spotting system for
`c_habmCaptureThreat`, and a player targeting-command system for `c_habmMiner`)
rather than forcing a fabricated integration point, exactly as the task brief
directs.

## What WAS landed (family completeness)

The two missing constants are added to `DataHullTypeIGC.cs`, completing the
native 16-bit family (`Igc/missionigc.cpp:5078-5108`) — 16/16 named, each read
via the existing `HasCapability(short)`:

- `HabmCaptureThreat = 0x08` (`igc.h:729`), docstring cites the native consumer
  and records that wiring is deferred.
- `HabmMiner = 0x4000` (`igc.h:740`), docstring cites the native consumer and
  records that wiring is deferred.

## Corpus proof these are real, populated bits (not dead enum members)

Selected **by bit** (never by model name — names recur across cores with
different capability sets, the same discipline P299 used), measured against the
representative static core in the shipped corpus:

```
CAPTURE_THREAT (0x08) hulls:  Stealth Bomber [NG] (fig17/fig23/wc_icsf/fig32/bom03/
                             dn_fadrshstl/tfsb2), Cruiser (cap302) …
MINER (0x4000) hulls:         Adv Miner / Enh Miner / Miner (utl91/utl101/utl19) …
```

The miner bit lands **exactly** on the hulls literally named “Miner”; the
capture-threat bit lands on station-capture-capable capital hulls (stealth
bombers, a cruiser) — real, meaningful, populated capabilities.

## Tests

`Allegiance.Core.Tests/HullAbilityFamilyTests.cs` (4, pure corpus/decode — no
networking, deterministic):

- `Newly_added_bits_match_native_values` — pins `0x08` and `0x4000`.
- `Complete_native_hull_ability_family_is_named` — asserts every one of the 16
  canonical native bits has a named `Habm*` constant; fails loudly on family
  drift.
- `CaptureThreat_bit_is_populated_on_real_hulls` — ⭐ at least one real corpus
  hull carries `0x08`.
- `Miner_bit_is_populated_on_real_hulls` — ⭐ at least one real corpus hull
  carries `0x4000`.

(The populate tests are the honest stand-in for a live behavioral proof: there
is no behavioral change to exercise, but these prove the bits are genuine and
populated rather than theoretical enum members.)

## Build / test status

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- New tests: 4/4 pass in isolation.
- Full `Allegiance.Core.Tests` suite run 4× this task. Every failure observed is
  a documented or pristine-confirmed environmental live-network flake — none
  related to this change (which is purely two constant declarations + a new
  corpus test file):
  - `LiveTurretGunnerBindingTests` (documented persistent flake).
  - `StationsUpdateLiveTests.Damaged_station_fraction_reflected_in_next_update`
    — fails identically on the pristine tree (confirmed this task).
  - `LiveMoneyTests.Board_refund_broadcasts_money_change_sidewide_not_cross_side`
    — fails identically on the pristine tree (confirmed this task).
  - `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`
    (documented flap), `GameOverPlayersWireTests.Conquest_win_...` (green in
    isolation).
- Baseline moved **3192 → 3196** (+4 new tests).

## Out of scope (unchanged)

- `c_habmThreatToStation` (P299, already landed).
- Building an asset-mask / cluster-warning / high-value-spotting system for
  `c_habmCaptureThreat`, or a targeting-command system for `c_habmMiner` —
  each named as a future prerequisite, not built here.
- Native reference source (`~/Development/Allegiance`) — read-only, never modified.
