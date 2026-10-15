# P415 — friend/foe/hull-class-filtered nearest-target keys (the whole native targeting-command family)

## Why this task exists

Same key-binding survey as P413/P414. Native's real bindable-command list
(`~/Development/Allegiance/src/WinTrek/trekinput.cpp:1531-1600`) has a
large, real "Targeting" family this port never ported at all: nearest
target filtered by enemy/friendly/allied-base/friendly-base, and further
by hull role (fighter/bomber/miner/constructor/lifepod/major-base).
Domingo's original ask was specifically "nearest enemy"; investigating
turned up the full real family and confirmed the port already has the
data needed to build a proper general version instead of one narrow key.

## Confirmed native commands (the real, complete set — trekinput.cpp:1531-1600)

- **Generic** (already exist as native commands, some already covered by
  this port's kind-based `T`/`G` keys, some not): `TK_Target`,
  `TK_TargetNothing`, `TK_TargetSelf`, `TK_TargetNearest`,
  `TK_TargetPrev`, `TK_TargetCommand`, `TK_TargetCenter`.
- **Flag/Artifact/Aleph nearest** (this port has real `Side.Flags`/
  `Side.Artifacts` per this session's earlier research — confirm current
  state before assuming): `TargetFlagNearest/Prev`,
  `TargetArtifactNearest/Prev`, `TargetWarpNearest/Prev`.
- **Enemy family**: `TargetHostile` ("thing that's done the most damage
  to me recently"), `TargetEnemy`, `TargetEnemyNearest/Prev`,
  `TargetEnemyBase(Nearest/Prev)`, `TargetEnemyMajorBase(Nearest/Prev)`,
  and per-hull-role: `TargetEnemyBomber/Fighter/Constructor/Miner`
  (`Nearest`/`Prev` each).
- **Friendly family**, structurally identical: `TargetFriendly(Nearest/
  Prev)`, `TargetFriendlyBase(Nearest/Prev)`,
  `TargetFriendlyMajorBase(Nearest/Prev)`, per-role
  `TargetFriendlyBomber/Fighter/Constructor/Miner/Lifepod`
  (`Nearest`/`Prev` each).
- **Allied-base family** (native comment "#Imago 8/1/09" — a later
  addition, alliance-aware): `TargetAlliedBase(Nearest/Prev)`.

## Confirmed this port already has what's needed to build this properly

- Hull-role ability bits are already decoded:
  `DataHullTypeIGC.HabmFighter` etc. (`Data/DataHullTypeIGC.cs`) — the
  same bitmask family native's role filters read from
  (`c_habmFighter`/`c_habmBuilder`/etc., `igc.h:726-741`). Confirm which
  of Bomber/Miner/Constructor/Lifepod/MajorBase also have real decoded
  bits already (some are cited in that file per this session's earlier
  reads — `c_habmLifepod`, `c_habmBuilder`); if any role bit isn't
  decoded yet, decode it faithfully rather than guessing a substitute.
- The client already tracks `MySideId` (`ClientCore/State/
  ClientWorld.cs:143`) and per-ship `SideId` is already in the wire data
  the client receives (ship export already carries side, used
  elsewhere for HUD/color).
- **Confirmed real gap**: `Game1.Targeting.cs`'s `CandidateObject`/
  `AutopilotTarget` records carry only `(Position, Kind, Id, Name)` —
  no `SideId`, no hull-role — so `CollectTargetCandidates` cannot
  currently filter by friend/foe/role at all. This is the actual
  missing piece, not a deeper architectural gap.

## Scope

1. Extend the candidate-gathering records with what's needed to filter:
   the owning side (for ships/stations) and hull-role bits (for ships).
   Reuse the same source data already flowing into
   `CollectTargetCandidates`'s existing `ships`/`stations` dictionaries —
   don't add a new data source.
2. Build a **generalized** filtered-nearest-target function parameterized
   by (relation: enemy/friendly/allied-base/self, optional role bitmask,
   optional station/major-base-only) rather than hand-writing ~30 near-
   identical methods — the native command list is a combinatorial
   product of relation × role, and this port's own convention elsewhere
   (see how deadzone/mount-ramp logic is shared across similar cases)
   favors one parameterized implementation over duplicated near-clones.
3. Wire the real, most valuable subset to actual keys (check
   `KeyBindingTable.cs` for free keys; a reasonable modifier scheme, e.g.
   plain key = enemy-nearest family, Shift+key = friendly-nearest
   family, matches how many other flight sims layer these) — at minimum
   cover: nearest enemy, nearest friendly, nearest enemy base, nearest
   friendly/allied base, cycle-prev for whichever you bind nearest for.
   Bind the per-role variants (fighter/bomber/miner/constructor/
   lifepod) too if a sane key scheme fits without overloading; if it
   doesn't cleanly fit, bind the relation-level ones and document
   plainly which role-specific variants were left unbound and why —
   don't force an unusable key layout just to hit 100% coverage.
4. Add every new binding to `KeyBindingTable.cs`'s help overlay.
5. **Allied-base note**: native's alliance system requires ≥3 active
   sides to form real alliances (`Server.cs:5453`, confirmed this
   session during the vizfight showcase work) — a 2-side test mission
   will never exercise `TargetAlliedBase` meaningfully; verify it with a
   3+-side test config, or clearly document that it's implemented but
   only live-testable in a 3-side game.

## Explicitly out of scope

- `TK_TargetCommand`/`TK_TargetCenter`/`TK_TargetSelf`/
  `TK_TargetNothing` — minor, standalone, low-value; fold in only if
  trivially cheap alongside the main work, otherwise leave for a future
  small batch.
- Any native command tied to a UI panel this port doesn't have
  (`TK_ConMode*` family) — genuinely no equivalent, don't build one.
- P368-P414 — unrelated, don't touch.

## Verification — real proof, not just build/test

- A test proving the generalized filter correctly separates enemy vs.
  friendly vs. self candidates by side, and correctly narrows by hull
  role when a role filter is given.
- A test proving "nearest" picks the closest matching candidate and
  "prev"/cycle steps through matching candidates only (not all
  candidates) — mirroring the existing `CycleTarget`'s wrap-around shape
  for the unfiltered case.
- **Live proof, required**: in a live 2+-side session, confirm nearest-
  enemy and nearest-friendly keys each select a real, correctly-sided
  ship; confirm nearest-enemy-base/nearest-friendly-base select real
  stations by side.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The feature, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P415_FRIEND_FOE_TARGETING_KEYS.md` in the repo root: the
   generalized filter's shape, exactly which relation/role combinations
   got real keys vs. were left unbound (and why), and the verification
   performed.
