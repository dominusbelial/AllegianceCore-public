using Allegiance.Server;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P612 — pure tests for the server cinematic features: the admin-only sim time
/// scale clamp/enforcement and the admin-unrestricted spectator visibility gate
/// (admin sees all ships / any sector; non-admin sees only its side's view).
/// Uses the internal static seams granted by Allegiance.Server's
/// InternalsVisibleTo (mirroring how P554/P602 test the admin-cheat gate).
/// </summary>
public class P612ServerCinematicTests
{
    // ── sim.timeScale clamp (and its admin-only range) ─────────────────────

    [Theory]
    [InlineData(0.049, 0.1)]   // below min → clamp to 0.1
    [InlineData(0.0999, 0.1)]
    [InlineData(0.4, 0.4)]     // in range → unchanged
    [InlineData(0.75, 0.75)]
    [InlineData(1.0, 1.0)]     // default/normal
    [InlineData(1.5, 1.0)]     // above max → clamp to 1.0 (no speeding up)
    [InlineData(5.0, 1.0)]
    public void ClampTimeScale_clamps_to_admin_range(double input, double expected)
        => Assert.Equal(expected, LiveSettingsConfig.ClampTimeScale(input), precision: 9);

    [Fact]
    public void ClampTimeScale_range_constants_match_the_brief()
    {
        Assert.Equal(0.1, LiveSettingsConfig.MinTimeScale);
        Assert.Equal(1.0, LiveSettingsConfig.MaxTimeScale);
    }

    // ── admin spectator visibility gate ────────────────────────────────────

    [Fact]
    public void AdminSpectatorAllowed_is_false_when_admin_cheats_disabled()
    {
        // Not enabled → nobody is a spectator admin, even an allow-listed name.
        var cfg = new AdminCheatsConfig { Enabled = false, AllowNames = ["Domino"] };
        Assert.False(global::Allegiance.Server.Server.AdminSpectatorAllowed(cfg, "Domino"));
    }

    [Fact]
    public void AdminSpectatorAllowed_is_false_when_config_absent()
    {
        Assert.False(global::Allegiance.Server.Server.AdminSpectatorAllowed(null, "Domino"));
        Assert.False(global::Allegiance.Server.Server.AdminSpectatorAllowed(null, null));
    }

    [Fact]
    public void AdminSpectatorAllowed_gates_on_the_allow_names_list()
    {
        var cfg = new AdminCheatsConfig { Enabled = true, AllowNames = ["Domino"] };
        Assert.True(global::Allegiance.Server.Server.AdminSpectatorAllowed(cfg, "Domino"), "allow-listed name is an admin spectator");
        Assert.False(global::Allegiance.Server.Server.AdminSpectatorAllowed(cfg, "bot1"), "non-allow-listed name is NOT an admin spectator");
        Assert.False(global::Allegiance.Server.Server.AdminSpectatorAllowed(cfg, ""), "empty name is not an admin spectator");
        Assert.False(global::Allegiance.Server.Server.AdminSpectatorAllowed(cfg, null), "null name is not an admin spectator");
    }

    [Fact]
    public void AdminSpectatorAllowed_is_case_insensitive()
    {
        var cfg = new AdminCheatsConfig { Enabled = true, AllowNames = ["Domino"] };
        Assert.True(global::Allegiance.Server.Server.AdminSpectatorAllowed(cfg, "DOMINO"));
        Assert.True(global::Allegiance.Server.Server.AdminSpectatorAllowed(cfg, "domino"));
    }

    [Fact]
    public void AdminSpectatorAllowed_supports_prefix_wildcards()
    {
        var cfg = new AdminCheatsConfig { Enabled = true, AllowNames = ["mcp-*"] };
        Assert.True(global::Allegiance.Server.Server.AdminSpectatorAllowed(cfg, "mcp-b007-random"));
        Assert.False(global::Allegiance.Server.Server.AdminSpectatorAllowed(cfg, "Domino"));
    }

    [Fact]
    public void AdminSpectatorAllowed_matches_any_entry()
    {
        var cfg = new AdminCheatsConfig { Enabled = true, AllowNames = ["alice", "bob*", "Carol"] };
        Assert.True(global::Allegiance.Server.Server.AdminSpectatorAllowed(cfg, "alice"));
        Assert.True(global::Allegiance.Server.Server.AdminSpectatorAllowed(cfg, "bobster"));
        Assert.True(global::Allegiance.Server.Server.AdminSpectatorAllowed(cfg, "carol"));
        Assert.False(global::Allegiance.Server.Server.AdminSpectatorAllowed(cfg, "mallory"));
    }

    // ── TimescaleUpdate wire round-trip ────────────────────────────────────

    [Theory]
    [InlineData(1.0f)]
    [InlineData(0.4f)]
    [InlineData(0.1f)]
    [InlineData(0.987f)]
    public void TimescaleUpdate_round_trips_the_scale(float scale)
    {
        var msg = new TimescaleUpdate { Scale = scale };
        var decoded = TimescaleUpdate.Decode(msg.Encode());
        Assert.Equal(scale, decoded.Scale, precision: 2);   // stored ×1000 as uint
    }

    [Fact]
    public void TimescaleUpdate_defaults_to_normal_speed()
    {
        Assert.Equal(1.0f, new TimescaleUpdate().Scale, precision: 4);
    }
}
