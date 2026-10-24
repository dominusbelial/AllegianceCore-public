using System.Globalization;
using System.Reflection;
using System.Text;
using Allegiance.Core;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P811 — map-generation seed reproducibility. The generator draws every bit
/// of randomness (cluster decorations, cluster names, asteroid placement,
/// Brawl's 3–5 scatter count) through a single RNG stream
/// (<see cref="IMapRandom"/>), so a fixed <see cref="MapGenParams.Seed"/>
/// reproduces the identical generated mission map on every run. These tests
/// prove "same seed → same map" (and that different seeds really differ), and
/// that the server's <c>--map-seed N</c> CLI overlay lands on a generated-map
/// config.
/// </summary>
public sealed class MapGenSeedReproTests
{
    private static readonly Lazy<StaticCore> GenCore = new(MapGenTestCore.LoadRepresentative);

    private static int[] CivIds(int n) =>
        GenCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId)
            .Where(c => c.InitialStationTypeId != -1
                && GenCore.Value.StationTypes.ContainsKey(c.InitialStationTypeId))
            .Take(n)
            .Select(c => (int)c.CivilizationId)
            .ToArray();

    private static MissionMap Generate(int seed, MapGenType type)
        => MissionMapGenerator.Generate(new MapGenParams
        {
            Type = type,
            Teams = 2,
            CivIds = CivIds(2),
            MapSize = 0,
            Seed = seed,
        }, GenCore.Value, 0.0);

    /// <summary>A canonical, order-preserving text fingerprint of every record
    /// in the generated map. Record-struct <c>==</c> can't be used directly
    /// (their float[] members compare by reference across two runs), so we
    /// flatten every field — including nested arrays — in declaration order
    /// with invariant formatting. Identical bits in, identical string out.</summary>
    private static string Fingerprint(MissionMap map)
    {
        var sb = new StringBuilder();
        AppendRecords(sb, map.Clusters);
        AppendRecords(sb, map.Warps);
        AppendRecords(sb, map.Stations);
        AppendRecords(sb, map.Asteroids);
        return sb.ToString();
    }

    private static void AppendRecords<T>(StringBuilder sb, IEnumerable<T> items)
    {
        foreach (var item in items)
            sb.Append(Flatten(item)).Append('\n');
    }

    private static string Flatten(object? obj)
    {
        if (obj is null) return "∅;";
        Type t = obj.GetType();
        if (t.IsPrimitive || obj is string or decimal)
            return Convert.ToString(obj, CultureInfo.InvariantCulture) + ";";
        if (obj is System.Collections.IEnumerable e)
        {
            var sb = new StringBuilder("[");
            foreach (var x in e) sb.Append(Flatten(x));
            sb.Append(']');
            return sb.ToString();
        }
        var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                      .Where(f => !f.IsStatic)
                      .OrderBy(f => f.MetadataToken)
                      .ToList();
        var inner = new StringBuilder();
        foreach (var f in fields)
            inner.Append(f.Name).Append('=').Append(Flatten(f.GetValue(obj))).Append(',');
        return inner.ToString();
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SameSeed_ProducesIdenticalMap()
    {
        Require.Artwork();
        var a = Generate(seed: 812377, MapGenType.HiHigher);
        var b = Generate(seed: 812377, MapGenType.HiHigher);
        Assert.Equal(Fingerprint(a), Fingerprint(b));

        // Sanity: the map is non-trivial — generated content, not an empty stub.
        Assert.NotEqual(0, a.Clusters.Count + a.Warps.Count + a.Stations.Count + a.Asteroids.Count);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void DifferentSeed_ProducesDifferentMap()
    {
        Require.Artwork();
        var a = Generate(seed: 813, MapGenType.HiHigher);
        var b = Generate(seed: 814, MapGenType.HiHigher);
        Assert.NotEqual(Fingerprint(a), Fingerprint(b));
    }

    [Fact]
    public void ApplyCli_MapSeed_OverlaysOntoGeneratedMap()
    {
        string path = Path.GetTempFileName();
        try
        {
            // A game configured with a procedural mapGen block.
            File.WriteAllText(path, """
                { "games": [ { "name": "G", "mapGen": { "type": "Brawl" }, "teams": 2,
                              "civIds": [ 1, 3 ], "sideNames": [ "A", "B" ] } ] }
                """);
            var config = ServerConfigLoader.Load(path, [("map-seed", "4242")]);
            var mapGen = Assert.IsType<Allegiance.Server.MapGenConfig>(Assert.Single(config.Games).MapGen);
            Assert.Equal(4242, mapGen.Seed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ApplyCli_MapSeed_RejectsFileOnlyConfig()
    {
        string path = Path.GetTempFileName();
        try
        {
            // A game that loads a file map has no generator to seed.
            File.WriteAllText(path, """
                { "games": [ { "name": "G", "map": "m2xspiderw.igc", "teams": 2,
                              "civIds": [ 1, 3 ], "sideNames": [ "A", "B" ] } ] }
                """);
            var ex = Assert.Throws<InvalidDataException>(
                () => ServerConfigLoader.Load(path, [("map-seed", "4242")]));
            Assert.Contains("requires a game configured with a generated map", ex.Message);
        }
        finally { File.Delete(path); }
    }
}
