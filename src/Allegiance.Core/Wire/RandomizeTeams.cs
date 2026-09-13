namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_RANDOMIZE_TEAMS</c> (id 21, <c>Messages.h:201</c>) — the mission
/// owner's "balance the teams" trigger. Despite the name it is not actually
/// random: native's handler (<c>FedSrv.CPP:6964-6997</c>) gates it on the
/// sender being the mission owner and the mission being <c>NotStarted</c>,
/// then calls <c>CFSMission::RandomizeSides</c> — a highest-rank-first greedy
/// reassignment to whichever side currently has the lowest total rank.
/// Native shape:
/// <code>
///   DEFINE_FEDMSG(C, RANDOMIZE_TEAMS, 21) // randomly assigns everyone to a team
/// </code>
/// Header-only marker (zero fields), the same as <see cref="StartGame"/>/
/// <see cref="Suicide"/>. Direction is <c>C</c> (client-initiated) — the
/// server owner-gates it, so the client who sends it must be the mission owner.
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 21
///   = 4 B fixed, no fields → cbmsg = 4
/// </code>
public sealed class RandomizeTeams : IFedMessage
{
    public const ushort Fmid = 21;
    public const int FixedSize = 4;   // header only — no fields (Messages.h:201)

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"RANDOMIZE_TEAMS internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static RandomizeTeams Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"RANDOMIZE_TEAMS needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected RANDOMIZE_TEAMS fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");
        return new RandomizeTeams();
    }
}
