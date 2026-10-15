# P560 — MCP fixes from the first 221 showcase: bot lifepods loop back and forth through the aleph (Alderan ⇄ Rigel), and showcase minefields never get laid (ambiguous layer bucket name; invest doesn't complete)

## Why this task exists (live on 221, 2026-10-06, server/MCP `762dfe5e`)

`spawn_scenario {preset:"showcase", target_sector:"Alderan", minefields:{side:"both",count:1}}`
(P557 `1f3bfcbf`, P558 `29de2eeb`), 15v15 staged into Alderan.

**1. Bot lifepods ping-pong through the aleph.** Of 14 ejections, 9 pods were
shot down within ~1 s (native true death → docked lifepod at home, correct,
`Server.Combat.cs:~1562`, `FedSrv.CPP:12026-12074`). Of the 5 surviving pods,
side-1 pod **8124** flew home correctly (Alderan → Juniper). All four side-0 pods
(**8105, 8112, 8114, 8115**) **loop between Alderan and Rigel**, crossing every
few seconds:
```
[warp] ship 8105 crossed from 'Alderan' (1052) to 'Rigel' (1059) via warp 2187
[warp] ship 8105 crossed from 'Rigel' (1059) to 'Alderan' (1052) via warp 2186
[warp] ship 8105 crossed from 'Alderan' (1052) to 'Rigel' (1059) via warp 2187   (repeats)
```
This is the oscillation **P540b** fixed for the PLAYER autopilot ("routed legs
braked to a stop and a small hull re-crossed the aleph"; fixed with
`AutopilotSteering.Pursue`, fly-through at full throttle). The bot pod law
(`tools/allegiance-mcp/BotManager.cs`, `DrivePodHome` `~:2089+`, P539) routes
with the older P496 warp-approach law (`WarpApproachParams` / `CombatFlyTo`).
Find exactly why a pod re-enters the aleph it just exited (exit placement plus
velocity, the next-hop choice in Rigel, braking inside the trigger sphere, or a
stale route), fix it at the root, and reuse P540b's `AutopilotSteering.Pursue`
for warp legs rather than a third law. Check combat/transit warp legs for the
same bug. Also replace the pod's straight-line nearest-station pick (ignores
sectors) with the same nearest-valid-station rule the server uses (P550,
nearest `c_sabmRestart`/rescue station), or route-distance.

**2. Showcase minefields never get laid.**
- `FindLayerBucketName` returned the **name** `.TowerAC`, which matches **10**
  buckets on the 221 map → `invest: bucket '.TowerAC' is ambiguous`, and the
  whole showcase aborted (no bots). Claude patched it locally (return the
  bucket **id**; `ResolveBucket` resolves numeric ids exactly) in
  `~/allegiance-deploy` (uncommitted). Commit that fix properly, with a test.
- With the id, the invest then reported `layer-drone invest did not complete in
  window — no minefield laid` for both sides. Find why: price vs grant money,
  `TimeToBuild` vs the 40 s window (`mgr.Invest(side, layerBucket, null, 40)`,
  `McpTools.cs:~733`), whether `.TowerAC` is really the right layer bucket
  for the side's civ (maybe a tech-gated one; after P551, `CanBuy` refuses
  unbuyable buckets), or the drone spawning elsewhere. Make it work on the
  221 map (`m2xspiderw.igc`, civIds [25,3], testUnlock allTech + money).
- If laying takes longer than the fleets' travel, lay the fields **before**
  launching the fleets (or delay fleet launch until laid), and report it.
- Abort semantics: a minefield failure must **not** abort the fleet spawn;
  report the error row and continue.

**3. Pod visibility (Domingo: "pods are actually invisible or perhaps the model is
too tiny? i could not see it").** Native lifepod hulls are small (PCore001: hull
444 'Lifepod' model `utl22` length 4; hull 113 'Lifepod' model `wc_gtpod`
length 5; vs Adv Fighter 17.14, Adv Scout 11.4). The client resolves pods per civ
via `HullModelResolver` (assets `gt-pod`, `rix-pod`, `bios-ejectpod`,
`ic-ejectpod`; fallback `bios_scout_upscaled`), drawn through
`ResolveShipModel(...) ?? _fallbackModel` (`Game1.Rendering.cs:~2342-2407`).
Verify, with evidence:
- which asset each 221 civ (civIds 25 and 3) resolves for hulls 444/113, and the
  in-flight world scale it's drawn at vs native length (a headless contact sheet:
  pod next to an Adv Fighter at the same distance, both civs);
- that the HUD contact indicators (and target cycling) include lifepods, as
  native does for any visible ship (cite native's indicator/target rules);
- if a pod resolves to the wrong asset, the wrong scale, or is excluded from
  indicators, fix it (native-faithful; don't upscale pods beyond native).

**4. Relaunched bots linger at home / look stuck on the station ring (Domingo,
2026-10-06 21:55).** After pods docked home (`[p539] pod bot … DOCKED at station
4328 — pod reached home`) or after native true-death respawn, many bots stayed in
their **home sectors** (list_active_bots: 15 side-1 bots in Organia 1051, 4 side-0
bots in Ganymede 1050, all `combat`) while the fight was in Alderan, and some
appeared **stuck on the station's outer ring**. The server logged **no station
collisions**, so check: whether relaunch happens and the bot re-enters its route
to the target sector (staging may wait forever for an already-assembled side, or the
relaunched bot isn't re-staged), whether bots hover inside the placeholder ring's
drawn volume (visual mismatch with the native station hull; P564 replaces the
placeholder), and the launch exit point vs native's garage exit. Fix so relaunched
bots return to the fight. Proof: a pod docks, the bot relaunches and reaches the
rendezvous (server warp lines), live.

## Explicitly out of scope

- Native respawn rules (correct as is).
- Combat steering (P558).

## Proof

- Failing-first tests: pod warp leg never re-enters the exited aleph (closed
  loop sim on the real map's warp geometry); layer bucket resolution by id;
  minefield failure doesn't abort the fleet spawn.
- **Live (separate-process server over TCP + standalone MCP, the 221 config and
  map):** the showcase with minefields. Show (a) both minefields laid at the
  enemy arrival alephs (server log lines + positions); (b) at least one ship
  taking mine damage from them (P452 lines); (c) every surviving bot pod's warp
  path home (no ping-pong; every pod docks at a home/valid station within N
  minutes); (d) docked bots relaunching and rejoining.
- Report `docs/reports/P560_BOT_PODS_AND_SHOWCASE_MINEFIELDS.md`.

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

- **Real-window proof tooling:** use P572's `scripts/live-proof.sh` and the client
  `--auto-side/--auto-ready/--auto-launch/--auto-goto/--autoshots` flags
  (`docs/LIVE_PROOF.md`) to take unattended real-window screenshots (with MCP bots
  where needed). "Couldn't automate the window" is no longer an acceptable gap.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
