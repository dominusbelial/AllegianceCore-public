# Browser "Watch": the desktop director camera over a browser battle — P1067

Started **2026-11-21 04:51**, lane local time (America/Bogota, UTC−5). Finished **2026-11-21 05:58** (final gate at
the end of this report). Four-hour cap: **2026-11-21 08:51**. No commits, pushes, installs or public-host access.

Scope: the small first slice of P1063 gap row F1 ("Watch without flying", task D item 4) — a **parked director
camera** for a pilot who has launched. A ship-less observer seat is a server rule with no native counterpart and is
out of scope.

| Brief item | Result |
|---|---|
| 1 — snapshot from the browser world | Done: browser `DirectorSnapshot` from the P1065 sector index + the stricter `CanDrawInSector` gate, desktop kinds/weights/cap/re-acquire, 0 bytes/frame |
| 2 — Watch control | Done: `#flight-watch` toolbar, desktop F2 `Director` action, cancel-on-flight-input, `WATCH` HUD tag in Classic and Modern, dev-only deterministic seed |
| 3 — docked / pod / dead / no subjects | Done and tested: docked and dead end Watch, a pod keeps it, no subjects falls back to the own ship |
| 4 — proof | Unit tests and a managed seam proof done; host run precondition proven; **browser sampling and captures are for Claude — this lane has no Chromium** |

## Item 1 — the snapshot from the browser world

The shared pure planner `src/Allegiance.ClientCore.Game/Rendering/Director.cs` is already compiled into the browser
assembly (the browser csproj compiles `ClientCore.Game/**/*.cs` except `Game1*.cs`, and `Game1.Director.cs` is a
`Game1*` file). Nothing called it. P1067 adds the browser glue as a **browser copy**, not a shared move:

- `src/Allegiance.ClientCore.Game.Browser/DirectorWatch.cs:106-190` — `BuildSnapshot`.
  It mirrors the desktop `Game1.Director.cs:376-458` `BuildDirectorSnapshot` field-for-field:
  - own sector + docked flag from the own `ShipEntity` (`:384-391`);
  - pass 1: drawable **opposing-side** ships in the sector become engagement partners (`:394-401`);
  - pass 2: one `Ship` subject per drawable non-own ship with interest `20 + (1 − hull) × 60`, plus one
    `Engagement` subject at `0.5 × (enemy + ship)` with interest `90 + (1 − hull) × 10` for every partner within
    **1,500 units** (`:402-419`);
  - explosions, recency-scored `max(40, 110 − age × 20)` (`:421-429`), alephs (30, `:431-437`), stations (35,
    `:439-445`), then the **24-subject cap** (`:447-449`);
  - `SectorChanged = sector ≠ last ∨ docked ≠ last` (`:452-457`), the planner's re-acquire trigger.
  - Desktop parity quirk kept deliberately: a ship pairs with *itself* when it is an opposing ship (distance 0 <
    1,500), so the pool also carries an `Engagement` at the enemy's own position. A unit test pins this so a future
    change is a decision, not drift.
- Roster source: the P1065 sector index `ClientWorld.ShipsInSector` (`src/Allegiance.ClientCore/State/BrowserSectorShips.cs:7`,
  built for `BattleShipScene.cs:23-35`) and the **stricter** gate `ShipEntity.CanDrawInSector`
  (`src/Allegiance.ClientCore/State/ShipEntity.cs:71-77`: not dead/gone, not unknown, not docked state, not a
  parented gunner, pose sector **and** strategic sector equal to the viewed sector, a pose present). The scene's own
  docked exclusion (`StationId ≥ 0`) is applied too, so the snapshot roster equals the hull/contact roster
  (`DirectorWatch.cs:196-197`). The desktop keeps its `IsNotVisible` + all-ships scan untouched.
- Why a copy, not a move: the desktop scans `_session.World.Ships` under `_session.Gate` with `IsNotVisible`; the
  browser reads a wire-maintained sector index with `CanDrawInSector`. Sharing would force one of the two gates to
  change — a desktop behaviour change. The browser copy cites the desktop lines it mirrors.
- No per-frame allocations: the subject and opposing-ship lists are instance fields that are cleared and reused
  (`DirectorWatch.cs:54-56`); the desktop allocates two lists per frame. The `DirectorSnapshot` is a readonly record
  struct and `DirectorSubject` a readonly record struct.

## Item 2 — Watch control

- Toolbar: `src/Allegiance.ClientCore.Browser/Pages/Menu.razor:105` adds `#flight-watch` next to `#flight-contact`;
  it reads "Watch" / "Stop watching" from `Game1.Watching`. Button handler `Menu.Online.cs:225-226`.
- Game glue: `src/Allegiance.ClientCore.Game.Browser/Game1.Flight.cs:39-65` — `Watch`, `Watching`, `ToggleWatch`,
  `ConfigureWatchSeed`. Toggling Watch also leaves the fly-by contact view (`_cameraMode = Chase`) because the
  director owns the camera; toggling the contact view cancels Watch (`:38`), the desktop's "choosing a manual camera
  cancels the director" rule (`Game1.Director.cs:469-485`).
- Camera: `Game1.Flight.cs:404-408` replaces the chase matrices with `UpdateWatchPose` (`:378-385`), which mirrors
  `Game1.Director.cs:363-374`: the snapshot, then `Director.Update`. The director's gentle free-orientation drift
  (`RotateRates(…, 0.04, 0.03, 0)`, desktop `:366-368`) lives in `DirectorWatch.Update`
  (`DirectorWatch.cs:235-244`) on a **private copy** of the own-ship attitude — the desktop aliases the live pose
  there, and `Orientation` is a mutable class, so a test pins that the ship's attitude is never altered.
  Framing inputs: the own hull length from `BattleShipScene.Hull`, own radius
  `0.5 × length`, aspect from the viewport; the shared planner keeps the desktop FOV/near/far
  (`ExternalCameras.DefaultFovDegrees/NearPlane/FarPlane`, i.e. the same 50°/10/10000 the browser chase camera uses).
- **F2 routes** (`Game1.Flight.cs:279`, `Edge("Director")`): `wwwroot/flight-input.js:58-66` forwards every held
  `event.code` through `FlightKeys`, `ParseKey` maps `"F2"` to `Keys.F2` (`Game1.Flight.cs:222-229`) and the action
  map's `Director` default is F2 (`Rendering/InputActionCatalog.cs:363`), the same action the desktop reads
  (`Game1.Director.cs:516`). No browser default claims F2, and the keydown listener runs on `window` in the capture
  phase, so the key is not lost. The P1045 input table (`docs/reports/BROWSER_CLIENT_P1045.md:256-265`) records the
  browser conflicts — F5 reload, F11 fullscreen, Esc, Ctrl+W/R/L and the tab/arrow/Space defaults — and **F2 is not
  among them**, so the desktop binding is kept as-is rather than remapped; the toolbar button stays as the
  pointer-driven equivalent. The harness asserts one F2 on/off round trip (`tools/p1067-browser-watch.cjs`).
- Cancel: `Game1.Flight.cs:280-285` calls `DirectorWatch.CancelOnFlightInput`, which is the desktop predicate
  `Director.AnyFlightControl` (`Director.cs:99-102`) over the same eight ship movement actions the desktop reads
  (`Game1.Director.cs:557-571`), resolved through the live action map so a rebind cancels too. Firing and throttle
  keys do **not** cancel — exactly the desktop predicate (a unit test pins this).
- HUD: both styles draw the bottom-left camera-mode tag from `HudModel.CameraLabel` (`Rendering/HudRenderer.cs:366-372`,
  inherited by `ModernHudRenderer.cs:123` via `base.Draw`). The browser passes `cameraLabel: "WATCH"`
  (`Game1.Flight.cs:522-523`), so Classic and Modern both show `WATCH` in the same place they show `CHASE`.
  Divergence, stated: the desktop director instead hides the whole flight HUD (`Game1.Rendering.cs:3620`,
  `hidden: _hideHud || _directorActive || _hudHidden`). The browser keeps its instruments because the watching pilot
  is still flying the ship (parked, not an observer seat) and needs state and the cancel affordance; the tag is the
  desktop's own way of naming the active camera: its capture camera uses the same override
  (`_captureSpec is not null ? "OVERVIEW" : null`, `Game1.Rendering.cs:3606`).
- Seed: `NextWatchSeed()` = dev override or `unchecked((int)DateTime.Now.Ticks ^ 0x5EED)` (`Game1.Flight.cs:65`),
  the desktop construction seed (`Game1.Director.cs:56,68`). The desktop's `ALLEGIANCE_DIRECTOR_SEED` has no WASM
  equivalent (no process environment), so the dev-only **`?dev=1&director-seed=N`** query is its equivalent
  (`Pages/BrowserEntry.cs:61-79`, wired in `Menu.razor.cs:72`); without `dev=1` it is ignored. Tests seed the model
  directly and replay shot-for-shot.

## Item 3 — docked, pod, dead, no subjects

Rules implemented and tested:

| State | Rule | Basis |
|---|---|---|
| Docked | Watch is unavailable and ends: the Flight toolbar is replaced by the hangar state (`Menu.Online.cs:386` flips Flight→Hangar when `StationId ≥ 0`), and `UpdateOnlineFlight` stops Watch the first frame `Flying` is false (`Game1.Flight.cs:266-273`). | The browser draws the hangar screen while docked (`Game1.Flight.cs:392-398`), so there is no 3D scene to film; the desktop's "film the station while docked" rule (`Director.cs:186-193`) cannot apply. |
| Ejected (lifepod) | Watch keeps working: a pod is still a flying ship (`Flying`, `Game1.Flight.cs:133-134`), the camera frames other ships, and the own pod is only the chase fallback. | Desktop has no pod rule; the director films from wherever the own ship is. |
| Dead | Watch ends. `UpdateOnlineFlight` stops it when `own.IsDead` (`Game1.Flight.cs:266-273`). | Least surprising: no b-roll of a death view; the browser has no dedicated death camera. |
| Sector has no other subjects | The planner picks subject −1 and frames the own (parked) ship with the generic external fallback; the shot cycle keeps advancing. | Desktop `Director.PickSubject` returns −1 when the pool is empty (`Director.cs:206-209`) and the fallback branches frame the own ship (`:275,285,298,319`). |

The docked *snapshot* rule itself is still implemented and unit-tested (`DirectorSnapshot.Docked`, station-best
pick in `Director.cs:186-193`) so the model matches the desktop if a future slice ever films from a hangar.

## Item 4 — proof

### Unit tests

`tests/Allegiance.ClientCore.Game.Browser.Tests/P1067WatchTests.cs` (30 cases, run ×3 alone below):

| Test | What it pins |
|---|---|
| `Snapshot_frames_visible_sector_ships_with_desktop_interest_and_engagement_midpoints` | own ship never a subject; ship interest `20+(1−hull)·60`; engagement at the 1,500-unit midpoint with `90+(1−hull)·10`; the deliberate self-pair |
| `Snapshot_engagement_needs_the_desktop_1500_unit_range` | 1,800-unit gap adds no midpoint |
| `Snapshot_uses_the_stricter_p1065_visibility_gate` (9 fields) | dead / gone / unknown / parented / docked-state / station / other pose sector / other strategic sector / missing pose are all excluded, and the roster equals `BattleShipScene.Gather` |
| `Explosion_subjects_score_recency_the_desktop_way` | recency decay to the 40 floor, other sectors excluded |
| `Station_and_aleph_subjects_are_added_and_the_cap_is_24` | station (35) and aleph (30) kinds, other sectors excluded |
| `Twenty_four_subject_cap_keeps_the_first_candidates` | 30 candidates → exactly 24 |
| `Sector_change_forces_a_re_acquisition_on_that_frame` | first snapshot and a warp raise `SectorChanged` and the planner re-picks on that frame; the old sector's ships disappear |
| `Docked_pilot_films_the_station_and_reports_the_dock_transition` | dock transition flag and `ShotKind.Station` framing the station subject |
| `No_subjects_keeps_filming_the_own_ship_instead_of_stalling` | subject −1, finite pose, the shot cycle still advances |
| `Any_flight_control_cancels_watch_like_the_desktop` (8 actions) | every movement action cancels once; a second frame does not re-report |
| `Fire_and_throttle_keys_do_not_cancel_matching_the_desktop_predicate` | Space / throttle-zero leave Watch running |
| `Same_seed_replays_the_same_shot_sequence` | 120 frames, identical kind/subject/shot-count/eye |
| `Director_drifts_its_own_orientation_and_never_mutates_the_ships` | the free orientation drifts and the own ship's attitude is untouched |
| `Toggle_starts_with_the_seed_stops_and_restarts_cleanly` | toggle/stop/seed bookkeeping |
| `Watch_hud_tag_uses_the_desktop_camera_label_both_styles_draw` | the shared HUD model reports `WATCH` |

`tests/Allegiance.ClientCore.Browser.Tests/P1067WatchEntryTests.cs` (10 cases): the dev-only `director-seed` URL
contract (off without `dev=1`, bad/oversized values ignored).

### Browser run — this lane has no Chromium

Checked first, as the brief requires: **Node 26.12.1 is present; Playwright and @sparticuz/chromium are not
installed anywhere on this container, and no system Chromium/Chrome binary can start** — the bundled Hermes Chromium
(`~/.hermes/tools/chromium-1208`) fails to load (`libglib-2.0.so.0: cannot open shared object file`, plus libnss3,
libatk, libcups, libxkbcommon, libcairo, libpango, libasound), and the container's apt cache holds none of those
libraries while the lane forbids new packages and non-loopback network. No package was installed.

Therefore the brief's browser-run evidence (launch, fly to Ixion, park, press Watch, 60-second samples, frame pacing
vs chase, four JPEG captures) is **for Claude**, using the harness and exact commands in the next section. No browser
metric, frame time, heap number or capture is claimed as measured here, and the report assets directory intentionally
holds no images. This is the same explicit unavailable-browser path P1065 recorded.

What this lane *could* prove about the run's preconditions — and did: the proof host was built, a fresh browser
generation was published from the real traces and artwork, and `p1051-browser-host --bots 5 --seconds 900` reached
`battle.ready`: **size 5**, `targetSector: "Ixion"` one real aleph from home (`ClusterId 1053 → destinationSectorId
1058`), `identityRequired: true`, 11 enrolled accounts and the typed game-password file — exactly the first
assertions `tools/p1067-browser-watch.cjs` makes. Receipt:
[host-readiness.json](BROWSER_DIRECTOR_WATCH_P1067_assets/host-readiness.json). The host, driver, bots, server and
proxy exited (checked: no process left). The browser half still stops at Chromium.

### Exact workstation browser work — for Claude

No npm/NuGet install. Run from the repo root. All endpoints stay on loopback.

```bash
set -euo pipefail
export DOTNET="$HOME/.dotnet/dotnet"
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p1067/nuget-http"
export ALLEGIANCE_ARTWORK_PATH=/srv/corpus/Artwork
export BROWSER_CLIENT_VERSION=2.0.0
NPM_DIR=.scratch/p1051/npm       # existing playwright + @sparticuz/chromium
test -d "$NPM_DIR/node_modules/playwright"
test -d "$NPM_DIR/node_modules/@sparticuz/chromium"

for project in tools/p1051-browser-host tools/allegiance-mcp src/Allegiance.ClientCore.MonoGame \
  src/Allegiance.ClientCore.Browser; do
  "$DOTNET" build "$project" --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false \
    -p:RestoreSources="$HOME/.nuget/packages" -p:NuGetAudit=false
done

mkdir -p .scratch/p1067/traces
python3 - <<'PY'
import gzip, json
from pathlib import Path
traces = json.loads(gzip.decompress(Path('docs/reports/BROWSER_FLIGHT_P1051_assets/desktop-opens.json.gz').read_bytes()))
for name, trace in traces.items():
    target = Path('.scratch/p1067/traces') / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(trace))
PY
scripts/publish-browser-flight.sh .scratch/p1067/browser-generation \
  .scratch/p1067/traces/opens/Classic.json .scratch/p1067/traces/opens/Modern.json \
  .scratch/p1067/traces/flight-opens4/Classic.json .scratch/p1067/traces/flight-opens4/Modern.json \
  .scratch/p1067/traces/flight-miners0/Classic.json .scratch/p1067/traces/flight-miners1/Modern.json

state_dir=$(mktemp -d "$PWD/.scratch/p1067/browser-host-5.XXXXXX")
output_dir="$PWD/docs/reports/BROWSER_DIRECTOR_WATCH_P1067_assets/browser"
"$DOTNET" tools/p1051-browser-host/bin/Debug/net10.0/p1051-browser-host.dll \
  "$ALLEGIANCE_ARTWORK_PATH" .scratch/p1067/browser-generation "$state_dir" \
  --bots 5 --seconds 1200 > "$state_dir/host.log" 2>&1 &
host_pid=$!
cleanup() { touch "$state_dir/stop"; wait "$host_pid" || true; }
trap cleanup EXIT INT TERM
result=0
node tools/p1067-browser-watch.cjs "$NPM_DIR" "$state_dir" "$output_dir" \
  .scratch/p1067/browser-generation 60 || result=$?
cleanup
trap - EXIT INT TERM
exit "$result"
```

The harness uses the same `/play/menu.html?dev=1` entry, passkey registration, typed game password, side/launch
buttons, ordinary waypoint/aleph autopilot and the production session/renderers as P1065. It parks the pilot at
`(0, 550, 0)` in Ixion, proves an F2 round trip, then for each of Classic/Modern × 1920×1080/800×480 measures chase
rAF pacing, clicks `#flight-watch`, samples the 60-second Watch window (250 ms cadence: shot kind, subject index,
subject categories, contacts/meshes/stand-ins, sector, flying), measures Watch rAF pacing, captures a JPEG q80 with
the metrics recorded at that moment, and finally proves both the button and a flight key return to chase. It fails
the run on any pilot loss (`flying` false or sector ≠ Ixion in any sample) — never relabelled — and enforces the
≤ 2 MiB capture set. Output: `browser-watch.json` (samples, pacing, capture table) + four captures + `failure.json`
on a failed run.

Capture slots the harness writes (values are recorded at capture time and are **for Claude**; no image exists on this
lane):

| File | Style / size | Shot kind | Subject category framed | Contacts / meshes |
|---|---|---|---|---|
| `watch-classic-1920x1080.jpg` | Classic 1920×1080 | from `flight.watch.kind` | `flight.watch.categories` | `flight.battle.contacts` / `.meshes` |
| `watch-modern-1920x1080.jpg` | Modern 1920×1080 | " | " | " |
| `watch-classic-800x480.jpg` | Classic 800×480 | " | " | " |
| `watch-modern-800x480.jpg` | Modern 800×480 | " | " | " |

The bottom-left HUD tag in each capture should read `WATCH`; the `chaseVersusWatchPacing` array in
`browser-watch.json` carries the frame-time comparison the brief asks for.

### What Claude must add to this report

Open every capture, describe only what is visible (shot framing, ships/nebula/HUD tag), and paste the measured
`chaseVersusWatchPacing` and per-capture rows from `browser-watch.json`. This lane cannot view images.

### Managed proof produced here

`tools/p1067-watch-proof` (Release JIT, tiering disabled) exercises the real browser snapshot + camera seam over
synthetic wire-built worlds. It makes no Chromium/WASM/GPU claim. Output:
`.scratch/p1067/watch-benchmark.json` (kept in ignored scratch; the numbers are pasted below).

| Ships in sector | Known off-sector | µs per snapshot+update | Bytes allocated per snapshot | Bytes per shared-planner update |
|---:|---:|---:|---:|---:|
| 2 | 0 | 0.502 | **0** | 13.95 |
| 10 | 0 | 3.051 | **0** | 13.95 |
| 20 | 0 | 8.104 | **0** | 13.95 |
| 20 | 500 | 7.899 | **0** | 13.95 |

The snapshot is allocation-free after warm-up (the reused subject/opposing lists). The first build of
`DirectorWatch.BuildSnapshot` allocated 216 B per call because iterating `world.Warps`/`world.Stations` through
`IReadOnlyDictionary` boxes the `Dictionary` enumerator each frame; the browser copy now iterates the concrete
`Dictionary` (with the interface loop kept only as an unreachable fallback) and the measurement is 0. The remaining
~14 B/frame average is the **shared desktop planner**: `Director.BuildTarget`'s `Pass` branch constructs a
`Rendering/InspectorFlyBy` **class** every Pass frame (`InspectorFlyBy.cs:27`, roughly 21 % of frames). That is
pre-existing desktop code and was deliberately not touched; it is a clean follow-up (make it a struct) for the
desktop owner. (An earlier run reported 70 B/frame because the harness itself evaluated `Orientation.Identity` — a
`new Orientation()` per call — inside the measured loop; the harness now uses the director's own orientation copy.)

A simulated 120-second watch at 60 Hz over a 20-ship battle cut **21 shots** (one per 5.71 s — inside the 4–8 s
dwell), cycled all five aerial kinds (FlyBy 1,404 / Target 1,302 / Pass 1,509 / Chase 1,573 / Orbit 1,412 frames),
framed Engagement subjects on 6,167 frames and Ship subjects on 1,033, and the same seed replayed an identical
kind/subject sequence (SHA-256
`322a2ceede3f1b0e25521b1cb1f11284cacce3d9971e49bec3419f10098595f4` for both runs).

## Desktop unchanged

No file under `src/Allegiance.ClientCore.Game/` (or any other desktop project) was edited: `git diff --stat --
src/Allegiance.ClientCore.Game` is empty, and the full solution build compiles the untouched desktop. The shared
`Director.cs` is reused as-is, so no pure move and no HUD zero-delta capture comparison was required (P1065 needed
one only for its filesystem move). The browser-only additions are new files in
`src/Allegiance.ClientCore.Game.Browser/` plus the two browser-app pages.

## Native intent / Modern approach

**Native intent:** native Allegiance has **no director camera**. The port's P580 b-roll is a non-native addition that
recycles the native external camera modes — `cmExternalOrbit` (`WinTrek/WinTrek.cpp:7187-7196`), `cmExternalFlyBy`
(`:7137-7164`), `cmExternalTarget` (`:7099-7111`), `cmExternalStation` (`:7209-7227`) and native `TrackCamera`
(`:6848-6878`) — and the native rule that a manual view choice returns to the cockpit (`:9820-9864`, `:9357-9383`).
Watch keeps the native visibility intent: only current scanner-visible ships in the viewed sector can be framed
(`clintlib/appmsg.cpp:475-488`; `WinTrek/trekigc.cpp:2167-2169`), through the existing shared gate.

**Modern approach:** the browser reuses the already-compiled pure planner rather than porting anything again, builds
the snapshot from the P1065 wire-maintained sector index (proportional to the viewed sector, not to all known ships),
cancels on the same flight predicate as the desktop, names the mode through the shared HUD camera tag in both styles,
and seeds a recording through a dev-only URL so a capture set is reproducible. Visuals follow the browser's existing
renderers; no scene-effect work, server rule or observer seat was added.

## Gate

All checks on the final source; the Fast tier was the last test run (only comments and the report were touched
afterwards).

| Check | Result |
|---|---|
| Solution build (`dotnet build AllegianceCore.slnx`, disabled build servers/node reuse/shared compilation) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **all 150 tool projects built**, 0 warnings / 0 errors (includes the new `tools/p1067-watch-proof`) |
| New/changed tests, alone ×3 — `P1067WatchTests` | **30/0/0 ×3** (68 ms each) |
| New/changed tests, alone ×3 — `P1067WatchEntryTests` | **10/0/0 ×3** (24 ms each) |
| `tests/Allegiance.ClientCore.Game.Browser.Tests` full | **148 passed / 0 failed** |
| `tests/Allegiance.ClientCore.Browser.Tests` full | **49 passed / 0 failed** |
| Lobby suite | **328 passed / 0 failed / 3 optional PostgreSQL skips** (standalone); 325/0/0 inside Fast (Live rows excluded) |
| Final Fast tier (`tools/run-tests.sh --tier fast`, run last) | **15,841 tests, 604.0 s**, **0 failed assertions**, 7 not executed, 9/9 infrastructure checks pass, calibrated limit 11.520 s — **red for timing only**: 10 passing rows over the limit from 9 existing load-sensitive families |
| Each timing method, 3 fresh isolated processes | **27 processes, all passed**, zero skips; durations in [TEST_FLAKES.md](../TEST_FLAKES.md) (new P1067 entry; two families new to the ledger, seven chronic) |
| Desktop zero-delta | `git diff --stat -- src/Allegiance.ClientCore.Game src/Allegiance.ClientCore src/Allegiance.Server` **empty** — no desktop/shared/core/server file touched, so no capture comparison was needed |
| Glossary link check | `tools/check-glossary-links.py` **OK (1,062 paths)** |
| Parity CSV | `scripts/parity-scorecard.py docs/parity/p1067-browser-director-watch.csv` **validated 3 rows**, LF only |
| Whitespace / CRLF on every touched file | 0 trailing-space lines, 0 CR |
| Host-readiness run (`p1051-browser-host --bots 5`) | **battle.ready**, size 5, Ixion adjacent (1058), identity required, 11 accounts; receipt linked above |
| Browser sampling, frame pacing, captures | **not run — no Chromium on this lane; for Claude** |

Every process started here has exited: the proof host, its MCP driver/workers, the server, the TLS proxy and all
test processes were stopped by exact PID or by the host's stop file, and `ps` shows none left. `.scratch/p1067/`
holds the ignored logs/TRX (3.5 MB; the 534 MB published generation used for the host-readiness run was deleted);
`docs/reports/BROWSER_DIRECTOR_WATCH_P1067_assets/` holds only the 934-byte `host-readiness.json` (no images,
because none could be produced).

**Unfinished:** the browser sampling, chase-vs-Watch frame pacing and the four Classic/Modern captures — this lane has
no Chromium (missing system libraries, no package installs allowed). Everything needed to finish them is in place:
`tools/p1067-browser-watch.cjs`, the exact commands above, and a host that already reaches `battle.ready` with
`--bots 5`. Claude runs it, opens the captures, and appends the metrics and visual descriptions.

Nothing else is open: items 1–3 are implemented and tested, and item 4's managed proof and host precondition are
complete.


## Workstation browser results — Claude

Run 2026-11-21 on the workstation from the merged review branch (P1066 + P1067), headless Chromium via the P1051
Playwright modules, `p1051-browser-host --bots 5`, dev seed 1067, **20 s per window** (chase, then Watch, per case).
[Summary JSON](BROWSER_DIRECTOR_WATCH_P1067_assets/browser/browser-watch-summary.json).

**Harness fix in review:** the first run timed out at "F2 starts Watch". `keyboard.press('F2')` released the key before
the next game frame, so the per-frame edge (`Edge("Director")`) never saw it held; every other press in the harness
already held 100 ms. The F2 presses now hold 100 ms; no game change. The 20 s window keeps the four cases inside the
≈3 minutes a parked pilot survives in this battle (P1066 lost the pilot twice at 60 s); a pilot loss would still fail
the run.

| Case | Chase p50 / p95 ms | Watch p50 / p95 / p99 ms | Watch frames | Flight key cancels |
|---|---|---|---|---|
| Classic 1920×1080 | 16.7 / 16.8 | 16.7 / 16.8 / 16.8 | 1,212 | yes |
| Modern 1920×1080 | 16.7 / 16.7 | 16.7 / 16.8 / 16.8 | 1,212 | yes |
| Classic 800×480 | 16.7 / 16.8 | 16.7 / 16.7 / 16.8 | 1,195 | yes |
| Modern 800×480 | 16.7 / 16.8 | 16.7 / 16.8 / 16.8 | 1,195 | yes |

Headless pacing is capped at 60 Hz (not a real-GPU cost). Across 315 samples the planner used all five shot kinds
(Chase 118, Pass 92, Target 56, Orbit 40, FlyBy 9 samples) and framed Ship, Engagement, Warp and Explosion subjects;
the pilot stayed flying in Ixion (1058) throughout. Every capture had contacts = meshes, 0 stand-ins.

**Captures** (opened and checked by Claude):
- `watch-classic-1920x1080.jpg` (Orbit, subjects Ship 3 / Warp 1): a wide shot — the own ship small at the bottom with a
  bright four-point flare and its label, side-lit rocks around, one enemy label at the right edge; `WATCH` tag
  bottom-left, the toolbar shows "Stop watching".
- `watch-modern-1920x1080.jpg` (Orbit, Ship 4 / Engagement 2 / Warp 2): the camera is very close to a large textured
  hull (blue panels with green detail) that fills the centre and is cut off at its edges, with the own pilot label
  across it; Modern gauges, `WATCH` tag. **Follow-up:** this close orbit passes through or near a hull; the desktop
  director's framing/near-plane behaviour should be compared before calling it intended.
- `watch-classic-800x480.jpg` (FlyBy, Ship 3 / Explosion 10 / Warp 3): rock field with three enemy labels and the own
  label; Classic gauges; `WATCH`.
- `watch-modern-800x480.jpg` (FlyBy, Ship 5 / Engagement 4 / Explosion 12 / Warp 3): a busy field with enemy labels and
  ranges, target bracket, blue/yellow contact markers; Modern gauges; `WATCH`.
