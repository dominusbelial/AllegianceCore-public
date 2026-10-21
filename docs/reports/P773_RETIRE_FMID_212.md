# P773 — Retire the port-only OwnShipKillBonusUpdate(212)

Branch `pi/p773` (worktree `allegiance-pi-p773`), base `master` after HUD-20
(`33982d00`) and HUD-21b (`5356d172`). No commits made; changes left in the
working tree. Model: GLM-5.3 Flash (thinking high).

## Native rule (all cites opened and verified under `~/Development/Allegiance/src/`)

The own Kill Bonus readout is a **pure client-side derivation** from the own
ship's experience:

- `WinTrek/console.cpp:824-829` — every frame,
  `experience = trekClient.GetShip()->GetExperienceMultiplier();
  m_pnumberExperience->SetValue(100.0f * (experience - 1.0f));`.
  `trekClient.GetShip()` is the player's own ship; the value binds as
  `Experience` (`console.cpp:688`) and is drawn by `ExperienceImage`
  (`Artwork/dialog.mdl:2556-2567`, placed at `:2617`).
- `Igc/shipIGC.h:1812-1818` — `GetExperienceMultiplier() =
  1 + 0.5*m_experience/(m_experience + 4)`, so the readout is
  `50*e/(e+4)`. `m_experience` is a float (`shipIGC.h:2610`);
  `AddExperience` is `+= 1.0f` (`shipIGC.h:1827-1830`).
- The client's experience only ever comes from the wire's standard streams:
  seed `pship->SetExperience(pfmPlayerInfo->fExperience)`
  (`clintlib/appmsg.cpp:2280`; server fills it at `FedSrv/FedSrv.CPP:2084`);
  victim `SetExperience(0.0f)` on `bDeathCredit`
  (`appmsg.cpp:2766-2769`, an eject keeps it); killer `AddExperience()`
  only when sides differ and are not allied (`appmsg.cpp:2805-2811`).
- **No "streak" concept exists in native** — `grep -r streak src/` is empty.
  The raw `m_experience` is never drawn; the only client reads are
  `GetExperienceMultiplier()` at `console.cpp:826` (own) and `:837` (target).
  The `Streak` field of 212 was a port invention.
- `FM_S_SET_EXPERIENCE` is only a commented-out client handler
  (`appmsg.cpp:2918-2928`); it was **not** revived.

Step 0 confirmed HUD-20 is in the base: the client seed
(`ClientWorld.Apply(PlayerInfo)` → `e.Experience = info.FExperience`,
`ClientWorld.cs:3735`), the victim reset (`:4218`) and the killer +1
(`:4261`, cross-side non-allied gate) are all present. HUD-20 named the
field `ShipEntity.Experience` (float) and added
`TargetPaneModel.TargetExperience(e) = 50*e/(e+4)` (item 4(f)) in
`Allegiance.ClientCore.Game` — which `Allegiance.ClientCore` cannot
reference (Game references ClientCore, not vice versa), so the shared
formula helper lives in `Allegiance.Core` and both callers route through it.

## What was removed

- `src/Allegiance.Core/Wire/OwnShipKillBonusUpdate.cs` — the 10-byte codec
  (header + float `KillBonusPercent` + short `Streak`, fmid 212) deleted.
- Server:
  - the `Step("broadcastOwnShipKillBonus", …)` registration and its comment
    and the private `BroadcastOwnShipKillBonus()` method
    (`Server.Broadcast.cs`);
  - the pooled packer `PackKillBonus` (`Server.BroadcastBuffers.cs`);
  - the three test-seam lines replaying the step
    (`Server.BroadcastTestSeams.cs`);
  - the `CoalesceOwnKillBonus = 10` constant — **no other key renumbered**
    (`MissileLifetimeQueueTests` checks key 6 by value); a one-line comment
    marks key 10 retired (P773, fmid 212);
  - the `BroadcastOwnShipCloak` doc no longer names the Kill Bonus stream
    ("Sent every tick, always present — no change-tracking").
- Client: the `OwnShipKillBonusUpdate` dispatch case in
  `ConnectionSession.OnReceive` (a stray 212 now lands in
  `ConnectionSession.UnknownFmids`); `ClientWorld`'s `_killBonusPercent`,
  `_killBonusStreak`, `KillBonusStreak`, `Apply(OwnShipKillBonusUpdate)`,
  its Apply switch case, and the four reset lines in `Reset()` /
  `BeginNextMission()`.
- Bot: the `_ownShipKillBonusUpdates` field, the
  `OwnShipKillBonusUpdates`/`ResetOwnShipKillBonus` accessors and the
  dispatch case (`Bot.cs`).
- `HudFrame`: the `KillBonusStreak` parameter and its doc deleted; the
  `KillBonusPercent` doc rewritten (derived client-side, no stream, cites
  `console.cpp:824-828`, `appmsg.cpp:2280,2769,2811`); `Game1.Rendering.cs`
  drops the `KillBonusStreak:` argument and the 212 comment; the two
  visibility gates in `HudRenderer.cs` (command-summary layout reservation
  and summary line) are now `f.KillBonusPercent > 0f` — identical behaviour,
  because `50*e/(e+4) > 0` exactly when `e > 0`. Named `KillBonusStreak:`
  arguments fixed in `Hud11LayoutPerfTests`, `Hud12DataAndArtTests`,
  `Hud12bReviewFixTests` and in the proof tools `hud-gauge-proof`,
  `hud-polish-proof` (2 sites), `weapon-status-proof`; two more tools in the
  solution (`p629-proof`, `hud2-proof`) passed `HudFrame` positionally and
  had their argument lists re-shifted.
- Tests: `tests/Allegiance.Core.Tests/OwnShipKillBonusUpdateTests.cs`
  deleted (codec-only; its `FromShip` check duplicated `KillBonusTests`);
  `FedMsgDispatchCoverageTests` drops `(212, "OwnShipKillBonusUpdate")`,
  17→16 port-added, 116→115 total, doc comments fixed;
  `BroadcastSnapshotLoopbackTests` drops `OwnShipKillBonusUpdate.Fmid` from
  the stream-lane filter.
- `OwnShipCloakUpdate.cs`'s precedent list no longer cites "P298" as a live
  member: reworded to name the P773 retirement explicitly (chosen over plain
  deletion, to keep the history readable).

`git grep -n "OwnShipKillBonus\|KillBonusStreak\|CoalesceOwnKillBonus\|broadcastOwnShipKillBonus" -- src tests tools`
returns only the guard test's own pattern string (`tests/Allegiance.Core.Tests/P773Fmid212RetiredTests.cs`); the same grep over `src` alone returns nothing.

## What replaced it

- `Allegiance.Core/Sim/Ship.cs` — new static
  `Ship.KillBonusPercentFor(float experience) =>
  Math.Clamp(50f*experience/(experience+4f), 0f, 50f)`
  (comment cites `shipIGC.h:1812-1818` and `console.cpp:826-828`);
  `Ship.KillBonusPercent` now calls it. Allocation-free.
- `TargetPaneModel.TargetExperience` (HUD-20 4(f)) now delegates to the same
  helper — one formula, three call sites (server `Ship.KillBonusPercent`,
  client own readout, target pane).
- `ClientWorld.KillBonusPercent` is a derived read:
  `MyShipId` → `_ships[me].Experience` → `Ship.KillBonusPercentFor(...)`,
  `0f` when no own ship. Same `_ships` entity map the other own-ship reads
  use; `Reset`/`BeginNextMission` clear `_ships`, so the value returns to 0
  with no extra code; no per-frame allocation.
- Server experience untouched: `Ship._experience`, `AddKillBonus`,
  `ResetKillBonus`, `KillBonusPercent` and their callers
  (`Server.Combat.cs:1617,1689` region) all stay — the damage multiplier
  (`Ship.ExperienceMultiplier`) needs them, and HUD-20's PLAYERINFO seed
  reads `ship.KillBonus`.

## Tests (all new/rewritten; recorded failures on the unfixed code)

Raw failure logs: `~/allegiance-evidence/P773/unfixed-*.txt`.

- `tests/Allegiance.ClientCore.Tests/ClientWorldKillBonusTests.cs`
  (rewritten, pure `ClientWorld.Apply`, no 212; own ship seated via
  `SetCluster` + `PlayerInfo`):
  - `Own_kill_bonus_is_seeded_from_PLAYERINFO_experience` — experience 2 →
    ≈16.667. **Failed on unfixed code** (getter read `_killBonusPercent`,
    only 212 set it → 0).
  - `Own_kill_credit_on_an_enemy_raises_the_own_kill_bonus` — cross-side
    kill credit → 50·3/7 ≈ 21.429. **Failed on unfixed code.**
  - `Own_death_resets_and_eject_keeps_the_own_kill_bonus` — eject keeps the
    value, real death → 0. **Failed on unfixed code** at the first non-zero
    assertion.
  - `Fresh_world_has_no_kill_bonus` — passes on **both** (kept as guard).
  3/3 green ×3 runs after the fix.
- `tests/Allegiance.Core.Tests/P773Fmid212RetiredTests.cs` (new registry
  guard): no type in the wire assembly declares a `const ushort Fmid` 212;
  `typeof(Server)` has no `BroadcastOwnShipKillBonus`/`PackKillBonus`.
  **Both failed on unfixed code** (found `OwnShipKillBonusUpdate`; both
  methods present). 3/3 green ×3 after the fix.
- `tests/Allegiance.Core.Tests/KillBonusWireLiveTests.cs` (rewritten in
  place; file name kept for the parity row): real server + real
  `ConnectionSession` shooter (MissileLifetimeQueue logon/seat pattern,
  `SideSelectAutoSeatSeconds=30`, `SpawnInitialMinersAtBoot=false`),
  `BroadcastKillShip` by reflection, kills 1..4 on cross-side NPC ships,
  waiting for the client to know each victim and its side first.
  `server._testSendCapture` + `server._testSendCoalescingCapture` capture
  every outbound frame; each is unpacked with `FedPacket.Unpack` and its
  fmid recorded. Asserts `session.World.KillBonusPercent` reaches
  `50c/(c+4)` for c=1..4, then that **no captured frame carried fmid 212**
  and `session.UnknownFmids` does not contain 212.
  **Failed on unfixed code** exactly on the 212 assertion (captured frames
  contained 212 — the server sent it every tick); the percent assertions
  passed on both. 3/3 green ×3 after the fix (~1.0 s each).
- Kept-green as required: `HudModelTests` (incl. the
  `Signature_and_kill_bonus_surface_native_units…` case) unchanged and
  passing, 3× with the Hud11/12/12b set (98 tests) — passes on both.
- `FedMsgDispatchCoverageTests`, `BroadcastSnapshotLoopbackTests`,
  `KillBonusTests`, `KillBonusLiveTests`, `PipelineCostTests`,
  `MissileLifetimeQueueTests`, HUD-20's `Hud20PlayerInfoSeedTests`,
  `Hud20PlayerInfoWireTests`, `ClientWorldHud20Tests`,
  `ClientWorldExportTests`, `Hud20ArtGapsTests`, `TargetPaneModelTests`,
  `Hud18TargetPaneTests` — all 3× green (27-test combined Core.Tests run
  ×3 at 1 m 40 s; 20- and 8- and 39-test combined runs ×3).

## Measured gates

- Whole-solution `dotnet build AllegianceCore.slnx -c Debug`: **0 errors,
  0 warnings**.
- **AOT publish** (`dotnet publish src/Allegiance.Server/Allegiance.Server.csproj
  -c Release -r linux-x64 --self-contained -p:PublishAot=true -o .scratch/aot`):
  succeeded, **no IL2026/IL3050** diagnostics.
- Each new/affected class run **3× alone** (see list above): all green 3/3.
- **Fast tier** (`tools/run-tests.sh --tier fast`): **PASS** — 9801 tests,
  271.4 s, 0 failed (3 `NotExecuted` in MonoGame.Tests are tier-skipped
  live/perf cases, as on base).
- Showcase never-parks
  (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`)
  **5×**: passed 5/5, ~1 m 49–1 m 50 s each (no regression vs. the usual
  rate for this load-sensitive test).
- Out-of-solution proof tools each still build: `hud-gauge-proof`,
  `hud-polish-proof`, `weapon-status-proof` (0 errors each); the in-solution
  `p629-proof`/`hud2-proof` were fixed and build via the solution build.

## Matrix + docs

- `docs/parity/parity-matrix.csv` — only the `hud,Kill bonus` row (line 389)
  changed; `git diff --stat docs/parity` = 1 changed line; one-off CSV parse
  confirms all 666 rows have exactly 6 fields, LF endings, no CRLF. New
  `port_cite_or_reason` is the brief's field verbatim. **Deviation noted:**
  FONT-1 (`0fab95a5`, "parity matrix rows flipped") had turned this row from
  `partial` (its state at `e96b9943`) to `done`; per the brief ("port_status
  stays partial — the HUD font still substitutes for medBoldVerdana", and
  the honesty rules' parenthetical) this task restores `partial` while
  keeping the new reason text.
- No other row in `parity-matrix.csv`, `sim-rules-matrix.csv` or
  `fedmsg-wiring.csv` names 212/`OwnShipKillBonus`/experience (re-grepped
  after HUD-20; the apparent `212` substring hits are `appmsg.cpp:2129-2147`
  cites and SIM-0162/0212 IGC callback ids — unrelated). The
  `commander,…Kill experience multiplier and reset` row and the
  `Target kill bonus` row were not touched.
- `docs/KILLBONUS_ROLL_HUD_P298.md` — superseded-by line added at the top.
- No files under `docs/reports/*` (other than this report), `docs/audits/*`
  or `docs/reviews/` were touched.

## Known differences (documented, out of scope)

- Seed divergence: native starts a ship at `SetExperience(1.0f)`
  (`Igc/shipIGC.cpp:51-52`); the port starts at 0 by design
  (`Sim/Ship.cs:100-107`, SEED DIVERGENCE note). Unchanged.
- Native's pre-start reset on QUIT_SIDE (`FedSrv/fsmission.cpp:1140-1141`
  server, `clintlib/clintlib.cpp:3907-3908` client): the port server mutates
  experience only at the kill-credit/reset sites (`Server.Combat.cs`). Not
  ported here.
- Every other port-only fmid (appendix of the brief) untouched.

## Unfinished

Nothing. `.scratch/` was removed after the runs (raw logs preserved under
`~/allegiance-evidence/P773/`).
