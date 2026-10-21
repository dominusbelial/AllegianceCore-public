using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// HUD-17 — LIVE proof that the own-side <c>Detected</c> bit reaches the wire on
/// its native edge, for BOTH copies native maintains:
/// <list type="bullet">
/// <item>the ship's own SHIP_STATUS(90) (its own side's copy), and</item>
/// <item>the same-side teammate status a second side-0 peer receives.</item>
/// </list>
/// Two side-0 peers A and B plus a side-1 ship E whose scanner is the only
/// enemy eye: before E sees A both copies are <c>Detected=false</c>; moving E
/// into range flips both to true within ~20 ticks (not the 100-tick teammate
/// refresh — the HUD-17 send-on-change step); moving E away flips both back;
/// and docking A (which leaves the cluster, <c>Igc/shipIGC.h:421-457</c>) flips
/// them false while E still sees the ship's last position. Port of
/// <c>FedSrv/fsship.cpp:237-335</c> + the changed-status delta
/// <c>FedSrv/FedSrv.CPP:3663-3700</c>.
/// </summary>
[Collection("LiveNetwork")]
public sealed class Hud17DetectionLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static DataHullTypeIGC FighterHull()
        => Core().HullTypes.Values
            .Where(h => h.HitPoints > 0f && !h.HasCapability(DataHullTypeIGC.HabmLifepod))
            .OrderBy(h => h.HitPoints).First();

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "HUD-17 Detection", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], MaxTeamImbalance = 0x7fff },
        ],
    };

    private static LogonRequest Request(string name, ulong steam) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 944u, DwCookie = 0x944u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = steam, DrmHash = "hud17",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Seat(Srv server, FltBot bot, string name, ulong steam, short side)
    {
        bot.AutoSelectSideAfterLogon = false;
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name, steam));
        int ack = PumpUntil(server, () => bot.Ack is { FValidated: true }, 4000);
        Assert.True(ack < 4000, name + " must log on");
        bot.SendPositionReq(side);
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, name + " must be seated on side " + side);
    }

    /// <summary>A base point clear of every real station/asteroid in the sector
    /// (the P132 sweep-test control), so line of sight cannot be occluded.</summary>
    private static Vector3 CleanBase(Sector sector, float clearance = 8000f)
    {
        var content = new List<Vector3>();
        foreach (var a in sector.Asteroids)
            if (a.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));
        foreach (var st in sector.Stations)
            if (st.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));
        float cy = content.Count == 0 ? 0f : content.Average(v => v.Y);
        float cz = content.Count == 0 ? 0f : content.Average(v => v.Z);
        float maxX = content.Count == 0 ? 0f : content.Max(v => v.X);
        return new Vector3(maxX + clearance, cy, cz);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Own_and_teammate_detected_bits_flip_on_the_enemy_visibility_edge()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var hull = FighterHull();

        Ship? a = null, b = null, enemy = null;
        Sector? sector = null;
        Vector3 @base = Vector3.Zero;
        short side0 = 0, side1 = 1;
        int placed = 0;
        server.ShipSpawner = (_, mission, id) =>
        {
            side0 = mission.Sides[0].Id;
            side1 = mission.Sides[1].Id;
            // A side-0 home sector that carries a side-0 landing-capable station,
            // so the docking leg below can use a real ResolveDockCollisions pass.
            sector ??= mission.Sectors.FirstOrDefault(s => s.StationRuntimes.Any(st =>
                st.SideId == side0 && st.Type.HasCapability(DataStationTypeIGC.SabmLand)))
                ?? mission.Sectors[0];
            @base = CleanBase(sector);
            var pos = placed switch
            {
                0 => @base,
                1 => @base + new Vector3(200f, 0f, 0f),
                _ => @base + new Vector3(100_000f, 0f, 0f),   // far outside every scanner
            };
            var side = placed++ < 2 ? side0 : side1;
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(ship, sector);
            if (side == side0 && a is null) a = ship;
            else if (side == side0) b = ship;
            else enemy = ship;
            return (ship, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;   // bypass the ready-up lobby
        var port = server.BoundPort;

        using var aBot = new FltBot();
        using var bBot = new FltBot();
        using var eBot = new FltBot();
        Seat(server, aBot, "hud17_a", 99000000000000011ul, 0);
        Seat(server, bBot, "hud17_b", 99000000000000012ul, 0);
        Seat(server, eBot, "hud17_enemy", 99000000000000013ul, 1);
        Assert.NotNull(a); Assert.NotNull(b); Assert.NotNull(enemy); Assert.NotNull(sector);
        Assert.Equal(side0, a!.SideId);
        Assert.Equal(side0, b!.SideId);
        Assert.Equal(side1, enemy!.SideId);

        // ── BEFORE any enemy sees A: both copies carry false. ────────────────
        int settle = PumpUntil(server, () => !a.SeenBySide(side1), 200);
        Assert.True(settle < 200, "enemy must start out of sight");
        PumpUntil(server, () => aBot.ShipStatuses.Count > 0, 200);
        aBot.ResetShipStatuses();
        bBot.ResetShipStatuses();
        int quiet = PumpUntil(server, () =>
            aBot.ShipStatuses.Any(s => s.ShipId == a.Id) &&
            bBot.ShipStatuses.Any(s => s.ShipId == a.Id), 200);
        Assert.True(quiet < 200, "both peers must receive A's status while unseen");
        Assert.All(aBot.ShipStatuses.Where(s => s.ShipId == a.Id), s => Assert.False(s.Detected));
        Assert.All(bBot.ShipStatuses.Where(s => s.ShipId == a.Id), s => Assert.False(s.Detected));

        // ── SPOT: move the enemy ship onto A. Both copies flip within 20 ticks. ─
        float range = VisibilityMath.DetectionRadius(
            enemy.EffectiveScannerRange(), 0.5f * hull.Length, 0f, hull.Signature);
        Assert.True(range > 100f, $"enemy scanner range {range} must be usable");
        lock (server) { enemy.Position = a.Position + new Vector3(10f, 0f, 0f); enemy.Velocity = Vector3.Zero; }
        int seen = PumpUntil(server, () => a.SeenBySide(side1), 4000);
        Assert.True(seen < 4000, "the enemy ship must come to see A");

        int ownEdge = PumpUntil(server, () =>
            aBot.ShipStatuses.Any(s => s.ShipId == a.Id && s.Detected), 20);
        Assert.True(ownEdge < 20, $"A's OWN status must carry Detected=true within 20 ticks (took {ownEdge})");
        int mateEdge = PumpUntil(server, () =>
            bBot.ShipStatuses.Any(s => s.ShipId == a.Id && s.Detected), 20);
        Assert.True(mateEdge < 20, $"B's TEAMMATE status for A must carry Detected=true within 20 ticks (took {mateEdge})");

        // ── LOSE SIGHT: move the enemy away; both flip back within 20 ticks. ──
        aBot.ResetShipStatuses();
        bBot.ResetShipStatuses();
        lock (server) { enemy.Position = @base + new Vector3(10f * range, 0f, 0f); enemy.Velocity = Vector3.Zero; }
        int unseen = PumpUntil(server, () => !a.SeenBySide(side1), 4000);
        Assert.True(unseen < 4000, "the enemy ship must lose sight of A");
        int ownOff = PumpUntil(server, () =>
            aBot.ShipStatuses.Any(s => s.ShipId == a.Id && !s.Detected), 20);
        Assert.True(ownOff < 20, $"A's OWN status must clear Detected within 20 ticks (took {ownOff})");
        int mateOff = PumpUntil(server, () =>
            bBot.ShipStatuses.Any(s => s.ShipId == a.Id && !s.Detected), 20);
        Assert.True(mateOff < 20, $"B's TEAMMATE status for A must clear Detected within 20 ticks (took {mateOff})");

        // ── SPOT AGAIN, then DOCK: docking leaves the cluster, so false. ──────
        aBot.ResetShipStatuses();
        bBot.ResetShipStatuses();
        lock (server) { enemy.Position = a.Position + new Vector3(10f, 0f, 0f); enemy.Velocity = Vector3.Zero; }
        PumpUntil(server, () => a.SeenBySide(side1), 4000);
        int reSpot = PumpUntil(server, () =>
            aBot.ShipStatuses.Any(s => s.ShipId == a.Id && s.Detected), 20);
        Assert.True(reSpot < 20, "re-spot must set Detected again");

        var station = sector.StationRuntimes.FirstOrDefault(st =>
            st.SideId == side0 && st.Type.HasCapability(DataStationTypeIGC.SabmLand));
        Assert.NotNull(station);
        aBot.ResetShipStatuses();
        bBot.ResetShipStatuses();
        var stationData = sector.Stations.First(st => st.StationId == station!.StationId);
        lock (server)
        {
            // A real landing-bay interior (DockTestPlacement uses the station's
            // native InGarage geometry), so the ordinary ResolveDockCollisions
            // pass latches StationId.
            a.Position = DockTestPlacement.Inside(a.Mission!, stationData, a.HullType);
            a.Velocity = Vector3.Zero;
            a.Controls = ControlData.Neutral;
        }
        int docked = PumpUntil(server, () => a.StationId is not null, 4000);
        Assert.True(docked < 4000, "A must really dock through ResolveDockCollisions");
        int dockOff = PumpUntil(server, () =>
            aBot.ShipStatuses.Any(s => s.ShipId == a.Id && !s.Detected), 20);
        Assert.True(dockOff < 20, $"A's OWN status must be Detected=false once docked (took {dockOff})");
        int mateDockOff = PumpUntil(server, () =>
            bBot.ShipStatuses.Any(s => s.ShipId == a.Id && !s.Detected), 20);
        Assert.True(mateDockOff < 20, $"B's TEAMMATE status must be Detected=false once A docks (took {mateDockOff})");

        // The docked status also carries the docked state, not flying.
        Assert.Contains(aBot.ShipStatuses, s => s.ShipId == a.Id
            && s.State == ShipStatusMessage.StateDocked && !s.Detected);

        Console.WriteLine($"HUD17-LIVE: A={a.Id} B={b.Id} enemy={enemy.Id} side1 scan range {range:0}; " +
            $"own edge {ownEdge} ticks, teammate edge {mateEdge}, own clear {ownOff}, teammate clear {mateOff}, dock clear {dockOff}/{mateDockOff}");
    }
}
