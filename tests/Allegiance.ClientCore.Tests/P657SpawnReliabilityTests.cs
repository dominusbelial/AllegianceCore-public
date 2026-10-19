using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;
using ModelContextProtocol.Protocol;
using BotClass = global::Allegiance.Bot.Bot;

namespace Allegiance.ClientCore.Tests;

public sealed class P657SpawnReliabilityTests
{
    private static ServerShipUpdate Pose(short shipId)
    {
        var bytes = new byte[ServerShipUpdate.Width];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, shipId);
        var reader = new WireReader(bytes);
        return ServerShipUpdate.DecodeBody(ref reader);
    }

    // Exercises the real receive dispatch, without creating a transport/socket.
    private static void Feed(ITransportSite peer, IFedMessage message) =>
        peer.OnReceive(ConnectionId.Server, FedPacket.Pack(message.Encode()));

    private sealed class Peer : IDisposable
    {
        public ConnectionSession? Combat { get; }
        public BotClass? Plain { get; }
        public ITransportSite Wire => (ITransportSite?)Combat ?? Plain!;
        public bool Disposed { get; private set; }
        public Peer(bool combat)
        {
            if (combat) Combat = new ConnectionSession(); else Plain = new BotClass { AutoSelectSideAfterLogon = false };
            Feed(Wire, new SetCluster { Ship = Pose(7), SectorId = 2 });
        }
        public Func<bool> Observe() => Combat is { } cs ? SpawnLaunch.Observe(cs, 7) : SpawnLaunch.Observe(Plain!, 7);
        public IReadOnlyList<BuyLoadoutAck> Acks() => Combat?.BuyLoadoutAcks ?? Plain!.BuyLoadoutAcks;
        public void Dispose() { Disposed = true; Combat?.Dispose(); Plain?.Dispose(); }
    }

    private static bool ImmediateWait(Func<bool> done, int ms) => done();
    private static BuyLoadoutAck Ack() => new() { HullId = 15, FLaunch = true };
    private static void Validate(BuyLoadoutAck ack) => Assert.Equal((short)15, ack.HullId);

    [Theory]
    [InlineData(true, "cluster")]
    [InlineData(false, "cluster")]
    [InlineData(true, "undock")]
    [InlineData(false, "undock")]
    [InlineData(true, "status")]
    [InlineData(false, "status")]
    public void OwnSessionConfirmsLaunch_WhileProbeNeverSeesShip(bool combat, string signal)
    {
        using var probe = new ConnectionSession(); // Empty throughout; no probe ship telemetry.
        using var peer = new Peer(combat);
        Feed(peer.Wire, new DockedNotify { StationId = 23 });
        var own = peer.Observe();
        int sends = 0;
        void Send()
        {
            sends++;
            Feed(peer.Wire, Ack());
            IFedMessage message = signal switch
            {
                "cluster" => new SetCluster { Ship = Pose(7), SectorId = 2 },
                "undock" => new DockedNotify { StationId = -1 },
                _ => new ShipStatusMessage { ShipId = 7, HullId = 15, SectorId = 2, StationId = -1 },
            };
            Feed(peer.Wire, message);
        }
        var ack = SpawnLaunch.Confirm("bot", () => true, own, Send, peer.Acks, Validate, 15000, ImmediateWait);
        Assert.Equal((short)15, ack.HullId);
        Assert.Equal(1, sends);
        Assert.Empty(probe.World.Ships);
        Assert.False(peer.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Late_seat_dock_does_not_mask_newer_launch_and_later_redock_revokes_it(bool combat)
    {
        using var peer = new Peer(combat);
        var own = peer.Observe();
        // Observe is installed after SET_CLUSTER, before the rest of the join
        // burst arrives. A dock history must not permanently override status.
        Feed(peer.Wire, new DockedNotify { StationId = 23 });
        Assert.False(own());
        Feed(peer.Wire, new ShipStatusMessage
        {
            ShipId = 7, HullId = 15, SectorId = 2, StationId = -1,
            State = ShipStatusMessage.StateFlying, StateTime = 1,
        });
        Assert.True(own());
        Feed(peer.Wire, new DockedNotify { StationId = 23 });
        Assert.False(own());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StaleSeatAndPurchaseAck_WithoutOwnLaunch_TimeOutDisposeAndReportFailure(bool combat)
    {
        var peer = new Peer(combat);
        var own = peer.Observe(); // Old seat is already in space, but must not pass.
        Assert.False(own());
        int sends = 0, waits = 0;
        bool Wait(Func<bool> done, int ms)
        {
            Assert.Equal(15000, ms);
            waits++;
            Assert.False(done());
            return false; // Simulated full timeout, no socket or 30-second sleep.
        }
        BotManager.SpawnResult Spawn()
        {
            using var pending = new PendingOwnership();
            pending.Own(peer);
            SpawnLaunch.Confirm("bot", () => true, own, () => { sends++; Feed(peer.Wire, Ack()); }, peer.Acks, Validate, 15000, Wait);
            pending.Transfer();
            return Result("bot", 0);
        }
        var batch = new SpawnBatch([new(0, 0, "fighter", Spawn)]);
        batch.Run(false, _ => !peer.Disposed);
        Assert.Equal(2, sends);
        Assert.Equal(2, waits);
        Assert.True(peer.Disposed);
        Assert.Equal(new SpawnBatch.SideCounts(0, 1, 0, 1, 0), batch.Counts(0));
        Assert.Empty(batch.Results);
        Assert.Contains("own session never confirmed launch", Assert.Single(batch.Failures).error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaunchOnSecondPurchase_SucceedsWithExactlyOneRetry(bool combat)
    {
        using var peer = new Peer(combat);
        var own = peer.Observe();
        int sends = 0;
        SpawnLaunch.Confirm("bot", () => true, own, () =>
        {
            Feed(peer.Wire, Ack());
            if (++sends == 2) Feed(peer.Wire, new SetCluster { Ship = Pose(7), SectorId = 2 });
        }, peer.Acks, Validate, 15000, ImmediateWait);
        Assert.Equal(2, sends);
    }

    [Fact]
    public void RealWait_IsBounded_AndMissingAckIsReported()
    {
        int sends = 0;
        var ex = Assert.Throws<InvalidOperationException>(() => SpawnLaunch.Confirm("bot", () => true, () => false,
            () => sends++, () => [], Validate, 1));
        Assert.Equal(2, sends);
        Assert.Contains("no BUY_LOADOUT_ACK", ex.Message);
        Assert.Contains("1 ms each", ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OtherShipsFlyingStatus_CannotConfirmOwnLaunch(bool combat)
    {
        using var peer = new Peer(combat);
        var own = peer.Observe();
        Feed(peer.Wire, new ShipStatusMessage { ShipId = 8, SectorId = 2, StationId = -1 });
        Assert.False(own());
    }

    [Fact]
    public void DisconnectDuringLaunch_FailsWithoutSecondPurchase()
    {
        bool connected = true;
        int sends = 0;
        var ex = Assert.Throws<InvalidOperationException>(() => SpawnLaunch.Confirm("bot", () => connected, () => true,
            () => { sends++; connected = false; }, () => [Ack()], Validate, 15000, ImmediateWait));
        Assert.Equal(1, sends);
        Assert.Contains("disconnected during launch", ex.Message);
    }

    [Fact]
    public void RefusedLoadout_FailsEvenWhenOwnShipIsFlying()
    {
        int sends = 0;
        var ex = Assert.Throws<InvalidOperationException>(() => SpawnLaunch.Confirm("bot", () => true, () => true,
            () => sends++, () => [new BuyLoadoutAck { HullId = 16 }],
            _ => throw new InvalidOperationException("REFUSED hull 15"), 15000, ImmediateWait));
        Assert.Equal(1, sends);
        Assert.Contains("REFUSED", ex.Message);
    }

    [Fact]
    public void LateSeatAck_IsIgnoredUntilRequestedPurchaseAckArrives()
    {
        var acks = new List<BuyLoadoutAck>();
        int validations = 0;
        bool Wait(Func<bool> done, int ms)
        {
            Assert.False(done());
            acks.Add(Ack());
            return done();
        }
        var granted = SpawnLaunch.Confirm("bot", () => true, () => true,
            () => acks.Add(new BuyLoadoutAck { HullId = 16 }), () => acks,
            a => { Validate(a); validations++; }, 15000, Wait, a => a.HullId == 15);
        Assert.Equal((short)15, granted.HullId);
        Assert.Equal(1, validations);
    }

    private static BotManager.SpawnResult Result(string name, short side) =>
        new(name, 7, side, "fighter", 1, new(15, []), null);

    [Theory]
    [InlineData(false, 1, 1, 0)]
    [InlineData(true, 2, 0, 1)]
    public void ScenarioCountsSuccessfulSlotsPerSide_RefillRestoresBalance(bool refill, int spawned, int failed, int refilled)
    {
        int attempts = 0;
        var batch = new SpawnBatch([
            new(0, 0, "fighter", () => Result("a0", 0)),
            new(0, 1, "fighter", () => Result("b0", 1)),
            new(1, 0, "fighter", () => ++attempts == 1 ? throw new InvalidOperationException("launch timed out") : Result("a1", 0)),
            new(1, 1, "bomber", () => Result("b1", 1)),
        ]);
        batch.Run(refill, _ => true);
        Assert.Equal(new SpawnBatch.SideCounts(0, 2, spawned, failed, refilled), batch.Counts(0));
        Assert.Equal(new SpawnBatch.SideCounts(1, 2, 2, 0, 0), batch.Counts(1));
        var failure = Assert.Single(batch.Failures);
        Assert.Equal(1, failure.attempt);
        Assert.Equal("launch timed out", failure.error);
        Assert.Equal(refill ? 2 : 1, attempts);
        // Same counters serialized by the homogeneous and fleet tool responses.
        var json = JsonSerializer.SerializeToElement(batch.Counts(0));
        Assert.Equal(spawned, json.GetProperty("spawned").GetInt32());
        Assert.Equal(2, json.GetProperty("requested").GetInt32());
    }

    [Fact]
    public void PermanentlyFailedSlots_GetOnlyOneRefill_AndRetainBothReasons()
    {
        int attempts = 0;
        var batch = new SpawnBatch([new(0, 1, "bomber", () => throw new InvalidOperationException($"timeout {++attempts}"))]);
        batch.Run(true, _ => true);
        Assert.Equal(2, attempts);
        Assert.Equal(new SpawnBatch.SideCounts(1, 1, 0, 1, 0), batch.Counts(1));
        Assert.Equal(new[] { "timeout 1", "timeout 2" }, batch.Failures.Select(f => f.error));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void BotDisposedWhileLaterSlotsSpawn_IsRemovedFromSummary_AndMayBeRefilled(bool refill, int spawned)
    {
        bool firstAlive = true;
        int attempts = 0;
        var batch = new SpawnBatch([
            new(0, 0, "fighter", () => Result(++attempts == 1 ? "original" : "replacement", 0)),
            new(0, 1, "fighter", () => { firstAlive = false; return Result("b", 1); }),
        ]);
        batch.Run(refill, r => r.Name != "original" || firstAlive);
        Assert.Equal(spawned, batch.Counts(0).spawned);
        Assert.DoesNotContain(batch.Results, r => r.Name == "original");
        Assert.Contains("disposed during scenario", Assert.Single(batch.Failures).error);
    }

    [Fact]
    public void SpawnBotError_ReportsRequestedSideCountsAndReason()
    {
        var manager = (BotManager)RuntimeHelpers.GetUninitializedObject(typeof(BotManager));
        var response = McpTools.Handle("spawn_bot", new Dictionary<string, JsonElement>
        {
            ["side"] = JsonSerializer.SerializeToElement(1),
        }, manager);
        Assert.True(response.IsError);
        var json = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text).RootElement;
        Assert.Equal(1, json.GetProperty("requested").GetInt32());
        Assert.Equal(0, json.GetProperty("spawned").GetInt32());
        Assert.Equal(1, json.GetProperty("failed").GetInt32());
        Assert.Equal(1, json.GetProperty("sides")[0].GetProperty("side").GetInt32());
        Assert.Contains("hull", json.GetProperty("failures")[0].GetProperty("error").GetString());
    }
}
