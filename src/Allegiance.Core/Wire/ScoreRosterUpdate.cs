using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// One per-player roster record inside the <see cref="ScoreRosterUpdate"/>
/// (209) message's <c>FM_VAR_ITEM(rgPlayers)</c> blob.
/// <para><b>This port's own shape</b> — native has no in-mission live
/// scoreboard wire message (its client reads the score object directly from
/// the local IGC sim, and its FM_S_PLAYERINFO(10) is a one-shot join-time
/// roster snapshot whose stats fields this server leaves at 0). So this
/// record mirrors the convention the P194 <c>GameOverPlayerEntry</c> already
/// established for reporting P193's <c>PlayerScore</c> to the renderer-only
/// MonoGame client (no static core to read scores from): a fixed
/// <c>characterName[c_cbName]</c> name followed by small numeric columns,
/// padded to natural (MSVC) alignment. It is a deliberate redesign, not a
/// byte-compat claim against a real native client.</para>
/// <para><b>Fields &amp; layout.</b> Kills/Deaths come straight from P193's
/// <c>PlayerScore</c> (P215 now mutates them on real combat); SideId is the
/// player ship's real side. Both are carried as 16-bit to match the wire
/// tradition (native's <c>PLAYERINFO</c> uses <c>short</c> nKills/nDeaths,
/// <c>PlayerInfo.cs</c>) and are ample for in-mission tallies. P275 widens the
/// record to also carry the local player's own <c>Rank</c> (P193, a short),
/// <c>Score</c> and <c>CombatRating</c> (both P193 floats) so the periodic
/// roster — not just the game-over <c>GameOverPlayers</c>(63) snapshot —
/// surfaces them live to the renderer-only MonoGame client (the Pilot Info
/// overlay's feed). These three were previously only ever sent at game-over
/// (<c>Rank</c>/<c>Score</c> via 63; <c>CombatRating</c> never at all). Layout
/// under natural alignment:</para>
/// <code>
///   offset 0:  char characterName[25]
///   offset 25: — pad (1) — align sideId short to even
///   offset 26: short sideId
///   offset 28: short kills
///   offset 30: short deaths
///   offset 32: short rank
///   offset 34: — pad (2) — align score float to 4
///   offset 36: float score
///   offset 40: float combatRating
///   = 44 bytes
/// </code>
/// </summary>
public readonly record struct ScoreRosterEntry(
    string CharacterName,
    short SideId,
    short Kills,
    short Deaths,
    short Rank,
    float Score,
    float CombatRating)
{
    /// <summary>Wire width of one record = 44 bytes (see the layout note above).</summary>
    public const int Size = 44;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>Write a char[25]: up to 24 chars as Latin-1 bytes + a trailing NUL, then zero-pad the rest (same convention as <c>GameOverPlayerEntry</c>/<c>PlayerInfo</c>).</summary>
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

    public void Write(ref WireWriter w)
    {
        WriteFixedName(ref w, CharacterName);        // char[25] @0
        w.Advance(1);                                // pad @25 (align sideId short to even)
        w.WriteI16(SideId);                          // @26
        w.WriteI16(Kills);                           // @28
        w.WriteI16(Deaths);                          // @30
        w.WriteI16(Rank);                            // @32
        w.WriteZeros(2);                             // pad @34 (align score float to 4)
        w.WriteU32(BitConverter.SingleToUInt32Bits(Score));        // @36
        w.WriteU32(BitConverter.SingleToUInt32Bits(CombatRating)); // @40
    }

    public static ScoreRosterEntry Read(ReadOnlySpan<byte> b)
    {
        var name = ReadFixedName(b);
        return new ScoreRosterEntry(
            CharacterName: name,
            SideId: BinaryPrimitives.ReadInt16LittleEndian(b[26..]),
            Kills: BinaryPrimitives.ReadInt16LittleEndian(b[28..]),
            Deaths: BinaryPrimitives.ReadInt16LittleEndian(b[30..]),
            Rank: BinaryPrimitives.ReadInt16LittleEndian(b[32..]),
            Score: BinaryPrimitives.ReadSingleLittleEndian(b[36..]),
            CombatRating: BinaryPrimitives.ReadSingleLittleEndian(b[40..]));
    }
}

/// <summary>
/// <c>ScoreRosterUpdate</c> (id 209) — a periodic, owner-only, in-mission
/// scoreboard snapshot: every connected player on a real side, with their
/// real P193 <c>PlayerScore</c> kills/deaths. This is the smallest real wire
/// addition that lets the renderer-only MonoGame client draw a live roster.
///
/// <para><b>Why a new message (and why this shape).</b> The only existing
/// player-roster message, FM_S_PLAYERINFO(10), is sent same-side one-shot at
/// join (<c>Server.BroadcastShipStates</c>) with its nKills/nEjections/nDeaths
/// fields hard-zeroed (<c>PlayerInfo.cs</c>) — it is a structural identity
/// snapshot, not a live score stream, and it is never resent. The per-tick
/// SHIP_UPDATE carries physics, not scores. So none of the existing traffic
/// can surface P215's real Kills/Deaths to the client. Following the
/// established owner-only periodic-extension precedent (P230 <c>MissileLockUpdate</c>
/// 204, P255 <c>OwnShipMiningUpdate</c> 206, P264 <c>MissileTrackWarningUpdate</c>
/// 208) and the P194 var-item-list reporting shape, this message broadcasts
/// the whole in-mission roster (both real sides) on a slow periodic tick.</para>
///
/// <para><b>Layout</b> — a single <c>FM_VAR_ITEM</c> blob with no explicit
/// count field (count = blob length ÷ <see cref="ScoreRosterEntry.Size"/>),
/// exactly like <c>GameOverPlayers</c>(63)/<c>ProbesUpdate</c>(92):</para>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 209
///   offset 4: ushort ib (blob offset, from byte 0)
///   offset 6: ushort cb (blob byte length = n × 32)
///   offset 8: blob — n × ScoreRosterEntry(32)
///   = 8 + blob bytes total
/// </code>
/// <para>FMID 209 sits above native's real max (<c>FMID_MAX</c> ≈ 200) and the
/// port's earlier owner-only additions (204-208), so it can never collide with
/// a real wire message.</para>
/// </summary>
public sealed class ScoreRosterUpdate : IFedMessage
{
    public const ushort Fmid = 209;

    /// <summary>Wire width of the fixed region before the blob:
    /// header(4) + one ib/cb var pair(4) = 8. No fixed count field — the count
    /// is implicit in the blob length over <see cref="ScoreRosterEntry.Size"/>.</summary>
    public const int FixedSize = FedMessageHeader.HeaderSize + VarItem.ItemSize;

    /// <summary>The in-mission player roster, in the sender's collection order.</summary>
    public ScoreRosterEntry[] Entries = [];

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int blobLen = Entries.Length * ScoreRosterEntry.Size;
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ib @4
        int slotCb = w.Reserve(2);  // cb @6
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"SCORE_ROSTER_UPDATE internal layout drift: {w.Position} != {FixedSize}");

        // blob at the fixed-region end (ib = offset from byte 0)
        int ib = w.Position;
        foreach (var e in Entries) e.Write(ref w);
        w.WriteU16At(slotIb, (ushort)ib);
        w.WriteU16At(slotCb, (ushort)(w.Position - ib));
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg
        return buf;
    }

    public static ScoreRosterUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SCORE_ROSTER_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SCORE_ROSTER_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));   // the single ib/cb pair
        var entries = new List<ScoreRosterEntry>();
        if (it.IsPresent)
        {
            if (it.Ib < 0 || it.Ib + it.Cb > buf.Length)
                throw new ArgumentException($"SCORE_ROSTER_UPDATE blob [{it.Ib},{it.Ib + it.Cb}) out of range for length {buf.Length}");
            if (it.Cb % ScoreRosterEntry.Size != 0)
                throw new ArgumentException($"SCORE_ROSTER_UPDATE blob length {it.Cb} is not a multiple of {ScoreRosterEntry.Size}");
            var blob = buf.Slice(it.Ib, it.Cb);
            for (int i = 0; i < it.Cb / ScoreRosterEntry.Size; i++)
                entries.Add(ScoreRosterEntry.Read(blob.Slice(i * ScoreRosterEntry.Size, ScoreRosterEntry.Size)));
        }
        return new ScoreRosterUpdate { Entries = entries.ToArray() };
    }
}
