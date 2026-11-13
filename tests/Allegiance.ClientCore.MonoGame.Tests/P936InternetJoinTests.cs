using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Discovery;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
public sealed class P936InternetJoinTests
{
    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Password_edit_uses_rebound_actions_and_consumes_the_finish_key(HudStyle style)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        void Set(string name, object value) => typeof(Game1).GetField(name, hidden)!.SetValue(game, value);
        T Get<T>(string name) => (T)typeof(Game1).GetField(name, hidden)!.GetValue(game)!;
        Set("_hudSettings", new HudSettings { Style = style }); Set("_lanJoinPassword", new System.Text.StringBuilder("abc"));
        Set("_displayServers", new List<ServerListing>()); Set("_visibleRows", new List<int>());
        var map = KeyBindingStore.Instance.Map;
        var previous = map.BindingOf("LobbyPassword"); var previousJoin = map.BindingOf("LobbyJoin");
        try
        {
            map.SetBinding("LobbyPassword", new(Keys.F10, InputModifiers.None), true);
            map.SetBinding("LobbyJoin", new(Keys.F11, InputModifiers.None), true);
            bool Handle(Keys key, bool backspace = false) => (bool)typeof(Game1).GetMethod("HandleLobbyPassword", hidden)!
                .Invoke(game, [new KeyboardState(key), backspace])!;
            Assert.False(Handle(Keys.Tab));
            Assert.True(Handle(Keys.F10)); Assert.True(Get<bool>("_editingLanPassword"));
            Assert.True(Handle(Keys.None, true)); Assert.Equal("ab", Get<System.Text.StringBuilder>("_lanJoinPassword").ToString());
            Assert.True(Handle(Keys.F11)); Assert.False(Get<bool>("_editingLanPassword"));
            Assert.True(Get<bool>("_enterWasDown")); // next held-key frame cannot join
            Assert.False(Handle(Keys.F11)); Assert.True(Get<bool>("_enterWasDown"));
        }
        finally { map.SetBinding("LobbyPassword", previous, true); map.SetBinding("LobbyJoin", previousJoin, true); }
    }
    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public async Task Browser_keeps_the_selected_listing_identity_and_manual_TCP_stays_manual(HudStyle style)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        void Set(string name, object? value) => typeof(Game1).GetField(name, hidden)!.SetValue(game, value);
        using var session = new ConnectionSession();
        var first = new ServerListing("first", "Host A", "127.0.0.1", 6073, 1, 8, "A", "PCore001.igc", "1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UdpPort: 32001, PlayerHosted: true);
        var selected = first with { Id = "second", Name = "Host B", UdpPort = 32002 };
        Set("_hudSettings", new HudSettings { Style = style, Transport = ClientTransportSetting.TCP });
        Set("_session", session); Set("_introLobbyLock", new object());
        Set("_servers", new List<ServerListing> { first, selected }); Set("_lobbyUrl", "https://lobby.example.org");
        Set("_connectListingTarget", selected);
        Task Configure() => ((Func<CancellationToken, Task>)typeof(Game1).GetMethod("CaptureTransportSetup", hidden)!.Invoke(game,
            [selected.Address, (ushort)selected.Port, selected.MissionCookie, null])!)(CancellationToken.None);
        await Configure();
        Assert.Equal("second", session.InternetJoinTarget!.ListingId);
        Assert.Equal((ushort)32002, session.UdpPort); Assert.Equal("udp", session.TransportProtocol);
        Assert.Contains("[player hosted]", selected.FormatRow());
        // A later Direct Connect must clear the previous introduction target,
        // even if a cached lobby row has the identical address and TCP port.
        Set("_connectListingTarget", null); await Configure();
        Assert.Null(session.InternetJoinTarget); Assert.Equal("tcp", session.TransportProtocol);
    }
}
