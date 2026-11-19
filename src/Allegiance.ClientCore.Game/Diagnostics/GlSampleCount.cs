using System.Runtime.InteropServices;

namespace Allegiance.ClientCore.MonoGame.Diagnostics;

/// <summary>Read the current DRAW framebuffer's physical sample count. The GL
/// driver can allocate more samples than MonoGame's requested/reported count.</summary>
public static class GlSampleCount
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Proc(string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Get(uint name, out int value);
    public static int? Current()
    {
        if (OperatingSystem.IsAndroid()) return null;
        foreach (string library in new[] { "libSDL2-2.0.so.0", "SDL2.dll", "libSDL2.dylib" })
        {
            if (!NativeLibrary.TryLoad(library, out var handle)) continue;
            try
            {
                if (!NativeLibrary.TryGetExport(handle, "SDL_GL_GetProcAddress", out var proc)) continue;
                var address = Marshal.GetDelegateForFunctionPointer<Proc>(proc)("glGetIntegerv");
                if (address == IntPtr.Zero) continue;
                Marshal.GetDelegateForFunctionPointer<Get>(address)(0x80A9, out int samples); // GL_SAMPLES
                return samples;
            }
            finally { NativeLibrary.Free(handle); }
        }
        return null;
    }
}
