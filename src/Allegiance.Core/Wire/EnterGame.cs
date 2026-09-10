namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_ENTER_GAME</c> (id 128, <c>src/Inc/Messages.h:831-835</c>) — the
/// final "you're ready" signal sent to a joining player once it has all the
/// info it needs to start playing in the current game (note: the countdown
/// may not have elapsed yet). Deliberately empty: the struct is header-only
/// (<c>FMD_S_ENTER_GAME</c> = <c>{ ushort cbmsg; ushort fmid; }</c>), so there
/// are no fields to encode or decode and <c>cbmsg = sizeof(FMD_S_ENTER_GAME) =
/// 4</c> — the same zero-field shape as <c>FM_C_SUICIDE</c>(80) and
/// <c>FM_CS_LOGOFF</c>(3), differing only in its server-direction marker.
/// <para><b>Wire semantics</b> (P141). Native fires it inside the mission-join
/// path gated on <c>STAGE_STARTED == GetStage()</c>
/// (<c>FedSrv/fsmission.cpp:799-805</c>), flushed to the joining player's OWN
/// connection only (<c>g.fm.SendMessages(pfsPlayer->GetConnection(),
/// FM_GUARANTEED, FM_FLUSH)</c>, fsmission.cpp:830) — the same per-player
/// shape as this port's <c>SET_MONEY</c>/<c>SET_BRIEFING_TEXT</c>/
/// <c>RANK_INFO</c> join-sync sends. The native <c>GetPlayerScoreObject()-&gt;
/// Connect(...)</c> alongside it (fsmission.cpp:801) is N/A here (no score
/// system, confirmed repeatedly P115/P125/P126/P140). Because this port is
/// direct-connect with no lobby/countdown — the mission is assembled and set
/// <c>Stage = Started</c> at boot (Server.cs:397-398), before any player can
/// join — the port's missions are always effectively started by the time
/// join-sync completes, so this port sends <c>ENTER_GAME</c> unconditionally,
/// once, as the final message of the join-sync burst.</para>
/// </summary>
public sealed class EnterGame : IFedMessage
{
    public const ushort Fmid = 128;
    public const int FixedSize = 4;   // header only — no fields (Messages.h:831-835)

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"ENTER_GAME internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static EnterGame Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"ENTER_GAME needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected ENTER_GAME fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");
        return new EnterGame();
    }
}
