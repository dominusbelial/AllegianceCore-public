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
/// roster message mirrors the established one-shot identity-seed precedent
/// (the per-(peer, ship) PLAYERINFO(10)/EXPORT(26) pair, HUD-20) rather than
/// widening the huge MISSIONDEF struct.
/// <para>P864b — <see cref="LobbyPlayerCount"/> carries the size of the team
/// lobby pool (players in the mission on no side, <c>SIDE_TEAMLOBBY</c>/−2), so
/// the client's team screen can show the lobby pool ("Not on a team") exactly
/// as native's team screen lists its lobby members (teamscreen.cpp:378,445;
/// clintlib.cpp:994). Kept as a scalar rather than a fake −2 <see cref="SideRecord"/>
/// so the many real-side consumers of <see cref="Sides"/> are undisturbed.</para>
///
/// Layout (variable-length; one 46-byte record per side):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 211
///   offset 4: short  nSides                  (number of SideRecord entries)
///   offset 6: short  lobbyPlayerCount        (P864b — team-lobby pool size)
///   offset 8: [ SideRecord × nSides ]
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

    public const int HeaderSize = 8;          // cbmsg + fmid + nSides + lobbyPlayerCount
    public const int RecordSize = 46;         // 3 shorts + char[24] + 4 floats

    /// <summary>One joinable side in the roster.</summary>
    public readonly record struct SideRecord(
        short SideId, short CivId, short PlayerCount, string Name,
        float R = 0.5f, float G = 0.5f, float B = 0.5f, float A = 1f);

    public SideRecord[] Sides = System.Array.Empty<SideRecord>();

    /// <summary>P864b — number of players currently in the team lobby (on no
    /// side) for this mission. 0 when the roster carries no lobby.</summary>
    public short LobbyPlayerCount;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[HeaderSize + RecordSize * Sides.Length];
        WireWriter w = new(buf);
        w.WriteU16(0);               // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16((short)Sides.Length);   // offset 4: nSides
        w.WriteI16(LobbyPlayerCount);      // offset 6: P864b lobby pool size
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
        if (buf.Length < 6)   // smallest header (pre-P864b 6-byte); new format needs 8
            throw new ArgumentException($"SIDE_ROSTER(211) needs >= 6 bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SIDE_ROSTER fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);
        short n = r.ReadI16();
        if (n < 0) throw new ArgumentException($"SIDE_ROSTER(211) bad record count {n}");
        // P864b — the roster gained a lobbyPlayerCount short in its header
        // (HeaderSize 6 → 8). Pre-P864b recorded wire captures (battle replays)
        // use the old 6-byte header: sniff by exact length so both formats
        // decode; old captures simply carry no lobby count (LobbyPlayerCount=0).
        bool oldFormat = buf.Length == 6 + RecordSize * n;
        bool newFormat = buf.Length == HeaderSize + RecordSize * n;
        if (!oldFormat && !newFormat)
            throw new ArgumentException($"SIDE_ROSTER(211) bad record count {n} for {buf.Length}-byte buffer");
        short lobby = oldFormat ? (short)0 : r.ReadI16();   // P864b lobby pool size (absent pre-P864b)
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
        return new SideRoster { Sides = sides, LobbyPlayerCount = lobby };
    }
}
