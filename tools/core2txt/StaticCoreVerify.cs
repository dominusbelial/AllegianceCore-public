using Allegiance.Core;

namespace Allegiance.Tools.Core2Txt;

/// <summary>
/// Self-test helpers shared by the unit tests and the <c>SelfTest</c> command.
/// Verifies the P870 round trip for a core: export it to text and rebuild the
/// <c>.igc</c>; the whole file must be byte-identical to the original, which
/// implies every text-determined field survived and every non-text byte came back
/// from <c>.coremeta</c>. Per-type counts localise a failure.
/// </summary>
public static class StaticCoreVerify
{
    public sealed record Result(bool Ok, string Detail, int FirstDiff);

    public static Result VerifyFile(string igcPath, string? workRoot = null)
    {
        if (!File.Exists(igcPath))
            return new Result(false, $"missing {igcPath}", -1);

        byte[] original = File.ReadAllBytes(igcPath);
        IgcFile file;
        try { file = IgcReader.Parse(original); }
        catch (Exception e) { return new Result(false, $"parse: {e.Message}", -1); }

        string dir = Path.Combine(workRoot ?? StaticCoreRoundtrip.ScratchRoot(),
            "core2txt-rt-" + Guid.NewGuid().ToString("N"));
        try
        {
            StaticCoreRoundtrip.Export(file, dir);
            byte[] rebuilt = StaticCoreRoundtrip.BuildIgc(dir);

            IgcFile? built = null;
            try { built = IgcReader.Parse(rebuilt); }
            catch (Exception e) { return new Result(false, $"re-parse: {e.Message}", FirstDiff(original, rebuilt)); }

            string detail = CompareRecords(file, built);

            int diff = FirstDiff(original, rebuilt);
            if (diff < 0) return new Result(true, detail, -1);
            return new Result(false, $"{detail} | first byte difference at {diff} " +
                $"(original {original.Length} B, rebuilt {rebuilt.Length} B)", diff);
        }
        catch (Exception e)
        {
            return new Result(false, $"round trip threw {e.GetType().Name}: {e.Message}", -1);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    private static string CompareRecords(IgcFile a, IgcFile b)
    {
        var totals = new Dictionary<IgcObjectType, (int Ok, int Total)>();
        void Bump(IgcObjectType t, bool ok)
        {
            var v = totals.GetValueOrDefault(t);
            totals[t] = (v.Ok + (ok ? 1 : 0), v.Total + 1);
        }

        bool sameShape = a.Records.Count == b.Records.Count;
        if (sameShape)
            for (int i = 0; i < a.Records.Count; i++)
                if (a.Records[i].Type != b.Records[i].Type) { sameShape = false; break; }

        var ab = a.Body.Span;
        var bb = b.Body.Span;
        var firstBad = new List<string>();
        if (sameShape)
        {
            for (int i = 0; i < a.Records.Count; i++)
            {
                var ra = a.Records[i];
                var rb = b.Records[i];
                bool ok = ra.Size == rb.Size && ab.Slice(ra.PayloadOffset, ra.Size)
                    .SequenceEqual(bb.Slice(rb.PayloadOffset, rb.Size));
                Bump(ra.Type, ok);
                if (!ok && firstBad.Count < 6)
                {
                    if (ra.Size != rb.Size)
                        firstBad.Add($"{ra.Type}[{i}] size {ra.Size}->{rb.Size}");
                    else
                    {
                        var x = ab.Slice(ra.PayloadOffset, ra.Size);
                        var y = bb.Slice(rb.PayloadOffset, rb.Size);
                        int d = 0; while (d < x.Length && x[d] == y[d]) d++;
                        firstBad.Add($"{ra.Type}[{i}] @{d} {x[d]:X2}->{y[d]:X2}");
                    }
                }
            }
        }
        else
        {
            foreach (var ra in a.Records) Bump(ra.Type, false);
        }

        string detail = string.Join("; ", totals.OrderBy(kv => (short)kv.Key).Select(kv =>
            $"{kv.Key}: {kv.Value.Ok}/{kv.Value.Total}"));
        if (!sameShape)
            detail = $"record sequence/count differs ({a.Records.Count} vs {b.Records.Count}); " + detail;
        if (firstBad.Count > 0) detail += " | " + string.Join(", ", firstBad);
        return detail;
    }

    private static int FirstDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return a.Length == b.Length ? -1 : n;
    }

    /// <summary>Every static core in the artwork corpus (a
    /// <see cref="IgcWrapper.VersionAndDataSize"/> file whose record walk lands
    /// exactly on end-of-body). The map files are the other wrapper form.</summary>
    public static IEnumerable<string> StaticCoresIn(string artworkRoot)
    {
        if (!Directory.Exists(artworkRoot)) yield break;
        foreach (string path in Directory.EnumerateFiles(artworkRoot, "*.igc").OrderBy(p => p, StringComparer.Ordinal))
        {
            IgcFile file;
            try { file = IgcReader.Parse(File.ReadAllBytes(path)); }
            catch { continue; }
            if (file.Wrapper == IgcWrapper.VersionAndDataSize && file.IsExact && file.Records.Count > 0)
                yield return path;
        }
    }
}
