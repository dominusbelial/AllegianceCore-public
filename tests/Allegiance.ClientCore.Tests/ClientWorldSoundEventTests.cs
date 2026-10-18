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
    public void AcquirePart_RecordsPickupPartSound()
    {
        // P735 — SIM-0268: a c_tcPart pickup plays the pickUpPartSound cue
        // (native plays it server-side at the ship, shipIGC.h:1307; the client
        // ACQUIRE_TREASURE handler itself is silent, appmsg.cpp:267-276).
        var world = new ClientWorld();
        world.Apply(new AcquireTreasure { TreasureCode = (byte)Allegiance.Core.TreasureCode.Part, TreasureId = 601, Amount = 1 });
        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.PickupPart, ev.Kind);
        Assert.Equal(601, ev.Id);
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
    public void ClassifySonicChat_SelectsVoiceOverBeforePlainChat()
    {
        Assert.Equal(ClientSoundEventKind.ChatVoiceOver, ClientWorld.ClassifyChatSound(Msg(sender: 771, chatTarget: (int)Allegiance.Core.Sim.ChatTarget.Everyone, voiceOver: 550),
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

    // ── P716 — combat & physics feedback one-shots ─────────────────────────
    // The own ship's warp transit (SET_CLUSTER landing in a new sector beside
    // its aleph) plays the native jumpSound (trekigc.cpp:3998,4415; the same
    // wavein cue the teleport path uses). The collision wire (COLLISION_EVENT,
    // 216) is buffered for the positional collision-sound pass (P463c).
    // Failing-first: ClientSoundEventKind.SectorJump did not exist before P716
    // (a compile error in these tests on the pre-change tree).

    private static ServerShipUpdate PoseAt(short id, Vector3 pos) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 1u,
            pos, Orientation.Identity, Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f);

    private static void SeatMe(ClientWorld w, short me, short sector, Vector3 pos) =>
        w.Apply(new SetCluster { SectorId = sector, Ship = PoseAt(me, pos), Cookie = 0x1 });

    private static void SeedWarp(ClientWorld w, short id, short cluster, Vector3 pos) =>
        w.Apply(new ExportWarp
        {
            ObjectType = ExportWarp.OtWarp,
            Warp = new DataWarpIGC(id, (short)(id + 900), 60, "aleph", "", "w",
                new[] { pos.X, pos.Y, pos.Z }, new[] { 0f, 0f, -1f },
                new[] { 0f, 0f, 0f, 1f }, 0f, cluster),
        });

    [Fact]
    public void OwnShipWarpsToNewSectorBesideAleph_RecordsSectorJump()
    {
        var w = new ClientWorld();
        SeatMe(w, 770, sector: 1, Vector3.Zero);                 // join sector 1
        SeedWarp(w, 880, cluster: 2, new Vector3(500f, 0f, 0f)); // destination aleph

        w.Apply(new SetCluster { SectorId = 2, Ship = PoseAt(770, new Vector3(500f, 0f, 0f)), Cookie = 0x2 });

        var ev = Assert.Single(w.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.SectorJump, ev.Kind);
        Assert.Equal((short)770, ev.Id);
    }

    [Fact]
    public void OwnShipSameSectorReSeat_NoSectorJump()
    {
        var w = new ClientWorld();
        SeatMe(w, 770, sector: 1, Vector3.Zero);
        SeedWarp(w, 884, cluster: 1, new Vector3(20f, 0f, 0f));

        w.Apply(new SetCluster { SectorId = 1, Ship = PoseAt(770, Vector3.Zero), Cookie = 0x2 });

        Assert.Empty(w.TakeSoundEvents());
    }

    [Fact]
    public void RemoteShipSectorArrival_NoSectorJump()
    {
        var w = new ClientWorld();
        SeatMe(w, 777, sector: 1, Vector3.Zero);                 // own ship stays put
        SeedWarp(w, 887, cluster: 2, new Vector3(510f, 0f, 0f));

        w.Apply(new SetCluster { SectorId = 2, Ship = PoseAt(989, new Vector3(510f, 0f, 0f)), Cookie = 0x2 });

        Assert.Empty(w.TakeSoundEvents());                       // only the OWN ship cues
    }

    [Fact]
    public void CollisionWire_IsBufferedForTheCollisionSoundPass()
    {
        var w = new ClientWorld();
        w.Apply(new Allegiance.Core.Wire.CollisionEvent
        {
            Party1Id = 1, Party2Id = 2, ClusterId = 3,
            Jiggle1 = 0.5f, Jiggle2 = 0.0f, Position = new Vector3(10f, 0f, 0f),
        });

        var ce = Assert.Single(w.TakeCollisionEvents());
        Assert.Equal((short)1, ce.Party1Id);
        Assert.Equal((short)2, ce.Party2Id);
        Assert.Equal((short)3, ce.ClusterId);
    }

    // ── P717 — ordnance deploy (mine) & own-ship shield-offline ─────────────

    private static ExportMine ExportMineFor(short id, short launcher, short side = 2, short cluster = 3) =>
        new()
        {
            Mine = new DataMineExport(
                P0: Vector3.Zero, Time0: 12u, MineId: id, ExportF: true, ClusterId: cluster,
                MinetypeId: 920, LauncherId: launcher, SideId: side,
                Fraction: 240, CreateNow: true),
        };

    [Fact]
    public void OwnShipDeploysMine_RecordsMineDeployed()
    {
        var w = new ClientWorld();
        SeatMe(w, 774, sector: 1, Vector3.Zero);   // own ship = 774
        w.TakeSoundEvents();                        // discard seat pass

        w.Apply(ExportMineFor(500, launcher: 774));

        var ev = Assert.Single(w.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.MineDeployed, ev.Kind);
        Assert.Equal((short)500, ev.Id);
    }

    [Fact]
    public void RemoteShipDeploysMine_RecordsNoDeploySound()
    {
        var w = new ClientWorld();
        SeatMe(w, 774, sector: 1, Vector3.Zero);
        w.TakeSoundEvents();

        w.Apply(ExportMineFor(501, launcher: 999));   // someone else's mine

        Assert.Empty(w.TakeSoundEvents());
    }

    private static ClientWorld ShieldArmedWorld(short me, float shieldFraction)
    {
        var w = new ClientWorld();
        var pose = ServerShipUpdate.FromValues(
            me, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 1u,
            Vector3.Zero, Orientation.Identity, Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: shieldFraction, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 1f);
        w.Apply(new SetCluster { SectorId = 1, Ship = pose, Cookie = 0x1 });
        w.UpdateOwnStatusAlarms();      // arm
        w.TakeSoundEvents();            // discard arming-pass emissions
        return w;
    }

    [Fact]
    public void OwnShieldCollapses_RecordsShieldsOffline()
    {
        var w = ShieldArmedWorld(774, shieldFraction: 0.9f);

        w.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromValues(
                774, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 2u,
                Vector3.Zero, Orientation.Identity, Vector3.Zero,
                turnRates: new[] { 0f, 0f, 0f },
                hullFrac: 0.9f, shieldFrac: 0f, ammoFrac: 1f, fuelFrac: 1f,
                energyFrac: 1f, oreFrac: 0f, power: 1f),
            Cookie = 0x2,
        });
        w.UpdateOwnStatusAlarms();

        var ev = Assert.Single(w.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.ShieldsOffline, ev.Kind);
        Assert.Equal((short)774, ev.Id);
    }

    [Fact]
    public void OwnShieldAlreadyDown_DoesNotRefire()
    {
        var w = ShieldArmedWorld(774, shieldFraction: 0f);   // arm already-down

        // stays down: no further offline cue
        w.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromValues(
                774, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 3u,
                Vector3.Zero, Orientation.Identity, Vector3.Zero,
                turnRates: new[] { 0f, 0f, 0f },
                hullFrac: 0.9f, shieldFrac: 0f, ammoFrac: 1f, fuelFrac: 1f,
                energyFrac: 1f, oreFrac: 0f, power: 1f),
            Cookie = 0x2,
        });
        w.UpdateOwnStatusAlarms();

        Assert.Empty(w.TakeSoundEvents());
    }

    [Fact]
    public void OwnShieldStaysOnline_NoShieldsOffline()
    {
        var w = ShieldArmedWorld(774, shieldFraction: 0.8f);

        w.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromValues(
                774, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 2u,
                Vector3.Zero, Orientation.Identity, Vector3.Zero,
                turnRates: new[] { 0f, 0f, 0f },
                hullFrac: 0.9f, shieldFrac: 0.7f, ammoFrac: 1f, fuelFrac: 1f,
                energyFrac: 1f, oreFrac: 0f, power: 1f),
            Cookie = 0x2,
        });
        w.UpdateOwnStatusAlarms();

        Assert.Empty(w.TakeSoundEvents());
    }

    // ── P735 (parity wave 22): own-ship state notification one-shots ────────

    /// <summary>Arm <see cref="ClientWorld.UpdateOwnStatusAlarms"/> with the
    /// local ship wearing the given cloak stateM bit (CloakActive = 0x800,
    /// ShipControlState.CloakActive), then discard the arming pass.</summary>
    private static ClientWorld CloakArmedWorld(short me, int stateM)
    {
        var w = new ClientWorld();
        var pose = ServerShipUpdate.FromValues(
            me, stateM: stateM, new ControlData(0f, 0f, 0f, 0f), time: 1u,
            Vector3.Zero, Orientation.Identity, Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 1f);
        w.Apply(new SetCluster { SectorId = 1, Ship = pose, Cookie = 0x1 });
        w.UpdateOwnStatusAlarms();   // arm
        w.TakeSoundEvents();         // discard arming-pass emissions
        return w;
    }

    [Fact]
    public void OwnCloakEngages_RecordsCloakEngaged()
    {
        // SIM-0248 — the local ship's CloakActive bit going false→true plays
        // the cloak-engage cue (native shipIGC.cpp:1608; vo_sal_cloakengage).
        var w = CloakArmedWorld(775, stateM: 0);

        w.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromValues(
                775, stateM: Allegiance.Core.Sim.ShipControlState.CloakActive,
                new ControlData(0f, 0f, 0f, 0f), time: 2u,
                Vector3.Zero, Orientation.Identity, Vector3.Zero,
                turnRates: new[] { 0f, 0f, 0f },
                hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
                energyFrac: 1f, oreFrac: 0f, power: 1f),
            Cookie = 0x2,
        });
        w.UpdateOwnStatusAlarms();

        var ev = Assert.Single(w.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.CloakEngaged, ev.Kind);
        Assert.Equal((short)775, ev.Id);
    }

    [Fact]
    public void OwnCloakAlreadyActive_DoesNotRefire()
    {
        // Arming already cloaked: staying cloaked must not re-fire the cue.
        var w = CloakArmedWorld(775, stateM: Allegiance.Core.Sim.ShipControlState.CloakActive);

        w.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromValues(
                775, stateM: Allegiance.Core.Sim.ShipControlState.CloakActive,
                new ControlData(0f, 0f, 0f, 0f), time: 3u,
                Vector3.Zero, Orientation.Identity, Vector3.Zero,
                turnRates: new[] { 0f, 0f, 0f },
                hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
                energyFrac: 1f, oreFrac: 0f, power: 1f),
            Cookie = 0x2,
        });
        w.UpdateOwnStatusAlarms();

        Assert.Empty(w.TakeSoundEvents());
    }

    [Fact]
    public void OwnCloakUncloaks_NoEvent()
    {
        // Cloak turning back off is not a cue (native only sounds the engage).
        var w = CloakArmedWorld(775, stateM: Allegiance.Core.Sim.ShipControlState.CloakActive);

        w.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromValues(
                775, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 4u,
                Vector3.Zero, Orientation.Identity, Vector3.Zero,
                turnRates: new[] { 0f, 0f, 0f },
                hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
                energyFrac: 1f, oreFrac: 0f, power: 1f),
            Cookie = 0x2,
        });
        w.UpdateOwnStatusAlarms();

        Assert.Empty(w.TakeSoundEvents());
    }

    // ── P719 (parity wave 18): station/economy + loadout command one-shots ──

    [Fact]
    public void DockingOwnShip_RecordsDockedAndWelcomeHome()
    {
        var world = WorldWithMeSeated(me: 881);
        world.Apply(new DockedNotify { StationId = 900 });

        var evs = world.TakeSoundEvents();
        Assert.Contains(evs, e => e.Kind == ClientSoundEventKind.Docked);
        Assert.Contains(evs, e => e.Kind == ClientSoundEventKind.WelcomeHome);
    }

    [Fact]
    public void RepeatingDockNotice_DoesNotRefireDockedCue()
    {
        var world = WorldWithMeSeated(me: 881);
        world.Apply(new DockedNotify { StationId = 901 });
        Assert.NotEmpty(world.TakeSoundEvents());

        // A second docked-notify for the same station must not re-fire.
        world.Apply(new DockedNotify { StationId = 903 });
        Assert.Empty(world.TakeSoundEvents());
    }

    [Fact]
    public void PassengerBoardsLocalShip_RecordsBoarded()
    {
        var world = WorldWithMeSeated(me: 884);
        // A teammate ship already in the entity table.
        world.Apply(new ExportShip { ShipId = 950, SideId = 2 });
        // The local ship's LOADOUT_CHANGE re-seats the passenger onto it.
        world.Apply(new Allegiance.Core.Wire.LoadoutChange
        {
            SidShip = 884,
            HullId = 888,
            Parts = [],
            Passengers = [new PassengerData(950, 0)],
        });

        Assert.Equal(ClientSoundEventKind.Boarded, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void PassengerAlreadyBoarded_DoesNotRefireBoarded()
    {
        var world = WorldWithMeSeated(me: 886);
        world.Apply(new ExportShip { ShipId = 962, SideId = 2 });
        world.Apply(new Allegiance.Core.Wire.LoadoutChange
        {
            SidShip = 887,   // a DIFFERENT ship's loadout change, not ours
            HullId = 899,
            Parts = [],
            Passengers = [new PassengerData(962, 0)],
        });

        Assert.Empty(world.TakeSoundEvents());
    }

    // SIM-0430 / SIM-0433 reconciliation: the newCommand / player-waiting
    // producers already exist on HIGHLIGHT_CLUSTER / POSITIONREQ — asserted here
    // so the matrix's stale `missing` rows are covered by trigger-level tests.

    [Fact]
    public void HighlightCluster_RecordsSectorHighlighted()
    {
        var world = new ClientWorld();
        world.Apply(new ExportCluster { Cluster = new(1, [0, 0, 1], 0xffffffff, 0, 0, 7, 0, 0, "Sol", "", "", 0, 0, 0, true, false) });
        world.Apply(new HighlightCluster { ClusterId = 7, Highlight = true });

        Assert.Equal(ClientSoundEventKind.SectorHighlighted, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void PositionRequestToOurSide_RecordsPlayerWaiting()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        world.Apply(new ExportShip { ShipId = 930, SideId = 2 });
        world.Apply(new PositionReqBroadcast { ShipId = 937, SideId = 2 });

        Assert.Equal(ClientSoundEventKind.PlayerWaiting, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    // ── P736 (parity wave 23): presence / side-change salutations ──────────
    // Native voices these from the CLIENT's RemovePlayerFromSide /
    // AddPlayerToSide (clintlib.cpp:3867-4009, 4140-4172) when it processes the
    // server's player-quit/player-join side events. The port reproduces them on
    // the JOIN_SIDE(125) / QUIT_MISSION(124) / QUIT_SIDE(126) broadcasts the
    // server already sends. Failing-first: the SalQuit/SalChangeSides/SalMia/
    // SalEnemyLeaves/SalRecruitsArrived/SalEnemyJoiners kinds did not exist
    // before P736 (compile errors on the pre-change tree).

    /// <summary>Seat the local player on a side and advance the mission to
    /// STAGE_STARTED (native gates every presence salutation on
    /// STAGE_STARTED, clintlib.cpp:3924, 4141).</summary>
    private static ClientWorld StartedWorld(short mySide)
    {
        var w = new ClientWorld();
        SetMySide(w, mySide);
        w.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        return w;
    }

    /// <summary>Seed a known HUMAN player ship on a side (PlayerInfo with
    /// PilotType=player — native's IsHuman() gate, clintlib.cpp:3924).</summary>
    private static void SeedHumanShip(ClientWorld w, short shipId, short side)
    {
        w.Apply(new ExportShip { ShipId = shipId, SideId = side });
        w.Apply(new PlayerInfo { ShipId = shipId, ISide = side, CharacterName = $"p{shipId}", PilotType = PlayerInfo.PilotTypePlayer });
    }

    [Fact]
    public void HumanJoinsOurSide_RecordsSalRecruitsArrived()
    {
        var world = StartedWorld(mySide: 2);
        SeedHumanShip(world, 511, side: 999);   // sitting in the team lobby pre-join

        world.Apply(new JoinSide { ShipId = 511, SideId = 2 });   // joins OUR side

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.SalRecruitsArrived, ev.Kind);
        Assert.Equal((short)511, ev.Id);
    }

    [Fact]
    public void HumanJoinsEnemySide_RecordsSalEnemyJoiners()
    {
        var world = StartedWorld(mySide: 2);
        SeedHumanShip(world, 512, side: 999);

        world.Apply(new JoinSide { ShipId = 512, SideId = 7 });   // enemy side

        Assert.Equal(ClientSoundEventKind.SalEnemyJoiners, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void HumanJoinsAlliedDifferentSide_RecordsNothing()
    {
        // Native: an allied-but-different side join is TEXT ONLY (sal id 0,
        // clintlib.cpp:4151) — no sound cue.
        var world = StartedWorld(mySide: 2);
        SeedHumanShip(world, 513, side: 999);
        world.Apply(new ChangeAlliances { Allies = [0, 0, 1, 1, -1, -1] });   // sides 2 and 3 allied
        SetMySide(world, 2);

        world.Apply(new JoinSide { ShipId = 513, SideId = 3 });   // allied side

        Assert.Empty(world.TakeSoundEvents());
    }

    [Fact]
    public void DroneJoinsSide_RecordsNothing()
    {
        // Native's IsHuman() gate (clintlib.cpp:4141): a non-human joiner
        // (drone/constructor) voices no presence salutation.
        var world = StartedWorld(mySide: 2);
        world.Apply(new ExportShip { ShipId = 519, SideId = 998 });
        world.Apply(new PlayerInfo { ShipId = 519, ISide = 997, CharacterName = "miner", PilotType = 4 });   // c_ptMiner

        world.Apply(new JoinSide { ShipId = 519, SideId = 994 });

        Assert.Empty(world.TakeSoundEvents());
    }

    [Fact]
    public void MissionNotStarted_JoinRecordsNothing()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);                                   // stage defaults to NotStarted
        SeedHumanShip(world, 516, side: 995);

        world.Apply(new JoinSide { ShipId = 516, SideId = 996 });

        Assert.Empty(world.TakeSoundEvents());
    }

    [Fact]
    public void SameSideHumanGoesLinkDead_RecordsSalMia()
    {
        var world = StartedWorld(mySide: 2);
        SeedHumanShip(world, 601, side: 2);   // a teammate

        world.Apply(new QuitMission { ShipId = 601, Reason = QuitSideReason.LinkDead });

        Assert.Equal(ClientSoundEventKind.SalMia, Assert.Single(world.TakeSoundEvents()).Kind);
        Assert.False(world.Ships.ContainsKey(601)); // P718 cleanup survives the cue.
        Assert.Equal(QuitSideReason.LinkDead, world.LastDeparture!.Value.Reason);
        world.Apply(new QuitMission { ShipId = 601, Reason = QuitSideReason.LinkDead });
        Assert.Empty(world.TakeSoundEvents());
    }

    [Fact]
    public void SameSideHumanSwitchesSides_RecordsSalChangeSides()
    {
        var world = StartedWorld(mySide: 2);
        SeedHumanShip(world, 602, side: 2);

        world.Apply(new QuitSide { ShipId = 602, Reason = QuitSideReason.SwitchingSides });

        Assert.Equal(ClientSoundEventKind.SalChangeSides, Assert.Single(world.TakeSoundEvents()).Kind);
        Assert.Equal((short)-2, world.Ships[602].SideId);
        world.Apply(new QuitSide { ShipId = 602, Reason = QuitSideReason.SwitchingSides });
        Assert.Empty(world.TakeSoundEvents());
    }

    [Fact]
    public void EnemySideHumanLeaves_RecordsSalEnemyLeaves()
    {
        var world = StartedWorld(mySide: 2);
        SeedHumanShip(world, 703, side: 7);   // enemy

        world.Apply(new QuitMission { ShipId = 703, Reason = QuitSideReason.LinkDead });

        Assert.Equal(ClientSoundEventKind.SalEnemyLeaves, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void AlliedSideHumanLeaves_RecordsSalQuit()
    {
        // Native other-side branch (clintlib.cpp:4006-4009): an ALLIED (not our
        // own) side leaver voices salQuitSound; only a non-allied one gets
        // salEnemyLeavesSound.
        var world = StartedWorld(mySide: 2);
        SeedHumanShip(world, 704, side: 3);
        world.Apply(new ChangeAlliances { Allies = [0, 0, 1, 1, -1, -1] });   // sides 2 and 3 allied
        SetMySide(world, 2);

        world.Apply(new QuitMission { ShipId = 704, Reason = QuitSideReason.LinkDead });

        Assert.Equal(ClientSoundEventKind.SalQuit, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void OwnShipQuitMission_RecordsNothing()
    {
        // Native: our OWN leave goes through OnQuitSide, not the presence
        // salutation (clintlib.cpp:3940-3941).
        var world = StartedWorld(mySide: 2);
        SeatMe(world, 885, sector: 3, Vector3.Zero);

        world.Apply(new QuitMission { ShipId = 885, Reason = QuitSideReason.LinkDead });

        Assert.Empty(world.TakeSoundEvents());
    }

    // ── P737 (parity wave 24): own-ship reload one-shots ─────────────────
    // Native plays a type-specific sal + the generic startReloadSound when a
    // fire empties a pool and a spare pack auto-reloads (clintlib.cpp:3216-3417,
    // shipIGC.h:605-650). The port observes the own-ship RELOAD(129) relay and
    // classifies each consumed cargo slot's part type (Ship.TryApplyReloadEntry).

    private static DataPartTypeIGC ReloadPart(short id, DataPartTypeIGC.Subtype t) => new(
        Price: 0, TimeToBuild: 0, ModelName: "m", IconName: "i", Name: "p",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 1f, Signature: 0f, PartId: id, SuccessorPartId: -1, PartMask: 0,
        InventoryLineMdl: "", Type: t);

    /// <summary>Seed a world whose own ship carries the given cargo slots
    /// (mount, part) and whose PartTypeResolver answers from the map.</summary>
    private static ClientWorld ReloadWorld(short me,
        IEnumerable<(sbyte mount, short partId, DataPartTypeIGC type)> cargo)
    {
        var w = WorldWithMeSeated(me);
        var map = new Dictionary<short, DataPartTypeIGC>();
        var parts = new List<ExpandedPartData>();
        foreach (var (mount, partId, type) in cargo)
        {
            map[partId] = type;
            parts.Add(new ExpandedPartData(PartId: partId, Amount: 1,
                MountId: unchecked((byte)mount), FractionMounted: 0));
        }
        w.PartTypeResolver = pid => map.TryGetValue(pid, out var t) ? t : null;
        w.Apply(new BuyLoadoutAck { HullId = 7813, FBoughtEverything = true, Parts = parts });
        return w;
    }

    [Fact]
    public void OwnAmmoReload_RecordsSalReloadingAmmoAndStartReload()
    {
        // Ammo pack (c_packAmmo = 0) at cargo slot 0 (mount -1): native
        // shipIGC.h:617 plays salReloadingAmmoSound + startReloadSound.
        var w = ReloadWorld(me: 911,
            [(mount: -1, partId: 910, type: ReloadPart(910, new DataPartTypeIGC.Pack(PackType: 0, Amount: 100)))]);
        w.Apply(new Reload { ShipID = 911, Entries = [new ReloadData((short)-1, (sbyte)-1)] });

        var evs = w.TakeSoundEvents();
        Assert.Contains(evs, e => e.Kind == ClientSoundEventKind.SalReloadingAmmo);
        Assert.Contains(evs, e => e.Kind == ClientSoundEventKind.StartReload);
        Assert.All(evs, e => Assert.Equal((short)911, e.Id));
        Assert.Equal(2, evs.Count);
        Assert.Empty(w.Ships[911].Cargo!); // P726 transfer runs after classification.
        Assert.Equal(1, w.Ships[911].ReloadCount);
        w.Apply(new Reload { ShipID = 911, Entries = [new ReloadData((short)-1, (sbyte)-1)] });
        Assert.Empty(w.TakeSoundEvents());
    }

    [Fact]
    public void OwnFuelReload_RecordsSalReloadingFuel()
    {
        // Fuel pack (c_packFuel = 1): shipIGC.h:647 salReloadingFuelSound.
        var w = ReloadWorld(me: 912,
            [(mount: -1, partId: 920, type: ReloadPart(920, new DataPartTypeIGC.Pack(PackType: 1, Amount: 100)))]);
        w.Apply(new Reload { ShipID = 912, Entries = [new ReloadData((short)-1, (sbyte)-1)] });

        var evs = w.TakeSoundEvents();
        Assert.Contains(evs, e => e.Kind == ClientSoundEventKind.SalReloadingFuel);
        Assert.Contains(evs, e => e.Kind == ClientSoundEventKind.StartReload);
    }

    [Fact]
    public void OwnMissileReload_RecordsSalReloadingMissiles()
    {
        // Missile pack (EquipmentType.Magazine): clintlib.cpp:3221.
        var w = ReloadWorld(me: 913,
            [(mount: -1, partId: 930, type: ReloadPart(930, new DataPartTypeIGC.MissileLauncher(5)))]);
        w.Apply(new Reload { ShipID = 913, Entries = [new ReloadData((short)-1, (sbyte)-1)] });

        var evs = w.TakeSoundEvents();
        Assert.Contains(evs, e => e.Kind == ClientSoundEventKind.SalReloadingMissiles);
        Assert.Contains(evs, e => e.Kind == ClientSoundEventKind.StartReload);
    }

    [Fact]
    public void OwnChaffReload_RecordsSalReloadingChaff()
    {
        var w = ReloadWorld(me: 914,
            [(mount: -1, partId: 931, type: ReloadPart(931, new DataPartTypeIGC.ChaffLauncher(6)))]);
        w.Apply(new Reload { ShipID = 914, Entries = [new ReloadData((short)-1, (sbyte)-1)] });

        var evs = w.TakeSoundEvents();
        Assert.Contains(evs, e => e.Kind == ClientSoundEventKind.SalReloadingChaff);
        Assert.Contains(evs, e => e.Kind == ClientSoundEventKind.StartReload);
    }

    [Fact]
    public void OwnDispenserReload_RecordsSalReloadingDispenser()
    {
        // Mine pack (EquipmentType.Dispenser): clintlib.cpp:3403 default.
        var w = ReloadWorld(me: 915,
            [(mount: -1, partId: 932, type: ReloadPart(932, new DataPartTypeIGC.MineLauncher(7)))]);
        w.Apply(new Reload { ShipID = 915, Entries = [new ReloadData((short)-1, (sbyte)-1)] });

        var evs = w.TakeSoundEvents();
        Assert.Contains(evs, e => e.Kind == ClientSoundEventKind.SalReloadingDispenser);
        Assert.Contains(evs, e => e.Kind == ClientSoundEventKind.StartReload);
    }

    [Fact]
    public void OtherShipReload_RecordsNothing()
    {
        // Native gates all reload one-shots to the local ship (pShip == m_ship).
        var w = ReloadWorld(me: 916,
            [(mount: -1, partId: 933, type: ReloadPart(933, new DataPartTypeIGC.Pack(PackType: 0, Amount: 100)))]);
        w.Apply(new Reload { ShipID = 917, Entries = [new ReloadData((short)-1, (sbyte)-1)] });

        Assert.Empty(w.TakeSoundEvents());
    }

    [Fact]
    public void NonReloadableCargoType_StillRecordsGenericStartReload()
    {
        // A weapon (not a pack/launcher) reloads nothing type-specific in
        // native's Reload, but the generic startReloadSound is always played on
        // a successful reload entry.
        var w = ReloadWorld(me: 918,
            [(mount: -1, partId: 934, type: ReloadPart(934, new DataPartTypeIGC.Weapon(0,0,0,0,1,1,0,0,0)))]);
        w.Apply(new Reload { ShipID = 918, Entries = [new ReloadData((short)-1, (sbyte)-1)] });

        Assert.Equal(ClientSoundEventKind.StartReload, Assert.Single(w.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void ReloadOfAbsentCargoSlot_RecordsNothing()
    {
        // No cargo item at the relayed slot: nothing was classified.
        var w = ReloadWorld(me: 919,
            [(mount: -1, partId: 935, type: ReloadPart(935, new DataPartTypeIGC.Pack(PackType: 0, Amount: 100)))]);
        w.Apply(new Reload { ShipID = 919, Entries = [new ReloadData((short)-1, (sbyte)-5)] });

        Assert.Empty(w.TakeSoundEvents());
    }
}
