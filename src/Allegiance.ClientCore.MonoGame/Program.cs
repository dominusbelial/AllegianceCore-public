using Allegiance.ClientCore.MonoGame;

// Entry point: parse [host] [port] [playerName] and run the MonoGame client.
// host defaults to 127.0.0.1 — the same loopback convention Allegiance.Bot /
// Explorer use. Note: ConnectionSession.Connect pins the transport to
// 127.0.0.1, so host is parsed for CLI symmetry and only loopback is honoured
// (any other value is warned about, not silently ignored).

string host = "127.0.0.1";
ushort port = 2326;
string playerName = "monogame";

if (args.Length >= 1) host = args[0];
if (args.Length >= 2 && ushort.TryParse(args[1], out var p)) port = p;
if (args.Length >= 3) playerName = args[2];

if (host != "127.0.0.1" && host != "localhost")
    Console.WriteLine($"[monogame] warning: host '{host}' ignored — ConnectionSession connects on 127.0.0.1");

Console.WriteLine($"[monogame] launching for 127.0.0.1:{port} as '{playerName}' ...");

using var game = new Game1(port, playerName);
game.Run();
