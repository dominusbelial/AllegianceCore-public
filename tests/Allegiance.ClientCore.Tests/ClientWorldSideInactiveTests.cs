using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P455b — <c>FM_CS_SIDE_INACTIVE(68)</c> decodes and lands in
/// <see cref="ClientWorld"/> (the exact dispatch path
/// <see cref="Session.ConnectionSession"/> uses: <c>SideInactive.Decode</c> then
/// <c>ClientWorld.Apply</c>), so the client records the per-side active state and
/// can mark an eliminated side "destroyed" in the scoreboard / team list, exactly
/// native's team list. Unknown sides default to active (the server only sends a
/// <c>bActive=false</c> to DEACTIVATE a side, plus a possible later re-activation
/// via the P202 pre-game toggle), mirroring native's
/// <c>MissionInfo::SideActive(sideID)</c>.
/// </summary>
public class ClientWorldSideInactiveTests
{
    /// <summary>A decoded <c>bActive=false</c> for a side makes
    /// <see cref="ClientWorld.IsSideActive"/> read that side as inactive.</summary>
    [Fact]
    public void Decoded_SideInactive_marks_side_inactive()
    {
        var world = new ClientWorld();

        var msg = new SideInactive { SideId = 2, BActive = false, BChangeAet = false, BAet = false };
        var decoded = SideInactive.Decode(msg.Encode());   // exactly what ConnectionSession dispatches
        world.Apply(decoded);

        Assert.False(world.IsSideActive(2), "side 2 must be inactive after the message");
        Assert.True(world.IsSideActive(0), "an unreported side stays active");
        Assert.True(world.IsSideActive(1), "an unreported side stays active");
    }

    /// <summary>A <c>bActive=true</c> (a later re-activation) flips the side back
    /// active, last-write-wins like native's SetSideActive.</summary>
    [Fact]
    public void Reactivation_flips_side_back_active()
    {
        var world = new ClientWorld();
        world.Apply(new SideInactive { SideId = 2, BActive = false });
        Assert.False(world.IsSideActive(2));

        world.Apply(new SideInactive { SideId = 2, BActive = true });
        Assert.True(world.IsSideActive(2));
    }

    /// <summary>The <c>AllowEmptyTeams</c> bit is only updated when the message
    /// carries <c>bChangeAET=true</c>, exactly native appmsg.cpp:2585-2587. The
    /// P455 mid-game elimination broadcast carries <c>bChangeAET=false</c>, so
    /// normal deactivation leaves AET untouched.</summary>
    [Fact]
    public void AET_bit_only_updates_when_bChangeAET()
    {
        var world = new ClientWorld();
        Assert.Null(world.AllowEmptyTeams);

        // bChangeAET=false (the elimination path) must NOT flip AET.
        world.Apply(new SideInactive { SideId = 2, BActive = false, BChangeAet = false, BAet = true });
        Assert.Null(world.AllowEmptyTeams);

        // bChangeAET=true (the P202 pre-game path) flips it to the carried value.
        world.Apply(new SideInactive { SideId = 3, BActive = false, BChangeAet = true, BAet = true });
        Assert.True(world.AllowEmptyTeams);
    }
}
