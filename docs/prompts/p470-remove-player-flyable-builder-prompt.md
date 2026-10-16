# P470 — remove the player-flyable Builder (P285), keep native behaviour

## Why this task exists

Domingo's decision (2026-09-29): **builders are AI drones only, as in
native.** The 2026-09-29 parity pass found that P285 (`4f4a3ec9`, "player
chooses a Builder ship's target station type at purchase") is a port
invention. In native, a constructor exists only when a team funds a
station-type bucket. The server then spawns an AI constructor drone
(`FedSrv.CPP:11342-11367`), and players never fly one. P458, which runs
earlier in this chain, adds that native path. Check that P458's commit
exists (`git log --oneline | grep P458`). If it doesn't, **stop and
report**, because removing P285 without it would leave no way to build
stations.

What P285 added, all of which goes:
- A port-only C→S wire message, `SET_BUILDER_TARGET` (fmid 201). This
  includes its codec (`src/Allegiance.Core/Wire/SetBuilderTarget.cs`), the
  server dispatch in `Server.Dispatch.cs`, and the handler
  `SetBuilderTargetOnShip` (`src/Allegiance.Server/Server.ShipCommands.cs`,
  ~2020-2063).
- Client sending (`ConnectionSession.cs`), plus the shop's builder-target
  chooser: `ShopCatalog.IsBuilderHull` / `BuilderTargetsFor` /
  `IsBuilderConstructible` (`Rendering/ShopCatalog.cs` ~131), `ShopModel`
  builder-target state, `ShopLayout` "BUILDER TARGET" rows, and the `,`/`.`
  cycling and send-on-launch in `Game1` (for example `Game1.Panes.cs`).
- Test-bot support (`src/Allegiance.Bot/Bot.cs`) and the
  `P285PurchaseBuilderTargetLiveTests`.

**Why a player can buy a Builder hull at all.** Native's hangar lists a
hull only when
`pht->GetGroupID() >= 0 && pstation->CanBuy(pht) && !pstation->IsObsolete(pht)`.
See `WinTrek/load.cpp:1603-1606` and `:1817-1820`, and the budget-picker
variant at `clintlib/clintlib.h:1858`. Drone-only hulls are kept out by a
**negative group id**. The port decodes `GroupId` as an unsigned `byte`
(`src/Allegiance.Core/Data/DataHullTypeIGC.cs:20,332`, `b[261]`). If
native's `BuyableGroupID` is a signed char, which you must verify (its
typedef isn't found with a simple grep; look through the headers and
struct layout), then a native `-1` decodes as `255` and the `>= 0` filter
can't be expressed. Establish this from the real `PCore001.igc` data: list
each hull's raw group byte and its capabilities, and check whether the
constructor and miner hulls are the ones with a negative (0xFF-style) id.

## Scope

1. **Player hull filter matches native.** Decode or interpret the hull
   group id with native's signedness, and apply native's `GroupID >= 0`
   rule. Apply it in the client shop's hull list (`ShopCatalog`) and on the
   server's `BUY_LOADOUT` hull acceptance. P441 already added tech and funds
   checks there, so extend the same gate and don't add a parallel one. The
   server must **refuse** a non-player-buyable hull the same way it refuses
   an un-buyable one. If the real data shows the group id does *not*
   separate drone hulls, stop and report what does, with evidence. Don't
   invent a capability-bit filter native doesn't use.
2. **Remove P285 entirely**, as listed above: the wire message, codec,
   dispatch, handler, client send, shop chooser UI, key bindings, bot
   support and tests. Leave no dead code behind, and free fmid 201.
3. **Keep** everything native: the P163-P165 BuildingEffect mechanic,
   `Ship.BuilderStationType` (now set by P458's drone spawn), the
   P165 builder AI, and P425's commanding of builder drones from the
   MonoGame client (`CommandOrderResolver.cs`). Confirm with a test that a
   bucket-spawned constructor drone still builds its station end to end.

## Explicitly out of scope

- Changing P458/P459's drone spawning or the builder AI.
- Miner hulls. If the step-1 filter also removes player-buyable miners,
  that is native-correct: note it in the report and keep it.
- Any other P285-adjacent feature not listed above. If you find one,
  report it rather than removing it.

## Verification

- Tests that fail on unfixed code first:
  - a player BUY_LOADOUT for a real constructor hull is refused;
  - the shop catalogue does not list it;
  - a normal player hull is still buyable.
- The drone-builds-station end-to-end test from step 3 passes.
- `grep -rn "SetBuilderTarget\|BuilderTargetsFor\|IsBuilderConstructible" src tests`
  returns nothing.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full `/home/domingo/.dotnet/dotnet test`, 3
  consecutive clean runs. Only the pre-existing documented live-wire timing
  flakes are acceptable, and each one seen must be named.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. Report doc `docs/reports/P470_REMOVE_PLAYER_FLYABLE_BUILDER.md`. Cover
   the group-id signedness finding with the real per-hull data, what was
   removed, and the end-to-end drone-build proof.
