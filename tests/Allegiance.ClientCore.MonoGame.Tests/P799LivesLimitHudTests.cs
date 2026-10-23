using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P799 — the death banner's gate in the HUD model. Native
/// <c>DeathBannerPane = SwitchPane(IsGhost, deathbannerbmp)</c>
/// (dialog.mdl:5438-5439) with the value stamped at <c>WinTrek.cpp:3653</c>
/// (<c>m_pnumberIsGhost</c>): the banner shows iff the local ship is a GHOST —
/// deaths &gt; iLives (<c>Igc/shipIGC.h:2465-2469</c>) — not, as the port did
/// before P799, while the transient Dead flag is set. With the unlimited
/// sentinel nobody is ever a ghost, so a production mission never shows the
/// banner (the parity row's old behaviour gap).
/// </summary>
public sealed class P799LivesLimitHudTests
{
    [Fact]
    public void Death_banner_gates_on_ghost_even_when_docked()
    {
        // A ghost sees the banner docked in command view exactly like native.
        var model = HudModel.Create(null, new HudContext { Ghost = true, Docked = true });
        Assert.True(model.DeathVisible);
    }

    [Fact]
    public void Dead_alone_no_longer_shows_the_death_banner()
    {
        // The pre-P799 gap: the port showed the banner off the transient Dead
        // flag; native shows it only for a ghost.
        var model = HudModel.Create(null, new HudContext { Dead = true });
        Assert.False(model.DeathVisible);
    }

    [Fact]
    public void Death_banner_element_value_is_the_native_isghost_number()
    {
        var ghost = HudModel.Create(null, new HudContext { Ghost = true });
        var element = ghost.Elements.First(e => e.Id == "Death banner");
        Assert.True(element.Visible);
        Assert.Equal(1f, element.Value);

        var alive = HudModel.Create(null, new HudContext());
        var aliveElement = alive.Elements.First(e => e.Id == "Death banner");
        Assert.False(aliveElement.Visible);
        Assert.Equal(0f, aliveElement.Value);
    }
}
