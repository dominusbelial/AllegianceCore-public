using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// One <c>RankInfo</c> wire record inside the <see cref="RankInfo"/> (13)
/// message's <c>FM_VAR_ITEM(ranks)</c> array. The native struct is defined in
/// <c>src/Inc/Messages.h:139-145</c> (quoted in full in the task): four members
/// <c>int requiredRanking</c>, <c>CivID civ</c> (a <c>short</c>,
/// ClubMessages.h:21), <c>RankID rank</c> (a <c>short</c>, ClubMessages.h:25),
/// and <c>char RankName[c_cbName]</c> with <c>c_cbName = 24+1 = 25</c>
/// (<c>_Utility/Utility.h:60,64</c>).
/// <para><b>Element stride.</b> The array blob is memcpy'd verbatim by the
/// client (<c>clintlib/appmsg.cpp:3377-3381</c>:
/// <c>memcpy(m_vRankInfo, FM_VAR_REF(pfm, ranks), cRanks * sizeof(RankInfo))</c>),
/// and the server emits it as <c>FM_VAR_PARM(g.vRankInfo, sizeof(RankInfo) *
/// cRankInfo)</c> (<c>FedSrv.CPP:998-1000</c>), so the per-element size is the
/// native compile-time <c>sizeof(RankInfo)</c>. RankInfo (Messages.h:139-145)
/// sits <b>outside</b> the file's <c>#pragma pack(1)</c> regions (those are
/// Messages.h:636 and :917), so it takes the default MSVC packing: 4+2+2+25 =
/// 33 field bytes, aligned up to the 4-byte alignment of the leading
/// <c>int</c> → <b>sizeof = 36</b> with 3 trailing pad bytes:</para>
/// <code>
///   offset  0: int   requiredRanking
///   offset  4: short civ            (always -1, standalone mode)
///   offset  6: short rank
///   offset  8: char  RankName[25]   (ASCII, NUL-padded)
///   offset 33: 3 pad bytes (struct trailing alignment to sizeof 36)
///   = 36 bytes
/// </code>
/// </summary>
public readonly record struct RankInfoEntry(
    int RequiredRanking,
    short Civ,
    short Rank,
    string RankName)
{
    /// <summary>Wire width of one record = native <c>sizeof(RankInfo)</c>=36
    /// under default MSVC packing (33 field bytes + 3 trailing pad).</summary>
    public const int Size = 36;

    public void Write(ref WireWriter w)
    {
        w.WriteI32(RequiredRanking);       // offset 0
        w.WriteI16(Civ);                   // offset 4
        w.WriteI16(Rank);                  // offset 6
        // RankName[25] @8: ASCII, NUL-padded to the fixed width (CutAtNull on decode).
        var nb = Encoding.ASCII.GetBytes(RankName);
        int n = Math.Min(nb.Length, 25);
        for (int i = 0; i < 25; i++) w.WriteByte(i < n ? nb[i] : (byte)0);
        w.WriteZeros(3);                   // offset 33: trailing pad to sizeof 36
    }

    public static RankInfoEntry Read(ReadOnlySpan<byte> b)
    {
        string name = DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(b.Slice(8, 25)));
        return new RankInfoEntry(
            RequiredRanking: BinaryPrimitives.ReadInt32LittleEndian(b),
            Civ: BinaryPrimitives.ReadInt16LittleEndian(b.Slice(4)),
            Rank: BinaryPrimitives.ReadInt16LittleEndian(b.Slice(6)),
            RankName: name);
    }
}

/// <summary>
/// <c>FM_S_RANK_INFO</c> (id 13), the server's per-player rank-name table
/// sync (<c>src/Inc/Messages.h:148-150</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, RANK_INFO, 13)
///     FM_VAR_ITEM(ranks); // an array of RankInfo sorted by civ, then rank
///     short   cRanks;
///   END_FEDMSG
/// </code>
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 13
///   offset  4: ushort ib (blob offset, from byte 0)
///   offset  6: ushort cb (blob byte length = cRanks × 36)
///   offset  8: short  cRanks
///   offset 10: blob — cRanks × RankInfoEntry(36)
///   = 10 + blob bytes total
/// </code>
/// Ported per the port's packed var-item convention (var item first, fixed
/// fields next, blob appended — the same shape as <see cref="SetBriefingText"/>
/// and <see cref="TreasureSets"/>): the <c>ranks</c> var-item points at an
/// array of <see cref="RankInfoEntry"/>, and <c>cRanks</c> is the count. The
/// fixed region is header(4) + one ib/cb var pair(4) + <c>cRanks</c>(2) = 10 —
/// identical to TreasureSets, whose record-array message shares the same
/// FM_VAR_ITEM + short-count shape.
/// <para><b>Wire semantics</b> (P130): native builds the frame right after
/// <c>LOGONACK</c> on the valid-connect path, for every connecting player, from
/// the boot-time <c>LoadRankInfo</c> table (<c>FedSrv.CPP:993-1001</c>;
/// <c>LoadRankInfo</c> at <c>:103-150</c>, ran unconditionally at
/// <c>:9347</c>). Crucially, <c>LoadRankInfo</c> is <b>not</b> compiled out
/// under <c>ALLSRV_STANDALONE</c> — its <c>#else</c> branch loads
/// <c>&lt;artwork&gt;/ranks.txt</c> instead of SQL (<c>FedSrv.CPP:125-148</c>),
/// and in that standalone branch every entry carries <c>requiredRanking = 0</c>
/// and <c>civ = -1</c> (<c>FedSrv.CPP:140-141</c>). This port is the
/// standalone-equivalent shape (no SQL/lobby, per P129), so it uses exactly
/// that branch: the real <c>ranks.txt</c> file, parsed faithfully, with
/// <c>civ=-1</c>/<c>requiredRanking=0</c> for every entry — not an invented
/// simplification, but native's own behavior for this build shape.</para>
/// </summary>
public sealed class RankInfo : IFedMessage
{
    public const ushort Fmid = 13;

    /// <summary>Wire width of the fixed region before the blob:
    /// header(4) + one ib/cb var pair(4) + cRanks(2) = 10.</summary>
    public const int FixedSize = FedMessageHeader.HeaderSize + VarItem.ItemSize + 2;

    /// <summary>The rank-name table, in file order (ascending rank in the real
    /// corpus; native sorts by civ then rank, but standalone assigns one civ = -1
    /// to every entry so the sort degenerates to the file order as-read).</summary>
    public RankInfoEntry[] Ranks = [];

    public ushort GetFmid() => Fmid;

    /// <summary>
    /// Parse the real standalone rank table file (<c>&lt;artwork&gt;/ranks.txt</c>),
    /// faithfully reproducing native's <c>LoadRankInfo</c> standalone branch
    /// (<c>FedSrv.CPP:125-148</c>): a count header line, then that many
    /// <c>&lt;rank&gt;,&lt;name&gt;</c> lines via
    /// <c>fscanf("%d,%[^,\n]\n", &amp;rank, name)</c>. The count drives the loop,
    /// so any <em>extra</em> trailing line past the header count is ignored (the
    /// real 41-line file's header is <c>40</c>, so the final duplicate
    /// <c>40,Master 9</c> line is dead data native never consumes — faithfully
    /// reproduced here). Every entry gets <c>requiredRanking = 0</c> and
    /// <c>civ = -1</c> (FedSrv.CPP:140-141). A missing/unreadable file yields an
    /// empty table, exactly native's <c>else g.cRankInfo = 0</c>
    /// (<c>FedSrv.CPP:145-146</c>).
    /// </summary>
    public static RankInfo FromRanksFile(string path)
    {
        var entries = new List<RankInfoEntry>();
        if (!File.Exists(path)) return new RankInfo();
        using var sr = new StreamReader(path);
        string? header = sr.ReadLine();
        if (header is null || !int.TryParse(header.Trim(), out int count) || count < 0)
            return new RankInfo();
        for (int i = 0; i < count; i++)
        {
            string? line = sr.ReadLine();
            if (line is null) break;                       // file shorter than header — read what's there
            int comma = line.IndexOf(',');
            if (comma < 0 || !int.TryParse(line.AsSpan(0, comma), out int rank))
                continue;                                  // malformed line — skip, as scanf would leave rank untouched
            string name = line[(comma + 1)..];
            entries.Add(new RankInfoEntry(RequiredRanking: 0, Civ: -1, Rank: (short)rank, RankName: name));
        }
        return new RankInfo { Ranks = entries.ToArray() };
    }

    public byte[] Encode()
    {
        int blobLen = Ranks.Length * RankInfoEntry.Size;
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ib @4
        int slotCb = w.Reserve(2);  // cb @6
        w.WriteI16((short)Ranks.Length);   // cRanks @8
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"RANK_INFO internal layout drift: {w.Position} != {FixedSize}");

        // blob at the fixed-region end (ib = offset from byte 0)
        int ib = w.Position;
        foreach (var r in Ranks) r.Write(ref w);
        w.WriteU16At(slotIb, (ushort)ib);
        w.WriteU16At(slotCb, (ushort)(w.Position - ib));
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg
        return buf;
    }

    public static RankInfo Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"RANK_INFO needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected RANK_INFO fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));   // the single ib/cb pair
        short count = BinaryPrimitives.ReadInt16LittleEndian(buf[8..]);
        var ranks = new List<RankInfoEntry>();

        if (it.IsPresent && count > 0)
        {
            if (it.Ib < 0 || it.Ib + it.Cb > buf.Length)
                throw new ArgumentException($"RANK_INFO blob [{it.Ib},{it.Ib + it.Cb}) out of range for length {buf.Length}");
            var blob = buf.Slice(it.Ib, it.Cb);
            for (int i = 0; i < count; i++)
            {
                int off = i * RankInfoEntry.Size;
                if (off + RankInfoEntry.Size > blob.Length)
                    throw new ArgumentException($"RANK_INFO declares {count} ranks but blob is only {blob.Length} bytes");
                ranks.Add(RankInfoEntry.Read(blob.Slice(off, RankInfoEntry.Size)));
            }
        }
        else if (count != 0)
        {
            throw new ArgumentException($"RANK_INFO declares {count} ranks but the ranks var-item is absent");
        }

        return new RankInfo { Ranks = ranks.ToArray() };
    }
}
