# DROP_PART (P144) — in-space part-drop, creating a real treasure

Branch `master`, on top of `e0ecc3a` (P143, SWAP_PART). Baseline: 2713 tests;
this task adds 3 (`DropPartTests`) → 2716 total.

This is the second Tier-3 "parts-system remainder" item
(`allegiance-next-phase-plan`). Unlike `SWAP_PART` (a pure relay), `DROP_PART`
is the first code path in this port to actually send the previously-built-but-
never-sent `FM_S_TREASURE`(28) codec and to create a real in-sector
`Treasure` model. Everything else it needs already existed (the
`DataTreasureIGC` 44-byte decode, the `Treasure` sim model, `Sector.Add`,
`Mission.NextObjectId`, the `0.5·hull.Length` radius convention, and the
`SendSectorWide` relay established by P140/P143).

## What changed

- **`src/Allegiance.Core/Wire/DropPart.cs`** (new) — the `FM_CS_DROP_PART(33)`
  codec, mirroring `SwapPart.cs` (single codec serves both the client→server
  leg and the server's sector-wide echo, which fills in `shipID`). Fixed
  10-byte layout: header(4) + `shipID` short @4 + `et` short @6 + `mount` char
  @8 + 1 pad byte @9 (C++ align-2 → sizeof 10). Matches native
  `Inc/Messages.h:266-270`.
- **`src/Allegiance.Core/Sim/Ship.cs:883` `TryRemovePart(et, mount, out part)`**
  — locates the mounted part at `(et, mount)` using the same
  `_parts.FirstOrDefault(p => p.EquipmentType == et && p.MountId == mount)`
  lookup `TrySwapPart` shares (Ship.cs:814/820) and **removes it from the
  ship's loadout** (a removal, not a reposition): drops it from `_parts`, and
  when it carried a realized `Weapon` also drops that from `_weapons`
  (the analogue of native `ppart->Terminate()`, `FedSrv.CPP:6327`). Returns
  `false` when the mount is empty — native's silent no-op.
- **`src/Allegiance.Core/Sim/Ship.cs:949` `CreateDropTreasure(part, now, time0)`**
  — the port of the native `CreateTreasure` chain (`FedSrv.CPP:4324-4396`).
  Computes `P0` = ship position + `(0.5·hull.Length + 10)·`(uniform random
  unit direction); `V0` = that direction × `25.0f` + ship velocity
  (dv=25, lifespan=120 are the DROP_PART handler's call args,
  `FedSrv.CPP:6326`); builds the `DataTreasureIGC`
  (`Code=Part`, `TreasureId=part.PartId`, `Amount=part.Amount`,
  `Lifespan=120`, `CreateNow=false`, `ObjectId=Mission.NextObjectId()`,
  `ClusterId=sector.Id`, `Time0`=the server tick index); constructs the
  `Treasure` and `Sector.Add`s it. Contains the port's `RandomDirection()`
  (uniform unit vector, the analogue of `Vector::RandomDirection()`).
- **`src/Allegiance.Server/Server.cs:2282` dispatch branch + `:2999`
  `HandleDropPart`** — decode, gate exactly like `HandleSwapPart` (mission
  started + `ParentShip == null`), resolve+remove the part (silent no-op on an
  empty mount), forward the original `DropPart` sector-wide with `shipID`
  filled in, then the **conditional** treasure creation + sector-wide
  `TreasureSpawn` send.
- **`src/Allegiance.Bot/Bot.cs`** — `SendDropPart` (:711) plus inbound
  accumulators + decode cases for `DropPart`(33) (:820) and `TreasureSpawn`(28)
  (:823), so a live loopback test can observe both.
- **`tests/Allegiance.Core.Tests/DropPartTests.cs`** — codec pins for
  `DropPart` (10 B / fmid 33) and `TreasureSpawn` (48 B / fmid 28), plus a
  3-bot live loopback proof (same-side observer + cross-side enemy) covering
  the weapon drop and the empty-Pack skip.

## Treasure math — confirmed

Verified against the quoted native `CreateTreasure` overloads
(`FedSrv.CPP:4324-4396`) and the DROP_PART call site
(`FedSrv.CPP:6323-6326`):
- `P0 = position + (radius + 10)·direction` (`FedSrv.CPP:4329-4330`) — radius
  is this port's `0.5·HullType.Length` convention (Ship.cs:411-415), the
  `GetRadius()` analogue.
- `V0 = direction·25 + ship velocity` (`FedSrv.CPP:4331,6326`).
- `TreasureCode = Part` (c_tcPart, `FedSrv.CPP:4367`);
  `TreasureId = part.PartId` (ppt->GetObjectID(), :4369);
  `Lifespan = 120.0f` (the handler's arg, :6326); `CreateNow = false` (:4372);
  `ObjectId = Mission.NextObjectId()` (= GenerateNewTreasureID, :4326);
  `ClusterId = sector.Id` (pcluster->GetObjectID(), :4335);
  `Time0 = (uint)TickIndex` (this port's established uint-Time convention, cf.
  ProjectileInstance stamps `(uint)TickIndex`, Server.cs:4676).

The test asserts the exact structural fields (`Code/TreasureId/Amount/
Lifespan/CreateNow/ClusterId`) and the plausible kinematics: `|P0−shipPos|`
≈ radius+10 (±6), `|V0−shipVel|` ≈ 25 (±6), and `(P0−shipPos)` is (nearly)
parallel to `(V0−shipVel)` (cos ≥ 0.995), i.e. the treasure is offset near the
ship and moving away.

## Skip-condition simplification — documented, not dropped

Native's creation guard is `((type != OT_pack) &&
!IlauncherIGC::IsLauncher(type)) || (ppart->GetAmount() != 0)`
(`FedSrv.CPP:6323`). `IlauncherIGC::IsLauncher` covers magazine/dispenser/
chaff-launcher part types, which **do not exist** in this port's
`DataPartTypeIGC` subtype set (confirmed at `DataPartTypeIGC.cs:9-19`: only
ChaffLauncher/Weapon/Magazine/Dispenser/Shield/Cloak/Pack/Afterburner enum
values exist, and the realized record subtypes are only
Weapon/Shield/Cloak/Afterburner/Pack — the magazine/dispenser gap is the
open Phase-2 item the Tier-3 bucket already documents for
`FIRE_EXPENDABLE`/`CREATE_CHAFF`). So the guard collapses to:
**skip iff the dropped part is a `Pack` with `Amount == 0`**. Implemented at
`Server.cs:3045` and mirrored in `CreateDropTreasure`'s remarks. The test
exercises the skip (an `Amount == 0` Pack still broadcasts `DropPart` but
produces no `TreasureSpawn`).

## The open question — a real FM_S_TREASURE send site?

I did my own search of the checked-in native tree at
`/home/domingo/Development/Allegiance/src` and independently corroborated the
task's finding:
- `grep FM_S_TREASURE` in `FedSrv/FedSrv.CPP` → **no send site**. The only
  hits are the message definition (`Inc/Messages.h:253`) and client-side /
  WOPR-interop receivers (`WinTrek/WinTrek.cpp:11090`,
  `Wopr/Wopr/MessageReceiver.cs:290`).
- `grep '\.Export(' FedSrv/FedSrv.CPP` → **zero call sites** in the server at
  all; `CtreasureIGC::Export` (`Igc/treasureIGC.cpp:268`) is implemented but
  never invoked.
- The only generic object-export helper, `ExportObj` (`FedSrv.CPP:2022-2043`,
  wraps any IGC object into `FM_S_EXPORT`), is called for **static,
  `c_mtPredictable`** objects on visibility change — `ShowObject`
  (`FedSrv.CPP:10750-10778`: stations, warps, building-effects, etc.). A
  treasure is a transient runtime object, not `c_mtPredictable`, so it does
  not ride that path either.

Conclusion: **no real server-side `FM_S_TREASURE` send site exists in native**;
treasure announcement to clients is genuinely unspecified there. Per the
task's stated default, this port sends `TreasureSpawn`(28) **once, sector-wide
(`SendSectorWide`)**, at the moment of creation — the same audience the
`DROP_PART` echo itself uses (`GetGroupSectorFlying(pcluster)`,
`FedSrv.CPP:6312`).

## Explicit exclusions (accepted, not silent)

- **Pickup/consumption of the created part-type treasure** — `ADD_PART`(164),
  out of scope. The treasure this task creates sits uncollectable until the
  `FM_C_TREASURE_ACK` mount-targeting flow exists (`PerformTreasurePickup` is
  cash-only auto-pickup-by-proximity). Same honest partial-coverage precedent
  as P123.
- **Treasure motion.** `V0` is computed and carried on the wire (a real client
  needs it to render), but `Treasure.OnUpdate` does **not** integrate it — the
  treasure rests static, exactly as cash treasures already do
  (`Treasure.cs` remarks: no spawn/launch velocity path exists yet).
- **Expiry** (`lifespan`/`time0`-based destruction) — carried on the wire but
  not enforced, per `Treasure.cs`'s own remarks.
- **`ADD_PART`(164) and `LOADOUT_CHANGE`(96)** — untouched.
- **Amount simplification:** this task sets the treasure's `Amount` to the
  dropped part's own `Amount` per the task spec. Native's third `CreateTreasure`
  overload instead reserves a nonzero quantity only for magazine/dispenser/pack
  and falls into `default: amount = 0` for weapon/shield/cloak/afterburner
  (`FedSrv.CPP:4383-4394`); the consumable-only distinction is moot here since
  this port represents no consumable part subtype. Documented in
  `CreateDropTreasure`'s remarks, not silently dropped.

## Verification

- Loopback live test (3 real bots over TCP): drops a mounted weapon (Amount 20)
  and asserts (a) the part is removed from the ship's server-side loadout,
  (b) `DropPart`(33) reaches **every** sector peer (same-side observer *and*
  cross-side enemy, with resolved `shipID`/`et`/`mount` — the sector-wide
  `GetGroupSectorFlying` target), and (c) `TreasureSpawn`(28) is sent with the
  full `DataTreasureIGC` whose P0/V0 are plausible and whose structural fields
  match. The empty-Pack skip case (phase 2) asserts `DropPart` still broadcasts
  but no `TreasureSpawn` follows.
- Full suite: **2716 total, all green** on clean runs. Interleaved failures
  across reruns were exclusively the documented `LiveCombatWireTests` timing
  flake (nondeterministic — pass/fail/pass on identical code with a varying
  failing-test subset; the DROP_PART path is never exercised by combat tests).
  Verified with `dotnet test` 3 consecutive clean runs (2716/2716) after
  flake reruns.

No VM, no Obsidian, no `sudo`; every build/test used
`/home/domingo/.dotnet/dotnet`.
