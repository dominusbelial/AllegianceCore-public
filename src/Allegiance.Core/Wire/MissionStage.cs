namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_MISSION_STAGE</c> (id 37) — tells the client the mission's stage
/// (<c>Inc/Messages.h:290-292</c>: <c>DEFINE_FEDMSG(S, MISSION_STAGE, 37) STAGE
/// stage; END_FEDMSG</c>). <c>STAGE</c> is a <c>[v1_enum]</c> (<c>AGCIDL.idl:480</c>,
/// <c>AGCGameStage</c>), i.e. a 4-byte int32, same convention as
/// <see cref="ExportShip"/>'s/<see cref="ExportCluster"/>'s <c>objecttype</c>.
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 37
///   offset 4: int32  stage
///   = 8 B fixed; no var items → cbmsg = 8
/// </code>
/// This is the message the native client needs to leave its boot default
/// (<c>STAGE_NOTSTARTED</c>) and actually start ticking its mission
/// simulation: <c>CmissionIGC::Update</c> (<c>missionigc.cpp:3133-3134</c>) and
/// the per-cluster ship plot/execute loop (<c>clusterIGC.cpp:103-231</c>) are
/// both no-ops until the client's core mission stage is
/// <c>STAGE_STARTED</c>/<c>STAGE_STARTING</c>, and the client's only path to
/// that (outside the lobby, which a direct-connect client never sees) is
/// receiving this message (<c>appmsg.cpp:2314-2349</c>,
/// <c>case FM_S_MISSION_STAGE</c> → <c>SetInProgress(true)</c> +
/// <c>m_pCoreIGC->SetMissionStage(STAGE_STARTED)</c> for the
/// <c>STAGE_STARTED</c> case). Found missing (P37, 2026-08-31): the server
/// already tracks <c>Mission.Stage = Started</c> internally
/// (<c>Server.cs:243</c>) but never told the client — the reason the ship's
/// autopilot/movement code never ran even with a correct position and a
/// resolved goto target.
/// </summary>
public sealed class MissionStageMessage : IFedMessage
{
    public const ushort Fmid = 37;
    public const int FixedSize = 8;

    /// <summary><c>STAGE_NOTSTARTED</c> = <c>AGCGameStage_NotStarted</c> = 0 (<c>AGCIDL.idl:482</c>).</summary>
    public const int StageNotStarted = 0;
    /// <summary><c>STAGE_STARTING</c> = <c>AGCGameStage_Starting</c> = 1 (<c>AGCIDL.idl:483</c>).</summary>
    public const int StageStarting = 1;
    /// <summary><c>STAGE_STARTED</c> = <c>AGCGameStage_Started</c> = 2 (<c>AGCIDL.idl:484</c>).</summary>
    public const int StageStarted = 2;
    /// <summary><c>STAGE_OVER</c> = <c>AGCGameStage_Over</c> = 3 (<c>AGCIDL.idl:485</c>).</summary>
    public const int StageOver = 3;
    /// <summary><c>STAGE_TERMINATE</c> = <c>AGCGameStage_Terminate</c> = 4 (<c>AGCIDL.idl:486</c>).</summary>
    public const int StageTerminate = 4;

    public int Stage = StageStarted;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);            // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI32(Stage);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"MISSION_STAGE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static MissionStageMessage Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"MISSION_STAGE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected MISSION_STAGE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);              // header
        int stage = r.ReadI32();

        return new MissionStageMessage { Stage = stage };
    }
}
