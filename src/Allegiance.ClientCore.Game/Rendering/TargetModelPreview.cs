using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-21 — the PURE rules for the classic target pane's 3-D model preview
/// (parity row 409, "Target model preview", native
/// <c>GeoImage(targetGeo, Rect(0,0,96,96), targetCamera, true)</c>,
/// <c>Artwork/dialog.mdl:4224-4231</c>). No <see cref="GraphicsDevice"/> types
/// appear here, mirroring <see cref="TargetPaneModel"/> / P529's pure-mapping
/// division: the matrices, the eligibility gate and the light are all
/// headless-testable; <see cref="TargetModelPreviewRenderer"/> owns the GPU
/// render target and calls the native mesh renderers.
/// </summary>
/// <remarks>
/// Native chain (cited in the HUD-21 report):
/// <list type="bullet">
/// <item>Eligibility: <c>WinTrek/WinTrek.cpp:8783-8818</c>
/// <c>SetTargetGeo</c> (own ship / other cluster / not visible / no ThingGeo
/// → empty).</item>
/// <item>Rotation + default dp: <c>WinTrek/WinTrek.cpp:431-452</c>
/// (<c>CameraControl::Apply</c>), <c>TurnTo</c> at
/// <c>zlib/orientation.cpp:220-231</c>, default <c>dp=(0,1,5)</c> when
/// <c>|dp|² &lt; 1</c>.</item>
/// <item>Camera: <c>WinTrek/WinTrek.cpp:328-330</c> (eye distance 8, ZClip
/// 1..20), ortho branch of <c>Camera::Evaluate</c>
/// (<c>engine/camera.cpp:46-69</c>), <c>GeoImage::Render</c>'s
/// <c>Scale2(0.5·rect.XSize())</c> (<c>engine/image.cpp:1164-1165</c>).</item>
/// <item>Radius-1 framing: <c>TargetGeo::Render</c>
/// (<c>effect/thinggeo.cpp:373-381</c>) and <c>RenderTargetGeo</c>
/// (<c>thinggeo.cpp:1482-1573</c>), net scale <c>1/m_radiusMesh</c>.</item>
/// <item>Light: <c>GeoImage</c> default <c>m_vecLight(0,0,1)</c>, white,
/// non-bidirectional, ambient 0 (<c>engine/image.cpp:1120-1129</c>);
/// <c>Device3D::DirectionalLight</c> (<c>engine/device3d.cpp:444-452</c>) +
/// the light apply at <c>device3d.cpp:734-752</c> with <c>m_matLight</c>
/// (<c>device3d.cpp:295-297</c>).</item>
/// </list>
/// </remarks>
public enum TargetPreviewKind
{
    /// <summary>A ship (after the native <c>GetSourceShip</c> parent climb).</summary>
    Ship,
    Station,
    Asteroid,
}

/// <summary>
/// HUD-21 — the target pane's decorative child paint order (native
/// <c>DefaultNormalTargetDisplay</c>, <c>dialog.mdl:4218-4242</c>, rendered
/// first-to-last by <c>GroupImage::Render</c> at <c>engine/image.cpp:954-972</c>).
/// The model preview is a <c>GeoImage</c> child that paints AFTER the two target
/// gauges and BEFORE <c>TargetOval</c>/<c>TargetOvalColor</c> and the words, so
/// the oval art sits on top of the model. <see cref="DrawTargetPane"/> paints in
/// exactly this order (the model helper runs before the oval art calls); the
/// static list is the documented, testable source of truth for that order (the
/// per-frame path draws directly, never allocating a list).
/// </summary>
public enum PaneDecorKind { Model, Oval, OvalColor, OvalWord1, OvalWord2 }

/// <summary>See <see cref="TargetModelPreview"/>.</summary>
public static class TargetModelPreview
{
    /// <summary>Native eye distance from the target (<c>WinTrek.cpp:330</c>
    /// <c>SetPosition(0,0,8)</c>).</summary>
    public const float EyeDistance = 8f;

    /// <summary>Native ZClip far bound (<c>WinTrek.cpp:329</c>).</summary>
    public const float ZNear = 1f;
    /// <summary>Native ZClip far bound (<c>WinTrek.cpp:329</c>).</summary>
    public const float ZFar = 20f;

    /// <summary>Orthographic viewport width/height: one model unit (after the
    /// radius-1 normalisation) = half the viewport, so a radius-1 unit sphere
    /// fills the full 96 px pane (<c>camera.cpp:46-69</c>,
    /// <c>image.cpp:1164-1165</c>).</summary>
    public const float ViewportUnits = 2f;

    /// <summary>The |cos| threshold above which <c>TurnTo</c> drops the player's
    /// up for ±forward (<c>orientation.cpp:220-231</c>).</summary>
    public const float NearParallelCos = 0.99f;

    /// <summary>Native default dp when |dp|² &lt; 1 (<c>WinTrek.cpp:438-440</c>).</summary>
    public static readonly Vector3 DefaultDp = new(0f, 1f, 5f);

    /// <summary>The documented target-pane decorative paint order (see
    /// <see cref="PaneDecorKind"/>). Static — built once, never per-frame.</summary>
    public static readonly PaneDecorKind[] DecorOrder =
        [PaneDecorKind.Model, PaneDecorKind.Oval, PaneDecorKind.OvalColor,
         PaneDecorKind.OvalWord1, PaneDecorKind.OvalWord2];

    /// <summary>
    /// The native <c>SetTargetGeo</c> gate (<c>WinTrek.cpp:8783-8818</c>): a
    /// model preview is shown only for a ship/station/asteroid that is not the
    /// player's own ship, is in the player's cluster, is visible (CanSee) and
    /// resolves to a native mesh. Warp/probe/mine/missile/treasure (scope item 6)
    /// and no-mesh fall back to the empty viewport.
    /// </summary>
    public static bool Eligible(
        TargetPreviewKind kind, bool isOwnShip, bool sameCluster, bool visible, bool hasMesh)
        => !isOwnShip && sameCluster && visible && hasMesh
           && kind is TargetPreviewKind.Ship or TargetPreviewKind.Station or TargetPreviewKind.Asteroid;

    /// <summary>Native dp = target−player; the constant <c>(0,1,5)</c> replaces
    /// it when the real dp is inside the unit sphere (<c>WinTrek.cpp:438-444</c>).</summary>
    public static Vector3 EffectiveDp(in Vector3 targetDirXna)
        => targetDirXna.LengthSquared() < 1f ? DefaultDp : targetDirXna;

    /// <summary>
    /// The camera up after <c>Orientation::TurnTo</c> (<c>orientation.cpp:220-231</c>):
    /// the player's up, except when the view direction is near-parallel to it
    /// (|cos| &gt; 0.99) where it falls back to ±forward (∓forward), exactly the
    /// native singular-avoidance rule.
    /// </summary>
    public static Vector3 ViewUp(in Vector3 dirNorm, in Vector3 ownUp, in Vector3 ownForward)
    {
        float dot = Vector3.Dot(dirNorm, ownUp);
        if (dot < -NearParallelCos) return ownForward;   // dot < -0.99 → +forward
        if (dot > NearParallelCos) return -ownForward;   // dot > +0.99 → −forward
        return ownUp;
    }

    /// <summary>
    /// The native preview view: eye at <c>−normalize(dp)·8</c> looking at the
    /// origin (the target), up = the player's up (<c>WinTrek.cpp:328-330,438-444</c>;
    /// <c>orientation.cpp:220-231</c>). Inputs are already XNA (PoseConverter).
    /// </summary>
    public static Matrix View(in Vector3 targetDirXna, in Vector3 ownUpXna, in Vector3 ownForwardXna)
    {
        var dp = EffectiveDp(targetDirXna);
        var dir = Vector3.Normalize(dp);
        var eye = -dir * EyeDistance;
        var up = ViewUp(dir, ownUpXna, ownForwardXna);
        return Matrix.CreateLookAt(eye, Vector3.Zero, up);
    }

    /// <summary>The orthographic projection (<c>2,2,1,20</c>) — the
    /// <c>m_focus==0</c> ortho branch of <c>Camera::Evaluate</c>
    /// (<c>camera.cpp:46-69</c>) with the native ZClip 1..20
    /// (<c>WinTrek.cpp:328-330</c>) and <c>GeoImage</c>'s <c>Scale2(0.5·XSize)</c>
    /// (<c>image.cpp:1164-1165</c>).</summary>
    public static Matrix Projection()
        => Matrix.CreateOrthographic(ViewportUnits, ViewportUnits, ZNear, ZFar);

    /// <summary>
    /// The 96×96 pane-local preview rect, y-up lower-left at (270,9)
    /// (<c>dialog.mdl:4224-4231</c>) → screen y-down top-left. Returns a
    /// 96·s square whose lower-left sits at <c>(x0+270s, y0−9s)</c>.
    /// </summary>
    public static Rectangle PaneRect(float x0, float y0, float s)
    {
        int size = (int)MathF.Round(96f * s);
        int x = (int)MathF.Round(x0 + 270f * s);
        int top = (int)MathF.Round(y0 - 9f * s - size);
        return new Rectangle(x, top, size, size);
    }

    /// <summary>
    /// The preview's native light (<c>GeoImage</c> default): a single white
    /// directional light, no alt light, ambient 0 (<c>image.cpp:1120-1129</c>,
    /// <c>device3d.cpp:444-452,734-752,295-297</c>). Sign derivation (full in
    /// the HUD-21 report, reusing P529's reasoning): native's brightest face
    /// normal aligns with the stored light direction (<c>DirectionalLight</c>
    /// stores <c>(0,0,1)</c>, <c>m_matLight</c> negates x/z → D3D lights the
    /// face whose normal = +stored, i.e. the toward-CAMERA side; the native
    /// preview camera is fixed at +Z looking at the target, so the light is a
    /// headlight). The port reproduces the invariant "the camera-facing side is
    /// lit" by pointing the key propagation from the camera toward the target
    /// (<c>+normalize(dp)</c>), which BasicEffect lights as normal =
    /// <c>−propagation</c> = toward the camera. The alt light is encoded as
    /// black (the renderer's <see cref="SectorLightingEffect"/> always enables
    /// Light1, so black is the "no alt light" encoding); ambient is 0.
    /// </summary>
    public static SectorLighting Lighting(in Vector3 targetDirXna)
    {
        var dp = EffectiveDp(targetDirXna);
        var dir = Vector3.Normalize(dp);
        var toward = -dir;                       // camera-facing (bright-normal)
        return new SectorLighting(
            ToSim(-toward),                      // key propagation = +dir
            new SimVec(1f, 1f, 1f),              // white
            ToSim(toward),                       // alt propagation = −dir, black → no alt
            new SimVec(0f, 0f, 0f),
            new SimVec(0f, 0f, 0f));             // ambient 0
    }

    private static SimVec ToSim(Vector3 v) => new(v.X, v.Y, v.Z);
}
