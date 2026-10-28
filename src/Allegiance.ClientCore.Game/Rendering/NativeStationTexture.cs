using System.Text;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Native station image namespaces may be compiled BinarySurfaceInfo
/// or text ImportImageFromFile (engine/modeler.cpp:623-674). Resolve the actual
/// import argument instead of assuming the PNG shares the namespace's name.</summary>
public static partial class NativeStationTexture
{
    [GeneratedRegex("ImportImageFromFile\\s*\\(\\s*\"([^\"]+)\"\\s*,\\s*(?:true|false)\\s*\\)")]
    private static partial Regex ImageImport();

    public static string? ImportedImagePath(byte[] mdl)
    {
        if (mdl.Length >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(mdl)
            == MdlTextureDecoder.MdlMagic) return null;
        var match = ImageImport().Match(Encoding.UTF8.GetString(mdl));
        if (!match.Success) throw new InvalidDataException("unsupported station image namespace");
        return match.Groups[1].Value;
    }

    public static Texture2D Load(GraphicsDevice gd, IAssetSource assets, string stem)
    {
        byte[] data = AssetSources.ReadAllBytes(assets, stem + ".mdl");
        MdlTexture decoded;
        if (ImportedImagePath(data) is { } imagePath)
        {
            using var stream = assets.OpenRead(imagePath);
            using var source = Texture2D.FromStream(gd, stream);
            var pixels = new Color[source.Width * source.Height];
            source.GetData(pixels);
            var rgba = new byte[pixels.Length * 4];
            for (int y = 0; y < source.Height; y++)
                for (int x = 0; x < source.Width; x++)
                {
                    var pixel = pixels[y * source.Width + x];
                    int i = ((source.Height - 1 - y) * source.Width + x) * 4;
                    rgba[i] = pixel.R; rgba[i + 1] = pixel.G; rgba[i + 2] = pixel.B;
                    rgba[i + 3] = pixel.A;
                }
            decoded = new MdlTexture(source.Width, source.Height, rgba);
        }
        else decoded = MdlTextureDecoder.Decode(data);

        var texture = new Texture2D(gd, decoded.Width, decoded.Height);
        // P862 — upload the decoded RGBA bytes straight (SurfaceFormat.Color is
        // RGBA8); the Color[] staging doubled the peak allocation of every station
        // texture, and a cold sector loads a whole station's set in one frame.
        texture.SetData(decoded.Rgba);
        return texture;
    }
}
