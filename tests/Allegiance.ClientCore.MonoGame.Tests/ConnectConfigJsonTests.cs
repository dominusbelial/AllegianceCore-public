namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P500 — pure-logic tests for the connect.json first-run-seed parser
/// (<see cref="ConnectConfigJson"/>). The headline case pins the P500 bug
/// fix: P499 deserialized with default CASE-SENSITIVE options while the
/// committed connect.json is camelCase, so the file never matched and the app
/// silently ran its hard-coded defaults (a 221-pointed APK still dialed
/// 127.0.0.1:2326).
/// </summary>
public class ConnectConfigJsonTests
{
    [Fact]
    public void CamelCase_file_with_non_default_values_is_honoured()
    {
        const string json = """{"host":"192.168.1.50","port":2327,"playerName":"Domo"}""";
        var r = ConnectConfigJson.Parse(json);
        Assert.True(r.HasConfig, $"expected a usable config, got warning: {r.Warning}");
        Assert.NotNull(r.Values);
        Assert.Equal("192.168.1.50", r.Values!.Host);
        Assert.Equal(2327, r.Values.Port);
        Assert.Equal("Domo", r.Values.PlayerName);
        Assert.Null(r.Warning);
    }

    [Fact]
    public void PascalCase_file_is_honoured_too()
    {
        const string json = """{"Host":"10.0.0.7","Port":2500,"PlayerName":"Beltalowda"}""";
        var r = ConnectConfigJson.Parse(json);
        Assert.True(r.HasConfig);
        Assert.Equal("10.0.0.7", r.Values!.Host);
        Assert.Equal(2500, r.Values.Port);
        Assert.Equal("Beltalowda", r.Values.PlayerName);
    }

    [Fact]
    public void Defaults_apply_when_a_field_is_omitted()
    {
        const string json = """{"port":4444}""";
        var r = ConnectConfigJson.Parse(json);
        Assert.True(r.HasConfig);
        Assert.Equal("127.0.0.1", r.Values!.Host);   // default retained
        Assert.Equal(4444, r.Values.Port);
    }

    [Fact]
    public void File_present_but_no_known_field_is_flaged_and_warns()
    {
        const string json = """{"somethingElse":true}""";
        var r = ConnectConfigJson.Parse(json);
        Assert.False(r.AnyFieldRecognized);
        Assert.False(r.HasConfig);
        Assert.NotNull(r.Warning);   // caller must warn: file present but no field recognised
        // Falls back to defaults, not to a thrown exception.
        Assert.NotNull(r.Values);
    }

    [Fact]
    public void Empty_or_whitespace_returns_no_config()
    {
        Assert.False(ConnectConfigJson.Parse("").HasConfig);
        Assert.False(ConnectConfigJson.Parse("   \n  ").HasConfig);
        Assert.False(ConnectConfigJson.Parse(null).HasConfig);
    }

    [Fact]
    public void Unparsable_json_degrades_without_throwing()
    {
        var r = ConnectConfigJson.Parse("{not json!!");
        Assert.False(r.HasConfig);
        Assert.Null(r.Values);
        Assert.NotNull(r.Warning);
    }
}
