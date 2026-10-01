using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// One mounted weapon on the local ship, resolved against the loaded static
/// core (P462). The P462 echo is pure visual — it never affects damage, energy,
/// or ammo (those all stay server-authoritative). This is the minimal set of
/// fields the local tracer needs to reproduce the server's real round: the
/// weapon's cadence and energy/ammo cost, and its projectile's real speed /
/// lifespan / absolute-ness / dispersion.
/// </summary>
/// <param name="MountId">The fixed mount/hardpoint id this weapon occupies.</param>
/// <param name="ProjectileTypeId">The projectile type this weapon fires
/// (<c>DataWeaponTypeIGC.projectileTypeID</c>).</param>
/// <param name="DtimeBurst">Seconds between shots in a burst
/// (<c>GetDtBurst()</c> = <c>DataWeaponTypeIGC.dtimeBurst</c>).</param>
/// <param name="EnergyPerShot">Energy the weapon consumes per shot
/// (<c>dataWeapon.energyPerShot</c>).</param>
/// <param name="CAmmoPerShot">Ammo the weapon consumes per shot; 0 for an
/// energy weapon that never touches the magazine.</param>
/// <param name="Dispersion">Maximum random angular scatter of a shot around the
/// forward heading (<c>dataWeapon.dispersion</c>).</param>
/// <param name="ProjectileSpeed">The fired projectile's real speed
/// (<c>DataProjectileTypeIGC.speed</c>).</param>
/// <param name="ProjectileLifespan">The fired projectile's real lifespan in
/// seconds (<c>DataProjectileTypeIGC.lifespan</c>).</param>
/// <param name="AbsoluteF">True when the projectile ignores the launcher's
/// velocity (<c>DataProjectileTypeIGC.bAbsoluteF</c>).</param>
/// <param name="MinEnergyFraction">The floor the ship's last-known energy
/// <em>fraction</em> must clear for this weapon to fire — i.e.
/// <c>EnergyPerShot / effectiveMaxEnergy</c>, translated to the 0..1 wire
/// fraction by the caller (which owns the hull + side-GA resolution). 0 when
/// the weapon consumes no energy or the ship's max energy is unknowable, so the
/// gate is skipped.</param>
public readonly record struct OwnShotWeapon(
    int MountId,
    short ProjectileTypeId,
    float DtimeBurst,
    float EnergyPerShot,
    short CAmmoPerShot,
    float Dispersion,
    float ProjectileSpeed,
    float ProjectileLifespan,
    bool AbsoluteF,
    float MinEnergyFraction,
    // P465 — the weapon part's sound ids, carried so each emitted round (and the
    // per-mount burst loop) can play the right per-weapon wave via WeaponSoundMap.
    short SingleShotSoundId,
    short BurstSoundId);

/// <summary>
/// The per-frame fire selection the local pilot is actually sending to the
/// server this frame — the <c>stateM</c> fire bits (<c>AllWeapons</c>, or
/// <c>OneWeapon | SelectedWeaponMask</c>) exactly as <c>Weapon.IsWeaponSelected</c>
/// (<c>Weapon.cs:397-401</c>) reads them. 0 when the autopilot is driving
/// (which sends no fire) or the trigger is up. Encodes both group vs single
/// mode and the selected mount, so no separate selection input is needed.
/// </summary>
public readonly record struct OwnShotFireState(int FireStateM);

/// <summary>
/// The world-derived inputs the echo needs each frame. Pure data — the caller
/// (Game1) resolves our pose, velocity, and the last server-reported energy /
/// ammo fractions from the wire, exactly as the brief scopes: no local energy/
/// ammo bookkeeping beyond the single gate.
/// </summary>
/// <param name="Position">The local ship's render pose position (the pose the
/// renderer draws the own hull from — the P447 prediction seam; see the class
/// remarks).</param>
/// <param name="Orientation">The local ship's render pose orientation (same
/// seam).</param>
/// <param name="Velocity">The local ship's server-reported velocity (inherited
/// into each round when the projectile is not <c>AbsoluteF</c>).</param>
/// <param name="EnergyFraction">Last server-reported energy fraction (0..1),
/// <c>ServerShipUpdate.EnergyFraction</c>.</param>
/// <param name="AmmoFraction">Last server-reported ammo fraction (0..1),
/// <c>ServerShipUpdate.AmmoFraction</c>.</param>
/// <param name="NowSeconds">Monotonic seconds on the client clock (cadence /
/// first-shot-immediate timing).</param>
/// <param name="NowLocalMs">The same instant as <c>Environment.TickCount64</c>,
/// used to stamp each round so the renderer's <c>PositionAt</c> and the de-dup
/// echo window share one clock.</param>
public readonly record struct OwnShotContext(
    Vector3 Position,
    Orientation Orientation,
    Vector3 Velocity,
    float EnergyFraction,
    float AmmoFraction,
    double NowSeconds,
    long NowLocalMs);

/// <summary>
/// A single locally-emitted visual round. Everything the renderer needs to draw
/// it (creation position, velocity, heading, lifespan, emit instant) plus
/// everything the de-dup rule needs to recognise it when the server's own
/// <c>PROJECTILE_INSTANCE(12)</c> echo arrives (type id, spawn position, emit
/// instant). The same record serves both concerns.
/// </summary>
public readonly record struct LocalRound(
    short ProjectileTypeId,
    Vector3 Position,
    Vector3 Velocity,
    Vector3 Forward,
    float Lifespan,
    long EmittedLocalMs,
    // P465 — the firing weapon's sound ids + mount, so the caller can play the
    // right per-weapon single-shot cue and drive that mount's burst loop.
    int MountId,
    short SingleShotSoundId,
    short BurstSoundId);

/// <summary>
/// P462 — the client's own-shot visual echo. Native Allegiance fires the local
/// ship's shots <em>on the client</em>: every native client runs
/// <c>CshipIGC::Update</c> → <c>weapon->Update</c>
/// (<c>src/Igc/shipIGC.cpp:312-340</c>), which creates its projectiles locally
/// in <c>CweaponIGC::FireWeapon</c> (<c>src/Igc/weaponIGC.cpp:142-322</c>) —
/// so the player's own shot appears (and its sound plays) on the very frame the
/// trigger is pulled. This port's client is a pure renderer with no sim and
/// only sent controls, so it previously waited a full round trip for the server
/// echo. This class restores the native <em>feel</em> by emitting a local
/// visual tracer (and the fire one-shot) at the trigger frame, mirroring
/// <c>Weapon.FireWeapon</c>'s cadence (first shot immediate, then
/// <c>DtimeBurst</c>) and per-round physics (real projectile speed/lifespan,
/// inherited velocity when <c>!AbsoluteF</c>, dispersion).
///
/// <para>It is <b>visual only</b>: the emitted rounds never deal damage, never
/// consume energy/ammo client-side, and are dropped from the world model (the
/// server echo is de-duplicated) so the server remains the sole hit/ammo
/// authority. The only gate is a stop-emit when the last server-reported energy
/// can't cover <c>EnergyPerShot</c>, or ammo is empty for an ammo weapon — no
/// local bookkeeping beyond that.</para>
///
/// <para><b>P447 compatibility seam.</b> The echo reads <see cref="OwnShotContext.Position"/>
/// and <see cref="OwnShotContext.Orientation"/> from the caller, who supplies
/// them from the <b>single render pose the renderer draws the own hull from</b>
/// (Game1's <c>_poses[me]</c>, a <see cref="PredictedPose"/>). Once P447 adds
/// own-ship prediction, that render pose becomes the predicted one and the echo
/// automatically follows it — no change here. This task deliberately does not
/// implement any prediction.</para>
/// </summary>
/// <remarks>
/// <para><b>Cadence faithfulness.</b> Each fixed mount keeps its own
/// <c>nextFire</c> in seconds. On the rising edge of the trigger (no stored
/// <c>nextFire</c>) it fires immediately, then advances by <c>DtimeBurst</c> —
/// the same "first shot immediate, then cadence" the server's armed weapon uses
/// (<c>Weapon.Activate</c> schedules the first shot one burst-interval away,
/// but its <c>_nextFire</c> starts at <c>ship.LastUpdate</c> so the very first
/// press fires at once; here we reproduce the observable outcome: an immediate
/// first tracer). A held trigger that accumulates more than one cadence interval
/// (frame dt &gt; <c>DtimeBurst</c>, a very fast weapon) fires up to
/// <see cref="MaxBurstShots"/> rounds per frame to catch up, mirroring native's
/// burst loop (<c>weaponIGC.cpp:247-315</c>). Releasing the trigger clears the
/// mount's <c>nextFire</c> so the next press is again immediate.</para>
///
/// <para><b>Threading.</b> The emitted-round list is guarded by an internal lock:
/// <see cref="Update"/> (written on the main thread) and
/// <see cref="ShouldDropServerEcho"/>/<see cref="GetActiveRounds"/> (read on the
/// transport receive thread and the render thread respectively) all share it.</para>
/// </remarks>
public sealed class OwnShotEcho
{
    /// <summary>
    /// Ammo fraction at or below which an ammo weapon is treated as empty — the
    /// same epsilon <see cref="WeaponReloadTracker"/> uses for the "depleted"
    /// judgement (the wire fraction is a 0..240 byte, so 0.016 sits above
    /// quantisation noise while still meaning ~0).
    /// </summary>
    public const float EmptyFractionEpsilon = 0.016f;

    /// <summary>
    /// The bounded echo window: how long after a local round is emitted its
    /// server <c>PROJECTILE_INSTANCE</c> echo is still recognised as "our own
    /// shot" and dropped. Derivation: the server fires within ≤1 sim tick of
    /// receiving our controls (~50 ms @ 20 ups), and the echo returns over one
    /// network round trip (negligible same-host, &lt;150 ms realistic internet).
    /// 400 ms therefore covers tick + RTT with generous margin while staying far
    /// shorter than any round's visual lifespan, so a foreign round that merely
    /// happens to use the same projectile type near our position in a later tick
    /// is never mis-deduped. Named + documented so the heuristic is tunable.
    /// </summary>
    public const long EchoWindowMs = 400;

    /// <summary>
    /// Distance (world units) from our server-reported position within which an
    /// incoming projectile's spawn point is treated as "our own muzzle". Mirrors
    /// <c>ProjectileAmbientSoundCurve.FireOriginRadius</c> (the existing
    /// local-vs-other heuristic, since <c>PROJECTILE_INSTANCE</c> carries no
    /// launcher id). Kept as a private copy so this pure class has no dependency
    /// on the Audio namespace; the value is intentionally identical.
    /// </summary>
    public const float FireOriginRadius = 150f;

    /// <summary>Native's burst cap (<c>weaponIGC.cpp:245,312</c>: a burst never
    /// exceeds 10 shots per weapon per update).</summary>
    public const int MaxBurstShots = 10;

    /// <summary>
    /// Tiny tolerance on the cadence gate (seconds). <c>DtimeBurst</c> is a core
    /// float promoted to the double cadence clock; a shot advancing by
    /// <c>(double)0.2f</c> lands a hair (~3e-9 s) after the exact double 0.2, so
    /// a strict <c>now &gt;= nextFire</c> would let the next shot slip one frame.
    /// 1 µs absorbs the float→double promotion error with zero perceptual effect
    /// (cadences are ≫ 1 ms) and keeps the burst arithmetic exact.
    /// </summary>
    public const double CadenceEpsilon = 1e-6;

    private readonly object _lock = new();
    private readonly Dictionary<int, double> _nextFireByMount = new();
    private readonly List<LocalRound> _recent = new();

    /// <summary>Clear all per-mount cadence and emitted-round state (e.g. on a
    /// hull/loadout change or a reconnect).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _nextFireByMount.Clear();
            _recent.Clear();
        }
    }

    /// <summary>
    /// Advance the echo one frame and emit any local rounds due while the
    /// trigger is held. Returns the rounds emitted this frame (the caller plays
    /// one fire one-shot per round — exactly one sound per shot — and never
    /// needs to remember them beyond the internal recent list). Returns empty
    /// when the trigger is up, the mount is deselected, or the energy/ammo gate
    /// blocks.
    /// </summary>
    /// <param name="weapons">The local ship's mounted weapon parts resolved
    /// against the loaded core (see <see cref="OwnShotWeapon"/>).</param>
    /// <param name="fire">This frame's actual fire selection (the sent
    /// <c>stateM</c> fire bits; 0 when autopilot driving / trigger up).</param>
    /// <param name="ctx">This frame's pose, velocity, energy/ammo fractions and
    /// clocks.</param>
    public List<LocalRound> Update(IReadOnlyList<OwnShotWeapon> weapons, in OwnShotFireState fire, in OwnShotContext ctx)
    {
        lock (_lock)
        {
            Prune(ctx.NowLocalMs);

            bool allWeapons = (fire.FireStateM & ShipControlState.AllWeapons) != 0;
            bool oneWeapon = (fire.FireStateM & ShipControlState.OneWeapon) != 0;
            // The selected-mount comparison replicates Weapon.IsWeaponSelected
            // (Weapon.cs:397-401): the OneWeapon mode selects exactly the fixed
            // mount whose id equals the 2-bit selection encoded in stateM.
            int selectedMount = (fire.FireStateM & ShipControlState.SelectedWeaponMask)
                                >> ShipControlState.SelectedWeaponShift;

            var emitted = new List<LocalRound>(weapons.Count);
            foreach (var w in weapons)
            {
                bool selected = allWeapons || (oneWeapon && w.MountId == selectedMount);
                if (!selected)
                {
                    _nextFireByMount.Remove(w.MountId);
                    continue;
                }

                // Energy gate: the weapon consumes energy and the last-known
                // energy fraction can't cover a shot.
                if (w.MinEnergyFraction > 0f && ctx.EnergyFraction < w.MinEnergyFraction)
                {
                    _nextFireByMount.Remove(w.MountId);
                    continue;
                }
                // Ammo gate: an ammo weapon with an empty magazine.
                if (w.CAmmoPerShot != 0 && ctx.AmmoFraction <= EmptyFractionEpsilon)
                {
                    _nextFireByMount.Remove(w.MountId);
                    continue;
                }

                if (!_nextFireByMount.TryGetValue(w.MountId, out var nextFire))
                    nextFire = ctx.NowSeconds;   // first shot immediate on the rising edge

                int n = MaxBurstShots;
                while (n-- > 0 && ctx.NowSeconds >= nextFire - CadenceEpsilon)
                {
                    var round = BuildRound(w, ctx);
                    emitted.Add(round);
                    _recent.Add(round);
                    nextFire += w.DtimeBurst;
                }
                _nextFireByMount[w.MountId] = nextFire;
            }
            return emitted;
        }
    }

    /// <summary>
    /// The P462 de-dup predicate: drop an incoming server
    /// <c>PROJECTILE_INSTANCE</c> as "our own echo" when it matches a locally
    /// emitted round — same projectile type, spawned within
    /// <see cref="FireOriginRadius"/> of our server-reported position, and
    /// arrived within <see cref="EchoWindowMs"/>. Foreign rounds (different
    /// type, outside the radius, or outside the window) are kept; a round that
    /// doesn't match (we gated on energy/ammo but the server had enough, or the
    /// window passed) is <em>not</em> dropped, so the server copy renders — the
    /// documented fallback for a local/server disagreement. Invoked on the
    /// transport receive thread under the world gate.
    /// </summary>
    /// <param name="projectileTypeId">The incoming round's projectile type.</param>
    /// <param name="serverSpawnPos">The incoming round's spawn (muzzle) position.</param>
    /// <param name="serverPos">Our ship's server-reported position.</param>
    /// <param name="nowLocalMs"><c>Environment.TickCount64</c> at receipt.</param>
    public bool ShouldDropServerEcho(short projectileTypeId, Vector3 serverSpawnPos, Vector3 serverPos, long nowLocalMs)
    {
        lock (_lock)
        {
            Prune(nowLocalMs);
            foreach (var r in _recent)
            {
                if (r.ProjectileTypeId != projectileTypeId) continue;
                if (nowLocalMs - r.EmittedLocalMs > EchoWindowMs) continue;   // outside the echo window → keep
                if ((serverSpawnPos - serverPos).LengthSquared() > FireOriginRadius * FireOriginRadius) continue;
                return true;
            }
            return false;
        }
    }

    /// <summary>Snapshot of the currently-live local rounds (pruned by lifespan),
    /// for the renderer to draw through the projectile bolt path.</summary>
    public IReadOnlyList<LocalRound> GetActiveRounds(long nowLocalMs)
    {
        lock (_lock)
        {
            Prune(nowLocalMs);
            return _recent.ToArray();
        }
    }

    /// <summary>Build one round's physics: disperse the forward heading, apply
    /// the real projectile speed, inherit the launcher's velocity when the
    /// projectile is not absolute — the exact sequence of
    /// <c>weaponIGC.cpp:255-269</c> / <c>Weapon.cs:282-295</c>.</summary>
    private static LocalRound BuildRound(in OwnShotWeapon w, in OwnShotContext ctx)
    {
        Vector3 forward = ctx.Orientation.GetForward();
        if (w.Dispersion != 0f)
        {
            float r = NextRandom(0f, w.Dispersion);
            float a = NextRandom(0f, 2f * MathF.PI);
            forward += (r * MathF.Cos(a)) * ctx.Orientation.GetRight()
                     + (r * MathF.Sin(a)) * ctx.Orientation.GetUp();
            forward = forward.Normalize();
        }

        Vector3 velocity = w.ProjectileSpeed * forward;
        if (!w.AbsoluteF)
            velocity += ctx.Velocity;

        return new LocalRound(
            w.ProjectileTypeId,
            ctx.Position,        // muzzle = ship centre (no hull mesh → no muzzle offset)
            velocity,
            forward,
            w.ProjectileLifespan,
            ctx.NowLocalMs,
            w.MountId,
            w.SingleShotSoundId,
            w.BurstSoundId);
    }

    /// <summary>A uniform draw in [min, max) — the C++ <c>random(min,max)</c> used
    /// for dispersion (mirrors <c>Weapon.NextRandom</c>).</summary>
    private static float NextRandom(float min, float max) =>
        min + (float)(Random.Shared.NextDouble() * (max - min));

    /// <summary>Drop rounds no longer needed: past their visual lifespan OR past
    /// the echo window (whichever is longer), so a short-lived tracer still
    /// matches its echo within the window.</summary>
    private void Prune(long nowLocalMs) =>
        _recent.RemoveAll(r => nowLocalMs - r.EmittedLocalMs > RetainMs(r.Lifespan));

    private static long RetainMs(float lifespanSec) =>
        (long)Math.Ceiling(Math.Max(lifespanSec * 1000.0, EchoWindowMs));
}
