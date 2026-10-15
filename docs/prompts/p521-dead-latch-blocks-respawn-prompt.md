# P521 — after a death + respawn, the own ship stays "dead" forever on the client: local sim frozen, no SHIP_UPDATEs, controls dead

## Why this task exists

**Domingo's live playtest (2026-10-03):** "controls not working after undocking".
The evidence log is saved at **`~/monogame-client-run-deadlatch.log`**.

**Chain, confirmed from both logs:**
1. Domingo suicided. The server ejected him to a lifepod, then killed it:
   `KILL_SHIP(105)` plus `SHIP_DELETE(20) SDR_KILLED` for ship 8104. He
   respawned in the hangar **with the same ship id 8104**.
2. The client's `ClientWorld.Apply(KillShip)`
   (`src/Allegiance.ClientCore/State/ClientWorld.cs:~2150-2160`) and the
   `SDR_KILLED` branch of `Apply(ShipDelete)` (`:~2197`) **permanently**
   latch `ShipEntity.IsDead = true`. That's by design for remote corpses
   (P344/P370). **Nothing ever clears it.** `IsGone` is cleared by later
   pose/identity updates (`ClientWorld.cs` ~1169/1325/1411), but `IsDead`
   isn't. `IsNotVisible => IsDead || IsGone` (`ShipEntity.cs:49`).
3. P508's own-ship sim freezes whenever `mine.IsNotVisible`
   (`src/Allegiance.ClientCore.Game/Game1.Prediction.cs:~103-118`). The
   client log shows `[sim] freeze: dead`, then `[sim] STOPPED sending native
   SHIP_UPDATE`, and **no RESUME on any later undock**.
4. **Server side**, 221's `[ctrlak]` summary: `accepted` frozen at 6208,
   `dropped[cookie=0 noship=0 …]` all zero. The client simply stopped
   sending. P518's cookie rotation was working (cookie 0x0E → 0x0F).

**Probably also affects other players:** a **remote** player who dies and
respawns with the same ship id stays `IsDead`/hidden on everyone else's
client forever. Verify this.

## Scope

1. **Find native's respawn semantics.** Native is at
   `~/Development/Allegiance/src/`; look at `clintlib` / `WinTrek` handling
   of the own ship after `KILL_SHIP`, at respawn (the hangar/docked
   re-entry), and at the ship re-entering a cluster
   (`SET_CLUSTER`/undock/launch). Find what native resets on the client.
2. **Clear the death latch at the right, native-matching moment:**
   - for the **own ship**: on respawn or re-seat (for example docked at a
     station after death, a new `SET_CLUSTER`, or a launch);
   - for **remote ships**: when real presence returns after a death, such as
     a new `EXPORT` or a fresh pose for that id after respawn.

   Keep P344/P370's protections: a corpse must not "re-present" from stale
   or late packets. Base the rule on a real respawn signal, not just any
   packet.
3. Make sure P508's own-ship sim **resumes**, and SHIP_UPDATEs restart,
   after respawn + undock, while the server's `accepted` count climbs again.

## Explicitly out of scope

- P507/P518 server acceptance and cookies, which work.
- Lifepod rescue mechanics.

## Verification

- **A live test, failing first:** a real client against a real server goes
  undock, then `#suicide` or a real kill, then respawn (docked), then undock
  and fly. The sim resumes and `accepted` climbs after the respawn. Repeat
  the death twice.
- A two-client test: client B sees client A again after A dies and respawns.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P521_DEAD_LATCH_RESPAWN.md`.
