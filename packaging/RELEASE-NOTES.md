AllegianceCore: portable bundles plus Windows setup and Linux AppImage.

## New since v0.1.7

This release fixes three desktop crashes, closes four security findings in the new WebSocket
transport, and brings a round of skirmish, hangar and sound fixes. It is also the first release
whose server can host the new browser client.

**Crash fixes**
- Choosing the **Grand** skirmish size no longer crashes the game.
- **F12** (credits) no longer crashes with the default black sky.
- The loadout screen no longer crashes in a small window.

**Skirmish and spectating**
- The spectator can travel between sectors: fly the free camera into an aleph to come out of the
  linked one, **Home**/**End** step through sectors, and **Tab** opens a clickable sector map.
- Seat hotkeys only work while spectating.
- Remote ships move more smoothly in single player (at least 20 updates a second).
- AI commanders recover a constructor that never reaches its asteroid.

**Hangar, ships and sound**
- A lifepod that docks is always offered a fresh ship, and the restore no longer gets stuck.
- Buying a loadout keeps a part you already own even when the tech tree would drop it.
- Guns stop looping after you pod, dock or leave flight; space ambient sounds stop while docked
  and when you switch to spectating.
- After the death camera ends you return to the cockpit.
- The flight scene no longer shows through the hangar's side margins.
- Ejecting now shows the original ejection blast.

**Visuals and options**
- Desktop **MSAA** (Off / 2x / 4x / 8x, default Off) in the graphics options.
- Ship trails at every distance, and full ship detail at every distance by default
  (**Options → Visuals**, `ships.fullDetail`).
- The controls help (**F1**) now has two pages.

**Multiplayer and servers**
- Players marked AFK (automatically after 180 s idle, as in the original) are not given seats.
- Pressing Launch again while waiting for the match to start shows the countdown instead of an
  error.
- The game server can also accept players over **WebSocket** (off by default). This is the
  transport the browser client uses; a security review of it found four issues, all fixed in this
  release.
- A perpetual public battle preset: one never-ending 5v5 with random civilizations, where bots fill
  empty seats and step aside when a human joins (`docs/PUBLIC_PERPETUAL_BATTLE.md`). The bot
  driver now recovers on its own and backs off between reconnects.
- The client stops sending to a server after it disconnects.

**Browser client (preview, not a download)**
- The game client now runs in Chromium-based browsers. A lobby that hosts it shows **Play now**:
  join as a guest, pick a side, fit a ship and fly, with the full keyboard table and both HUD
  styles. Art streams per sector so flight starts quickly. Hosting it is described in
  `docs/BROWSER_HOSTING.md`.

**Experimental (off by default)**
- **Jet HUD**: further work on the vector HUD (stores block, master caution panel, RWR-style
  radar, console and chat, an amber palette, and Jet-styled screens). Turn it on with
  `--visuals hud.jetSkin=on`, then **Shift+F12** to cycle Classic → Modern → Jet. Still incomplete;
  feedback welcome.

**Known limits**: the Android client is experimental and has no download. The browser client is a
preview for Chromium-based browsers. Behaviour parity with native Allegiance is about 99.6 % of the
tracked rules; the per-area scorecard is in `docs/parity/`.

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
