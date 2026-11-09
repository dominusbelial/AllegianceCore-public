using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P944JoinLinkUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Confirmation_uses_browser_password_edit_and_cancel_returns_to_intro(HudStyle style)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        void Set(string field, object value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
        T Get<T>(string field) => (T)typeof(Game1).GetField(field, Hidden)!.GetValue(game)!;
        void EnumField(string field, string value) => Set(field, Enum.Parse(typeof(Game1).GetField(field, Hidden)!.FieldType, value));
        object? Call(string method, params object[] args) => typeof(Game1).GetMethod(method, Hidden)!.Invoke(game, args);
        Set("_hudSettings", new HudSettings { Style = style });
        Set("_lobbyUrl", "https://remembered.example");
        Set("_lanJoinPassword", new StringBuilder());
        Set("_joinLinkResolution", Task.CompletedTask);
        Set("_joinLinkInputArmed", true);
        Set("_joinLinkConfirmation", new JoinLinkConfirmation(null, "https://remembered.example", JoinLink.InvalidMessage));
        EnumField("_state", "Intro"); EnumField("_introView", "JoinLink");
        Call("UpdateJoinLink", new KeyboardState(Keys.Tab));
        Assert.True(Get<bool>("_editingLanPassword"));
        Call("OnTextInput", game, new TextInputEventArgs('p', Keys.P));
        Assert.Equal("p", Get<StringBuilder>("_lanJoinPassword").ToString());
        Call("UpdateJoinLink", new KeyboardState(Keys.Back));
        Assert.Empty(Get<StringBuilder>("_lanJoinPassword").ToString());
        Call("OnTextInput", game, new TextInputEventArgs('s', Keys.S));
        Call("UpdateJoinLink", new KeyboardState(Keys.Enter));
        Assert.False(Get<bool>("_editingLanPassword"));
        Assert.False(Get<JoinLinkConfirmation>("_joinLinkConfirmation").Closed); // Enter finished editing, did not join
        Call("UpdateJoinLink", new KeyboardState(Keys.Escape));
        Assert.True(Get<JoinLinkConfirmation>("_joinLinkConfirmation").Closed);
        Assert.Equal("Main", Get<object>("_introView").ToString());
        Assert.Equal("Intro", Get<object>("_state").ToString());
        Assert.Empty(Get<StringBuilder>("_lanJoinPassword").ToString());
        Assert.Equal("https://remembered.example", Get<string>("_lobbyUrl"));
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Held_enter_cannot_accept_a_newly_opened_confirmation(HudStyle style)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        void Set(string field, object value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
        Assert.True(JoinLink.TryParse("allegiance://join?lobby=https%3A%2F%2Flobby.example.org&id=one", out var link));
        var confirmation = new JoinLinkConfirmation(link, "https://saved.example");
        typeof(JoinLinkConfirmation).GetProperty("Listing")!.SetValue(confirmation,
            new ServerListing("one", "test", "127.0.0.1", 6073, 1, 8, "mission", "core", "1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Set("_hudSettings", new HudSettings { Style = style });
        Set("_joinLinkConfirmation", confirmation); Set("_joinLinkResolution", Task.CompletedTask);
        typeof(Game1).GetMethod("UpdateJoinLink", Hidden)!.Invoke(game, [new KeyboardState(Keys.Enter)]);
        Assert.False(confirmation.Closed);
        Assert.False((bool)typeof(Game1).GetField("_joinLinkInputArmed", Hidden)!.GetValue(game)!);
        typeof(Game1).GetMethod("UpdateJoinLink", Hidden)!.Invoke(game, [default(KeyboardState)]);
        Assert.True((bool)typeof(Game1).GetField("_joinLinkInputArmed", Hidden)!.GetValue(game)!);
    }

    [Fact]
    public void Program_guard_runs_before_training_benchmark_automation_and_update()
    {
        string[] args = ["--training", "--auto-ready", "--exit-after", "1", "--benchmark", "replay", "allegiance://join?bad=link"];
        Assert.True(JoinLinkLaunch.Take(ref args).IsLinkLaunch);
        Assert.False(Game1.LaunchOptions.TakeTrainingSwitch(ref args));
        Assert.False(AutoProofPlan.Parse(args, out _).AnyActive);
        Assert.Null(Allegiance.SinglePlayer.Benchmark.BenchmarkCommand.Parse(args));
        Assert.Null(Allegiance.SinglePlayer.Benchmark.SizingCalibrationCommand.Parse(args));
        string root = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(root, "AllegianceCore.slnx"))) root = Directory.GetParent(root)!.FullName;
        string program = File.ReadAllText(Path.Combine(root, "src/Allegiance.ClientCore.MonoGame/Program.cs"));
        Assert.True(program.IndexOf("JoinLinkLaunch.Take", StringComparison.Ordinal) < program.IndexOf("TakeTrainingSwitch", StringComparison.Ordinal));
        Assert.Contains("if (!joinLaunch.IsLinkLaunch) await ClientUpdateBootstrap.RunAsync(args);", program);
        Assert.Contains("JoinLinkLaunch = joinLaunch", program);
        string ui = File.ReadAllText(Path.Combine(root, "src/Allegiance.ClientCore.Game/Game1.JoinLink.cs"));
        Assert.Contains("BeginConnect(listing.Address, (ushort)listing.Port, listing.MissionCookie,", ui);
        Assert.Contains("listingTarget: listing, listingLobby:", ui);
        Assert.DoesNotContain(".Save(", ui);
        string longHost = string.Join(".", Enumerable.Repeat(new string('a', 60), 4));
        Assert.True(JoinLink.TryParse("allegiance://join?lobby=" + Uri.EscapeDataString("https://" + longHost) + "&id=one", out var longLink));
        Assert.Equal("https://" + longHost, new JoinLinkConfirmation(longLink, "https://saved.example").LobbyDescription);
        Assert.True(JoinLink.TryParse("allegiance://join?lobby=" + Uri.EscapeDataString("https://éxample.org") + "&id=one", out var idnLink));
        Assert.All(new JoinLinkConfirmation(idnLink, "https://saved.example").LobbyDescription, c => Assert.True(char.IsAscii(c)));
    }

    [Theory]
    [InlineData(HudStyle.Classic, false)]
    [InlineData(HudStyle.Modern, false)]
    [InlineData(HudStyle.Classic, true)]
    [InlineData(HudStyle.Modern, true)]
    public async Task Resolved_listing_drives_transport_and_punch_uses_the_link_lobby(HudStyle style, bool playerHosted)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        void Set(string field, object value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
        using var session = new ConnectionSession();
        var listing = new ServerListing("resolved", "test", "127.0.0.1", 54321, 1, 8, "mission", "core", "1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MissionCookie: 943, UdpPort: 54322, PlayerHosted: playerHosted);
        Set("_session", session); Set("_introLobbyLock", new object());
        Set("_servers", new List<ServerListing>()); // link resolution never needs to pollute the saved browser
        Set("_lobbyUrl", "https://remembered.example"); Set("_connectListingLobby", "https://link-lobby.example");
        Set("_connectListingTarget", listing);
        Set("_hudSettings", new HudSettings { Style = style, Transport = Discovery.ClientTransportSetting.UDP });
        var setup = (Func<CancellationToken, Task>)typeof(Game1).GetMethod("CaptureTransportSetup", Hidden)!
            .Invoke(game, [listing.Address, (ushort)listing.Port, listing.MissionCookie, null])!;
        await setup(CancellationToken.None);
        Assert.Equal("udp", session.TransportProtocol); Assert.Equal((ushort)54322, session.UdpPort);
        if (playerHosted)
        {
            Assert.Equal("resolved", session.InternetJoinTarget!.ListingId);
            Assert.Equal("https://link-lobby.example", session.InternetJoinTarget.LobbyUrl);
        }
        else Assert.Null(session.InternetJoinTarget);
        Assert.Equal("https://remembered.example", typeof(Game1).GetField("_lobbyUrl", Hidden)!.GetValue(game));
    }

    [Fact]
    public void Mac_zero_window_SDL_drop_event_preserves_the_single_URL_argument()
    {
        string url = "allegiance://join?lobby=https%3A%2F%2Flobby.example.org&id=one&v=1";
        IntPtr text = Marshal.StringToCoTaskMemUTF8(url), ev = Marshal.AllocHGlobal(56);
        try
        {
            for (int i = 0; i < 56; i++) Marshal.WriteByte(ev, i, 0);
            Marshal.WriteInt32(ev, 0x1000); Marshal.WriteIntPtr(ev, 8, text);
            // Cocoa sends window ID 0; GameWindow.FileDrop filters this out.
            Assert.Equal(0, Marshal.ReadInt32(ev, 16));
            Assert.Equal(url, MacJoinLinkEvents.ReadUrl(ev));
            Assert.True(JoinLink.TryParse(MacJoinLinkEvents.ReadUrl(ev), out _));
            Marshal.WriteInt32(ev, 0x1003); Assert.Null(MacJoinLinkEvents.ReadUrl(ev));
            Marshal.WriteInt32(ev, 0x1000); Marshal.WriteIntPtr(ev, 8, IntPtr.Zero);
            Assert.Null(MacJoinLinkEvents.ReadUrl(ev));
        }
        finally { Marshal.FreeCoTaskMem(text); Marshal.FreeHGlobal(ev); }
    }
}
