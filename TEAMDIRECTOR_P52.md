# P52 — Let the direct-connect bot harness attach CommanderResearchAndExpand, not just ScoutExploreMap

## Status: DIAGNOSIS-QUALITY, UNCOMMITTED — pending Claude Code's live-VM pass

This change cannot be built or tested in this environment (no VM, no reachable
build/test harness). It is a small, targeted fix to `TeamDirector.cs` that
mirrors an existing, already-correct two-way routing decision elsewhere in the
same file. Per the established precedent (P50's native fix to
`ScoutExploreMap.cs` was likewise scoped, delegated, and left uncommitted for
live judgment), `TeamDirector.cs` is deliberately **left uncommitted** here. The
live pass decides whether to commit.

---

## What was changed

File: `src/Wopr/Wopr/TeamDirector.cs` (note: the file is at `Wopr/TeamDirector.cs`,
not under `Strategies/`).

In the direct-connect ship-assignment block (`if (gotShip)`, ~lines 472-487),
the previously unconditional

    ChangeStrategy(clientConnection, StrategyID.ScoutExploreMap, ...);

is now selected by a local `botStrategy` variable:

    StrategyID botStrategy = createPlayerParams.isCommander
        ? StrategyID.CommanderResearchAndExpand
        : StrategyID.ScoutExploreMap;

passed to `ChangeStrategy(...)`, with the log line above it naming whichever
strategy was chosen (both the attach log and the catch-handler log use
`botStrategy`, so they are not hardcoded to ScoutExploreMap anymore). The
existing try/catch around `ChangeStrategy` is preserved untouched — this repo's
own comment explains why: against the headless-server milestone, `Start()` may
throw because in-world state isn't fully hydrated, and that is expected /
tolerated, not a bug to fix here.

That's the entirety of the change: `git diff` shows 1 file, 10 insertions,
3 deletions, nothing else touched. The pre-existing uncommitted
`ScoutExploreMap.cs` (P50) and the untracked `TRANSPORT_P*.md`/`BOOT_PATH.md`
report artifacts remain exactly as they were — untouched by this task.

---

## Why the routing parity matters (both trigger points now agree)

The lobby-mediated path already routed commander bots correctly, but only as a
reaction to `ConnectToGame` completing — and direct-connect explicitly never
attaches `ConnectToGame` (`TeamDirector.cs:404-406`:
`if (directConnect == false) ChangeStrategy(..., ConnectToGame, ...)`), so
`Strategy_OnStrategyComplete`'s `ConnectToGame`-completion case was dead on
this path. Hence every bot this project has run (exploration demos, P51's live
pass) was `ScoutExploreMap` regardless of the caller's `isCommander`. The new
branch makes the two paths agree: for the same `isCommander` value, both decide
identically.

Existing path (`Strategy_OnStrategyComplete`, `TeamDirector.cs:541-568`):
- `isCommander == true` → on `ConnectToGame` completion → `CommanderResearchAndExpand` (line 546)
- else (pilot) → on `ConnectToGame` completion → `ScoutExploreMap` (line 560)

New path (direct connect, `TeamDirector.cs:~480`):
- `isCommander == true` → `CommanderResearchAndExpand`
- else → `ScoutExploreMap`

Same two-way choice, same values, reached from a different trigger (ship
assigned vs. strategy complete). Identical routing. No other consumers
changed.

---

## Honest prediction for `Start()` on this server (the load-bearing bit)

Read: `CommanderResearchAndExpand.cs`, `Start()`, lines 705-721.

Sequence of first statements:

    709  var buyableItems = ClientConnection.GetSide().GetBuckets()
                .Where(p => ClientConnection.GetSide().CanBuy(p) == true);
    711  Log("Buyable Items");
    712-713  foreach (var buyableItem in buyableItems) Log($"\t{...}");
    715  Log("Ships");
    716-717  foreach (var ship in ClientConnection.GetSide().GetShips()) Log($"\t{ship.GetName()}");
    719  Log("Stations");
    720-721  foreach (var station in ClientConnection.GetSide().GetStations())
              Log($"\t{station.GetName()} - {station.GetBaseStationType().GetName()}");

The **first** call that depends on server-provided data is
`ClientConnection.GetSide().GetBuckets()` at **line 709**.

What happens when that data is absent:

- `GetSide()` (`ClientConnection.h:174`) returns `nullptr` iff the underlying
  native client is null; otherwise wraps `m_nativeClient->GetSide()`. The
  direct-connect loop only reaches `gotShip==true` if a ship with a real
  `GetBaseHullType() != null` exists — a ship implies an assigned side, so
  `GetSide()` is non-null here. No null, no NRE from this call.
- `GetBuckets()` (`igcWrapper.cpp:4046-4056`): builds a managed
  `List<IbucketIGCWrapper^>` walking the native side's bucket list from
  `.last()` backwards. An empty bucket list (nothing researched/bought on this
  server) yields an **empty managed list — no throw**. Note it does NOT guard
  `m_instance->GetBuckets()` for null like `ConvertSList` does, but an IGC
  `Side` always owns its `_Buckets` member list, so this is safe in practice.
- `CanBuy` is only evaluated inside the `.Where(...)` predicate, i.e. only for
  elements of the (empty) bucket list — never invoked.
- `GetShips()`/`GetStations()` go through `ConvertSList`
  (`igcWrapper.h:13-24`), which null-guards the native list and returns an
  **empty managed list** for an empty/absent side ship/station list — no throw.
  This server has no stations (`Server.cs:414`: `StationId = -1 // NA — no
  docking in this server`; `:1000-1001`: `ships are born flying in a sector`),
  so `GetStations()` is empty; and its simplification stance gives
  `Unknown=false/Detected=true` always, with no fleet composition to speak of.

Therefore my reading of `Start()` is that it does **NOT** throw on empty
collections — the three `Log(...)` header lines (`"Buyable Items"`, `"Ships"`,
`"Stations"`, at 711/715/719) will print, each followed by zero entries, and
`Start()` returns normally. The commander bot attaches and then **idles doing
nothing**, because every `foreach` in `Start()` iterates an empty collection and
there is no message-drive doing real work in the brief `Start()` body. The
result is the acceptable, expected outcome: attached-but-inert commander bot.

Confidence this is right: **moderate-high (~80%)** that no exception fires from
these three list traversals specifically. Residual risk: some unforeseen
NullReferenceException inside the deep IGC bridge (beyond the three documented
calls) — but the bridge path from `GetBuckets`/`GetShips`/`GetStations` to
managed lists is straightforward. If ANY exception does occur, the existing
try/catch at `TeamDirector.cs:475-486` catches it, logs
`"<StrategyID> attach/start threw: <msg>"`, and the outer
`SendAndReceiveUpdate` pump loop continues — the bot thread survives either way
(see "what Claude Code should verify" #2 below).

---

## What Claude Code's live pass should check

On the Windows VM, build this and run a direct-connect bot with
`isCommander=true` against AllegianceCore, then inspect
`c:\1\Logs\<playerName>_teamdirector.txt` and `_exceptions.txt`:

1. **Routing/log**: the teamdirector log records
   `"Ship assigned, attaching CommanderResearchAndExpand strategy."` (i.e. the
   code path `TeamDirector.cs:480` chose `StrategyID.CommanderResearchAndExpand`,
   not `ScoutExploreMap`) for a bot created with `isCommander=true`, and the
   `ChangeStrategy` call actually dispatched the new strategy.

2. **Does Start() throw? Caught?**: 
   - If `Start()` throws, confirm the try/catch at `TeamDirector.cs:475-486`
     caught it (log shows `"CommanderResearchAndExpand attach/start threw: ..."`),
     the bot thread survived, and the outer `SendAndReceiveUpdate` pump
     (`while (_cancellationTokenSource.IsCancellationRequested == false)`,
     `TeamDirector.cs:496-518`) keeps running — bot not dead.
   - If it does NOT throw (my prediction), confirm the strategy attached and
     proceeds: `Start()` logs `"Buyable Items"` / `"Ships"` / `"Stations"` each
     with zero entries and returns, leaving the bot inert-but-alive.

3. **What `Start()` logs before any failure**: the three header logs at
   `CommanderResearchAndExpand.cs:711, 715, 719` run *before* any
   station/bucket-dependent call and should succeed regardless — verify they
   appear in the commander's own strategy log if one is written, or in the
   teamdirector log stream. Even in the throwing case, these precede the throw
   and should be visible.

Deliberately NOT in scope for this change: making `CommanderResearchAndExpand`
function (station/bucket data doesn't exist server-side yet; that's a separate,
much larger arc), touching the lobby path / `Strategy_OnStrategyComplete`, or
changing how callers thread `isCommander` into `CreatePlayer`. All untouched.

---

## Session stats

Logged via `python3 ~/Development/allegiance-tools/session_stats.py
20260902_084505_5c5203`:

    20260902_084505_5c5203  start=08:45:13  dur=STILL RUNNING / no clean end
    msgs=42 tool_calls=23 api_calls=19  tokens_in=596,602 tokens_out=6,585

(captured mid-run; "STILL RUNNING" is expected — this session had not closed
when the log was taken.)
