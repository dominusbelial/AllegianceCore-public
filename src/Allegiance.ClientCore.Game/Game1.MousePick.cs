using System.Collections.Generic;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using NumericsVec2 = System.Numerics.Vector2;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// The mouse-input slice of <see cref="Game1"/> (P587). Partial class — shares
/// the <c>Game1</c> type, so private field/method access across the split files
/// is unaffected.
///
/// <para>Native has TWO mouse modes toggled by TK_ToggleMouse (WinTrek.cpp:
/// 9289-9298, <c>m_bEnableVirtualJoystick</c>; the port binds it to <b>M</b>,
/// <c>_mouseFlightEnabled</c>):</para>
/// <list type="bullet">
/// <item><b>Virtual joystick ON</b> (the mouse flies the ship): the buttons
/// follow <c>mouseButtonCommandMap</c> in <c>Artwork/defaultinputmap.mdl</c> —
/// <c>(0,55) (2,56) (9,58) (8,59) (1,90)</c>, i.e. left = TK 55
/// (<c>TK_FireWeapon</c>), middle = TK 56 (<c>TK_FireMissile</c>), wheel =
/// throttle up/down (58/59, already ported P445), right = TK 90
/// (<c>TK_TargetCenter</c>). The joystick <c>buttonCommandMap</c> triple
/// <c>(0,0,55) (0,1,56)</c> confirms 55 = FireWeapon and 56 = FireMissile.</item>
/// <item><b>Virtual joystick OFF</b> (the mouse is a free POINTER): clicks pick
/// per <c>console.cpp</c> <c>ResolvePick</c> (:1857-1900) / <c>PickObject</c>
/// (:1912-1990) — left = OWN-SIDE ships (make them the command recipient),
/// right = whatever <c>LegalCommand</c> allows, <b>stations included</b>; a
/// right-click issues the default command (<c>c_cidDefault</c> → the ship's
/// default order, e.g. dock a friendly base) which sets the command target and
/// the autopilot (SetTarget :8823). A drag &gt; 100 px² cancels the pick.</item>
/// </list>
/// </summary>
public sealed partial class Game1
{
    /// <summary>
    /// P587 — the pointer pick's hit-test radius, in NORMALIZED screen units
    /// (the screen spans [-1, 1] on each axis). Native hit-tests each visible
    /// model by its OWN screen radius (<c>ThingSite::GetScreenRadius</c>,
    /// console.cpp:1893-1899); this port's candidates carry no model radius, so
    /// it approximates with a fixed fraction of the half-screen. Chosen so a
    /// mid-sized base / ship fills comfortably inside it at typical distances.
    /// </summary>
    internal const float PointerPickRadius = 0.085f;

    /// <summary>P587 — squared pointer drag (in NORMALIZED screen units) above
    /// which a click is treated as a drag and the pick is cancelled — the port
    /// of native's &gt;100 px² drag test (console.cpp:1931-1934).</summary>
    internal const float PointerDragCancelSq = 0.006f;

    // Per-frame edge latches for the pointer-mode buttons (so a press that
    // straddles two frames still fires exactly once) and the drag tracker.
    private bool _ptrLeftWasDown;
    private bool _ptrRightWasDown;
    private bool _ptrLeftDown;
    private NumericsVec2 _ptrDragStart;

    /// <summary>
    /// P587 — called every flying frame from <see cref="Game1.FlightInput.UpdateFlying"/>.
    /// Dispatches the mode-dependent mouse buttons (native's two TK_ToggleMouse
    /// modes). Returns the fire stateM bits the mouse contributes in
    /// virtual-joystick mode (0 when the mouse is in pointer mode), so the
    /// caller ORs it into the translated command — the same unified
    /// <c>IsTrekKeyDown(TK_FireWeapon, bReadKeyboard)</c> shape native uses
    /// (WinTrek.cpp:10876).<br/>
    /// <b>Virtual joystick ON:</b> left = fire weapon (held), middle = fire
    /// missile (held, shared 250 ms throttle), right = target centre (edge).<br/>
    /// <b>Virtual joystick OFF:</b> no fire; left/right are pointer picks
    /// (see <see cref="HandlePointerPickClick"/>).
    /// </summary>
    private int HandleFlightMouseButtons(MouseState ms)
    {
        bool left = ms.LeftButton == ButtonState.Pressed;
        bool right = ms.RightButton == ButtonState.Pressed;
        bool middle = ms.MiddleButton == ButtonState.Pressed;

        if (_mouseFlightEnabled)
        {
            int fire = 0;
            // Left = TK_FireWeapon (55): held, continuous like Space.
            if (left)
                fire |= FlightInput.FireContribution(true, _groupFire, _selectedMount);

            // Middle = TK_FireMissile (56): held, throttled like Left Ctrl.
            if (middle)
            {
                long nowMs = Environment.TickCount64;
                if (nowMs - _lastMissileSendMs >= 250)
                {
                    _lastMissileSendMs = nowMs;
                    _session.SendFireMissile();
                    Console.WriteLine("[monogame] fire missile (mouse middle) -> FIRE_MISSILE(70)");
                }
            }

            // Right = TK_TargetCenter (90): edge-triggered like the ' key.
            if (right && !_ptrRightWasDown)
                SelectCentermostTarget();

            _ptrRightWasDown = right;
            return fire;
        }

        // Pointer mode (virtual joystick off): clicks pick; no fire.
        HandlePointerPickClick(ms, left, right);
        return 0;
    }

    /// <summary>
    /// P587 — the pointer-mode click actions (native <c>console.cpp</c>
    /// <c>Button</c>/<c>PickObject</c>): LEFT on an own-side ship makes it the
    /// command recipient (<c>_commandee</c>); RIGHT on a legal command target
    /// (any non-self ship, station, asteroid or warp — <b>stations included</b>)
    /// sets it as the autopilot target and ENGAGES the autopilot (native's
    /// default command → SetTarget/SetAccepted → the autopilot flies, and
    /// P575's DockApproach docks a friendly dockable station). A drag beyond the
    /// threshold cancels the pick (native &gt;100 px², console.cpp:1931).
    /// </summary>
    private void HandlePointerPickClick(MouseState ms, bool left, bool right)
    {
        var world = _session.World;
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own))
        {
            _ptrLeftDown = false; _ptrRightWasDown = false;
            return;
        }
        if (!TryOwnPose(out var pos, out var orient, out _))
        {
            _ptrLeftDown = false; _ptrRightWasDown = false;
            return;
        }

        bool leftEdge = left && !_ptrLeftWasDown;
        bool leftReleased = !left && _ptrLeftWasDown;
        bool rightEdge = right && !_ptrRightWasDown;
        bool rightReleased = !right && _ptrRightWasDown;

        // Press: capture the press position (to tell a click from a drag) and
        // latch the edge (native captures on down, acts on release).
        if (leftEdge)
        {
            _ptrLeftDown = true;
            _ptrDragStart = new NumericsVec2(ms.X, ms.Y);
        }
        if (rightEdge)
            _ptrDragStart = new NumericsVec2(ms.X, ms.Y);

        // Release: act only if the pointer barely moved (a click, not a drag).
        if (leftReleased)
        {
            bool wasClick = _ptrLeftDown && SquaredDrag(ms.X, ms.Y, _ptrDragStart) <= PointerDragCancelSq;
            _ptrLeftDown = false;
            if (wasClick) ActOnLeftPick(me, own, pos, orient, ms);
        }
        if (rightReleased)
        {
            bool wasClick = SquaredDrag(ms.X, ms.Y, _ptrDragStart) <= PointerDragCancelSq;
            if (wasClick) ActOnRightPick(me, own, pos, orient, ms);
        }

        _ptrLeftWasDown = left;
        _ptrRightWasDown = right;
    }

    private static float SquaredDrag(float x1, float y1, NumericsVec2 start)
    {
        float dx = (x1 - start.X), dy = (y1 - start.Y);
        return dx * dx + dy * dy;
    }

    /// <summary>P587 — LEFT pointer pick: resolve the nearest own-side ship
    /// within the pick radius of the cursor and make it the commandee (native
    /// <c>ResolvePick</c> with button==0, console.cpp:1874-1877).</summary>
    private void ActOnLeftPick(short me, ShipEntity own, SimVec pos, Orientation orient, MouseState ms)
    {
        var world = _session.World;
        List<CandidateObject> objs;
        lock (_session.Gate)
            objs = GatherSectorCandidates(world.Ships, world.Warps, world.Stations, world.Asteroids,
                                          me, own.SectorId, ShipRoleBits, StationRoleBits);
        if (!TryPointerProjection(pos, orient, out var proj)) return;
        var cursor = NormalizedCursor(ms);
        var picked = ResolvePointerPick(objs, proj, cursor.X, cursor.Y, PointerPickRadius,
                                        world.MySideId, me, leftButton: true);
        if (picked is { } p)
        {
            _commandee = p;
            Console.WriteLine($"[monogame] pointer: commandee {p.Name} (ship #{p.Id})");
        }
        else
        {
            Console.WriteLine("[monogame] pointer: no own-side ship under the cursor");
        }
    }

    /// <summary>P587 — RIGHT pointer pick: resolve the nearest legal command
    /// target within the pick radius of the cursor and set it as the autopilot
    /// target, ENGAGING the autopilot (native default command → target +
    /// autopilot; a friendly dockable station then docks via P575).</summary>
    private void ActOnRightPick(short me, ShipEntity own, SimVec pos, Orientation orient, MouseState ms)
    {
        var world = _session.World;
        List<CandidateObject> objs;
        lock (_session.Gate)
            objs = GatherSectorCandidates(world.Ships, world.Warps, world.Stations, world.Asteroids,
                                          me, own.SectorId, ShipRoleBits, StationRoleBits);
        if (!TryPointerProjection(pos, orient, out var proj)) return;
        var cursor = NormalizedCursor(ms);
        var picked = ResolvePointerPick(objs, proj, cursor.X, cursor.Y, PointerPickRadius,
                                        world.MySideId, me, leftButton: false);
        if (picked is { } p)
        {
            EngageAutopilotToward(p, "pointer:");
            Console.WriteLine($"[monogame] pointer: target {p.Name} ({p.Kind}#{p.Id}) — autopilot engaged");
        }
        else
        {
            Console.WriteLine("[monogame] pointer: no command target under the cursor");
        }
    }

    /// <summary>
    /// P587 — build a world→normalized-screen projector for the CURRENT flight
    /// camera framing (mirrors the targeting pass's camera selection so the
    /// pick hits where the pilot sees the model). Returns false when the own
    /// pose cannot be projected (caller aborts the pick).
    /// </summary>
    private bool TryPointerProjection(SimVec simPos, Orientation simOrient, out Func<SimVec, (float Sx, float Sy)?> project)
    {
        project = null!;
        var xnaPos = PoseConverter.ToXna(simPos);
        var rot = PoseConverter.ToXnaRotation(simOrient);
        float aspect = GraphicsDevice.Viewport.AspectRatio;

        Matrix view, projection;
        Vector3 eye;
        if (_cameraMode == CameraMode.Cockpit)
            (view, projection) = _cockpitCamera.Matrices(xnaPos, rot, aspect, out eye);
        else if (_directorActive)
            (view, projection) = _chaseCamera.Matrices(xnaPos, rot, aspect, 0f, out eye, _myRenderRadius);
        else
            (view, projection, eye) = ComputeFlightCamera(xnaPos, rot, simOrient, aspect, 0f);
        var viewProj = view * projection;

        project = p =>
        {
            var rel = PoseConverter.ToXna(p) - eye;
            var clip = Vector4.Transform(new Vector4(rel.X, rel.Y, rel.Z, 1f), viewProj);
            if (clip.W <= 1e-6f) return null;
            float sx = clip.X / clip.W;
            float sy = clip.Y / clip.W;
            if (MathF.Abs(sx) > 1f || MathF.Abs(sy) > 1f) return null;
            return (sx, sy);
        };
        return true;
    }

    /// <summary>P587 — the cursor as normalized screen coords ([-1,1] on each
    /// axis, origin at screen centre), matching native's
    /// <c>point - rectImage.Center()</c> / halfwidth (console.cpp:1860-1862).</summary>
    private NumericsVec2 NormalizedCursor(MouseState ms)
    {
        int cx = GraphicsDevice.Viewport.Width / 2;
        int cy = GraphicsDevice.Viewport.Height / 2;
        float hw = GraphicsDevice.Viewport.Width * 0.5f;
        float hh = GraphicsDevice.Viewport.Height * 0.5f;
        float nx = hw <= 0f ? 0f : (ms.X - cx) / hw;
        float ny = hh <= 0f ? 0f : (ms.Y - cy) / hh;
        return new NumericsVec2(nx, ny);
    }

    /// <summary>
    /// P587 — the pure pointer-pick resolver (the port of native
    /// <c>ResolvePick</c>, console.cpp:1857-1900): over the already-gathered
    /// candidate objects, project each to screen, keep only LEGAL candidates
    /// (LEFT = own-side ships; RIGHT = any non-self commandable kind), hit-test
    /// against the cursor within <paramref name="radius"/> (screen-position ±
    /// screen-radius, nearest to the cursor), and return the NEAREST legal one.
    /// Pure + static for unit testing.
    /// </summary>
    internal static AutopilotTarget? ResolvePointerPick(
        IReadOnlyList<CandidateObject> objs,
        Func<SimVec, (float Sx, float Sy)?> projectToScreen,
        float cursorSx, float cursorSy, float radius,
        short? mySideId, short selfId, bool leftButton)
    {
        AutopilotTarget? best = null;
        float bestD = float.MaxValue;
        foreach (var o in objs)
        {
            if (leftButton)
            {
                // Native button!=1 → only own-side ships (console.cpp:1874-1877).
                if (o.Kind != TargetKind.Ship || o.Id == selfId || o.SideId != mySideId) continue;
            }
            else
            {
                // Native button==1 → whatever LegalCommand(default) allows
                // (stations included); never self.
                if (o.Id == selfId) continue;
            }
            if (projectToScreen(o.Position) is not { } scr) continue;
            float dx = scr.Sx - cursorSx;
            float dy = scr.Sy - cursorSy;
            float d = dx * dx + dy * dy;
            if (d > radius * radius) continue;
            if (d < bestD)
            {
                bestD = d;
                best = new AutopilotTarget(o.Kind, o.Id, o.Name);
            }
        }
        return best;
    }
}
