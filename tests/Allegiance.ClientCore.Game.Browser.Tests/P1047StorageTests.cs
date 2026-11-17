using System.Text;
using Allegiance.ClientCore.MonoGame.Platform;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1047StorageTests
{
    [Fact]
    public void Browser_store_round_trips_only_committed_nonsecret_documents()
    {
        var storage = new ResidentProfileStorage();
        storage.WriteAllText("player.json", "{\"Pilot\":\"pilot\",\"InvertMouseY\":true}");
        storage.WriteAllText("HUD.json.tmp", "{\"Skin\":\"Classic\"}");
        Assert.Single(storage.Snapshot());
        storage.Move("hud.json.tmp", "hud.json", true);
        var reloaded = new ResidentProfileStorage();
        reloaded.Import(storage.Snapshot());
        Assert.Equal(storage.ReadAllText("PLAYER.JSON"), reloaded.ReadAllText("player.json"));
        Assert.Equal(storage.ReadAllText("hud.json"), reloaded.ReadAllText("hud.json"));
        Assert.Equal(2, reloaded.Snapshot().Count);
        Assert.Throws<FileNotFoundException>(() => reloaded.ReadAllText("keymap.json"));
    }
    [Theory]
    [InlineData("password")]
    [InlineData("access_token")]
    [InlineData("refresh-token")]
    [InlineData("ZoneTicketToken")]
    [InlineData("clientSecret")]
    [InlineData("privateKey")]
    public void Browser_store_rejects_nested_secrets_without_mutating_residency(string field)
    {
        var storage = new ResidentProfileStorage();
        storage.WriteAllText("player.json", "{\"Pilot\":\"pilot\"}");
        var revision = storage.Revision;
        Assert.Throws<InvalidDataException>(() => storage.WriteAllText("player.json", "{\"nested\":[{\"" + field + "\":\"secret\"}]}"));
        Assert.Equal(revision, storage.Revision);
        Assert.DoesNotContain("secret", storage.ReadAllText("player.json"));
        Assert.Throws<InvalidDataException>(() => storage.WriteAllText("identity.json", "{}"));
        Assert.Throws<InvalidDataException>(() => storage.WriteAllText("saved-passwords.json", "{}"));
    }
    [Fact]
    public void Invalid_import_is_atomic_and_quota_bounds_are_enforced()
    {
        var storage = new ResidentProfileStorage();
        storage.WriteAllText("player.json", "{\"Pilot\":\"original\"}");
        Assert.Throws<InvalidDataException>(() => storage.Import(new Dictionary<string, string>
            { ["player.json"] = "{}", ["identity.json"] = "{}" }));
        Assert.Contains("original", storage.ReadAllText("player.json"));
        Assert.Throws<InvalidDataException>(() => storage.WriteAllText("player.json", new string('x', 128 * 1024 + 1)));
    }
}
