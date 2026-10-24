# P786 (PAR-5) — HUD wire data: the native values the client HUD needs but does not receive

Base: **public master at dispatch** (the working repo is now the PUBLIC `AllegianceCore-public` clone; P797 map discovery is merged) — it brings per-side seen state and ore seen (re-check
`git log -1`; re-grep every line number). Branch **`pi/p786`**, in the worktree the dispatcher created. Report:
`docs/reports/P786_HUD_WIRE_DATA.md` — per row: native source and rule, how the value now reaches the client (existing native
field vs native message), before/after, tests (and which fail on master), gate numbers; the parity rows updated.

Read `/home/domingo/.claude/jobs/82c1509b/tmp/pi-lane-rules.md` first and follow it (no remote hosts, **no `git stash`**,
`~/.dotnet/dotnet` explicitly, **no `pkill -f`/`pgrep -f`**). Native reference: `~/Development/Allegiance/src/`. HUD
conventions: native 2-D images are **y-up**; a StringImage hangs **below** its point; GroupImage paints its mdl list in **reverse**.

**Wire parity first.** For each value, find how the **native client** gets it (which native message/field, or computed
client-side from data it already has) and do exactly that. Only add data the native wire carries; if native computes it on the
client from IGC state, compute it on the client. If a value truly needs a non-native field, stop and list it in the report.

## Why
P782's re-audit left these HUD rows `partial` with a `BEHAVIOUR GAP:` that is missing data, not missing drawing
(`docs/parity/parity-matrix.csv`):
1. **Signature percentage** — no live cloak ramp (`GetCloaking`): `shipIGC.h:268-270`, `afterburnerIGC.h:78/89`,
   `cloakigc.h:76/89`; HUD `console.cpp:845,865-866`.
2. **Launcher arming progress** — `GetArmedFraction` absent, so `ReadyState 2` is unreachable (`consoledata.cpp:1157,1189`,
   `Artwork/invsmissile.mdl:69`).
3. **Radar probe and mine contacts** — lifetime fill `GetTimeFraction` needs `m_time0` (`radarimage.cpp:1073-1079`,
   `probeigc.h:271-279`, `mineigc.h`).
4. **Radar missile contacts** — missile model radius for the range clip (`radarimage.cpp:626-636,819-832`).
5. **Ore seen per side** — `GetOreSeenBySide/(2*capacity)` for **Radar asteroid contacts**, **Radar ore and miner fill bars**
   (`radarimage.cpp:1053-1069`) and **Target ore** (`consoledata.cpp:449`); P797 adds per-side ore seen — consume it.
6. **Scanner "can see" + last seen** — **Target hull** (`consoledata.cpp:202-212`), **Target shield** (`:252-276`),
   **Target name** (`:65`), **Target type description** (`:40`), **Accepted and current order turn arrows** (`indicator.cpp:411,783`):
   the per-target `CanSee` scanner gate and the last-seen hull type for an unseen target.
7. **Cloak strength and drained state** — check whether this is behaviour (drained state) or only GlassHUD art; reclassify
   with evidence (`consoledata.cpp:480`, `dialog.mdl:570-656`).

## Scope
Implement 1-6 end to end (sim/server where the native server sends it, client state, HUD consumer), and settle 7. Update each
parity row to `done` / `done-visual` (reason starts `VISUAL:`) / exact `BEHAVIOUR GAP:` with cites. Respect the
zero-allocation/no-lock frame-path rules already in the HUD code (snapshot buffers, `HudSnapshotBuffers.cs`).

## Explicitly out of scope
Map discovery itself (P797); turret gunner rows; training rows; fonts/art; new HUD elements; the radar label tiers (P783 done).

## Verification (light gate)
Loopback/wire tests per value (fail on master), HUD model tests for each consumer, a real-window screenshot pair per visible
change (`scripts/live-proof.sh`). Build 0/0; `tools/build-tools.sh`; new/affected tests **3× alone**; **one** Fast-tier suite;
`scripts/parity-scorecard.py` validates. Flakes → `docs/TEST_FLAKES.md`.
