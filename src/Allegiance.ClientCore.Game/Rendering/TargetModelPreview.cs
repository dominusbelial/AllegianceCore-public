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
    /// <summary>P805 (row 409) — an aleph (warp): native's preview shows the
    /// aleph's <c>LoadAleph</c> surface ThingGeo (warpIGC.cpp:33-34;
    /// modelIGC.cpp:127-132).</summary>
    Warp,
    /// <summary>P805 (row 409) — a probe: native loads a REAL mesh per probe
    /// type (<c>HitTest::Load(m_data.modelName)</c>, probetypeigc.cpp:28-33).
    /// </summary>
    Probe,
    /// <summary>P805 (row 409) — a treasure: the buyable's model (acs14 /
    /// artifact / part model) or the flag's LoadDecal billboard
    /// (treasureIGC.cpp:118-171).</summary>
    Treasure,
    /// <summary>P805 (row 409) — a mine. NOT reachable through the port yet
    /// (the port's target cycle has no Mine kind); the enum value exists so the
    /// gate and art resolve are complete (native LoadMine sprite,
    /// modelIGC.cpp:134-139; mineigc.cpp:103).</summary>
    Mine,
}

/// <summary>
/// HUD-21b — the resolved-preview cache key: what the preview art depends on.
/// The native preview re-resolves the target's <c>ThingGeo</c> every frame, but
/// the PORT's lookup path (<c>ResolveNativeShip</c>'s <c>NativeShipRef</c>, the
/// per-renderer <c>TextureStemFor</c> closure + string concat, the mesh dictionary
/// probes) allocates, so the GPU path caches the resolved (geometry, texture,
/// scale) and refreshes only when this key changes: the selected target, or the
/// target's hull/model identity (<c>WinTrek.cpp:8783-8818</c> resolves the mesh
/// from the model, and a hull or asteroid/station model swap is the only thing
/// that changes the art). A readonly struct of ints + string REFERENCES, so
/// building and comparing it is allocation-free. <see cref="Matches"/> is
/// pure and headless-testable.
/// </summary>
public readonly struct TargetPreviewKey
{
    public readonly TargetPreviewKind Kind;
    public readonly int TargetId;
    public readonly int HullId;
    public readonly string? ModelStem;
    public readonly string? TextureHint;

    /// <summary>P805b — a treasure's <c>DataTreasureIGC.TreasureId</c> payload (the
    /// part/development id, the cash amount, the flag's side or the team-lobby
    /// sentinel). Native resolves a treasure's art from this payload, NOT from the
    /// object id (<c>treasureIGC.cpp:118-171</c>); P805 passed the object id, so
    /// every PART treasure resolved to an unknown part and showed nothing.</summary>
    public readonly int PayloadId;

    public TargetPreviewKey(TargetPreviewKind kind, int targetId, int hullId,
        string? modelStem, string? textureHint, int payloadId = 0)
    {
        Kind = kind;
        TargetId = targetId;
        HullId = hullId;
        ModelStem = modelStem;
        TextureHint = textureHint;
        PayloadId = payloadId;
    }

    /// <summary>True when this key identifies the same target AND the same
    /// hull/model/texture — i.e. the cached (geo, tex, scale) is still valid.
    /// Ordinal string compares (reference/contents, no allocation).</summary>
    public bool Matches(in TargetPreviewKey other) =>
        Kind == other.Kind
        && TargetId == other.TargetId
        && HullId == other.HullId
        && PayloadId == other.PayloadId
        && string.Equals(ModelStem, other.ModelStem, StringComparison.Ordinal)
        && string.Equals(TextureHint, other.TextureHint, StringComparison.Ordinal);
}

/// <summary>
/// HUD-21b (Claude review) — the preview's key → cache → gate step, pulled out
/// of <c>Game1.RenderTargetModelPreview</c> so it is headless-testable (no
/// <c>GraphicsDevice</c>): production calls <see cref="Step"/> once per frame
/// with the target facts it read under the session gate, and the allocating
/// art resolve (<c>ResolveNativeShip</c>'s <c>NativeShipRef</c>, the
/// <c>TextureStemFor</c> closure + concat) runs only when the
/// <see cref="TargetPreviewKey"/> changes. A target whose model has NO native
/// mesh is cached too (a null resolution), so it is resolved once, not every
/// frame; the gate then sees the real geometry-present state.
/// </summary>
/// <typeparam name="TArt">The resolved art (the GPU geometry/texture/scale
/// bundle in production; any reference type in tests).</typeparam>
public sealed class TargetPreviewCache<TArt> where TArt : class
{
    private readonly Func<TargetPreviewKey, TArt?> _resolve;
    private TargetPreviewKey _key;
    private bool _resolved;
    private TArt? _art;

    /// <param name="resolve">Resolves a key to its art, or null when the target
    /// has no native mesh. Called only on a key change.</param>
    public TargetPreviewCache(Func<TargetPreviewKey, TArt?> resolve) => _resolve = resolve;

    /// <summary>How many times the resolver ran (tests/diagnostics).</summary>
    public int ResolveCount { get; private set; }

    /// <summary>The cached resolution (null = unresolved or no mesh).</summary>
    public TArt? Art => _art;

    /// <summary>Forget the cached resolution (next <see cref="Step"/> re-resolves).</summary>
    public void Invalidate()
    {
        _resolved = false;
        _art = null;
    }

    /// <summary>
    /// One frame: re-resolve only when <paramref name="key"/> differs from the
    /// cached key (a null — no-mesh — result is cached as well), then apply the
    /// native <c>SetTargetGeo</c> gate (<see cref="TargetModelPreview.Eligible"/>,
    /// <c>WinTrek.cpp:8783-8818</c>) with <c>hasMesh</c> = the real resolution.
    /// Returns the art to draw, or null for the empty fallback. Allocation-free
    /// when the key is unchanged.
    /// </summary>
    public TArt? Step(in TargetPreviewKey key, int targetSourceShipId, int ownSourceShipId,
        bool sameCluster, bool visible)
    {
        if (!_resolved || !_key.Matches(key))
        {
            _art = _resolve(key);
            _key = key;
            _resolved = true;
            ResolveCount++;
        }
        return TargetModelPreview.Eligible(key.Kind, targetSourceShipId, ownSourceShipId,
            sameCluster: sameCluster, visible: visible, hasMesh: _art is not null) ? _art : null;
    }
}

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

    /// <summary>Native's <c>TurnTo</c> near-parallel threshold applies to the
    /// SIGNED cos², not cos (<c>Orientation::TurnTo</c> calls
    /// <c>CosUp2(target)</c> and compares ±0.99, <c>zlib/orientation.cpp:220-231</c>;
    /// <c>cosX2</c> returns <c>±dot²/l²</c>, <c>orientation.cpp:395-414</c>). On
    /// cos² this means |cos| &gt; √0.99 ≈ 0.99499, so e.g. cos 0.992 keeps the
    /// player's up (HUD-21 compared raw cos and wrongly flipped at 0.99).</summary>
    public const float NearParallelCos2 = 0.99f;

    /// <summary>Native default dp when |dp|² &lt; 1 (<c>WinTrek.cpp:438-440</c>).</summary>
    public static readonly Vector3 DefaultDp = new(0f, 1f, 5f);

    /// <summary>
    /// The native <c>SetTargetGeo</c> gate (<c>WinTrek.cpp:8783-8818</c>): a model
    /// preview is shown only for a model whose SOURCE ship is not the player's
    /// own source ship, that is in the player's cluster, is visible
    /// (CanSee) and resolves to a native mesh. P805 (row 409) extends the kind
    /// set to warps/probes/treasures/mines: native's gate is NOT kind-specific —
    /// it requires only a hit test (<c>GetHitTest() != NULL</c>) and a ThingSite
    /// with a ThingGeo, and every one of these models loads one (aleph surface
    /// warpIGC.cpp:33; probe mesh probetypeigc.cpp:28-33; mine sprite
    /// mineigc.cpp:103; treasure decal/model treasureIGC.cpp:118-171).
    /// <para>The own-ship test is the native one: it compares the target's
    /// <c>GetSourceShip()</c> with <c>trekClient.GetShip()-&gt;GetSourceShip()</c>
    /// (<c>WinTrek.cpp:8796-8797</c>), so the caller climbs BOTH sides' parent
    /// chain first (a gunner's own carrier, or one's own turret, shows nothing).
    /// </para>
    /// </summary>
    public static bool Eligible(
        TargetPreviewKind kind, int targetSourceShipId, int ownSourceShipId,
        bool sameCluster, bool visible, bool hasMesh)
        => targetSourceShipId != ownSourceShipId && sameCluster && visible && hasMesh
           && kind is TargetPreviewKind.Ship or TargetPreviewKind.Station or TargetPreviewKind.Asteroid
              or TargetPreviewKind.Warp or TargetPreviewKind.Probe or TargetPreviewKind.Treasure
              or TargetPreviewKind.Mine;

    /// <summary>
    /// P805b — which native draw path a resolved preview art uses. The kind alone
    /// does not decide it: a treasure is a MESH (its buyable's model) or a DECAL
    /// (the flag's <c>biosflag</c> billboard, <c>treasureIGC.cpp:118-171</c>), and
    /// a kind whose art is missing must draw nothing rather than dereference a
    /// null visual. Pure so the dispatch is pinned headlessly — the P805 resolve
    /// left <see cref="TargetPreviewKind.Probe"/> without a treasure visual while
    /// the draw switch still read <c>art.Treasure!.Value</c>, which threw on the
    /// first real probe target.
    /// </summary>
    public enum PreviewDrawPath
    {
        /// <summary>No drawable art — the native empty fallback.</summary>
        None,
        /// <summary>A native ship mesh (<see cref="TargetPreviewKind.Ship"/>).</summary>
        Ship,
        /// <summary>A native station mesh.</summary>
        Station,
        /// <summary>A native asteroid mesh.</summary>
        Asteroid,
        /// <summary>A camera-facing surface quad (a flag treasure's
        /// <c>biosflag</c> decal).</summary>
        Decal,
        /// <summary>The aleph vortex mesh (native <c>LoadAleph</c> builds the
        /// additive textured funnel, not a flat surface;
        /// <c>effect/alephgeo.cpp:80-102,205-232</c>).</summary>
        WarpFunnel,
        /// <summary>A model mesh drawn through the native treasure renderer (a
        /// probe's per-type mesh, <c>probetypeigc.cpp:28-33</c>, or a model
        /// treasure, <c>treasureIGC.cpp:118-171</c>).</summary>
        TreasureModel,
    }

    /// <summary>
    /// P805b — the native draw path for a resolved preview: the target kind plus
    /// which art actually resolved (a mesh vs the treasure visual vs a decal vs
    /// the aleph funnel). Never selects a path whose art is absent, so a resolve
    /// gap shows the native empty viewport instead of throwing.
    /// </summary>
    public static PreviewDrawPath DrawPathFor(TargetPreviewKind kind,
        bool hasShipGeo, bool hasTreasureVisual, bool treasureIsDecal, bool hasWarpTexture)
    {
        switch (kind)
        {
            case TargetPreviewKind.Ship:
                return hasShipGeo ? PreviewDrawPath.Ship : PreviewDrawPath.None;
            case TargetPreviewKind.Station:
                return hasShipGeo ? PreviewDrawPath.Station : PreviewDrawPath.None;
            case TargetPreviewKind.Asteroid:
                return hasShipGeo ? PreviewDrawPath.Asteroid : PreviewDrawPath.None;
            case TargetPreviewKind.Warp:
                // warpIGC.cpp:33-34 / modelIGC.cpp:127-132 — LoadAleph builds the
                // vortex with textureName+"bmp" as its texture.
                return hasWarpTexture ? PreviewDrawPath.WarpFunnel : PreviewDrawPath.None;
            case TargetPreviewKind.Treasure:
                if (!hasTreasureVisual) return PreviewDrawPath.None;
                return treasureIsDecal ? PreviewDrawPath.Decal : PreviewDrawPath.TreasureModel;
            case TargetPreviewKind.Probe:
            case TargetPreviewKind.Mine:
                // A probe/mine ThingGeo is a real mesh/sprite
                // (probetypeigc.cpp:28-33; mineigc.cpp:103).
                return hasTreasureVisual ? PreviewDrawPath.TreasureModel : PreviewDrawPath.None;
            default:
                return PreviewDrawPath.None;
        }
    }

    /// <summary>Native dp = target−player; the constant <c>(0,1,5)</c> replaces
    /// it when the real dp is inside the unit sphere (<c>WinTrek.cpp:438-444</c>).</summary>
    public static Vector3 EffectiveDp(in Vector3 targetDirXna)
        => targetDirXna.LengthSquared() < 1f ? DefaultDp : targetDirXna;

    /// <summary>
    /// The camera up after <c>Orientation::TurnTo</c> (<c>orientation.cpp:220-231</c>):
    /// the player's up, except when the view direction is near-parallel to it
    /// where it falls back to ±forward (∓forward) — the native singular-avoidance
    /// rule. The comparison uses <c>CosUp2</c>'s SIGNED cos²
    /// (<c>orientation.cpp:395-414,431-434</c>), so the effective |cos| threshold
    /// is √0.99 ≈ 0.99499.
    /// </summary>
    public static Vector3 ViewUp(in Vector3 dirNorm, in Vector3 ownUp, in Vector3 ownForward)
    {
        float dot = Vector3.Dot(dirNorm, ownUp);
        float c2 = dot * MathF.Abs(dot);          // CosUp2: signed cos² (dot²/l²)
        if (c2 < -NearParallelCos2) return ownForward;   // c2 < -0.99 → +forward
        if (c2 > NearParallelCos2) return -ownForward;   // c2 > +0.99 → −forward
        return ownUp;
    }

    /// <summary>
    /// HUD-21b (Claude review) — the camera's world up and forward from a flight
    /// view matrix. Every flight view is <c>Matrix.CreateLookAt</c>
    /// (<c>CockpitCamera.cs</c>, <c>Director.cs</c>, <c>FreeCam.cs</c>,
    /// <c>ExternalCameras.cs</c>, <c>CommandCamera.cs</c>), which stores the
    /// camera basis in the matrix COLUMNS: right = (M11,M21,M31), up =
    /// (M12,M22,M32), backward = (M13,M23,M33) (same reading as
    /// <c>Game1.VolumetricProof.cs</c>). HUD-21b read the rows, i.e. the
    /// transposed basis, which is only right for an identity/axis-aligned camera.
    /// </summary>
    public static void CameraBasis(in Matrix view, out Vector3 up, out Vector3 forward)
    {
        up = new Vector3(view.M12, view.M22, view.M32);
        forward = new Vector3(-view.M13, -view.M23, -view.M33);
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
    /// The radius-1 preview scale: <c>1 / MaxVertexRadius</c>, the net of
    /// <c>TargetGeo::Render</c>'s <c>Scale3(1/m_radius)</c>
    /// (<c>effect/thinggeo.cpp:373-381</c>) and <c>RenderTargetGeo</c>'s
    /// <c>m_scale = radius/m_radiusMesh</c> (<c>thinggeo.cpp:1185-1193,1482-1573</c>)
    /// — where native <c>m_radiusMesh</c> is the MAXIMUM VERTEX DISTANCE
    /// (<c>engine/geometry.cpp:408-418</c>, <c>thinggeo.cpp:1053</c>), not a
    /// bounding-box sphere. A zero/absent radius keeps scale 1 (never NaN).
    /// </summary>
    public static float ScaleForMaxVertexRadius(float maxVertexRadius)
        => maxVertexRadius > 0f ? 1f / maxVertexRadius : 1f;

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
