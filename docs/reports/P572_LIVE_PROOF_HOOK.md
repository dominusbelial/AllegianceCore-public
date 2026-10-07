# P572 — make REAL-WINDOW visual proof scriptable

## Summary

Made the required real-client-window proofs unattended and one-command. A normal
WINDOWED MonoGame client now accepts `--auto-*` automation flags that seat it,
ready it, launch it, fly it to a sector, frame a target, take real-backbuffer
2560×1440 screenshots (valid PNGs, via `SaveAsPng` — not the P566 bad-CRC
encoder), dock and photograph hangar hull previews, and exit — all off by
default and client-only (nothing on the wire beyond what a player sends). A new
`scripts/live-proof.sh` boots a local server (221 map `m2xspiderw.igc`,
testUnlock, optional adminCheats), optionally spawns MCP combat bots
(`spawn_scenario` with `arena`), launches the windowed client, collects the
PNGs and shuts everything down by exact PID.

This closes the gap P566 reported: real-window screenshots previously "needed
the client to side-seat interactively, which is capture-mode-only here and not
automatable headlessly."

## What was added

### Client automation flags (`src/Allegiance.ClientCore.Game/LiveProof/`)
- `AutoProofPlan.cs` — pure flag parser. Flags: `--auto-side <id>`,
  `--auto-ready`, `--auto-launch [hull]`, `--auto-goto <sector>`,
  `--auto-target <kind>` (`enemy|friendly|station|aleph`), `--camera
  chase|cockpit`, `--hide-hud`, `--autoshots <t1,t2,...>`,
  `--autoshot-dir <dir>`, `--auto-dock-after <s>`, `--dock-sweep <N>`,
  `--exit-after <s>`. All off by default (`AnyActive` is false for a bare
  plan, so an unflagged launch is unchanged).
- `AutoProofMachine.cs` — pure state machine that sequences
  side → ready → launch → goto → shots → dock-sweep → exit. It owns only the
  timing/phase decisions and emits one-shot `AutoAction`s; the client drains
  them each frame (`ConsumeDue` computes before draining so actions become due
  on the same frame that fed the event).
- `Game1.AutoProof.cs` — the thin glue: feeds world observations to the
  machine, and performs each action (request side, send ready, buy loadout,
  issue the P541 chat-goto buoy, engage the client's own autopilot for
  goto/target/head-to-dock, schedule autoshots, step the dock sweep, exit).
  Applied into `UpdateSideSelect`, `UpdateReadyCheck`, `HandleDockedScreen`,
  `UpdateFlying` and the Draw-end hook.
- `--hide-hud` gates the HUD draw; autoshots dismiss the P570 briefing first
  (mirroring `ALLEGIANCE_AUTOSHOT`) and write valid PNGs named
  `proof_<t>|<sweepNN>_s<SECTOR>_c<camera>.png` via `SaveAsPng`.
- Parsing wired into `src/Allegiance.ClientCore.MonoGame/Program.cs`;
  `LaunchOptions.AutoProof` carries the plan.

### Scripts
- `scripts/live-proof.sh` — one command (boot server → optional MCP bots →
  windowed client with the flags → collect PNGs → shut down by exact PID).
  `--client-extra` passes arbitrary client flags (e.g.
  `--auto-goto Alderan --auto-target enemy`).
- `scripts/mcp_driver.py` — self-contained copy of the proven MCP stdio driver
  (no dependence on a machine-local path), so `--bots` works anywhere.

### Docs
- `docs/LIVE_PROOF.md` — the recipes (own ship chase view, dogfight with bots,
  aleph at range, home station, hangar sweep, soft on/off, `--ships
  native|meshy`). The P564+ prompt footers already point at this file; creating
  it fulfils the pointer.
- `docs/reports/P572_LIVE_PROOF_HOOK.md` — this report.

## Unit tests (`tests/Allegiance.ClientCore.MonoGame.Tests/P572AutoProofTests.cs`)

12 tests, all pure/headless: full flag-set parsing, `--flag=value` forms,
inert-default plan, malformed-value fallbacks, the comma-list sort/dedupe/
malformed-skip, the machine's deterministic initial actions, the full
side→ready→(launch)→goto→target→shots→exit sequence, one-shot latches over many
ticks, docked auto-launch + sweep kick-off, the 1.5 s sweep pacing to
completion, the auto-dock deadline, and the launch clock.

Pass on the final code: **12/12**.

## Real-window proofs (all REAL 2560×1440 presented-backbuffer PNGs)

Every PNG below was captured from the real windowed client via the P572
autoshot path (`GetBackBufferData` before `Present`, written with `SaveAsPng`);
each is a valid image that opens in a normal viewer (verified with `file` —
`PNG image data, 2560 x 1440, 8-bit/color RGBA`). Assets:
`docs/reports/P572_LIVE_PROOF_HOOK_assets/{a,b,c,d}_*`.

**(a) Own ship in chase view (native ships)** — `a_chase/`
`--chase` (camera), `--auto-side 0 --auto-ready`, autoshots at 8 s and 12 s.
Frames show the own native scout in third-person chase view with engine glow,
starfield and HUD (clean, no briefing box):
`proof_t008.0_sOmega_cchase.png`, `proof_t012.4_sOmega_cchase.png`.

**(b) 5v5 dogfight frame with bots** — `b_dogfight/`
`--bots 'spawn_scenario {"size":5,"target_sector":"Omega","arena":true}'`
placed 5 real combat bots per side, mirrored about the Omega centre; the MCP
driver reports `arena: 5 bots/side` and `"spawned":5` on both sides, and the
client (`--auto-target enemy`) acquired an enemy and engaged (laser fire,
target bracket on a bot, muzzle flash):
`proof_t030.4_sOmega_cchase.png` (target bracket on a distant bot),
`proof_t040.7_sOmega_cchase.png`, `proof_t050.9_sOmega_cchase.png` (firing).
Honest note: headless framing of a tight multi-ship brawl is timing-sensitive —
the client acquires and fires at arena bots, but a single frame may show one
locked enemy rather than every ship in the pack.

**(c) The Rigel aleph at range** — `c_aleph/`
`--client-extra '--auto-goto Rigel --auto-target aleph'` engaged the P541
cross-sector goto (logged `autopilot ON → Rigel (Cluster#1059)`) and flew the
client at a warp funnel. Honest note: `--auto-target` fires once at launch, so
it locked the NEAREST aleph (Omega's) rather than Rigel's; the travel also
crossed Alderan (frames `sAlderan`). The close-approach frame catches the P562
double-horn aleph funnel filling the frame:
`proof_t086.4_sOmega_cchase.png` (large swirling funnel dead ahead),
`proof_t076.1_sAlderan_cchase.png` (asteroid field en route), plus
`proof_t040.1_sOmega_cchase.png`, `proof_t052.5_sOmega_cchase.png`,
`proof_t064.8_sAlderan_cchase.png`.

**(d) Hangar preview sweep of 6 hulls** — `d_hangar/`
`--client-extra '--auto-dock-after 12 --dock-sweep 6'` — the client flew home,
docked (server `Sector.ResolveDockCollisions`), and the machine cycled the
hangar shop through 6 distinct hull previews (log: hulls 93→120→123→125→126),
photographing each (`STATION DOCKED - LOADOUT` + ship preview):
`proof_sweep00..05_sOmega_cchase.png`. The three `proof_t00*` frames are the
in-flight autoshots before docking.

`file` check over the whole asset set: every one of the **20** PNGs is a valid
2560×1440 RGBA image.

## Verification

- Whole-solution build: **0 warnings, 0 errors** (`dotnet build -m:8`).
- P572 unit tests: **12/12** passed (included in the MonoGame 1369/1369 below).
- Full 3× suite on the final code, executed with `run-tests.sh`'s P471 discipline
  (build once, then the four test assemblies run SEQUENTIALLY with `--no-build`
  so only one assembly's live network ticks at a time). Each run reported as it
  happened (per `docs/TEST_FLAKES.md`; every failure below is one of the
  documented **live-wire load-flakes** — `SessionLossLive`,
  `FullSessionKillIntegration`, `LiveChatSectorWire`, `GameOverPlayersWire`,
  `AddPartTests` — none touched by this client-only change; they fluctuate run
  to run and the ClientCore flake was green in run 3):
  - Run 1 — Lobby **12/12**, MonoGame **1369/1369**, ClientCore **544/545**, Core **3762/3766**
  - Run 2 — Lobby **12/12**, MonoGame **1369/1369**, ClientCore **544/545**, Core **3764/3766**
  - Run 3 — Lobby **12/12**, MonoGame **1369/1369**, ClientCore **545/545**, Core **3761/3766**
- All live proofs were run on the real workstation (X/Wayland display present),
  foreground, one at a time; server + client + MCP driver cleaned up by exact
  PID via the script's `trap`.
- Committed on **master**, not pushed (`git branch --show-current` = master).

## Testing-failing-first

The P572 tests are new-feature tests (the automation flags did not exist
before this change), so there was no prior behavior to fail-first against; the
machine's timing was corrected during development (actions now become due on
the same frame that fed the event, and the dock-sweep pacing no longer depends
on the launch clock) — the suite pins those exact guarantees.
