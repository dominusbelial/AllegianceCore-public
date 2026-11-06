using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Xunit.Abstractions;

namespace Allegiance.Lobby.Tests;

[Trait("Tier", "Live")]
public sealed class P936InternetSessionTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Embedded_host_publishes_to_real_HTTP_lobby_and_ordinary_internet_session_joins()
    {
        Require.Artwork();
        using var app = new LobbyTestApp(configure: s => { s.PlayerHostedGames = true; s.StunBindAddress = "127.0.0.1"; s.StunPrimaryPort = 0; s.StunSecondaryPort = 0; });
        app.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); app.StartServer();
        string url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = NatTraversalClient.CreateHttp(url);
        var responder = app.Services.GetRequiredService<NatBindingResponder>();
        await responder.StartAsync(CancellationToken.None);
        try
        {
            using var host = await SkirmishHost.StartAsync(new() { HostLan = true, HostInternet = true,
                InternetLobbyUrl = url, HostPort = FreePort(), GameName = "P936 embedded", Password = "friends" });
            var listing = Assert.Single((await http.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!);
            Assert.True(listing.PlayerHosted); Assert.Equal(host.Port, listing.UdpPort);
            using var guest = new ConnectionSession { InternetJoinTarget = new(url, listing.Id) };
            guest.Connect(host.Port, "P936 real HTTP guest", "127.0.0.1");
            Logon(guest, "P936 real HTTP guest"); guest.SendPositionReq(0);
            await Until(() => Read(guest, () => guest.World.MySideId == 0 && guest.World.MyShipId is not null));
            Assert.Equal("udp", guest.ActiveTransportProtocol); Assert.True(host.BotsAlive);
            Assert.Contains("Listed on lobby", host.LanStatus);
            host.Dispose();
            using var deleted = await http.GetAsync("/api/servers/" + listing.Id);
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
            output.WriteLine("Real loopback Kestrel + STUN: embedded host and owned bots published, ordinary InternetJoin connected and seated over UDP, host disposed and deregistered.");
        }
        finally { await responder.StopAsync(CancellationToken.None); }
    }
    private static int FreePort()
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
            int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            if (port >= 65534) continue;
            try { using var game = new UdpClient(port); using var discovery = new UdpClient(port + 1); return port; }
            catch (SocketException) { }
        }
        throw new IOException("No free game/discovery port pair");
    }
    [SkippableTheory]
    [InlineData(P936NatKind.FullCone)]
    [InlineData(P936NatKind.PortRestricted)]
    public async Task Real_game_joins_seats_launches_and_receives_ticks_through_both_NATs(P936NatKind kind)
    {
        Require.Artwork();
        using var app = new LobbyTestApp(configure: s => { s.PlayerHostedGames = true; s.StunBindAddress = "127.0.0.1"; s.StunPrimaryPort = 0; s.StunSecondaryPort = 0; });
        using var http = app.CreateClient(); var responder = app.Services.GetRequiredService<NatBindingResponder>();
        await responder.StartAsync(CancellationToken.None);
        using var server = new Allegiance.Server.Server { RequireUdpIntroductions = true };
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(new()
        {
            Port = 0, BindAddress = "127.0.0.1", Discovery = new() { Enabled = false },
            Transport = new() { Protocol = "udp", Mode = "pool" },
            LiveSettings = new() { TimeDilation = "off" },
            Games = [new() { Name = "P936 friends", Password = "friends", Map = "m2xspiderw.igc", Teams = 2,
                CivIds = [25, 3], SideNames = ["Home", "Guests"], StartCountdownSeconds = 1 }]
        });
        var udp = server.UdpGameTransport!;
        udp.AllowUnintroducedLoopback = false;
        using var hostNat = new P936NatShim(udp, kind);
        using var stop = new CancellationTokenSource();
        var ticker = Task.Run(async () => { while (!stop.IsCancellationRequested) { server.TickSim(); await Task.Delay(20); } });
        P936NatShim? guestNat = null;
        try
        {
            using var publisher = await InternetHostPublisher.StartAsync(udp, http.BaseAddress!.ToString(), "P936 friends",
                server.BoundPort, 12, () => server.LivePlayerCount, http: http);
            var before = (await http.GetFromJsonAsync<ServerListingDto>("/api/servers/" + publisher.ListingId))!;
            using var guest = new ConnectionSession
            {
                InternetJoinTarget = new(http.BaseAddress.ToString(), publisher.ListingId!), TraversalHttpForTesting = http,
                ConfigureTraversalForTesting = transport => guestNat = new(transport, kind)
            };
            guest.Connect(server.BoundPort, "P936 guest", "127.0.0.1");
            Assert.Equal("udp", guest.ActiveTransportProtocol);
            Logon(guest, "P936 guest");
            guest.SendPositionReq(1);
            using var pilot = new ConnectionSession { TransportProtocol = "tcp" };
            pilot.Connect(server.BoundPort, "P936 host pilot");
            Logon(pilot, "P936 host pilot"); pilot.SendPositionReq(0);
            await Until(() => Read(guest, () => guest.World.MySideId == 1 && guest.World.MyShipId is not null)
                && Read(pilot, () => pilot.World.MySideId == 0 && pilot.World.MyShipId is not null));
            guest.SendPlayerReady(true, guest.World.MyShipId!.Value); pilot.SendPlayerReady(true, pilot.World.MyShipId!.Value);
            await Until(() => Read(guest, () => guest.World.Mission?.Stage == MissionStageMessage.StageStarted));
            short hull; ExpandedPartData[] loadout;
            lock (guest.Gate)
            {
                var ship = guest.World.Ships[guest.World.MyShipId!.Value]; hull = ship.HullId; loadout = ship.Loadout?.ToArray() ?? [];
            }
            guest.SendBuyLoadout(hull, true, loadout);
            await Until(() => Read(guest, () => guest.World.Ships[guest.World.MyShipId!.Value].StationId < 0));
            var times = new HashSet<uint>(); var watch = Stopwatch.StartNew();
            while (times.Count < 5 && watch.Elapsed.TotalSeconds < 10)
            {
                guest.CommandControls(new ControlData(.02f, 0, 0, 1), ShipControlState.AllWeapons);
                lock (guest.Gate)
                    if (guest.World.Ships[guest.World.MyShipId!.Value].Latest is { } pose) times.Add(pose.Time);
                await Task.Delay(100);
            }
            Assert.True(times.Count >= 5, "Must receive several distinct native game updates");
            // Survive the 10-second introduction lifetime: connected peer validation
            // is now owned by the normal UDP cookie, not an expired introduction.
            await Task.Delay(11000);
            guest.SendPing();
            await Until(() => Read(guest, () => guest.World.LastAppliedTick64 > Environment.TickCount64 - 2000));
            Assert.True(guest.Connected); Assert.False(guest.Lost);
            var after = (await http.GetFromJsonAsync<ServerListingDto>("/api/servers/" + publisher.ListingId))!;
            Assert.True(after.LastSeenUtc > before.LastSeenUtc); Assert.Equal(before.UdpPort, after.UdpPort);
            Assert.Contains("Listed on lobby", publisher.Status);
            publisher.Dispose();
            using var deleted = await http.GetAsync("/api/servers/" + before.Id);
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
            output.WriteLine($"{kind}: real LOGONREQ/ACK, POSITIONREQ side 1, ready/start, launch and {times.Count} distinct pose ticks; alive after introduction expiry; heartbeat refreshed; listing deleted.");
        }
        finally
        {
            stop.Cancel(); await ticker;
            guestNat?.Dispose();
            await responder.StopAsync(CancellationToken.None);
        }
    }
    private static void Logon(ConnectionSession session, string name)
    {
        session.SendLogonRequest(new() { CharacterName = name, FedsrvVer = LogonRequest.MsgVer, DwCookie = uint.MaxValue,
            CrcFileList = -1, MissionPassword = "friends", Time = 1 });
        Assert.True(session.WaitForAck(10000).FValidated);
    }
    private static bool Read(ConnectionSession session, Func<bool> predicate) { lock (session.Gate) return predicate(); }
    private static async Task Until(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.Elapsed.TotalSeconds < 20) await Task.Delay(20);
        Assert.True(predicate(), "P936 real-session condition timed out");
    }
}
