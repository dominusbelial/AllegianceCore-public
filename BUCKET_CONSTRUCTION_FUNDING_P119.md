# P119 — Bucket construction funding: BUCKET_DONATE(64) / BUCKET_STATUS(65) / CREATE_BUCKETS(191)

Date: 2026-09-09
Repo: `/home/domingo/Development/AllegianceCore` (branch `master`)
Task: build the construction-funding mechanic (donate + status + the bucket-catalog
broadcast) — the foundation slice of the bucket system P118 named as Phase 4's next
slice — WITHOUT building what a completed bucket produces (spawn drone / build
factory or storage pit), which is the explicit follow-up task.

---

## 1. The one genuinely-open question — bucket-catalog source, RESOLVED

**Finding (stated plainly): bucket definitions are NOT part of the mission `.igc`
corpus nor of this port's static-data decode.** Verified against the native
source and the port's decoder:

- Native creates buckets at **runtime**, wrapping a side's civilizational buyables
  — `CsideIGC::CreateBuckets` (`src/Igc/sideigc.cpp:113-310`) builds a
  `DataBucketIGC db = {<buyable>, side}` around each buildable and instantiates an
  `OT_bucket`. There is no serialized bucket record in any `.igc` file.
- `BucketData` (`src/Inc/Messages.h:505-525`) is a **client-side UI struct**
  (bucketitemtype/cost/name/description + hullidDrone), not a file record.
- The port decodes no `DataBucketIGC` (a grep of `src/Allegiance.Core/Data/` and
  `docs/` for bucket/OТ_bucket records returns nothing — only documentation).

Because there is no real catalog source, this task **seeds an explicitly-synthetic
catalog**: one `BIT_ShipType` bucket per side (`BucketCatalog`, Model/Bucket.cs),
referencing a **real** corpus hull (id 504, PCore001.igc) for the drone-hull
reference and the human name/description.

**A second, measured finding shaped the seed:** this port's corpus decode yields
`TimeToBuild == 0` for **every** hull record in PCore001.igc (measured by dumping
all `core.HullTypes` via `StaticCore.Load`; no hull has a nonzero build time). The
native build-time field is not meaningfully populated by this decode, so deriving
a bucket's build time from a real hull would produce a degenerate zero-time bucket
that can never traverse the completion gate (bucketigc.h:51). The synthetic bucket
therefore carries its own explicit `SyntheticPrice = 5800` and
`SyntheticTimeToBuild = 30` (seconds), and uses the real hull 504 only for
`hullIdDrone`/name/description — **labeled synthetic**, never presented as decoded
real data.

## 2. The load-bearing state-machine facts (read from bucketigc.h, not guessed)

The port's `Bucket` (Model/Bucket.cs) ports `CbucketIGC` (`src/Igc/bucketigc.h:22-324`,
read in full):

- **Completion gate** — `ttbms = m_timeToBuild * 1000` ms (bucketigc.h:46), and
  completion when `(m_investedMoney >= m_price) && (m_investedMilliseconds >= ttbms)`
  (bucketigc.h:51). Time only accrues while there is invested money (bucketigc.h:44)
  and is **clamped** to the money-proportional share
  `mim = 1000 * ((m_investedMoney * m_timeToBuild) / m_price)` (bucketigc.h:58-61),
  so completion requires money-full AND time-full together.
- **Leftover-refund / cap** — `AddMoney`: `leftover = price - invested - m; if
  (leftover < 0) m += leftover;` (bucketigc.h:284-287), invests the capped amount
  and **returns the actually-spent figure** (bucketigc.h:289-292).
- **Time advance** — native `Update` refreshes `m_lastUpdate` unconditionally
  (bucketigc.h:73) and only accumulates when funded; the port mirrors this by
  calling `Bucket.Update` from `Side.Update` every tick (`Mission.Update` →
  `side.Update`, Mission.cs:277-281), so `_lastUpdate` stays current and a funded
  bucket's time advances toward `ttbms`.

## 3. Pooled-money donation mapping + leftover refund (with real test evidence)

**Mapping (stated plainly): donations draw from the side's pooled `Side.Money`
(Model/Side.cs:52), not an individual wallet** — the same honest single-pool
simplification `SET_MONEY` (P118) established. Native's per-player check
`pfsPlayer->GetMoney() >= moneyGiven` (`FedSrv.CPP:7148`) becomes
`Side.Money >= moneyGiven` in the port's `HandleBucketDonate` (Server.cs).

The donation handler (`HandleBucketDonate`, port of `FedSrv.CPP:7137-7211`) does
three things faithfully:

1. **Caps at what's still needed** — `bucket.AddMoney(moneyGiven)` returns the
   actually-spent figure (bucketigc.h:289-292); only that figure leaves the pool.
2. **Refunds the excess implicitly** — because only `spent` is deducted from
   `Side.Money`, an over-sized donation's excess simply never leaves the treasury.
   This is the ported equivalent of the native's explicit `mnyRefund` path
   (FedSrv.CPP:7151-7160): the native "spends only what's needed and sends back
   the rest"; the port "spends only what's needed and leaves the rest in the pool".
3. **Broadcasts `BUCKET_STATUS` side-wide** — `BroadcastBucketStatusSideWide`
   (Server.cs) sends to every peer seated on the side, port of the native send to
   the whole side's peer group (`FedSrv.CPP:7183-7196`), and NOT cross-side.

**Real test evidence (LiveBucketTests, all green):**

- **Small donation** (`Small_donation_funds_bucket_and_broadcasts_sidewide_not_cross_side`):
  donating 5799 (< price 5800) left the bucket invested exactly 5799, the treasury
  fell by exactly 5799 (100000 → 94201), `BUCKET_STATUS` reached BOTH same-side
  peers with moneyTotal=5799/iBucket/sideID correct, and the OTHER side's peer saw
  none.
- **Over-sized donation** (`Oversized_donation_caps_at_price_and_leaves_excess_in_treasury`):
  donating 62120 (price 5800 + 56320 excess) invested the bucket to **exactly 5800**
  (never over), treasury fell by exactly 5800 (the excess 56320 stayed in the pool:
  100000 → 94200), and the broadcast reported moneyTotal=5800.

## 4. What was wired (with citations)

1. `Wire/BucketDonate.cs` — `BUCKET_DONATE(64)` codec, FixedSize 12 (Money +
   BucketID + 2 trailing pad, Messages.h:488-491).
2. `Wire/BucketStatus.cs` — `BUCKET_STATUS(65)` codec, FixedSize 20 (timeTotal +
   sideID + pad + moneyTotal + iBucket + pad, Messages.h:493-498).
3. `Wire/CreateBuckets.cs` — `CREATE_BUCKETS(191)` codec, FixedSize 104 (two 50-byte
   TechTreeBitMask fields, Messages.h:983-986; mask width `u8[((400-1)/8)+1]=50`,
   zlib/mask.h:351), both masks **zeroed** (see §5).
4. `Model/Bucket.cs` — the `Bucket` state machine (add-money cap + build-time
   accumulation + completion detection) and the synthetic `BucketCatalog`.
5. `Model/Side.cs` — `Side.Buckets` collection, advanced in `Side.Update`;
   `Mission.Assemble` (Mission.cs:173) seeds each side from `BucketCatalog`.
6. `Server.cs` — `BUCKET_DONATE` inbound dispatch branch + `HandleBucketDonate` +
   `BroadcastBucketStatusSideWide`; join-sync (the SET_MONEY block, Server.cs:1255+)
   now also sends `CREATE_BUCKETS` and one `BUCKET_STATUS` per side bucket to the
   joining peer (native `SendMissionInfo`, fsmission.cpp:4255-4273).
7. `Sim/IIgcSite.cs` + `Sim/RecordingIgcSite.cs` — a new C#-only no-op seam
   `BucketCompleted(IBucketIgc, double)` (the port's established no-op-default seam
   convention, confirmed to exist via `IIgcSite.BucketChangeEvent`/`LandOnCarrierEvent`);
   ServerHost exposes a `BucketCompletedHook` and the Server records completions
   (`Server.BucketCompletions`) for observation — **detection only**.
8. `Wire/FedMessageDumper.cs` already named 64/65/191 (no change needed); `Bot.cs`
   gained `SendDonate`, `BucketStatuses`/`CreateBucketsReceived` capture.

## 5. Tech-tree simplification (confirmed absent)

The two `CREATE_BUCKETS` fields are `TechTreeBitMask` (`GetDevelopmentTechs()` /
`GetInitialTechs()`, fsmission.cpp:4257-4258). The `TechTreeBitMask` infrastructure
is **confirmed absent** from this port (the same P117 finding every tech-tree-gated
slice in this arc recorded), so — per the task's stated simplification — the two
50-byte masks are transmitted all-zero (exactly what a freshly-cleared bitmask is,
mask.h:149-152), riding the full wire width so the frame is byte-correct against the
reference protocol. No new tech-tree system was added.

## 6. PLAYER_DONATE / AUTODONATE finding

Briefly investigated, read directly:

- `PLAYER_DONATE(67)` (`FedSrv.CPP:7213-7262`): a per-player money transfer between
  two specific ships (`pfssTo->SetMoney(...)`, ally-gated). It is fundamentally
  per-player-wallet dependent — there is no individual wallet in this port to move
  money from/to — so it does **not** map onto the pooled `Side.Money` architecture.
  **N/A without a real reinterpretation** (akin to `SET_MONEY`'s single-pool
  simplification, but no sensible pooled reading of "donate from ship A to ship B"
  exists). Not implemented.
- `AUTODONATE(112/116)`: sets/reads a player's automatic bucket-donation target
  amount — again per-player, requiring a per-player donation config and a personal
  wallet the port lacks. **N/A** under the pooled model. Not implemented.

Neither is forced into this task (explicitly out of scope).

## 7. Verification (all green)

- Codec tests `Wire/BucketWireTests.cs` (14): FixedSize pins, round-trips
  (incl. nonzero 50-byte masks for `CREATE_BUCKETS`), byte-exact frames against the
  independently-derived `ByteBuf` expectations, and reject (truncated / wrong-fmid)
  cases for all three codecs.
- Live loopback tests `LiveBucketTests.cs` (4) over a real in-process server + real
  TCP bots, covering the four scenarios in §3 + join-sync (§4.6) + completion:
  fully funding a bucket AND letting 30s of build time pass fired the completion
  seam **exactly once** — `Server.BucketCompletions == [bucketId]`, `bucket.Completed`
  true, **no object spawned** (ship count unchanged), **no further broadcast** (no
  second `BUCKET_STATUS`) — the explicit "detect but don't act yet" boundary.
- Full existing suite: **2555 passed, 0 failed** (P118 checkpoint 2537 + 18 new).
  Three consecutive clean full-suite runs recorded. Two transient pre-existing
  timing flakes (LiveChatSectorWireTests, LiveCombatWireTests) fired mid-way across
  separate runs and reran clean — the documented ~1-in-8 flakes, not P119 regressions.

## 8. Next task (the immediate follow-up)

**Bucket-completion effects** — what happens when a bucket's money AND time thresholds
are both met: for a `BIT_ShipType` bucket, spawn the drone hull (native
`DroneTypeCompleted`, bucketigc.h:212); the completed-detection seam
(`IIgcSite.BucketCompleted`) and `Bucket.Completed` state are in place and ready to
drive it. Also open later: a real bucket-catalog source (native sideigc.cpp:113-310
buildup from civilizational buyables, once tech/dev data is available) and the
`PLAYER_DONATE`/`AUTODONATE` reinterpretation above.
