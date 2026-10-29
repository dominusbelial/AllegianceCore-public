using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P776 — the aleph trigger is native's <b>cone</b>, not a sphere
/// (<c>Igc/modelIGC.cpp:127-130</c>, <c>_Utility/HitTest.cpp:254-283</c>).
/// These tests pin three things: the pure cone geometry
/// (<see cref="AlephConeHitTest"/>), that real approaches still transit, and
/// that a native-exact exit — 5&nbsp;u in front of the destination disc — no
/// longer re-triggers after the re-arm. The live cases use the shipped
/// <c>m2xspiderw.igc</c> map and Tellus warp 2157, the aleph from the batch-16
/// rig bounce.
/// </summary>
[Collection("LiveNetwork")]
public class P776AlephConeTriggerTests(ITestOutputHelper output)
{
    private const short TellusWarpId = 2157;   // Tellus (1055) -> Omega (1053)

    // ── pure geometry / scenarios (no network) ───────────────────────────────

    private static readonly Lazy<(StaticCore Core, MissionMap Map)> Assets = new(() =>
    {
        var coreFile = IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc"));
        if (coreFile.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        var mapFile = IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "m2xspiderw.igc"));
        if (mapFile.Wrapper != IgcWrapper.DataSize)
            throw new InvalidDataException("m2xspiderw.igc is not a mission map");
        return (StaticCore.Load(coreFile), MissionMap.Load(mapFile));
    });

    /// <summary>Records only the warp hits belonging to the ship under test, so
    /// map initialisation traffic cannot mask a miss.</summary>
    private sealed class WarpRecorder : IIgcSite
    {
        public List<(short ShipId, short WarpId)> Hits { get; } = new();
        public void HitWarpEvent(IShipIgc ship, IWarpIgc warp)
        {
            if (ship is Ship s && warp is ResolvedWarp rw) Hits.Add((s.Id, rw.Warp.WarpId));
        }
        public int CountFor(short shipId) => Hits.Count(h => h.ShipId == shipId);
    }

    private static (Mission Mission, Sector Sector, ResolvedWarp Warp) NewScenario(IIgcSite site)
    {
        var (core, map) = Assets.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var mission = Mission.Assemble(core, map, [new SideSpec(0, "Team 1", civ)], site);
        mission.Stage = MissionStage.Started;
        var warp = mission.Warps.First(w => w.Warp.WarpId == TellusWarpId);
        var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
        return (mission, sector, warp);
    }

    private static Vector3 Vec(float[]? v, Vector3 fallback) =>
        v is { Length: 3 } ? new Vector3(v[0], v[1], v[2]) : fallback;

    private static void Tick(Mission m, int n, double dt = 0.05)
    {
        double t = m.LastUpdate;
        for (int i = 0; i < n; i++) { t += dt; m.Update(t); }
    }

    /// <summary>⭐ Test 1 — the pure cone section. With R = 100, r = 10 the
    /// triangle <c>(0,0),(R,0),(0,R)</c> in <c>(rho, z)</c> must hit inside and
    /// within r of it, and miss on the forward side of the disc / beyond the
    /// apex / outside the slant. The same answers must hold for a non-unit
    /// forward (the wire vectors are not unit length, e.g. warp 2156's
    /// <c>(1,-0.811,0.088)</c>). New API: this cannot run on the unfixed base.</summary>
    [SkippableTheory]
    [InlineData(0f, 50f, true)]      // backward axis, mid-cone
    [InlineData(0f, 0f, true)]       // the centre
    [InlineData(50f, -9f, true)]     // front of the disc, within r
    [InlineData(0f, 105f, true)]     // at the apex, within r
    [InlineData(7f, -15f, false)]    // native exit spot: 15 u in front, rho 7
    [InlineData(0f, -30f, false)]    // front of the disc
    [InlineData(111f, 0f, false)]    // outside the rim
    [InlineData(0f, 115f, false)]    // beyond the apex
    [InlineData(55f, 60f, false)]    // outside the slant (55+60 > 100, dist ~10.6 > 10)
    public void Cone_section_matches_native_geometry(float rho, float z, bool expected)
    {
        Assert.Equal(expected, Probe(new Vector3(0f, 0f, -1f), rho, z));
        // Non-unit forward with the same local frame, plus a tilted axis.
        Assert.Equal(expected, Probe(new Vector3(0f, 0f, -2.5f), rho, z));
        Assert.Equal(expected, Probe(new Vector3(1f, -0.811f, 0.088f), rho, z));
    }

    private static bool Probe(Vector3 forward, float rho, float z)
    {
        var backward = AlephConeHitTest.Backward(forward);
        // Any unit vector perpendicular to backward gives the same rho.
        var side = Vector3.CrossProduct(backward, new Vector3(0f, 1f, 0f));
        if (side.LengthSquared() < 1e-6f) side = Vector3.CrossProduct(backward, new Vector3(1f, 0f, 0f));
        side = side.Normalize();
        var centre = new Vector3(0f, 0f, 0f);
        var ship = centre + backward * z + side * rho;
        return AlephConeHitTest.Touches(centre, forward, 100f, ship, 10f);
    }

    /// <summary>⭐ Test 5 — the trigger still fires for every real approach.
    /// Guards against over-tightening the cone. Passes on the unfixed base too
    /// (the old sphere was strictly larger). Budgets are sim ticks at 20 Hz.</summary>
    [SkippableTheory]
    [InlineData("front")]
    [InlineData("back")]
    [InlineData("slow")]
    [InlineData("centre")]
    public void Ship_reaching_the_aleph_still_warps(string approach)
    {
        Require.Artwork();
        var recorder = new WarpRecorder();
        var (mission, sector, warp) = NewScenario(recorder);
        var wpos = Vec(warp.Warp.Position, Vector3.Zero);
        var backward = AlephConeHitTest.Backward(Vec(warp.Warp.Forward, new Vector3(0f, 0f, -1f)));
        var hull = Assets.Value.Core.HullTypes[121];               // Fighter, length 14
        float r = 0.5f * hull.Length;

        Vector3 position, velocity;
        int budget;
        bool coast = false;
        switch (approach)
        {
            case "front":                                          // nose-on at cruise
                position = wpos - backward * 40f;
                velocity = backward * hull.Speed;
                budget = 40;
                break;
            case "back":                                           // along the backward axis, beyond the apex
                position = wpos + backward * (warp.Warp.Radius + 50f);
                velocity = -backward * hull.Speed;
                budget = 60;
                break;
            case "slow":                                           // 5 u/s, vector-locked (no drag)
                position = wpos - backward * 15f;
                velocity = backward * 5f;
                budget = 200;
                coast = true;
                break;
            default:                                               // parked at the centre: native warps it
                position = wpos;
                velocity = Vector3.Zero;
                budget = 5;
                break;
        }

        // Point the nose along the motion so the throttle does not fight it;
        // a zero velocity (the centre case) uses identity.
        var orientation = velocity.LengthSquared() > 0f ? Facing(velocity.Normalize()) : Orientation.Identity;
        var ship = new Ship(9001, hull, position, velocity, orientation) { SideId = 0 };
        if (coast) ship.StateM = ShipControlState.CoastButton;
        else if (velocity.LengthSquared() > 0f) ship.Controls = new ControlData(0f, 0f, 0f, 1f);
        mission.PlaceShip(ship, sector);

        for (int i = 0; i < budget && recorder.CountFor(ship.Id) == 0; i++)
            mission.Update(mission.LastUpdate + 0.05);

        Assert.True(recorder.CountFor(ship.Id) >= 1,
            $"'{approach}' approach must still raise HitWarpEvent within {budget} ticks " +
            $"(pos {position}, vel {velocity}, warp radius {warp.Warp.Radius}, ship r {r})");
    }

    /// <summary>⭐ Test 6 — a ship whose per-tick displacement (50 u) exceeds the
    /// cone's thickness near <c>rho = 0.95R</c> (about 19 u) crosses the disc
    /// there and must still warp. The end-of-tick point is outside the cone but
    /// still inside the old sphere, so this passes on the unfixed base and fails
    /// if the cone is point-sampled at the end of the chunk instead of swept.</summary>
    [SkippableFact]
    public void Fast_ship_crossing_the_rim_is_caught_by_the_sweep()
    {
        Require.Artwork();
        var recorder = new WarpRecorder();
        var (mission, sector, warp) = NewScenario(recorder);
        var wpos = Vec(warp.Warp.Position, Vector3.Zero);
        var backward = AlephConeHitTest.Backward(Vec(warp.Warp.Forward, new Vector3(0f, 0f, -1f)));
        var side = Vector3.CrossProduct(backward, new Vector3(0f, 1f, 0f));
        if (side.LengthSquared() < 1e-6f) side = Vector3.CrossProduct(backward, new Vector3(1f, 0f, 0f));
        side = side.Normalize();

        float rho = 0.95f * warp.Warp.Radius;
        var start = wpos + side * rho - backward * 30f;   // in front, just inside the rim
        var velocity = backward * 1000f;                  // 50 u in a 0.05 s chunk (> 19 u slab)
        var ship = new Ship(9002, Assets.Value.Core.HullTypes[121], start, velocity,
                            Facing(backward))
        {
            SideId = 0,
            StateM = ShipControlState.CoastButton,        // hold the speed (no drag)
        };
        mission.PlaceShip(ship, sector);

        mission.Update(mission.LastUpdate + 0.05);

        Assert.True(recorder.CountFor(ship.Id) >= 1,
            "the swept cone test must catch a 50 u/tick crossing near the rim");
    }

    // ── live server cases (real m2xspiderw map) ──────────────────────────────

    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = new Ls(),
        Games =
        [
            new Gm { Name = "P776", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name, ulong steamId) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 911u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = steamId, DrmHash = name,
    };

    private static void PumpUntil(Srv server, Func<bool> done, int max)
    {
        for (int i = 0; i < max && !done(); i++) { server.TickSim(); Thread.Sleep(1); }
    }

    private static Orientation Facing(Vector3 forward)
    {
        var o = new Orientation();
        o.Set(forward);
        return o;
    }

    private static int CountSimTicks(Srv server, Ship ship, short sourceSectorId, int ticks,
                                     Action<Ship>? eachTick = null)
    {
        short last = sourceSectorId;
        int crossings = 0;
        for (int i = 0; i < ticks; i++)
        {
            server.TickSim();
            Thread.Sleep(1);
            if (ship.Sector?.Id != last)
            {
                crossings++;
                last = ship.Sector!.Id;
            }
            eachTick?.Invoke(ship);
        }
        return crossings;
    }

    /// <summary>
    /// ⭐ Test 2 — a ship that transits Tellus and exits at the native spot
    /// (shipRadius+5 in front of Omega's disc) must not re-warp when it re-arms
    /// and lingers there. Before P776 the trigger sphere (~110 u both sides of
    /// the disc) swallowed the exit and produced 6 crossings in 10 s; after the
    /// cone it is exactly 1, the ship stays in Omega.
    /// </summary>
    [SkippableTheory]
    [InlineData((short)126, 1.0f)]   // Guardian, cruise 30
    [InlineData((short)121, 0.1f)]   // Fighter, braked to 10
    public void Exited_ship_lingering_in_front_of_the_destination_aleph_does_not_rewarp(
        short hullId, float speedFrac)
    {
        Require.Artwork();
        using var server = new Srv();
        Sector? source = null;
        ResolvedWarp? entry = null;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes[hullId];
            var warp = mission.Warps.First(w => w.Warp.WarpId == TellusWarpId);
            entry = warp;
            var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
            source = sector;
            var wpos = Vec(warp.Warp.Position, Vector3.Zero);
            var forward = Vec(warp.Warp.Forward, new Vector3(0f, 0f, -1f)).Normalize();
            // Just in front of the disc, nose in: the same starting point the
            // port's native-exact exit uses (shipRadius+5). The old sphere's
            // 110 u rim is too far: native drag/braking stops a coasting hull
            // before it ever reaches the disc (measured — see the report).
            var ship = new Ship(shipId, hull, wpos + forward * 5f,
                                -forward * hull.Speed * speedFrac, Orientation.Identity)
            { SideId = 0 };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p776-one");
        bot.SendLogonRequest(Request("p776-one", 72000000000000077ul));
        PumpUntil(server, () => server.Ships.Count >= 1, 4000);
        var ship = server.Ships.First().Ship;
        Assert.NotNull(source);
        Assert.NotNull(entry);

        int crossings = CountSimTicks(server, ship, source!.Id, 20 * 10, s =>
        {
            if (s.Sector != entry!.Destination)
                output.WriteLine($"t={server.Ships.Count} sector={s.Sector?.Id} pos={s.Position} speed={s.Velocity.Length():F1}");
        });

        Assert.Equal(1, crossings);
        Assert.Equal(entry!.Destination!.Id, ship.Sector!.Id);
    }

    /// <summary>
    /// ⭐ Test 3 — two Guardians transit on the same tick and collide at the
    /// shared exit spot; neither may bounce back. Before P776 each had already
    /// re-entered by the 2 s re-arm.</summary>
    [SkippableFact]
    public void Two_ships_warping_together_do_not_bounce()
    {
        Require.Artwork();
        using var server = new Srv();
        Sector? source = null;
        ResolvedWarp? entry = null;
        int spawned = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes[126];                    // Guardian
            var warp = mission.Warps.First(w => w.Warp.WarpId == TellusWarpId);
            entry = warp;
            var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
            source = sector;
            var wpos = Vec(warp.Warp.Position, Vector3.Zero);
            var forward = Vec(warp.Warp.Forward, new Vector3(0f, 0f, -1f)).Normalize();
            var side = Vector3.CrossProduct(forward, new Vector3(0f, 1f, 0f)).Normalize();
            float offset = spawned++ == 0 ? -1f : 1f;
            var ship = new Ship(shipId, hull, wpos + forward * 5f + side * offset,
                                -forward * hull.Speed, Orientation.Identity)
            { SideId = 0 };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var botA = new FltBot();
        using var botB = new FltBot();
        botA.Connect(server.BoundPort, "p776-a");
        botB.Connect(server.BoundPort, "p776-b");
        botA.SendLogonRequest(Request("p776-a", 72000000000000078ul));
        botB.SendLogonRequest(Request("p776-b", 72000000000000079ul));
        PumpUntil(server, () => server.Ships.Count >= 2, 6000);

        var ships = server.Ships.Select(x => x.Ship).ToList();
        Assert.Equal(2, ships.Count);
        Assert.NotNull(source);
        var crossings = ships.Select(_ => 0).ToArray();
        var last = new[] { source!.Id, source!.Id };
        for (int t = 0; t < 20 * 10; t++)
        {
            server.TickSim();
            Thread.Sleep(1);
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].Sector?.Id != last[i])
                {
                    crossings[i]++;
                    last[i] = ships[i].Sector!.Id;
                }
            }
        }

        Assert.All(crossings, c => Assert.InRange(c, 0, 1));
        Assert.All(ships, s => Assert.Equal(entry!.Destination!.Id, s.Sector!.Id));
    }

    /// <summary>
    /// ⭐ Test 4 — the NPC path (<see cref="Ship.IsNpc"/> keeps
    /// <see cref="Ship.WarpReady"/> always true; only the port-only 2 s NPC
    /// cooldown gates it). A slowly exiting drone must not re-warp either.
    /// </summary>
    [SkippableFact]
    public void Npc_exited_slowly_does_not_rewarp()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var mission = server.Missions[0];
        var warp = mission.Warps.First(w => w.Warp.WarpId == TellusWarpId);
        var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
        var wpos = Vec(warp.Warp.Position, Vector3.Zero);
        var forward = Vec(warp.Warp.Forward, new Vector3(0f, 0f, -1f)).Normalize();

        var side = mission.Sides[0].Id;
        var npc = server.SpawnNpcShip(mission, sector, side, wpos + forward * 5f);
        npc.Velocity = -forward * 60f;                         // slow, nose in

        // Hold the drone at the native exit spot once it arrives (15 u in front
        // of the destination disc): that is the batch-16 "lingering exit" pose.
        // On the unfixed sphere it is still inside the 2 s re-arm volume and
        // bounces; on the cone it is outside native's shape and stays put.
        int crossings = CountSimTicks(server, npc, sector.Id, 20 * 10, s =>
        {
            if (s.Sector?.Id == warp.Destination!.Id) s.Velocity = Vector3.Zero;
        });

        Assert.Equal(1, crossings);
        Assert.Equal(warp.Destination!.Id, npc.Sector!.Id);
    }
}
