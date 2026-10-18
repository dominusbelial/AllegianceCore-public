AllegianceCore — Download and play

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
- Windows releases are unsigned: SmartScreen may show an unknown publisher.
  Verify the download/checksum and use your normal trust process.
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
