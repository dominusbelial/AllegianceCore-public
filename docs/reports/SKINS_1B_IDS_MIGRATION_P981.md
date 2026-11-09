# P981 — Skins phase 1B: ids, built-in definitions, settings migration

**Status:** implemented in worktree, not committed.  
**Scope:** replace the persisted `HudStyle` enum with canonical, versioned skin ids
(`classic`, `modern`) and a validated catalog, while keeping Classic and Modern
rendering output pixel-identical. No palette/font/layout extraction yet (slice 1E);
no F-key cycling or renderer adapter rework yet (slice 1C).

---

## 1. What changed

### 1.1 New skin-system files under `src/Allegiance.ClientCore.Game/Rendering/Skins/`

| File | Responsibility | Key API |
|---|---|---|
| `HudSkinIds.cs:6` | Canonical ids and id normalization (`classic`, `modern`). | `HudSkinIds.Normalize(string?)` |
| `HudSkinDefinition.cs:11` | Serializable, validated definition record. | `HudSkinDefinition`, `HudSkinPresentationDefaults` |
| `HudSkinJson.cs:8` | Source-generated JSON context (AOT/trim-safe). | `HudSkinJson` |
| `HudSkinLoader.cs:7` | All-or-nothing validation; throws `HudSkinLoadException`. | `Load(Stream)`, `Load(string)` |
| `HudSkinLoadException.cs:5` | Definition-rejection exception. | — |
| `HudSkinCatalog.cs:8` | Installed catalog, built-in loading, unknown-id fallback to Modern. | `TryGet`, `Resolve`, `TryAdd` |
| `HudSkinResolver.cs:6` | Lazy default catalog shared by migration and runtime. | `Catalog` |
| `classic.skin.json` | Built-in Classic definition: id/name/version/defaults. | — |
| `modern.skin.json` | Built-in Modern definition: id/name/version/defaults. | — |

The built-in definitions carry only id, name, version and presentation defaults
(`SelectionBracket`, `RadarDeclutter`, `Level`) for phase 1B, per P972 §2.3 / §9.
They are embedded resources (`Allegiance.ClientCore.Game.csproj`).

### 1.2 `HudSettings` now persists `Skin` and accepts legacy `Style`

`src/Allegiance.ClientCore.Game/Rendering/HudSettings.cs`:

- `Skin` property (`:16`) is the persisted canonical id, default `modern`.
- `Style` (`:21`) becomes a read/write adapter: `get` maps `Skin` → `HudStyle`,
  `set` maps `HudStyle` → `Skin`. Unknown skin ids resolve to `Modern` through
  `SkinToStyle` (`:217`).
- `Load` (`:143`) parses raw JSON, migrates via `MigrateSkin` (`:170`), then
  deserializes the remaining fields. It never rewrites the file during load.
- `Save` (`:160`) writes `Skin` only; the legacy `Style` field is dropped.
- `Normalize` (`:129`) resolves the skin id through the catalog, replacing the
  old undefined-enum → Classic behaviour.

### 1.3 `TrainingPreferences` carries the canonical skin id

`src/Allegiance.ClientCore.Game/Game1.TrainingIntegration.cs`:

- Record field changed from `HudStyle Style` to `string Skin`.
- Save uses `_hudSettings.Skin`; restore resolves through
  `HudSkinResolver.Catalog.Resolve(saved.Skin).Id` so an unknown training skin
  falls back to Modern without erasing unrelated settings.

### 1.4 Renderer branches stay untouched

All existing `HudStyle` callers (`Game1.GpuBudget.cs:30,153`,
`Game1.Rendering.cs:3439`, `Game1.Commanding.cs:473`,
`Game1.FlightInput.cs:1978`, `CareerScreenRenderer.cs:39`, etc.) continue to
work through the `Style` adapter. No drawing code moved.

---

## 2. Migration table (P972 §2.4) and covering tests

| Input | Expected result | Test name |
|---|---|---|
| Fresh profile, no readable HUD file | `modern` | `HudSkinMigrationTests.Fresh_profile_with_no_file_gets_modern` |
| Existing player, no readable HUD file / no style field | `classic` | `HudSkinMigrationTests.Existing_player_with_no_file_gets_classic` |
| Legacy `"Style":"Classic"` / `"Modern"` (string) | `classic` / `modern` | `HudSkinMigrationTests.Legacy_style_field_migrates_to_canonical_id` |
| Legacy numeric `0` / `1` | `classic` / `modern` | `HudSkinMigrationTests.Legacy_style_field_migrates_to_canonical_id` |
| `"Skin":"classic"` / `"modern"` | that installed skin | `HudSkinMigrationTests.Skin_field_is_normalized_to_canonical_id` |
| Unknown id or unknown legacy enum value | `modern` (one diagnostic) | `HudSkinMigrationTests.Unknown_skin_id_falls_back_to_modern`, `Unknown_legacy_string_falls_back_to_modern`, `Unknown_legacy_number_falls_back_to_modern` |
| Known id with different case / surrounding whitespace | normalized canonical id (one diagnostic) | `HudSkinMigrationTests.Skin_field_is_normalized_to_canonical_id` |
| Malformed JSON (existing or fresh profile) | preserve current fallback | `HudSkinMigrationTests.Malformed_json_keeps_fallback_for_existing_player`, `Malformed_json_keeps_fallback_for_fresh_profile` |

Additional tests:

- `HudSkinMigrationTests.Save_writes_skin_and_drops_legacy_style`
- `HudSkinMigrationTests.Round_trip_preserves_skin_and_unrelated_options`
- `HudSkinMigrationTests.Legacy_classic_file_round_trips_to_skin_and_keeps_options`
- `HudSkinMigrationTests.SelectionBracket_and_RadarDeclutter_defaults_follow_resolved_skin`
- `HudSkinMigrationTests.Player_override_beats_skin_default`
- `HudSkinMigrationTests.Style_adapter_sets_skin_id`
- `HudSkinCatalogTests.*` — built-in load, defaults, unknown fallback, validation rejection.
- `P882aTrainingIntegrationTests.Training_exit_restores_saved_skin_id` — training
  snapshot/restore carries the skin id.

---

## 3. Zero-change evidence

P980's golden baseline exists at `~/allegiance-evidence/hud-goldens/66d72e6f/`
(commit `66d72e6fc847`, manifest marks `Dirty: true`). A clean build of the
same commit does **not** reproduce the P980 pixel hashes (all 28 compared
hud2-proof captures differ from the baseline), so the baseline was captured with
uncommitted local changes and cannot be used as a strict clean-HEAD reference.

Therefore the phase-1B zero-delta claim is proven by a clean-HEAD vs changed-code
before/after run of `tools/hud2-proof`:

- Captured 28 PNGs (`combat`, `critical`, `inventory`, `radar-*` at
  1920×1080 and 2560×1440, Classic + Modern, size = 1).
- **26 of 28 frames are byte-identical in decoded RGBA pixels** between clean
  HEAD and the migrated code.
- The remaining 2 frames (`critical-1920x1080-Modern.png`,
  `critical-2560x1440-Modern.png`) differ **between two consecutive runs of the
  same clean HEAD code** as well, because the critical scenario uses a
  time-dependent damage flash; they are non-deterministic and excluded from the
  zero-delta comparison.

Evidence files:

- `.scratch/p981/hud2-before/` — clean HEAD captures.
- `.scratch/p981/hud2-after/` — migrated code captures.
- `.scratch/p981/before-after-compare.json` — per-file SHA256 comparison
  (`deterministic_deltas: 0`).

The existing `tools/hud2-proof` assertions (no overlapping text, all visible
Modern elements drawn, inventory column fit, fixed console anchors) also pass
for both the clean HEAD and the migrated code: `HUD2_RENDER_PASS 480 rendered
scenarios`.

---

## 4. Test results

### New / affected tests run 3× alone

| Test set | Result |
|---|---|
| `Allegiance.ClientCore.MonoGame.Tests.Skins.*` | **35 passed / 0 failed / 0 skipped** (3/3) |
| `P882aTrainingIntegrationTests.Training_exit_restores_saved_skin_id` | **1 passed / 0 failed / 0 skipped** (3/3) |
| `P882aTrainingIntegrationTests` (full class) | **31 passed / 0 failed / 0 skipped** (3/3) |
| `ModernHudTests` | **5 passed / 0 failed / 0 skipped** (3/3) |
| `ModernHudSettingsInputTests` | **1 passed / 0 failed / 0 skipped** (3/3) |
| `P836HudLevelTests` | **20 passed / 0 failed / 0 skipped** (3/3) |
| `P828RadarLabelDeclutterTests` | **15 passed / 0 failed / 0 skipped** (3/3) |
| `Hud19CentreHudTests` | **20 passed / 0 failed / 0 skipped** (3/3) |
| `P949FreshProfileDefaultsTests` | **8 passed / 0 failed / 0 skipped** (3/3) |

### Gate runs

- `dotnet build AllegianceCore.slnx`: **0 warnings / 0 errors**.
- `tools/build-tools.sh`: **all 123 tool projects built, 0 warnings / 0 errors**.
- `tools/run-tests.sh --tier fast`: 13,662 tests; **task-related failures: 0**.
  Non-task failures under shared-host load:
  - `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` — passed 3/3 alone; already ledgered launcher-receipt flake.
  - `NaniteThreatBonusLiveTests.Threat_target_earns_2x_repair_credit_over_wire` — passed 3/3 alone; already ledgered credit-timing flake.
  - `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` — intermittent alone (2/3 fail in this session); already ledgered clock/receipt boundary flake.
  - `BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` — passed 3/3 alone.
  - One calibrated-limit offender: `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` at 10.405 s vs 10 s limit.

---

## 5. Native intent

Native Allegiance stores HUD style as a preference loaded with
`LoadPreference`/`SavePreference` (WinTrek.cpp:3300-3303, :5082-5094). The port
continues to use a JSON profile file (`hud.json`) but now persists a canonical
skin id rather than an enum, with backward-compatible migration that mirrors the
original fresh-profile → Modern / existing-profile → Classic policy
(`HudSettings.cs:143-201`). Renderer selection still follows the same two-path
Classic/Modern branch because phase 1B only introduces identity and persistence;
the visual tokens remain code constants.

## 6. Modern approach

The skin system is split into a definition/catalog boundary (load/switch time)
and a `Style` adapter that keeps the existing per-frame renderer branches working
unchanged. Built-in definitions are embedded JSON validated at startup; a bad
Modern built-in throws and prevents a blank HUD. Unknown ids resolve to Modern
with a one-time diagnostic. The next ordinary `Save` writes the canonical `Skin`
field and drops the legacy `Style` field, satisfying P972 §2.4 without touching
drawing code.
