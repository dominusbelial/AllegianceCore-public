using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P267 — ClientWorld's wiring of the one-shot sound events from their real
/// wire messages: STATION_DESTROYED(59), STATION_CAPTURE(58) and
/// DEV_COMPLETED(8) each record a <see cref="ClientSoundEvent"/> that
/// <c>Game1</c> drains into the audio manager, with the own-vs-enemy variant
/// decided from the local side (native appmsg.cpp:548-551, 674-677, 3326).
/// </summary>
public class ClientWorldSoundEventTests
{
    private static DataStationIGC Station(short id, short side, short cluster = 1) =>
        new(
            Position: new float[] { 0f, 0f, 0f },
            Up: new float[] { 0f, 1f, 0f },
            Forward: new float[] { 0f, 0f, -1f },
            Rotation: new float[] { 0f, 0f, 0f, 0f },
            ClusterId: cluster, SideId: side, StationId: id, StationTypeId: 1,
            BpHull: 0, BpShield: 0, Name: "base");

    /// <summary>Seed the client's own side id via the own-ship dummy EXPORT
    /// (ShipId = NA(-1)), the only wire source of the player's own team.</summary>
    private static void SetMySide(ClientWorld world, short side) =>
        world.Apply(new ExportShip { ShipId = -1, SideId = side });

    [Fact]
    public void DevCompleted_RecordsDevChimeEvent()
    {
        var world = new ClientWorld();
        world.Apply(new DevCompleted { DevId = 41 });

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.DevCompleted, ev.Kind);
        Assert.Equal(41, ev.Id);
        Assert.Empty(world.TakeSoundEvents());   // drained exactly once
    }

    [Fact]
    public void OwnStationDestroyed_RecordsDestroyed_AndRemovesStation()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        world.Apply(new ExportStation { Station = Station(id: 7, side: 2) });

        world.Apply(new StationDestroyed { StationId = 7, Launcher = 5 });

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.StationDestroyed, ev.Kind);
        Assert.Equal(7, ev.Id);
        Assert.False(world.Stations.ContainsKey(7), "destroyed station must leave the world table");
    }

    [Fact]
    public void EnemyStationDestroyed_RecordsEnemyDestroyed()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        world.Apply(new ExportStation { Station = Station(id: 7, side: 9) });   // enemy

        world.Apply(new StationDestroyed { StationId = 7, Launcher = 5 });

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.StationEnemyDestroyed, ev.Kind);
    }

    [Fact]
    public void LostOurStationToEnemy_RecordsCaptured_AndReSides()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        world.Apply(new ExportStation { Station = Station(id: 7, side: 2) });

        // sidOld == ours (we lost it to the enemy) → the "captured" readout.
        world.Apply(new StationCapture { StationId = 7, SidOld = 2, SidNew = 9, ShipIdCredit = 5 });

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.StationCaptured, ev.Kind);
        Assert.Equal(9, world.Stations[7].SideId);   // re-sided to the new owner
    }

    [Fact]
    public void TookEnemyStation_RecordsEnemyCaptured()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        world.Apply(new ExportStation { Station = Station(id: 7, side: 9) });

        // sidNew == ours (we captured an enemy base) → the "enemy captured" readout.
        world.Apply(new StationCapture { StationId = 7, SidOld = 9, SidNew = 2, ShipIdCredit = 5 });

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.StationEnemyCaptured, ev.Kind);
        Assert.Equal(2, world.Stations[7].SideId);
    }

    [Fact]
    public void SoundEvents_AreBoundedAndAtomic()
    {
        var world = new ClientWorld();
        world.Apply(new DevCompleted { DevId = 1 });
        world.Apply(new DevCompleted { DevId = 2 });

        var all = world.TakeSoundEvents();
        Assert.Equal(2, all.Count);
        Assert.Empty(world.TakeSoundEvents());   // second drain is empty
    }

    // ── P710 — wealth announcements (clintlib/appmsg.cpp) ────────────────────
    [Fact]
    public void Payday_RecordsPaydaySoundEvent()
    {
        var world = new ClientWorld();
        world.Apply(new Payday { DMoney = 900 });
        Assert.Equal(ClientSoundEventKind.PaydayReceived, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Theory]
    [InlineData((byte)Allegiance.Core.TreasureCode.Powerup, ClientSoundEventKind.PickupPowerup)]
    [InlineData((byte)Allegiance.Core.TreasureCode.Development, ClientSoundEventKind.PickupDevelopment)]
    [InlineData((byte)Allegiance.Core.TreasureCode.Cash, ClientSoundEventKind.PickupCash)]
    [InlineData((byte)Allegiance.Core.TreasureCode.Flag, ClientSoundEventKind.PickupDevelopment)] // flag reuses dev cue (appmsg.cpp:303)
    public void AcquireTreasure_RecordsPerTypePickupSound(byte code, ClientSoundEventKind expected)
    {
        var world = new ClientWorld();
        world.Apply(new AcquireTreasure { TreasureCode = code, TreasureId = 501, Amount = 100 });
        Assert.Equal(expected, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void AcquirePart_RecordsNoPickupSound()
    {
        // c_tcPart has no pickup cue (it triggers a TREASURE_ACK instead, appmsg.cpp:267-276).
        var world = new ClientWorld();
        world.Apply(new AcquireTreasure { TreasureCode = (byte)Allegiance.Core.TreasureCode.Part, TreasureId = 1 });
        Assert.Empty(world.TakeSoundEvents());
    }

    // ── P710 — incoming-message notification cues (trekigc.cpp:4536-4560) ───
    private static ChatMessage Msg(short sender, int chatTarget, short recipient = -1,
        short commandId = -1, short voiceOver = -1) => new()
    {
        SidSender = sender, ChatTarget = chatTarget, OidRecipient = recipient,
        CommandID = commandId, VoiceOver = voiceOver,
    };

    [Fact]
    public void ClassifyRegularChat_IsChatRegular()
    {
        // General chat line (Everyone target), non-leader sender, online.
        Assert.Equal(ClientSoundEventKind.ChatRegular,
            ClientWorld.ClassifyChatSound(Msg(sender: 711, chatTarget: (int)Allegiance.Core.Sim.ChatTarget.Everyone),
                myShipId: 712, senderIsLeader: false));
    }

    [Fact]
    public void ClassifyCommandChat_IsChatCommand()
    {
        // A message with a real command id (cid != c_cidNone) → newCommandMsgSound (:4540).
        Assert.Equal(ClientSoundEventKind.ChatCommand,
            ClientWorld.ClassifyChatSound(Msg(sender: 713, chatTarget: (int)Allegiance.Core.Sim.ChatTarget.Everyone, commandId: 3),
                myShipId: 720, senderIsLeader: false));
    }

    [Fact]
    public void ClassifyPersonalChatToMe_IsChatPersonal()
    {
        Assert.Equal(ClientSoundEventKind.ChatPersonal,
            ClientWorld.ClassifyChatSound(Msg(sender: 715, chatTarget: (int)Allegiance.Core.Sim.ChatTarget.Individual, recipient: 730),
                myShipId: 730, senderIsLeader: false));
    }

    [Fact]
    public void ClassifyLeaderPersonalChat_IsChatCommander()
    {
        // Individual message from the team leader → commander variant (:4546).
        Assert.Equal(ClientSoundEventKind.ChatCommander,
            ClientWorld.ClassifyChatSound(Msg(sender: 733, chatTarget: (int)Allegiance.Core.Sim.ChatTarget.Individual, recipient: 731),
                myShipId: 731, senderIsLeader: true));
    }

    [Fact]
    public void ClassifyLeaderGeneralChat_IsChatCommander()
    {
        // General chat from the team leader → commander variant (:4555).
        Assert.Equal(ClientSoundEventKind.ChatCommander,
            ClientWorld.ClassifyChatSound(Msg(sender: 740, chatTarget: (int)Allegiance.Core.Sim.ChatTarget.Everyone),
                myShipId: 741, senderIsLeader: true));
    }

    [Fact]
    public void ClassifyOfflineChat_IsChatOffline()
    {
        // Disconnected receiver → the quieter offline variant (:4560).
        Assert.Equal(ClientSoundEventKind.ChatOffline,
            ClientWorld.ClassifyChatSound(Msg(sender: 750, chatTarget: (int)Allegiance.Core.Sim.ChatTarget.Everyone),
                myShipId: 751, senderIsLeader: false, online: false));
    }

    [Fact]
    public void ClassifyOwnChatEcho_IsSilent()
    {
        // Native drops its own echoed chat (appmsg.cpp:2644) — no sound.
        Assert.Null(ClientWorld.ClassifyChatSound(Msg(sender: 770, chatTarget: (int)Allegiance.Core.Sim.ChatTarget.Everyone),
            myShipId: 770, senderIsLeader: false));
    }

    [Fact]
    public void ClassifySonicChat_IsSilent()
    {
        // A sonic chat (non-NA voiceOver) is left to the voice-over table, not
        // a generic cue (SIM-0399, out of this bundle's scope).
        Assert.Null(ClientWorld.ClassifyChatSound(Msg(sender: 771, chatTarget: (int)Allegiance.Core.Sim.ChatTarget.Everyone, voiceOver: 550),
            myShipId: 772, senderIsLeader: false));
    }

    [Fact]
    public void RegularChatWire_RecordsChatRegular()
    {
        var world = new ClientWorld();
        world.Apply(new ChatMessage { SidSender = 801, ChatTarget = (int)Allegiance.Core.Sim.ChatTarget.Everyone, VoiceOver = -1, CommandID = -1 });
        Assert.Equal(ClientSoundEventKind.ChatRegular, Assert.Single(world.TakeSoundEvents()).Kind);
    }
}
