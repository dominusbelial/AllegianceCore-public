using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class LodKeyBindingTests
{
    [Theory]
    [InlineData(InputLayout.Port)]
    [InlineData(InputLayout.Native)]
    public void Alt_brackets_drive_the_lod_slider_not_weapon_cycling(InputLayout layout)
    {
        var map = new ActionMap(InputActionCatalog.Definitions, layout);
        var altOpen = new KeyboardState(Keys.LeftAlt, Keys.OemOpenBrackets);
        var altClose = new KeyboardState(Keys.LeftAlt, Keys.OemCloseBrackets);
        Assert.True(map.IsDownMostSpecific(altOpen, "LODUp"), $"{layout}: LODUp={map.BindingOf("LODUp")}");
        Assert.True(map.IsDownMostSpecific(altClose, "LODDown"), $"{layout}: LODDown={map.BindingOf("LODDown")}");
        Assert.False(map.IsDownMostSpecific(altOpen, "PrevWeapon"));
    }
}

public sealed class ShipFullDetailFlagTests
{
    [Fact]
    public void Full_ship_detail_is_a_visual_toggle_default_off()
    {
        var flag = VisualFlags.Find(VisualFlags.ShipFullDetailKey);
        Assert.NotNull(flag);
        Assert.False(flag!.DefaultOn);
        Assert.Equal(VisualFlagKind.Toggle, flag.Kind);
    }
}
