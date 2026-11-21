using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P1067 — the browser half of the desktop director b-roll camera (P580,
/// <see cref="Director"/>): the parked "Watch" camera a launched pilot can turn
/// on to film the battle instead of flying it. This type owns the browser's
/// editorial state and the per-frame <see cref="DirectorSnapshot"/>; the shot
/// kinds, subject weighting, dwell and transitions stay in the shared pure
/// <see cref="Director"/> that the desktop also uses.
/// <para>
/// The snapshot mirrors the desktop glue
/// <c>Allegiance.ClientCore.Game/Game1.Director.cs:376-458</c>
/// (<c>BuildDirectorSnapshot</c>) — same subject kinds, the same interest
/// weights, the same 24-subject cap and the same sector-change/dock
/// re-acquire — but reads the P1065 sector index
/// (<see cref="ClientWorld.ShipsInSector"/>, <c>BattleShipScene.cs:23-35</c>)
/// and the stricter <see cref="ShipEntity.CanDrawInSector"/> gate instead of
/// the desktop's <c>IsNotVisible</c> + all-ships scan. It is a browser copy,
/// not a shared move: the desktop's table scan and visibility predicate are
/// different API, and moving it would change desktop behaviour.
/// </para>
/// <para>
/// Allocation: the subject and enemy-point lists live for the instance and are
/// cleared, never re-created, so a frame adds no allocations after the first
/// warm-up (the desktop allocates both lists per frame).
/// </para>
/// </summary>
public sealed class DirectorWatch
{
    /// <summary>Desktop candidate cap (<c>Game1.Director.cs:447-449</c>).</summary>
    public const int SubjectCap = 24;
    /// <summary>Desktop engagement radius — opposing ships within ~1,500 units
    /// (<c>Game1.Director.cs:394,415</c>).</summary>
    public const float EngagementRange = 1500f;
    /// <summary>The desktop's alphabetical subject weights
    /// (<c>Game1.Director.cs:409,417,428,436,444</c>), named here so the tests
    /// and the report cite one place.</summary>
    public const float ShipInterestBase = 20f, ShipInterestHull = 60f,
        EngagementInterestBase = 90f, EngagementInterestHull = 10f,
        ExplosionInterestMax = 110f, ExplosionInterestMin = 40f, ExplosionDecay = 20f,
        WarpInterest = 30f, StationInterest = 35f;

    private readonly Director _director;
    // The director's own free orientation (native m_orientationExternalCamera):
    // a mutable copy of the own-ship attitude it never aliases, gently drifted
    // each frame like the desktop glue (Game1.Director.cs:366-368).
    private Orientation _camera = Orientation.Identity;
    private readonly List<DirectorSubject> _subjects = new(SubjectCap + 1);
    private readonly List<(SimVec Position, float Hull)> _opposing = new(16);
    private short _lastSector = -1;
    private bool _lastDocked;

    public DirectorWatch(int seed = 0) => _director = new Director(seed);

    /// <summary>True while the director owns the camera.</summary>
    public bool Active { get; private set; }

    /// <summary>The shared pure planner (shot kinds, dwell, smoothing).</summary>
    public Director Planner => _director;

    /// <summary>Subjects gathered for the last snapshot (diagnostics/tests).</summary>
    public IReadOnlyList<DirectorSubject> Subjects => _subjects;

    /// <summary>The seed used for the current episode (deterministic evidence).</summary>
    public int Seed { get; private set; }

    /// <summary>Start an episode with <paramref name="seed"/>, replaying exactly
    /// the same shot sequence for the same seed and snapshot stream.</summary>
    public void Start(int seed, Orientation? ownOrientation = null)
    {
        _director.Reseed(seed);
        _camera = ownOrientation is null ? Orientation.Identity : new Orientation(ownOrientation);
        _lastSector = -1; _lastDocked = false; _subjects.Clear();
        Seed = seed;
        Active = true;
    }

    /// <summary>Return the camera to the chase view.</summary>
    public void Stop() => Active = false;

    /// <summary>Toolbar/key edge: stop if active, otherwise start with
    /// <paramref name="seed"/>. Returns the new state.</summary>
    public bool Toggle(int seed, Orientation? ownOrientation = null)
    {
        if (Active) { Stop(); return false; }
        Start(seed, ownOrientation);
        return true;
    }

    /// <summary>
    /// Build this frame's snapshot from the browser world. Mirrors
    /// <c>Game1.Director.cs:376-458</c>: own ship's sector and docked flag,
    /// opposing-side ships for engagement midpoints, a <c>Ship</c> subject per
    /// visible non-own ship (with one <c>Engagement</c> midpoint per opposing
    /// ship within 1,500 units), recency-scored explosions, alephs, stations,
    /// then the 24-subject cap. Returns the flag that makes the planner
    /// re-acquire after a sector change or a dock transition.
    /// </summary>
    public DirectorSnapshot BuildSnapshot(ClientWorld world, Vector3 ownPosition)
    {
        _subjects.Clear(); _opposing.Clear();
        short mySector = -1, mySide = -1;
        bool docked = false;
        short? own = world.MyShipId;
        if (own is { } me && world.Ships.TryGetValue(me, out var ownShip))
        {
            mySector = ownShip.SectorId;
            docked = ownShip.StationId >= 0;
        }
        mySide = world.MySideId ?? -1;

        IReadOnlyList<ShipEntity> members = mySector >= 0 ? world.ShipsInSector(mySector) : Array.Empty<ShipEntity>();

        // Pass 1 (desktop's first loop): every drawable opposing ship is a
        // potential engagement partner.
        for (int i = 0; i < members.Count; i++)
        {
            var ship = members[i];
            if (ship.SideId == mySide || !Visible(ship, mySector)) continue;
            _opposing.Add((ship.Latest!.Position, ship.Latest!.HullFraction));
        }

        // Pass 2: ship subjects plus one engagement midpoint per nearby pair.
        for (int i = 0; i < members.Count; i++)
        {
            var ship = members[i];
            if (ship.ShipId == own || !Visible(ship, mySector)) continue;
            var pose = ship.Latest!;
            float hull = pose.HullFraction;
            _subjects.Add(new DirectorSubject(ToXna(pose.Position), SubjectCategory.Ship,
                ShipInterestBase + (1f - hull) * ShipInterestHull,
                ship.CharacterName ?? $"ship{ship.ShipId}"));
            for (int e = 0; e < _opposing.Count; e++)
            {
                var gap = _opposing[e].Position - pose.Position;
                if (gap.LengthSquared() < EngagementRange * EngagementRange)
                    _subjects.Add(new DirectorSubject(
                        ToXna(0.5f * (_opposing[e].Position + pose.Position)),
                        SubjectCategory.Engagement,
                        EngagementInterestBase + (1f - hull) * EngagementInterestHull, "engagement"));
            }
        }

        // Explosions: recency-scored, always compelling (native blast).
        long now = Environment.TickCount64;   // ClientWorld.NowSeconds defaults to this clock.
        for (int i = 0; i < world.Explosions.Count; i++)
        {
            var explosion = world.Explosions[i];
            if (explosion.ClusterId != mySector) continue;
            float age = Math.Max(0f, (now - explosion.ReceivedLocalMs) / 1000f);
            _subjects.Add(new DirectorSubject(ToXna(explosion.Position), SubjectCategory.Explosion,
                Math.Max(ExplosionInterestMin, ExplosionInterestMax - age * ExplosionDecay), "explosion"));
        }

        // The splash collections are literal Dictionary instances in the client
        // world; iterating the IReadOnlyDictionary interface would box the
        // Dictionary enumerator every frame, so iterate the struct enumerator.
        if (world.Warps is Dictionary<short, DataWarpIGC> warps) foreach (var pair in warps) AddWarp(pair.Value);
        else foreach (var pair in world.Warps) AddWarp(pair.Value);
        if (world.Stations is Dictionary<short, DataStationIGC> stations) foreach (var pair in stations) AddStation(pair.Value);
        else foreach (var pair in world.Stations) AddStation(pair.Value);

        if (_subjects.Count > SubjectCap) _subjects.RemoveRange(SubjectCap, _subjects.Count - SubjectCap);

        bool sectorChanged = mySector != _lastSector || docked != _lastDocked;
        _lastSector = mySector;
        _lastDocked = docked;
        return new DirectorSnapshot(_subjects, ownPosition, sectorChanged, docked);

        // Direct calls to a capturing local function use the stack closure, so
        // these helpers add no allocation to the frame.
        void AddWarp(DataWarpIGC warp)
        {
            if (warp.ClusterId != mySector) return;
            _subjects.Add(new DirectorSubject(Vec(warp.Position), SubjectCategory.Warp, WarpInterest, "aleph"));
        }

        void AddStation(DataStationIGC station)
        {
            if (station.ClusterId != mySector) return;
            _subjects.Add(new DirectorSubject(Vec(station.Position), SubjectCategory.Station,
                StationInterest, station.Name ?? "station"));
        }
    }

    /// <summary>The P1065 visibility gate (<c>ShipEntity.CanDrawInSector</c>) plus
    /// the scene's docked exclusion — the same roster <c>BattleShipScene.Gather</c>
    /// gives the hull/contact pass.</summary>
    public static bool Visible(ShipEntity ship, short sector) =>
        ship.CanDrawInSector(sector) && ship.StationId < 0;

    /// <summary>The desktop's "any flight control returns the camera" rule
    /// (<c>Director.AnyFlightControl</c>, <c>Game1.Director.cs:557-571</c>),
    /// resolved through the live action map so a rebind cancels too.</summary>
    public static bool AnyFlightControl(KeyboardState keys)
        => Director.AnyFlightControl(
            forward: Down(keys, "ThrustForward"), back: Down(keys, "ThrustBackward"),
            strafeLeft: Down(keys, "RollLeft"), strafeRight: Down(keys, "RollRight"),
            thrustUp: Down(keys, "ThrustLeft"), thrustDown: Down(keys, "ThrustRight"),
            turnLeft: Down(keys, "YawLeft"), turnRight: Down(keys, "YawRight"));

    private static bool Down(KeyboardState keys, string action)
        => KeyBindingStore.Instance.Map.IsDownMostSpecific(keys, action);

    /// <summary>Cancel on flight input (desktop <c>CancelDirectorOnInput</c>).
    /// <paramref name="down"/> lets tests supply the action map without the
    /// on-disk binding store; production passes null.</summary>
    public bool CancelOnFlightInput(KeyboardState keys, Func<KeyboardState, string, bool>? down = null)
    {
        if (!Active) return false;
        bool pressed = down is null ? AnyFlightControl(keys) : Director.AnyFlightControl(
            forward: down(keys, "ThrustForward"), back: down(keys, "ThrustBackward"),
            strafeLeft: down(keys, "RollLeft"), strafeRight: down(keys, "RollRight"),
            thrustUp: down(keys, "ThrustLeft"), thrustDown: down(keys, "ThrustRight"),
            turnLeft: down(keys, "YawLeft"), turnRight: down(keys, "YawRight"));
        if (!pressed) return false;
        Stop();
        return true;
    }

    /// <summary>
    /// Advance the planner and produce this frame's camera pose. Mirrors the
    /// desktop's <c>UpdateDirectorPose</c> (<c>Game1.Director.cs:363-374</c>): the
    /// director's private free orientation is drifted by the desktop's gentle
    /// rates (0.04 pitch / 0.03 yaw rad/s, <c>:366-368</c>) and used as the
    /// backing axis. The caller's own-ship orientation is never touched.
    /// </summary>
    public (Matrix View, Matrix Proj, Vector3 Eye, bool ShotChanged) Update(
        double dt, DirectorSnapshot snapshot, float ownRadius, float hullLength, float aspect)
    {
        ExternalCameras.RotateRates(_camera, (float)dt, 0.04f, 0.03f, 0f);
        var backward = _camera.GetBackward();
        return _director.Update(dt, snapshot, new Vector3(backward.X, backward.Y, backward.Z),
            ownRadius, hullLength, aspect);
    }

    /// <summary>The HUD mode label for the bottom-left camera tag that both
    /// HUD styles draw (<c>Rendering/HudRenderer.cs:366-372</c>, inherited by
    /// <c>ModernHudRenderer.cs:123</c>).</summary>
    public const string HudLabel = "WATCH";

    private static Vector3 ToXna(SimVec value) => new(value.X, value.Y, value.Z);
    private static Vector3 Vec(float[] value) => value is { Length: 3 } ? new(value[0], value[1], value[2]) : Vector3.Zero;
}
