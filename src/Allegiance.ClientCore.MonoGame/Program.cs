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
    else if (!a.StartsWith('-'))
    {
        positional.Add(a);
    }
    // Unknown '-'-prefixed flags are ignored.
}

string host = "127.0.0.1";
ushort port = 2326;
string playerName = "monogame";

if (positional.Count >= 1) host = positional[0];
if (positional.Count >= 2 && ushort.TryParse(positional[1], out var p)) port = p;
if (positional.Count >= 3) playerName = positional[2];

// A host+port pair selects the direct-connect (skip-the-lobby) shortcut.
bool haveDirect = positional.Count >= 2;

var opts = new Game1.LaunchOptions
{
    PlayerName = playerName,
    LobbyUrl = lobbyUrl,
};

if (haveDirect)
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
