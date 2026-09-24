using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>SIDE_ROSTER</c> (id 211) — a port-local server→client snapshot of the
/// mission's joinable sides, pushed to a peer when it lands in the post-connect
/// <b>side-select</b> stage (P281) so the client can render a real side-choice
/// screen without hardcoding side/civ identities. This is a port extension, not
/// a native protocol message: native carries the equivalent roster inside
/// <c>MISSIONDEF(38)</c> (side display names in <c>rgszName[c_cSidesMax]
/// [c_cbSideName]</c>, per-side member counts in <c>rgcPlayers[]</c>, per-side
/// civ ids in <c>misparms.rgCivID[]</c>; Messages.h:294-318), but this port's
/// <see cref="MissionDef"/> deliberately decodes only the join-flow fields and
/// the native client-side of side-select is out of scope. A single compact
/// roster message mirrors the established <c>ScoreRosterUpdate(209)</c>
/// snapshot precedent rather than widening the huge MISSIONDEF struct.
///
/// Layout (variable-length; one 46-byte record per side):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 211
///   offset 4: short  nSides                  (number of SideRecord entries)
///   offset 6: [ SideRecord × nSides ]
///   SideRecord (46 B):
///     short  sideId        (SideID; NA(-1) sentinels are skipped by sender)
///     short  civId         (civilization id for this side)
///     short  playerCount   (connected players seated on this side right now)
///     char[24] name        (ASCII side display name, null-padded)
///     float  r, g, b, a    (P363 — the side's RGBA colour, 4 × 4 B; the same
///                          16-byte zlib <c>Color</c> the native <c>DataSideIGC</c>
///                          carries, igc.h:2064, from <c>Side.ColorR..A</c>)
/// </code>
/// Entries use the mission's real <c>Side.Name</c> / <c>Side.Civilization</c>
/// / <c>Side.ColorR..A</c> data — never hardcoded.
/// </summary>
public sealed class SideRoster : IFedMessage
{
    public const ushort Fmid = 211;

    public const int HeaderSize = 6;          // cbmsg + fmid + nSides
    public const int RecordSize = 46;         // 3 shorts + char[24] + 4 floats

    /// <summary>One joinable side in the roster.</summary>
    public readonly record struct SideRecord(
        short SideId, short CivId, short PlayerCount, string Name,
        float R = 0.5f, float G = 0.5f, float B = 0.5f, float A = 1f);

    public SideRecord[] Sides = System.Array.Empty<SideRecord>();

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[HeaderSize + RecordSize * Sides.Length];
        WireWriter w = new(buf);
        w.WriteU16(0);               // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16((short)Sides.Length);   // offset 4: nSides
        foreach (var s in Sides)
        {
            w.WriteI16(s.SideId);
            w.WriteI16(s.CivId);
            w.WriteI16(s.PlayerCount);
            var name = new byte[24];
            int n = Math.Min(s.Name.Length, 24);
            if (n > 0) Encoding.Latin1.GetBytes(s.Name.Substring(0, n), name.AsSpan(0, n));
            w.WriteBytes(name);
            w.WriteU32(BitConverter.SingleToUInt32Bits(s.R));
            w.WriteU32(BitConverter.SingleToUInt32Bits(s.G));
            w.WriteU32(BitConverter.SingleToUInt32Bits(s.B));
            w.WriteU32(BitConverter.SingleToUInt32Bits(s.A));
        }
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != buf.Length) throw new InvalidOperationException($"SIDE_ROSTER(211) layout drift: {w.Position} != {buf.Length}");
        return buf;
    }

    public static SideRoster Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < HeaderSize)
            throw new ArgumentException($"SIDE_ROSTER(211) needs >= {HeaderSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SIDE_ROSTER fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);
        short n = r.ReadI16();
        if (n < 0 || HeaderSize + RecordSize * n != buf.Length)
            throw new ArgumentException($"SIDE_ROSTER(211) bad record count {n} for {buf.Length}-byte buffer");
        var sides = new SideRecord[n];
        for (int i = 0; i < n; i++)
        {
            short sideId = r.ReadI16();
            short civId = r.ReadI16();
            short pc = r.ReadI16();
            var nameBytes = buf.Slice(r.Position, 24);
            r.Advance(24);
            int len = 24;
            while (len > 0 && nameBytes[len - 1] == 0) len--;
            float rr = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(r.Position, 4)));
            float gg = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(r.Position + 4, 4)));
            float bb = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(r.Position + 8, 4)));
            float aa = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(r.Position + 12, 4)));
            r.Advance(16);
            sides[i] = new SideRecord(sideId, civId, pc, Encoding.Latin1.GetString(nameBytes[..len]), rr, gg, bb, aa);
        }
        return new SideRoster { Sides = sides };
    }
}
