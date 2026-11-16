# P1029 — R1b F4: server repositions (carrier relaunch, ripcord, admin relocate) skip the cookie / SetCluster

Owner finding: **F4** of `R1B_CARBON_REPORT.md`. Report name: `R1B_REPOSITION_SYNC_P1029`. Gameplay bug as well as an
authority gap: honest players landing on a carrier may lose flight control.

- **Relaunch:** `Server.Docking.cs` `PerformRelaunch` (~`:1466-1479`) sends `RelaunchShip.Cookie = 0`. Native
  `LandOnCarrierEvent` stamps `pfmLaunch->cookie = pfsShip->GetPlayer()->NewCookie()` for players
  (`FedSrv/FedSrv.CPP` ~`:13015-13016`). Port: stamp `NextCookie(EnsurePeerUpdate(conn))` for a player-owned lander
  (bots/NPCs as native). Prove with a live test: land on a carrier, then a normal SHIP_UPDATE is accepted.
- **Ripcord:** `PerformRipcordTeleport` (~`:553-676`) moves the ship to the target sector without `SetCluster` /
  new cookie / treasure snapshot for the ripper. Native `UseRipcord` → `SetCluster` → `CFSPlayer::SetCluster`
  (`FedSrv.CPP` ~`:13442-13527`, `fsship.cpp` ~`:747-930`). Make the ripper's own sector change match native.
  **Not yours:** propagating sector changes to boarded child ships — that is P1034 (F3); leave a TODO pointing at it.
- **Admin relocate:** `Server.AdminCheats.cs` `HandleRelocateCheat` / `RelocateShipToSector` (~`:322-380`): refuse docked,
  boarded, team-lobby or dead targets, and send `SetCluster` + new cookie for the moved player.
- Check the client side (`src/Allegiance.ClientCore/State/ClientWorld*.cs`) handles the corrected messages (it should —
  they are native shapes); if a client change is needed, keep it minimal and test it.

---
## Common to every R1b fix story (P1029–P1034)

**Source:** the second review `R1B_CARBON_REPORT.md` (repo root, by Gemini 4 Carbon). Claude spot-checked the
code and native citations, but the findings have **no proof tests yet** and some details may be wrong. Read your
finding's section in full, then **first reproduce it with a failing test** (live/loopback server test where the
finding is about wire messages — copy the patterns of `tests/Allegiance.Core.Tests/R1ReviewTests.cs` and the existing
live tests for the area). If a part of the finding is not real, say so with `file:line` and evidence, and skip it.

**Definition of done for each finding you own:**
1. Your reproduction test fails before the fix and passes after it, unchanged.
2. Fix the product code; add focused tests for the edges (legit client unaffected, limit boundary, config off/on).
3. **Legitimate play must not change:** normal clients, bots (MCP/`Allegiance.Bot`), the showcase/skirmish presets and
   the existing live tests must keep passing. Name the existing test classes you ran that cover the touched paths.
4. Any limit gets a named constant or `server.json` setting with a sane default, documented in the report.
5. Native: say for each fix whether it is native parity or a deliberate port hardening (the report says which), cite
   native `file:line` (re-grep), and update `docs/parity/*.csv` rows where a behaviour vs native changes.

**Lane environment:** you run in an MS-A2 Debian 13 container with your own clone; your worktree is the current
directory (own branch). SDK `~/.dotnet/dotnet`. Artwork `/srv/corpus/Artwork` and native reference
`/srv/corpus/Allegiance/src/` are READ-ONLY (`local.env` points at them). Docker is not installed. Base: Claude's
master at dispatch. Do not leave the brief file in the change set.

**Rules:** read `AGENTS.md` and `GLOSSARY.md` first. Public-repo hygiene (no personal paths/IPs/hosts), **never
`git stash`**, never `pkill -f`/`pgrep -f`, no synthetic CPU load, scratch in `.scratch/<id>/`, do not commit or push,
do not contact the rig, mini, Mac, VPS or any public host. Every process you start must end before you finish.
Build-hang workaround: `--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`. Do not disable
NuGet audit. Stay inside your finding — the other R1b findings are separate stories (P1029–P1034); do not fix them. **Time:** 8 hours max; write the report first, then gate.

**Gate (light):** build 0/0 + `tools/build-tools.sh`; new/unskipped tests 3× alone; **one** full fast suite
(`tools/run-tests.sh --tier fast`, ≥ 90 min bound). Non-task failures: 3× alone + `docs/TEST_FLAKES.md`.

**Report:** `docs/reports/<NAME>_<ID>.md` — per finding: root cause (file:line), fix, native citation + parity
verdict, tests (unskipped + new), config/limits; then test counts (Passed/Failed/Skipped) and the gate. Footer:
"Native intent" and "Modern approach".
