using System.Text.RegularExpressions;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class P664StationEventSoundTests
{
    private static void Feed(ConnectionSession session, IFedMessage message) =>
        ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack([message.Encode()]));

    private static DataStationIGC Station(short side, short type = 42) => new(
        Position: [0, 0, 0], Up: [0, 1, 0], Forward: [0, 0, -1], Rotation: [0, 0, 0, 0],
        ClusterId: 1, SideId: side, StationId: 7, StationTypeId: type,
        BpHull: 240, BpShield: 240, Name: "misleading garrison name");

    private static ConnectionSession Session(short stationSide)
    {
        var session = new ConnectionSession();
        Feed(session, new ExportShip { ShipId = -1, SideId = 0 });
        Feed(session, new ChangeAlliances { Allies = [0, 0, -1, -1, -1, -1] });
        Feed(session, new ExportStation { Station = Station(stationSide) });
        return session;
    }

    // Deliberately mix families on one type. Only the core's selected field
    // determines the clip, never the station name, type ID, or previous owner.
    private static readonly IReadOnlyDictionary<short, DataStationTypeIGC> Types =
        new Dictionary<short, DataStationTypeIGC>
        {
            [42] = default(DataStationTypeIGC) with
            {
                DestroyedSound = 861, EnemyDestroyedSound = 902,
                CapturedSound = 885, EnemyCapturedSound = 926,
            },
        };

    private static void Cue(ConnectionSession session, ClientSoundEventKind kind, string expected)
    {
        var ev = Assert.Single(session.World.TakeSoundEvents());
        Assert.Equal(kind, ev.Kind);
        Assert.Equal((short)7, ev.Id);
        Assert.Equal((short)42, ev.StationTypeId);
        Assert.True(StationEventSoundMap.TryResolve(ev, Types, out var stem));
        Assert.Equal(expected, stem);
        Assert.Empty(session.World.TakeSoundEvents());
        Assert.Empty(session.UnknownFmids);
    }

    [Fact]
    public void SIM_0421_Own_and_allied_destruction_preserve_type_after_removal()
    {
        foreach (short side in new short[] { 0, 1 })
        {
            using var session = Session(side);
            Feed(session, new StationDestroyed { StationId = 7, Launcher = -1 });
            Assert.DoesNotContain((short)7, session.World.Stations.Keys);
            Cue(session, ClientSoundEventKind.StationDestroyed, "vo_destroy_outpost");
            // A duplicate destruction has no subject, hence no second cue.
            Feed(session, new StationDestroyed { StationId = 7, Launcher = -1 });
            Assert.Empty(session.World.TakeSoundEvents());
        }
    }

    [Fact]
    public void SIM_0422_Enemy_destruction_uses_enemy_field_and_current_alliances()
    {
        using var session = Session(1);
        Feed(session, new ChangeAlliances { Allies = [-1, -1, -1, -1, -1, -1] });
        Feed(session, new StationDestroyed { StationId = 7, Launcher = -1 });
        Cue(session, ClientSoundEventKind.StationEnemyDestroyed, "vo_destroy_enemyshipyard");
    }

    [Fact]
    public void SIM_0423_Own_or_allied_loss_uses_old_side_and_precedes_friendly_gain()
    {
        foreach (short oldSide in new short[] { 0, 1 })
        foreach (short newSide in new short[] { 0, 1, 2 })
        {
            using var session = Session(oldSide);
            Feed(session, new StationCapture { StationId = 7, SidOld = oldSide, SidNew = newSide, ShipIdCredit = -1 });
            Assert.Equal(newSide, session.World.Stations[7].SideId);
            // A later export can change the type before Game1 drains the cue.
            Feed(session, new ExportStation { Station = Station(newSide, type: 99) });
            Cue(session, ClientSoundEventKind.StationCaptured, "vo_capture_expansion");
        }
    }

    [Fact]
    public void SIM_0424_Own_or_allied_gain_uses_enemy_captured_field()
    {
        foreach (short newSide in new short[] { 0, 1 })
        {
            using var session = Session(2);
            Feed(session, new StationCapture { StationId = 7, SidOld = 2, SidNew = newSide, ShipIdCredit = -1 });
            Assert.Equal(newSide, session.World.Stations[7].SideId);
            Cue(session, ClientSoundEventKind.StationEnemyCaptured, "vo_capture_enemysupremecy");
        }
    }

    [Fact]
    public void Unrelated_capture_and_missing_subjects_are_silent_but_ownership_updates()
    {
        using var session = Session(2);
        Feed(session, new StationCapture { StationId = 7, SidOld = 2, SidNew = 3 });
        Assert.Equal((short)3, session.World.Stations[7].SideId);
        Feed(session, new StationCapture { StationId = 8, SidOld = 0, SidNew = 2 });
        Feed(session, new StationDestroyed { StationId = 8 });
        Assert.Empty(session.World.TakeSoundEvents());
    }

    [Fact]
    public void Missing_core_type_and_unknown_or_NA_sound_never_fall_back_to_garrison()
    {
        var ev = new ClientSoundEvent(ClientSoundEventKind.StationDestroyed, 7, 42);
        Assert.False(StationEventSoundMap.TryResolve(ev, null, out _));
        Assert.False(StationEventSoundMap.TryResolve(ev with { StationTypeId = 99 }, Types, out _));
        foreach (short id in new short[] { -1, 0, short.MaxValue })
        {
            var types = new Dictionary<short, DataStationTypeIGC>
            {
                [42] = Types[42] with { DestroyedSound = id },
            };
            Assert.False(StationEventSoundMap.TryResolve(ev, types, out _));
        }
        Assert.False(StationEventSoundMap.TryResolve(ev with { Kind = ClientSoundEventKind.DevCompleted }, Types, out _));
    }

    [Fact]
    public void Every_station_event_template_and_alias_matches_shipped_sounddef_and_builds()
    {
        string content = ContentDirectory();
        string mdl = Regex.Replace(File.ReadAllText(Path.Combine(content, "sounddef.mdl")), @"//[^\n]*", "");
        var definitions = Regex.Matches(mdl, @"(\w+)\s*=\s*([^;]+);")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        string Resolve(string name)
        {
            string definition = definitions[name];
            var wave = Regex.Match(definition, "ImportWave\\(\"([^\"]+)\"\\)");
            return wave.Success ? wave.Groups[1].Value : Resolve(definition.Trim());
        }
        var entries = Regex.Matches(mdl, @"\((\d+),\s*(\w+)\s*\)")
            .Where(m => StationEventSoundMap.Stems.ContainsKey(short.Parse(m.Groups[1].Value))).ToList();
        Assert.Equal(48, entries.Count);
        string mgcb = File.ReadAllText(Path.Combine(content, "Content.mgcb"));
        foreach (var entry in entries)
        {
            short id = short.Parse(entry.Groups[1].Value);
            string expected = Resolve(entry.Groups[2].Value);
            Assert.Equal(expected, StationEventSoundMap.Stems[id]);
            Assert.True(File.Exists(Path.Combine(content, "Sounds", expected + ".ogg")), expected);
            Assert.Contains($"/build:Sounds/{expected}.ogg", mgcb);
        }
        // Teleport/refinery capture is errorSound, not an invented VO clip.
        Assert.Equal("ef03", StationEventSoundMap.Stems[883]);
        Assert.Equal("ef03", StationEventSoundMap.Stems[924]);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Every_real_core_station_event_sound_resolves_from_its_decoded_field()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(
            ExternalPaths.Resolve(ExternalResourceKind.Artwork), "PCore001.igc")));
        Assert.NotEmpty(core.StationTypes);
        foreach (var type in core.StationTypes.Values)
        foreach (var kind in new[] { ClientSoundEventKind.StationDestroyed, ClientSoundEventKind.StationEnemyDestroyed,
            ClientSoundEventKind.StationCaptured, ClientSoundEventKind.StationEnemyCaptured })
        {
            var ev = new ClientSoundEvent(kind, 7, type.StationTypeId);
            Assert.True(StationEventSoundMap.TryResolve(ev, core.StationTypes, out _), $"{type.Name}: {kind}");
        }
    }

    private static string ContentDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Allegiance.ClientCore.MonoGame", "Content");
    }
}
