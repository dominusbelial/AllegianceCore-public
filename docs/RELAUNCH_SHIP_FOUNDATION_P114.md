# P114 — RELAUNCH_SHIP foundation: capability bits + landing-bay data investigation + simplified InGarage

Branch `master`, on top of `2e24e3a` (P113). Three consecutive clean full-suite runs
verified (2500 tests, 0 failures — the pre-existing `LiveChatSectorWireTests` timing
flake fired once mid-sequence and was rerun clean per the P108-P113 protocol; it is
not a P114 regression).

---

## 1. The landing-bay-data question: which of the two possibilities is true?

**Neither, exactly — it is a hybrid, and the answer is the *good* news for the arc.**

The task posed two possibilities: (1) the geometry is inside the `OT_hullType`
record, unparsed; or (2) it is never on the wire and lives only in a local static
file this port's parser has never read for hull types.

### Possibility 1 is ruled out — structurally, definitively
The native serialized hull record is byte-for-byte the `DataHullTypeIGC` struct
(`src/Igc/igc.h:1767-1820`), ending with the comment `// HardpointData[] comes here`
(`igc.h:1819`). It contains **no** launch/land slot members. Therefore no amount of
corpus measuring of `OT_hullType` records can ever recover slot geometry — it is not
there, at any byte offset. The port's existing `DataHullTypeIGC.Size = 540` +
`HardpointStride` decode (`DataHullTypeIGC.cs:63-65,123-133`) is complete; there are
no unparsed trailing bytes of this kind hiding a bay.

### Possibility 2, refined: the data IS recoverable — from the `.cvh` model frames, which this port already parses
Native `HullTypeIGC::Initialize` populates its slot arrays by loading the hull's
collision model — `HitTest::Load(m_data->modelName)` (`hullTypeIGC.cpp:56`) — and
scanning its named **frames**:
- launch slots: frames `launch1`, `launch2`, … (`hullTypeIGC.cpp:158-173`)
- landing-bay planes: frames `garage1A`, `garage1B`, …, `garage2A`, … (`hullTypeIGC.cpp:178-212`)

So the geometry is not in any `.igc` static file (ruling out possibility 2's literal
claim of "a local `.igc` hull static file") — it is in the per-model **`.cvh`**
collision files, which **this port already parses**: `CvhReader` reads the named
frame section into `CvhFile.Frames` (`CvhReader.cs:109-119`, `CvhTypes.cs:33-59`).

Empirical confirmation across the corpus:
- **136 of 627** on-disk `.cvh` files carry `launchN` / `garageNA` frames.
- Many are referenced by real carrier hull records (found inside the `.igc` corpus):
  `faohshcar`, `cap401/cap500-504/cap600/cap602`, `dregcar`, `drgcrr`,
  `dn_faphshcar`, `p1_car`, `raincarr`, `ta_drac_carr`, `weed_car`, ….

**Conclusion:** the landing-bay/launch-slot geometry *is* present in this port's
existing data and is decodable — but from `.cvh` frames keyed by the hull's
`modelName`, not from the `OT_hullType` record. This is why the task's "if found →
decode into `DataHullTypeIGC`" prescription did not apply literally: the record
cannot carry it. Instead the decode happens at the `modelName` → `.cvh` → frame
level.

### One more critical detail the decode must honor
`CvhReader` deliberately stores **raw on-disk** frame values and skips the native
load-time sign flips (its remarks, `CvhReader.cs:42-44`). Native `HitTest::Load`
applies, per frame: `position.x = -x`, `forward.y = -y`, `forward.z = -z`
(`src/_Utility/HitTest.cpp:792-796`), and only *then* scales by
`m_scale = 0.5f * length / originalRadius` (`hullTypeIGC.cpp:61-62`, `originalRadius`
= the `.cvh` header radius). Without reproducing that flip the plane normals are
inconsistent (a box that looks like 4 planes pointing one way and 1 the other) and
`InGarage` is meaningless. The reconstruction applies it (see §3).

---

## 2. The three capability bits landed

`DataHullTypeIGC.cs:92-116`, following the existing `Habm*` constant pattern exactly
(XML doc citing the real `igc.h` line):

| Constant | Value | Cited source |
|---|---|---|
| `HabmRescue` | `0x02` | `src/Igc/igc.h:727` |
| `HabmLandOnCarrier` | `0x10` | `src/Igc/igc.h:730` |
| `HabmCarrier` | `0x400` | `src/Igc/igc.h:736` |

No new infrastructure was needed — the existing `HasCapability(short)` predicate
(`DataHullTypeIGC.cs:101`) reads them. (Deliberately cited to `igc.h`, not the stale
`AGCIDL.idl` mapping the task flagged.)

Verified against real data: the `faohshcar` carrier hull decodes with
`HabmCapabilities = 0x1D02`, which has the Carrier and Rescue bits set and not the
LandOnCarrier bit — exactly right for a carrier that hosts but does not land.

---

## 3. What was built (because the data really was found)

### `src/Allegiance.Core/Collision/HullLandingBay.cs` (new)
Static reconstruction of a hull's landing-bay/launch-slot geometry from its
`.cvh` frames:
- `Reconstruct(CvhFile model, float length)` returns scaled launch slots and grouped
  land slots, mirroring native `HullTypeIGC::Initialize` (`hullTypeIGC.cpp:153-213`),
  incl. the load-time frame sign flip (`HitTest.cpp:792-796`) and
  `scale = 0.5f * length / model.Radius` (`hullTypeIGC.cpp:62`), and the native cap
  constants (`c_maxShipLaunchSlots/c_maxLandSlots/c_maxLandPlanes`, `igc.h:29-34`).
- Real-data proof (unit test): `faohshcar.cvh` (radius `16.29676056`) with hull
  length `300` yields `scale = 9.204283`; a single 5-plane land slot from
  `garage1A..E`; first plane `(0,-23.756,28.978)` dir `(0,0,1)` — matches a
  hand-computation from the raw frame values.

### `Ship.InGarage(Ship lander, float tCollision)` (`src/Allegiance.Core/Sim/Ship.cs:358-434`)
Faithful port of `CshipIGC::InGarage` (`shipIGC.cpp:4382-4443`): for each land-slot,
each plane's direction/position transformed by the carrier's orientation, the
lander's extreme point tested against the half-space `(pMin - point)·direction < 0`,
`bInside` falling out only when a slot's planes are all passed (`j == -1`). Plane
geometry comes from the carrier's own `.cvh` via `HullLandingBay.Reconstruct`.

**Explicit, sourced simplification (cited the same way P111/P113 cited theirs):**
native drives the lander's real oriented hit-test extremes
(`pht->GetMinExtreme`, `shipIGC.cpp:4421`). This port's established ship-extent
primitive is the `0.5 × HullType.Length` sphere (P113's ripcord-teleport margin;
`ServerHost` spawn offsets), so the lander's extreme is taken as its swept sphere
centre minus `direction × 0.5·Length` — exactly the sphere branch of
`Collider.GetMinExtreme` (`Collider.cs:193`).

> **Correction to the task's premise:** the task asked to confirm "no
> `GetMinExtreme`/`HitTest` equivalent exists" in the port. That is **not** the
> case — the port already has real oriented hit-box extremes in
> `Collider.GetMinExtreme(int, Vector3, Orientation, Vector3)`
> (`Collider.cs:182-197`), fed by real `.cvh` hulls (`CvhCorpus`). I used the sphere
> anyway, because (a) the task explicitly sanctions that simplification and (b) it
> keeps the predicate unit-testable against bare kinematic ships without a wired
> lander `.cvh`. Upgrading `InGarage` to the full oriented hit-box later is a
> drop-in — the infrastructure is already there.

### Unit tests (`tests/Allegiance.Core.Tests/LandingBayTests.cs`, 6 tests)
- The three new capability constants equal their `igc.h` values; `faohshcar`'s real
  record carries the Carrier bit and not LandOnCarrier.
- `HullLandingBay.Reconstruct` on real `faohshcar` data (scale, plane count, plane
  values).
- `InGarage` against real data: real carrier at origin with its real `.cvh`, real
  tiny lander (`dreghpod`, length 2 → radius 1) so it fits. Inside point
  `(0,-23,31)` → **true**; three outside points (`(0,-23,5)` below the deck plane,
  `(0,0,31)` outside the y-band, `(100,-23,31)` outside in x) → **false**. Each
  expectation is hand-derived from the reconstructed half-spaces
  (x∈[−14.64,14.76], y∈[−31.65,−14.16], z∈[29.98,∞) for radius 1) and printed
  verbatim in the test comments.

No sim integration, no collision-dispatch wiring, no `RELAUNCH_SHIP`/`RescueShipEvent`
trigger — the predicate is exercised in isolation only, exactly as scoped.

---

## 4. Out of scope — confirmed left out (per task)
- `LandOnCarrierEvent` resupply / auto-loadout / part-upgrade / `GetDesiredLoadout()`
- Launch-slot repositioning (`GetLaunchPosition`/`GetLaunchDirection`) and the real
  `RELAUNCH_SHIP` broadcast
- Wiring `InGarage`/`LandOnCarrierEvent`/`RescueShipEvent` into the collision
  dispatch / sim loop
- `RescueShipEvent` / `PLAYER_RESCUED`(16) — still a confirmed real, separate,
  cheap finding (capability-bit-only, no bay geometry needed); deliberately not
  bundled
- No changes to P110-P113 ripcord/teleport/dock code

---

## 5. Concrete next tasks this unblocks

1. **`LandOnCarrierEvent` + resupply/loadout foundation.** The predicate and
   capability bits now exist; the next slice is `LandOnCarrierEvent`'s resupply /
   reload / part-upgrade behavior and the player "desired loadout"
   (`CFSPlayer::GetDesiredLoadout()`) preference system — scoped once this
   foundation's findings are in, as the task planned.
2. **`RELAUNCH_SHIP`(192) broadcast** with launch-slot repositioning: `HullLandingBay`
   already reconstructs `LaunchSlots` (position + direction, scaled), so the
   relaunch positioning has its geometry source ready. (`faohshcar` itself has no
   `launch1` — only a `launch` frame — so a relaunch-aware test should pick a
   carrier with real `launch1/2` frames, e.g. `p1_car`.)
3. **Wiring `InGarage` into the collision dispatch** (`shipIGC.cpp:1034-1047` port):
   the friendly carrier / land-on-carrier gates + `LandOnCarrierEvent` call path —
   the actual integration the native trigger chain shows.
4. **(Separate, cheap) `RescueShipEvent`/`PLAYER_RESCUED`(16)** — just the
   capability check (`c_habmRescue`/`c_habmLifepod`) + the event; its own small task.
5. **(Optional hardening)** Upgrade `InGarage` from the sphere extreme to the
   port's already-present real oriented hit-box extremes
   (`Collider.GetMinExtreme`) — drop-in, infra exists.

### Infrastructure a pure-`RescueShipEvent`-style follow-up or relaunch wiring would touch
No new loaders are needed: the `.cvh` frame source is already parsed by `CvhReader`.
What a follow-up must supply is the `modelName → .cvh` resolution (the corpus lookup
tests use: match `DataHullTypeIGC.ModelName` to a `CvhCorpus` filename), plus
whatever event/resupply bookkeeping `LandOnCarrierEvent` and `RELAUNCH_SHIP`
demand.
