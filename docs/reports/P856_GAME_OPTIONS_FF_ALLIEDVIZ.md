# P856 — friendly fire and allied visualization become real game options

Base: **public master at dispatch** (`1e4c33d4`), worktree `allegiance-pi-p856`.
Changes are left in the working tree (no commit, no push). No remote host was
contacted; the native reference (`~/Development/Allegiance/src/`) was read-only
and every line number below was re-grepped this session.

Rows promoted: **SIM-0818** (`friendly_fire_true_policy`) and **SIM-0819**
(`allied_visualization_true_policy`), both `reachability=test-only` →
`production`.

## 1. Result

Both native create-game options are now selectable the same way P832a made goals
selectable, they reach the simulation, they ride the wire the native client reads,
and they appear where the port shows game settings:

| Layer | Friendly fire (`bAllowFriendlyFire`, `Igc/igc.h:1149`) | Allied visualization (`bAllowAlliedViz`, `Igc/igc.h:1127`) |
|---|---|---|
| Per-game config | `games[].allowFriendlyFire` | `games[].allowAlliedViz` |
| Environment | `ALLEGIANCE_GAME_ALLOW_FRIENDLY_FIRE` | `ALLEGIANCE_GAME_ALLOW_ALLIED_VIZ` |
| CLI (overrides env, which overrides JSON) | `--friendly-fire 0\|1` | `--allied-viz 0\|1` |
| Skirmish (in-process, remembered) | `SkirmishOptions.AllowFriendlyFire`, setup row “Friendly fire” | `SkirmishOptions.AllowAlliedViz`, setup row “Allied visibility” |
| Mission model | `Mission.AllowFriendlyFire` | `Mission.AllowAlliedViz` |
| Mission info / wire | `MISSIONDEF(38)` `MissionParams` bit **28** (params byte 111) | `MISSIONDEF(38)` `MissionParams` bit **6** (params byte 108) |
| Client snapshot | `ClientWorld.MissionAllowFriendlyFire` | `ClientWorld.MissionAllowAlliedViz` |
| Lobby directory / GAME DETAILS text | `allowFriendlyFire` → “FF FIRE: Yes/No” | `allowAlliedViz` → “ALLY VIZ: Yes/No” |
| Native log line | `[boot] game '<name>' options: Allow Friendly Fire: yes/no, Allow Allied Viz: yes/no` (`Igc/missionigc.cpp:4116` precedent) | same line |

**Defaults are native's.** `MissionParams::Reset` zeroes the struct and never
assigns `bAllowFriendlyFire` (`Igc/igc.h:1246`), and sets `bAllowAlliedViz = false`
explicitly (`Igc/igc.h:1243`). A config that omits both therefore runs with
friendly fire off and allied visualization off — exactly the pre-P856 behaviour,
so no existing server config changes meaning.

## 2. Native consumers, re-grepped, and what the port does with each

The full machine-readable inventory (native site → port site → disposition →
whether P856 changed it) is `docs/reports/P856_assets/native-consumers.json`.
Greps: `bAllowFriendlyFire|bAllowAlliedViz|AllowFriendlyFire|AllowAlliedViz`,
`GetAllowFriendlyFire`, `AlliedSides`, `SetAllowAlliedViz`, plus the two call
sites of `RevealAlliedClusters`/`RevealMap`.

### 2.1 Friendly fire — implemented, on and off

| Native | Rule | Port |
|---|---|---|
| `Igc/shipIGC.cpp:1222-1228` | `launcher && !bAllowFriendlyFire && (same side \|\| AlliedSides) && amount >= 0` → `c_drNoDamage` | `Sim/BlastDamage.cs` `Suppressed`, used by `Ship.ReceiveDamage` |
| `Igc/shipIGC.cpp:1166-1178` | collision damage passes the other ship as launcher, so ramming obeys the same gate | `Sector` collision passes → `Ship.ReceiveDamage` (P778a already proved the FF-off push is damage-free) |
| `Igc/stationIGC.cpp:126-133` | same gate for stations, `AlliedSides` form | `Station.ReceiveDamage` |
| `Igc/missileigc.h:86-94` | same gate for missiles | `Missile.ReceiveDamage` |
| `Igc/probeigc.h:74-89` | gate exempts `c_eabmRescueAny`, then returns `c_drNoDamage` **only for an armed probe** (`GetProjectileType() != NULL`) | **fixed here** in `Probe.ReceiveDamage` |
| `Igc/igc.h:4297-4305` | `AlliedSides`: same side, or both sides carry the same non-`NA` alliance id | **fixed here** in `Mission.AreSidesFriendly` |
| `FedSrv/FedSrv.CPP:13096-13105` | dock-capture of an *allied* station is allowed when friendly fire is on | **not applicable**: the port's `DockWithStationEvent` is friendly-only by documented scope (P760); there is no hostile capture-on-dock branch to gate |
| `WinTrek/newgamescreen.cpp:126,347,968`; `AGC/AGCGameParameters.cpp:1931-1950` | checkbox, “Allow Friendly Fire: YesNo” in Game details, `MISSIONPARAMS` write | server config + skirmish row + `MISSIONDEF` bit + GAME DETAILS line (the port has no create-game wizard, P722; no AGC) |
| `Igc/missionigc.cpp:4116` | `debugf("Allow Friendly Fire: %s")` | boot log line in `AssembleGameMission` |

Two real consumer gaps were found and fixed (both proven fail-first, §4):

1. **Armed probes had no gate at all.** P100/P491 had deliberately omitted native’s
   armed-probe exception, so every friendly probe took friendly fire. Native
   protects an armed probe but keeps sensor probes damageable (“Always damage
   probes … Only damage ‘sensor’ probes”, `Igc/probeigc.h:80-88`) and exempts
   `c_eabmRescueAny` before the armed test (`:77`). `Probe.ReceiveDamage` now
   implements that predicate in native’s order and position (before the damage
   matrix), which also fixes splash damage to friendly probes.
2. **The allied leg of every friendly-fire gate was dead in production.**
   `AlliedSidePairs` is only ever filled by tests; the server’s `UpdateAlliances`
   (`FedSrv/fsmission.cpp:5638-5811`, port P190) writes `Side.AllyGroup`. So with a
   real alliance, `AreSidesFriendly` returned false and allied ships/structures
   took full friendly damage even with friendly fire off. It now reads the live
   group (`AlliedSides` semantics) with the explicit pair set kept as the
   host/test override, and `Probe.IsFriendlySide` no longer keeps a second copy
   of the rule.

### 2.2 Allied visualization — implemented, on and off

| Native | Rule | Port |
|---|---|---|
| `Igc/probeigc.cpp:448-463` | an armed probe’s first shot reveals it to sides with scanners and to allied sides without one when the option is on | already ported (`Probe.RevealOnFiring`); now resolves production alliances through the fixed `AreSidesFriendly` |
| `FedSrv/FedSrv.CPP:10893-10895` | `UpdateSideVisibility`: an allied side sees the model (visible + current-eye) with no scanner | **added**: allied instance leg in `Sector.ResolveCrossSideVisibility` |
| `FedSrv/FedSrv.CPP:10989-11000` | a contact one ally’s scanner spotted is shared with that side’s other allies, same last spotter (`pLastSpotter(s)`, `CurrentEyed(true)`) | **added**: scan-sharing pass (one pass is enough — alliance groups are equivalence classes) |
| `Igc/mapmakerigc.cpp:473-474,1020-1058` | `RevealAlliedClusters` at map creation: every model of a cluster where a side owns a station is revealed to its allies; warps explicitly excluded | **added**: `Mission.RevealAlliedClusters`, called from `SeedStartingVisibility` in native’s order (home → allied → map) |
| `FedSrv/FedSrv.CPP:10714-10728` | sector change: with allied viz only *non-allied* sides lose contact | **structural**: the port has no hide-on-sector-change state — a model that leaves the sector simply stops being a sweep target |
| `FedSrv/FedSrv.CPP:10845-10872` | `ShowObject`/`HideObject` keep allied ship status flowing / never hide allies | covered by the flags the sweep now writes; the host’s export sweeps read `SeenBySide` |
| `WinTrek/trekigc.cpp:2073-2118`, `teampane.cpp:511`, `sectormap.cpp:961`, `teamscreen.cpp:2335-2337`, `newgamescreen.cpp:1147` | client-local rendering/roster/map checks and the client forcing its **local** `MissionParams` copy on when allies exist | client-side; the port is server-authoritative (visibility arrives as EXPORTs), so there is no local copy to force and no second filter to relax. Stated, not silently dropped |
| `Igc/igc.h:1127,1243` | field + Reset default false | `Mission.AllowAlliedViz` default false |

**One known deviation, left alone deliberately.** `Igc/common.cpp:771,810`
(`FindRipcordModel`, allied-rip branch) lets an allied station/ship be a ripcord
anchor when it is `SeenBySide` **or** allied visualization is on. The port’s
`Server.FindRipcordModel` (P115) already accepts allied anchors under
`bAllowAlliedRip` without a visibility test, so it is *more permissive* than
native when allied viz is off. Wiring the parameter there could only tighten an
existing P115-scoped simplification (its anchor search is already restricted to
the requested sector, documented in `Server.cs`), so it belongs to the
allied-rip owner rather than to this reachability task.

## 3. Changes (file by file)

**Core / model / sim**
- `src/Allegiance.Core/Model/Mission.cs` — `AreSidesFriendly` now implements
  native `IsideIGC::AlliedSides` over live `Side.AllyGroup` plus the explicit
  pair override; new `RevealAlliedClusters` (native order via
  `SeedStartingVisibility`, warps excluded).
- `src/Allegiance.Core/Model/Sector.cs` — the cross-side visibility sweep gains
  native’s two allied legs, gated on `Mission.AllowAlliedViz`; scouting telemetry
  is not credited for visibility that came free through an alliance; the
  obsolete “no alliance concept” note and the “probe has no gate” notes are
  corrected.
- `src/Allegiance.Core/Sim/Probe.cs` — native armed-probe friendly-fire gate
  (`Igc/probeigc.h:74-89`); `IsFriendlySide` delegates to the single shared rule.
- `src/Allegiance.Core/Wire/MissionDef.cs` — the 36-bit `MissionParams` bool:1
  block is now encoded/decoded through one bit helper: `bAllowAlliedViz` bit 6,
  `bLockGameOpen` bit 18 (existing), `bAllowFriendlyFire` bit 28. Golden bytes
  asserted independently of the codec.

**Server / config**
- `src/Allegiance.Server/ServerConfig.cs` — `GameConfig.AllowFriendlyFire` /
  `AllowAlliedViz` (documented native defaults), `ApplyMissionOptionEnvironment`,
  `--friendly-fire` / `--allied-viz` (accept `0/1`, `true/false`, `yes/no`,
  `on/off`; malformed values fail with the switch name; both need a configured
  game). Precedence: CLI → environment → JSON.
- `src/Allegiance.Server/Server.Lifecycle.cs` — the two flags reach the assembled
  mission next to `ShowMap`/`AllowAlliedRip`, plus the native-style option log
  line (also printed on rotation because assembly re-runs).
- `src/Allegiance.Server/Server.Lobby.cs` — both MISSIONDEF sends carry the flags.
- `src/Allegiance.Server/LobbyHeartbeat.cs` — `LobbyGameParams` publishes both
  (optional trailing members, native default off) via
  `FromMission`.
- `src/Allegiance.Server/server.example.json` — the two fields shown at their
  native defaults.

**Lobby / client**
- `src/Allegiance.Lobby/ServerListing.cs`, `src/Allegiance.Lobby/ServerRegistry.cs`
  — optional `allowFriendlyFire`/`allowAlliedViz` on the register payload,
  normalized to native’s off when omitted.
- `src/Allegiance.ClientCore.Game/LobbyGameMetadata.cs` — `GameParams` carries the
  two switches; `GameParams.YesNo` matches native’s `YesNo()` wording.
- `src/Allegiance.ClientCore.Game/ServerListing.cs` — GAME DETAILS lines
  `FF FIRE:` / `ALLY VIZ:` (shared model used by the one drawing path).
- `src/Allegiance.ClientCore/State/MissionInfo.cs`,
  `src/Allegiance.ClientCore/State/ClientWorld.cs` — decode/retain the two options
  from `MISSIONDEF`, expose `MissionAllowFriendlyFire` / `MissionAllowAlliedViz`,
  and keep them across `MISSION_STAGE` refinements.

**Skirmish**
- `src/Allegiance.SinglePlayer/SkirmishOptions.cs` — the two options and their
  remembered-preference fields.
- `src/Allegiance.SinglePlayer/SkirmishScreenModel.cs` — two new setup rows
  (`Friendly fire`, `Allied visibility`); `StartRow`/`BackRow` shift to 12/13.

**Config/deployment**
- `docker-compose.yml` — `ALLEGIANCE_GAME_ALLOW_FRIENDLY_FIRE` /
  `ALLEGIANCE_GAME_ALLOW_ALLIED_VIZ` (default empty so per-game JSON is
  preserved); `docker compose config` validates.
- `docker/README.md` — the two options, their precedences and effects.
- `GLOSSARY.md` — new “Friendly fire / allied visualization options (P856)”
  entry with key files and the native cites.

**Tests / tools**
- `tests/Allegiance.Core.Tests/P856FriendlyFireAlliedVizTests.cs` — 24 cases:
  JSON/env/CLI (defaults, precedence, per-game isolation, bad values), MISSIONDEF
  bit positions + client snapshot, and on/off behaviour for ship, station,
  missile, armed/sensor/RescueAny probe, self-splash, allied damage via live
  alliance groups, allied instance visibility, allied scan sharing, the start
  reveal and the probe firing reveal through live groups.
- `tests/Allegiance.Core.Tests/P856OptionServerTests.cs` — 5 cases: real boot
  from JSON for all four combinations, mission flags, the joining client’s
  decoded options (and after a `MISSION_STAGE`), the lobby projection, and a real
  heartbeat round-trip into an in-process lobby directory.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P856GameDetailsOptionsTests.cs` —
  the GAME DETAILS lines and native `YesNo` wording.
- Updated to the native contract: `P649CombatBlastParityTests` (armed probe
  immunity, allied armed probe, same-side missile probe damage),
  `ProbeCombatLiveTests` (same-side friendly hit: immune with FF off, identical
  damage with it on), `SP6GameSpeedTests` / `P799LivesLimitTests` (menu indices).
- `tools/p856-options-proof/` — real-window proof: a real `Allegiance.Lobby`
  child process with two registered games (options on / native defaults off),
  the production server browser + GAME DETAILS panel in Classic and Modern, and
  the skirmish setup rows in both styles.

## 4. Fail-first evidence for the fixed consumers

The new tests were written first, then run against the pre-P856 versions of
`Model/Mission.cs`, `Model/Sector.cs` and `Sim/Probe.cs` (restored from `HEAD`,
everything else unchanged):

```
Friendly_probe_damage_follows_native_armed_probe_immunity(allow: False, kind: "armed", damaged: False) [FAIL]
Allied_sides_see_each_others_models_only_with_allied_visualization(allow: True, visible: True)       [FAIL]
Allied_scans_are_shared_with_allies_only_with_allied_visualization(allow: True, visible: True)       [FAIL]
Start_reveals_an_allied_home_cluster_only_with_allied_visualization(allow: True, visible: True)      [FAIL]
Allied_damage_obeys_the_option_through_live_alliance_groups(allow: False, damaged: False)            [FAIL]
Allied_damage_obeys_the_option_through_live_alliance_groups(allow: True, damaged: True)              [FAIL]
Failed!  - Failed: 6, Passed: 18, Skipped: 0, Total: 24
```

(with the fixes restored the same 24/24 pass; log:
`docs/reports/P856_assets/pre-fix-new-tests.txt`). The reachability layers
(config, wire bits, boot, lobby, GAME DETAILS) are new behaviour, so their tests
have no pre-P856 “before” state to fail against — they are covered by the live
boot/loopback test above.

## 5. Verification

All commands run in this worktree with `~/.dotnet/dotnet`.

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx --no-incremental` | **0 warnings, 0 errors** |
| `tools/build-tools.sh` | all **91** tool projects built, 0 warnings |
| Rig AOT publish (`Allegiance.Server`, linux-x64, `PublishAot`) | clean native image, **no IL2026 / IL3050** |
| New + affected tests, 3× alone | P856 behaviour 24/24 ×3; P856 server 5/5 ×3; P856 client 2/2 ×3; P649 57/57 ×3; ProbeCombatLive 3/3 ×3; skirmish menus 21/21 ×3 |
| `tools/run-tests.sh --tier fast` (run 1) | 10 993 tests, **1 failure** — `ProbeCombatLiveTests.Same_side_friendly_missile_damages_the_probe_no_friendly_fire_gate` asserted the non-native pre-P856 probe contract; the test was corrected to native’s armed-probe gate and re-proven 3/3 |
| `tools/run-tests.sh --tier fast` (run 2) | 10 993 tests, **0 failures** (3 not-executed pre-existing). The tier’s only error is one calibrated slow-limit: `P750NativeAsteroidTypesTests…` 10.886 s vs the 10 s limit, an already-ledgered load flake (7/7 pass alone in ~1 s, 3/3 runs); ledger row added for this run in `docs/TEST_FLAKES.md` |
| `python3 scripts/parity-scorecard.py` | 840 sim rows validated; SIM-0818/0819 now `production` |
| `docker compose config` | valid |

Sim headline, before → after (production-weighted, the scorecard’s headline):

```
FedSrv mission rules (event-driven)  1.52% (9/592)  →  2.53% (15/592)
TOTAL (sim-rules-matrix.csv)        67.92% (1279/1883) → 68.24% (1285/1883)
unweighted                          418/682 (61.29%) → 420/682 (61.58%)
production implemented weight       1279 → 1285     test-only implemented weight 6 → 0
```

Evidence: `docs/reports/P856_assets/score-before.txt`, `score-after.txt`,
`fast-tier-run1.txt`, `fast-tier-run2.txt`, `targeted-tests-3x.txt`,
`verification.json`. Raw TRX/logs stay in `TestResults/tiers-20261027T120222.002757Z`
and `…T120844.018657Z` (outside the repo’s report assets).

### 5.1 Rows changed (only ours)

`docs/parity/sim-rules-matrix.csv`, rows `SIM-0818` and `SIM-0819` only:

- `native_cite` extended to every consumer re-grepped this session
  (`shipIGC`, `stationIGC`, `missileigc`, `probeigc`, `igc.h`, plus
  `FedSrv.CPP:10893-10895,10989-11000` and `mapmakerigc.cpp` for viz);
- `native_rule` states the real predicate instead of “only enabled by test-side
  setters”;
- `port_cite_or_reason` names the production config path
  (`games[].allowFriendlyFire` / `…allowAlliedViz`, the env vars and CLI switches,
  `ServerConfig` → `Server.Lifecycle` → `Mission`), the consumers and the wire
  bit;
- `evidence` names the P856 tests; `reachability` = `production`;
- `notes` records the two consumer fixes and the one structural deviation.

No other row was touched (other lanes edit their own).

## 6. UI evidence (screenshots opened and described)

Tool: `tools/p856-options-proof` (build the lobby first). Run:
`dotnet run --project tools/p856-options-proof -- docs/reports/P856_assets` and
`… -- docs/reports/P856_assets --skirmish`.

- `game-details-options-on-classic.png` / `…-modern.png` — the server browser with
  a real lobby listing. GAME DETAILS panel reads, top to bottom: GAME Options ON,
  SERVER Options ON, ADDRESS 127.0.0.1:6777, CORE PCore001.igc, VERSION 0.1.0,
  PLAYERS 4/16, TYPE Conquest, STATE running, GOALS conquest, DEVEL on,
  LIVES unlimited, **FF FIRE: Yes**, **ALLY VIZ: Yes**.
- `game-details-options-off-modern.png` — the second registered game (native
  defaults): the same panel ends **FF FIRE: No**, **ALLY VIZ: No**, STATE waiting,
  PLAYERS 0/16.
- `skirmish-setup-options-classic.png` / `…-modern.png` — Single Player ›
  Skirmish setup: between “Lives: Unlimited” and “Bot gunners: On” the two new
  rows read **“Friendly fire: Off”** and **“Allied visibility: Off”**.

Both HUD styles were captured. Honest observation: the server browser/details
panel and the skirmish setup screen are **not repainted by `HudStyle`** — the two
captures are pixel-identical apart from the row selection (they share the
connect-screen typography path). The behaviour lives in the shared models
(`ServerDirectorySearch.DetailsLines`, `SkirmishScreenModel.Rows`), so both
styles show identical text by construction; there is no style-specific artwork
to review for these two lines.

## 7. Native-intent classification

- **Friendly fire** — *faithful*: the predicate, its polarity (`amount >= 0`),
  the self-launch case, the armed/sensor/RescueAny probe split and the
  allied-side leg all match the native bodies; the option now arrives from the
  same kind of source (create-game mission params) and is advertised in the same
  bitfield block. *Structural gaps*: no dock-capture-of-allied-station branch
  (P760 scope) and no AGC/create-game-wizard UI (P722 scope).
- **Allied visualization** — *faithful for the server half*: sweep legs, scan
  sharing, start reveal, probe firing reveal and the wire bit match
  `FedSrv.CPP`/`mapmakerigc.cpp`/`probeigc.cpp`. *Structural gap*: sector-change
  hiding has no counterpart because the port has no hide state. *Deliberate
  divergence*: the native client flips its **local** copy of the parameter on
  whenever an alliance exists and pushes it via `MISSIONPARAMS`
  (`teamscreen.cpp:2335-2337`); the port has no `MISSIONPARAMS` path, so the
  configured server value is authoritative — an operator who wants native’s
  “always on with allies” behaviour sets it in one place.
- **Both options** are read-only inputs to the simulation, so nothing in the
  scoring, kill-credit or victory rules changed shape; only the damage/visibility
  predicates they already consulted.

## 8. Modern / better-approach note

Native’s implementation is a set of scattered `: 1` bitfields read directly from
a global mission-params pointer, plus a client that mutates its own copy of those
bits behind the server’s back. The port keeps one authoritative value per option
on `Mission`, sourced from one documented config path, and broadcasts it in the
mission definition the client already parses; the client exposes it read-only
(`ClientWorld.MissionAllow*`) instead of keeping a second, divergent copy. The
allied-visibility legs are two small, targeted additions to the single
visibility sweep (an instance leg and a scan-sharing pass) rather than a second
visibility system, which keeps the P835 cost profile intact: with the option off
(the default) the sweep does exactly the work it did before, and the flags are
identical. Finally, the codec change reuses one bit-helper and an independent
golden-byte test instead of hardcoded byte/mask constants per field, so the next
native mission-param bit is a one-line addition with the same verification.

## 9. Unfinished / follow-ups

- `Igc/common.cpp:771,810` allied-rip anchor visibility (§2.2) — left to the
  allied-rip (P115) owner; the port is currently more permissive there.
- Native’s client-side re-checks (`trekigc.cpp`, `teampane.cpp`, `sectormap.cpp`)
  are replaced by server exports; if the port ever adds a client-side
  re-filter, it must consult `MissionAllowAlliedViz` the same way.
- The GAME DETAILS/skirmish rows are text-only additions; if a future HUD pass
  gives the connect screens style-specific typography, the two lines need the
  usual Classic/Modern screenshot pair revisited (models already shared).
