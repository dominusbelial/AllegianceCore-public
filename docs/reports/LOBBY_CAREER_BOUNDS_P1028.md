# P1028 — Lobby career results: unbounded values and int overflow (R1 §4 lead)

Status: **done** (all parts confirmed and fixed; changes left uncommitted in the working tree).

## Part (a) — unbounded values on posted results

**Confirmed.** `CareerStore.ApplyMatchReport` applied every numeric field of a
`MatchResultEntry` verbatim once the server proof passed and the native gates
(`ScoresCount`, time played) held — no magnitude check existed anywhere on the
path (endpoint src/Allegiance.Lobby/Career/CareerEndpoints.cs:26-31 → store
src/Allegiance.Lobby/Career/CareerStore.cs:84-131, pre-fix numbering).

Confirmation test (run against the pre-fix tree, since replaced by the
fixed-behavior tests): `Posting_absurd_values_is_accepted_before_fix` posted
`Kills=int.MaxValue`, `Minutes=2_000_000_000`, `Score=float.MaxValue` through
`ApplyMatchReport`; outcome `(Applied=1, Duplicates=0, Skipped=0)` and
`GetCareer` served the values back. **Passed pre-fix** → bug real.

## Part (b) — int overflow in accumulated totals

**Confirmed.** The per-civ accumulation (pre-fix CareerStore.cs:130,
`prevMinutes + p.Minutes` and friends) was unchecked `int` arithmetic, and the
leaderboard's all-civ `SUM()` columns were parsed back with `int.Parse`
(pre-fix CareerStore.cs:244,247,249,250), which throws past `int.MaxValue`.

Confirmation test (pre-fix tree): `Posting_huge_then_small_minutes_wraps_negative_before_fix`
posted one match with `Minutes=int.MaxValue-1` and a second with `Minutes=10`;
the stored minutes wrapped to a negative value. **Passed pre-fix** → bug real.

## Fix

- **Validation on entry** — `MatchResultEntry.Validate()`
  (src/Allegiance.Lobby/Career/CareerContracts.cs) returns `null` or a short
  reason naming the offending field and bound. `ApplyMatchReport` validates
  every entry up front, *before* its transaction opens
  (CareerStore.cs, "P1028" comment), and throws `ReportRejectedException`
  (src/Allegiance.Lobby/Career/CareerStore.cs) on the first violation, so a
  rejected report can never partially apply. The endpoint maps the exception to
  `400 BadRequest` with the reason (CareerEndpoints.cs). Negative values are
  rejected (previously negative minutes were silently skipped; only exactly
  zero time played skips, matching the native gate `0.0f == GetTimePlayed()`,
  FedSrv.CPP:1052-1055). `NaN`/`Infinity` floats are rejected too.
- **Overflow-safe totals** — the accumulation into `career_by_civ` is now
  `checked` arithmetic (CareerStore.cs); on `OverflowException` the store throws
  `ReportRejectedException("... would overflow ...")` and the transaction rolls
  back (nothing applied, endpoint → 400). Kept `int` storage: the columns are
  `INTEGER` in the shared DDL (IdentityStore.cs:70), and changing the stored
  type would require a migration for existing rows — checked failure fits
  instead. The leaderboard's `SUM()` columns are now parsed with `long` and
  clamped to the int wire contract (`ClampedInt`, CareerStore.cs) instead of
  throwing on parse.
- Unchanged by design: `Minutes == 0` and `ScoresCount == false` entries remain
  idempotently skipped (native gates, FedSrv.CPP:1052-1065); the
  one-result-per-player-per-match key still de-duplicates replays.

## Limits chosen (CareerContracts.Limits)

No native values exist to mirror — native had no HTTP result-ingestion surface
(the club DB was fed by the operator-run game server itself), so these are
port-side bounds chosen well above anything a real match can produce:

| Field | Limit | Rationale |
|---|---|---|
| `Minutes` | 1440 | a match cannot outlast a day; Allegiance games end on goals or server time limits well under 3 h |
| `Score` | 100,000 | the rank ladder tops out at 1,000,000 career score (CareerRankTable); one match cannot award a tenth of that |
| `CombatRating` | 100,000 | same magnitude bound as the score |
| `Kills`/`Deaths`/`Ejections`/`Rescues`/`WarpsSpotted` | 1000 | respawn cycles alone make a thousand per sitting impossible |
| `BaseKills`/`BaseCaptures`/`Flags`/`Artifacts`/`TechsRecovered` | 100 | map-limited: a handful of bases, at most dozens of flaggable objects |
| `AsteroidsSpotted` | 10,000 | a sector holds at most hundreds of asteroids |
| `TimePlayedSeconds` | 1440·60 | consistency with `Minutes` |

## Tests

New file `tests/Allegiance.Lobby.Tests/P1028CareerBoundsTests.cs` (5 tests):
absurd values rejected with the field named; negatives rejected for every
numeric field; `Validate()` reason strings; near-`int.MaxValue` seeded totals
fail loudly and stay unchanged instead of wrapping; endpoint returns 400 with a
clear message and applies nothing (plus a valid report still applies).

- `~/.dotnet/dotnet test tests/Allegiance.Lobby.Tests --filter "FullyQualifiedName~P1028"` ×3:
  **Passed 5 / Failed 0 / Skipped 0** each run.
- Whole project once: **Passed 152 / Failed 0 / Skipped 9** (the 9 skips are the
  pre-existing Postgres-store skips, unchanged).
- `dotnet build AllegianceCore.slnx`: **0 warnings, 0 errors**.

## Unfinished

None. `GameResult`/`TeamResult` report sections are not validated: the career
store does not persist them yet (they are carried for wire compatibility,
CareerContracts.cs:84-120), so nothing they contain can reach storage; they
should be bounded when a games table grows.

---

**Native intent**: career stats are a port/lobby feature (P808b ports the native
club DB character-info + leaderboard screens, `Inc/ClubMessages.h:67-152,320-360`
and `FedSrv/FedSrv.CPP:1045-1140`); native has no equivalent of this HTTP
ingestion boundary, so the limits and 4xx handling are port hardening, not
parity. The native skip gates (time played zero, scores-not-counting) are kept
exactly (`FedSrv.CPP:1052-1065`).

**Modern approach**: validate at the trust boundary with named per-field limits
and a single rejection path (`ReportRejectedException` → 400 with the reason),
fail loudly on overflow with transactional rollback so nothing partially
applies, and keep the wire contract unchanged.
