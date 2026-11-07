using System.Runtime.InteropServices;

namespace Allegiance.ClientCore.MonoGame.Flight;

internal enum MouseCaptureOwner { None, Flight, FreeLook }

/// <summary>Shared by both HUD styles. Only motion consumers may own the pointer.</summary>
internal static class MouseCapturePolicy
{
    internal static MouseCaptureOwner Choose(bool focused, bool flying, bool docked, bool blocked,
        bool mouseFlight, bool expandedMap, bool commandCamera, bool spectatorCamera, bool freeLook,
        bool menuConsumedFrame = false)
    {
        // Shipless observers return before the flight frame clears this latch.
        // Visible menus still block both owners; a closed menu must not leave
        // free-look waiting forever on a flight-only input-consumption flag.
        if (!focused || !flying || docked || blocked || expandedMap || commandCamera
            || menuConsumedFrame && !freeLook)
            return MouseCaptureOwner.None;
        if (freeLook) return MouseCaptureOwner.FreeLook;
        return mouseFlight && !spectatorCamera ? MouseCaptureOwner.Flight : MouseCaptureOwner.None;
    }
}

internal interface IRelativeMouseBackend : IDisposable
{
    bool SetEnabled(bool enabled);
    (int X, int Y) ReadDelta();
}

/// <summary>
/// Owns capture and discards motion on every handoff. SDL deltas and the old
/// centre offsets have identical pixel units/signs; the flight controller is unchanged.
/// Native intent: effect/efimage.cpp:182-209; engine/camera.cpp:408-425.
/// </summary>
internal sealed class RelativeMouse(Func<IRelativeMouseBackend?> createBackend, Action<string> log) : IDisposable
{
    private IRelativeMouseBackend? _backend;
    private bool _loaded, _discardNext, _loggedRelative, _loggedWarp, _disposed;
    internal MouseCaptureOwner Owner { get; private set; }
    internal bool IsRelative { get; private set; }

    internal bool SetOwner(MouseCaptureOwner owner)
    {
        if (_disposed || Owner == owner) return false;
        if (IsRelative) _backend!.SetEnabled(false);
        IsRelative = false;
        Owner = owner;
        _discardNext = true;
        if (owner == MouseCaptureOwner.None) return true;
        if (!_loaded)
        {
            _backend = createBackend();
            _loaded = true;
        }
        IsRelative = _backend?.SetEnabled(true) == true;
        if (IsRelative)
        {
            _backend!.ReadDelta(); // flush motion queued before capture
            if (!_loggedRelative) { log("[mouse] relative"); _loggedRelative = true; }
        }
        else if (!_loggedWarp) { log("[mouse] warp"); _loggedWarp = true; }
        return true;
    }

    internal (int X, int Y) ReadDelta(int pointerX, int pointerY, int centerX, int centerY,
        Action<int, int> warp)
    {
        if (Owner == MouseCaptureOwner.None) return (0, 0);
        var delta = IsRelative ? _backend!.ReadDelta() : (pointerX - centerX, pointerY - centerY);
        if (!IsRelative) warp(centerX, centerY);
        if (_discardNext) { _discardNext = false; return (0, 0); }
        return delta;
    }

    public void Dispose()
    {
        if (_disposed) return;
        SetOwner(MouseCaptureOwner.None);
        _backend?.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// DesktopGL's SDL wrapper is internal and Mouse.GetState uses global position.
/// Resolve the same SDL2 library search paths as MonoGame's FuncLoader, never a
/// second uninitialized SDL instance. Android has no SDL window and uses fallback.
/// Keep the library reference alive until capture is released.
/// </summary>
internal sealed class SdlRelativeMouse : IRelativeMouseBackend
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetMode(int enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint GetDelta(out int x, out int y);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint WasInit(uint flags);
    private readonly nint _library;
    private readonly SetMode _setMode;
    private readonly GetDelta _getDelta;

    private SdlRelativeMouse(nint library)
    {
        _library = library;
        _setMode = Marshal.GetDelegateForFunctionPointer<SetMode>(NativeLibrary.GetExport(library, "SDL_SetRelativeMouseMode"));
        _getDelta = Marshal.GetDelegateForFunctionPointer<GetDelta>(NativeLibrary.GetExport(library, "SDL_GetRelativeMouseState"));
    }

    internal static IRelativeMouseBackend? TryCreate()
    {
        if (OperatingSystem.IsAndroid()) return null;
        string name = OperatingSystem.IsWindows() ? "SDL2.dll"
            : OperatingSystem.IsMacOS() ? "libSDL2-2.0.0.dylib" : "libSDL2-2.0.so.0";
        // DesktopGL's universal macOS SDL package uses "osx", not "osx-arm64".
        string rid = OperatingSystem.IsMacOS() ? "osx" : RuntimeInformation.RuntimeIdentifier;
        string root = AppContext.BaseDirectory;
        string[] candidates = OperatingSystem.IsMacOS()
            ? [Path.Combine(root, name), Path.Combine(root, "..", "Frameworks", name)]
            : [Path.Combine(root, Environment.Is64BitProcess ? "x64" : "x86", name)];
        foreach (string path in candidates.Concat([
            Path.Combine(root, "runtimes", rid, "native", name),
            Path.Combine(root, name), name]))
        {
            if (!NativeLibrary.TryLoad(path, out var library)) continue;
            try
            {
                var initialized = Marshal.GetDelegateForFunctionPointer<WasInit>(NativeLibrary.GetExport(library, "SDL_WasInit"));
                if ((initialized(0x20) & 0x20) != 0) return new SdlRelativeMouse(library);
            }
            catch (EntryPointNotFoundException) { }
            NativeLibrary.Free(library);
        }
        return null;
    }

    public bool SetEnabled(bool enabled) => _setMode(enabled ? 1 : 0) == 0;
    public (int X, int Y) ReadDelta() { _getDelta(out int x, out int y); return (x, y); }
    public void Dispose() => NativeLibrary.Free(_library);
}
