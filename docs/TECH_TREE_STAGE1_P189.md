# TECH_TREE_STAGE1_P189.md

Stage-1 port of the native tech tree's **development** bucket slice into
AllegianceCore. This stage makes the already-decoded tech-tree bytes **live**:
per-side development techs and global attribute sets seeded from the civ, an
additive development-bucket catalog, and the real completion wire chain
(`DEV_COMPLETED(8)` / `SIDE_TECH_CHANGE(75)` side-group, gated on
`Stage == Started`; `SIDE_ATTRIBUTE_CHANGE(76)` whole-mission). Everything here
was read from the live native source (`/home/domingo/Development/Allegiance`),
not guessed.

Relationship to the roadmap page
`allegiance-fedmsg-roadmap.md` §"Tech-tree": the three fedmsgs that were
"genuinely blocked" are now unblocked — the port already parsed every byte the
tech tree needs (`DataDevelopmentIGC`, `DataCivilizationIGC`); it was just never
wired to anything live. This stage is that wiring.

---

## What changed

### Value types (new)
- `src/Allegiance.Core/Model/TechTreeBitMask.cs` — a 400-bit / 50-byte value
  type (`TechTreeBitMask = TLargeBitMask<c_ttbMax>`, `igc.h:768`, `c_ttbMax=400`
  `igc.h:578`, `uint8_t[((400-1)/8)+1]=[50]` `mask.h:351`) with `Or` (the native
  `|=`, `mask.h:235-241`), `IsSubsetOf` (the native `<=`, `mask.h:300-313`), and
  equality. This is the type the brief asked for, wrapping the raw 50-byte array
  the port already decodes everywhere.
- `src/Allegiance.Core/Model/GlobalAttributeSet.cs` — a 25-float value type
  (`c_gaMax=25`, `igc.h:915,948`) with `Apply` matching the native semantics
  (below) and a baseline-identity seed.

### Side state (scope #3)
- `src/Allegiance.Core/Model/Side.cs` — added
  `DevelopmentTechs` (`TechTreeBitMask`) and `GlobalAttributes`
  (`GlobalAttributeSet`), seeded in the constructor from
  `Civilization.TtbmBaseTechs` / `Civilization.GasBaseAttributes`
  (`m_data.ttbmDevelopmentTechs = GetCivilization()->GetBaseTechs()`,
  `sideigc.cpp:102`; civ gas `civilizationigc.h:73`).

### Development predicates (derived, not on the wire)
- `src/Allegiance.Core/Model/DevelopmentLogic.cs` — `IsTechOnly` and `IsObsolete`
  computed from the decoded `DataDevelopmentIGC` (native derives these at
  runtime, not from the struct — see below).

### Bucket catalog (scope #4)
- `src/Allegiance.Core/Model/Bucket.cs` — added `BucketItemType.BitDevelopment`
  and a `DataDevelopmentIGC? Development` carrier on `Bucket`. `BucketCatalog.ForSide`
  keeps the existing synthetic ship-type bucket (id 1) and **appends** one
  `BitDevelopment` bucket (ids 2…) per **non-obsolete** development in
  `core.Developments`, in dev-id order, taking price/time/name/description from
  the real decoded record. The side's initial development techs are passed in
  (from `Mission.Assemble`) to determine obsolescence at seed time.

### Completion wiring (scope #5) — `src/Allegiance.Server/Server.cs`
- `OnBucketCompleted` now routes development-type buckets through a new
  `CompleteDevelopment(mission, side, dev)` that reproduces native
  `FedSrvSiteBase::DevelopmentCompleted` (`FedSrv.CPP:11259-11276`) exactly:
  1. apply effect techs (`ApplyDevelopmentTechs`, OR-in only genuinely-new bits
     per the native `rc = !(ttbm <= ttbmDevelopmentTechs)`, `sideigc.h:194-210`);
     only if new bits were introduced **and** `Stage == Started` broadcast
     `SIDE_TECH_CHANGE(75)` to the side's own group (`SideDevelopmentTechChange`,
     `FedSrv.CPP:11282-11289`);
  2. broadcast `DEV_COMPLETED(8)` `{devId}` to the side's own group —
     unconditional (`FedSrv.CPP:11262-11266`);
  3. unless tech-only, apply the dev's `Gas` via `GlobalAttributeSet.Apply` and
     broadcast `SIDE_ATTRIBUTE_CHANGE(76)` to the **whole mission**
     (`ApplyGlobalAttributeSet`, `sideigc.h:343-345`;
     `SideGlobalAttributeChange`, `FedSrv.CPP:11313-11318`, unconditional).
- The two side-group broadcasts are scoped by **both** the side id and the
  mission, because side ids are not unique across this server's world missions
  (see Divergences #4).

### New wire codecs (scope #6)
- `src/Allegiance.Core/Wire/DevCompleted.cs` (8, `{devId: short}`,
  `Messages.h:89-91`) — 6 B.
- `src/Allegiance.Core/Wire/SideTechChange.cs` (75, `{sideID, ttbmDevelopments}`,
  `Messages.h:560-564`) — 56 B (header + short + 50-byte mask).
- `src/Allegiance.Core/Wire/SideAttributeChange.cs` (76,
  `{sideID, gasAttributes: float[25]}`) — 108 B (header + short + 2 align pad +
  100 B). The align pad is real: `GlobalAttributeSet` is 4-byte aligned
  (`float[25]`), so the short `sideID` is padded to offset 8.
- Registered all three in `FedMessageDumper` (ported + formatters) and in the
  `Allegiance.Bot` inbound capture.

### Join-sync (behavioral improvement)
- `CreateBuckets(191)` join-sync now fills `TtbmDevelopments`/`TtbmInitial` from
  the side's **real** development techs instead of the P117-era all-zero carve-out
  (native `fsmission.cpp:4255-4258`; `TtbmInitial` from the `UpdateInitialTechs`
  equivalence, `sideigc.h:219-222`). LiveBucketTests Scenario 1 updated to assert
  the real masks.

### Tests
- `tests/Allegiance.Core.Tests/TechTreeDevelopmentLiveTests.cs` (6): main
  completion chain (tech + gas + all three scoped broadcasts), the
  `SIDE_TECH_CHANGE` stage gate (fund in Started, complete in Starting →
  suppressed; Started → sent), obsolete-development never-fund/broadcast,
  `TechTreeBitMask`/`GlobalAttributeSet` unit correctness, and the whole-mission
  `SIDE_ATTRIBUTE_CHANGE` cross-mission scoping.
- `LiveBucketTests` updated for the additive multi-bucket catalog (locate the
  ship-type bucket explicitly; real CreateBuckets masks).
- Discriminating proofs (temporarily reverted and watched red):
  removing the completion wiring fails the main-chain test (dev tech not applied);
  removing the stage gate fails the gate test (SIDE_TECH_CHANGE leaked before
  start).

---

## GlobalAttributeSet::Apply — real semantics (quoted, not paraphrased)

Read from `src/Igc/igc.h:915-946`:

```cpp
struct GlobalAttributeSet
{
    public:
        void    Initialize(void)
        {
            for (int i = 0; (i < c_gaMax); i++)
                m_attributes[i] = 1.0f;
        }
        ...
        void    Apply(const GlobalAttributeSet& gas)
        {
            for (int i = 0; (i < c_gaMax); i++)
            {
                assert (m_attributes[i] > 0.0f);
                assert (m_attributes[i] < 100.0f);
                assert (gas.m_attributes[i] > 0.0f);
                assert (gas.m_attributes[i] < 100.0f);

                m_attributes[i] *= gas.m_attributes[i];
            }
        }
```

**`Apply` is a per-element multiplication, not an addition.** The baseline is
the multiplicative identity (all `1.0f`). The port's
`GlobalAttributeSet.Apply(gas)` returns `result[i] = this[i] * gas[i]`, immutable
(a fresh set replaces the side's). A development whose `Gas` is all 1.0 grants no
attribute change — which is exactly the condition the native uses to derive
tech-only-ness (below).

## TechOnly derivation for DataDevelopmentIGC

Read from `src/Igc/developmentigc.cpp:32-47` (the `CdevelopmentIGC` constructor):

```cpp
if (m_data.developmentID == c_didTeamMoney)
    m_techOnly = false;     //Wining the game is not "tech only"
else
{
    m_techOnly = true;
    for (int i = 0; (i < c_gaMax); i++)
    {
        if (m_data.gas.GetAttribute(i) != 1.0f)
        {
            m_techOnly = false;
            break;
        }
    }
}
```

`m_techOnly` is **not** a wire field — it is derived at construction from the
decoded `Gas` array (and the `c_didTeamMoney` economic-victory id, which is out
of scope here). The port therefore adds **no** flag to `DataDevelopmentIGC`;
`DevelopmentLogic.IsTechOnly(dev)` returns true exactly when every one of the 25
decoded `Gas` factors equals `1.0`. `IsObsolete(dev, ttbm)` is
`IsTechOnly(dev) && effects.IsSubsetOf(ttbm)` (native `developmentigc.h:110-113`).

---

## Scope boundaries honored / deferred

- **Store & broadcast only** for the 25 named attribute multipliers. Their
  gameplay consumers (scan range, shield regen, signature, … consulted
  throughout `shieldIGC.h`, `probeigc.h`, `clusterIGC.h`, etc.) are NOT wired —
  matching P119's "detect completion, defer what it produces" precedent.
- `Side.BuildingTechs` / `ResetBuildingTechs` (the other tech source that
  `GetTechs()` ORs in, `sideigc.h:133-136`) is out of scope. NOTE (correction to
  the brief's uncertainty): `DataStationTypeIGC` **does** already decode its
  `TtbmEffects` (50 B, `DataStationTypeIGC.cs:30,145`), so the decode is not the
  blocker — the missing pieces are the station-capture hook that recomputes
  building techs on capture (`ResetBuildingTechs`, `sideigc.h:142-158`) and the
  `BuildingEffect` wiring. A real but separate follow-on.
- Station-type-successor upgrades and `UpgradeDrones` (the tail of native
  `SideDevelopmentTechChange`) are out of scope.
- The `c_didTeamMoney` economic-victory shortcut is out of scope (deliberately;
  the `TechOnly` derivation here omits only that one special case, per the brief).
- Hull/part purchase-time tech gating (`GetRequiredTechs` on hulls/parts) is out
  of scope — this stage is buckets and broadcasts only. (The `CanBuy` development
  gate `ttbmRequired <= ttbmSide`, `sideigc.h:238-243`, was read and used for
  context but is not enforced by a purchase path this task builds.)

---

## Divergences from the brief's assumptions (verified against live native)

1. **Catalog obsolete-filter uses real `IsObsolete`, not plain-subset.** The
   brief suggested skipping any dev whose `TtbmEffects` is already a subset of
   the side's techs. That would wrongly drop a non-tech-only development that
   still grants an attribute delta even when its effect techs are already known.
   Native's `AdjustBuckets` only auto-completes `IsObsolete` devs
   (`techOnly && effects ⊆ techs`, `sideigc.h:629-633`), so the port filters by
   the real `DevelopmentLogic.IsObsolete`, keeping every non-obsolete dev
   (including gas-granting ones) as a bucket.
2. **`DataStationTypeIGC.EffectTechs` IS decoded** (as `TtbmEffects`,
   `DataStationTypeIGC.cs:30,145`) — contrary to the brief's open question. The
   BuildingTechs blocker is the capture hook, not the decode (recorded above).
3. **`DataDevelopmentIGC` needs no `TechOnly` flag** — confirmed, per the native
   derivation above (it is computed from `Gas`, not stored or transmitted).
4. **Side-group broadcasts are scoped by mission AND side id.** DEV_COMPLETED and
   SIDE_TECH_CHANGE initially keyed only on `SideId`, but the world missions'
   sides share ids (both missions have side ids 0/1), so a mission-B peer on the
   same-numbered side was incorrectly matched. Fixed to
   `Ship.Mission == mission && Ship.SideId == side.Id` — a multi-mission
   correctness requirement the brief did not anticipate (caught by the
   cross-mission scoping test).
5. **`BucketCatalog.ForSide` signature changed** to take the side's initial
   development techs (for obsolete filtering) — the additive intent (keep the
   synthetic ship-type bucket) is preserved; `Mission.Assemble` passes the civ's
   base techs.
6. **CreateBuckets now sends real tech masks** instead of the P117-era all-zero
   carve-out — the natural consequence of having live `Side.DevelopmentTechs`,
   matching native `fsmission.cpp:4255-4258`.

## Verification

- Full build clean (`/home/domingo/.dotnet/dotnet build`).
- Full suite: **2929 tests**, two fully-clean runs (runs 1 and 4) plus one run
  interrupted only by the documented flake
  `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`
  (confirmed green in isolation). No deterministic regressions; the new
  `TechTreeDevelopmentLiveTests` (6) pass in isolation and in-suite.
- Discriminating live tests proven to fail when the wiring is removed.
