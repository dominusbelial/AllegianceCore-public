# P641 — weapons and parts in flight

Implemented in the working tree; **no commits or pushes**. Preserved the
pre-existing staged P640 work. Build is clean and the selected socket-free
checks pass. Live tests, the mandatory bot check and presented-window frames
are prepared for the unrestricted runner; they are not claimed as completed.

## Player behavior

- **1–4** selects an occupied fixed weapon and single fire. Pressing that same
  weapon again returns to group fire; selecting a different weapon stays single.
  Empty sockets, turrets and boarded pilots cannot select a fixed weapon.
  The HUD displays the fire mode, selected weapon and mounted weapon names.
- **Shift+B** latches the booster. Held boost still combines with the latch;
  release of the hold does not cancel a latched request. A fresh flight-entry
  prime clears the latch. The HUD shows the latch state, not a claim that fuel
  or a booster is present. The existing simulator determines actual thrust.
- **Period / Shift+Period** cycles the whole mounted/cargo inventory; the HUD
  names the selected entry, signed mount and amount. **Shift+M** mounts selected
  cargo in a compatible empty socket. **Shift+U** replaces compatible occupied
  equipment, returning the previous part to that cargo slot. The selected fixed
  weapon is preferred when compatible. **Ctrl+U** unmounts selected equipment
  into the first free cargo slot. A full hold refuses unmounting. **Ctrl+Delete**
  drops the selected equipped or cargo part through the existing treasure path.
  All four mutations require a live, airborne, unboarded pilot. Docked buying
  and loadout changes continue through the existing shop.
- These commands use the action catalog, F1 help and F6 rebinding. Native weapon
  defaults reclaim 1–4; the four conflicting port head looks move to **Alt+1–4**.
  Other existing head looks keep their bindings. Modified chords do not also
  invoke the plain action on the same key (P640 action-map specificity).
- Fixed the P626 negative-mount sentinel bug: valid cargo mounts are no longer
  mistaken for “no selection,” and DropCargo uses the selected occupied slot.
  NextCargo remains a separate cargo-only selection.

## Wire, simulation and prediction

`ConnectionSession.SendSwapPart` sends native SWAP_PART(34). The production
receive path now consumes SWAP_PART and DROP_PART(33) and updates mounted/cargo
inventory for self and remote ships. Equipment type distinguishes simultaneous
weapon/booster/etc. mount-zero sockets. Exported cargo is split from equipment.
Unknown ships and empty sources are harmless; same-slot swaps do not remount.

The port applies guaranteed server echoes once, including self. Native instead
mutates locally and skips its own echo. Waiting for the echo avoids optimistic
rollback and the dangerous double-swap that would undo a local prediction.
Inventory application does not change pose, cookie or the pending-reset flag.
The next own-simulation configuration picks up booster removal/replacement.

A mount change resets readiness, including the displaced part. Cargo weapons
are removed from the functional weapon list; remounting realizes them again.
Weapons, shields, cloaks and missile launchers honor mounting readiness. Shields
restart discharged. The core mount-rate constant advances readiness; the native
BytePercentage full scale is **240**, with existing 255 fixtures accepted as full.
The afterburner's existing ramp now uses that full scale and records completion.

Own-ship simulation now carries the booster power/fuel/mount ramp rather than
reporting permanent zero power. Movement runs before equipment advancement, as
on the server. Equipment changes reset the runtime; SHIP_RESET seeds its power
from authoritative telemetry. Socket-free tests compare 200 authoritative and
local steps across boost, release, fuel exhaustion, mounting and removal.
A live absence-of-reset-storm claim still requires the provided proof run.

Quick-ready already had its 5s/30s initial timer in `Probe.cs:128`; new tests pin
both sides of the strict firing boundary at a nonzero deployment time. These
are **probe abilities**, not a general ship-weapon fire restriction. Target-only
probes never enter the free-acquisition branch. Added source-carrier resolution,
removed/dead-target refusal and native allied-side restraint. Tests cover no
persisted target, wrong sector, removed/dead/friendly/allied/out-of-range targets,
a closer alternative enemy, and a boarded designated target.

## Native evidence and classification

Paths are relative to `~/Development/Allegiance/src/` unless stated otherwise.

| Rule | Native file:line | Classification / port |
|---|---|---|
| Per-weapon toggle and occupied fixed/hangar/parent guards | `WinTrek/WinTrek.cpp:9027-9058,9650-9671` | Keep; `Game1.FlightParts.cs:52`, `Flight/FlightInput.cs:95` |
| Native 1–4 defaults, TK identities | `Igc/igc.h:368-371`; `~/Development/Artwork/defaultinputmap.mdl:76-79` | Keep; catalog `:107`; move conflicting port looks to Alt chords |
| Generic part commands | `Igc/igc.h:234-237`; `WinTrek/trekinput.cpp:173-176` | Declared but no WinTrek handlers or native defaults. Modern inventory actions implement their declared intent using the native swap/drop mechanisms; do not invent native handlers |
| Flight swap: unboarded, airborne, compatible cargo/socket | `WinTrek/WinTrek.cpp:9075-9119,9122-9149`; `Igc/hullTypeIGC.h:199-213` | Keep flight/type/per-hardpoint-mask rules; explicit mount vs replace vs unmount instead of native swap-cycle UI; `InFlightPartRules.cs:15,24` |
| Five negative cargo mounts and slot addressing | `Igc/shipIGC.cpp:1516-1549`; `WinTrek/trekigc.cpp:2603-2642` | Keep; explicit zero sentinel fixes the port's selection ambiguity |
| Native swap/send and self-echo suppression | `clintlib/clintlib.h:2371-2408`; `clintlib/appmsg.cpp:1430-1456`; `FedSrv/FedSrv.CPP:6459-6501` | Same codec and server route; authoritative echo application replaces optimistic inventory mutation; `ClientWorld.FlightParts.cs:14` |
| Drop/terminate and server treasure | `WinTrek/WinTrek.cpp:9698-9730`; `clintlib/clintlib.h:2348-2369`; `clintlib/appmsg.cpp:1411-1428`; `FedSrv/FedSrv.CPP:6296-6456` | Keep wire/removal/treasure; generic DropPart can also select equipped inventory; `ClientWorld.FlightParts.cs:28` |
| Remount readiness reset, empty shield | `Igc/shipIGC.cpp:1557-1591`; `Igc/shieldIGC.cpp:121-139` | Keep; `Ship.cs:2759` |
| Readiness and mounting rate | `Igc/weaponIGC.h:30-63`; `Igc/shieldIGC.cpp:66-88`; `Igc/cloakigc.cpp:80-81`; `Igc/magazineigc.cpp:85-100`; `_Utility/Utility.h:1307-1362` | Keep state/timing; shared equipment readiness replaces native individual-part update objects; `Ship.cs:2777` |
| Booster declaration vs actual held boost | `Igc/igc.h:287`; `WinTrek/trekinput.cpp:222`; `WinTrek/WinTrek.cpp:8270-8271` | Toggle TK is declared but unhandled/unbound in native. Port latch composes with held boost, using the same state bit; `Game1.FlightParts.cs:93` |
| Booster mount, power and fuel | `Igc/afterburnerIGC.cpp:91-164`; `Igc/shipIGC.cpp:2939-2959` | Keep; `OwnShipSimulator.cs:108`, shared movement step |
| Quick-ready | `Igc/igc.h:775`; `Igc/probeigc.cpp:137-139,352-354` | Keep 5/30s timer and strict `nextFire < now`; `Probe.cs:128,431` |
| Target-only branch, source carrier and hostility | `Igc/igc.h:780`; `Igc/probeigc.cpp:268-274,390-399` | Keep designated target/no fallback; defensive liveness check; `Probe.cs:496` |

The native swap helper also automatically reloads certain equipment
(`clintlib.h:2402-2408`). This pack retains the existing explicit Reload action
and server auto-reload paths; it does not synthesize additional reload packets
from the generic inventory actions. Existing probe visibility, station-target
representation and dispenser load-time mechanics are not expanded by this pack.

## Parity scores

Measured against the actual working matrix at P641 entry (including P640), not
the brief's older wave-1 baseline. Formula: done/non-na, with visibility weights;
partial rows receive no done credit.

| Area | Weighted before → after | Unweighted before → after |
|---|---:|---:|
| commands | 73.6% → 78.0% | 66.8% → 71.6% |
| fedmsg | 77.5% → 78.3% | 76.8% → 77.6% |
| screens | 86.8% → 86.8% | 79.2% → 79.2% |
| parts | 82.4% → 86.3% | 77.5% → 82.5% |
| commander | 100.0% → 100.0% | 100.0% → 100.0% |
| lobby | 76.5% → 76.5% | 75.0% → 75.0% |
| **total** | **78.2% → 80.6%** | **73.4% → 76.3%** |

Closed nine command rows, SWAP_PART and the two ability rows. Refreshed citations
for DROP_PART, inventory cycling and the four moved head looks. Updated
`fedmsg-wiring.csv`; executable coverage is now **88 consumed / 30 known
unconsumed**, with the original 102-native-message completeness assertion intact.

## Verification and unrestricted handoff

- `dotnet build AllegianceCore.slnx --no-restore -m:1`: **0 warnings / 0 errors**.
- **273 socket-free cases passed**: ClientCore 16, MonoGame 220, Core 37;
  **40 new P641 cases**. Includes the P625b source guard, action-map rebinding,
  P624 movement/input, weapon fire, fuel, repair/refuel, native probe firing,
  own-simulator and dispatch-completeness regressions.
- Executed using xUnit's in-process discovery/execution API via the temporary
  `/tmp/p640-runner` harness. Final logs: `/tmp/p641-build.log`,
  `/tmp/p641-client-tests.log`, `/tmp/p641-mono-tests.log`, `/tmp/p641-core-tests.log`.
  An earlier mixed-class selection reached three existing `Live_*` tests and
  failed with socket `Permission denied`; the final selection excludes them.
- `git diff --check`, CSV shape/weight checks and
  `bash -n scripts/proof-p641.sh`: passed.
- Written, built, **not run**: `P641FlightPartsLiveTests` drives production
  mount/unmount/replace/drop senders, weapon-selection control bits and booster
  fuel use through a real local server/client. Added quick/normal readiness and
  target-only restraint cases to `ArmedProbeLiveTests`.
- Run `scripts/proof-p641.sh [output-directory]` outside this lane. It creates
  an isolated temporary detached worktree, overlays current changes, builds,
  and uses P572's local server and presented MonoGame window. It requests frames
  at 2/4/8/11/15s for single selection, unmounted cargo, remounted equipment and
  latched boost. It requires a real server-confirmed mount, accepted `[ctrlak]`
  updates, and at most two transition resets. It then runs the gated P632 30v30
  regression, requires nonzero target arrivals and kills, and saves the trace.
  Inspect those frames and run the full live suite before accepting visible
  parity. No PNG, live bot success or reset-storm result is fabricated here.
