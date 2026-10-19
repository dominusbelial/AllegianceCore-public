using System.Buffers.Binary;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class HdDdsTests
{
    [Fact]
    public void Budget_counts_small_blocks_and_selected_mip_tails_exactly()
    {
        var bc7 = new HdDds(4096, 4096, 13, 7);
        var bc5 = new HdDds(2048, 2048, 12, 5);
        var bc4 = new HdDds(2048, 2048, 12, 4);
        Assert.Equal(16, bc7.LevelBytes(12));
        Assert.Equal(48, bc7.BytesFrom(10));
        long material = bc7.BytesFrom(0) + 2 * bc5.BytesFrom(0) + 2 * bc4.BytesFrom(0);
        Assert.InRange(material / 1048576d, 37.33, 37.34);
        Assert.Equal(0, HdDds.SelectFirstMip(4096, 4096, 2400));
        Assert.Equal(5, HdDds.SelectFirstMip(4096, 4096, 64));
        Assert.Equal(6, HdDds.SelectFirstMip(4096, 4096, 1));
    }
    [Fact]
    public void Dds_rejects_truncation_arrays_bad_mips_and_wrong_colour_space()
    {
        string path = Path.Combine(Path.GetTempPath(), "hd4-" + Guid.NewGuid() + ".dds");
        var bytes = new byte[196]; // 4x4 + 2x2 + 1x1 BC7
        void U(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        "DDS "u8.CopyTo(bytes); U(4, 124); U(12, 4); U(16, 4); U(28, 3);
        U(76, 32); U(80, 4); U(84, 0x30315844); U(128, 99); U(132, 3); U(140, 1);
        try
        {
            File.WriteAllBytes(path, bytes);
            Assert.Equal(48, HdDds.Read(path, "basecolor").BytesFrom(0));
            Assert.Throws<InvalidDataException>(() => HdDds.Read(path, "normal"));
            File.WriteAllBytes(path, bytes[..^1]);
            Assert.Throws<InvalidDataException>(() => HdDds.Read(path, "basecolor"));
            foreach (var change in new[] { (140, 2u), (28, 1u), (128, 98u), (136, 4u), (16, 8192u) })
            {
                byte[] invalid = (byte[])bytes.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(change.Item1), change.Item2);
                File.WriteAllBytes(path, invalid);
                Assert.Throws<InvalidDataException>(() => HdDds.Read(path, "basecolor"));
            }
        }
        finally { File.Delete(path); }
    }
}
