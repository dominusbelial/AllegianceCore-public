using System.Buffers.Binary;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_ACTIVE_TURRET_UPDATE</c> (id 99) — the message a <b>turret
/// gunner</b> sends instead of a normal <c>SHIP_UPDATE(24)</c> while it is
/// actively controlling/firing a turret (<c>Messages.h:673-676</c>,
/// <c>clintlib.cpp:1884-1893</c>: the client chooses this message when
/// <c>stateM &amp; weaponsMaskIGC</c>). Layout:
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 99
///   offset 4: uint32 timeUpdate   (outer Time, Messages.h:674)
///   offset 8: ClientActiveTurretUpdate atu  (16 B)
///   = 24; no var items → cbmsg = 24
/// </code>
/// The 16-byte <c>atu</c> payload — the <c>ClientActiveTurretUpdate</c>
/// struct (<c>igc.h:2470-2476</c>) — carries the three fields the server's
/// <c>ProcessShipUpdate(const ClientActiveTurretUpdate&amp;)</c> actually
/// consumes (<c>shipIGC.cpp:3349-3392</c>): <c>Time time; CompactControlData
/// controls; CompactOrientation orientation;</c>. Its <c>controls</c> and
/// <c>orientation</c> slots reuse the exact same rich/wire types the normal
/// <c>ClientShipUpdate</c> (SHIP_UPDATE(24)) already decodes —
/// <see cref="ControlData"/> via <c>CompactQuant.ControlAxis</c> and
/// <see cref="Orientation"/> via <see cref="CompactOrientationCodec"/> — so
/// this codec invents no new quantization, it only reuses the existing pair.
///
/// Two real finding this codec records:
/// <list type="bullet">
/// <item><b>Outer <c>timeUpdate</c> is redundant on the server.</b> The native
/// client sets both the outer <c>timeUpdate</c> and the inner
/// <c>atu.time</c> from the same source (<c>clintlib.cpp:1892</c>
/// <c>ServerTimeFromClientTime(GetLastUpdate())</c>; <c>ExportShipUpdate</c>,
/// <c>shipIGC.h:1206-1212</c> sets <c>atu.time</c> identically), and the
/// native server discards the outer field entirely — it stores only the inner
/// <c>atu</c> via <c>SetActiveTurretUpdate(pfmATU-&gt;atu)</c>
/// (<c>FedSrv.CPP:4877</c>) and hands it to
/// <c>ProcessShipUpdate(const ClientActiveTurretUpdate&amp;)</c>. The outer
/// field is nevertheless carried in the codec for <b>byte-exact wire
/// fidelity</b> against a real client, exactly as <see cref="ShipUpdate"/>
/// carries both its outer <c>Time</c> and the payload's own leading time
/// (<c>ShipUpdate.cs:22</c>, the live P33 capture), rather than inventing a
/// 20-byte message the real client would never send.</item>
/// <item><b>No position/velocity.</b> A gunner never moves independently, only
/// turns (<c>igc.h:2470-2476</c>), so the payload is just
/// time+controls+orientation.</item>
/// </list>
/// </summary>
public sealed class ActiveTurretUpdate : IFedMessage
{
    public const ushort Fmid = 99;
    public const int FixedSize = 24;

    /// <summary>The outer <c>timeUpdate</c> (offset 4) — the client's
    /// server-converted last-update time. Carried for wire fidelity; the
    /// server handler reads the payload's own <see cref="Atu"/>.<c>Time</c>
    /// (native ignores the outer field, FedSrv.CPP:4877).</summary>
    public uint TimeUpdate;

    /// <summary>The inner <c>ClientActiveTurretUpdate</c> (offsets 8..23),
    /// the three-field struct the handler consumes.</summary>
    public ClientActiveTurretUpdate Atu = null!;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                 // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU32(TimeUpdate);
        Atu.EncodeBody(ref w);         // 16 B, immediately (no alignment pad)
        w.WriteU16At(0, (ushort)FixedSize);
        return buf;
    }

    public static ActiveTurretUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"ACTIVE_TURRET_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected ACTIVE_TURRET_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                              // header
        uint time = r.ReadU32();
        var atu = ClientActiveTurretUpdate.DecodeBody(ref r);
        return new ActiveTurretUpdate { TimeUpdate = time, Atu = atu };
    }
}

/// <summary>
/// The 16-byte inner <c>ClientActiveTurretUpdate</c> payload
/// (<c>igc.h:2470-2476</c>) embedded in <c>FM_C_ACTIVE_TURRET_UPDATE(99)</c>.
/// Layout — the exact <c>Time; CompactControlData; CompactOrientation</c>
/// triplet the task left as "needs inference", settled here by usage against
/// the quoted <c>ProcessShipUpdate</c> (<c>shipIGC.cpp:3349-3392</c>):
/// <code>
///   0 uint     time         (Time — the update's last-update instant)
///   4 [4]      controls     (CompactControlData — the commanded yaw/pitch/roll/throttle)
///   8 [8]      orientation  (CompactOrientation — the reported turret facing)
/// </code>
/// A gunner never moves independently, only turns — no position/velocity.
/// Same round-trip-exact-by-construction pattern as <see cref="ClientShipUpdate"/>:
/// the object stores the canonical 16 bytes and every rich accessor re-derives
/// from those bytes, so <c>DecodeBody(EncodeBody(x))</c> is a fixed point.
/// </summary>
public sealed class ClientActiveTurretUpdate
{
    /// <summary>Wire width, bytes.</summary>
    public const int Width = 16;

    private readonly byte[] _b = new byte[Width];

    private ClientActiveTurretUpdate() { }

    /// <summary>The update's last-update time (<c>Time time</c>, <c>igc.h:2473</c>). Units are sim-seconds (same source as the ship's <c>LastUpdate</c>); the server compares it directly against <c>Ship.LastUpdate</c> for the staleness bound.</summary>
    public uint Time => BinaryPrimitives.ReadUInt32LittleEndian(_b);

    /// <summary>The commanded joystick axes — yaw/pitch/roll/throttle, each <c>char/127</c> (<c>CompactControlData</c>).</summary>
    public ControlData Controls => new(
        CompactQuant.DeControlAxis(_b[4]),
        CompactQuant.DeControlAxis(_b[5]),
        CompactQuant.DeControlAxis(_b[6]),
        CompactQuant.DeControlAxis(_b[7]));

    /// <summary>The reported turret facing, reconstructed from the compact quaternion (<c>CompactOrientation</c>, offsets 8..15).</summary>
    public Orientation Orientation => CompactOrientationCodec.Decode(_b.AsSpan(8, 8));

    /// <summary>Build an update from rich values (the <c>ExportShipUpdate</c>
    /// producer side, <c>shipIGC.h:1206-1212</c>): the last-update instant and
    /// the gunner's live controls + orientation, re-quantized immediately so
    /// the stored bytes already equal what a wire decode would produce.</summary>
    public static ClientActiveTurretUpdate From(uint time, ControlData controls, Orientation orientation)
    {
        var u = new ClientActiveTurretUpdate();
        BinaryPrimitives.WriteUInt32LittleEndian(u._b, time);
        u._b[4] = CompactQuant.ControlAxis(controls.Yaw);
        u._b[5] = CompactQuant.ControlAxis(controls.Pitch);
        u._b[6] = CompactQuant.ControlAxis(controls.Roll);
        u._b[7] = CompactQuant.ControlAxis(controls.Throttle);
        CompactOrientationCodec.Encode(orientation, u._b.AsSpan(8, 8));
        return u;
    }

    /// <summary>Encode the 16 canonical bytes at the writer's current position.</summary>
    public void EncodeBody(ref WireWriter w) => w.WriteBytes(_b);

    /// <summary>Read a 16-byte body from the reader's current position.</summary>
    public static ClientActiveTurretUpdate DecodeBody(ref WireReader r)
    {
        if (r.HasRemaining && r.Length - r.Position < Width)
            throw new ArgumentException($"ClientActiveTurretUpdate needs {Width} bytes, got {r.Length - r.Position}");
        var u = new ClientActiveTurretUpdate();
        r.ReadBytes(Width).CopyTo(u._b);
        return u;
    }
}
