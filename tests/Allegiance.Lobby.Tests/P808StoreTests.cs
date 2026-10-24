using Allegiance.Lobby.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Allegiance.Lobby.Tests;

public sealed class P808StoreTests
{
    [Theory]
    [InlineData("A", true)] [InlineData("Élan Pilot", true)]
    [InlineData("123456789012345678901234", true)] [InlineData("1234567890123456789012345", false)]
    [InlineData("", false)] [InlineData("Trailing ", false)] [InlineData("Unencodable\u2603", false)]
    [InlineData("Control\n", false)]
    public void Callsigns_fit_native_24_byte_Latin1_field(string callsign, bool valid) =>
        Assert.Equal(valid, Allegiance.Core.Identity.JoinTokens.ValidCallsign(callsign));

    [Fact]
    public void Session_expiry_challenge_consumption_and_external_subjects_do_not_link_by_callsign()
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "store-" + Guid.NewGuid().ToString("N"));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var settings = new IdentitySettings { ConnectionString = "Data Source=" + Path.Combine(dir, "accounts.db") };
        var store = new IdentityStore(settings, clock); store.Migrate(); store.Migrate();
        var first = store.ExternalAccount("GitHub", "provider-id", "OriginalPilot");
        Assert.Equal(first, store.ExternalAccount("GitHub", "provider-id", "IgnoredName"));
        Assert.ThrowsAny<System.Data.Common.DbException>(() => store.ExternalAccount("Google", "different-id", "originalpilot"));
        var second = store.ExternalAccount("Google", "different-id", "OtherPilot"); Assert.NotEqual(first.Id, second.Id);
        string session = store.Session(first.Id); Assert.Equal(first, store.Authenticate(session));
        clock.Advance(TimeSpan.FromHours(12)); Assert.Null(store.Authenticate(session));
        string challenge = store.Challenge("login", "options"); Assert.Null(store.ConsumeChallenge(challenge, "register"));
        Assert.Equal("options", store.ConsumeChallenge(challenge, "login")); Assert.Null(store.ConsumeChallenge(challenge, "login"));
    }
}
