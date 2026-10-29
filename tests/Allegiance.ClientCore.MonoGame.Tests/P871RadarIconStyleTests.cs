using System.Collections.Generic;
using System.IO;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P871 / SIM-0227 — named radar-icon lookup certified in BOTH HUD styles: the
/// hull/model <c>iconName</c> request resolves through the client's asset
/// catalog (known → the <c>&lt;iconName&gt;bmp</c> surface, absent → no icon,
/// procedural fallback), and Classic and Modern both draw that resolution
/// through the one shared radar renderer (native <c>WinTrek/trekigc.cpp:2731-2746</c>,
/// <c>Igc/hullTypeIGC.cpp:51</c>, <c>Igc/modelIGC.cpp:118</c>).
/// </summary>
public class P871RadarIconStyleTests
{
    private static string Src(string relative)
    {
        string root = System.AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        return File.ReadAllText(Path.Combine(root, "src", "Allegiance.ClientCore.Game", relative));
    }

    [Fact]
    public void Known_icons_resolve_to_the_bmp_surface_and_absent_names_have_no_icon()
    {
        var catalogHull = default(DataHullTypeIGC) with { HullId = 15, IconName = "dn_hifig" };
        var cache = new RadarIconNameCache(null, new Dictionary<short, DataHullTypeIGC> { [15] = catalogHull });
        Assert.Equal("dn_hifigbmp", cache.Ship(15));
        // Absent: an empty iconName is native's NULL LoadRadarIcon result.
        var bare = default(DataHullTypeIGC) with { HullId = 16, IconName = "" };
        var bareCache = new RadarIconNameCache(null, new Dictionary<short, DataHullTypeIGC> { [16] = bare });
        Assert.Null(bareCache.Ship(16));
        // Absent: an unknown hull id resolves to nothing (procedural fallback).
        Assert.Null(cache.Ship(999));
        // Instance names (wire-carried warp/asteroid iconName) follow the same rule.
        Assert.Equal("iconwarpbmp", cache.Warp("iconwarp"));
        Assert.Null(cache.Warp(null));
    }

    [Fact]
    public void Both_hud_styles_draw_the_one_shared_native_icon_resolution()
    {
        // Classic: the radar renderer owns the additive native-icon pass.
        var radar = Src(Path.Combine("Rendering", "RadarBracketRenderer.cs"));
        Assert.Contains("private void DrawNativeIcons(IReadOnlyList<RadarContact> contacts, Viewport viewport)", radar);
        Assert.Contains("bool hasIcon = _icons?.Peek(c.IconStem) is not null;", radar);

        // Modern: no second icon path — it hands the same textures to the same
        // renderer, so both styles show the identical known/absent result.
        var modern = Src(Path.Combine("Rendering", "ModernHudRenderer.cs"));
        Assert.Contains("public void SetRadarIcons(RadarIconTextures? icons) => _radar.SetRadarIcons(icons);", modern);
        Assert.Contains("_radar.Draw(model, viewport, _settings.ColorBlindSafe);", modern);
        Assert.DoesNotContain("SurfaceStem", modern);

        // Both are wired with the one textures cache at startup.
        var lifecycle = Src("Game1.Lifecycle.cs");
        Assert.Contains("_radarRenderer.SetRadarIcons(_radarIconTextures);", lifecycle);
        Assert.Contains("_modernHudRenderer.SetRadarIcons(_radarIconTextures);", lifecycle);
    }
}
