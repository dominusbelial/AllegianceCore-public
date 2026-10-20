# P750 — native asteroid types: real models and icons instead of invented "rock_mine"/"rock_build"

## Summary

The port's procedural map generator and research-showcase created asteroids with
invented model names (`"rock_mine"` / `"rock_build"`) and empty icon names. Those
`.mdl` files don't exist in the artwork, so the client's native asteroid renderer
logged `native asteroid model 'rock_build.mdl' unavailable (not present) — falling
back to imported/procedural rock` and painted distant rocks as flat squares.

This task ports the native fixed asteroid-type table (and its `GetRandomType` /
`GetTypeDefaults` selection) into **one shared place** in Core, and uses it for
every asteroid the port creates, so **model, icon, hit points, radius and name
prefix all come from the real native rows**. The client now resolves every
generated rock to a real `bgrndNN.mdl` mesh + native surface — verified in the
real window.

## The native table (transcribed, cited)

Source: `~/Development/Allegiance/src/Igc/asteroidigc.cpp:126-137` (`asteroidTypes[]`),
`AsteroidDef` at `src/Igc/igc.h:1709-1720`, capability constants at `igc.h:761-766`.

| # | name | prefix | aabmCapabilities | hp | radius | model | icon | cite |
|---|------|--------|------------------|----|--------|-------|------|------|
| 0 | asteroid | `\0a` | 0 | 25000 | 400 | bgrnd03 | meteoricon | asteroidigc.cpp:128 |
| 1 | asteroid | `\0a` | 0 | 25000 | 400 | bgrnd05 | meteoricon | asteroidigc.cpp:129 |
| 2 | asteroid | `\0a` | buildable (0x08) | 10000 | 200 | bgrnd03 | meteoricon | asteroidigc.cpp:130 |
| 3 | asteroid | `\0a` | buildable (0x08) | 10000 | 200 | bgrnd05 | meteoricon | asteroidigc.cpp:131 |
| 4 | Helium 3 | He | mineHe3 (0x01) | 25000 | 100 | bgrnd55 | heliumrock | asteroidigc.cpp:132 |
| 5 | Helium 3 | He | mineHe3 (0x01) | 25000 | 100 | bgrnd56 | heliumrock | asteroidigc.cpp:133 |
| 6 | Uranium | U | special<<0 (0x10) | 25000 | 200 | bgrnd51 | hotrock | asteroidigc.cpp:134 |
| 7 | Silicon | Si | special<<1 (0x20) | 25000 | 200 | bgrnd52 | copperrock | asteroidigc.cpp:135 |
| 8 | Carbonaceous | C | special<<2 (0x40) | 25000 | 200 | bgrnd53 | carbonrock | asteroidigc.cpp:136 |

Selection rules:
- `GetRandomType(aabm)` (`asteroidigc.cpp:231-255`): aabm `0` → index 0..1
  (huge pair); `c_aabmBuildable` → 2..3; `c_aabmMineHe3` → 4..5; each via native
  `randomInt(0,1)` inclusive → port `Random.Shared.Next(0,2)`.
- Specials are placed by `GetSpecialAsterioid` (`asteroidigc.cpp:190-229`) →
  `nFirstSpecialType + 0/1/2` = indices 6/7/8. The port resolves a special
  capability mask to its exact row via `ResolveTypeIndex`.
- `CreateAsteroid` (`common.cpp:2844-2893`) pulls every field from
  `GetTypeDefaults(type)` (`asteroidigc.cpp:174-177`); `signature = radius/100`
  (`common.cpp:2866`); ore multiplied by `amountHe3` only when the row's base ore
  is non-zero (`common.cpp:2873-2875`, only the helium rows carry ore 1.0); name
  via `GetAsteroidName` (`common.cpp:2786-2801`) = `prefix + (1024 + ((id+1093)·5237)
  % 8713)`.

## What changed

New file `src/Allegiance.Core/Data/NativeAsteroidTypes.cs` — the single shared
table + `GetTypeDefaults` / `GetTypePrefix` / `GetRandomType` / `ResolveTypeIndex`
/ `AsteroidName`. It transcribes all 9 rows verbatim (models/icons/hp/radius/
ore/prefix) and implements the native selection.

- `src/Allegiance.Core/MapGen/MissionMapGenerator.cs` — `AddAsteroid` (used by
  every map topology: ring/radial/grid/family/Brawl/PinWheel/EastWest…) now
  derives `typeIndex = NativeAsteroidTypes.ResolveTypeIndex(capabilities)`, then
  `GetTypeDefaults`, and writes the real `ModelName`/`TextureName`/`IconName`,
  `HitPoints`, and `Radius = randomInt(def.Radius, 2·def.Radius)` (common.cpp:2865),
  plus the native `GetAsteroidName` name. Ore logic unchanged (only the helium
  row's base ore 1.0 × `amountHe3`). Removed the invented 120/100/140 radius
  ladder and the hardcoded `1000` hp.
- `src/Allegiance.Server/Server.Showcase.cs` — research-showcase rocks use the
  same table: real model/icon/hp/radius/name for their build-capability mask,
  dropping `"rock_build"` + empty icon.
- `src/Allegiance.ClientCore.Game/Rendering/RockTextureSource.cs` — documented
  `meteoriconbmp.mdl` in the native icon-file list so the bundle manifest scanner
  ships it (the one missing asteroid asset).
- `packaging/artwork-manifest.txt` + `artwork-roots.txt` — regenerated; the only
  diff is `+meteoriconbmp.mdl`. All 7 rock models (`bgrnd03/05/51/52/53/55/56.mdl`)
  and all 5 radar icons (`meteoriconbmp`, `heliumrockbmp`, `hotrockbmp`,
  `copperrockbmp`, `carbonrockbmp`) are in the manifest.

No other asteroid-creation sites exist (searched `new DataAsteroidIGC` — only the
two above, plus the codec). `rock_mine`/`rock_build` now appear nowhere in code.

## Client behaviour (scope items 3–4)

The client renderer was already native-model-capable (`NativeAsteroidRenderer`
parses each rock's own `.mdl` + native texture per `AsteroidDef`, `NativeAsteroidResolver.cs`);
the flat-square root cause was the invented model names forcing the
"not present → fall back to imported/procedural rock" path. With real `bgrndNN`
names that fallback no longer fires (verified: zero `not present` / zero
`falling back to imported/procedural rock` lines in the client log of a generated
skirmish; the native per-resource surfaces `bgrnd05bmp/bgrnd55bmp/bgrnd51bmp/
bgrnd52bmp/bgrnd53bmp` all load). The procedural-rock fallback remains only as the
degrade path for a genuinely missing file, logged once per stem.

**Flat-square sweep.** Ran a real windowed skirmish on a port-generated map
(`scripts/live-proof.sh` + `mapGen` server config). Real-backbuffer frames show
asteroids rendered as 3D textured rocky volumes (lit on one side, shadowed on the
other, irregular silhouette, native surface detail) — not flat squares. Stations
and the own ship also render as proper 3D in the captured frames; no square/
billboard object was observed. Limitation: distant ships / chaff / probes / mines
could not be exercised without spawning combat bots, which would contend with a
concurrent agent's running suite on this box; the P750-specific regression
(invented rock names → asteroid squares) is conclusively fixed.

## Evidence

Primary real-window screenshot (2560×1440, real backbuffer via the P572 autoshot
path, `SaveAsPng`): `docs/reports/P750_assets/proof_t030.0_sSolitaire_cchase.png`
— the pilot in chase view in the generated `Solitaire` sector; native 3D asteroids
scattered around a warp funnel. Raw frames + client/server logs (full set):
`~/allegiance-evidence/P750/`.

Key log lines (generated skirmish, sector Solitaire):
```
[boot] game 'P750 Native Asteroid Proof' generating map: topology=SingleRing, 4 sectors / 8 warps / 2 stations / 80 asteroids
[client] asteroids: NATIVE per-rock .mdl meshes + native textures
[monogame] rock surface 'Plain': 2048x2048 'bgrnd05bmp.mdl'
[monogame] rock surface 'Helium': 2048x2048 'bgrnd55bmp.mdl'
... (Uranium bgrnd51bmp / Silicon bgrnd52bmp / Carbon bgrnd53bmp / Buildable bgrnd05bmp)
```
No `native asteroid model '…' unavailable` / `falling back to imported/procedural rock`
anywhere. The server also reported live mining on native-prefixed helium rocks
(`[mine] … began mining asteroid 3 'He7622' (ore 83.33/83.33)`), confirming the
native `He` name prefix and ore are flowing end-to-end.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Fixed 9-row asteroid type table (models/icons/hp/radius/ore/prefix) | asteroidigc.cpp:126-137 | design | Transcribed verbatim into `NativeAsteroidTypes` (single shared source) |
| `GetRandomType` selection by capability | asteroidigc.cpp:231-255 | design | Ported; `randomInt(0,1)` → `Random.Shared.Next(0,2)` |
| `GetTypeDefaults` field pull | asteroidigc.cpp:174-177 | design | Ported |
| `randomInt(radius, 2·radius)` inclusive radius | common.cpp:2865, zmath.h:125-127 | design | `Random.Shared.Next(r, 2r+1)` |
| `signature = radius/100` | common.cpp:2866 | design | Ported |
| Ore = base·amountHe3 only for non-zero base | common.cpp:2873-2875 | design | Ported (only helium rows carry ore) |
| `GetAsteroidName` prefix + number | common.cpp:2786-2801 | design | Ported; leading-NUL `\0a` prefix renders empty (native C-string semantics) → port emits bare number for huge/buildable |
| Special rocks distributed U/Si/C by tech path | common.cpp:3052-3058, asteroidigc.cpp:190-229 | design | Kept the port's existing single-capability assignment (specials = `c_aabmSpecial<<0` = uranium) and maps it to the real uranium row. Distributing U/Si/C would change which special bases can build (tech-build semantics) — out of P750's model/icon scope; flagged, not silently changed |

## Modern alternative considered

Native hardcodes the 9-row table in C++. The "modern" approach would be a
data-driven (JSON/ini) asteroid catalogue, but for a 1:1 fidelity port with the
corpus as ground truth, a static, cited, compile-checked table in Core is clearer
and keeps a single source of truth (the manifest scanner already keys off the
code literals). The 20-year-old `randomInt(radius, 2·radius)` scaling and the
prefix+number naming are retained as native intent. For the radar, native loads
per-icon surfaces (`*rockbmp.mdl`); the port's sector-map info pane already
aggregates asteroids by capability (faithful to `sectormap.cpp:280-347`) and the
icons are now shipped in the bundle for the renderer/future use — no icon-atlas
refactor needed for this task.

## Verification (measured)

- Whole-solution build (`AllegianceCore.slnx`, Release): **0 warnings, 0 errors**.
- AOT publish (`dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true`): **success**, native binary produced, no IL2026/IL3050.
- New tests (`P750NativeAsteroidTypesTests`, 7 tests) — failing-first proven:
  generated-ring / brawl / showcase tests failed on the pre-change code (emit
  `rock_mine`/`rock_build`); pass after. **3× alone = 21/21 pass.**
- Fast tier (`tools/run-tests.sh --tier fast`, final code): 8876 tests;
  **1 failure — `ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy`**
  (a chat-targeting Live test in code I did not touch; already listed as a known
  flake in `docs/TEST_FLAKES.md:1196`; **confirmed 3/3 isolated passes**). All 7
  P750 tests passed within the fast run. (The only edits after the fast run were
  two test-fixture model literals — production Core/Server code was unchanged;
  those fixtures re-pass: P733 14/14, P703 4/4.)
- `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
  (bots/flight touched by showcase-rock size/hp): **5/5 pass** (each 1m48s).
- Touched test fixtures (`P733ResearchTimeTests` 14/14, `P703ResearchCommanderLiveTests` 4/4) re-pass after routing their rock model through `NativeAsteroidTypes`.
- Bundle manifest: regenerated; only diff `+meteoriconbmp.mdl`; all 7 models + 5
  icons present.
- No `rock_` name remains in any `src/` or `tests/` code path (only explanatory
  comments and the unrelated `Loopback_synthetic_rock_mines…` method name).

Raw AOT publish output and fast-tier logs are in `~/allegiance-evidence/P750/` and
`TestResults/` (not committed; ≤2 MB committed in `docs/reports/P750_assets/`).
