# P523 — readying up with Y also targets yourself (key conflict), and target brackets/labels shake in chase view (drawn from raw 20 Hz server pose)

## Why this task exists

**Domingo's live playtest (2026-10-03):** "I see my own name on the targeting HUD
and it's very shaky, looks ugly … that's on the external view … chase."
The evidence log is `~/monogame-client-run-selftarget.log`
(`[monogame] target: self (domingo1 #8107)` right after connect).

**Bug 1: `Y` is bound twice.** `KeyBindingTable.cs:98` binds `Y` to
"Target self [TK_TargetSelf]" (P417; `Game1.Targeting.cs:683-706`,
`SelectSelfTarget`, native `WinTrek.cpp:10440-10443`). `KeyBindingTable.cs:161`
binds `Y` to "Toggle your ready (PLAYER_READY)" (P282, `UpdateReadyCheck`).
Native readies up with a UI button, so it has no conflict. Readying up
leaves the Y press live as flight starts, which selects your own ship as
the target.
- **Fix it the native-faithful way:** check native's real default key
  for `TK_TargetSelf` in `Artwork/defaultinputmap.mdl` (P445 decoded the
  TK_* map; reuse that method) and bind it there if it differs.
- Make pre-flight keys (ready, side-select) **edge-gated so they can't leak
  into flight** on the state transition.
- Keep `;` = TargetNothing.

**Bug 2: the shaky bracket and label.** In chase/external view the own ship
is drawn from P508's **local sim**, at frame rate. The target bracket and
label come from `ComputeTargetSelectionBracket` → `ResolveTargetPosition`
(`Game1.Camera.cs:~118-221`), which reads the entity's **raw server pose**
(20 Hz `ServerShipUpdate`). So the label jitters around the smoothly drawn
ship. Remote ships are drawn through P448b's dead reckoner, so their
brackets likely jitter the same way. Verify this.
- **Fix:** brackets, labels, the lock bracket and any other HUD element
  anchored to a ship must use the **same pose the renderer drew this
  frame**: the own-ship local sim for self, and the P448b dead-reckoned
  pose for remotes. Use one shared "render pose" accessor, not a third
  path.

## Scope

1. Fix Bug 1: native key for TK_TargetSelf, plus pre-flight keys edge-gated
   at the state transition.
2. Fix Bug 2: HUD ship anchors use the render pose, for self and remotes.
3. Check whether targeting **self** shows a self bracket or label in native
   at all. If native suppresses it in chase view, match native.

## Explicitly out of scope

- The death latch (P521) and pregame flight (P522).
- Lock-on mechanics (P230/P428).

## Verification

- Failing-first tests:
  - readying up (Y) then entering flight doesn't select self;
  - the bracket anchor equals the render pose (self via a stubbed local sim,
    remote via the dead reckoner) each frame;
  - the jitter metric (frame-to-frame anchor delta) drops versus the
    raw-pose anchor in a scripted run.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P523_SELF_TARGET_AND_BRACKET_JITTER.md`.
