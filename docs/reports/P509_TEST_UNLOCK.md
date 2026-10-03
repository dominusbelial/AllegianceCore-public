# P509 — test unlock: grant sides all tech (+ test money) through the real tech path, so every hull and weapon can be playtested

Date: 2026-10-03 · Branch: `master` (committed, not pushed)

## Summary

Domingo's live playtest docked-shop offered **2 hulls / 15 parts / 9 launchers**
(client log `hangar/loadout screen (2 hulls, 15 parts, 9 launchers offered)`).
That is *correct*: P441/P444/P468 made tech gating real, so a fresh side's
garrison sells starter hulls only. Reaching bombers, capital ships and the full
weapon roster requires researching and building techbases — impractical for
feature testing.

P509 adds an explicit, off-by-default, loudly-logged **`liveSettings.testUnlock`**
option (the natural companion to P473's `testHarness`) that, when enabled:

- grants **every side** the core's **required-tech union** (every `TtbmRequired`
  bit any real core buyable needs) **through the real tech path** — OR-ed into
  `Side.DevelopmentTechs` (the same `ApplyDevelopmentTechs` OR research uses,
  sideigc.h:194-210), so clients receive `SIDE_TECH_CHANGE(75)` and the shop
  (P468) + investment screen (P487) agree with the server's buy-gate (P441/P444/P283);
- grants **every seated player wallet** a test `money` at seat (after P443
  starting money) so purchases are affordable — purchases still pay the real
  price through the real debit path;
- is **buyability-only**: no development is marked completed, so no
  global-attribute delta (`ApplyGlobalAttributeSet`) is applied and
  **flight/combat stats stay unmodified** (the task's stated default).

Measured effect on the real `PCore001.igc` + `XXV_simple.igc`: the garrison's
offered hull count goes from the **starter set (2)** to the **full player roster
(198)**, and a seated Bot buys a real tech-gated **capital-class hull
(Flagship.4, 10 000 cr) + a gated weapon** through `BUY_LOADOUT`, both granted
and charged.

## Why P473's harness isn't enough

P473's `testHarness` skips the tech gate **on the server** for `mcp-`-prefixed
connections only. The client shop's tech filter (P468, fails closed) still hides
every item the side lacks tech for — so a human would get money but **still not
see the hulls**. The unlock must grant the **side** real tech through the same
path research uses:

- P189's `CompleteDevelopment` ORs bits into `Side.DevelopmentTechs` and
  broadcasts `SIDE_TECH_CHANGE(75)` / `DEV_COMPLETED(8)` / `SIDE_ATTRIBUTE_CHANGE(76)`.
- `CanBuy` / `StationSellsHull` check `TtbmRequired ⊆ station.TtbmLocal | side.Techs`
  (P283/P441/P444).

Then the server, the shop (P468) and the investment screen (P487) all agree.

## Native reality (why this is a port extension)

Native has **no "unlock all"**. Its mission params only *restrict* tech:
`bAllowDevelopments` (`Igc/sideigc.cpp:117`, `FedSrv/fsmission.cpp:1948`,
client `clintlib.h:548`) and `bAllowSupremacyPath` / `bAllowTacticalPath` /
`bAllowExpansionPath` (`Igc/asteroidigc.cpp:183-195`). Native is at
`~/Development/Allegiance/src/`. So this is a **port extension for testing**, in
the same spirit as P473's `testHarness`: explicit, off by default, loudly logged.

## 1. Config shape — `liveSettings.testUnlock`

Added `TestUnlockConfig` under the existing `LiveSettingsConfig` (P378's one
growable home for runtime/testing knobs):

```jsonc
"liveSettings": {
  "debugMode": true,
  "testUnlock": {
    "enabled": true,    // off by default; absent == no grant (byte-identical to P441/P444/P468/P488)
    "allTech": true,    // when true (default), grant the full required-tech union; when false, money only
    "money": 1000000    // wallet set absolutely at seat (refreshes the P443 seed)
  }
}
```

- Registered in `ServerJsonContext` (`[JsonSerializable(typeof(TestUnlockConfig))]`)
  so it survives Native AOT config binding.
- Production / example configs (`docker/compose.server.json`,
  `docker/compose.override.debugserver.yml`) are untouched — no grant there.
  Domingo decides whether the 221 run uses it.

## 2. The required-tech union — `StaticCore.ComputeRequiredTechUnion()`

Rather than blindly setting every bit of the 400-bit space, P509 derives the
grant **from the core**: `StaticCore.ComputeRequiredTechUnion()` ORs every
**hull, part, launcher, station-type, drone and development `TtbmRequired`**.
Notably:

- **Launchers** contribute the tech their *built* mountable part carries (the
  expendable's `LauncherTtbmRequired`, via `TryBuildExpendableLauncher`) — the
  exact requirement the shop's launcher offer and the server's BUY_LOADOUT gate
  check, so a granted side can actually buy every real launcher/dispenser.
- **Development `TtbmRequired`** (a development's research prerequisite) is
  included so the investment screen agrees every development is researchable.

Measured on `PCore001.igc`: the union is **281 bits** (a fresh side's base-tech
mask is **28 bits**).

## 3. Server-side grant

`Server.ApplyTestUnlock(Mission)` (`Server.Lifecycle.cs`), called from
`AssembleGameMission` — which both **boot** and the **P439/P480 game-over
rotation** call, so every side's `DevelopmentTechs` carries the union before
ANY joiner seats (thus `CREATE_BUCKETS` at join carries the full mask and the
shop/investment agree from the first tick). For each side it:

1. ORs the union into `Side.DevelopmentTechs` (`before.Or(union)`, the identical
   byte OR native `ApplyDevelopmentTechs` performs on research, sideigc.h:194-210),
   logging one `[testunlock] side N '...': GRANTED all NNN required-tech bits
   (dev mask A -> B)` line per side;
2. broadcasts `SIDE_TECH_CHANGE(75)` to the side's connected peers whenever any
   are present (via the existing `BroadcastSideTechChange`) — so an
   already-connected client (e.g. a P480-migrated player) sees the mask through
   the real tech path even though it did not arrive via CREATE_BUCKETS at join.
   `RotateMissionAfterGameOver` re-calls `ApplyTestUnlock(fresh)` after peer
   migration for exactly that re-announce; it is idempotent (re-ORing an already
   granted mask changes nothing, so no spurious new-tech log lines).

**Buyability-only decision (the task's choice, cited):** P509 does **not** mark
any development completed. Native `DevelopmentCompleted` (FedSrv.CPP:11259-11276)
does three things: apply the effect techs (`ApplyDevelopmentTechs`), broadcast
`DEV_COMPLETED`, and — unless the dev is tech-only — apply the attribute delta
(`ApplyGlobalAttributeSet`, which fires `SIDE_ATTRIBUTE_CHANGE`). P509 ORs the
union (step 1 of that flow) but **never invokes `ApplyGlobalAttributeSet`**, so
developments with global-attribute effects are **not** marked complete and
**flight/combat stats stay unmodified** — exactly "unlock buyability only". No
`DEV_COMPLETED` is emitted either (nothing was researched).

## 4. Seat-time wallet grant

In `CompleteJoinAndLaunch` (right beside P473's harness grant),
`live.Wallet.Money = money` when `testUnlock.enabled`, logging one
`[testunlock] <peer> '<name>' wallet SET to $N at seat` line per connection —
so every seated player (human or bot) gets the money at seat, and the join-time
`SET_MONEY` sync reflects it. Purchases still pay the real price through the
real debit path.

## 5. Still faithful where it matters

- **P470 `GroupId >= 0` drone-hull gate is untouched**: players still cannot buy
  drones — the grant gives them the tech, it does not relax the drone rule
  (the live test's capital selection explicitly requires `GroupId >= 0`, and the
  shop test asserts the full offer contains no drone hull).
- **P488 `IsObsolete` still hides out-teched items**: granting all tech makes
  successors visible and their predecessors **obsolete** in the shop offer. On
  `PCore001.igc` this hides **97 of the 295** `GroupId >= 0` player hulls from
  the shop offer (the "full roster" is 198). This is **reported, not hacked
  around**: P488's semantic is that you buy the successor, and every hull family
  is represented by its best buyable member. If a tester specifically wants a
  predecessor hull in the shop too, that is a P488 follow-up, not something P509
  should suppress.
- When the option is **off** (absent section), the code path is **byte-identical
  to today**: no tech granted, no wallet money, for anyone — proven by the
  grant-off live test (same capital hull refused, wallet 0).

## 6. Committed configs

`docker/compose.server.debugmode.json` and
`docker/compose.server.debugmode.mapgen.json` now enable `testUnlock`
(`enabled: true, allTech: true, money: 1000000`). `docker/compose.server.json`
(production) is unchanged — no `testUnlock` there.

## 7. Verification

### Failing-first tests

- **RED:** with the grant wiring temporarily removed (the `ApplyTestUnlock`
  call in `AssembleGameMission` and the seat-time wallet statements commented
  out), `GrantOn_SideHasUnion_BotBuysCapitalAndGatedWeapon_ChargedRealPrice`
  **FAILS** (side lacks the union / wallet is 0 / buy refused) — proving the
  grant test discriminates the feature. Reverted; no `TEMP` markers remain.
- **GREEN:** both new tests pass with the wiring in place (below).

### Live wire test — `tests/Allegiance.Core.Tests/TestUnlockGrantLiveTests.cs`

Two loopback proofs over a real in-process server + a real TCP Bot
(`PCore001.igc`, `XXV_simple.igc`, teams 2, docked at the friendly garrison):

- **Option ON** — `GrantOn_SideHasUnion_BotBuysCapitalAndGatedWeapon_ChargedRealPrice`:
  - the side's `DevelopmentTechs` **contains the 281-bit union** (starter was 28);
  - the seated wallet = `1000000` at seat;
  - the docked Bot buys a real **tech-gated capital-class hull** (Flagship.4,
    id 73, 10 000 cr — tech-gated at starter, sellable with the union, terminal
    so P488 buys it exactly) **plus a gated weapon** (PE SkyCap family, granted
    as its P488 successor SkyCap 3) through `BUY_LOADOUT`;
  - both are **granted** (ship hull = flagship, a weapon is mounted that is
    buyable with the granted union) and **charged the real price** (wallet
    `1000000 -> 990000`, debit = flagship price + mounted part prices).
- **Option OFF** — `GrantOff_SideKeepsStarterTechs_SameCapitalRefused`: the same
  side keeps its starter 28-bit mask (union NOT granted), wallet stays 0, and
  the same capital hull is **refused** (ship unchanged).

Server log excerpt (option ON):

```
[boot] *** TEST UNLOCK ACTIVE (liveSettings.testUnlock.enabled=true) *** allTech=True money=1000000 — ...
[testunlock] side 0 'Alpha': GRANTED all 281 required-tech bits (dev mask 28 -> 281)
[testunlock] side 1 'Bravo': GRANTED all 281 required-tech bits (dev mask 28 -> 281)
[testunlock] 1 'unlock-bot' wallet SET to $1000000 at seat (testUnlock enabled)
UNLOCK-ON-LIVE: side 0 'Alpha' dev mask 281 bits ⊇ union 281 bits (starter was 28)
UNLOCK-ON-LIVE: bot 8098 bought capital 73('Flagship.4 [AsCr]',10000) + gated weapon 37('PE SkyCap 3',0) at garrison -> wallet 1000000->990000 (debit 10000)
```

### Client-level shop test — `tests/Allegiance.ClientCore.MonoGame.Tests/P509TestUnlockShopVisibilityTests.cs`

Loads the real `ShopCatalog` + real `XXV_simple.igc`, feeds `FilteredFor` the
side's development mask that P509's grant produces (the required-tech union)
vs the fresh side's starter base-techs, and reports the offered hull counts:

```
P509-SHOP: XXV garrison offered hulls starter=2 -> full=198 (all GroupId>=0 player hulls=295, obsolete-hidden=97); dev mask starter=28 bits -> union 281 bits
```

So the garrison's offered hull count goes from the **starter set (2 — matching
Domingo's live log) to the full player roster (198)**.

### Build

`/home/domingo/.dotnet/dotnet build` (whole solution): **0 warnings, 0 errors**.

### Full suite, 3 consecutive runs

`scripts/run-tests.sh` (P471), 3 consecutive runs (each builds once, then runs
the four test assemblies sequentially). **Measured** (only numbers actually
observed):

| run | Lobby | ClientCore.MonoGame | ClientCore | Core | failed (all pre-existing, cited in `docs/TEST_FLAKES.md` §P509) |
|---|---|---|---|---|---|
| 1 | 12/12 | 1208/1208 | 417/418 | 3703/3708 | DockOut (P482 residual); P366Leader seat; ShipAsteroidCollision.Loopback; ShipsUpdate.Ship_leaving_cluster; GameOverPlayers.conquest_leak; AddPart.pickup_decline |
| 2 | 12/12 | 1208/1208 | 418/418 | 3705/3708 | ShipAsteroidCollision.Loopback; LoadoutChange.Boarding_leaving; CloakVisibilityGate |
| 3 | 12/12 | 1208/1208 | 417/418 | 3707/3708 | FullSessionKill.RealCrossSideKill; SideElimination.Eliminating_side2 |

P509's own tests passed in **all three runs** (`TestUnlockGrantLiveTests` 2/2
each run; `P509TestUnlockShopVisibilityTests` 1/1 each run), and the whole
`Allegiance.ClientCore.MonoGame.Tests` assembly (the area P509 touches on the
client side) was **1208/1208 in every run**. Every failure above is a
pre-existing live-socket timing / load flake in the untouched Core/ClientCore
live-wire area, documented and cited in `docs/TEST_FLAKES.md` (newly-observed
ones added in its P509 section); none reproduce in isolation and none touch
P509's tech/money paths. No P509 regression.

## Files changed

- `src/Allegiance.Core/Model/StaticCore.cs` — `ComputeRequiredTechUnion()`
- `src/Allegiance.Server/ServerConfig.cs`, `ServerJsonContext.cs` — `TestUnlockConfig`
- `src/Allegiance.Server/Server.Lifecycle.cs` — boot banner + `ApplyTestUnlock` + assembly wiring
- `src/Allegiance.Server/Server.Lobby.cs` — seat-time wallet grant + rotation re-announce
- `docker/compose.server.debugmode.json`, `docker/compose.server.debugmode.mapgen.json` — enable the option
- `tests/Allegiance.Core.Tests/TestUnlockGrantLiveTests.cs` (new)
- `tests/Allegiance.ClientCore.MonoGame.Tests/P509TestUnlockShopVisibilityTests.cs` (new)
- `docs/TEST_FLAKES.md` (P509 session observations section)
- `docs/reports/P509_TEST_UNLOCK.md` (this report)

## Out of scope (honoured)

- Changing real tech or economy rules when the option is off — byte-identical to today.
- New ship art. Meshy regeneration for GigaCorp, OH, TF and Dreg is a separate, older follow-up.
- The own-ship model (P507/P508).
- P488: making out-teched predecessor hulls ALSO show in the shop (reported, not hacked).
