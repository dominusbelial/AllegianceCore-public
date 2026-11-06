using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.SinglePlayer;

namespace Allegiance.ClientCore.Tests;

public sealed class P936InternetHostModelTests
{
    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Both_styles_offer_explicit_internet_hosting_and_preserve_LAN_defaults(HudStyle style)
    {
        var settings = new HudSettings { Style = style };
        Assert.Equal(style, settings.Style);
        Assert.Equal(IntroCommand.HostInternet, IntroScreenModel.Activate(Array.IndexOf(IntroScreenModel.MainLabels, "Host internet game")));
        var model = new HostLanScreenModel();
        Assert.False(model.CreateOptions().HostInternet);
        model.UseInternetLobby("https://lobby.example.org");
        var options = model.CreateOptions();
        Assert.True(options.HostLan); Assert.True(options.HostInternet); Assert.Equal("https://lobby.example.org", options.InternetLobbyUrl);
        Assert.Contains("Host internet game", model.Rows[HostLanScreenModel.StartRow]);
        Assert.Equal("0.0.0.0", options.CreateConfig().BindAddress); // local TCP bots retain IPv4; UDP binds dual stack
        Assert.Equal("udp", options.CreateConfig().Transport!.Protocol);
        Assert.Null(options.CreateConfig().Lobby); // owned player publisher, no second dedicated heartbeat
        model.UseInternetLobby(null);
        Assert.False(model.CreateOptions().HostInternet); Assert.Equal("0.0.0.0", model.CreateOptions().CreateConfig().BindAddress);
        model.UseInternetLobby("http://lobby.example.org");
        Assert.Throws<ArgumentException>(() => model.CreateOptions());
    }
}
