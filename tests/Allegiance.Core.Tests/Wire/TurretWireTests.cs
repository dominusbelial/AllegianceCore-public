using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-exact tests for the P109 turret wire codecs:
/// <see cref="ActiveTurretUpdate"/> (FM_C_ACTIVE_TURRET_UPDATE, 99) and
/// <see cref="InactiveTurretUpdate"/> (FM_C_INACTIVE_TURRET_UPDATE, 100).
///
/// The ACTIVE message is the full native wire (<c>Messages.h:673-676</c>):
/// header + outer <c>Time timeUpdate</c> + the 16-byte inner
/// <c>ClientActiveTurretUpdate</c> (<c>igc.h:2470-2476</c> = time, controls,
/// orientation), 24 bytes total. Its <c>controls</c>/<c>orientation</c> reuse
/// exactly the same wire rules as the existing <c>ClientShipUpdate</c> /
/// <c>ServerShipUpdate</c> codecs. The INACTIVE message has <b>no fields</b>
/// (<c>Messages.h:678-679</c>) — exactly the 4-byte header, cbmsg=4.
/// Byte expectations are built with <see cref="ByteBuf"/>; the two control
/// char rows and the compact orientation reproduce the codec's own
/// quantization (via the public <see cref="CompactOrientationCodec"/> and a
/// local clone of the <c>char = floor(v·127 + 0.5)</c> rule) so the test
/// independently pins the wire layout without depending on the internal
/// <c>CompactQuant</c>.
/// </summary>
public class TurretWireTests
{
    private static byte CtrlAxis(float v)
    {
        int x = (int)MathF.Floor(v * 127.0f + 0.5f);
        x = Math.Clamp(x, -128, 127);
        return unchecked((byte)x);
    }

    private static byte[] CtrlRow(ControlData c) =>
        [CtrlAxis(c.Yaw), CtrlAxis(c.Pitch), CtrlAxis(c.Roll), CtrlAxis(c.Throttle)];

    private static byte[] Orient8(Orientation o)
    {
        var b = new byte[8];
        CompactOrientationCodec.Encode(o, b);
        return b;
    }

    // ---- ActiveTurretUpdate (99): timeUpdate + atu{time,controls,orientation} --

    [Fact]
    public void ActiveTurretUpdate_RoundTrips_AllFields()
    {
        var ctrl = new ControlData(0.6f, -0.4f, 0.2f, 0.9f);
        var orient = new Orientation();
        orient.Yaw(0.5f);
        orient.Pitch(-0.3f);

        var msg = new ActiveTurretUpdate
        {
            TimeUpdate = 77123,
            Atu = ClientActiveTurretUpdate.From(88123, ctrl, orient),
        };

        var d = ActiveTurretUpdate.Decode(msg.Encode());
        Assert.Equal(ActiveTurretUpdate.Fmid, d.GetFmid());
        Assert.Equal(77123u, d.TimeUpdate);              // outer timeUpdate
        Assert.Equal(88123u, d.Atu.Time);                // inner atu.time
        // Controls are CompactControlData (char/127) — lossy by design, so
        // compare at 2 decimals (the same tolerance the port's ship-update
        // codecs use for the identical quantized axes).
        Assert.Equal(ctrl.Yaw, d.Atu.Controls.Yaw, 2);
        Assert.Equal(ctrl.Pitch, d.Atu.Controls.Pitch, 2);
        Assert.Equal(ctrl.Roll, d.Atu.Controls.Roll, 2);
        Assert.Equal(ctrl.Throttle, d.Atu.Controls.Throttle, 2);
        // Orientation is CompactOrientation (16-bit quaternion) — lossy at
        // ~1e-4, so compare components at 2 decimals (3 hits a rounding
        // boundary for a component just off a decade).
        Assert.Equal(orient.GetForward().X, d.Atu.Orientation.GetForward().X, 2);
        Assert.Equal(orient.GetForward().Y, d.Atu.Orientation.GetForward().Y, 2);
        Assert.Equal(orient.GetForward().Z, d.Atu.Orientation.GetForward().Z, 2);
    }

    [Fact]
    public void ActiveTurretUpdate_IsByteExact_24ByteLayout()
    {
        var ctrl = new ControlData(0.5f, -0.5f, 0.0f, 1.0f);
        var orient = new Orientation();
        orient.Yaw(0.4f);
        var msg = new ActiveTurretUpdate
        {
            TimeUpdate = 77201,
            Atu = ClientActiveTurretUpdate.From(88201, ctrl, orient),
        };

        var got = msg.Encode();
        Assert.Equal(24, got.Length);
        var exp = ByteBuf.Build(24, w =>
        {
            w.U16(24); w.U16(99); w.U32(77201);        // header + outer timeUpdate
            w.U32(88201);                               // atu.time
            foreach (var b in CtrlRow(ctrl)) w.Byte(b); // atu.controls (4 chars)
            foreach (var b in Orient8(orient)) w.Byte(b); // atu.orientation (8)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void ActiveTurretUpdate_RoundTrip_IsAFixedPoint_ByteExact()
    {
        var ctrl = new ControlData(0.1f, 0.9f, -0.7f, 0.3f);
        var orient = new Orientation();
        orient.Roll(0.6f);
        orient.Pitch(0.2f);
        var msg = new ActiveTurretUpdate
        {
            TimeUpdate = 77311,
            Atu = ClientActiveTurretUpdate.From(88311, ctrl, orient),
        };
        byte[] a = msg.Encode();
        byte[] b2 = ActiveTurretUpdate.Decode(a).Encode();   // Decode→Encode is identity
        Assert.Equal(a, b2);
    }

    [Fact]
    public void ActiveTurretUpdate_RejectsWrongFmid_AndShortBuffer()
    {
        var bytes = new ActiveTurretUpdate
        {
            TimeUpdate = 77400,
            Atu = ClientActiveTurretUpdate.From(88400, ControlData.Neutral, Orientation.Identity),
        }.Encode();
        var bad = (byte[])bytes.Clone(); bad[2] = 200;
        Assert.Throws<ArgumentException>(() => ActiveTurretUpdate.Decode(bad));
        Assert.Throws<ArgumentException>(() => ActiveTurretUpdate.Decode(new byte[23]));
    }

    // ---- InactiveTurretUpdate (100): no fields, exactly the 4-byte header ----

    [Fact]
    public void InactiveTurretUpdate_IsByteExact_HeaderOnly()
    {
        var got = new InactiveTurretUpdate().Encode();
        Assert.Equal(4, got.Length);
        Assert.Equal(ByteBuf.Build(4, w => { w.U16(4); w.U16(100); }), got);
        _ = InactiveTurretUpdate.Decode(got);   // parses
    }

    [Fact]
    public void InactiveTurretUpdate_RejectsWrongFmid_AndShortBuffer()
    {
        var bytes = new InactiveTurretUpdate().Encode();
        var bad = (byte[])bytes.Clone(); bad[2] = 200;
        Assert.Throws<ArgumentException>(() => InactiveTurretUpdate.Decode(bad));
        Assert.Throws<ArgumentException>(() => InactiveTurretUpdate.Decode(new byte[3]));
    }

    // ---- catalog/dumper wiring --------------------------------------------------

    [Fact]
    public void CatalogMarksBothTurretUpdatesPorted()
    {
        Assert.True(FedMessageCatalog.IsPorted(ActiveTurretUpdate.Fmid));
        Assert.True(FedMessageCatalog.IsPorted(InactiveTurretUpdate.Fmid));
        Assert.Equal("ACTIVE_TURRET_UPDATE", FedMessageCatalog.Name(ActiveTurretUpdate.Fmid));
        Assert.Equal("INACTIVE_TURRET_UPDATE", FedMessageCatalog.Name(InactiveTurretUpdate.Fmid));
    }

    [Fact]
    public void DumperFormatsBothTurretUpdates()
    {
        var ctrl = new ControlData(0.5f, 0.0f, 0.0f, 1.0f);
        var msg = new ActiveTurretUpdate
        {
            TimeUpdate = 77500,
            Atu = ClientActiveTurretUpdate.From(88500, ctrl, Orientation.Identity),
        };
        var linesActive = FedMessageDumper.DumpPacket(FedPacket.Pack(new[] { msg.Encode() }));
        Assert.Contains(linesActive, l => l.Contains("timeUpdate=77500"));
        Assert.Contains(linesActive, l => l.Contains("atu.time=88500"));

        var linesInactive = FedMessageDumper.DumpPacket(FedPacket.Pack(new[] { new InactiveTurretUpdate().Encode() }));
        Assert.Contains(linesInactive, l => l.Contains("(port INACTIVE_TURRET_UPDATE)"));
    }
}
