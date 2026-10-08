using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P577b — validates <c>docs/reports/P577_assets/sky-hd.json</c>, the manifest
/// that drives the HD sky tier's credits/licences screen and records every
/// asset's source. It locks in Domingo's P577b review decisions:
/// <list type="bullet">
/// <item>all 10 environments are present with a source, exact subject, source
/// URL, licence and (page-verified) credit line;</item>
/// <item>globe1's background is the Veil Nebula (heic0712a), NOT the Whirlpool
/// Galaxy M51 — the weak P577 pick it replaces;</item>
/// <item>globe3's background is Westerhout 5 / the Soul Nebula (potw2251a),
/// NOT Supernova 1987A — the weak P577 pick it replaces;</item>
/// <item>the <c>planets</c> entry is the native-art declaration, NOT the NASA
/// Solar-System body list P577 shipped (Domingo's review: keep the original
/// native planets in the HD tier).</item>
/// </list>
/// Failing-first: on the pre-P577b manifest (env1 = Whirlpool heic0506a,
/// env3 = SN 1987A heic1704a, planets = array of NASA bodies) every
/// P577b-specific assertion below fails; they pass once the corrected
/// manifest lands.
/// </summary>
public class SkyHdManifestValidationTests
{
    private static string ManifestPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Allegiance.ClientCore.MonoGame")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new InvalidOperationException("cannot locate AllegianceCore repo root");
        return Path.Combine(dir, "docs", "reports", "P577_assets", "sky-hd.json");
    }

    private static JsonElement Root()
    {
        var text = File.ReadAllText(ManifestPath());
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static JsonElement[] Environments(JsonElement root)
        => root.GetProperty("environments").EnumerateArray().ToArray();

    private static JsonElement Env(JsonElement root, int n)
    {
        var envs = Environments(root);
        var e = envs.Single(x => x.GetProperty("hd").GetString() == $"hdsky/nebula/env{n}.png");
        return e;
    }

    [Fact]
    public void Manifest_exists_and_has_exactly_10_environments()
        => Assert.Equal(10, Environments(Root()).Length);

    [Fact]
    public void Every_environment_has_source_subject_url_licence_and_credit()
    {
        foreach (var e in Environments(Root()))
        {
            Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("native").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("hd").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("subject").GetString()), e.GetProperty("source_url").ToString());
            Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("source").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("source_url").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("credit").GetString()));
            Assert.Equal("CC BY 4.0", e.GetProperty("licence").GetString());
            Assert.Contains("esahubble.org", e.GetProperty("source_url").GetString());
        }
    }

    [Fact]
    public void Globe1_background_is_the_Veil_Nebula_not_the_Whirlpool_Galaxy()
    {
        var env1 = Env(Root(), 1);
        var url = env1.GetProperty("source_url").GetString();
        var subject = env1.GetProperty("subject").GetString();
        // P577b replaced the Whirlpool Galaxy (wrong kind of object for a
        // wrap-around backdrop) with the Veil Nebula (a wide-field supernova
        // remnant filling the frame).
        Assert.Contains("heic0712a", url);
        Assert.DoesNotContain("Whirlpool", subject);
        Assert.DoesNotContain("M51", subject);
        Assert.Contains("Veil", subject);
    }

    [Fact]
    public void Globe3_background_is_the_Soul_Nebula_not_Supernova_1987A()
    {
        var env3 = Env(Root(), 3);
        var url = env3.GetProperty("source_url").GetString();
        var subject = env3.GetProperty("subject").GetString();
        // P577b replaced SN 1987A (tiny, mostly-black) with the red emission
        // Soul Nebula (Westerhout 5) that fills the frame.
        Assert.Contains("potw2251a", url);
        Assert.DoesNotContain("1987", url + subject);
        Assert.DoesNotContain("Supernova", subject);
        Assert.Contains("Soul", subject);
    }

    [Fact]
    public void Planets_entry_is_native_art_not_a_NASA_body_list()
    {
        var planets = Root().GetProperty("planets");
        // P577b keeps the NATIVE planets: the entry is the native-art
        // declaration, not an array of NASA Solar-System bodies.
        Assert.Equal(JsonValueKind.Object, planets.ValueKind);
        var note = planets.GetProperty("note").GetString();
        Assert.Contains("native", note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Earth", note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Jupiter", note, StringComparison.OrdinalIgnoreCase);
    }
}
