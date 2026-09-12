# CHAFF / COUNTERMEASURES (P182) — a real, unblocked capability

Branch `master`, on top of the prior P-stage (P181). Lands the port's first real
chaff/countermeasure: a `Chaff` sim model, `Ship.DeployChaff` (the missile-retarget
"spoof" loop), the five `FIRE_EXPENDABLE`-family wire codecs, and the
`FM_C_FIRE_EXPENDABLE(82)` server dispatch. Two prior audits (2026-09-08) named this
unbuildable because (a) missile homing didn't exist and (b) the mount/ammo concept for
a chaff launcher didn't exist. Both premises are now false: missile homing landed in
P179 (`Missile.Target`, `Sim/Missile.cs`), and the generic mount/ammo machinery
(`Ship.MountPart` / `TryRemovePart`, `MountedPart.Amount`) is keyed on
`EquipmentType` for any part — with one gap, closed here and documented below
(the `DataPartTypeIGC.ChaffLauncher` mount subtype). This is a real, buildable
capability now.

## Relationship to native (citations verified against the live tree this session)

Wire structs — **verified field-for-field against the live `Inc/Messages.h`** (line
numbers in this report are the live numbers; the task brief's transcription was
accurate — **no field-name/type corrections were needed**):

- **`Inc/Messages.h:592-596`** `DEFINE_FEDMSG(C, FIRE_EXPENDABLE, 82)` — `EquipmentType et;`
- **`Inc/Messages.h:618-623`** `DEFINE_FEDMSG(S, FIRE_EXPENDABLE, 87)` — `ShipID launcherID; EquipmentType equipmentType;`
- **`Inc/Messages.h:731-737`** `DEFINE_FEDMSG(S, CREATE_CHAFF, 109)` — `Vector p0; Vector v0; Time time0; ExpendableTypeID etid;`
- **`Inc/Messages.h:739-742`** `DEFINE_FEDMSG(S, MISSILE_SPOOFED, 110)` — `MissileID missileID;`
- **`Inc/Messages.h:744-746`** `DEFINE_FEDMSG(S, END_SPOOFING, 111)` — (no fields)

Authoritative server flow — `FedSrv/FedSrv.CPP:5609-5750` `case FM_C_FIRE_EXPENDABLE`,
ported in `Server.HandleFireExpendable`:
1. refuse unless `STAGE_STARTED` (`FedSrv.CPP:5611`);
2. `TrapHackBoot(pShip->GetParentShip() == NULL)` (`:5644`) → ported as the same
   graceful boarded-gunner log-and-return refusal `HandleSwapPart`/`HandleDropPart`
   use for their `TrapHackBoot`s;
3. `GetMountedPart(et, 0)` (`:5652`) → the silent `if (pdispenser)` guard;
4. `TrapHackBoot(pdispenser->GetAmount() > 0)` (`:5656`) → ported as a graceful
   refusal (report-and-ignore, **no throw**) — the must-fail-if-removed guard;
5. create the chaff + the spoof loop (`:5672-5711`) → `Ship.DeployChaff`;
6. `SetAmount(GetAmount() - 1)` (`:5744-5745`);
7. flush all four broadcasts to `GetGroupSectorFlying(pcluster)` (`:5749`).

Chaff object behaviour — `src/Igc/chaffigc.cpp` (86 lines, read whole): constant
velocity / no acceleration (`SetMass(0.0f)`, `:59`), radius grows `radius·(1 −
dtLeft/lifespan)` floored at 1.0 (`:82-83`), expires after `time0 + lifespan`
(`:61,78-79`). No destruction wire message exists for chaff (`Terminate` is a bare
cluster removal, `chaffigc.h:45-47,68-73`), so — unlike Mine/Probe/Missile — the port's
`Chaff` raises **no** destruction event.

`random(0, chaff) > random(0, missile)` spoof equivalence
(`FedSrv.CPP:5688-5707`) is ported verbatim in `Ship.DeployChaff`:
`cm = chaff·missile; f = (chaff > missile) ? (cm − 0.5·missile²) : (0.5·chaff²);
spoofed ⇔ random(0, cm) ≤ f`.

## What changed

New files:

- **`src/Allegiance.Core/Sim/Chaff.cs`** — the sim model (style/structure mirroring
  `Mine.cs`/`Probe.cs`, but behaviourally a coasting `Projectile`: real constant
  `Velocity`, no acceleration). `OnUpdate` = native `CchaffIGC::Update` verbatim:
  expiry (bare `sector.Remove`, no event) else radius-growth + `Position +=
  Velocity·dt`. Participates in no combat pass (the swept detector yields `_ => null`
  for a non-Ordnance/non-Ship/non-Probe model, `Sector.cs:1220-1229`).
- **`src/Allegiance.Core/Wire/FireExpendableRequest.cs`** (82), **`FireExpendable.cs`**
  (87), **`CreateChaff.cs`** (109), **`MissileSpoofed.cs`** (110), **`EndSpoofing.cs`**
  (111) — five codecs. `FireExpendable` 82 vs 87 are **two distinct shapes sharing one
  logical name with different fields**, so (following the port's existing
  request/broadcast split convention — `RipcordRequest`/`RipcordActivate`,
  `BuyLoadoutRequest`/`BuyLoadoutAck`) they are two classes rather than one shape.
  Sizes pin the native C++ `sizeof` with the port's established natural-alignment
  convention: 82 → 6 (header + 1 short), 87 → 8 (2 shorts), 109 → 36 (2 vectors +
  uint + short, 4-aligned), 110 → 6 (1 short), 111 → 4 (header only, the same
  zero-payload shape as `Suicide`/`Logoff`).

Modified files:

- **`src/Allegiance.Core/Sim/Ship.cs`** — `DeployChaff(DataChaffTypeIGC, double now,
  Random? rng = null)` + nested `Ship.DeployChaffResult(Chaff, IReadOnlyList<Missile>)`.
  Directly-callable only (the same Layer-1 deferral every prior fire/deploy method in
  this arc makes — not wired into control-input dispatch). Creates the `Chaff` at the
  ship's plain `Position` with the ship's plain `Velocity`, then runs the spoof loop
  over `sector.Models.OfType<Missile>()` whose `Target` is this ship, returning the
  chaff + the spoofed missiles for the caller to broadcast. Injectable `rng`
  (default `Random.Shared`) mirrors `Weapon`'s dispersion-RNG pattern
  (`Sim/Weapon.cs:52,72,378`).
- **`src/Allegiance.Core/Sim/Missile.cs`** — `Target` setter `private → internal` so
  `Ship.DeployChaff` can perform the native `SetTarget(chaff)` retarget
  (`FedSrv.CPP:5705`). This is the one in-scope change to Missile permitted by the
  task ("reassigning `Target` on a successful spoof"); P179's existing per-tick
  `!_sector.Models.Contains(t)` target-drop is untouched and confirmed (by test) to
  still clear a chaff target that leaves the cluster.
- **`src/Allegiance.Core/Data/DataPartTypeIGC.cs`** — added a construction-only
  `ChaffLauncher(short ExpendableTypeId)` subtype (details under Divergence #1).
- **`src/Allegiance.Server/Server.cs`** — `HandleFireExpendable` + the
  `fmid == FireExpendableRequest.Fmid` dispatch branch in `ProcessDatagram`. Builds the
  four broadcasts in one guaranteed batch and sends them to the whole cluster (all
  sides — `GetGroupSectorFlying(pcluster)`, the `SendSectorWide` audience), decrements
  the mounted dispenser's `Amount`, and logs. Uses the server's shared `_rand` (like
  native's global random).
- **`src/Allegiance.Bot/Bot.cs`** — `SendFireExpendable(EquipmentType)`,
  `FireExpendables`/`CreateChaffs`/`MissileSpoofeds`/`EndSpoofings` decode+capture
  (the `MineExports` instrumentation pattern) so the live dispatch tests can assert the
  real wire traffic.

Tests — **`tests/Allegiance.Core.Tests/ChaffTests.cs`** (9, of which 6 domain/wire +
3 live): deploy-creates-chaff-and-spoofs-only-the-firer (deterministic via
`ChaffResistance == 0` + injectable RNG); no-spoof-when-roll-favors-missile;
no-threatening-missile → empty spoofed list; radius-grows-and-expires-after-lifespan;
spoofed-missile-drops-chaff-target-when-chaff-leaves-cluster; wire round-trip + native
sizes for all five codecs; live mount-Amount>0 → chaff-created + ammo-decrement +
FE/CREATE_CHAFF/END_SPOOFING; live Amount==0 → refused (must-fail-if-removed, proven
by disabling the guard and watching it red); live zero-resistance-missile spoofed over
the wire with exactly one END_SPOOFING.

## Conventions followed vs. genuinely new

Followed (existing port conventions reused verbatim, not invented):

- **Directly-callable deploy methods** (`DeployMine`/`DeployProbe`/`FireMissile`):
  `DeployChaff` is the same shape.
- **Graceful TrapHackBoot porting**: boarded-gunner and Amount==0 guards become
  log-and-return (the `HandleSwapPart`/`HandleDropPart`/`HandleSuicide` idiom), never
  a throw.
- **Injectable RNG** for the spoof roll: `Weapon._rng` pattern.
- **Whole-cluster all-sides audience** via `SendSectorWide`: the task brief's mandate,
  and the exact native `GetGroupSectorFlying(pcluster)` — distinct from the
  same-side-only mine/probe/missile broadcasts, so no existing audience was altered;
  only the new chaff flow uses it.
- **Natural-C++-alignment codec sizing** and the standard Encode/Decode/round-trip
  codec skeleton.
- **`(uint)TickIndex` uint-Time convention** for `CREATE_CHAFF.time0` (same as
  `BroadcastFireMissile`).
- **Generic mount/ammo** machinery `Ship.MountPart`/`SetPartAmount` — the `ChaffLauncher`
  part mounts and tracks `Amount` like any other `MountedPart`.

Genuinely new pattern introduced:

- The `DataPartTypeIGC.ChaffLauncher` **construction-only subtype** — see Divergence #1.

Choice documented per the brief's step 5 (event hook vs. inline send): **no
`ChaffCreatedEvent` hook was added.** `Ship.DeployChaff` is a single call site and the
brief's own FIRE_EXPENDABLE handler builds all the messages in the same code path as
native does, so `CREATE_CHAFF` is sent directly from `HandleFireExpendable` rather than
through an `IIgcSite` event hook. This is the more faithful match to native's
single-call-site structure; the mine/probe `CreatedEvent` hooks exist only because
those objects have second call sites (tests/helper paths), which chaff does not.

## Divergences from the brief's assumptions (verified, not guessed)

1. **The mount/ammo machinery does *not* already cover a chaff launcher in the data
   model.** The brief asserted `DataPartTypeIGC` covers "any `EquipmentType`, not just
   weapons" — true of the `Ship.Parts`/`MountPart` machinery, but the `Subtype`
   discriminated union had **no `ChaffLauncher`/`Dispenser` case** and its `Decode`
   (`ExpectedSize`) rejected them. Native mounts chaff as a 24-byte
   `DataLauncherTypeIGC` record that names its expendable via `expenditureTypeID`
   (`igc.h:1881`; the corpus has **no** `OT_partType` part-record that decodes as
   ChaffLauncher — those are all 24-byte launcher records). So the faithful close was
   a **construction-only `ChaffLauncher(short ExpendableTypeId)` subtype**: it lets a
   ship mount a chaff launcher tracking an `Amount` (the brief's premise) *and* lets
   the dispatch resolve which chaff type it fires (the native `expenditureTypeID`
   link). It is deliberately **not** added to `Decode`'s size switch (nothing decodes
   as it). This is the single real correction to the brief's "already covers it" claim;
   the P94/P144 docs had long flagged exactly this gap.
2. **Chaff drops at the ship's plain position with the ship's plain velocity** — the
   brief's mandated simplification (native offsets by the never-decoded per-hull
   `ChaffPosition`, `FedSrv.CPP:5680`, and kicks velocity up by `GetUp()·5`,
   `:5681`; the native singleplayer branch itself drops at plain position,
   `clintlib.cpp:3276`). Stated, not silent.
3. **No `ChaffCreatedEvent`** — see the choice above.

## Verification

- **Build**: `/home/domingo/.dotnet/dotnet build` on the full solution — clean,
  0 warnings / 0 errors.
- **New tests**: all 9 `ChaffTests` pass in isolation.
- **Must-fail-if-removed**: temporarily disabling the `part.Amount <= 0` guard in
  `Server.HandleFireExpendable` makes `Live_firing_empty_chaff_launcher_is_refused`
  fail (chaff+broadcast leak through); guard restored.
- **Full suite, 3 consecutive runs** (`dotnet test -c Debug` on
  `Allegiance.Core.Tests`, baseline grew from P169's 2805 to **2861** with this + the
  intervening P170-181 additions and my 9 new tests):
  - Run 1: 2860 passed, 1 failed — the documented flake
    `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`.
  - Run 2: 2860 passed, 1 failed — the **same** documented flake.
  - Run 3: **2861 passed, fully clean.**
  The intermittent failure is one of the six documented flakes, confirmed green in
  isolation, and its test file has zero references to any symbol this task added.

## Out of scope (per brief)

- The mine/probe branch of `FIRE_EXPENDABLE` (already-solved `Ship.DeployMine`/
  `DeployProbe`; not routed through the new dispatch).
- Live control-input wiring (no `chaffFireIGC`-equivalent `ClientShipUpdate` bit).
- Per-hull `ChaffPosition` offset and the `Up`-kick velocity component.
- Any stat interaction beyond `ChaffStrength`/`ChaffResistance`.
- Any change to `Missile.cs` homing beyond reassigning `Target` on a spoof.
