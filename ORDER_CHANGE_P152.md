# ORDER_CHANGE(93) — P152 (Stage 6: command a docked NPC to launch)

Task: command a docked NPC ship to launch — the second half of native
`HandleOrderChange`'s docked branch, completing P148's "stay docked" slice with
its deferred "launch" arm. Branch `master` at `59ec0d8` (P151) going in.

The launch arm is native `src/FedSrv/FedSrv.CPP:7296-7302` (the `else` of the
docked `if` at 7290), and the direct session finding behind this task is that
launch needs **no `PickDefaultOrder`-equivalent call** in this port's current
scope — just the state transition.

---

## 1. Confirming the finding: launch needs no `PickDefaultOrder` call

Native's launch arm (`FedSrv.CPP:7296-7302`) does three things:

```cpp
pship->SetStayDocked(false);
pfsMission->GetSite()->SendChat(pship, CHAT_INDIVIDUAL, ...voAffirmativeSound, "Affirmative. Preparing to launch.");
pship->PickDefaultOrder(pship->GetStation()->GetCluster(), pship->GetStation()->GetPosition(), true);
```

The third is `PickDefaultOrder(..., bDocked=true)`. Reading native
`CshipIGC::PickDefaultOrder`'s dispatch confirms the premise:

- The dispatch is a `switch (m_pilotType)` over `c_ptMiner`/`c_ptBuilder`/
  `c_ptLayer`/`c_ptWingman` (`shipIGC.h:2225-2456`). This port has no
  `PilotType` enum — `Ship.IsNpc` is a single `bool` marker (P148) — and P151
  ported exactly one sub-block of that whole switch: the miner take-cover
  check at `shipIGC.h:2230-2246`.
- **That one ported sub-block is gated on `!bDocked`**: the condition is
  `if (GetFraction() < 1.0f && !bDocked)` (`shipIGC.h:2230`). Passing
  `bDocked=true` makes this false outright, so the take-cover block never
  fires for a just-launched ship. **This is the operative fact.**
- Every *other* native sub-block in that switch is either irrelevant without
  infrastructure this port doesn't have, or itself gated on something that
  doesn't fire here:
  - The mining/ore/cargo body (`shipIGC.h:2248-2352`) drives off
    `capacity`/`m_fOre` and `FindTarget(..., c_ttAsteroid, ...)` — no ore/mining
    infrastructure exists in this port (P151 explicitly excluded it).
  - "Arriving in a sector after player command" (`shipIGC.h:2252-2276`) gates on
    `m_commandIDs[c_cmdQueued] == c_cidJoin` — a command this port never
    produces.
  - The unload/land legs and the "don't go back to the previous sector" walk
    (`shipIGC.h:2324-2380`) consume `m_fOre`, `c_sabmUnload`/`c_sabmLand`, and
    `FindTarget` with `c_ttAnyCluster` (the cross-cluster warp-graph BFS P149
    explicitly deferred).
  - Builder/layer/wingman cases (`shipIGC.h:2388-2456`) are other pilot types,
    all out of the port's scope.

So, within this port's scope, the only `PickDefaultOrder` sub-block that could
possibly have any effect at launch is P151's take-cover — and `bDocked=true`
suppresses even that. **Conclusion (stated plainly, not silently omitted): the
launch arm ports `SetStayDocked(false)` plus the actual undock, and makes no
`PickDefaultOrder`-equivalent call, because such a call would be dead code
here.** This is commented at the call site (`Server.cs`) and documented here.

## 2. `SetStayDocked(false)` alone is not sufficient — the ship must actually leave

Mirroring the task's finding (and P148's own scope), the launch arm must not
just flip a flag: it must perform the real undock — clear `StationId`, push the
ship clear of `Sector.DockRadius`, and start the undock debounce. This is the
connection-agnostic core of `PerformUndock` (`Server.cs`).

### 2a. Why the real-peer `PerformUndock` is not directly reusable — and its peer-only part is genuinely inapplicable

`PerformUndock` (`Server.cs`) ends by re-sending `FM_S_SET_CLUSTER(7)`:

```csharp
var set = new SetCluster { ... };
SendPeer(from, FedPacket.Pack(new[] { set.Encode() }));
```

`from` is a `ConnectionId`; an NPC ship (P147's `Server.SpawnNpcShip`, no
`_peerShips` entry) has no connection. Tracing *why* that message exists
confirms it is not merely inconvenient but inapplicable: the doc comment above
`PerformUndock` (and the P148/LaunchUndock writeups) explain it exists to teach
the **undocking peer's own client-side simulation** that `GetStation() == NULL`
locally (`appmsg.cpp:1044-1049` — the only message that clears the native
client's model station/cluster state). An NPC has no owning peer's client to
tell. And **other real peers observing the NPC need no such message** — they
already learn the NPC's new flying state via the existing
`HeavyShipsUpdate`/`BroadcastShipsUpdate` machinery (P147 established exactly
this reasoning for why an NPC needs no `_peerShips`-keyed send).

### 2b. The extraction: `Server.UndockShipState(Ship, Sector)`

Rather than duplicating the state-mutation core, I factored it into a small
connection-agnostic helper (`Server.UndockShipState`, added directly before
`PerformUndock`):

1. resolve the station from `ship.StationId` in the given `Sector`'s station
   list (the same `live.Sector.Stations` lookup `PerformUndock` used),
2. clear `ship.StationId`,
3. `RepositionForLaunch(ship, station)` — confirmed this session to take a
   plain `Ship`, no connection, directly reusable as-is (`Server.cs`),
4. set `ship.LastLaunchTime = _simNow` — the P75 undock debounce that prevents
   `Sector.ResolveDockCollisions` (`Sector.cs:453`) from immediately re-docking
   a ship that hasn't physically moved yet.

`PerformUndock` was then refactored to call this helper and keep only its own
peer-specific trailing work (capturing the station id for its diagnostic, then
the `SET_CLUSTER` re-send + event feed). **This is a pure extraction: the
helper reproduces `PerformUndock`'s former state-mutation body verbatim (the
comments travel with it), so the real-peer path observes zero behavior change**
— verified by the existing real-peer undock live tests (`LaunchUndockTests`)
still passing unmodified, plus the full suite below.

## 3. The `HandleOrderChange` docked-branch extension

`HandleOrderChange`'s docked branch (`target.IsNpc && target.StationId is not
null`) previously had only P148's `commandID == c_cidDoNothing` arm. It now
branches on the decoded `commandID`:

- `commandID == c_cidDoNothing` → P148 stay-docked arm unchanged
  (`StayDocked = true`, no undock) — `FedSrv.CPP:7290-7294`. Untouched.
- otherwise (**launch**) → `StayDocked = false` (`FedSrv.CPP:7298`), then the
  shared `UndockShipState(target, target.Sector!)`. A docked NPC is always a
  placed ship (`Mission.PlaceShip` sets `Sector`; the dock seam only sets
  `StationId` afterward), so `Sector` is present; a defensive `else` clears
  `StationId` anyway rather than leave a contradictory
  `StayDocked=false` + `StationId!=null` state (unreachable in practice).

The return-to-base (flying, `StationId is null`) branch and the out-of-scope
no-op log (non-NPC / generic `SetCommand` fallback) are unchanged.

## 4. Explicit exclusions (out of scope, same exclusions as every prior stage)

- **Any `PickDefaultOrder`-equivalent call at launch** — per Section 1, it
  would be dead code in this port's scope (only the P151 take-cover sub-block is
  ported, and `bDocked=true` suppresses it). Skipped, and the decision stated
  plainly rather than omitted uncommented.
- **Mining/ore/economy logic, other pilot types, the carrier search,
  `c_ttAnyCluster`** — same exclusions as P148/P149/P150/P151.
- **Chat acknowledgments** (stretch goal only) — native sends
  `SendChat(... CHAT_INDIVIDUAL ...)` "Affirmative. Preparing to launch."
  (`FedSrv.CPP:7299-7300`); no reusable server-originated-chat helper exists in
  this port (same precedent as P148/P150). The state transition
  (`StayDocked=false`, undocked, repositioned) is the proof point and stands
  alone.
- **No behavior change to the existing real-peer `PerformUndock` path** — the
  extraction is provably behavior-preserving (Section 2b).
- **No change to P103-P151 code** beyond the `UndockShipState` extraction, the
  `PerformUndock` refactor, and the new docked-branch handling.

## 5. Test

`tests/Allegiance.Core.Tests/OrderChangeLaunchLiveTests.cs` — real loopback
`Bot`/in-process-server live test, same harness style as P148:

1. **The proof.** Boot a real connected bot on a side; choose a sector with a
   real station; place an NPC in that sector (so the undock core's sector-scoped
   station lookup finds the station) at an offset well *inside* `DockRadius`
   (50 < 200), and dock it there (`StationId` set directly, matching P148's
   test). Issue `ORDER_CHANGE` targeting the NPC with a non-`c_cidDoNothing`
   `commandID` (`c_cidGoto`). Assert server-side:
   - `StayDocked == false`,
   - `StationId == null`,
   - the ship's position is now **clear of `DockRadius`** (post-launch distance
     > 200, from a pre-launch distance ≤ 200) — confirming
     `RepositionForLaunch` ran via the shared helper, not just the two flags,
   - `LastLaunchTime >= 0` — the undock debounce started.
2. **Regression guard on the restructured branch.** A docked NPC commanded
   `c_cidDoNothing` still sets `StayDocked = true`, keeps `StationId` set, and
   is **not** repositioned — proving the new launch `else` did not disturb
   P148's arm.

## 6. Verification

- No VM needed at all.
- Targeted live tests: 9/9 passed (2 new + P148's 2 stay-docked + P150's 3
  return-to-base + 2 real-peer `LaunchUndockTests`).
- Full existing suite passes unmodified — see the 3-consecutive-clean-run log
  below (baseline 2744 going in; this task adds 2 → 2746).
- Five documented flakes (`LiveChatSectorWireTests`,
  `LiveCombatWireTests`, `LiveInSpaceEjectTests.
  Already_a_lifepod_is_truly_killed_not_re_ejected`,
  `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`,
  `ShipsUpdateLiveTests.Locked_target_fills_first_heavy_slot_with_real_fractions`)
  are rerun-if-fired, not regressions — the diff is additive plus the pure
  `PerformUndock` extraction, touching none of their code. A concurrent Windows
  build VM has correlated with an elevated flake rate this session, so a
  higher-than-usual retry count is expected.
- Every claim above cites the exact native `FedSrv.CPP` / `shipIGC.h` /
  `appmsg.cpp` lines read this session.
- Committed only after build + test clean on 3 consecutive runs.

## Files changed

- `src/Allegiance.Server/Server.cs` — `UndockShipState` extraction,
  `PerformUndock` refactor to call it, the docked-branch launch arm in
  `HandleOrderChange`, and the handler doc comment updated (launch no longer
  deferred).
- `tests/Allegiance.Core.Tests/OrderChangeLaunchLiveTests.cs` — new test class.
- `ORDER_CHANGE_P152.md` — this writeup.
