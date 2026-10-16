using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
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

    // ── P714 — objective & status announce VOs ─────────────────────────────
    // All 5 ride the established ClientSoundEvent → Game1.Audio → SoundManager
    // one-shot path, driven by the fed messages the server already broadcasts:
    // SET_TEAM_LEADER(121), AUTODONATE(116), GAIN_FLAG(156).

    private static Ship MakeShip(short id) =>
        new(id, TestHull(), new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity);

    private static DataHullTypeIGC TestHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f], TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 7813, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 805f, HardpointOffset: 835,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    /// <summary>Seed <see cref="ClientWorld.MyShipId"/> via the real wire path
    /// (SET_CLUSTER, the only source of the player's own ship id) so the
    /// leadership/investor announce cues that key on "our own ship" are testable.</summary>
    private static ClientWorld WorldWithMeSeated(short me = 7840)
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster
        {
            SectorId = 3,
            Ship = ServerShipUpdate.FromModel(me, MakeShip(me), 1u),
            Cookie = 0x11,
        });
        return w;
    }

    [Fact]
    public void BecomingTeamLeader_RecordsCommanderElected()
    {
        var world = WorldWithMeSeated(me: 841);
        world.Apply(new SetTeamLeader { SideId = 2, ShipId = 841 });
        Assert.Equal(ClientSoundEventKind.CommanderElected, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void AnotherShipBecomesLeader_RecordsNoSound()
    {
        var world = WorldWithMeSeated(me: 841);
        world.Apply(new SetTeamLeader { SideId = 2, ShipId = 941 });
        Assert.Empty(world.TakeSoundEvents());
    }

    [Fact]
    public void FirstDonorBeginsDonatingToMe_RecordsInvestor()
    {
        var world = WorldWithMeSeated(me: 843);
        world.Apply(new AutoDonateUpdate { SidDonateBy = 944, SidDonateTo = 843 });
        Assert.Equal(ClientSoundEventKind.InvestorReceived, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void SecondDonorDoesNotRefireInvestor()
    {
        short me = 6627;
        var world = WorldWithMeSeated(me);
        world.Apply(new AutoDonateUpdate { SidDonateBy = 6134, SidDonateTo = me });  // donor A → count 1 → investor fires
        Assert.Equal(ClientSoundEventKind.InvestorReceived, Assert.Single(world.TakeSoundEvents()).Kind);
        world.Apply(new AutoDonateUpdate { SidDonateBy = 6683, SidDonateTo = me });  // donor B (distinct) → count 2 → no investor
        Assert.Empty(world.TakeSoundEvents());
    }

    [Fact]
    public void DonatingMyself_NoInvestor()
    {
        short me = 6852;
        var world = WorldWithMeSeated(me);
        world.Apply(new AutoDonateUpdate { SidDonateBy = me, SidDonateTo = 6291 });  // I donate to someone else
        Assert.Empty(world.TakeSoundEvents());
    }

    [Fact]
    public void StoppingDonation_NoInvestor()
    {
        short me = 4479;
        var world = WorldWithMeSeated(me);
        world.Apply(new AutoDonateUpdate { SidDonateBy = 4561, SidDonateTo = me });  // donor → count 1 → investor fires
        Assert.Equal(ClientSoundEventKind.InvestorReceived, Assert.Single(world.TakeSoundEvents()).Kind);
        world.Apply(new AutoDonateUpdate { SidDonateBy = 3652, SidDonateTo = -1 });  // stop donating (NA) → count 0
        Assert.Empty(world.TakeSoundEvents());
    }

    [Fact]
    public void TeammateStealsEnemyFlag_RecordsEnemyFlagStolen()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        world.Apply(new ExportShip { ShipId = 983, HullId = 1, SideId = 2, CharacterName = "Mate" });
        world.Apply(new GainFlag { SideidFlag = 3, ShipidRecipient = 983 });   // recipient on our side, enemy flag
        Assert.Equal(ClientSoundEventKind.EnemyFlagStolen, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void TeammateFindsArtifact_RecordsArtifactFound()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        world.Apply(new ExportShip { ShipId = 988, HullId = 1, SideId = 2, CharacterName = "Mate" });
        world.Apply(new GainFlag { SideidFlag = -2, ShipidRecipient = 988 });  // lobby/artifact sentinel
        Assert.Equal(ClientSoundEventKind.ArtifactFound, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void EnemyStealsOurFlag_RecordsOurFlagStolen()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        world.Apply(new ExportShip { ShipId = 974, HullId = 1, SideId = 3, CharacterName = "Raider" });
        world.Apply(new GainFlag { SideidFlag = 2, ShipidRecipient = 974 });   // our-side flag in enemy hands
        Assert.Equal(ClientSoundEventKind.OurFlagStolen, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void SecureOrUnclassifiedGainFlag_NoSound()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        // secure / tech-recovery (NA flag origin) → no announce VO
        world.Apply(new GainFlag { SideidFlag = -1, ShipidRecipient = 933 });
        // recipient not in our entity table → side unknowable → no classification
        world.Apply(new GainFlag { SideidFlag = 3, ShipidRecipient = 929 });
        Assert.Empty(world.TakeSoundEvents());
    }
}
