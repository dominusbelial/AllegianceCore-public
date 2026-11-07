using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P950RelativeMouseTests
{
    private sealed class Backend(bool available = true) : IRelativeMouseBackend
    {
        internal readonly List<bool> Modes = [];
        internal (int X, int Y) Delta;
        internal bool Disposed;
        public bool SetEnabled(bool enabled) { Modes.Add(enabled); return available; }
        public (int X, int Y) ReadDelta() { var result = Delta; Delta = (0, 0); return result; }
        public void Dispose() => Disposed = true;
    }

    // Run the complete pixel history, including clamping, deadzone, hold, resume,
    // inversion, both response curves and the sensitivity bounds. The warp-side
    // controller is the unchanged centre-offset input calculation.
    [Theory]
    [InlineData(false, false, .1f)]
    [InlineData(false, true, 1f)]
    [InlineData(true, false, 2f)]
    [InlineData(true, true, .6f)]
    public void Relative_pixels_produce_exactly_the_old_center_offset_controls(bool invert, bool linear, float sensitivity)
    {
        foreach (float deadzone in MouseFlightController.DeadzonePresets)
        {
            var relative = new MouseFlightController { InvertPitch = invert, LinearControls = linear,
                MouseSensitivity = sensitivity, Deadzone = deadzone };
            var old = new MouseFlightController { InvertPitch = invert, LinearControls = linear,
                MouseSensitivity = sensitivity, Deadzone = deadzone };
            var backend = new Backend();
            using var mouse = new RelativeMouse(() => backend, _ => { });
            mouse.SetOwner(MouseCaptureOwner.Flight);
            relative.Enable(); old.Enable();
            (int X, int Y)[] motion = [(999, -999), (1, -1), (20, 40), (-75, 110), (0, 0),
                (250, -120), (1600, 900), (-2400, -1700), (100, -150), (0, 0)];
            foreach (var delta in motion)
            {
                backend.Delta = delta;
                var read = mouse.ReadDelta(13, 27, 640, 360, (_, _) => throw new Exception("Relative mode must not warp"));
                var actual = relative.Update(read.X, read.Y);
                var expected = old.Update(640 + delta.X - 640, 360 + delta.Y - 360);
                Assert.Equal(expected, actual);
                Assert.Equal(MouseFlightController.MapResponse(expected, deadzone, linear),
                    MouseFlightController.MapResponse(actual, deadzone, linear));
            }
        }
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Capture_releases_for_every_pointer_state_and_reacquires_without_stale_motion(HudStyle style)
    {
        Assert.True(style is HudStyle.Classic or HudStyle.Modern); // policy is shared
        var backend = new Backend();
        var logs = new List<string>();
        using var mouse = new RelativeMouse(() => backend, logs.Add);
        MouseCaptureOwner Choose(bool focused = true, bool flying = true, bool docked = false,
            bool blocked = false, bool enabled = true, bool map = false, bool command = false,
            bool spectator = false, bool free = false) => MouseCapturePolicy.Choose(focused, flying, docked,
                blocked, enabled, map, command, spectator, free);
        // menu, hangar, chat/quick-chat, ToggleMouse off, focus loss, map,
        // command pointer-picking, orbit camera, disconnect/lobby.
        MouseCaptureOwner[] releases = [Choose(flying: false), Choose(docked: true), Choose(blocked: true),
            Choose(enabled: false), Choose(focused: false), Choose(map: true), Choose(command: true),
            Choose(spectator: true), Choose(flying: false, enabled: false)];
        foreach (var release in releases)
        {
            Assert.Equal(MouseCaptureOwner.None, release);
            Assert.True(mouse.SetOwner(Choose()));
            Assert.True(mouse.IsRelative);
            Assert.False(mouse.SetOwner(Choose()));
            backend.Delta = (400, -500);
            Assert.Equal((0, 0), mouse.ReadDelta(0, 0, 640, 360, (_, _) => Assert.Fail("warp")));
            backend.Delta = (16, -9);
            Assert.Equal((16, -9), mouse.ReadDelta(0, 0, 640, 360, (_, _) => Assert.Fail("warp")));
            Assert.True(mouse.SetOwner(release));
            Assert.False(mouse.IsRelative);
            Assert.False(backend.Modes[^1]);
            backend.Delta = (800, 800);
            Assert.Equal((0, 0), mouse.ReadDelta(0, 0, 640, 360, (_, _) => Assert.Fail("warp")));
        }
        Assert.Equal(MouseCaptureOwner.FreeLook, Choose(enabled: false, spectator: true, free: true));
        Assert.Equal(MouseCaptureOwner.None, Choose(blocked: true, spectator: true, free: true));
        Assert.Equal(MouseCaptureOwner.None, Choose(focused: false, spectator: true, free: true));
        mouse.SetOwner(Choose(spectator: true, free: true));
        mouse.SetOwner(Choose()); // handoff without a UI frame still releases and flushes
        Assert.Equal(new[] { true, false, true }, backend.Modes.TakeLast(3));
        mouse.Dispose(); mouse.Dispose();
        Assert.True(backend.Disposed);
        Assert.False(backend.Modes[^1]);
        Assert.Equal(MouseCaptureOwner.None, mouse.Owner);
        Assert.False(mouse.SetOwner(Choose()));
        Assert.Equal(new[] { "[mouse] relative" }, logs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_refused_relative_mode_falls_back_to_warp_and_logs_once(bool missing)
    {
        var backend = new Backend(available: false);
        var logs = new List<string>();
        using var mouse = new RelativeMouse(() => missing ? null : backend, logs.Add);
        var warps = new List<(int, int)>();
        for (int cycle = 0; cycle < 3; cycle++)
        {
            mouse.SetOwner(MouseCaptureOwner.Flight);
            Assert.False(mouse.IsRelative);
            Assert.Equal((0, 0), mouse.ReadDelta(1900, -99, 640, 360, (x, y) => warps.Add((x, y))));
            Assert.Equal((30, -15), mouse.ReadDelta(670, 345, 640, 360, (x, y) => warps.Add((x, y))));
            mouse.SetOwner(MouseCaptureOwner.None);
        }
        Assert.Equal(6, warps.Count);
        Assert.All(warps, point => Assert.Equal((640, 360), point));
        Assert.Equal(new[] { "[mouse] warp" }, logs);
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Free_look_resumes_after_menu_close_despite_the_observers_stale_flight_frame_latch(HudStyle style)
    {
        Assert.True(style is HudStyle.Classic or HudStyle.Modern);
        var backend = new Backend();
        using var mouse = new RelativeMouse(() => backend, _ => { });
        MouseCaptureOwner Choose(bool menuOpen, bool freeLook) => MouseCapturePolicy.Choose(
            focused: true, flying: true, docked: false, blocked: menuOpen, mouseFlight: true,
            expandedMap: false, commandCamera: false, spectatorCamera: freeLook, freeLook: freeLook,
            menuConsumedFrame: true);
        mouse.SetOwner(Choose(menuOpen: false, freeLook: true));
        Assert.Equal(MouseCaptureOwner.FreeLook, mouse.Owner);
        Assert.True(mouse.IsRelative);
        mouse.SetOwner(Choose(menuOpen: true, freeLook: true));
        Assert.Equal(MouseCaptureOwner.None, mouse.Owner);
        Assert.False(mouse.IsRelative);
        // Local observer frames never execute UpdateFlying's latch reset.
        mouse.SetOwner(Choose(menuOpen: false, freeLook: true));
        Assert.Equal(MouseCaptureOwner.FreeLook, mouse.Owner);
        Assert.True(mouse.IsRelative);
        Assert.Equal(MouseCaptureOwner.None, Choose(menuOpen: false, freeLook: false));
    }
}
