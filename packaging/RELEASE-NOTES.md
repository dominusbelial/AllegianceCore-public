AllegianceCore: portable bundles plus Windows setup and Linux AppImage.

## New since v0.1.4

This release makes joining games easier and fixes the controls testers found hard to start with.

**Joining games**
- `allegiance://` join links: the lobby page has a Join column, and clicking a link opens the game and
  asks before joining. The Windows installer and the Linux per-user installer register the link
  handler; on macOS the app declares it.
- An explicit `--lobby` address now always wins over the saved connect profile.
- Intro screen: the arrow keys always move through the menu, the pilot-name field shows when it has
  focus, and Find Server no longer swallows what you type into the password box.

**Controls**
- New players can fly from the keyboard again: fresh profiles start on the Port layout (W/S/A/D thrust,
  arrow keys pitch and yaw), with a one-time hint in flight. The original Allegiance default map, which
  binds no steering keys, is one press away as the "Classic Allegiance" preset in the F6 controls screen.
  Existing keymaps are not changed.
- Mouse flight and free-look capture the mouse properly on Wayland and over remote desktop.
- Mouse options are saved: invert Y and sensitivity (default matches the original). Holding a
  sensitivity key repeats it and shows the new value on the HUD.

**Screens**
- The docked hangar uses the original hangar art: background, button strips, the launch animation,
  hover captions and the turret badge.
- Training slideshows show the original slide images.

**Gameplay**
- A lifepod is no longer destroyed when it bumps into a station, an asteroid or another ship right after
  ejecting.
- Drones launch with their hull's standard loadout, as in the original, and other pilots on the same side
  see it.

**Bots and showcase games** (skirmish and showcase servers only)
- Bot commanders research first on generated maps instead of queuing expensive buildings ahead of
  research they could already buy.
- Bots pick resources and lay mine fields again in showcase games, and the showcase game-over text
  matches the original's.

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
