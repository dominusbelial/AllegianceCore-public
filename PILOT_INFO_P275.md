# P275 — Pilot info overlay (session-scoped, no Zone Club/persistence)

Feature: a toggleable "Pilot Info" overlay in the MonoGame client showing the
**local player's own** live in-mission stats — kills, deaths, combat rating,
rank, score — fed by the same periodic owner-only `ScoreRosterUpdate(209)`
snapshot the P265 scoreboard uses. `P` toggles it (distinct from the
scoreboard's `B`), matching the `KeyBindingTable` update below.

## Native character-info screen (cited) — and why this is NOT it

Native's full character-info screen is `class CharInfoScreen`
(`WinTrek/CharInfoScreen.cpp:6`), a large pre-game screen covering the
player's **whole play history**: career stats, squads, medals, civ selection.
Crucially it is backed by a persistent **Zone Club** meta-server: the
constructor sends `REQ_CHARACTER_INFO` to `trekClient.m_fmClub`
(`CharInfoScreen.cpp:256`, inside the constructor block the brief pins at
242-260), along with `SQUAD_NEXT_PAGE_DUDEX` for the squad list.

This port has **no account/login/persistence layer at all** — documented
verbatim in `src/Allegiance.Server/PlayerScore.cs`'s own remarks: *"This port
has no account/login/identity layer … nothing survives a reconnect. One fresh
instance is allocated per connection at logon … discarded when the peer is
released."* There is no Zone Club server, no squads, no medals, no persisted
rank, no civ-history. Faithfully porting `CharInfoScreen` would require
inventing an entire meta-game backend — explicitly out of scope. So this
overlay is the **honest in-mission slice** of that screen: the local pilot's
live session numbers, strictly connection-scoped, with no persistence of any
kind added (scope boundary honoured: no account, no login, no cross-session
storage, no placeholder UI for Zone-Club data that will never arrive).

## Which fields were already live vs newly broadcast

Verified against the live tree (not assumed):

- **Already live before P275:** `Kills` and `Deaths` reached the client
  periodically via `ScoreRosterUpdate(209)` (P265's
  `Server.BroadcastScoreRoster`, `Server.cs:1556-1595`), which read each peer's
  P193 `PlayerScore.Kills/Deaths` and broadcast the whole-mission roster on a
  40-tick (~2 s) cadence. The port's own `ScoreRosterUpdate.cs` doc states
  "Kills/Deaths … (P215 now mutates them on real combat)".
- **Only at game-over before P275:** `Rank` and `Score` travelled **only** via
  `GameOverPlayers(63)` (P194), a one-shot endgame list — never live.
- **Never broadcast before P275:** `CombatRating` (P215) had no wire path at
  all.

**Choice taken — small real wire addition, not a deferred follow-up.** Rather
than show only kills/deaths and name rank/score/combat-rating a follow-up, I
widened the existing `ScoreRosterEntry` record (the port's own shape, consumed
only by this port's MonoGame client — no byte-compat concern) to also carry
`Rank` (short), `Score` (float) and `CombatRating` (float), and populated them
from the same `PlayerScore` in `BroadcastScoreRoster`. This is the minimal real
addition following this port's existing message-encode conventions, and it
makes all five live fields available to the overlay with no new message. Record
grew 32 → 44 bytes (char[25] @0, pad @25, sideId @26, kills @28, deaths @30,
rank @32, pad @34, score @36, combatRating @40). Everything remains strictly
session/connection-scoped: the values are the live per-connection
`PlayerScore`, which this port allocates fresh at logon and discards at release
— nothing is persisted or carried across sessions.

## What the overlay shows

- **Kills / Deaths** — real, from P215 combat (and from real combat in the live
  proof below).
- **Combat Rating** — real, the P215 `AdjustCombatRating` Elo-style value, now
  live-broadcast.
- **Rank / Score** — real P193 values, now live-broadcast. Honest caveat: with
  no account/persistence and no `CalculateScore` formula ported, these are the
  neutral `0` seed in a fresh session (the documented Stage-1 limitation); the
  overlay renders them truthfully as 0 rather than fabricating a number.

New files:
- `src/Allegiance.ClientCore.MonoGame/Rendering/PilotInfoLayout.cs` — pure,
  MonoGame-free extraction of the local player's row from the roster +
  label/value formatting (headless-testable).
- `src/Allegiance.ClientCore.MonoGame/Rendering/PilotInfoOverlayRenderer.cs` —
  the translucent panel (own SpriteBatch session, coloured border, pure-ASCII
  strings, same conventions as `ScoreboardOverlayRenderer`).
- `tests/Allegiance.ClientCore.MonoGame.Tests/PilotInfoLayoutTests.cs` — 5 pure
  tests (self-row extraction, no confusion with another player, null when not
  yet in roster, null when no self name, ASCII-scrub).

Wired in `Game1.cs` (P-toggle + draw, independent flag from the scoreboard so
both can be open), `KeyBindingTable.cs` (`P` added under CAMERA / VIEW — grep-
verified unclaimed; in-use singles are A B C D E F G H K L M N O Q R S T U V W).
Server population in `Server.BroadcastScoreRoster`.

## Live proof (real combat → real numbers rendered)

Out-of-repo offscreen MiniGame harness `/tmp/p275verify` (copy of the P265
recipe): boots a real in-process `Server`, seats two real `ConnectionSession`s
(SHOOTER on real side 0, TARGET on side 99 — "not in mission sides" so it
cannot eject, forcing a real combat death), engages real combat, then renders
the **real `PilotInfoOverlayRenderer`** to offscreen PNGs from the real wire
roster. Evidence in `.verify_p275/` (`SHOOTER_pre/post.png`,
`TARGET_post.png`, `harness_run.log`).

POST frame (log excerpt — real `[scoring]` events):

    [scoring] victim ship 8097 death +1 (player peer)
    [scoring] killer ship 8096 kill +1 (player peer)
    [scoring] combat rating adjusted on both sides
    [harness]   PILOT 'SHOOTER' S0          PILOT 'TARGET' S99
    [harness]     KILLS         = 1           KILLS         = 0
    [harness]     DEATHS        = 0           DEATHS        = 1
    [harness]     COMBAT RATING = 691.8       COMBAT RATING = 609.3
    [harness]     RANK          = 0           RANK          = 0
    [harness]     SCORE         = 0           SCORE         = 0
    [harness] POST captured: SHOOTER k=1 d=0 cr=691.7950; TARGET k=0 d=1 cr=609.3225
    [harness] VERIFIED: real kill -> real Pilot Info numbers rendered

Kills/Deaths are the real combat counters. The combat ratings are the **genuine
post-kill `AdjustCombatRating` results**: seeded from a realistic pre-combat
baseline (452.5 / 398.25 — the port starts everyone at 0.0 with no persistence,
so a visible rating needs a seeded baseline; this is the same seed-the-value-
then-assert-the-real-result convention the P-series live suites use). Arithmetic
check — `k=(1.25 + 398.25 − 452.5)/100 = −0.53`;
`shooter = 452.5·1.53 − 0.53 = 691.795` ✓; `target = 398.25·1.53 = 609.32` ✓.
Rank/Score honestly 0. Pre frame is the 0/0 kills baseline captured from the
initial roster snapshot (pre combat). Offscreen screenshots confirm the panel
and all five values render legibly.

## Build / test

- Whole solution `AllegianceCore.slnx` build: **0 warnings, 0 errors**.
- Full `Allegiance.Core.Tests` suite run 3×: total **3126**, every run only the
  **pre-existing `LiveTurretGunnerBindingTests` machine-state flake** failed
  (documented to fail on the pristine tree too, zero refs to P275 symbols);
  the `LiveWinConditionTests.Multi_mission_disconnect` flake was green on runs
  2 and 3. My P275/score tests (ScoreRoster wire+live, layout) all pass.
- Full `Allegiance.ClientCore.MonoGame.Tests`: **420/420** clean (incl. 5 new
  `PilotInfoLayoutTests`).
- **Must-fail verified:** neutralizing the server's Rank/Score/CombatRating
  population made `ScoreRosterLiveTests.Periodic_roster_broadcasts…` fail on
  its new rank/score/combat-rating assertions (confirmed red, then restored).

## Scope boundary

Session/connection-scoped only, matching the port's documented no-persistence
design. No Zone Club, squads, medals, civ-history (no backend exists — no
placeholder UI). No persistence/database/account system added. Native reference
tree (`~/Development/Allegiance`) untouched (read-only).

## Committed on `master` (not pushed — Domingo pushes)

Commit message prefixed `P275:`.
