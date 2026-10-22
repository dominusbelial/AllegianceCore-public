using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P779b-1 — the medevac DROP gate (design, not parity) through the real
/// <c>MaybeUseDispensers</c> controller: (a) no own-side station in the sector,
/// (b) ≥<see cref="BotManager.MedevacMinOwnPods"/> own pods there OR
/// ≥<see cref="BotManager.MedevacMinRecentEjects"/> first-seen there in 60 s,
/// (c) no own-side rescue-capable probe within
/// <see cref="BotManager.MedevacSpacing"/>, (d) the 8 s/load-time pacing.
/// Each failing gate must produce no FIRE_EXPENDABLE(82); when all hold exactly
/// one is sent. The bot's world comes from wire-applied exports (no TCP).
/// </summary>
public sealed partial class BotFixRegressionTests
{
    private static (object Bot, ConnectionSession Cs) MedevacScene()
    {
        var cs = Session();
        var bot = Bot(cs);
        Seat(cs.World);                                  // own ship 1: side 0, sector 1, pos 0
        SC4Inventory(cs, new ExpandedPartData(19, 3, 0, 240));   // real type-19 Rescue launcher
        Set(bot, "Role", "scout");
        Set(bot, "MedevacScout", true);
        Set(bot, "CanBuyRescueProbe", true);
        return (bot, cs);
    }

    private static int Drops(object bot) => Get<int>(bot, "RescueProbesDropped");

    private static short PodHull() => Catalog.RepresentativeCore().HullTypes.Values
        .First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod)).HullId;

    private static void Pod(ConnectionSession cs, short id, float x)
    {
        // A remote pod: identity + status sector + in-sector pose (a SET_CLUSTER
        // would retire every other remote ship, as native does).
        cs.World.Apply(new ExportShip { ShipId = id, HullId = PodHull(), SideId = 0, CharacterName = $"pod{id}" });
        cs.World.Apply(new ShipStatusMessage { ShipId = id, HullId = PodHull(), SectorId = 1, StationId = -1 });
        cs.World.Apply(new SingleShipUpdate { OidTarget = id, Ship = Pose(id, x) });
    }

    private static void LeaveSector(ConnectionSession cs, short id)
        => cs.World.Apply(new ShipDelete { ShipId = id, Sdr = ShipDelete.SdrLeftSector });

    private static void RescueProbe(ConnectionSession cs, short id, short typeId, float x)
        => cs.World.Apply(new ExportProbe
        {
            Probe = new() { ProbeId = id, ProbetypeId = typeId, SideId = 0, ClusterId = 1, P0 = new Vector3(x, 0, 0) },
        });

    [SkippableFact]
    public void Gate_a_own_station_in_the_sector_blocks_the_drop()
    {
        Allegiance.TestInfra.Require.Artwork();
        var (bot, cs) = MedevacScene();
        Pod(cs, 20, 100); Pod(cs, 21, 200);
        cs.World.Apply(new ExportStation { Station = Station(100, 0, 1) });   // own side, this sector
        Call("MaybeUseDispensers", null, bot, cs, 10.0);
        Assert.Equal(0, Drops(bot));
        Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastRescueProbeAt"));
    }

    [SkippableFact]
    public void Gate_b_blocks_with_one_pod_and_no_recent_eject_signal()
    {
        Allegiance.TestInfra.Require.Artwork();
        var (bot, cs) = MedevacScene();
        Pod(cs, 20, 100);                                  // 1 pod, first seen now: ejects60 = 1
        Call("MaybeUseDispensers", null, bot, cs, 10.0);
        Assert.Equal(0, Drops(bot));
        Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastRescueProbeAt"));
    }

    [SkippableFact]
    public void Gate_b_recent_ejects_can_substitute_for_live_pods_then_all_true_drops_once()
    {
        Allegiance.TestInfra.Require.Artwork();
        var (bot, cs) = MedevacScene();
        // Three own pods first seen at t=10; an own rescue probe covers the spot,
        // so the observation runs but gate (c) blocks the drop.
        Pod(cs, 20, 100); Pod(cs, 21, 200); Pod(cs, 22, 300);
        RescueProbe(cs, 90, 19, 50);
        Call("MaybeUseDispensers", null, bot, cs, 10.0);
        Assert.Equal(0, Drops(bot));
        // The probe is destroyed and two pods leave the sector: 1 live pod but
        // ejects60 = 3 within the 60 s window -> all gates hold -> exactly one.
        cs.World.Apply(new ProbeDestroyed { ProbeId = 90 });
        LeaveSector(cs, 21); LeaveSector(cs, 22);
        Call("MaybeUseDispensers", null, bot, cs, 11.0);
        Assert.Equal(1, Drops(bot));
        Assert.Equal(11.0, Get<double>(bot, "LastRescueProbeAt"));
        // Pacing (d) blocks the immediate repeat.
        Call("MaybeUseDispensers", null, bot, cs, 11.5);
        Assert.Equal(1, Drops(bot));
        // Past the load-time floor, with a second live pod again, it may drop again.
        Pod(cs, 23, 400);
        Call("MaybeUseDispensers", null, bot, cs, 111.0);
        Assert.Equal(2, Drops(bot));
    }

    [SkippableFact]
    public void Gate_c_own_rescue_probe_within_spacing_blocks_the_drop()
    {
        Allegiance.TestInfra.Require.Artwork();
        var (bot, cs) = MedevacScene();
        Pod(cs, 20, 100); Pod(cs, 21, 200);
        RescueProbe(cs, 90, 19, 1490);                     // inside MedevacSpacing (1500)
        Call("MaybeUseDispensers", null, bot, cs, 10.0);
        Assert.Equal(0, Drops(bot));

        // A scanner probe never counts as coverage, and the rescue probe now
        // sits OUTSIDE the spacing: the drop is allowed.
        cs.World.Apply(new ProbeDestroyed { ProbeId = 90 });
        RescueProbe(cs, 91, 22, 10);                       // scanner, eabm 0
        RescueProbe(cs, 92, 19, 1501);                     // just outside
        Call("MaybeUseDispensers", null, bot, cs, 10.0);
        Assert.Equal(1, Drops(bot));
    }

    [SkippableFact]
    public void Gate_d_pacing_blocks_until_max_8s_loadtime()
    {
        Allegiance.TestInfra.Require.Artwork();
        var (bot, cs) = MedevacScene();
        Pod(cs, 20, 100); Pod(cs, 21, 200);
        Set(bot, "LastRescueProbeAt", 10.0);
        Call("MaybeUseDispensers", null, bot, cs, 17.9);
        Assert.Equal(0, Drops(bot));
        Call("MaybeUseDispensers", null, bot, cs, 18.0);   // 8 s floor reached
        Assert.Equal(1, Drops(bot));
        Assert.Equal(18.0, Get<double>(bot, "LastRescueProbeAt"));
    }

    [SkippableFact]
    public void All_gates_true_sends_exactly_one_fire_expendable()
    {
        Allegiance.TestInfra.Require.Artwork();
        var (bot, cs) = MedevacScene();
        Pod(cs, 20, 100); Pod(cs, 21, 200);
        Call("MaybeUseDispensers", null, bot, cs, 10.0);
        Assert.Equal(1, Drops(bot));
        Call("MaybeUseDispensers", null, bot, cs, 10.1);
        Assert.Equal(1, Drops(bot));
    }

    [SkippableFact]
    public void A_non_medevac_scout_never_drops_its_rescue_launcher()
    {
        Allegiance.TestInfra.Require.Artwork();
        var (bot, cs) = MedevacScene();
        Set(bot, "MedevacScout", false);                    // e.g. a stranger's launcher
        Pod(cs, 20, 100); Pod(cs, 21, 200);
        Call("MaybeUseDispensers", null, bot, cs, 10.0);
        Assert.Equal(0, Drops(bot));
    }
}
