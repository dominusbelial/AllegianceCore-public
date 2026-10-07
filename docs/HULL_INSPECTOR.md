# Hull Inspector (P565) — `--inspect`

The offline hull inspector is a client-only review mode that loads the static core + art
and shows **one hull at a time** under a free mouse camera, so you can eyeball every ship
for transparent/missing parts, flat shading, scale and texture issues *before* fixing them.

It runs **completely offline** — no server, nothing on the wire.

```
dotnet run --project src/Allegiance.ClientCore.MonoGame -- --inspect [--window 2560x1440]
```

## Launch flags

| Flag | Meaning |
|------|---------|
| `--inspect` | Open the offline hull inspector (no server connect). |
| `--inspect-civ <key\|name>` | Preselect the first civilization (e.g. `dreg`, `ic`, `bios`). |
| `--inspect-hull <id\|name>` | Preselect the first hull (by HullId or name, e.g. `"Adv Fighter"`). |
| `--inspect-shots <n>` | Unattended: screenshot `n` successive hulls (real backbuffer, ~0.7 s apart) then exit. |
| `--inspect-demo` | Unattended: run the scripted showcase sequence (close-up, cloak, damage+engine+explosion, guns+missiles, mines, chaos, side-by-side, fly-by) then exit. |
| `--window WxH` | Window/backbuffer size (proof uses `2560x1440`). |

Existing look flags still apply: `--ships native|meshy`, `--fx native|enhanced`,
`--soft off`, `--bloom on`, `--asteroids …`, `--debris …`, `--flightFov …`.

## Controls

### Navigation
| Key | Action |
|-----|--------|
| `PgUp` / `PgDn` | Previous / next hull |
| `[` / `]` | Previous / next civilization |
| `D` | Toggle drone/utility hulls |

### Camera
| Input | Action |
|-------|--------|
| Left-drag | Orbit around the hull |
| Right- or middle-drag | Pan |
| Mouse wheel | Zoom (far ↔ very close) |
| Double-click | Focus |
| `R` | Reset to native preview framing |
| `A` | Slow auto-orbit (native hangar spin) |
| `F` | Toggle fly-by (cinematic pass) |
| `T` | In fly-by: toggle "hull moves past the camera" |

### Look
| Key | Action |
|-----|--------|
| `M` | Toggle native ⇄ Meshy art for this hull (preview) |
| `S` | Side-by-side (native left, Meshy right) |
| `K` | **Keep this choice** → writes `assets/ships/ship-art.json` |
| `C` | Cloak on/off; `+` / `-` cloak fraction |
| `L` | Cycle lighting (stock / bright / dark / Alderan) |
| `Q` | Soft particles on/off |
| `O` | Bloom on/off |
| `X` | Wireframe reference frame |
| `Z` | Normals (local axes) |
| `V` | Back-faces |
| `Y` | Side tint |

### VFX sandbox (freely combinable — real renderers, real assets)
| Key | Action |
|-----|--------|
| `1` | Engine trail + nozzle glow |
| `2` | Damage smoke (25% hull fraction) |
| `3` | Explosion near the hull |
| `4` | Projectile stream (picker selects type) |
| `5` | Missile volley (picker selects type) |
| `6` | Mine field (picker selects type) |
| `9` | **Chaos** — everything on at once |
| `0` | Pause/step time |
| `Home` / `End` | Slower / faster slow-motion |
| `,` / `.` | Previous / next **projectile** type |
| `;` / `'` | Previous / next **missile** type |
| `?` | Next **mine** type |

The weapon/missile/mine pickers enumerate the **real core** (every `ProjectileType`,
missile type, mine type) — not a hard-coded list.

### Capture & notes
| Key | Action |
|-----|--------|
| `F11` | Real-backbuffer screenshot → `screenshots/inspect_<hull>_<ts>.png` |
| `Tab` | Cycle mark-bad tag (holes / transparent / flat / scale / texture / vfx-overlap / none) |
| `Enter` | **Mark bad** → append a line to `inspector-notes.jsonl` (hull, civ, camera pose, toggles, active VFX, screenshot) |
| `?` | (console) help |

## Per-hull art override (`assets/ships/ship-art.json`)

A committed map `{ "<ModelName|HullId>": "native" | "meshy" }`. Unlisted hulls default to
**native** (P566). The game (flight, hangar preview, remote ships) resolves each hull as:

> global `--ships` flag → per-hull override → native default

A Meshy override whose asset is missing falls back to native (logged once). Use the
inspector's `M` (preview) + `K` (keep this choice) keys to build this file — it is the
curated "good Meshy ships" list. Meshy assets are never deleted.

## Proofs

See `docs/reports/P565_HULL_INSPECTOR.md` and `docs/reports/P565_assets/` for real-window
2560×1440 screenshots of every showcase state.
