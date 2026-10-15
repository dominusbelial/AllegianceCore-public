using System.Runtime.InteropServices;

namespace Allegiance.ClientCore.MonoGame.Diagnostics;

/// <summary>DesktopGL timer queries via SDL's current context. Never waits for GPU results.
/// Android uses the explicit unavailable path (SDL is not its context owner).</summary>
public sealed class GlFrameTimer : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Proc(string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Gen(int count, out uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Begin(uint target, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void End(uint target);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Get(uint id, uint pname, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Result(uint id, uint pname, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Delete(int count, ref uint id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr GetString(uint name);
    private const uint Elapsed = 0x88BF, Available = 0x8867, QueryResult = 0x8866;
    private readonly Begin _begin;
    private readonly End _end;
    private readonly Get _get;
    private readonly Result _result;
    private readonly Delete _delete;
    private readonly uint[] _queries = new uint[32];
    private readonly long[] _frames = new long[32];
    private int _next, _active = -1;
    private GlFrameTimer(Proc proc)
    {
        T Load<T>(string name) where T : Delegate
        {
            var ptr = proc(name);
            if (ptr == IntPtr.Zero) throw new NotSupportedException(name);
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }
        var version = Marshal.PtrToStringAnsi(Load<GetString>("glGetString")(0x1F02));
        // Core desktop timer queries require GL 3.3. Do not invoke unsupported entrypoints.
        if (version is null || version.StartsWith("OpenGL ES", StringComparison.Ordinal)
            || !Version.TryParse(version.Split(' ')[0], out var v) || v < new Version(3, 3))
            throw new NotSupportedException("GL 3.3 timer queries unavailable");
        _begin = Load<Begin>("glBeginQuery"); _end = Load<End>("glEndQuery");
        _get = Load<Get>("glGetQueryObjectiv"); _result = Load<Result>("glGetQueryObjectui64v");
        _delete = Load<Delete>("glDeleteQueries");
        var gen = Load<Gen>("glGenQueries");
        for (int i = 0; i < _queries.Length; i++) gen(1, out _queries[i]);
    }
    public static GlFrameTimer? TryCreate()
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
    public void Start(long frame, bool post)
    {
        if (_active >= 0 || _frames[_next] != 0) return;
        _active = _next;
        _frames[_active] = frame * 2 + (post ? 1 : 0);
        _begin(Elapsed, _queries[_active]);
        _next = (_next + 1) % _queries.Length;
    }
    public void Stop()
    {
        if (_active < 0) return;
        _end(Elapsed); _active = -1;
    }
    public void Poll(Action<long, bool, double> receive)
    {
        for (int i = 0; i < _queries.Length; i++)
        {
            if (_frames[i] == 0 || i == _active) continue;
            _get(_queries[i], Available, out int ready);
            if (ready == 0) continue;
            _result(_queries[i], QueryResult, out ulong ns);
            receive(_frames[i] / 2, (_frames[i] & 1) != 0, ns / 1e6);
            _frames[i] = 0;
        }
    }
    public void Dispose()
    {
        Stop();
        for (int i = 0; i < _queries.Length; i++) _delete(1, ref _queries[i]);
    }
}
