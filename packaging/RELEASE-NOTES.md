AllegianceCore: portable bundles plus Windows setup and Linux AppImage.

## New since v0.1.5

This release brings a new look for space, HUD skins you can switch in flight, passwords for private
games, and a set of fixes to ejecting, lifepods and connecting.

**Look**
- Space has a new default look: an HDR scene with a filmic colour grade, simulated stars and glare,
  star streaks at speed, subtle space dust, and full-range visibility (objects stay visible out to
  10 km instead of popping in). Asteroids in particular read as real rock now.
- Every part of the new look can be switched off in **Options → Visuals** (or with `--visuals key=off`),
  which restores the original rendering exactly.
- Station construction looks like the original: the violet build shell grows around the rock,
  turns solid and shrinks away as the station completes.
- Flight cameras draw out to the original 10 km far plane.

**HUD**
- HUD skins: **Shift+F12** switches between the Classic and Modern HUD in flight; the choice is also
  in Options and is remembered. Training missions restore the skin they need.

**Joining games**
- Password-protected games: the server list shows which games need a password, the join screen asks
  for it and lets you retry in place after a wrong password. Remembering passwords is opt-in and stored
  only for your user account.
- Connecting is more robust: one connection attempt at a time, a clear 15-second limit, and a cancel
  that really cancels.

**Gameplay fixes**
- Ejecting heads for your own or an allied station, and rescue probes pick up any friendly pod, as in the
  original.
- Lifepods no longer die moments after launch because of an oxygen timer left over from a previous life,
  and a pod no longer keeps the dead ship's shield, booster or cargo.
- After a station teleport the hangar no longer gets stuck.

**Updates and stability**
- Installing an update is now atomic: it is checked before anything is touched, and an interrupted
  update is rolled back or finished on the next start.
- Fresh installs report their version to the updater.
- Malformed messages from a server are dropped instead of crashing the client.

**Servers**
- Less bandwidth in big battles: gunfire is batched into one message per player per tick (about 13 %
  less server traffic in the 400-player test), and ship status messages are only sent when something
  changed, as in the original (87 % fewer status bytes).
- Hardening: repeated logon requests are rate-limited, maps load only from the artwork catalogue, and a
  wrong reconnect token refuses only that client.

**For bot and server tinkerers (experimental, off by default)**
- `ALLEGIANCE_BOTS_CIV_STRATEGY=1`: bots plan research and fleets from each civilization's own
  strengths instead of one generic path. Not yet the default; it is being measured on the 200v200 rig.

**Known limits**: the Android client is experimental and has no download. Behaviour parity with native
Allegiance is about 98 % of the tracked rules; the per-area scorecard is in `docs/parity/`.

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
