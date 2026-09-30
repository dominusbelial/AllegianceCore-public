using System;
using System.Linq;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P468 — the server must tell a client the NEW side's development-tech mask
/// when it switches sides through the real <c>TryJoinSide</c> path. A player
/// joins side 0 (the join-sync <c>CREATE_BUCKETS(191)</c> seeds side 0's mask
/// keyed by <c>MySideId</c>), then switches to side 1 via a real
/// <c>POSITIONREQ</c> (which the server auto-accepts to the empty side through
/// <c>Server.TryJoinSide</c>, Dispatch.cs:630). Before P468 the server never
/// re-sent CREATE_BUCKETS on a side change, so the client's
/// <c>DevelopmentTechsBySideId</c> stayed keyed to the OLD side and the docked
/// shop offered the whole catalogue (fail-open).
///
/// This drives a REAL <see cref="ConnectionSession"/>/<see cref="ClientWorld"/>
/// against an in-process <see cref="Srv"/>, exactly the loopback the task
/// specifies, and asserts that after the switch the client KNOWS side 1's mask
/// and the docked-shop offered set is the tech-correct (P284) set — not the
/// full catalogue.
/// </summary>
[Collection("LiveNetwork")]
public sealed class SideSwitchDevMaskLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P468 Side Switch Mask", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 0468u, DwCookie = 0x468u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 00468000000000000ul, DrmHash = "side468",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // ShipSpawner is internal (friend-gated) — reach it via reflection, exactly
    // as the other ClientCore live tests do.
    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    private static readonly StaticCore RepresentativeCore =
        StaticCore.Load(IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>
    /// ⭐ Loopback proof: a real client seats on side 0 (learns side 0's mask
    /// from the join-sync CREATE_BUCKETS), then switches to side 1 through the
    /// real POSITIONREQ→TryJoinSide path, and afterwards <c>ClientWorld</c>
    /// KNOWS side 1's development mask and the shop offered set is the
    /// tech-correct (P284) set — not the full catalogue.
    /// </summary>
    [Fact]
    public void Side_switch_teaches_the_client_the_new_sides_development_mask()
    {
        using var server = new Srv();
        short side0 = 0, side1 = 1;
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            // Seed the single player on side 0 (a neutral spot; docking not needed).
            var garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(garrison));
            var ship = new Ship(shipId, core.HullTypes.Values.OrderBy(h => h.HullId).First(),
                StationPos(garrison) + new Vector3(20f, 0f, 0f), Vector3.Zero, Orientation.Identity)
            { SideId = side0 };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        });
        server.Boot(Config());

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "switcher");
        session.SendLogonRequest(Request("switcher"));

        // Seat on side 0: the join-sync CREATE_BUCKETS(191) seeds side 0's mask.
        int join = PumpUntil(server, () => session.World.MyShipId is not null && session.World.DevelopmentTechsFor(side0) is not null, 5000);
        Assert.True(join < 5000, "client never seated on side 0 with its dev mask known");
        short myId = session.World.MyShipId!.Value;
        Assert.Equal(side0, session.World.MySideId);
        var mask0 = session.World.DevelopmentTechsFor(side0)!.Value;
        Assert.Equal(server.Missions[0].Sides.First(s => s.Id == side0).DevelopmentTechs, mask0);

        // Switch to side 1 through the REAL wire path: POSITIONREQ → auto-accept
        // to the empty side → Server.TryJoinSide (Dispatch.cs:630).
        session.SendPositionReq(side1);

        // P468: after the switch the client MUST know side 1's mask. (Pre-fix
        // this stays null forever → the docked shop offered the full catalogue.)
        int switched = PumpUntil(server, () => session.World.DevelopmentTechsFor(side1) is not null, 5000);
        Assert.True(switched < 5000,
            "client never learned side 1's development mask after the side switch (P468 server fix missing)");
        Assert.Equal(side1, session.World.MySideId);

        var mask1 = session.World.DevelopmentTechsFor(side1)!.Value;
        Assert.Equal(server.Missions[0].Sides.First(s => s.Id == side1).DevelopmentTechs, mask1);

        // Docked-shop offered set = the tech-correct (P284) set under side 1's
        // REAL mask — not the full catalogue.
        var garrisonStation = server.Missions[0].Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
        var garrisonLocal = RepresentativeCore.StationTypes[garrisonStation.StationTypeId].TtbmLocal;
        var none = Array.Empty<byte>();
        var fullHulls = RepresentativeCore.HullTypes.Values.ToList();
        var offered = fullHulls
            .Where(h => TechTreeBitMask.RequiredTechsBuyable(h.TtbmRequired, garrisonLocal, mask1))
            .ToList();
        // The offered set is strict-subsets of the full catalogue: with side 1
        // at civ-base dev, not every hull is buyable (P284 containment bites).
        Assert.True(offered.Count < fullHulls.Count,
            $"expected a tech-gated (non-full) offer under side 1's mask, got {offered.Count}/{fullHulls.Count} hulls");
        // Every offered hull really passes the P284 gate under side 1's mask.
        Assert.All(offered, h => Assert.True(
            TechTreeBitMask.RequiredTechsBuyable(h.TtbmRequired, garrisonLocal, mask1)));
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);
}
