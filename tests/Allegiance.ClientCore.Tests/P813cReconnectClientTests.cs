using System.Net;
using System.Net.Sockets;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P813c (EXPERIMENTAL) — the CLIENT half of the reconnect grace, against a real
/// in-process server over real loopback TCP, with a small drop proxy in front so
/// the "blip" is a genuine socket death on BOTH sides (the client sees EOF, the
/// server sees <c>OnPeerLeft</c> and parks the seat).
///
/// <para>Native has no client-side reconnect at all: a dropped connection deletes
/// the player (<c>FedSrv/FedSrv.CPP:4549-4566</c> → <c>fsship.cpp:995,58-68</c>),
/// and the client only ever reacts to the loss
/// (<c>clintlib/clintlib.cpp:2063-2068</c> <c>CheckServerLag</c>). This suite
/// proves the opt-in port behaviour: the session re-dials inside the server's
/// grace window, re-sends the SAME <c>LOGONREQ</c> (the resume token rides the
/// native <c>dwCookie</c> field, <c>Inc/Messages.h:43</c>), receives a validated
/// LOGONACK for the SAME ship id and lets the server's native join burst re-sync
/// the world — same ship, same seat. With the window missed, the session latches
/// <see cref="ConnectionSession.Lost"/> and raises <c>SessionLost</c> exactly as
/// before.</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P813cReconnectClientTests
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    private const short SideId = -1;   // "pick the first side" on POSITIONREQ

    private static Cfg Config(bool reconnect = true, double graceSeconds = 3) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,
        Reconnect = new global::Allegiance.Server.ReconnectConfig { Enabled = reconnect, GraceSeconds = graceSeconds },
        Games =
        [
            new Gm { Name = "P813c Client", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 813u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 8813000000000000001ul, DrmHash = "p813c-client",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(2); }
        return n;
    }

    /// <summary>Deterministic spawn: every player in sector 0, side 0.</summary>
    private static void InjectSectorZeroSpawn(Srv server)
    {
        server.ShipSpawner = (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(id, hull, new Vector3(300f, 740f, 890f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        };
    }

    /// <summary>Arm + seat a real client through the drop proxy. The test thread
    /// must pump the in-process server while the handshake completes, so every
    /// wait is a <see cref="PumpUntil"/> (never a blocking ack wait).</summary>
    private static ConnectionSession Seat(Srv server, ushort proxyPort, string name, double windowSeconds)
    {
        var session = new ConnectionSession();
        session.EnableAutoReconnect(windowSeconds);
        session.Connect(proxyPort, name);
        session.SendLogonRequest(Request(name));
        int ack = PumpUntil(server, () => session.Ack is not null, 8000);
        Assert.True(ack < 8000, "no LOGONACK through the proxy");
        Assert.True(session.Ack!.FValidated, $"logon refused: {session.Ack.CharNameOrFailureReason}");
        session.SendPositionReq(SideId);
        int seated = PumpUntil(server, () =>
        {
            lock (session.Gate) return session.World.MyShipId is not null && session.World.MySideId is not null;
        }, 6000);
        Assert.True(seated < 6000, "the client was never seated");
        return session;
    }

    /// <summary>
    /// ⭐ A dropped socket inside the grace window: the session re-dials, the
    /// server resumes the SAME seat, and the native join burst re-syncs the world
    /// in place. <see cref="ConnectionSession.Lost"/> never latches, so a real
    /// client keeps flying instead of showing the disconnect screen.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Auto_reconnect_resumes_the_same_ship_and_resyncs_the_world()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config(reconnect: true, graceSeconds: 5));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        using var proxy = new DropProxy(server.BoundPort);
        using var session = Seat(server, proxy.Port, "RETURNING", windowSeconds: 5);
        short myShip;
        short mySide;
        long missionGeneration;
        lock (session.Gate)
        {
            myShip = session.World.MyShipId!.Value;
            mySide = session.World.MySideId!.Value;
            missionGeneration = session.World.MissionGeneration;
        }
        Assert.True(session.AutoReconnect);

        // A network blip: both sockets die abruptly, the proxy keeps listening.
        proxy.DropConnections();
        int resumed = PumpUntil(server, () =>
            session.ResumeSuccesses > 0 && !session.Reconnecting
            && (session.Connected || server.Ships.Any(s => s.Ship.Id == myShip)), 4000);

        // Wait for the burst to land and re-establish MyShipId in the fresh world.
        int resynced = PumpUntil(server, () =>
        {
            lock (session.Gate) return session.World.MyShipId == myShip && session.World.MySideId == mySide;
        }, 6000);
        Assert.True(resumed < 4000, "the client never re-dialed/resumed");
        Assert.True(resynced < 6000, "the join burst did not re-establish the same ship/seat in the client world");
        Assert.False(session.Lost, "a resumed session must not latch Lost");
        Assert.Equal(1, session.ResumeSuccesses);
        Assert.Equal(0, session.ResumeShipMismatches);
        Assert.Equal(1, server.ReconnectResumedTotal);
        Assert.Equal(0, server.ReconnectParkedShipCount);   // the parked entry was consumed by the resume
        lock (session.Gate)
        {
            Assert.True(session.World.Ships.ContainsKey(myShip), "the client world knows its own ship again");
            Assert.NotNull(session.World.Mission);
            // A resume is NOT a mission rotation: the identity is preserved so
            // consumers (MCP bot life tracking) do not reset a returning bot's
            // plan as if the match had changed.
            Assert.Equal(missionGeneration, session.World.MissionGeneration);
        }
        Console.WriteLine($"P813C-CLIENT: ship {myShip} side {mySide} resumed after a real socket drop " +
                          $"(attempts={session.ResumeAttempts}, serverResumed={server.ReconnectResumedTotal})");
    }

    /// <summary>
    /// ⭐ A drop that outlasts the window: the client gives up and latches
    /// <see cref="ConnectionSession.Lost"/> (raising <c>SessionLost</c>, today's
    /// behaviour), and the server's grace expiry retires the ship through the
    /// native removal. Neither side keeps a ghost.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Beyond_the_window_the_session_is_lost_and_the_ship_retired()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config(reconnect: true, graceSeconds: 1));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        using var proxy = new DropProxy(server.BoundPort);
        using var session = Seat(server, proxy.Port, "GONER", windowSeconds: 1);
        short myShip;
        lock (session.Gate) myShip = session.World.MyShipId!.Value;
        int lostRaised = 0;
        session.SessionLost += () => Interlocked.Exchange(ref lostRaised, 1);

        proxy.GoDown();   // no new connections can be made from now on
        // Wait for BOTH: the latch (set before the event) and the synchronous
        // SessionLost raise the latch is paired with — the event fires on the
        // reconnect thread, so polling only `Lost` would race the handler.
        int lost = PumpUntil(server, () => session.Lost && Volatile.Read(ref lostRaised) == 1, 6000);
        Assert.True(lost < 6000, "the session never gave up, latched Lost and raised SessionLost");
        Assert.True(session.ResumeAttempts > 0, "the client must have tried to reconnect first");

        int retired = PumpUntil(server,
            () => !server.Missions[0].Sectors.SelectMany(s => s.Models).OfType<Ship>().Any(s => s.Id == myShip), 4000);
        Assert.True(retired < 4000, "the server must retire the ship when its grace window expires");
        Assert.True(server.ReconnectExpiredTotal >= 1, $"the server must expire the parked ship (actual {server.ReconnectExpiredTotal})");
        Console.WriteLine($"P813C-CLIENT-EXPIRE: session lost after {session.ResumeAttempts} attempts; " +
                          $"ship {myShip} retired by the server");
    }

    /// <summary>
    /// ⭐ An intentional shutdown is NOT a drop: no reconnect is attempted (the
    /// transport's intentional-shutdown flag suppresses <c>OnSessionLost</c>), so
    /// quitting/logging off keeps today's semantics.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Intentional_shutdown_is_never_reconnected()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config(reconnect: true, graceSeconds: 30));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        using var proxy = new DropProxy(server.BoundPort);
        using var session = Seat(server, proxy.Port, "QUITTER", windowSeconds: 30);

        session.Shutdown();
        PumpUntil(server, () => false, 100);
        Assert.False(session.Reconnecting);
        Assert.False(session.Lost);
        Assert.Equal(0, session.ResumeAttempts);
        Console.WriteLine("P813C-CLIENT-QUIT: intentional shutdown did not trigger a reconnect");
    }

    /// <summary>
    /// Minimal TCP drop proxy: forwards one loopback port to the server and can
    /// kill every live connection (a WAN blip) and/or stop accepting new ones
    /// (an unreachable server) — the fault injection the feature needs to be
    /// proven against, without touching the server's own sockets.
    /// </summary>
    private sealed class DropProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ushort _targetPort;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _gate = new();
        private readonly List<TcpClient> _live = new();
        private bool _down;

        public DropProxy(ushort targetPort)
        {
            _targetPort = targetPort;
            _listener.Start();
            Port = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoop();
        }

        public ushort Port { get; }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception) { return; }
                if (Volatile.Read(ref _down)) { client.Dispose(); continue; }
                _ = Forward(client);
            }
        }

        private async Task Forward(TcpClient client)
        {
            var upstream = new TcpClient();
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, _targetPort, _cts.Token);
            }
            catch (Exception)
            {
                client.Dispose();
                return;
            }
            // A forward that started while we were going down must not survive the
            // kill switch: check again AFTER the upstream connect completed, and
            // register under the same lock GoDown uses.
            if (Volatile.Read(ref _down))
            {
                try { client.Dispose(); } catch { }
                try { upstream.Dispose(); } catch { }
                return;
            }
            lock (_gate) { _live.Add(client); _live.Add(upstream); }
            client.NoDelay = true;
            upstream.NoDelay = true;
            var down = Pump(client.GetStream(), upstream.GetStream());
            var up = Pump(upstream.GetStream(), client.GetStream());
            await Task.WhenAll(down, up);
            try { client.Dispose(); } catch { }
            try { upstream.Dispose(); } catch { }
        }

        private async Task Pump(NetworkStream from, NetworkStream to)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int n = await from.ReadAsync(buffer, _cts.Token);
                    if (n <= 0) return;
                    await to.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
                    await to.FlushAsync(_cts.Token);
                }
            }
            catch (Exception) { /* socket died — that is the point */ }
        }

        /// <summary>Kill every live connection; keep listening (a blip).</summary>
        public void DropConnections()
        {
            TcpClient[] live;
            lock (_gate) { live = _live.ToArray(); _live.Clear(); }
            foreach (var c in live)
            {
                try { c.Client.Dispose(); } catch { }
                try { c.Dispose(); } catch { }
            }
        }

        /// <summary>Kill everything and stop accepting: the server is unreachable.</summary>
        public void GoDown()
        {
            Volatile.Write(ref _down, true);
            try { _listener.Stop(); } catch { }
            DropConnections();
            // Reap any handshake that was already in flight when the listener
            // closed (it registers after the first sweep), then sweep again.
            Thread.Sleep(100);
            DropConnections();
        }

        public void Dispose()
        {
            Volatile.Write(ref _down, true);
            _cts.Cancel();
            DropConnections();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }
}
