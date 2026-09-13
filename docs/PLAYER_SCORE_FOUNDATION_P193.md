# PLAYER_SCORE_FOUNDATION (P193) — in-memory, connection-lifetime-only player score/rank

Branch `master`, on top of P192. Adds the first and smallest piece of the
player-stats/rank blocker: an honest, clearly-labelled **in-memory,
connection-lifetime-only** per-player `PlayerScore` (just `Rank` + `Score`)
attached to each connected player. P194 (`GAME_OVER_PLAYERS`, 63) and P195
(`RANDOMIZE_TEAMS`, 21) both build on this. It deliberately does **not**
claim to be "the tech tree" or "real player stats" — it resets to a neutral
default on every reconnect, and that limitation is stated everywhere it is
surfaced (code comments, this report, and the observable behavior).

## Background / relationship to native (verified against the live source)

- **`Wopr/AllegianceInterop/igcWrapper.h:2142-2200`** — the only place the
  shapes of `PlayerScoreObject`/`PersistPlayerScoreObject` are visible in this
  checkout (neither struct has its own C++ declaration anywhere; same
  missing-header shape as `MissionParams`, reconstructed from the managed
  interop wrapper's method list). The real fields visible include `Rank`
  (short, `GetRank()`), `Score` (float, `GetScore()`/`SetScore`),
  `CombatRating` (float), plus dozens of live-session counters (kills by ship
  class, base kills/captures, warps/asteroids spotted, techs recovered, repair
  contribution, connect/command time…) all feeding a `CalculateScore` formula.
  **This task ports none of those counters** — only `Rank` and `Score`, the two
  fields P194/P195 actually consume.
- **`FedSrv/fsmission.cpp:5217-5220`** (inside `RandomizeSides`, the P195
  consumer) — the real read site:
  ```c
  RankID rank = pshipTemp->GetPersistPlayerScore(NA)->GetRank();
  ```
  This confirms `Rank` is read from the **persisted** score object (per-civ,
  `GetPersistPlayerScore(civId)`), not the live `PlayerScoreObject`. Since this
  port has no persistence layer at all, both collapse to the same in-memory
  value — that collapsing is stated explicitly rather than hidden.

## What changed

1. **`src/Allegiance.Server/PlayerScore.cs`** (new) — `public sealed class
   PlayerScore` with exactly two members, both defaulting to neutral zero:
   `short Rank` and `float Score`. The class doc-comment is the single source
   of truth for what it is *not* (not a port of the full `PlayerScoreObject`;
   no counters), and for the non-persistence + rank-seeding limitations.

2. **`src/Allegiance.Server/Server.cs`**
   - `internal sealed record LiveShip(...)` gained a fourth component,
     `PlayerScore PlayerScore` — the per-connection container this port
     already uses for "state that lives as long as a connection/ship does"
     (the `Dictionary<ConnectionId, LiveShip> _peerShips` map, the same shape
     as `_peerNames`/`_playerReady`/`_pendingJoinRequests`). We deliberately
     reused it rather than inventing a second container.
   - `Server.AssignShip` (the validated-logon path) now constructs the
     `LiveShip` with a **fresh** `new PlayerScore()`, seeded `Rank=0,
     Score=0`.

### Exactly where `PlayerScore` is attached, and why

It is attached as a component of the `LiveShip` record, which is the existing
per-connection state holder in `_peerShips`. Choosing that placement makes the
lifecycle *structural* rather than a separate bookkeeping rule:

- **Created** in `AssignShip` at validated logon, alongside the ship placement
  — one per connection.
- **Discarded** by `ReleasePeer` (`_peerShips.Remove(id, ...)`), the single
  disconnect path shared by graceful `LOGOFF` and abrupt drops — removing the
  `LiveShip` necessarily drops its `PlayerScore`.
- **Never preserved**: a reconnect is a new logon → a brand-new `LiveShip` →
  a brand-new `PlayerScore` at the neutral defaults. There is no lookup of any
  prior value because there is nowhere to read one from.

### Rank seeding (the honest Stage-1 decision)

There is no real rank data anywhere — no account/login/identity system (the
port's deliberate no-account, `ALLSRV_STANDALONE`-equivalent design), no
historical games, nothing to seed from. Therefore **every** player's `Rank`
starts at the neutral default `0`. Consequence, stated plainly: P195's
"assign highest-rank-first" algorithm degrades to **assign in arrival /
iteration order** until something — deliberately *not* this task — ever
changes a player's `Rank`. This is a real, known limitation of the Stage-1
approach, not a bug to silently work around. It is documented in
`PlayerScore.cs` and here.

## Explicitly out of scope (unchanged / not attempted)

- Any of native's dozens of live-session score counters.
- Real persistence (a file, database, or any storage surviving a process
  restart or reconnect).
- Any account/login/identity system.
- Modifying `Rank`/`Score` from combat, mining, or any other gameplay event.
  Nothing in this port ever changes these values except whatever P194/P195
  need — and neither of those changes `Rank`/`Score` either; they only *read*
  them. (The class exposes settable properties so P194/P195 *could* write
  later, but nothing wires a writer in this task.)
- No wire message, no broadcast, no dispatch. This task is purely the
  in-memory data structure; P194 and P195 wire it to real fedmsgs.

## Tests — `tests/Allegiance.Core.Tests/PlayerScoreFoundationTests.cs` (3 new)

- `Newly_connected_player_score_starts_at_neutral_defaults` — every seated
  player's `PlayerScore` starts at `Rank=0, Score=0`.
- `Player_score_is_discarded_across_a_disconnect_and_reconnect` — mutate a
  seated player's `PlayerScore` to non-zero, disconnect, reconnect, and show
  the fresh connection is back at `Rank=0, Score=0`. This is the real proof of
  genuine non-persistence, not an accidental persistence leak.
- `Two_players_have_independent_player_score_instances` — two players in the
  same mission hold distinct `PlayerScore` instances (`Assert.NotSame`);
  mutating one leaves the other untouched.

All three are **discriminating**: temporarily substituting a shared,
non-reset singleton `PlayerScore` for the fresh-per-connection allocation makes
all three fail (default-seed test sees the stale non-zero `Rank`; reconnect
test sees the leaked value; independence test's `NotSame` trips). Verified by
running the suite against that probe, watching it go red, then reverting.

## Verification

- `/home/domingo/.dotnet/dotnet build` clean (0 warnings, 0 errors).
- New tests pass in isolation: 3/3.
- Full-suite baseline moved **2957 → 2960** (the +3 above; total 2960).
- Full-suite runs: multiple fully-clean runs at 2960 (0 failures). Other runs
  were interrupted only by the documented live-network machine-state flakes —
  `LiveCombatWireTests.*`, `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`,
  `LiveChatSectorWireTests.*`, `LiveWinConditionTests.*` — each verified
  environmental by failing identically on the **pristine** tree (git stash -u)
  and/or passing in isolation, and each file with zero references to the
  changed symbols. No non-documented failure was observed in any run.

## The non-persistence limitation, restated for whoever reads this later

A `PlayerScore` exists only for the duration of one TCP connection and is
created fresh at its logon with the neutral defaults `Rank=0, Score=0`. It is
destroyed the moment that connection ends, and a later connection by the same
human (even with the same character name) starts over at the defaults. There
is no account to key it to, no storage to read a prior value from, and no code
path that carries a value across a reconnect. If P194/P195 are later wired to
surfaces that imply persistence, this foundation provides none — it must be
built on top, not assumed.
