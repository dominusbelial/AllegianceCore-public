using Allegiance.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// clash44 (2026-10-15): the rig's server.json has no per-game "treasure" section,
/// and P663's <c>GameConfig.Treasure { get; init; } = new()</c> came back null from
/// the source-generated JSON reader (init-only properties are assigned in the
/// generated object initializer, so an absent field overwrites the default with
/// null) — the server crash-looped at boot in AssembleGameMission. Every earlier
/// test built configs in code, so none loaded JSON. These load the real loader.
/// </summary>
public sealed class ConfigDefaultsJsonTests
{
    [Theory]
    [InlineData("{\"games\":[{\"name\":\"T\",\"map\":\"m2xspiderw.igc\",\"teams\":2,\"civIds\":[25,3],\"sideNames\":[\"A\",\"B\"]}]}")]
    [InlineData("{\"games\":[{\"name\":\"T\",\"map\":\"m2xspiderw.igc\",\"teams\":2,\"civIds\":[1,3],\"sideNames\":[\"A\",\"B\"],\"treasure\":{}}]}")]
    public void Game_without_or_with_empty_treasure_section_keeps_a_usable_default(string json)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var g = Assert.Single(ServerConfigLoader.Load(path, []).Games);
            Assert.NotNull(g.Treasure);
            g.Treasure.Validate();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Absent_sections_keep_their_defaults()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{}");
            var c = ServerConfigLoader.Load(path, []);
            Assert.NotNull(c.Sim);
            Assert.NotNull(c.LiveSettings);
        }
        finally { File.Delete(path); }
    }
}
