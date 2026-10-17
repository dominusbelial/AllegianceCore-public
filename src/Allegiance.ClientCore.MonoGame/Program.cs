using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.LiveProof;

// Entry point.
//
//   No positional args  → LOBBY (default): open the server browser against the
//                         configured lobby URL, browse/join from the list.
//   [host] [port] [name]→ DIRECT connect (unchanged dev/testing shortcut):
//                         skip the lobby and connect straight to the given
//                         host:port, exactly as before.
//
//   --lobby <url> (or --lobby=<url>) overrides the lobby directory URL in either
//   mode (the client polls its /api/servers endpoint). Default is the lobby's
//   real default listen address, http://127.0.0.1:5000 — confirmed by launching
//   `dotnet run --project src/Allegiance.Lobby` (with no appsettings/launch
//   settings it binds Kestrel's default port 5000). Pass --lobby to point at a
//   lobby on another address/port; do not guess.
//
// Note: ConnectionSession.Connect (fixed 2026-09-15) honours whatever host is
// given here — direct-connect works against any reachable server, not just
// loopback.

string lobbyUrl = ServerListing.LobbyUrlDefault;
var positional = new List<string>();
bool wantCapture = false;
bool wantDebugMode = false;
// P448b — client-only kill switch for the remote-ship dead-reckoner. Default OFF
// (i.e. dead-reckoning stays ON, per LaunchOptions.RemoteShipDeadReckoning=true);
// --noDeadReckon flips it so remote ships render on their raw last-known pose.
bool noDeadReckon = false;
// P447b — client-only kill switch for OWN-ship prediction. Default OFF (i.e.
// prediction stays ON, per LaunchOptions.OwnShipPrediction=true); --noPrediction
// flips it so the local hull/camera render on the raw last ServerShipUpdate pose.
bool noPrediction = false;
// P449a — client-only ambient-dust density (native's user `Debris` preference,
// default 1.0; 0 = off). Mirrors the P378 liveSettings philosophy as a launch
// setting; never sent on the wire.
float debrisDensity = 1.0f;
// P449b — client-only in-flight camera FOV in degrees (native default 50). Mirrors
// the P378 liveSettings philosophy as a launch setting; never sent on the wire.
float flightFovDegrees = 50f;
// P546 — VFX renderer: default ENHANCED; --fx native forces the P545/P263
// native-art path for parity checks (see LaunchOptions.EnhancedVfx).
bool enhancedVfx = true;
// P573 — requested effects tier + volumetric technique (--fx volumetric|displaced).
// Null = platform default (DesktopGL→Volumetric, Android→Enhanced).
Allegiance.ClientCore.State.EffectsQuality? fxEffectsRequested = null;
Allegiance.ClientCore.State.VolumetricTechnique fxVolumetricTech = Allegiance.ClientCore.State.VolumetricTechnique.RaymarchA;
// P547 — soft particles (default ON in Enhanced; --soft off disables) and
// bloom (default OFF; --bloom on enables). See LaunchOptions.
bool softParticles = true;
bool bloomEnabled = false;
// P566 — ship renderer: --ships native (default) | meshy. See LaunchOptions.ShipsNative.
bool shipsNative = true;
// P567 — asteroid renderer: --asteroids native (default) | imported. See LaunchOptions.AsteroidsNative.
bool asteroidsNative = true;
// P576 — sky tier: --sky black (default, P588) | native | hd + the aleph
// destination-glimpse / transit flare: --aleph-glimpse on (default, P607) |
// --aleph-glimpse off.
string skyTier = "black";
bool alephGlimpse = AlephGlimpseFlag.DefaultOn;
// P577 — HD sky global intensity (--sky-intensity F, default 1.0) and the
// low-res asset set (--sky-lowres, for Android).
float skyIntensity = 1.0f;
bool skyLowRes = false;
// P589 — the black-sky space-object toggles (ON by default; --moons off,
// --distant-objects off, --galaxies off disable each).
bool moonsOn = true;
bool distantObjectsOn = true;
bool galaxiesOn = true;
// P592 — the black-sky GIANT object toggle (ON by default; --giant-objects off).
bool giantObjectsOn = true;
// P565 — the offline hull inspector (--inspect) + optional preselect (--inspect-civ / --inspect-hull).
bool inspectRequested = false;
string? inspectCiv = null, inspectHull = null;
string? inspectCatalogue = null;
int inspectShots = 0;
bool inspectDemo = false;
bool inspectRepaired = false;
int inspectAngles = 0;
float inspectDist = 0;
bool inspectStations = false;
string? inspectStation = null;
float? inspectLightTime = null;
float inspectPitch = 22f;
bool inspectNativeOnly = false, inspectHd = false;
// P498 — when set, wrap the artwork asset source in a recorder and write a JSON
// manifest of which Artwork files the client actually opens on exit. Input for
// Android packaging (P499). Optional.
string? assetManifestPath = null;
// Initial window size for fixed-size recordings: --window 2560x1440 (or
// --window=2560x1440). Null keeps the 1280x720 default; client-only.
int? windowWidth = null, windowHeight = null;
static bool TryParseWindowSize(string v, out int w, out int h)
{
    w = h = 0;
    var parts = v.Split('x', 'X');
    return parts.Length == 2
        && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h)
        && w >= 320 && h >= 240 && w <= 16384 && h <= 16384;
}

// P572 — strip the live-proof automation flags (--auto-*, --camera,
// --hide-hud, --autoshots, --autoshot-dir, --auto-dock-after, --dock-sweep,
// --exit-after) into a plan; the remaining args parse exactly as before.
var autoPlan = AutoProofPlan.Parse(args, out var autoRest);
args = autoRest.ToArray();

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a == "--lobby")
    {
        if (i + 1 < args.Length) lobbyUrl = args[++i];
    }
    else if (a.StartsWith("--lobby=", StringComparison.Ordinal))
    {
        lobbyUrl = a.Substring("--lobby=".Length);
    }
    else if (a == "--debris")
    {
        if (i + 1 < args.Length
            && float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var fd))
            debrisDensity = fd;
    }
    else if (a.StartsWith("--debris=", StringComparison.Ordinal))
    {
        if (float.TryParse(a.Substring("--debris=".Length),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var fd2))
            debrisDensity = fd2;
    }
    else if (a == "--window" || a.StartsWith("--window=", StringComparison.Ordinal))
    {
        string? v = a == "--window" ? (i + 1 < args.Length ? args[++i] : null) : a.Substring("--window=".Length);
        if (v is not null && TryParseWindowSize(v, out var ww, out var wh))
        {
            windowWidth = ww;
            windowHeight = wh;
        }
        else
            Console.Error.WriteLine($"[monogame] --window: expected WxH (e.g. 2560x1440), got '{v}' - using the default size");
    }
    else if (a == "--flightFov")
    {
        // P449b — client-only in-flight camera FOV override (degrees; native default
        // 50). Defaults in LaunchOptions.FlightFovDegrees; this flag overrides.
        if (i + 1 < args.Length
            && float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ff))
            flightFovDegrees = ff;
    }
    else if (a.StartsWith("--flightFov=", StringComparison.Ordinal))
    {
        if (float.TryParse(a.Substring("--flightFov=".Length),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ff2))
            flightFovDegrees = ff2;
    }
    else if (a == "--asset-manifest")
    {
        if (i + 1 < args.Length) assetManifestPath = args[++i];
    }
    else if (a.StartsWith("--asset-manifest=", StringComparison.Ordinal))
    {
        assetManifestPath = a.Substring("--asset-manifest=".Length);
    }
    else if (a == "--capture")
    {
        wantCapture = true;   // the capture args themselves are the positional list
    }
    else if (a == "--debugMode")
    {
        // P385 — client-only boolean presence flag: flip the capture-overview
        // camera FOV to native's real default (50° instead of 100°) for a live
        // framing comparison against native. Set by hand alongside the server's
        // liveSettings.debugMode; never sent on the wire (see the task brief).
        wantDebugMode = true;
    }
    else if (a == "--noDeadReckon")
    {
        // P448b — client-only boolean presence flag: kill switch for the remote-ship
        // dead-reckoner. Renders every REMOTE ship on its raw last-known pose instead
        // of the P448a-dead-reckoned pose, so the two can be A/B'd live. Default OFF
        // (dead-reckoning stays ON); never sent on the wire (see the task brief).
        noDeadReckon = true;
    }
    else if (a == "--noPrediction")
    {
        // P447b — client-only boolean presence flag: kill switch for OWN-ship
        // prediction. Renders the local hull + camera on the raw last ServerShipUpdate
        // pose (today's behaviour) so the latency benefit can be A/B'd live (prediction
        // ON vs OFF). Default OFF (prediction stays ON); never sent on the wire (see
        // the task brief / P378 liveSettings philosophy).
        noPrediction = true;
    }
    else if (a == "--fx" || a.StartsWith("--fx=", StringComparison.Ordinal))
    {
        // P546 — VFX renderer switch: --fx native | enhanced | volumetric | displaced.
        // P573 extends it: 'volumetric' = raymarched Technique A, 'displaced' =
        // Technique B (both are the Volumetric tier); native/enhanced unchanged.
        // Used by captures to force the Native path for parity checks.
        string? v = a == "--fx" ? (i + 1 < args.Length ? args[++i] : null)
                                : a.Substring("--fx=".Length);
        var parsed = Allegiance.ClientCore.State.EffectsQualityResolver.ParseFx(v);
        if (parsed is { } p)
        {
            fxEffectsRequested = p.Quality;
            fxVolumetricTech = p.Tech;
            // Legacy bool stays in sync for the existing native/enhanced paths.
            enhancedVfx = p.Quality != Allegiance.ClientCore.State.EffectsQuality.Native;
        }
        else
        {
            Console.Error.WriteLine($"[monogame] --fx: expected 'native|enhanced|volumetric|displaced', got '{v}' - using the default");
        }
    }
    else if (a == "--soft" || a.StartsWith("--soft=", StringComparison.Ordinal))
    {
        // P547 — soft-particle switch: --soft off|on (default on in Enhanced).
        string? v = a == "--soft" ? (i + 1 < args.Length ? args[++i] : null)
                                  : a.Substring("--soft=".Length);
        if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase))
            softParticles = false;
        else if (string.Equals(v, "on", StringComparison.OrdinalIgnoreCase))
            softParticles = true;
        else
            Console.Error.WriteLine($"[monogame] --soft: expected 'off' or 'on', got '{v}' - using the default");
    }
    else if (a == "--bloom" || a.StartsWith("--bloom=", StringComparison.Ordinal))
    {
        // P547 — bloom switch: --bloom on|off (default off). Capture CLI toggle.
        string? v = a == "--bloom" ? (i + 1 < args.Length ? args[++i] : null)
                                   : a.Substring("--bloom=".Length);
        if (string.Equals(v, "on", StringComparison.OrdinalIgnoreCase))
            bloomEnabled = true;
        else if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase))
            bloomEnabled = false;
        else
            Console.Error.WriteLine($"[monogame] --bloom: expected 'on' or 'off', got '{v}' - using the default (off)");
    }
    else if (a == "--ships" || a.StartsWith("--ships=", StringComparison.Ordinal))
    {
        // P566 — ship renderer switch: --ships native (default) | meshy.
        // Meshy keeps the P287 AI content models; native renders the ORIGINAL
        // .mdl meshes + native textures (falling back to Meshy on decode failure).
        string? v = a == "--ships" ? (i + 1 < args.Length ? args[++i] : null)
                                   : a.Substring("--ships=".Length);
        if (string.Equals(v, "native", StringComparison.OrdinalIgnoreCase))
            shipsNative = true;
        else if (string.Equals(v, "meshy", StringComparison.OrdinalIgnoreCase))
            shipsNative = false;
        else
            Console.Error.WriteLine($"[monogame] --ships: expected 'native' or 'meshy', got '{v}' - using the default (native)");
    }
    else if (a == "--asteroids" || a.StartsWith("--asteroids=", StringComparison.Ordinal))
    {
        // P567 — asteroid renderer switch: --asteroids native (default) | imported.
        // Imported keeps the P286 shared variants (which now wind like native, P567);
        // native renders each rock from its OWN original .mdl mesh + texture
        // (falling back to imported on decode failure).
        string? v = a == "--asteroids" ? (i + 1 < args.Length ? args[++i] : null)
                                       : a.Substring("--asteroids=".Length);
        if (string.Equals(v, "native", StringComparison.OrdinalIgnoreCase))
            asteroidsNative = true;
        else if (string.Equals(v, "imported", StringComparison.OrdinalIgnoreCase))
            asteroidsNative = false;
        else
            Console.Error.WriteLine($"[monogame] --asteroids: expected 'native' or 'imported', got '{v}' - using the default (native)");
    }
    else if (a == "--sky" || a.StartsWith("--sky=", StringComparison.Ordinal))
    {
        // P576/P588 — sky tier: --sky black (default) | native | hd.
        string? v = a == "--sky" ? (i + 1 < args.Length ? args[++i] : null)
                                 : a.Substring("--sky=".Length);
        if (string.Equals(v, "black", StringComparison.OrdinalIgnoreCase))
            skyTier = "black";
        else if (string.Equals(v, "native", StringComparison.OrdinalIgnoreCase))
            skyTier = "native";
        else if (string.Equals(v, "hd", StringComparison.OrdinalIgnoreCase))
            skyTier = "hd";
        else
            Console.Error.WriteLine($"[monogame] --sky: expected 'black', 'native' or 'hd', got '{v}' - using the default (black)");
    }
    else if (a == "--aleph-glimpse" || a.StartsWith("--aleph-glimpse=", StringComparison.Ordinal))
    {
        // P576-B/P607 — the aleph destination-glimpse / transit flare:
        // --aleph-glimpse on (default, P607) | --aleph-glimpse off. Off still
        // keeps the native arrival-star swell for visible transits (scope 2),
        // only the glimpse/funnel-boost/flare extras are disabled.
        string? v = a == "--aleph-glimpse" ? (i + 1 < args.Length ? args[++i] : null)
                                           : a.Substring("--aleph-glimpse=".Length);
        if (AlephGlimpseFlag.Parse(v) is { } ag)
            alephGlimpse = ag;
        else
            Console.Error.WriteLine("[monogame] --aleph-glimpse: expected 'on' or 'off', got '{0}' - using the default (on)", v);
    }
    else if (a == "--sky-intensity" || a.StartsWith("--sky-intensity=", StringComparison.Ordinal))
    {
        // P577 — global HD-sky intensity (readability guard; default 1.0).
        string? v = a == "--sky-intensity" ? (i + 1 < args.Length ? args[++i] : null)
                                           : a.Substring("--sky-intensity=".Length);
        if (v is not null && float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var si))
            skyIntensity = Math.Clamp(si, 0f, 2f);
        else
            Console.Error.WriteLine($"[monogame] --sky-intensity: expected a number, got '{v}' - using the default (1.0)");
    }
    else if (a == "--sky-lowres")
    {
        // P577 — use the Android/low-res HD sky asset set.
        skyLowRes = true;
    }
    else if (a == "--moons" || a.StartsWith("--moons=", StringComparison.Ordinal))
    {
        // P589 — black-sky moons toggle (--moons on|off, default on).
        string? v = a == "--moons" ? (i + 1 < args.Length ? args[++i] : null)
                                   : a.Substring("--moons=".Length);
        if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)) moonsOn = false;
        else if (string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) moonsOn = true;
        else Console.Error.WriteLine($"[monogame] --moons: expected 'on' or 'off', got '{v}' - using the default");
    }
    else if (a == "--distant-objects" || a.StartsWith("--distant-objects=", StringComparison.Ordinal))
    {
        // P589 — black-sky distant-galaxy/nebula cut-outs toggle (default on).
        string? v = a == "--distant-objects" ? (i + 1 < args.Length ? args[++i] : null)
                                             : a.Substring("--distant-objects=".Length);
        if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)) distantObjectsOn = false;
        else if (string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) distantObjectsOn = true;
        else Console.Error.WriteLine($"[monogame] --distant-objects: expected 'on' or 'off', got '{v}' - using the default");
    }
    else if (a == "--galaxies" || a.StartsWith("--galaxies=", StringComparison.Ordinal))
    {
        // P589 — black-sky deep-field galaxy + Little-Red-Dot field toggle (default on).
        string? v = a == "--galaxies" ? (i + 1 < args.Length ? args[++i] : null)
                                      : a.Substring("--galaxies=".Length);
        if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)) galaxiesOn = false;
        else if (string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) galaxiesOn = true;
        else Console.Error.WriteLine($"[monogame] --galaxies: expected 'on' or 'off', got '{v}' - using the default");
    }
    else if (a == "--giant-objects" || a.StartsWith("--giant-objects=", StringComparison.Ordinal))
    {
        // P592 — black-sky GIANT sky-object toggle (default on).
        string? v = a == "--giant-objects" ? (i + 1 < args.Length ? args[++i] : null)
                                           : a.Substring("--giant-objects=".Length);
        if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)) giantObjectsOn = false;
        else if (string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) giantObjectsOn = true;
        else Console.Error.WriteLine($"[monogame] --giant-objects: expected 'on' or 'off', got '{v}' - using the default");
    }
    else if (a == "--inspect-shots" || a.StartsWith("--inspect-shots=", StringComparison.Ordinal))
    {
        string? v = a == "--inspect-shots" ? (i + 1 < args.Length ? args[++i] : null)
                                           : a.Substring("--inspect-shots=".Length);
        if (v is not null && int.TryParse(v, out var n)) inspectShots = Math.Max(0, n);
    }
    else if (a == "--inspect-demo")
    {
        // P565 — run the scripted showcase sequence in inspect mode (visual proof).
        inspectRequested = true;
        inspectDemo = true;
    }
    else if (a == "--inspect-repaired")
    {
        // P581 — preview the REPAIRED Meshy model in the inspector (view-only).
        inspectRequested = true;
        inspectRepaired = true;
    }
    else if (a == "--inspect-catalogue" || a.StartsWith("--inspect-catalogue=", StringComparison.Ordinal))
    {
        // P581b — drive the catalogue browser to a specific replacement model id on the
        // current hull (e.g. --inspect-catalogue bios-carrier). Sets the browser index so
        // the meshy side draws that catalogue model (repaired preferred).
        string? v = a == "--inspect-catalogue" ? (i + 1 < args.Length ? args[++i] : null)
                                               : a.Substring("--inspect-catalogue=".Length);
        if (!string.IsNullOrWhiteSpace(v)) inspectCatalogue = v!.Trim();
        inspectRequested = true;
    }
    else if (a == "--inspect-angles" || a.StartsWith("--inspect-angles=", StringComparison.Ordinal))
    {
        string? v = a == "--inspect-angles" ? (i + 1 < args.Length ? args[++i] : null)
                                            : a.Substring("--inspect-angles=".Length);
        if (v is not null && int.TryParse(v, out var n)) inspectAngles = Math.Max(0, n);
    }
    else if (a == "--inspect-dist" || a.StartsWith("--inspect-dist=", StringComparison.Ordinal))
    {
        // P581c — close-up inspector framing: override the orbit distance (e.g. 12).
        string? v = a == "--inspect-dist" ? (i + 1 < args.Length ? args[++i] : null)
                                          : a.Substring("--inspect-dist=".Length);
        if (v is not null && float.TryParse(v, System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out var d))
            inspectDist = d;
    }
    else if (a == "--inspect")
    {
        // P565 — the offline hull inspector (no server connect).
        inspectRequested = true;
    }
    else if (a == "--inspect-hd")
    {
        inspectRequested = true; inspectHd = true;
    }
    else if (a == "--inspect-native-only")
    {
        inspectNativeOnly = true;
    }
    else if (a == "--inspect-station" && i + 1 < args.Length)
    {
        inspectRequested = true;
        inspectStations = true;
        inspectStation = args[++i];
    }
    else if (a == "--inspect-pitch" && i + 1 < args.Length)
    {
        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pitch)
            && float.IsFinite(pitch))
            inspectPitch = Math.Clamp(pitch, -89f, 89f);
    }
    else if (a == "--inspect-light-time" && i + 1 < args.Length)
    {
        if (float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lightTime)
            && float.IsFinite(lightTime) && lightTime >= 0f)
            inspectLightTime = lightTime;
    }
    else if (a == "--inspect-stations")
    {
        // P615 — the offline STATION inspector: walk the core's 143 station
        // types under the same orbit/angle-sweep/mark-bad workflow as hulls,
        // native .mdl + texture vs the procedural placeholder.
        inspectRequested = true;
        inspectStations = true;
    }
    else if (a == "--inspect-civ" || a.StartsWith("--inspect-civ=", StringComparison.Ordinal))
    {
        string? v = a == "--inspect-civ" ? (i + 1 < args.Length ? args[++i] : null)
                                         : a.Substring("--inspect-civ=".Length);
        if (v is not null) inspectCiv = v;
    }
    else if (a == "--inspect-hull" || a.StartsWith("--inspect-hull=", StringComparison.Ordinal))
    {
        string? v = a == "--inspect-hull" ? (i + 1 < args.Length ? args[++i] : null)
                                          : a.Substring("--inspect-hull=".Length);
        if (v is not null) inspectHull = v;
    }
    else if (!a.StartsWith('-'))
    {
        positional.Add(a);
    }
    // Unknown '-'-prefixed flags are ignored.
}

string host = "127.0.0.1";
ushort port = 2326;
string playerName = "monogame";

// P306 — headless capture launch: `--capture host port name durationSec outdir [maxfps] [camera]`.
// Sets DirectHost/Port/PlayerName so the existing direct-connect path runs, and
// attaches the Capture spec so Game1 renders the fleet overview + PNG sequence.
// Optional trailing `camera` word "chase" selects the local ship's REAL chase
// camera (P338 live-proof) instead of the default fleet-overview camera.
Game1.CaptureSpec? capture = null;
if (wantCapture)
{
    if (positional.Count < 5 ||
        !ushort.TryParse(positional.ElementAtOrDefault(1), out port) ||
        !double.TryParse(positional.ElementAtOrDefault(3), out var dur))
    {
        Console.Error.WriteLine("usage: Client.MonoGame --capture <host> <port> <name> <durationSec> <outdir> [maxfps] [chase]");
        return 1;
    }
    host = positional[0];
    playerName = positional.ElementAtOrDefault(2) ?? "capture";
    string outdir = positional[4];
    double maxfps = 30;
    if (positional.Count >= 6 && double.TryParse(positional[5], out var mf)) maxfps = mf;
    bool chase = positional.Count >= 7 && positional[6].Equals("chase", StringComparison.OrdinalIgnoreCase);
    capture = new Game1.CaptureSpec
    {
        Host = host,
        Port = port,
        PlayerName = playerName,
        DurationSeconds = dur,
        OutDir = outdir,
        MaxFps = maxfps,
        Chase = chase,
    };
}
else
{
    if (positional.Count >= 1) host = positional[0];
    if (positional.Count >= 2 && ushort.TryParse(positional[1], out var p)) port = p;
    if (positional.Count >= 3) playerName = positional[2];
}

// A host+port pair selects the direct-connect (skip-the-lobby) shortcut.
bool haveDirect = positional.Count >= 2;

// P498 — optional asset-manifest recording: wrap the desktop artwork source in
// a recorder and have Game1 read through it, then dump the opened-file list on
// exit. Defaults to null (no recording), preserving today's behaviour.
RecordingAssetSource? recorder = assetManifestPath is not null
    ? new RecordingAssetSource(AssetSources.Game)
    : null;

var opts = new Game1.LaunchOptions
{
    PlayerName = playerName,
    LobbyUrl = lobbyUrl,
    Capture = capture,
    DebugMode = wantDebugMode,
    // P448b — RemoteShipDeadReckoning defaults ON in LaunchOptions; only the
    // explicit --noDeadReckon flag turns it off (see the flag parse above).
    RemoteShipDeadReckoning = !noDeadReckon,
    // P447b — OwnShipPrediction defaults ON in LaunchOptions; only the explicit
    // --noPrediction flag turns it off (see the flag parse above).
    OwnShipPrediction = !noPrediction,
    // P449a — ambient-dust density (default 1.0; --debris overrides, 0 = off).
    DebrisDensity = debrisDensity,
    // P449b — in-flight camera FOV (native default 50°; --flightFov overrides).
    FlightFovDegrees = flightFovDegrees,
    // P546 — VFX renderer (Enhanced default; --fx native forces native-art path).
    EnhancedVfx = enhancedVfx,
    // P573 — effects tier + volumetric technique (--fx volumetric|displaced).
    EffectsRequested = fxEffectsRequested,
    VolumetricTech = fxVolumetricTech,
    // P547 — soft particles (default on in Enhanced; --soft off) + bloom
    // (default off; --bloom on).
    SoftParticles = softParticles,
    Bloom = bloomEnabled,
    // P566 — ships renderer (default native; --ships native|meshy).
    ShipsNative = shipsNative,
    // P567 — asteroid renderer (default native; --asteroids native|imported).
    AsteroidsNative = asteroidsNative,
    // P576/P588 — sky tier (default black; --sky black|native|hd) + aleph glimpse (default ON, P607).
    SkyTier = skyTier,
    AlephGlimpse = alephGlimpse,
    // P577 — global HD-sky intensity (default 1.0) + low-res asset set.
    SkyIntensity = skyIntensity,
    SkyLowRes = skyLowRes,
    // P589 — black-sky space-object toggles (moons / distant objects /
    // galaxies; ON by default, --… off disables).
    MoonsOn = moonsOn,
    DistantObjectsOn = distantObjectsOn,
    GalaxiesOn = galaxiesOn,
    // P592 — the black-sky GIANT object (ON by default, --giant-objects off).
    GiantObjectsOn = giantObjectsOn,
    // P565 — the offline hull inspector (+ optional civ/hull preselect).
    Inspect = inspectRequested,
    InspectCiv = inspectCiv,
    InspectHull = inspectHull,
    InspectShots = inspectShots,
    InspectDemo = inspectDemo,
    InspectRepaired = inspectRepaired,
    InspectAngles = inspectAngles,
    InspectDist = inspectDist,
    InspectStations = inspectStations,
    InspectStation = inspectStation,
    InspectLightTime = inspectLightTime,
    InspectPitch = inspectPitch,
    InspectNativeOnly = inspectNativeOnly,
    InspectHd = inspectHd,
    InspectCatalogue = inspectCatalogue,
    // P572 — live-proof automation plan (null when no --auto-* flag was given).
    AutoProof = autoPlan.AnyActive ? autoPlan : null,
    WindowWidth = windowWidth,
    WindowHeight = windowHeight,
    // P498 — when --asset-manifest is given, the game reads artwork through the
    // recorder so the opened-file manifest is captured.
    AssetSource = recorder,
};

if (capture is not null)
{
    opts.DirectHost = capture.Host;
    opts.DirectPort = capture.Port;
    Console.WriteLine($"[monogame] CAPTURE mode: {capture.Host}:{capture.Port} as '{capture.PlayerName}', {capture.DurationSeconds:0.##}s -> {capture.OutDir}");
}
else if (haveDirect)
{
    opts.DirectHost = host;
    opts.DirectPort = port;
    Console.WriteLine($"[monogame] direct connect: {host}:{port} as '{playerName}' (lobby skipped)");
}
else
{
    // P731 — no game-starting arguments: open the intro screen (native's
    // introscreen.cpp) where the player chooses single or multiplayer. This is
    // what the P644/P726 desktop shortcut runs. Command-line args ([host]
    // [port] [name], capture, inspect) still bypass the intro entirely.
    opts.Intro = true;
    Console.WriteLine("[monogame] intro mode: no positional args (choose Single Player or Multiplayer at start)");
}

using var game = new Game1(opts);
if (recorder is not null)
{
    // P498 — on exit, serialize the manifest of Artwork files the client
    // actually opened during this session.
    game.Exiting += (_, _) =>
    {
        string json = recorder.ToManifestJson(AssetSources.ResolveRoot());
        System.IO.File.WriteAllText(assetManifestPath!, json);
        Console.WriteLine($"[manifest] recorded {recorder.OpenedCount} opened Artwork files " +
                          $"({recorder.TotalBytes:N0} bytes) -> {assetManifestPath}");
    };
}
game.Run();
return 0;
