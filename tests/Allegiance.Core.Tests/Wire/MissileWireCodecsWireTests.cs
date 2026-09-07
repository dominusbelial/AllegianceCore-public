using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for the P89 wire-only missile codecs:
/// <see cref="MissileLaunchData"/> (the per-missile var-array entry),
/// <see cref="FireMissile"/> (FM_CS_FIRE_MISSILE 70) and
/// <see cref="MissileDestroyed"/> (FM_S_MISSILE_DESTROYED 83). All layouts
/// are pinned against programmatically-built expected buffers (the
/// declarative-builder technique <c>ExportShipWireTests</c> uses for its
/// part-tail case), not hand-typed hex — so a regression in the encoder
/// surfaces as a byte mismatch against an independently-derived expectation,
/// and a drift between <see cref="MissileLaunchData.Size"/> and
/// <see cref="FireMissile.FixedSize"/> as a failing length assert.
/// </summary>
public class MissileWireCodecsWireTests
{
    // ------------------------------------------------------------------ //
    // MissileLaunchData — wire type                                       //
    // ------------------------------------------------------------------ //

    [Fact]
    public void MissileLaunchData_Size_IsPaddedElementStride()
    {
        // Logical content is 38 B (3 × Vector = 36 + one short = 2). It is
        // only ever carried as the elements of an array whose count every
        // native consumer derives as cb / sizeof(MissileLaunchData)
        // (FedSrv.CPP:5447, appmsg.cpp:1611), and MSVC's default alignment
        // rounds a struct's size up to its largest member's alignment — 4 here
        // (the Vector floats) — so each element is 40 B with 2 trailing pad.
        Assert.Equal(40, MissileLaunchData.Size);
    }

    [Fact]
    public void MissileLaunchData_RoundTrips_EveryField()
    {
        var e = new MissileLaunchData(
            new Vector3(1.5f, -2.25f, 310f),
            new Vector3(0f, 1f, 0f),
            new Vector3(-12.75f, 0.03125f, 4456.5f),
            9001);
        var r = new WireReader(e.EncodeForTest());
        var decoded = MissileLaunchData.Read(ref r);
        Assert.Equal(e, decoded);       // record-struct equality covers all 4 fields
        Assert.Equal(40, r.Position);   // consumed exactly one element
    }

    [Fact]
    public void MissileLaunchData_EmitsExactFortyBytes()
    {
        var e = new MissileLaunchData(
            new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f), new Vector3(7f, 8f, 9f),
            (short)0x1234);
        var got = e.EncodeForTest();

        var exp = ByteBuf.Build(40, w =>
        {
            F(w, 1f); F(w, 2f); F(w, 3f);        // vecPosition
            F(w, 4f); F(w, 5f); F(w, 6f);        // vecForward
            F(w, 7f); F(w, 8f); F(w, 9f);        // vecVelocity
            w.I16(0x1234);                       // missileID (short)
            w.Pad(2);                            // trailing pad → 40
        });
        Assert.Equal(exp, got);
    }

    // ------------------------------------------------------------------ //
    // FireMissile (FM_CS_FIRE_MISSILE, 70)                                //
    // ------------------------------------------------------------------ //

    [Fact]
    public void FireMissile_Constants_AreDeclared()
    {
        Assert.Equal((ushort)70, FireMissile.Fmid);
        // hdr(4) + var ib/cb(4) + lock(4) + timeFired(4) + three shorts(6) +
        // pad(2) + targetType(4) + targetID(2) + bDud(1) + trailing pad(1) = 32
        Assert.Equal(32, FireMissile.FixedSize);
    }

    [Fact]
    public void FireMissile_RoundTrips_ZeroEntries()
    {
        var msg = new FireMissile
        {
            Lock = 1.5f, TimeFired = 7700u, MissileTypeId = 501, LauncherId = 502,
            ClusterId = 504, TargetType = 505, TargetId = -1, BDud = false,
        };
        var d = FireMissile.Decode(msg.Encode());
        Assert.Equal(1.5f, d.Lock);
        Assert.Equal(7700u, d.TimeFired);
        Assert.Equal(501, d.MissileTypeId);
        Assert.Equal(502, d.LauncherId);
        Assert.Equal(504, d.ClusterId);
        Assert.Equal(505, d.TargetType);
        Assert.Equal(-1, d.TargetId);
        Assert.False(d.BDud);
        Assert.Empty(d.Entries);
        Assert.Equal(32, msg.Encode().Length); // no tail
    }

    [Fact]
    public void FireMissile_RoundTrips_SingleEntry_WithTailFields()
    {
        var msg = new FireMissile
        {
            Lock = -0.25f, TimeFired = 8810u, MissileTypeId = 721, LauncherId = 723,
            ClusterId = 727, TargetType = 729, TargetId = 731, BDud = true,
            Entries =
            [
                new MissileLaunchData(new Vector3(10f, 11f, 12f), new Vector3(0f, 0f, 1f),
                                      new Vector3(100f, 110f, 120f), 741),
            ],
        };
        var got = msg.Encode();
        Assert.Equal(32 + 40, got.Length);

        var d = FireMissile.Decode(got);
        Assert.Equal(-0.25f, d.Lock);
        Assert.Equal(8810u, d.TimeFired);
        Assert.Equal(721, d.MissileTypeId);
        Assert.Equal(723, d.LauncherId);
        Assert.Equal(727, d.ClusterId);
        Assert.Equal(729, d.TargetType);
        Assert.Equal(731, d.TargetId);
        Assert.True(d.BDud);
        Assert.Single(d.Entries);
        Assert.Equal(msg.Entries[0], d.Entries[0]);
    }

    [Fact]
    public void FireMissile_RoundTrips_MultipleSalvoEntries_InOrder()
    {
        var msg = new FireMissile
        {
            Lock = 6499f, TimeFired = 980010u, MissileTypeId = 950, LauncherId = 951,
            ClusterId = 952, TargetType = 953, TargetId = 954, BDud = false,
            Entries =
            [
                new MissileLaunchData(new Vector3(1f, 1f, 1f), new Vector3(1f, 0f, 0f), new Vector3(5f, 5f, 5f), 961),
                new MissileLaunchData(new Vector3(2f, 2f, 2f), new Vector3(0.75f, 0.6614378f, 0f), new Vector3(6f, 6f, 6f), 962),
                new MissileLaunchData(new Vector3(3f, 3f, 3f), new Vector3(0f, 1f, 0f), new Vector3(7f, 7f, 7f), 963),
            ],
        };
        var d = FireMissile.Decode(msg.Encode());

        // fixed tail fields survive alongside a non-empty array
        Assert.Equal(6499f, d.Lock);
        Assert.Equal(980010u, d.TimeFired);
        Assert.Equal(950, d.MissileTypeId);
        Assert.Equal(951, d.LauncherId);
        Assert.Equal(952, d.ClusterId);
        Assert.Equal(953, d.TargetType);
        Assert.Equal(954, d.TargetId);
        Assert.False(d.BDud);
        Assert.Equal(3, d.Entries.Count);
        for (int i = 0; i < 3; i++) Assert.Equal(msg.Entries[i], d.Entries[i]);
    }

    [Fact]
    public void FireMissile_MultiSalvo_EmitsExactBytes()
    {
        var msg = new FireMissile
        {
            Lock = 513f, TimeFired = 6140u, MissileTypeId = 615, LauncherId = 618,
            ClusterId = 621, TargetType = 625, TargetId = 628, BDud = true,
            Entries =
            [
                new MissileLaunchData(new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f), new Vector3(7f, 8f, 9f), 631),
                new MissileLaunchData(new Vector3(21f, 22f, 23f), new Vector3(24f, 25f, 26f), new Vector3(27f, 28f, 29f), 641),
            ],
        };
        var got = msg.Encode();
        Assert.Equal(32 + 2 * 40, got.Length);

        var exp = ByteBuf.Build(32 + 2 * 40, w =>
        {
            w.U16((ushort)(32 + 2 * 40));   // cbmsg
            w.U16(70);                      // fmid
            w.U16(32);                      // ib missileLaunchData → fixed-region size
            w.U16(2 * 40);                  // cb missileLaunchData
            F(w, 513f);                     // lock
            w.U32(6140);                    // timeFired (Time → uint)
            w.I16(615);                     // missiletypeID (ExpendableTypeID)
            w.I16(618);                     // launcherID (ShipID)
            w.I16(621);                     // clusterID (SectorID)
            w.Pad(2);                       // pad → align targetType
            w.I32(625);                     // targetType ([v1_enum] int32)
            w.I16(628);                     // targetID (ObjectID)
            w.Byte(1);                      // bDud
            w.Pad(1);                       // trailing pad → fixed region 32

            w.Entry(1, 2, 3, 4, 5, 6, 7, 8, 9, 631);
            w.Entry(21, 22, 23, 24, 25, 26, 27, 28, 29, 641);
        });
        Assert.Equal(exp, got);

        var d = FireMissile.Decode(got);
        Assert.Equal(2, d.Entries.Count);
        Assert.Equal(msg.Entries[0], d.Entries[0]);
        Assert.Equal(msg.Entries[1], d.Entries[1]);
    }

    [Fact]
    public void FireMissile_RejectsWrongFmid()
    {
        var bytes = new FireMissile
        {
            Lock = 6600f, TimeFired = 6660u, MissileTypeId = 671, LauncherId = 672,
            ClusterId = 674, TargetType = 676, TargetId = 675, BDud = true,
            Entries = [new MissileLaunchData(Vector3.Zero, Vector3.Zero, Vector3.Zero, 678)],
        }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 245;               // corrupt the fmid low byte
        Assert.Throws<ArgumentException>(() => FireMissile.Decode(badFmid));
    }

    [Fact]
    public void FireMissile_RejectsNonWholeEntryCount()
    {
        var bytes = new FireMissile
        {
            Lock = 6800f, TimeFired = 6900u, MissileTypeId = 691, LauncherId = 692,
            ClusterId = 694, TargetType = 697, TargetId = 695, BDud = false,
            Entries =
            [
                new MissileLaunchData(new Vector3(1f, 1f, 1f), Vector3.Zero, new Vector3(1f, 1f, 1f), 681),
                new MissileLaunchData(new Vector3(2f, 2f, 2f), Vector3.Zero, new Vector3(2f, 2f, 2f), 682),
            ],
        }.Encode();
        // cb sits at message offset 6 (2 bytes); 2×40 = 80. Corrupt to 79 →
        // not a whole multiple of Size(40) → reject.
        Assert.Equal(80, bytes[6] | (bytes[7] << 8));
        bytes[6] = 79;
        Assert.Throws<ArgumentException>(() => FireMissile.Decode(bytes));
    }

    // ------------------------------------------------------------------ //
    // MissileDestroyed (FM_S_MISSILE_DESTROYED, 83)                       //
    // ------------------------------------------------------------------ //

    [Fact]
    public void MissileDestroyed_FixedSize_IsTwenty()
    {
        // hdr(4) + Vector(12) + clusterID(2) + missileID(2) = 20. No alignment
        // gaps: the 12-byte Vector is 4-aligned throughout and the two shorts
        // sit back-to-back at 16-19, so 20 is both the field sum and the struct
        // size (no member wider than 4). Cf. DataStationIGC.cs:13-20 for the
        // trailing-pad-to-alignment model.
        Assert.Equal(20, MissileDestroyed.FixedSize);
    }

    [Fact]
    public void MissileDestroyed_RoundTrips_ThreeFields()
    {
        var msg = new MissileDestroyed
        {
            Position = new Vector3(-33.5f, 4199.25f, 7255.5f),
            ClusterId = 571,
            MissileId = 583,
        };
        var d = MissileDestroyed.Decode(msg.Encode());
        Assert.Equal(msg.Position, d.Position);
        Assert.Equal(571, d.ClusterId);
        Assert.Equal(583, d.MissileId);
    }

    [Fact]
    public void MissileDestroyed_EmitsExactBytes()
    {
        var msg = new MissileDestroyed
        {
            Position = new Vector3(1f, 2f, 3f),
            ClusterId = 611,
            MissileId = 655,
        };
        var got = msg.Encode();

        var exp = ByteBuf.Build(20, w =>
        {
            w.U16(20);                   // cbmsg
            w.U16(83);                   // fmid
            F(w, 1f); F(w, 2f); F(w, 3f);// position (Vector)
            w.I16(611);                  // clusterID (SectorID)
            w.I16(655);                  // missileID (MissileID)
        });
        Assert.Equal(exp, got);
    }

    [Fact]
    public void MissileDestroyed_RejectsWrongFmid()
    {
        var bytes = new MissileDestroyed { Position = Vector3.Zero, ClusterId = 651, MissileId = 663 }.Encode();
        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 234;
        Assert.Throws<ArgumentException>(() => MissileDestroyed.Decode(badFmid));
    }

    // ------------------------------------------------------------------ //
    // helpers                                                            //
    // ------------------------------------------------------------------ //

    private static void F(ByteBuf.Writer w, float v) =>
        w.U32(BitConverter.SingleToUInt32Bits(v));
}

/// <summary>Tiny declarative little-endian byte-buffer builder so
/// expectations are expressed structurally (field-by-field) rather than as
/// long hex literals — the same idea <c>ExportShipWireTests</c>'s local
/// <c>u16/u32</c> closures serve, lifted to a reusable shape here. Includes a
/// <see cref="MissileLaunchData"/>-entry helper so multi-entry salvo
/// expectations stay compact.</summary>
internal static class ByteBuf
{
    public sealed class Writer(byte[] buf)
    {
        private int _p;
        public void U16(ushort v)
        {
            buf[_p++] = (byte)v;
            buf[_p++] = (byte)(v >> 8);
        }
        public void I16(short v) => U16(unchecked((ushort)v));
        public void U32(uint v)
        {
            buf[_p++] = (byte)v;
            buf[_p++] = (byte)(v >> 8);
            buf[_p++] = (byte)(v >> 16);
            buf[_p++] = (byte)(v >> 24);
        }
        public void I32(int v) => U32(unchecked((uint)v));
        public void Byte(byte v) => buf[_p++] = v;
        public void Pad(int n) { for (int i = 0; i < n; i++) buf[_p++] = 0; }
        public int Pos => _p;

        /// <summary>One 40-byte <see cref="MissileLaunchData"/> entry: three
        /// integer-valued vectors + a missileID + 2 trailing pad bytes.</summary>
        public void Entry(int px, int py, int pz, int fx, int fy, int fz, int vx, int vy, int vz, short id)
        {
            F3(this, px, py, pz); F3(this, fx, fy, fz); F3(this, vx, vy, vz);
            I16(id);
            Pad(2);
        }

        private static void F3(Writer w, int x, int y, int z)
        {
            w.U32(unchecked((uint)BitConverter.SingleToUInt32Bits(x)));
            w.U32(unchecked((uint)BitConverter.SingleToUInt32Bits(y)));
            w.U32(unchecked((uint)BitConverter.SingleToUInt32Bits(z)));
        }
    }

    public static byte[] Build(int len, Action<Writer> fill)
    {
        var buf = new byte[len];
        var w = new Writer(buf);
        fill(w);
        if (w.Pos != len) throw new InvalidOperationException($"test expectation drift: wrote {w.Pos} of {len} bytes");
        return buf;
    }
}

/// <summary>Expose the wire type's normal serialization as a standalone buffer
/// so byte-level tests don't have to wrap it in a full message.</summary>
internal static class MissileLaunchDataTestExt
{
    public static byte[] EncodeForTest(this MissileLaunchData e)
    {
        var buf = new byte[MissileLaunchData.Size];
        WireWriter w = new(buf);
        e.Write(ref w);
        return buf;
    }
}
