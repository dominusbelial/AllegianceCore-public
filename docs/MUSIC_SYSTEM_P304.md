# P304 — Music system plumbing (toggle + volume + flight/station switching)

Builds the real background-music plumbing on top of the confirmed real native
scaffolding (see `~/Documents/Domingo-Llm-Wiki/pages/allegiance-music-system.md`
for the source trace): a real music toggle, a real volume control, and real
flight-vs-docked track selection between the two named native tracks
(`flightmusic` / `stationmusic`). No real tracks yet — the two beds are clearly
labelled generated placeholders so the whole system is live-exercisable now,
with real tracks droppable in later with **zero further code changes**.

## Design chosen

**Three dedicated keybindings (mirroring native's three dedicated menu
commands), not a combined control.** Native exposes `idmToggleMusic`(704) /
`idmMusicVolumeUp`(705) / `idmMusicVolumeDown`(706) and a real
`m_pnumMusicGain` volume object (WinTrek.cpp:1201) — so a full 3-key scheme is
the faithful shape, and dedicated volume-up/down keys are what make "volume
control actually changing output level" cleanly live-verifiable (a single
combined control could not be demonstrated as unambiguously). Keys are free
singles not used by the game (grep-verified against Game1): **J** toggles
music (mnemonic "Jam"), **+ / =** volume up, **− / **_ volume down.

**Pure-planner + audio-wrapper split**, following this directory's established
convention (`StationSoundPlanner` + `SoundManager`, `WarpSoundPlanner`, …):

- `MusicPlanner` (`Audio/MusicPlanner.cs`) — pure, MonoGame-free. Owns the real
  toggle state, a discrete 1/16-step volume ladder (a countable, measurable
  output level), and the track-selection decision: enabled+undocked →
  `flightmusic`, enabled+docked → `stationmusic`, off → stop. Emits
  `MusicCommand`s (Start/Stop/SetVolume). Fully headless-unit-testable and
  live-drivable.
- `Music` (`Audio/Music.cs`) — the MonoGame wrapper. Owns the two looping
  `SoundEffectInstance` beds, applies the planner's directives, and exposes
  live State / actual-Volume for measurement. Degrades gracefully to silent
  with no audio device (same policy as `SoundManager`).
- `PlaceholderMusicFactory` (`Audio/PlaceholderMusicFactory.cs`) — generated,
  not loaded (the `RockTextureFactory`/`WarpMeshFactory` precedent). Produces
  two deterministic, seamlessly-looping, clearly distinct tone beds: a low
  flight hum vs a higher, warmer station pad, so dock/undock is audibly and
  measurably distinguishable.

**Docked state is not re-derived** — `Game1.UpdateMusic` reads the exact real
expression the client already uses everywhere else:
`world.Ships[me].StationId != -1` (the `HandleDockedScreen` latch from the
real SHIP_STATUS / DockedNotify wire). The music update runs after
`UpdateFlying` (which early-returns on the docked screen), so it keeps driving
in both the flying and docked states — exactly where the switch must happen.

## Placeholder approach

Real-asset-first, generated-fallback. `Music.Load` tries
`Content.Load<SoundEffect>` for `Sounds/flightmusic` and `Sounds/stationmusic`;
only if an asset is missing does it fall back to the generated tone. The two
placeholder beds are deliberately distinct (low G3/A3 flight hum vs higher
C4/E4 station pad) with integer-cycle loops so they cross the seam without a
click, and are honest stand-ins — never passed off as real music.

## Live proof (real server + real client + real audio device)

`/.verify_p304/harness` boots a real in-process Allegiance server and a real
`ConnectionSession` client over a real UDP socket, joins, readies, starts, and
launches a real flying ship, then drives the exact `MusicPlanner` Game1 wires
through the real docked signal and replays its directives onto real MonoGame
`SoundEffectInstance`s (an audio device IS present on this host — output levels
below are the actual instance Volumes).

- **PHASE A (undocked, music on)**: `Start Flight` → `flight=Playing@0.800`
- **PHASE B (real dock via `DockWithStationEvent`)**: `Stop Flight, Start
  Station` → `flight=Stopped, station=Playing@0.800`
- **PHASE C (real undock via `SendDocked`)**: `Stop Station, Start Flight` →
  `flight=Playing@0.800`
- **PHASE D (volume up then down)**: `0.800 → UP → 0.863 → DOWN×2 → 0.738`,
  `flight=Playing@0.738` — output level measurably changed
- **PHASE E (toggle off)**: `Stop Flight` → `flight=Stopped`

Result: **PASS**, exit 0 (full log `/.verify_p304/live_proof.log`).

## Build / test status

- Whole-solution build: **0 warnings, 0 errors**.
- New unit tests: `MusicPlannerTests` (toggle/volume/switch contract) and
  `PlaceholderMusicFactoryTests` (valid PCM, deterministic, distinct,
  higher-pitched station, seamless loop) — all green.
- Full test suite: run twice; only the pre-existing `LiveTurretGunnerBindingTests`
  timing flake is expected/acceptable.

## What it takes to drop in real generated tracks

When the ComfyUI audio pass produces the two real tracks, **no code change is
needed** — `Music.Load` already tries real assets first:

1. Generate two looping `.ogg` files: `flightmusic.ogg` and `stationmusic.ogg`.
2. Drop them into `src/Allegiance.ClientCore.MonoGame/Content/Sounds/`
   (next to the existing `accelsmall.ogg` etc.), so the existing Content build
   (Content.mgcb + MonoGame.Content.Builder.Task) compiles them to
   `Sounds/flightmusic.xnb` / `Sounds/stationmusic.xnb`.
3. Register both in `Content.mgcb` (like the other `Sounds/*.ogg` entries).

`Content.Load` then succeeds first and the generated `PlaceholderMusicFactory`
tones are simply never used — the placeholder is swapped by replacing the asset,
not by editing `Music.cs`. The constant asset paths to watch are
`Music.FlightAsset` / `Music.StationAsset` (= `"Sounds/flightmusic"` /
`"Sounds/stationmusic"`).
