using System.Buffers.Binary;
using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;
using SimModel = Allegiance.Core.Sim.Model;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
[Trait("Requires", "Artwork")]
public sealed class P904CertificationTests
{
    private static ServerConfig Config() => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
        LiveSettings = new() { AutoNextMatch = new() { Enabled = false } },
        Games = [new() { Name = "P904", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static object? Call(Srv server, string method, params object[] args) =>
        typeof(Srv).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, args);

    private static Ship Place(Mission mission, short id, short side)
    {
        var ship = new Ship(id, mission.Core.HullTypes.Values.First(), new(50000 + id, 0, 0),
            Vector3.Zero, Orientation.Identity) { SideId = side };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return ship;
    }

    // Igc/missionigc.cpp:3386-3416; FedSrv/FedSrv.CPP:3211.
    [SkippableFact]
    public void Generic_model_lookup_and_chat_orders_use_live_registries_for_every_native_kind()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        var mission = server.Missions[0]; var sector = mission.Sectors[0];
        const short id = 30001; // overlapping IDs must be resolved by type
        var ship = Place(mission, id, 0);
        sector.Remove(ship); ship.Sector = null; // a command may name an unclustered ship
        var station = new Station(mission.GetStations()[0].Data with { StationId = id }, mission.Core.StationTypes.Values.First());
        var warp = new ResolvedWarp(mission.GetWarps()[0].Warp with { WarpId = id }, sector);
        var asteroid = new Asteroid(mission.GetAsteroids()[0].Data with { AsteroidId = id });
        var treasure = new Treasure(id, sector, DataTreasureIGC.Decode(new byte[DataTreasureIGC.Size]), 0);
        var probe = new Probe(id, sector, mission.Core.ProbeTypes.Values.First(), Vector3.Zero, 0, 0);
        var mine = new Mine(id, sector, mission.Core.MineTypes.Values.First(), Vector3.Zero, 0);
        var buoy = new Buoy(new([0, 0, 0], sector.Id, 0, id, false));
        mission.AddStation(station); mission.AddWarp(warp); mission.AddAsteroid(asteroid);
        mission.AddTreasure(treasure); mission.AddProbe(probe); mission.AddMine(mine); mission.AddBuoy(buoy);
        (IgcObjectType Type, IModelIgc Model, Action Remove)[] models =
        [
            (IgcObjectType.Ship, ship, () => mission.DeleteShip(ship)),
            (IgcObjectType.Station, station, () => mission.DeleteStation(station)),
            (IgcObjectType.Warp, warp, () => mission.DeleteWarp(warp)),
            (IgcObjectType.Asteroid, asteroid, () => mission.DeleteAsteroid(asteroid)),
            (IgcObjectType.Treasure, treasure, () => mission.DeleteTreasure(treasure)),
            (IgcObjectType.Probe, probe, () => mission.DeleteProbe(probe)),
            (IgcObjectType.Mine, mine, () => mission.DeleteMine(mine)),
            (IgcObjectType.Buoy, buoy, () => mission.DeleteBuoy(buoy)),
        ];
        foreach (var (type, model, remove) in models)
        {
            Assert.Same(model, mission.GetModel(type, id));
            var resolved = ((bool Found, SimModel? Model, IgcObjectType Type, short Id))Call(server,
                "ResolveChatOrderTarget", mission, type, id)!;
            Assert.True(resolved.Found); Assert.Equal((type, id), (resolved.Type, resolved.Id));
            Assert.Same(model as SimModel, resolved.Model);
            remove();
            Assert.Null(mission.GetModel(type, id));
            var removed = ((bool Found, SimModel? Model, IgcObjectType Type, short Id))Call(server,
                "ResolveChatOrderTarget", mission, type, id)!;
            Assert.False(removed.Found);
        }
        foreach (var type in Enum.GetValues<IgcObjectType>()) Assert.Null(mission.GetModel(type, short.MaxValue));
        foreach (var type in new short[] { -1, 2, 6, 10, 12, 13, 34, short.MaxValue })
            Assert.Null(mission.GetModel((IgcObjectType)type, id)); // missile (2) is NOT in native GetModel

        // This record remains in the map after live station removal. The old
        // production chat resolver incorrectly accepted it as a valid target.
        var original = mission.GetStations()[0]; mission.DeleteStation(original);
        Assert.Contains(sector.Stations.Concat(mission.Sectors.SelectMany(s => s.Stations)), s => s.StationId == original.StationId);
        var deleted = ((bool Found, SimModel? Model, IgcObjectType Type, short Id))Call(server,
            "ResolveChatOrderTarget", mission, IgcObjectType.Station, original.StationId)!;
        Assert.False(deleted.Found);
    }

    // FedSrv/fsmission.cpp:2282-2326,2514-2552: the wrapper visits EVERY side,
    // distributes integer shares, preserves the remainder and redirects donations.
    [SkippableFact]
    public void Periodic_payday_visits_all_sides_preserves_empty_treasury_and_credits_donations()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config()); var mission = server.Missions[0];
        mission.AddSide(new Side(2, "Empty", mission.Core.Civilizations.Values.First()));
        using var foreign = Mission.Assemble(mission.Core, mission.Map,
            [new(0, "Foreign", 1), new(1, "Other", 3)], new NullIgcSite());
        var ships = new[] { Place(mission, 30000, 0), Place(mission, 30001, 0), Place(mission, 30002, 0),
            Place(mission, 30003, 1), Place(mission, 30004, 1), Place(foreign, 30005, 0) };
        var peers = ships.Select((ship, i) => (new ConnectionId((uint)(i + 1)), ship, ship.Sector!)).ToArray();
        server.TestSeedPeers(peers);
        var live = server.Ships.ToDictionary(s => s.Ship.Id);
        live[ships[0].Id].Wallet.AutoDonateTarget = ships[1];
        live[ships[5].Id].Wallet.Money = 77;
        using var interval = new CoreConstantOverride(mission, 6, 10);
        using var income = new CoreConstantOverride(mission, 30, 101);
        var totals = new long[3]; int[] counts = [3, 2, 0];
        for (int i = 0; i < 3; i++)
        {
            var side = mission.Sides[i]; side.Money = 7 + i;
            totals[i] = side.Money + (long)(101 * (1 + side.Civilization.IncomeMoney))
                + mission.GetStations().Where(s => s.SideId == side.Id).Sum(s => (long)s.Type.Income);
        }
        var sent = new List<(ConnectionId Peer, byte[] Message)>();
        server._testSendCapture = (peer, bytes) => sent.AddRange(FedPacket.Unpack(bytes).Select(m => (peer, m)));
        mission.Stage = MissionStage.Started; mission.NextPaydayTime = 1;
        mission.TreasureParameters.PlayerRegenerateSet = mission.TreasureParameters.NeutralRegenerateSet = -1;
        mission.Update(1);
        var paydays = sent.Where(s => FedMessageHeader.Read(s.Message).Fmid == Payday.Fmid).ToArray();
        Assert.Equal(5, paydays.Length);
        for (int i = 0; i < 5; i++)
        {
            int side = ships[i].SideId!.Value;
            Assert.Equal((int)(totals[side] / counts[side]), Payday.Decode(Assert.Single(paydays, p => p.Peer == peers[i].Item1).Message).DMoney);
        }
        Assert.Equal(0, live[ships[0].Id].Wallet.Money);
        Assert.Equal(2 * (totals[0] / 3), live[ships[1].Id].Wallet.Money); // two integer shares
        Assert.Equal(totals[0] / 3, live[ships[2].Id].Wallet.Money);
        Assert.Equal(77, live[ships[5].Id].Wallet.Money);
        Assert.Equal(totals[0] % 3, mission.Sides[0].Money);
        Assert.Equal(totals[1] % 2, mission.Sides[1].Money);
        Assert.Equal(totals[2], mission.Sides[2].Money);
        Assert.Equal(11, mission.NextPaydayTime);
        mission.Update(1.001); Assert.Equal(5, sent.Count(s => FedMessageHeader.Read(s.Message).Fmid == Payday.Fmid));
    }

    private sealed class DuringMovement(short id, Action action) : SimModel(id, Vector3.Zero)
    {
        protected override void OnUpdate(double now, double dt) => action();
    }

    // FedSrv/FedSrv.CPP:8245-8278: MoveShips precedes DoTick. Income must
    // reflect a station type change (or capture/destruction) in this movement pass.
    [SkippableFact]
    public void Payday_reads_station_income_after_the_same_ticks_movement()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config()); var mission = server.Missions[0];
        mission.Stage = MissionStage.Started; mission.NextPaydayTime = 1;
        var station = mission.GetStations().First(s => s.SideId == 0);
        long initialIncome = mission.GetStations().Where(s => s.SideId == 0).Sum(s => (long)s.Type.Income);
        var changed = station.Type with { Income = station.Type.Income + 123 };
        mission.Sectors[0].Add(new DuringMovement(30000, () => station.SetBaseStationType(changed)));
        mission.Update(1);
        long baseline = (long)(mission.IncomePerPayday * (1 + mission.Sides[0].Civilization.IncomeMoney));
        Assert.Equal(initialIncome + baseline + 123, mission.Sides[0].Money);
        Assert.Equal(0, server.SimPassStageFailures);
    }

    // FedSrv/fsmission.cpp:2514-2581: late ticks advance ONE payday phase,
    // treasure dt includes the lateness, and development-off still advances
    // phase/treasures without accumulating income to pay back later.
    [SkippableTheory]
    [InlineData(MissionStage.NotStarted, true)]
    [InlineData(MissionStage.Starting, true)]
    [InlineData(MissionStage.Over, true)]
    [InlineData(MissionStage.Started, true)]
    [InlineData(MissionStage.Started, false)]
    public void Economic_tick_preserves_stage_development_late_phase_and_treasure_guards(MissionStage stage, bool developments)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config()); var mission = server.Missions[0];
        using var interval = new CoreConstantOverride(mission, 6, 10);
        mission.Stage = stage; mission.AllowDevelopments = developments; mission.NextPaydayTime = 1;
        mission.TreasureParameters.PlayerRegenerateSet = mission.TreasureParameters.NeutralRegenerateSet = -1;
        mission.TreasureParameters.PlayerSectorTreasureRate = .01f;
        mission.TreasureParameters.NeutralSectorTreasureRate = .015f;
        mission.Update(.5); Assert.Equal(1, mission.NextPaydayTime);
        Assert.All(mission.Sides, side => Assert.Equal(0, side.Money));
        mission.Update(2);
        bool started = stage == MissionStage.Started;
        Assert.Equal(started ? 11 : 1, mission.NextPaydayTime);
        foreach (var sector in mission.Sectors)
            Assert.Equal(started ? 11 * (sector.Cluster.HomeSector ? .01f : .015f) : 0, sector.PendingTreasures, 6);
        Assert.All(mission.Sides, side => Assert.Equal(started && developments, side.Money > 0));
        if (!started) return;
        long[] balances = mission.Sides.Select(s => s.Money).ToArray();
        mission.Update(25);
        Assert.Equal(21, mission.NextPaydayTime); // not a while-loop catch-up
        if (!developments)
        {
            Assert.Equal(balances, mission.Sides.Select(s => s.Money));
            mission.AllowDevelopments = true;
        }
        mission.Update(25.001); Assert.Equal(31, mission.NextPaydayTime);
        Assert.All(mission.Sides, side => Assert.True(side.Money > balances[side.Id]));
    }

    // FedSrv/fsmission.cpp:2508-2512 returns before the ballot tail at 2584.
    // FedSrv/FedSrv.CPP:8247-8278 calls DoTick only in Started.
    [SkippableTheory]
    [InlineData(MissionStage.NotStarted, false)]
    [InlineData(MissionStage.Starting, false)]
    [InlineData(MissionStage.Over, false)]
    [InlineData(MissionStage.Started, false)]
    [InlineData(MissionStage.Started, true)]
    public void Sim_tick_gates_ballots_by_stage_and_timeout_wins_over_a_passing_draw(MissionStage stage, bool expired)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config()); var mission = server.Missions[0];
        var ship = Place(mission, 30000, 0);
        server.TestSeedPeers([(new(1), ship, ship.Sector!)]);
        mission.Stage = stage; mission.CountdownDuration = expired ? .01 : 100;
        mission.Sides[0].Kills = 10;
        var ballot = new PendingBallot { Id = 1, Type = BallotType.AcceptDraw, Expiration = 100 };
        ballot.InFavor[0] = 1; // all votes in; would immediately replace the winner with a draw
        var ballots = (Dictionary<Mission, List<PendingBallot>>)typeof(Srv)
            .GetField("_ballots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        ballots[mission] = [ballot];
        var results = new List<GameOver>();
        server._testSendCapture = (_, packet) =>
        {
            foreach (var message in FedPacket.Unpack(packet))
                if (FedMessageHeader.Read(message).Fmid == GameOver.Fmid) results.Add(GameOver.Decode(message));
        };
        server.TickSim(); server.FlushBroadcastSnapshots();
        if (stage == MissionStage.Started)
        {
            var result = Assert.Single(results);
            Assert.Equal(expired ? (short)0 : GameOver.NA, result.SideWinner);
            Assert.Equal(MissionStage.Over, mission.Stage);
        }
        else
        {
            Assert.Empty(results);
            Assert.Contains(ballot, ballots[mission]);
            Assert.Equal(stage, mission.Stage);
        }
        Assert.Equal(0, server.SimPassStageFailures);
    }

    // Managed equivalent of Igc/missionigc.cpp:3016-3053: immutable data may
    // remain referenced, but the last consumer must relinquish cache ownership.
    [SkippableFact]
    public void Client_revision_change_retires_each_cached_core_at_its_last_session_consumer()
    {
        Require.Artwork();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AllegianceCore.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string folder = Path.Combine(root.FullName, ".scratch", "p904", "core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string? previous = Environment.GetEnvironmentVariable(ExternalPaths.ArtworkEnvVar);
        try
        {
            string path = Path.Combine(folder, "revision.igc");
            byte[] bytes = File.ReadAllBytes(Path.Combine(TestArtRoot.Value, "PCore001.igc"));
            File.WriteAllBytes(path, bytes); var stamp = File.GetLastWriteTimeUtc(path);
            Environment.SetEnvironmentVariable(ExternalPaths.ArtworkEnvVar, folder);
            using var first = new ConnectionSession(); using var same = new ConnectionSession(); using var edited = new ConnectionSession();
            void Join(ConnectionSession session)
            {
                session.World.Apply(new MissionDef { SzIGCStaticFile = "revision" });
                session.World.Apply(new JoinedMission { ShipId = 1 });
                Assert.Null(session.World.JoinError);
            }
            Join(first); Join(same);
            var old = first.World.MissionCore!; Assert.Same(old, same.World.MissionCore);
            int version = old.Version;
            BinaryPrimitives.WriteInt32LittleEndian(bytes, version + 1);
            File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, stamp.AddSeconds(2));
            Join(edited); var current = edited.World.MissionCore!;
            Assert.NotSame(old, current); Assert.Equal(version + 1, current.Version);
            first.World.Reset();
            using (var retained = StaticCoreCache.Shared.Retain(old)) Assert.NotNull(retained);
            same.Dispose(); same.Dispose();
            Assert.Null(StaticCoreCache.Shared.Retain(old));
            Assert.Equal(version, old.Version); Assert.NotEmpty(old.HullTypes); // retained managed data is safe
            using (var retained = StaticCoreCache.Shared.Retain(current)) Assert.NotNull(retained);
            edited.Dispose();
            Assert.Null(StaticCoreCache.Shared.Retain(current));
            Join(first); Assert.NotSame(current, first.World.MissionCore); // reload after last release
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExternalPaths.ArtworkEnvVar, previous);
            Directory.Delete(folder, true);
        }
    }
}
