using System.IO;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P487 — the client-side bucket synthesis (<see cref="BucketCatalog.Describe"/>)
/// must reproduce the server's bucket catalog (<see cref="BucketCatalog.ForSide"/>)
/// <b>id-for-id</b> for the same core + initial-techs + GA, because the wire's
/// <c>BUCKET_STATUS(65)</c> correlates funding by bucket id alone. These pin the
/// two synthesis paths to one numbering (contiguous ids from 1, in native
/// CreateBuckets order) against the REAL PCore001.igc, so the investment pane can
/// map every server-reported bucket to a name/price/kind.
/// </summary>
public class BucketCatalogDescribeTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // IIgcSite is all no-op-default members, so an empty impl is a valid site for
    // ForSide's bucket construction.
    private sealed class NullSite : IIgcSite
    {
    }

    private static StaticCore Core() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Describe_MatchesForSide_IdForId_ForSameInputs()
    {
        Require.Artwork();
        var core = Core();
        var techs = TechTreeBitMask.Empty;
        var ga = GlobalAttributeSet.Identity;
        var site = new NullSite();

        var server = BucketCatalog.ForSide(core, site, techs, ga);
        var client = BucketCatalog.Describe(core, techs, ga);

        // Same length and every field that drives the UI lines up by index.
        Assert.Equal(server.Count, client.Count);
        for (int i = 0; i < server.Count; i++)
        {
            Assert.Equal(server[i].Id, client[i].Id);
            Assert.Equal(server[i].ItemType, client[i].ItemType);
            Assert.Equal(server[i].Name, client[i].Name);
            Assert.Equal(server[i].Price, client[i].Price);
            Assert.Equal(server[i].TimeToBuild, client[i].TimeToBuildSec);
            Assert.Equal(server[i].HullIdDrone, client[i].HullIdDrone);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Describe_MatchesForSide_AcrossSeveralInitialTechSets()
    {
        Require.Artwork();
        var core = Core();
        var ga = GlobalAttributeSet.Identity;
        var site = new NullSite();
        // Exercise the reachable/obsolete skips with non-trivial initial masks:
        // an all-bits mask makes almost every development obsolete and unlocks all
        // reachable drone/station buckets; an empty mask is the conservative pole.
        var masks = new[]
        {
            TechTreeBitMask.Empty,
            TechTreeBitMask.FromBytes(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray()),
        };
        foreach (var techs in masks)
        {
            var server = BucketCatalog.ForSide(core, site, techs, ga);
            var client = BucketCatalog.Describe(core, techs, ga);
            Assert.Equal(server.Count, client.Count);
            for (int i = 0; i < server.Count; i++)
                Assert.Equal(server[i].Id, client[i].Id);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Describe_Id1_IsSyntheticShipBucket()
    {
        Require.Artwork();
        var core = Core();
        var views = BucketCatalog.Describe(core, TechTreeBitMask.Empty, GlobalAttributeSet.Identity);
        Assert.NotEmpty(views);
        Assert.Equal(1, views[0].Id);
        Assert.Equal(BucketItemType.BitShipType, views[0].ItemType);
        Assert.Equal(BucketCatalog.SyntheticDroneHullId, views[0].HullIdDrone);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Describe_IdsAreContiguous_StartingAtOne()
    {
        Require.Artwork();
        var core = Core();
        var views = BucketCatalog.Describe(core, TechTreeBitMask.Empty, GlobalAttributeSet.Identity);
        for (int i = 0; i < views.Count; i++)
            Assert.Equal(i + 1, views[i].Id);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Describe_GroupsAreOrdered_DevDroneStation()
    {
        Require.Artwork();
        var core = Core();
        // An all-techs mask yields every development (none obsolete), and unlocks
        // all reachable drone/station buckets, exercising all three groups in order.
        var views = BucketCatalog.Describe(
            core,
            TechTreeBitMask.FromBytes(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray()),
            GlobalAttributeSet.Identity);

        // First after the synthetic ship bucket should be a development; then
        // drone types; then station types. Record the first index of each.
        int? devIdx = null, droneIdx = null, stationIdx = null;
        for (int i = 0; i < views.Count; i++)
        {
            switch (views[i].ItemType)
            {
                case BucketItemType.BitDevelopment when devIdx is null: devIdx = i; break;
                case BucketItemType.BitDroneType when droneIdx is null: droneIdx = i; break;
                case BucketItemType.BitStationType when stationIdx is null: stationIdx = i; break;
            }
        }
        Assert.NotNull(devIdx);
        Assert.NotNull(droneIdx);
        Assert.NotNull(stationIdx);
        Assert.True(devIdx < droneIdx, "developments must precede drone buckets");
        Assert.True(droneIdx < stationIdx, "drone buckets must precede station buckets");
    }
}
