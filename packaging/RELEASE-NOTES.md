AllegianceCore: portable bundles plus Windows setup and Linux AppImage.

## New since v0.1.3

This release fixes the problems testers reported with pilot training.

**Controls**
- New players now get the original Allegiance default controls: left mouse fires, middle mouse fires
  missiles, right mouse targets the centre, Q/E roll, SPACE boosts and the wheel sets the throttle.
  These are the keys the training lessons teach.
- The earlier AllegianceCore key set is kept as the "Port layout" preset (press P in the F6 controls
  screen). Existing keymaps move to the new defaults and keep your own rebinds.

**Training**
- Training and skirmish no longer play like a slideshow: the game's own frames had been waiting on the
  embedded server.
- Basic Flight: after the speed demonstration the throttle is released to you again, and the SPACE
  booster works. The lesson still unlocks steering and throttle in stages, as the original does.
- The spoken instructions play again (the voice clips were being dropped silently). Spoken lines that
  name the wrong key are no longer repeated by the 30-second reminder; the reminder repeats the
  corrected on-screen text instead.
- Lesson fixes: hangar refunds and loadouts, the Practice Arena target list, enemy visibility and the
  Nanite wingman's default order now match the original.

**Multiplayer**: player-hosted games over the internet (STUN and hole punching through the lobby) are
in, off by default.

**Simulation**: missiles explode with their blast size; native client/server time conversion; admin
tech-bit overrides; native special-asteroid placement on generated maps.

**Known limits**: the Android client is experimental and has no download. Behaviour parity with native
Allegiance is about 97% of the tracked rules; the per-area scorecard is in `docs/parity/`.

## Downloads (all self-contained, with native artwork and the complete sky pack):

- Windows x64 installer: `Allegiance-win-x64-setup.exe` (per-user by default).
- Windows x64 portable: `Allegiance-win-x64.zip`.
- Linux x64 AppImage: `Allegiance-linux-x64.AppImage` (chmod +x, then run).
- Linux x64 portable: `Allegiance-linux-x64.tar.gz`.
- macOS Apple silicon, macOS 14+: `Allegiance-osx-arm64.zip` (contains the .app).

Extract the entire archive and run `allegiance.sh` (Linux) or `play.bat`
(Windows), or open `AllegianceCore.app` (macOS 14+). .NET, native FreeAllegiance artwork and the sky pack are included.
Read `README-PLAYERS.txt` for server/pilot configuration, system libraries and
troubleshooting. F1 shows help; F6 rebinds controls.

Verify every download against `SHA256SUMS.txt`. Windows and macOS builds are unsigned;
see the player README for SmartScreen (More info -> Run anyway) and Gatekeeper instructions.
Start without arguments for the intro screen. Single Player -> Skirmish works
offline; multiplayer needs a server/lobby address from your operator.
Upgrades and uninstall keep per-user player.json/keymap.json.
