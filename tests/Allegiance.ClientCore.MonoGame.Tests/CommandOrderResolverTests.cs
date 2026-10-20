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

/// <summary>
/// P757 — the full generic target-to-order table: native
/// <c>IshipIGC::GetDefaultOrder</c> (Igc/shipIGC.h:2071-2161), which the
/// issue-order key now resolves through (<c>SetTarget(pmodel,
/// c_cidDefault)</c>, WinTrek.cpp:8830). Each case pins one native branch.
/// </summary>
public sealed class P757DefaultOrderTests
{
    private static Game1.CommandOrder? Resolve(
        short roles, bool isLayer = false, bool onParent = false,
        Game1.TargetKind kind = Game1.TargetKind.Ship, short id = 1,
        bool? friendly = null, short? targetHullAbm = null,
        short? stationSabm = null, short? probeEabm = null)
        => Game1.CommandOrderResolver.ResolveDefaultOrder(roles, isLayer, onParent,
            new Game1.CommandTargetProfile(kind, id, friendly, targetHullAbm, stationSabm, probeEabm));

    private const short Miner = DataHullTypeIGC.HabmMiner;
    private const short Builder = DataHullTypeIGC.HabmBuilder;
    private const short Fighter = DataHullTypeIGC.HabmFighter;
    private const short Lifepod = DataHullTypeIGC.HabmLifepod;
    private const short Rescue = DataHullTypeIGC.HabmRescue;
    private const short Carrier = DataHullTypeIGC.HabmCarrier;
    private const short LandOnCarrier = DataHullTypeIGC.HabmLandOnCarrier;

    [Fact]
    public void Miner_and_builder_roles_split_mine_and_build_at_an_asteroid()
    {
        // shipIGC.h:2080-2090: Miner → Mine, Builder → Build at an asteroid;
        // anything else (warp) stays Goto.
        Assert.Equal(OrderChange.CidMine, Resolve(Miner, kind: Game1.TargetKind.Asteroid)!.Value.CommandId);
        Assert.Equal(OrderChange.CidBuild, Resolve(Builder, kind: Game1.TargetKind.Asteroid)!.Value.CommandId);
        Assert.Equal(OrderChange.CidGoto, Resolve(Miner, kind: Game1.TargetKind.Warp)!.Value.CommandId);
    }

    [Fact]
    public void Layer_commands_build_at_a_warp()
    {
        // shipIGC.h:2092-2097: Layer + buoy/warp → Build; anything else Goto.
        Assert.Equal(OrderChange.CidBuild, Resolve(0, isLayer: true, kind: Game1.TargetKind.Warp)!.Value.CommandId);
        Assert.Equal(OrderChange.CidGoto, Resolve(0, isLayer: true, kind: Game1.TargetKind.Asteroid)!.Value.CommandId);
    }

    [Fact]
    public void Wingman_defends_friendly_ships_and_attacks_enemy_ships()
    {
        // shipIGC.h:2099-2106 friendly → Defend; :2160-2161 sided enemy → Attack.
        Assert.Equal(OrderChange.CidDefend, Resolve(Fighter, kind: Game1.TargetKind.Ship, friendly: true)!.Value.CommandId);
        Assert.Equal(OrderChange.CidAttack, Resolve(Fighter, kind: Game1.TargetKind.Ship, friendly: false)!.Value.CommandId);
        Assert.Equal(OrderChange.CidGoto, Resolve(Fighter, kind: Game1.TargetKind.Asteroid)!.Value.CommandId);   // sideless → Goto
    }

    [Fact]
    public void Lifepod_rescue_pairing_and_carrier_make_the_friendly_ship_order_pickup_or_goto()
    {
        // shipIGC.h:2108-2119: target lifepod + own rescue (or the reverse) →
        // Pickup; target carrier + own LandOnCarrier → Goto. Only when the
        // commandee has no parent ship.
        Assert.Equal(OrderChange.CidPickup,
            Resolve(Rescue, kind: Game1.TargetKind.Ship, friendly: true, targetHullAbm: Lifepod)!.Value.CommandId);
        Assert.Equal(OrderChange.CidPickup,
            Resolve(Lifepod, kind: Game1.TargetKind.Ship, friendly: true, targetHullAbm: Rescue)!.Value.CommandId);
        Assert.Equal(OrderChange.CidGoto,
            Resolve(LandOnCarrier, kind: Game1.TargetKind.Ship, friendly: true, targetHullAbm: Carrier)!.Value.CommandId);
        // On a parent ship the refinements never fire (m_pshipParent != NULL).
        Assert.Equal(OrderChange.CidDefend,
            Resolve(Rescue, onParent: true, kind: Game1.TargetKind.Ship, friendly: true, targetHullAbm: Lifepod)!.Value.CommandId);
        // Unrelated capabilities keep Defend.
        Assert.Equal(OrderChange.CidDefend,
            Resolve(Fighter, kind: Game1.TargetKind.Ship, friendly: true, targetHullAbm: Fighter)!.Value.CommandId);
    }

    [Fact]
    public void Friendly_station_stays_defend_unless_the_capability_ladder_makes_it_goto()
    {
        // shipIGC.h:2121-2136: non-fighter + CapLand → Goto; lifepod +
        // (Rescue|Land) → Goto; else fighter + Land → Goto; otherwise Defend.
        // The non-fighter branch checks CapLand ONLY (native shipIGC.h:2124-2128):
        // a plain Land-capable station does not make a non-fighter Goto. Use a
        // rescue-role hull: miner/builder hulls take native's earlier pilot-type
        // branch (shipIGC.h:2080-2090), which also leaves the Goto default.
        Assert.Equal(OrderChange.CidDefend,
            Resolve(Fighter, kind: Game1.TargetKind.Station, friendly: true, stationSabm: DataStationTypeIGC.SabmStart)!.Value.CommandId);
        Assert.Equal(OrderChange.CidDefend,
            Resolve(Rescue, kind: Game1.TargetKind.Station, friendly: true, stationSabm: DataStationTypeIGC.SabmLand)!.Value.CommandId);
        Assert.Equal(OrderChange.CidGoto,
            Resolve(Rescue, kind: Game1.TargetKind.Station, friendly: true, stationSabm: DataStationTypeIGC.SabmCapLand)!.Value.CommandId);
        // A miner/builder pilot at a station stays at native's initial Goto
        // (shipIGC.h:2076, 2080-2090): the pilot-type branch never reaches the
        // wingman station ladder.
        Assert.Equal(OrderChange.CidGoto,
            Resolve(Builder, kind: Game1.TargetKind.Station, friendly: true, stationSabm: DataStationTypeIGC.SabmLand)!.Value.CommandId);
        Assert.Equal(OrderChange.CidGoto,
            Resolve(Miner, kind: Game1.TargetKind.Station, friendly: true, stationSabm: DataStationTypeIGC.SabmCapLand)!.Value.CommandId);
        // A lifepod is a fighter hull too: the fighter/lifepod branch checks
        // Rescue|Land (shipIGC.h:2129-2133).
        Assert.Equal(OrderChange.CidGoto,
            Resolve((short)(Fighter | Lifepod), kind: Game1.TargetKind.Station, friendly: true, stationSabm: DataStationTypeIGC.SabmRescue)!.Value.CommandId);
        Assert.Equal(OrderChange.CidGoto,
            Resolve(Fighter, kind: Game1.TargetKind.Station, friendly: true, stationSabm: DataStationTypeIGC.SabmLand)!.Value.CommandId);
        // A fighter at a CapLand-only station: the fighter branch takes the Land
        // ladder, not CapLand (native elif chain).
        Assert.Equal(OrderChange.CidDefend,
            Resolve(Fighter, kind: Game1.TargetKind.Station, friendly: true, stationSabm: DataStationTypeIGC.SabmCapLand)!.Value.CommandId);
    }

    [Fact]
    public void Friendly_rescue_probe_orders_goto_for_a_lifepod_commandee()
    {
        // shipIGC.h:2137-2146: own lifepod + probe Rescue|RescueAny → Goto;
        // any other friendly probe stays Defend.
        Assert.Equal(OrderChange.CidGoto,
            Resolve(Lifepod, kind: Game1.TargetKind.Probe, friendly: true, probeEabm: DataStationTypeIGC.SabmRescue)!.Value.CommandId);
        Assert.Equal(OrderChange.CidGoto,
            Resolve(Lifepod, kind: Game1.TargetKind.Probe, friendly: true, probeEabm: DataStationTypeIGC.SabmRescueAny)!.Value.CommandId);
        Assert.Equal(OrderChange.CidDefend,
            Resolve(Fighter, kind: Game1.TargetKind.Probe, friendly: true, probeEabm: DataStationTypeIGC.SabmRescue)!.Value.CommandId);
    }

    [Fact]
    public void Treasure_pickup_and_enemy_probe_attack()
    {
        // shipIGC.h:2147-2159: treasure → Pickup; probe → Attack, except a
        // lifepod against RescueAny → Goto; sided models → Attack.
        Assert.Equal(OrderChange.CidPickup, Resolve(Fighter, kind: Game1.TargetKind.Treasure)!.Value.CommandId);
        Assert.Equal(OrderChange.CidAttack, Resolve(Fighter, kind: Game1.TargetKind.Probe, friendly: false, probeEabm: 0)!.Value.CommandId);
        Assert.Equal(OrderChange.CidGoto,
            Resolve(Lifepod, kind: Game1.TargetKind.Probe, friendly: false, probeEabm: DataStationTypeIGC.SabmRescueAny)!.Value.CommandId);
        Assert.Equal(OrderChange.CidAttack, Resolve(Fighter, kind: Game1.TargetKind.Station, friendly: false)!.Value.CommandId);
        // The enemy treasure branch is sideless: a treasure stays Pickup even
        // "between" enemies (native does not consult sides for treasure).
        Assert.Equal(OrderChange.CidPickup, Resolve(Fighter, kind: Game1.TargetKind.Treasure, friendly: false)!.Value.CommandId);
    }
}
