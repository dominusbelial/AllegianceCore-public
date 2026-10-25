using Allegiance.Core;
using Allegiance.Core.Collision;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// P812 — the intermediate model the exporter writes and the re-import stub
/// reads. Deliberately <b>not</b> <see cref="MdlFile"/>/<see cref="CvhFile"/>:
/// the GLB layer works on plain structs so it can round-trip synthetic models
/// in tests without shipping any artwork, and so the eventual runtime loader
/// can be pointed at the port's own structures without a GLB dependency.
/// </summary>
public sealed record GlbExportModel
{
    public required string Name { get; init; }

    /// <summary>Geometry in <b>raw native .mdl space</b> (see <see cref="ModelSpace"/>).</summary>
    public required IReadOnlyList<GlbMesh> Meshes { get; init; }

    /// <summary>Mount points, in the same raw .mdl space as <see cref="Meshes"/>.</summary>
    public required IReadOnlyList<MountPoint> Mounts { get; init; }

    public required IReadOnlyList<GlbLight> Lights { get; init; }

    /// <summary>The .mdl's LODGeo baked pixel-radius thresholds, finest→coarsest
    /// (<c>engine/geometry.cpp</c> <c>LODGeoImpl::m_radii</c>).</summary>
    public required IReadOnlyList<float> LodPixelRadii { get; init; }

    /// <summary>Texture unit stems the model imports (e.g. <c>fig30bmp</c>) —
    /// the .mdl carries <b>no</b> image data; textures live in their own
    /// <c>&lt;stem&gt;bmp.mdl</c> compiled units, so a .glb export ships
    /// UVs only.</summary>
    public required IReadOnlyList<string> TextureStems { get; init; }

    /// <summary>Collision sidecar content (parsed <c>.cvh</c>); null when no
    /// <c>.cvh</c> was supplied.</summary>
    public required CollisionSidecar? Collision { get; init; }

    /// <summary>
    /// P823 — the source <c>.mdl</c>'s own <c>FrameData</c> records (name,
    /// position, forward and <b>up</b>, in file order) when the model came from
    /// a native file or a sidecar that carried them. The GLB node tree carries
    /// mount position + forward only — the up vector and the exact FrameData
    /// chunk layout ride the sidecar (see <see cref="SidecarDocument.MdlFrames"/>).
    /// Null for glb-only (new-art) models; the writer then synthesizes frames
    /// from the mount nodes.
    /// </summary>
    public IReadOnlyList<MdlFrameRecord>? SourceMdlFrames { get; init; }

    /// <summary>Coordinate-space provenance note carried verbatim into the sidecar.</summary>
    public const string ModelSpace =
        "raw native .mdl space: DirectX-style left-handed, +Z nose, +Y up, y-up 2-D images; " +
        "native renders the mesh with an RY(pi) flip (thinggeo.cpp:1501-1503) so the game-facing nose is -Z";
}

/// <summary>One drawable mesh (a native MeshGeo resolved under the "object" export).</summary>
public sealed record GlbMesh(
    string Name,
    string? TextureStem,
    float[] Positions,
    float[] TexCoords,
    float[] Normals,
    ushort[] Indices);

/// <summary>
/// One mount point exported as a GLB empty node <c>HP_&lt;Kind&gt;_&lt;Index&gt;</c>.
/// <see cref="Position"/>/<see cref="Forward"/> are in raw .mdl space; when the
/// mount came from a <c>.cvh</c> frame the values are the mirror-z mapping
/// <c>(x,y,z) → (x,y,−z)</c> applied to BOTH position and forward — verified
/// numerically against the .mdl's own FrameData on every shared frame of
/// fig30/bom04/ss27 (see the P812 report).
/// </summary>
public sealed record MountPoint
{
    public required string NodeName { get; init; }
    public required string Kind { get; init; }
    public required int Index { get; init; }

    /// <summary>The original frame name (e.g. <c>wepemt</c>, <c>garage1B</c>).</summary>
    public required string NativeName { get; init; }

    /// <summary>Which file(s) the mount came from: "mdl", "cvh" or both.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    public required float[] Position { get; init; }
    public required float[] Forward { get; init; }

    /// <summary>The .mdl FrameData forward when the mount also exists there
    /// (art files disagree on some frames — recorded, never silently merged).</summary>
    public float[]? MdlForward { get; init; }

    public float[]? MdlPosition { get; init; }
}

/// <summary>
/// P823 — one raw <c>.mdl</c> FrameData record: the native name plus position /
/// forward / <b>up</b>. The up vector is not expressible in the GLB node tree
/// (a node rotation carries forward only), so the sidecar carries it; the port
/// parser stores it on <see cref="Allegiance.Core.MdlFrame"/>.
/// </summary>
public sealed record MdlFrameRecord(string Name, float[] Position, float[] Forward, float[] Up);

/// <summary>A native LightsGeo light (effect/efapp.cpp:101-112 LightData);
/// blinking timing is not expressible in core glTF, so it rides in the sidecar.</summary>
public sealed record GlbLight(
    string Name,
    float[] Color,
    float[] Position,
    float Period,
    float Phase,
    float RampUp,
    float Hold,
    float RampDown);

/// <summary>
/// The collision sidecar: the parsed <c>.cvh</c> (raw file values — the native
/// load-time sign flips of <c>_Utility/HitTest.cpp:712-715, 790-797</c> are
/// recorded as a note, not applied) plus the .cvh's own frames.
/// </summary>
public sealed record CollisionSidecar
{
    public const string NativeLoadFlipNote =
        "native HitTest::Load (_Utility/HitTest.cpp:639, 712-715, 790-797) negates center.x and vertex.x " +
        "and flips frame forward.y/forward.z at load time; values here are RAW file values";

    public required float Radius { get; init; }
    public required float[] EllipseEquation { get; init; }
    public required float EllipseRadiusMultiplier { get; init; }
    public required IReadOnlyList<SidecarHull> Hulls { get; init; }
    public required IReadOnlyList<SidecarFrame> Frames { get; init; }
}

public sealed record SidecarHull(float[] Center, IReadOnlyList<SidecarVertex> Vertices);

public sealed record SidecarVertex(float[] P, int[] Adj);

public sealed record SidecarFrame(string Name, float[] Position, float[] Forward);

/// <summary>
/// Maps native frame-name conventions onto the wivuu/stellarallegiance
/// <c>HP_&lt;Kind&gt;_&lt;Index&gt;</c> hardpoint kinds. Native classifies frame
/// names by SUBSTRING when it builds FrameData (effect/efapp.cpp:562-583:
/// thrust/smoke/rocket → engine, weapon/wepatt/wepemt/wepmnt/trail → attach
/// point) and when it consumes them (thinggeo.cpp:935 "trail", :1428 "thrust");
/// the sim reads mount geometry from the .cvh frames by exact name
/// (Igc/hullTypeIGC.cpp:89-136 weapons, :158-204 launch/garage, :80 cockpt,
/// :85 chaff; Igc/stationtypeigc.cpp:57-138).
/// </summary>
public static class MountKindMapper
{
    /// <summary>Order defines HP node kind precedence (first match wins).</summary>
    public static readonly IReadOnlyList<(string Kind, Func<string, bool> Match)> Rules =
    [
        ("Turret", n => n.StartsWith("tur", StringComparison.Ordinal)),
        ("Weapon", n => n.Contains("wepemt", StringComparison.Ordinal)
                     || n.Contains("wepmnt", StringComparison.Ordinal)
                     || n.Contains("wepatt", StringComparison.Ordinal)
                     || n.Contains("weapon", StringComparison.Ordinal)
                     || n.Contains("missemt", StringComparison.Ordinal)),
        ("MainEngine", n => n.Contains("thrust", StringComparison.Ordinal)
                         || n.Contains("trail", StringComparison.Ordinal)),
        ("Thruster", n => n.Contains("smoke", StringComparison.Ordinal)
                       || n.Contains("rocket", StringComparison.Ordinal)),
        ("Cockpit", n => n.Contains("cockpt", StringComparison.Ordinal)),
        ("Chaff", n => n.Contains("chaff", StringComparison.Ordinal)),
        ("DockingEntrance", n => n.Contains("garage", StringComparison.Ordinal)),
        ("DockingExit", n => n.Contains("launch", StringComparison.Ordinal)),
    ];

    /// <summary>Escape kind for a frame none of the rules recognise.</summary>
    public const string FrameKind = "Frame";

    public static string KindFor(string nativeName)
    {
        foreach (var (kind, match) in Rules)
            if (match(nativeName))
                return kind;
        return FrameKind;
    }
}
