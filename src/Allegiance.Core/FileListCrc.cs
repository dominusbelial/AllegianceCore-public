namespace Allegiance.Core;

/// <summary>
/// P871 / autoupdate::crcFileList — the client's reported file-set identity and
/// the server's policy check.
///
/// <para><b>Native.</b> The client sends the CRC-32 of its <c>FileList.txt</c>
/// in <c>LOGON_LOBBY.crcFileList</c> /
/// <c>LOGONREQ.crcFileList</c>
/// (<c>clintlib/clintlib.cpp:1418</c>, <c>:4342-4344</c>; a value of 0 means the
/// check is disabled, as <c>g_bCheckFiles</c> does). The lobby compares it with
/// its own list CRC and, on a mismatch, answers with
/// <c>AUTO_UPDATE_INFO(253)</c> instead of a logon ack/nack — the update flow
/// (<c>lobby/client.cpp:272-281</c>; <c>Inc/Messages.h:44</c>).</para>
///
/// <para><b>Port.</b> The packaged client ships a file-set manifest
/// (<c>artwork-manifest.txt</c>, the modern <c>FileList.txt</c>); its CRC-32 is
/// the reported identity, and a server may require it to match the release it
/// supports. A value of <see cref="Unknown"/> (0) asserts nothing — the native
/// "checks disabled" case — and is refused by a server that requires a file
/// set, so a stale or forged client cannot simply omit it. This is the
/// server-side half; the lobby still advertises the release/update feed
/// (<c>/api/update/info</c>, P810) for the client-side update flow.</para>
/// </summary>
public static class FileListCrc
{
    /// <summary>Native's "no assertion" value (file checks disabled).</summary>
    public const int Unknown = 0;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>CRC-32 (IEEE 802.3, native <c>FileCRC</c>'s polynomial) of a
    /// byte blob, as the signed 32-bit value the wire carries.</summary>
    public static int Compute(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in bytes) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFFu;
        return unchecked((int)crc);
    }

    /// <summary>CRC of a list file's exact bytes (native <c>FileCRC("FileList.txt")</c>).</summary>
    public static int ComputeFile(string path)
    {
        try { return Compute(File.ReadAllBytes(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException) { return Unknown; }
    }

    /// <summary>
    /// CRC of a canonical file set: every entry rendered as
    /// <c>path\0size\0sha</c> and joined by <c>\n</c> after an ordinal path sort,
    /// so directory-enumeration order cannot change the identity. Used by the
    /// port's build/update tooling and by tests.
    /// </summary>
    public static int ComputeSet(IEnumerable<(string Path, long Size, string Sha256)> entries)
    {
        var ordered = entries
            .Select(e => (Path: e.Path.Replace('\\', '/'), e.Size, e.Sha256))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => $"{e.Path}\0{e.Size}\0{e.Sha256}\n");
        return Compute(System.Text.Encoding.UTF8.GetBytes(string.Concat(ordered)));
    }

    /// <summary>
    /// The server-side policy: does <paramref name="presented"/> satisfy the
    /// configured requirement? <paramref name="required"/> false accepts
    /// anything (the default). A required list refuses <see cref="Unknown"/>
    /// outright, and refuses anything but <paramref name="expected"/> when one is
    /// configured.
    /// </summary>
    public static bool IsAccepted(int presented, bool required, int? expected)
    {
        if (!required) return true;
        if (presented == Unknown) return false;
        return expected is not { } wanted || presented == wanted;
    }
}
