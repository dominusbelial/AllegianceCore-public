using System.Buffers.Binary;

namespace Allegiance.Core;

/// <summary>
/// Reads the <c>.igc</c> container: outer wrapper detection plus the record
/// stream. Little-endian throughout; no decryption is needed (see
/// <see cref="IgcFile"/>).
/// </summary>
public static class IgcReader
{
    /// <summary>
    /// Parse a whole <c>.igc</c> file.
    /// </summary>
    /// <remarks>
    /// Two wrapper forms exist and both must be handled — the static cores
    /// carry a leading version word, the map files do not. Rather than guess
    /// from the filename, each candidate is walked and the one whose record
    /// stream lands exactly on end-of-body wins. That is unambiguous in
    /// practice: a wrong wrapper desynchronises the very first header.
    /// </remarks>
    /// <exception cref="InvalidDataException">No candidate wrapper yields a walkable stream.</exception>
    public static IgcFile Parse(ReadOnlySpan<byte> raw)
    {
        Span<(IgcWrapper w, int bodyStart, int bodyLen, int? ver)> cands = stackalloc (IgcWrapper, int, int, int?)[2];
        var n = 0;

        if (raw.Length >= 8)
        {
            var ver = BinaryPrimitives.ReadInt32LittleEndian(raw[..4]);
            var len = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(4, 4));
            if (len > 0 && len <= raw.Length - 8)
                cands[n++] = (IgcWrapper.VersionAndDataSize, 8, len, ver);
        }
        if (raw.Length >= 4)
        {
            var len = BinaryPrimitives.ReadInt32LittleEndian(raw[..4]);
            if (len > 0 && len <= raw.Length - 4)
                cands[n++] = (IgcWrapper.DataSize, 4, len, null);
        }

        // Prefer a candidate that consumes its body exactly; fall back to the
        // first that merely walks, so a malformed file still reports usefully.
        IgcFile? loose = null;
        for (var i = 0; i < n; i++)
        {
            var (w, start, len, ver) = cands[i];
            var body = raw.Slice(start, len);
            if (!TryWalk(body, out var recs, out var consumed)) continue;

            var file = new IgcFile
            {
                Wrapper = w,
                Version = ver,
                Records = recs,
                BodyLength = len,
                BytesConsumed = consumed,
            };
            if (file.IsExact) return file;
            loose ??= file;
        }

        return loose ?? throw new InvalidDataException(
            "No candidate wrapper produced a walkable record stream.");
    }

    /// <summary>
    /// Walk the record stream. Stops cleanly when fewer than a header's worth of
    /// bytes remain; reports failure only on a header that cannot be honoured
    /// (negative size, or a payload running past the end of the body).
    /// </summary>
    private static bool TryWalk(ReadOnlySpan<byte> body, out List<IgcRecord> records, out int consumed)
    {
        records = [];
        var off = 0;
        while (off + IgcRecord.HeaderSize <= body.Length)
        {
            var type = BinaryPrimitives.ReadInt16LittleEndian(body.Slice(off, 2));
            var size = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(off + 2, 4));
            if (size < 0 || off + IgcRecord.HeaderSize + size > body.Length)
            {
                consumed = off;
                return false;
            }
            records.Add(new IgcRecord((IgcObjectType)type, size, off + IgcRecord.HeaderSize));
            off += IgcRecord.HeaderSize + size;
        }
        consumed = off;
        return true;
    }

    public static IgcFile ParseFile(string path) => Parse(File.ReadAllBytes(path));
}
