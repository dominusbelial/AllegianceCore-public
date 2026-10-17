using Allegiance.ClientCore.State;
using Allegiance.Core.Audio;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class P732VoiceOverTests
{
    private static ChatMessage Chat(short voice, string text, short sender = 42) => new()
    {
        Message = text, VoiceOver = voice, SidSender = sender, CommandID = OrderChange.CidAttack,
        ChatTarget = (int)ChatTarget.Individual, OidRecipient = 7,
    };

    [Theory]
    [InlineData(1131, "Donate credits", "vo_player_donatecredits")]
    [InlineData(1328, "New miner requesting He3 asteroid.", "vo_request_miner")]
    [InlineData(820, "Base under attack", "vo_attack_garrison")]
    [InlineData(1178, "Affirmative.", "vo_player_affirmative")]
    public void Wire_direct_id_selects_wave_before_command_and_personal_cues(short id, string text, string wave)
    {
        var world = new ClientWorld();
        world.Apply(ChatMessage.Decode(Chat(id, text).Encode()));
        var sound = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.ChatVoiceOver, sound.Kind);
        Assert.Equal(id, sound.Id);
        Assert.Equal(wave, sound.Voice!.Stem);
        Assert.Empty(world.TakeSoundEvents());
    }

    // The P725 AI producers and P732 receiver must use the same native IDs.
    // ~/Development/Allegiance/src/Igc/igc.h:456-464 starts sounds.h at 1001.
    [Theory]
    [InlineData(DroneChatterCue.DroneEnemyOnScopeSound, "vo_miner_enemyonscope")]
    [InlineData(DroneChatterCue.DroneTooMuchDamageSound, "vo_miner_dontgetpaid")]
    [InlineData(DroneChatterCue.DroneWhereToSound, "vo_request_miner")]
    [InlineData(DroneChatterCue.DroneComingHomeEmptySound, "vo_sal_minerpartial")]
    [InlineData(DroneChatterCue.DroneWhereToLayMinefieldSound, "vo_request_minefield")]
    [InlineData(DroneChatterCue.DroneWhereToLayTowerSound, "vo_request_tower")]
    [InlineData(DroneChatterCue.DroneInTransitSound, "vo_miner_intransit")]
    [InlineData(DroneChatterCue.DroneTooCrowdedSound, "vo_request_miner")]
    [InlineData(DroneChatterCue.ConstructorRunningSound, "vo_cover_builder")]
    public void Drone_producer_ids_reach_the_native_voice_receiver_once(short id, string wave)
    {
        var world = new ClientWorld();
        var chat = Chat(id, "Drone status");
        chat.CommandID = OrderChange.CidNone;
        chat.ChatTarget = (int)ChatTarget.Team;
        world.Apply(ChatMessage.Decode(chat.Encode()));
        var sound = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.ChatVoiceOver, sound.Kind);
        Assert.Equal(wave, sound.Voice!.Stem);
        Assert.Equal(wave, DroneChatterCue.Resolve(id));
        Assert.Empty(world.TakeSoundEvents());
    }

    [Theory]
    [InlineData(0, "Goto aleph", "vo_player_meetatjump")]
    [InlineData(10, "Attack my target!", "vo_player_attacktarget")]
    [InlineData(28, "Donate me your credits.", "vo_player_donatecredits")]
    public void Sonic_indices_with_empty_or_legacy_caption_text_select_the_same_template(short id, string caption, string wave)
    {
        foreach (var text in new[] { "", caption })
        {
            var world = new ClientWorld();
            var chat = Chat(id, text);
            chat.MessageIsAbsent = text.Length == 0;
            world.Apply(ChatMessage.Decode(chat.Encode()));
            Assert.Equal(wave, Assert.Single(world.TakeSoundEvents()).Voice!.Stem);
            Assert.Equal(caption, Assert.Single(world.Events.Chats).Message);
        }
    }

    [Fact]
    public void Unknown_voice_and_unrelated_text_do_not_fall_back_to_command_or_index_zero()
    {
        var world = new ClientWorld();
        world.Apply(Chat(30000, "unknown"));
        world.Apply(Chat(0, "ordinary typed chat"));
        Assert.Empty(world.TakeSoundEvents());
        Assert.Equal(2, world.Events.Chats.Count);
    }

    [Fact]
    public void Na_preserves_plain_command_cue()
    {
        var world = new ClientWorld();
        world.Apply(Chat(-1, "ordinary order"));
        Assert.Equal(ClientSoundEventKind.ChatCommand, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void Notification_has_one_audio_event_and_retains_hud_feedback()
    {
        var world = new ClientWorld();
        world.Apply(new Allegiance.Core.Wire.ExportShip { ShipId = 7 });
        // MyShipId comes from SetCluster; classifier accepts NA recipient for
        // unseated clients, but HUD feedback must name the actual own ship.
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromValues(7, 0, new ControlData(0, 0, 0, 0), 1,
                Allegiance.Core.Collision.Vector3.Zero, Orientation.Identity,
                Allegiance.Core.Collision.Vector3.Zero, new float[3], 1, 1, 1, 1, 1, 0, 1),
        });
        world.TakeSoundEvents();
        world.Apply(Chat(NotificationCue.Critical, "Probe detected!", sender: -1));
        Assert.Equal("criticalmessage", Assert.Single(world.TakeSoundEvents()).Voice!.Stem);
        var feedback = Assert.Single(world.TakeFeedbackLines());
        Assert.True(feedback.SoundAlreadyQueued);
        Assert.True(feedback.IsCritical);
    }

    [Fact]
    public void Own_plain_echo_is_silent_and_own_sonic_echo_voices_once()
    {
        Assert.Null(ClientWorld.ClassifyChatSound(Chat(1131, "Donate credits", sender: 7), 7, false));
        Assert.Equal(ClientSoundEventKind.ChatVoiceOver,
            ClientWorld.ClassifyChatSound(Chat(10, "Attack my target!", sender: 7), 7, false));
    }

    [Fact]
    public void Chatbuoy_uses_the_same_voice_template()
    {
        var world = new ClientWorld();
        var chat = Chat(1178, "Affirmative.");
        world.Apply(ChatBuoy.Decode(new ChatBuoy { Message = chat.Message, Cd = chat.Cd }.Encode()));
        Assert.Equal("vo_player_affirmative", Assert.Single(world.TakeSoundEvents()).Voice!.Stem);
    }

    [Fact]
    public void Native_null_text_wire_preserves_index_and_displays_its_caption()
    {
        var chat = Chat(28, "");
        chat.MessageIsAbsent = true;
        var bytes = chat.Encode();
        Assert.Equal(ChatMessage.FixedSize, bytes.Length);
        Assert.Equal(ChatMessage.FixedSize, BitConverter.ToUInt16(bytes, 4));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 6));
        var decoded = ChatMessage.Decode(bytes);
        Assert.True(decoded.MessageIsAbsent);
        Assert.Equal(28, decoded.VoiceOver);
        var world = new ClientWorld();
        world.Apply(decoded);
        Assert.Equal("Donate me your credits.", Assert.Single(world.Events.Chats).Message);
        Assert.Equal("vo_player_donatecredits", Assert.Single(world.TakeSoundEvents()).Voice!.Stem);
    }

    [Fact]
    public void Null_text_never_falls_back_to_direct_sound_ids_and_present_empty_text_is_not_a_sonic_index()
    {
        var world = new ClientWorld();
        var invalidSonic = Chat(1131, "");
        invalidSonic.MessageIsAbsent = true;
        world.Apply(ChatMessage.Decode(invalidSonic.Encode()));
        world.Apply(Chat(0, ""));
        Assert.Empty(world.TakeSoundEvents());
        world.Apply(Chat(1131, "")); // actual present empty string: direct SoundID
        Assert.Equal("vo_player_donatecredits", Assert.Single(world.TakeSoundEvents()).Voice!.Stem);
    }

    [Fact]
    public void Chatbuoy_null_text_wire_resolves_sonic_index_and_caption()
    {
        var world = new ClientWorld();
        var chat = Chat(10, "");
        var bytes = new ChatBuoy { Cd = chat.Cd, MessageIsAbsent = true }.Encode();
        Assert.Equal(ChatBuoy.FixedSize, bytes.Length);
        Assert.Equal(ChatBuoy.FixedSize, BitConverter.ToUInt16(bytes, 4));
        var decoded = ChatBuoy.Decode(bytes);
        Assert.True(decoded.MessageIsAbsent);
        world.Apply(decoded);
        Assert.Equal("vo_player_attacktarget", Assert.Single(world.TakeSoundEvents()).Voice!.Stem);
        Assert.Equal("Attack my target!", Assert.Single(world.Events.ChatBuoys).Message);
    }

    [Fact]
    public void Missing_artwork_is_explicit_and_keeps_the_caption()
    {
        var missing = NativeVoiceCatalog.SonicChats.First(p => !p.Value.Cue.Available);
        var world = new ClientWorld();
        world.Apply(Chat(missing.Key, missing.Value.Caption));
        Assert.False(Assert.Single(world.TakeSoundEvents()).Voice!.Available);
        Assert.Equal(missing.Value.Caption, Assert.Single(world.Events.Chats).Message);
    }
}
