using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Rendering;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P435 — LIVE end-to-end proof, one per new expendable kind (mine, chaff,
/// probe): a REAL human shop sequence (cursor onto the dispenser row, SPACE to
/// buy with its real ammo, L to launch) is replayed over a REAL in-process
/// server + real <see cref="ConnectionSession"/>, the authoritative server
/// (P435 <c>ApplyLoadout</c> via <c>StaticCore.TryBuildExpendableLauncher</c>)
/// GRANTS the real dispenser in the granted loadout, and then the real wire
/// <c>SendFireExpendable</c> makes the server's authoritative
/// <c>FIRE_EXPENDABLE(82)</c> handler deploy the real object (a real Mine /
/// Chaff / Probe in the ship's sector) and decrement the dispenser's ammo —
/// the same proof standard P433 used for missiles, extended to each of the
/// three dispenser kinds. Mirrors <see cref="ShopLauncherBuyLiveTests"/> for the
/// shop half and ChaffTests' live-dispatch mechanics for the fire half.
/// </summary>
[Collection("LiveNetwork")]
public sealed class DispenserEquipFireLiveTests
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
            new Gm { Name = "P435 Dispenser Equip", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 993u, DwCookie = 0x993u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 98900000000000004ul, DrmHash = "p435",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    private static (Ship Ship, Sector Sector) SpawnDocked(
        StaticCore core, Mission mission, short shipId, out short stationId)
    {
        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(st));
        var ship = new Ship(shipId, hull, StationPos(st) + new Vector3(20f, 0f, 0f), Vector3.Zero, Orientation.Identity)
        {
            SideId = st.SideId,
        };
        mission.PlaceShip(ship, sector);
        stationId = st.StationId;
        return (ship, sector);
    }

    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    private static int LauncherFlatIndex(short partId)
    {
        var list = Catalog.Launchers!.ToList();
        return Catalog.Hulls.Count + Catalog.Parts.Count + list.FindIndex(l => l.Part.PartId == partId);
    }

    /// <summary>The EquipmentType the server's FIRE_EXPENDABLE handler resolves a
    /// dispenser of <paramref name="kind"/> at mount 0 (native GetEquipmentType:
    /// chaff → ChaffLauncher; mine/probe → the shared Dispenser).</summary>
    private static EquipmentType FireEt(LauncherKind kind) => kind switch
    {
        LauncherKind.Chaff => EquipmentType.ChaffLauncher,
        LauncherKind.Mine or LauncherKind.Probe => EquipmentType.Dispenser,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The expendable type id a deployed object of <paramref name="kind"/>
    /// must carry (from the bought dispenser's construction-only subtype).</summary>
    private static short ExpendableId(LauncherOffer lo) => lo.Part.Type switch
    {
        DataPartTypeIGC.ChaffLauncher cl => cl.ExpendableTypeId,
        DataPartTypeIGC.MineLauncher ml => ml.ExpendableTypeId,
        DataPartTypeIGC.ProbeLauncher pl => pl.ExpendableTypeId,
        _ => throw new InvalidOperationException($"offer {lo.Part.PartId} is not a chaff/mine/probe dispenser"),
    };

    /// <summary>Locate the server-side <see cref="Ship"/> by id through the PUBLIC
    /// mission/sector model graph (no access to the server's internal peer-ship
    /// registry needed) — the authoritative server object whose mounted parts,
    /// ammo and sector deployments we assert.</summary>
    private static Ship ShipById(Srv server, short id) =>
        server.Missions[0].Sectors.SelectMany(s => s.Models.OfType<Ship>())
            .First(s => s.Id == id);

    /// <summary>⭐ P435 — the headline live proof for one dispenser kind: buy it in
    /// the real shop, launch (BUY_LOADOUT fLaunch) so the server's authoritative
    /// ApplyLoadout GRANTS it, then fire the real wire FIRE_EXPENDABLE and confirm
    /// the server deploys the real object and decrements the ammo.</summary>
    [Theory]
    [InlineData(LauncherKind.Mine)]
    [InlineData(LauncherKind.Chaff)]
    [InlineData(LauncherKind.Probe)]
    public void BuyMountAndFireARealDispenser_Live(LauncherKind kind)
    {
        using var server = new Srv();
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
            SpawnDocked(core, mission, id, out _));
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var session = new ConnectionSession();
        string botName = "p435_" + kind;
        session.Connect(server.BoundPort, botName);
        session.SendLogonRequest(Request(botName));

        int join = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "timed out waiting for ship assignment");
        short myId = session.World.MyShipId!.Value;

        int docked = PumpUntil(server, () => session.World.Ships[myId].StationId != -1, 8000);
        Assert.True(docked < 8000, $"ship never docked (StationId={session.World.Ships[myId].StationId})");

        // A REAL dispenser of this kind offered by the shop (real core.Launchers
        // record whose expendable is a real mine/chaff/probe type).
        var lo = Catalog.Launchers!.First(l => l.Kind == kind);
        short spawnedHull = session.World.Ships[myId].HullId;

        // HUMAN SHOP SEQUENCE (Game1.Panes): open the shop on the dock, cursor onto
        // the dispenser row, SPACE to buy it with its real ammo, L to launch.
        var shop = new ShopModel(Catalog, spawnedHull, session.World.Ships[myId].Loadout!);
        shop.MoveCursor(LauncherFlatIndex(lo.Part.PartId) - shop.Cursor);
        Assert.True(shop.CursorOnLauncher, "cursor must rest on the dispenser row");
        shop.Activate();                                        // SPACE: buy it

        var bought = Assert.Single(shop.ChosenParts);
        Assert.Equal(lo.Part.PartId, bought.PartId);            // the real dispenser id
        Assert.Equal(lo.Ammo, bought.Amount);                   // the real ammo reserve

        // L — launch on the SPAWNED hull (no hull swap). P334's CanMountPart gate
        // applies only on the hull-CHANGE path; a same-hull loadout mounts freely
        // (free-mount), which is how the shop buys a dispenser on the flown hull.
        session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);
        int undocked = PumpUntil(server, () => session.World.Ships[myId].StationId == -1, 8000);
        Assert.True(undocked < 8000, "fLaunch=true never undocked the ship");

        // ⭐ The authoritative server actually GRANTED the dispenser (ApplyLoadout
        // via TryBuildExpendableLauncher) at its real ammo, mounted under the right
        // equipment slot at mount 0.
        var serverShip = ShipById(server, myId);
        var mounted = serverShip.Parts.First(p => p.PartId == lo.Part.PartId);
        Assert.Equal(lo.Ammo, mounted.Amount);
        Assert.Equal(FireEt(kind), mounted.EquipmentType);      // right equipment slot
        Assert.Equal(0, mounted.MountId);                        // mount 0, what the fire handler resolves
        short ammoBefore = mounted.Amount;

        // ⭐ Fire it over the real wire (FM_C_FIRE_EXPENDABLE(82)).
        session.SendFireExpendable(FireEt(kind));

        // The server's authoritative HandleFireExpendable deploys the real object
        // and decrements the dispenser's ammo by 1.
        int fired = PumpUntil(server, () =>
            ShipById(server, myId).Parts.First(p => p.PartId == lo.Part.PartId).Amount < ammoBefore,
            6000);
        Assert.True(fired < 6000, "server never decremented the dispenser ammo after FIRE_EXPENDABLE");
        Assert.Equal((short)(ammoBefore - 1),
            ShipById(server, myId).Parts.First(p => p.PartId == lo.Part.PartId).Amount);

        var sector = ShipById(server, myId).Sector!;
        short expId = ExpendableId(lo);
        switch (kind)
        {
            case LauncherKind.Chaff:
                Assert.Contains(sector.Models.OfType<Chaff>(), c => c.ChaffType.ExpendableTypeId == expId);
                break;
            case LauncherKind.Mine:
                Assert.Contains(sector.Models.OfType<Mine>(), m => m.MineType.ExpendableTypeId == expId);
                break;
            case LauncherKind.Probe:
                Assert.Contains(sector.Models.OfType<Probe>(), p => p.ProbeType.ExpendableTypeId == expId);
                break;
        }
    }
}
