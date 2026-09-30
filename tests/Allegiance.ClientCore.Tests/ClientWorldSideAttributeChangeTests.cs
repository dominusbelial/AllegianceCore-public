using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P446 — <c>FM_S_SIDE_ATTRIBUTE_CHANGE(76)</c> decodes and lands in
/// <see cref="ClientWorld"/> (the exact dispatch path
/// <see cref="Session.ConnectionSession"/> uses: <c>SideAttributeChange.Decode</c>
/// then <c>ClientWorld.Apply</c>), so the client learns each side's global
/// attribute multiplier set and can scale the autopilot braking estimate and,
/// later, P447/P448 prediction. The client has no per-side join preface for
/// the GA set (unlike CREATE_BUCKETS for the tech mask), so an unknown side
/// reads identity via <see cref="ClientWorld.GlobalAttributesFor"/>.
/// </summary>
public class ClientWorldSideAttributeChangeTests
{
    /// <summary>Decode+apply round-trip of a real wire payload lands the side's
    /// multiplier set in the world, indexed by SideId.</summary>
    [Fact]
    public void Decoded_SideAttributeChange_lands_in_ClientWorld()
    {
        var world = new ClientWorld();

        // A 25-float set with a distinctive max-speed + thrust delta.
        var gas = new float[GlobalAttributeSet.Count];
        Array.Fill(gas, 1f);
        gas[GlobalAttributeSet.MaxSpeed] = 1.5f;
        gas[GlobalAttributeSet.Thrust] = 1.2f;
        var msg = new SideAttributeChange { SideId = 3, GasAttributes = gas };

        // Round-trip through the wire exactly as ConnectionSession dispatches it.
        var decoded = SideAttributeChange.Decode(msg.Encode());
        world.Apply(decoded);

        Assert.Equal(1.5f, world.GlobalAttributesFor(3).Get(GlobalAttributeSet.MaxSpeed), 6);
        Assert.Equal(1.2f, world.GlobalAttributesFor(3).Get(GlobalAttributeSet.Thrust), 6);
        Assert.True(world.GlobalAttributesBySideId.ContainsKey(3));
    }

    /// <summary>A later broadcast for the same side REPLACES the whole set (last
    /// post-Apply wins), like the tech-mask upsert.</summary>
    [Fact]
    public void Later_broadcast_replaces_the_sides_set()
    {
        var world = new ClientWorld();
        var a = new float[GlobalAttributeSet.Count]; Array.Fill(a, 1f); a[GlobalAttributeSet.MaxSpeed] = 1.5f;
        var b = new float[GlobalAttributeSet.Count]; Array.Fill(b, 1f); b[GlobalAttributeSet.MaxSpeed] = 2.0f;

        world.Apply(new SideAttributeChange { SideId = 3, GasAttributes = a });
        world.Apply(new SideAttributeChange { SideId = 3, GasAttributes = b });

        Assert.Equal(2.0f, world.GlobalAttributesFor(3).Get(GlobalAttributeSet.MaxSpeed), 6);
        Assert.Equal(1.0f, world.GlobalAttributesFor(3).Get(GlobalAttributeSet.Thrust), 6);   // untouched element from the newest set
    }

    /// <summary>A side never broadcast for reads identity (all 1.0) — the honest
    /// neutral before any development completes, matching a no-bonus side.</summary>
    [Fact]
    public void Unknown_side_reads_identity()
    {
        var world = new ClientWorld();
        Assert.Equal(GlobalAttributeSet.Identity, world.GlobalAttributesFor(99));
        Assert.Equal(1.0f, world.GlobalAttributesFor(99).Get(GlobalAttributeSet.MaxSpeed), 6);
        Assert.False(world.GlobalAttributesBySideId.ContainsKey(99));
    }
}
