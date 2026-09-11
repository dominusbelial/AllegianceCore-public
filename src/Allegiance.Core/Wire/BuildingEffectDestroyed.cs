namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_BUILDINGEFFECT_DESTROYED</c> (id 181), the server's "this
/// building effect was interrupted / destroyed" message
/// (<c>src/Inc/Messages.h:936-938</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, BUILDINGEFFECT_DESTROYED, 181)
///       AsteroidID          asteroidID;
///   END_FEDMSG
/// </code>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 181
///   offset 4: short  asteroidID   (AsteroidID, igc.h:492)
///   = 6 B fixed, no var items → cbmsg = 6
/// </code>
/// Simple fixed-size message, no var-item — the structural sibling of
/// <see cref="DestroyTreasure"/>(30) and <see cref="AsteroidDestroyed"/>(86):
/// a single destroyed-object id broadcast <b>whole-mission</b>. Emitted by the
/// native <c>FedSrvSiteBase::KillBuildingEffectEvent</c>
/// (<c>FedSrv.CPP:12484-12492</c>) whenever an in-progress building effect is
/// <b>interrupted</b> (the Builder ship died mid-build) — the wire half of the
/// <c>CshipIGC::Terminate</c> → <c>BuilderTerminated()</c> →
/// <c>KillBuildingEffectEvent</c> chain (<c>shipIGC.cpp:205-220</c>,
/// <c>buildingeffectigc.h:294-297</c>) — sent guaranteed, whole-mission
/// (<c>GetGroupRealSides()</c>). The native only sends it on the
/// <b>interruption</b> path; a build that reaches its 40-second completion
/// deadline transforms the asteroid in place (P163 <c>CompleteBuild</c>) and
/// sends nothing. This port sends it on the P163 interruption branch to every
/// peer regardless of side, mirroring <see cref="DestroyTreasure"/>'s own
/// whole-mission broadcast exactly.
/// </summary>
public sealed class BuildingEffectDestroyed : IFedMessage
{
    public const ushort Fmid = 181;

    /// <summary>Wire width of the whole message: header(4) + asteroidID(2).
    /// No alignment gaps — the single 2-byte id trails the header directly.</summary>
    public const int FixedSize = 6;

    public short AsteroidId;     // the asteroid whose build was interrupted

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(AsteroidId);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"BUILDINGEFFECT_DESTROYED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static BuildingEffectDestroyed Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"BUILDINGEFFECT_DESTROYED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected BUILDINGEFFECT_DESTROYED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new BuildingEffectDestroyed
        {
            AsteroidId = r.ReadI16(),
        };
    }
}
