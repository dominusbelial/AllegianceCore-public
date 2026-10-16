AllegianceCore — Download and play

Extract the WHOLE archive. No .NET SDK, git, local.env or art download is needed.
Linux x64: run ./allegiance.sh (chmod +x allegiance.sh Allegiance if necessary).
Windows x64: double-click play.bat or Allegiance.exe.
Linux desktop shortcut: run ./install-desktop.sh after placing the folder in its
permanent location. Run it again if you move the folder.

Connection: edit player.json in your per-user AllegianceCore config directory:
  Linux: ${XDG_CONFIG_HOME:-~/.config}/AllegianceCore/player.json
  Windows: %APPDATA%\AllegianceCore\player.json
Example (replace the public example hostname with your server operator's address):
  {"Server":"game.example.org","Port":6073,"Pilot":"My Pilot","Lobby":"http://127.0.0.1:5000"}
The launcher creates the file on first run. Server="" opens the in-client lobby;
set Lobby to the directory URL supplied by your operator (the default is local).
There is no bundled server or promised public server. Command-line overrides:
  Linux: ./allegiance.sh game.example.org 6073 "My Pilot"
  Windows: play.bat game.example.org 6073 "My Pilot"
Explicit arguments take precedence over player.json.

Pick a team and press Y to ready up. F1: help/keys. F6: rebind keys.
keymap.json is saved in the same per-user config directory. To use HD sky,
add --sky hd to a direct-connect command. The default black sky also uses the
included moon/galaxy/giant layers. Artwork and sky must stay beside the executable.

Troubleshooting:
- Linux needs an OpenGL 3.3 capable driver, X11/Wayland desktop and audio libraries
  (Debian/Ubuntu: libgl1, libopenal1, libasound2 or libasound2t64, libx11-6,
  libxrandr2, libxi6, libxcursor1, libxinerama1; SDL is bundled by MonoGame).
  Standard .NET system dependencies (glibc, libstdc++, ICU, OpenSSL, zlib)
  are still required; use a supported current Linux distribution.
- To check unpacking without a display/network: ./Allegiance --check-assets
  (Windows: Allegiance.exe --check-assets). Exit 0 means assets passed.
- Missing assets: extract the whole archive again, check checksums, and clear
  stale ALLEGIANCE_ARTWORK_PATH / ALLEGIANCE_SKY_PACK_PATH overrides. A developer
  local.env found above your current directory can also override bundle paths.
- Connection refused: check Server/Port/Lobby with the server operator.
- A malformed player.json prevents startup; fix it or delete it and run the launcher.
- Windows releases are unsigned: SmartScreen may show an unknown publisher.
  Verify the download/checksum and use your normal trust process. macOS is not
  supported by these archives; Gatekeeper signing is outside this release.
- Private Meshy replacement models are optional and are not in this bundle;
  the client uses native FreeAllegiance art. No optional sky layers are omitted.

Licenses: LICENSE, NOTICE, CREDITS.md; artwork/LICENSE; sky/CREDITS.md,
sky/LICENSE and sky/licenses/; Content/Vfx/licenses/. Icon is original MIT artwork.
Releases: https://github.com/dominusbelial/AllegianceCore-public/releases
