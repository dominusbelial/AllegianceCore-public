using System.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P1100RenderSnapshotTests
{
    private static readonly ServerShipUpdate Pose = ServerShipUpdate.FromValues(1, 0, default, 100,
        SimVec.Zero, Orientation.Identity, SimVec.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1);

    private static void Seat(ClientWorld world, short side, uint seed)
    {
        world.Apply(new SetCluster { SectorId = 4, Ship = Pose });
        world.Apply(new ExportShip { ShipId = 1, SideId = side, HullId = 9, CharacterName = $"Leader {side}" });
        world.Apply(new JoinSide { ShipId = 1, SideId = side });
        world.Apply(new ExportCluster { Cluster = default(DataClusterIGC) with
            { ClusterId = 4, StarSeed = seed, LightDirection = [0, 1, 0] } });
    }

    [Theory]
    [InlineData("eject")] [InlineData("side")] [InlineData("warp")]
    [InlineData("rotate")] [InlineData("resume")]
    public async Task Update_pose_admission_expires_at_receive_boundaries(string transition)
    {
        using var session = new ConnectionSession();
        var world = session.World;
        world.LifepodHullResolver = _ => 77;
        var frame = new FlightRenderSnapshot();
        for (uint iteration = 1; iteration <= 24; iteration++)
        {
            Seat(world, 0, iteration);
            var update = FlightRenderSnapshot.View.Read(world, world.MyShipId, 4);
            lock (session.Gate) frame.CaptureHeader(update);
            await Task.Run(() =>
            {
                lock (session.Gate)
                {
                    switch (transition)
                    {
                        case "eject":
                            world.Apply(new KillShip { ShipId = 1, BDeathCredit = false });
                            world.Apply(Eject.ForSurvivor(1, SimVec.Zero, SimVec.Zero, new(0, 0, -1)));
                            break;
                        case "side": world.Apply(new JoinSide { ShipId = 1, SideId = 1 }); break;
                        case "warp": world.Apply(new SetCluster { SectorId = 4, Ship = Pose }); break;
                        case "rotate": world.BeginNextMission(); Seat(world, 0, 999); break;
                        case "resume": world.ResetPreservingMissionIdentity(); Seat(world, 0, 999); break;
                    }
                }
            });
            var draw = FlightRenderSnapshot.View.Read(world, world.MyShipId, 4);
            Assert.NotEqual(update, draw);
            Assert.Equal(update, frame.Identity);
            Assert.Equal(iteration, frame.Sky!.Value.StarSeed);
            Assert.NotSame(world.Clusters[4].LightDirection, frame.Sky.Value.LightDirection);
            frame.CaptureHeader(draw);
            Assert.Equal(world.Clusters[4].StarSeed, frame.Sky.Value.StarSeed);
        }
    }

    [Fact]
    public void Endgame_result_side_roster_and_leaders_survive_reuse_of_the_same_ids()
    {
        var world = new ClientWorld();
        Seat(world, 0, 1);
        world.Apply(new SetTeamLeader { ShipId = 1, SideId = 0 });
        world.Apply(new RankInfo { Ranks = [new(0, 0, 0, "First rank")] });
        world.Apply(new GameOver { NumSides = 2, SideWinner = 0, GameoverMessage = "First" });
        world.Apply(new GameOverPlayers { Entries = [new("Leader 0", 0, 10, 45)] });
        var frame = new FlightRenderSnapshot();
        frame.CaptureHeader(FlightRenderSnapshot.View.Read(world, 1, 4));
        world.BeginNextMission();
        Seat(world, 1, 2);
        world.Apply(new SetTeamLeader { ShipId = 1, SideId = 1 });
        world.Apply(new RankInfo { Ranks = [new(0, 0, 0, "Second rank")] });
        world.Apply(new GameOver { NumSides = 2, SideWinner = 1, GameoverMessage = "Second" });
        world.Apply(new GameOverPlayers { Entries = [new("Leader 1", 1, 3, 12)] });
        Assert.Equal("First", frame.Result!.GameoverMessage);
        Assert.Equal((short)0, frame.Identity.Side);
        Assert.Equal("Leader 0", Assert.Single(frame.Players).CharacterName);
        Assert.Equal("Leader 0", Assert.Single(frame.Commanders));
        var rowFirst = Assert.Single(frame.BuildTables(default, default).PlayerRows);
        Assert.Equal(("Leader 0", "First rank", true), (rowFirst.Name, rowFirst.RankName, rowFirst.Commander));
        frame.CaptureHeader(FlightRenderSnapshot.View.Read(world, 1, 4));
        Assert.Equal("Second", frame.Result!.GameoverMessage);
        Assert.Equal((short)1, frame.Identity.Side);
        Assert.Equal("Leader 1", Assert.Single(frame.Players).CharacterName);
        Assert.Equal("Leader 1", Assert.Single(frame.Commanders));
        var rowSecond = Assert.Single(frame.BuildTables(default, default).PlayerRows);
        Assert.Equal(("Leader 1", "Second rank", true), (rowSecond.Name, rowSecond.RankName, rowSecond.Commander));
    }

    [SkippableTheory, Trait("Requires", "Gpu")]
    [InlineData("classic")] [InlineData("modern")]
    public async Task Prepared_frame_pixels_HUD_and_commands_survive_receive_rotation(string skin)
    {
        var draws = await Task.WhenAll(Capture(skin, "steady"), Capture(skin, "rotate"));
        Assert.Equal(draws[0], draws[1]);
        Assert.EndsWith("commands=0A006A00B11F020000003BB353D3.0C005D00B11FFFFFFFFF0102C64D28FF.0600B800FFFFCBC984FE", draws[0]);
    }

    [SkippableFact, Trait("Requires", "Gpu")]
    public async Task Receive_transitions_between_pose_gather_and_Draw_discard_geometry_and_HUD_contacts()
        => Assert.Equal("PROOF transitions=15", await Capture("modern", "transitions"));

    private static async Task<string> Capture(string skin, string mode)
    {
        Require.Artwork();
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var repo = output;
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "AllegianceCore.slnx"))) repo = repo.Parent;
        var bin = Path.Combine("bin", output.Parent!.Name, output.Name);
        var root = repo!.FullName;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { Path.Combine(root, "tools/hud2-proof", bin, "hud2-proof.dll"), "--render-snapshot", skin, mode,
                Path.Combine(root, "src/Allegiance.ClientCore.MonoGame", bin) }
        };
        start.Environment["SDL_VIDEODRIVER"] = "offscreen";
        start.Environment["ALSOFT_DRIVERS"] = "null";
        start.Environment["LIBGL_ALWAYS_SOFTWARE"] = "1";
        start.Environment["ALLEGIANCE_BENCHMARK_FRAME"] = "999999";
        var config = Directory.CreateTempSubdirectory("flight-render-");
        start.Environment["XDG_CONFIG_HOME"] = config.FullName;
        try
        {
            return await ReadProof(start);
        }
        finally { config.Delete(recursive: true); }
    }

    private static async Task<string> ReadProof(ProcessStartInfo start)
    {
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        string log = await stdout + await stderr;
        Require.GraphicsDevice(log);
        Assert.True(process.ExitCode == 0, log);
        return Assert.Single(log.Split('\n'), line => line.StartsWith("PROOF "));
    }
}
