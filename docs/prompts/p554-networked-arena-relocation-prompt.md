# P554 — arena mode on a real (networked) server: a config-gated, privileged "relocate" command through native's cheat-code mechanism, so `spawn_scenario {arena:true}` works on 221, not just in the in-process test

## Why this task exists

**P539d** (`78831d20`) added `spawn_scenario {…, arena: true}` (both sides
placed mirrored in the target sector). In its tests, the higher-tech side won
5/6 in open Rigel. **But arena placement needs `BotManager.Relocator`
(`tools/allegiance-mcp/BotManager.cs:39`, used at `:342`), and the only place
that sets it is the in-process test**
(`tests/Allegiance.ClientCore.Tests/P539dFairContactLiveTests.cs:~140-160`,
`WireArenaRelocator`, which calls server internals directly: sector
remove/add, then the `ShipWarped` hook → per-peer SET_CLUSTER,
`Server.Lifecycle.cs:679` → `Server.OnShipWarped`). **The standalone MCP talking
to a networked server (221) has no Relocator, so `arena:true` silently falls
back to staging** (its own note: "without one bots stage/travel from home").
Claude confirmed this on 221 (2026-10-05).

**Native frame to follow (no native teleport cheat exists):**
- Chat-line cheat commands: `DoCheatCode(CFSShip*, const char* msg)`
  (`FedSrv/FedSrv.CPP:2814+`), called from the chat handler (`:5023`, debug
  builds). Tokens include `stations`, `gimme`, `sides`, `createdrones`, `capture`, …
  (`:2822-3022`).
- Privilege: `CanCheat()` / `UTL::PrivilegedUser` (`:664`); privileged pilots
  get `c_ptCheatPlayer` (`:915-939`).
- The port has **no** cheat/admin concept today (`Server.Lobby.cs:769-772`), but
  has a precedent for **loudly-bannered, config-gated port extensions**:
  `liveSettings.testUnlock` (`ServerConfig.cs:149-161, 209-240`;
  `Server.Lifecycle.cs:78-88`).

## Scope

1. **Port native's cheat-code hook:** the chat handler routes privileged
   players' cheat lines to a `DoCheatCode` equivalent (same "consumed, not
   broadcast as chat" semantics). Port only the infrastructure plus the
   token(s) needed here; list the other native tokens as follow-ups.
2. **Privilege, off by default:** a new `liveSettings` option (e.g.
   `adminCheats: { enabled, allowNames: [...] }`), mirroring native
   `CanCheat`/`PrivilegedUser`. **Disabled unless configured.** Bannered loudly
   at boot like `testUnlock`. Never on in a default config.
3. **A port-extension token `relocate <shipId> <sectorId> <x> <y> <z> [fx fy fz]`**
   (document clearly that native has no such cheat). It moves a ship the
   privileged player **controls or launched** (the MCP's own bots; decide
   and justify the ownership rule) using **the same server path a warp
   transit uses**: sector change, then `ShipWarped` → SET_CLUSTER to peers,
   pose set, velocity zeroed, then the normal ship-update flow. Move
   `WireArenaRelocator`'s logic into the server as the implementation, so
   the test and the network path share it.
4. **MCP:** when connected to a networked server, set `Relocator` to send
   that command via the probe/leader connection (or the bot's own), and wait
   for the bot's world to show the new sector (SET_CLUSTER received). If the
   server refuses (not privileged / disabled), arena reports a clear error
   in the tool output instead of silently staging.
5. **221 config:** add the option to `~/allegiance-deploy-compose.server.json.local`
   only as a documented step for Domingo. **Don't edit 221's live config
   yourself.**

## Explicitly out of scope

- Other native cheats (follow-ups).
- Any change to warp/transit physics.
- Making cheats available to normal players.

## Proof

- Failing-first tests: a non-privileged `relocate` is refused and broadcast as
  normal chat (or dropped, per native semantics); with the option disabled
  it's refused even for allow-listed names; a privileged relocate moves
  the ship, sends SET_CLUSTER, and the bot's ClientWorld shows the new
  sector; MCP arena with a refusing server returns an explicit error.
- **Networked live proof:** a local server **as a separate process over
  TCP** (not in-process), the option enabled for the MCP probe name. Run
  `spawn_scenario {size:5, target_sector:"Rigel", arena:true, tech_a:4,
  tech_b:0}` from the **standalone MCP** (stdio, like the 221 driver). Show
  bots placed mirrored (positions from server logs), engagement within
  seconds, and kills. Then 3 × tech 4 vs 0 and 3 × tech 0 vs 4 arena runs
  (240 s) with gunfire kills and collision deaths per side.
- Report `docs/reports/P554_NETWORKED_ARENA.md`, including the exact config
  snippet Domingo adds to enable it on 221.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
