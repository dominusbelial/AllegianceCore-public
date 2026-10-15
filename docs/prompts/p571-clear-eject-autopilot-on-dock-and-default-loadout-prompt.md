# P571 — after death the respawned player's NEW ship keeps the eject autopilot (flies back to dock right after launch), and the shop launches new hulls with ZERO parts (no guns/shields/missiles) instead of the native default loadout

## Why this task exists (live on 221, 2026-10-06, Domingo's client `99df9de6`)

**Domingo:** "some of the ships as soon as I undock are trying to dock again …
that's right when I come back from dead … back in station". Client log:
```
[eject] own ship 8139 became lifepod hull 444 at …
[monogame] eject: autopilot home autopilot ON → Garrison (Station#4328)
[monogame] docked: hangar/loadout screen …
[monogame] LAUNCH -> BUY_LOADOUT(fLaunch) hull 85 + 0 part(s)
[monogame] undocked: leaving hangar
[sim] hull reconfigured (respawn/lifepod/hull-swap)
[monogame] autopilot OFF (manual input)      ← only when he touched the controls
```
1. **The P540 eject autopilot is never cleared on dock/respawn/hull change**: the
   next launched ship keeps steering to the Garrison. Native clears the autopilot
   on dock (find the native code: `clintlib`/`WinTrek` autopilot reset on dock or
   ship change, e.g. `SetAutoPilot(false)` when docking or entering the station).
2. **Every launch sends `+ 0 part(s)`** (also seen server-side: `BUY_LOADOUT_ACK
   hull 13 nParts 0`, `hull 73 nParts 0`). Native's hangar gives a newly chosen
   hull its **default loadout** (`DataHullTypeIGC.PreferredPartsTypes`; P555 made
   them mountable; see native `hangarscreen.cpp`/`load.cpp` default-loadout
   logic and `BuyLoadout`). Check `Rendering/ShopModel.cs`
   (`SelectHullAtCursor` → `RebaseForEffectiveHull`, `ChosenParts`) and
   `Game1.Panes.cs:~263-275` (`SendBuyLoadout(..., parts: shop.ChosenParts)`).

## Scope

1. Clear the eject/pod autopilot (and any autopilot target/goto) when the own
   ship docks, respawns, or its hull changes, as native does. Keep P540's pod
   autopilot behaviour while actually in a pod.
2. New hull selection in the shop starts from the hull's **native default
   loadout** (the preferred parts that mount, P555 masks), as native; the
   player can still edit it. Launching an unchanged hull keeps its current
   parts. Check affordability as today (P468).
3. Check the "relaunch after death" path end to end: the respawned docked
   lifepod → choose a hull → launch with the default loadout, no autopilot.

## Proof

- Failing-first tests: autopilot cleared on dock/hull change; shop default
  loadout for Adv Scout (Sm Shield 1, PW Gat Gun 1, MRM Seeker 1, Counter 1, …)
  and for a fighter and a bomber; `SendBuyLoadout` carries those parts.
- Live (local server, real window): die → pod autopilots home → dock → launch a
  new hull: the ship launches **with weapons and shields** (server
  `BUY_LOADOUT_ACK … nParts N>0`) and **no autopilot**. Screenshot.
- Report `docs/reports/P571_RESPAWN_AUTOPILOT_AND_DEFAULT_LOADOUT.md`.

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
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

- **Real-window proof tooling:** use P572's `scripts/live-proof.sh` and the client
  `--auto-side/--auto-ready/--auto-launch/--auto-goto/--autoshots` flags
  (`docs/LIVE_PROOF.md`) to take unattended real-window screenshots (with MCP bots
  where needed). "Couldn't automate the window" is no longer an acceptable gap.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
