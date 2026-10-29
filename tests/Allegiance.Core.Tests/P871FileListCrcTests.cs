using Allegiance.Core;

namespace Allegiance.Core.Tests;

/// <summary>
/// P871 / autoupdate::crcFileList — the file-set identity and the server policy
/// (native <c>FileCRC("FileList.txt")</c> and the lobby comparison,
/// <c>clintlib/clintlib.cpp:1418</c>, <c>lobby/client.cpp:272-281</c>).
/// </summary>
public sealed class P871FileListCrcTests
{
    [Fact]
    public void The_crc_is_the_standard_native_polynomial()
    {
        // CRC-32/ISO-HDLC ("123456789" -> 0xCBF43926).
        Assert.Equal(unchecked((int)0xCBF43926), FileListCrc.Compute("123456789"u8));
        Assert.Equal(0, FileListCrc.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void A_file_list_crc_is_the_crc_of_its_exact_bytes()
    {
        string path = Path.Combine(Path.GetTempPath(), "p871-filelist-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "PCore001.igc\nXXV_simple.igc\n");
            Assert.Equal(FileListCrc.Compute(File.ReadAllBytes(path)), FileListCrc.ComputeFile(path));
            // Appending one character changes the identity.
            File.AppendAllText(path, " ");
            Assert.NotEqual(FileListCrc.Compute("PCore001.igc\nXXV_simple.igc\n"u8), FileListCrc.ComputeFile(path));
        }
        finally
        {
            File.Delete(path);
        }
        // A missing file is native's "unknown" (0), never a throw.
        Assert.Equal(FileListCrc.Unknown, FileListCrc.ComputeFile(path));
    }

    [Fact]
    public void A_file_set_identity_ignores_enumeration_order_and_separators()
    {
        var a = new (string Path, long Size, string Sha256)[]
        {
            ("art/a.mdl", 10, "aa"),
            ("art/b.mdl", 20, "bb"),
        };
        var b = new (string Path, long Size, string Sha256)[]
        {
            ("art\\b.mdl", 20, "bb"),
            ("art/a.mdl", 10, "aa"),
        };
        Assert.Equal(FileListCrc.ComputeSet(a), FileListCrc.ComputeSet(b));
        var changed = new (string Path, long Size, string Sha256)[]
        {
            ("art/a.mdl", 10, "aa"),
            ("art/b.mdl", 21, "bb"),
        };
        Assert.NotEqual(FileListCrc.ComputeSet(a), FileListCrc.ComputeSet(changed));
    }

    [Theory]
    [InlineData(0, false, null, true)]                 // gate off: anything accepted
    [InlineData(0, true, null, false)]                 // required: omission refused (forged/stale)
    [InlineData(123, true, null, true)]                // required, no fixed release: must report a set
    [InlineData(123, true, 123, true)]                 // matching release
    [InlineData(124, true, 123, false)]                // stale/forged release
    [InlineData(-1, false, 123, true)]                 // gate off ignores a mismatch
    public void The_server_policy_refuses_omitted_and_mismatched_file_sets(int presented,
        bool required, int? expected, bool accepted)
        => Assert.Equal(accepted, FileListCrc.IsAccepted(presented, required, expected));
}
