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
/// P396 live proof for the two newly-wired <see cref="ChatTarget"/> cases
/// <see cref="ChatTarget.Leaders"/> and <see cref="ChatTarget.Wing"/>: real
/// <see cref="FltBot"/> instances exchange real <c>FM_CS_CHATMESSAGE(23)</c>
/// over loopback TCP and the server relays them per <c>SelectChatTargets</c>,
/// using the same two-sector <c>XXV_simple.igc</c> layout the sector test uses
/// (Ceto 1053 / Farout 1056, sides Alpha/Bravo).
/// <para>
/// Leadership is established in the spawner via the P183
/// <c>Side.LeaderShipId</c> (first Alpha → Alpha leader, first Bravo → Bravo
/// leader), which is exactly the storage <c>SelectChatTargets</c> reads, and
/// the Leadership result is validated from the authoritative
/// <see cref="Srv.Ships"/> table — not guessed. Wings are assigned by the bots
/// themselves over the REAL <c>SET_WINGID</c> wire path
/// (<see cref="FltBot.SendSetWingId"/>), which is precisely the mechanism that
/// mutates <see cref="Ship.WingId"/> server-side (P146) — proving both the
/// wing-assignment mechanism and the wing-chat routing work end-to-end.
/// </para>
/// <para>
/// Layout (slot → side, sector; leaders/wings noted):
///   slot 0 → Alpha, Ceto   (ALPHA LEADER, wing 1)
///   slot 1 → Bravo, Ceto   (BRAVO LEADER)
///   slot 2 → Alpha, Farout (wing 2 — sender)
///   slot 3 → Alpha, Farout (wing 1)
///   slot 4 → Bravo, Farout
/// Sender is the slot-2 Farout Alpha throughout.
/// </para>
/// </summary>
[Collection("LiveNetwork")]
public class LiveChatLeadersWingWireTests
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
            new Gm { Name = "Leaders Wing Demo", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 933u, DwCookie = 0xDEEDu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 76200000000000000ul, DrmHash = "liveleaderswing",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // shipId placed at each slot, captured so the test never assumes bot
    // connection order == spawn order (same trick the sector test uses, but
    // keyed by the actual assigned ship ids rather than sector order).
    private static readonly List<short> PlacedIds = new();
    private static int _layoutCounter;

    /// <summary>
    /// Seed <see cref="Srv.ShipSpawner"/> deterministically:
    ///   slot 0 → Ceto, Alpha   (Alpha leader)      slot 1 → Ceto, Bravo (Bravo leader)
    ///   slot 2 → Farout, Alpha                     slot 3 → Farout, Alpha
    ///   slot 4 → Farout, Bravo
    /// The first ship to seat on each side becomes that side's leader via the
    /// P183 <see cref="global::Allegiance.Core.Model.Side.LeaderShipId"/>
    /// storage (native's first-player-on-empty-side election,
    /// fsmission.cpp:601-613). Wings are NOT set here — the bots assign them
    /// over real SET_WINGID after launch.
    /// </summary>
    private static void InjectLayout(Srv server)
    {
        PlacedIds.Clear();
        server.ShipSpawner = (core, mission, id) =>
        {
            var ceto = mission.Sectors.SingleOrDefault(s => s.Id == SectorCeto)
                ?? throw new InvalidOperationException($"XXV_simple has no sector {SectorCeto}");
            var farout = mission.Sectors.SingleOrDefault(s => s.Id == SectorFarout)
                ?? throw new InvalidOperationException($"XXV_simple has no sector {SectorFarout}");
            var alpha = mission.Sides[0];
            var beta = mission.Sides[1];

            var placed = _layoutCounter++;
            (short side, Sector sector) slot = placed switch
            {
                0 => (alpha.Id, ceto),
                1 => (beta.Id, ceto),
                2 => (alpha.Id, farout),
                3 => (alpha.Id, farout),
                _ => (beta.Id, farout),
            };
            var hull = core.HullTypes.Values.First();
            float x = 800f + 360f * placed;
            var ship = new Ship(id, hull, new Vector3(x, 820f, 440f), Vector3.Zero, Orientation.Identity);
            ship.SideId = slot.side;
            mission.PlaceShip(ship, slot.sector);

            // First ship on each side becomes its leader (P183 storage).
            if (slot.side == alpha.Id && alpha.LeaderShipId is null) alpha.LeaderShipId = ship.Id;
            if (slot.side == beta.Id && beta.LeaderShipId is null) beta.LeaderShipId = ship.Id;

            PlacedIds.Add(ship.Id);
            return (ship, slot.sector);
        };
    }

    /// <summary>shipId → LiveShip resolution for reading side/leader of every peer.</summary>
    private static Dictionary<short, Srv.LiveShip> ByShip(Srv server) =>
        server.Ships.ToDictionary(l => l.Ship.Id);

    private static FltBot BotOf(List<(FltBot bot, Srv.LiveShip live)> infos, short shipId) =>
        infos.First(t => t.live.Ship.Id == shipId).bot;

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Leaders_and_Wing_chat_targets_relay_to_real_peers_over_the_wire()
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
            bots[i].Connect(port, $"LWBOT{i}");
        }
        for (int i = 0; i < 5; i++) bots[i].SendLogonRequest(Request($"LWPLAY{i}"));

        int ticks = PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4500);
        Assert.True(ticks < 4500, "not all five bots were assigned a ship");

        var byShip = ByShip(server);
        var infos = bots.Select(b => (bot: b, live: byShip[b.ShipAssignment!.Ship.ShipId])).ToList();
        Assert.Equal(5, infos.Count);

        // Resolve roles from the captured spawn slots (order-independent of bot connect).
        short alphaLeaderId = PlacedIds[0];      // slot 0: Alpha Ceto → Alpha leader
        short bravoLeaderId = PlacedIds[1];      // slot 1: Bravo Ceto → Bravo leader
        short senderId = PlacedIds[2];           // slot 2: Alpha Farout (sender, non-leader)
        short faroutWingMateId = PlacedIds[3];   // slot 3: Alpha Farout
        short bravoFaroutId = PlacedIds[4];      // slot 4: Bravo Farout

        // The spawner's leadership is exercised below: a Leaders chat reaching
        // alphaLeaderId + bravoLeaderId (and only them) is the live proof that
        // Side.LeaderShipId held on the authoritative ship table.
        Console.WriteLine($"LIVE-LWCHAT: leaders elected — Alpha leader ship {alphaLeaderId}, Bravo leader ship {bravoLeaderId}; "
                          + $"Alpha peers: {alphaLeaderId},{senderId},{faroutWingMateId}; Bravo peers: {bravoLeaderId},{bravoFaroutId}");

        // ── Assign wings over the real SET_WINGID wire path (P146) ────────────
        // slot0 Alpha(Ceto) → wing 1 ; slot3 Alpha(Farout) → wing 1 ; slot2 Alpha(Farout) → wing 2.
        BotOf(infos, alphaLeaderId).SendSetWingId(alphaLeaderId, 1);
        BotOf(infos, faroutWingMateId).SendSetWingId(faroutWingMateId, 1);
        BotOf(infos, senderId).SendSetWingId(senderId, 2);
        PumpUntil(server, () =>
            byShip[senderId].Ship.WingId == 2
            && byShip[alphaLeaderId].Ship.WingId == 1
            && byShip[faroutWingMateId].Ship.WingId == 1, 1500);
        Assert.Equal(1, byShip[alphaLeaderId].Ship.WingId);
        Assert.Equal(1, byShip[faroutWingMateId].Ship.WingId);
        Assert.Equal(2, byShip[senderId].Ship.WingId);
        Console.WriteLine($"LIVE-LWCHAT: wings assigned via SET_WINGID — wing1={{ship {alphaLeaderId} (Ceto), ship {faroutWingMateId} (Farout)}}, "
                          + $"wing2={{ship {senderId}}} — WingId is a real, reachable assignment mechanism (P146)");

        var senderBot = BotOf(infos, senderId);
        var alphaLeaderBot = BotOf(infos, alphaLeaderId);
        var bravoLeaderBot = BotOf(infos, bravoLeaderId);

        void ResetAll() { foreach (var b in bots) b.ResetChatReceived(); }
        bool Seen(FltBot b, string m) => b.Chats.Any(c => c.Message == m);

        // ── Phase 1: Leaders reaches EVERY side's leader, no one else ─────────
        ResetAll();
        senderBot.SendChat("LEADERS CALL", ChatTarget.Leaders);
        PumpUntil(server, () => Seen(alphaLeaderBot, "LEADERS CALL") && Seen(bravoLeaderBot, "LEADERS CALL"), 2000);
        Assert.Contains(alphaLeaderBot.Chats, c => c.Message == "LEADERS CALL");
        Assert.Contains(bravoLeaderBot.Chats, c => c.Message == "LEADERS CALL");
        // Non-leaders must NOT receive it (sender herself, the other Alpha, the other Bravo).
        Assert.DoesNotContain(senderBot.Chats, c => c.Message == "LEADERS CALL");
        Assert.DoesNotContain(BotOf(infos, faroutWingMateId).Chats, c => c.Message == "LEADERS CALL");
        Assert.DoesNotContain(BotOf(infos, bravoFaroutId).Chats, c => c.Message == "LEADERS CALL");
        Console.WriteLine($"LIVE-LWCHAT: Leaders from non-leader {senderId} reached BOTH leaders ({alphaLeaderId} Alpha, {bravoLeaderId} Bravo), "
                          + "no non-leader — RESULT PASS");

        // ── Phase 2: a leader's own Leaders chat includes the sender herself ──
        ResetAll();
        alphaLeaderBot.SendChat("LEADERS SELF", ChatTarget.Leaders);
        PumpUntil(server, () => Seen(alphaLeaderBot, "LEADERS SELF") && Seen(bravoLeaderBot, "LEADERS SELF"), 2000);
        Assert.Contains(alphaLeaderBot.Chats, c => c.Message == "LEADERS SELF");   // the sending leader self-relays (Everyone-style)
        Assert.Contains(bravoLeaderBot.Chats, c => c.Message == "LEADERS SELF");
        Assert.DoesNotContain(senderBot.Chats, c => c.Message == "LEADERS SELF");  // non-leader Alpha: NO
        Assert.DoesNotContain(BotOf(infos, faroutWingMateId).Chats, c => c.Message == "LEADERS SELF");
        Assert.DoesNotContain(BotOf(infos, bravoFaroutId).Chats, c => c.Message == "LEADERS SELF");
        Console.WriteLine("LIVE-LWCHAT: Leaders from the Alpha leader reached herself + the Bravo leader, no non-leader — RESULT PASS");

        // ── Phase 3: Wing reaches only same-side ships on the matching wing ───
        ResetAll();
        senderBot.SendChat("WING SPEAK", ChatTarget.Wing, /*oidRecipient=*/1);
        PumpUntil(server, () =>
            Seen(alphaLeaderBot, "WING SPEAK")       // Alpha wing 1, Ceto (different sector!)
            && Seen(BotOf(infos, faroutWingMateId), "WING SPEAK"), 2000);
        Assert.Contains(alphaLeaderBot.Chats, c => c.Message == "WING SPEAK");                    // same-side wing 1, other sector: YES
        Assert.Contains(BotOf(infos, faroutWingMateId).Chats, c => c.Message == "WING SPEAK");     // same-side wing 1, same sector: YES
        Assert.DoesNotContain(senderBot.Chats, c => c.Message == "WING SPEAK");                    // sender is wing 2: NO
        Assert.DoesNotContain(bravoLeaderBot.Chats, c => c.Message == "WING SPEAK");               // Bravo (opposite side): NO
        Assert.DoesNotContain(BotOf(infos, bravoFaroutId).Chats, c => c.Message == "WING SPEAK");  // Bravo: NO
        Console.WriteLine($"LIVE-LWCHAT: Wing(1) from {senderId} (wing 2) reached same-side wing-1 mates "
                          + $"{alphaLeaderId} (Ceto) + {faroutWingMateId} (Farout) — cross-sector reach proved, "
                          + "wing-2 same-side and opposite-side excluded — RESULT PASS");
    }
}
