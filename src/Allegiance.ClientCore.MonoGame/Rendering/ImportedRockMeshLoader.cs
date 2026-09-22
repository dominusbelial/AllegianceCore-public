using System.Buffers.Binary;
using Microsoft.Xna.Framework.Graphics;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Loads one of the three imported Meshy asteroid meshes (drgrock / rrocket /
/// tacrock — shipped through the MonoGame content pipeline as FBX, exactly the
/// ship-model precedent) into a <see cref="RockMesh"/> so
/// <see cref="AsteroidRenderer"/> can build its vertex/index buffers from real
/// imported geometry instead of (or alongside) the fully-procedural
/// <see cref="RockMeshFactory"/> rock.
/// </summary>
/// <remarks>
/// <para>
/// The raw data is pulled out of the content-pipeline <see cref="XnaModel"/>
/// (position / normal / texture-coordinate per vertex, triangle-list indices),
/// concatenating every mesh part, then handed to the pure
/// <see cref="Finalize"/> stage. The imported geometry is intentionally <b>not</b>
/// used as a MonoGame <c>Model</c> at draw time — <see cref="AsteroidRenderer"/>
/// builds its own VertexBuffer/IndexBuffer from the resulting <see cref="RockMesh"/>,
/// so the Meshy baked (flat gray) texture is discarded entirely in favour of the
/// port's procedural <see cref="RockTextureFactory"/> diffuse.
/// </para>
/// <para>
/// Finalize mirrors <see cref="RockMeshFactory"/>'s exact conventions so the
/// imported meshes behave like the procedural one under the renderer's
/// <c>Matrix.CreateScale(ast.Radius)</c>: positions are centred on the origin
/// and normalized to unit extent, per-vertex normals are recomputed as smooth
/// outward face-average normals (the P251 discipline — the imported normals are
/// discarded), and the triangle winding is fixed so normal back-face culling
/// works despite the Meshy/Blender FBX back-facing winding quirk that forces
/// <see cref="ShipRenderer"/> to use <c>CullNone</c> (see its remarks).
/// </para>
/// </remarks>
public static class ImportedRockMeshLoader
{
    /// <summary>
    /// Convert a content-pipeline <see cref="XnaModel"/> into a unit-extent
    /// <see cref="RockMesh"/>. Every mesh/mesh-part is concatenated; indices
    /// are remapped into the compact concatenated vertex list.
    /// </summary>
    public static RockMesh FromModel(XnaModel model)
    {
        var positions = new List<float>();
        var uvs = new List<float>();
        var indices = new List<int>();

        foreach (var mesh in model.Meshes)
        {
            if (mesh.MeshParts.Count == 0) continue;

            VertexBuffer vb = mesh.MeshParts[0].VertexBuffer;
            IndexBuffer ib = mesh.MeshParts[0].IndexBuffer;

            var decl = vb.VertexDeclaration;
            int stride = decl.VertexStride;
            int posOff = ElementOffset(decl, VertexElementUsage.Position, 0);
            int uvOff = ElementOffset(decl, VertexElementUsage.TextureCoordinate, 0);
            if (posOff < 0)
                throw new InvalidOperationException("imported mesh has no Position element");

            // Read the whole shared vertex buffer as raw bytes, then pull each
            // vertex's fields at their declaration offsets (robust to extra
            // channels / element ordering in the FBX).
            var raw = new byte[vb.VertexCount * stride];
            vb.GetData<byte>(0, raw, 0, raw.Length, 0);

            // Read the whole shared index buffer once (16- or 32-bit).
            bool is32 = ib.IndexElementSize == IndexElementSize.ThirtyTwoBits;
            var idx32 = is32 ? new uint[ib.IndexCount] : null;
            var idx16 = is32 ? null : new ushort[ib.IndexCount];
            if (is32) ib.GetData(idx32!);
            else ib.GetData(idx16!);

            int vertexBase = positions.Count / 3;
            var remap = new Dictionary<int, int>();
            int nextVertex = 0;

            foreach (var part in mesh.MeshParts)
            {
                int start = part.StartIndex;
                for (int k = 0; k < part.PrimitiveCount * 3; k++)
                {
                    // P340 (U3) — a MeshPart's index values are relative to
                    // part.VertexOffset (they select among the part's vertices
                    // in the shared buffer), so the resolved vertex index into
                    // the shared VertexBuffer must add it. FBX imports happen to
                    // produce VertexOffset=0 today, masking this; any future
                    // import path that doesn't would read the wrong vertices
                    // (and silently remap wrong geometry) without this.
                    int orig = part.VertexOffset + (int)(is32 ? idx32![start + k] : idx16![start + k]);
                    if (!remap.TryGetValue(orig, out int ni))
                    {
                        ni = nextVertex++;
                        remap[orig] = ni;

                        int vo = orig * stride;
                        positions.Add(FloatAt(raw, vo + posOff));
                        positions.Add(FloatAt(raw, vo + posOff + 4));
                        positions.Add(FloatAt(raw, vo + posOff + 8));

                        if (uvOff >= 0)
                        {
                            uvs.Add(FloatAt(raw, vo + uvOff));
                            uvs.Add(FloatAt(raw, vo + uvOff + 4));
                        }
                        else
                        {
                            uvs.Add(0f);
                            uvs.Add(0f);
                        }
                    }
                    indices.Add(vertexBase + ni);
                }
            }
        }

        return Finalize(positions.ToArray(), indices.ToArray(), uvs.ToArray());
    }

    /// <summary>
    /// Pure finalize stage (no graphics device, unit-testable): centre on the
    /// origin, rescale to unit extent, recompute smooth outward normals from the
    /// actual geometry (the P251 discipline), and fix the triangle winding so
    /// back-face culling is correct for the import.
    /// </summary>
    public static RockMesh Finalize(float[] positions, int[] indices, float[] uvs)
    {
        int vcount = positions.Length / 3;

        // 1. Centre the mesh on the origin (the renderer places it by
        //    CreateTranslation, so the centroid must sit at the asteroid's sim
        //    position).
        float cx = 0f, cy = 0f, cz = 0f;
        for (int i = 0; i < vcount; i++)
        {
            cx += positions[i * 3]; cy += positions[i * 3 + 1]; cz += positions[i * 3 + 2];
        }
        cx /= vcount; cy /= vcount; cz /= vcount;
        for (int i = 0; i < vcount; i++)
        {
            positions[i * 3] -= cx; positions[i * 3 + 1] -= cy; positions[i * 3 + 2] -= cz;
        }

        // 2. Normalize to unit extent (max radius = 1) so the renderer's
        //    Matrix.CreateScale(ast.Radius) reproduces the real world size —
        //    exactly the RockMeshFactory unit-icosphere convention.
        float maxR = 0f;
        for (int i = 0; i < vcount; i++)
        {
            float x = positions[i * 3], y = positions[i * 3 + 1], z = positions[i * 3 + 2];
            maxR = MathF.Max(maxR, MathF.Sqrt(x * x + y * y + z * z));
        }
        float s = maxR > 1e-9f ? 1f / maxR : 1f;
        if (s != 1f)
            for (int i = 0; i < vcount; i++)
            {
                positions[i * 3] *= s; positions[i * 3 + 1] *= s; positions[i * 3 + 2] *= s;
            }

        // 3. Smooth outward face-average normals (RockMeshFactory's P251
        //    algorithm — read the diagnosis there): average each triangle's
        //    geometric face normal (oriented outward from the centroid) into its
        //    three vertices, then renormalize. Imported normals are discarded so
        //    lighting genuinely tracks this bumpy imported silhouette.
        var normals = new float[positions.Length];
        int[] tris = indices;
        for (int t = 0; t < tris.Length; t += 3)
        {
            int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
            float ax = positions[ia * 3], ay = positions[ia * 3 + 1], az = positions[ia * 3 + 2];
            float bx = positions[ib * 3], by = positions[ib * 3 + 1], bz = positions[ib * 3 + 2];
            float cxv = positions[ic * 3], cyv = positions[ic * 3 + 1], czv = positions[ic * 3 + 2];

            float ux = bx - ax, uy = by - ay, uz = bz - az;
            float vx = cxv - ax, vy = cyv - ay, vz = czv - az;
            float nx = uy * vz - uz * vy;
            float ny = uz * vx - ux * vz;
            float nz = ux * vy - uy * vx;
            float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-12f) continue;

            float mx = (ax + bx + cxv) / 3f, my = (ay + by + cyv) / 3f, mz = (az + bz + czv) / 3f;
            if (nx * mx + ny * my + nz * mz < 0f) { nx = -nx; ny = -ny; nz = -nz; }

            normals[ia * 3] += nx; normals[ia * 3 + 1] += ny; normals[ia * 3 + 2] += nz;
            normals[ib * 3] += nx; normals[ib * 3 + 1] += ny; normals[ib * 3 + 2] += nz;
            normals[ic * 3] += nx; normals[ic * 3 + 1] += ny; normals[ic * 3 + 2] += nz;
        }
        for (int i = 0; i < vcount; i++)
        {
            float nx = normals[i * 3], ny = normals[i * 3 + 1], nz = normals[i * 3 + 2];
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9f)
            {
                float rx = positions[i * 3], ry = positions[i * 3 + 1], rz = positions[i * 3 + 2];
                len = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
                if (len < 1e-9f) len = 1f;
                normals[i * 3] = rx / len; normals[i * 3 + 1] = ry / len; normals[i * 3 + 2] = rz / len;
            }
            else
            {
                normals[i * 3] = nx / len; normals[i * 3 + 1] = ny / len; normals[i * 3 + 2] = nz / len;
            }
        }

        // 4. Fix winding: the Meshy/Blender FBX export is back-facing to
        //    MonoGame's default counter-clockwise-front convention (the quirk
        //    that forces ShipRenderer to CullNone). Compare each triangle's
        //    raw geometric normal against its recomputed outward normal; if the
        //    majority oppose, reverse the whole index list (a uniform flip
        //    preserves a watertight mesh). Normals are independent of winding,
        //    so only culling is corrected here.
        int mismatches = 0, counted = 0;
        for (int t = 0; t < tris.Length; t += 3)
        {
            int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
            float ax = positions[ia * 3], ay = positions[ia * 3 + 1], az = positions[ia * 3 + 2];
            float bx = positions[ib * 3], by = positions[ib * 3 + 1], bz = positions[ib * 3 + 2];
            float cxv = positions[ic * 3], cyv = positions[ic * 3 + 1], czv = positions[ic * 3 + 2];
            float gx = (by - ay) * (czv - az) - (bz - az) * (cyv - ay);
            float gy = (bz - az) * (cxv - ax) - (bx - ax) * (czv - az);
            float gz = (bx - ax) * (cyv - ay) - (by - ay) * (cxv - ax);
            float gl = MathF.Sqrt(gx * gx + gy * gy + gz * gz);
            if (gl < 1e-12f) continue;
            gx /= gl; gy /= gl; gz /= gl;

            float rx = normals[ia * 3] + normals[ib * 3] + normals[ic * 3];
            float ry = normals[ia * 3 + 1] + normals[ib * 3 + 1] + normals[ic * 3 + 1];
            float rz = normals[ia * 3 + 2] + normals[ib * 3 + 2] + normals[ic * 3 + 2];
            float rl = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
            if (rl < 1e-9f) continue;
            rx /= rl; ry /= rl; rz /= rl;

            if (gx * rx + gy * ry + gz * rz < 0f) mismatches++;
            counted++;
        }
        if (counted > 0 && mismatches > counted / 2)
        {
            for (int t = 0; t < tris.Length; t += 3)
                (tris[t + 1], tris[t + 2]) = (tris[t + 2], tris[t + 1]);
        }

        return new RockMesh(positions, normals, indices, uvs);
    }

    private static int ElementOffset(VertexDeclaration decl, VertexElementUsage usage, int usageIndex)
    {
        foreach (var el in decl.GetVertexElements())
            if (el.VertexElementUsage == usage && el.UsageIndex == usageIndex)
                return el.Offset;
        return -1;
    }

    private static float FloatAt(byte[] raw, int off) =>
        BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(off, 4));
}
