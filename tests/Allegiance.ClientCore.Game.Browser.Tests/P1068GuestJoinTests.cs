using System.Text.Json;
using Allegiance.ClientCore.Browser.Pages;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Identity;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Game.Browser.Tests;

public sealed class P1068GuestJoinTests
{
    private static BrowserServer Server(bool accepts = true, bool verified = false, string? identity = null) =>
        new("id", "guest server", 6073, "mission", "PCore001.igc", verified, identity, "wss://game.invalid/game", AcceptsGuests: accepts);
    private static string? Refusal(BrowserServer server, bool guest = true, bool version = true, string page = "https://game.invalid") =>
        BrowserJoinPolicy.Refusal(new(page), server, version, guest);

    [Fact]
    public void Guest_requires_explicit_admission_and_signed_in_still_requires_verified_issuer()
    {
        Assert.Null(Refusal(Server()));
        Assert.Equal("Sign in to join", Refusal(Server(false, true, "id")));
        Assert.NotNull(Refusal(Server(), guest: false));
        Assert.NotNull(Refusal(Server(verified: true), guest: false));
        Assert.Null(Refusal(Server(verified: true, identity: "id"), guest: false));
    }

    [Theory]
    [InlineData("https://game.invalid", "wss://other.invalid/game")]
    [InlineData("https://game.invalid", "wss://game.invalid:8443/game")]
    [InlineData("http://game.invalid", "wss://game.invalid/game")]
    [InlineData("https://game.invalid", "ws://game.invalid/game")]
    [InlineData("https://game.invalid", "wss://user:password@game.invalid/game")]
    [InlineData("https://game.invalid", null)]
    public void Guest_never_relaxes_origin_or_route_rules(string page, string? route) =>
        Assert.NotNull(Refusal(Server() with { WebSocketUrl = route }, page: page));

    [Fact]
    public void Guest_keeps_core_and_reported_version_gates_and_password_is_not_a_bypass()
    {
        Assert.NotNull(Refusal(Server() with { CoreFile = "PCore002.igc" }));
        Assert.NotNull(Refusal(Server() with { MinClientVersion = "2.0.0" }, version: false));
        Assert.Null(Refusal(Server() with { MinClientVersion = "2.0.0", PasswordRequired = true }));
    }

    [Fact]
    public void Browser_json_round_trip_defaults_old_listings_to_sign_in_only()
    {
        string json = """[{"id":"id","name":"server","port":6073,"coreFile":"PCore001.igc"}]""";
        var old = Assert.Single(JsonSerializer.Deserialize(json, BrowserJoinJson.Default.BrowserServerArray)!);
        Assert.False(old.AcceptsGuests);
        string serialized = JsonSerializer.Serialize(new[] { old with { AcceptsGuests = true } }, BrowserJoinJson.Default.BrowserServerArray);
        Assert.Contains("\"acceptsGuests\":true", serialized);
        Assert.True(Assert.Single(JsonSerializer.Deserialize(serialized, BrowserJoinJson.Default.BrowserServerArray)!).AcceptsGuests);
    }

    [Theory]
    [InlineData("character name already in use in this mission", "already in use", true)]
    [InlineData("that callsign is registered — sign in to use it", "registered", true)]
    [InlineData("wrong client version", "client version", false)]
    [InlineData("requires client version 2.0.0", "2.0.0", false)]
    [InlineData("your client (1.0.0) is older than this server's minimum (2.0.0) — update the client and reconnect; the lobby's update feed installs it", "Client version mismatch", false)]
    [InlineData("this server requires lobby sign-in", "Sign in to join", false)]
    [InlineData("unknown refusal", "unknown refusal", false)]
    public void Existing_logon_refusals_have_clear_messages_and_only_name_conflicts_offer_reroll(string reason, string message, bool conflict)
    {
        var failure = BrowserJoinFailure.From(reason);
        Assert.Contains(message, failure.Message); Assert.Equal(conflict, failure.NameConflict);
        if (failure.Message.Contains("version mismatch", StringComparison.Ordinal)) Assert.DoesNotContain("update feed installs", failure.Message);
    }

    [Fact]
    public void Native_mission_password_refusal_explains_how_to_retry()
    {
        var failure = BrowserJoinFailure.From("refused", DelPositionReqReason.BadPassword);
        Assert.Contains("password is incorrect", failure.Message); Assert.False(failure.NameConflict);
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Shared_browser_identity_model_never_shows_the_default_unverified_pilot(HudStyle style)
    {
        Assert.True(Enum.IsDefined(style));
        var model = new BrowserIdentityModel();
        Assert.DoesNotContain("unverified", model.PilotLabel("pilot"));
        model.Guest = "Pilot-7F3K";
        Assert.Equal("Guest: Pilot-7F3K", model.PilotLabel("pilot"));
        model.Account = new IdentityAccount("id", "RegisteredPilot");
        Assert.Equal("Signed in: RegisteredPilot", model.PilotLabel("pilot"));
        Assert.DoesNotContain("F2", model.PilotLabel("pilot"));
        Assert.True(JoinTokens.ValidCallsign(model.Guest));
    }

    [Fact]
    public void Proof_host_guest_flag_is_explicit_and_never_changes_its_default()
    {
        Assert.False(BattleOptions.Parse(["art", "assets", "scratch"]).Guests);
        Assert.True(BattleOptions.Parse(["art", "assets", "scratch", "--guests", "1"]).Guests);
        Assert.False(BattleOptions.Parse(["art", "assets", "scratch", "--guests", "0"]).Guests);
        Assert.Throws<ArgumentException>(() => BattleOptions.Parse(["art", "assets", "scratch", "--guests", "2"]));
    }

    [Fact]
    public void Loopback_guest_is_subject_to_the_same_origin_rule()
    {
        var server = Server() with { WebSocketUrl = "ws://localhost:8048/game" };
        Assert.Null(Refusal(server, page: "http://localhost:8048"));
        Assert.NotNull(Refusal(server, page: "http://127.0.0.1:8048"));
    }
}
