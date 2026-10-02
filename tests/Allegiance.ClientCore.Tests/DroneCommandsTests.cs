using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P486 — prove the commander tool's pure translation rules in isolation (NO
/// live server): the command-name → native command-id table and the
/// target-descriptor → (object type, id) forms that <c>invest</c> /
/// <c>list_drones</c> / <c>order_drone</c> build their wire messages from.
/// These are the exact rules <see cref="DroneCommands"/> feeds the P469
/// CommandToDrone path (FM_CS_CHATMESSAGE with CommandID + target), so a wrong
/// mapping here is a real wrong order on the wire. Must-fail-if-removed pairs
/// are spelled out per test.
/// </summary>
public class DroneCommandsTests
{
    // ── command → cid (native c_cid*, igc.h:897-912, mirrored in OrderChange) ──

    [Theory]
    [InlineData("default", OrderChange.CidDefault)]      // 0
    [InlineData("attack", OrderChange.CidAttack)]        // 1
    [InlineData("defend", OrderChange.CidDefend)]        // 3
    [InlineData("goto", OrderChange.CidGoto)]            // 5
    [InlineData("mine", OrderChange.CidMine)]            // 8
    [InlineData("build", OrderChange.CidBuild)]          // 9
    [InlineData("donothing", OrderChange.CidDoNothing)]  // -3
    [InlineData("hold", OrderChange.CidDoNothing)]
    [InlineData("repair", OrderChange.CidRepair)]        // 6
    [InlineData("pickup", OrderChange.CidPickup)]        // 4
    [InlineData("join", OrderChange.CidJoin)]            // 7
    public void ParseCommand_maps_native_names_to_cids(string name, short expectedCid)
    {
        // Must-fail-if-removed: drop the mapping row and this reds.
        Assert.True(DroneCommands.TryParseCommand(name, out short cid));
        Assert.Equal(expectedCid, cid);
    }

    [Fact]
    public void ParseCommand_is_case_insensitive_and_trims()
    {
        Assert.True(DroneCommands.TryParseCommand("  Mine ", out short cid));
        Assert.Equal(OrderChange.CidMine, cid);
        Assert.True(DroneCommands.TryParseCommand("GOTO", out short g));
        Assert.Equal(OrderChange.CidGoto, g);
    }

    [Fact]
    public void ParseCommand_rejects_unknown_names()
    {
        // An unknown command must refuse (never guess a cid).
        Assert.False(DroneCommands.TryParseCommand("fly", out _));
        Assert.False(DroneCommands.TryParseCommand("", out _));
        Assert.False(DroneCommands.TryParseCommand(null, out _));
    }

    // ── target descriptor → (ot, oid) ──

    [Fact]
    public void ParseTarget_typed_ids_map_to_object_types()
    {
        Assert.True(DroneCommands.TryParseTarget("asteroid:42", out var ot1, out var o1));
        Assert.Equal(IgcObjectType.Asteroid, ot1); Assert.Equal((short)42, o1);

        Assert.True(DroneCommands.TryParseTarget("a:7", out var ot2, out var o2));
        Assert.Equal(IgcObjectType.Asteroid, ot2); Assert.Equal((short)7, o2);

        Assert.True(DroneCommands.TryParseTarget("ship:9", out var ot3, out var o3));
        Assert.Equal(IgcObjectType.Ship, ot3); Assert.Equal((short)9, o3);

        Assert.True(DroneCommands.TryParseTarget("station:2", out var ot4, out var o4));
        Assert.Equal(IgcObjectType.Station, ot4); Assert.Equal((short)2, o4);

        Assert.True(DroneCommands.TryParseTarget("warp:5", out var ot5, out var o5));
        Assert.Equal(IgcObjectType.Warp, ot5); Assert.Equal((short)5, o5);
    }

    [Fact]
    public void ParseTarget_bare_integer_means_ship()
    {
        Assert.True(DroneCommands.TryParseTarget("12", out var ot, out var oid));
        Assert.Equal(IgcObjectType.Ship, ot);
        Assert.Equal((short)12, oid);
    }

    [Fact]
    public void ParseTarget_empty_or_none_is_targetless()
    {
        // No target → OT_invalid (-1) / NA (-1): a targetless (e.g. donothing)
        // order.
        Assert.True(DroneCommands.TryParseTarget("", out var ot1, out var o1));
        Assert.Equal((IgcObjectType)(-1), ot1); Assert.Equal((short)-1, o1);
        Assert.True(DroneCommands.TryParseTarget("none", out var ot2, out var o2));
        Assert.Equal((IgcObjectType)(-1), ot2); Assert.Equal((short)-1, o2);
    }

    [Fact]
    public void ParseTarget_rejects_names_and_bad_ids()
    {
        // A bare NAME is not a form this parser handles (needs live lookup) —
        // must refuse rather than fabricate an id.
        Assert.False(DroneCommands.TryParseTarget("Alderaan", out _, out _));
        Assert.False(DroneCommands.TryParseTarget("asteroid:abc", out _, out _));
        Assert.False(DroneCommands.TryParseTarget("ship:-3", out _, out _));
        Assert.False(DroneCommands.TryParseTarget("bogus:1", out _, out _));
    }
}
