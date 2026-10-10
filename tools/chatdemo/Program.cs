using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using BotClass = global::Allegiance.Bot.Bot;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.Core;

namespace Allegiance.Tools.ChatDemo;

/// <summary>
/// The live chat demo (P25 extended for P33): an in-process <see cref="Srv"/>
/// plus real <see cref="BotClass"/> instances over loopback TCP, exchanging a
/// real <c>FM_CS_CHATMESSAGE(23)</c> for each wired target. Ships are placed by
/// the <see cref="Srv.ShipSpawner"/> seam across <b>two real sectors</b> of
/// <c>XXV_simple.igc</c> — <c>1053 'Ceto'</c> (one Alpha + one Beta) and
/// <c>1056 'Farout'</c> (two Alphas + one Beta) — so every new target has a
/// clean scenario: <c>Individual</c> reaches only the addressed same-side ship
/// and drops a cross-side address; <c>AllSector</c> to Ceto reaches both Ceto
/// sides; <c>FriendlySector</c> to Farout reaches both Farout Alphas but not
/// the Farout Beta nor Ceto. <c>Everyone</c>/<c>Team</c> and an unimplemented
/// target (<c>Admin</c> → none) still hold; <c>Leaders</c> and <c>Wing</c> are
/// wired too (P396) — see <c>LiveChatLeadersWingWireTests</c>. Prints the relay
/// + verbatim reception, exits 0 iff every behavior held.
/// </summary>
internal static class Program
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    private const short SectorCeto = 1053;    // 'Ceto'  (verified via missiondump: XXV_simple, 13 sectors)
    private const short SectorFarout = 1056;  // 'Farout'

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
        FedsrvVer = LogonRequest.MsgVer, Time = 931u, DwCookie = 0xFACEu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 74300000000000000ul, DrmHash = "chatdemo",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static int _layoutCounter;

    /// <summary>Deterministic layout: Ceto → Alpha,Beta; Farout → Alpha,Alpha,Beta.</summary>
    private static void InjectLayout(Srv server)
    {
        server.ShipSpawner = (core, mission, id) =>
        {
            var ceto = mission.Sectors.Single(s => s.Id == SectorCeto);
            var farout = mission.Sectors.Single(s => s.Id == SectorFarout);
            var alpha = mission.Sides[0].Id;
            var beta = mission.Sides[1].Id;
            var idx = _layoutCounter++;
            (short side, Sector sector) slot = idx switch
            {
                0 => (alpha, ceto),
                1 => (beta, ceto),
                2 => (alpha, farout),
                3 => (alpha, farout),
                _ => (beta, farout),
            };
            var hull = core.HullTypes.Values.First();
            var ship = new Ship(id, hull, new Vector3(500f + 440f * idx, 640f, 980f), Vector3.Zero, Orientation.Identity);
            ship.SideId = slot.side;
            mission.PlaceShip(ship, slot.sector);
            return (ship, slot.sector);
        };
    }

    private static int Main(string[] args)
    {
        int port = 0;
        if (args.Length > 0 && !int.TryParse(args[0], out port))
        {
            Console.Error.WriteLine("usage: chatdemo [port]");
            Console.Error.WriteLine("  boots an in-process server, connects five real bots across two real sectors,");
            Console.Error.WriteLine("  and proves CHATMESSAGE(23) relay for Everyone, Team, Individual (same+cross side),");
            Console.Error.WriteLine("  AllSector, FriendlySector, and that an unimplemented target (Admin) reaches none.");
            Console.Error.WriteLine($"  Requires the stock artwork tree at {ArtRoot}.");
            return 2;
        }

        using var server = new Srv();
        InjectLayout(server);
        server.Boot(Config(port));
        ushort bound = server.BoundPort;
        Console.WriteLine($"[demo] server listening on 127.0.0.1:{bound}");

        var bots = new BotClass[5];
        for (int i = 0; i < 5; i++) bots[i] = new BotClass();
        Console.WriteLine("[demo] connecting 5 bots...");
        for (int i = 0; i < 5; i++) bots[i].Connect(bound, $"BOT{i}");
        for (int i = 0; i < 5; i++) bots[i].SendLogonRequest(Request($"PLAYER{i}"));

        int ticks = PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
        if (ticks >= 4000) { Console.Error.WriteLine("demo FAILED: not all five bots got a ship"); return 1; }

        // Map each bot to its authoritative LiveShip so roles come from the server.
        var byShip = server.Ships.ToDictionary(l => l.Ship.Id);
        var recs = bots.Select(b => (bot: b, live: byShip[b.ShipAssignment!.Ship.ShipId])).ToList();
        var inCeto = recs.Where(t => t.live.Sector.Id == SectorCeto).ToArray();
        var inFarout = recs.Where(t => t.live.Sector.Id == SectorFarout).ToArray();
        var faroutBeta = inFarout.First(t => t.live.Ship.SideId != inFarout[0].live.Ship.SideId);
        var alphas = inFarout.Where(t => t.live.Ship.SideId != faroutBeta.live.Ship.SideId).ToArray();
        var sender = alphas[0];                                   // a Farout Alpha
        short senderId = sender.live.Ship.Id;
        short addressedSameSideId = alphas[1].live.Ship.Id;       // the other Farout Alpha
        short addressedCrossSideId = faroutBeta.live.Ship.Id;     // the Beta in the same sector
        Console.WriteLine($"[demo] sender ship {senderId} side {sender.live.Ship.SideId} in '{sender.live.Sector.Name}'; "
                          + $"same-side target {addressedSameSideId}; cross-side target {addressedCrossSideId}");

        bool Seen(BotClass b, string m) => b.Chats.Any(c => c.Message == m);

        // ── Everyone ──
        sender.bot.SendChat("ALL HELLO", ChatTarget.Everyone);
        PumpUntil(server, () => bots.All(b => Seen(b, "ALL HELLO")), 2000);

        // ── Team ── (reaches the other Alpha in Farout + echoes to sender)
        sender.bot.SendChat("TEAM HI", ChatTarget.Team);
        PumpUntil(server, () => alphas.All(t => Seen(t.bot, "TEAM HI")), 2000);

        // ── Individual to same-side ship ──
        sender.bot.SendChat("WHISP SAME", ChatTarget.Individual, addressedSameSideId);
        PumpUntil(server, () => Seen(alphas[1].bot, "WHISP SAME"), 2000);

        // ── Individual to cross-side ship ──
        sender.bot.SendChat("WHISP ENEMY", ChatTarget.Individual, addressedCrossSideId);
        PumpUntil(server, () => false, 50);

        // ── AllSector to Ceto (both sides there) ──
        sender.bot.SendChat("SECTOR WIDE", ChatTarget.AllSector, SectorCeto);
        PumpUntil(server, () => inCeto.All(t => Seen(t.bot, "SECTOR WIDE")), 2000);

        // ── FriendlySector to Farout (only the two Alphas) ──
        sender.bot.SendChat("FRND FAROUT", ChatTarget.FriendlySector, SectorFarout);
        PumpUntil(server, () => alphas.All(t => Seen(t.bot, "FRND FAROUT")), 2000);

        // ── Admin (unimplemented) ──
        sender.bot.SendChat("ADMIN ONLY", ChatTarget.Admin);
        PumpUntil(server, () => false, 50);

        bool everyoneOk = bots.All(b => Seen(b, "ALL HELLO"));
        bool teamOk = alphas.All(t => Seen(t.bot, "TEAM HI"));
        bool indSameOk = Seen(alphas[1].bot, "WHISP SAME")
                      && recs.Where(t => t.live.Ship.Id != addressedSameSideId).All(t => !Seen(t.bot, "WHISP SAME"));
        bool indCrossOk = recs.All(t => !Seen(t.bot, "WHISP ENEMY"));
        bool allSectorOk = inCeto.All(t => Seen(t.bot, "SECTOR WIDE"))
                        && recs.Where(t => t.live.Sector.Id != SectorCeto).All(t => !Seen(t.bot, "SECTOR WIDE"));
        bool frndOk = alphas.All(t => Seen(t.bot, "FRND FAROUT"))
                   && !Seen(faroutBeta.bot, "FRND FAROUT")
                   && inCeto.All(t => !Seen(t.bot, "FRND FAROUT"));
        bool adminOk = bots.All(b => !Seen(b, "ADMIN ONLY"));

        Console.WriteLine("[demo] === CHAT RECEPTION (verbatim, per real bot) ===");
        foreach (var t in recs)
            Console.WriteLine($"[demo] ship {t.live.Ship.Id} (side {t.live.Ship.SideId}, '{t.live.Sector.Name}') received: "
                              + string.Join(" | ", t.bot.Chats.Select(c => c.Message).Distinct()));
        Console.WriteLine("[demo] Everyone=" + (everyoneOk ? "OK" : "FAIL") + " Team=" + (teamOk ? "OK" : "FAIL")
                          + " Individual(same)=" + (indSameOk ? "OK" : "FAIL") + " Individual(cross)=" + (indCrossOk ? "OK" : "FAIL")
                          + " AllSector=" + (allSectorOk ? "OK" : "FAIL") + " FriendlySector=" + (frndOk ? "OK" : "FAIL")
                          + " Admin(drop)=" + (adminOk ? "OK" : "FAIL"));
        bool all = everyoneOk && teamOk && indSameOk && indCrossOk && allSectorOk && frndOk && adminOk;
        Console.WriteLine(all ? "[demo] RESULT PASS" : "[demo] RESULT FAIL");
        return all ? 0 : 1;
    }
}
