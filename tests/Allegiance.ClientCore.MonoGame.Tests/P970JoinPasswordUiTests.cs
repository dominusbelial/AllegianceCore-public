using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Allegiance.ClientCore.MonoGame.Discovery;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P970 — the join-password UX: the advertised lock, the focused password field
/// with its character count, the opt-in "remember for this server" store (round
/// trip, 0600 permissions, clear-on-refusal) and the refusal bounce back to the
/// join screen. Every screen-level case runs in Classic AND Modern: the shared
/// model is style-independent by design (<see cref="JoinPasswordPrompt"/>), and
/// these tests pin that down rather than assuming it.
/// </summary>
public sealed class P970JoinPasswordUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string ServerJson = """
        [{"id":"abc","name":"Guarded server","address":"127.0.0.1","port":6073,"playerCount":2,"maxPlayers":16,
          "missionName":"Guarded","coreFile":"PCore001.igc","version":"0.1.0",
          "registeredUtc":"2026-11-08T00:00:00+00:00","lastSeenUtc":"2026-11-08T00:00:00+00:00",
          "passwordRequired":true,
          "missions":[{"cookie":3,"name":"Guarded","playerCount":2,"passwordRequired":true},
                      {"cookie":4,"name":"Open","playerCount":1,"passwordRequired":false}]}]
        """;

    private static Game1 NewGame(HudStyle style)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_hudSettings", new HudSettings { Style = style });
        Set(game, "_lanJoinPassword", new StringBuilder());
        Set(game, "_savedGamePasswords", SavedGamePasswords.Load(Path.Combine(Path.GetTempPath(), "p970-none-" + Guid.NewGuid().ToString("N") + ".json")));
        return game;
    }

    private static void Set(Game1 game, string field, object value)
        => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);

    private static T Get<T>(Game1 game, string field) => (T)typeof(Game1).GetField(field, Hidden)!.GetValue(game)!;

    private static void SetEnum(Game1 game, string field, string value)
        => Set(game, field, Enum.Parse(typeof(Game1).GetField(field, Hidden)!.FieldType, value));

    private static object? Call(Game1 game, string method, params object[] args)
        => typeof(Game1).GetMethod(method, Hidden)!.Invoke(game, args);

    private static string TempStore() => Path.Combine(Path.GetTempPath(), "p970-" + Guid.NewGuid().ToString("N") + ".json");

    // ── 1. the client listing mirror reads the flag and shows the lock ──────

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void The_client_listing_reads_the_password_flag_and_shows_the_lock(HudStyle style)
    {
        _ = style;   // the listing model carries no HUD state; both styles read the same row
        var listings = System.Text.Json.JsonSerializer.Deserialize<List<ServerListing>>(ServerJson, ServerListing.JsonOptions)!;
        var listing = Assert.Single(listings);
        Assert.True(listing.PasswordRequired);

        var rows = ServerListing.ExpandMissions(listings).ToList();
        var guarded = Assert.Single(rows, r => r.MissionName == "Guarded");
        var open = Assert.Single(rows, r => r.MissionName == "Open");
        Assert.True(guarded.PasswordRequired);   // the PER-GAME fact survives expansion
        Assert.False(open.PasswordRequired);
        Assert.Contains("[password required]", guarded.FormatRow(), StringComparison.Ordinal);
        Assert.DoesNotContain("[password required]", open.FormatRow(), StringComparison.Ordinal);
        Assert.Contains("PASSWORD: required", Assert.Single(ServerDirectorySearch.DetailsLines(guarded), l => l.StartsWith("PASSWORD", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain(ServerDirectorySearch.DetailsLines(open), l => l.StartsWith("PASSWORD", StringComparison.Ordinal));

        // An older/pre-P970 payload (no member at all) reads as open.
        var old = System.Text.Json.JsonSerializer.Deserialize<List<ServerListing>>(
            """[{"id":"x","name":"Old","address":"1.2.3.4","port":6073,"missionName":"Open","coreFile":"c","version":"1"}]""",
            ServerListing.JsonOptions)!;
        Assert.False(Assert.Single(old).PasswordRequired);
    }

    // ── 2. the shared prompt model (identical in both HUD styles) ────────────

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void The_prompt_states_the_requirement_counts_characters_and_stays_ascii(HudStyle style)
    {
        _ = style;   // pure text: HudStyle changes fonts/colours, never this model
        Assert.Contains("requires a password", JoinPasswordPrompt.RequirementLine(true), StringComparison.Ordinal);
        Assert.Empty(JoinPasswordPrompt.RequirementLine(false));
        Assert.Contains("case-sensitive", JoinPasswordPrompt.RequirementLine(true), StringComparison.OrdinalIgnoreCase);

        string edit = JoinPasswordPrompt.EditLine(true, "Password", 3, true);
        Assert.Contains("***", edit, StringComparison.Ordinal);
        Assert.Contains("(3/" + JoinPasswordPrompt.MaxLength + ")", edit, StringComparison.Ordinal);
        Assert.EndsWith("_", edit, StringComparison.Ordinal);

        Assert.Contains("ON", JoinPasswordPrompt.RememberLine(true, true), StringComparison.Ordinal);
        Assert.Contains("OFF", JoinPasswordPrompt.RememberLine(false, false), StringComparison.Ordinal);
        Assert.Contains("saved", JoinPasswordPrompt.RememberLine(true, true), StringComparison.Ordinal);
        Assert.Contains("forget", JoinPasswordPrompt.RememberHint(false, true), StringComparison.OrdinalIgnoreCase);

        // The refusal message reuses native's DPR_BadPassword wording and offers retry.
        Assert.Contains("The game password is incorrect.", JoinPasswordPrompt.RefusalMessage(), StringComparison.Ordinal);
        Assert.Contains("ENTER", JoinPasswordPrompt.RefusalMessage(), StringComparison.Ordinal);
        Assert.True(JoinPasswordPrompt.IsPasswordRefusal(Allegiance.Core.Wire.DelPositionReqReason.BadPassword));
        Assert.False(JoinPasswordPrompt.IsPasswordRefusal(Allegiance.Core.Wire.DelPositionReqReason.NoMission));

        // The bundled HUD fonts carry ASCII 32..126 only in BOTH styles.
        foreach (string text in new[]
                 {
                     JoinPasswordPrompt.RequirementLine(true), edit, JoinPasswordPrompt.Hint(true, true),
                     JoinPasswordPrompt.Hint(true, false), JoinPasswordPrompt.RememberLine(true, true),
                     JoinPasswordPrompt.RememberHint(true, false), JoinPasswordPrompt.RefusalMessage(),
                 })
            Assert.All(text, c => Assert.InRange(c, ' ', '~'));
    }

    // ── 3. Join link: opens focused on the password, retry is in place ───────

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void The_join_link_opens_on_the_password_field_and_sends_enter_to_the_join(HudStyle style)
    {
        var game = NewGame(style);
        Assert.True(Allegiance.ClientCore.Session.JoinLink.TryParse(
            "allegiance://join?lobby=https%3A%2F%2Flobby.example.org&id=abc", out var link));
        var confirmation = new JoinLinkConfirmation(link, "https://other.example");
        var guardedListing = new ServerListing("abc", "Guarded", "127.0.0.1", 6073, 2, 16, "Guarded", "PCore001.igc", "1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, PasswordRequired: true);
        typeof(JoinLinkConfirmation).GetProperty("Listing")!.SetValue(confirmation, guardedListing);
        typeof(JoinLinkConfirmation).GetField("_missions", Hidden)!.SetValue(confirmation,
            (IReadOnlyList<ServerListing>)[guardedListing]);
        Set(game, "_joinLinkConfirmation", confirmation);
        Set(game, "_joinLinkResolution", Task.CompletedTask);
        Set(game, "_joinLinkInputArmed", true);
        SetEnum(game, "_state", "Intro");
        SetEnum(game, "_introView", "JoinLink");

        // First update: the screen resolves and takes focus on the password field.
        Call(game, "UpdateJoinLink", default(KeyboardState));
        Assert.True(Get<bool>(game, "_editingLanPassword"));
        Assert.True(confirmation.PasswordRequired);

        // Typed characters land in the password box (0/15 counter drives the line).
        Call(game, "OnTextInput", game, new TextInputEventArgs('p', Keys.P));
        Call(game, "OnTextInput", game, new TextInputEventArgs('w', Keys.W));
        Assert.Equal("pw", Get<StringBuilder>(game, "_lanJoinPassword").ToString());

        // A required password submits on ENTER (native's dialog OK) — the rule the
        // screen applies — while an optional one keeps the two-step edit.
        Assert.True(JoinPasswordPrompt.EnterSubmits(editing: true, passwordRequired: true));
        Assert.False(JoinPasswordPrompt.EnterSubmits(editing: true, passwordRequired: false));
        Assert.True(JoinPasswordPrompt.EnterSubmits(editing: false, passwordRequired: false));

        // Native's in-place retry: the refusal reopens the SAME confirmation.
        confirmation.RejectPassword(JoinPasswordPrompt.RefusalMessage());
        Assert.False(confirmation.Closed);
        Assert.Contains("incorrect", confirmation.Message, StringComparison.Ordinal);
        bool joinedAgain = false;
        confirmation.Confirm((_, _) => joinedAgain = true);
        Assert.True(joinedAgain);
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void An_optional_password_keeps_the_two_step_enter(HudStyle style)
    {
        var game = NewGame(style);
        var confirmation = new JoinLinkConfirmation(null, "https://saved", Allegiance.ClientCore.Session.JoinLink.InvalidMessage);
        typeof(JoinLinkConfirmation).GetProperty("Listing")!.SetValue(confirmation,
            new ServerListing("abc", "Open", "127.0.0.1", 6073, 1, 16, "Open", "PCore001.igc", "1",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Set(game, "_joinLinkConfirmation", confirmation);
        Set(game, "_joinLinkResolution", Task.CompletedTask);
        Set(game, "_joinLinkInputArmed", true);
        SetEnum(game, "_state", "Intro");
        SetEnum(game, "_introView", "JoinLink");
        Call(game, "UpdateJoinLink", default(KeyboardState));
        Assert.False(Get<bool>(game, "_editingLanPassword"));   // no requirement, no forced focus
        Call(game, "UpdateJoinLink", new KeyboardState(Keys.Tab));
        Assert.True(Get<bool>(game, "_editingLanPassword"));
        Call(game, "OnTextInput", game, new TextInputEventArgs('p', Keys.P));
        Call(game, "UpdateJoinLink", new KeyboardState(Keys.Enter));
        Assert.False(Get<bool>(game, "_editingLanPassword"));   // ENTER finished editing only
        Assert.False(confirmation.Closed);
    }

    // ── 4. Find Server: the selected protected game owns the field ──────────

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Find_server_focuses_the_password_and_prefills_a_remembered_one(HudStyle style)
    {
        string store = TempStore();
        string? previous = Environment.GetEnvironmentVariable("ALLEGIANCE_GAME_PASSWORDS");
        try
        {
            Environment.SetEnvironmentVariable("ALLEGIANCE_GAME_PASSWORDS", store);
            var saved = SavedGamePasswords.Load(store);
            Assert.True(saved.Set(SavedGamePasswords.HostKey("127.0.0.1", 6073), "pw"));
            saved.Save(store);

            var game = NewGame(style);
            Set(game, "_savedGamePasswords", SavedGamePasswords.Load(store));
            Set(game, "_introFoundLock", new object());
            Set(game, "_introFound", new List<DiscoveredServer>
            {
                new("Guarded", "127.0.0.1", 6073, DiscoverySource.Lobby, 0, "Guarded", 1, 16, "1", "", PasswordRequired: true),
            });
            Set(game, "_introFoundIndex", 0);

            Call(game, "SyncFindServerPasswordFocus");
            Assert.True(Get<bool>(game, "_editingLanPassword"));
            Assert.Equal("pw", Get<StringBuilder>(game, "_lanJoinPassword").ToString());
            Assert.True(Get<bool>(game, "_rememberGamePassword"));
            Assert.Contains("requires", IntroScreenModel.FindServerHint(true, true), StringComparison.OrdinalIgnoreCase);
        }
        finally { Environment.SetEnvironmentVariable("ALLEGIANCE_GAME_PASSWORDS", previous); }
    }

    // ── 5. server browser: the selection drives the same field ──────────────

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void The_server_browser_prefills_a_protected_selection_without_stealing_navigation(HudStyle style)
    {
        string store = TempStore();
        string? previous = Environment.GetEnvironmentVariable("ALLEGIANCE_GAME_PASSWORDS");
        try
        {
            Environment.SetEnvironmentVariable("ALLEGIANCE_GAME_PASSWORDS", store);
            var game = NewGame(style);
            var guarded = new ServerListing("abc", "Guarded", "127.0.0.1", 6073, 1, 16, "Guarded", "PCore001.igc", "1",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, PasswordRequired: true);
            Set(game, "_lobbyUrl", "https://lobby.example.org");
            Set(game, "_displayServers", new List<ServerListing> { guarded });
            Set(game, "_visibleRows", new List<int> { 0 });
            Set(game, "_selectedIndex", 0);

            // Remembered password for THAT lobby listing: it must appear in the box.
            var saved = SavedGamePasswords.Load(store);
            Assert.True(saved.Set(SavedGamePasswords.LobbyKey("https://lobby.example.org", "abc"), "wing"));
            saved.Save(store);
            Set(game, "_savedGamePasswords", SavedGamePasswords.Load(store));

            Call(game, "SyncLobbyPasswordFocus");
            Assert.Equal("wing", Get<StringBuilder>(game, "_lanJoinPassword").ToString());
            Assert.True(Get<bool>(game, "_rememberGamePassword"));
            // The browser joins off the row list, so Up/Down must stay live: the
            // field is NOT auto-focused (TAB focuses it; a refusal arrives focused).
            Assert.False(Get<bool>(game, "_editingLanPassword"));
        }
        finally { Environment.SetEnvironmentVariable("ALLEGIANCE_GAME_PASSWORDS", previous); }
    }

    // ── 6. refusal clears the saved entry and returns to the join screen ────

    [Theory]
    [InlineData(HudStyle.Classic, "Intro")]
    [InlineData(HudStyle.Modern, "Intro")]
    [InlineData(HudStyle.Classic, "Lobby")]
    [InlineData(HudStyle.Modern, "Lobby")]
    public void A_refused_password_clears_the_saved_entry_and_returns_to_the_join_screen(HudStyle style, string origin)
    {
        string store = TempStore();
        string? previous = Environment.GetEnvironmentVariable("ALLEGIANCE_GAME_PASSWORDS");
        try
        {
            Environment.SetEnvironmentVariable("ALLEGIANCE_GAME_PASSWORDS", store);
            var game = NewGame(style);
            string key = SavedGamePasswords.LobbyKey("https://lobby.example.org", "abc");
            var seeded = SavedGamePasswords.Load(store);
            Assert.True(seeded.Set(key, "pw"));
            seeded.Save(store);

            Set(game, "_savedGamePasswords", SavedGamePasswords.Load(store));
            Set(game, "_joinPasswordKey", key);
            SetEnum(game, "_joinOrigin", origin);
            SetEnum(game, "_state", origin == "Lobby" ? "Lobby" : "Connecting");
            Set(game, "_lanJoinPassword", new StringBuilder("wrong"));

            Call(game, "HandleJoinPasswordRefusal");

            Assert.False(SavedGamePasswords.Load(store).Has(key));   // cleared on disk too
            Assert.True(Get<bool>(game, "_editingLanPassword"));     // field focused for the retype
            Assert.False(Get<bool>(game, "_rememberGamePassword"));
            Assert.Equal(origin == "Lobby" ? "Lobby" : "Intro", Get<object>(game, "_state").ToString());
            if (origin == "Lobby")
                Assert.Contains("incorrect", Get<string>(game, "_lobbyError"), StringComparison.Ordinal);
            else
                Assert.Contains("incorrect", Get<string>(game, "_joinPasswordError"), StringComparison.Ordinal);
            Assert.Equal("wrong", Get<StringBuilder>(game, "_lanJoinPassword").ToString());   // attempt kept, native pre-typed it
        }
        finally { Environment.SetEnvironmentVariable("ALLEGIANCE_GAME_PASSWORDS", previous); }
    }

    // ── 7. the opt-in store itself ──────────────────────────────────────────

    [Fact]
    public void The_saved_password_store_round_trips_per_server_and_is_user_only()
    {
        string path = TempStore();
        var store = SavedGamePasswords.Load(path);
        Assert.False(store.Has(SavedGamePasswords.HostKey("127.0.0.1", 6073)));

        string host = SavedGamePasswords.HostKey("127.0.0.1", 6073);
        string lobby = SavedGamePasswords.LobbyKey("https://lobby.example.org/", "abc");
        Assert.True(store.Set(host, "hostpw"));
        Assert.True(store.Set(lobby, "lobbypw"));
        store.Save(path);

        var loaded = SavedGamePasswords.Load(path);
        Assert.Equal("hostpw", loaded.Get(host));
        Assert.Equal("lobbypw", loaded.Get(lobby));
        // Key hygiene: the same lobby with/without its trailing slash is one server,
        // and the host key is case-insensitive on the host only.
        Assert.Equal(lobby, SavedGamePasswords.LobbyKey("https://LOBBY.example.org", "abc"));
        Assert.Equal(host, SavedGamePasswords.HostKey(" 127.0.0.1 ", 6073));

        Assert.True(loaded.Remove(host));
        loaded.Save(path);
        Assert.Null(SavedGamePasswords.Load(path).Get(host));
        Assert.Equal("lobbypw", SavedGamePasswords.Load(path).Get(lobby));

        // The value is stored in its own file, never in player.json.
        Assert.NotEqual(Path.GetFileName(IntroPreferences.FilePath), Path.GetFileName(path));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        else
        {
            // Windows keeps the user-profile ACL; the value itself is DPAPI-encrypted
            // unless crypt32 was unavailable (then it is plaintext, observable here).
            Assert.True(SavedGamePasswords.DpapiFailed
                || File.ReadAllText(path).Contains("dpapi:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Oversized_empty_and_corrupt_entries_are_refused_not_thrown()
    {
        string path = TempStore();
        var store = SavedGamePasswords.Load(path);
        string key = SavedGamePasswords.HostKey("host", 1);
        Assert.False(store.Set(key, ""));
        Assert.False(store.Set(key, null));
        Assert.False(store.Set(key, new string('x', SavedGamePasswords.MaxPasswordLength + 1)));
        Assert.True(store.Set(key, new string('x', SavedGamePasswords.MaxPasswordLength)));

        File.WriteAllText(path, "{ not json");
        var broken = SavedGamePasswords.Load(path);
        Assert.Equal(0, broken.Count);         // degrades to "nothing remembered"
        Assert.Null(broken.Get(key));
        broken.Save(path);                     // and saving over it is safe
        Assert.Equal(0, SavedGamePasswords.Load(path).Count);
    }
}
