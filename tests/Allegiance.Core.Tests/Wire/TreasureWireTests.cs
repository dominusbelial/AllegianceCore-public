using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the P123 treasure codecs:
/// <see cref="TreasureSpawn"/> (FM_S_TREASURE, 28),
/// <see cref="DestroyTreasure"/> (FM_S_DESTROY_TREASURE, 30),
/// <see cref="AcquireTreasure"/> (FM_S_ACQUIRE_TREASURE, 162),
/// <see cref="TreasureAck"/> (FM_C_TREASURE_ACK, 163) and
/// <see cref="TreasureSets"/> (FM_S_TREASURE_SETS, 89). Layouts are pinned
/// against the native struct arithmetic (Messages.h:253-255, :257-261,
/// :624-628, :871-879; all five messages sit in default-MSVC-packed regions —
/// outside the two <c>#pragma pack(1)</c> blocks at :636-642/:917-925),
/// composed with the declarative <see cref="ByteBuf"/> builder — not hand-typed
/// hex — so a regression in any encoder surfaces as a byte mismatch against an
/// independently-derived expectation.
/// </summary>
public class TreasureWireTests
{
    // ------------------------------------------------------------------ //
    // TreasureSpawn (FM_S_TREASURE, 28)                                  //
    // ------------------------------------------------------------------ //

    [Fact]
    public void TreasureSpawn_FixedSize_IsFortyEight()
    {
        // header(4) + DataTreasureIGC(44) = 48, 4-aligned (the embedded struct).
        Assert.Equal(48, TreasureSpawn.FixedSize);
    }

    private static TreasureSpawn SampleSpawn() => new()
    {
        Data = new DataTreasureIGC(
            P0: [1f, 2f, 3f],
            V0: [4f, 5f, 6f],
            Lifespan: 300f,
            Time0: 98765u,
            ObjectId: 921,
            TreasureId: 9500,      // a corpus-range cash amount (500..10000)
            ClusterId: 930,
            Amount: 940,
            TreasureCode: TreasureCode.Cash,
            CreateNow: true),
    };

    [Fact]
    public void TreasureSpawn_RoundTrips_TheEmbeddedStruct()
    {
        var d = TreasureSpawn.Decode(SampleSpawn().Encode());
        Assert.Equal(TreasureSpawn.Fmid, d.GetFmid());
        Assert.Equal(new[] { 1f, 2f, 3f }, d.Data.P0);
        Assert.Equal(new[] { 4f, 5f, 6f }, d.Data.V0);
        Assert.Equal(300f, d.Data.Lifespan);
        Assert.Equal(98765u, d.Data.Time0);
        Assert.Equal((short)921, d.Data.ObjectId);
        Assert.Equal((short)9500, d.Data.TreasureId);
        Assert.Equal((short)930, d.Data.ClusterId);
        Assert.Equal((short)940, d.Data.Amount);
        Assert.Equal(TreasureCode.Cash, d.Data.TreasureCode);
        Assert.True(d.Data.CreateNow);
    }

    [Fact]
    public void TreasureSpawn_EmitsExactBytes()
    {
        var got = SampleSpawn().Encode();
        var exp = ByteBuf.Build(48, w =>
        {
            w.U16(48);          // cbmsg
            w.U16(28);          // fmid
            w.F32(1f); w.F32(2f); w.F32(3f);   // p0
            w.F32(4f); w.F32(5f); w.F32(6f);   // v0
            w.F32(300f);        // lifespan
            w.U32(98765u);      // time0
            w.I16(921);         // objectID
            w.I16(9500);        // treasureID
            w.I16(930);         // clusterID
            w.I16(940);         // amount
            w.Byte(4);          // treasureCode = c_tcCash
            w.Byte(1);          // createNow = true
            w.Pad(2);           // struct rounds to 44 (offsets 42-43)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void TreasureSpawn_RejectsWrongFmid_And_TooShort()
    {
        var bad = (byte[])SampleSpawn().Encode();
        bad[2] = 254;
        Assert.Throws<ArgumentException>(() => TreasureSpawn.Decode(bad));
        Assert.Throws<ArgumentException>(() => TreasureSpawn.Decode(new byte[47]));
        Assert.Throws<ArgumentException>(() => TreasureSpawn.Decode([]));
    }

    // ------------------------------------------------------------------ //
    // DestroyTreasure (FM_S_DESTROY_TREASURE, 30)                        //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DestroyTreasure_FixedSize_IsEight()
    {
        // header(4) + treasureID(2) + sectorID(2) = 8, no pad (2-aligned).
        Assert.Equal(8, DestroyTreasure.FixedSize);
    }

    [Fact]
    public void DestroyTreasure_RoundTrips_BothIds()
    {
        var msg = new DestroyTreasure { TreasureId = 951, SectorId = 952 };
        var d = DestroyTreasure.Decode(msg.Encode());
        Assert.Equal(DestroyTreasure.Fmid, d.GetFmid());
        Assert.Equal((short)951, d.TreasureId);
        Assert.Equal((short)952, d.SectorId);
    }

    [Fact]
    public void DestroyTreasure_EmitsExactBytes()
    {
        var got = new DestroyTreasure { TreasureId = 953, SectorId = 954 }.Encode();
        var exp = ByteBuf.Build(8, w =>
        {
            w.U16(8); w.U16(30); w.I16(953); w.I16(954);
        });
        Assert.Equal(exp, got);
    }

    // ------------------------------------------------------------------ //
    // AcquireTreasure (FM_S_ACQUIRE_TREASURE, 162)                       //
    // ------------------------------------------------------------------ //

    [Fact]
    public void AcquireTreasure_FixedSize_IsTen()
    {
        // header(4) + code char(1) + pad(1) + id(2) + amount(2) = 10, 2-aligned
        // (interior pad at 5 aligns the short; no trailing pad).
        Assert.Equal(10, AcquireTreasure.FixedSize);
    }

    [Fact]
    public void AcquireTreasure_RoundTrips_AllFields()
    {
        var msg = new AcquireTreasure { TreasureCode = (byte)TreasureCode.Cash, TreasureId = 960, Amount = 961 };
        var d = AcquireTreasure.Decode(msg.Encode());
        Assert.Equal(AcquireTreasure.Fmid, d.GetFmid());
        Assert.Equal((byte)TreasureCode.Cash, d.TreasureCode);
        Assert.Equal((short)960, d.TreasureId);
        Assert.Equal((short)961, d.Amount);
    }

    [Fact]
    public void AcquireTreasure_EmitsExactBytes_WithInteriorPad()
    {
        var got = new AcquireTreasure { TreasureCode = 4, TreasureId = 962, Amount = 963 }.Encode();
        var exp = ByteBuf.Build(10, w =>
        {
            w.U16(10); w.U16(162);
            w.Byte(4);          // treasureCode @4
            w.Byte(0);          // pad @5 (align the short)
            w.I16(962);         // treasureID @6
            w.I16(963);         // amount @8
        });
        Assert.Equal(exp, got);
    }

    // ------------------------------------------------------------------ //
    // TreasureAck (FM_C_TREASURE_ACK, 163)                               //
    // ------------------------------------------------------------------ //

    [Fact]
    public void TreasureAck_FixedSize_IsSix()
    {
        // header(4) + Mount char(1) + 1 pad to the 2-align = 6.
        Assert.Equal(6, TreasureAck.FixedSize);
    }

    [Fact]
    public void TreasureAck_RoundTrips_MountId()
    {
        // P145 correction: MountId is a REAL variable field — the handler branches
        // on pfmTA->mountID != c_mountNA (FedSrv.CPP:6099), so there is no
        // hardcoded "always NA" default. Round-trip BOTH the decline sentinel and
        // a real mount choice.
        var decl = new TreasureAck { MountId = TreasureAck.MountNA };
        var declDec = TreasureAck.Decode(decl.Encode());
        Assert.Equal(TreasureAck.Fmid, declDec.GetFmid());
        Assert.Equal(TreasureAck.MountNA, declDec.MountId);

        var acc = new TreasureAck { MountId = 4 };
        var accDec = TreasureAck.Decode(acc.Encode());
        Assert.Equal((byte)4, accDec.MountId);
        Assert.NotEqual(TreasureAck.MountNA, accDec.MountId);
    }

    [Fact]
    public void TreasureAck_EmitsExactBytes()
    {
        var got = new TreasureAck { MountId = TreasureAck.MountNA }.Encode();
        var exp = ByteBuf.Build(6, w =>
        {
            w.U16(6); w.U16(163);
            w.Byte(TreasureAck.MountNA);   // mountID @4
            w.Byte(0);                     // pad @5
        });
        Assert.Equal(exp, got);
    }

    // ------------------------------------------------------------------ //
    // TreasureSets (FM_S_TREASURE_SETS, 89)                              //
    // ------------------------------------------------------------------ //

    [Fact]
    public void TreasureSets_FixedSize_IsTen()
    {
        // header(4) + one ib/cb var pair(4) + cTreasureSets short(2) = 10.
        Assert.Equal(10, TreasureSets.FixedSize);
    }

    [Fact]
    public void TreasureSets_RoundTrips_TheCatalogRecords()
    {
        var msg = new TreasureSets
        {
            Sets =
            [
                new TreasureSetRecord("SetOne", 970, 971, false),
                new TreasureSetRecord("Two", 972, 973, true),
            ],
        };
        var d = TreasureSets.Decode(msg.Encode());
        Assert.Equal(TreasureSets.Fmid, d.GetFmid());
        Assert.Equal(2, d.Sets.Length);
        Assert.Equal("SetOne", d.Sets[0].Name);
        Assert.Equal((short)970, d.Sets[0].TreasureSetId);
        Assert.Equal((short)971, d.Sets[0].Count);
        Assert.False(d.Sets[0].BZoneOnly);
        Assert.Equal("Two", d.Sets[1].Name);
        Assert.Equal((short)972, d.Sets[1].TreasureSetId);
        Assert.Equal((short)973, d.Sets[1].Count);
        Assert.True(d.Sets[1].BZoneOnly);
    }

    [Fact]
    public void TreasureSets_EmitsExactBytes_OneRecord()
    {
        var msg = new TreasureSets { Sets = [new TreasureSetRecord("AB", 974, 975, true)] };
        var got = msg.Encode();

        var exp = ByteBuf.Build(42, w =>
        {
            w.U16(42);          // cbmsg = 10 fixed + 32 blob
            w.U16(89);          // fmid
            w.U16(10);          // ib = blob offset (right after cTreasureSets)
            w.U16(32);          // cb = one 32-byte record
            w.I16(1);           // cTreasureSets
            // blob: name[25] "AB" NUL-padded, id, count, zone, 2 pad
            w.Byte((byte)'A'); w.Byte((byte)'B');
            w.Pad(23);          // rest of name[]
            w.I16(974);         // treasureSetID
            w.I16(975);         // nTreasureData
            w.Byte(1);          // bZoneOnly
            w.Pad(2);           // head rounds to 32
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void TreasureSets_FromCatalog_BuildsFromDecodedSets()
    {
        var catalog = new[]
        {
            new DataTreasureSetIGC("Cash", 976, false,
                [new DataTreasureSetIGC.TreasureData(9000, TreasureCode.Cash, 0)]),
        };
        var msg = TreasureSets.FromCatalog(catalog);
        var rec = Assert.Single(msg.Sets);
        Assert.Equal("Cash", rec.Name);
        Assert.Equal((short)976, rec.TreasureSetId);
        Assert.Equal((short)1, rec.Count);
        Assert.False(rec.BZoneOnly);
    }

    [Fact]
    public void TreasureSets_RejectsWrongFmid_And_TooShort()
    {
        var bad = (byte[])new TreasureSets { Sets = [] }.Encode();
        bad[2] = 254;
        Assert.Throws<ArgumentException>(() => TreasureSets.Decode(bad));
        Assert.Throws<ArgumentException>(() => TreasureSets.Decode(new byte[9]));
        Assert.Throws<ArgumentException>(() => TreasureSets.Decode([]));
    }
}
