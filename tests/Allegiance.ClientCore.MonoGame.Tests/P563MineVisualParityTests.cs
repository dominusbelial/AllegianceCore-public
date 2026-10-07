using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P563 — pins the NATIVE-DERIVED decal half-extent for mine-field sprites.
/// Prior to P563 the port drew each mine decal at
/// <see cref="MineRenderer.DecalHalfExtent"/> = 10 world units (a 20-unit-wide
/// billboard), roughly 10× native — Domingo reported the disks "look a bit big
/// and too close to each other" live on 221 (2026-10-06).
///
/// Native derivation (authoritative, /home/domingo/Development/Allegiance):
///  <c>MineFieldGeoImpl::Render</c> draws each decal with scale 1, angle 0 and
///  zero forward/right (<c>src/effect/efgeo.cpp:74-84</c>). In
///  <c>Context::DrawDecal</c> that yields <c>decal.m_scale = mat.GetScale()*1</c>
///  (<c>src/engine/context.cpp:2292-2296</c>, the local-to-eye matrix scale),
///  and — because forward is zero — <c>Decal::GetVertices</c> builds the quad
///  with <c>dx = dy = m_scale</c> (cos0·m_scale, angle 0; context.cpp:218-240),
///  i.e. the quad spans ±<c>m_scale</c> about its centre: <b>half-extent =
///  m_scale</b>. <c>Matrix::GetScale()</c> is the length of the first column
///  (<c>src/zlib/matrix.cpp:80-84</c>), which for the mine field thing's rigid
///  (rotation+translation) local-to-eye transform is exactly <b>1.0</b>. So the
///  native world half-extent is 1.0 unit — the value this test pins.
/// </summary>
public class P563MineVisualParityTests
{
    [Fact]
    public void Decal_half_extent_equals_the_native_derived_world_value()
    {
        // Native: m_scale = mat.GetScale() * 1 (context.cpp:2295) and
        // GetVertices half-extent = m_scale with the mine's rigid transform
        // giving GetScale() = 1 (matrix.cpp:80-84). There is no per-mine
        // display inflation in the port.
        Assert.Equal(1.0f, MineRenderer.DecalHalfExtent, 6);
    }
}
