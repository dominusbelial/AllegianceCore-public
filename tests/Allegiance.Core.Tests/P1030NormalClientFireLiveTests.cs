using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P1030 — the "honest client is never refused" control for the new combat
/// authority gates: a real <see cref="ConnectionSession"/> over the real
/// transport drops chaff and launches a missile from a normally armed ship
/// (both launchers mounted at the undock state, deadline already passed). The
/// server must deploy both, exactly as before the hardening.
/// </summary>
[Collection("LiveNetwork")]
public class P1030NormalClientFireLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,
        Games =
        [
            new Gm { Name = "P1030 honest fire", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request() => new()
    {
        CharacterName = "P1030FIRE", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 1u, DwCookie = uint.MaxValue, CrcFileList = -1,
        SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = 10300000000000000ul, DrmHash = "p1030",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship, Sector)> spawn) =>
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, spawn);

    private static DataPartTypeIGC LauncherPart(StaticCore core, string kind, short partId)
    {
        DataPartTypeIGC.Subtype subtype = kind switch
        {
            "chaff" => new DataPartTypeIGC.ChaffLauncher(core.ChaffTypes.Keys.First()),
            _ => new DataPartTypeIGC.MissileLauncher(core.MissileTypes.Values
                    .First(m => m.MaxLock == 0f && m.InitialSpeed > 0f && m.Lifespan >= 1f).ExpendableTypeId),
        };
        return new(Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: kind, Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: partId, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "", Type: subtype);
    }

    /// <summary>⭐ A real client fires a mounted chaff launcher and a mounted
    /// missile launcher right after undocking: both deploy and both spend one
    /// round. This is the native honest path (dispenserigc.cpp:103-119,
    /// magazineigc.cpp:111-123) and must never be refused by the P1030 gates.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_normal_client_chaff_and_missile_fire_is_never_refused()
    {
        Require.Artwork();
        using var server = new Srv();
        Ship? placed = null;
        SetShipSpawner(server, (core, mission, id) =>
        {
            var hull = core.HullTypes.Values
                .First(h => h.CapacityChaffLauncher >= 4 && h.CapacityMagazine >= 100 && !h.HasCapability(DataHullTypeIGC.HabmLifepod));
            var ship = new Ship(id, hull, new Vector3(0f, 5000f, 5000f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            // The undock state: every part fully mounted, deadline at the mount tick.
            ship.MountPart(LauncherPart(core, "chaff", 780), 3, 0, ExpandedPartData.FullyMounted);
            ship.MountPart(LauncherPart(core, "missile", 781), 3, 0, ExpandedPartData.FullyMounted);
            placed = ship;
            return (ship, mission.Sectors[0]);
        });
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "P1030FIRE");
        session.SendLogonRequest(Request());
        int joined = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "the client must be seated");
        Assert.NotNull(placed);
        var sector = placed!.Sector!;

        short chaffBefore = placed.Parts.Single(p => p.EquipmentType == EquipmentType.ChaffLauncher).Amount;
        session.SendFireExpendable(EquipmentType.ChaffLauncher);
        int chaff = PumpUntil(server, () => sector.Models.OfType<Chaff>().Any(), 2000);
        Assert.True(chaff < 2000, "a normal client's chaff drop was refused");
        Assert.Equal((short)(chaffBefore - 1), placed.Parts.Single(p => p.EquipmentType == EquipmentType.ChaffLauncher).Amount);

        short missileBefore = placed.Parts.Single(p => p.EquipmentType == EquipmentType.Magazine).Amount;
        session.SendFireMissile();
        int missile = PumpUntil(server, () => sector.Models.OfType<Missile>().Any(), 2000);
        Assert.True(missile < 2000, "a normal client's missile launch was refused");
        Assert.Equal((short)(missileBefore - 1), placed.Parts.Single(p => p.EquipmentType == EquipmentType.Magazine).Amount);
    }
}
