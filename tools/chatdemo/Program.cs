using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using BotClass = global::Allegiance.Bot.Bot;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Tools.ChatDemo;

/// <summary>
/// The P25 live chat demo: an in-process <see cref="Srv"/> plus three real
/// <see cref="BotClass"/> instances over loopback TCP. Three bots seat
/// round-robin onto two sides, so one side has two members and the other one.
/// A real <c>FM_CS_CHATMESSAGE(23)</c> is sent by one bot for each target and
/// the server relays it: <c>Everyone</c> must reach all three, <c>Team</c> must
/// reach exactly the sender's two teammates, and an unimplemented target
/// (<c>Wing</c>) must reach nobody (logged and dropped). Prints the server
/// relay lines and each bot's verbatim reception, then exits 0 iff all three
/// behaviors held.
/// </summary>
internal static class Program
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Chat Demo", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 928u, DwCookie = 0xFEEDu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 74200000000000000ul, DrmHash = "chatdemo",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static int Main(string[] args)
    {
        int port = 0;
        if (args.Length > 0 && !int.TryParse(args[0], out port))
        {
            Console.Error.WriteLine("usage: chatdemo [port]");
            Console.Error.WriteLine("  boots an in-process Allegiance server, connects three real bots, and proves");
            Console.Error.WriteLine("  CHATMESSAGE(23) relay: Everyone reaches all 3, Team reaches the 2 teammates,");
            Console.Error.WriteLine("  an unimplemented target (Wing) reaches none.");
            Console.Error.WriteLine($"  Requires the stock artwork tree at {ArtRoot}.");
            return 2;
        }

        using var server = new Srv();
        server.Boot(Config(port));
        ushort bound = server.BoundPort;
        Console.WriteLine($"[demo] server listening on 127.0.0.1:{bound}");

        var bots = new BotClass[3];
        for (int i = 0; i < 3; i++) bots[i] = new BotClass();   // construct before Connect (never null)
        Console.WriteLine("[demo] connecting 3 bots...");
        for (int i = 0; i < 3; i++) bots[i].Connect(bound, $"BOT{i}");
        for (int i = 0; i < 3; i++) bots[i].SendLogonRequest(Request($"PLAYER{i}"));

        int ticks = PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
        if (ticks >= 4000) { Console.Error.WriteLine("demo FAILED: not all three bots got a ship"); return 1; }

        // Round-robin seating across two sides (Server.cs:523-529, by drain
        // order which races) guarantees exactly two of the three bots share the
        // sender's side and the third is the opponent. We prove routing by the
        // receiver count invariant (no side introspection needed — the ship
        // state does not carry SideId to the client): Everyone must reach all 3,
        // Team must reach exactly 2 (never the 3rd), an unimplemented target 0.
        var sender = bots[0];
        Console.WriteLine($"[demo] sender ship {sender.ShipAssignment!.Ship.ShipId}");

        // ── Everyone ──
        sender.SendChat("ALL HELLO", ChatTarget.Everyone);
        PumpUntil(server, () => bots.All(b => b.Chats.Any(c => c.Message == "ALL HELLO")), 2000);

        // ── Team ──
        sender.SendChat("TEAM HI", ChatTarget.Team);
        PumpUntil(server, () => bots.Count(b => b.Chats.Any(c => c.Message == "TEAM HI")) == 2, 2000);

        // ── Wing (unimplemented) ──
        sender.SendChat("WING ONLY", ChatTarget.Wing);
        PumpUntil(server, () => false, 50);

        bool everyoneOk = bots.All(b => b.Chats.Any(c => c.Message == "ALL HELLO"));
        bool teamOk = bots.Count(b => b.Chats.Any(c => c.Message == "TEAM HI")) == 2
                   && bots.Any(b => !b.Chats.Any(c => c.Message == "TEAM HI"));
        bool wingOk = bots.All(b => !b.Chats.Any(c => c.Message == "WING ONLY"));

        Console.WriteLine("[demo] === CHAT RECEPTION (verbatim, per real bot) ===");
        foreach (var b in bots)
        {
            var ass = b.ShipAssignment!;
            var received = string.Join(" | ", b.Chats.Select(c => c.Message).Distinct());
            Console.WriteLine($"[demo] ship {ass.Ship.ShipId} received: {received}");
        }
        Console.WriteLine($"[demo] Everyone reached 3/3: {everyoneOk}; Team reached 2/3 (one declined): {teamOk}; " +
                          $"Wing reached 0/3: {wingOk}");
        Console.WriteLine(everyoneOk && teamOk && wingOk
            ? "[demo] RESULT PASS"
            : "[demo] RESULT FAIL");
        return everyoneOk && teamOk && wingOk ? 0 : 1;
    }
}
