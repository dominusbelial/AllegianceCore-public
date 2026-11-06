using Allegiance.Core.Sim;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P935TechOverrideTests
{
    private static ServerConfig Config() => new()
    {
        Port = 0,
        Transport = new() { Mode = "pool" },
        Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true, ShipUpdateModel = "native" },
        Sim = new() { NativeGotoPlan = true },
        LiveSettings = new() { AutoNextMatch = new() { Enabled = false } },
        Games = [new() { Name = "P935", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3],
            SideNames = ["Alpha", "Bravo"], MinimumPlayersPerTeam = 0, StartCountdownSeconds = 0 }],
    };
    private static bool Bit(TechTreeBitMask mask, int bit) => (mask.AsSpan()[bit / 8] & (0x80 >> (bit % 8))) != 0;
    private static void Command(Srv server, string command)
    {
        Assert.True(server.RequestConsoleCommand(command));
        server.TickSim();
    }
    private static void Pump(Srv server, Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 8000) { server.TickSim(); Thread.Sleep(2); }
        Assert.True(condition(), "production console/session transition timed out");
    }
    private static ConnectionSession ReadyPlayer(Srv server)
    {
        var peer = new ConnectionSession();
        try
        {
            peer.Connect(server.BoundPort, "P935-admin-test");
            peer.SendLogonRequest(new() { CharacterName = "P935-admin-test", FedsrvVer = LogonRequest.MsgVer,
                CrcFileList = -1, DwCookie = server.Missions[0].Cookie });
            Pump(server, () => peer.Ack is not null);
            peer.SendPositionReq(0);
            Pump(server, () => { lock (peer.Gate) return peer.World.MySideId == 0; });
            return peer;
        }
        catch { peer.Dispose(); throw; }
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Console_set_clear_and_last_setting_win_at_real_ready_up_then_refuse_after_start()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        var mission = server.Missions[0];
        var original = mission.Sides.Select(s => s.DevelopmentTechs).ToArray();
        Assert.False(mission.TechOverrides.IsAllocated);
        int clear = Enumerable.Range(0, 400).First(bit => Bit(original[0], bit));
        int set = Enumerable.Range(0, 400).First(bit => !Bit(original[0], bit));
        Command(server, "override-tech 0 -1 2 on");
        Command(server, "override-tech 0 6 2 on");
        Command(server, "override-tech 0 0 400 on");
        Assert.False(mission.TechOverrides.IsAllocated);
        Command(server, $"override-tech 0 0 {clear} off");
        Command(server, $"override-tech 0 0 {set} off");
        Command(server, $"override-tech 0 0 {set} on");
        Command(server, "override-tech 0 5 399 on"); // native valid slot even in a two-team game
        Assert.True(mission.TechOverrides.IsAllocated);
        Assert.Equal(original[0], mission.Sides[0].DevelopmentTechs); // queued until generation
        using var peer = ReadyPlayer(server);
        peer.SendPlayerReady(true, peer.Ack!.ShipID);
        Pump(server, () => mission.Stage == MissionStage.Started);
        Assert.False(Bit(mission.Sides[0].DevelopmentTechs, clear));
        Assert.True(Bit(mission.Sides[0].DevelopmentTechs, set));
        Assert.Equal(mission.Sides[0].DevelopmentTechs, mission.Sides[0].InitialTechs);
        Assert.Equal(original[1], mission.Sides[1].DevelopmentTechs);
        Pump(server, () => { lock (peer.Gate) return peer.World.DevelopmentTechsBySideId.TryGetValue(0, out var mask)
            && !Bit(mask, clear) && Bit(mask, set); });
        var started = mission.Sides[0].DevelopmentTechs;
        Command(server, $"override-tech 0 0 {set} off");
        Assert.Equal(started, mission.TechOverrides.Apply(0, original[0]));
        Assert.Equal(started, mission.Sides[0].DevelopmentTechs);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void No_overrides_preserves_generation_techs_and_bucket_identity()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        var mission = server.Missions[0];
        var techs = mission.Sides.Select(s => s.DevelopmentTechs).ToArray();
        var buckets = mission.Sides.Select(s => s.Buckets.ToArray()).ToArray();
        using var peer = ReadyPlayer(server);
        peer.SendPlayerReady(true, peer.Ack!.ShipID);
        Pump(server, () => mission.Stage == MissionStage.Started);
        for (int side = 0; side < mission.Sides.Count; side++)
        {
            Assert.Equal(techs[side], mission.Sides[side].DevelopmentTechs);
            Assert.Equal(buckets[side], mission.Sides[side].Buckets);
        }
        Assert.False(mission.TechOverrides.IsAllocated);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Admin_game_parameters_copy_masks_into_generation_and_retain_console_edits_across_rotation()
    {
        Require.Artwork();
        // Deserialize with the production source-generated context, also exercised by AOT.
        var config = Config();
        config.Games[0].TechBitOverrides = [new() { Side = 0, Bit = 399, Setting = true },
            new() { Side = 1, Bit = 1, Setting = false }];
        string json = JsonSerializer.Serialize(config, ServerJsonContext.Default.ServerConfig);
        config = JsonSerializer.Deserialize(json, ServerJsonContext.Default.ServerConfig)!;
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(config);
        var old = server.Missions[0];
        Assert.True(Bit(old.Sides[0].DevelopmentTechs, 399));
        Assert.False(Bit(old.Sides[1].DevelopmentTechs, 1));
        Command(server, "override-tech 0 0 399 off");
        Command(server, "override-tech 0 1 398 on");
        // The rotation trigger is isolated; all mask edits use the real queued console.
        typeof(Srv).GetMethod("RotateMissionAfterGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [old]);
        var fresh = server.Missions[0];
        Assert.NotSame(old, fresh);
        Assert.False(Bit(fresh.Sides[0].DevelopmentTechs, 399));
        Assert.True(Bit(fresh.Sides[1].DevelopmentTechs, 398));
        Assert.True(Bit(old.Sides[0].DevelopmentTechs, 399)); // no shared mutable masks
        Command(server, "override-tech 0 1 398 off");
        Assert.True(Bit(old.TechOverrides.Apply(1, TechTreeBitMask.Empty), 398));
    }

    [Fact]
    public void Overrides_precede_initial_techs_buckets_and_authored_station_compatibility()
    {
        var records = new List<IgcRecord>(); var bytes = new List<byte>();
        void Add(IgcObjectType kind, byte[] data)
        { records.Add(new(kind, data.Length, bytes.Count)); bytes.AddRange(data); }
        var constants = new byte[DataConstantsIGC.Size];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(constants.AsSpan(0), .2f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(constants.AsSpan(4), 10000);
        Add(IgcObjectType.Constants, constants);
        Add(IgcObjectType.Civilization, new byte[DataCivilizationIGC.Size]);
        var core = StaticCore.Load(new IgcFile { Wrapper = IgcWrapper.VersionAndDataSize, Version = 1,
            Records = records, Body = bytes.ToArray(), BodyLength = bytes.Count, BytesConsumed = bytes.Count });
        byte[] Bits(int bit) { var bits = new byte[50]; bits[bit / 8] = (byte)(0x80 >> (bit % 8)); return bits; }
        var civs = (Dictionary<short, DataCivilizationIGC>)core.Civilizations;
        civs[0] = civs[0] with { TtbmBaseTechs = Bits(8), GasBaseAttributes = Enumerable.Repeat(1f, 25).ToArray() };
        var stations = (Dictionary<short, DataStationTypeIGC>)core.StationTypes;
        stations[0] = DataStationTypeIGC.Decode(new byte[DataStationTypeIGC.Size]);
        stations[1] = stations[0] with { StationTypeId = 1, TtbmRequired = Bits(399) };
        ((Dictionary<short, DataHullTypeIGC>)core.HullTypes)[504] =
            DataHullTypeIGC.Decode(new byte[DataHullTypeIGC.Size]) with { HullId = 504 };
        var devs = (Dictionary<short, DataDevelopmentIGC>)core.Developments;
        var development = DataDevelopmentIGC.Decode(new byte[DataDevelopmentIGC.Size]) with { Gas = Enumerable.Repeat(1f, 25).ToArray() };
        devs[2] = development with { DevelopmentId = 2, TtbmEffects = Bits(399) };
        devs[3] = development with { DevelopmentId = 3, TtbmEffects = Bits(8) };
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 1 };
        var station = new DataStationIGC([0, 0, 0], [0, 1, 0], [1, 0, 0], [0, 0, 1, 0], 1, 0, 7, 1, 153, 76, "Home");
        using var mission = Mission.Assemble(core, MissionMap.FromLists([cluster], [], [station], []),
            [new SideSpec(0, "A", 0)], new NullIgcSite());
        mission.ReplaceUnavailableStationTypes();
        Assert.Equal(0, mission.GetStation(7)!.Type.StationTypeId);
        Assert.Contains(mission.Sides[0].Buckets, b => b.Development?.DevelopmentId == 2);
        Assert.DoesNotContain(mission.Sides[0].Buckets, b => b.Development?.DevelopmentId == 3);
        Assert.True(mission.OverrideTechBit(0, 399, true));
        Assert.True(mission.OverrideTechBit(0, 8, false));
        mission.ApplyStartingTechOverrides();
        Assert.Equal(1, mission.GetStation(7)!.Type.StationTypeId);
        Assert.True(Bit(mission.Sides[0].InitialTechs, 399)); Assert.False(Bit(mission.Sides[0].InitialTechs, 8));
        Assert.DoesNotContain(mission.Sides[0].Buckets, b => b.Development?.DevelopmentId == 2);
        Assert.Contains(mission.Sides[0].Buckets, b => b.Development?.DevelopmentId == 3);
    }

    [Fact]
    public void Altered_and_setting_masks_preserve_other_bits_and_support_every_bit_boundary()
    {
        var masks = new TechBitOverrides();
        Assert.False(masks.IsAllocated);
        Assert.Equal(TechTreeBitMask.Empty, masks.Apply(0, TechTreeBitMask.Empty));
        foreach (int bit in new[] { 0, 7, 8, 399 }) masks.Set(0, bit, true);
        masks.Set(0, 7, false);
        var result = masks.Apply(0, TechTreeBitMask.Empty);
        Assert.True(Bit(result, 0)); Assert.False(Bit(result, 7)); Assert.True(Bit(result, 8)); Assert.True(Bit(result, 399));
        Assert.Equal(TechTreeBitMask.Empty, masks.Apply(1, TechTreeBitMask.Empty));
        var copy = masks.Copy(); copy.Set(0, 0, false);
        Assert.True(Bit(masks.Apply(0, TechTreeBitMask.Empty), 0));
        Assert.False(Bit(copy.Apply(0, TechTreeBitMask.Empty), 0));
    }
}
