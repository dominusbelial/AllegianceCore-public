using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>DesktopGL bridge for BC formats absent from MonoGame 3.8.5.1's enum.
/// MonoGame owns the texture lifetime/binding/samplers; GL receives BC blocks.
/// No SetData/GetData/FromStream call may be made on the returned wrapper.</summary>
public sealed class HdCompressedUpload
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Proc(string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Integer(uint name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Bind(uint target, int texture);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Upload(uint target, int level, uint format, int w, int h, int border, int size, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr StringAt(uint name, uint index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Error();
    private readonly Integer _integer;
    private readonly Bind _bind;
    private readonly Upload _upload;
    private readonly Error _error;
    private readonly HashSet<string> _extensions = [];
    // Pinned to the DesktopGL package; fail closed if a future framework changes
    // its ownership contract. Reflection is confined to the JIT graphics client.
    private static readonly FieldInfo? TextureId = typeof(Texture2D).BaseType?.GetField("glTexture", BindingFlags.Instance | BindingFlags.NonPublic);
    public bool Bptc { get; }
    private HdCompressedUpload(Proc proc)
    {
        T Load<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(proc(name));
        _integer = Load<Integer>("glGetIntegerv"); _bind = Load<Bind>("glBindTexture");
        _upload = Load<Upload>("glCompressedTexImage2D"); _error = Load<Error>("glGetError");
        var at = Load<StringAt>("glGetStringi");
        _integer(0x821D, out int count);
        for (uint i = 0; i < count; i++) _extensions.Add(Marshal.PtrToStringAnsi(at(0x1F03, i)) ?? "");
        _integer(0x821B, out int major); _integer(0x821C, out int minor);
        if (major < 3 || TextureId is null) throw new NotSupportedException("DesktopGL BC bridge unavailable");
        if (!_extensions.Contains("GL_EXT_texture_compression_s3tc")) throw new NotSupportedException("S3TC unavailable");
        Bptc = major > 4 || major == 4 && minor >= 2 || _extensions.Contains("GL_ARB_texture_compression_bptc");
    }
    public static HdCompressedUpload? TryCreate()
    {
        if (OperatingSystem.IsAndroid()) return null;
        foreach (string lib in new[] { "libSDL2-2.0.so.0", "SDL2.dll", "libSDL2.dylib" })
        {
            if (!NativeLibrary.TryLoad(lib, out var handle)) continue;
            try
            {
                if (NativeLibrary.TryGetExport(handle, "SDL_GL_GetProcAddress", out var address))
                    return new(Marshal.GetDelegateForFunctionPointer<Proc>(address));
            }
            catch (Exception e) when (e is NotSupportedException or ArgumentException) { }
            finally { NativeLibrary.Free(handle); }
        }
        return null;
    }
    public Texture2D Load(GraphicsDevice gd, string path, HdDds dds, int first, bool colorSrgb = true)
    {
        _integer(0x8069, out int previous); // GL_TEXTURE_BINDING_2D on current unit
        Texture2D? texture = null;
        try
        {
            // Allocate a compressed placeholder, then redefine each level in its
            // real format. Even staging GPU storage remains compressed.
            texture = new Texture2D(gd, Math.Max(1, dds.Width >> first), Math.Max(1, dds.Height >> first), true, SurfaceFormat.Dxt5);
            int id = (int)(TextureId?.GetValue(texture) ?? throw new NotSupportedException("MonoGame texture handle"));
            _bind(0x0DE1, id);
            if (_error() != 0) throw new InvalidOperationException("GL error before BC upload");
            using var stream = File.OpenRead(path);
            stream.Position = 148 + Enumerable.Range(0, first).Sum(i => (long)dds.LevelBytes(i));
            for (int i = first; i < dds.Levels; i++)
            {
                byte[] bytes = new byte[dds.LevelBytes(i)]; stream.ReadExactly(bytes);
                var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try
                {
                    uint format = dds.Bc switch { 7 => colorSrgb ? 0x8E8Du : 0x8E8Cu, 5 => 0x8DBD, 4 => 0x8DBB, 3 => colorSrgb ? 0x8C4Fu : 0x83F3u, _ => throw new InvalidDataException("BC format") };
                    _upload(0x0DE1, i - first, format, Math.Max(1, dds.Width >> i), Math.Max(1, dds.Height >> i), 0, bytes.Length, pinned.AddrOfPinnedObject());
                    if (_error() != 0) throw new InvalidOperationException("GL rejected compressed upload");
                }
                finally { pinned.Free(); }
            }
            return texture;
        }
        catch { texture?.Dispose(); throw; }
        finally { _bind(0x0DE1, previous); }
    }
}
