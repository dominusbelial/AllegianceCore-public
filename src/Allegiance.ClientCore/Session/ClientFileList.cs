using Allegiance.Core;

namespace Allegiance.ClientCore.Session;

/// <summary>
/// P871 / autoupdate::crcFileList — the installed client's file-set identity.
/// Native computes <c>FileCRC("FileList.txt")</c> and sends it as
/// <c>LOGONREQ.crcFileList</c> (<c>clintlib/clintlib.cpp:1418,4342-4344</c>); the
/// server/lobby compares it against the release it supports and, on a mismatch,
/// runs the update flow (<c>lobby/client.cpp:272-281</c>).
/// <para>
/// The packaged port client ships <c>artwork-manifest.txt</c> (the modern
/// file list, see <c>PlayerBundleCheck</c>); its CRC-32 is the reported
/// identity. When no list is installed the value is
/// <see cref="FileListCrc.Unknown"/> (0) — native's file-checks-disabled case
/// — which a server that requires a file set refuses.
/// </para>
/// </summary>
public static class ClientFileList
{
    /// <summary>The file-list file names the client looks for, in order: the
    /// packaged manifest, then a native-style <c>FileList.txt</c> beside the
    /// executable.</summary>
    public static readonly string[] CandidateNames = ["artwork-manifest.txt", "FileList.txt"];

    private static int? _cached;

    /// <summary>The path the identity was computed from, or null when none is
    /// installed.</summary>
    public static string? ResolvedPath { get; private set; }

    /// <summary>Resolve the installed list path under <paramref name="baseDirectory"/>
    /// (default: the application directory).</summary>
    public static string? Find(string? baseDirectory = null)
    {
        string dir = baseDirectory ?? AppContext.BaseDirectory;
        foreach (string name in CandidateNames)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>The reported CRC-32, or <see cref="FileListCrc.Unknown"/> when
    /// no file list is installed. Cached after the first call (the application
    /// directory does not change under a running client).</summary>
    public static int Compute(string? baseDirectory = null)
    {
        if (baseDirectory is null && _cached is { } cached) return cached;
        string? path = Find(baseDirectory);
        ResolvedPath = path;
        int crc = path is null ? FileListCrc.Unknown : FileListCrc.ComputeFile(path);
        if (baseDirectory is null) _cached = crc;
        return crc;
    }

    /// <summary>Test seam: forget the cached value.</summary>
    public static void ResetCache()
    {
        _cached = null;
        ResolvedPath = null;
    }
}
