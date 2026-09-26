# P374 — sector/cluster threat warning system: no sound, no flashing starmap icons, no warning text

## Why this task exists

Domingo remembered "other visual cues for warnings" from the original
game, on top of the sound-cue sweep that found missing threat-related
sounds. Investigated both halves — they're the same underlying native
feature (`ClusterWarning` state), confirmed missing from this port
entirely, sound AND visual.

## Confirmed real native mechanic

**Sound** (`WinTrek/trekigc.cpp:335-381`): a `c_vAlertSounds` array
picks a distinct sound per threat severity/type as a sector's
`ClusterWarning` state changes — `minerThreatenedSound`/
`constructorThreatenedSound`/`carrierThreatenedSound`/
`bomberDetectedSound`/`stationThreatenedSound`, plus
`sectorSecuredSound`/`sectorLostSound` for control-state changes
(lines 375-381). `trekigc.cpp:362` plays the picked sound; posts text
via `trekClient.PostText` (lines 349-350, format `"%s in %s"` — the
warning description + sector name).

**Visual** (`WinTrek/sectormap.cpp`):
- **Flashing threat icons on the starmap**, per severity
  (`sectormap.cpp:1055-1082`): a distinct icon blit ONLY while
  `m_bFlashFrame` is true (a blink toggle at `sectormap.cpp:737`,
  `m_bFlashFrame = !m_bFlashFrame`, presumably on a fixed timer) —
  `sectorwarningbmp` (station threatened), `sectorcapitalbmp` (capital
  ship detected), `sectorbomberbmp` (bomber detected),
  `sectorminerbmp` (miner/builder threatened), `sectorcombatbmp`
  (general combat) — the exact visual companions to the sounds above.
- **Flashing cyan sector-outline highlight** (`sectormap.cpp:1044,
  1192`), gated on `pCluster->GetHighlight() && !m_bFlashFrame`.
- **Warning text on the starmap info pane**
  (`GetClusterWarningText(cw)`, `sectormap.cpp:523-534`), shown on
  hover (`sectormap.cpp:1449-1451`) and posted as a chat/status line on
  state change (`trekigc.cpp:349-350`).

**Port status, confirmed**: this port has ZERO equivalent of any of
the above — no threat-icon layer on `StarmapRenderer.cs`, no flashing
highlight, no warning text, no sound.

## Explicitly confirmed NOT part of this task (ruled out, don't build)

The same investigation also checked three OTHER plausible "warning"
features and found native genuinely has no visual for them either —
do not build these, they'd be inventing something that was never
there:
- `salBoundryExceededSound`/`salSectorCrowdedSound`
  (`WinTrek.cpp:8656-8666`) — sound-only in native, no visual/border
  tied to them anywhere. If you choose to port these two SOUNDS as
  part of a separate/future audio task, that's fine, but no visual
  companion exists to build here.
- A persistent "critically damaged" hull-warning visual distinct from
  the existing one-shot hit-flash — native's `hullLowSound` (fires at
  hull fraction < 0.15) has no visual attached; the only flash
  mechanism (`GetFlash`/`SetTimeDamaged`, `WinTrek.cpp:8922-8955`) is
  the SAME one-shot per-hit flash this port's `DamageFlashTracker`
  already correctly and completely ports. Don't add a second,
  persistent low-hull flash — it wasn't in native.
- An off-screen "danger direction" arrow — doesn't exist in native at
  all (`grep -in danger` over all of `WinTrek/*.cpp` returns zero
  hits). The only off-screen concept is the already-ported radar
  bracket edge-clamp system.

## Scope

1. **Determine the real trigger condition for a sector's threat state**
   in THIS port's own data model — what does this codebase already
   track that corresponds to native's `ClusterWarning` (e.g. enemy
   ship presence/type in a sector, or a station under attack)? Check
   `Sector.cs`/`Server.cs` for anything resembling this already (a
   "who's currently in this sector and what are they" concept likely
   already exists for other systems — reuse it, don't build parallel
   tracking).
2. **Read `GetClusterWarningText`/the full `ClusterWarning` enum**
   (search native for its definition) to get the real distinct
   severity/type list right, not an approximation.
3. **Build the flashing starmap icon layer** on `StarmapRenderer.cs`/
   `StarmapLayout.cs` — real per-severity icons (or a reasonable
   simplified visual distinction if exact icon art isn't available,
   documented clearly if so), blinking on a real timer matching
   native's flash cadence (check `m_bFlashFrame`'s toggle rate).
4. **Build the flashing sector-outline highlight** for a
   currently-relevant/selected threatened sector.
5. **Surface the warning text** — on starmap hover and as a real
   posted status/chat line on state change, matching native's
   `"%s in %s"` format (warning description + sector name).
6. **Add the sector-threat sound cues** (`c_vAlertSounds`, the 5+
   distinct sounds cited above) triggered by the same real state
   change, reusing whatever audio plumbing already exists
   (`SoundManager.cs`) — this task covers both halves since they share
   one trigger, don't split them into a separate task.

## Explicitly out of scope

- P372 (radar/HUD side-coloring), P373 (new-contact sound cue) —
  separate, complementary, don't touch.
- The three ruled-out items above — don't build them.
- P368-P371 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: real multi-client session with a real
  threat scenario (an enemy ship entering a sector containing a
  friendly station/miner/etc., or a station changing ownership).
  Confirm the real flashing icon appears on the starmap, the sector
  outline flashes, warning text appears on hover and as a posted
  status line, and a real distinct sound plays — all tied to the same
  real trigger event, not simulated separately.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The sector-threat warning system (sound + visual), committed on
   `master` (not pushed — Domingo pushes). Do not create a side
   branch.
2. Report doc `P374_SECTOR_THREAT_WARNINGS.md` in the repo root: the
   real `ClusterWarning` severity/type list ported, the real trigger
   condition wired up in this port's own data model, and the live
   verification performed (describe the actual scenario used and what
   was observed).
