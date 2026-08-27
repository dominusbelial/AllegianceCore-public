using System.Buffers.Binary;

namespace Allegiance.Core;

/// <summary>Which outer wrapper an <c>.igc</c> file uses.</summary>
public enum IgcWrapper
{
    /// <summary><c>int32 datasize</c> then payload — the map files (111 of 210).</summary>
    DataSize,
    /// <summary><c>int32 version</c> + <c>int32 datasize</c> — the static cores (99 of 210).</summary>
    VersionAndDataSize,
}

/// <summary>
/// A parsed <c>.igc</c> file: the outer wrapper plus its record stream.
/// </summary>
/// <remarks>
/// The outer wrapper is read by <c>LoadIGCFile</c>
/// (<c>src/Igc/missionigc.cpp:94</c>), which passes the payload to
/// <c>ImissionIGC::Import</c>. On-disk data is <b>plaintext</b>: the
/// <c>munge</c> decrypt hook is wired only for the Zone Club download path, and
/// <c>ALLSRV_STANDALONE</c> passes NULL (<c>src/FedSrv/fsmission.cpp:164-166</c>).
/// </remarks>
public sealed class IgcFile
{
    public required IgcWrapper Wrapper { get; init; }
    /// <summary>Version word, present only for <see cref="IgcWrapper.VersionAndDataSize"/>.</summary>
    public int? Version { get; init; }
    public required IReadOnlyList<IgcRecord> Records { get; init; }
    /// <summary>Length of the unwrapped body, in bytes.</summary>
    public required int BodyLength { get; init; }
    /// <summary>Bytes actually consumed walking the record stream.</summary>
    public required int BytesConsumed { get; init; }

    /// <summary>
    /// True when the record stream ends exactly at end-of-body. This is the
    /// corpus-wide acceptance property: measured 210/210 on 2026-08-27. A file
    /// that parses but leaves bytes over indicates a wrong field width or
    /// offset somewhere upstream, not a benign tail.
    /// </summary>
    public bool IsExact => BytesConsumed == BodyLength;

    public int CountOf(IgcObjectType type)
    {
        var n = 0;
        foreach (var r in Records) if (r.Type == type) n++;
        return n;
    }
}
