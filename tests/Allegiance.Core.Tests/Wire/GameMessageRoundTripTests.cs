using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the three ported game messages
/// (<c>SET_CLUSTER</c> 7, <c>SHIP_UPDATE</c> 24, <c>SINGLE_SHIP_UPDATE</c> 78)
/// and the two compact payloads they embed. Round-trip exactness is by
/// construction (the payloads hold the canonical compact bytes); these tests
/// pin the sizes, the re-quantization fixed point, and the byte layout against
/// hand-composed buffers derived from BOT.md / igc.h (not from the encoder).
/// </summary>
public class GameMessageRoundTripTests
{
    private static DataHullTypeIGC TestHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 501, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 540,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static Ship TestShip() => new(
        shipId: 42,
        hull: TestHull(),
        position: new Vector3(1000f, -2000f, 3000f),
        velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    [Fact]
    public void MessageSizes_AreTheSpecifiedFixedSizes()
    {
        Assert.Equal(56, SetCluster.FixedSize);
        Assert.Equal(48, ShipUpdate.FixedSize); // corrected P33: real client sends 48, no 4-byte align pad (see ShipUpdate.cs doc comment)
        Assert.Equal(56, SingleShipUpdate.FixedSize);
        Assert.Equal(47, ServerShipUpdate.Width);
        Assert.Equal(39, ClientShipUpdate.Width);
    }

    [Fact]
    public void ServerShipUpdate_FromModel_RoundTripsThroughWire()
    {
        var s = TestShip();
        s.Controls = new ControlData(0.5f, -0.25f, 1f, 1f);
        var u = ServerShipUpdate.FromModel(s.Id, s, time: 54321u);

        // encode → decode must yield the identical canonical rich state
        var buf = new byte[ServerShipUpdate.Width];
        WireWriter w = new(buf);
        u.EncodeBody(ref w);
        WireReader r = new(buf);
        var d = ServerShipUpdate.DecodeBody(ref r);

        Assert.Equal(u.ShipId, d.ShipId);
        Assert.Equal(u.StateM, d.StateM);
        Assert.Equal(u.Time, d.Time);
        Assert.Equal(u.Position, d.Position);
        Assert.Equal(u.Velocity, d.Velocity);
        Assert.Equal(u.HullFraction, d.HullFraction);
        Assert.Equal(u.Power, d.Power);
        // quantized controls / turn rates / orientation decode to the canonical form
        Assert.Equal(u.Controls, d.Controls);
        Assert.Equal(u.TurnRates, d.TurnRates);
        Assert.Equal(u.Orientation.GetForward(), d.Orientation.GetForward());
    }

    [Fact]
    public void ServerShipUpdate_PreservesFullPrecisionPosition()
    {
        var s = TestShip();
        var u = ServerShipUpdate.FromModel(s.Id, s, 0u);
        Assert.Equal(new Vector3(1000f, -2000f, 3000f), u.Position); // raw floats survive
    }

    [Fact]
    public void SetCluster_RoundTrips()
    {
        var s = TestShip();
        var msg = new SetCluster
        {
            SectorId = 7,
            Ship = ServerShipUpdate.FromModel(s.Id, s, 99u),
            Cookie = 0x2A,
        };
        var d = SetCluster.Decode(msg.Encode());
        Assert.Equal(7, d.SectorId);
        Assert.Equal(0x2A, d.Cookie);
        Assert.Equal(s.Id, d.Ship.ShipId);
        Assert.Equal(99u, d.Ship.Time);
        Assert.Equal(new Vector3(1000f, -2000f, 3000f), d.Ship.Position);
    }

    [Fact]
    public void ShipUpdate_RoundTrips_ControlsAndState()
    {
        var controls = new ControlData(1f, 0.5f, -1f, 1f);   // yaw 1, pitch 0.5, roll -1, throttle 1 (full fwd)
        int state = ShipControlState.ForwardButton;
        var msg = new ShipUpdate
        {
            Time = 5050u,
            Cookie = 3,
            Payload = ClientShipUpdate.FromControls(5050u, state, controls),
        };
        var d = ShipUpdate.Decode(msg.Encode());
        Assert.Equal(5050u, d.Time);
        Assert.Equal(3, d.Cookie);
        Assert.Equal(state, d.Payload.StateM);
        // controls are compact (lossy); compare within 1/127 quantization resolution
        Assert.Equal(controls.Yaw, d.Payload.Controls.Yaw, precision: 2);
        Assert.Equal(controls.Pitch, d.Payload.Controls.Pitch, precision: 2);
        Assert.Equal(controls.Roll, d.Payload.Controls.Roll, precision: 2);
        Assert.Equal(controls.Throttle, d.Payload.Controls.Throttle, precision: 2);
    }

    [Fact]
    public void ShipUpdate_ThrottleOne_QuantizesToOne()
    {
        var controls = new ControlData(0f, 0f, 0f, 1f);
        var msg = new ShipUpdate { Time = 1u, Cookie = 0, Payload = ClientShipUpdate.FromControls(1u, 0, controls) };
        var d = ShipUpdate.Decode(msg.Encode());
        Assert.Equal(1f, d.Payload.Controls.Throttle, precision: 2);
        // 127/127 == 1.0 exactly (floor(1*127+0.5)=127)
        Assert.Equal(1f, d.Payload.Controls.Throttle);
    }

    [Fact]
    public void SingleShipUpdate_RoundTrips()
    {
        var s = TestShip();
        var msg = new SingleShipUpdate
        {
            OtTarget = 0,             // OT_ship
            OidTarget = s.Id,
            Ship = ServerShipUpdate.FromModel(s.Id, s, 77u),
            BIsRipcording = false,
        };
        var d = SingleShipUpdate.Decode(msg.Encode());
        Assert.Equal(0, d.OtTarget);
        Assert.Equal(s.Id, d.OidTarget);
        Assert.Equal(77u, d.Ship.Time);
        Assert.False(d.BIsRipcording);
    }

    // --- byte-level layout of SET_CLUSTER (hand-composed from BOT.md) --------

    [Fact]
    public void SetCluster_EmitsExactDeclaredBytes()
    {
        var s = TestShip();
        s.Position = new Vector3(1f, 2f, 3f);
        s.Controls = new ControlData(0f, 0f, 0f, 1f);   // full forward throttle
        var msg = new SetCluster
        {
            SectorId = 9,
            Ship = ServerShipUpdate.FromModel(s.Id, s, time: 0x11223344u),
            Cookie = 0x7E,
        };
        var got = msg.Encode();

        var exp = new byte[56];
        int p = 0;
        void u16(ushort v) { exp[p++] = (byte)(v & 0xFF); exp[p++] = (byte)(v >> 8); }
        void u32(uint v) { exp[p++] = (byte)v; exp[p++] = (byte)(v >> 8); exp[p++] = (byte)(v >> 16); exp[p++] = (byte)(v >> 24); }
        void i16(short v) { u16(unchecked((ushort)v)); }
        void f32(float v) { foreach (var b in BitConverter.GetBytes(v)) exp[p++] = b; }
        void ff(byte v) { exp[p++] = v; }

        u16(56);        // cbmsg
        u16(7);         // fmid
        i16(9);         // sectorID
        p += 2;         // pad (2) to align shipupdate at 8
        i16(s.Id);      // shipID
        u16(0);         // stateM (0)
        ff(0); ff(0); ff(0); ff(0x7F);  // controls: yaw 0, pitch 0, roll 0, throttle +1 → 127
        u32(0x11223344); // time
        f32(1f); f32(2f); f32(3f);  // position
        ff(0xFF); ff(0xFF); ff(0); ff(0); ff(0); ff(0); ff(0); ff(0); // orientation: identity quaternion qa=65535, qx=qy=qz=0
        p += 5;         // velocity compact
        p += 3;         // turnRates compact
        ff(240); ff(0); ff(240); ff(240); ff(240); ff(0); // fractions hull,shield,ammo,fuel,energy,ore (BytePercentage Divisor=240)
        ff(240);        // power
        ff(0x7E);       // cookie
        Assert.Equal(56, p);

        Assert.Equal(exp, got);
    }

    // --- quantization fidelity ----------------------------------------------

    [Fact]
    public void ThrottleFullForward_EncodesTo127_AndZeroTo0()
    {
        Assert.Equal(127, CompactQuantViaSbyte(1f));
        Assert.Equal(0, CompactQuantViaSbyte(0f));
        Assert.Equal(-127, CompactQuantViaSbyte(-1f));
        static int CompactQuantViaSbyte(float v)
        {
            // decode through the wire like a client would
            var u = ClientShipUpdate.FromControls(0u, 0, new ControlData(v, 0f, 0f, 0f));
            var buf = new byte[ClientShipUpdate.Width];
            WireWriter w = new(buf);
            u.EncodeBody(ref w);
            WireReader r = new(buf);
            return (int)(ClientShipUpdate.DecodeBody(ref r).Controls.Yaw * 127f);
        }
    }
}
