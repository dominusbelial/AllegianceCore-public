# P1018 — Loadout V2 integration

Implemented and validated; this report was drafted before implementation/gate. Base: `7254ffcc`.
Changes stay in the working tree; no commits or pushes. The flag stays default off.

## Findings and root causes

Two causes made the first dock appear empty: base `Server.Lobby.cs:5388` (fixed at `:5380`) sent no
parts outside training, and `Server.ShipCommands.cs:3139` used the legacy
`MountWeapon` helper, which created a firing weapon without registering a part.
Native `WinTrek/load.cpp:535-538` copies the actual source ship into the shop;
`clintlib/appmsg.cpp:3180-3187` applies the full acknowledgement. Native
`Igc/shipIGC.h:1490-1516` creates the part and calls SetShip;
`Igc/weaponIGC.cpp:78-98` adds it to the ship's part list. Native fresh seats
start hull-less (`FedSrv/FedSrv.CPP:902-909`); this fix preserves the port's
free-spawn policy and its existing weapon selection, without inventing a kit.

Native first-dock detail: `WinTrek/trekigc.cpp:2832-2834` calls RestoreLoadout
for an unboarded non-ghost. With no matching saved hull, `clintlib.h:1630-1633`
falls back to ReplaceLoadout; with no saved entry, `clintlib.h:1848-1866` selects
the first affordable, buyable non-hidden hull and its successor, then buys the
default. `clintlib.h:2110-2142` fills the hull's preferred parts on budget.
Thus native does not intentionally show an all-empty equipped ship. The port's
spawn kit and its incomplete same-hull RestoreLoadout remain explicit policy/
parity differences; this story fixes truthful inventory, not that separate gap.

The original `Game1.Panes.cs:810-829` tested legacy touch toggles before V2.
Header touches could activate invisible teleport/investment panes. V2 also
lacked visible Back/Undock targets. Hidden V2 layouts and pickers could survive
screen changes; early hangar/command returns skipped device observation.

The Visuals flag was already registered, with a clear description, default off
and generic profile persistence. No second options setting was needed.

## Fix

The seat acknowledgement exports the real complete inventory, including cargo;
default player weapons use MountPart so functional and serialized inventory
agree. Free provenance still has zero trade-in value. Both old and V2 shops
consume that same state.

V2 consumes touch input before the old bands. Disjoint header Back/Undock
rectangles are drawn and hit-tested from LoadoutV2Layout via DockedTouchZones.
Back uses the existing picker/leave behavior (`WinTrek/load.cpp:1533-1538`);
Undock uses the existing DOCKED(NA) action (`FedSrv/FedSrv.CPP:7031-7052`).
The screen replacement boundary (`WinTrek/WinTrek.cpp:6706-6740`) now suspends
picker/hover/focus, clears stale frame/layout/marker and keeps observing held
devices even across early returns. Popup gates retain their existing behavior.

The proof restores fighter/bomber/capital fixtures and extras through actual
P1015 Tab focus and mouse hover. The integrated walkthrough uses actual UI
input, server purchases and money messages, then real station collision docking.
Return placement is held in an authored landing bay across launch cooldown and
client flight updates; it never sets a client dock flag. The flag stays off by
default. `docs/LOADOUT_V2.md` is linked from the glossary; matching parity rows
are updated.

## Real-window walkthrough and media

Production Game1, X11/Xvfb, software GL, PCore001 and a loopback server. Native
keyboard layout is selected independently of HUD style. Setup grants side techs
and wallet amounts using ordinary server broadcasts. Input uses the shipping
pointer/key seams; only the display-only hardpoint fixtures replace ShopModel.
The integrated walkthrough never replaces it.

| Session | Assertions (Passed / Failed / Skipped) | Wall time |
| --- | --- | --- |
| Classic 1920×1080 | **51 / 0 / 0** | **120.4 s** |
| Modern 1920×1080 | **51 / 0 / 0** | **120.4 s** |
| Modern 1280×720 | **51 / 0 / 0** | **120.4 s** |
| Fighter + extras, Classic / Modern | **10 / 0 / 0 each** | 13.4 / 13.4 s |
| Bomber + extras, Classic / Modern | **10 / 0 / 0 each** | 11.0 / 10.8 s |
| Cruiser + extras, Classic / Modern | **11 / 0 / 0 each** | 11.3 / 11.2 s |
| Flag off, Classic / Modern | **8 / 0 / 0 each** | 11.4 / 11.2 s |
| **Final matrix total** | **231 / 0 / 0** | 11 client processes |

The walkthrough asserts: first-dock inventory; F4; mouse hull selection; slot
open/close; offer hover and deltas; mounting; independent wheel scrolling;
Tab/arrow/Space picker focus; Escape; Left/Right hull changes; turret marker;
Default by D and button; Revert; shortfall and unaffordable Buy; a tech-loss
server refusal; live money refresh; affordable Buy with exact wallet debit;
fully armed parts; Launch; collision re-dock; reopening V2. It runs to at least
120 seconds even when the actions finish sooner. At 720p the picker can start
scrolled to the current part: the proof now reveals early offers with actual
wheel input before clicking them. This was a script assumption, not a shop fix.

The hardpoint modes were reconstructed from `b87f4769` and use P1015 Tab focus
(Fighter) or hover (Bomber/Cruiser), with gunner data through EXPORT/LOADOUT_CHANGE.
All six modes also assert opposite-orbit projection and shield hover suppression.
PCore001 has no serialized minDot (`Igc/igc.h:1696-1708`); native UpdateCone and
its callbacks are commented out (`WinTrek/load.cpp:1321-1356,1370-1377`). The
markers/guide remain a presentation feature, not firing-angle parity.

**10 original PNGs retained**, below the 12-image limit, all individually viewed:

| Evidence | Captures |
| --- | --- |
| Classic shortfall and refusal line | [1920×1080](P1018_assets/classic-1920x1080-p1017-1-cannot-afford.png) |
| Modern inline offer and deltas | [1920×1080](P1018_assets/modern-1920x1080-1-click-slot-picker.png) |
| Bought ship / exact paid loadout | [Modern 1920×1080](P1018_assets/modern-1920x1080-p1017-3-bought.png) |
| Re-docked, actual ship and visible navigation | [Modern 1280×720](P1018_assets/modern-1280x720-6-redocked.png) |
| Fighter focus marker | [Classic](P1018_assets/classic-1920x1080-hardpoint-fighter.png), [Modern](P1018_assets/modern-1920x1080-hardpoint-fighter.png) |
| Bomber turret / gunner | [Classic](P1018_assets/classic-1920x1080-hardpoint-bomber.png), [Modern](P1018_assets/modern-1920x1080-hardpoint-bomber.png) |
| Cruiser multiple turrets / dimmed obscured mount | [Classic](P1018_assets/classic-1920x1080-hardpoint-capital.png), [Modern](P1018_assets/modern-1920x1080-hardpoint-capital.png) |

[Qualification and media hashes](P1018_assets/qualification.json) records the
final runs, dimensions, byte hashes and individual review qualifications.
All **38 generated PNG files** (23 distinct byte images) were viewed; identical
copies share that review. Every PNG passed chunk CRC and decompression/size
checks. Additional captures and raw logs remain in ignored `.scratch/p1018/`.
Media contain native artwork, generic pilot/gunner names and no private content.

**Visual qualification remains open:** this lane reproduces P1019's intermittent
missing UI draws/glyph runs, including some first-capture frames. The four
retained Bomber/Cruiser images have partial headings, stat labels/values or
footer text; their mount/gunner markers are visible. The retained walkthrough,
Fighter and bought-state frames show the intended layout. Selecting one
checkpoint per fresh process limits readback for focused review; it does not
fix the renderer. A CPU readback encoder and texture-binding invalidation
experiment reproduced the problem and were removed. No synthetic image or
speculative production rendering change is included. Capture coverage and
behavior pass; these images are not unconditional visual approval.

## Tests and gate

The report was drafted before implementation/gate. Counts are Passed / Failed /
Skipped unless otherwise stated.

| Check | Result |
| --- | --- |
| Final solution build | **0 warnings / 0 errors**, 10.09 s |
| `tools/build-tools.sh --drop-bins` | **140 / 140** projects, zero warnings/errors; updated proof also rebuilt independently |
| New integration cases | **8 / 0 / 0** in each of three isolated repetitions |
| Changed selection: P1018 + P1015 screen routing | **48 / 0 / 0 each ×3**, **144 / 0 / 0** total |
| P1018 with corpus deliberately absent | **6 / 0 / 2**, clean artwork skips |
| Input/layout/screen integration neighbors | **152 / 0 / 0** |
| Server weapon arming / mount-mask / P1017 purchase neighbors | **17 / 0 / 0** |
| Final real-window matrix | **231 / 0 / 0** assertions |
| One full Fast suite | **14,503 / 13 / 7**, **624.6 s**, **FAIL**; six fixture assumptions corrected afterward, non-task repeats **67 / 2 / 0** across 33 processes |
| Glossary / parity / diff checks | PASS; **861** glossary paths |
| Added-line/public media hygiene | PASS; 10 reviewed PNGs (3,857,672 bytes); no secret/email/home-path hits |

The new tests exercise both HUD settings, visible touch targets, legacy-band
ownership, popup/launch gating, Back closing the picker before leaving, Undock,
menu row/description and disk persistence, off/on lifecycle and Escape safety,
held-device edges after suspension, and a real first-seat inventory/zero-refund
path with PCore001. The existing screen-gate test now redraws after a screen
replacement before clicking: discarded hidden geometry is intentionally inert.
No training rules changed; this story uses the requested light gate.

Six failures in that sweep were **task-caused obsolete fixture assumptions**,
not flakes. Their fixes preserve the original drop/swap/privacy/round-trip
assertions:

| Fixture | Adaptation |
| --- | --- |
| `LoadoutRoundTripTests.BuyLoadout_WithRealHullAndWeapon_RoundTrips` | Initial ACK equals actual server inventory; the separate explicitly empty purchase still checks hull-only bytes. |
| `TeammateLoadoutBroadcastTests.Teammates_real_loadout_is_broadcast_to_observers_but_enemy_loadout_is_not` | Initial same-side export equals real inventory; wait for the requested part/amount on the later export. Enemy suppression assertion stays. |
| `P847MissionRulesCertifyTests.Dropped_weapon_treasure_carries_native_zero_amount` | Empty the fixture socket before mounting the particular weapon it intends to drop. Native treasure amount remains zero. |
| `P847MissionRulesCertifyTests.Dropped_empty_launcher_creates_no_treasure` | Select Dispenser/0 by type and mount instead of emptying Weapon/0; re-read the immutable replacement after SetPartAmount. Still requires no treasure. |
| `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | Empty the fixture socket before adding its test part, so removing it removes that part. Owner/teammate delivery and enemy suppression remain. |
| `CargoBayReloadTests.SwapPart_wire_out_of_range_negative_mount_is_rejected_no_broadcast_no_change` | Check the prepared Weapon/1 socket rather than assuming only one weapon exists; full before/after inventory and no-broadcast assertions remain. |

All **six corrected methods passed three isolated final-binary runs each:
18 / 0 / 0**. Combined with the changed UI selection, changed-test repetitions
are **162 / 0 / 0**. These corrections only change test setup/expectations;
production binaries are the same ones exercised by the full sweep and window
matrix. The full sweep remains **FAIL**, and was not repeated, honoring the
one-full-sweep brief. Non-task failure/timing repeats are recorded in
`docs/TEST_FLAKES.md` and the [portable verification](P1018_assets/verification.json).

The seven non-task assertion failures were keymap persistence, audio allocation,
FullSession's early team-lobby PLAYERINFO side, asteroid-capacity receipt,
native-miner progress, lethal probe missile receipt and armed-probe damage.
All seven methods were repeated alone three times. Four additional timing-only
methods (varied loadout, gunner selection, nanite support and civ profile) were
also repeated three times with every theory case. Across those 33 processes:
**67 passed / 2 failed / 0 skipped**. Asteroid capacity failed once again, and
lethal probe missile failed once again; the other methods passed all repeats.
The remaining slow repeat was that failed lethal-probe case (14.755 s).
No native timing, tier, allowance or assertion was changed to hide these results.
The seven original skips are pre-existing optional pack/audio/manifest and
server callback cases; all new artwork-backed cases ran in the full sweep.

Full sweep command (two-hour outer bound, exceeding the requested 90 minutes):

```sh
timeout --signal=TERM --kill-after=30s 7200s tools/run-tests.sh \
  --tier fast --no-build --results-dir .scratch/p1018/fast
```

Builds used `--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`.
NuGet audit stayed enabled, using supplied cached official vulnerability data
from 2026-11-12 through a temporary loopback feed and the local package cache.
The feed has stopped. No public host or other machine was contacted. Docker is
absent; no production service, store, port or environment variable was added.

## Hand-offs

Known epic gaps: cargo in the shop model, disk persistence of custom loadouts,
same-hull RestoreLoadout, launch only when all parts were bought, and reuse of
parts no longer sold. These remain documented follow-up work. Also retained:

- Dock restock's free-part successor provenance can overstate the client's refund
  (P1017 hand-off); the wire does not expose that provenance.
- The later dock-sync acknowledgement still omits cargo; the initial seat sync
  now includes it, but cargo editing and full dock cargo synchronization remain
  separate from this story.
- The P1019 software-GL missing draw/glyph defect remains unconfirmed; see the
  explicit media qualifications above. No hardware-GPU playtest was performed.

Parity evidence: `docs/parity/p1018-integration.csv`, updated P1015 gates/actions,
restored P1016 proof evidence, and the main purchase-answer row. User/operator
instructions live in `docs/LOADOUT_V2.md`; the flag remains default off.

All started proof clients, loopback server tickers, Xvfb instances, build/test
processes and the temporary audit feed have ended. The brief is only in ignored
scratch. Changes remain unstaged/uncommitted; no commits or pushes were made.
The final working-tree hygiene, glossary, parity and diff scans pass.

## Native intent

The server owns purchases, mounting, money and launch; the loadout shows the ship
the player actually has and follows native hangar actions.

## Modern approach

One ShopModel, two presentation styles, visible touch targets, and reproducible
production-client walkthroughs against a loopback server.
