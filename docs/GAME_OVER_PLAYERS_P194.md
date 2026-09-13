# GAME_OVER_PLAYERS (P194) — per-player endgame broadcast

Branch `master`, on top of P193 (commit `69ee9d3`, `PlayerScore`). Adds the
`GAME_OVER_PLAYERS`(63) wire codec and wires it into P178/P191's shared
`Server.DeclareGameOver` hook so that whenever a mission ends it broadcasts a
per-player name/side/rank/score list whole-mission **alongside** `GAME_OVER`(62).

## Background / relationship to native (verified against the live source)

- **`Inc/Messages.h:481-489`** — the native message and its per-player record,
  quoted in the task brief and re-verified:
  ```c
  struct PlayerEndgameInfo
  {
    char                      characterName[c_cbName];
    GameOverScoreObject       scoring;
    PersistPlayerScoreObject  stats;
    SideID                    sideId;
  };
  DEFINE_FEDMSG(S, GAME_OVER_PLAYERS, 63)
    FM_VAR_ITEM(rgPlayerInfo);
  END_FEDMSG
  ```
  The sole member is the `rgPlayerInfo` var item — a variable-length blob of
  fixed per-player records with no explicit count field (count = blob bytes /
  record stride), exactly the `ProbesUpdate`(92) shape.
- **`FedSrv/fsmission.cpp:3088-3141`** — the real sender, inside
  `CFSMission::QueueGameoverMessage` (the function that also builds
  `GAME_OVER`). The loop over `m_pMission->GetShips()` collects only players
  who (a) have actually played (`GetTimePlayed() > 0.0f`) and (b) are on a real
  side (`GetSide()->GetObjectID() != SIDE_TEAMLOBBY`), then sends in chunks of
  at most 50 players per message (`nMaxPlayersPerMsg = 50`,
  `fsmission.cpp:3131-3141`). `QueueGameoverMessage` is invoked from
  `ProcessGameOver` — the same moment this port's `DeclareGameOver` fires.

### The payload substitution (the central design decision)

Native's `PlayerEndgameInfo` embeds `GameOverScoreObject` and
`PersistPlayerScoreObject`. **Neither has a declaration anywhere in the native
checkout** — the same missing-header shape the P193 report already documented
(the only visibility into these types is the managed interop wrapper method
list at `Wopr/AllegianceInterop/igcWrapper.h:2142-2200`, which exposes dozens
of live-session counters feeding a `CalculateScore` formula). This port does
**not** interoperate with a real native client, so chasing wire fidelity is
pointless. This task therefore defines its **own, much smaller** per-player
payload carrying exactly what P193's `PlayerScore` provides and this port can
honestly report:

```
{characterName: char[25], sideId: short, rank: short, score: float}
```

`characterName` is `c_cbName = 25` (same fixed-name width every other ported
name-bearing record uses); `rank`/`score` are read straight from P193's
`PlayerScore`; `sideId` is the ship's real side. **This is a deliberate,
explicitly-non-faithful substitution, not a claim of byte compatibility** —
stated in the codec's doc-comment and here.

### Record stride (this port's own layout, natural alignment)

Since this is our own shape, the layout mirrors the alignment convention the
sibling `SideEndgameInfo` already documents (natural MSVC alignment, explicit
pad bytes):

```
offset 0:  char characterName[25]
offset 25: — pad (1) — align sideId short to even
offset 26: short sideId
offset 28: short rank
offset 30: — pad (2) — align score float to 4
offset 32: float score
= 36 bytes
```

Message: header(4) + one ib/cb var pair(4) = 8 fixed bytes, then the blob of
`n × 36`. Pinned by a discrete-offset layout test.

## What changed

1. **`src/Allegiance.Core/Wire/GameOverPlayers.cs`** (new) — `GameOverPlayerEntry`
   (the 36-byte record) and `GameOverPlayers` (fmid 63), modeled field-for-field
   on `ProbesUpdate` (single var-item blob, no count field). Encoder/decoder
   with explicit offset accounting and layout-drift guards.

2. **`src/Allegiance.Server/Server.cs`** — `DeclareGameOver` now calls the new
   `BroadcastGameOverPlayers(mission)` after the `GameOver` broadcast, using the
   **same** shared end-of-mission hook (the explicit P191 instruction: never a
   second game-over path). The new method gathers every peer whose ship belongs
   to that mission and whose `Ship.SideId` is a real (non-null) side, then sends
   one `GameOverPlayers` to the same whole-mission audience `GameOver` uses.

3. **`src/Allegiance.Bot/Bot.cs`** — added a `GameOverPlayerBroadcasts`
   accumulator (property/reset + a receive-switch case) so live tests can
   observe the broadcast. Named deliberately **different from the
   `GameOverPlayers` type** it holds to avoid the P186 `ChangeAlliances`
   shadowing pitfall.

### Player filters (native's real ones, ported)

- **"Has actually played" (`GetTimePlayed() > 0`)** — this port has **no**
  time-played counter anywhere (P193's `PlayerScore` is only `Rank`/`Score`), so
  the task's allowed fallback applies: the filter collapses to "is a seated
  connection." Every connected peer is included.
- **"On a real side" (`!= SIDE_TEAMLOBBY`)** — ported. This port has no explicit
  team-lobby side; a peer whose ship has `SideId == null` is the closest
  analogue of native lobby membership and is excluded exactly as native
  excludes `SIDE_TEAMLOBBY`.

### The `m_oldPlayers` simplification (stated plainly)

Native's second loop over `m_oldPlayers` (`fsmission.cpp:3115-3128`,
recently-disconnected-but-still-tracked players) is **out of scope**: this port
has no "keep a disconnected player's stats around briefly" mechanism, so a
disconnected player simply isn't in the list, whereas native would still include
them if they'd played and disconnected recently. Only currently-connected
players are reported.

### The chunking decision (stated plainly)

Native sends the list in ≤50-player chunks (`nMaxPlayersPerMsg = 50`). **This
port sends one message** — judged genuinely unneeded at this port's scale
(test missions seat a handful of peers, and there is no `m_oldPlayers` backlog).
The single-message blob codec is trivially reusable if chunking is ever wanted.

## Explicitly out of scope (unchanged)

- `m_oldPlayers` recently-disconnected tracking (above).
- Any of native's real `GameOverScoreObject`/`PersistPlayerScoreObject` fields
  beyond `Rank`/`Score` — this task uses its own smaller, explicitly-
  non-faithful payload.
- Modifying `Rank`/`Score` — P194 only *reads* P193's values, never writes them.

## Tests — `tests/Allegiance.Core.Tests/GameOverPlayersWireTests.cs` (5 new)

1. `GameOverPlayers_round_trips_payload` (codec) — a 3-player list round-trips
   name/side/rank/score, including the >24-char name truncation to 24 + NUL.
2. `GameOverPlayers_layout_pins_stride_and_offsets` (codec) — pins Size=36,
   FixedSize=8, ib/cb, and each discrete record offset (name NUL, pad@25,
   sideId@26, rank@28, pad@30, score@32).
3. `Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` (live,
   P178 path) — a win-by-elimination fires the broadcast alongside GAME_OVER to
   the surviving peer; the departed player is **not** listed (the deliberate
   simplification), so the survivor's message carries exactly its own entry,
   with its seeded non-zero rank/score.
4. `Conquest_win_lists_every_connected_player_including_losing_side` (live,
   P191 path) — a conquest win where **both** players remain connected broadcasts
   to every peer a list of **every** connected player — including the LOSING-side
   player with their own correct `sideId` (not the winner's roster) — with each
   player's seeded rank/score carried intact.
5. `Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another`
   (live, multi-mission) — a conquest win in mission A reaches only mission A's
   peers; the mission-B peer hears no `GAME_OVER_PLAYERS` and mission B stays
   Started (the established multi-mission proof pattern).

All three live tests were **proven discriminating** (must-fail-if-removed):
temporarily commenting out the `BroadcastGameOverPlayers` call in
`DeclareGameOver` turned exactly those 3 tests red (the 2 codec tests stayed
green); restoring the call turned them green again.

## Verification

- `/home/domingo/.dotnet/dotnet build` clean (0 warnings, 0 errors) across the
  solution.
- New tests pass in isolation: 5/5.
- Full-suite baseline moved **2960 → 2965** (P193 baseline 2960 + the 5 above).
- 4 full-suite runs total: run 1 interrupted by two live-network flakes (the
  documented machine-state `LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire`
  and a companion live-network flake), then **3 consecutive fully-clean runs at
  2965** (runs 2, 3, 4 — 0 failures each). No non-documented failure was
  observed in any run, and the flagged flakes never touch a P194 symbol (the
  same environmental profile the skill documents).

## Divergences from the prompt's assumptions (all verified against live source)

None of the prompt's premises were contradicted by the live source this task.
The task's two allowed "state your choice" branches were both taken and are
documented above: **one message, no chunking** (chunking judged unneeded at this
port's scale) and **include every connected player** (no time-played counter
exists, so the native "has actually played" filter collapses to "is connected,"
the task's own permitted fallback).
