AllegianceCore: portable bundles plus Windows setup and Linux AppImage.

## New since v0.1.6

This release brings a new loadout screen, fixes for the bugs testers reported in v0.1.6, and a
large round of server and lobby hardening.

**New loadout screen (on by default)**
- A redesigned hangar loadout screen: hulls, slots and parts side by side, with an inline part list,
  a stats bar, the price, and Buy / Launch / Back buttons.
- Ship stats use the original game's labels and formulas, with better/worse arrows when you swap a
  part. Shield recharge is shown in seconds to refill, as in the original.
- Hovering a weapon slot marks its mount on the rotating ship; turret slots show their aim guide and
  the seated gunner's name.
- Works with mouse, keyboard and controller: **Tab** (or pad X) moves between columns, and the number
  keys still restore your saved custom loadouts, as in the original.
- Buying follows the original rules: Launch does nothing if you can't afford the ship, Buy asks your
  team for the shortfall, and refusals use the original wording. **Need $X** turns into **Buy** as
  soon as you have the money.
- Switching hulls works like the original: nothing carries over, the hull starts from your saved or
  default loadout, and saved loadouts are re-bought with the closest available parts.
- Your first dock now shows the parts your ship really has (it could appear empty before).
- Prefer the old screen? **Options → Visuals → Loadout V2** switches back.

**Fixes from tester reports**
- Escape no longer opens a main menu on top of the intro, and an open popup keeps your keys and text.
- Hangar and loadout controls only work on their own screen; the mission briefing shows only while the
  game is starting; undocking always clears the hangar.
- The afterburner sound plays only when you have a working afterburner (pods and empty tanks are
  silent), with the original afterburner sounds for inside and outside views.
- Asteroids spin at the right speed even when a big battle slows the game down.
- Skirmish: your station no longer raises an alert for harmless scout fire.

**Joining games**
- The server list shows whether a running game still has team slots (`[team slots open]` / `[full]` /
  `[no team slots]`), and full-looking games can still be joined.
- Clearer connection errors: HTTP error, unreachable, timed out or invalid response.
- On a password game, Enter jumps to the password field and Enter again joins.

**Servers and lobby: hardening**
- Two security reviews of what a broken or hostile client could send to a server or the lobby led to
  about twenty fixes, including:
  - limits on relayed chat, wrong-password guessing, inbound messages and connection setup;
  - mission settings that reject invalid numbers;
  - treasure only revealed to sides that can see it;
  - combat, ripcord, boarding and squad actions checked the way the original server checks them.
- Buying parts is closer to the original for every client: every bought part is armed, passengers are
  re-seated after a buy, and keeping your spawn kit through a buy costs nothing.

**Experimental (off by default)**
- **Jet HUD**: a military-style green vector HUD in development (pitch ladder, heading and speed tapes,
  flight-path marker, target box and missile lock). Try it with `--visuals hud.jetSkin=on`, then
  **Shift+F12** to cycle Classic → Modern → Jet. It is incomplete; feedback welcome.

**Known limits**: the Android client is experimental and has no download. Behaviour parity with native
Allegiance is about 99.6 % of the tracked rules; the per-area scorecard is in `docs/parity/`.

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
