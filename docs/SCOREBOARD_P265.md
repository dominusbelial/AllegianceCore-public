# P265 — In-mission scoreboard / player roster (real kills/deaths)

Feature: a toggleable in-mission scoreboard overlay in the MonoGame client
showing every real-side player's name, side, kills and deaths — the real P193
`PlayerScore` values P215 now mutates in combat. Zero client rendering of any
of this existed before this stage.

## Native roster content (cited)

`WinTrek/teampane.cpp:570` wires `PlayerListPane`, and `PlayerPainter::Paint`
(`teampane.cpp:127-245`) draws, per player row:

- `CharacterName()` — the player name (`:150-167`), white, or maroon when docked.
- Money `"$: N"` — humans on own/allied side (`:200-231`).
- Deaths via `MissionDeaths()` at column x143 (`:235-238`).
- Kills via `MissionKills()` at column x120 (`:241-244`).

Native's sortable columns (the `OnButtonBar` sort, `:997-1044`) cover
leader/status/name/ship/sector/credits/objective/rank/wing/**deaths/kills**/turrets.
`PlayerRankCompare` reads `GetPersistScore(NA).GetRank()` (`:1866`).

The task asks for name/side/kills/deaths at minimum; the side is carried in the
record even though native's per-side pane shows one team at a time.

## Wire mechanism: NEW periodic owner-only ScoreRosterUpdate(209)

Finding: the only existing player-roster message, `FM_S_PLAYERINFO(10)`
(`src/Allegiance.Core/Wire/PlayerInfo.cs`), is a **same-side, one-shot**
identity snapshot sent at join (`Server.BroadcastShipStates`) whose
nKills/nEjections/nDeaths fields are **hard-zeroed on encode**
(`PlayerInfo.cs:180`) — it is never resent and carries no score. The per-tick
SHIP_UPDATE carries physics, not scores. So none of the existing traffic can
surface P215's real Kills/Deaths to the renderer-only MonoGame client (which
has no static core to read scores locally — same A-vs-B constraint as
P230/P255/P264).

Therefore the smallest real wire addition is a dedicated periodic snapshot,
following the established owner-only periodic-extension precedent (P230
`MissileLockUpdate` 204, P255 `OwnShipMiningUpdate` 206, P264
`MissileTrackWarningUpdate` 208) and the P194 `GameOverPlayers(63)` var-item
list-reporting shape:

- `src/Allegiance.Core/Wire/ScoreRosterUpdate.cs` — FMID **209** (above native's
  real max ≈200 and the port's 204-208), a single `FM_VAR_ITEM` blob of fixed
  `ScoreRosterEntry{characterName[25], sideId, kills, deaths}` records
  (32 B each, no count field; count = blob ÷ 32), byte-identical encoding
  convention to `GameOverPlayers`.
- `Server.BroadcastScoreRoster()` — called per sim tick, throttled to
  `ScoreRosterRefreshTicks = 40` (~2 s @ 20 upd/s), keyed per mission. Builds the
  whole-mission roster (every peer on a **real** side — `SideId == null` is the
  port's team-lobby analogue, excluded like native's SIDE_TEAMLOBBY) reading
  `live.PlayerScore.Kills/Deaths` + `_peerNames`, and sends each mission's list
  to every peer in that mission. Kills/Deaths clamped to short (wire tradition,
  same 16-bit width as native's `PLAYERINFO`).
- Client: `ClientWorld` gains `ScoreRoster` (replaced wholesale on each
  receive) + `Apply(ScoreRosterUpdate)` + the dispatch `case` inside
  `Apply(IFedMessage)`'s switch (the P264 trap: the per-type `Apply` alone is
  dead code without the hub case); `ConnectionSession` gains the 209 receive
  case.

Why a periodic broadcast and not a request/response (cf. P200 PINGDATA): the
scoreboard should stay live-updated without the client polling; a throttled
server push is the smallest thing that keeps it fresh.

## Toggle key: B (scoreBoaRd)

`docs/KEYBINDING_AUDIT_P220.md` is stale (predates Tab/H/several), so I grepped
`Game1.cs` for `XnaKeys.` myself. Claimed letters: A C D E G H K M N Q R S T V W
(plus Shift/Ctrl/Space/Tab/`/[/]/arrows/Enter/Esc/Back). **B is unclaimed**, a
normal single letter, and mnemonic for "scoreboaRd". Edge-triggered + composing-
gated like the H toggle, independent of the starmap/help flags. The `H` help
overlay's `KeyBindingTable` was updated with the B row (P257's named
maintenance burden).

## Verification

- Whole-solution build: 0 warnings, 0 errors.
- **Live visual proof (real combat event):** `/tmp/p265verify` — a real
  in-process `Allegiance.Server` with a fight-hull shooter (real side 0) and a
  target on a non-mission side (99, so it cannot eject — P215's real-death
  technique), two real `ConnectionSession`s, real projectile combat killing the
  target. The real `ScoreboardOverlayRenderer` is drawn offscreen to PNGs:
  - `score_pre.png`: SHOOTER S0 k=0 d=0, TARGET S99 k=0 d=0
  - `score_post.png`: SHOOTER S0 **k=1 d=0** (green self-highlight), TARGET S99
    **k=0 d=1** — real live-updated numbers, no fabricated values. (Vision-verified
    the rendered text/numbers; "SCOREBOARD (B TOGGLE)" + NAME/SIDE/K/D headers.)
- **Tests:**
  - Core `ScoreRosterWireTests` (+3): codec round-trip, 32 B stride + offset pins,
    empty-list fixed header.
  - Core `ScoreRosterLiveTests` (+1, deterministic): two real peers on real
    sides; seed distinct real `PlayerScore` kills/deaths (P194 convention — the
    real combat → PlayerScore direction is P215's proven territory; the real-combat
    visual proof is the harness above), assert the periodic broadcast carries both
    players with their own side and real values. **Must-fail proven:** disabling
    `BroadcastScoreRoster()` reds it ("no post-seed ScoreRosterUpdate...").
  - MonoGame `ScoreboardLayoutTests` (+4): side-then-kills ordering, ASCII
    scrubbing of non-ASCII names, numeric side label, column-width accommodation.
- **Full suites (baseline 3104 Core, 342 MonoGame, 117 ClientCore):**
  - Core run: only the pre-existing `LiveTurretGunnerBindingTests` flake
    (machine-state, zero refs to P265 symbols) — 3103/3104 (details below).
  - MonoGame 342/342, ClientCore 117/117, all green.

## Notes / scope boundary

- Rank is intentionally not displayed: `PlayerScore.Rank` is the always-0 Stage-1
  seed (known limitation), and the task explicitly scopes rank progression out.
  The overlay surfaces only name/side/kills/deaths.
- Squad/profile lookups (`LeaderBoardScreen`, online-service-coupled) remain out
  of scope; this is a minimal in-mission roster only.
- Native reference (`~/Development/Allegiance`) untouched (read-only).
- The side column shows the numeric id (e.g. "S0"), since this port's client has
  no side-name registry on the wire — honest real data, not a fabricated label.
