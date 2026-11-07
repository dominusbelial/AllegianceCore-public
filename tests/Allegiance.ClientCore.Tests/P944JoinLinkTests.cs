using Allegiance.ClientCore.Session;

namespace Allegiance.ClientCore.Tests;

public sealed class P944JoinLinkTests
{
    private static string Link(string lobby, string id = "abc-123_ABC") =>
        "allegiance://join?lobby=" + Uri.EscapeDataString(lobby) + "&id=" + Uri.EscapeDataString(id);

    [Theory]
    [InlineData("https://lobby.example.org")]
    [InlineData("https://lobby.example.org:4443/directory")]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://127.2.3.4:5000")]
    [InlineData("http://[::1]:5000")]
    public void Valid_links_use_version_one_and_ignore_extra_fields(string lobby)
    {
        Assert.True(JoinLink.TryParse(Link(lobby) + "&v=1&host=evil.example&port=9&auto=--auto-join", out var parsed));
        Assert.Equal(new Uri(lobby), parsed!.Lobby);
        Assert.Equal("abc-123_ABC", parsed.ListingId);
        Assert.True(JoinLink.TryParse(Link(lobby), out _));
        // GIO and other OS URI launchers normalize an empty authority path to /.
        Assert.True(JoinLink.TryParse(Link(lobby).Replace("join?", "join/?"), out _));
    }

    [Theory]
    [InlineData("http://lobby.example.org")]
    [InlineData("http://localhost.example.org")]
    [InlineData("http://127.0.0.1.example.org")]
    [InlineData("ftp://localhost")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:pass@lobby.example.org")]
    [InlineData("https://lobby.example.org#fragment")]
    [InlineData("https://lobby.example.org?--exit-after=1")]
    [InlineData("https://lobby.example.org/with space")]
    [InlineData("https://lobby.example.org/with%20space")]
    [InlineData("https://lobby.example.org/\"quote")]
    [InlineData("https://lobby.example.org/\nline")]
    [InlineData("https://lobby.example.org\\@evil.example")]
    public void Rejects_untrusted_lobby_forms(string lobby) => Assert.False(JoinLink.TryParse(Link(lobby), out _));

    [Theory]
    [InlineData("")]
    [InlineData("--exit-after")]
    [InlineData("a --auto-join")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("a?b")]
    [InlineData("a&v=2")]
    [InlineData("a\"b")]
    [InlineData("a'b")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("%2F")]
    [InlineData("%252F")]
    [InlineData("é")]
    public void Rejects_odd_ids_even_when_encoded(string id) => Assert.False(JoinLink.TryParse(Link("https://lobby.example.org", id), out _));

    [Fact]
    public void Rejects_oversize_fields_and_ambiguous_query()
    {
        Assert.False(JoinLink.TryParse(Link("https://" + string.Join(".", Enumerable.Repeat(new string('a', 60), 5))), out _));
        Assert.False(JoinLink.TryParse(Link("https://lobby.example.org", new string('a', 129)), out _));
        Assert.True(JoinLink.TryParse(Link("https://lobby.example.org", new string('a', 128)), out _));
        Assert.False(JoinLink.TryParse(Link("https://lobby.example.org") + "&extra=" + new string('a', 4096), out _));
        foreach (string suffix in new[] { "&v=2", "&v=01", "&v=", "&v=1&v=1", "&id=other", "&lobby=https%3A%2F%2Fother.example", "&x=%", "&x=%ZZ", "&x=%0A", "&x=%22", "#fragment" })
            Assert.False(JoinLink.TryParse(Link("https://lobby.example.org") + suffix, out _), suffix);
    }

    [Theory]
    [InlineData("javascript://join?lobby=https%3A%2F%2Flobby.example.org&id=a")]
    [InlineData("https://join?lobby=https%3A%2F%2Flobby.example.org&id=a")]
    [InlineData("allegiance://train?lobby=https%3A%2F%2Flobby.example.org&id=a")]
    [InlineData("allegiance://join/path?lobby=https%3A%2F%2Flobby.example.org&id=a")]
    [InlineData("allegiance://user@join?lobby=https%3A%2F%2Flobby.example.org&id=a")]
    [InlineData("allegiance://join?id=a")]
    [InlineData("allegiance://join?lobby=https%3A%2F%2Flobby.example.org")]
    [InlineData("allegiance://join?lobby=https%253A%252F%252Flobby.example.org&id=a")]
    [InlineData("allegiance://join?lobby=https%3A%2F%2Flobby.example.org&id=%C0%AF")]
    [InlineData(" allegiance://join?lobby=https%3A%2F%2Flobby.example.org&id=a")]
    public void Rejects_invalid_scheme_action_structure_and_encoding(string value) => Assert.False(JoinLink.TryParse(value, out _));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Link_launch_suppresses_every_other_switch_even_when_invalid(bool invalid)
    {
        string url = invalid ? "allegiance://join?bad=link" : Link("https://lobby.example.org");
        string[] args = ["--benchmark", "replay", "--benchmark-headless", "--calibrate", "small", "--training", "--check-assets", "--auto-join", "--exit-after", "1", "--lobby", "https://other.example", "127.0.0.1", "6073", url];
        var launch = JoinLinkLaunch.Take(ref args);
        Assert.True(launch.IsLinkLaunch);
        Assert.Empty(args);
        Assert.Equal(invalid, launch.Error is not null);
        Assert.Equal(invalid, launch.Link is null);
    }

    [Fact]
    public void Ordinary_lobby_switch_and_multiple_links_are_unambiguous()
    {
        string[] ordinary = ["--lobby", "https://lobby.example.org", "--training"];
        Assert.False(JoinLinkLaunch.Take(ref ordinary).IsLinkLaunch);
        Assert.Equal(3, ordinary.Length);
        string[] wrongScheme = ["https://join?lobby=https%3A%2F%2Flobby.example.org&id=one"];
        Assert.NotNull(JoinLinkLaunch.Take(ref wrongScheme).Error);
        Assert.Empty(wrongScheme);
        string[] equalsSwitch = ["--lobby=https://lobby.example.org"];
        Assert.False(JoinLinkLaunch.Take(ref equalsSwitch).IsLinkLaunch);
        Assert.Single(equalsSwitch);
        string[] multiple = [Link("https://one.example"), Link("https://two.example")];
        Assert.NotNull(JoinLinkLaunch.Take(ref multiple).Error);
        Assert.Empty(multiple);
    }
}
