using System.Text.Json;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>Full sidecar document written next to each exported .glb.</summary>
public sealed record SidecarDocument
{
    public const string FormatId = "allegiance-mdl-sidecar/1";

    public required string Format { get; init; }
    public required string Mdl { get; init; }
    public required string Cvh { get; init; }
    public required string MdlSha256 { get; init; }
    public required string CvhSha256 { get; init; }
    public required string ModelSpace { get; init; }
    public required string ScaleHint { get; init; }
    public required string IndexWinding { get; init; }
    public required IReadOnlyList<string> TextureStems { get; init; }
    public required IReadOnlyList<float> LodPixelRadii { get; init; }
    public required IReadOnlyList<GlbLight> Lights { get; init; }
    public required IReadOnlyList<MountPoint> Mounts { get; init; }
    public required CollisionSidecar? Collision { get; init; }

    public static SidecarDocument Build(GlbExportModel model, string mdlPath, string cvhPath)
    {
        static string Sha(string path) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        return new SidecarDocument
        {
            Format = FormatId,
            Mdl = Path.GetFileName(mdlPath),
            Cvh = Path.GetFileName(cvhPath),
            MdlSha256 = File.Exists(mdlPath) ? Sha(mdlPath) : "",
            CvhSha256 = File.Exists(cvhPath) ? Sha(cvhPath) : "",
            ModelSpace = GlbExportModel.ModelSpace,
            ScaleHint = "native ship scale = 0.5*Length/cvh radius (Igc/hullTypeIGC.cpp:62); " +
                        "station scale = radius/originalRadius (Igc/stationtypeigc.cpp:51); " +
                        "cvh frame positions/forwards are unscaled model units",
            IndexWinding = "native DirectX clockwise-front (raw index order preserved); " +
                           "reverse each triangle for glTF/CCW-front consumers",
            TextureStems = model.TextureStems,
            LodPixelRadii = model.LodPixelRadii,
            Lights = model.Lights,
            Mounts = model.Mounts,
            Collision = model.Collision,
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, SidecarJson.Options);

    public static SidecarDocument FromJson(string json) =>
        JsonSerializer.Deserialize<SidecarDocument>(json, SidecarJson.Options)
        ?? throw new InvalidDataException("empty sidecar");
}
