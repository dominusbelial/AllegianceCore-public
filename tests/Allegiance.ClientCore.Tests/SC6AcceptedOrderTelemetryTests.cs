using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class SC6AcceptedOrderTelemetryTests
{
    [SkippableFact]
    public void Player_orders_count_once_on_acceptance_and_never_on_queue_or_rejection()
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P733ResearchTests.Config(16));
        using var a = P660NextMatchLiveTests.Connect(server, "SC6-a", 86001);
        using var b = P660NextMatchLiveTests.Connect(server, "SC6-b", 86002);
        using var c = P660NextMatchLiveTests.Connect(server, "SC6-c", 86003);
        var peers = new[] { a, b, c };
        var pair = peers.GroupBy(p => p.World.MySideId).Single(g => g.Count() == 2).ToArray();
        var sender = pair[0]; var recipient = pair[1];
        short side = sender.World.MySideId!.Value, id = recipient.World.MyShipId!.Value;
        var enemy = peers.Single(p => p.World.MySideId != side);
        var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
        foreach (var (cid, skill, type, target) in new[]
        {
            (OrderChange.CidAttack, ShowcaseSkill.OrderAttack, IgcObjectType.Ship, enemy.World.MyShipId!.Value),
            (OrderChange.CidRepair, ShowcaseSkill.OrderRepair, IgcObjectType.Station,
                mission.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == side).StationId)
        })
        {
            void Send() => sender.SendChat("", ChatTarget.Individual, oidRecipient: id, commandId: cid, otTarget: (short)type, oidTarget: target);
            bool Pending() => P660NextMatchLiveTests.Read(recipient, () => recipient.World.MyQueuedCommand?.CommandId == cid);
            Send(); P660NextMatchLiveTests.Pump(server, Pending);
            Assert.Equal(0, mission.Skills!.Count(side, skill));
            recipient.SendOrderChange(id, OrderChange.CidNone, OrderChange.CmdQueued, type, target);
            P660NextMatchLiveTests.Pump(server, () => !Pending());
            Assert.Equal(0, mission.Skills.Count(side, skill));
            Send(); P660NextMatchLiveTests.Pump(server, Pending);
            recipient.SendOrderChange(id, cid, OrderChange.CmdQueued, type, target);
            P660NextMatchLiveTests.Pump(server, () => !Pending());
            Assert.Equal(1, mission.Skills.Count(side, skill));
            recipient.SendOrderChange(id, cid, OrderChange.CmdQueued, type, target);
            string barrier = $"SC6 duplicate acceptance barrier {cid}";
            recipient.SendChat(barrier, ChatTarget.Team);
            P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(sender,
                () => sender.World.Events.Chats.Any(e => e.Message == barrier)));
            Assert.Equal(1, mission.Skills.Count(side, skill));
        }
    }
}
