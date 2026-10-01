using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P448a golden test — <b>records the pre-refactor behaviour of
/// <see cref="Ship.ExecuteMove"/> and locks it</b>, proving the extraction of
/// <see cref="ShipKinematics.Step"/> changed nothing. The expected values were
/// captured by running this exact scripted sequence against the <b>original
/// inline <c>ExecuteShipMove</c> port</b> (the pre-P448a body, verified via
/// <c>git stash</c> of the delegation) and hard-coding its output below.
/// <see cref="Ship.ExecuteMove"/> now delegates to the pure
/// <see cref="ShipKinematics.Step"/>; if that delegation drifts a single
/// mantissa bit from the original inline algorithm, these exact-equality
/// asserts fail. Because the ship's kinematics state (velocity, orientation,
/// turn rates) accumulates across every step, these constants are sensitive to
/// any behavioural divergence, not just a first-step difference.
/// </summary>
/// <remarks>
/// The four scenarios span every engine branch of <c>ExecuteShipMove</c>:
/// throttle cruise (turning climb), manual strafe (move buttons), coast /
/// vector lock, and pure turn with idle throttle. The hull is the same
/// synthetic pinned <c>DemoHull</c> <c>BotFlightTests</c> uses, so the test is
/// deterministic and needs no artwork corpus. Exact (<c>Assert.Equal</c> on
/// <see cref="Vector3"/> record equality and <c>float</c> on rates) — the same
/// no-tolerance style <c>ShipFlightModelTests.Identical_inputs...</c> uses.
/// </remarks>
public class ShipKinematicsGoldenTests
{
    private static readonly DataHullTypeIGC DemoHull = new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 535f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.666f, 4.444f, 5.022f],
        TurnTorques: [5100f, 4900f, 4700f],
        Thrust: 52800f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 950f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 509, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 515f, HardpointOffset: 563,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static Ship NewShip()
    {
        var s = new Ship(shipId: 907, DemoHull, new Vector3(120f, -40f, 370f), new Vector3(9.5f, -2.5f, -31.0f), Orientation.Identity);
        s.Orientation.SetForward(new Vector3(0.3512f, -0.9063f, -0.2345f).Normalize());
        s.SetTurnRate(0, 0.7712f);
        s.SetTurnRate(1, -0.4921f);
        s.SetTurnRate(2, 0.6011f);
        return s;
    }

    [Fact]
    public void TurningClimb_Golden_Matches_PreRefactorInline()
    {
        var s = NewShip();
        s.Controls = new ControlData(Yaw: 0.6341f, Pitch: -0.8117f, Roll: 0.0993f, Throttle: 0.5876f);
        s.StateM = 0;
        for (int i = 0; i < 24; i++) s.ExecuteMove(0.015625 * i, 0.015625 * (i + 1));
        AssertEqualGoldens(s, "S1",
            vel: new Vector3(13.865166f, -25.163467f, -38.134647f),
            fwd: new Vector3(0.15149914f, -0.28426996f, -0.94669884f),
            up: new Vector3(0.44654983f, 0.874131f, -0.19101878f),
            right: new Vector3(0.8818397f, -0.39380905f, 0.2593708f),
            rates: [2.8592246f, -3.0264812f, 0.48191637f]);
    }

    [Fact]
    public void ManualStrafe_Golden_Matches_PreRefactorInline()
    {
        var s = NewShip();
        s.Controls = new ControlData(0f, 0f, 0f, 0f);
        s.StateM = ShipControlState.ForwardButton | ShipControlState.RightButton | ShipControlState.DownButton;
        for (int i = 0; i < 24; i++) s.ExecuteMove(0.01875 * i, 0.01875 * (i + 1));
        AssertEqualGoldens(s, "S2",
            vel: new Vector3(13.628472f, -48.965687f, 16.665167f),
            fwd: new Vector3(0.33319142f, -0.905857f, -0.26154652f),
            up: new Vector3(0.7292811f, 0.423423f, -0.5374589f),
            right: new Vector3(0.5976057f, -0.011664241f, 0.80170524f),
            rates: [0f, 0f, 0f]);
    }

    [Fact]
    public void Coast_Golden_Matches_PreRefactorInline()
    {
        var s = NewShip();
        s.Controls = new ControlData(Yaw: 0.9021f, Pitch: 0.0411f, Roll: -0.7770f, Throttle: 0.9123f);
        s.StateM = ShipControlState.CoastButton;
        for (int i = 0; i < 24; i++) s.ExecuteMove(0.0215 * i, 0.0215 * (i + 1));
        AssertEqualGoldens(s, "S3",
            vel: new Vector3(9.5f, -2.5f, -31f),
            fwd: new Vector3(-0.3154446f, -0.2199296f, -0.92310655f),
            up: new Vector3(0.94847214f, -0.042398468f, -0.31401113f),
            right: new Vector3(0.029922038f, -0.97459394f, 0.22197147f),
            rates: [3.5332735f, 0.15331818f, -2.631959f]);
    }

    [Fact]
    public void PureTurn_Golden_Matches_PreRefactorInline()
    {
        var s = NewShip();
        s.Controls = new ControlData(Yaw: 1f, Pitch: 0f, Roll: 0f, Throttle: -1f);
        s.StateM = 0;
        for (int i = 0; i < 24; i++) s.ExecuteMove(0.0133 * i, 0.0133 * (i + 1));
        AssertEqualGoldens(s, "S4",
            vel: new Vector3(6.855571E-28f, -3.4277855E-28f, 6.855571E-28f),
            fwd: new Vector3(0.01388037f, -0.79209876f, -0.6102353f),
            up: new Vector3(0.7261856f, 0.42751688f, -0.5384085f),
            right: new Vector3(0.68735856f, -0.43567073f, 0.5811448f),
            rates: [2.3922126f, 0f, 0f]);
    }

    private static void AssertEqualGoldens(Ship s, string tag, Vector3 vel, Vector3 fwd, Vector3 up, Vector3 right, float[] rates)
    {
        var o = s.Orientation;
        Assert.Equal(vel, s.Velocity);                       // Vector3 record equality — exact
        Assert.Equal(fwd, o.GetForward());
        Assert.Equal(up, o.GetUp());
        Assert.Equal(right, o.GetRight());
        var tr = s.GetTurnRates(new float[3]);
        Assert.Equal(rates[0], tr[0]);
        Assert.Equal(rates[1], tr[1]);
        Assert.Equal(rates[2], tr[2]);
    }
}
