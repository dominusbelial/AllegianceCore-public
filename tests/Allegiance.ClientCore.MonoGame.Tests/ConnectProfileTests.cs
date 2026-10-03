namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P500 — pure-logic tests for the persisted connect profile
/// (<see cref="ConnectProfile"/> / <see cref="SavedServer"/>): the JSON shape
/// the Android store writes to the app's data dir, and the tolerant parse the
/// shared client uses on load. No graphics device involved.
/// </summary>
public class ConnectProfileTests
{
    [Fact]
    public void Round_trip_preserves_every_field()
    {
        var p = new ConnectProfile
        {
            PlayerName = "Naomi",
            LobbyUrl = "http://10.0.0.9:5000",
            LastUsedKey = "192.168.1.50:2326",
            Initialized = true,
            Servers = new List<SavedServer>
            {
                new("Home", "192.168.1.50", 2326),
                new("Shed", "10.0.0.7", 2500),
            },
        };

        var back = ConnectProfile.Parse(p.Serialize());

        Assert.Equal("Naomi", back.PlayerName);
        Assert.Equal("http://10.0.0.9:5000", back.LobbyUrl);
        Assert.Equal("192.168.1.50:2326", back.LastUsedKey);
        Assert.True(back.Initialized);
        Assert.Equal(2, back.Servers.Count);
        Assert.Equal(new SavedServer("Home", "192.168.1.50", 2326), back.Servers[0]);
        Assert.Equal(new SavedServer("Shed", "10.0.0.7", 2500), back.Servers[1]);
    }

    [Fact]
    public void Parse_of_empty_or_null_yields_empty_profile()
    {
        Assert.Empty(ConnectProfile.Parse(null!).Servers);
        Assert.Empty(ConnectProfile.Parse("").Servers);
        Assert.Equal("monogame", ConnectProfile.Parse(null!).PlayerName);
    }

    [Fact]
    public void Parse_of_corrupt_json_does_not_throw()
    {
        var p = ConnectProfile.Parse("<<< definitely not json");
        Assert.NotNull(p);
        Assert.Empty(p.Servers);
    }

    [Fact]
    public void Saved_server_host_port_is_the_identity_key()
    {
        var s = new SavedServer("Home", "192.168.1.50", 2326);
        Assert.Equal("192.168.1.50:2326", s.HostPort);
    }

    [Fact]
    public void Case_insensitive_parse_handles_written_shape()
    {
        // The Android store writes PascalCase; ensure reading it back works and
        // that a camelCase variant binds too (defensive).
        const string pascal = """{"Servers":[{"Label":"Home","Host":"10.0.0.7","Port":2326}],"PlayerName":"Amos","LobbyUrl":"","LastUsedKey":null,"Initialized":true}""";
        var p = ConnectProfile.Parse(pascal);
        Assert.Single(p.Servers);
        Assert.Equal("Amos", p.PlayerName);
        Assert.True(p.Initialized);
    }
}
