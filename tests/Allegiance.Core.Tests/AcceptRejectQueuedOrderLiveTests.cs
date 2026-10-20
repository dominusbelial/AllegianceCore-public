using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P424 — accept / reject / clear a received queued order (native
/// <c>TK_AcceptCommand</c>/<c>TK_RejectCommand</c>/<c>TK_ClearCommand</c>,
/// WinTrek.cpp:10389-10406) plus the 15&nbsp;s auto-reject timer
/// (<c>c_dtRejectQueuedCommand = 15.0f</c>, WinTrek.cpp:84, 7686-7687).
/// Live-loopback proofs over the real wire:
/// <list type="number">
/// <item><b>Accept promotes queued → accepted</b> — a real player ship has a
/// queued order set (via the P424 test/demo helper
/// <c>Server.QueueCommandForPlayer</c> — the only real-player queued-command
/// writer, see P424_ACCEPT_REJECT_QUEUED_ORDERS.md §3), its owner client is
/// notified (<c>QUEUED_COMMAND_UPDATE(215)</c>), and an ACCEPT over the real
/// ORDER_CHANGE wire moves the queued action + target into the accepted slot
/// and clears the queued slot (native <c>AcceptQueuedCommand</c>).</item>
/// <item><b>Reject clears the queued slot</b> — an ORDER_CHANGE with
/// commandID=c_cidNone on the queued slot clears it (native
/// <c>RejectQueuedCommand</c>).</item>
/// <item><b>15&nbsp;s auto-reject</b> — a queued order left neither accepted nor
/// rejected auto-clears after 15 sim-seconds of inaction (and not before).</item>
/// <item><b>Negatives</b> — accept/reject with no pending queued order are
/// safe no-ops; an accept echoing a different command id than the pending one
/// is refused (no guessing).</item>
/// </list>
/// Harness mirrors P422 (booted in-process server + seated real bot). The
/// "keys" are exercised by sending exactly the ORDER_CHANGE the MonoGame
/// client's ACCEPT/REJECT/CLEAR keys send (the same payloads — see
/// Game1.FlightInput.cs P424 block).
/// </summary>
[Collection("LiveNetwork")]
public sealed class AcceptRejectQueuedOrderLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P424 Accept/Reject Queued Order Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 762u, DwCookie = 0x762u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 66400000000000002ul, DrmHash = "p424queue",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void PumpNTicks(Srv server, int n)
    {
        for (int i = 0; i < n; i++) { server.TickSim(); Thread.Sleep(1); }
    }

    /// <summary>Boot the in-process server + seated real bot, capturing the
    /// bot's real player ship (<c>IsNpc == false</c>) on side 0 and a
    /// same-sector NPC ship to serve as an order target.</summary>
    private static (Srv Server, Mission Mission, Sector Sector, Ship Peer, Ship TargetShip)
        BootFixture(out FltBot bot)
    {
        var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        Ship? peer = null;
        Ship? targetShip = null;
        short peerSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            peerSide = m.Sides[0].Id;

            var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            peer = new Ship(id, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peer, sector);
            return (peer, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby (P180 pattern)
        bot = new FltBot();
        var b = bot;
        b.Connect(server.BoundPort, "p424_queue");
        b.SendLogonRequest(Request("p424_queue"));
        Pump(server, () => mission is not null && b.ShipAssignment is not null, 4000);

        Assert.NotNull(mission); Assert.NotNull(sector); Assert.NotNull(peer);
        Assert.False(peer.IsNpc, "fixture peer must be a real player ship (IsNpc=false) for the queued-order flow");

        // A same-sector NPC ship to serve as the queued order's target model.
        targetShip = server.SpawnNpcShip(mission!, sector!, peerSide, new Vector3(80f, 0f, 0f), null);
        Assert.NotNull(targetShip);

        return (server!, mission!, sector!, peer!, targetShip!);
    }

    /// <summary>Codec: <see cref="QueuedCommandUpdate"/> (215) round-trips its
    /// full shape — the wire path the helper's queued/clear notifications ride.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void QueuedCommandUpdate_roundtrips_full_shape()
    {
        Require.Artwork();
        var msg = new QueuedCommandUpdate
        {
            CommandId = OrderChange.CidGoto,
            TargetType = IgcObjectType.Ship,
            TargetId = 555,
            SenderId = 777,
        };
        var bytes = msg.Encode();
        Assert.Equal(QueuedCommandUpdate.FixedSize, bytes.Length);
        var dec = QueuedCommandUpdate.Decode(bytes);
        Assert.Equal(OrderChange.CidGoto, dec.CommandId);
        Assert.Equal(IgcObjectType.Ship, dec.TargetType);
        Assert.Equal((short)555, dec.TargetId);
        Assert.Equal((short)777, dec.SenderId);
        Assert.True(dec.HasPending);

        // A clear (CidNone) notification has no pending order.
        var clear = new QueuedCommandUpdate { CommandId = OrderChange.CidNone, TargetType = (IgcObjectType)(-1), TargetId = -1, SenderId = -1 };
        var decClear = QueuedCommandUpdate.Decode(clear.Encode());
        Assert.False(decClear.HasPending);
        Assert.Equal((short)-1, decClear.SenderId);
        Console.WriteLine("P424-CODEC: QUEUED_COMMAND_UPDATE(215) queued + cleared shapes round-tripped");
    }

    /// <summary>⭐ Primary: queue a real order for the player (via the P424
    /// test/demo helper), the owner client is notified over the wire, then an
    /// ACCEPT (ORDER_CHANGE command=c_cmdQueued echoing the pending id) promotes
    /// queued → accepted (action + target) and clears the queued slot — native
    /// AcceptQueuedCommand, WinTrek.cpp:10626-10712.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Accept_promotes_queued_to_accepted()
    {
        Require.Artwork();
        var (srv, _, _, peer, targetShip) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            // ── Queue a real "goto that ship" order on the player (helper) ────
            srv.QueueCommandForPlayer(peer, OrderChange.CidGoto, targetShip);
            Assert.Equal(OrderChange.CidGoto, peer.QueuedCommandId);
            Assert.Same(targetShip, peer.QueuedShipTarget);
            Assert.NotEqual(0, peer.QueuedAt);   // auto-reject timer armed

            // Owner client learns of its pending order over the wire.
            int pumped = Pump(srv, () => bot.QueuedCommandUpdates.Any(u => u.HasPending && u.CommandId == OrderChange.CidGoto), 2000);
            Assert.True(pumped < 2000, "owner peer must receive the queued-order notification");
            var queuedUpd = Assert.Single(bot.QueuedCommandUpdates);
            Assert.Equal(OrderChange.CidGoto, queuedUpd.CommandId);
            Assert.Equal(IgcObjectType.Ship, queuedUpd.TargetType);
            Assert.Equal(targetShip.Id, queuedUpd.TargetId);
            Console.WriteLine($"P424-QUEUE: peer {peer.Id} queued order c_cidGoto -> ship {targetShip.Id}, owner notified via QUEUED_COMMAND_UPDATE(215)");

            // ── ACCEPT over the real wire (the key sends this exact payload) ──
            bot.ResetLeadershipSignals();
            bot.SendOrderChange(peer.Id, OrderChange.CidGoto, OrderChange.CmdQueued);
            Pump(srv, () => peer.AcceptedShipCommandId == OrderChange.CidGoto, 2000);

            Assert.Equal(OrderChange.CidGoto, peer.AcceptedShipCommandId);
            Assert.Same(targetShip, peer.AcceptedShipTarget);   // target moved too (WinTrek.cpp:10668-10681)
            Assert.Equal(OrderChange.CidNone, peer.QueuedCommandId);   // queued cleared
            Assert.Null(peer.QueuedShipTarget);
            Assert.Equal(0, peer.QueuedAt);   // timer disarmed

            // Owner client is told the queued slot is now clear.
            Pump(srv, () => bot.QueuedCommandUpdates.Any(u => !u.HasPending), 2000);
            Assert.Contains(bot.QueuedCommandUpdates, u => !u.HasPending);
            Console.WriteLine($"P424-ACCEPT: peer {peer.Id} accepted queued c_cidGoto -> accepted slot (target #{targetShip.Id}), queued cleared, client notified");
        }
    }

    /// <summary>⭐ Reject: an ORDER_CHANGE with commandID=c_cidNone on the
    /// queued slot clears the pending queued order (native RejectQueuedCommand,
    /// WinTrek.cpp:10583-10623). The accepted slot is untouched.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Reject_clears_the_queued_slot()
    {
        Require.Artwork();
        var (srv, _, _, peer, targetShip) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            srv.QueueCommandForPlayer(peer, OrderChange.CidGoto, targetShip);
            Pump(srv, () => bot.QueuedCommandUpdates.Any(u => u.HasPending), 2000);
            Assert.Equal(OrderChange.CidGoto, peer.QueuedCommandId);

            bot.ResetLeadershipSignals();
            bot.SendOrderChange(peer.Id, OrderChange.CidNone, OrderChange.CmdQueued);
            Pump(srv, () => peer.QueuedCommandId == OrderChange.CidNone, 2000);

            Assert.Equal(OrderChange.CidNone, peer.QueuedCommandId);
            Assert.Null(peer.QueuedShipTarget);
            Assert.Equal(OrderChange.CidNone, peer.AcceptedShipCommandId);   // accepted untouched
            Console.WriteLine($"P424-REJECT: peer {peer.Id} rejected queued c_cidGoto -> queued cleared (accepted untouched)");
        }
    }

    /// <summary>⭐ Auto-reject: a queued order left neither accepted nor rejected
    /// survives under 15&nbsp;s but is cleared once the 15&nbsp;s inaction window
    /// elapses (native WinTrek.cpp:7686-7687, c_dtRejectQueuedCommand=15.0f).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Auto_reject_clears_after_15_seconds_of_inaction()
    {
        Require.Artwork();
        var (srv, _, _, peer, targetShip) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            srv.QueueCommandForPlayer(peer, OrderChange.CidGoto, targetShip);
            Pump(srv, () => peer.QueuedCommandId != OrderChange.CidNone, 2000);
            Assert.Equal(OrderChange.CidGoto, peer.QueuedCommandId);

            // Well under 15 s (20 ups → 190 ticks ≈ 9.5 s): still pending.
            PumpNTicks(srv, 190);
            Assert.Equal(OrderChange.CidGoto, peer.QueuedCommandId);
            Console.WriteLine("P424-AUTO: queued order still pending at ~9.5s (<15s)");

            // Past 15 s (another 220 ticks → ~20.5 s total): auto-rejected.
            PumpNTicks(srv, 220);
            Assert.Equal(OrderChange.CidNone, peer.QueuedCommandId);
            Assert.Null(peer.QueuedShipTarget);
            Assert.Equal(0, peer.QueuedAt);
            Console.WriteLine($"P424-AUTO: peer {peer.Id} queued order auto-rejected after >15s inaction");

            // Owner client is told the slot is clear.
            Pump(srv, () => bot.QueuedCommandUpdates.Any(u => !u.HasPending), 2000);
            Assert.Contains(bot.QueuedCommandUpdates, u => !u.HasPending);
        }
    }

    /// <summary>Accept with no pending queued order is a safe no-op (native
    /// cid==c_cidNone → nothing to promote, WinTrek.cpp:10628).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Accept_with_no_pending_order_is_noop()
    {
        Require.Artwork();
        var (srv, _, _, peer, _) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            Assert.Equal(OrderChange.CidNone, peer.QueuedCommandId);
            bot.SendOrderChange(peer.Id, OrderChange.CidGoto, OrderChange.CmdQueued);
            PumpNTicks(srv, 10);
            Assert.Equal(OrderChange.CidNone, peer.AcceptedShipCommandId);
            Assert.Equal(OrderChange.CidNone, peer.QueuedCommandId);
            Console.WriteLine("P424-NEG: ACCEPT with no pending queued order — no-op");
        }
    }

    /// <summary>Reject with no pending queued order is a safe no-op.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Reject_with_no_pending_order_is_noop()
    {
        Require.Artwork();
        var (srv, _, _, peer, _) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            Assert.Equal(OrderChange.CidNone, peer.QueuedCommandId);
            bot.SendOrderChange(peer.Id, OrderChange.CidNone, OrderChange.CmdQueued);
            PumpNTicks(srv, 10);
            Assert.Equal(OrderChange.CidNone, peer.QueuedCommandId);
            Assert.Equal(OrderChange.CidNone, peer.AcceptedShipCommandId);
            Console.WriteLine("P424-NEG: REJECT with no pending queued order — no-op");
        }
    }

    /// <summary>An accept that echoes a command id different from the one
    /// actually pending is refused (no guessing) — the client only ever echoes
    /// its real pending id (world.MyQueuedCommand).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Accept_echoing_a_different_command_id_is_refused()
    {
        Require.Artwork();
        var (srv, _, _, peer, targetShip) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            srv.QueueCommandForPlayer(peer, OrderChange.CidGoto, targetShip);
            Pump(srv, () => peer.QueuedCommandId != OrderChange.CidNone, 2000);
            Assert.Equal(OrderChange.CidGoto, peer.QueuedCommandId);

            // Echo a DIFFERENT command id (c_cidDefend) — not an accept of this order.
            bot.SendOrderChange(peer.Id, OrderChange.CidDefend, OrderChange.CmdQueued);
            PumpNTicks(srv, 10);
            Assert.Equal(OrderChange.CidNone, peer.AcceptedShipCommandId);   // nothing promoted
            Assert.Equal(OrderChange.CidGoto, peer.QueuedCommandId);         // still pending
            Console.WriteLine("P424-NEG: ACCEPT echoing mismatched commandID refused — queued order intact");
        }
    }
}
