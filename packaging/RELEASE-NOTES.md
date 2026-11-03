AllegianceCore: portable bundles plus Windows setup and Linux AppImage.

## New since v0.1.1

**Training**: every native lesson now runs offline in a real game window: Basic Flight, Basic Weaponry,
Enemy Engagement, Command View, Practice Arena (with its AI combat waves), Nanite and Free Flight, on their
native setups, with the native order buoys, carrier loadout restore and lesson chat.

**Multiplayer and lobby**
- The native team lobby: side requests, team leaders, flush/randomize sides, team names and the
  game-settings form for the host.
- Private games by invitation, hidden games, a server allow-list and a richer server listing.
- Host a LAN game straight from the client.
- Optional UDP transport, with automatic transport selection (TCP stays the fallback).
- Reconnect grace, and the fleet recovers after a server restart.
- Squads, squad game results and career stats with a leaderboard.

**Game modes**: native game goals (prosperity, flags, artifacts, conquest, kills) with goal rotation,
lives limits, random civilizations per match and the friendly-fire / allied-visibility options.

**Flight and HUD**
- Positional 3-D sound: distance rolloff, Doppler and direction cones.
- HUD scale and detail levels, radar label tiers and decluttering, a turret gunner HUD.
- Every hull renders, with corrected mesh texture orientation and own-ship smoke and afterburner effects.
- Autopilot no longer drops a starmap goto after an eject-home flight has docked.

**Bots and big battles**
- Bots play every phase: scout openings and map discovery, ordered miner routines, bomber massing,
  minelaying, turret gunners, finale strikes, and they play each game goal.
- Server: native ship travel planning, a per-match tick budget, worker pools sized by cores and players,
  and no heap growth across match rotation.

**Server administration**: a local server console for AllSrv-style administration.

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
