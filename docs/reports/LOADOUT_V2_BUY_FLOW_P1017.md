# P1017 — Loadout V2: Buy / Launch flow, refusal messages, and the server buy gaps

**Finding.** The V2 screen could send Buy and Launch but did nothing with the answer. After a buy that
was refused or only partly granted, the screen still showed the loadout the player had asked for, priced
against a ship that no longer matched the server's; a launch the server did not perform left the screen
locked. The same was true of the old loadout screen. On the server, three native rules of the buy were
missing. All of it is fixed, with one wire path for both screens and no new message.

**The server changes are behaviour changes for every client** (old loadout, V2, bots, harness, any
future native client), with the flag on or off:

1. After a buy, passengers are sent away as native does (hull change: observers and gunners whose turret
   is gone; same hull: passengers whose turret mount is empty).
2. Every part a buy puts on the ship is fully mounted, whatever mount fraction the client sent.
3. A free spawn part that the ship keeps through a buy stays free: it is no longer charged for on the
   first buy. It still refunds nothing.

Base `a4c68e09`. Changes are in the working tree; nothing committed or pushed. .NET SDK 10, Debian
container, artwork `/srv/corpus/Artwork`, native `/srv/corpus/Allegiance/src`. This report was written
before the gate ran; the gate section was filled in afterwards.

## 1. Native: the buy and launch flow

All citations re-grepped in the mounted native source.

| Step | Native | What it does |
|---|---|---|
| Launch button | `WinTrek/load.cpp:1498-1509` | If docked-ship value + money ≥ pending-ship value: one `BUY_LOADOUT` with `fLaunch`. Otherwise **nothing** — no message, no money request. |
| Buy button | `WinTrek/load.cpp:1511-1531` | `cost = pending − (docked + money)`. `cost > 0`: `REQUEST_MONEY(cost, hull)`. Otherwise `BUY_LOADOUT` without launch, then the pending ship is saved as custom loadout 0. |
| Button states | `WinTrek/load.cpp:2139-2190` | `loadoutShipState` 0 already own / 1 worth nothing / 2 affordable / 3 not affordable. Launch enabled when already own or affordable. Buy enabled when not already own and the player **or a teammate** can cover the shortfall (`:2152-2166`). The Buy button is swapped for "Confirm change" when the pending ship is worth nothing (`:2184-2190`). |
| State text | `Artwork/loadoutpane.mdl:48-57` | "This is your current loadout." / "Press 'confirm change' to select this loadout." / "Press 'buy' to purchase this loadout." / "You can't afford this loadout." The block is **commented out** in the shipped file, so native draws no state line; the wording is still native's. |
| Money change | `WinTrek/load.cpp:1417-1422` | Updates the money number. (Button states are recomputed at the next `UpdateButtons`.) |
| Server answer | `clintlib/appmsg.cpp:3176-3187` | Ends the lock-down unless `fLaunch`; applies the acknowledged loadout to the real ship; calls `OnPurchaseCompleted(fBoughtEverything)`. |
| Purchase completed | `WinTrek/load.cpp:2193-2210` | Not everything bought: message box **"Some of the parts you requested are no longer available"** and the pending ship becomes a copy of the real ship. Always: old-ship cost and buttons recomputed from the real ship. |

Native never sends an unaffordable buy, so its server treats one as a hack and disconnects
(`TrapHackBoot`, `FedSrv/FedSrv.CPP:6198`). The port's server refuses instead (P441) and answers with the
unchanged ship; the client has to handle that answer.

Server side of the buy (`FedSrv/FedSrv.CPP:6174-6260`):

| Rule | Native |
|---|---|
| Price = new ship value − old ship value | `FedSrv.CPP:6183-6193`, `Igc/shipIGC.h:396-406` |
| A fresh player's ship has no hull — "it has to be free so they don't get a cash back bonus when they trade it in for a new ship" | `FedSrv.CPP:902-909` |
| Each bought part is armed: `Arm()` sets the mounted fraction to 1 | `Igc/shipIGC.h:1039-1040`, `:1051-1052`; `Igc/weaponIGC.h:149-153` |
| Hull change re-seats gunners by turret number; a gunner whose turret the new hull lacks becomes an observer | `Igc/shipIGC.cpp:1379-1399` |
| Hull changed: every observer leaves | `FedSrv.CPP:6212-6224` |
| Same hull with turrets: every passenger whose turret id matches no part's mount id leaves | `FedSrv.CPP:6225-6245` |
| Leaving: lifepod hull, docked, side-wide `LEAVE_SHIP` | `FedSrv.CPP:2277-2320` |
| Launch only when everything was bought | `FedSrv.CPP:6247` |

Two details of the passenger rule, ported as written:

- On a hull change a gunner who still has a turret stays **even if that turret has no gun**; only
  observers are sent away. The "turret has no gun" test belongs to the same-hull branch.
- In the same-hull branch native compares the passenger's turret id with every part's mount id, cargo
  included. An observer's turret id is NA (−1), which is also the first cargo mount. So an observer stays
  only while the first cargo slot is filled.

## 2. Root causes

| # | Problem | Root cause |
|---|---|---|
| 1 | The screen ignored the server's answer; pending loadout and docked ship went stale after any non-launch buy | `ShopModel` was built once per dock from the ship at that moment (`Game1.Panes.cs:173`); `CurrentHullId` / `CurrentParts` were get-only (`ShopModel.cs:98`). Nothing read `BUY_LOADOUT_ACK` back into the shop. |
| 2 | A launch the server refused left the loadout dead | `LaunchFromHangar` sets `_launchSent`; nothing cleared it when the answer came without `fLaunch`. |
| 3 | No refusal text on V2 beyond "Need $X" | `LoadoutV2Presentation` had no status line; `ShopModel.BlockReason` was the port's own "cannot afford: loadout cost …" wording. |
| 4 | Buy was live for the ship the player already has, and when no teammate could cover a shortfall | native `UpdateButtons` (`load.cpp:2139-2190`) was not ported; V2 only dimmed Launch on a shortfall. |
| 5 | Server left observers and gunners aboard after a buy | `ApplyLoadout` had no passenger step. |
| 6 | Server trusted the client's mount fraction | `ApplyLoadout` passed `p.FractionMounted` through (was `Server.ShipCommands.cs:2797`). |
| 7 | Client showed a refund for a spawn ship; server refunded nothing | Server: `RefundableShipValue` (`Server.Docking.cs:1710`) skips free-spawn hardware (P653, H-03). Client: `CurrentShipValue` summed list prices (was `ShopModel.cs:223`). The client had no way to know which hardware was free. |
| 8 | Server charged for the player's own free spawn parts on the first buy, and refused a penniless player's launch | `ApplyLoadout` priced every requested part at full price against an old-ship value of 0 (was `Server.ShipCommands.cs:2808`). Found while fixing 7. |

## 3. Fix

### Client (both loadout screens; V2 extras behind `visuals.hangar.loadoutV2`)

- **`LoadoutBuyFlow`** (new, pure, `Rendering/LoadoutBuyFlow.cs`) — `Launch` (`:87`), `Buy` (`:72`),
  `Sync` (`:107`). `Game1.Hangar.cs` `LaunchFromHangar` (`:49`) and `BuyFromHangar` (`:93`) send what it
  returns; `SyncShopWithServer` (`:130`) calls `Sync` every docked frame. V2 passes `nativeGate: true`,
  so a disabled Buy does nothing; the old screen has no disabled buttons and keeps its behaviour.
- **`ShopModel.PurchaseCompleted`** (`ShopModel.cs:857`) — the docked-ship snapshot becomes the
  acknowledged ship. Incomplete buy: native's message and the pending ship becomes a copy of the real
  one. A buy the server refused for money (ship unchanged, cost above the wallet): native's "can't
  afford" text. A pending loadout the player had not edited keeps following the ship.
- **Launch not performed** — `Sync` reports it and `SyncShopWithServer` releases `_launchSent`
  (native ends its lock-down, `appmsg.cpp:3180-3181`).
- **Native states** — `ShopModel.State`, `StateText`, `AlreadyOwn`, `LaunchEnabled`, `BuyEnabled`,
  `BuyIsConfirmChange` (`ShopModel.cs:277-350`). The team's money comes from
  `ClientWorld.WalletByShipId`.
- **V2 presentation** — `LoadoutV2Presentation.Status` (`:136`), drawn by `LoadoutV2Renderer` (`:91`)
  as a status line under the key hint, full width, so long messages are not cut. Buy reads `Need $X`
  (red, or dimmed when nobody on the team can cover it), `Confirm change` or `Buy`; a disabled button is
  dimmed. The price box's last line is `Shortfall $X`, `Owned` or `Ready`. P1013's layout rectangles are
  untouched.
- **Old screen** — the same messages appear on its existing `!! …` line (`ShopLayout.cs:109`). The
  "cannot afford" line now starts with native's sentence and keeps the port's numbers after it.
- **Money** — `Sync` refreshes the wallet and the richest teammate every frame; a "can't afford" line
  disappears when the money arrives.

Classification when the answer arrives (`LoadoutBuyFlow.Sync`): the buy is *complete* unless the hull
was not granted (the ship still has the hull it had) or parts were asked for and `fBoughtEverything` is
false. The port's server sends `fBoughtEverything = false` for a granted hull-only buy; the flow does
not treat that as a failure.

### Server (`src/Allegiance.Server/Server.ShipCommands.cs`, `ApplyLoadout`)

- **(a) Passengers** — `BootPassengersAfterPurchase` (`:3015`, called at `:2940`, before the ack):
  the rules of section 1, using the existing `PerformLeave`.
- **(b) Arming** — every granted part, cargo included, is mounted with `ExpandedPartData.FullyMounted`
  (`:2804`). All existing checks stay: dock / alive / ghost / unboarded / side, part count, negative
  amounts, per-mount `CanMount`, duplicate mounts, amount clamp, tech gate, funds gate.
- **(c) Free hardware** — see below (`:2813`, `:2824`, `:2897`).

### (c) The refund rule

**Native's rule:** a buy costs new ship value minus old ship value, and nothing a player holds was ever
free — the ship a player starts with has no hull, precisely so that there is no cash back
(`FedSrv.CPP:902-909`). The port hands every player a spawn ship, so "refund the list value" would mint
money; P653 (H-03) already made that hardware refund nothing. That stays.

What was missing is the other half and the client's view:

- **Server:** free hardware is worth nothing on *both* sides of the price. A free part that the new
  loadout keeps (same part type, no more units than the ship holds — the same type match native uses to
  re-use an old part, `Igc/shipIGC.h:1044-1052`) costs nothing and stays free. Before, the first buy
  charged the player for their own spawn kit. The rule is one function, `FreeHardware.Kept`
  (`src/Allegiance.Core/Sim/FreeHardware.cs`).
- **Client:** `OwnHardwareLedger` (`src/Allegiance.ClientCore/State/OwnHardwareLedger.cs`, on
  `ClientWorld.OwnHardware`) follows the same rule from the acknowledgements it already receives: until
  the client has sent a buy of its own, every loadout it is told about is hardware it never paid for;
  after that a part stays free only while it is still on the ship, and a hull is paid for when a buy of
  ours changed it. `ShopModel.CurrentShipValue` (`:235`) and `NewLoadoutValue` (`:250`) use it, so
  "Refund", "Net", "Need $X" and the Launch gate are what the server will apply.
- No wire change. Local training has no wallet and is excluded (`trackFreeHardware: false`).

Known limits of the client's record (it cannot see server-side provenance). In the first two the shown
refund is lower than the real one, never higher:

1. A gunner promoted into a paid ship sees the hull as free.
2. A session that rejoins a ship bought earlier sees it as free until its next buy.
3. If the dock restock upgrades a **free** part to its successor, the server keeps it free but the client
   sees a new part type and counts it as paid — here the shown refund is too high by that part's price.
   It needs a priced spawn part whose successor becomes available; see Hand-offs.

## 4. Tests

New tests, all green (counts in section 6).

**Live loopback** — `tests/Allegiance.ClientCore.MonoGame.Tests/P1017BuyFlowLiveTests.cs`: a real
in-process server, real client sessions, the shipping `ShopModel` + `LoadoutBuyFlow` driven as `Game1`
drives them.

| Brief item | Test | What it proves |
|---|---|---|
| unaffordable buy refused with the native message | `Unaffordable_buy_is_refused_with_the_native_message_Live` | Status "You can't afford this loadout.", `Need $X`, Launch disabled. Launch sends nothing. Buy sends `REQUEST_MONEY` for the shortfall; server ship and wallet untouched. Then a buy sent while the wallet drops: the server refuses, the shop shows the native text, pending loadout = the unchanged ship, cost 0. |
| revert after a failed buy | `Part_the_station_does_not_sell_is_dropped_and_the_shop_follows_the_ack_Live` | The side loses the tech while the buy is on its way. The shop shows "Some of the parts you requested are no longer available", pending = real ship, wallet moved by exactly the value the shop now shows. |
| hull change boots a gunner and an observer | `Hull_change_boots_a_gunner_and_an_observer_Live` | Three clients; the pilot changes to a hull without turrets; both passengers are off on the server (lifepod, docked) and on both clients. |
| (same) | `Same_hull_buy_boots_the_gunner_whose_turret_lost_its_gun_Live` | Same hull, turret gun sold: the gunner leaves; the observer leaves too (native's NA / first-cargo-mount comparison). |
| bought parts arrive fully mounted | `Bought_parts_arrive_fully_mounted_Live` | A request with every mount fraction 0: server parts at 1.0, ack and shop at 240. |
| spawn-ship refund shown = refund applied | `Spawn_ship_refund_shown_is_the_refund_applied_Live` | Spawn ship with $800 list value: shown refund $0; keeping it with an empty wallet is granted and free; trading it in moves the wallet by exactly the shown amount; bought again it costs and later refunds exactly the shown amounts. |
| money change while docked flips Need $X ↔ Buy | `Money_change_while_docked_flips_need_and_buy_Live` | Two clients. Nobody can cover: Buy disabled. Teammate gets rich: Buy asks. Teammate donates (`PLAYER_DONATE`): `Need $X` → `Buy`. Buyer gives $1 away: `Buy` → `Need $1`. $1 back: the buy goes through for the shown cost. |

**Server, real handler** — `tests/Allegiance.Core.Tests/P1017LoadoutBuyServerTests.cs`: gunner re-seating
by turret number across hulls with different fixed-gun counts; a gunner with a turret but no gun stays on
a hull change; everybody leaves a hull without turrets; same-hull rule incl. the observer / cargo-slot
case in both directions; no check on a hull without turrets; arming for fractions 0 / 17 / 255 incl.
cargo; free hardware (kept free with an empty wallet, extra part refused then paid, refund only of what
was bought, free hull never refunded); `FreeHardware.Kept`.

**Client, pure** — `tests/Allegiance.ClientCore.MonoGame.Tests/P1017BuyFlowTests.cs` (the four native
states → status line, button labels and inks; Launch / Buy decisions; completed, incomplete,
money-refused and unasked answers; the flow reading its own answer from the ledger; free-hardware
pricing; refusal ink distinct in Classic and Modern) and
`tests/Allegiance.ClientCore.Tests/P1017OwnHardwareLedgerTests.cs`.

One existing test changed: `LiveTeleportDockTests.Teleport_with_non_buyable_hull_refunds_strips_and_swaps_to_lifepod`
pinned "the spawn gun is charged for on the first buy" (server change 3); it now sells the spawn gun first
and then buys a gun. See section 6.

**Real window** — `tools/p1014-ui-proof` with the new `--buy` mode: production `Game1`, loopback server,
Xvfb + software GL (recipe of `SCREENSHOT_DEBT_P1019.md`). 16 checks per run, all true, in Classic
1280×720 and Modern 1920×1080 (`P1017_PASS`); the real Buy and Launch buttons are clicked and the
server's ship and wallet are checked after each step. Six PNGs kept in `docs/reports/P1017_assets/`, each
viewed:

| File | Shows |
|---|---|
| `classic-1280x720-p1017-1-cannot-afford.png`, `modern-1920x1080-p1017-1-cannot-afford.png` | $100, a $1,200 loadout pending: `Need $1,100` on Buy, Launch dimmed, `Shortfall $1,100`, status "You can't afford this loadout. Asked your team for $1,100." in the refusal colour. |
| `…-2-server-refused.png` (both) | The server refused the buy: status "Some of the parts you requested are no longer available", the screen is back on the real ship, `Net $0`, money untouched. |
| `…-3-bought.png` (both) | The buy went through: money $998,800, `Refund $1,200`, `Owned`, Buy dimmed, status "This is your current loadout." |

What the screenshots also show, not caused by this story: the docked spawn ship lists every slot as
`<empty>` (see Hand-offs), and with every tech unlocked the hull column repeats names (`Corvette` ×6).

## 5. Parity rows

- `docs/parity/parity-matrix.csv`: "Hull change boots observers and gunners…" missing → **done**;
  "Every bought part is armed" partial → **done**; new **done** rows "Hardware nobody paid for has no
  trade-in value" and "The purchase answer updates the loadout screen"; new **missing** rows "Launch
  only when everything was bought" and "A part no longer for sale is re-used from the old ship".
- `docs/parity/p1017-buy-flow.csv` (new, 7 rows): Launch, Buy, state text, button states, Confirm
  change, money refresh, purchase answer.
- `GLOSSARY.md`: "Loadout buy / launch flow (P1017)".

## 6. Gate

- `dotnet build AllegianceCore.slnx`: **0 warnings, 0 errors**. `tools/build-tools.sh`: 140 / 140 tool
  projects. `scripts/parity-scorecard.py` and `tools/check-glossary-links.py`: OK. Hygiene scan of every
  changed and new file (home paths, e-mail, key patterns): 0 hits. (`scripts/publish-guard.sh` checks
  commits; there are none.)
- **New tests alone, three times: 32 passed / 0 failed / 0 skipped each time** — Core 9, ClientCore 5,
  MonoGame 18 (7 live, 11 pure).
- Neighbouring suites before the sweep (shop, loadout, hangar, P1006, P1012–P1016, P996, P803, P748 and
  the server loadout / board / turret / restock classes): MonoGame 453 / 0 / 0, Core 309 / 0 / 0.
- **Fast suite, run twice.**
  - Sweep 1: **14,503 passed / 5 failed / 7 not executed** of 14,515 (554 s). One failure was mine:
    `LiveTeleportDockTests.Teleport_with_non_buyable_hull_refunds_strips_and_swaps_to_lifepod` bought the
    spawn gun's own part type and expected to be charged for it — exactly server change 3. The test now
    sells the spawn gun first (refund 0) and then buys; it passes 3/3 alone. The other four are ledgered
    flakes (alone ×3: three pass 3/3; the auto-seat boundary test fails 2 of 3, as ledgered, and has no
    buy in it).
  - Sweep 2, after that adaptation: **14,506 passed / 2 failed / 7 not executed** (551 s). Both are
    ledgered flakes, different from sweep 1's, and pass 3/3 alone.
  - `Allegiance.ClientCore.MonoGame.Tests`: 5,463 passed, 0 failed in both sweeps.
  - 5 and 6 cases over the Fast time limit, none in the buy path. The runner reports both sweeps as
    FAIL. Details: `docs/TEST_FLAKES.md`, 2026-11-14 P1017 entry.
- Real-window proof: `P1017_PASS` in Classic 1280×720 and Modern 1920×1080, 16 / 16 checks each; six
  PNGs viewed.
- **Not run:** the Live training gate (`tools/run-training-tests.sh --no-build --repeat 3`). This story's
  gate is the light one and P1012 measured that run at well over two hours in this lane. The hangar
  Launch path that training shares goes through the new flow; the headless training tests in the Fast
  suite pass, and free-hardware tracking is off for local training. The reviewer should run the training
  gate where it fits.

## Hand-offs

- **Server, not changed (new `missing` parity rows):**
  - Native launches only when everything was bought (`FedSrv.CPP:6247`); the port launches whenever
    `fLaunch` is set, also after dropping a part. The client is ready for the native behaviour (a launch
    not performed releases the screen and shows the message).
  - Native re-uses a part the ship already carries when the station no longer sells it
    (`Igc/shipIGC.h:1044-1096`); P441 chose not to port that. The two belong together: without re-use, a
    ship whose own parts are not sold at the station could never launch under the first rule.
- **Dock restock and free parts:** the restock keeps a successor-upgraded free part free
  (`ServerHost.cs`, `MountPart(successor, …, part.FreeSpawn)`); the client cannot see that (limit 3 in
  section 3). A fix needs the successor chain in the ledger's match.
- **The seat acknowledgement carries no parts** (`Server.Lobby.cs`, `Parts = []` outside local training)
  and the first dock sends no dock sync, so a freshly joined player's loadout screen shows every slot
  empty whatever the server ship carries. The dock-sync acknowledgement also leaves out the cargo hold
  (`Server.Combat.cs`, `ship.Parts` only). Both predate this story; P1012 noted the cargo side.
- **P1018 (integration):** the `--buy` proof mode lives next to the P1015 script in
  `tools/p1014-ui-proof`; the walkthrough should include a refused buy. The status line shares the
  footer band with the key hint; if the docs page shows the footer, both lines are there.
- **P1018 / later:** native's "can anyone on the team afford it" uses every ship on the side; the port
  uses the wallets it has been told about (`WalletByShipId`). With no teammate known the Buy button
  stays live.
- **Training gate:** not run here; see section 6.
- **`LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds`** fails 2 of 3
  alone again (ledgered, unrelated to the buy path); it still needs its own task.

## Native intent

The hangar is a shop with one rule: a ship is traded in for what was paid for it, and the server has the
last word. Launch is "buy this and go" and is simply unavailable when the money is not there; Buy is the
polite version that can ask the team. When the server could not deliver, the player is told once and
shown the ship they really have. Crew ride along only while there is a seat for them.

## Modern approach

The same rules as one small pure flow shared by both screens, with the server's answer fed back into the
one shop model every frame instead of a modal message box. Refusals are a line on the screen in native's
words, next to the button they concern. Money and ship value are always the server's: the client keeps a
ledger of what it never paid for, so the price it shows is the price it will be charged.
