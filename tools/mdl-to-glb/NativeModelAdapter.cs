using Allegiance.Core;
using Allegiance.Core.Collision;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// Builds a <see cref="GlbExportModel"/> from the port's own parsed
/// <see cref="MdlFile"/> + <see cref="CvhFile"/>.
/// </summary>
public static class NativeModelAdapter
{
    public static GlbExportModel FromNative(string stem, MdlFile mdl, CvhFile? cvh)
    {
        var meshes = new List<GlbMesh>();
        for (int i = 0; i < mdl.Meshes.Count; i++)
        {
            var m = mdl.Meshes[i];
            meshes.Add(new GlbMesh(
                Name: m.TextureStem is { } tex ? $"{stem}_{tex}_{i}" : $"{stem}_{i}",
                TextureStem: m.TextureStem,
                Positions: m.Positions,
                TexCoords: m.TexCoords,
                Normals: m.Normals,
                Indices: m.Indices));
        }

        return new GlbExportModel
        {
            Name = stem,
            Meshes = meshes,
            Mounts = BuildMounts(mdl, cvh),
            Lights = BuildLights(mdl),
            LodPixelRadii = mdl.LodLevels.Select(l => l.PixelRadius).ToArray(),
            TextureStems = mdl.Meshes.Select(m => m.TextureStem).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Collision = cvh is null ? null : BuildCollision(cvh),
        };
    }

    /// <summary>
    /// Merge the .mdl FrameData and the .cvh frames into one mount set keyed by
    /// native name. The .cvh is the SIM-authoritative source (it is the file the
    /// native hull/station loaders read: Igc/hullTypeIGC.cpp:89-136, 158-204;
    /// Igc/stationtypeigc.cpp:57-138), so a cvh frame wins; the .mdl FrameData
    /// fill mounts the cvh lacks and are kept as the FX-side record.
    /// </summary>
    public static List<MountPoint> BuildMounts(MdlFile mdl, CvhFile? cvh)
    {
        var mounts = new List<MountPoint>();
        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(string nativeName, float[] pos, float[] fwd, string source, float[]? mdlPos, float[]? mdlFwd)
        {
            var kind = MountKindMapper.KindFor(nativeName);
            int idx = byKind.TryGetValue(kind, out var k) ? k : 0;
            byKind[kind] = idx + 1;
            mounts.Add(new MountPoint
            {
                NodeName = $"HP_{kind}_{idx}",
                Kind = kind,
                Index = idx,
                NativeName = nativeName,
                Sources = [source],
                Position = pos,
                Forward = fwd,
                MdlPosition = mdlPos,
                MdlForward = mdlFwd,
            });
        }

        // cvh first (authoritative). cvh file space → mdl space is a pure
        // MIRROR-Z of the file values, applied to position AND forward:
        // (x,y,z) → (x,y,−z). Verified against every cvh∩mdl shared frame of
        // fig30/bom04/ss27 (positions bit-near-exact, forwards ≤0.002 — text
        // rounding). Native then applies its own further sign flips to the
        // loaded values (HitTest.cpp:712-715, 790-797), mirrored by the port's
        // HullLandingBay.Transform; those live in game space, not file space.
        if (cvh is not null)
        {
            foreach (var f in cvh.Frames)
            {
                Add(f.Name,
                    [f.Position.X, f.Position.Y, -f.Position.Z],
                    [f.Forward.X, f.Forward.Y, -f.Forward.Z],
                    "cvh", null, null);
            }
        }

        // .mdl frames: add only names the cvh did not already provide, but carry
        // their position/forward into the matching cvh mount as Mdl* evidence.
        foreach (var f in mdl.Frames)
        {
            var existing = mounts.FirstOrDefault(m => m.NativeName.Equals(f.Name, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (!existing.Sources.Contains("mdl"))
                {
                    var i = mounts.IndexOf(existing);
                    mounts[i] = existing with { Sources = [.. existing.Sources, "mdl"], MdlPosition = f.Position, MdlForward = f.Forward };
                }
                continue;
            }
            Add(f.Name, f.Position, f.Forward, "mdl", f.Position, f.Forward);
        }

        return mounts;
    }

    private static List<GlbLight> BuildLights(MdlFile mdl)
    {
        var lights = new List<GlbLight>(mdl.Lights.Count);
        for (int i = 0; i < mdl.Lights.Count; i++)
        {
            var l = mdl.Lights[i];
            lights.Add(new GlbLight($"Light_{i}", l.Color, l.Position, l.Period, l.Phase, l.RampUp, l.Hold, l.RampDown));
        }
        return lights;
    }

    internal static CollisionSidecar BuildCollision(CvhFile cvh) => new()
    {
        Radius = cvh.Radius,
        EllipseEquation = [cvh.EllipseEquation.X, cvh.EllipseEquation.Y, cvh.EllipseEquation.Z],
        EllipseRadiusMultiplier = cvh.EllipseRadiusMultiplier,
        Hulls = cvh.Hulls.Select(h => new SidecarHull(
            [h.Center.X, h.Center.Y, h.Center.Z],
            h.Vertices.Select(v => new SidecarVertex(
                [v.Position.X, v.Position.Y, v.Position.Z],
                (int[])v.AdjacentIndices.Clone())).ToArray())).ToArray(),
        Frames = cvh.Frames.Select(f => new SidecarFrame(
            f.Name,
            [f.Position.X, f.Position.Y, f.Position.Z],
            [f.Forward.X, f.Forward.Y, f.Forward.Z])).ToArray(),
    };
}
