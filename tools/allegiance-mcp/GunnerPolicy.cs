using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P814 — pure, data-driven turret-gunner policy and aim/lead math, shared by
/// the bot integration (<see cref="BotManager"/>) and the unit tests. Nothing
/// here touches a live connection or a server: it is the decision surface a
/// bot gunner runs on, so it can be proved without the rig.
///
/// Behaviour parity with native is the contract:
/// <list type="bullet">
/// <item><b>Who may serve.</b> Native crewing is seat-based — any player may
/// board a carrier's EMPTY, EQUIPPED turret slot (<c>BoardShip</c>,
/// FedSrv.CPP:2347-2402; the slot must have a mounted part, :2377-2392). This
/// policy narrows the BOTS' choice (fighters pool) so the side never starves
/// its economy or recon — a data-driven overlay, race-agnostic, per
/// item 4. It never relaxes a native gate.</item>
/// <item><b>Seat capacity.</b> A carrier's turret seats = its turret mounts
/// (<c>MaxWeapons - MaxFixedWeapons</c>, the same count native's client uses
/// to report free turrets, teleportpane.cpp:520-531). A gunner occupies a seat
/// when parented in the TURRET state (FedSrv.CPP:2450, c_ssTurret).</item>
/// <item><b>Aim/lead.</b> Lead comes from the shared Core
/// <see cref="ShipDodgeLaw.SolveForImpact"/> (the faithful port of native's
/// universal <c>SolveForImpact</c>, common.cpp:1692-1790) — the same solver
/// native's wingman uses for <c>solveForLead</c> (shipIGC.cpp:2463-2468,
/// <see cref="Ship.SolveForLead"/>). The turret slews toward the lead point at
/// native's <c>ExecuteTurretMove</c> slew rates (<c>c_minRate</c>/<c>c_maxRate</c>,
/// shipIGC.cpp:1640-1641) via <see cref="Orientation.TurnTo(Vector3,float)"/>.</item>
/// </list>
/// </summary>
internal static class GunnerPolicy
{
    /// <summary>P814 — the tunables. Data-driven and race-agnostic: nothing here
    /// names a civ, a hull or a station. Defaults are the sane showcase values
    /// (item 4c — gunners are ON by default, not a flagged experiment).</summary>
    internal sealed record Tuning(
        bool Enable = true,
        // The bounded gather window a turreted carrier holds its launch while
        // docked teammates board (item 4a, default ~15-20 s). P814b raised the
        // default and made gunners park (below) so the gather and the boarding
        // reliably overlap in a live game (scope 3).
        double GatherSeconds = 28,
        // P814b scope 3 — how long a docked gunner-eligible fighter parks before
        // launching when a turret carrier is docked-and-gathering at ITS station
        // but has no free seat visible yet (hull refresh / seats filling). A
        // fighter that saw no carrier a moment ago otherwise insta-launches past
        // the carrier's whole gather window and never boards.
        double BoardParkSeconds = 12,
        // Cap on gunners a single carrier will actively recruit (the server's
        // free-seat ceiling binds regardless; this bounds the bot-side push).
        int MaxGunnersPerCarrier = 2,
        // Never pull the side below this many free (non-boarded) pilots —
        // "never starve the economy roles" / "side's minimum free pilots".
        int MinFreePilots = 2,
        // A docked gunner's bounded wait for its board to reflect or NACK before
        // it falls back to launching its own ship (item 2: "on BOARD_NACK the
        // gunner falls back to flying").
        double BoardConfirmSeconds = 3.0,
        // Native turret slew floor/ceiling (rad/s), shipIGC.cpp:1640-1641.
        float MinSlewRate = 7.5f * MathF.PI / 180.0f,
        float MaxSlewRate = 75.0f * MathF.PI / 180.0f,
        // Fire only when the turret has slewed to within this angle (rad) of the
        // lead point AND the target is inside the turret weapon's range.
        float FireAngleRad = 0.088f);

    private static volatile Tuning? _cached;
    internal static Tuning Effective
    {
        get
        {
            var t = _cached;
            if (t is null)
            {
                var env = Environment.GetEnvironmentVariable("ALLEGIANCE_GUNNERS");
                // Default OFF (Domingo, 2026-10-24): with gunners on, the P745 finale
                // did not kill a station in review; "on"/"1"/"true" enables the flow,
                // the turret-bomber preference and the gunship slot.
                bool enabled = env is not null && (env.Equals("on", StringComparison.OrdinalIgnoreCase)
                    || env == "1" || env.Equals("true", StringComparison.OrdinalIgnoreCase));
                double gather = ParseDouble("ALLEGIANCE_GUNNER_GATHER_SECONDS", 28);
                int cap = ParseInt("ALLEGIANCE_GUNNER_MAX_PER_CARRIER", 2);
                int minFree = ParseInt("ALLEGIANCE_GUNNER_MIN_FREE_PILOTS", 2);
                double park = ParseDouble("ALLEGIANCE_GUNNER_BOARD_PARK_SECONDS", 12);
                t = new Tuning(Enable: enabled, GatherSeconds: gather,
                    MaxGunnersPerCarrier: cap, MinFreePilots: minFree, BoardParkSeconds: park);
                _cached = t;
            }
            return t;
        }
    }
    internal static void ResetCache() => _cached = null;

    private static double ParseDouble(string name, double dflt)
        => double.TryParse(Environment.GetEnvironmentVariable(name),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            && v > 0 ? v : dflt;
    private static int ParseInt(string name, int dflt)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v >= 0 ? v : dflt;

    // ── who serves / who hosts ────────────────────────────────────────────

    /// <summary>Item 4 — the roles that HOST turret gunners: strike/finale
    /// bombers and gunships/carriers (bombers + heavy capital). A role never
    /// hosts unless its hull is actually turret-capable (checked by the caller
    /// against the real hull).</summary>
    internal static bool CanHost(Showcase.HullRole role) =>
        role is Showcase.HullRole.Bomber or Showcase.HullRole.Heavy;

    /// <summary>Item 4 — the roles that may SERVE as a gunner (the side's
    /// fighter pool). Economy/recon/support and a strike's own bomber never
    /// climb into a turret (miners/constructors/economy, a scout on recon, a
    /// strike's own bomber — the native-esque carve-outs of item 4a).</summary>
    internal static bool CanServeAsGunner(Showcase.HullRole role) =>
        role is Showcase.HullRole.Fighter or Showcase.HullRole.Interceptor;

    /// <summary>Item 4a — whether this docked bot should prefer to board a
    /// teammate carrier instead of launching its own ship. Gated on the side
    /// keeping <see cref="Tuning.MinFreePilots"/> free pilots (never starve the
    /// economy) and the bot's role allowing gunner service.</summary>
    internal static bool ShouldSeekBoard(Showcase.HullRole myRole, int sideFreePilots, Tuning t)
        => t.Enable && CanServeAsGunner(myRole) && sideFreePilots > t.MinFreePilots;

    /// <summary>Item 2 — how many turret seats a carrier hull has
    /// (<c>MaxWeapons - MaxFixedWeapons</c>, teleportpane.cpp:520-531). 0 = not
    /// a turret carrier.</summary>
    internal static int TurretCapacity(DataHullTypeIGC hull)
        => Math.Max(0, hull.MaxWeapons - hull.MaxFixedWeapons);

    /// <summary>Item 2 — occupied turret seats on a carrier: its live children
    /// in the TURRET state (FedSrv.CPP:2450 c_ssTurret; the port's free-seat
    /// counter is the same definition, ClientWorld.Apply(BoardNack)).</summary>
    internal static int OccupiedGunners(IEnumerable<int> occupiedParentedTurretStates)
        => occupiedParentedTurretStates.Count();

    /// <summary>Item 2 — free turret seats left on a carrier.</summary>
    internal static int FreeSeats(int capacity, int occupied) => Math.Max(0, capacity - occupied);

    /// <summary>Item 4 — the cap a carrier will actively recruit (bounded by
    /// the real free-seat count, which the caller takes the min of).</summary>
    internal static int RecruitLimit(int freeSeats, Tuning t)
        => Math.Min(freeSeats, Math.Max(1, t.MaxGunnersPerCarrier));

    // ── aim / lead (shared Core math) ─────────────────────────────────────

    /// <summary>Shared-Core lead: solve the intercept direction from the
    /// shooter (carrier) to where the target will be, given the projectile
    /// speed (<c>SolveForImpact</c>, common.cpp:1692-1790; the solver native's
    /// wingman <c>solveForLead</c> uses, shipIGC.cpp:2463-2468). Returns the
    /// aim direction (unit) and intercept time; false when no valid intercept
    /// exists (target moving away faster than the shot / already receding).</summary>
    internal static bool TryLead(Vector3 shooterPos, Vector3 shooterVel,
        Vector3 targetPos, Vector3 targetVel, float projSpeed,
        out Vector3 aimDir, out float interceptTime)
    {
        aimDir = default;
        interceptTime = 0f;
        if (projSpeed <= 0f) return false;
        Vector3 deltaP = targetPos - shooterPos;
        Vector3 deltaV = targetVel - shooterVel;
        float t = ShipDodgeLaw.SolveForImpact(deltaP, deltaV, projSpeed, 0f, out aimDir);
        if (t <= 0f || t > 1e6f) return false;
        interceptTime = t;
        return aimDir.LengthSquared() > 1e-6f;
    }

    /// <summary>Slew the turret orientation toward <paramref name="aimDir"/>
    /// (world frame, any length) by at most the native turret slew rate over
    /// <paramref name="dt"/> seconds, returning the remaining angle (rad).
    /// Pure wrapper over <see cref="Orientation.TurnTo(Vector3,float)"/> —
    /// identical convergence to the server's <c>ExecuteTurretMove</c> catch-up
    /// (shipIGC.cpp:1618-1702). Mutates <paramref name="orientation"/>.</summary>
    internal static float SlewToward(Orientation orientation, Vector3 aimDir, float dt, Tuning t)
        => orientation.TurnTo(aimDir, t.MaxSlewRate * dt);

    /// <summary>Derive the joystick axes that continue slewing the turret toward
    /// <paramref name="aimDir"/> at full rate (throttle +1, native turret look
    /// always runs at full slew, clintlib.cpp:1870-1905). Expressed in the
    /// turret's own local frame like <c>ExecuteTurretMove</c> consumes them.</summary>
    internal static ControlData AimControls(Orientation orientation, Vector3 aimDir)
    {
        // Local-frame components of the aim direction: right/up/forward dot.
        Vector3 right = orientation.GetRight();
        Vector3 up = orientation.GetUp();
        float lx = right * aimDir;
        float ly = up * aimDir;
        float mag = MathF.Max(1e-4f, MathF.Sqrt(lx * lx + ly * ly));
        // Full-deflection toward the target's local offset, scaled to unit so
        // ExecuteTurretMove's circle constraint is respected (shipIGC.cpp:1627-1634).
        return new ControlData(Math.Clamp(lx / mag, -1f, 1f),
                               Math.Clamp(-ly / mag, -1f, 1f), 0f, 1f);
    }

    /// <summary>The native fire gate for a turret gunner: the weapon is selected
    /// (fires) when the gunner holds fire AND the turret is on target; we tighten
    /// to "slewed within <see cref="Tuning.FireAngleRad"/> of the lead point AND
    /// the target inside the weapon's range" so the bot doesn't waste shots while
    /// sweeping. (Native has no separate range check at selection — the weapon's
    /// own <c>IsWeaponSelected</c>, weaponIGC.h:236-254, only needs the gunner +
    /// weaponsMask; range is enforced by projectile lifespan at fire.)</summary>
    internal static bool FireGate(float remainingAngle, float distToTarget, float weaponRange, Tuning t)
        => remainingAngle <= t.FireAngleRad && distToTarget <= weaponRange;
}
