using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P709CallbacksLiveTests
{
    private static void Barrier(Srv server, ConnectionSession peer)
    {
        peer.TakePingEchoes(); peer.SendPing();
        P660NextMatchLiveTests.Pump(server, () => peer.TakePingEchoes().Count > 0);
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void Site_chat_preserves_sender_command_target_and_routes_only_to_the_named_team(bool formatted)
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var a = P660NextMatchLiveTests.Connect(server, "P709-A", 70901);
        using var b = P660NextMatchLiveTests.Connect(server, "P709-B", 70902);
        var sender = server.Ships.Single(s => s.Ship.Id == a.World.MyShipId).Ship;
        Assert.NotEqual(sender.SideId, b.World.MySideId);
        var site = sender.Mission!.Site;
        if (formatted) site.SendChatf(sender, ChatTarget.Team, sender.SideId!.Value, 41, "P709 {0} ${1}", "chat", 123);
        else site.SendChat(sender, ChatTarget.Team, sender.SideId!.Value, 41, "P709 chat $123",
            OrderChange.CidGoto, IgcObjectType.Ship, sender.Id, sender, true);
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(a,
            () => a.World.Events.Chats.Any(c => c.Message == "P709 chat $123")));
        Barrier(server, b);
        lock (a.Gate)
        {
            var message = Assert.Single(a.World.Events.Chats, c => c.Message == "P709 chat $123");
            Assert.Equal(sender.Id, message.SidSender); Assert.Equal(41, message.VoiceOver);
            Assert.Equal(formatted ? OrderChange.CidNone : OrderChange.CidGoto, message.CommandID);
            Assert.Equal(formatted ? -1 : sender.Id, message.OidTarget);
            Assert.Equal(!formatted, message.BObjectModel);
        }
        Assert.True(P660NextMatchLiveTests.Read(b, () => b.World.Events.Chats.All(c => c.Message != "P709 chat $123")));
    }

    [SkippableFact]
    public void Formatted_client_chat_reaches_peer_with_no_accidental_default_command()
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var a = P660NextMatchLiveTests.Connect(server, "P709-A", 70911);
        using var b = P660NextMatchLiveTests.Connect(server, "P709-B", 70912);
        a.SendChatf(ChatTarget.Everyone, -1, -1, "P709 {0:F2}", 1.25);
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(b,
            () => b.World.Events.Chats.Any(c => c.Message == "P709 1.25")));
        lock (b.Gate) Assert.Equal(OrderChange.CidNone, b.World.Events.Chats.Single(c => c.Message == "P709 1.25").CommandID);
    }

    [SkippableFact]
    public void Money_request_arrives_without_payment_then_approval_uses_real_player_donate()
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var a = P660NextMatchLiveTests.Connect(server, "P709-donor", 70921);
        using var b = P660NextMatchLiveTests.Connect(server, "P709-requester", 70922);
        var donor = server.Ships.Single(s => s.Ship.Id == a.World.MyShipId);
        var requester = server.Ships.Single(s => s.Ship.Id == b.World.MyShipId);
        donor.Ship.Mission!.Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(requester.Ship, donor.Ship.SideId!.Value));
        donor.Ship.Mission.ApplyStartingMoneyAndInitialPayday(donor.Ship.Mission.LastUpdate);
        // Synchronize both join identity and the real starting payday.
        Barrier(server, a); Barrier(server, b);
        int beforeA = donor.Wallet.Money, beforeB = requester.Wallet.Money;
        Assert.True(beforeA >= 100);
        b.SendMoneyRequest(100, donor.Ship.HullType.HullId);
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(a,
            () => a.World.PendingMoneyRequest is not null));
        Assert.Equal(beforeA, donor.Wallet.Money); Assert.Equal(beforeB, requester.Wallet.Money);
        PlayerDonate donation;
        lock (a.Gate)
        {
            donation = Assert.IsType<PlayerDonate>(a.World.AcceptMoneyRequest());
            Assert.Equal(requester.Ship.Id, donation.ShipID); Assert.Equal(100, donation.MoneyGiven);
        }
        a.SendPlayerDonation(donation);
        P660NextMatchLiveTests.Pump(server, () => donor.Wallet.Money == beforeA - 100 && requester.Wallet.Money == beforeB + 100);
        Assert.True(P660NextMatchLiveTests.Read(a, () => a.World.PendingMoneyRequest is null));
    }
}
