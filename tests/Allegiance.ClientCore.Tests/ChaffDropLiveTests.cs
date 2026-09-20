using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Live integration proof for <see cref="ConnectionSession.SendFireExpendable"/>
/// — the outbound half of <c>FM_C_FIRE_EXPENDABLE(82)</c> (the client-side mirror
/// of the <c>Bot.SendFireExpendable</c> P182 already proved at the dispatch level).
/// The server's <c>HandleFireExpendable</c> (<c>Server.cs:6658</c>) resolves the
/// mounted dispenser at mount 0 of the requested <see cref="EquipmentType"/>,
/// deploys the chaff (<c>Ship.DeployChaff</c>), decrements its ammo, and broadcasts
/// the FE(87)+CREATE_CHAFF(109)[+MISSILE_SPOOFED]+END_SPOOFING(111) flow sector-wide.
/// Because <see cref="ClientWorld"/> does not consume CREATE_CHAFF (the client does
/// not track chaff objects), the processing proof is asserted on the real server-side
/// state, exactly as P182's live dispatch test asserted it: the firing ship's sector
/// gains a real <see cref="Chaff"/> and the launcher's <c>Amount</c> decrements.
/// </summary>
[Collection("LiveNetwork")]
public class ChaffDropLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Chaff Drop Client Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 885u, DwCookie = 0x885u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 8870000000000000000ul, DrmHash = "chaffdropclient",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // ShipSpawner is `internal` and this assembly isn't friend-granted — reach
    // it through reflection, the same recipe SuicideLiveTests uses.
    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    private static DataChaffTypeIGC RealChaff(StaticCore core)
    {
        var c = core.ChaffTypes.Values
            .Where(cc => cc.ChaffStrength > 0f && cc.Lifespan >= 2f)
            .OrderBy(cc => cc.Lifespan)
            .FirstOrDefault();
        if (c.ExpendableTypeId == 0 && core.ChaffTypes.Count > 0)
            throw new InvalidOperationException("representative core has no chaff with strength>0 and lifespan>=2");
        return c;
    }

    /// <summary>Mount a chaff launcher at mount 0 on <paramref name="ship"/> that names
    /// a real core chaff type, armed with <paramref name="amount"/> rounds (the same
    /// construction-only mount case P182's dispatch tests use — the
    /// <see cref="DataPartTypeIGC.ChaffLauncher"/> subtype has no corpus part-record).</summary>
    private static void MountChaffLauncher(Ship ship, StaticCore core, short amount)
    {
        var chaffType = RealChaff(core);
        var part = new DataPartTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "chaff", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: 661, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.ChaffLauncher(chaffType.ExpendableTypeId));
        ship.MountPart(part, amount, mountId: 0, fractionMounted: 255);
    }

    /// <summary>⭐ The outbound round trip: a real <see cref="ConnectionSession"/>
    /// sends <see cref="ConnectionSession.SendFireExpendable"/> for a ship carrying a
    /// mounted chaff launcher; the server's <c>HandleFireExpendable</c> fires — a real
    /// <see cref="Chaff"/> appears in the firing ship's sector and the launcher's
    /// <c>Amount</c> decrements. This is the exact wire path the <c>C</c> key in
    /// <c>Game1.cs</c> drives.</summary>
    [Fact]
    public void SendFireExpendable_DeploysChaffAndDecrementsAmmo_OverTheWire()
    {
        using var server = new Srv();

        Ship? placedShip = null;
        StaticCore? placedCore = null;
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.First();
            var ship = new Ship(shipId, hull, new Vector3(810f, 820f, 930f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            // Arm the placed ship with a real chaff launcher at mount 0 (the client
            // key sends EquipmentType.ChaffLauncher, which the server resolves there).
            MountChaffLauncher(ship, core, amount: 3);
            placedShip = ship;
            placedCore = core;
            return (ship, mission.Sectors[0]);
        });
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "CHAFFDROP");
        session.SendLogonRequest(Request("CHAFFDROP"));

        int joined = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "client must be assigned a ship");
        Assert.NotNull(placedShip);
        Assert.NotNull(placedCore);

        var launcher = placedShip.Parts.First(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.MountId == 0);
        Assert.Equal(3, launcher.Amount);
        Assert.Empty(placedShip.Sector!.Models.OfType<Chaff>());

        // Press the chaff key equivalent: one fire-and-forget FIRE_EXPENDABLE(82)
        // over the real transport requesting the chaff launcher.
        session.SendFireExpendable(EquipmentType.ChaffLauncher);

        // Server-side proof: a real Chaff appears in the firing ship's sector and the
        // launcher's ammo decrements 3 -> 2.
        int deployed = PumpUntil(server,
            () => placedShip.Sector!.Models.OfType<Chaff>().Any(),
            2000);
        Assert.True(deployed < 2000, $"chaff never appeared in the firing ship's sector via ConnectionSession.SendFireExpendable (deployed={deployed})");

        Assert.Contains(placedShip.Sector.Models.OfType<Chaff>(),
            c => c.ChaffType.ExpendableTypeId == RealChaff(placedCore).ExpendableTypeId);
        Assert.Equal(2, placedShip.Parts.First(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.MountId == 0).Amount);

        Console.WriteLine($"CHAFFDROP-CLIENT-LIVE: chaff deployed in sector after {deployed} ticks via ConnectionSession.SendFireExpendable()");
    }

    /// <summary>⭐ P292 — the NEW client-side tracking: the same real
    /// FIRE_EXPENDABLE(82) → CREATE_CHAFF(109) wire path now also hydrates the
    /// <em>client's</em> <see cref="ClientWorld.Chaff"/> (ConnectionSession +
    /// ClientWorld.Apply). This is the P292 double-wiring proof — it reds if the
    /// ConnectionSession <c>case CreateChaff.Fmid:</c> or the
    /// <c>ClientWorld.Apply(CreateChaff)</c> arm is removed.</summary>
    [Fact]
    public void SendFireExpendable_ClientWorldReceivesCreateChaff_OverTheWire()
    {
        using var server = new Srv();

        Ship? placedShip = null;
        StaticCore? placedCore = null;
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.First();
            var ship = new Ship(shipId, hull, new Vector3(810f, 820f, 930f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            MountChaffLauncher(ship, core, amount: 3);
            placedShip = ship;
            placedCore = core;
            return (ship, mission.Sectors[0]);
        });
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "CHAFFDROPCLIENT");
        session.SendLogonRequest(Request("CHAFFDROPCLIENT"));

        int joined = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "client must be assigned a ship");
        Assert.NotNull(placedShip);
        Assert.NotNull(placedCore);

        Assert.Empty(session.World.Chaff);

        // Press the chaff key equivalent: the real FIRE_EXPENDABLE(82).
        session.SendFireExpendable(EquipmentType.ChaffLauncher);

        // The CLIENT's own world model must now receive the CREATE_CHAFF(109)
        // broadcast and track the deployed chaff at its real spawn position.
        int received = PumpUntil(server, () =>
        {
            lock (session.Gate) return session.World.Chaff.Count > 0;
        }, 2000);
        Assert.True(received < 2000, "client world never received CREATE_CHAFF(109) — P292 tracking not wired");

        ChaffEntity? ce;
        lock (session.Gate) ce = session.World.Chaff.Count > 0 ? session.World.Chaff[0] : null;
        Assert.NotNull(ce);
        Assert.Equal(RealChaff(placedCore).ExpendableTypeId, ce!.Etid);
        Assert.Equal(810f, ce.P0.X, 0.01f);
        Assert.Equal(820f, ce.P0.Y, 0.01f);
        Assert.Equal(930f, ce.P0.Z, 0.01f);

        Console.WriteLine($"CHAFFDROP-CLIENT-LIVE(P292): client world.Chaff hydrated from CREATE_CHAFF(109): etid={ce.Etid} at ({ce.P0.X:F1},{ce.P0.Y:F1},{ce.P0.Z:F1})");
    }
}
