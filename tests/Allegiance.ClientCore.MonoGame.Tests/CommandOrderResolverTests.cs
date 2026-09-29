using Allegiance.Core;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P425 — the pure ORDER-CHANGE payload decision for a commandee+target pair
/// (<see cref="Game1.CommandOrderResolver"/>). These pin the contract that earns
/// the test — the ISSUE-ORDER key computes the exact real wire shape the
/// server's direct-NPC Build gate expects:
/// <list type="bullet">
/// <item>A Builder-role commandee at an asteroid target → a real
/// <c>ORDER_CHANGE</c> with <c>CommandId=c_cidBuild</c>,
/// <c>ObjectType=OT_asteroid</c>, <c>ObjectID=</c>the rock (mirroring
/// <c>HandlePlayerBuildOrder</c>'s own gate, so the client pre-check and the
/// server's authoritative refusal agree).</item>
/// <item>A non-Builder commandee → null (no order; the server would refuse
/// Builder-role anyway, so the client refuses upfront).</item>
/// <item>A Builder ordered at something other than an asteroid → null.</item>
/// <item>A non-ship "commandee" → null (only ships/drones are commanded).</item>
/// </list>
/// The commandee's role bits are its hull's <c>HabmCapabilities</c>; Builder =
/// <c>DataHullTypeIGC.HabmBuilder</c> (0x8000).
/// </summary>
public class CommandOrderResolverTests
{
    private static readonly short BuilderRole = DataHullTypeIGC.HabmBuilder;
    private static readonly short FighterRole = DataHullTypeIGC.HabmFighter;

    [Fact]
    public void Builder_commandee_at_asteroid_yields_real_build_order_change()
    {
        var order = Game1.CommandOrderResolver.Resolve(BuilderRole, Game1.TargetKind.Ship, Game1.TargetKind.Asteroid, targetId: 6211);
        Assert.NotNull(order);
        // The exact real wire shape the server's HandleOrderChange/direct-NPC
        // Build gate consumes: CommandId=c_cidBuild, ObjectType=OT_asteroid,
        // ObjectID=the target rock. ShipID is supplied separately by the key
        // handler (the commandee's id), not part of the resolver's target half.
        Assert.Equal(OrderChange.CidBuild, order.Value.CommandId);
        Assert.Equal(IgcObjectType.Asteroid, order.Value.ObjectType);
        Assert.Equal((short)6211, order.Value.ObjectId);
    }

    [Fact]
    public void Non_builder_commandee_at_asteroid_yields_null()
    {
        Assert.Null(Game1.CommandOrderResolver.Resolve(FighterRole, Game1.TargetKind.Ship, Game1.TargetKind.Asteroid, 999));
    }

    [Fact]
    public void Builder_commandee_at_ship_or_station_yields_null()
    {
        // A Builder is only orderable to build an asteroid; ordering at a ship or
        // station is not a Build order.
        Assert.Null(Game1.CommandOrderResolver.Resolve(BuilderRole, Game1.TargetKind.Ship, Game1.TargetKind.Ship, 888));
        Assert.Null(Game1.CommandOrderResolver.Resolve(BuilderRole, Game1.TargetKind.Ship, Game1.TargetKind.Station, 887));
        Assert.Null(Game1.CommandOrderResolver.Resolve(BuilderRole, Game1.TargetKind.Ship, Game1.TargetKind.Warp, 886));
    }

    [Fact]
    public void Non_ship_commandee_yields_null()
    {
        // Only ships/drones are commanded; an asteroid or station can never be a
        // commandee, regardless of target.
        Assert.Null(Game1.CommandOrderResolver.Resolve(BuilderRole, Game1.TargetKind.Asteroid, Game1.TargetKind.Asteroid, 881));
        Assert.Null(Game1.CommandOrderResolver.Resolve(BuilderRole, Game1.TargetKind.Station, Game1.TargetKind.Asteroid, 882));
    }

    [Fact]
    public void Zero_role_bits_yields_null_even_at_asteroid()
    {
        // Unknown/unresolvable role bits (no ShopCatalog match) never fabricate a
        // Build order — the server must be the authority, not a guess.
        Assert.Null(Game1.CommandOrderResolver.Resolve(0, Game1.TargetKind.Ship, Game1.TargetKind.Asteroid, 871));
    }
}
