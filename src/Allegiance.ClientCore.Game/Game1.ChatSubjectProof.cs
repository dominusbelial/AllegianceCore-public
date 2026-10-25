using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P818 — real-window proof of the composer's individual/group recipient
/// selection (<c>ALLEGIANCE_P818_PROOF=1</c>), the P624/P626 proof pattern: the
/// real windowed client is driven through the REAL input path and the real
/// backbuffer is captured per step, so the radar's <c>c_maskSubject</c>
/// brackets can be photographed with nothing faked.
///
/// <para>Every step goes through production code: the compose is opened with
/// <c>SetComposeState(Chat)</c> (native <c>StartChat</c>, console.cpp:2423-2431),
/// and the selection is made by replaying a real LEFT click / drag through
/// <see cref="HandlePointerPickClick"/> with a synthesized <c>MouseState</c>
/// aimed at a ship's own projected screen point — the same
/// <c>ResolvePick</c> → <c>PickShip</c>/rectangle path a player's mouse takes
/// (console.cpp:2078-2193). A press and its release happen in the SAME frame, so
/// no other frame observes the half-pressed latch.</para>
///
/// <para>Sequence (one real backbuffer shot per step, ~1.6 s apart):</para>
/// <list type="number">
/// <item>command view + radar All + an open chat compose (baseline),</item>
/// <item>LEFT click a friendly ship → <c>CHAT_INDIVIDUAL</c> → shot,</item>
/// <item>LEFT drag over two or three friendly ships → <c>CHAT_GROUP</c> → shot,
/// </item>
/// <item>done.</item>
/// </list>
/// </summary>
public sealed partial class Game1
{
    private bool _p818ProofStarted, _p818ProofDone;
    private double _p818ProofFlying;
    private double _p818ProofStepAge;
    private int _p818ProofStep;
    private readonly List<short> _p818OnScreen = [];

    /// <summary>Seconds of flying before the first selection, so the autoproof
    /// seat/launch and the MCP bots have settled (env override
    /// <c>ALLEGIANCE_P818_PROOF_DELAY</c>).</summary>
    private static double P818StartDelay =>
        double.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_P818_PROOF_DELAY"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d : 5.0;

    private void RunChatSubjectProof(double dtSec)
    {
        if (_p818ProofDone) return;
        if (!TryOwnPose(out var ownPosition, out var ownOrientation, out _)) return;

        _p818ProofFlying += dtSec;
        _p818ProofStepAge += dtSec;
        if (!_p818ProofStarted)
        {
            if (_p818ProofFlying < P818StartDelay) return;
            // The command view is where native assigns psubjects at all
            // (bRangeLabels OFF ⇔ CommandCamera, radarimage.cpp:524,570-574).
            _radarMode = RadarMode.All;
            ApplyConsoleMode("ViewCommand");
            _cameraMode = CameraMode.Command;
            _commandCentered = false;
            IsMouseVisible = true;
            // Native StartChat: Reset(true) then the Chat compose
            // (console.cpp:1656,2423-2431).
            ResetChatSubjectOnStart();
            SetComposeState(NativeComposeState.Chat);
            _throttle = -1f;   // hold still so the friendly contacts stay framed
            _chatBuffer.Clear();
            _chatBuffer.Append("regroup");
            _p818ProofStarted = true;
            _p818ProofStepAge = 0;
            Console.WriteLine($"[p818-proof] command view + chat compose open (radar {_radarMode}, recipient {_chatTarget})");
            return;
        }

        const double step = 1.4;
        if (_p818ProofStepAge < step) return;
        _p818ProofStepAge = 0;
        _throttle = -1f;   // hold still so the friendly contacts stay framed

        // Each iteration works on the PREVIOUS step's selection: one full step has
        // elapsed, so the radar contact list drawn since then is settled and the
        // shot names what that list actually contains — then the next selection is
        // made, so a later iteration can photograph it.
        switch (_p818ProofStep)
        {
            case 0:
                if (!CollectOnScreenShips(ownPosition, ownOrientation)) return;
                if (!ClickOnScreenShip()) return;
                _p818ProofStep = 1;
                break;

            // The shot frame is the one whose Draw follows THIS update, so each
            // selection gets its own step: select, then (next step) report + shoot
            // on a frame that still holds it, and only then move on.
            case 1:
                LogContacts("the individual click");
                Shot("p818-individual", "individual recipient");
                _p818ProofStep = 2;
                break;

            case 2:
                if (!CollectOnScreenShips(ownPosition, ownOrientation)) return;
                if (!DragOverOnScreenShips()) return;
                _p818ProofStep = 3;
                break;

            default:
                LogContacts("the group drag");
                Shot("p818-group", "group recipient");
                _p818ProofDone = true;
                Console.WriteLine("[p818-proof] done (individual + group subject shots)");
                break;
        }
    }

    /// <summary>Project every same-side ship of the drawn sector through the
    /// SAME projector the pointer pick resolves against, keep those that land on
    /// screen (and are not under the minimap or the command bar, which would
    /// swallow the click), player-piloted ones first, then nearest the centre.
    /// Returns false (and retries next step) while there is nothing to pick.</summary>
    private bool CollectOnScreenShips(SimVec ownPosition, Allegiance.Core.Sim.Orientation ownOrientation)
    {
        _p818OnScreen.Clear();
        if (!TryPointerProjection(ownPosition, ownOrientation, out var project)) return false;
        var world = _session.World;
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)) return false;
        short sector = RenderSector(own.SectorId);
        // The SAME candidate set the production rectangle/pick resolves against
        // (GatherSectorCandidates with the pointer pick's role bits), so every
        // ship collected here is one the rectangle can actually select.
        List<CandidateObject> candidates;
        lock (_session.Gate)
            candidates = GatherSectorCandidates(world.Ships, world.Warps, world.Stations, world.Asteroids,
                                                me, sector, ShipRoleBits, StationRoleBits);
        AnchorCommandCandidates(candidates);
        var found = new List<(bool System, float D, short Id, int X, int Y)>();
        var vp = GraphicsDevice.Viewport;
        foreach (var candidate in candidates)
        {
            if (candidate.Kind != TargetKind.Ship || candidate.Id == me) continue;
            if (candidate.SideId != own.SideId) continue;
            if (project(candidate.Position) is not { } screen) continue;
            int x = (int)MathF.Round(vp.Width / 2f + screen.Sx * vp.Width / 2f);
            int y = (int)MathF.Round(vp.Height / 2f + screen.Sy * vp.Height / 2f);
            var ms = new MouseState(x, y, 0, ButtonState.Released,
                ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
            if (PointerOverStarmap(ms) || OverCommandBar(x, y)) continue;
            bool system;
            lock (_session.Gate)
                system = !world.Ships.TryGetValue(candidate.Id, out var ship) || ship.Player is null;
            float dx = screen.Sx, dy = screen.Sy;
            found.Add((system, dx * dx + dy * dy, candidate.Id, x, y));
        }
        found.Sort((a, b) => a.System != b.System ? (a.System ? 1 : -1) : a.D.CompareTo(b.D));
        foreach (var f in found) _p818OnScreen.Add(f.Id);
        Console.WriteLine($"[p818-proof] {found.Count} pickable same-side ship(s) on screen: " +
                          string.Join(",", found.Select(f => $"{f.Id}@{f.X},{f.Y}")));
        return found.Count > 0;
    }

    /// <summary>The ship's current pixel position from the same projector the
    /// pointer pick uses (<see cref="TryPointerProjection"/>).</summary>
    private bool ShipPixel(short id, out int x, out int y, SimVec ownPosition,
        Allegiance.Core.Sim.Orientation ownOrientation)
    {
        x = y = 0;
        if (!TryPointerProjection(ownPosition, ownOrientation, out var project)) return false;
        var world = _session.World;
        float sx, sy;
        lock (_session.Gate)
        {
            if (!world.Ships.TryGetValue(id, out var ship) || ship.Latest is not { } latest) return false;
            var position = _poses.TryGetValue(id, out var pose) ? pose.Position : latest.Position;
            if (project(position) is not { } screen) return false;
            (sx, sy) = (screen.Sx, screen.Sy);
        }
        var vp = GraphicsDevice.Viewport;
        x = (int)MathF.Round(vp.Width / 2f + sx * vp.Width / 2f);
        y = (int)MathF.Round(vp.Height / 2f + sy * vp.Height / 2f);
        return true;
    }

    /// <summary>The native command bar's panel rect (HUD-10/HUD-11) — a click
    /// inside it is consumed by the bar, not by the world pick.</summary>
    private bool OverCommandBar(int x, int y)
    {
        if (_cameraMode != CameraMode.Command) return false;
        float progress = _paneSlides.Progress(Rendering.SlidingPane.CommandPane, _nowSeconds);
        if (progress < 0.9f) return false;
        var vp = GraphicsDevice.Viewport;
        float left = Rendering.CommandBarLayout.Left(vp.Width, progress);
        float top = Rendering.CommandBarLayout.Top(vp.Height, progress);
        return x >= left && x < left + Rendering.CommandBarLayout.PanelWidth
            && y >= top && y < top + Rendering.CommandBarLayout.PanelHeight;
    }

    /// <summary>Replay a real LEFT click on the chosen friendly ship (press and
    /// release in the same frame).</summary>
    private bool ClickOnScreenShip()
    {
        if (_p818OnScreen.Count == 0) return false;
        short target = _p818OnScreen[0];
        if (!TryOwnPose(out var ownPosition, out var ownOrientation, out _)) return false;
        if (!ShipPixel(target, out int x, out int y, ownPosition, ownOrientation))
        {
            Console.WriteLine($"[p818-proof] ship {target} did not project this frame — retrying");
            return false;
        }
        HandlePointerPickClick(MouseAt(x, y, pressed: true), left: true, right: false);
        HandlePointerPickClick(MouseAt(x, y), left: false, right: false);
        LogSubjects($"click ship {target} at {x},{y}");
        return true;
    }

    /// <summary>Replay a real LEFT drag whose rectangle covers every on-screen
    /// friendly ship (native's rectangle → <c>CHAT_GROUP</c>,
    /// console.cpp:2100-2147), then verify the production rectangle actually
    /// formed a multi-ship group — otherwise retry as the ships drift.</summary>
    private bool DragOverOnScreenShips()
    {
        if (_p818OnScreen.Count < 2)
        {
            Console.WriteLine($"[p818-proof] only {_p818OnScreen.Count} same-side ship(s) on screen — " +
                              "a group drag needs two, retrying");
            return false;
        }
        if (!TryOwnPose(out var ownPosition, out var ownOrientation, out _)) return false;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        int covered = 0;
        foreach (short id in _p818OnScreen)
        {
            if (!ShipPixel(id, out int x, out int y, ownPosition, ownOrientation)) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            covered++;
        }
        if (minX == int.MaxValue)
        {
            Console.WriteLine("[p818-proof] no group ship projected this frame — retrying");
            return false;
        }
        var vp = GraphicsDevice.Viewport;
        int x0 = Math.Clamp(minX - 20, 1, vp.Width - 2), y0 = Math.Clamp(minY - 20, 1, vp.Height - 2);
        int x1 = Math.Clamp(maxX + 20, 1, vp.Width - 2), y1 = Math.Clamp(maxY + 20, 1, vp.Height - 2);
        HandlePointerPickClick(MouseAt(x0, y0, pressed: true), left: true, right: false);
        HandlePointerPickClick(MouseAt(x1, y1), left: false, right: false);
        int formed;
        lock (_session.Gate) formed = _chatRecipient.Count;
        LogSubjects($"drag {x0},{y0} -> {x1},{y1} enclosing {covered} ship(s)");
        if (formed < 2)
        {
            Console.WriteLine($"[p818-proof] the rectangle formed {formed} subject(s) — retrying the drag");
            return false;
        }
        return true;
    }

    private static MouseState MouseAt(int x, int y, bool pressed = false)
    {
        var button = pressed ? ButtonState.Pressed : ButtonState.Released;
        return new MouseState(x, y, 0, button, ButtonState.Released, ButtonState.Released,
            ButtonState.Released, ButtonState.Released);
    }

    /// <summary>Report the compose state right after a selection (the radar
    /// contacts it produces are reported one frame later by
    /// <see cref="LogContacts"/>).</summary>
    private void LogSubjects(string what)
    {
        string ships;
        ChatTarget target;
        NativeComposeState state = LiveComposeState;
        lock (_session.Gate)
        {
            ships = string.Join(",", _chatRecipient.Ships);
            target = _chatTarget;
        }
        Console.WriteLine($"[p818-proof] {what}: recipient {target} subjects [{ships}] compose state {state}");
    }

    /// <summary>Every ship contact carrying native c_maskSubject
    /// (radarimage.cpp:651) on the last drawn frame, with its placed point and
    /// the native bracket radius (1.05 x GetScreenRadius, radarimage.cpp:313).</summary>
    private void LogContacts(string what)
    {
        int marked = 0, ships = 0;
        string marks = "";
        foreach (var c in _drawRadarContacts)
        {
            if (c.Kind == HudContactKind.Ship) ships++;
            if (!c.Subject) continue;
            marked++;
            marks += $" [{c.Position.X:0},{c.Position.Y:0} onScreen={c.OnScreen} r={c.ScreenRadius:0.#}]";
        }
        string state;
        lock (_session.Gate)
            state = $"recipient {_chatTarget} subjects [{string.Join(",", _chatRecipient.Ships)}]";
        Console.WriteLine($"[p818-proof] radar frame after '{what}' ({state}, compose {LiveComposeState}): " +
                          $"{marked} subject contact(s) of {ships} ship contact(s) / {_drawRadarContacts.Count} total{marks}");
    }

    /// <summary>Latch a named real-backbuffer shot (the P572 autoshot flush).</summary>
    private void Shot(string name, string what)
    {
        _autoShotName = name;
        _autoShotScheduled = true;
        Console.WriteLine($"[p818-proof] shot: {name} ({what})");
    }
}
