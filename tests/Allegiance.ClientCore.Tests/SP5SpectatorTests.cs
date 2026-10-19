using System.Diagnostics;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

public sealed class SP5SpectatorTests
{
    [Fact]
    public void Large_and_epic_default_to_spectating_and_every_size_can_override()
    {
        foreach (var size in Enum.GetValues<SkirmishSize>())
        {
            Assert.Equal(size is SkirmishSize.Large or SkirmishSize.Epic, new SkirmishOptions { Size = size }.StartAsSpectator);
            Assert.True(new SkirmishOptions { Size = size, StartAsSpectator = true }.StartAsSpectator);
            Assert.False(new SkirmishOptions { Size = size, StartAsSpectator = false }.StartAsSpectator);
            var options = new SkirmishOptions { Size = size };
            Assert.True(options.CreateConfig().Transport!.MaxConnectionsPerIp >= options.TeamSize * 2 + 20);
        }
        var menu = new SkirmishScreenModel();
        menu.Move(6); menu.Change(1);   // row 6 since P728 added Commander at row 5
        Assert.True(menu.Options.StartAsSpectator);
        Assert.Contains("Start as spectator: Yes", menu.Rows);
    }

    private static SkirmishObservation Frame(double seconds, params (short Id, short Sector, float Health)[] ships)
    {
        var messages = new List<IFedMessage>();
        foreach (var ship in ships)
        {
            messages.Add(new ExportShip { ShipId = ship.Id, SideId = 0, HullId = 1 });
            messages.Add(new ShipStatusMessage { ShipId = ship.Id, SectorId = ship.Sector, StationId = -1, ParentId = -1 });
            messages.Add(new SingleShipUpdate { OidTarget = ship.Id, Ship = ServerShipUpdate.FromValues(ship.Id,
                0, ControlData.Neutral, (uint)(seconds * 1000), default, Allegiance.Core.Sim.Orientation.Identity, default, [0, 0, 0],
                ship.Health, 0, 1, 1, 1, 0, 0) });
        }
        return new("test", seconds, MissionStage.Started, messages.ToArray(), [], []);
    }

    [Fact]
    public void Camera_tracks_recent_damage_kills_then_cycles_ships_both_directions()
    {
        var camera = new SkirmishCamera();
        camera.Update(Frame(0, (1, 10, 1), (2, 10, 1), (3, 20, 1)));
        Assert.Equal(10, camera.SectorId);
        camera.Update(Frame(6, (1, 10, 1), (2, 10, 1), (3, 20, .5f)));
        Assert.Equal(20, camera.SectorId);
        camera.Update(Frame(12, (1, 10, 1), (3, 20, .5f)) with { RecentKills = new Dictionary<short, double> { [10] = 12 } });
        Assert.Equal(10, camera.SectorId);
        camera.SelectMode(SkirmishCameraMode.Ship);
        camera.Update(Frame(13, (1, 10, 1), (3, 20, .5f)), -1);
        Assert.Equal((short)3, camera.ShipId);
        camera.Update(Frame(14, (1, 10, 1), (3, 20, .5f)), 1);
        Assert.Equal((short)1, camera.ShipId);
        camera.SelectMode(SkirmishCameraMode.Free);
        camera.Update(Frame(15, (3, 20, .5f)));
        Assert.Equal(10, camera.SectorId);
        camera.Update(new("next", 0, MissionStage.Started, [], [], []));
        Assert.Null(SkirmishCamera.Cycle([], null, 1));
    }

    [Fact]
    public void Station_camera_wraps_both_directions_and_survives_an_empty_next_match()
    {
        var frame = Frame(0) with
        {
            Objects = new IFedMessage[] {
                new ExportStation { Station = new DataStationIGC { StationId = 100, ClusterId = 10, Position = [0, 0, 0] } },
                new ExportStation { Station = new DataStationIGC { StationId = 200, ClusterId = 20, Position = [0, 0, 0] } }
            }
        };
        var camera = new SkirmishCamera();
        camera.SelectMode(SkirmishCameraMode.Station);
        camera.Update(frame);
        Assert.Equal((short)100, camera.StationId);
        Assert.Null(camera.ShipId);
        camera.Update(frame, -1);
        Assert.Equal((short)200, camera.StationId);
        Assert.Equal(20, camera.SectorId);
        camera.Update(frame, 1);
        Assert.Equal((short)100, camera.StationId);
        camera.Update(new("next", 0, MissionStage.NotStarted, [], [], []));
        Assert.Null(camera.ShipId); Assert.Null(camera.StationId);
    }

    [Fact]
    public void Observation_updates_without_an_own_ship_and_removes_departed_entities()
    {
        var world = new ClientWorld();
        world.ApplyLocalObservation(Frame(1, (1, 10, 1), (2, 20, 1)).Objects, []);
        Assert.Null(world.MyShipId); Assert.Null(world.MySideId);
        Assert.Equal(2, world.Ships.Count);
        Assert.All(world.Ships.Values, ship => Assert.False(ship.IsMine));
        world.ApplyLocalObservation(Frame(2, (2, 20, .5f)).Objects, []);
        Assert.Single(world.Ships);
        Assert.Equal(.5f, world.Ships[2].Latest!.HullFraction, .01f);
        Assert.True(world.Ships[2].CanDrawInSector(20));
    }
}

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]   // whole local matches (38-215 s); TT-1 tiering after the P727/SP-5 merge
public sealed class SP5SpectatorLiveTests
{
    private static async Task Until(Func<bool> condition, int seconds = 60)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed.TotalSeconds < seconds) await Task.Delay(50);
        Assert.True(condition(), "Timed out waiting for local spectator/seat transition");
    }

    private static ConnectionSession Connect(SkirmishHost host, short side)
    {
        var session = new ConnectionSession();
        session.Connect(host.Port, "sp5-pilot");
        session.SendLogonRequest(new() { CharacterName = "sp5-pilot", FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", Time = 1,
            DwCookie = 5, CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = 50501 });
        session.WaitForAck(10000);
        session.SendPositionReq(side);
        return session;
    }

    [SkippableFact]
    public async Task Shipless_updates_swap_both_sides_launch_unseat_and_keep_choice_across_next_match()
    {
        Require.Artwork();
        using var host = await SkirmishHost.StartAsync(new() { StartAsSpectator = true, TechMode = "unlocked" });
        await Until(() => host.Stats.ValueKind == System.Text.Json.JsonValueKind.Object && host.Stats.GetProperty("botCount").GetInt32() == 10);
        var world = new ClientWorld();
        await Until(() => host.Observe(-1).Objects.OfType<SingleShipUpdate>().Any());
        var frame = host.Observe(-1);
        world.ApplyLocalObservation(frame.Objects, frame.Projectiles);
        Assert.Null(world.MyShipId); Assert.Null(world.MySideId);
        Assert.NotEmpty(world.Ships);
        double time = host.SimulationSeconds;
        await Until(() => host.SimulationSeconds > time + .25);
        uint firstTick = world.Ships.Values.Max(s => s.LastUpdateTick);
        var refreshed = host.Observe(-1);
        world.ApplyLocalObservation(refreshed.Objects, refreshed.Projectiles, refreshed.Missiles);
        Assert.Null(world.MyShipId);
        Assert.Contains(world.Ships.Values, s => s.LastUpdateTick > firstTick);
        int[] population = host.OnSimulation(s => new short[] { 0, 1 }.Select(side => s.Ships.Count(live => live.Ship.SideId == side)).ToArray());
        foreach (short side in new short[] { 0, 1 })
        {
            if (side == 0) await host.SetPausedAsync(true);
            await host.SetPlayerSideAsync(side);
            Assert.False(host.Paused);
            Assert.Equal(side, host.PlayerSide);
            using (var pilot = Connect(host, side))
            {
                await Until(() => { lock (pilot.Gate) return pilot.World.MyShipId is not null && pilot.World.MySideId == side; });
                await Until(() => host.Stats.GetProperty("botCount").GetInt32() == 9);
                await Until(() => host.OnSimulation(s => new short[] { 0, 1 }.Select(team => s.Ships.Count(live => live.Ship.SideId == team)).SequenceEqual(population)));
                short id;
                lock (pilot.Gate)
                {
                    id = pilot.World.MyShipId!.Value;
                    Assert.True(pilot.World.Ships[id].StationId >= 0); // normal hangar, no bypass
                    pilot.SendPlayerReady(true, id);
                    var ship = pilot.World.Ships[id];
                    pilot.SendBuyLoadout(ship.HullId, true, ship.Loadout);
                }
                await Until(() => { lock (pilot.Gate) return pilot.World.Ships[id].StationId < 0; });
                if (side == 1)
                {
                    var old = host.OnSimulation(s => s.Missions[0]);
                    host.OnSimulation(s => { P660NextMatchLiveTests.End(s); return true; });
                    await Until(() => host.OnSimulation(s => !ReferenceEquals(old, s.Missions[0]) && s.Missions[0].Stage == MissionStage.Started), 90);
                    Assert.Equal(side, host.PlayerSide);
                    await Until(() => { lock (pilot.Gate) return pilot.World.MySideId == side && pilot.World.MyShipId is not null; });
                }
            }
            await host.SetPlayerSideAsync(null);
            Assert.Null(host.PlayerSide);
            await Until(() => host.Stats.GetProperty("botCount").GetInt32() == 10);
            Assert.True(host.BotsAlive);
            await Until(() => host.OnSimulation(s => new short[] { 0, 1 }.Select(team => s.Ships.Count(live => live.Ship.SideId == team)).SequenceEqual(population)));
        }
        // A seated choice can move directly to the other side: restore Alpha's
        // vacancy before reserving Bravo's, then return both teams to full bots.
        await host.SetPlayerSideAsync(0);
        await host.SetPlayerSideAsync(1);
        Assert.Equal((short)1, host.PlayerSide);
        await Until(() => host.Stats.GetProperty("botCount").GetInt32() == 9);
        await host.SetPlayerSideAsync(null);
        await Until(() => host.Stats.GetProperty("botCount").GetInt32() == 10);
        var previous = host.Observe(-1).Epoch;
        host.OnSimulation(s => { P660NextMatchLiveTests.End(s); return true; });
        await Until(() => host.Observe(-1).Epoch != previous);
        Assert.Null(host.PlayerSide);
        world.Reset(); frame = host.Observe(-1); world.ApplyLocalObservation(frame.Objects, frame.Projectiles);
        Assert.Null(world.MyShipId);
    }
}
