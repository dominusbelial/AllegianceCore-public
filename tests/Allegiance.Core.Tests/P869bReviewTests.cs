using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public sealed class P869bReviewTests
{
    // Isolate registration overhead from the pre-existing projectile/model
    // allocations by reusing the emitted models. Exercise the actual sector
    // Add/Terminate paths, ship moves and changing orders each logical tick.
    [SkippableFact]
    public void Projectile_emission_retirement_moves_and_goto_allocate_no_bytes_after_warmup()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
        using var mission = Mission.Assemble(core, map, [new(0, "A", 1), new(1, "B", 3)], new NullIgcSite());
        var sector = mission.Sectors[0];
        var ship = new Ship(30000, core.HullTypes.Values.First(), Vector3.Zero, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(ship, sector);
        var projectiles = Enumerable.Range(0, 240).Select(i => new Projectile((short)(10000 + i), sector,
            core.ProjectileTypes.Values.First(), Vector3.Zero, Vector3.Zero, new(0, 0, 1), 0)).ToArray();
        void Tick(int i)
        {
            foreach (var projectile in projectiles) sector.Add(projectile);
            foreach (var projectile in projectiles) sector.Terminate(projectile);
            sector.Remove(ship); sector.Add(ship);
            ship.GotoWaypoint = new GotoOrder(GotoKind.Waypoint, sector.Id, new(i, 2, 3));
        }
        for (int i = 0; i < 100; i++) Tick(i);
        for (int window = 0; window < 3; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 500; i++) Tick(i);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, bytes);
        }
        Assert.All(projectiles, p => Assert.Null(p.ThingSite));
        Assert.Single(mission.GetShips());
        // The deferred identity still materializes the last order correctly.
        Assert.Equal(new Vector3(499, 2, 3), Assert.Single(mission.GetBuoys()).Position);
    }

    [SkippableFact]
    public void Lazy_buoys_preserve_command_order_live_views_and_constant_time_removal()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
        using var mission = Mission.Assemble(core, map, [new(0, "A", 1), new(1, "B", 3)], new NullIgcSite());
        Ship Place(short id)
        {
            var s = new Ship(id, core.HullTypes.Values.First(), Vector3.Zero, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(s, mission.Sectors[0]); return s;
        }
        var a = Place(30000); var b = Place(30001);
        b.GotoWaypoint = new(GotoKind.Waypoint, 0, new(1, 0, 0));
        a.GotoWaypoint = new(GotoKind.Waypoint, 0, new(2, 0, 0));
        b.GotoWaypoint = new(GotoKind.Waypoint, 0, new(3, 0, 0));
        var view = mission.GetBuoys();
        Assert.Equal(new[] { 2f, 3f }, view.Select(v => v.Position.X));
        var previous = view[0];
        a.GotoWaypoint = null;
        Assert.Single(view); Assert.Null(mission.GetBuoy(previous.Id));
        a.GotoWaypoint = new(GotoKind.Waypoint, 0, new(4, 0, 0));
        Assert.Equal(new[] { 3f, 4f }, view.Select(v => v.Position.X));
        mission.Sectors[0].Terminate(b);
        Assert.Equal(4f, Assert.Single(view).Position.X);
    }

    [SkippableFact]
    public void Client_file_revision_reloads_while_old_world_keeps_its_core()
    {
        Require.Artwork();
        string folder = Path.Combine(Environment.CurrentDirectory, ".scratch", "p869b-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string? previous = Environment.GetEnvironmentVariable(ExternalPaths.ArtworkEnvVar);
        try
        {
            string path = Path.Combine(folder, "edit.igc");
            var bytes = File.ReadAllBytes(Path.Combine(TestArtRoot.Value, "PCore001.igc"));
            File.WriteAllBytes(path, bytes);
            DateTime initialTime = File.GetLastWriteTimeUtc(path);
            Environment.SetEnvironmentVariable(ExternalPaths.ArtworkEnvVar, folder);
            using var a = new ConnectionSession(); using var same = new ConnectionSession(); using var edited = new ConnectionSession();
            void Join(ConnectionSession session)
            {
                session.World.Apply(new MissionDef { SzIGCStaticFile = "edit" });
                session.World.Apply(new JoinedMission { ShipId = 1 });
                Assert.Null(session.World.JoinError); Assert.NotNull(session.World.MissionCore);
            }
            Join(a); Join(same);
            Assert.Same(a.World.MissionCore, same.World.MissionCore);
            int oldVersion = a.World.MissionCore!.Version;
            BitConverter.GetBytes(oldVersion + 1).CopyTo(bytes, 0);
            File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, initialTime.AddSeconds(2));
            Join(edited);
            Assert.NotSame(a.World.MissionCore, edited.World.MissionCore);
            Assert.Equal(oldVersion, a.World.MissionCore.Version);
            Assert.Equal(oldVersion + 1, edited.World.MissionCore!.Version);
        }
        finally { Environment.SetEnvironmentVariable(ExternalPaths.ArtworkEnvVar, previous); Directory.Delete(folder, true); }
    }

    [SkippableFact]
    public void Shared_server_core_is_unchanged_by_simulation_and_test_overrides_are_private()
    {
        Require.Artwork();
        using var a = new Srv(); using var b = new Srv();
        a.Boot(UpdateCadenceTests.Config()); b.Boot(UpdateCadenceTests.Config());
        var first = a.Missions[0]; var second = b.Missions[0];
        Assert.Same(first.Core, second.Core);
        string snapshot = JsonSerializer.Serialize(first.Core);
        float original = second.Core.Constants.FloatConstants[3];
        using (new CoreConstantOverride(first, 3, 123))
        {
            Assert.NotSame(first.Core, second.Core);
            Assert.NotSame(first.Core.Constants.DamageConstants[0], second.Core.Constants.DamageConstants[0]);
            Assert.Equal(original, second.Core.Constants.FloatConstants[3]);
            Assert.Equal(123, first.Core.Constants.FloatConstants[3]);
        }
        first.Stage = second.Stage = MissionStage.Started;
        for (int i = 0; i < 100; i++) { a.TickSim(); b.TickSim(); }
        Assert.Equal(snapshot, JsonSerializer.Serialize(first.Core));
        Assert.Equal(snapshot, JsonSerializer.Serialize(second.Core));
    }

    [SkippableFact]
    public void Rotation_keeps_old_world_until_tick_readers_unwind_then_disposes_it()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(UpdateCadenceTests.Config());
        var old = server.Missions[0]; old.Stage = MissionStage.Started;
        var side = old.Sides[0]; var station = old.GetStations()[0];
        typeof(Srv).GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [old, (short)0, "P869b lifetime"]);
        Assert.NotSame(old, server.Missions[0]);
        Assert.False(old.IsDisposed); Assert.NotEmpty(old.Sides); Assert.Same(old, side.Mission);
        server.TickSim();
        Assert.True(old.IsDisposed); Assert.Empty(old.Sides); Assert.Empty(old.Sectors);
        Assert.Null(side.Mission); Assert.Null(station.Mission);
        Assert.False(server.Missions[0].IsDisposed); Assert.NotEmpty(server.Missions[0].GetStations());
    }
}
