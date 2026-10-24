using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Identity;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P808IdentityScreenTests
{
    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Shared_intro_identity_carries_code_callsign_and_verification_for_both_styles(HudStyle style)
    {
        Assert.True(Enum.IsDefined(style));
        var model = new IdentityScreenModel(); Assert.Contains("unverified", model.PilotLabel("Guest"));
        model.Device = new DeviceAuthorization("poll-secret", "ABCD123456", "https://lobby.example.invalid/identity.html", 300, 5);
        Assert.Contains("ABCD123456", model.ApprovalLine); Assert.DoesNotContain("poll-secret", model.ApprovalLine);
        model.Device = null; model.Account = new IdentityAccount("account-id", "CanonicalPilot");
        Assert.Equal("Pilot: CanonicalPilot (verified)  |  F2 sign out", model.PilotLabel("Spoof"));
        var server = new ServerListing("id", "Server", "localhost", 6073, 1, 10, "Mission", "Core", "version", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Verified: true, IdentityServerId: "server-id");
        Assert.StartsWith("[verified]", server.FormatRow()); Assert.StartsWith("[unverified]", (server with { Verified = false }).FormatRow());
    }
}
