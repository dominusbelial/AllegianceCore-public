AllegianceCore: portable bundles plus Windows setup and Linux AppImage.

Downloads (all self-contained, with native artwork and the complete sky pack):

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
