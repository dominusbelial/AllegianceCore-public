using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Allegiance.ClientCore.Session;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// SDL 2.32 Cocoa's handleURLEvent:withReplyEvent: sends SDL_DROPFILE with
/// windowID=0. MonoGame 3.8.5.1 only raises FileDrop for its own window ID, so
/// capture the URL before that dispatch. No Apple Event/argv re-splitting.
/// </summary>
internal sealed class MacJoinLinkEvents : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Filter(IntPtr user, IntPtr ev);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetFilter(out IntPtr filter, out IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetFilter(IntPtr filter, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte EventState(uint type, int state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Free(IntPtr memory);

    private readonly Filter _filter;
    private readonly SetFilter _set;
    private readonly GetFilter _get;
    private readonly Free _free;
    private readonly IntPtr _priorPointer, _priorUser;
    private readonly Filter? _prior;
    private readonly Action<string> _receive;
    private readonly IntPtr _pointer;
    private readonly EventState _eventState;
    private readonly byte _priorDropState;

    public static MacJoinLinkEvents? Install(Action<string> receive) =>
        OperatingSystem.IsMacOS() ? new(receive) : null;

    private MacJoinLinkEvents(Action<string> receive)
    {
        // Use MonoGame's actual loaded SDL, including its packaged native search path.
        var sdl = typeof(Game).Assembly.GetType("Sdl", throwOnError: true)!;
        var library = (IntPtr)sdl.GetField("NativeLibrary", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        _set = Export<SetFilter>("SDL_SetEventFilter");
        _get = Export<GetFilter>("SDL_GetEventFilter");
        _free = Export<Free>("SDL_free");
        _eventState = Export<EventState>("SDL_EventState");
        _receive = receive;
        _get(out _priorPointer, out _priorUser);
        _prior = _priorPointer == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<Filter>(_priorPointer);
        _filter = OnEvent;
        _pointer = Marshal.GetFunctionPointerForDelegate(_filter);
        _priorDropState = _eventState(0x1000, 1); // SDL_DROPFILE / SDL_ENABLE
        _set(_pointer, IntPtr.Zero);
    }

    internal static string? ReadUrl(IntPtr ev)
    {
        if ((uint)Marshal.ReadInt32(ev) != 0x1000) return null;
        IntPtr data = Marshal.ReadIntPtr(ev, 8); // SDL_DropEvent.file (64-bit desktop ABI)
        if (data == IntPtr.Zero) return null;
        var bytes = new List<byte>();
        for (int i = 0; i <= JoinLink.MaxLength; i++)
        {
            byte b = Marshal.ReadByte(data, i);
            if (b == 0) break;
            bytes.Add(b);
        }
        string value = Encoding.UTF8.GetString(bytes.ToArray());
        return JoinLink.LooksLikeLink(value) ? value : null;
    }

    private int OnEvent(IntPtr user, IntPtr ev)
    {
        try
        {
            if (ReadUrl(ev) is { } url)
            {
                _receive(url);
                _free(Marshal.ReadIntPtr(ev, 8)); // consuming filter owns the SDL-allocated string
                return 0;
            }
            return _prior?.Invoke(_priorUser, ev) ?? 1;
        }
        catch { return 1; } // never unwind a managed exception through SDL/Cocoa
    }

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _get(out var active, out _);
        if (active == _pointer) _set(_priorPointer, _priorUser);
        _eventState(0x1000, _priorDropState);
        GC.KeepAlive(_filter);
    }
}
