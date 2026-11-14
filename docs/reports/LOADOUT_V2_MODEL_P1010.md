# P1010 — Loadout V2: view-model over ShopModel

Story P1010 of the *Loadout V2* epic (mockup `claude.ai/artifact/TLJFmmdgRefCgM8pGdZQ75`,
Domingo 2026-11-14). This story is the **model only**: a pure
`LoadoutV2Model` that the V2 screen (P1014) will render, wrapping the existing
`ShopModel` so the two screens can never disagree about what may be bought,
mounted, defaulted or spent. No screen, input or draw code is touched here, and
the flag `visuals.hangar.loadoutV2` is not introduced until P1014 — the flag-off
path (today's three-page shop) is byte-identical.

## Findings

| Finding | Classification | Native evidence |
| --- | --- | --- |
| The shop can only list the hulls the station already offers; there is no way to show a player *what they could unlock* or why a hull is locked. | Missing port capability (model) | Native's hull list walks the core's hull list and takes a hull only when `pht == phtCurrent || (GroupID >= 0 && pstation->CanBuy(pht) && !pstation->IsObsolete(pht))` — `WinTrek/load.cpp:1600-1609`, predicate `:1603-1606`. `CanBuy` is the tech containment `b->GetRequiredTechs() <= (m_myStationType.GetLocalTechs() \| s->GetTechs())` (`Igc/stationIGC.h:383-391`) and `IsObsolete` is "a buyable successor exists" (`:393-397`). |
| Nothing tells the player which hulls their side's tech tree can *ever* reach (native's `ttbmLocalUltimate`), so a locked row could only guess. | Missing port capability (model) | `CsideIGC::CreateBuckets` seeds `m_ttbmUltimateTechs` from the civ's dev techs, ORs every part type's and station type's effect techs, closes over affordable developments, then ORs every station type's local techs — `Igc/sideigc.cpp:119-206` (`:142` local, `:148-172` dev closure, `:206` union). The port already has this as one function: `BucketCatalog.ReachableTechs` (`src/Allegiance.Core/Model/Bucket.cs`, P458). |
| The shop's slot machinery is tab/page-driven (`OpenSlotAtCursor` + `ChoosePartInOpenSlot`) and has no way to ask for a slot's offers without opening it, no way to pick a hull by id, and no "back to the docked loadout" action — all three are what a one-screen `hulls \| ship \| slots` layout needs. | Missing port capability (model API) | Slot admission is `Slot::SlotValidForHull` — `NA` always valid, `ET_Weapon` mounts below `GetMaxFixedWeapons()`, `ET_Turret` mounts below `GetMaxWeapons() - GetMaxFixedWeapons()`, every other type only where `GetPartMask(type, mount) != 0` (`WinTrek/load.cpp:112-122`), applied to the authored slot table at `:2023-2044` (`defaultloadout.mdl` / `loadout.mdl:31-54`: weapons 0-3, turrets 0-3, magazine/afterburner/shield/cloak/dispenser/chaff, cargo). A turret's mount is the relative slot plus `GetMaxFixedWeapons()` (`:104-110`) and its gunner is drawn from `GetGunner()` with `<open>` when unseated (`:176-205`). The offer filter is `LoadoutImpl::CanMount` plus the per-mount mask in `BuyPartMenu` (`:1656-1678`, `:1734-1743`), over the station's `CanBuy` set (`:1714-1717`). |
| Money is *not* a buyability lock in native, so a "locked because poor" reason would be an invention. | Native design (parity note) | Native hull rows carry name + price only (`GetBuyableName`, `load.cpp:18-37`); affordability is the buy button's business — `OnButtonBuy` computes the shortfall `cost = new - (current + money)` and asks the server for it (`:1513-1520`), and the buy button's enable test is the wallet (`:2139-2182`). The model therefore exposes affordability as a row flag and the shortfall as the "Need $X" value, never folded into `CanBuy`. |

The "building" reason in the brief is real for native but only through the
station path: `Station::CanBuy` is tech-only for hulls/parts, while the side's
`CanBuy` loop over stations exists for station/drone buyables
(`IsideIGC::CanBuy`, `Igc/sideigc.h:224-272`, any station's local techs), and a
side's **building** techs are the built stations' effect techs
(`sideigc.h:140-158`). A hull's missing bit therefore has three real sources,
all of which the reason text names: a researchable development ("research X"), a
station type's **local** techs ("a X station" — this station does not sell it,
that one does), or a station's **effect** techs ("building X"). A bit carried
only by a part's effect mask reads "part X"; the raw index is the last resort.

**Citation correction for the epic.** The epic's note "slots per mount + gunners
`:183,:342`" points at the gunner *display* (`GetGunner` inside `Slot::Paint`,
`WinTrek/load.cpp:182-202`) and the `m_pstringGunners` member array (`:342`), not
at the slot construction. The construction is the authored slot table walk
`WinTrek/load.cpp:1993-2048` (validity at `:2023`, `new Slot` at `:2030`), and the
per-hull slot set is `Slot::SlotValidForHull` `:112-122`. This story cites those
re-grepped lines.

## Root cause (base)

- **Base** `Rendering/ShopModel.cs` exposed only page/cursor navigation
  (`OpenSlotAtCursor`/`ChoosePartInOpenSlot`, `:342-420`), a per-tab slot list
  `Slots` (`:281-308`) that omits the pack socket and orders sockets by its own
  sequence, and no revert/select-by-id entry points. Any V2 screen written
  straight onto it would have had to duplicate the offer filter (and could
  drift from the old screen's rule) or drive the tab state machine.
- **Base** `Core/Model/Bucket.cs` kept the native `ttbmLocalUltimate`
  transcription (`ReachableTechs`, `:677`) private, so a client model could not
  ask "could this side ever unlock hull X?" without re-deriving the mask.

## Fix

New pure model — `src/Allegiance.ClientCore.Game/Rendering/LoadoutV2Model.cs`
(726 lines, no `GraphicsDevice`, headless-testable):

- **Hull column** (`HullRows`, `:260`): the offered prefix is
  `ShopModel.BuyableHulls` in native `GetNextHullType` order — the order
  `ShopModel.CycleHull` already walks (`load.cpp:1568-1636`); the locked tail is
  `ComputeLockedHulls` (`:281`): `GroupId >= 0`, not obsolete
  (`SuccessorUpgrade.HullIsObsolete` = `stationIGC.h:393-397`), not buyable by
  the current `ttbmLocal \| side techs` (`stationIGC.h:383-391`), yet inside the
  side's reachable tree (`BucketCatalog.ReachableTechs`), i.e. the hulls the side
  could unlock. Each row carries the core name, the hull's armour class from the
  core's defence-constant table (`CoreTable.ConstantsDamageComment`,
  `Igc/missionigc.cpp:4246-4299`) — no invented class names — its price, the
  marked current/chosen hull and the shop's grey-out affordability. Lock reasons
  (`LockReasonFor`/`MissingPhrase`, `:342-412`) name the real source of each
  missing bit.
- **Ship column** (`SlotRows`, `:428`): fixed weapon rows, turret rows (labelled
  `TURRET n` in native's separate turret mount space, `load.cpp:104-110`), then
  one row per non-zero `PmEquipment` socket in native `ET_*` order
  (`Igc/igc.h:530-538`), pack included. Rows carry the chosen part (pending
  loadout) and the docked ship's real part, the amount, and — turret rows only —
  the gunner name from a caller-supplied resolver (null = open seat,
  `load.cpp:176-205`).
- **Offers** (`OffersFor`, `:537`): the shop's own filtered list, each offer
  with price, amount, the "already mounted on the docked ship" marker, the
  "chosen" marker, and the whole `DataPartTypeIGC` so P1011 can derive stats.
- **Actions**: `SelectHull` (`:569`), `OpenSlotFor`/`CloseSlot`,
  `ChooseOffer` (`:616`, `:639`), `ClearSlot` (`:661`), `ApplyDefault` (`:671`),
  `Revert` (`:680`), `NextHull`/`PreviousHull` — every mutation calls
  `ShopModel`; the price box (`PriceTotal`/`CurrentValue`/`Cost`/`MoneyNeeded`/
  `CanAffordTotal`) and `BlockReason` are read straight off it.

`ShopModel` gained only what was missing, all behaviour-neutral for the old
screen (`src/Allegiance.ClientCore.Game/Rendering/ShopModel.cs`):

- `SlotOffers(slot)` (`:398`) — the offer predicate; `OpenSlotParts` is now this
  same call for the opened slot (identical list, identical order).
- `MountInSlot(slot, offer)` (`:501`) — pick a part into an explicit slot after
  re-checking it is one of that slot's offers; raises `SlotPartChosen(true)`
  like the tab path (`load.cpp:1887`).
- `ClearSlot(slot, notify = false)` (`:479`) — default keeps every existing
  caller silent; the open-slot menu now passes `notify: true` for the unmount
  cue (`load.cpp:1889`) and V2 does the same.
- `SelectHull(hullId)` (`:291`) — targeted form of `SelectHullAtCursor`; only a
  hull the station offers is selectable, the docked hull de-picks (native
  `NextHull`'s `pht == phtCurrent` branch, `load.cpp:1620-1625`), a real pick
  rebases the loadout and raises `HullChosen` (`:1480-1487`).
- `RevertToCurrentLoadout()` (`:946`) — the constructor's docked-loadout seeding
  applied again after edits, with the pending swap dropped.
- `PartById(partId)` (`:962`) — the shop's own part resolution (filtered
  catalogue + real launcher table), so the V2 rows never need a second copy.

`src/Allegiance.Core/Model/Bucket.cs:683` — `BucketCatalog.ReachableTechs` is now
public (same body, same callers) so the client reuses the port's one native
`ttbmLocalUltimate` transcription instead of re-deriving it.

Related decision recorded for review: `ShopModel.Slots` is deliberately left
unchanged (the old screen's rows), and `SlotRows` is the V2 column — a superset
(pack socket, fixed/turret split, native `ET_*` socket order) that still
contains every slot the old screen lists (pinned by a test). Native's authored
slot table has 4 fixed + 4 turret entries (`loadout.mdl:31-54`); no retail hull
exceeds either bound, so covering every hardpoint below `MaxWeapons` (the
`SlotValidForHull` rule) never adds a slot native could not show.

## Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/LoadoutV2ModelTests.cs` — 15 tests,
real `PCore001.igc` under the artwork dir, docked at **Gigacorp's own station
type** (civ 18 → station `Garrison` #15) with a **fresh side** (the civ's base
techs); `[SkippableFact]` + `Require.Artwork()` so a clean clone skips:

| Test | What it pins |
| --- | --- |
| `Hull_column_lists_native_order_then_unlockable_hulls_with_reasons` | offered prefix == `ShopModel.BuyableHulls` order; locked tail is non-obsolete, fails `CanBuy`, is inside the reachable mask, has a reason, class, price; Enh Fighter is a research lock; current hull marked once; no raw ids in the display strings. |
| `Selecting_a_locked_hull_is_refused_with_its_reason` | a locked row cannot be selected, the refusal names the hull and its reason, the effective hull and `ChosenHullId` stay put; an unknown id is refused without a crash. |
| `Selecting_an_offered_hull_marks_the_rows_and_pends_the_swap` | selection goes through `ShopModel.SelectHull`; rows re-mark, `WillSwapHull` true; re-picking the docked hull de-picks. |
| `Slot_rows_follow_slot_valid_for_hull_for_every_hull_class` | Enh Fighter (fixed-only), Bomber, Gunship (turret hull) and a plain Fighter: row counts, fixed/turret/socket split, independent `SlotValidForHull` re-implementation, `WEAPON n`/`TURRET n` labels, absolute turret mounts, native `ET_*` socket order, empty rows on a bare hull. |
| `Turret_rows_carry_the_gunner_seat` | turret rows show the resolver's gunner, an open seat stays null, fixed/socket rows never carry one, no resolver = no gunner. |
| `Slot_rows_cover_every_slot_the_old_screen_shows` | every `ShopModel.Slots` entry is a V2 row (superset, no slot lost). |
| `Offers_per_slot_match_the_native_mount_masks` | every slot of Enh Fighter/Bomber/Gunship: offers == the independent mask-overlap scan over the whole core; name/price/mask come from the core record. |
| `Offers_only_include_parts_the_side_can_buy` | fresh side: every offer is in the station's filtered catalogue, and the filter really bites. |
| `Offers_mark_the_already_mounted_and_chosen_parts` | docked part marked `AlreadyMounted`, frozen at the current ship even after a different part is chosen; the picked one becomes `IsChosen`. |
| `Open_choose_and_clear_a_slot_through_the_shop` | picker open → offers → pick mounts at mount 0 with the offer's amount, price total moves; a wrong type is refused; clear drops it and restores the total. |
| `Default_then_revert_round_trips_the_docked_loadout` | Default (native `OnButtonDefault`) then Revert restores the exact docked hull+parts, zero cost; Revert also cancels a pending hull swap. |
| `Next_and_previous_hull_cycle_the_buyable_list` | Next/Previous visit every other buyable hull once and wrap back. |
| `Price_and_affordability_come_from_the_shop` | `PriceTotal`/`CurrentValue`/`Cost`/`MoneyNeeded`/`AffordabilityKnown` equal the shop's own values, the row grey-out flips with the wallet, `TryLaunch`'s `BlockReason` surfaces through the model, refresh clears it. |
| `Old_screen_open_slot_menu_and_v2_picker_agree` | the old open-slot menu's list == `SlotOffers` == the V2 picker == the independent mask scan (the refactor changed no behaviour). |
| `Empty_catalogue_renders_an_empty_screen_without_throwing` | the `ShopCatalog.Empty` degradation the docked screen already allows: no rows/offers, actions refuse cleanly, no throw. |

The pre-existing shop tests were **not modified**: `ShopModelTests`,
`ShopLauncherCatalogueTests`, `P571DefaultLoadoutTests`, `P764UiSoundSeamTests`,
`P996LifepodHangarRestoreTests`, `ShopLayoutTests`, `PartInfoLayoutTests`,
`P787BShopHoverAndPickTests` all pass unchanged (129 tests, see the gate).

## Gate

- `dotnet build AllegianceCore.slnx --disable-build-servers -nodeReuse:false
  -p:UseSharedCompilation=false` → **0 warnings, 0 errors**.
- `tools/build-tools.sh` (with node reuse and the MSBuild build server disabled via
  `MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1`) →
  **all 138 tool projects built**, no failures.
- New tests 3× alone → **15/15 passed in each of three runs** (195 / 198 / 197 ms).
- The pre-existing shop tests unchanged: **129/129 passed** (ShopModel,
  ShopLauncherCatalogue, P571DefaultLoadout, P764UiSoundSeam, P996LifepodHangarRestore,
  ShopLayout, PartInfoLayout, P787BShopHoverAndPick).
- One full Fast sweep (`tools/run-tests.sh --tier fast`): **14,266 passed /
  6 failed / 7 skipped of 14,279** in 570.5 s; 9/9 infrastructure checks passed.
  Two small additions landed after the sweep: the V2 model's `Revert` now keeps
  `ShopModel`'s page aligned with its tab, and one extra test covers the empty
  catalogue; both are reached only by the new tests, which pass 3× alone on the
  final tree. A second
  sweep was attempted and blocked by a stale test-runner lock in `/tmp` left by an
  earlier lane (no runner process held it; not removed, since `/tmp` is outside
  this lane's writable scope).
  The six failures are all pre-existing ledgered flake families
  (`P902SoundTemplateTests.Asr_and_cache_hit_mix_math_allocate_nothing_after_warmup`,
  `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire`,
  `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn`,
  `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange`,
  `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission`
  (delay 5/7), `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds`),
  each repeated **3× alone**: pass/pass/pass except `MissileLock` (fail/pass/fail, the
  ledgered 38 s receipt signature) and `LiveSideSelect` (fail/pass/pass, the ledgered
  [28,31] clock boundary). None touches the shop/loadout/tech code. Recorded in
  `docs/TEST_FLAKES.md` (2026-11-14 P1010 section). Host load average 17–21 on 16
  CPUs during the sweep.
- HUD/UI: this story adds no draw code; Classic + Modern pixels belong to
  P1014, which the epic gates with screenshots (and the epic's S0 GL blocker is
  still open for the lane containers).

## Hand-offs

- **P1011 (stats)** — `LoadoutV2Offer.Part` and `LoadoutV2SlotRow`'s chosen/
  current part ids are the hooks; `ShopModel.PartById` resolves records; no stats
  are computed here by design.
- **P1012 (carry-over parity)** — hull changes still go through
  `ShopModel.RebaseForEffectiveHull`; `ChosenPartsThatDontFitEffectiveHull` is
  untouched. The V2 model clears its open picker on a hull change; the parts
  carry-over rule itself is P1012's.
- **P1013/P1014 (layout/renderer)** — `SlotRows`/`Offers`/`HullRows` are the
  row data; hulls are ordered offered-then-locked, so the renderer needs no
  second ordering rule. Flag wiring (`visuals.hangar.loadoutV2`) and the
  `HudStyle` palettes are P1014's.
- **P1015 (input)** — `SelectHull`/`NextHull`/`PreviousHull`/`OpenSlotFor`/
  `ChooseOffer`/`ClearSlot`/`ApplyDefault`/`Revert` are the actions; the gunner
  resolver can be fed from `HudInventoryPaneBuilder.SourceShipOccupants`
  (`ParentId`/`BoardTurretId`) when P1016/P1015 needs live seats.
- **P1017 (buy/launch)** — `MoneyNeeded` is the native `REQUEST_MONEY`
  shortfall and `BlockReason` the refusal text; the wire path stays Game1's.

## Native intent

The hangar's hull list is "what this station can sell *you*, now" plus the
pointer its Next/Previous buttons walk (`GetNextHullType`); a hull you cannot buy
yet is simply absent, and the only visible money signal is the buy button's
shortfall. Slots are a fixed authored layout filtered per hull, with turrets as
their own gunner-manned slots and launcher counts drawn in the slot; the offered
parts are exactly the station's buyable parts whose mask that mount accepts.

## Modern approach

One screen shows the same rules as data: the hull column keeps native's order and
adds, behind the same `CanBuy`/`IsObsolete` predicate, the hulls the side's tech
tree can still reach — each with a human reason built from the real research /
station-local / building tech that grants the missing bit, and the armour class
and price as core facts. The ship column lists the hull's real slots (fixed
weapons, gunner turrets with the seated name, equipment sockets incl. pack), and
each slot's inline picker lists exactly the parts native's mount mask and the
station's tech filter allow, marked with price and whether the docked ship
already carries them. Every mutation and every number still comes from
`ShopModel`, so the V2 screen and today's screen cannot disagree.
