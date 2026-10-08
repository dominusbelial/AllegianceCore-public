# P584 — 2D HUD art drawn upside down: give MdlTextureDecoder an orientation choice and fix all 2-D consumers

Branch: `master` (not pushed). Final amended commit: the latest `git log -1`
(amended after the 3× full-suite run). Build: `/home/domingo/.dotnet/dotnet build -m:8` —
**0 warnings, 0 errors.**

---

## 1. Root cause

`MdlTextureDecoder.Decode` (`Rendering/MdlTextureDecoder.cs`) always wrote
`dstRow = sizeY − 1 − srcRow` — a hard-coded vertical mirror it inherited from
the reference `decode_texture.py`. That convention is right for textures bound
to 3-D geometry (and for native's 3-D textured-quad paths), but it is wrong for
flat 2-D HUD bitmaps that native blits straight to screen.

Domingo (2026-10-08, live on 221 @ `3db766aa`): *"the eject banner looks
inverted somehow? like seen from a mirror"*. The P578 feedback art
(`deathbannerbmp.mdl`, `conejectpoddisplaybmp.mdl`) is stored **top row first**
in the file; the decoder's unconditional flip rendered it upside down.

## 2. Native fidelity — the 2-D blit does NOT flip (justifies the split)

Two distinct native rendering paths for a `Surface`, distinguished in
`engine/context.cpp`:

- **2-D HUD (screen-space) — `Context::DrawImage`** (`context.cpp:546`): the
  comment at `context.cpp:551-553` states plainly
  *"this uses upper left as (0, 0) on psurface"*, and the body is a straight
  `m_psurface->BitBlt(...)` (`context.cpp:586`). No vertical flip. The
  `.mdl` BinarySurfaceInfo that backs these images is read verbatim into the
  surface in `modeler.cpp` `ImportImageFactory::Read` (pixel data copied
  straight from `psite->GetPointer()`, `modeler.cpp:736-760`) — file row 0
  stays at the top.

- **3-D (world) — `Context::DrawImage3D`** (`context.cpp:598`): reverses v —
  `yt * (sizeSource.Y() - rectSource.YMin())` (`context.cpp:648-649`), i.e. a
  bottom-left texture-origin convention. A texture uploaded file-order would
  render upside down on world geometry; the decoder's flip matches this.

Conclusion: **every consumer that blits a HUD image in screen space is "2-D"
and must keep file row 0 at the top; every consumer that draws in world space
(geometry, billboards, decals) keeps the flipped convention.** This is exactly
how native separates `DrawImage` from `DrawImage3D`/`DrawDecal`.

## 3. The fix

`MdlTextureDecoder.Decode(byte[] data, bool flipVertical = true)` — default
`true` preserves today's behaviour for all 3-D consumers (no other call site
changes), and `false` returns the unflipped file-order rows for 2-D HUD art.

Three 2-D HUD consumers were switched to `flipVertical: false` (each with a
comment citing `Context::DrawImage` → `BitBlt`, upper-left origin, no flip):

| File | 2-D art |
|---|---|
| `Rendering/FeedbackTextures.cs` | eject-pod panel (`conejectpoddisplaybmp`), death banner (`deathbannerbmp`), in-HUD eject icon (`ejectpodbmp`) — **the reported bug** |
| `Rendering/LockTextures.cs` | missile-lock sprites (`plockbmp`, `lockedbmp`) |
| `Rendering/GaugeTextures.cs` | six HUD gauges (`HullGaugebmp` … `ThrustGaugebmp`) |

The local un-flip hack in the play client (`~/allegiance-play2`,
`FeedbackTextures.TryDecode`) is now obsolete — it is dropped when the play
client moves to this commit (the decoder + `FlipVertical:false` is the real fix).

## 4. Full consumer audit

Every `MdlTextureDecoder.Decode` call site classified by how it draws the
decoded texture (2-D screen blit vs 3-D world space), with current-orientation
verdict.

| Consumer | Draws as | 2-D? | Orientation |
|---|---|---|---|
| `FeedbackTextures` (FeedbackTextures.cs) | HUD sprite (SpriteBatch) | **2-D** | was wrong → fixed |
| `LockTextures` (LockTextures.cs) | HUD sprite (SpriteBatch) | **2-D** | was wrong → fixed |
| `GaugeTextures` (GaugeTextures.cs) | HUD sprite (SpriteBatch) | **2-D** | was wrong → fixed |
| `Game1.Lifecycle.cs:462` — chaff sprites (ChaffRenderer) | world camera-facing sprite | 3-D | kept (correct) |
| `NativeEffectTextures` (decals/billboards) | world decals/billboards | 3-D | kept (correct) |
| `ProjectileSpriteRenderer` | world billboard | 3-D | kept (correct) |
| `ProjectileModelRenderer` | world mesh | 3-D | kept (correct) |
| `ChaffModelSource` / `MineModelSource` | world sprite/decal | 3-D | kept (correct) |
| `AmbientDustRenderer` | world billboard | 3-D | kept (correct) |
| `NativeSkyRenderer` | world sky dome | 3-D | kept (correct) |
| `RockTextureSource` | world mesh | 3-D | kept (correct) |
| `NativeStationRenderer` | world mesh | 3-D | kept (correct) |
| `NativeShipRenderer` | world mesh | 3-D | kept (correct) |
| `NativeAsteroidRenderer` | world mesh | 3-D | kept (correct) |
| `MineRenderer` / `ExplosionAssets` | world decals/explosions | 3-D | kept (correct) |

Cursor / target brackets and HUD panes are **procedurally drawn** (no `.mdl`
texture), so nothing to fix there. World-space sprites/decals map to native's
`DrawDecal`/`DrawImage3D` flipped-v convention, so keeping the flipped decode is
correct; unflipping them would wrongly invert them against their UV layout.

## 5. Failing-first tests

`tests/.../MdlTextureDecoderTests.cs` — the sprite-variant assertions fail on
today's always-flip code (observed: pixel (0,0) is blue = the file's LAST row
instead of red = its first row) and pass after:

- `Decode_with_flipVertical_false_keeps_file_row_order` — pixel (0,0) is the
  file's first row in the sprite variant.
- `Decode_default_flip_and_sprite_variant_are_opposite_orientations` — the two
  variants are exact vertical mirrors; (0,0) differs (file-first vs file-last).

`tests/.../P572AutoProofTests.cs` — the new live-proof hook:
- `Machine_AutoSuicideAt_SendsOneSuicideThenA_PostKillShot`
- `Machine_AutoSuicideAt_TwoSuicides_TakeOnePostKillShotAfterTheLast`

(The failing-first run is recorded in this task's working notes: with the
always-flip decoder restored the two orientation tests fail; with the fix they
pass.)

## 6. Real-window proof (P572 tooling, 2560×1440 backbuffer, `ALLEGIANCE_ARTWORK_PATH` set)

`scripts/live-proof.sh` on the 221 map (`m2xspiderw.igc`), windowed client,
real `SaveAsPng` backbuffer autoshots. A new `--auto-suicide-at <t[,t…]>`
flag (P572 tooling) sends the client's own K-key suicide on a timer so the
player's death/eject state can be photographed deterministically (the path
P578 could not capture). Evidence in `docs/reports/P584_assets/`.

- **Eject-pod panel — upright.** `realwindow_eject_pod_panel_upright.png`
  (+ `crop_eject_panel_tight.png`): real `conejectpoddisplaybmp.mdl` reads
  `YOU ARE IN AN EJECT POD` and the O₂ gauge is horizontal left-to-right.
- **Death banner — upright.** `realwindow_death_banner_upright.png`
  (+ `crop_death_banner_topband.png`): real `deathbannerbmp.mdl` reads
  `YOU HAVE DIED` / `ESC "Q" TO QUIT` flanked by skulls, top line first.
- **Flight HUD gauges.** `realwindow_flight_hud_gauges.png`: the six gauge
  sprites render correctly in the live flight HUD.

Client logs confirm the real (non-fallback) art loaded:
`feedback art 'conejectpoddisplaybmp': 172x44`, `feedback art 'deathbannerbmp':
136x25`, `feedback art 'ejectpodbmp': 16x16`.

Honest limitation: the lock sprites (`plockbmp`/`lockedbmp`, which need a live
missile-lock to appear) and the 16×16 in-HUD eject icon are not shown in a
dedicated real-window frame here. They are decoded by the **same single
`flipVertical:false` call path** as the captured eject panel/banner and are
covered by the same decoder unit test, so their orientation is fixed by the
same verified rule; a lock-icon frame is scenario-dependent (needs a bot to
acquire/hold a lock) and left as a known limitation rather than fabricated.

## 7. Full-suite runs (scripts/run-tests.sh, P471, on the final code)

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | **1600/1600** | 560/561 | 3813/3815 |
| 2 | 12/12 | **1600/1600** | 559/561 | 3812/3815 |
| 3 | 12/12 | **1600/1600** | 561/561 | 3812/3815 |

**MonoGame — where every P584 change lives — is 100% clean in all three runs**
(all decoder, consumer, and AutoProofMachine tests pass, 1600/1600 each run).

The ClientCore/Core failures are all **pre-existing live-wire / fixed-sleep
socket-receipt load-flakes** already ledged in `docs/TEST_FLAKES.md` — none
touch P584 code (P584 changes zero server-side logic; all edits are client
rendering + autoproof tooling), and the failing names rotate run-to-run exactly
as the ledger describes:

- Run 1: 1×ClientCore + 2×Core (the piped run did not retain the names; the
  ClientCore failure passed on an isolated 561/561 re-run, confirming the flake
  class).
- Run 2: `FullSessionKillIntegrationTests…RealCrossSideKill`,
  `ThreePeerSeededSessionTests…DeterministicVisibility`,
  `CargoBayReloadTests…broadcasts`, `LiveChatSectorWireTests…relay…over_the_wire`,
  `LiveStationRescueTests…docked_without_player_rescued` — all ledged
  (TEST_FLAKES.md lines 33, 38, 53-54, 156).
- Run 3: `GameOverPlayersWireTests…leak_game_over_players_to_another`,
  `GameOverPlayersWireTests.Win_by_elimination…surviving_peer`,
  `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` — all ledged
  (TEST_FLAKES.md lines 30, 206).

Consistent with prior P-series reports (e.g. P578), these are attributed to the
repo's known live-wire load-flake class, not to P584.

## 8. Notes

- The decoder keeps `flipVertical:true` as the default so the untouched 3-D
  consumers are provably unaffected (their call sites are unchanged).
- The `--auto-suicide-at` proof flag is additive, off by default, and only does
  what a player does (presses K). Tested in the machine unit tests.
