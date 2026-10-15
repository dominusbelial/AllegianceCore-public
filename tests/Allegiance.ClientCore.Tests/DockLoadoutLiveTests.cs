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
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Live integration proof for the new docking/loadout surface, against a real
/// in-process server over real loopback TCP:
///
///  1. DOCK/UNDOCK — a ship seated inside a friendly station's dock radius
///     docks; <see cref="ConnectionSession"/> receives the <c>FM_S_DOCKED(61)</c>
///     notify and <see cref="ClientWorld"/> latches <c>StationId</c>. Then a
///     real <c>FM_C_DOCKED(48)</c> undock (stationId = NA) clears it, and the
///     subsequent SHIP_STATUS/SetCluster traffic reverts <c>StationId</c> to
///     flying.
///
///  2. BUY_LOADOUT — the real <c>FM_C_BUY_LOADOUT(4)</c> round-trips against a
///     live server: the ACK updates the own-ship entity (hull + loadout), and
///     with <c>fLaunch=true</c> on a docked ship the proven undock + fresh
///     SetCluster follows (the Hangar "Launch" path), reverting <c>StationId</c>
///     and moving the ship.
///
/// Accessibility note: the read-only <c>Allegiance.Server</c> assembly exposes
/// its internals (including the <c>ShipSpawner</c> hook and <c>Ships</c>) only
/// to <c>Allegiance.Core.Tests</c> and its own tools (AssemblyInfo.cs) — not to
/// this assembly. The only way to seat a ship for docking is therefore the
/// reflection-set <c>ShipSpawner</c> field, mirroring <c>DockDockingTests</c>'s
/// spawner shape exactly (same coordinate/offset recipe). Nothing read-only is
/// modified; all observable assertions read the client's own
/// <see cref="ClientWorld"/>.
/// </summary>
[Collection("LiveNetwork")]
public class DockLoadoutLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Dock/Loadout Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 612u, DwCookie = 0x612u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 85000000000000000ul, DrmHash = "dockload",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    // The ShipSpawner hook is `internal` (friend-gated to Allegiance.Core.Tests
    // and the tools) — unreachable from here directly, so reach it through
    // reflection. Field name pinned to the read-only declaration; value is the
    // exact spawner shape DockDockingTests uses (friendly station's side,
    // inside DockRadius).
    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    /// <summary>Seat the peer's ship on a friendly station's side, just inside
    /// DockRadius — the dock arrangement DockDockingTests uses. The chosen
    /// station id is written to <paramref name="stationId"/> for the test to
    /// assert against.</summary>
    private static (Ship Ship, Sector Sector) SpawnDocked(
        StaticCore core, Mission mission, short shipId, out short stationId)
    {
        // H-07: the fixture purchases a real gun at hardpoint zero.
        var hull = EconomyLoadoutTestSetup.WeaponHull(core);
        var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(st));
        var ship = new Ship(shipId, hull, StationPos(st) + new Vector3(20f, 0f, 0f), Vector3.Zero, Orientation.Identity)
        {
            SideId = st.SideId, StationId = st.StationId,
        };
        mission.PlaceShip(ship, sector);
        stationId = st.StationId;
        return (ship, sector);
    }

    // The representative static core the server boots (PCore001.igc under
    // Artwork), parsed once and reused — used only to pick a REAL weapon part id
    // for the loadout test, the same way the server's own part resolution
    // (StaticCore.PartTypes keyed by PartId) and AllegianceCore's
    // LoadoutRoundTripTests do.
    private static readonly StaticCore RepresentativeCore =
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>The first real weapon part id in the representative core (any
    /// Weapon subtype), ordered by PartId. Guaranteed resolvable by the server's
    /// ApplyLoadout (it looks up PartTypes keyed by PartId), so it always mounts
    /// and the ACK tail is non-empty — never a fabricated value.</summary>
    private static ExpandedPartData RealWeaponPart() =>
        new(
            RepresentativeCore.PartTypes.Values
                .Where(p => p.EquipmentType == EquipmentType.Weapon)
                .OrderBy(p => p.PartId)
                .First().PartId,
            Amount: 20, MountId: 0, FractionMounted: ExpandedPartData.FullyMounted);


    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void DockedNotify_SetsAndClearsStationId_Live()
    {
        Require.Artwork();
        using var server = new Srv();
        short friendlyStation = -1;
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var r = SpawnDocked(core, mission, shipId, out short st);
            friendlyStation = st;
            return r;
        });
        server.Boot(Config());

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "dock_live");
        session.SendLogonRequest(Request("dock_live"));

        int join = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "timed out waiting for ship assignment");
        short myId = session.World.MyShipId!.Value;

        // P180/P188 gate: the per-tick dock-radius detection lives in the sector
        // passes that only run for a Started (or Starting) mission — while
        // NotStarted, Mission.Update does nothing, so the ship is already inside
        // DockRadius yet never docks. This single peer readying up satisfies
        // IsAllOccupiedSidesReady; then we observe the server-side Mission.Stage
        // (Server.Missions[0]) crossing into Started through the 15s countdown.
        session.SendPlayerReady(fReady: true, shipId: myId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, $"mission never reached Started after single-peer ready-up (stage={server.Missions[0].Stage})");

        // The dock event fires within a tick or two of spawn (ship is already
        // inside the radius): the FM_S_DOCKED(61) notify must latch StationId.
        int docked = PumpUntil(server, () => session.World.Ships[myId].StationId != -1, 8000);
        Assert.True(docked < 8000, $"ship never docked (StationId={session.World.Ships[myId].StationId})");
        Assert.Equal(friendlyStation, session.World.Ships[myId].StationId);

        // Undock announce (FM_C_DOCKED, stationID = NA) → the ship leaves the
        // dock; the fresh SetCluster + SHIP_STATUS revert StationId to flying.
        session.SendDocked();
        int freed = PumpUntil(server, () => session.World.Ships[myId].StationId == -1, 8000);
        Assert.True(freed < 8000, "ship never undocked (StationId stayed latched)");
        Assert.Equal(-1, session.World.Ships[myId].StationId);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void BuyLoadout_RoundTrips_And_WithFLaunch_Undocks_Live()
    {
        Require.Artwork();
        using var server = new Srv();
        short friendlyStation = -1;
        Vector3 dockedPos = default;
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var r = SpawnDocked(core, mission, shipId, out short st);
            friendlyStation = st;
            dockedPos = r.Ship.Position;
            return r;
        });
        server.Boot(Config());

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "launch_live");
        session.SendLogonRequest(Request("launch_live"));

        int join = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "timed out waiting for ship assignment");
        short myId = session.World.MyShipId!.Value;

        // P180/P188 gate: dock-radius detection (and thus the StationId latch
        // the launch phase needs to undo) only runs for a Started/Starting
        // mission. This single peer readying up satisfies IsAllOccupiedSidesReady;
        // observe the server-side Mission.Stage (Server.Missions[0]) reaching
        // Started through the 15s countdown before expecting any dock event.
        session.SendPlayerReady(fReady: true, shipId: myId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, $"mission never reached Started after single-peer ready-up (stage={server.Missions[0].Stage})");

        int docked = PumpUntil(server, () => session.World.Ships[myId].StationId != -1, 8000);
        Assert.True(docked < 8000, "ship never docked before launch");
        Assert.Equal(friendlyStation, session.World.Ships[myId].StationId);
        // The server already sent a join-burst adopt-a-hull ACK (empty tail), so
        // Loadout is an empty list before our request — not null.
        Assert.NotNull(session.World.Ships[myId].Loadout);
        Assert.Empty(session.World.Ships[myId].Loadout!);
        short hull = session.World.Ships[myId].HullId;
        Assert.NotEqual((short)0, hull);                          // SHIP_STATUS assigned a real hull

        // The real client's Launch-click message: BUY_LOADOUT, fLaunch=true,
        // hull-only (nParts=0) — HangarScreen::OnButtonLaunch's own call shape.
        session.SendBuyLoadout(hull, fLaunch: true);

        // (a) The ACK updates the own-ship entity: adopted hull + empty tail.
        int acked = PumpUntil(server, () => session.World.Ships[myId].Loadout is not null, 8000);
        Assert.True(acked < 8000, "BUY_LOADOUT_ACK never landed on the world");
        Assert.Equal(hull, session.World.Ships[myId].HullId);
        Assert.NotNull(session.World.Ships[myId].Loadout);
        Assert.Empty(session.World.Ships[myId].Loadout!);

        // (b) fLaunch=true on a docked ship → the server undocks it (the fresh
        // SetCluster + subsequent SHIP_STATUS), so StationId reverts and the
        // ship leaves the dock.
        int undocked = PumpUntil(server,
            () => session.World.Ships[myId].StationId == -1 &&
                  (session.World.Ships[myId].Latest!.Position - dockedPos).Length() > 1e-3f,
            8000);
        Assert.True(undocked < 8000, "fLaunch=true never undocked the ship");
        Assert.Equal(-1, session.World.Ships[myId].StationId);
    }

    /// <summary>⭐ The real, non-hull-only loadout send: BUY_LOADOUT(4) with a
    /// genuinely non-empty part list. Proves <see cref="ConnectionSession.SendBuyLoadout"/>
    /// is wired end-to-end (its <c>parts</c> parameter reaches the wire and the
    /// server's ApplyLoadout), and that the resulting BUY_LOADOUT_ACK(5)
    /// populates <see cref="ClientWorld"/>'s <c>ShipEntity.Loadout</c> with the
    /// real parts — NOT an empty hull-only list. The part id is pulled from the
    /// same static core the server boots, so it always resolves and mounts.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void BuyLoadout_WithRealParts_PopulatesShipLoadout_Live()
    {
        Require.Artwork();
        using var server = new Srv();
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var r = SpawnDocked(core, mission, shipId, out _);
            return r;
        });
        server.Boot(Config());

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "parts_live");
        session.SendLogonRequest(Request("parts_live"));

        int join = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "timed out waiting for ship assignment");
        short myId = session.World.MyShipId!.Value;

        // The join-burst adopt-a-hull ACK set Loadout to [] — hull-only so far.
        int loaded = PumpUntil(server, () => session.World.Ships[myId].Loadout is not null, 4000);
        Assert.True(loaded < 4000, "join-burst adopt-a-hull ACK never populated Loadout");
        Assert.Empty(session.World.Ships[myId].Loadout!);
        short hull = session.World.Ships[myId].HullId;
        Assert.NotEqual((short)0, hull);

        // A genuinely non-empty request: a real weapon part on mount 0.
        var requested = RealWeaponPart();
        EconomyLoadoutTestSetup.Sell(server.Missions[0], server.Missions[0].Core.PartTypes[requested.PartId]);
        session.SendBuyLoadout(hull, fLaunch: false, parts: new[] { requested });

        // The ACK mounts the real part, so ShipEntity.Loadout becomes non-empty.
        int acked = PumpUntil(server, () => session.World.Ships[myId].Loadout is { Count: > 0 }, 8000);
        Assert.True(acked < 8000, "BUY_LOADOUT_ACK with real parts never populated Loadout");
        Assert.Single(session.World.Ships[myId].Loadout!);
        Assert.Equal(requested, session.World.Ships[myId].Loadout![0]);   // byte-for-byte round-trip
        Assert.Equal(hull, session.World.Ships[myId].HullId);              // hull unchanged (same hull requested)
    }
}
