using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// Exercises the <see cref="IIgcSite"/> seam: the interface's default
/// implementations behave sensibly, <see cref="RecordingIgcSite"/> observes
/// calls (ordered, with salient args), <see cref="NullIgcSite"/> takes every
/// default, and <see cref="ThingSite"/>/<see cref="ClusterSite"/> are creatable
/// and hold their owner.
/// </summary>
/// <remarks>
/// <see cref="Mission.Assemble"/> binds the site but never invokes it — the
/// C++ only calls back into the site during <em>runtime</em> object
/// construction (<c>modelIGC.cpp:117</c>, <c>clusterIGC.cpp:41</c>), which is a
/// later task, so static assembly legitimately emits no calls. The recording
/// assertions here therefore exercise the site directly rather than forcing
/// assembly to manufacture a call.
/// </remarks>
public class IgcSiteTests
{
    // ── default behavior ───────────────────────────────────────────────────
    [Fact]
    public void ServerTime_is_identity_on_the_default_impl()
    {
        var site = new RecordingIgcSite();
        Assert.Equal(1234.5, site.ServerTimeFromClientTime(1234.5));
        Assert.Equal(678.25, site.ClientTimeFromServerTime(678.25));
    }

    [Fact]
    public void Settlement_and_combat_defaults_match_the_Cpp()
    {
        var site = new RecordingIgcSite();
        Assert.True(site.DockWithStationEvent(null!, null!));
        Assert.True(site.LandOnCarrierEvent(null!, null!, 1f));
        Assert.True(site.RescueShipEvent(null!, null!));
        Assert.True(site.HitTreasureEvent(0, null!, null!));
        Assert.True(site.ContinueRipcord(null!, null!));
        Assert.False(site.UseRipcord(null!, null!));
        Assert.False(site.Reload(null!, null!, EquipmentType.Weapon));
        Assert.False(site.HandlePickDefaultOrder(null!));
        Assert.Null(site.GetCluster(null!, null!));
        Assert.Null(site.GetRipcordCluster(null!, 0));
    }

    [Fact]
    public void Shims_are_creatable_and_hold_their_owner()
    {
        var site = new RecordingIgcSite();
        var ts = site.CreateThingSite(null!);
        var cs = site.CreateClusterSite(null!);
        Assert.IsAssignableFrom<ThingSite>(ts);
        Assert.IsAssignableFrom<ClusterSite>(cs);
        // The C++ default constructs the shim unattached (ignores the model/cluster).
        Assert.Null(ts.Owner);
        Assert.Null(cs.Owner);
    }

    // ── NullIgcSite takes every default ─────────────────────────────────────
    [Fact]
    public void NullIgcSite_behaves_like_the_default_impl()
    {
        var nullSite = new NullIgcSite();
        var rec = new RecordingIgcSite();
        IIgcSite baseNull = nullSite;

        // Spot-check that a NullIgcSite (which overrides nothing, so every
        // member falls through to the interface default) produces the same
        // results as a RecordingIgcSite delegated to the defaults. Default
        // interface implementations are only reachable via the interface type,
        // hence the cast.
        Assert.Equal(rec.ServerTimeFromClientTime(7), baseNull.ServerTimeFromClientTime(7));
        Assert.True(baseNull.DockWithStationEvent(null!, null!));
        Assert.False(baseNull.UseRipcord(null!, null!));
        Assert.Null(baseNull.GetRipcordCluster(null!, 0));
    }

    // ── RecordingIgcSite observes calls ────────────────────────────────────
    [Fact]
    public void RecordingIgcSite_records_every_call_in_order_with_args()
    {
        var site = new RecordingIgcSite();
        site.KillShipEvent(10.5, null!, null!, 99f, new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f));
        site.PostPlainText(true, "hello");
        site.CreateThingSite(null!);
        site.UseRipcord(null!, null!);

        Assert.Equal(4, site.Calls.Count);
        Assert.StartsWith("KillShipEvent(", site.Calls[0]);
        Assert.Contains("10.5", site.Calls[0]);
        Assert.Contains("99", site.Calls[0]);
        Assert.Equal("PostPlainText(True, \"hello\")", site.Calls[1]);
        Assert.Equal("CreateThingSite(null)", site.Calls[2]);
        Assert.Equal("UseRipcord(null, null)", site.Calls[3]);
    }

    [Fact]
    public void RecordingIgcSite_distinguishes_real_object_arguments_from_null()
    {
        var site = new RecordingIgcSite();
        var model = new TestModel();
        site.CreateThingSite(model);
        Assert.Equal($"CreateThingSite({typeof(TestModel).Name})", site.Calls.Single());

        site.Reset();
        site.CreateThingSite(null!);
        Assert.Equal("CreateThingSite(null)", site.Calls.Single());
    }

    [Fact]
    public void RecordingIgcSite_can_reset()
    {
        var site = new RecordingIgcSite();
        site.ChangeGameState();
        Assert.True(site.Any);
        site.Reset();
        Assert.False(site.Any);
        Assert.Empty(site.Calls);
    }

    // A minimal real marker instance, so a test can distinguish a concrete
    // object argument from null in the recorded trace.
    private sealed class TestModel : IModelIgc;
}
