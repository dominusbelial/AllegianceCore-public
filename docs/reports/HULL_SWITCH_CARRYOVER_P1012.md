# P1012 — Loadout V2: hull-switch carry-over parity

**Finding.** Native carries **nothing** over when the player picks another hull. The pending ship is
emptied and rebuilt from the loadout last bought (or defaulted) for the new hull, else from that hull's
default loadout. The mockup's "keep what fits" is not native. The port already did this for a swap *away*
from the docked hull, but had five differences, all fixed in `ShopModel`:

1. Going back to the docked ship's own hull kept the other hull's parts.
2. Picking the hull that was already pending wiped the player's slot edits (and played the mount sound).
3. A saved loadout was replayed verbatim instead of being re-bought from what the station sells now.
4. Launchers were bought with the launcher's stock amount instead of what the hull holds, and the shown
   price counted a launcher once instead of amount × price; the docked cargo hold was left out of the
   ship's value. The shown cost could differ from what the server took.
5. The saved loadouts were thrown away at every undock.

This changes the current loadout screen too (intended: it restores native behaviour). Nothing is behind
the `visuals.hangar.loadoutV2` flag because no V2 screen exists yet; the model is the one V2 will drive.

Base `874fd66a`. Changes are in the working tree; nothing committed or pushed. .NET SDK 10.0.404, Debian
container, artwork `/srv/corpus/Artwork`, native `/srv/corpus/Allegiance/src`.

## 1. Native: what a hull change does (client)

Every hull change goes through `LoadoutImpl::BuyHull` (`WinTrek/load.cpp:1476-1490`):

| Entry | Native | Guard |
|---|---|---|
| Hull menu click | `OnMenuCommand`, `load.cpp:1894-1899` | only when `phullType != m_pship->GetBaseHullType()` (`:1896`) |
| Next / Previous | `NextHull`, `load.cpp:1618-1625`; target from `GetNextHullType`, `:1568-1616` | only when `pht != phtCurrent` (`:1623`) |

`BuyHull` then does, in order:

1. `SaveLoadout(m_pship)` (`load.cpp:1478` → `clintlib.h:1437-1478`): the pending ship is filed in
   `m_loadouts` under its hull. `BuyHull` never reads this back; it is read when the screen opens (§3).
2. `RefreshLoadout(m_customLoadouts[0], phullType)` (`load.cpp:1481`, body `:1453-1474`): **every pending
   part is terminated** (`:1456-1470`, asserted empty), then
   `ReplaceLoadout(m_pship, station, phullType, moneyLots, customLoadouts[0])` (`:1473`).
3. `ReplaceLoadout` by hull (`clintlib.h:1790-1802`): if custom-loadout slot 0 holds a loadout for this
   hull, clone it; otherwise `BuyDefaultLoadout`.
4. `mountSound` (`load.cpp:1487`).

The budget is `moneyLots`, so the wallet never trims the rebuilt loadout. Affordability is judged only at
Buy / Launch (`load.cpp:1498-1531`).

**Cloning a saved loadout** (`clintlib.h:1804-1849`, loop `:1824-1845`), per saved part in mount order:

- the part is re-resolved against the station: buyable → its furthest buyable successor
  (`stationIGC.h:399-440`); not buyable → the nearest buyable predecessor (`GetSimilarPart`,
  `stationIGC.h:453-470`); neither → dropped;
- it is mounted only if the hull accepts it **at the saved mount** (`pht->CanMount(ppt, cpl.mount)`, `:1839`);
- it is bought fresh by `BuyPartOnBudget` (`clintlib.h:1305-1339`): a launcher gets `GetAmount(pship)`,
  everything else `0x7fff`;
- if the hull itself was upgraded to a successor, the remaining empty slots get default parts (`:1847-1848`).

**Default loadout** (`BuyDefaultLoadout`, `clintlib.h:2110-2298`):

- mounts: `TryToBuyParts` (`clintlib.h:1341-1381`) walks the hull's preferred parts in order; each buyable
  one (as its successor) goes into every empty mount that accepts it — all weapon mounts including turrets
  (`iMountMax = GetMaxWeapons()`, `:1364-1366`), one mount for other equipment;
- cargo (`:2143-2296`): nanite, ammo, fuel, dispenser, prox or plasma for scout-type hulls, then the hold is
  topped up with spare missiles / dispenser / fuel / ammo / chaff.

**Where slot 0 comes from.** `SaveCustomLoadout(m_pship, 0)` is called after a successful Buy
(`load.cpp:1524`, `:1527`) and by Default (`:1643`); Ctrl+0 writes it too (`:2230`). Launch does not write
it (`:1498-1509`). The slots are a client member (`m_customLoadouts`, `clintlib.h:825-826`, allocated
`clintlib.cpp:1155`) and are saved to disk (`clintlib.h:1944-1975`).

Answers to the brief's questions:

| Question | Native |
|---|---|
| Which parts move to which mounts? | None move. The new hull gets slot 0's parts at their saved mounts, or default parts by preferred order. |
| What is dropped? | Everything pending. From a saved loadout: a part no longer sold in any form, or one the hull rejects at that mount. |
| Do defaults fill empty slots? | Only when there is no saved loadout for the hull (whole default), or when the saved hull was upgraded to a successor. A saved loadout with an empty slot stays empty. |
| Cargo / packs | Rebuilt with the rest: saved cargo at its saved cargo index, or the default cargo fill. |
| Turret parts | Same rule as fixed guns; each mount is tested against its own mask. |
| Partially mounted parts | Do not exist after a hull change: parts are created new and the server arms each one (`shipIGC.h:1039-1040`). |
| A → B → A | A is rebuilt from slot 0 / default. The docked ship's parts and unbought edits do not come back. |
| Same hull picked again | Nothing happens (`load.cpp:1896`, `:1623`). |

## 2. Native: what the server does on buy

`FM_C_BUY_LOADOUT` (`FedSrv/FedSrv.CPP:6174-6260`):

1. `cost = -ship->GetValue()` (`:6183-6184`): the whole current ship is refunded — hull plus every part,
   cargo included (`shipIGC.h:396-406`).
2. `PurchaseShipLoadout` (`shipIGC.h:989-1110`): a changed hull must pass `CanBuy`, else the buy aborts
   (`:996-1001`), and is bought as its successor (`:1004`). Old parts are set aside. Each requested part is
   mounted if the hull accepts it at its mount and the mount is free (`:1029-1031`); a buyable part is bought
   as its successor and armed (`:1033-1040`); a part no longer buyable is re-used from the old ship if it had
   one (`:1044-1052`). Anything else makes the buy incomplete (`:1097`). Unused old parts are destroyed.
3. `cost += ship->GetValue()` (`:6193`); the wallet is debited by `cost` (negative = refund), and the side
   is told (`:6196-6209`).
4. If the hull changed, observers are booted; on a turret hull, gunners whose turret has no gun are booted
   (`:6212-6245`).
5. Launch happens only if everything was bought (`:6247`).

A part instance's price: launcher = amount × launcher price (`magazineigc.h:85-88`, `dispenserigc.h:86-89`),
pack = 0 (`packigc.h:80-83`), others = type price. A launcher's amount is hull capacity for its equipment
type / expendable size, minimum 1 (`launchertypeigc.h:130-142`), so the same launcher costs a different
amount on a different hull. The client shows the same sum: `m_pship->GetValue() - (current ship value +
money)` (`load.cpp:1513`).

## 3. Differences found

| # | Difference | Class | Root cause | Result |
|---|---|---|---|---|
| 1 | Back to the docked hull kept the other hull's parts (Cycle) or the pending parts (list pick) | port bug | `ShopModel.RebaseForEffectiveHull` returned early when `!WillSwapHull` | fixed |
| 2 | Picking the pending hull again rebuilt its default and raised `HullChosen` | port bug | `SelectHullAtCursor` always rebased | fixed |
| 3 | Saved loadout replayed verbatim: no successor / similar part, no per-mount check, stale launcher amounts | port bug | `RebaseForEffectiveHull` / `RestoreCustomLoadout` copied the saved list | fixed |
| 4 | Launcher amount = launcher stock amount, not what the hull holds | port bug | `DefaultAmmoFor` / `OpenSlotParts` used `LauncherOffer.Ammo` | fixed |
| 5 | Shown value counted a launcher once, counted packs, ignored the cargo hold | port bug | `ValueOf` summed type prices of mounted parts | fixed |
| 6 | Saved loadouts lost at every undock | port bug | the store was a field of `ShopModel`, rebuilt per dock (`Game1.Panes.cs`) | fixed |
| 7 | "Will be dropped" projection tested the hull, not the mount, and only during a swap | port bug | `ChosenPartsThatDontFitEffectiveHull` | fixed |
| 8 | Default loadout has no cargo; a buy empties the hold | port gap (known, P996 report) | the shop model holds mounted parts only | hand-off |
| 9 | Slots not saved to disk | port gap | in-memory store | hand-off |
| 10 | Server leaves observers / gunners seated on a hull change | port gap | `ApplyLoadout` has no eviction step | hand-off |
| 11 | Server mounts with the client's mount fraction instead of arming | port gap | `ApplyLoadout` passes `FractionMounted` through | hand-off |
| 12 | Screen open does not re-buy the same-hull loadout from the cache | port gap | only the lifepod branch of `RestoreLoadout` is ported | hand-off |
| 13 | Nothing carried between hulls | native design | — | kept |
| 14 | Unbought edits lost when leaving a hull | native design | — | kept |
| 15 | Wallet does not limit the rebuilt loadout | native design (`moneyLots`) | — | kept |

What difference 4/5 meant in practice (PCore001): Cash Box 1 ($1000, stock 2) on a Hvy Scout was shown as
$1000 and charged $2000. Sanctuary ($100, stock 1) on a Lxy Scout was bought ×1 where native buys ×3.
An Adv Scout's default MRM Seeker rack was 10 missiles where native loads 3 (magazine 15 / size 5).

## 4. Fix

All in `src/Allegiance.ClientCore.Game/Rendering/ShopModel.cs`:

- `BuyHull` (`:841`) replaces `RebaseForEffectiveHull`. It is a no-op when the hull is already pending;
  otherwise it clears the pending parts and rebuilds from slot 0 for that hull, else the default.
  `CycleHull` (`:285`) and `SelectHullAtCursor` (`:303`) call it; `HullChosen` fires only on a real change.
- `ReplaySavedLoadout` (`:868`) + `ResolveSavedPart` (`:888`): the native clone loop. Also used by
  `RestoreCustomLoadout` (the 0..9 keys).
- `DefaultAmmoFor` (`:1072`): launcher amount = what the hull holds (`Ship.ClampPartAmount`, the server's
  own clamp). Used for slot offers, defaults and replay.
- `ValueOf` / `PartInstancePrice` (`:191`, `:210`): launcher = amount × price, pack = 0.
  `CurrentShipValue` (`:223`) adds the docked cargo hold (`CurrentCargo`), because the server refunds it.
- `ChosenPartsThatDontFitEffectiveHull` (`:162`): per-mount test, no longer gated on a swap.
- `ShopCustomLoadouts` (`:1142`): the slot store, owned by `Game1` (`Game1.cs:1046`) and handed to every
  `ShopModel` (`Game1.Panes.cs:178`), together with the docked cargo.

No wire change, no server change, no new setting.

One deviation, recorded in the parity row: the model sees the station's offer list (buyable and not
obsolete), not raw `CanBuy`. A saved part whose own tech is missing while a *later* successor is buyable
resolves to that successor; native would take the predecessor.

## 5. Tests

New, `tests/Allegiance.ClientCore.MonoGame.Tests/`:

`P1012HullSwitchCarryOverTests.cs` (16 cases, real PCore001, expected lists written out by hand):

| Class | Test |
|---|---|
| fighter → fighter | `Fighter_to_fighter_rebuilds_the_default_and_keeps_nothing_that_fits` (all five docked parts fit the new hull and are still gone) |
| fighter → bomber | `Fighter_to_bomber_rebuilds_the_bomber_default_with_turret_guns` |
| → turret hull | `To_a_turret_hull_fills_fixed_and_turret_mounts_by_their_own_masks` |
| → fewer mounts | `To_a_hull_with_fewer_mounts_leaves_no_part_on_a_missing_mount` |
| A → B → A | `Back_and_forth_does_not_restore_the_docked_parts_or_the_edits`, `Back_and_forth_restores_the_loadout_last_bought_for_that_hull`, `Picking_the_pending_hull_again_keeps_the_edits` |
| saved loadouts | `Saved_loadouts_survive_into_the_next_dock`, `A_saved_part_is_rebought_as_its_successor_or_similar_part` |
| amounts | `Launcher_amounts_follow_the_hull` |
| price / money | `A_priced_launcher_costs_amount_times_price_on_each_hull` (3 hulls: $100 / $200 / $800, shortfall and launch gate at $150), `Switching_away_refunds_the_old_hull_and_its_parts`, `Current_ship_value_counts_the_cargo_hold` |
| projection | `Dont_fit_projection_is_per_mount` |

`P1012HullSwitchMoneyLiveTests.cs` (1 case, real in-process server and client session): buy Lxy Scout +
3 Sanctuary → the server wallet drops by exactly the shown $800; switch to a free hull → refunded exactly
the shown $800; switch back → the loadout bought in leg 1 returns and costs $800 again.

Changed existing tests (they pinned the old behaviour):

- `ShopModelTests.Hull_pick_raises_hull_chosen_only_on_change` — re-pick is silent; picking the docked hull
  while another is pending is a change.
- `ShopLauncherCatalogueTests` (4 tests), `ShopLauncherBuyLiveTests`, `P571DefaultLoadoutTests.Launch_transmits_the_default_loadout_not_zero_parts`
  — launcher amount is what the hull holds, not the stock amount.
- `P764UiSoundSeamTests.SaveRestoreRoundTrip_…` — the fixture weapon must really fit hardpoint 0, because a
  restored slot now applies the native per-mount test.

Both HUD styles: this story changes no drawing code. The loadout text screen is the same code path in
Classic and Modern and reads the model through `ShopLayout`; no screenshots were taken (and the lane has
no GL — epic blocker S0).

## 6. Gate

- `dotnet build AllegianceCore.slnx`: 0 warnings, 0 errors. `tools/build-tools.sh`: 138/138.
  `scripts/parity-scorecard.py` and `tools/check-glossary-links.py`: OK.
- New and changed tests alone, three times: **79 passed / 0 failed / 0 skipped** each time (17 new cases,
  62 in the changed classes).
- One Fast sweep: **14,269 passed / 6 failed / 7 not executed** of 14,282 (662 s).
  `Allegiance.ClientCore.MonoGame.Tests`, the only project that uses the shop model: 5,246 passed, 0 failed.
  The six failures are in `Allegiance.Core.Tests` (5) and `Allegiance.ClientCore.Tests` (1), which do not
  use the shop model, and are all already-ledgered families. Alone ×3: four pass 3/3; the auto-seat
  boundary case passed 1 of 3 and the P739 warp-bomb theory failed 1–2 of its 3 cases in two of three runs.
  Details in `docs/TEST_FLAKES.md` (2026-11-14 entry).
- **Training gate not completed.** `tools/run-training-tests.sh --no-build --repeat 3` was stopped by the
  lane's 2-hour limit during repeat 1 (the real-window lessons take about 9–15 minutes each here). Headless
  half: 126/126. Real-window half: two failures before the stop — mission 2, policy `reminders`, Classic and
  Modern, `M2 9.2: native prompts missing or out of order`. Both fail the same way alone, and the Classic
  case fails identically on an untouched checkout of the base commit, so P1012 did not cause it. The
  remaining real-window lessons were not run on this tree; the reviewer should run the training gate on a
  host where it fits.
- No screenshots (no drawing change; no GL in the lane).

## Hand-offs

- **P1010 (view-model) / P1013 (layout):** native slots include the cargo hold (five cargo mounts). The
  model has no cargo: implementing native `BuyDefaultLoadout`'s cargo fill (`clintlib.h:2143-2296`) and
  cargo slots belongs with the slot rows. Until then a hull change or Default leaves the hold empty, and
  any buy sells the docked cargo back (the shown cost now includes that refund).
- **P1010:** `SlotPartOffer.Amount` is now per hull. A part row that shows a price should show
  `ShopModel.PartInstancePrice(part, amount)`; `ShopLayout` and `PartRowGreyed` still use the unit price.
- **P1014 (renderer):** `ChosenPartsThatDontFitEffectiveHull` is now a per-mount projection that is valid
  without a pending hull swap. `ShopLayout` still prints its "DROPPED on launch" rows only when
  `WillSwapHull`; the V2 slot rows should mark such a part directly.
- **P1011 (stats):** ammo / missile counts in a stat sheet must use the amount in `ChosenParts`, which is
  per hull.
- **P1017 (buy flow):** (a) the server does not boot observers / turretless gunners on a hull change
  (`FedSrv.CPP:6212-6245`); (b) the server keeps the client's mount fraction instead of arming every bought
  part (`shipIGC.h:1039-1040`); (c) the server refunds nothing for free-spawned hulls and parts
  (`RefundableShipValue`), which the client cannot see, so the shown refund for a *spawn* ship carrying a
  priced default part can be higher than the real one.
- **P1018 (integration) or a separate story:** save the custom-loadout slots to disk
  (`clintlib.h:1903-1975`); port the same-hull branch of `RestoreLoadout` at screen open
  (`clintlib.h:1480-1636`): re-buy as successors, refill cargo and empty slots from the cached loadout.
- **Hull order:** native Next / Previous walks the core's hull list order; the port walks hull-id order,
  and from a hull that is not on offer (a lifepod) "next" lands on the second offered hull. Not touched.

## Native intent

A hull is bought as a whole ship. The player's kit belongs to a hull type, not to the session: choosing a
hull brings back the kit last paid for on that hull, or the designers' default for it. Parts are never
moved between hulls, and the price is always "new ship minus old ship".

## Modern approach

One pure rule (`ShopModel.BuyHull`) used by every way of changing hull, with the saved kits in a small
client-owned store and one value function that matches the server's debit. The V2 screen can therefore show
"this is what you get and what it costs" before the player commits, without a second copy of the rules.
