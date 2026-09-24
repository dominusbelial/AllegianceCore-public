using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// One <c>SideEndgameInfo</c> wire record inside the <see cref="GameOver"/>
/// (62) message's fixed <c>rgSides[c_cSidesMax]</c> array. The native struct is
/// defined at <c>Messages.h:453-462</c>:
/// <code>
///   struct SideEndgameInfo
///   {
///     char  sideName[c_cbName];   // c_cbName = 25 (Utility.h:60,64)
///     CivID civID;                // short (ClubMessages.h:21)
///     Color color;                // see the Color note below
///     short cKills;
///     short cDeaths;
///     short cEjections;
///     short cBaseKills;
///     short cBaseCaptures;
///     short cFlags;
///     short cArtifacts;
///   };
/// </code>
/// <para><b>Color's width — resolved this task (P178).</b> The prompt left this
/// open; the answer fell out of the include chain. <c>Messages.h</c> includes
/// <c>&lt;igc.h&gt;</c> (<c>Messages.h:14</c>), which includes
/// <c>&lt;color.h&gt;</c> (<c>Igc/igc.h:17</c>) — the single <c>class Color</c>
/// at <c>zlib/color.h:16</c>, holding four <c>float</c> members
/// (<c>m_r,m_g,m_b,m_a</c>, <c>color.h:18</c>) = <b>16 bytes</b>. That this is
/// the wire type (not a packed RGB) is corroborated by the consumers: the
/// server writes <c>pside-&gt;GetColor()</c> — a zlib <c>Color</c> — straight
/// into the field (<c>fsmission.cpp:3073</c>), and the client reads it back as
/// a render color, <c>GetEndgameSideColor</c> returning
/// <c>m_vsideEndgameInfo[sideId].color</c> as a zlib <c>Color</c>
/// (<c>trekigc.cpp:5438</c>). So <c>Color</c> here is the 16-byte RGBA
/// float color, not a packed 3/4-byte form.</para>
/// <para><b>Element stride.</b> The record is memcpy'd verbatim
/// (<c>trekigc.cpp:5365</c> does <c>memcpy(..., sizeof(SideEndgameInfo) * c_cSidesMax)</c>)
/// and the message structs are <em>not</em> pragma-packed (the only pack
/// regions in <c>Messages.h</c> are <c>:636-642</c> and <c>:917-925</c>,
/// neither enclosing GAME_OVER at <c>:465-472</c>), so the layout is native
/// default (natural) alignment, which this repo's other records already
/// replicate field-for-field with explicit pad bytes (e.g. the DataShipIGC
/// layout in <c>NewShipInstance.cs</c>). Under MSVC default alignment
/// (char 1 / short 2 / float 4), <c>char[25]</c> pushes the first field to
/// offset 25 (odd), so <c>civID</c> is padded one byte to an even offset:
/// <code>
///   offset 0:  char  sideName[25]
///   offset 25: — pad (1) — align civID short to even
///   offset 26: short civID
///   offset 28: Color — 4 × float (r@g, g@32, b@36, a@40)
///   offset 44: short cKills
///   offset 46: short cDeaths
///   offset 48: short cEjections
///   offset 50: short cBaseKills
///   offset 52: short cBaseCaptures
///   offset 54: short cFlags
///   offset 56: short cArtifacts
///   offset 58: — tail pad (2) — struct align 4 → sizeof 60
///   = 60 bytes
/// </code>
/// The two tail-pad bytes (58-59) are real member of the array stride — each
/// of the six array slots is 60 bytes apart — and are written as zeros here
/// for determinism.</para>
/// </summary>
public readonly record struct SideEndgameInfo(
    string SideName,
    short CivId,
    float R,
    float G,
    float B,
    float A,
    short CKills,
    short CDeaths,
    short CEjections,
    short CBaseKills,
    short CBaseCaptures,
    short CFlags,
    short CArtifacts)
{
    /// <summary>Wire width of one record = native <c>sizeof(SideEndgameInfo)</c> = 60.</summary>
    public const int Size = 60;

    public void Write(ref WireWriter w)
    {
        WriteFixedName(ref w, SideName); // char[25] @0
        w.Advance(1);                    // pad @25 (align civID short to even)
        w.WriteI16(CivId);               // @26
        w.WriteU32(BitConverter.SingleToUInt32Bits(R)); // Color @28
        w.WriteU32(BitConverter.SingleToUInt32Bits(G)); // @32
        w.WriteU32(BitConverter.SingleToUInt32Bits(B)); // @36
        w.WriteU32(BitConverter.SingleToUInt32Bits(A)); // @40
        w.WriteI16(CKills);              // @44
        w.WriteI16(CDeaths);             // @46
        w.WriteI16(CEjections);          // @48
        w.WriteI16(CBaseKills);          // @50
        w.WriteI16(CBaseCaptures);       // @52
        w.WriteI16(CFlags);              // @54
        w.WriteI16(CArtifacts);          // @56
        w.WriteZeros(2);                 // tail pad @58 → 60
    }

    public static SideEndgameInfo Read(ReadOnlySpan<byte> b)
    {
        var name = ReadFixedName(b);
        return new SideEndgameInfo(
            SideName: name,
            CivId: BinaryPrimitives.ReadInt16LittleEndian(b[26..]),
            R: BinaryPrimitives.ReadSingleLittleEndian(b[28..]),
            G: BinaryPrimitives.ReadSingleLittleEndian(b[32..]),
            B: BinaryPrimitives.ReadSingleLittleEndian(b[36..]),
            A: BinaryPrimitives.ReadSingleLittleEndian(b[40..]),
            CKills: BinaryPrimitives.ReadInt16LittleEndian(b[44..]),
            CDeaths: BinaryPrimitives.ReadInt16LittleEndian(b[46..]),
            CEjections: BinaryPrimitives.ReadInt16LittleEndian(b[48..]),
            CBaseKills: BinaryPrimitives.ReadInt16LittleEndian(b[50..]),
            CBaseCaptures: BinaryPrimitives.ReadInt16LittleEndian(b[52..]),
            CFlags: BinaryPrimitives.ReadInt16LittleEndian(b[54..]),
            CArtifacts: BinaryPrimitives.ReadInt16LittleEndian(b[56..]));
    }

    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>Write a char[25]: up to 24 chars as Latin-1 bytes + a trailing NUL, then zero-pad the rest (same convention as <c>NewShipInstance</c>/<c>PlayerInfo</c>'s <c>WriteFixedName</c>).</summary>
    private static void WriteFixedName(ref WireWriter w, string s)
    {
        int n = Math.Min(s.Length, 24);
        if (n > 0) w.WriteBytes(Latin1.GetBytes(s[..n]));
        w.WriteByte(0);
        w.WriteZeros(25 - n - 1);
    }

    private static string ReadFixedName(ReadOnlySpan<byte> b)
    {
        int n = 25;
        while (n > 0 && b[n - 1] == 0) n--;
        return Latin1.GetString(b[..n]);
    }
}

/// <summary>
/// <c>FM_S_GAME_OVER</c> (id 62), the server's end-of-mission announcement
/// (<c>Messages.h:465-472</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, GAME_OVER, 62)
///     FM_VAR_ITEM(szGameoverMessage);
///     SideEndgameInfo   rgSides[c_cSidesMax];   // c_cSidesMax = 6
///     SideID            iSideWinner;
///     char              nNumSides;
///     bool              bEjectPods;
///   END_FEDMSG
/// </code>
/// The <c>FM_VAR_ITEM</c> (variable-length item) always precedes the fixed
/// members on the wire (<c>messagecore.h:756</c>: &quot;variable length items
/// MUST go first, then the fixed length items&quot;), so the ib/cb pair sits
/// right after the 4-byte base header and the fixed payload — the 6-record
/// <c>rgSides</c> array, <c>iSideWinner</c>, <c>nNumSides</c>, <c>bEjectPods</c>
/// — follows, with the <c>szGameoverMessage</c> string trailing as the blob:
/// <code>
///   offset 0:   ushort cbmsg
///   offset 2:   ushort fmid = 62
///   offset 4:   ushort ib (szGameoverMessage blob offset, from byte 0)
///   offset 6:   ushort cb (blob length, incl. trailing NUL)
///   offset 8:   SideEndgameInfo rgSides[6] × 60 B = 360 B
///   offset 368: short  iSideWinner   (SideID; NA(-1) on a draw)
///   offset 370: char   nNumSides
///   offset 371: bool   bEjectPods
///   offset 372: blob — szGameoverMessage (NUL-terminated)
///   = 372 + blob bytes total
/// </code>
/// <para><b>P178 — ported subset.</b> This message is the simplified
/// win-by-elimination announcement. Per-side stats (<c>cKills</c>/<c>cDeaths</c>/
/// <c>cEjections</c>/<c>cBaseKills</c>/<c>cBaseCaptures</c>/<c>cFlags</c>/
/// <c>cArtifacts</c>) are written as zero: this port tracks none of them (the
/// same honest zeroing the sibling codecs <c>ExportShip</c>/<c>NewShipInstance</c>/
/// <c>PlayerInfo</c> already apply to their kill/death/ejection slots).
/// <c>bEjectPods</c> is <c>false</c> — the port has no mission-param config
/// field for it (native reads <c>misparms.bEjectPods</c>). And since this port
/// has no per-side team-color concept, every side's <c>Color</c> is the neutral
/// gray the native itself uses for an absent side, <c>Color(0.5,0.5,0.5)</c>
/// (<c>fsmission.cpp:3073</c>); no color subsystem is invented for this
/// message. <c>GAME_OVER_PLAYERS</c>(63) is explicitly out of scope (it needs
/// per-player <c>PlayerScoreObject</c> stats this port has never built).</para>
/// </summary>
public sealed class GameOver : IFedMessage
{
    public const ushort Fmid = 62;

    /// <summary>Native <c>c_cSidesMax</c> (asserted at <c>fsmission.cpp:3585</c> etc.) — the fixed <c>rgSides</c> array length.</summary>
    public const int MaxSides = 6;

    /// <summary>Wire width of one <see cref="SideEndgameInfo"/> record.</summary>
    public const int SideRecordSize = SideEndgameInfo.Size;

    /// <summary>Wire width of the fixed region before the blob:
    /// header(4) + one ib/cb var pair(4) + 6×SideEndgameInfo(60) + iSideWinner(2)
    /// + nNumSides(1) + bEjectPods(1) = 372.</summary>
    public const int FixedSize = FedMessageHeader.HeaderSize + VarItem.ItemSize
        + MaxSides * SideEndgameInfo.Size + 2 + 1 + 1;

    /// <summary>Native <c>NA</c> sentinel (-1), the <c>iSideWinner</c> value on a draw (and the absent-side <c>civID</c>). Matches <c>PlayerInfo.cs</c>/<c>ExportShip.cs</c>/<c>NewShipInstance.cs</c>'s own <c>NA = -1</c> convention for an absent id.</summary>
    public const short NA = -1;

    /// <summary>The six per-side endgame records, in side-id order 0..5
    /// (length <see cref="MaxSides"/>).</summary>
    public SideEndgameInfo[] Sides = new SideEndgameInfo[MaxSides];

    /// <summary>The game-over reason string (<c>szGameoverMessage</c>).</summary>
    public string GameoverMessage = "";

    /// <summary>The winning side id (<c>iSideWinner</c>), or <see cref="NA"/> on a draw.</summary>
    public short SideWinner = NA;

    /// <summary>Number of real sides in the mission (<c>nNumSides</c>).</summary>
    public byte NumSides;

    /// <summary>Whether defeated players' pods are ejected (<c>bEjectPods</c>); always false in this port.</summary>
    public bool EjectPods;

    public ushort GetFmid() => Fmid;

    /// <summary>Build the message for a mission that has just ended: fills the
    /// six <c>rgSides</c> slots in side-id order (real side data where a side
    /// with that id exists, otherwise the native "<c>&lt;bug&gt;</c>" placeholder
    /// with <c>civID = NA</c> and gray color — <c>fsmission.cpp:3066-3077</c>),
    /// with all per-side stat counters zeroed (P178 simplification). The side
    /// <em>color</em> is the neutral gray <c>Color(0.5,0.5,0.5)</c> for every
    /// real side too, because this port has no per-side color to read.</summary>
    public static GameOver ForMission(Allegiance.Core.Model.Mission mission, short winnerSideId, string reason)
    {
        var sides = new SideEndgameInfo[MaxSides];
        for (int i = 0; i < MaxSides; i++)
        {
            var side = mission.Sides.FirstOrDefault(s => s.Id == i);
            sides[i] = side is null
                ? new SideEndgameInfo("<bug>", NA, 0.5f, 0.5f, 0.5f, 1f, 0, 0, 0, 0, 0, 0, 0)
                : new SideEndgameInfo(
                    SideName: side.Name ?? $"side{side.Id}",
                    CivId: side.Civilization.CivilizationId,
                    R: side.ColorR, G: side.ColorG, B: side.ColorB, A: side.ColorA,
                    CKills: 0, CDeaths: 0, CEjections: 0,
                    CBaseKills: 0, CBaseCaptures: 0, CFlags: 0, CArtifacts: 0);
        }

        return new GameOver
        {
            Sides = sides,
            GameoverMessage = reason,
            SideWinner = winnerSideId,
            NumSides = (byte)Math.Min(mission.Sides.Count, MaxSides),
            EjectPods = false,
        };
    }

    public byte[] Encode()
    {
        var strBytes = System.Text.Encoding.Latin1.GetBytes(GameoverMessage);
        int blobLen = strBytes.Length + 1;               // incl. trailing NUL
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ib @4
        int slotCb = w.Reserve(2);  // cb @6
        if (w.Position != 8) throw new InvalidOperationException($"GAME_OVER var-pair drift: {w.Position}");

        // fixed region: 6 side records, then winner/sides/pods.
        foreach (var e in Sides) e.Write(ref w);
        w.WriteI16(SideWinner);
        w.WriteByte(NumSides);
        w.WriteByte(EjectPods ? (byte)1 : (byte)0);
        if (w.Position != FixedSize) throw new InvalidOperationException($"GAME_OVER fixed-region drift: {w.Position} != {FixedSize}");

        // blob: the NUL-terminated reason string.
        int ib = w.Position;
        w.WriteBytes(strBytes);
        w.WriteByte(0);
        w.WriteU16At(slotIb, (ushort)ib);
        w.WriteU16At(slotCb, (ushort)blobLen);
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg
        return buf;
    }

    public static GameOver Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"GAME_OVER needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected GAME_OVER fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));
        var sides = new SideEndgameInfo[MaxSides];
        for (int i = 0; i < MaxSides; i++)
            sides[i] = SideEndgameInfo.Read(buf.Slice(8 + i * SideEndgameInfo.Size, SideEndgameInfo.Size));

        short winner = BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(368, 2));
        byte numSides = buf[370];
        bool ejectPods = buf[371] != 0;

        string reason = "";
        if (it.IsPresent)
        {
            if (it.Ib < 0 || it.Ib + it.Cb > buf.Length)
                throw new ArgumentException($"GAME_OVER blob [{it.Ib},{it.Ib + it.Cb}) out of range for length {buf.Length}");
            var blob = buf.Slice(it.Ib, it.Cb);
            int n = blob.Length;
            while (n > 0 && blob[n - 1] == 0) n--;     // chomp trailing NUL
            reason = System.Text.Encoding.Latin1.GetString(blob[..n]);
        }

        return new GameOver
        {
            Sides = sides,
            GameoverMessage = reason,
            SideWinner = winner,
            NumSides = numSides,
            EjectPods = ejectPods,
        };
    }
}
