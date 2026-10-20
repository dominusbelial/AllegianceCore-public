using System;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P759 — the runtime LOD-slider model, the pure port of native's in-flight LOD
/// scroll bar (<c>WinTrek.cpp:3179-3185</c>, <c>:3553-3555</c>, <c>:9448-9453</c>,
/// <c>:10415-10424</c>). Native creates a horizontal scroll pane
/// (<c>CreateScrollPane(WinPoint(128, 10), size=100, pageSize=10, lineSize=1, pos=90)</c>)
/// whose integer value feeds <c>ThingGeo::SetLODBias(value / 100)</c>. The three
/// commands are:
/// <list type="bullet">
/// <item><c>TK_ToggleLODSlider</c> — show/hide the slider image
/// (<c>m_pwrapImageLOD-&gt;SetImage(m_pimageLOD / Image::GetEmpty())</c>);</item>
/// <item><c>TK_LODUp</c> — <c>m_pscrollPane-&gt;PageUp()</c> (value + pageSize);</item>
/// <item><c>TK_LODDown</c> — <c>m_pscrollPane-&gt;PageDown()</c> (value - pageSize).</item>
/// </list>
/// The port keeps the same value domain (0..100), page step (10), initial value
/// (90, native's <c>pos=90</c>) and bias formula (<c>value / 100</c>), and stores
/// the result in <see cref="ShipLodProfile.LodBias"/> for the renderer to consume.
/// Pure (no GraphicsDevice) so every rule is headless-testable.
/// </summary>
public sealed class LodControl
{
    /// <summary>Native <c>CreateScrollPane</c> page size (<c>WinTrek.cpp:3182</c>):
    /// <c>TK_LODUp</c>/<c>TK_LODDown</c> move the value by this many units.</summary>
    public const int PageSize = 10;

    /// <summary>Native <c>CreateScrollPane</c> initial position (<c>WinTrek.cpp:3182</c>).</summary>
    public const int InitialValue = 90;

    /// <summary>Native scroll-bar upper bound (<c>size=100</c>, so 0..100).</summary>
    public const int MaxValue = 100;

    private int _value = InitialValue;
    private bool _visible;

    /// <summary>The current scroll-bar value (0..100). Native's pane clamps to its range.</summary>
    public int Value => _value;

    /// <summary>Whether the slider overlay is currently shown
    /// (<c>TK_ToggleLODSlider</c> toggles <c>m_pwrapImageLOD</c>).</summary>
    public bool Visible
    {
        get => _visible;
        set => _visible = value;
    }

    /// <summary>The LOD bias the renderer applies: native
    /// <c>ThingGeo::SetLODBias(((float)value) / 100)</c> (<c>WinTrek.cpp:3554</c>).</summary>
    public float Bias => Math.Clamp(_value / 100f, 0f, 1f);

    /// <summary>Show/hide the slider (native <c>TK_ToggleLODSlider</c>).</summary>
    public void ToggleVisible() => _visible = !_visible;

    /// <summary><c>TK_LODUp</c> — <c>m_pscrollPane-&gt;PageUp()</c>: +<see cref="PageSize"/>,
    /// clamped to <see cref="MaxValue"/>.</summary>
    public void PageUp() => _value = Math.Clamp(_value + PageSize, 0, MaxValue);

    /// <summary><c>TK_LODDown</c> — <c>m_pscrollPane-&gt;PageDown()</c>: -<see cref="PageSize"/>,
    /// clamped to 0.</summary>
    public void PageDown() => _value = Math.Clamp(_value - PageSize, 0, MaxValue);

    /// <summary>Apply the current bias to a projected pixel radius, the pure
    /// <c>SetLOD(screenRadius * lodBias)</c> step (<c>thinggeo.cpp:1624</c>).</summary>
    public float ApplyBias(float projectedPx) => projectedPx * Bias;
}
