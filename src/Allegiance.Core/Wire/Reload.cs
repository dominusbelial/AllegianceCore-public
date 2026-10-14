namespace Allegiance.Core.Wire;

/// <summary>
/// One <c>RELOAD</c> entry — the native <c>ReloadData</c>
/// (<c>Inc/Messages.h:837-841</c>):
/// <code>
///   struct ReloadData
///   {
///     short         amountTransfered;
///     Mount         mount;              //always < 0
///   };
/// </code>
/// <c>Mount</c> is a signed <c>char</c> (<c>Igc/igc.h:841</c>), so a
/// successful entry always carries a <b>negative</b> cargo-bay mount in
/// <c>[-c_maxCargo, -1]</c> = <c>[-5, -1]</c> — never an equipped-slot
/// (non-negative) index (the server's handler boots any such,
/// <c>FedSrv.CPP:6531</c>). <c>amountTransfered</c> uses the native
/// <c>NA = -1</c> sentinel to mean "terminate the whole cargo item" rather
/// than transferring a specific quantity.
/// </summary>
/// <param name="AmountTransfered">How much to transfer out of the cargo item
/// (<c>&lt; 0</c> is malformed; <c>== -1</c> = terminate the whole item).</param>
/// <param name="Mount">The negative cargo-bay mount of the target slot
/// (<c>-1..-5</c>), <c>Mount</c> = signed <c>char</c>.</param>
public readonly record struct ReloadData(short AmountTransfered, sbyte Mount)
{
    /// <summary>Wire width of one entry: short + signed-char + 1 align pad = 4.</summary>
    public const int Size = 4;

    public void Write(ref WireWriter w)
    {
        w.WriteI16(AmountTransfered);
        w.WriteByte(unchecked((byte)Mount));   // offset 2: mount (signed char)
        w.WriteByte(0);                         // offset 3: align pad
    }

    public static ReloadData Read(ref WireReader r)
    {
        short amt = r.ReadI16();                          // offset 0
        sbyte mount = unchecked((sbyte)r.ReadByte());     // offset 2 (sign-extend)
        r.Advance(1);                                     // offset 3 pad
        return new ReloadData(amt, mount);
    }
}

/// <summary>
/// <c>FM_CS_RELOAD</c> (id 129, <c>Inc/Messages.h:843-846</c>):
/// <code>
///   DEFINE_FEDMSG(CS, RELOAD, 129)
///       FM_VAR_ITEM(rgReloads);
///       ShipID  shipID;
///   END_FEDMSG
/// </code>
/// A flying, unboarded ship reloads its cargo hold into its live pools /
/// equipped launchers: up to 5 <c>ReloadData</c> entries (fuel/ammo/missile/
/// chaff/dispenser, <c>FedSrv.CPP:6523</c>), one per cargo slot. The CS variant
/// is used verbatim in both directions — the client sends the entries with
/// <c>shipID</c> uninitialized/ignored, and the server re-broadcasts the same
/// bytes sector-wide with <c>shipID</c> filled in (<c>FedSrv.CPP:6651-6652</c>),
/// so a single codec serves both legs (the same convention as
/// <see cref="SwapPart"/>).
/// </summary>
/// <remarks>
/// <para><b>Layout.</b> Natural alignment (no pack region encloses this
/// message; the only pack blocks are <c>Messages.h:636-642</c> and
/// <c>:917-925</c>), all fixed members are 2-byte shorts so there is no
/// intra-struct padding beyond the per-<c>ReloadData</c> char pad:</para>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 129
///   offset 4: ushort ibReloads   (var-item pair — the FM_VAR_ITEM descriptor)
///   offset 6: ushort cbReloads   = nReloads × 4
///   offset 8: short  shipID      (ShipID)
///   offset 10: ReloadData[ nReloads ] entries (4 bytes each)
///   = 10 + 4·nReloads bytes total
/// </code>
/// <c>nReloads</c> is bounded to <c>[1, 5]</c> by the server's handler
/// (<c>TrapHackBoot((nReloads &gt; 0) &amp;&amp; (nReloads &lt;= 5))</c>,
/// <c>FedSrv.CPP:6523</c>); the codec itself accepts any whole entry count
/// (the gate is the handler's, not the layout's).</para>
/// </remarks>
public sealed class Reload : IFedMessage
{
    public const ushort Fmid = 129;

    /// <summary>Wire width of the fixed region: header(4) + var descriptor(4) + shipID(2).</summary>
    public const int FixedSize = 10;

    /// <summary>The <c>ReloadData[]</c> entries (<c>rgReloads</c>), in wire
    /// order. Up to 5 per message; each targets a negative cargo mount.</summary>
    public IReadOnlyList<ReloadData> Entries { get; init; } = [];

    /// <summary>The requesting ship's id. Ignored/uninitialized on the
    /// client→server leg (the server fills it in on the echo,
    /// <c>FedSrv.CPP:6651</c>), like <c>SWAP_PART</c>'s.</summary>
    public short ShipID;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int n = Entries.Count;
        int total = FixedSize + n * ReloadData.Size;
        var buf = new byte[total];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ibReloads @4
        int slotCb = w.Reserve(2);  // cbReloads @6
        w.WriteI16(ShipID);         // @8
        int blobStart = w.Position; // @10
        foreach (var e in Entries) e.Write(ref w);
        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(w.Position - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != total)
            throw new InvalidOperationException($"RELOAD internal layout drift: {w.Position} != {total}");
        return buf;
    }

    public static Reload Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"RELOAD needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected RELOAD fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        if (vi.Cb % ReloadData.Size != 0)
            throw new ArgumentException($"RELOAD cbReloads={vi.Cb} is not a whole number of ReloadData({ReloadData.Size}-byte) entries");
        // P654 (H-35) — validate the entries slice BEFORE pre-sizing the list,
        // so a claimed length that runs past the buffer cannot force a large
        // allocation first (the old code built `new List<ReloadData>(n)` from the
        // claimed cb before the Slice proved the range).
        if (vi.Cb > 0 && (vi.Ib + (long)vi.Cb) > buf.Length)
            throw new ArgumentException($"RELOAD entries [ib={vi.Ib}, cb={vi.Cb}] exceed buffer {buf.Length}");
        int n = vi.Cb / ReloadData.Size;

        short shipID = BitConverter.ToInt16(buf.Slice(8));

        var entries = new List<ReloadData>(n);
        WireReader r = new(buf.Slice(vi.Ib, vi.Cb));
        for (int i = 0; i < n; i++) entries.Add(ReloadData.Read(ref r));

        return new Reload { Entries = entries, ShipID = shipID };
    }
}
