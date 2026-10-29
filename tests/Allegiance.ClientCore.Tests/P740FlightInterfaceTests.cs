using System.Buffers.Binary;
using System.Collections.Concurrent;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P740FlightInterfaceTests
{
    private static void Feed(ConnectionSession session, params IFedMessage[] messages) => P718InterfaceMessageTests.Feed(session, messages);
    private static DataPartTypeIGC? Part(short id) => id is 10 or 20 ? new DataPartTypeIGC(0, 0, "", "", "Beacon", "", 0,
        [], [], 0, 0, id, -1, 1, "", id == 10 ? new DataPartTypeIGC.ProbeLauncher(0) : new DataPartTypeIGC.ChaffLauncher(0)) : null;
    private static void Seed(ConnectionSession session)
    {
        P718InterfaceMessageTests.Seed(session);
        session.World.PartTypeResolver = Part;
        Feed(session, new ExportShip { ShipId = 10, SideId = 0, HullId = 5, CharacterName = "Me",
                Parts = [new(10, 4, 0), new(20, 3, 0)] },
            new ExportShip { ShipId = 11, SideId = 0, HullId = 5, CharacterName = "Scout", Parts = [new(10, 4, 0)] },
            new ShipStatusMessage { ShipId = 12, SectorId = 1, HullId = 5, State = ShipStatusMessage.StateFlying });
        session.World.TakeSoundEvents();
    }

    // Independent native layouts: Inc/Messages.h:619,861,881,940,988,1006.
    // RELAUNCH's floats/var item/padding are written by offset, never its codec.
    public static IEnumerable<object[]> GoldenMessages()
    {
        yield return [87, "080057000B000300"];
        yield return [156, "0C009C0001000B0000000000"];
        yield return [164, "0C00A4000B000A000300FF00"];
        yield return [182, "0C00B6007B0000000B000700"];
        var relaunch = new byte[50];
        BinaryPrimitives.WriteUInt16LittleEndian(relaunch, 50);
        BinaryPrimitives.WriteUInt16LittleEndian(relaunch.AsSpan(2), 192);
        BinaryPrimitives.WriteUInt16LittleEndian(relaunch.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(relaunch.AsSpan(6), 2);
        BinaryPrimitives.WriteInt16LittleEndian(relaunch.AsSpan(8), 11);
        BinaryPrimitives.WriteInt16LittleEndian(relaunch.AsSpan(10), 12);
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteSingleLittleEndian(relaunch.AsSpan(12 + 4 * i), i + 1);
        relaunch[36] = relaunch[37] = 255; // identity quaternion, a=65535
        relaunch[44] = 85;
        BinaryPrimitives.WriteInt16LittleEndian(relaunch.AsSpan(48), 20);
        yield return [192, Convert.ToHexString(relaunch)];
        yield return [194, "0A00C2000B0096000700"];
    }
    private static IFedMessage Sample(int id) => id switch
    {
        87 => new FireExpendable { LauncherId = 11, EquipmentType = EquipmentType.Dispenser },
        156 => new GainFlag { ShipidRecipient = 11, SideidFlag = 1 },
        164 => new AddPart { ShipID = 11, NewPartData = new(10, 3, 255) },
        182 => new RequestMoney { ShipIdRequest = 11, Amount = 123, HidFor = 7 },
        192 => new RelaunchShip { ShipId = 11, CarrierId = 12, HullId = 20, Position = new(1, 2, 3), Velocity = new(4, 5, 6), Cookie = 85 },
        194 => new PingData { ShipId = 11, Ping = 150, Loss = 7 },
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
    private static IFedMessage Decode(int id, byte[] b) => id switch
    {
        87 => FireExpendable.Decode(b), 156 => GainFlag.Decode(b), 164 => AddPart.Decode(b),
        182 => RequestMoney.Decode(b), 192 => RelaunchShip.Decode(b), 194 => PingData.Decode(b),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    private sealed class Site : ITransportSite
    {
        public readonly TaskCompletionSource<ConnectionId> Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<byte[]> Received = new();
        public void OnPeerJoined(ConnectionId id, string name) => Joined.TrySetResult(id);
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId id, ReadOnlySpan<byte> bytes) => Received.Enqueue(bytes.ToArray());
    }

    [Theory]
    [MemberData(nameof(GoldenMessages))]
    public async Task Native_golden_frames_cross_actual_session_tcp_and_mutate_world_in_order(int id, string hex)
    {
        var site = new Site(); var host = new TcpTransport(site);
        using var session = new ConnectionSession();
        try
        {
            host.Host(0); session.Connect(host.BoundPort, "P740");
            var peer = await site.Joined.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (session.Gate) Seed(session);
            byte[] golden = Convert.FromHexString(hex);
            Assert.Equal(golden, Sample(id).Encode());
            Assert.Equal(golden, Decode(id, golden).Encode());
            host.Send(peer, FedPacket.Pack([new EnterGame().Encode(), golden]), true);
            // A separate packet fences TCP order and the actual receive dispatch.
            host.Send(peer, FedPacket.Pack(new ChangeTeamCiv { SideId = 1, CivId = 7 }.Encode()), true);
            var stop = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < stop)
            {
                lock (session.Gate) if (session.World.SideCivilizations.TryGetValue(1, out var state) && state.CivId == 7) break;
                await Task.Delay(5);
            }
            lock (session.Gate)
            {
                Assert.Equal((short)7, session.World.SideCivilizations[1].CivId);
                Assert.DoesNotContain((ushort)id, session.UnknownFmids);
                var ship = session.World.Ships[11];
                switch (id)
                {
                    case 87: Assert.Equal(3, Assert.Single(ship.Loadout!).Amount); break;
                    case 156:
                        Assert.Equal(1, ship.CarriedFlagSideId);
                        Assert.Equal("Scout has stolen Bravo's flag.", Assert.Single(session.World.TakeFeedbackLines()).Text); break;
                    case 164: Assert.Equal(new ExpandedPartData(10, 3, 255, 0), Assert.Single(ship.Cargo!)); break;
                    case 182: Assert.Equal(123, session.World.PendingMoneyRequest!.Value.Amount); break;
                    case 192:
                        Assert.Equal(20, ship.HullId); Assert.Equal(new Vector3(1, 2, 3), ship.Latest!.Position);
                        Assert.Equal(1f, ship.Latest.AmmoFraction); Assert.Empty(ship.Loadout!); break;
                    case 194: Assert.Equal(new ClientWorld.PlayerConnectionData(150, 7), session.World.PlayerConnections[11]); break;
                }
            }
        }
        finally { host.Shutdown(); }
    }

    [Fact]
    public void Add_part_gates_and_merges_only_same_type_in_the_named_socket_including_own_echo()
    {
        using var s = new ConnectionSession(); Seed(s);
        var add = new AddPart { ShipID = 10, NewPartData = new(10, 2, 0) };
        Feed(s, add); Assert.Equal(4, s.World.Ships[10].Loadout![0].Amount);
        Feed(s, new EnterGame(), add, new AddPart { ShipID = 10, NewPartData = new(20, 2, 255) },
            new AddPart { ShipID = 10, NewPartData = new(10, 2, 255) }, // occupied cargo of different type: ignore
            new AddPart { ShipID = 99, NewPartData = new(10, 2, 0) },
            new AddPart { ShipID = 10, NewPartData = new(99, 2, 1) },
            new AddPart { ShipID = 10, NewPartData = new(10, 2, 250) });
        var ship = s.World.Ships[10];
        Assert.Equal(6, ship.Loadout![0].Amount); Assert.Equal(3, ship.Loadout[1].Amount);
        Assert.Equal(new ExpandedPartData(20, 2, 255, 0), Assert.Single(ship.Cargo!));
        Assert.False(s.World.Ships.ContainsKey(99));
        Feed(s, new Allegiance.Core.Wire.LoadoutChange { SidShip = 11, HullId = 5, Parts = [], Passengers = [new(10, 0)] }, add);
        Assert.Equal(11, ship.ParentId); Assert.Equal(6, ship.Loadout[0].Amount);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void Fire_expendable_decrements_named_mounted_launcher_once_and_never_cargo_or_below_zero(int kind)
    {
        using var s = new ConnectionSession(); Seed(s);
        short id = kind == 0 ? (short)20 : (short)10;
        var fire = new FireExpendable { LauncherId = 10, EquipmentType = (EquipmentType)kind };
        Feed(s, fire); Assert.Equal(kind == 0 ? 3 : 4, s.World.Ships[10].Loadout!.Single(p => p.PartId == id).Amount);
        Feed(s, new EnterGame(), new AddPart { ShipID = 10, NewPartData = new(id, 9, 255) });
        for (int i = 0; i < 8; i++) Feed(s, fire);
        Feed(s, new FireExpendable { LauncherId = 99, EquipmentType = (EquipmentType)kind },
            new FireExpendable { LauncherId = 10, EquipmentType = EquipmentType.Magazine });
        Assert.Equal(0, s.World.Ships[10].Loadout!.Single(p => p.PartId == id).Amount);
        Assert.Equal(9, Assert.Single(s.World.Ships[10].Cargo!).Amount);
        Assert.False(s.World.Ships.ContainsKey(99));
    }

    [Fact]
    public void Flag_clear_tech_recovery_enemy_and_unknown_recipient_preserve_native_notice_and_sound_rules()
    {
        using var s = new ConnectionSession(); Seed(s);
        Feed(s, new GainFlag { ShipidRecipient = 11, SideidFlag = -2 });
        Assert.Equal(-2, s.World.Ships[11].CarriedFlagSideId);
        Assert.Equal("Scout has found an artifact.", Assert.Single(s.World.TakeFeedbackLines()).Text);
        Assert.Equal(ClientSoundEventKind.ArtifactFound, Assert.Single(s.World.TakeSoundEvents()).Kind);
        Feed(s, new GainFlag { ShipidRecipient = 11, SideidFlag = -1, BIsTreasureDocked = true, ParttypeidDocked = 10 });
        Assert.Equal(-2, s.World.Ships[11].CarriedFlagSideId);
        Assert.Equal("Scout has secured Beacon", Assert.Single(s.World.TakeFeedbackLines()).Text);
        Assert.Empty(s.World.TakeSoundEvents());
        Feed(s, new GainFlag { ShipidRecipient = 11, SideidFlag = -1 },
            new GainFlag { ShipidRecipient = 12, SideidFlag = 0 },
            new GainFlag { ShipidRecipient = 12, SideidFlag = -1, BIsTreasureDocked = true, ParttypeidDocked = 10 },
            new GainFlag { ShipidRecipient = 99, SideidFlag = 1 });
        Assert.Equal(-1, s.World.Ships[11].CarriedFlagSideId); Assert.Equal(0, s.World.Ships[12].CarriedFlagSideId);
        Assert.Empty(s.World.TakeFeedbackLines()); Assert.Empty(s.World.TakeSoundEvents()); // native side-zero predicate
        Assert.False(s.World.Ships.ContainsKey(99));
        Feed(s, new ExportShip { ShipId = -1, SideId = 1 }, new GainFlag { ShipidRecipient = 11, SideidFlag = 1 });
        Assert.Equal("Scout of Alpha has stolen our flag.", Assert.Single(s.World.TakeFeedbackLines()).Text);
        Assert.Equal(ClientSoundEventKind.OurFlagStolen, Assert.Single(s.World.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void Flag_announces_ignore_game_over_even_after_back_and_resume_on_next_start()
    {
        using var s = new ConnectionSession(); Seed(s);
        Feed(s, new GameOver { Sides = Enumerable.Range(0, 6).Select(_ => default(SideEndgameInfo) with { SideName = "" }).ToArray() }, new GainFlag { ShipidRecipient = 11, SideidFlag = 1 });
        s.World.ClearGameOver();
        Feed(s, new GainFlag { ShipidRecipient = 11, SideidFlag = 1 });
        Assert.Equal(-1, s.World.Ships[11].CarriedFlagSideId);
        Assert.Empty(s.World.TakeFeedbackLines());
        Feed(s, new MissionStageMessage { Stage = MissionStageMessage.StageStarting },
            new GainFlag { ShipidRecipient = 11, SideidFlag = 1 });
        Assert.Equal(1, s.World.Ships[11].CarriedFlagSideId);
        Assert.Single(s.World.TakeFeedbackLines());
    }

    [Fact]
    public void Money_requests_are_same_side_and_do_not_pay_until_explicit_acceptance()
    {
        using var s = new ConnectionSession(); Seed(s);
        Feed(s, new SetMoney { ShipId = 10, Money = 80 },
            new RequestMoney { ShipIdRequest = 12, Amount = 123, HidFor = 7 },
            new RequestMoney { ShipIdRequest = 99, Amount = 123, HidFor = 7 });
        Assert.Null(s.World.PendingMoneyRequest); Assert.Empty(s.World.TakeFeedbackLines());
        Feed(s, new RequestMoney { ShipIdRequest = 11, Amount = 123, HidFor = 7 });
        Assert.Equal(80, s.World.MyMoney); Assert.Equal(11, s.World.PendingMoneyRequest!.Value.SenderId);
        var donation = Assert.IsType<PlayerDonate>(s.World.AcceptMoneyRequest());
        Assert.Equal(80, donation.MoneyGiven); Assert.Equal(11, donation.ShipID); Assert.Null(s.World.AcceptMoneyRequest());
        Assert.Equal(80, s.World.MyMoney);
    }

    [Theory]
    [InlineData(10)] [InlineData(11)]
    public void Carrier_relaunch_replaces_loadout_pose_and_pools_with_own_only_cookie_camera_and_voice(short id)
    {
        using var s = new ConnectionSession(); Seed(s);
        var message = new RelaunchShip { ShipId = id, CarrierId = 12, HullId = 25, Cookie = 73,
            Position = new(1, 2, 3), Velocity = new(4, 5, 6), Parts = [new(10, 9, 0, 120), new(20, 4, 255, 240)] };
        Feed(s, message); Assert.Equal(5, s.World.Ships[id].HullId);
        Feed(s, new EnterGame(), message);
        var ship = s.World.Ships[id];
        Assert.Equal(25, ship.HullId); Assert.Equal(-1, ship.ParentId); Assert.Equal<short?>(1, ship.PoseSectorId);
        Assert.Equal(new Vector3(1, 2, 3), ship.Latest!.Position);
        Assert.Equal(1f, ship.Latest.AmmoFraction); Assert.Equal(1f, ship.Latest.FuelFraction); Assert.Equal(1f, ship.Latest.EnergyFraction);
        Assert.Equal(message.Parts[0], Assert.Single(ship.Loadout!)); Assert.Equal(message.Parts[1], Assert.Single(ship.Cargo!));
        Assert.Equal(id == 10, ship.HasPendingReset); Assert.Equal(id == 10 ? 73 : 0, ship.Cookie);
        if (id == 10)
        {
            var camera = Assert.Single(s.World.TakeCameraOverrideRequests()); Assert.Equal(12, camera.FocusShipId); Assert.Equal(3, camera.DurationSeconds);
            // Native salRepairedAtCarrierSound is 1386 (Igc/sounds.h:479 with the
            // dead `/* NYI no turrets */` block at 443-459 removed by the
            // preprocessor); P871's 1399 was the generator's +13 bug.
            Assert.Equal(1386, Assert.Single(s.World.TakeSoundEvents()).Id);
        }
        else { Assert.Empty(s.World.TakeCameraOverrideRequests()); Assert.Empty(s.World.TakeSoundEvents()); }
        Feed(s, new RelaunchShip { ShipId = id, CarrierId = 99, HullId = 99 }, new RelaunchShip { ShipId = 99, CarrierId = 12 });
        Assert.Equal(25, ship.HullId); Assert.False(s.World.Ships.ContainsKey(99));
    }

    [Fact]
    public void Ping_data_updates_existing_players_only_and_marker_freezes_native_display_snapshot()
    {
        using var s = new ConnectionSession(); Seed(s);
        Feed(s, new PingData { ShipId = 10, Ping = 1, Loss = 101 }, new PingData { ShipId = 11, Ping = 1001, Loss = 100 },
            new PingData { ShipId = 99, Ping = 20 }, new PingData { ShipId = -1, Ping = 2 });
        Assert.Equal(0, s.World.PlayerPingsRevision); Assert.Empty(s.World.PlayerPingLines);
        Assert.Equal(2, s.World.PlayerConnections.Count);
        Feed(s, new PingData { ShipId = -1, Ping = 1 });
        Assert.Equal(1, s.World.PlayerPingsRevision);
        Assert.Equal(new[] { "You: 10ms / 99% loss", "Scout: 999ms / 100% loss", "Enemy: 10ms / 0% loss", "Average: 339ms / 66% loss" }, s.World.PlayerPingLines);
        var old = s.World.PlayerPingLines;
        Feed(s, new PingData { ShipId = 11, Ping = 1000, Loss = 15 });
        Assert.Same(old, s.World.PlayerPingLines); Assert.Equal((ushort)1000, s.World.PlayerConnections[11].Ping);
        Feed(s, new QuitMission { ShipId = 11 }); Assert.False(s.World.PlayerConnections.ContainsKey(11));
        s.World.Reset(); Assert.Empty(s.World.PlayerConnections); Assert.Empty(s.World.PlayerPingLines); Assert.Equal(0, s.World.PlayerPingsRevision);
    }

    [SkippableFact]
    public void Player_ping_request_uses_real_server_and_final_marker_after_entries()
    {
        Require.Artwork(); using var server = new Allegiance.Server.Server(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var a = P660NextMatchLiveTests.Connect(server, "P740-A", 74001);
        using var b = P660NextMatchLiveTests.Connect(server, "P740-B", 74002);
        var requester = server.Ships.Single(s => s.Ship.Id == b.World.MyShipId).Ship;
        requester.Mission!.Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(requester, a.World.MySideId!.Value));
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(a,
            () => a.World.Ships.TryGetValue(b.World.MyShipId!.Value, out var ship) && ship.Player is not null));
        a.SendPlayerPingsRequest();
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(a, () => a.World.PlayerPingsRevision > 0));
        lock (a.Gate)
        {
            // FedSrv/FedSrv.CPP:7755-7763 includes every live player, including
            // the requester. Keep the final-marker/display assertions below.
            Assert.Contains(a.World.MyShipId!.Value, a.World.PlayerConnections.Keys);
            Assert.Equal(2, a.World.PlayerConnections.Count);
            Assert.Contains(b.World.MyShipId!.Value, a.World.PlayerConnections.Keys);
            Assert.Contains(a.World.PlayerPingLines, l => l.StartsWith("You:"));
            Assert.Contains(a.World.PlayerPingLines, l => l.StartsWith("P740-B:"));
            Assert.StartsWith("Average:", a.World.PlayerPingLines[^1]);
            Assert.DoesNotContain(PingData.Fmid, a.UnknownFmids);
        }
    }
}
