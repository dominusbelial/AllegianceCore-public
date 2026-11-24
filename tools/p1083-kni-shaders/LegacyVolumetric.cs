using System.Security.Cryptography;
using System.Text;

namespace Allegiance.Tools.KniShaders;

/// <summary>Spike-only lossless container downgrade for the existing source-matched
/// Volumetric artifact. KNI 4.3.9001 Effect.cs supports MGFX10, and its BlazorGL
/// ConcreteShader translates legacy GLSL to ES300. MonoGame 3.8.5.1 added only
/// source/entrypoint strings in MGFX11 (Shader.cs:110-115). No shader code is edited.</summary>
public static class LegacyVolumetric
{
    public static byte[] Convert(byte[] source, string sourceHash, string recordedHash, byte[] desktop)
    {
        if (System.Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant() != sourceHash || sourceHash != recordedHash.Trim())
            throw new InvalidDataException("Desktop Volumetric source hash does not match its artifact.");
        using var input = new MemoryStream(desktop, writable: false);
        using var reader = new BinaryReader(input, Encoding.UTF8);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        const int magic = 0x5846474d;
        if (reader.ReadInt32() != magic || reader.ReadByte() != 11 || reader.ReadByte() != 0)
            throw new InvalidDataException("Expected MonoGame MGFX11 OpenGL artifact.");
        _ = reader.ReadInt32(); // Input's cache key covers a different container.
        writer.Write(magic); writer.Write((byte)10); writer.Write((byte)0); writer.Write(0);
        int buffers = Count(reader.ReadInt32()); writer.Write(buffers);
        for (int i = 0; i < buffers; i++)
        {
            writer.Write(reader.ReadString()); writer.Write(reader.ReadInt16());
            int parameters = Count(reader.ReadInt32()); writer.Write(parameters);
            Copy(checked(parameters * 6));
        }
        int shaders = Count(reader.ReadInt32()); writer.Write(shaders);
        for (int i = 0; i < shaders; i++)
        {
            writer.Write(reader.ReadByte());
            _ = reader.ReadString(); // MGFX11 debug source filename
            _ = reader.ReadString(); // MGFX11 debug entrypoint
            int bytes = Count(reader.ReadInt32(), 4 * 1024 * 1024); writer.Write(bytes); Copy(bytes);
            byte samplers = reader.ReadByte(); writer.Write(samplers);
            for (int s = 0; s < samplers; s++)
            {
                Copy(3); bool state = reader.ReadBoolean(); writer.Write(state);
                if (state) Copy(20); // U/V/W + RGBA + filter + anisotropy/mip/bias
                writer.Write(reader.ReadString()); Copy(1);
            }
            byte constants = reader.ReadByte(); writer.Write(constants); Copy(constants);
            byte attributes = reader.ReadByte(); writer.Write(attributes);
            for (int a = 0; a < attributes; a++) { writer.Write(reader.ReadString()); Copy(4); }
        }
        // MGFX10 and 11 parameters, techniques, passes, state and tail are identical.
        if (input.Position > input.Length - 4 || BitConverter.ToInt32(desktop, desktop.Length - 4) != magic)
            throw new InvalidDataException("Invalid MGFX tail.");
        Copy(checked((int)(input.Length - input.Position)));
        writer.Flush();
        byte[] result = output.ToArray();
        // A deterministic content-derived key isolates this asset from all other cached effects.
        SHA256.HashData(result.AsSpan(10)).AsSpan(0, 4).CopyTo(result.AsSpan(6, 4));
        return result;

        void Copy(int count)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException("Truncated MGFX artifact.");
            writer.Write(bytes);
        }
    }

    private static int Count(int value, int maximum = 4096) => value is >= 0 && value <= maximum
        ? value : throw new InvalidDataException("Invalid MGFX count.");
}
