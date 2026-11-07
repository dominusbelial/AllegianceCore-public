using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;

// Workstation-only X11/Xwayland proof: an OS pointer source delivers motion,
// then SDL and the production controller/simulator consume it. No writes to
// controls, virtual-stick deflection, ship orientation or proof-input fields.
if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This proof injects local X11 pointer motion.");
if (args.Length is < 2 or > 3) throw new ArgumentException("usage: p950-mouse-proof <content-directory> <output-directory> [manual|compositor]");
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
Environment.SetEnvironmentVariable("ALLEGIANCE_TRAINING_START", "10"); // ordinary Free Flight host, no scripted pilot
using var game = new Game1(new Game1.LaunchOptions
{
    Intro = true, PlayerName = "Mouse pilot", WindowWidth = 960, WindowHeight = 600,
    EffectsRequested = Allegiance.ClientCore.State.EffectsQuality.Native,
});
game.Content.RootDirectory = Path.GetFullPath(args[0]);
string input = args.Length == 3 ? args[2] : "manual";
if (input is not ("manual" or "compositor")) throw new ArgumentException("Unknown input source");
var proof = new MouseProof(game, output, input);
game.Components.Add(proof);
try
{
    game.Run();
    if (!proof.Completed) throw new InvalidOperationException("Mouse proof exited before completion.");
    if (Native.SDL_GetRelativeMouseMode() != 0) throw new InvalidOperationException("Exit did not release relative capture");
    Console.WriteLine("[p950] exit released pointer");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1; // dispose the window/capture even when a proof assertion fails
}
return 0;

sealed class MouseProof(Game1 game, string output, string input) : GameComponent(game)
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _step, _motions;
    private bool _focusLossSeen;
    private double _next;
    private System.Numerics.Vector3 _forward;
    private readonly List<object> _receipts = [];
    private readonly Native.EventWatch _watch = (_, ev) =>
    {
        if (Marshal.ReadInt32(ev) == 1024)
            Console.WriteLine($"[p950-motion] xrel={Marshal.ReadInt32(ev, 28)} yrel={Marshal.ReadInt32(ev, 32)}");
        return 1;
    };
    internal bool Completed;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Fields)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Fields)!.SetValue(game, value);
    private object? Call(string name, params object[] args) => typeof(Game1).GetMethod(name, Fields)!.Invoke(game, args);
    private string Owner => Get<object>("_relativeMouse").GetType().GetProperty("Owner", Fields)!.GetValue(Get<object>("_relativeMouse"))!.ToString()!;
    private bool Relative => (bool)Get<object>("_relativeMouse").GetType().GetProperty("IsRelative", Fields)!.GetValue(Get<object>("_relativeMouse"))!;
    private void Check(bool value, string message)
    {
        if (value) return;
        string[] flags = ["_mouseFlightEnabled", "_dockedActive", "_chatActive", "_qcMenuOpen", "_mainMenuVisible",
            "_menuConsumedFrame", "_helpVisible", "_keyBindVisible", "_graphicsSettingsVisible", "_minimapExpanded", "_proofEnabled"];
        throw new InvalidOperationException(message + $"; focused={game.IsActive} owner={Owner}; "
            + string.Join(" ", flags.Select(f => f + "=" + Get<object>(f))));
    }
    private System.Numerics.Vector3 Forward()
    {
        var f = Get<OwnShipSimulator>("_ownSim").Orientation.GetForward();
        return new(f.X, f.Y, f.Z);
    }
    private void Shot(string label)
    {
        Set("_autoshotDir", output); Set("_autoShotName", label); Set("_autoShotScheduled", true);
    }
    private void TurnReceipt(string style)
    {
        Check(Get<HudSettings>("_hudSettings").Style.ToString() == style, "HUD style mismatch");
        var controls = Get<ControlData>("_lastSentControls");
        float degrees = MathF.Acos(Math.Clamp(System.Numerics.Vector3.Dot(_forward, Forward()), -1, 1)) * 180 / MathF.PI;
        Check(Relative && Owner == "Flight", "Relative flight capture was lost");
        Check(MathF.Abs(controls.Yaw) + MathF.Abs(controls.Pitch) > .01f, "OS motion did not reach flight controls");
        Check(degrees > 1, "Ship did not turn at least one degree");
        _receipts.Add(new { style, input, controls.Yaw, controls.Pitch, turnDegrees = degrees });
        Console.WriteLine($"[p950] {style} relative={Relative} yaw={controls.Yaw:F4} pitch={controls.Pitch:F4} turnDegrees={degrees:F2}");
        Shot("mouse-flight-" + style.ToLowerInvariant());
    }
    public override void Update(GameTime gameTime)
    {
        if (Completed) return;
        if (_clock.Elapsed.TotalSeconds > 180) throw new TimeoutException("P950 mouse window did not finish");
        if (_clock.Elapsed.TotalSeconds < _next) return;
        if (_step > 0 && !game.IsActive)
        {
            Check(!Relative && Owner == "None", "Focus loss did not release capture");
            if (!_focusLossSeen) Console.WriteLine("[p950] focus loss released pointer");
            _focusLossSeen = true;
            Native.SDL_RaiseWindow(game.Window.Handle);
            Native.SDL_SetWindowInputFocus(game.Window.Handle);
            _next = _clock.Elapsed.TotalSeconds + .1;
            return;
        }
        switch (_step)
        {
            case 0:
                if (Get<object>("_state").ToString() != "Flying" || Get<bool>("_dockedActive")) return;
                if ((bool)Call("IsBriefingVisible", new object[] { null! })!)
                    Check(!Relative && Owner == "None", "Briefing did not release capture");
                Call("DismissBriefing");
                Native.SDL_RaiseWindow(game.Window.Handle);
                Native.SDL_SetWindowInputFocus(game.Window.Handle);
                if (!game.IsActive) return;
                Check(Marshal.PtrToStringUTF8(Native.SDL_GetCurrentVideoDriver()) == "x11", "Use SDL_VIDEODRIVER=x11 for this proof");
                Native.SDL_AddEventWatch(_watch, 0);
                foreach (string module in File.ReadAllLines("/proc/self/maps").Where(l => l.Contains("libSDL2", StringComparison.Ordinal)).Select(l => l[(l.LastIndexOf(' ') + 1)..]).Distinct())
                    Console.WriteLine("[p950] SDL module=" + module);
                Get<HudSettings>("_hudSettings").Style = HudStyle.Classic;
                Set("_mouseFlightEnabled", false); Call("SyncMouseCapture", false);
                Console.WriteLine("[p950] pointer setup; SDL video=x11; Wayland desktop via Xwayland; no RDP; scripted pilot disabled");
                _forward = Forward();
                _next = _clock.Elapsed.TotalSeconds + (input == "compositor" ? 3 : .5); _step++;
                break;
            case 1:
                if (!Get<bool>("_mouseFlightEnabled"))
                {
                    Set("_mouseFlightEnabled", true); Call("SyncMouseCapture", false);
                    _next = _clock.Elapsed.TotalSeconds + .3;
                    return;
                }
                Check(Relative, "SDL did not enable relative mode");
                if (_motions == 0) Console.WriteLine($"[p950] SDL relative flag={Native.SDL_GetRelativeMouseMode()} mouseFocus={Native.SDL_GetMouseFocus() == game.Window.Handle} keyboardFocus={Native.SDL_GetKeyboardFocus() == game.Window.Handle}");
                if (++_motions < 20) return;
                _next = _clock.Elapsed.TotalSeconds + 1; _step++;
                break;
            case 2:
                if (input is "manual" or "compositor" && Get<ControlData>("_lastSentControls") is { Yaw: 0, Pitch: 0 })
                {
                    _next = _clock.Elapsed.TotalSeconds + .2;
                    return;
                }
                TurnReceipt("Classic");
                _next = _clock.Elapsed.TotalSeconds + .4; _step++;
                break;
            case 3:
                Check(!Get<bool>("_autoShotScheduled"), "Classic screenshot was not flushed");
                Get<HudSettings>("_hudSettings").Style = HudStyle.Modern;
                _forward = Forward();
                _next = _clock.Elapsed.TotalSeconds + 1; _step++;
                break;
            case 4:
                TurnReceipt("Modern");
                _next = _clock.Elapsed.TotalSeconds + .4; _step++;
                break;
            case 5:
                Check(!Get<bool>("_autoShotScheduled"), "Modern screenshot was not flushed");
                Set("_chatActive", true);
                _next = _clock.Elapsed.TotalSeconds + .3; _step++;
                break;
            case 6:
                Check(!Relative && Owner == "None" && game.IsMouseVisible, "Chat did not release capture");
                Console.WriteLine("[p950] chat released pointer");
                Set("_chatActive", false);
                _next = _clock.Elapsed.TotalSeconds + .3; _step++;
                break;
            case 7:
                Check(Relative && Owner == "Flight", "Chat close did not restore flight capture");
                Call("ToggleMainMenu");
                _next = _clock.Elapsed.TotalSeconds + .3; _step++;
                break;
            case 8:
                Check(!Relative && Owner == "None" && game.IsMouseVisible, "Menu did not release capture");
                Console.WriteLine("[p950] menu released pointer");
                Call("ToggleMainMenu");
                Call("ToggleFreeCam");
                _next = _clock.Elapsed.TotalSeconds + .3; _step++;
                break;
            case 9:
                Check(Relative && Owner == "FreeLook", "Free-look did not acquire capture");
                // The shipless observer does not execute the flight-frame
                // reset; verify its stale-latch state through the live owner.
                Set("_menuConsumedFrame", true);
                Call("SyncMouseCapture", false);
                Check(Relative && Owner == "FreeLook", "A stale flight latch blocked free-look");
                Console.WriteLine("[p950] spectator free-look relative capture");
                Call("ToggleFreeCam");
                Set("_mouseFlightEnabled", false);
                _next = _clock.Elapsed.TotalSeconds + .3; _step++;
                break;
            case 10:
                Check(!Relative && Owner == "None" && game.IsMouseVisible, "ToggleMouse off did not release pointer");
                Console.WriteLine("[p950] ToggleMouse off released pointer");
                File.WriteAllText(Path.Combine(output, "window.json"), JsonSerializer.Serialize(new
                {
                    desktop = "Wayland", sdlVideo = "x11 (Xwayland)", rdpTested = false, nativeWaylandTested = false,
                    input, physicalDeviceTested = input == "manual",
                    focusLossSeen = _focusLossSeen,
                    results = _receipts, chatReleased = true, menuReleased = true, freeLookRelative = true, toggleOffReleased = true,
                }, new JsonSerializerOptions { WriteIndented = true }));
                Native.SDL_DelEventWatch(_watch, 0);
                Completed = true; game.Exit();
                break;
        }
    }
}

static class Native
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int EventWatch(nint data, nint ev);
    [DllImport("libSDL2-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_AddEventWatch(EventWatch watch, nint data);
    [DllImport("libSDL2-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_DelEventWatch(EventWatch watch, nint data);
    [DllImport("libSDL2-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] internal static extern void SDL_RaiseWindow(nint window);
    [DllImport("libSDL2-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_SetWindowInputFocus(nint window);
    [DllImport("libSDL2-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] internal static extern nint SDL_GetCurrentVideoDriver();
    [DllImport("libSDL2-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] internal static extern int SDL_GetRelativeMouseMode();
    [DllImport("libSDL2-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] internal static extern nint SDL_GetMouseFocus();
    [DllImport("libSDL2-2.0.so.0", CallingConvention = CallingConvention.Cdecl)] internal static extern nint SDL_GetKeyboardFocus();
}
