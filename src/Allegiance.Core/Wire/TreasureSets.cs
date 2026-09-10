using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// One <c>TreasureSet</c> wire record inside the <see cref="TreasureSets"/>
/// (89) message's <c>FM_VAR_ITEM(treasureSet)</c> array. The native comment
/// (<c>Messages.h:624-628</c>) calls it \"array of TreasureSet\" and says to
/// <em>ignore pargChance — you don't get (or need) that blob</em>: i.e. the
/// array carries the fixed identity part of each catalog set but <b>not</b>
/// the per-entry chance table. This port therefore maps each record onto the
/// measured 32-byte head of <see cref="DataTreasureSetIGC"/> (its own
/// corpus-measured catalog record: <c>name[25]</c> + <c>treasureSetID</c> +
/// the untrusted two-byte count + <c>bZoneOnly</c> + 2 pad, see
/// <c>Data/DataTreasureSetIGC.cs</c>) and drops the entries/chance blob
/// entirely — a faithful, documented mapping since modern native FedSrv
/// <em>never sends</em> TREASURE_SETS (it is a legacy message, parsed by the
/// Wopr/WinTrek clients only, both of which treat it as NYI — see the P123
/// report's spawn-trigger finding).
/// </summary>
/// <remarks>
/// <para><b>Layout</b> — identical to the <c>DataTreasureSetIGC</c> head that
/// <c>Data/DataTreasureSetIGC.cs</c> measures for the 99-record corpus
/// (<c>igc.h:2107-2118</c>, C++ default alignment):</para>
/// <code>
///   offset  0: char name[25]        (fixed-width, NUL-padded, c_cbName)
///   offset 25: short treasureSetID
///   offset 27: short nTreasureData  (entry count; writer-contradicted → informational)
///   offset 29: bool  bZoneOnly
///   offset 30: 2 pad bytes
///   = 32 bytes
/// </code>
/// The per-set <c>TreasureData</c> entries (which carry the <c>chance</c> blob)
/// are deliberately omitted, per the message's own comment.
/// </remarks>
public readonly record struct TreasureSetRecord(
    string Name,
    short TreasureSetId,
    short Count,
    bool BZoneOnly)
{
    /// <summary>Wire width of one record: the 32-byte catalog head.</summary>
    public const int Size = 32;

    public void Write(ref WireWriter w)
    {
        // name[25]: ASCII, NUL-padded to the fixed width (CutAtNull on decode).
        var nb = Encoding.ASCII.GetBytes(Name);
        int n = Math.Min(nb.Length, 25);
        for (int i = 0; i < 25; i++) w.WriteByte(i < n ? nb[i] : (byte)0);
        w.WriteI16(TreasureSetId);
        w.WriteI16(Count);
        w.WriteByte(BZoneOnly ? (byte)1 : (byte)0);
        w.WriteByte(0);
        w.WriteByte(0);
    }

    public static TreasureSetRecord Read(ReadOnlySpan<byte> b)
    {
        string name = DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(b[..25]));
        return new TreasureSetRecord(
            Name: name,
            TreasureSetId: System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b[25..]),
            Count: System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b[27..]),
            BZoneOnly: b[29] != 0);
    }
}

/// <summary>
/// <c>FM_S_TREASURE_SETS</c> (id 89), the server's join-time treasure-catalog
/// sync (<c>src/Inc/Messages.h:624-628</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, TREASURE_SETS, 89)
///     FM_VAR_ITEM(treasureSet); // array of TreasureSet. But ignore pargChance. You don't get (or need) that blob.
///     // useage: TreasureSet * pargTreasureSet = FM_VAR_REF(pfmTreasureSets, treasureSet);
///     short cTreasureSets;
///   END_FEDMSG
/// </code>
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 89
///   offset  4: ushort ib (blob offset, from byte 0)
///   offset  6: ushort cb (blob byte length = cTreasureSets × 32)
///   offset  8: short  cTreasureSets
///   offset 10: blob — cTreasureSets × TreasureSetRecord(32)
///   = 10 + blob bytes total
/// </code>
/// Ported per the port's packed var-item convention (var item first, fixed
/// fields next, blob appended — the same shape as <see cref="SetBriefingText"/>):
/// the <c>treasureSet</c> var-item points at an array of
/// <see cref="TreasureSetRecord"/>, and <c>cTreasureSets</c> is the count.
/// Broadcast at join-sync (P123) from the real decoded
/// <c>StaticCore.TreasureSets</c> catalog via <see cref="FromCatalog"/>, with
/// the chance blob omitted per the message's own comment.
/// </summary>
public sealed class TreasureSets : IFedMessage
{
    public const ushort Fmid = 89;

    /// <summary>Wire width of the fixed region before the blob:
    /// header(4) + one ib/cb var pair(4) + cTreasureSets(2) = 10.</summary>
    public const int FixedSize = FedMessageHeader.HeaderSize + VarItem.ItemSize + 2;

    /// <summary>The catalog of treasure sets, in catalog order.</summary>
    public TreasureSetRecord[] Sets = [];

    public ushort GetFmid() => Fmid;

    /// <summary>Build the message from the decoded static-core catalog
    /// (<c>StaticCore.TreasureSets</c>), dropping the per-entry chance blob.</summary>
    public static TreasureSets FromCatalog(IEnumerable<DataTreasureSetIGC> catalog)
    {
        var sets = new List<TreasureSetRecord>();
        foreach (var ts in catalog)
            sets.Add(new TreasureSetRecord(ts.Name, ts.TreasureSetId, (short)ts.Entries.Length, ts.BZoneOnly));
        return new TreasureSets { Sets = sets.ToArray() };
    }

    public byte[] Encode()
    {
        int blobLen = Sets.Length * TreasureSetRecord.Size;
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ib @4
        int slotCb = w.Reserve(2);  // cb @6
        w.WriteI16((short)Sets.Length);   // cTreasureSets @8
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"TREASURE_SETS internal layout drift: {w.Position} != {FixedSize}");

        // blob at the fixed-region end (ib = offset from byte 0)
        int ib = w.Position;
        foreach (var s in Sets) s.Write(ref w);
        w.WriteU16At(slotIb, (ushort)ib);
        w.WriteU16At(slotCb, (ushort)(w.Position - ib));
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg
        return buf;
    }

    public static TreasureSets Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"TREASURE_SETS needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected TREASURE_SETS fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));   // the single ib/cb pair
        short count = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(buf[8..]);
        var sets = new List<TreasureSetRecord>();

        if (it.IsPresent && count > 0)
        {
            if (it.Ib < 0 || it.Ib + it.Cb > buf.Length)
                throw new ArgumentException($"TREASURE_SETS blob [{it.Ib},{it.Ib + it.Cb}) out of range for length {buf.Length}");
            var blob = buf.Slice(it.Ib, it.Cb);
            for (int i = 0; i < count; i++)
            {
                int off = i * TreasureSetRecord.Size;
                if (off + TreasureSetRecord.Size > blob.Length)
                    throw new ArgumentException($"TREASURE_SETS declares {count} sets but blob is only {blob.Length} bytes");
                sets.Add(TreasureSetRecord.Read(blob.Slice(off, TreasureSetRecord.Size)));
            }
        }
        else if (count != 0)
        {
            throw new ArgumentException($"TREASURE_SETS declares {count} sets but the treasureSet var-item is absent");
        }

        return new TreasureSets { Sets = sets.ToArray() };
    }
}
