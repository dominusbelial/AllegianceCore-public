using System.Diagnostics;
using System.Text;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Model;
using Allegiance.Core.StaticCoreText;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
[Trait("Requires", "Artwork")]
public sealed class P908TextCoreTests
{
    private sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(StaticCoreRoundtrip.ScratchRoot(), "p908", Guid.NewGuid().ToString("N"));
        public string Binary => Path.Combine(Root, "text.igc");
        public string Text => Path.Combine(Root, "text");
        public Folder()
        {
            Require.Artwork();
            Directory.CreateDirectory(Root);
            // Exercise the actual authoring command, not hand-written fixture CSVs.
            Assert.Equal(0, Allegiance.Tools.Core2Txt.Program.Main(["Core2Text", Source, Text]));
            File.Copy(Path.Combine(TestArtRoot.Value, "XXV_simple.igc"), Path.Combine(Root, "XXV_simple.igc"));
        }
        public static string Source => Path.Combine(TestArtRoot.Value, "PCore001.igc");
        public ServerConfig Config() => new()
        {
            ArtPath = Root, StaticCore = "text.igc", Port = 0,
            Broadcast = new() { ParallelBuild = true }, Transport = new() { Mode = "pool" },
            Games = [new() { Name = "P908 text core", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        };
        public void Dispose() => Directory.Delete(Root, true);
    }

    private static object Semantics(StaticCore c) => new
    {
        c.Constants, c.ProjectileTypes, c.MissileTypes, c.MineTypes, c.ProbeTypes, c.ChaffTypes,
        c.HullTypes, c.PartTypes, c.Launchers, c.StationTypes, c.Developments, c.DroneTypes,
        c.Civilizations, c.TreasureSets,
    };

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Text_fallback_has_binary_semantics_version_1337_and_native_cache_lifetime(bool extension)
    {
        using var folder = new Folder();
        var cache = new StaticCoreCache();
        string binary = extension ? folder.Binary : folder.Text;
        // Version-only checks do not parse malformed text or acquire consumers.
        Assert.Equal(1337, cache.ReadVersion("text", binary));
        using var first = cache.Load("text", binary);
        var core = first.Core;
        Assert.Equal(1337, core.Version);
        Assert.Equivalent(Semantics(StaticCore.Load(IgcReader.ParseFile(Folder.Source))), Semantics(core), strict: true);
        Directory.Move(folder.Text, folder.Text + "-away");
        Assert.Equal(1337, cache.ReadVersion("TEXT", folder.Binary));
        using var second = cache.Load("TEXT", folder.Binary);
        Assert.Same(core, second.Core);
        first.Dispose();
        using (var retained = cache.Retain(core)) Assert.NotNull(retained);
        second.Dispose();
        Assert.Null(cache.Retain(core));
        Assert.Equal(-1, cache.ReadVersion("text", folder.Binary));
        Assert.Throws<FileNotFoundException>(() => cache.Load("text", folder.Binary));
    }

    [SkippableFact]
    public void Sidecar_free_text_loads_named_tech_part_and_ability_masks_through_production()
    {
        using var folder = new Folder();
        File.Delete(Path.Combine(folder.Text, StaticCoreRoundtrip.MetaFileName));
        File.WriteAllText(Path.Combine(folder.Text, "BitsOfTechs.csv"), "BITID\tSHORT NAME\n0\tAlphaTech\n7\tBetaTech\n");
        File.WriteAllText(Path.Combine(folder.Text, "BitsOfParts.csv"),
            $"EQUIPMENT\tSHORT NAME\n{CoreTable.ETWeapon}\tOne\n{CoreTable.ETWeapon}\tTwo\n{CoreTable.ETAfterburner}\tBoost\n");
        string path = Path.Combine(folder.Text, "TypesOfShips.csv");
        var ships = File.ReadAllLines(path, Encoding.Latin1);
        int index = Array.FindIndex(ships, 1, l => CoreTable.ParseInt(l.Split('\t')[19]) > 0);
        var cells = ships[index].Split('\t');
        short id = short.Parse(cells[0], System.Globalization.CultureInfo.InvariantCulture);
        cells[42] = "alphatech BETATECH"; cells[34] = "Boost"; cells[27] = "IsFighter IsMiner";
        ships[index] = string.Join('\t', cells); File.WriteAllLines(path, ships, Encoding.Latin1);
        path = Path.Combine(folder.Text, "Hardpoints.csv");
        var hardpoints = File.ReadAllLines(path, Encoding.Latin1);
        index = Array.FindIndex(hardpoints, 1, l => CoreTable.ParseInt(l.Split('\t')[0]) == id);
        cells = hardpoints[index].Split('\t'); cells[5] = "One Two";
        hardpoints[index] = string.Join('\t', cells); File.WriteAllLines(path, hardpoints, Encoding.Latin1);
        using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(folder.Config());
        var core = server.Missions[0].Core;
        Assert.Equal(1337, core.Version);
        Assert.Equal(0x81, core.HullTypes[id].TtbmRequired[0]);
        Assert.Equal(4, core.HullTypes[id].PmEquipment[CoreTable.ETAfterburner]);
        Assert.Equal(3, core.HullTypes[id].Hardpoints[0].PartMask);
        Assert.Equal(0x4080, core.HullTypes[id].HabmCapabilities);
        Assert.Equal(StaticCore.Load(IgcReader.ParseFile(Folder.Source)).HullTypes.Count, core.HullTypes.Count);
    }

    [SkippableFact]
    public void Present_binary_is_preferred_and_a_corrupt_binary_never_falls_back()
    {
        using var folder = new Folder();
        File.Copy(Folder.Source, folder.Binary);
        File.WriteAllText(Path.Combine(folder.Text, "Constants.csv"), "malformed");
        var cache = new StaticCoreCache();
        int version = IgcReader.ParseFile(Folder.Source).Version!.Value;
        Assert.Equal(version, cache.ReadVersion("preferred", folder.Binary));
        using (var lease = cache.Load("preferred", folder.Binary)) Assert.Equal(version, lease.Core.Version);
        using (var server = new Srv { SpawnInitialMinersAtBoot = false })
        {
            server.Boot(folder.Config());
            Assert.Equal(version, server.Missions[0].Core.Version);
        }
        File.WriteAllBytes(folder.Binary, [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => cache.Load("corrupt", folder.Binary));
    }

    [SkippableTheory]
    [InlineData("Constants.csv", "truncated")]
    [InlineData("TypesOfShips.csv", "columns")]
    [InlineData("TypesOfProjectiles.csv", "number")]
    [InlineData("Civilizations.csv", "missing")]
    public void Malformed_text_is_refused_atomically_with_a_useful_error(string table, string damage)
    {
        using var folder = new Folder();
        string path = Path.Combine(folder.Text, table);
        byte[] original = File.ReadAllBytes(path);
        if (damage == "missing") File.Delete(path);
        else if (damage == "truncated") File.WriteAllText(path, "header\n0\t1\n");
        else
        {
            var lines = File.ReadAllLines(path, Encoding.Latin1);
            if (damage == "columns") lines[1] = "1\t2";
            else
            {
                var cells = lines[1].Split('\t'); cells[1] = "not-a-number";
                lines[1] = string.Join('\t', cells);
            }
            File.WriteAllLines(path, lines, Encoding.Latin1);
        }
        var cache = new StaticCoreCache();
        Assert.Equal(1337, cache.ReadVersion("bad", folder.Binary));
        var error = Assert.Throws<InvalidDataException>(() => cache.Load("bad", folder.Binary));
        Assert.Contains(table, error.Message);
        using (var server = new Srv { SpawnInitialMinersAtBoot = false })
            Assert.Throws<InvalidDataException>(() => server.Boot(folder.Config()));
        File.WriteAllBytes(path, original);
        using var repaired = cache.Load("bad", folder.Binary);
        Assert.Equal(1337, repaired.Core.Version); // failed import did not cache a partial core
    }

    [SkippableFact]
    public void Missing_binary_and_text_preserve_server_failure_and_client_null_resolution()
    {
        using var folder = new Folder(); Directory.Delete(folder.Text, true);
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        Assert.Contains("not found under artPath", Assert.Throws<InvalidDataException>(() => server.Boot(folder.Config())).Message);
        WithArtwork(folder.Root, () =>
        {
            using var client = new ConnectionSession();
            Assert.Null(client.World.MissionCoreResolver("text"));
        });
    }

    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Real_server_boot_and_client_join_use_text_only_core_over_loopback()
    {
        using var folder = new Folder();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(folder.Config());
        Assert.False(File.Exists(folder.Binary));
        Assert.Equal(1337, server.Missions[0].Core.Version);
        WithArtwork(folder.Root, () =>
        {
            using var client = new ConnectionSession();
            client.Connect(server.BoundPort, "TextPilot");
            client.SendLogonRequest(new() { CharacterName = "TextPilot", FedsrvVer = LogonRequest.MsgVer, DwCookie = uint.MaxValue, CrcFileList = -1 });
            Pump(server, () => client.Ack is not null);
            Assert.True(client.Ack!.FValidated);
            Pump(server, () => client.World.SideRoster is not null);
            client.SendPositionReq(0);
            Pump(server, () => client.World.JoinedShipId.HasValue || client.World.JoinError is not null);
            Assert.Null(client.World.JoinError);
            Assert.True(client.Ack!.FValidated);
            Assert.Equal(1337, client.World.Mission!.StaticCoreVersion);
            Assert.Equal(1337, client.World.MissionCore!.Version);
            Assert.NotSame(server.Missions[0].Core, client.World.MissionCore);
            Assert.Equivalent(Semantics(server.Missions[0].Core), Semantics(client.World.MissionCore), strict: true);
            Pump(server, () => client.World.MyShipId.HasValue);
            Assert.NotEmpty(client.World.Clusters);
        });
    }

    [SkippableFact]
    public void Client_text_revisions_share_and_retire_independently_and_refuse_binary_version_mismatch()
    {
        using var folder = new Folder();
        WithArtwork(folder.Root, () =>
        {
            using var a = new ConnectionSession(); using var b = new ConnectionSession(); using var edited = new ConnectionSession();
            void Join(ConnectionSession s, int version = 1337)
            {
                s.World.Apply(new MissionDef { SzIGCStaticFile = "text", VerIgcCore = version });
                s.World.Apply(new JoinedMission { ShipId = 1 });
            }
            Join(a); Join(b);
            Assert.Null(a.World.JoinError); Assert.Null(b.World.JoinError);
            var old = a.World.MissionCore!; Assert.Same(old, b.World.MissionCore);
            string table = Path.Combine(folder.Text, "TypesOfShips.csv");
            File.AppendAllText(table, "\n"); // invalid revision must fail and not reuse the old cache
            Join(edited); Assert.Contains("TypesOfShips.csv", edited.World.JoinError);
            Assert.Null(edited.World.MissionCore);
            var lines = File.ReadAllLines(table, Encoding.Latin1).Where(l => l.Length > 0).ToArray();
            var fields = lines[1].Split('\t'); fields[39] = "P908 Edited Hull"; lines[1] = string.Join('\t', fields);
            File.WriteAllLines(table, lines, Encoding.Latin1);
            Join(edited); Assert.Null(edited.World.JoinError);
            var current = edited.World.MissionCore!; Assert.NotSame(old, current);
            Assert.Contains(current.HullTypes.Values, h => h.Name == "P908 Edited Hull");
            a.World.Reset();
            using (var retained = StaticCoreCache.Shared.Retain(old)) Assert.NotNull(retained);
            b.Dispose(); Assert.Null(StaticCoreCache.Shared.Retain(old));
            using (var retained = StaticCoreCache.Shared.Retain(current)) Assert.NotNull(retained);
            edited.Dispose(); Assert.Null(StaticCoreCache.Shared.Retain(current));
            Join(a, 12345); Assert.Contains("out of sync", a.World.JoinError); Assert.Null(a.World.JoinedShipId);
            File.Copy(Folder.Source, folder.Binary);
            Join(a); Assert.Contains("out of sync", a.World.JoinError); Assert.Null(a.World.JoinedShipId);
            Join(a, IgcReader.ParseFile(Folder.Source).Version!.Value);
            Assert.Null(a.World.JoinError); // present binary preferred by client even beside text
        });
    }

    private static void WithArtwork(string root, Action action)
    {
        string? old = Environment.GetEnvironmentVariable(ExternalPaths.ArtworkEnvVar);
        try { Environment.SetEnvironmentVariable(ExternalPaths.ArtworkEnvVar, root); action(); }
        finally { Environment.SetEnvironmentVariable(ExternalPaths.ArtworkEnvVar, old); }
    }
    private static void Pump(Srv server, Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < 15)
        {
            server.TickSim();
            if (done()) return;
            Thread.Sleep(2);
        }
        Assert.True(done(), "Timed out waiting for production client wire state.");
    }
}
