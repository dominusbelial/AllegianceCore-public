using Srv = global::Allegiance.Server.Server;
using Ac = global::Allegiance.Server.AdminCheatsConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P602 — pure (no-server, no-artwork) unit tests for the admin-cheats safety
/// additions: the loud never-on-a-public-server boot warning (fires when admin
/// cheats are enabled AND the server is bound to a non-loopback address), and
/// the optional shared secret (a trusted cheat line must present it as its
/// leading token). These run in CI (no Requires trait) so the safety behaviour
/// can't rot unseen.
/// </summary>
public sealed class P602AdminCheatSafetyTests
{
    // ── Boot warning ─────────────────────────────────────────────────────────

    [Fact]
    public void Boot_warning_is_null_when_admin_cheats_disabled()
    {
        Assert.Null(Srv.AdminCheatBootWarning(new Ac { Enabled = false }, boundToLoopbackOnly: false));
        Assert.Null(Srv.AdminCheatBootWarning(null, boundToLoopbackOnly: false));
    }

    [Fact]
    public void Boot_warning_still_prints_active_banner_on_loopback_only()
    {
        var msg = Srv.AdminCheatBootWarning(new Ac { Enabled = true, AllowNames = ["admin"] }, boundToLoopbackOnly: true);
        Assert.NotNull(msg);
        Assert.Contains("ADMIN CHEATS ACTIVE", msg);
        Assert.DoesNotContain("NEVER ENABLE ADMIN CHEATS ON A PUBLIC/SHARED SERVER", msg);
    }

    [Fact]
    public void Boot_warning_is_loud_when_enabled_on_non_loopback()
    {
        var msg = Srv.AdminCheatBootWarning(new Ac { Enabled = true, AllowNames = ["admin"] }, boundToLoopbackOnly: false);
        Assert.NotNull(msg);
        Assert.Contains("NEVER ENABLE ADMIN CHEATS ON A PUBLIC/SHARED SERVER", msg);
        Assert.Contains("NAME-BASED TRUST IS SELF-DECLARED AND SPOOFABLE", msg);
        // No secret configured → the warning calls out the name-only default.
        Assert.Contains("NO shared secret set (name-only trust)", msg);
    }

    [Fact]
    public void Boot_warning_names_the_secret_when_one_is_set()
    {
        var msg = Srv.AdminCheatBootWarning(
            new Ac { Enabled = true, AllowNames = ["admin"], Secret = "s3cr3t" }, boundToLoopbackOnly: false);
        Assert.NotNull(msg);
        Assert.Contains("NEVER ENABLE ADMIN CHEATS ON A PUBLIC/SHARED SERVER", msg);
        Assert.Contains("A shared secret is set", msg);
    }

    // ── Shared secret ───────────────────────────────────────────────────────

    [Fact]
    public void Secret_prefix_accepted_and_stripped()
    {
        bool ok = Srv.ConsumeSecretPrefix("s3cr3t", "s3cr3t relocate 5 1053 1 2 3", out var rest);
        Assert.True(ok);
        Assert.Equal("relocate 5 1053 1 2 3", rest);
    }

    [Fact]
    public void Secret_prefix_must_be_the_leading_token()
    {
        // Missing secret → the line is ordinary chat (never a cheat).
        Assert.False(Srv.ConsumeSecretPrefix("s3cr3t", "relocate 5 1053 1 2 3", out _));
        // Wrong secret → rejected.
        Assert.False(Srv.ConsumeSecretPrefix("s3cr3t", "WRONG relocate 5 1053 1 2 3", out _));
        // Trailing-wrapped secret is not a leading token.
        Assert.False(Srv.ConsumeSecretPrefix("s3cr3t", " relocate  s3cr3t 5", out _));
    }

    [Fact]
    public void No_secret_configured_means_no_gate()
    {
        // When no secret is configured, ConsumeSecretPrefix reports "no gate",
        // leaving the existing name-only behaviour untouched.
        Assert.False(Srv.ConsumeSecretPrefix(null, "relocate 5 1053 1 2 3", out _));
        Assert.False(Srv.ConsumeSecretPrefix("", "relocate 5 1053 1 2 3", out _));
    }
}
