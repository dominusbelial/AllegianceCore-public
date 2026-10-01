using Allegiance.ClientCore.MonoGame;

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
    Console.WriteLine($"[monogame] lobby mode: browsing {lobbyUrl}/api/servers (pass [host] [port] [name] to skip the lobby)");
}

using var game = new Game1(opts);
game.Run();
return 0;
