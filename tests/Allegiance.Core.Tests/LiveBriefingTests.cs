using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P121 — <c>SET_BRIEFING_TEXT</c>(9), the per-player join-sync mission-briefing
/// message. Real loopback proof over an in-process server + real TCP bots:
/// joining a mission sends the joining player a <c>SET_BRIEFING_TEXT</c>
/// carrying (in this port) the cluster name of the joining side's first-found
/// friendly station (reusing P105's <c>FindFriendlyStation</c>), with
/// <c>fGenerateCivBriefing = true</c>, addressed to that player's own
/// connection only — not side-wide, not whole-mission. A side with zero
/// friendly stations gets the honest skip (the helper's documented null return),
/// asserted below rather than guessed. The codec round-trips byte-exact in
/// Wire.SetBriefingTextWireTests.
/// </summary>
[Collection("LiveNetwork")]
public class LiveBriefingTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Briefing Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 882u, DwCookie = 0x882u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9420000000000000000ul, DrmHash = "brief",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short SideOf(Srv server, FltBot bot)
    {
        var shipId = bot.ShipAssignment!.Ship.ShipId;
        return server.Ships.Single(s => s.Ship.Id == shipId).Ship.SideId!.Value;
    }

    /// <summary>Independently compute the expected briefing text for a side:
    /// the first station (deterministic sector-then-station order) owned by that
    /// side, mapped to its sector's name — mirroring the deliberate
    /// simplification in <see cref="Srv.FindFriendlyStation"/> (any one friendly
    /// match is fine). Returns <c>null</c> when the side owns no station
    /// anywhere, in which case the honest port sends no briefing.</summary>
    private static string? ExpectedCluster(Mission mission, short sideId)
    {
        foreach (var sector in mission.Sectors)
            foreach (var station in sector.Stations)
                if (station.SideId == sideId)
                    return sector.Name;
        return null;
    }

    /// <summary>⭐ A real player join/sync sends each joining player exactly one
    /// <c>SET_BRIEFING_TEXT(9)</c> — to that player's own connection only —
    /// with <c>fGenerateCivBriefing = true</c> and text equal to a real
    /// friendly station's cluster name (the port's always-generate-civ-briefing
    /// branch). Two bots on different sides (round-robin default spawn, bot A →
    /// side 0, bot B → side 1) prove it is per-player: neither receives the
    /// other's message, and a side owning no friendly station receives none.</summary>
    [Fact]
    public void Join_sync_sends_per_player_briefing_with_friendly_cluster_or_skips()
    {
        using var server = new Srv();
        server.Boot(Config());

        using var a = new FltBot();
        using var b = new FltBot();
        a.Connect(server.BoundPort, "AA"); a.SendLogonRequest(Request("AA"));
        b.Connect(server.BoundPort, "BB"); b.SendLogonRequest(Request("BB"));
        int seat = PumpUntil(server,
            () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "both bots must seat");

        var mission = server.Missions[0];

        // Give join-sync briefings time to land (bots on a no-station side just
        // burn the window; a side-wide/whole-mission leak would show as >1).
        int pumped = 0;
        while ((a.SetBriefingTexts.Count < 1 || b.SetBriefingTexts.Count < 1) && pumped < 4000)
        {
            server.TickSim(); pumped++; Thread.Sleep(1);
        }

        foreach (var bot in new[] { a, b })
        {
            short mySide = SideOf(server, bot);
            string? expected = ExpectedCluster(mission, mySide);
            var list = bot.SetBriefingTexts;

            if (expected is not null)
            {
                var bm = Assert.Single(list);   // exactly one, to this player only — no cross-side / whole-mission leak
                Assert.True(bm.FGenerateCivBriefing);   // always the generate-civ-briefing branch (P121)
                Assert.Equal(expected, bm.Text);        // a real friendly station's cluster name
                Console.WriteLine($"BRIEFING-LIVE: side-{mySide} bot '{bot.ShipAssignment!.Ship.ShipId}' -> SET_BRIEFING_TEXT(9) generateCivBriefing=true text='{bm.Text}'");
            }
            else
            {
                // No friendly station anywhere on this side — the P105 helper's
                // documented null return. The port logs and skips rather than
                // fabricating text; a whole-mission broadcast would have leaked here.
                Assert.Empty(list);
                Console.WriteLine($"BRIEFING-LIVE: side-{mySide} bot '{bot.ShipAssignment!.Ship.ShipId}' -> no friendly station; SET_BRIEFING_TEXT skipped (asserted 0 messages)");
            }
        }
    }

    /// <summary>⭐ Verification point 2 — a joining player whose side owns
    /// <b>zero</b> friendly stations anywhere receives <b>no</b>
    /// <c>SET_BRIEFING_TEXT</c> (P105's <c>FindFriendlyStation</c> documents a
    /// <c>null</c> return for this case, Server.cs:4750-4759; the server logs
    /// and skips rather than fabricating a starting location — the honest
    /// mapping of native's guaranteed-present <c>GetBase(pside)</c>).</summary>
    [Fact]
    public void Join_on_a_side_with_no_friendly_station_receives_no_briefing()
    {
        using var server = new Srv();
        short absentSide = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            // A side id no station in this mission owns → FindFriendlyStation
            // returns null (mirrors LivePlayerDeathRespawnTests.AbsentSideId).
            var used = mission.Sectors.SelectMany(s => s.Stations).Select(st => st.SideId).ToHashSet();
            short candidate = 733;
            while (used.Contains(candidate)) candidate++;
            absentSide = candidate;

            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var sector = mission.Sectors[0];
            var ship = new Ship(id, hull, new Vector3(700f, 730f, 920f), Vector3.Zero, Orientation.Identity) { SideId = absentSide };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(Config());

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "VOID"); bot.SendLogonRequest(Request("VOID"));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "bot must seat");

        // Setup guard: the fabricated side must truly own no station anywhere.
        Assert.True(server.Missions[0].Sectors.SelectMany(s => s.Stations).All(st => st.SideId != absentSide),
            "setup: absentSide must own no station");

        // Give the (absent) briefing a fair window to land were it going to.
        int n = 0;
        while (bot.SetBriefingTexts.Count < 1 && n < 1000) { server.TickSim(); n++; Thread.Sleep(1); }

        Assert.Empty(bot.SetBriefingTexts);   // no friendly station → honest skip, no fabricated text
        Console.WriteLine($"BRIEFING-NEG: bot on side {absentSide} (no friendly station anywhere) -> 0 SET_BRIEFING_TEXT after {n} extra ticks");
    }
}
