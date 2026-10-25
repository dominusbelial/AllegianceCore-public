using Allegiance.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P810 — the pure gate in front of the server's game-over feed check
/// (<see cref="ServerUpdateGate"/>): the self-update runs only when the
/// <c>updates</c> config section is present, enabled, and names both the feed
/// and the verification key. Default (absent section) = never — the brief's
/// "optional self-update, behind config", and Docker deployments keep pulling
/// images.
/// </summary>
public class ServerUpdateGateTests
{
    [Fact]
    public void An_absent_section_never_checks()
        => Assert.False(ServerUpdateGate.IsConfigured(null));

    [Fact]
    public void A_disabled_section_never_checks()
        => Assert.False(ServerUpdateGate.IsConfigured(new UpdatesConfig { Enabled = false }));

    [Fact]
    public void Enabled_without_a_feed_or_key_never_checks()
    {
        Assert.False(ServerUpdateGate.IsConfigured(new UpdatesConfig { Enabled = true }));
        Assert.False(ServerUpdateGate.IsConfigured(new UpdatesConfig { Enabled = true, FeedUrl = "http://feed" }));
        Assert.False(ServerUpdateGate.IsConfigured(new UpdatesConfig { Enabled = true, PublicKeyPem = "-----BEGIN PUBLIC KEY-----" }));
    }

    [Fact]
    public void Fully_configured_checks()
        => Assert.True(ServerUpdateGate.IsConfigured(new UpdatesConfig
        {
            Enabled = true,
            FeedUrl = "http://feed",
            PublicKeyPem = "-----BEGIN PUBLIC KEY-----",
        }));

    [Fact]
    public void The_config_validates_a_complete_section_and_rejects_partial_ones()
    {
        new UpdatesConfig { Enabled = true, FeedUrl = "http://feed", PublicKeyPem = "k" }.Validate();

        Assert.Throws<InvalidDataException>(() =>
            new UpdatesConfig { Enabled = true, PublicKeyPem = "k" }.Validate());
        Assert.Throws<InvalidDataException>(() =>
            new UpdatesConfig { Enabled = true, FeedUrl = "http://feed" }.Validate());
        // Disabled sections are never validated (the default is off).
        new UpdatesConfig().Validate();
    }
}
