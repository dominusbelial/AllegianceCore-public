using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// The base for things that live in a sector and tick — the port of the C++
/// <c>ImodelIGC</c>/<c>CmodelIGC</c> abstraction (<c>src/Igc/modelIGC.h:22</c>),
/// deliberately kept to what this slice needs: an id, a position, and the
/// per-tick <see cref="Update"/>. <see cref="Ship"/> is the only concrete
/// model so far; stations, asteroids, treasures etc. join later and reuse this
/// base rather than a bespoke one-off (task rule: no class hierarchy for
/// objects that do not exist yet).
/// </summary>
/// <remarks>
/// The base owns the per-model clock and the guard, mirroring the C++: both
/// <c>CmodelIGC::Update</c> (<c>modelIGC.cpp:79-80</c>) and
/// <c>CshipIGC::Update</c> (<c>shipIGC.cpp:271-275</c>) recompute
/// <c>dt = now − m_lastUpdate</c> and only act when <c>now &gt; m_lastUpdate</c>.
/// <see cref="Update"/> therefore enforces the forward-clock guard and hands
/// the derived model its elapsed <c>dt</c>, then stores <c>now</c> as the new
/// last-update — exactly the C++ sequence. A backwards or repeated <c>now</c>
/// is a no-op (the C++ <c>if (now &gt; m_lastUpdate)</c> skips the whole body).
/// </remarks>
public abstract class Model : IModelIgc, ISideVisible
{
    private double _lastUpdate;

    /// <summary>Construct a model in a sector with the given id and position.</summary>
    protected Model(short id, Vector3 position)
    {
        Id = id;
        Position = position;
    }

    /// <summary>The per-side visibility state of this model — the port's host
    /// of the native <c>ThingSiteImpl::m_rgSideVisibility</c>
    /// (<c>FedSrv.CPP:11172</c>). Written by the sector's per-tick cross-side
    /// visibility sweep (<see cref="Sector.ResolveCrossSideVisibility"/>, P132)
    /// and read through <see cref="SeenBySide"/> / <see cref="GetCurrentEye"/>.
    /// A model whose side-visibility is never swept reads all-false (a fresh
    /// native array entry), so non-participating models are unaffected.</summary>
    public SideVisibilityState SideVisibility { get; } = new();

    /// <summary>Whether <paramref name="sideId"/> can currently see this model
    /// (<c>modelIGC.h:110-114</c>) — reads the live
    /// <see cref="SideVisibilityState"/> the sweep maintains.</summary>
    public bool SeenBySide(short sideId) => SideVisibility.SeenBySide(sideId);

    /// <summary>Whether <paramref name="sideId"/> is currently the eye on this
    /// model (<c>modelIGC.h:120-124</c>) — reads the live
    /// <see cref="SideVisibilityState"/> the sweep maintains.</summary>
    public bool GetCurrentEye(short sideId) => SideVisibility.GetCurrentEye(sideId);

    /// <summary>The model's object id.</summary>
    public short Id { get; }

    /// <summary>
    /// The model's last-updated time (the C++ <c>m_lastUpdate</c>, guarded
    /// and advanced by <see cref="Update"/>). 0 before the first forward
    /// <see cref="Update"/>. Exposed read-only so the combat slice can honour
    /// the C++ <c>FireWeapon</c> clamp "never fire retroactively"
    /// (<c>weaponIGC.cpp:154</c>), which reads the firing ship's
    /// <c>GetLastUpdate()</c>.
    /// </summary>
    public double LastUpdate => _lastUpdate;

    /// <summary>The model's position in its sector (world coords).</summary>
    public Vector3 Position { get; set; }

    /// <summary>
    /// An optional collision hull. When set, the model participates in
    /// collision detection via <see cref="CreateCollider"/> — the slice that
    /// reads the parsed <c>.cvh</c> hulls. <c>null</c> by default (no body).
    /// </summary>
    public CvhFile? Hull { get; set; }

    /// <summary>The velocity a collider built from this model should use (zero for a static body; <see cref="Ship"/> overrides with its velocity).</summary>
    public virtual Vector3 CollisionVelocity => Vector3.Zero;

    /// <summary>The orientation a collider built from this model should use (<see cref="Ship"/> overrides with its attitude).</summary>
    public virtual Orientation CollisionOrientation => Orientation.Identity;

    /// <summary>Whether a collider built from this model is static (its swept AABB does not travel).</summary>
    public virtual bool CollisionStatic => false;

    /// <summary>
    /// Build a collision body from this model's position/velocity/orientation and
    /// <see cref="Hull"/>. Returns <c>null</c> when the model has no hull (nothing
    /// to collide with) — matching the C++ <c>HitTest::Create</c> failing to find a
    /// <c>.cvh</c> and falling back to a primitive, which here is the caller's
    /// choice of radius/fallback shape.
    /// </summary>
    public Collider? CreateCollider(float desiredRadius, int fallbackShape = HitShape.Sphere)
        => Hull is null ? null
            : new Collider(this, Position, CollisionVelocity, CollisionOrientation, Hull, desiredRadius, CollisionStatic, fallbackShape);

    /// <summary>
    /// Seed the model's clock to <paramref name="now"/> <em>without</em> running
    /// <see cref="OnUpdate"/>. Used when a model is placed into a live sector
    /// mid-simulation so its first <see cref="Update"/> is not one oversized step
    /// equal to the whole mission age — the latent bug recorded in BOT.md (a ship
    /// placed at sim-time 120 s would otherwise take a 120-second first <c>dt</c>
    /// and, with non-neutral controls, teleport across the sector). Mirrors the
    /// C++ <c>CreateObject(now, ...)</c> path, which initialises
    /// <c>m_lastUpdate</c> to the creation time (<c>modelIGC.cpp:117</c>). Once
    /// seeded, subsequent <see cref="Update"/> calls behave exactly as before.
    /// </summary>
    public void InitClock(double now) => _lastUpdate = now;

    /// <summary>
    /// Tick the model to <paramref name="now"/> (seconds). Enforces the C++
    /// forward-clock guard (<c>now &gt; m_lastUpdate</c>): a backwards or
    /// unchanged <c>now</c> is a no-op and leaves the model untouched. Derived
    /// models do their per-tick work in <see cref="OnUpdate"/>.
    /// </summary>
    public void Update(double now)
    {
        if (now <= _lastUpdate) return;       // CmodelIGC/CshipIGC guard (modelIGC.cpp:79, shipIGC.cpp:271)
        double prev = _lastUpdate;
        OnUpdate(now, now - prev);            // dt handed to the derived model
        _lastUpdate = now;
    }

    /// <summary>
    /// Perform one tick's work for an elapsed <paramref name="dt"/> ending at
    /// <paramref name="now"/>. Called by <see cref="Update"/> only when the
    /// clock advanced; derived models implement their physics here.
    /// </summary>
    protected abstract void OnUpdate(double now, double dt);
}
