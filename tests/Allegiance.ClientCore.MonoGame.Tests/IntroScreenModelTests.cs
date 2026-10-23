namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P731 — screen-model tests for each intro-screen path (the task's
/// "screen-model tests for each path"): the single-player hook, the lobby
/// list entry, the Find Server entry, direct connect parsing, and the
/// remembered-values model. Pure, no graphics device.
/// </summary>
public class IntroScreenModelTests
{
    [Fact]
    public void Each_main_menu_button_maps_to_its_native_command()
    {
        // Single Player → skirmish hook (native trainBigButtonPane, introscreen.cpp:1221).
        Assert.Equal(IntroCommand.SinglePlayer, IntroScreenModel.Activate(0));
        // Play Online → lobby (native playIntButtonPane, :1213).
        Assert.Equal(IntroCommand.Lobby, IntroScreenModel.Activate(1));
        // Play LAN → Find Server (native playLanButtonPane, :1212).
        Assert.Equal(IntroCommand.FindServer, IntroScreenModel.Activate(2));
        // Direct Connect (modern addition).
        Assert.Equal(IntroCommand.DirectConnect, IntroScreenModel.Activate(3));
        // Options (native optionsButtonPane, :1223).
        Assert.Equal(IntroCommand.Options, IntroScreenModel.Activate(4));
        // Credits (native creditsButtonPane, :1225).
        Assert.Equal(IntroCommand.Credits, IntroScreenModel.Activate(5));
        // Replay Intro (native introButtonPane, :1224 / OnButtonIntro :1865).
        Assert.Equal(IntroCommand.ReplayIntro, IntroScreenModel.Activate(6));
        // Help (native helpButtonPane, :1226 / OnButtonHelp :1806).
        Assert.Equal(IntroCommand.Help, IntroScreenModel.Activate(7));
        // Exit (native exitButtonPane, :1238 / OnButtonExit :1799).
        Assert.Equal(IntroCommand.Exit, IntroScreenModel.Activate(8));
    }

    [Fact]
    public void Navigation_wraps_around_the_menu()
    {
        var m = new IntroScreenModel { Index = 0 };
        m.MoveUp();
        Assert.Equal(m.Count - 1, m.Index);   // wraps to the final entry
        m.MoveDown();
        Assert.Equal(0, m.Index);
        m.MoveDown();
        Assert.Equal(1, m.Index);
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", 6073)]
    [InlineData("myserver.local:2326", "myserver.local", 2326)]
    [InlineData("[2001:db8::1]:2500", "2001:db8::1", 2500)]
    [InlineData("2001:db8::1", "2001:db8::1", 6073)]   // bare IPv6 → no port
    [InlineData("host.tailnet.ts.net", "host.tailnet.ts.net", 6073)]  // MagicDNS
    public void Direct_connect_parses_host_and_optional_port(string text, string expHost, ushort expPort)
    {
        Assert.True(IntroScreenModel.TryParseDirect(text, 6073, out var host, out var port));
        Assert.Equal(expHost, host);
        Assert.Equal(expPort, port);
    }

    [Theory]
    [InlineData("127.0.0.1", 2326)]
    [InlineData("myserver.local", 2500)]
    [InlineData("2001:db8::1", 2500)]
    public void Remembered_endpoint_keeps_its_nondefault_port(string address, ushort port)
    {
        var restored = IntroScreenModel.DirectAddress(address, port);
        Assert.True(IntroScreenModel.TryParseDirect(restored, 6073, out var host, out var parsedPort));
        Assert.Equal(address, host);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host:abc")]
    [InlineData("host:0")]
    public void Direct_connect_rejects_invalid_input(string text)
    {
        Assert.False(IntroScreenModel.TryParseDirect(text, 6073, out _, out _));
    }

    [Fact]
    public void Menu_preserves_the_native_button_set_and_adds_showcase()
    {
        // P757 — Replay Intro and Help join the native button set after
        // Credits; the port's extra stays at the end. P789 renamed it Showcase.
        Assert.Equal(10, IntroScreenModel.MainLabels.Length);
        Assert.Equal("Replay Intro", IntroScreenModel.MainLabels[6]);
        Assert.Equal("Help", IntroScreenModel.MainLabels[7]);
        Assert.Equal("Exit", IntroScreenModel.MainLabels[8]);
        Assert.Equal("Showcase", IntroScreenModel.MainLabels[9]);
        Assert.Equal(IntroCommand.Benchmark, IntroScreenModel.Activate(9));
        Assert.Equal("Single Player", IntroScreenModel.MainLabels[0]);
    }
}
