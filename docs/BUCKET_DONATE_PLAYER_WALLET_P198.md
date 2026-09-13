# P198 — BUCKET_DONATE spends from the player's own wallet

Landing on top of **P197** (per-player `PlayerWallet` + `MoneyChange` codec).
Branch `master`, prior top commit `ae7b894 P197: per-player money & auto-donate foundation`.

## What changed

### `Server.HandleBucketDonate` (`src/Allegiance.Server/Server.cs`)
The already-shipped `FM_C_BUCKET_DONATE(64)` handler (ported in P119) spent
directly from the side's pooled treasury. This task moved **both the gate and
the debit onto the donating player's own wallet**, matching native
`FedSrv.CPP:7137-7211`:

| Aspect | P119 (before) | P198 (after) |
|---|---|---|
| Gate | `side.Money < moneyGiven` → refuse | `live.Wallet.Money < moneyGiven` → refuse (native `pfsPlayer->GetMoney() >= moneyGiven`, `FedSrv.CPP:7148`) |
| Debit | `side.Money -= spent` | `live.Wallet.Money -= spent` (native `pfsPlayer->SetMoney(... - mnySpent)`, `FedSrv.CPP:7160`) |
| `Side.Money` | spend source | **untouched by this handler entirely** |

The other gates (stage `STAGE_STARTED`, side/bucket resolution, negative
`moneyGiven`) are unchanged. `Side.Money` continues to accrue income via
payday; it is simply no longer a bucket-spend source. **No accrual code was
touched** (scope boundary honored).

### New `MONEY_CHANGE(60)` broadcasts (native `FedSrv.CPP:7162-7168`, `:7199-7208`)
On a successful spend the handler now emits two notifications:
1. **Negative spend** to the **whole side's peer group**
   (`BroadcastBucketDonateSpendSideWide`): `dMoney = -spent`,
   `sidTo == sidFrom == the donating player's ship id`. This is the
   *player-initiated purchase* shape (`sidFrom == sidTo`), distinct from the
   server-initiated `sidFrom = NA` used by `BroadcastMoneyChangeSideWide`
   (board/teleport refunds).
2. **Positive refund** to the **donating player alone** when the bucket needed
   less than offered: `dMoney = +mnyRefund`, `sidTo = the donating ship`,
   `sidFrom = NA` (native sends it to `pfsPlayer->GetConnection()` only).

### Refund-of-unneeded-money path (found in the live native source)
Native computes `mnyRefund = moneyGiven - mnySpent` (`FedSrv.CPP:7151-7158`),
where `mnySpent` is what `AddMoney` actually took (capped at the bucket's
remaining price, `bucketigc.h:281-293`). It only **debited** `mnySpent`, so the
excess physically stays with the donor; the refund `MONEY_CHANGE` is the wire
notification of that. This port replicates it exactly: debit only `spent`, and
when `moneyGiven > spent` send the `+mnyRefund` `MONEY_CHANGE` to the donor.
(P119 had reached the same *money outcome* by "simply never spending the
excess", but emitted no refund notification and pulled from the pool; P198 now
notifies the donor per native.)

The `GetMoney() < 0` branch (`FedSrv.CPP:7170-7180`) fires an AGC telemetry
event (`AllsrvEventID_MoneyError`) — this port has no AGC event sink, so that
branch is intentionally not ported (logged as a known gap, not a silent skip).

## Which pre-existing tests were touched, and how their assertions changed

### `tests/Allegiance.Core.Tests/LiveBucketTests.cs`
- **`Small_donation_funds_bucket_and_broadcasts_sidewide_not_cross_side`** —
  seeding moved from `side.Money = 100000` to the funder's `Wallet.Money =
  100000`, with the side treasury set to an untouched sentinel `888888`. Old
  assertion `side.Money == 100000 - donate` **became** `funder.Wallet.Money ==
  100000 - donate` **and** `side.Money == 888888` (proves the spend left the
  wallet, not the pool). Added: negative `MONEY_CHANGE(-donate)` with
  `sidTo == sidFrom == donor` reaching both same-side peers, absent cross-side.
- **`Oversized_donation_caps_at_price_and_leaves_excess_in_treasury`** —
  seeding moved to the funder's wallet; `side.Money` becomes sentinel `666666`.
  Old `side.Money == 100000 - price` **became** `funder.Wallet.Money == 100000
  - price` (excess stays in the **wallet**) and `side.Money == 666666`. Added:
  the negative spend `MONEY_CHANGE(-price)` AND the positive refund
  `MONEY_CHANGE(+excess, sidFrom = NA)` to the funder only.
- **`Full_funding_plus_build_time_fires_completion_seam_only`** — seeding moved
  to the builder's wallet; `side.Money` sentinel `555555`. Added wallet-debited
  by full price + side-sentinel-untouched assertions.
- **`Join_broadcasts_create_buckets_and_per_bucket_status`** — untouched (no
  donation performed).

New test added in the same file (suite 2986 → 2987):
- **`Insufficient_player_wallet_refused_even_if_side_treasury_is_large`** — the
  *must-fail-if-removed* proof: player wallet `10`, donation `5000`, side
  treasury `100000`. Asserts no `BUCKET_STATUS`, no `MONEY_CHANGE`, bucket
  unfunded. Verified to FAIL when the gate is reverted to `side.Money`.

### `tests/Allegiance.Core.Tests/TechTreeDevelopmentLiveTests.cs`
These fund development buckets through `HandleBucketDonate` and previously
seeded `side.Money = 10_000_000`. Seeding moved to the donor's wallet:
- **`FundAndComplete`** helper — seeds `funder.Wallet.Money = 10_000_000`.
- **`Side_tech_change_suppressed_before_start_and_sent_after`** — both inline
  funding sites seed the bot's wallet (per-donation reseed kept, matching the
  "re-fund before each donation" convention).

### Discrimination ("must fail if removed") verification
- Reverting the gate to `side.Money` → refusal test FAILS (donation accepted +
  broadcast when the pool is rich).
- Pointing the debit back at `side.Money` → small/oversized tests FAIL
  (sentinel `side.Money` changes, wallet not debited).
Both reverted cleanly after.

## Divergences from the prompt's assumptions
None material. The prompt anticipated reading the live function tail for the
refund path — confirmed and ported (refund `MONEY_CHANGE` to the donor, `sidFrom
= NA`). The prompt's "income still pools, only spend moves" reading is
confirmed against the handler: `Side.Money` remains the payday/accrual pool and
is no longer consulted or debited here. The `GetMoney() < 0` AGC-event branch
is out of scope for this port (no AGC event sink) and is called out above.

## Verification
- `/home/domingo/.dotnet/dotnet build src/Allegiance.Server/...` — clean, 0
  warnings / 0 errors.
- `dotnet test` full solution, Debug: **three fully-clean consecutive runs**
  at **2987/2987** (runs 1, 2, 4). Run 3 had a single failure —
  `LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire` —
  a documented machine-state flake: the file has **zero** references to
  money/wallet/bucket symbols, and the test fails 1/1 **identically on the
  pristine P197 tree** (verified via `git stash` → run → `pop`).
- `LiveBucketTests` run explicitly: **5/5 green** with their updated meanings.
- `TechTreeDevelopmentLiveTests`: **6/6 green** after the seeding updates.
