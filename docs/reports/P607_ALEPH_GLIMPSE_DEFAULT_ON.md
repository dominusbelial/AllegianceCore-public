# P607 — aleph glimpse / transit-flare ON by default; enemy crossings swell the native star even when off

Date: 2026-10-11 · Branch: `master` · Commit: `c3efcfa1` (not pushed)

## Summary

Domingo (2026-10-11, live 195v195 on 221): "enemy ships are still not producing the
flare when they cross the aleph … **we want that to be on by default**." The transit
flare (funnel boost + destination glimpse + lens flare, P585/P576-B) was opt-in under
`--aleph-glimpse on` (default off), and P599 routed **enemy** arrivals/departures through
`ClientWorld`'s transit-signal queue, which the renderer drained **only when the flag was
on** — so with the flag off an enemy crossing produced nothing, not even the native star
swell. This task:

1. Makes `--aleph-glimpse` **default ON** (`--aleph-glimpse off` disables it).
2. Makes **every legitimate visible transit** (own/team/**enemy**/neutral; arrival &
   departure, per P599's rules) trigger the **native star swell** (`WarpRenderer.TriggerTransit`)
   **even when the flag is off** — only the glimpse/funnel-boost/flare extras stay flag-gated.
3. Fixes the **client crash** when a hull's native art fails and there's no Meshy pack
   (live `NullReferenceException` at `Game1.Rendering.cs:611` during the 195v195 showcase):
   the ship colour pass now falls back to a guaranteed-non-null procedural stand-in,
   and a P593b-style startup audit reports native-resolution health.

The visuals themselves and P599's information-leak rules are unchanged.

## Cause (native references)

- The transit pulse + glimpse are opt-in under `--aleph-glimpse on`, default off
  (`src/Allegiance.ClientCore.MonoGame/Program.cs:61` `bool alephGlimpse = false;`).
  The play client ran without the flag; its log had **0** `[transit-pulse]` lines.
- P599 (`3c70c046`) routed enemy arrivals/departures through `ClientWorld`'s
  transit-signal queue (`SignalAlephTransit`), which `DrainAlephTransitSignals`
  (`Game1.Rendering.cs:2098`) processed **only when `_alephGlimpse` was true**; the
  `else` branch discarded them (`world.DrainAlephTransits()`). So with the flag off,
  enemy crossings produced **nothing** — not even the native star swell.
- **Teammate** crossings show as a sector change in `DetectTransitSwells`
  (`Game1.Rendering.cs:2055`), which calls `_warpRenderer.TriggerTransit` **regardless**
  of the flag. Hence the visible asymmetry: which side crossed decided whether anything
  happened.

## Changes

### 1. `--aleph-glimpse` defaults ON

- `Program.cs`: `bool alephGlimpse = AlephGlimpseFlag.DefaultOn;` (const `true`). Bare
  `--aleph-glimpse` / `--aleph-glimpse=on` → on; `--aleph-glimpse off` → off; garbage →
  warn + default (on). The on/off parse moved to a pure, testable
  `AlephGlimpseFlag.Parse` (`src/Allegiance.ClientCore.Game/AlephGlimpseFlag.cs`).
- `Game1.Session.cs` (`LaunchOptions.AlephGlimpse`) default `false → true`, so the
  Android head (which doesn't set it) also gets on-by-default.
- `Game1.cs` logs the **active** value once at startup:
  `[client] aleph glimpse/transit-flare: ON (--aleph-glimpse off …)`.
- `docs/GUIDE.md` client-flags table updated (`--aleph-glimpse on` / `off`, default **on**).

### 2. Enemy/neutral crossings swell the native star even when off

`DrainAlephTransitSignals` now delegates to a pure, testable
`Game1.DispatchAlephTransitSignals(alephGlimpse, signals, pulses, swell, pulseLog, now)`:

- For **every** drained transit signal, the **native star swell** fires
  (`swell = _warpRenderer.TriggerTransit`) **regardless of the flag** — this is what makes
  an enemy crossing visibly swell even with `--aleph-glimpse off`.
- Only the **glimpse pulse** (`_warpPulses.Trigger` — funnel boost, glimpse open-wide,
  lens flare) is gated by the flag, and only then is the `[transit-pulse]` line logged.
- **No double swell:** `TriggerTransit` is idempotent (same warp, same frame → one
  timeline), and `WarpPulseController` coalesces same-warp bursts within
  `MinRepeatGapSeconds` — P599's coalescing is preserved. Kills/docks never produce
  signals (unchanged, `ClientWorld`).

### 3. Crash fix — no null model in the ship colour pass

**Root-cause finding:** a whole-core audit (`AuditNativeShipResolution`, below) shows all
**447** shop-catalogue hulls resolve their native `.mdl` (exists + decodes to geometry).
So the crash ship was one whose `HullId` is **not in the 447-hull buyable catalogue**
(e.g. a LAYER mine-laying drone or other non-player hull) — for it `ResolveNativeShip`
returns null, and with **no Meshy pack** `_shipModelById[id]` and `_fallbackModel` are
both null, so the content path passed null to `ShipRenderer.Draw`
(`Game1.Rendering.cs:611`) → `NullReferenceException`.

Fix (`Game1.Rendering.cs` colour pass + `DrawDockedHullPreview`):

```
var drawModel = shipModel ?? _fallbackModel;
if (drawModel is null) { drawModel = _shipStandInModel; log once by ship id; }
_shipRenderer.Draw(drawModel, …);
```

`_shipStandInModel` (`Rendering/ShipStandInModel.cs`) is a **procedural elongated
octahedron** (pure geometry via `CreateGeometry()`, wrapped into a MonoGame `Model` +
`BasicEffect` at load time) — independent of the artwork corpus and the Meshy pack, so it
can never be null. The ship stays visible and targetable. Logged once per unresolvable
ship id (`[client] P607 unresolvable ship #N … rendering procedural stand-in`).

**Startup audit** (`AuditNativeShipResolution`, via pure `Rendering/ShipNativeAudit`):
reports `N hull(s), 0 unresolved native` or the per-hull list, mirroring P593b. On this
workstation the real run printed:

```
[monogame] P607 ship-model audit: 447 hull(s), 0 unresolved native
```

The depth pass already guarded null (`PrepareSceneGeometry` skips null models), so only
the colour pass (and the hangar preview) needed the guard.

## Tests (all failing-first)

`tests/Allegiance.ClientCore.MonoGame.Tests/`:
- `SkyLaunchOptionDefaultsTests` — `AlephGlimpse_DefaultsToOn` (fails pre-P607: default
  was false), `AlephGlimpse_CanBeSwitchedOff`, `AlephGlimpseFlag_OnValuesParseOn`,
  `AlephGlimpseFlag_OffParsesToOff`, `AlephGlimpseFlag_AbsentOrGarbageIsNull_DefaultOnAtCaller`.
- `AlephTransitDispatchTests` — flag off: an enemy transit triggers **exactly one** star
  swell and **no** glimpse pulse; two enemy transits swell twice; flag on: enemy+team
  pulse once each (and log once each); same-warp coalesces to one pulse; empty (kills)
  never pulses or swells.
- `ShipStandInAndAuditTests` — stand-in geometry is a well-formed, fully-indexed
  octahedron (so `Build` can't throw); `ShipNativeAudit.CollectUnresolved` lists exactly
  the unresolvable hulls (0 for a healthy core); the fallback chain never yields null.
- `ClientWorldAlephTransitTests` (pre-existing) — kills/docks never signal; enemy
  arrivals/departures signal (P599 rules unchanged).

## Live proof (real window, local server — NOT 221)

Ran on a **temporary worktree** `git worktree add /tmp/p607-proof HEAD` (removed after),
local server `m2xspiderw.igc`, **default flags (no `--aleph-glimpse` passed)**. Observer
= windowed MonoGame client (2560×1440 presented backbuffer, `SaveAsPng` autoshots), side-0,
`--auto-goto Omega --auto-target Aleph --chase`. Enemy (side-1) bots via MCP
`spawn_scenario {"size":15,"target_sector":"Omega","staging":false}` crossed the observed
Omega↔Alderan gate.

- Startup (objective, default ON): `[client] aleph glimpse/transit-flare: ON …`.
- Enemy crossings at the framed gate, default flags (objective log,
  `docs/reports/P607_assets/proof/enemy_transit_pulse_log.txt`):
  ```
  [transit-pulse] warp #2153 ENEMY transit signal (ship 8118) @t=77.52
  [transit-pulse] warp #2153 ENEMY transit signal (ship 8124) @t=77.52
  [transit-pulse] warp #2153 ENEMY transit signal (ship 8110) @t=78.58
  [transit-pulse] warp #2152 ENEMY transit signal (ship 8126) @t=79.97   (… 8122/8100/8108/8114 at 81.5)
  ```
- **Real-window flare frame** `proof11/proof_t078.0_sAlderan_cchase.png` (t=78, DURING
  the `#2153 ENEMY` crossings at 77.52/78.58): the aleph funnel is **massive, brilliant
  white-blue, swollen**, casting glow on the enemy ships clustered at the gate —
  copied as `docs/reports/P607_assets/proof/enemy_flare_t078_burst.png`.
- **Baseline contrast** `proof_t072.0_sOmega_cchase.png` (t=72, just before the burst):
  the same funnel is **dim, small, dark**, with a black centre — the quiescent state —
  copied as `.../enemy_flare_baseline_t072_dim.png`. The brighten→swell swing is the
  P585 transit pulse playing out for **ENEMY** ships, with default flags.
- Close-up funnel frame `proof8_t054.5` copied as `.../transit_funnel_close_t054.png`.

No crash in any live run (the stand-in guard held). Assets in `docs/reports/P607_assets/proof/`.

## Suite (final code, via `scripts/run-tests.sh`, P471)

Two consecutive full-suite runs on the final code (commit `c3efcfa1`):

| Project | Run 1 | Run 2 |
|---|---|---|
| `Allegiance.Lobby.Tests` | 12/12 ✅ | 12/12 ✅ |
| `Allegiance.ClientCore.MonoGame.Tests` (P607 tests here) | 1733/1736 ✅ (3 pre-existing `Requires=Artwork` skips) | 1733/1736 ✅ (3 skips) |
| `Allegiance.ClientCore.Tests` | 593/593 ✅ | 592/593 ❌ (1 flake) |
| `Allegiance.Core.Tests` | 3912/3914 ❌ (2 flakes) | 3913/3914 ❌ (1 flake) |

Every failure is a **known ledgered load-flake** (`docs/TEST_FLAKES.md`), rotating between
runs exactly as P471 documented, and **none is in the P607 tests**:
- Run 1 (Core): `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer`
  (ledger line 432) and `CargoBayReloadTests.SwapPart_out_of_range_negative_mount_is_rejected_cleanly_not_thrown`
  (P472 `CargoBayReloadTests` load-flake family, ledger lines 53-54).
- Run 2 (ClientCore): `ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone`
  (ledger line 51, "live-wire timing / load-flake P472"). (Core):
  `WarpBombTests.Live_whole_mission_warp_bomb_reaches_the_cross_side_peer_and_the_delayed_blast_lands`
  (ledger line 183, "live-wire delayed-blast timing / load-flake").

Per `docs/TEST_FLAKES.md` the flakes are known and ledgered; the whole-solution build was
0 warnings / 0 errors before the suite. Report only reflects numbers actually measured.

## Deliverables & hygiene

- Commit `c3efcfa1` on `master` (not pushed). `git branch --show-current` → `master`.
- Working tree clean (0 tracked modifications; pre-existing untracked docs untouched).
- No local server/client/bot left running; the temporary worktree `/tmp/p607-proof` removed;
  Domingo's live 221 session (clients/MCP) was never touched.
- Shared-environment rules respected: never built/ran from `~/allegiance-play2` or
  `~/allegiance-deploy`; live proof on a local server from a temp worktree.
