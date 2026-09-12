# TEAMDIRECTOR_LIVE — live-VM confirmation that P52 works, and a real surprise

Date: 2026-09-02. Claude Code's own live pass, matching the standing
P4X→live-pass pattern. Real `Wopr.exe` client (Windows VM,
`192.168.0.240`) built from `Allegiance` commit `7cd20c43` (P52's own
commit — committed **after** this pass confirmed it, per P52's own
instruction that the live pass decides whether to commit).

## Method

Synced the updated `TeamDirector.cs` into the VM's existing (already
toolset-patched from prior passes) tree, managed-only rebuild
(`FZDebug-NoYellowComm`/x86/v145) — no native libs changed, only the
managed `Wopr.csproj`. The VM's own scratch `MultiExploreTest.cs` (not
git-tracked, a standing multi-bot test harness left over from P45/46) was
temporarily edited so bot index 0 passes `isCommander=true` while the
other two stay pilots, giving one commander + two `ScoutExploreMap` bots
in the same run — both to test P52's new branch and to confirm it doesn't
regress the existing pilot path running alongside it.

## Result — CONFIRMED, cleaner than predicted

```
CommanderResearchAndExpand explorer0@BOT: Starting strategy: CommanderResearchAndExpand
CommanderResearchAndExpand explorer0@BOT: Buyable Items
CommanderResearchAndExpand explorer0@BOT: Ships
CommanderResearchAndExpand explorer0@BOT: 	explorer0@BOT
CommanderResearchAndExpand explorer0@BOT: 	explorer0@BOT
CommanderResearchAndExpand explorer0@BOT: Stations
CommanderResearchAndExpand explorer0@BOT: 	Garrison - Garrison
...
Strategy destroyed for , CommanderResearchAndExpand
```

- The commander bot attached `CommanderResearchAndExpand` (P52's new
  branch fired correctly), `Start()` ran to completion — **no exception**,
  no `"attach/start threw"` log line, no crash. Matches hermes's own
  prediction from `TEAMDIRECTOR_P52.md` (~80% confidence, "Start() does
  NOT throw").
- **One real correction to hermes's prediction**: it expected
  `GetStations()` to return an **empty** list (reasoning: "AllegianceCore
  has no stations, no docking"). It actually returned a **real station**,
  `"Garrison"`. Reconciled: `AllegianceCore` has no *docking* (no ship can
  actually dock/undock), but it does already export static station
  *objects* to clients — the server's own boot log confirms this plainly
  (`[boot] game 'Combat Demo' ready: ... 2 stations, 0 asteroids`), and a
  real wire codec already exists for it
  (`src/Allegiance.Core/Wire/ExportStation.cs`). So `Side.GetStations()`
  on the native client genuinely has real station data to enumerate —
  "no docking" and "no station awareness" are two different, previously
  conflated claims. This is **good news for whatever docking-related task
  comes after this arc**: station existence/identity/position plumbing is
  already there; only the dock/undock *interaction* is missing.
- `Ships` listed `explorer0@BOT` twice — not investigated further here
  (cosmetic, doesn't affect anything checked in this pass; worth a note
  for whoever next reads `CommanderResearchAndExpand`'s own `GetShips()`
  call site if it matters to future work).
- **No regression**: the two `ScoutExploreMap` pilots running alongside
  the commander in the same process behaved exactly as every prior
  exploration demo has (`otherFriendlyScoutShips`/`is unexplored` decision
  log lines, normal operation).
- Strategy completed and was cleanly destroyed at the run's end
  (`"Strategy destroyed for , CommanderResearchAndExpand"`) when the test
  window closed — no hang, no orphaned state.
- Server-side: no crash, no exceptions in the vizfight backend log across
  the whole run; all three peers connected and disconnected cleanly at
  teardown.

## Disposition

Committed: `Allegiance` `7cd20c43`. Kept **local only** (not pushed),
matching this project's standing practice for native-repo commits.

## What this unblocks, honestly

`CommanderResearchAndExpand.Start()` only runs its top-level
listing/logging — this pass did **not** exercise its deeper logic (tech
queueing, station placement, mining dispatch, the ~1600 remaining lines).
Whether those paths are safe against `AllegianceCore`'s current gaps is
unknown and untested; this pass only proves the strategy is *attachable*
and its initial read-only enumeration is *safe*. The next real step for
the economy arc (docking, most likely, now confirmed to have partial
plumbing already) should treat this as "the AI can now be woken up," not
"the AI now works."
