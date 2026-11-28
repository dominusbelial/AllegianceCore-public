using System.Collections;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    private static object StrikePlan(FinaleRig rig) =>
        Get<IDictionary>(Get<object>(rig.Bots[0], "Finale"), "Plans")[(short)0]!;

    private static void Arrive(FinaleRig rig, int index, float distance = 800)
    {
        var bot = rig.Bots[index];
        var cs = rig.Sessions[index];
        short ship = Get<short>(bot, "ShipId");
        var station = rig.Stations[1];
        var pos = station.Position;
        cs.World.Apply(new ExportShip { ShipId = ship, HullId = 23, SideId = 0 });
        cs.World.Apply(new SetCluster { SectorId = station.ClusterId, Ship = ServerShipUpdate.FromValues(ship,
            0, default, 1, new(pos[0], pos[1], pos[2] + distance), Orientation.Identity, Vector3.Zero,
            [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        ExpandedPartData[] parts = [new(158, 2, 0, ExpandedPartData.FullyMounted)];
        Set(bot, "HullId", (short)23);
        Set(bot, "Parts", parts);
        Set(bot, "Granted", new BotManager.LoadoutGrant(23, parts));
        Set(bot, "FinaleGrantVerified", true);
        Set(bot, "FinaleBomberActive", true);
        Set(bot, "FinaleBomberReserved", true);
    }

    private static bool StrikeHold(FinaleRig rig, double t) =>
        (bool)Call("HoldFinaleStrike", rig.Manager, rig.Bots[0], rig.Started + t)!;

    private static void StrikeTick(FinaleRig rig, double t) => Call("FinaleStrikeMassTick", rig.Manager,
        Get<object>(rig.Bots[0], "Finale"), P961BotArray(rig.Bots), rig.Started + t);

    [SkippableTheory]
    [InlineData("{}")]
    [InlineData("{\"finale_weapon_envelope\":false}")]
    public void P1105b_Off_transcript_matches_base(string json)
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        var settings = McpTools.ReadFinaleSettings(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!, true);
        using var rig = new FinaleRig(settings with { Bombers = 2 }, perSide: 2);
        rig.Mature();
        var plan = StrikePlan(rig);
        Arrive(rig, 0);
        StrikeTick(rig, 1801);
        List<string> trace = [];
        void Record(double t)
        {
            bool held = StrikeHold(rig, t);
            var go = Get<double?>(plan, "GoTime") - rig.Started;
            var members = string.Join(',', Get<HashSet<short>>(plan, "GoMembers").Order());
            trace.Add(FormattableString.Invariant($"{t:F1}|{go:F1}|{members}|{held}|{Get<bool>(rig.Bots[0], "FinaleReleaseLogged")}|{Get<bool>(rig.Bots[0], "FinaleStrikeSpent")}"));
        }
        Record(1801);
        Arrive(rig, 1);
        StrikeTick(rig, 1802);
        Record(1802);
        Record(1806.9);
        Record(1807);
        Record(1808);
        // Clock | shared go | committed ids | hold owns tick | released | spent.
        Assert.Equal(new[] {
            "1801.0|||True|False|False",
            "1802.0|1807.0|1,2|True|False|False",
            "1806.9|1807.0|1,2|True|False|False",
            "1807.0|1807.0|1,2|False|True|True",
            "1808.0|1807.0|1,2|False|True|True" }, trace);
    }

    [SkippableFact]
    public void P1105b_Ready_mixed_wing_can_commit_before_the_rest_acquire()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(Bombers: 3, WeaponEnvelope: true), perSide: 3);
        rig.Mature();
        var plan = StrikePlan(rig);
        Set(plan, "TargetTypeId", (short)315);
        for (int i = 0; i < 3; ++i)
        {
            Arrive(rig, i);
            var world = rig.Sessions[i].World;
            var station = rig.Stations[1] with { StationTypeId = 315 };
            world.Apply(new ExportStation { Station = station });
            if (i == 2) continue;
            ExpandedPartData[] parts = [new(158, 4, 0, ExpandedPartData.FullyMounted)];
            Set(rig.Bots[i], "Granted", new BotManager.LoadoutGrant(23, parts));
            Call("HoldFinaleStrike", rig.Manager, rig.Bots[i], rig.Started + 1801);
            world.Apply(MissileLockUpdate.From(1, station.StationId, 1, Vector3.Zero, "ab", IgcObjectType.Station));
        }
        StrikeTick(rig, 1802);
        Assert.Equal(3, Get<int>(plan, "RequiredBombers"));
        Assert.Equal(rig.Started + 1802, Get<double?>(plan, "GoTime"));
        Assert.Equal(new short[] { 1, 2 }, Get<HashSet<short>>(plan, "GoMembers").Order());
        Assert.True(StrikeHold(rig, 1802));
        Assert.True(Get<bool>(rig.Bots[0], "FinaleReleaseLogged"));
        Assert.False(Get<bool>(rig.Bots[0], "FinaleStrikeSpent"));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void P1105b_Unready_arrivals_commit_and_release_on_deadline_or_timeout(bool deadline)
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(Bombers: 2, WeaponEnvelope: true), perSide: deadline ? 4 : 2);
        rig.Mature();
        var plan = StrikePlan(rig);
        // Outside the envelope gather radius, inside the legacy arrival band.
        Arrive(rig, 0, 5500);
        Arrive(rig, 1, 5600);
        Set(plan, "DeadlineAt", rig.Started + (deadline ? 1841 : 2400));
        StrikeTick(rig, 1801);
        double commit = deadline ? 1801 : 1809;
        if (deadline) Assert.True(Get<int>(plan, "RequiredBombers") > 2);
        else
        {
            StrikeTick(rig, 1808.99);
            Assert.Null(Get<double?>(plan, "GoTime"));
            StrikeTick(rig, commit);
        }
        double go = commit + 5;
        Assert.Equal(rig.Started + go, Get<double?>(plan, "GoTime"));
        Assert.Equal(new short[] { 1, 2 }, Get<HashSet<short>>(plan, "GoMembers").Order());
        Assert.True(StrikeHold(rig, go - .1));
        Assert.False(StrikeHold(rig, go));
        Assert.True(Get<bool>(rig.Bots[0], "FinaleReleaseLogged"));
        Assert.True(Get<bool>(rig.Bots[0], "FinaleStrikeSpent"));
    }

    [SkippableFact]
    public void P1105b_All_committed_pilots_missing_go_reform_as_a_new_wave()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(Bombers: 2, WeaponEnvelope: true), perSide: 2);
        rig.Mature();
        Arrive(rig, 0);
        Arrive(rig, 1);
        var plan = StrikePlan(rig);
        Set(plan, "GoTime", rig.Started + 1802);
        var members = Get<HashSet<short>>(plan, "GoMembers");
        members.UnionWith([1, 2]);
        Assert.True(StrikeHold(rig, 1802)); // neither pilot has owner confirmation
        StrikeTick(rig, 1822);
        Assert.Equal(2, members.Count);
        StrikeTick(rig, 1822.1);
        Assert.Empty(members);
        StrikeTick(rig, 1837);
        Assert.Null(Get<double?>(plan, "GoTime"));
        StrikeTick(rig, 1838);
        StrikeTick(rig, 1846);
        Assert.Equal(rig.Started + 1851, Get<double?>(plan, "GoTime"));
        Assert.Equal(new short[] { 1, 2 }, members.Order());
    }

    [SkippableTheory]
    [InlineData("empty magazine")]
    [InlineData("wrong grant")]
    [InlineData("unobserved station")]
    [InlineData("no grant")]
    [InlineData("missing pose")]
    [InlineData("dead")]
    [InlineData("docked")]
    [InlineData("wrong sector")]
    [InlineData("unknown hull")]
    [InlineData("unknown station type")]
    [InlineData("friendly station")]
    [InlineData("station sector")]
    [InlineData("station position")]
    [InlineData("wrong owner")]
    [InlineData("missing ship")]
    [InlineData("post-wave spent")]
    [InlineData("post-wave released")]
    [InlineData("accepted shot")]
    public void P1105b_Unusable_or_finished_approach_returns_tick_without_idle_control(string cause)
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(Bombers: 1, WeaponEnvelope: true), perSide: 2);
        rig.Mature();
        Arrive(rig, 0);
        var bot = rig.Bots[0];
        var world = rig.Sessions[0].World;
        var plan = StrikePlan(rig);
        Set(plan, "GoTime", rig.Started + 1801);
        var members = Get<HashSet<short>>(plan, "GoMembers");
        members.Add(1);
        Set(bot, "FinaleReleaseLogged", true);
        Set(bot, "CloakHeld", true);
        var station = rig.Stations[1];
        var ship = world.Ships[1];
        void ReleasedMissile() => world.Apply(new FireMissile { LauncherId = 1, Entries = [new(default, default, default, 991)] });
        switch (cause)
        {
            case "empty magazine": ReleasedMissile(); ReleasedMissile(); break;
            case "wrong grant": Set(bot, "Granted", new BotManager.LoadoutGrant(15, [new(158, 2, 0, 240)])); break;
            case "no grant": Set(bot, "Granted", new BotManager.LoadoutGrant(23, null!)); break;
            case "unobserved station": world.Apply(new StationDestroyed { StationId = station.StationId }); break;
            case "station sector": world.Apply(new ExportStation { Station = station with { ClusterId = 99 } }); break;
            case "friendly station": world.Apply(new ExportStation { Station = station with { SideId = 0 } }); break;
            case "station position": world.Apply(new ExportStation { Station = station with { Position = [] } }); break;
            case "unknown station type": world.Apply(new ExportStation { Station = station with { StationTypeId = -1 } }); break;
            case "unknown hull": world.Apply(new ExportShip { ShipId = 1, SideId = 0, HullId = 32000 }); Set(bot, "Granted", new BotManager.LoadoutGrant(32000, [])); break;
            case "missing pose": ship.GetType().GetProperty("Latest")!.SetValue(ship, null); break;
            case "dead": world.Apply(new ShipDelete { ShipId = 1 }); break;
            case "docked": world.Apply(new DockedNotify { StationId = 100 }); break;
            case "wrong sector": world.Apply(new SetCluster { SectorId = 1, Ship = Pose(1) }); break;
            case "wrong owner": Set(bot, "ShipId", (short)2); members.Add(2); break;
            case "missing ship": ((IDictionary)world.Ships).Remove((short)1); break;
            case "post-wave spent": members.Clear(); Set(bot, "FinaleStrikeSpent", true); break;
            case "post-wave released": members.Clear(); break;
            case "accepted shot": Set(bot, "AcceptedAtRelease", 0L); ReleasedMissile(); break;
        }
        var sender = P700Sender(bot);
        Assert.False(StrikeHold(rig, 1802));
        Assert.Empty(Get<IEnumerable>(sender, "_orderedQueue"));
        Assert.True(Get<bool>(bot, "CloakHeld"));
        if (cause == "accepted shot") Assert.True(Get<bool>(bot, "FinaleStrikeSpent"));
    }

    [SkippableFact]
    public void P1105b_Settled_visible_pilot_strafes_at_the_hold()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(Bombers: 1, WeaponEnvelope: true), perSide: 2);
        rig.Mature();
        Arrive(rig, 0, 788);
        var sender = P700Sender(rig.Bots[0]);
        Assert.True(StrikeHold(rig, 1801));
        int state = P700LastControls(sender).Payload.StateM;
        Assert.NotEqual(0, state & (ShipControlState.LeftButton | ShipControlState.RightButton));
        Assert.Equal(0, state & (ShipControlState.WeaponsMask | ShipControlState.CloakActive));
    }

    [SkippableFact]
    public void P1105b_Order_retry_keeps_bearing_and_stops_after_typed_acknowledgement()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(Bombers: 1, WeaponEnvelope: true), perSide: 2);
        rig.Mature();
        Arrive(rig, 0);
        var bot = rig.Bots[0];
        var world = rig.Sessions[0].World;
        var sender = P700Sender(bot);
        int Orders() => Get<IEnumerable>(sender, "_orderedQueue").Cast<object>().SelectMany(frame =>
            FedPacket.Unpack(Get<byte[]>(frame, "Frame").AsSpan(5, Get<int>(frame, "Length") - 5)))
            .Count(packet => FedMessageHeader.Read(packet).Fmid == OrderChange.Fmid);
        Set(bot, "TargetId", (short?)9);
        Set(bot, "OrderedTargetId", (short?)9);
        StrikeHold(rig, 1801);
        Assert.Null(Get<short?>(bot, "TargetId"));
        Assert.Null(Get<short?>(bot, "OrderedTargetId"));
        var bearing = Get<Vector3>(bot, "EnvelopeBearing");
        P700Move(world, 1, new(450, 0, 500));
        world.SetCurrentCommand(null);
        for (int i = 1; i < 20; ++i) StrikeHold(rig, 1801 + i * .1);
        Assert.Equal(1, Orders());
        StrikeHold(rig, 1803);
        Assert.Equal(2, Orders());
        // Local echo alone is insufficient. Retry even when it names the station.
        StrikeHold(rig, 1805);
        Assert.Equal(3, Orders());
        world.Apply(MissileLockUpdate.From(1, rig.Stations[1].StationId, 1, Vector3.Zero, "ab", IgcObjectType.Station));
        StrikeHold(rig, 1810);
        Assert.Equal(3, Orders());
        Assert.Equal(bearing, Get<Vector3>(bot, "EnvelopeBearing"));
    }
}
