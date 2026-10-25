using Allegiance.Core.Collision;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// P823 — the glb → mdl/cvh half of the pipeline: validates a
/// <see cref="GlbExportModel"/> (a <c>.glb</c> + optional sidecar re-imported
/// by <see cref="GlbReader"/>) and converts it to native's shipping formats —
/// a binary <c>.mdl</c> (<see cref="MdlWriter"/>) and a text <c>.cvh</c>
/// (<see cref="CvhWriter"/>). Fails loudly on anything the port's own parsers
/// (<c>MdlFile</c>, <c>CvhReader</c>) would reject.
/// </summary>
public static class GlbImporter
{
    public static (byte[] mdl, string cvh) Convert(GlbExportModel model)
    {
        Validate(model);

        var cvhText = model.Collision is not null
            ? CvhWriter.WriteExact(model.Collision)
            : CvhWriter.Generate(model);
        return (MdlWriter.Write(model), cvhText);
    }

    /// <summary>Structural validation before any bytes are written.</summary>
    public static void Validate(GlbExportModel model)
    {
        if (model.Meshes.Count == 0)
            throw new InvalidDataException("model has no meshes — a .mdl must carry geometry");

        foreach (var (mesh, mi) in model.Meshes.Select((m, i) => (m, i)))
        {
            if (mesh.Positions.Length == 0 || mesh.Positions.Length % 3 != 0)
                throw new InvalidDataException($"mesh {mi} ('{mesh.Name}'): positions must be a non-empty multiple of 3 floats");
            int verts = mesh.Positions.Length / 3;
            if (mesh.TexCoords.Length != verts * 2)
                throw new InvalidDataException($"mesh {mi} ('{mesh.Name}'): expected {verts * 2} texcoord floats, got {mesh.TexCoords.Length}");
            if (mesh.Normals.Length != verts * 3)
                throw new InvalidDataException($"mesh {mi} ('{mesh.Name}'): expected {verts * 3} normal floats, got {mesh.Normals.Length}");
            if (mesh.Indices.Length == 0 || mesh.Indices.Length % 3 != 0)
                throw new InvalidDataException($"mesh {mi} ('{mesh.Name}'): indices must be a non-empty multiple of 3 (triangle list)");
            foreach (var idx in mesh.Indices)
                if (idx >= verts)
                    throw new InvalidDataException($"mesh {mi} ('{mesh.Name}'): index {idx} out of range [0,{verts})");
            foreach (var v in mesh.Positions)
                if (!IsFinite(v))
                    throw new InvalidDataException($"mesh {mi} ('{mesh.Name}'): non-finite position component");
            foreach (var v in mesh.Normals)
                if (!IsFinite(v))
                    throw new InvalidDataException($"mesh {mi} ('{mesh.Name}'): non-finite normal component");
            foreach (var v in mesh.TexCoords)
                if (!IsFinite(v))
                    throw new InvalidDataException($"mesh {mi} ('{mesh.Name}'): non-finite texcoord component");
        }

        foreach (var mount in model.Mounts)
        {
            // Throws a clear InvalidDataException for malformed HP_ names.
            GlbReader.SplitNodeName(mount.NodeName);
            if (mount.Position.Length != 3 || mount.Forward.Length != 3)
                throw new InvalidDataException($"mount '{mount.NodeName}': position/forward must be 3-component");
        }

        foreach (var l in model.Lights)
            if (l.Color.Length != 4 || l.Position.Length != 3)
                throw new InvalidDataException($"light '{l.Name}': colour must be RGBA, position XYZ");

        if (model.Collision is not null)
            CvhWriter.Validate(model.Collision);
    }

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}
