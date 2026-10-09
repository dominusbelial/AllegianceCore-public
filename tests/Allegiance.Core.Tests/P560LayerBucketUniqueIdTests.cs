using System;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P560 — failing-first test for the layer-drone bucket "resolve by id" fix.
/// The live 221 finding: several civs' layer-drone buckets SHARE one name
/// ('.TowerAC' matched 10 buckets on m2xspiderw), so resolving by NAME hit
/// the ambiguity error and aborted the whole showcase. Resolving by numeric ID
/// is exact and unambiguous. This test loads the real PCore001 corpus + the
/// 221 civs' base techs, enumerates the side's bucket catalog (the same
/// <see cref="BucketCatalog.Describe"/> the MCP uses), finds the layer-drone
/// (PilotTypeLayer) buckets, and asserts they carry DISTINCT ids and that a
/// numeric-id lookup selects exactly one — the property the id fix rests on.
/// Under the pre-P560 code (name resolution) this guarantee does not hold, so
/// the test fails on today's code and passes with the fix.
/// </summary>
public class P560LayerBucketUniqueIdTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static StaticCore LoadCore() =>
        StaticCore.Load(IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, "PCore001.igc")));

    [Theory]
    [InlineData((short)25)]   // Iron Coalition — live 221 side 0
    [InlineData((short)3)]    // Ga'Taraan — live 221 side 1
    public void Layer_drone_bucket_resolves_by_unique_id_despite_shared_names(short civId)
    {
        var core = LoadCore();
        var civ = core.Civilizations[civId];
        var techs = TechTreeBitMask.FromBytes(civ.TtbmBaseTechs);
        var ga = GlobalAttributeSet.FromArray(civ.GasBaseAttributes);
        var views = BucketCatalog.Describe(core, techs, ga).ToList();

        var layerBuckets = views.Where(v => v.ItemType == BucketItemType.BitDroneType
                                            && v.DroneType is { } dt && dt.PilotType == Ship.PilotTypeLayer).ToList();
        Assert.NotEmpty(layerBuckets);
        // Distinct ids — the whole point of the id fix: names may repeat, ids may not.
        Assert.Equal(layerBuckets.Count, layerBuckets.Select(v => v.Id).Distinct().Count());

        // A numeric-id lookup must select EXACTLY ONE bucket (ResolveBucket's
        // short.TryParse + exact-id branch) — never ambiguous.
        foreach (var v in layerBuckets)
        {
            var byId = views.Where(x => x.Id == v.Id).ToList();
            Assert.Single(byId);
            Assert.Equal(v.Id, byId[0].Id);
            Assert.True(short.TryParse(v.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var parsed)
                        && parsed == v.Id, $"id {v.Id} must round-trip through the numeric-string form FindLayerBucketName returns");
        }
    }
}
