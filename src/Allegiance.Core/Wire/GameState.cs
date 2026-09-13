namespace Allegiance.Core.Wire;

using System.Linq;

/// <summary>
/// <c>FM_S_GAME_STATE</c> (id 155), the server's per-side conquest/territory
/// game-state announcement (<c>Messages.h:854-860</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, GAME_STATE, 155)
///     unsigned char conquest[c_cSidesMax];
///     unsigned char territory[c_cSidesMax];
///     unsigned char nFlags[c_cSidesMax];
///     unsigned char nArtifacts[c_cSidesMax];
///   END_FEDMSG
/// </code>
/// <c>c_cSidesMax = 6</c> (<c>Igc/igc.h:507</c>), so the payload is four 6-byte
/// byte arrays:
/// <code>
///   offset 0:  ushort cbmsg
///   offset 2:  ushort fmid = 155
///   offset 4:  byte   conquest[6]    (per-side conquest %, indexed by side id)
///   offset 10: byte   territory[6]   (per-side territory count)
///   offset 16: byte   nFlags[6]      (per-side flags — always 0, see below)
///   offset 22: byte   nArtifacts[6]  (per-side artifacts — always 0)
///   = 28 B fixed, no var items → cbmsg = 28
/// </code>
/// All four arrays are byte-width with no alignment gaps, so the fixed size is
/// exactly <c>header(4) + 4×6 = 28</c>.
/// <para><b>nFlags / nArtifacts carry real counters (corrected by P201).</b>
/// P191's finding — that <c>IsideIGC::SetFlags</c>/<c>SetArtifacts</c>
/// (<c>Igc/sideigc.h:546,555</c>) are "never called in native" — was
/// <b>incomplete</b>: they <em>are</em> called, from the flag-securing handler
/// inside <c>FedSrvSiteBase::DockWithStationEvent</c>
/// (<c>FedSrv.CPP:13127-13137</c>: <c>pside-&gt;SetFlags(...+1)</c> /
/// <c>SetArtifacts(...+1)</c> when a flag-carrying ship docks at its own
/// side's station). P201 therefore wires <see cref="Allegiance.Core.Model.Side.Flags"/>/
/// <see cref="Allegiance.Core.Model.Side.Artifacts"/> into
/// <see cref="ForMission"/> in place of the always-zero filler, and this
/// codec broadcasts them (still <c>unsigned char</c> per side, truncated as
/// native truncates a <c>short</c>).
/// <b>Wire/broadcast only.</b> The port's broadcast path is
/// <see cref="Allegiance.Server.Server"/>, driven by the
/// <c>VictoryByStationsComputed</c> seam the mission raises after its
/// conquest/territory recompute (P191); this codec just defines the message
/// shape, the same split as <see cref="StationCapture"/> (P126).</summary>
public sealed class GameState : IFedMessage
{
    public const ushort Fmid = 155;

    /// <summary>Native <c>c_cSidesMax</c> (Igc/igc.h:507) — the length of each
    /// per-side array, asserted in <c>fsmission.cpp:3585</c>.</summary>
    public const int MaxSides = 6;

    /// <summary>Wire width of the whole message: header(4) + 4 byte-arrays × 6.
    /// No alignment gaps — the four byte arrays trail back-to-back at offsets
    /// 4,10,16,22.</summary>
    public const int FixedSize = 4 + 4 * MaxSides;

    /// <summary>Per-side conquest percentage, indexed by side id 0..5.</summary>
    public byte[] Conquest = new byte[MaxSides];
    /// <summary>Per-side territory count, indexed by side id 0..5.</summary>
    public byte[] Territory = new byte[MaxSides];
    /// <summary>Per-side flag count, indexed by side id 0..5. Populated by
    /// <see cref="ForMission"/> from each real side's <c>Side.Flags</c> (P201)
    /// — no longer hardcoded zero.</summary>
    public byte[] NFlags = new byte[MaxSides];
    /// <summary>Per-side artifact count, indexed by side id 0..5. Populated by
    /// <see cref="ForMission"/> from each real side's <c>Side.Artifacts</c>
    /// (P201) — no longer hardcoded zero.</summary>
    public byte[] NArtifacts = new byte[MaxSides];

    public ushort GetFmid() => Fmid;

    /// <summary>
    /// Build the message for a mission's current per-side state: fill
    /// <see cref="Conquest"/> and <see cref="Territory"/> from each real side's
    /// <c>Side.ConquestPercent</c>/<c>Side.TerritoryCount</c> (in side-id order
    /// 0..5, the native <c>assert(sid == GetObjectID())</c> fill at
    /// <c>fsmission.cpp:3690-3695</c>; a missing side stays 0), and likewise
    /// fill <see cref="NFlags"/>/<see cref="NArtifacts"/> from each side's
    /// <c>Side.Flags</c>/<c>Side.Artifacts</c> (the P201 correction — native
    /// fills these the same way, <c>fsmission.cpp:3691</c> reading the side's
    /// flags/artifacts; see the class remarks). Cast to <c>byte</c> as the
    /// native wire width truncates.</summary>
    public static GameState ForMission(Allegiance.Core.Model.Mission mission)
    {
        var gs = new GameState();
        for (int i = 0; i < MaxSides; i++)
        {
            var side = mission.Sides.FirstOrDefault(s => s.Id == i);
            if (side is null) continue;
            gs.Conquest[i] = side.ConquestPercent;
            gs.Territory[i] = side.TerritoryCount;
            gs.NFlags[i] = (byte)side.Flags;
            gs.NArtifacts[i] = (byte)side.Artifacts;
        }
        return gs;
    }

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        foreach (var b in Conquest) w.WriteByte(b);
        foreach (var b in Territory) w.WriteByte(b);
        foreach (var b in NFlags) w.WriteByte(b);
        foreach (var b in NArtifacts) w.WriteByte(b);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"GAME_STATE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static GameState Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"GAME_STATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected GAME_STATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var gs = new GameState();
        int o = 4;                                          // header
        for (int i = 0; i < MaxSides; i++) gs.Conquest[i] = buf[o++];
        for (int i = 0; i < MaxSides; i++) gs.Territory[i] = buf[o++];
        for (int i = 0; i < MaxSides; i++) gs.NFlags[i] = buf[o++];
        for (int i = 0; i < MaxSides; i++) gs.NArtifacts[i] = buf[o++];
        return gs;
    }
}
