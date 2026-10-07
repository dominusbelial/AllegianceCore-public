using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Allegiance.Core;

/// <summary>
/// Minimal reader for the DirectX <c>.x</c> text mesh format — used to load the
/// native shield-flare cap geometries (<c>lens30.x</c>/<c>lens60.x</c>/<c>lens90.x</c>,
/// the DirectX exports of the <c>lens</c> namespace caps native loads via
/// <c>GetNameSpace(AWF_SHIELD_FLARE)-&gt;FindMember("lens30")</c>,
/// <c>src/WinTrek/trekigc.cpp:2517-2523</c>).
/// </summary>
/// <remarks>
/// <para><b>Why the <c>.x</c> files, not <c>lens.mdl</c>.</b> The port's
/// <see cref="MdlFile"/> pipeline (P566/P567) collects meshes only from the
/// <c>"object"</c> export (per <c>mdlc.cpp</c>'s <c>FindMemberLocal("object")</c>
/// convention). <c>lens.mdl</c> exposes its three caps as <b>named exports</b>
/// <c>lens30</c>/<c>lens60</c>/<c>lens90</c> (there is no <c>"object"</c> export),
/// so <see cref="MdlFile"/> decodes zero meshes from it. The task's scope
/// explicitly sanctions the <c>.x</c> fallback in exactly this case ("if
/// <c>lens.mdl</c> doesn't expose the members, use the <c>.x</c> meshes, with a
/// loader cited"). The <c>.x</c> files ARE the same caps exported to DirectX:
/// all three are unit-radius spherical caps — apex at <c>(0,0,1)</c>, concentric
/// latitude rings at <c>z = cos(θ)</c> for θ = 30°/60°/90°, per-vertex normals
/// equal to the vertex positions — matching the unit-radius cap
/// <c>m_radiusMesh = 1</c> the native <c>Matrix(orient, 0, m_radiusMesh)</c>
/// expects (thinggeo.cpp:1289).</para>
/// <para><b>Format parsed</b> (standard DirectX <c>.x</c> text): a
/// <c>Mesh &lt;name&gt; { nVertices; x;y;z;, … nFaces; 3;i,j,k;, … }</c> block,
/// plus optional <c>MeshTextureCoords { n; u;v;, … }</c> and
/// <c>MeshNormals { n; nx;ny;nz;, … }</c> siblings. The face stream in these
/// files repeats every triangle with both windings (double-sided authoring);
/// indices are preserved as-is.</para>
/// </remarks>
public static class DirectXMeshFile
{
    /// <summary>Parse a DirectX <c>.x</c> text mesh file into the shared
    /// <see cref="MdlMesh"/> shape (positions/texcoords/normals/indices) so a
    /// MonoGame renderer can build its buffers without a bespoke vertex type.</summary>
    public static MdlMesh Parse(string text)
    {
        var meshBody = ExtractBlock(text, @"Mesh\s+[A-Za-z_][\w]*\s*\{");
        if (meshBody is null)
            throw new InvalidDataException("no Mesh block in .x text");

        var num = ParseNumbers(meshBody);

        int nVertices = (int)num[0];
        var positions = new float[3 * nVertices];
        int idx = 1;
        for (int v = 0; v < nVertices; v++)
        {
            positions[3 * v] = num[idx++];
            positions[3 * v + 1] = num[idx++];
            positions[3 * v + 2] = num[idx++];
        }

        int nFaces = (int)num[idx++];
        var indices = new ushort[3 * nFaces];
        for (int f = 0; f < nFaces; f++)
        {
            int cnt = (int)num[idx++];          // "3" for triangles
            for (int k = 0; k < cnt; k++)
                indices[3 * f + k] = (ushort)num[idx++];
        }

        // Optional per-vertex texture coordinates.
        var texCoords = new float[2 * nVertices];
        var tcBody = ExtractBlock(text, @"(?<!template )MeshTextureCoords\s*\{");
        if (tcBody is not null)
        {
            var tn = ParseNumbers(tcBody);
            int tnCount = (int)tn[0];
            for (int v = 0; v < Math.Min(tnCount, nVertices); v++)
            {
                texCoords[2 * v] = tn[1 + 2 * v];
                texCoords[2 * v + 1] = tn[1 + 2 * v + 1];
            }
        }

        // Optional per-vertex normals; fall back to geometry-derived normals.
        float[] normals = new float[3 * nVertices];
        var nrmBody = ExtractBlock(text, @"(?<!template )MeshNormals\s*\{");
        if (nrmBody is not null)
        {
            var nn = ParseNumbers(nrmBody);
            int nnCount = (int)nn[0];
            if (nnCount >= nVertices)
            {
                for (int v = 0; v < nVertices; v++)
                {
                    normals[3 * v] = nn[1 + 3 * v];
                    normals[3 * v + 1] = nn[1 + 3 * v + 1];
                    normals[3 * v + 2] = nn[1 + 3 * v + 2];
                }
            }
            else
            {
                RecomputeNormals(positions, indices, normals);
            }
        }
        else
        {
            RecomputeNormals(positions, indices, normals);
        }

        return new MdlMesh
        {
            Positions = positions,
            TexCoords = texCoords,
            Normals = normals,
            Indices = indices,
        };
    }

    private static void RecomputeNormals(float[] positions, ushort[] indices, float[] normals)
    {
        int n = positions.Length / 3;
        for (int v = 0; v < n; v++)
        {
            normals[3 * v] = positions[3 * v];
            normals[3 * v + 1] = positions[3 * v + 1];
            normals[3 * v + 2] = positions[3 * v + 2];
            double len = Math.Sqrt(
                (double)normals[3 * v] * normals[3 * v]
                + (double)normals[3 * v + 1] * normals[3 * v + 1]
                + (double)normals[3 * v + 2] * normals[3 * v + 2]);
            if (len > 0)
            {
                normals[3 * v] = (float)(normals[3 * v] / len);
                normals[3 * v + 1] = (float)(normals[3 * v + 1] / len);
                normals[3 * v + 2] = (float)(normals[3 * v + 2] / len);
            }
        }
    }

    /// <summary>Return the balanced brace body of the FIRST block whose opening
    /// matches <paramref name="headerPattern"/> (content between the braces).</summary>
    private static string? ExtractBlock(string text, string headerPattern)
    {
        var m = Regex.Match(text, headerPattern);
        if (!m.Success) return null;
        int open = text.IndexOf('{', m.Index);
        if (open < 0) return null;
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return text.Substring(open + 1, i - open - 1);
            }
        }
        return null;
    }

    /// <summary>Extract every decimal number from a block, in order, as floats
    /// (positions/faces/uvs/normals are all semicolon/comma separated decimals).</summary>
    private static float[] ParseNumbers(string s)
    {
        var nums = new List<float>();
        foreach (Match mt in Regex.Matches(s, @"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?"))
            nums.Add(float.Parse(mt.Value, CultureInfo.InvariantCulture));
        return nums.ToArray();
    }
}
