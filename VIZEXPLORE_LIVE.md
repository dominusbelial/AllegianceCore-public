# VIZEXPLORE — live proof: real WOPR AI exploring, watched live (Claude Code's own pass)

Date: 2026-09-01. Follow-up to P45 (`3727dba`)/P46 (`63ffbb6`) and the
tuning commit `a6a7da0` — all `AllegianceCore` (branch `master`). Native
`Allegiance` @ `74f3b1db` (P42) — diagnosis/testing only, fully reverted,
no commit. This is the pass explicitly scoped as Claude Code's own job
(Windows-VM execution), not delegated — matching the P42 precedent.

**Headline verdict: YES — real, unmodified native `Wopr.exe` bots running
the real `ScoutExploreMap` strategy connect to the P45/P46 spectator
backend and are watched live, in a real browser, exploring and genuinely
crossing warps — multiple times, confirmed both server-side (`[warp] ship
... crossed` log lines) and in the actual rendered scene (the frontend's
own multi-sector HUD, `sectors: 2 · active: Haumea (3)`, appeared for the
first time against real bots, not just P46's own loopback verification).**

## 1. Trial host (native, temporary, fully reverted)

Wrote a small diagnostic (`Allegiance/src/Wopr/Wopr/MultiExploreTest.cs`,
~30 lines, plus a `-me <count>` dispatch case in `Program.cs` and one
`<Compile Include>` line in `Wopr.csproj`) that calls
`TeamDirector.CreatePlayer` N times on one `TeamDirector` — each call spins
up its own thread that direct-connects, waits for a ship, and (per
`TeamDirector.cs`'s own hardcoded direct-connect path) attaches
`StrategyID.ScoutExploreMap` automatically once a ship exists. No new
mechanism — this is exactly the pattern P38 already proved at 4-bot scale;
the diagnostic is just a minimal harness to invoke it N times against
P45's `explore` backend instead of a plain default-config server.

Removed entirely before finishing (`git diff` empty vs `74f3b1db`); a copy
is kept at `/tmp/.../scratchpad/MultiExploreTest.cs.bak` for reference if
this pattern is needed again.

## 2. Two real bugs found and fixed along the way — both process/harness
## issues, not `AllegianceCore` or `Allegiance` product bugs

**(a) A join-storm crash.** First attempt (4 bots dispatched 500ms apart)
crashed the whole `Wopr.exe` process — 4 crash dumps, identical timestamp,
one per bot thread, consistent with a genuine concurrency fault triggered
by near-simultaneous connection establishment. Fixed by widening the
stagger between `CreatePlayer` calls to 3s. Not investigated further at the
root-cause level (a deep native-threading question, out of scope for
getting the demo working) — flagged here as a real, reproducible finding
for a future task if it recurs.

**(b) An SSH-session-lifetime trap.** A second attempt (3s stagger) still
died — but with **zero** crash dumps this time, a different signature. The
launcher script had returned control (and closed its SSH session)
immediately after `Start-Process`, without waiting on the child. Windows'
process/session model appears to kill child processes tied to a closed
remote PowerShell session in this environment. Fixed by keeping the SSH
session open for the bots' full run (`Wait-Process -Timeout 1800` inside
the same remote invocation, run via a backgrounded local `ssh`) — matching
the pattern every single-bot P37-P42 trial already used, which this
multi-bot attempt initially skipped. Stable ever since.

## 3. A real frontend bug found from a live, multi-batch demo P46's own
## loopback verification couldn't have caught

After the crash-and-relaunch cycle above, a *second* batch of bots joined
the *same still-running* frontend page while the *first* batch's ships
(now disconnected) were still rendered — frozen at their last position
forever, because the renderer never prunes a ship whose owning peer
disconnected. The live batch's ships were real and moving (confirmed
server-side: real, varying per-tick velocities up to ~80 units/s) but
appeared alongside stale "ghost" ships, which is what actually looked like
"ships not doing anything" from the browser. Not fixed in `index.html`
(would need product code changes to prune on a `tick` no longer including
a previously-seen id) — worked around for this pass by restarting the
whole backend fresh (which resets connection state) rather than patching
the renderer, since a clean single continuous session naturally never hits
this path. **Worth fixing in a future small task** if this tool is used for
longer-running or reconnect-heavy demos.

## 4. Live proof — real crossings, multiple bots, multiple sectors, watched
## in a real browser (not just server logs)

**Setup that actually got fast, frequent crossings**: per a live request
("its going to take forever, can we... put them closer to the aleph"),
retuned `InjectExplorerSpawn` (`tools/vizfight/Program.cs`, committed
`a6a7da0`) to spawn each bot ~150 units from a real warp's own collision
volume (reusing P42's `warptrial` technique) instead of the far-away
default staged position — still a real flight and a real
`ScoutExploreMap` decision, just a short one. 8 real bots relaunched
against this; multiple genuine crossings landed within seconds:

```
[warp] ship 8098 crossed from 'Arrokoth' (1051) to 'Haumea' (1057) via warp 1051
[warp] ship 8099 crossed from 'Arrokoth' (1051) to 'Haumea' (1057) via warp 1051
[warp] ship 8098 crossed from 'Haumea' (1057) to 'Arrokoth' (1051) via warp 1052
...(30+ more crossings across the session, several distinct ships, both directions)
```

**Confirmed in the actual rendered page** (not just the server log) via
Claude Code's own `claude-in-chrome` session against the live page:
before any crossing, `__vstate()` showed `sectors:[{1051,Arrokoth}]`
(one sector known); after real crossings landed, the HUD read
`focus: Arrokoth (5 ships) · active: Haumea (3)` and the WS status line
read `sectors: 2` — P46's own multi-sector-awareness feature, built and
loopback-verified in P46 but never before proven against a real bot,
firing live for the first time.

## 5. Repos after this pass

- `AllegianceCore`: `git status` clean, two real commits from this pass —
  `a6a7da0` (spawn-near-warp tuning, product code, kept). Build/test
  verified clean **2232/2232 × 3** after the tuning change. No other
  product code touched.
- `Allegiance` (native): `git diff` empty vs `74f3b1db` — the temporary
  `MultiExploreTest.cs`/dispatch/`.csproj` entry fully reverted, confirmed.
  No commit. VM tree's Wopr.exe.config left pointing at whatever
  transport port the backend last used — **not committed anywhere**, purely
  local machine state; regenerate it (see `TRANSPORT_P42.md`'s own recipe)
  for any future trial.
- **Left running per explicit request**: `tools/vizfight explore` backend
  (local, port 7717) and 8 real `Wopr.exe` bots (VM, pid varies by
  session) — both untouched by this cleanup. `http://127.0.0.1:7717/` is
  live with real `ScoutExploreMap` AI exploring and crossing warps.

## 6. What this closes

This is the first time in the whole project that real autonomous WOPR AI
has been *watched*, not just logged — completing the arc from P37 (first
confirmed real flight) through P41/P42 (warp-transit mechanism, proven
against a real client) through P43-P46 (the spectator/visualizer
infrastructure) to this: real bots, real navigation, real crossings,
rendered live in a browser.
