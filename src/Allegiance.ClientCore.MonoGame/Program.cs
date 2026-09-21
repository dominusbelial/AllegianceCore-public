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
    else if (a == "--capture")
    {
        wantCapture = true;   // the capture args themselves are the positional list
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

// P306 — headless capture launch: `--capture host port name durationSec outdir [maxfps]`.
// Sets DirectHost/Port/PlayerName so the existing direct-connect path runs, and
// attaches the Capture spec so Game1 renders the fleet overview + PNG sequence.
Game1.CaptureSpec? capture = null;
if (wantCapture)
{
    if (positional.Count < 5 ||
        !ushort.TryParse(positional.ElementAtOrDefault(1), out port) ||
        !double.TryParse(positional.ElementAtOrDefault(3), out var dur))
    {
        Console.Error.WriteLine("usage: Client.MonoGame --capture <host> <port> <name> <durationSec> <outdir> [maxfps]");
        return 1;
    }
    host = positional[0];
    playerName = positional.ElementAtOrDefault(2) ?? "capture";
    string outdir = positional[4];
    double maxfps = 30;
    if (positional.Count >= 6 && double.TryParse(positional[5], out var mf)) maxfps = mf;
    capture = new Game1.CaptureSpec
    {
        Host = host,
        Port = port,
        PlayerName = playerName,
        DurationSeconds = dur,
        OutDir = outdir,
        MaxFps = maxfps,
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
