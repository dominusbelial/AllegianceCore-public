using Allegiance.Core;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    [SkippableFact]
    public void P682b_restored_bombers_and_escorts_can_join_the_next_finale_wing()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(Bombers: 1, EscortShare: 1, EscortCap: 1), 3);
        rig.Mature();
        var bomber = rig.Bots[0];
        var fighter = rig.Bots[2];
        var originals = new[] { bomber, fighter }.Select(b => (Bot: b, Hull: Get<short>(b, "HullId"),
            Role: Get<string>(b, "Role"), Target: Get<short?>(b, "TargetSector"))).ToArray();
        for (int match = 1; match <= 2; match++)
        {
            foreach (var bot in new[] { bomber, fighter })
            {
                Set(bot, "PodDocked", true);
                Call("PrepareFinaleRelaunch", null, bot, rig.Started + 1801);
            }
            Assert.True(Get<bool>(bomber, "FinaleBomberActive"));
            Assert.True(Get<bool>(fighter, "FinaleEscort"));
            foreach (var original in originals)
            {
                Call("RestoreFinaleForNextMission", null, original.Bot, rig.Started + 1810, null);
                Assert.Equal(original.Hull, Get<short>(original.Bot, "HullId"));
                Assert.Equal(original.Role, Get<string>(original.Bot, "Role"));
                Assert.Equal(original.Target, Get<short?>(original.Bot, "TargetSector"));
                Assert.Null(Get<object?>(original.Bot, "FinaleOriginal"));
                Assert.False(Get<bool>(original.Bot, "FinaleBomberActive"));
                Assert.False(Get<bool>(original.Bot, "FinaleEscort"));
                Assert.Equal(0, Get<int>(original.Bot, "CompletedPodCycles"));
            }
            if (match == 2) break;
            // Registration uses fresh station observations. A later wing must
            // save its own restoration record rather than retain the old one.
            var fresh = rig.Stations.Select(s => s with { StationId = (short)(s.StationId + 1000) }).ToArray();
            rig.Manager.RegisterFinale(new(Bombers: 1, EscortShare: 1, EscortCap: 1),
                rig.Bots.Select(b => Get<string>(b, "Name")).ToArray(), fresh);
            var run = Get<object>(bomber, "Finale");
            Set(run, "TriggeredAt", (double?)rig.Started);
            Set(run, "Sides", new short[] { 0 });
            Set(bomber, "FinaleBomberReserved", true);
            var plans = (System.Collections.IDictionary)Get<object>(run, "Plans");
            Set(plans[(short)0]!, "EscortBudget", 1);
        }
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void P682b_initial_launch_is_consumed_once_even_with_partial_grant_or_immediate_ejection(bool partial, bool ejected)
    {
        Require.Artwork();
        using var cs = Session();
        var bot = Bot(cs);
        cs.World.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        DefenseMap(cs.World);
        Set(bot, "MissionLaunchPending", true);
        Set(bot, "RelaunchAttempts", 1);
        Set(bot, "RelaunchAt", 0.0);
        cs.World.Apply(new DockedNotify { StationId = 100 });
        var hull = Get<short>(bot, "HullId");
        void Receive(params IFedMessage[] messages) => ((ITransportSite)cs).OnReceive(ConnectionId.Server,
            FedPacket.Pack(messages.Select(m => m.Encode())));
        Receive(new BuyLoadoutAck { HullId = hull, FBoughtEverything = false, FLaunch = false, Parts = [] });
        Assert.True((bool)Call("LaunchNextMission", null, bot, cs, .5)!);
        Assert.True(Get<bool>(bot, "MissionLaunchPending"));
        Receive(new BuyLoadoutAck { HullId = hull, FBoughtEverything = !partial, FLaunch = true, Parts = [] });
        // An ACK alone cannot turn a hangar seat into a launched bot.
        Assert.True((bool)Call("LaunchNextMission", null, bot, cs, 1.0)!);
        Assert.True(Get<bool>(bot, "MissionLaunchPending"));
        Receive(new SetCluster { Ship = Pose(1), SectorId = 1 });
        short pod = Catalog.RepresentativeCore().HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod)).HullId;
        if (ejected)
        {
            // The receive thread can consume launch, eject and dock before the
            // next decision pass. The launch ACK still belongs to that first life.
            cs.World.Apply(new ExportShip { ShipId = 1, HullId = pod, SideId = 0 });
            cs.World.Apply(new DockedNotify { StationId = 100 });
        }
        Assert.False((bool)Call("LaunchNextMission", null, bot, cs, 2.0)!);
        Assert.False(Get<bool>(bot, "MissionLaunchPending"));
        Assert.Equal(0, Get<int>(bot, "CompletedPodCycles"));
        if (!ejected)
        {
            cs.World.Apply(new ExportShip { ShipId = 1, HullId = pod, SideId = 0 });
            cs.World.Apply(new DockedNotify { StationId = 100 });
        }
        Assert.True((bool)Call("UpdatePodState", null, bot, cs, 3.0)!);
        Assert.True(Get<bool>(bot, "IsPod"));
        Assert.True(Get<bool>(bot, "PodDocked"));
        Receive(new BuyLoadoutAck { HullId = hull, FBoughtEverything = true, FLaunch = true, Parts = [] },
            new SetCluster { Ship = Pose(1), SectorId = 1 });
        Assert.False((bool)Call("UpdatePodState", null, bot, cs, 4.0)!);
        Assert.Equal(1, Get<int>(bot, "CompletedPodCycles"));
        Assert.False((bool)Call("UpdatePodState", null, bot, cs, 5.0)!);
        Assert.Equal(1, Get<int>(bot, "CompletedPodCycles"));
    }
}
