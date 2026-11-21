using System.Text;
using System.Text.Json;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class P1065BotIdentityTests
{
    [Fact]
    public void Absent_fixture_preserves_the_existing_driver_logon()
    {
        var request = new LogonRequest { CharacterName = "mcp-pilot", MissionPassword = "original", ZoneTicket = [1, 2] };
        BotLogonIdentity.Apply(request, "mcp-pilot", null);
        Assert.Equal("original", request.MissionPassword); Assert.Equal(new byte[] { 1, 2 }, request.ZoneTicket);
    }

    [Fact]
    public async Task Fixture_uses_the_brokers_canonical_identity_version_and_password_and_cleans_messages()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "p1065-broker-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        try
        {
            string config = Path.Combine(scratch, "fixture.json");
            File.WriteAllText(config, JsonSerializer.Serialize(new BotLogonIdentity.Settings("2.0.0", "proof-password", scratch)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var broker = Task.Run(async () =>
            {
                string[] requests;
                while ((requests = Directory.GetFiles(scratch, "*.request")).Length == 0) await Task.Delay(5, stop.Token);
                using var document = JsonDocument.Parse(File.ReadAllText(requests[0]));
                Assert.Equal("mcp-pilot", document.RootElement.GetProperty("name").GetString());
                string reply = Path.ChangeExtension(requests[0], ".reply");
                File.WriteAllText(reply + ".tmp", JsonSerializer.Serialize(new BotLogonIdentity.Reply("CanonicalPilot", "signed-fixture-ticket")));
                File.Move(reply + ".tmp", reply);
            }, stop.Token);
            var request = new LogonRequest { CharacterName = "mcp-pilot" };
            await Task.Run(() => BotLogonIdentity.Apply(request, "mcp-pilot", config), stop.Token);
            await broker;
            Assert.Equal("CanonicalPilot", request.CharacterName); Assert.Equal("2.0.0", request.ClientVersion);
            Assert.Equal("proof-password", request.MissionPassword); Assert.Equal("signed-fixture-ticket", Encoding.UTF8.GetString(request.ZoneTicket!));
            Assert.Single(Directory.GetFiles(scratch));
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public void Identity_fixture_is_accepted_only_for_loopback_and_forwarded_in_options(string host)
    {
        var options = DriverOptions.Parse([host, "6073", "--logon-file", "fixture.json"]);
        Assert.EndsWith("fixture.json", options.LogonFile);
        Assert.Throws<ArgumentException>(() => DriverOptions.Parse(["example.invalid", "6073", "--logon-file", "fixture.json"]));
    }
}
