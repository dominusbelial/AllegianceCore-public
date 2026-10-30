using System.Net.Http.Json;
using System.Text.Json;
using Allegiance.Lobby;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P895UdpMetadataTests
{
    [Fact]
    public async Task Heartbeat_publishes_udp_port_and_can_remove_it_on_tcp_roll_back()
    {
        using var app = new LobbyHarness();
        using var http = new HttpClient(app.Server.CreateHandler()) { BaseAddress = app.ClientOptions.BaseAddress };
        using var heartbeat = new LobbyHeartbeat(http,
            new LobbyConfig { Url = app.ClientOptions.BaseAddress.AbsoluteUri, DisplayName = "P895", EnrollmentFile = "" },
            () => 2, "P895", "PCore001.igc", "1", 6073) { UdpPort = 6075 };
        await heartbeat.PumpAsync();
        var listing = Assert.Single((await http.GetFromJsonAsync<ServerListingDto[]>("/api/servers"))!);
        Assert.Equal(6075, listing.UdpPort); Assert.Equal(6073, listing.Port);
        heartbeat.UdpPort = null;
        await heartbeat.PumpAsync();
        using var json = JsonDocument.Parse(await http.GetStringAsync("/api/servers"));
        var rolledBack = json.RootElement.EnumerateArray().Single();
        Assert.False(rolledBack.TryGetProperty("udpPort", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Registry_does_not_advertise_invalid_udp_ports(int port)
    {
        var registry = new ServerRegistry(TimeProvider.System);
        var registration = registry.Register(new RegisterRequest { Name = "P895", Address = "localhost", Port = 6073, UdpPort = port });
        Assert.NotNull(registration);
        Assert.Null(registry.Get(registration.Id)!.UdpPort);
    }
}
