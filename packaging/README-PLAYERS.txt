AllegianceCore — Download and play

Windows setup (Allegiance-win-x64-setup.exe): run the installer. The default
"Install for me only" needs no administrator and installs to
%LOCALAPPDATA%\Programs\AllegianceCore. You can instead choose all users (UAC
approval; Program Files). Start-menu and optional desktop shortcuts open the
intro screen: choose Single Player to play offline, or Multiplayer.
Run a newer setup to upgrade in place. Uninstall through Windows Installed apps;
player.json and keymap.json in %APPDATA%\AllegianceCore are kept.
The installer and portable EXE are unsigned. After verifying the checksum,
SmartScreen may require "More info" -> "Run anyway" (unknown publisher).
Code signing requires a certificate and is outside this release.

Linux AppImage (Allegiance-linux-x64.AppImage): place it in a permanent folder,
then chmod +x Allegiance-linux-x64.AppImage and double-click it, or run:
  ./Allegiance-linux-x64.AppImage
No .NET installation or root access is needed. Optional AppImageLauncher or
Gear Lever can add it to your desktop menu. Replace the file to upgrade; delete
it to uninstall. Preferences stay in ~/.config/AllegianceCore (or XDG_CONFIG_HOME).
If FUSE mounting is unavailable, use:
  ./Allegiance-linux-x64.AppImage --appimage-extract
  ./squashfs-root/AppRun
Or run with APPIMAGE_EXTRACT_AND_RUN=1. Do not put custom files inside the
extracted application. Its payload can stay read-only; preferences are per-user.

Portable downloads:

Extract the WHOLE archive. No .NET SDK, git, local.env or art download is needed.
Linux x64: run ./allegiance.sh (chmod +x allegiance.sh Allegiance if necessary).
Windows x64: double-click play.bat or Allegiance.exe.
macOS Apple silicon (arm64), macOS 14 or later: drag AllegianceCore.app to
Applications and open it. Keep the entire .app intact. This build is unsigned:
right-click the app -> Open. If Gatekeeper still blocks it after verifying the
archive checksum, use Terminal:
  xattr -dr com.apple.quarantine /Applications/AllegianceCore.app
Then open the app again. Signing/notarization is outside this release.
Linux desktop shortcut: run ./install-desktop.sh after placing the folder in its
permanent location. Run it again if you move the folder.

Connection: edit player.json in your per-user AllegianceCore config directory:
  Linux: ${XDG_CONFIG_HOME:-~/.config}/AllegianceCore/player.json
  Windows: %APPDATA%\AllegianceCore\player.json
  macOS: ~/Library/Application Support/AllegianceCore/player.json
Example (replace the public example hostname with your server operator's address):
  {"Server":"game.example.org","Port":6073,"Pilot":"My Pilot","Lobby":"http://127.0.0.1:5000"}
The launcher creates the file on first run. The client opens the main menu.
Single Player -> Skirmish runs entirely on your machine with the bundled host.
Server/Port remember a Direct Connect destination; Lobby sets the multiplayer
directory URL supplied by your operator (the default is local). No public server
is promised. Command-line overrides:
  Linux: ./allegiance.sh game.example.org 6073 "My Pilot"
  Windows: play.bat game.example.org 6073 "My Pilot"
  macOS: /Applications/AllegianceCore.app/Contents/MacOS/Allegiance game.example.org 6073 "My Pilot"
Explicit arguments take precedence over player.json.

Pick a team and press Y to ready up. F1: help/keys. F6: rebind keys.
keymap.json is saved in the same per-user config directory. To use HD sky,
add --sky hd to a direct-connect command. The default black sky also uses the
included moon/galaxy/giant layers. Artwork and sky must stay with the runtime
(on macOS these live inside AllegianceCore.app/Contents/Resources).
Music stays silent when real flight/station beds are absent; no generated tones.

Troubleshooting:
- Linux needs an OpenGL 3.3 capable driver, X11/Wayland desktop and audio libraries
  (Debian/Ubuntu: libgl1, libopenal1, libasound2 or libasound2t64, libx11-6,
  libxrandr2, libxi6, libxcursor1, libxinerama1; SDL is bundled by MonoGame).
  Standard .NET system dependencies (glibc, libstdc++, ICU, OpenSSL, zlib)
  are still required; use a supported current Linux distribution.
- To check unpacking without a display/network: ./Allegiance --check-assets
  (Windows: Allegiance.exe --check-assets; macOS:
  /Applications/AllegianceCore.app/Contents/MacOS/Allegiance --check-assets).
  Exit 0 means assets passed.
- Missing assets: extract the whole archive again, check checksums, and clear
  stale ALLEGIANCE_ARTWORK_PATH / ALLEGIANCE_SKY_PACK_PATH overrides. A developer
  local.env found above your current directory can also override bundle paths.
- Connection refused: check Server/Port/Lobby with the server operator.
- A malformed player.json prevents startup; fix it or delete it and run the launcher.
- Windows SmartScreen: verify the download/checksum, then "More info" ->
  "Run anyway" for this unsigned release.
- The client uses native FreeAllegiance art. No optional sky layers are omitted.

Licenses: LICENSE, NOTICE, CREDITS.md; artwork/LICENSE; sky/CREDITS.md,
sky/LICENSE and sky/licenses/; Content/Vfx/licenses/. Icon is original MIT artwork.
Releases: https://github.com/dominusbelial/AllegianceCore-public/releases

Single player (offline)
----------------------
Choose Single Player, then Skirmish from the main menu. Select both races,
size, tech mode and bot difficulty. You fly on side 0; AI commanders run the
teams. Small is 5v5 including you, Medium 15v15, Large 30v30, Epic 60v60.
Epic needs more CPU and may run in automatic slow motion.
Use Pause or Ctrl+P to pause/resume. Ctrl+F10 ends the skirmish and returns
to setup. The in-game menu also offers Pause and End skirmish.
For a headless local benchmark run: Allegiance --skirmish-smoke 60 Small
(replace Allegiance with Allegiance.exe on Windows).

Training (offline)
------------------
From the main menu choose Single Player, then Training on the chooser to
open the mission list (the native single-player button is the training
entry). Pick a mission with Up/Down and press Enter to start it. The native
training set is playable offline against the embedded host:

  1. Introduction   (slideshow/voice-over — not yet ported)
  2. Basic Flight   (radar, throttle, booster, autopilot home)
  3. Basic Weaponry (weapon mounts, ammo, energy, firing)
  4. Enemy Engagement (engage and destroy hostile craft)
  5. Command View   (command map, buying and giving orders)
  6. Practice Arena (a safe arena to practise ships and loadouts)
  8. Nanite         (mount the Nanite Repair System, repair the station,
                     defend the miners, escort the bomber)

Mission 7/Live was removed from the native set and does not appear. The
"Free Flight" dev mission (native mission 10) has no training-screen button —
natively it launches only through the bStartTraining debug flag — so it is
started here the same way: run the client with ALLEGIANCE_TRAIN_MISSION=10.

Each mission runs entirely on your machine; nothing is sent online. Training
uses the same controls as normal flight (F1 shows a control overview).

Benchmark and recommended settings
----------------------------------
The first launch offers an optional benchmark; Escape skips it. Benchmark is
also available from the main menu. Select a fixed skirmish size or the bundled
recorded battle. Skirmish runs the local server and bots; replay uses the client
alone. A skirmish run measures 150 seconds of battle, repeated three times.
Results show the middle run, the measured bottleneck, and reasons for each
suggested setting. A short check tries those settings and can reduce them once.
Click Apply (or press Enter) to save them. Escape keeps your previous settings.
If the target remains unmet, the results say so.

Command line:
  Allegiance --benchmark skirmish:small
  Allegiance --benchmark skirmish:medium
  Allegiance --benchmark skirmish:large
  Allegiance --benchmark skirmish:epic
  Allegiance --benchmark replay
  Allegiance --benchmark replay:/path/to/battle.alr

Optional: --benchmark-output /path/to/results.json, --benchmark-exit (close after
results), --benchmark-seconds 3 (short smoke), --benchmark-headless (no graphics;
FPS/GPU and graphics auto-tune are explicitly unavailable). To record a real
skirmish's incoming transport stream, add --benchmark-record /path/to/battle.alr.
Normal player bundles include the battle recording and all local host components.

OPTIONAL ANONYMOUS BENCHMARK SHARING
Sharing defaults to No. When a collector is configured, the benchmark asks
"Share these results to help improve the recommendations?" Press S for sharing
settings, Y for Yes, N for No, V for "View what's sent", or D for "Delete my shared
data" (also resets the random install ID and turns sharing off). You can change
this later through Options / Settings / Anonymous benchmark sharing.

Shared fields are hardware summary, benchmark timings, settings used/suggested,
game version/commit and a random ID created only on opt-in. After three play
sessions or seven days, an optional follow-up identifies settings you changed.
No pilot names, usernames, hostnames, file paths, serials or IPs are stored in the
collector. Data is private: raw rows 24 months, anonymous aggregates indefinitely.
Deletion removes raw results/follow-ups; anonymous aggregates remain. Results are
always saved locally; uploads and deletion retry offline without blocking play.
See docs/benchmark/PRIVACY.md in the source distribution for the full notice.
