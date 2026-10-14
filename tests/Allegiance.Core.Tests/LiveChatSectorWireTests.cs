using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P33 live proof for the three new <see cref="ChatTarget"/> cases (extends
/// <see cref="LiveChatWireTests"/>' P25 proof of Everyone/Team): real
/// <see cref="FltBot"/> instances exchange a real <c>FM_CS_CHATMESSAGE(23)</c>
/// over loopback TCP and the server relays it per <c>SelectChatTargets</c>.
/// Ships are spread across <b>two real sectors</b> of <c>XXV_simple.igc</c> —
/// <c>1053 'Ceto'</c> and <c>1056 'Farout'</c> (verified via missiondump:
/// "XXV_simple.igc MAP clusters=13 … sector 1053 'Ceto' … sector 1056
/// 'Farout'") — via the same <see cref="Srv.ShipSpawner"/> seam the combat and
/// flight tests use, which also sets each ship's <see cref="Ship.SideId"/>
/// deterministically (so same-side and cross-side pairs coexist, unlike the
/// default round-robin spawn that can't guarantee them). Roles are resolved
/// from the server's authoritative <see cref="Srv.Ships"/>, not guessed.
/// <para>
/// Layout: Ceto holds one Alpha and one Beta (mixed sides ⇒ the cleanest
/// AllSector proof); Farout holds two Alphas and one Beta (⇒ Individual
/// reaches a same-side peer, cross-side addressed peer is dropped;
/// FriendlySector to Farout reaches exactly the two Alphas). Sender is a
/// Farout Alpha throughout.
/// </para>
/// </summary>
[Collection("LiveNetwork")]
public class LiveChatSectorWireTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private const short SectorCeto = 1053;      // verified from XXV_simple.igc (missiondump): 'Ceto'
    private const short SectorFarout = 1056;    // verified: 'Farout'

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Chat Sector Demo", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 933u, DwCookie = 0xDEEDu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 76200000000000000ul, DrmHash = "livesectorchat",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>
    /// Seed <see cref="Srv.ShipSpawner"/> with a deterministic placement across
    /// the two real sectors Ceto (1053) and Farout (1056):
    ///   spawn 0 → Ceto, Alpha      spawn 1 → Ceto, Beta
    ///   spawn 2 → Farout, Alpha    spawn 3 → Farout, Alpha    spawn 4 → Farout, Beta
    /// Finding each sector by <b>id</b> (and asserting it exists) rather than by
    /// list index so the test never assumes mission.Sectors ordering — the ids
    /// are the verified ground truth.
    /// </summary>
    private static void InjectLayout(Srv server)
    {
        server.ShipSpawner = (core, mission, id) =>
        {
            var ceto = mission.Sectors.SingleOrDefault(s => s.Id == SectorCeto)
                ?? throw new InvalidOperationException($"XXV_simple has no sector {SectorCeto}");
            var farout = mission.Sectors.SingleOrDefault(s => s.Id == SectorFarout)
                ?? throw new InvalidOperationException($"XXV_simple has no sector {SectorFarout}");
            var alphaId = mission.Sides[0].Id;
            var betaId = mission.Sides[1].Id;

            var placed = _layoutCounter++;
            (short side, Sector sector) slot = placed switch
            {
                0 => (alphaId, ceto),      // Ceto, Alpha
                1 => (betaId, ceto),       // Ceto, Beta
                2 => (alphaId, farout),    // Farout, Alpha
                3 => (alphaId, farout),    // Farout, Alpha
                _ => (betaId, farout),     // Farout, Beta
            };
            var hull = core.HullTypes.Values.First();
            float x = 500f + 320f * placed;
            var ship = new Ship(id, hull, new Vector3(x, 620f, 940f), Vector3.Zero, Orientation.Identity);
            ship.SideId = slot.side;
            mission.PlaceShip(ship, slot.sector);
            return (ship, slot.sector);
        };
    }
    private static int _layoutCounter;

    /// <summary>shipId → LiveShip resolution for reading sector/side of every peer.</summary>
    private static Dictionary<short, Srv.LiveShip> ByShip(Srv server) =>
        server.Ships.ToDictionary(l => l.Ship.Id);

    [Trait("Requires", "Artwork")]
    [Trait("Lane", "LiveTiming")]   // P647 quarantine: live-wire chat sector relay is flaky even in isolation (8/10)
    [SkippableFact]
    public void Individual_AllSector_and_FriendlySector_relay_to_real_peers_over_the_wire()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectLayout(server);
        server.Boot(Config());
        ushort port = server.BoundPort;

        var bots = new FltBot[5];
        for (int i = 0; i < 5; i++)
        {
            bots[i] = new FltBot();
            bots[i].Connect(port, $"SECBOT{i}");
        }
        for (int i = 0; i < 5; i++) bots[i].SendLogonRequest(Request($"SECPLAY{i}"));

        int ticks = PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
        Assert.True(ticks < 4000, "not all five bots were assigned a ship");

        var byShip = ByShip(server);
        var infos = bots.Select(b => (bot: b, live: byShip[b.ShipAssignment!.Ship.ShipId])).ToList();
        Assert.Equal(5, infos.Count);

        // Resolve roles from the authoritative ship table, not connection order.
        var inCeto = infos.Where(t => t.live.Sector.Id == SectorCeto).ToArray();
        var inFarout = infos.Where(t => t.live.Sector.Id == SectorFarout).ToArray();
        Assert.Equal(2, inCeto.Length);     // one Alpha + one Beta
        Assert.Equal(3, inFarout.Length);   // two Alpha + one Beta

        var faroutOpponent = inFarout.First(t => t.live.Ship.SideId != inFarout[0].live.Ship.SideId); // the lone Beta
        var alphas = inFarout.Where(t => t.live.Ship.SideId != faroutOpponent.live.Ship.SideId).ToArray();
        Assert.Equal(2, alphas.Length);
        var sender = alphas[0];                                        // a Farout Alpha
        short senderId = sender.live.Ship.Id;
        var senderBot = sender.bot;
        var sameSideFarout = alphas;                                   // both Alphas (incl. sender echo)
        short addressedSameSideId = alphas[1].live.Ship.Id;            // the other Farout Alpha
        short addressedCrossSideId = faroutOpponent.live.Ship.Id;      // the Beta in the same sector

        void ResetAll() { foreach (var b in bots) b.ResetChatReceived(); }
        bool Seen(FltBot b, string m) => b.Chats.Any(c => c.Message == m);

        // ── Phase 1: Individual reaches ONLY the addressed same-side ship ──
        ResetAll();
        senderBot.SendChat("WHISP SAME", ChatTarget.Individual, addressedSameSideId);
        PumpUntil(server, () => sameSideFarout.Where(t => t.live.Ship.Id == addressedSameSideId).All(t => Seen(t.bot, "WHISP SAME")), 2000);
        // The addressed same-side Farout Alpha must have it…
        var addressedBot = infos.First(t => t.live.Ship.Id == addressedSameSideId).bot;
        Assert.Contains(addressedBot.Chats, c => c.Message == "WHISP SAME");
        // …and every other peer (including the cross-side Beta in the same
        // sector, and every Ceto bot) must NOT. The relay ran synchronously in
        // the same TickSim pass, so assert absence immediately.
        Assert.All(inFarout.Where(t => t.live.Ship.Id != addressedSameSideId).Concat(inCeto),
                   t => Assert.DoesNotContain(t.bot.Chats, c => c.Message == "WHISP SAME"));
        Console.WriteLine($"LIVE-SECCHAT: Individual({addressedSameSideId}) reached only the addressed ship — "
                          + $"{string.Join(",", inFarout.Concat(inCeto).Select(t => $"{t.live.Ship.Id}{(t.live.Ship.Id == addressedSameSideId ? ":YES" : ":NO")}"))}");

        // ── Phase 2: Individual cross-side addressed ship is dropped ──
        ResetAll();
        senderBot.SendChat("WHISP ENEMY", ChatTarget.Individual, addressedCrossSideId);
        PumpUntil(server, () => false, 50);    // let the relay process it
        Assert.All(bots, b => Assert.DoesNotContain(b.Chats, c => c.Message == "WHISP ENEMY"));
        Console.WriteLine("LIVE-SECCHAT: cross-side Individual dropped for all 5 — RESULT PASS");

        // ── Phase 3: AllSector reaches every peer in the named sector, both sides ──
        ResetAll();
        senderBot.SendChat("SECTOR WIDE", ChatTarget.AllSector, SectorCeto);
        PumpUntil(server, () => inCeto.All(t => Seen(t.bot, "SECTOR WIDE")), 2000);
        Assert.All(inCeto, t => Assert.Contains(t.bot.Chats, c => c.Message == "SECTOR WIDE" && c.SidSender == senderId));
        Assert.All(inFarout.Where(t => t.live.Ship.Id != senderId),
                   t => Assert.DoesNotContain(t.bot.Chats, c => c.Message == "SECTOR WIDE"));
        Console.WriteLine($"LIVE-SECCHAT: AllSector({SectorCeto}) reached both Ceto sides — "
                          + $"ceto={string.Join(",", inCeto.Select(t => $"{t.live.Ship.Id}(side{t.live.Ship.SideId})"))} sender={senderId}");

        // ── Phase 4: FriendlySector reaches only same-side peers in that sector ──
        ResetAll();
        senderBot.SendChat("FRND FAROUT", ChatTarget.FriendlySector, SectorFarout);
        PumpUntil(server, () => sameSideFarout.All(t => Seen(t.bot, "FRND FAROUT")), 2000);
        Assert.All(sameSideFarout, t => Assert.Contains(t.bot.Chats, c => c.Message == "FRND FAROUT"));       // both Farout Alphas (incl. sender echo)
        Assert.DoesNotContain(faroutOpponent.bot.Chats, c => c.Message == "FRND FAROUT");                      // Beta in same sector: NO
        Assert.All(inCeto, t => Assert.DoesNotContain(t.bot.Chats, c => c.Message == "FRND FAROUT"));          // other sector: NO
        Console.WriteLine("LIVE-SECCHAT: FriendlySector(Farout) reached both Farout Alphas, not the Farout Beta "
                          + $"{faroutOpponent.live.Ship.Id}(side{faroutOpponent.live.Ship.SideId}) nor Ceto — RESULT PASS");

        // ── Phase 5: Group aliases Individual (FedSrv.CPP:3531-3536 fallthrough) ──
        ResetAll();
        senderBot.SendChat("GRP WHISP", ChatTarget.Group, addressedSameSideId);
        PumpUntil(server, () => Seen(addressedBot, "GRP WHISP"), 2000);
        Assert.Contains(addressedBot.Chats, c => c.Message == "GRP WHISP");
        Assert.All(inFarout.Where(t => t.live.Ship.Id != addressedSameSideId).Concat(inCeto),
                   t => Assert.DoesNotContain(t.bot.Chats, c => c.Message == "GRP WHISP"));
        Console.WriteLine("LIVE-SECCHAT: Group target routed as Individual — only the addressed ship saw GRP WHISP — RESULT PASS");
    }
}
