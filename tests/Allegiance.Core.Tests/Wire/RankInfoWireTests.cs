using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level + loader-fidelity tests for the P130 rank-table
/// codec <see cref="RankInfo"/> (FM_S_RANK_INFO, 13). Layout pinned against
/// native's send (FedSrv.CPP:998-1001: <c>FM_VAR_PARM(g.vRankInfo,
/// sizeof(RankInfo)*cRankInfo)</c> then <c>cRanks=cRankInfo</c>) and the
/// client's memcpy decode (clintlib/appmsg.cpp:3377-3381), with
/// <c>sizeof(RankInfo)=36</c> (Messages.h:139-145, default MSVC packing —
/// outside the file's <c>#pragma pack(1)</c> regions) and a 10-byte fixed
/// region (var pair + short count) — the identical FM_VAR_ITEM array shape
/// TreasureSets already ports. Composed with the declarative
/// <see cref="ByteBuf"/> builder so a regression in the encoder surfaces as a
/// byte mismatch against an independently-derived expectation.
/// </summary>
public class RankInfoWireTests
{
    [Fact]
    public void FixedSize_IsTen()
    {
        // header(4) + one ib/cb var pair(4) + cRanks short(2) = 10.
        Assert.Equal(10, RankInfo.FixedSize);
        Assert.Equal(36, RankInfoEntry.Size);   // sizeof(RankInfo), default MSVC packing
    }

    [Fact]
    public void RoundTrips_RealCorpusShape()
    {
        var msg = new RankInfo
        {
            Ranks =
            [
                new RankInfoEntry(RequiredRanking: 0, Civ: -1, Rank: 0, RankName: "Newbie"),
                new RankInfoEntry(RequiredRanking: 0, Civ: -1, Rank: 39, RankName: "Master 9"),
            ],
        };
        var d = RankInfo.Decode(msg.Encode());
        Assert.Equal(RankInfo.Fmid, d.GetFmid());
        Assert.Equal(2, d.Ranks.Length);
        Assert.Equal((0, (short)-1, (short)0, "Newbie"), (d.Ranks[0].RequiredRanking, d.Ranks[0].Civ, d.Ranks[0].Rank, d.Ranks[0].RankName));
        Assert.Equal((0, (short)-1, (short)39, "Master 9"), (d.Ranks[1].RequiredRanking, d.Ranks[1].Civ, d.Ranks[1].Rank, d.Ranks[1].RankName));
    }

    [Fact]
    public void RoundTrips_EmptyTable()
    {
        var msg = new RankInfo();
        var d = RankInfo.Decode(msg.Encode());
        Assert.Empty(d.Ranks);
    }

    [Fact]
    public void EmitsExactBytes_SingleEntry()
    {
        // One entry: Rank=0, Name="Newbie", civ=-1, requiredRanking=0.
        // cbmsg = 10 fixed + 36 blob = 46; ib = 10; cb = 36; cRanks = 1.
        var msg = new RankInfo { Ranks = [new RankInfoEntry(RequiredRanking: 0, Civ: -1, Rank: 0, RankName: "Newbie")] };
        var got = msg.Encode();

        var exp = ByteBuf.Build(46, w =>
        {
            w.U16(46);          // cbmsg
            w.U16(13);          // fmid
            w.U16(10);          // ib = blob starts right after the 10-byte fixed region
            w.U16(36);          // cb = 1 × sizeof(RankInfo)
            w.I16(1);           // cRanks
            w.I32(0);           // requiredRanking
            w.I16(-1);          // civ = -1 (standalone)
            w.I16(0);           // rank = 0
            // "Newbie" (6 chars) NUL-padded to 25
            foreach (var c in "Newbie") w.Byte((byte)c);
            w.Pad(25 - 6);
            w.Pad(3);           // trailing struct-alignment pad to sizeof 36
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void TruncatesNameTo25Chars()
    {
        var msg = new RankInfo { Ranks = [new RankInfoEntry(0, -1, 1, new string('x', 40))] };
        var d = RankInfo.Decode(msg.Encode());
        Assert.Equal(new string('x', 25), d.Ranks[0].RankName);
    }

    [Fact]
    public void RejectsWrongFmid()
    {
        var bytes = new RankInfo { Ranks = [new RankInfoEntry(0, -1, 0, "N")] }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 254;
        Assert.Throws<ArgumentException>(() => RankInfo.Decode(badFmid));
    }

    [Fact]
    public void RejectsTooShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => RankInfo.Decode(new byte[9]));
        Assert.Throws<ArgumentException>(() => RankInfo.Decode([]));
    }

    [Fact]
    public void Rejects_MismatchedCbmsg()
    {
        var bytes = new RankInfo { Ranks = [new RankInfoEntry(0, -1, 0, "Y")] }.Encode();
        var badCb = (byte[])bytes.Clone();
        badCb[0] = 0x40;   // 64 != actual cbmsg → mismatch → reject
        Assert.Throws<ArgumentException>(() => RankInfo.Decode(badCb));
    }

    /// <summary>Loader fidelity against the REAL <c>&lt;artwork&gt;/ranks.txt</c>
    /// native format, incl. the trailing-duplicate-behind-the-header quirk: the
    /// count header drives the loop, so a 41st line past a "40" header is dead
    /// data (native's <c>fscanf</c> loop never reads it). Uses a temp file with
    /// the same shape so the test is hermetic.</summary>
    [Fact]
    public void LoaderParsesNativeFormat_Faithfully()
    {
        // header says 2; file carries a 3rd (dead) line — exactly the real
        // file's "40" header + 41 lines with a duplicate "Master 9" tail.
        var tmp = Path.Combine(Path.GetTempPath(), $"ranks_p130_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tmp, "2\n0,Newbie\n1,Novice 1\n2,Novice 2\n");
            var tbl = RankInfo.FromRanksFile(tmp);
            Assert.Equal(2, tbl.Ranks.Length);          // header count governs; 3rd line ignored
            Assert.Equal((0, (short)-1, (short)0, "Newbie"), (tbl.Ranks[0].RequiredRanking, tbl.Ranks[0].Civ, tbl.Ranks[0].Rank, tbl.Ranks[0].RankName));
            Assert.Equal((0, (short)-1, (short)1, "Novice 1"), (tbl.Ranks[1].RequiredRanking, tbl.Ranks[1].Civ, tbl.Ranks[1].Rank, tbl.Ranks[1].RankName));
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    [Fact]
    public void Loader_MissingFile_YieldsEmptyTable()
    {
        var tbl = RankInfo.FromRanksFile("/definitely/not/a/real/path/ranks.txt");
        Assert.Empty(tbl.Ranks);
    }

    [Fact]
    public void Loader_UnparseableHeader_YieldsEmptyTable()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ranks_p130bad_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tmp, "not-a-number\n0,Newbie\n");
            Assert.Empty(RankInfo.FromRanksFile(tmp).Ranks);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
