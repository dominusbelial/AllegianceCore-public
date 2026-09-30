# P438 — Client drops JOIN_SIDE(125), so a side switch leaves friend/enemy colour stale

Date: 2026-09-29
Branch: `master` (landed directly, per the AllegianceCore convention — Domingo pushes)

## 1. The exact real cause (confirmed)

Carried from P436's live testing: a **friendly** ship's target-selection box
rendered in the P376 **enemy** salmon-orange (`TargetSelectionStyle.EnemySelected`,
`Rendering/TargetSelectionStyle.cs`) instead of blue. P436 ruled out its own
changes. Direct source read traced the true cause:

- The classification in `Game1.Camera.cs:146` is
  `isEnemy = !(_session.World.MySideId is { } mySide && targetShip.SideId == mySide)`.
  It goes wrong whenever `ShipEntity.SideId` or `MySideId` is **stale**.
- The client has only one-shot sources for side:
  - `MySideId`: only from the own-ship dummy EXPORT (`ShipId == -1`),
    `ClientWorld.cs:1032-1047`.
  - A teammate's `ShipEntity.SideId`: only from the teammate EXPORT, latched
    once per (peer, ship) via `_teammateExportSent`
    (`Server.Broadcast.cs:522-539`).
  - A cross-side ship: the P369 cross-side EXPORT plus its slow self-heal
    (`Server.Broadcast.cs:~950-960`).
- **The server DOES tell everyone when a side changes, and the client threw it
  away.** `TryJoinSide` sets `ship.SideId = newSideId`
  (`Server.Lobby.cs:1705`) and calls `BroadcastJoinSide`, which sends
  `JOIN_SIDE(125)` to the whole mission (`Server.Lobby.cs:574-579`).
  `Allegiance.ClientCore` had **zero** references to `JoinSide`;
  `ConnectionSession` had no `case JoinSide.Fmid` (contrast
  `case SetTeamLeader.Fmid`, `ConnectionSession.cs:823-828` → `ClientWorld.Apply
  (SetTeamLeader)`, `ClientWorld.cs:1797`). Only the test `Bot` decoded it
  (`Bot.cs:1847-1848`).
- **Native handles it.** `clintlib/appmsg.cpp:2909-2915`, `case FM_S_JOIN_SIDE`
  → `AddPlayerToSide`, which does `pPlayerInfo->SetSideID(sideID)` and
  `pship->SetSide(pside)` (`clintlib/clintlib.cpp:4101-4125`).

**Empirically confirmed this session** by a two-client in-process loopback test
(the real server `TryJoinSide` path, real TCP, real `ConnectionSession` peers).
Against the unfixed code, the step-2 test FAILED exactly as predicted
(`A's view of B must follow the JOIN_SIDE homecoming (got 1, expected 0)`) and
the step-3 own-ship test FAILED (`MySideId (0) and own ShipEntity.SideId (0)
must become 1`). After the fix both pass.

## 2. Reproduction nuance — why the naive single switch is NOT the bug you see

The brief's literal scenario is: A on side 0, B seated on side 1, A receives B's
EXPORT, then B switches to side 0. I wrote exactly that and it **passed even on
unfixed code**. Reason: on a ship's FIRST same-side join, the server's one-shot
teammate EXPORT (`_teammateExportSent`, `Server.Broadcast.cs:522`) has not fired
for that (A,B) pair yet, so it fires fresh and re-stamps `SideId = 0` — masking
the staleness.

The staleness only bites once **both** EXPORT one-shot latches for the (A,B)
pair are spent, which happens in the real world as: B is A's same-side teammate
(spends the teammate latch) → B moves to the enemy side and A latches B as
enemy (spends the cross-side latch) → B comes home. Now neither export path
re-fires, so A's cached `Ships[B].SideId` can only be corrected by the
`JOIN_SIDE(125)` the server broadcasts on `TryJoinSide` — which the unfixed
client dropped. A keeps B's stale *enemy* side and paints a genuinely friendly
ship orange.

So the deterministic step-2 reproduction is the **two-phase "homecoming"**
test (`ClientJoinSideLiveTests.Teammate_side_change_updates_viewer_after_cross_side_stint`):
teammate → cross-side stint → home via the real `TryJoinSide`. It fails on
unfixed code (A stays at stale enemy side 1) and passes after the fix.

### Is the `MySideId == null ⇒ enemy` fallback a second contributing path?

No — and it cannot have produced the observed single-ship symptom. The fallback
`!(MySideId is { } mySide && …)` yields `isEnemy == true` for **every** ship at
once whenever `MySideId` is null (nothing would ever be blue). The observed bug
was one friendly ship orange among others rendering blue, which requires
`MySideId` to be *set* (so the rest resolve friendly) and only that ship's
`SideId` stale — the `JOIN_SIDE` gap. That said, `MySideId` itself is equally
stale-prone (it only updates from the one-shot dummy EXPORT), so a **local**
side switch would leave the whole classification inverted; the step-3 handler
fixes that too by updating `MySideId` from `JOIN_SIDE` when the local ship is
named. The fallback is left as-is per the brief (out of scope), and is not the
cause of P436's report.

## 3. The fix

Mirrors the ship-side half of native `AddPlayerToSide` (not the
`OnQuitSide`/`OnJoinSide` UI side effects, which stay out of scope):

1. **`ConnectionSession.cs`** — added `case JoinSide.Fmid:` to the dispatch,
   next to `SetTeamLeader`, same decode → `_world.Apply(...)` shape:
   `_world.Apply(JoinSide.Decode(m));`.

2. **`ClientWorld.cs`** —
   - added `case JoinSide joinSide: Apply(joinSide); break;` to the
     `Apply(IFedMessage)` switch;
   - added `Apply(JoinSide join)`:
     ```
     if (_ships.TryGetValue(join.ShipId, out var e))
         e.SideId = join.SideId;              // ship-side (native pship->SetSide)
     if (join.ShipId == MyShipId)
         MySideId = join.SideId;              // local player's own side
     ```
   It deliberately does **not** flip `ShipEntity.HasRealIdentity`: a
   `JOIN_SIDE` carries no hull, so it must not fabricate identity (P373).

## 4. Handling decisions

### Unknown ship — ignore, not buffer

A `JOIN_SIDE` for a ship the client has no entity for yet is ignored (no phantom
entity is minted). `JOIN_SIDE` carries only `SideId`/`ShipId` — no hull, no
character, no identity. Creating an entity from it would produce an
identity-less ship that violates P373's `HasRealIdentity` invariant (a
not-yet-identified ship must not be announced or classified), and its `SideId`
would be meaningless without a hull. The ship's EXPORT — the authoritative
source of `SideId` + hull — arrives on (re)contact and stamps the real side
anyway, so buffering buys nothing: whatever order the messages arrive in, the
EXPORT wins, which is correct. The one benign exception handled explicitly is
the local ship (see step 3): a `JOIN_SIDE` naming `MyShipId` updates `MySideId`
even if the own entity has not been created yet, since `MySideId` is independent
of the entity table and has its own one-shot source.

### SIDE_TEAMLOBBY (-2)

The lobby side is `Side.ArtifactSentinelFlag` (= -2, native `igc.h:514`). If a
`JOIN_SIDE` ever carries it, storing it on the entity is harmless: `isEnemy`
compares `SideId == MySideId`, and `MySideId` is always a real faction side
(never -2), so -2 can never equal it. A ship parked in the team lobby simply
resolves as "not on my side" (rendered as a non-friend) — it is never aliased to
a real side, friendly or enemy. No special-case code was needed; the unit test
`JoinSide_with_SIDE_TEAMLOBBY_does_not_alias_to_a_real_side` pins the behavior.

## 5. Verification — real proof

### 5a. Whole-solution build

`/home/domingo/.dotnet/dotnet build`:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 5b. Pre-fix failing loopback evidence (run against unfixed code)

Ran the new `ClientJoinSideLiveTests` **before** any fix; two failed exactly as
the root-cause trace predicted:

```
ClientJoinSideLiveTests.Teammate_side_change_updates_viewer_after_cross_side_stint [FAIL]
  A's view of B must follow the JOIN_SIDE homecoming (got 1, expected 0);
  without the P438 JOIN_SIDE handler A keeps B's stale enemy side and paints a friendly ship orange
ClientJoinSideLiveTests.Own_ship_join_side_updates_MySideId_and_own_entity [FAIL]
  own-ship JOIN_SIDE must update MySideId (0) and the own ShipEntity.SideId (0) to 1
Failed! - Failed: 2, Passed: 1
```

(The third, single-switch scenario passed unfixed — the §2 convergence point.)

### 5c. Post-fix live proof (real in-process server + real ConnectionSession wire)

`tests/Allegiance.ClientCore.Tests/ClientJoinSideLiveTests.cs` now passes 2/2
live tests repeatedly (3× consecutive below, alongside the unit tests):

```
P438-HOME: A 8098 sees teammate B 8099 side 0 after homecoming (fix: JOIN_SIDE honoured; was stale-enemy 1)
P438-OWN:  local ship 8098 side 0 -> 1; MySideId=1, ShipEntity.SideId=1
```

### 5d. Unit tests

`tests/Allegiance.ClientCore.Tests/ClientWorldSideIdTests.cs` — added four pure
(no-network) tests pinning `ClientWorld.Apply(JoinSide)`:

- `JoinSide_updates_a_known_teammate_side` — a known ship's `SideId` follows a
  `JOIN_SIDE`; `MySideId` is untouched by a teammate's.
- `JoinSide_naming_own_ship_updates_MySideId_and_own_entity` — step 3.
- `JoinSide_for_unknown_ship_creates_no_phantom` — no entity fabricated, no
  side/MySideId disturbance.
- `JoinSide_with_SIDE_TEAMLOBBY_does_not_alias_to_a_real_side` — -2 stored
  harmlessly, `HasRealIdentity` not flipped.

### 5e. Test suite (3 consecutive full-suite runs)

Whole solution via `/home/domingo/.dotnet/dotnet test --no-build`, 3 consecutive
runs:

| Project | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| `Allegiance.Lobby.Tests` | 12/12 pass | 12/12 pass | 12/12 pass |
| `Allegiance.ClientCore.MonoGame.Tests` | 868/868 pass | 868/868 pass | 868/868 pass |
| `Allegiance.ClientCore.Tests` | 299 pass, 2 flake | 298 pass, 3 flake | 298 pass, 3 flake |
| `Allegiance.Core.Tests` | 3517 pass, 5 flake | 3519 pass, 3 flake | 3520 pass, 2 flake |

The **P438 tests themselves** (`ClientJoinSideLiveTests` ×2 + the four new
`ClientWorldSideIdTests` ×4) passed in **every** full-suite run and never appear
among the failures. All failures are the **pre-existing documented live-wire
timing flakes**, in test files unrelated to the changed assembly:

- Run 1 — `ClientCore.Tests`: `ShipDeleteGoneLiveTests.DockOut_…`,
  `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`;
  `Core.Tests`: `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129…`,
  `LoadoutChangeLiveTests.Boarding_and_leaving…`,
  `LiveWinConditionTests.Multi_mission_disconnect…`,
  `LiveTurretGunnerBindingTests.Board_binds_gunner…`,
  `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity`.
- Run 2 — `ClientCore.Tests`: `ShipDeleteGoneLiveTests…`,
  `JoinFlowLiveTests…`, `FullSessionKillIntegrationTests.FullSession_ThreePeers…`;
  `Core.Tests`: `LiveWinConditionTests…`, `LiveTurretGunnerBindingTests…`,
  `LiveGainFlagTests.Flag_carrier_at_hostile_station…`.
- Run 3 — `ClientCore.Tests`: `ThreePeerSeededSessionTests.SeedThreePeers…`,
  `ShipDeleteGoneLiveTests…`, `JoinFlowLiveTests…`;
  `Core.Tests`: `WarpBombTests.Live_whole_mission_warp_bomb…`,
  `LiveTurretGunnerBindingTests…`.

These vary run to run (different tests each time) and pass on rerun — the
characteristic signature of the documented timing-sensitive live-wire tests,
not a deterministic regression. The changed source (`Allegiance.ClientCore`)
is referenced only by `ClientCore.Tests`, and none of the flaked tests exercise
the new `JOIN_SIDE` handler.


## 6. Out of scope (untouched, per brief)

- Server `_teammateExportSent` / `_crossSideExportSent` latches and re-sending
  EXPORT on side change. The port now learns side changes from `JOIN_SIDE`,
  like native.
- Native `OnQuitSide`/`OnJoinSide` UI side effects (money reset, requests list,
  `ResetShipStatus`) — separate follow-up if needed.
- `TargetSelectionStyle` colours and the `MySideId == null ⇒ enemy` fallback in
  `Game1.Camera.cs:146` (analysis in §2 — not the P436 cause).
- The other carried open items: vizfight's `[0,0,0]` position fallback
  (`SpectatorFrame.cs:190`) and the lack of mission rotation after `Over`.

## 7. Files changed

- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — `case JoinSide.Fmid`
  dispatch (the fix, part 1).
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `Apply(JoinSide)` +
  dispatch case (the fix, part 2).
- `tests/Allegiance.ClientCore.Tests/ClientJoinSideLiveTests.cs` — new two-client
  live loopback proofs (homecoming step-2 repro + own-ship step-3).
- `tests/Allegiance.ClientCore.Tests/ClientWorldSideIdTests.cs` — four pure
  `Apply(JoinSide)` unit tests.

Committed on `master` (not pushed — Domingo pushes).
