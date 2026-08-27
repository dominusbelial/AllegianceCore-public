using Allegiance.Core;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: igcdump <file.igc> [more.igc ...]");
    return 1;
}

var bad = 0;
foreach (var path in args)
{
    if (!File.Exists(path)) { Console.Error.WriteLine($"missing: {path}"); bad++; continue; }

    IgcFile f;
    try { f = IgcReader.ParseFile(path); }
    catch (Exception e) { Console.Error.WriteLine($"{Path.GetFileName(path)}: {e.Message}"); bad++; continue; }

    var mark = f.IsExact ? "exact" : $"LEFTOVER {f.BodyLength - f.BytesConsumed}B";
    var ver = f.Version is { } v ? $" version={v}" : "";
    Console.WriteLine($"{Path.GetFileName(path)}  {f.Wrapper}{ver}  " +
                      $"{f.Records.Count} records  {f.BytesConsumed}/{f.BodyLength}  [{mark}]");
    if (!f.IsExact) bad++;

    foreach (var g in f.Records.GroupBy(r => r.Type).OrderBy(g => (short)g.Key))
    {
        var sizes = g.Select(r => r.Size).Distinct().Order().ToList();
        var shown = string.Join(" ", sizes.Take(8));
        Console.WriteLine($"    {(short)g.Key,4} {g.Key,-16} {g.Count(),6}  sizes: {shown}{(sizes.Count > 8 ? " …" : "")}");
    }
}
return bad == 0 ? 0 : 1;
