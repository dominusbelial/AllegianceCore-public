using System.Text.Json.Serialization;

namespace Allegiance.Update;

/// <summary>
/// P810 — the signed per-file manifest, the modernisation of native's
/// <c>FileList.txt</c> + CRC list (<c>clintlib/AutoDownload.cpp:225-282</c>:
/// the client fetched the official file list, then verified local files one by
/// one and downloaded only what differed). One file per SHA-256 entry instead
/// of a CRC-32; <see cref="UpdateSignature"/> signs the exact manifest bytes.
/// </summary>
public sealed record UpdateManifest
{
    /// <summary>Semantic release version this manifest installs, e.g. <c>"2.0.0"</c>.</summary>
    public string Version { get; init; } = "";

    /// <summary>When the release pipeline built the manifest (informational).</summary>
    public DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>Every application-tree file the feed carries. <see cref="UpdateFileEntry.Path"/>
    /// is slash-separated and relative to the application root.</summary>
    public List<UpdateFileEntry> Files { get; init; } = [];
}

/// <summary>One file in an <see cref="UpdateManifest"/>. <c>Path</c> is relative
/// to the application root; <see cref="UnixMode"/> (octal, e.g. <c>"755"</c>)
/// preserves executable bits through the HTTP hop — native's FTP transfer kept
/// the FAT-style attributes implicitly; the port must re-apply them.
/// <see cref="AssetName"/> is the optional name the file is fetched under at the
/// feed root: GitHub Release assets are a FLAT namespace, so the release
/// pipeline flattens paths (<c>dir/file</c> → <c>prefix-dir__file</c>); a plain
/// static tree (nginx, GH Pages, the tests) leaves it null and fetches by path.</summary>
public sealed record UpdateFileEntry(
    string Path,
    long Size,
    string Sha256,
    string UnixMode = "644",
    string? AssetName = null);

/// <summary>
/// The feed index (<c>latest.json</c>): which manifest is current and the
/// base64 ECDSA-P256 signature over its exact bytes. The analogue of native's
/// lobby-pushed <c>AUTO_UPDATE_INFO(253)</c> payload — there the FTP location
/// plus the CRC of the official file list (<c>lobby/messageslc.h:45-52</c>);
/// here an HTTPS root plus a verifiable manifest reference.
/// </summary>
public sealed record UpdateFeedIndex(
    string Version,
    string Manifest,
    string Signature);

/// <summary>What a check concluded.</summary>
public enum UpdateCheckStatus
{
    /// <summary>Feed unreachable/invalid (details in <see cref="UpdateCheckResult.Error"/>) — nothing staged.</summary>
    Failed,
    /// <summary>The feed's version is not newer than the installed one — nothing staged.</summary>
    UpToDate,
    /// <summary>The newer version was downloaded, verified and staged; applied on next start.</summary>
    Pending,
}

/// <summary>The result of one <see cref="UpdateClient.CheckAndStageAsync"/> run.</summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    /// <summary>Feed version for UpToDate/Pending; feed URL for Failed.</summary>
    string VersionOrSource = "",
    /// <summary>Number of files that differed (the delta) — 0 unless Pending.</summary>
    int ChangedFiles = 0,
    /// <summary>Total bytes downloaded — 0 unless Pending.</summary>
    long BytesDownloaded = 0,
    string? Error = null);
