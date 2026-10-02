namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_EXPORT</c> (id 26) for <c>OT_probe</c> (4) — the probe half of the
/// generic object-export message, mirroring <see cref="ExportStation"/>
/// (OT_station), <see cref="ExportShip"/> (OT_ship), <see cref="ExportCluster"/>
/// (OT_cluster), <see cref="ExportWarp"/> (OT_warp) and <see cref="ExportMine"/>
/// (OT_mine). Like mines, probes need no dedicated \"instance\" message: the
/// reference server creates a probe through the <b>same generic</b>
/// <c>CreateObject(..., OT_probe, &amp;obj, sizeof(obj))</c> path every other
/// exported object uses, and the native client exports one via
/// <c>CprobeIGC::Export</c> (<c>src/Igc/probeigc.cpp:535-568</c>) writing
/// <c>sizeof(DataProbeExport)</c> (36). So this task is a new <c>ExportProbe</c>
/// codec sharing fmid 26 — not a new fmid and not a var-item-array message.
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 26
///   offset 4: VarItem (ib, cb) for exportData
///   offset 8: int32   objecttype   (4-byte; [v1_enum] AGCObjectType) = OT_probe (4)
///   offset 12: exportData blob = DataProbeExport (36 B fixed)
///   = 48 B message, no further blobs.
/// </code>
/// <c>objecttype</c> is the same <c>[v1_enum]</c> <c>AGCObjectType</c> 4-byte
/// int32 as the other EXPORT codecs; <c>OT_probe</c> = <c>AGC_Probe</c> = 4
/// (<c>src/Inc/AGCIDL.idl:312</c>, matching <c>src/Igc/igc.h:123</c>). The
/// blob is the fixed 36-byte <see cref="DataProbeExport"/> (§OT_probe,
/// <c>DataProbeExport.cs</c>). The enclosing var-item is the EXPORT envelope's
/// own mechanism, not evidence the blob varies in length —
/// <c>DataProbeExport</c> is fixed at 36 bytes and <c>Decode</c> requires
/// exactly that.
/// </summary>
/// <remarks>
/// Wire-only. A real <see cref=\"Sim\"/>-layer <c>Probe</c> object, a
/// <c>Ship.DeployProbe</c> trigger, and the same-side broadcast wiring are the
/// explicit follow-up (the next task in this arc, mirroring P94 for mines);
/// armed-sentry (<c>m_projectileType</c>/<c>m_ammo</c>/<c>m_target</c>) and
/// direct-fire-destructible behaviour are separate later work. This codec
/// merely carries <c>otTarget</c>/<c>oidTarget</c> on the wire (they are part
/// of the real struct) without acting on them.
/// </summary>
public sealed class ExportProbe : IFedMessage
{
    public const ushort Fmid = 26;

    /// <summary><c>OT_probe</c> = <c>AGC_Probe</c> = 4 (<c>src/Inc/AGCIDL.idl:312</c>;
    /// <c>src/Igc/igc.h:123</c>) — the only object type this codec emits.</summary>
    public const int OtProbe = 4;

    /// <summary>Wire width of the fixed <see cref="DataProbeExport"/> payload.</summary>
    public const int DataProbeSize = DataProbeExport.Size;   // 36

    /// <summary>Wire width of the whole message: header(4) + var item(4) + objecttype(4) + DataProbeExport(36).</summary>
    public const int FixedSize = 12 + DataProbeSize;         // 48

    public int ObjectType = OtProbe;         // int32 (not short) — [v1_enum] AGCObjectType
    public DataProbeExport Probe;           // the OT_probe record

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Reserve(2);            // var-item ib
        int slotCb = w.Reserve(2);            // var-item cb

        w.WriteI32(ObjectType);               // objecttype — int32 at offset 8, 4-aligned, no pad

        // exportData blob (offset 12) = DataProbeExport (36 B fixed).
        int blobStart = w.Position;
        w.WriteBytes(Probe.Encode());
        int blobEnd = w.Position;
        if (blobEnd - blobStart != DataProbeSize)
            throw new InvalidOperationException($"EXPORT DataProbeExport layout drift: {blobEnd - blobStart} != {DataProbeSize}");

        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(blobEnd - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"EXPORT probe layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ExportProbe Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"EXPORT probe needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected EXPORT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        int objectType = BitConverter.ToInt32(buf.Slice(8));   // int32, offset 8
        if (objectType != OtProbe)
            throw new ArgumentException($"objecttype={objectType} != OT_probe {OtProbe} (this codec only emits probes)");
        if (vi.Cb != DataProbeSize)
            throw new ArgumentException($"exportData cb={vi.Cb} != DataProbeExport {DataProbeSize}");

        var probe = DataProbeExport.Decode(buf.Slice(vi.Ib, vi.Cb));

        return new ExportProbe { ObjectType = objectType, Probe = probe };
    }

    /// <summary>
    /// Build the fresh-creation export for a <see cref="Sim.Probe"/>, the
    /// analogue of <c>ExportMine.FromMine</c> and the server-side counterpart
    /// to the native <c>CprobeIGC::Export</c> (<c>src/Igc/probeigc.cpp:535-568</c>),
    /// which assigns every field explicitly. Fields mirror that method: <c>p0</c>
    /// = the probe's position, <c>time0</c> = <paramref name="timeCreate"/> (uint
    /// Time = the server tick, this project's uint-Time convention),
    /// <c>probeID</c> = the probe's id, <c>exportF</c> = true,
    /// <c>clusterID</c> = its sector, <c>probetypeID</c> = its decoded type's
    /// <c>ExpendableTypeId</c>, <c>sideID</c> = <paramref name="sideId"/>,
    /// <c>shipID</c> = <paramref name="launcherId"/> (the deploying ship's id —
    /// <c>NA</c> if none, mirroring <c>pdme-&gt;shipID = m_launcher ?
    /// m_launcher-&gt;GetObjectID() : NA</c>, <c>probeigc.cpp:551</c>),
    /// <c>otTarget</c>/<c>oidTarget</c> = the probe's persisted target's object
    /// type/id, or NA/-1 when it has none — the exact
    /// <c>if (m_target) { otTarget = m_target-&gt;GetObjectType();
    /// oidTarget = m_target-&gt;GetObjectID(); } else { NA; NA; }</c>
    /// (<c>probeigc.cpp:553-562</c>); P491 wires these to
    /// <see cref="Sim.Probe.Target"/> so a commanded
    /// (<c>c_eabmShootOnlyTarget</c>) probe's target rides the wire, where a
    /// free-targeting sentry's per-cycle target leaves them NA (native behaves
    /// identically — <c>m_target</c> is only persisted for ShootOnlyTarget
    /// probes). <c>createNow</c> = true (the reference server calls
    /// <c>SetCreateNow()</c> right after creating a fresh object).
    /// </summary>
    public static ExportProbe FromProbe(Sim.Probe probe, uint timeCreate, short launcherId, short sideId)
    {
        var pt = probe.ProbeType;
        // P491: the probe's persisted target (null for a free-targeting sentry
        // and for every DeployProbe-created probe). Native maps it to
        // ObjectType/ObjectID (probeigc.cpp:553-557); a targetless probe is NA.
        (short ot, short oid) = probe.Target is { } t
            ? (ObjectTypeOf(t), t.Id)
            : ((short)-1, (short)-1);
        return new ExportProbe
        {
            ObjectType = OtProbe,
            Probe = new DataProbeExport(
                P0: probe.Position,
                Time0: timeCreate,
                ProbeId: probe.Id,
                ExportF: true,
                ProbetypeId: pt.ExpendableTypeId,
                SideId: sideId,
                ClusterId: probe.OwnerSector.Id,
                ShipId: launcherId,
                OtTarget: ot,
                OidTarget: oid,
                CreateNow: true),
        };
    }

    /// <summary>The native <c>ObjectType</c> short (<c>igc.h:114-160</c>) of a
    /// probe target model, for the wire <c>otTarget</c> field — the same
    /// <c>m_target-&gt;GetObjectType()</c> native writes
    /// (<c>probeigc.cpp:555</c>). <see cref="IgcObjectType"/> omits
    /// <c>OT_missile</c> (2, between Station and Mine), so a missile target is
    /// returned as the literal native value.</summary>
    private static short ObjectTypeOf(Sim.Model target) => target switch
    {
        Sim.Ship => (short)IgcObjectType.Ship,          // 0
        Sim.Missile => 2,                               // OT_missile (igc.h:117)
        Sim.Probe => (short)IgcObjectType.Probe,        // 4
        _ => -1,
    };
}
