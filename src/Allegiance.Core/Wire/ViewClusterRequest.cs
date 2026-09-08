namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_VIEW_CLUSTER</c> (id 104), a docked player's request to preview
/// another cluster — the server-side spectator/map-browse opener. Quoted
/// native shape (<c>src/Inc/Messages.h</c>, same family as the task's quoted
/// <c>FedSrv.CPP:6420-6454</c> handler):
/// <code>
///   DEFINE_FEDMSG(C, VIEW_CLUSTER, 104)
///     SectorID   clusterID;   // the cluster (sector) to preview
///     ObjectType otTarget;    // NA(-1) when no object to center on
///     ObjectID   oidTarget;   // NA(-1) when no object to center on
///   END_FEDMSG
/// </code>
/// Packed layout (this codebase's structs are uniformly packed with no host
/// alignment padding — see <see cref="SetCluster"/>'s doc comment for the P36
/// proof and the standing assumption):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 104
///   offset 4: short  clusterID   (SectorID = ObjectID = short, igc.h:477,487)
///   offset 6: short  otTarget    (ObjectType = typedef short, igc.h:114-115)
///   offset 8: short  oidTarget   (ObjectID  = typedef short, igc.h:477)
///   = 10 B fixed, no var items → cbmsg = 10
/// </code>
/// Both id fields are the <em>native</em> <c>short</c> typedefs, not the
/// 4-byte <c>[v1_enum]</c> int32 the EXPORT envelope uses. The native NA
/// sentinel for both "no cluster" and "no object" is -1, exactly as this
/// port reads everywhere else (e.g. <see cref="KillShip"/>'s NA side id).
/// </summary>
public sealed class ViewClusterRequest : IFedMessage
{
    public const ushort Fmid = 104;
    public const int FixedSize = 10;

    /// <summary>NA (-1) when the client only wanted the bare cluster, no object.</summary>
    public short ClusterId;
    /// <summary>ObjectType of the object to center on, NA(-1) for none. Cast to
    /// <see cref="Allegiance.Core.IgcObjectType"/> by the handler when resolving.</summary>
    public short OtTarget;
    /// <summary>ObjectID of the object to center on, NA(-1) for none.</summary>
    public short OidTarget;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ClusterId);
        w.WriteI16(OtTarget);
        w.WriteI16(OidTarget);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"VIEW_CLUSTER_REQUEST internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ViewClusterRequest Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"VIEW_CLUSTER_REQUEST needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected VIEW_CLUSTER_REQUEST fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new ViewClusterRequest
        {
            ClusterId = r.ReadI16(),
            OtTarget = r.ReadI16(),
            OidTarget = r.ReadI16(),
        };
    }
}
