using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class MeshyNoticeTests
{
    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, false, true)]
    public void Default_inspector_is_quiet_but_explicit_meshy_selection_warns(
        bool showNative, bool showMeshy, bool shipsNative, bool notice)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Game1).GetField("_shipsNative", flags)!.SetValue(game, shipsNative);
        typeof(Game1).GetField("_inspectShowNative", flags)!.SetValue(game, showNative);
        typeof(Game1).GetField("_inspectShowMeshy", flags)!.SetValue(game, showMeshy);
        var hull = new InspectorHullCatalog.InspectorHull(12, "test", "test", null,
            10, false, null, null, true, false);
        var slots = (IEnumerable)typeof(Game1).GetMethod("InspectHullDrawSlots", flags)!
            .Invoke(game, [hull])!;
        foreach (var slot in slots) Assert.NotNull(slot);
        Assert.Equal(notice, (bool)typeof(Game1).GetField("_meshyUnavailableLogged", flags)!.GetValue(game)!);
    }

    [Theory]
    [InlineData(true, "{}", false)]
    [InlineData(true, "{\"12\":\"native\"}", false)]
    [InlineData(false, "{}", true)]
    [InlineData(true, "{\"12\":\"meshy\"}", true)]
    [InlineData(true, "{\"12\":\"catalogue:example\"}", true)]
    public void Absent_pack_notice_requires_a_meshy_choice(bool native, string json, bool notice)
    {
        // Exercise the render-time fallback without constructing SDL/GL or Game.
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Game1).GetField("_shipsNative", flags)!.SetValue(game, native);
        typeof(Game1).GetField("_shipArtOverride", flags)!.SetValue(game, ShipArtOverride.Parse(json));
        var choose = typeof(Game1).GetMethod("ShipsNativeFor", flags)!;
        Assert.True((bool)choose.Invoke(game, [(short)12])!);
        Assert.Equal(notice, (bool)typeof(Game1).GetField("_meshyUnavailableLogged", flags)!.GetValue(game)!);
        // Repeated frames retain the log-once state and native fallback.
        Assert.True((bool)choose.Invoke(game, [(short)12])!);
    }
}
