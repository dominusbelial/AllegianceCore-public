# P428 — real missile-lock sprites (native's flower-burst), not a procedural bracket

Date: 2026-09-28
Branch: master (committed, not pushed — Domingo pushes)

## TL;DR

The procedural lock indicator is gone. The missile-lock target box now renders the
**real original-game sprites** the native radar swaps between by lock state:

- **Partial lock** → `plockbmp.mdl` — the four **orange corner brackets**, no centre.
- **Full lock** → `lockedbmp.mdl` — the **same orange brackets plus the green 4-petal
  flower-burst** filling the centre (the "flower" Domingo remembered; confirmed by
  decoding both real `.mdl` files to PNG and inspecting them — orange corners + green
  centre, not red).

Verified live against a real server + real `ConnectionSession`: a genuine missile lock
builds partial → full and the HUD draws the real sprite in each state (vision-confirmed
below).

## 1. The confirmed native mechanic (verified, not guessed)

Native's radar swaps two fixed assets by lock state:

- `src/WinTrek/artwork.h:80-81`:
  - `AWF_FLIGHT_MISSILE_LOCKED_ICON = "lockedbmp"`
  - `AWF_FLIGHT_PARTIAL_MISSILE_LOCK_ICON = "plockbmp"`
- `src/WinTrek/radarimage.cpp:217-218` loads both (`m_psurfacePartialLock`,
  `m_psurfaceLocked`).
- `src/WinTrek/radarimage.cpp:330-332` swaps by lock value:
  ```cpp
  if (lock != 0.0f) {
      if (lock == 1.0f) DrawExpandedBlip(..., m_psurfaceLocked, Color::White());
      else              DrawExpandedBlip(..., m_psurfacePartialLock, Color::White());
  }
  ```
  (partial also shrinks toward `radiusBracket` as lock→1).

Both are real 64×64 RGB565 bitmaps in the artwork tree. Decoding them (via this port's
own `MdlTextureDecoder`) and viewing the PNGs confirms the shape precisely:

- `plockbmp`: four **orange** corner brackets; **0 green** pixels; 532/4096 non-black.
- `lockedbmp`: the same orange brackets **plus a green 4-petal flower** in the centre;
  **166 green-dominant** pixels; 950/4096 non-black.

Domingo's "flower" memory is accurate; his colour memory (red) is close but off — the
real asset is orange corners + green centre.

## 2. Asset locations found

Both assets sit at the root of the artwork tree (the port's established
`ALLEGIANCE_ARTWORK_PATH` convention):

- `$ALLEGIANCE_ARTWORK_PATH/plockbmp.mdl`  (64×64)
- `$ALLEGIANCE_ARTWORK_PATH/lockedbmp.mdl` (64×64)

(defaults to `/home/domingo/Development/Artwork` when the env var is unset, exactly like
`GaugeTextureSource`/`RockTextureSource`.)

## 3. Implementation

Reuses the port's proven `.mdl` decode pipeline — **no new loading mechanism**.

- **New** `src/Allegiance.ClientCore.MonoGame/Rendering/LockTextures.cs`
  - `LockTextureSource` — maps lock state → real asset stem/path (`plockbmp` /
    `lockedbmp`), mirrors `GaugeTextureSource`.
  - `LockTextures` — decodes both real sprites via `MdlTextureDecoder` +
    `ToPremultipliedRgba` (the keyed transparent-surround form the HUD draws, exactly
    like `GaugeTextures`), loaded once in `Game1.LoadContent`, `IDisposable`
    (disposed alongside `_rockTextures`). Graceful: a missing/undecodable asset logs
    and falls back to the procedural bracket.
- `Rendering/LockBracketStyle.cs` — removed the now-superseded `ColorFor`
  grey/amber/red tint (the real sprites carry their own fixed colours) and the
  `RotationFor` acquisition spin (meaningless on a 4-fold-symmetric flower; native
  never rotates these assets). Kept the size helper `HalfSizeFor`, repurposed to drive
  the sprite's on-screen scale + its gentle pulse.
- `Rendering/HudRenderer.cs` — `DrawLockBracket` now:
  - `None` → draws nothing (native draws no lock indicator for `lock == 0`,
    radarimage.cpp:330).
  - `Partial` → draws the real `plockbmp` sprite.
  - `Full` → draws the real `lockedbmp` sprite (brackets + flower).
  - Untinted (`Color.White`) so the real orange/green colours show.
  - Falls back to the procedural corner-L bracket only when the real asset is
    unavailable.
- `Game1.Lifecycle.cs` / `Game1.cs` — load `LockTextures` at startup, hand to
  `HudRenderer`, dispose on shutdown.

### Animation choice (judgement, noted per brief)

The **shape** is always the real asset. On top of it I kept only a **gentle scale
pulse** (`HalfSizeFor`, partial ±5px, full ±9px — a full lock pulses harder so an
acquired lock reads hotter). I **dropped the acquisition spin**: rotating the real
sprite would be meaningless — the flower is 4-fold symmetric, so a spin reads as jitter
rather than motion, and native never animates these static blips. The pulse keeps a live
lock feeling "working" instead of a frozen sticker without distorting the real flower.

## 4. Out of scope honoured

- Lock STATE/math (`Ship.UpdateMissileLock`, the wire message, `MissileLockState`/
  `MissileLockFeedback`) — untouched, already faithful.
- `TargetSelectionStyle` selection box (P345, the closed aqua/red/blue rectangle for the
  player's own autopilot target) — a separate mechanic, left alone (its anti-alias tests
  were updated to compare against the real sprite hues instead of the removed tint).
- P368-P427 — untouched.

## 5. Verification

### Real-asset unit tests (new: `LockTextureSourceTests`, 5 cases)

Headless decode of the actual shipped artwork (same style as `RockTextureSourceTests`),
no GPU:

- each lock state resolves to its correct real stem (`None`→null, `Partial`→plockbmp,
  `Full`→lockedbmp);
- both real sprites exist on disk and decode to **64×64**, non-blank;
- **discriminating**: the full sprite genuinely ADDS a green flower over the partial
  (partial has 0 green-dominant px, full has 166) — proven red when Full is wrongly
  mapped to `plockbmp` (must-fail demonstrated and reverted);
- the two sprites are distinct files (distinct SHA-256);
- `ToPremultipliedRgba` keys the black surround to alpha 0 while keeping opaque content
  (so it renders cleanly over the scene).

Updated `LockBracketStyleTests` (remove tint/spin tests, keep the size-pulse contract) and
`TargetSelectionStyleTests` (anti-alias against the real sprite hues).

### Whole-solution build

`dotnet build AllegianceCore.slnx -c Release` → **0 warnings, 0 errors**.

### Full test suite (multiple runs)

- **Allegiance.ClientCore.MonoGame.Tests: 841/841 pass**, three consecutive runs
  (this is the only project the change compiles into besides the renderer itself).
- Allegiance.Core.Tests: only the documented **rotating live-wire timing flakes**
  (run 1: LiveWinCondition/LiveTurretGunner/LiveTreasurePickup/LiveStationRescue/
  LiveMoney/LiveCountdown; run 2: LiveTurretGunner/LiveMoney/AsteroidMinedLive —
  disjoint subsets). Core.Tests does not reference MonoGame; zero coupling to this
  rendering change.
- Allegiance.ClientCore.Tests: only the documented live-wire flakes (DockOut watcher,
  EntityTable). Does not reference MonoGame.

### Live proof (real server, real client, real sprites)

New throwaway harness `/tmp/p428lockproof` (P230 hybrid pattern) booted a **real
in-process server** on `XXV_simple.igc`, injected the spawner to arm the pilot with a
real `MaxLock` missile launcher facing an enemy target dead ahead, and joined a **real
`ConnectionSession`**. Over the real wire the lock built partial → full (measured
fraction 0.050 → 1.000, missile `mis06`, target #9097). It rendered the **real
`HudRenderer` + real `LockTextures`** offscreen at each observed state:

- `p428_lock_none.png` — **no lock indicator** (only the HUD crosshair); vision: "no
  orange bracket, no green flower."
- `p428_lock_partial.png` — the **real orange corner-bracket sprite** (four orange
  corner brackets forming a square, no green).
- `p428_lock_full.png` — the **same orange brackets PLUS a green flower in the centre**
  (vision: "four orange/yellow corner brackets AND something green in the center that
  resembles a small flower").

Evidence PNGs kept in `.verify_p428/` (local, not committed), matching the repo's
`.verify_pNNN/` convention.

## 6. Files

- `src/Allegiance.ClientCore.MonoGame/Rendering/LockTextures.cs` — **new** (source
  mapping + decoder/loader, mirrors `GaugeTextures`).
- `src/Allegiance.ClientCore.MonoGame/Rendering/LockBracketStyle.cs` — remove tint/spin,
  keep size-pulse.
- `src/Allegiance.ClientCore.MonoGame/Rendering/HudRenderer.cs` — sprite-based
  `DrawLockBracket` + procedural fallback.
- `src/Allegiance.ClientCore.MonoGame/Game1.Lifecycle.cs` / `Game1.cs` — load + dispose.
- `tests/Allegiance.ClientCore.MonoGame.Tests/LockTextureSourceTests.cs` — **new**
  real-asset tests.
- `tests/Allegiance.ClientCore.MonoGame.Tests/LockBracketStyleTests.cs` /
  `TargetSelectionStyleTests.cs` — updated for the new API / sprite-hue anti-alias.
- `docs/reports/P428_MISSILE_LOCK_FLOWER_SPRITE.md` — this report.

Out of scope honoured: no lock-state/math change, no P345 target box change, no other HUD
element, no P368-P427.
