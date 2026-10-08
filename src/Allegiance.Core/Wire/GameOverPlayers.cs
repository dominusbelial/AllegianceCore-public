using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core.Wire;

/// <summary>
/// One <c>GameOverPlayerEntry</c> wire record inside the
/// <see cref="GameOverPlayers"/> (63) message's <c>FM_VAR_ITEM(rgPlayerInfo)</c>
/// blob.
/// <para><b>Deliberately NOT a faithful port of native's
/// <c>PlayerEndgameInfo</c></b> (<c>Messages.h:481-488</c>). Native's struct
/// embeds two opaque members — <c>GameOverScoreObject scoring</c> and
/// <c>PersistPlayerScoreObject stats</c> — neither of which has a declaration
/// anywhere in the native checkout (the same missing-header shape as
/// <c>MissionParams</c>), and both of which aggregate this port's P193
/// <c>PlayerScore</c> superset by far (dozens of live-session counters, all
/// feeding a <c>CalculateScore</c> formula). Rather than chase wire fidelity
/// with a real native client this port will never interoperate with, this
/// task defines its own much smaller per-player payload carrying exactly what
/// P193's <c>PlayerScore</c> provides and this port can honestly report:
/// <c>{characterName, sideId, rank, score}</c>. This is a deliberate
/// substitution, not a claim of byte compatibility.</para>
/// <para><b>Character name width = <c>c_cbName</c> = 25</b> (the same fixed
/// name field <c>PlayerEndgameInfo.characterName[c_cbName]</c> uses and every
/// other ported name-bearing record already replicates via
/// <c>WriteFixedName</c>). Layout under natural (MSVC) alignment — this is
/// this port's own shape, so it mirrors the alignment convention the sibling
/// <c>SideEndgameInfo</c> already documents:</para>
/// <code>
///   offset 0:  char characterName[25]
///   offset 25: — pad (1) — align sideId short to even
///   offset 26: short sideId
///   offset 28: short rank
///   offset 30: — pad (2) — align score float to 4
///   offset 32: float score
///   = 36 bytes
/// </code>
/// The two pad regions (a real <c>short</c> aligner at 25 and a real
/// <c>float</c> aligner at 30) and the resulting 36-byte stride are part of
/// the record layout and are written/read explicitly so a regression names
/// the exact drifted field.</summary>
public readonly record struct GameOverPlayerEntry(
    string CharacterName,
    short SideId,
    short Rank,
    float Score,
    short Kills = 0,
    short Deaths = 0,
    short Ejections = 0,
    short Rescues = 0,
    short BaseKills = 0,
    short BaseCaptures = 0,
    short Artifacts = 0,
    short Flags = 0)
{
    /// <summary>Wire width of one record = 52 bytes (see the layout note below).</summary>
    public const int Size = 52;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>Write a char[25]: up to 24 chars as Latin-1 bytes + a trailing NUL, then zero-pad the rest (same convention as <c>NewShipInstance</c>/<c>PlayerInfo</c>/<c>SideEndgameInfo</c>'s <c>WriteFixedName</c>).</summary>
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
        w.WriteI16(Rank);                            // @28
        w.WriteZeros(2);                             // pad @30 (align score float to 4)
        w.WriteU32(BitConverter.SingleToUInt32Bits(Score)); // @32
        w.WriteI16(Kills);                           // @36
        w.WriteI16(Deaths);                          // @38
        w.WriteI16(Ejections);                       // @40
        w.WriteI16(Rescues);                         // @42
        w.WriteI16(BaseKills);                       // @44
        w.WriteI16(BaseCaptures);                    // @46
        w.WriteI16(Artifacts);                       // @48
        w.WriteI16(Flags);                           // @50
    }

    public static GameOverPlayerEntry Read(ReadOnlySpan<byte> b)
    {
        var name = ReadFixedName(b);
        return new GameOverPlayerEntry(
            CharacterName: name,
            SideId: BinaryPrimitives.ReadInt16LittleEndian(b[26..]),
            Rank: BinaryPrimitives.ReadInt16LittleEndian(b[28..]),
            Score: BinaryPrimitives.ReadSingleLittleEndian(b[32..]),
            Kills: BinaryPrimitives.ReadInt16LittleEndian(b[36..]),
            Deaths: BinaryPrimitives.ReadInt16LittleEndian(b[38..]),
            Ejections: BinaryPrimitives.ReadInt16LittleEndian(b[40..]),
            Rescues: BinaryPrimitives.ReadInt16LittleEndian(b[42..]),
            BaseKills: BinaryPrimitives.ReadInt16LittleEndian(b[44..]),
            BaseCaptures: BinaryPrimitives.ReadInt16LittleEndian(b[46..]),
            Artifacts: BinaryPrimitives.ReadInt16LittleEndian(b[48..]),
            Flags: BinaryPrimitives.ReadInt16LittleEndian(b[50..]));
    }
}

/// <summary>
/// <c>FM_S_GAME_OVER_PLAYERS</c> (id 63), the server's per-player endgame
/// broadcast (<c>Messages.h:481-489</c>), quoted in full:
/// <code>
///   struct PlayerEndgameInfo
///   {
///     char                      characterName[c_cbName];
///     GameOverScoreObject       scoring;
///     PersistPlayerScoreObject  stats;
///     SideID                    sideId;
///   };
///   DEFINE_FEDMSG(S, GAME_OVER_PLAYERS, 63)
///     FM_VAR_ITEM(rgPlayerInfo);
///   END_FEDMSG
/// </code>
/// The sole member is the <c>rgPlayerInfo</c> var item — an array of
/// per-player records with <b>no</b> explicit count field (the count is
/// implied by the blob's byte length over the record stride), exactly like
/// <see cref="ProbesUpdate"/> (92):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 63
///   offset 4: ushort ib (blob offset, from byte 0)
///   offset 6: ushort cb (blob byte length = n × 36)
///   offset 8: blob — n × GameOverPlayerEntry(36)
///   = 8 + blob bytes total
/// </code>
/// <para><b>Payload substitution.</b> Because native's embedded
/// <c>GameOverScoreObject</c>/<c>PersistPlayerScoreObject</c> structs are
/// undeclared in this checkout, this port reports its own smaller
/// <see cref="GameOverPlayerEntry"/> shape — <c>{characterName, sideId, rank,
/// score}</c> from P193's <c>PlayerScore</c>. Stated plainly: this is a
/// deliberate redesign, not an attempt at byte compatibility with a real
/// native client.</para>
/// <para><b>Chunking decision.</b> Native sends the player list in chunks of
/// ≤ 50 players per message (<c>fsmission.cpp:3131-3141</c>,
/// <c>nMaxPlayersPerMsg = 50</c>). This port does <b>not</b> chunk: the
/// chunking is a bandwidth-allocation concern for full 50+-player missions,
/// and this port's missions seat a handful of peers (there is no
/// <c>m_oldPlayers</c> recently-disconnected tracking either — see the
/// broadcast wiring). One message with the whole list is sent instead. This
/// is the judged-"genuinely unneeded at this port's scale" case, stated
/// explicitly.</para>
/// </summary>
public sealed class GameOverPlayers : IFedMessage
{
    public const ushort Fmid = 63;

    /// <summary>Wire width of the fixed region before the blob:
    /// header(4) + one ib/cb var pair(4) = 8. No fixed count field — the count
    /// is implicit in the blob length over <see cref="GameOverPlayerEntry.Size"/>.</summary>
    public const int FixedSize = FedMessageHeader.HeaderSize + VarItem.ItemSize;

    /// <summary>The per-player endgame list, in the sender's collection order.</summary>
    public GameOverPlayerEntry[] Entries = [];

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int blobLen = Entries.Length * GameOverPlayerEntry.Size;
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ib @4
        int slotCb = w.Reserve(2);  // cb @6
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"GAME_OVER_PLAYERS internal layout drift: {w.Position} != {FixedSize}");

        // blob at the fixed-region end (ib = offset from byte 0)
        int ib = w.Position;
        foreach (var e in Entries) e.Write(ref w);
        w.WriteU16At(slotIb, (ushort)ib);
        w.WriteU16At(slotCb, (ushort)(w.Position - ib));
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg
        return buf;
    }

    public static GameOverPlayers Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"GAME_OVER_PLAYERS needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected GAME_OVER_PLAYERS fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));   // the single ib/cb pair
        var entries = new List<GameOverPlayerEntry>();
        if (it.IsPresent)
        {
            if (it.Ib < 0 || it.Ib + it.Cb > buf.Length)
                throw new ArgumentException($"GAME_OVER_PLAYERS blob [{it.Ib},{it.Ib + it.Cb}) out of range for length {buf.Length}");
            if (it.Cb % GameOverPlayerEntry.Size != 0)
                throw new ArgumentException($"GAME_OVER_PLAYERS blob length {it.Cb} is not a multiple of {GameOverPlayerEntry.Size}");
            var blob = buf.Slice(it.Ib, it.Cb);
            for (int i = 0; i < it.Cb / GameOverPlayerEntry.Size; i++)
                entries.Add(GameOverPlayerEntry.Read(blob.Slice(i * GameOverPlayerEntry.Size, GameOverPlayerEntry.Size)));
        }
        return new GameOverPlayers { Entries = entries.ToArray() };
    }
}
