using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P464 — pure unit tests for <see cref="TurretLook"/>, the client-side
/// extraction of the server's turret-gunner facing integration
/// (<see cref="Allegiance.Core.Sim.Ship.ExecuteTurretMove"/>,
/// <c>shipIGC.cpp:1618-1702</c>). The decisive test proves the extracted math
/// agrees <em>numerically</em> with the real <see cref="Ship"/> method over a
/// grid of controls and deltas, so the client's local aim prediction and the
/// server's applied gunner facing cannot diverge. The behavioural tests pin the
/// slew-rate and circular-constraint semantics the port relies on.
/// </summary>
public sealed class TurretLookTests
{
    private static string ArtRoot =>
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static readonly Lazy<StaticCore> Core = new(() =>
        StaticCore.Load(IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, "PCore001.igc"))));

    private static Ship NewShip() => new(
        (short)7701, Core.Value.HullTypes.Values.First(),
        Vector3.Zero, Vector3.Zero, Orientation.Identity);

    /// <summary>The six scalars that pin an orientation (right/up/forward).</summary>
    private static (double rx, double ry, double rz, double ux, double uy, double uz) Basis(Orientation o)
    {
        var r = o.GetRight(); var u = o.GetUp(); var f = o.GetForward();
        return (r.X, r.Y, r.Z, u.X, u.Y, u.Z);
    }

    private static void AssertSame(Orientation a, Orientation b)
    {
        (double rx, double ry, double rz, double ux, double uy, double uz) = Basis(a);
        (double brx, double bry, double brz, double bux, double buy, double buz) = Basis(b);
        Assert.Equal(rx, brx, 5); Assert.Equal(ry, bry, 5); Assert.Equal(rz, brz, 5);
        Assert.Equal(ux, bux, 5); Assert.Equal(uy, buy, 5); Assert.Equal(uz, buz, 5);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, -1f)]        // identity neutral — no turn
    [InlineData(1f, 0f, 0f, 1f)]        // full yaw, full throttle
    [InlineData(0f, 1f, 0f, 1f)]        // full pitch
    [InlineData(0.5f, -0.3f, 0.2f, 0.7f)]
    [InlineData(-1f, 0f, 0f, 0f)]       // yaw left, zero throttle
    [InlineData(1f, 1f, 1f, 1f)]        // diagonal — circular constraint kicks in
    [InlineData(0.9f, 0.9f, 0f, -1f)]
    public void Matches_server_ExecuteTurretMove(float y, float p, float r, float thr)
    {
        var controls = new ControlData(y, p, r, thr);
        float dt = 0.083f;                                  // ~12 Hz frame
        var ship = NewShip();
        ship.Controls = controls;

        var viaServer = Orientation.Identity.Copy();
        ship.ExecuteTurretMove(0.0, dt, viaServer);         // start=0, end=dt → dT=dt

        var viaTurretLook = Orientation.Identity.Copy();
        TurretLook.Integrate(viaTurretLook, controls, dt);

        AssertSame(viaServer, viaTurretLook);
    }

    [Fact]
    public void Matches_server_over_many_deltas_from_a_yawed_start()
    {
        var ship = NewShip();
        var start = Orientation.Identity.Copy();
        start.Yaw(0.7f);
        ship.Controls = new ControlData(0.6f, 0.1f, 0f, 0.8f);

        foreach (float dt in new[] { 0.016f, 0.033f, 0.084f, 0.017f })
        {
            var viaServer = start.Copy();
            ship.ExecuteTurretMove(0.0, dt, viaServer);
            var viaTurretLook = start.Copy();
            TurretLook.Integrate(viaTurretLook, ship.Controls, dt);
            AssertSame(viaServer, viaTurretLook);
        }
    }

    [Fact]
    public void Zero_controls_do_not_change_the_orientation()
    {
        var o = Orientation.Identity.Copy();
        o.Yaw(0.5f);
        var before = o.Copy();
        TurretLook.Integrate(o, new ControlData(0f, 0f, 0f, 1f), 0.5f);
        AssertSame(before, o);
    }

    [Fact]
    public void Full_throttle_full_yaw_turns_at_max_slew_rate()
    {
        // yaw=1, throttle=1 → l=1, slew = min + 0.5(max-min) + 0.5(max-min)·1
        //                       = min + (max-min) = max = 75°/s = 1.309 rad/s.
        var o = Orientation.Identity.Copy();
        float dt = 1f;
        TurretLook.Integrate(o, new ControlData(1f, 0f, 0f, 1f), dt);
        var f = o.GetForward();                          // identity forward = (0,0,-1)
        // A yaw about +Y moves forward's X; angle swept = maxSlew·dt ≈ 1.309 rad.
        float swept = MathF.Acos(Math.Clamp(-f.Z, -1f, 1f));
        Assert.Equal(TurretLook.MaxSlewRate, swept, 3);
        Assert.Equal(-MathF.Sin(TurretLook.MaxSlewRate), f.X, 3);
    }

    [Fact]
    public void Diagonal_command_is_clamped_to_a_circle_not_a_box()
    {
        // The controls are constrained to the UNIT SPHERE: the total turn-rate
        // magnitude equals min(|controls|, 1) × maxSlew, never more. Measured on
        // a single axis (pure yaw, so the composed rotation angle is exact): a
        // command of magnitude 2 is clamped down to the same 1×maxSlew as a
        // magnitude-1 command, and a magnitude-0.5 command sweeps exactly half.
        float dt = 0.25f;
        float fullSweep = TurretLook.MaxSlewRate * dt;

        var one = Orientation.Identity.Copy();
        TurretLook.Integrate(one, new ControlData(1f, 0f, 0f, 1f), dt);
        Assert.Equal(fullSweep, RotationAngle(one), 4);

        var two = Orientation.Identity.Copy();
        TurretLook.Integrate(two, new ControlData(2f, 0f, 0f, 1f), dt);   // |2| > 1 → clamped
        Assert.Equal(fullSweep, RotationAngle(two), 4);
        Assert.Equal(RotationAngle(one), RotationAngle(two), 5);

        var half = Orientation.Identity.Copy();
        TurretLook.Integrate(half, new ControlData(0.5f, 0f, 0f, 1f), dt);
        Assert.Equal(0.5f * fullSweep, RotationAngle(half), 4);
    }

    /// <summary>The magnitude of a pure rotation, in radians, recovered from the
    /// rotation matrix's trace (θ = acos((tr(R) − 1)/2)).</summary>
    private static double RotationAngle(Orientation o)
    {
        double trace = o.GetRight().X + o.GetUp().Y + o.GetBackward().Z;
        return Math.Acos(Math.Clamp((trace - 1.0) / 2.0, -1.0, 1.0));
    }
}
