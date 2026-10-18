using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class P709ClientCallbacksTests
{
    internal static ClientWorld World()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Allegiance.Core.Collision.Vector3.Zero,
            Orientation.Identity, Allegiance.Core.Collision.Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        world.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "Donor" });
        world.Apply(new ExportShip { ShipId = 11, SideId = 0, CharacterName = "Requester" });
        world.Apply(new SetMoney { ShipId = 10, Money = 80 });
        return world;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Plain_text_preserves_literal_formats_and_formatted_text_is_bounded(bool critical)
    {
        var world = World(); world.NowSeconds = () => 12;
        string plain = new('x', 300); plain += "{0} %s";
        world.PostPlainText(critical, plain);
        world.PostText(critical, "{0}: {1}", "Hull", new string('y', 300));
        var lines = world.TakeFeedbackLines();
        Assert.Equal(plain, lines[0].Text); Assert.Equal(255, lines[1].Text.Length);
        Assert.All(lines, l => { Assert.Equal(critical, l.IsCritical); Assert.Equal(12, l.AtSeconds); });
        Assert.Empty(world.Events.Chats); Assert.Empty(world.TakeFeedbackLines());
    }

    [Fact]
    public void Notification_text_belongs_to_the_local_ship_and_never_to_its_source_carrier()
    {
        var world = World();
        world.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, ParentId = 11, StationId = -1 });
        world.PostNotificationText(11, true, "carrier {0}", 1);
        world.PostNotificationText(12, true, "observer");
        world.PostNotificationText(10, false, "{0} ready.", "Part");
        Assert.Equal("Part ready.", Assert.Single(world.TakeFeedbackLines()).Text);
    }

    [Fact]
    public void Incoming_notification_uses_the_same_HUD_queue_once()
    {
        var world = World();
        world.Apply(new ChatMessage { SidSender = -1, ChatTarget = (int)ChatTarget.Individual,
            OidRecipient = 10, VoiceOver = NotificationCue.Critical, CommandID = -1, Message = "Warning {0} %s" });
        Assert.Equal("Warning {0} %s", Assert.Single(world.TakeFeedbackLines()).Text);
        Assert.Single(world.Events.Chats); Assert.Empty(world.TakeFeedbackLines());
    }

    [Fact]
    public void Chat_and_buoy_callbacks_fire_once_after_the_event_log_is_updated()
    {
        var world = World(); int calls = 0;
        world.ChatReceived += () => { calls++; Assert.Equal(calls, world.Events.Chats.Count + world.Events.ChatBuoys.Count); };
        world.Apply(new ChatMessage { Message = "chat" });
        world.Apply(new ChatBuoy { Message = "buoy" });
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Chat_callback_observes_sound_and_notification_once_on_wire_and_direct_delivery(bool direct)
    {
        var world = World();
        world.TakeSoundEvents();
        int calls = 0;
        world.ChatReceived += () =>
        {
            calls++;
            Assert.Equal(calls, world.Events.Chats.Count);
            if (calls == 1)
            {
                Assert.Equal(ClientSoundEventKind.ChatPersonal, Assert.Single(world.TakeSoundEvents()).Kind);
                Assert.Empty(world.TakeFeedbackLines());
            }
            else
            {
                var cue = Assert.Single(world.TakeSoundEvents());
                Assert.Equal(ClientSoundEventKind.ChatVoiceOver, cue.Kind);
                Assert.Equal("criticalmessage", cue.Voice!.Stem);
                var line = Assert.Single(world.TakeFeedbackLines());
                Assert.Equal("Warning {0} %s", line.Text);
                Assert.True(line.IsCritical);
                Assert.True(line.SoundAlreadyQueued);
            }
        };
        var chat = new ChatMessage { SidSender = 11, ChatTarget = (int)ChatTarget.Individual,
            OidRecipient = 10, VoiceOver = -1, CommandID = -1, Message = "Hello" };
        var notification = new ChatMessage { SidSender = -1, ChatTarget = (int)ChatTarget.Individual,
            OidRecipient = 10, VoiceOver = NotificationCue.Critical, CommandID = -1, Message = "Warning {0} %s" };
        if (direct) { world.ReceiveChat(chat); world.ReceiveChat(notification); }
        else { world.Apply(chat); world.Apply(notification); }
        Assert.Equal(2, calls);
        Assert.Empty(world.TakeSoundEvents());
        Assert.Empty(world.TakeFeedbackLines());
    }

    // ~/Development/Allegiance/src/WinTrek/trekigc.cpp:4492,4507-4540,4788-4822:
    // muted messages do not reach listeners; accepted chat has its caption and
    // voice ready before the insertion callback, through either entry point.
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void Chat_callback_preserves_mute_caption_and_voice_for_chat_and_buoys(bool direct, bool buoy)
    {
        var world = World();
        world.TakeSoundEvents();
        int calls = 0;
        world.ChatReceived += () =>
        {
            calls++;
            string caption = buoy ? Assert.Single(world.Events.ChatBuoys).Message : Assert.Single(world.Events.Chats).Message;
            Assert.Equal("Attack my target!", caption);
            var cue = Assert.Single(world.TakeSoundEvents());
            Assert.Equal(ClientSoundEventKind.ChatVoiceOver, cue.Kind);
            Assert.Equal(10, cue.Id);
            Assert.Equal("vo_player_attacktarget", cue.Voice!.Stem);
        };
        void Receive()
        {
            var chat = new ChatMessage { SidSender = 11, VoiceOver = 10, CommandID = -1,
                ChatTarget = (int)ChatTarget.Team, MessageIsAbsent = true };
            if (buoy)
            {
                var message = ChatBuoy.Decode(new ChatBuoy { Cd = chat.Cd, MessageIsAbsent = true }.Encode());
                if (direct) world.ReceiveChat(message); else world.Apply(message);
            }
            else
            {
                chat = ChatMessage.Decode(chat.Encode());
                if (direct) world.ReceiveChat(chat); else world.Apply(chat);
            }
        }
        world.SetPilotMuted(11, true);
        Receive();
        Assert.Equal(0, calls);
        Assert.Empty(world.Events.Chats);
        Assert.Empty(world.Events.ChatBuoys);
        Assert.Empty(world.TakeSoundEvents());
        world.SetPilotMuted(11, false);
        Receive();
        Assert.Equal(1, calls);
        Assert.Empty(world.TakeSoundEvents());
        Assert.Empty(world.TakeFeedbackLines());
    }

    [Fact]
    public void Money_request_replaces_queue_expires_strictly_and_cannot_change_wallets_on_receipt()
    {
        var world = World(); double now = 20; world.NowSeconds = () => now;
        world.Apply(new QueuedCommandUpdate { CommandId = OrderChange.CidGoto, TargetId = 11, TargetType = IgcObjectType.Ship });
        world.Apply(new RequestMoney { ShipIdRequest = 11, Amount = 100, HidFor = 7 });
        Assert.Null(world.MyQueuedCommand); Assert.Equal(80, world.MyMoney);
        Assert.Contains("Requester has requested $100", Assert.Single(world.TakeFeedbackLines()).Text);
        now = 35; Assert.NotNull(world.PendingMoneyRequest);
        now = 35.001; Assert.Null(world.PendingMoneyRequest); Assert.Null(world.AcceptMoneyRequest());
        Assert.Equal(80, world.MyMoney);
    }

    [Fact]
    public void Approval_clamps_to_current_wallet_once_and_reject_reset_and_new_order_clear_pending_request()
    {
        var world = World(); world.NowSeconds = () => 10;
        world.GetMoneyRequest(11, 100, 1);
        var gift = Assert.IsType<PlayerDonate>(world.AcceptMoneyRequest());
        Assert.Equal(80, gift.MoneyGiven); Assert.Equal(11, gift.ShipID);
        Assert.Null(world.AcceptMoneyRequest()); Assert.Equal(80, world.MyMoney);
        world.GetMoneyRequest(11, 1, 1); world.RejectMoneyRequest(); Assert.Null(world.PendingMoneyRequest);
        world.GetMoneyRequest(11, 1, 1);
        world.Apply(new QueuedCommandUpdate { CommandId = OrderChange.CidGoto, TargetId = 11, TargetType = IgcObjectType.Ship });
        Assert.Null(world.PendingMoneyRequest);
        world.GetMoneyRequest(11, 1, 1); world.Reset(); Assert.Null(world.PendingMoneyRequest);
    }

    [Fact]
    public void Unknown_request_sender_is_ignored_and_self_request_never_donates()
    {
        var world = World(); world.GetMoneyRequest(99, 100, 1);
        Assert.Null(world.PendingMoneyRequest); Assert.Empty(world.FeedbackLines);
        world.GetMoneyRequest(10, 100, 1); Assert.Null(world.AcceptMoneyRequest()); Assert.Empty(world.FeedbackLines);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Kill_and_eject_refresh_game_state_after_mutation_without_ending_the_mission(bool killed)
    {
        var world = World(); int calls = 0;
        world.GameStateChanged += () => { calls++; Assert.Equal(killed, world.Ships[11].IsDead); Assert.Single(world.Events.Kills); };
        world.Apply(new KillShip { ShipId = 11, IdCredit = 10, BDeathCredit = killed });
        Assert.Equal(1, calls); Assert.Equal(1, world.GameStateRevision);
    }
}
