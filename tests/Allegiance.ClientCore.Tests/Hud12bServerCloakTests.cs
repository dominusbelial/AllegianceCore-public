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

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// HUD-12b — the server-side half of the cloak rows:
/// <list type="bullet">
/// <item>fix 2 — the owner-only <c>OWN_SHIP_CLOAK_UPDATE(220)</c> must stream the
/// SOURCE ship's ramp. Native <c>ModelData::GetCloaking()</c> reads
/// <c>GetShip()</c>, which is <c>GetSourceShip()</c>
/// (<c>WinTrek/consoledata.h:144-146</c>), and its visibility gate accepts the
/// own ship's parent (<c>consoledata.cpp:480-489</c>), so a turret gunner's
/// carrier cloak is what the HUD shows;</item>
/// <item>fix 3 — a live proof that each seated owner receives only its own 220
/// stream (real in-process server, two real <see cref="ConnectionSession"/>
/// clients over loopback, real <see cref="Ship.UpdateCloak"/> ramps).</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public class Hud12bServerCloakTests
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,
        Games =
        [
            new Gm { Name = "HUD-12b cloak", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 1212u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 1212000000000000000ul + (ulong)name.Length, DrmHash = "hud12b",
    };

    private static DataHullTypeIGC TestHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "hud12b hull",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f], TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 0f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 1000f, RechargeRate: 100f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 912, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 540,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    /// <summary>A real cloak mount with a fast ramp and negligible draw, so the
    /// live 220 value moves quickly without depending on a hull's energy pool.</summary>
    private static readonly DataPartTypeIGC CloakPart = new(
        Price: 0, TimeToBuild: 0, ModelName: "bcl1gncr.igc", IconName: "", Name: "HUD-12b cloak",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 1f, Signature: 0f, PartId: 9821, SuccessorPartId: -1, PartMask: 983,
        InventoryLineMdl: "", Type: new DataPartTypeIGC.Cloak(
            EnergyConsumption: 0.0001f, MaxCloaking: 0.99f, OnRate: 50f, OffRate: 50f,
            EngageSound: -1, DisengageSound: -1));

    // ── fix 2: the parent/source ship's ramp ───────────────────────────────

    [Fact]
    public void Own_ship_cloak_uses_the_parent_ships_ramp_for_a_turret_gunner()
    {
        var parent = new Ship(1, TestHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity);
        var child = new Ship(2, TestHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity);
        // Production only changes Cloaking from the ramp; the test writes the
        // distinct values directly (private setter).
        SetCloaking(parent, 0.25f);
        SetCloaking(child, 0.75f);
        child.ParentShip = parent;

        // Native GetShip() is GetSourceShip(): the gunner's own ship is the child,
        // but the visible fraction that matters is the carrier's
        // (WinTrek/consoledata.h:144-146, consoledata.cpp:480-489).
        Assert.Same(parent, child.ParentShip);
        Assert.Equal(0.25f, Srv.OwnShipCloaking(child), 5);
        Assert.NotEqual(child.Cloaking, Srv.OwnShipCloaking(child));

        // A free-flying ship has no parent: its own value is streamed.
        Assert.Equal(0.25f, Srv.OwnShipCloaking(parent), 5);
    }

    private static void SetCloaking(Ship ship, float value) =>
        typeof(Ship).GetField("<Cloaking>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(ship, value);

    // ── fix 3: owner-only 220 over the real wire ───────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Each_owner_receives_only_its_own_cloak_ramp_on_message_220()
    {
        Require.Artwork();
        using var server = new Srv();
        short index = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod) && h.MaxEnergy > 0);
            var ship = new Ship(shipId, hull with { Thrust = 0f },
                new Vector3(index++ * 2000f, 1000f, 1000f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            ship.MountPart(CloakPart, 1, mountId: 0, fractionMounted: 255);
            ship.Energy = hull.MaxEnergy;
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        };
        server.Boot(Config());
        foreach (var mission in server.Missions) mission.Stage = MissionStage.Started;

        using var a = new ConnectionSession();
        using var b = new ConnectionSession();
        a.Connect(server.BoundPort, "HUD12BA");
        b.Connect(server.BoundPort, "HUD12BB");
        a.SendLogonRequest(Request("HUD12BA"));
        b.SendLogonRequest(Request("HUD12BB"));

        int seated = PumpUntil(server,
            () => a.World.MyShipId is not null && b.World.MyShipId is not null, 6000);
        Assert.True(seated < 6000, "both HUD-12b clients must seat");

        // The per-tick stream primes both owners with the fully-visible value.
        PumpUntil(server, () => a.World.MyCloaking is not null && b.World.MyCloaking is not null, 1000);
        Assert.Equal(1f, a.World.MyCloaking!.Value, 3);
        Assert.Equal(1f, b.World.MyCloaking!.Value, 3);

        var ships = server.Ships.ToDictionary(s => s.Ship.Id, s => s.Ship);
        var shipA = ships[a.World.MyShipId!.Value];
        var shipB = ships[b.World.MyShipId!.Value];

        // Only A engages its cloak. Its own client must see the ramp fall; the
        // other owner must keep receiving its own (fully visible) value, never
        // A's fraction — that is the "only the owner receives it" rule.
        shipA.StateM |= ShipControlState.CloakActive;
        int cloakedA = PumpUntil(server,
            () => a.World.MyCloaking is { } ca && ca < 0.5f, 400);
        Assert.True(cloakedA < 400, $"A's client never received its cloak ramp (got {a.World.MyCloaking})");
        Assert.Equal(1f, b.World.MyCloaking!.Value, 3);
        Assert.NotEqual(a.World.MyCloaking!.Value, b.World.MyCloaking!.Value);

        // Swap: A decloaks, B cloaks. Each owner's stream tracks its own ship.
        shipA.StateM &= ~ShipControlState.CloakActive;
        shipB.StateM |= ShipControlState.CloakActive;
        int cloakedB = PumpUntil(server,
            () => b.World.MyCloaking is { } cb && cb < 0.5f && a.World.MyCloaking is { } ca && ca > 0.99f, 600);
        Assert.True(cloakedB < 600,
            $"owners did not swap their own values (A={a.World.MyCloaking}, B={b.World.MyCloaking})");
    }

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }
}
