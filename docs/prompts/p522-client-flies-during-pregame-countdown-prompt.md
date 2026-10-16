# P522 — first launch of a new game: the client flies while the server still drops updates as "pregame" → SHIP_RESET teleports the ship back to the start

## Why this task exists

**Domingo's live playtest (2026-10-03):** "first dock try is giving me a
repeated cycle, I teleport to the same spot I started at full speed, and the
second try it docks."

**Evidence:**
- **Server**, 221's `[ctrlak]` summaries for the new session (peer 4),
  using P518's drop counters:
  1. `accepted=0 … dropped[pregame=47]`;
  2. then `accepted=14 rejected=1 mean-reject-dist=1264.97m … pregame=99`;
  3. then `accepted=75, rejected=1` (stable, no more drops), followed by a
     normal `[dock]`.
- **Client:** `~/monogame-client-run-pregame-reset.log`, showing
  `[sim] seeded from server pose …`, then `[sim] RESUMED sending native
  SHIP_UPDATE`, then `[sim] SHIP_RESET #1 — snapped to server pose`.

**The mechanism:** the server's P180 gate drops SHIP_UPDATEs while the
mission isn't Started (`Server.Dispatch.cs` ShipUpdate path, "a ship must
not move while the mission hasn't started"). That's correct and native. But
the client **starts flying and sending as soon as the start countdown
begins**: P282's ready-check enters Flying on `CountdownStartLocalMs`
(`Game1.Panes.cs` `UpdateReadyCheck`, "mission start countdown began ->
flying"). So the player flies locally while the server ignores every update
(99 pregame drops) and keeps the ship at the start point. When the mission
starts, the first processed update is ~1.26 km off, which is rejected, so
`SHIP_RESET` snaps the ship back to the start point carrying its current
velocity. It only happens on the **first launch of a new game**.

**Native** (`~/Development/Allegiance/src/`): find what the native client
does during `STAGE_STARTING` (countdown). Can the player launch or fly,
or is the ship held, docked or frozen until `STAGE_STARTED`? Check
`clintlib/appmsg.cpp` MISSION_STAGE handling, the WinTrek
countdown/launch screens, and the FedSrv launch gating. Cite it.

## Second instance of the same family: EVERY undock causes one SHIP_RESET (found 2026-10-03, added before this task started)

Domingo asked "where am I?" after launches. Client log
`~/monogame-client-run-undock-reset.log`: **every** `[monogame] undocked:
leaving hangar` is followed within a line or two by `[sim] SHIP_RESET #N —
snapped to server pose` (#2, #3 and #4 for three consecutive launches). The
server's peer-4 summary shows `rejected=4, mean-reject-dist=704.92m`:
exactly one ~705 m reject per launch (plus the pregame one), with
`accepted` otherwise climbing normally.

**Likely mechanism (verify):** on undock the server places the ship at the
station's real launch point. The client's P508 sim resumes from a stale
pose, such as the pre-dock pose or the held docked pose (`SeedSimFrom(up)`
on the freeze edge, `Game1.Prediction.cs` ~103-125). Its first update is
~700 m off, so it's rejected and reset. The visual result is a teleport on
every launch.

**Add to scope:**
- after undock or launch, the client must **re-seed the local sim from the
  server's authoritative launch pose** (the first post-undock server update,
  or whatever native uses) **before** sending its first update;
- the live test must show **zero rejects and zero SHIP_RESETs across 3
  consecutive dock → undock cycles**, in addition to the first-launch case.

## Third undock artifact: a false red damage flash right after undock (added 2026-10-03, before this task started)

Domingo: "right after undocking I see the collision warning and then it goes
away, no ship damage or anything … red flash around the screen". Neither the
client log nor the server logged any collision or damage around the undocks.
- The red full-screen overlay is `DamageFlashTracker`
  (`src/Allegiance.ClientCore.Game/Flight/DamageFlashTracker.cs:~93-102`,
  fed at `Game1.Rendering.cs:~1489` with `up.HullFraction` and
  `up.ShieldFraction`). It arms on **any decrease in hull OR shield fraction**
  between frames, and its baseline is **never reset across transitions**.
- So an undock, especially into a **newly bought hull**, can register a
  "drop" (for example the previous ship's full shield → the new hull's starting
  shield, or a value change across the SHIP_RESET jump) with no damage at all.

**Add to scope:**
- Reset the damage-flash, and any sibling damage-inferring trackers (such as
  `DamageDebrisTracker`), baselines on **dock, undock/launch, hull change,
  respawn and SHIP_RESET**. These are state transitions, not damage.
- **The parity question: does native launch a ship with full shields?**
  Check native dock refit and launch (`FedSrv.CPP` dock restock/`RefillPart`,
  ship launch). P494 refills on dock. If the port launches new hulls with
  less than native's starting shield or energy, fix the server to match
  native, and cite it.
- Live check: 3 undocks, including one hull swap, give **no red flash**,
  while real damage still flashes.

## Scope

1. **Match native:** the client mustn't fly the own ship, or must hold
   the local sim frozen and not send, until the mission is Started, if
   that's what native does. Alternatively, if native lets the ship sit in
   space during the countdown, the client sim must stay frozen at the
   server pose until Started. Either way, there must be **zero pregame
   drops and no SHIP_RESET** on the first launch.
2. Keep the P180 server gate unchanged. It's correct.
3. Show the HUD countdown state clearly while held, using whatever native
   shows.

## Explicitly out of scope

- The death/respawn latch (P521).
- The mission rotation flows (P439/P480/P481).

## Verification

- **A live test, failing first:** a fresh mission, a client readies up, the
  countdown runs, then the first launch. Server `dropped[pregame]` stays
  around 0 after the countdown, there's **no SHIP_RESET**, and the first
  dock attempt succeeds.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P522_PREGAME_FLIGHT.md`.
