using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P822 (SIM-0470) — the two remaining native differences in the turret relay,
/// pinned against <c>FedSrv/FedSrv.CPP</c>:
/// <list type="number">
/// <item><b>One-shot relay.</b> <c>SendShipUpdates</c> gathers the relay lists and
/// then calls <c>pfsPlayer-&gt;ResetLastUpdate()</c> for every player
/// (<c>FedSrv.CPP:3796</c>; <c>fsship.h:290-293</c> sets
/// <c>m_ucLastUpdate = c_ucNone</c>). A stored <c>ClientActiveTurretUpdate</c> is
/// therefore relayed by exactly ONE pass — the first one after it arrived, if
/// <c>|atu.time − g.timeNow| ≤ 1.5 s</c> (<c>FedSrv.CPP:3783-3790</c>) — to every
/// other cluster player (<c>FedSrv.CPP:4049-4071</c>, skipping self at
/// <c>:4058</c>). The port used to re-send the stored update on every pass while
/// it stayed inside the 1.5 s window.</item>
/// <item><b>Active vs inactive selection.</b> Native selects active purely by the
/// last-update kind (<c>if (ucLastUpdate == c_ucActiveTurretUpdate)</c>, no weapons
/// check — <c>FedSrv.CPP:3783-3789</c>) and inactive only in the <c>else</c> branch
/// when no weapon state bit is set (<c>FedSrv.CPP:3791-3794</c>), so a gunner that
/// just sent a fresh turret update is relayed as active even while it is not
/// firing, and a stale active update with weapons on is relayed as neither. The
/// port used to require <c>weaponsMaskIGC</c> for the active relay and to put
/// every weapons-off gunner in the inactive list.</item>
/// </list>
/// Each behaviour runs with <c>sim.nativeBroadcastOrder</c> off/on, interest tiers
/// off/on and parallel projection off/on, i.e. through the legacy per-peer sender,
/// the P677 tier sender, the P603 serial and parallel sector workers and the P669
/// immutable-snapshot publisher.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P822TurretRelayOneShotTests
{
    private static ServerConfig Config(bool nativeOrder, bool tiers, bool parallel)
    {
        var config = UpdateCadenceTests.Config();
        // Reflection keeps the fixture executable on bases that predate the flag;
        // the resolver behind ALLEGIANCE_SIM_NATIVE_BROADCAST_ORDER is identical
        // (P788cConfigTests pins the env-var form).
        typeof(SimConfig).GetProperty("NativeBroadcastOrder")?.SetValue(config.Sim, nativeOrder);
        config.Broadcast!.InterestTiers = tiers;
        // Tiers and the immutable snapshot require encode-once; "parallel" alone
        // exercises the parallel projection with the legacy sender.
        config.Broadcast.ImmutableSnapshot = tiers || parallel;
        config.Broadcast.EncodeOnce = tiers || parallel;
        config.Broadcast.ParallelBuild = parallel;
        return config;
    }

    private static readonly MethodInfo Dispatch =
        typeof(Srv).GetMethod("ProcessDatagram", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void Receive(Srv server, ConnectionId connection, IFedMessage message)
        => Dispatch.Invoke(server, [connection, FedPacket.Pack([message.Encode()])]);

    /// <summary>The real FM_C_ACTIVE_TURRET_UPDATE(99) wire path, so the handler's
    /// own acceptance/monotonic/freshness gates decide what gets stored. Returns
    /// the client time it carried, so a test can prove a later pass still sits
    /// inside native's ±1.5 s relay window.</summary>
    private static uint SendActiveTurret(Srv server, ConnectionId connection)
    {
        uint time = (uint)(server.SimNow * 1000 + 1);
        Receive(server, connection, new ActiveTurretUpdate
        {
            Atu = ClientActiveTurretUpdate.From(time, ControlData.Neutral, Orientation.Identity),
        });
        return time;
    }

    /// <summary>Per-connection outbound frames for the current pass. The snapshot
    /// publisher runs the capture off the sim thread, hence the gate.</summary>
    private sealed class Capture
    {
        private readonly Dictionary<ConnectionId, List<byte[]>> _frames = new();
        private readonly object _gate = new();

        public Action<ConnectionId, byte[], int, bool> Sink => (conn, bytes, _, _) =>
        {
            lock (_gate)
            {
                if (!_frames.TryGetValue(conn, out var list)) _frames[conn] = list = new();
                list.Add(bytes);
            }
        };

        public void Clear() { lock (_gate) _frames.Clear(); }

        public List<byte[]> Frames(ConnectionId conn)
        {
            lock (_gate) return _frames.TryGetValue(conn, out var list) ? [.. list] : [];
        }
    }

    private static IEnumerable<byte[]> Messages(IEnumerable<byte[]> frames, ushort id)
        => frames.SelectMany(p => FedPacket.Unpack(p)).Where(m => (ushort)(m[2] | m[3] << 8) == id);

    private static HeavyShipsUpdate[] Heavy(Capture capture, ConnectionId conn)
        => Messages(capture.Frames(conn), HeavyShipsUpdate.Fmid).Select(m => HeavyShipsUpdate.Decode(m)).ToArray();

    private static short[] InactiveIds(Capture capture, ConnectionId conn)
        => Messages(capture.Frames(conn), LightShipsUpdate.Fmid)
            .Select(m => LightShipsUpdate.Decode(m)).SelectMany(u => u.InactiveTurretShipIds).ToArray();

    private sealed record Fixture(Srv Server,
        List<(ConnectionId Conn, Ship Ship, Sector Sector)> Peers, Ship Carrier, Ship Gunner);

    /// <summary>A carrier with a boarded gunner plus two further seated cluster
    /// players: the gunner's own connection, its carrier's and two unrelated
    /// viewers all sit in the same sector (native's per-cluster relay audience).</summary>
    private static Fixture Boot(bool nativeOrder, bool tiers, bool parallel)
    {
        var server = new Srv();
        server.Boot(Config(nativeOrder, tiers, parallel));
        server.TestMission!.Stage = MissionStage.Started;
        var peers = UpdateCadenceTests.Seed(server, ships: 6, viewers: 4);
        var carrier = peers[0].Ship;
        var gunner = peers[1].Ship;
        gunner.ParentShip = carrier;
        gunner.TurretId = 0;
        return new Fixture(server, peers, carrier, gunner);
    }

    private static void Pass(Srv server, Capture capture)
    {
        capture.Clear();
        server.TickSim();
        server.FlushBroadcastSnapshots();
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Fresh_active_update_is_relayed_in_exactly_one_pass_to_every_other_cluster_player(
        bool nativeOrder, bool tiers, bool parallel)
    {
        Require.Artwork();
        var fixture = Boot(nativeOrder, tiers, parallel);
        using var server = fixture.Server;
        var gunner = fixture.Gunner;
        var capture = new Capture();
        server.TestPublicationCapture = capture.Sink;
        try
        {
            uint atuTime = SendActiveTurret(server, fixture.Peers[1].Conn);

            // Pass N (the first ship-update pass after the update arrived).
            Pass(server, capture);
            foreach (var other in new[] { 0, 2, 3 })
            {
                var acts = Heavy(capture, fixture.Peers[other].Conn).Single().ActiveTurretUpdates;
                Assert.Single(acts);
                Assert.Equal(gunner.Id, acts[0].ShipId);
            }
            // Native skips the recipient's own gunner ship (FedSrv.CPP:4058) and
            // does not put a firing gunner in the inactive list.
            Assert.Empty(Heavy(capture, fixture.Peers[1].Conn).Single().ActiveTurretUpdates);
            Assert.DoesNotContain(gunner.Id, InactiveIds(capture, fixture.Peers[1].Conn));

            // Pass N+1: the stored m_atu is still there and still inside the 1.5 s
            // window, but ResetLastUpdate consumed its kind (FedSrv.CPP:3796) —
            // nothing is relayed again, and with weapons still on the gunner is
            // neither active nor inactive (FedSrv.CPP:3783-3791).
            Pass(server, capture);
            // The stored m_atu is still fresh, so pass N+1's silence can only be
            // the consumed kind, not the age gate (FedSrv.CPP:3786-3789).
            Assert.True(Math.Abs(atuTime / 1000.0 - gunner.Mission!.LastUpdate) <= 1.5,
                $"stored update must still be inside the ±1.5 s window (dt={atuTime / 1000.0 - gunner.Mission.LastUpdate:F3}s)");
            foreach (var peer in fixture.Peers)
            {
                Assert.Empty(Heavy(capture, peer.Conn).Single().ActiveTurretUpdates);
                Assert.DoesNotContain(gunner.Id, InactiveIds(capture, peer.Conn));
            }
        }
        finally { server.TestPublicationCapture = null; }
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Aiming_non_firing_gunner_with_a_fresh_update_is_relayed_as_active(
        bool nativeOrder, bool tiers, bool parallel)
    {
        Require.Artwork();
        var fixture = Boot(nativeOrder, tiers, parallel);
        using var server = fixture.Server;
        var gunner = fixture.Gunner;
        var capture = new Capture();
        server.TestPublicationCapture = capture.Sink;
        try
        {
            SendActiveTurret(server, fixture.Peers[1].Conn);
            // The gunner is aiming, not shooting: native's relay decision reads the
            // update KIND only, never the weapons state (FedSrv.CPP:3783-3789).
            gunner.StateM = 0;

            Pass(server, capture);
            foreach (var other in new[] { 0, 2, 3 })
                Assert.Equal([gunner.Id], Heavy(capture, fixture.Peers[other].Conn).Single()
                    .ActiveTurretUpdates.Select(a => a.ShipId).ToArray());
            foreach (var peer in fixture.Peers)
                Assert.DoesNotContain(gunner.Id, InactiveIds(capture, peer.Conn));

            // Next pass: the kind is consumed, weapons are still off, so the same
            // gunner now travels in the inactive list instead.
            Pass(server, capture);
            foreach (var peer in fixture.Peers)
            {
                Assert.Empty(Heavy(capture, peer.Conn).Single().ActiveTurretUpdates);
                Assert.Equal([gunner.Id], InactiveIds(capture, peer.Conn));
            }
        }
        finally { server.TestPublicationCapture = null; }
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Weapons_off_gunner_without_a_pending_update_is_inactive(
        bool nativeOrder, bool tiers, bool parallel)
    {
        Require.Artwork();
        var fixture = Boot(nativeOrder, tiers, parallel);
        using var server = fixture.Server;
        var gunner = fixture.Gunner;
        var capture = new Capture();
        server.TestPublicationCapture = capture.Sink;
        try
        {
            // The gunner fires (armed active kind), then stops in the same pass:
            // FM_C_INACTIVE_TURRET_UPDATE clears the shooting state
            // (fsship.cpp:1222-1223) and overwrites the kind, so the pending active
            // relay must NOT win over native's inactive branch (FedSrv.CPP:3791).
            SendActiveTurret(server, fixture.Peers[1].Conn);
            Receive(server, fixture.Peers[1].Conn, new InactiveTurretUpdate());

            Pass(server, capture);
            // Native fills the inactive list once for every cluster player,
            // including the gunner itself (FedSrv.CPP:4009-4020).
            foreach (var peer in fixture.Peers)
            {
                Assert.Equal([gunner.Id], InactiveIds(capture, peer.Conn));
                Assert.Empty(Heavy(capture, peer.Conn).Single().ActiveTurretUpdates);
            }
        }
        finally { server.TestPublicationCapture = null; }
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Stale_active_update_with_weapons_on_is_neither_active_nor_inactive(
        bool nativeOrder, bool tiers, bool parallel)
    {
        Require.Artwork();
        var fixture = Boot(nativeOrder, tiers, parallel);
        using var server = fixture.Server;
        var gunner = fixture.Gunner;
        var mission = server.TestMission!;
        var capture = new Capture();
        server.TestPublicationCapture = capture.Sink;
        try
        {
            // Accepted while fresh (native stores m_atu regardless), but the pass
            // runs after the ±1.5 s window closed: the active branch's age check
            // fails (FedSrv.CPP:3786-3789) and the inactive branch is not reached
            // because weapons are on (FedSrv.CPP:3791).
            server.RecordAcceptedActiveTurretUpdate(gunner,
                (uint)Math.Round((mission.LastUpdate + 2.0) * 1000.0),
                ControlData.Neutral, Orientation.Identity);
            gunner.StateM = ShipControlState.WeaponsMask;

            Pass(server, capture);
            foreach (var peer in fixture.Peers)
            {
                Assert.Empty(Heavy(capture, peer.Conn).Single().ActiveTurretUpdates);
                Assert.DoesNotContain(gunner.Id, InactiveIds(capture, peer.Conn));
            }

            // The stale kind is consumed by that pass too (native resets it after
            // every gather), so a second pass is just as empty.
            Pass(server, capture);
            foreach (var peer in fixture.Peers)
            {
                Assert.Empty(Heavy(capture, peer.Conn).Single().ActiveTurretUpdates);
                Assert.DoesNotContain(gunner.Id, InactiveIds(capture, peer.Conn));
            }
        }
        finally { server.TestPublicationCapture = null; }
    }
}
