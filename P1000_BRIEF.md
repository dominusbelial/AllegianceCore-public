# P1000 — Civ strategy, part 1: core-derived CivProfile + portfolio scoring behind `bots.civStrategy` (off), and match receipts

Dispatched 2026-11-11 to the MS-A2 Codex lane (LXC `lane-codex`). **Lane environment:** you run inside a Debian 13 container
(8 cores, 24 GB) with your own clone; your worktree is the current directory, branch `codex/p1000`. SDK at `~/.dotnet/dotnet`
(on PATH). Artwork corpus `/srv/corpus/Artwork` and native reference `/srv/corpus/Allegiance/src/` are mounted READ-ONLY
(`local.env` already points at them) — wherever this brief says `~/Development/Allegiance/src/`, use `/srv/corpus/Allegiance/src/`.
You are the only job in this container, so the full fast suite may use all 8 cores.

Base: Claude's local master at dispatch (≥ 67c950eb, includes P999). Own worktree and branch. Rules: public-repo hygiene
(no personal paths/IPs/hostnames in committed files), **never `git stash`**, `~/.dotnet/dotnet`, never `pkill -f`/`pgrep -f`,
no synthetic CPU load, scratch in `.scratch/p1000/`, do not commit or push, do not contact the rig, mini, Mac or any public
host. Every process you start must end before you finish (bound runs with `timeout`). Native reference
`/srv/corpus/Allegiance/src/` (read-only); re-grep every line number before citing. Build-hang workaround:
`--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`. Worktree needs `local.env` (report the Skipped
count). Do not disable NuGet audit.

**Time:** your limit is 8 hours and a timeout loses your turn. Write the report first, then gate.

## Why
`docs/reports/CIV_BALANCE_P999.md` (read all of it first): Gigacorp won 34/56 rig matches (20/26 at 400 bots) because one
civ-blind, serial, cheapest-first commander (`ResearchTree.Generate`, `ResearchCommander`) rewards Gigacorp's research
speed (dev time 1.0 vs Phoenix 1.5 / Omicron 2.5) and economy (+25 % payday, mining 1.25), and the fleet policy is
civ-blind (`Showcase.ComposeShowcaseFleetCount` 46/20/20/14; `TechLoadoutSelector` scores weapons vs a core-wide mean).
Domingo's rule: **bot strategy derives from each civ's specs, not one research path.** P999 designed it (§ "Specification-
driven strategy design", steps 1–6) and prototyped the scorer offline (`tools/p999-civ-strategy/CivStrategyScorer.cs`).
This task is P999 follow-up 1. Part 2 (P1001) wires it into the commander and fleet.

## Do
1. **Flag** `bots.civStrategy` (default **off**; config + env override, logged at startup, same pattern as other bot/
   showcase flags; document it where operator config/env is documented). Off = today's behaviour exactly (pin it).
2. **CivProfile (production code, not the tool):** move/rewrite the P999 prototype into the bot/commander layer as an
   immutable profile built from the loaded core for any civ (design steps 1): starting techs/stations, every legal
   prerequisite route, hull mobility/defence/energy/ammo/mounts/cargo/crew, part/projectile/missile damage matrices vs
   armor/shield types, economy (payday, mining cycle, constructor), dev cost/time and accumulated GA. Cached by core
   identity/version + settings + civ. No civ-ID switch statements and no hard-coded path names.
3. **Portfolio scoring (design steps 2–3):** concrete power spikes (anti-ship loadout, deliverable anti-base wing, miner
   turnaround, rescue/repair), scored per funding and critical-path time against the **actual opponent civs' profiles**
   (public civ data only — no hidden enemy state); choose primary + counter from 2–3 close portfolios by a stable hash
   of core identity, match seed, civ, settings, opponents. Log inputs, choice, alternatives and rejection reasons once per
   match/side (bounded).
4. **Receipts (observability):** bounded, match-stamped server/bot log lines for match seed, civ, home slot, map, chosen
   plan + alternatives, research completion sim-times, hull/part grants over time, damage by weapon × victim hull,
   miners/ore/payday, station kills **and losses** per side. No per-tick logging, no wire/protocol change. Update
   `tools/p999-civ-strategy/audit.py` (or a successor) to read them.
5. With the flag ON in this task, only the profile is built and the choice is logged — **no behaviour change yet** (P1001
   acts on it). Prove ON and OFF play byte-identically in a seeded loopback showcase apart from the new log lines.

## Tests
Unit: profile for every civ in PCore001 builds and validates; deterministic choice per seed; different opponents can
change the counter path; no civ-ID branching (a renamed/modded civ in a test core gets a profile). Loopback: flag OFF
pin; flag ON seeded showcase identical behaviour + receipts present. All 3× alone.

## Out of scope
Changing research order, funding, fleet mix or loadouts (P1001); core/plan data; P998/P996 areas; rig runs.

## Gate
Light gate: build 0/0 + `tools/build-tools.sh`; new tests 3× alone; **one** full fast suite (`tools/test-tiers/runner.py
--tier fast`, ≥ 90 min bound); showcase never-parks 5× (flag off and on) vs the base rate. Non-task failures: 3× alone +
`docs/TEST_FLAKES.md`.

## Report
`docs/reports/CIV_STRATEGY_PROFILE_P1000.md`: the profile schema, the scoring, the chosen portfolios for every PCore001 civ
(table vs today's generated plan: first 40 steps, time to key spikes), the receipts list with samples, test counts
(Passed/Failed/Skipped), gate. Footer: "Native intent" and "Modern approach".
