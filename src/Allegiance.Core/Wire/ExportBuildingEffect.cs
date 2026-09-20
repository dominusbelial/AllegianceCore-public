using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_EXPORT</c> (id 26) for <c>OT_buildingEffect</c> (11) — the building-
/// effect half of the generic object-export message, mirroring
/// <see cref="ExportProbe"/> (OT_probe), <see cref="ExportMine"/> (OT_mine),
/// <see cref="ExportStation"/> (OT_station) and the rest. Native creates a
/// building effect through the <b>same generic</b>
/// <c>CreateObject(..., OT_buildingEffect, &amp;dbe, sizeof(dbe))</c> path every
/// other exported object uses (<c>src/Igc/clusterIGC.cpp:707-735</c>), and
/// broadcasts it with the same <c>ExportObj(pbe, OT_buildingEffect, NULL)</c>
/// call that exports asteroids/stations/warps — both at sector-join sync
/// (<c>SendMissionInfo</c>, <c>src/FedSrv/fsmission.cpp:4209-4211</c>) and,
/// decisively, at <b>build start</b>: <c>FedSrvSiteBase::CreateBuildingEffect</c>
/// (<c>src/FedSrv/FedSrv.CPP:13733-13767</c>) exports the fresh effect to every
/// side that can see the build. So the creation broadcast is a new
/// <c>ExportBuildingEffect</c> codec sharing fmid 26 — not a new fmid and not a
/// var-item-array message (exactly the P98/P224 ExportProbe/ExportMine shape).
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 26
///   offset 4: VarItem (ib, cb) for exportData
///   offset 8: int32   objecttype   ([v1_enum] AGCObjectType) = OT_buildingEffect (11)
///   offset 12: exportData blob = DataBuildingEffectExport (56 B fixed)
///   = 68 B message, no further blobs.
/// </code>
/// <c>objecttype</c> is the same <c>[v1_enum]</c> 4-byte int32 as the other
/// EXPORT codecs; <c>OT_buildingEffect</c> = <c>AGC_BuildingEffect</c> = 11
/// (<c>src/Inc/AGCIDL.idl:319</c>, matching <c>src/Igc/igc.h:131</c>). The blob
/// is the fixed 56-byte <see cref="DataBuildingEffectExport"/>. The enclosing
/// var-item is the EXPORT envelope's own mechanism, not a varying blob —
/// <c>DataBuildingEffectExport</c> is fixed at 56 bytes and <c>Decode</c>
/// requires exactly that.
/// </summary>
/// <remarks>
/// Wire-only. This port's <b>only</b> server→client building-effect broadcasts
/// are this creation export (fed by <see cref="FromBuildingEffect"/>, the
/// server's counterpart to native <c>CbuildingEffectIGC::Export</c>) plus the
/// existing <see cref="BuildingEffectDestroyed"/>(181) interruption message —
/// mirroring native, which pushes <b>no</b> periodic progress: the client
/// reconstructs the whole 4-phase envelope→grow→opaque→shrink animation locally
/// from the fixed phase constants (<c>c_dtEnvelope = c_dtGrow = c_dtOpaque =
/// c_dtShrink = 10</c>, <c>buildingeffectigc.h:19-22</c>) and
/// <see cref="DataBuildingEffectExport.TimeStart"/>
/// (<c>buildingeffectigc.h:160-225</c>), and self-expires at completion (see
/// <see cref="Allegiance.Core.Sim.BuildingEffect.BuildDuration"/>).
/// </remarks>
public sealed class ExportBuildingEffect : IFedMessage
{
    public const ushort Fmid = 26;

    /// <summary><c>OT_buildingEffect</c> = <c>AGC_BuildingEffect</c> = 11
    /// (<c>src/Inc/AGCIDL.idl:319</c>; <c>src/Igc/igc.h:131</c>) — the only
    /// object type this codec emits.</summary>
    public const int OtBuildingEffect = 11;

    /// <summary>Wire width of the fixed <see cref="DataBuildingEffectExport"/> payload.</summary>
    public const int DataBuildingEffectSize = DataBuildingEffectExport.Size;   // 56

    /// <summary>Wire width of the whole message: header(4) + var item(4) + objecttype(4) + DataBuildingEffectExport(56).</summary>
    public const int FixedSize = 12 + DataBuildingEffectSize;                  // 68

    public int ObjectType = OtBuildingEffect;         // int32 (not short) — [v1_enum] AGCObjectType
    public DataBuildingEffectExport Effect;           // the OT_buildingEffect record

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

        // exportData blob (offset 12) = DataBuildingEffectExport (56 B fixed).
        int blobStart = w.Position;
        w.WriteBytes(Effect.Encode());
        int blobEnd = w.Position;
        if (blobEnd - blobStart != DataBuildingEffectSize)
            throw new InvalidOperationException($"EXPORT DataBuildingEffectExport layout drift: {blobEnd - blobStart} != {DataBuildingEffectSize}");

        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(blobEnd - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"EXPORT buildingEffect layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ExportBuildingEffect Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"EXPORT buildingEffect needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected EXPORT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        int objectType = BitConverter.ToInt32(buf.Slice(8));   // int32, offset 8
        if (objectType != OtBuildingEffect)
            throw new ArgumentException($"objecttype={objectType} != OT_buildingEffect {OtBuildingEffect} (this codec only emits building effects)");
        if (vi.Cb != DataBuildingEffectSize)
            throw new ArgumentException($"exportData cb={vi.Cb} != DataBuildingEffectExport {DataBuildingEffectSize}");

        var effect = DataBuildingEffectExport.Decode(buf.Slice(vi.Ib, vi.Cb));

        return new ExportBuildingEffect { ObjectType = objectType, Effect = effect };
    }

    /// <summary>
    /// Build the creation export for a <see cref="Sim.BuildingEffect"/>, the
    /// analogue of <c>ExportMine.FromMine</c>/<c>ExportProbe.FromProbe</c> and
    /// the server-side counterpart to the native
    /// <c>CbuildingEffectIGC::Export</c> (<c>src/Igc/buildingeffectigc.h:136-158</c>),
    /// which assigns every field explicitly. <c>timeStart</c> =
    /// <paramref name="timeStart"/> (the server tick the build began; native
    /// <c>ServerTimeFromClientTime(m_timeEnvelope)</c>,
    /// <c>buildingeffectigc.h:142</c>), <c>radiusAsteroid</c> = the target rock's
    /// radius (<c>pasteroid-&gt;GetRadius()</c>, clintlib.h:1025),
    /// <c>radiusStation</c> = the station type's radius (<c>GetRadius()</c>,
    /// clintlib.h:1026), <c>positionStart</c> = the builder's position,
    /// <c>positionStop</c> = the asteroid's position (native
    /// <c>m_positionStart</c>/<c>m_positionStop</c>,
    /// <c>clusterIGC.cpp:727-728</c>), <c>clusterID</c> = the building sector,
    /// <c>asteroidID</c> = the target rock's id, and <c>color</c> = the building
    /// side's colour (<c>buildingeffectigc.h:154</c>).
    /// </summary>
    public static ExportBuildingEffect FromBuildingEffect(
        Sim.BuildingEffect effect,
        uint timeStart,
        Vector3 builderPosition,
        float sideR, float sideG, float sideB, float sideA)
    {
        var target = effect.Target;
        var roidPos = target.Data.Position is { Length: 3 } rp
            ? new Vector3(rp[0], rp[1], rp[2])
            : Vector3.Zero;
        var builderPos = builderPosition;
        return new ExportBuildingEffect
        {
            ObjectType = OtBuildingEffect,
            Effect = new DataBuildingEffectExport(
                TimeStart: timeStart,
                RadiusAsteroid: target.Radius,
                RadiusStation: effect.StationType.Radius,
                PositionStart: builderPos,
                PositionStop: roidPos,
                ClusterId: effect.Target.Data.ClusterId,
                AsteroidId: target.Data.AsteroidId,
                ColorR: sideR, ColorG: sideG, ColorB: sideB, ColorA: sideA),
        };
    }
}
