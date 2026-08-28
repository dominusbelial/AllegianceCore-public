using Allegiance.Core.Collision;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: cvhdump <file.cvh> [more.cvh ...]");
    return 1;
}

var bad = 0;
foreach (var path in args)
{
    if (!File.Exists(path)) { Console.Error.WriteLine($"missing: {path}"); bad++; continue; }

    CvhFile f;
    try { f = CvhReader.Parse(File.ReadAllText(path)); }
    catch (Exception e) { Console.Error.WriteLine($"{Path.GetFileName(path)}: {e.Message}"); bad++; continue; }

    var vertices = f.Hulls.Sum(h => h.VertexCount);
    var adj = f.Hulls.Sum(h => h.AdjacencyCount);
    Console.WriteLine($"{Path.GetFileName(path)}  radius={f.Radius:F3}  ee=({f.EllipseEquation.X:F2},{f.EllipseEquation.Y:F2},{f.EllipseEquation.Z:F2})  " +
                      $"erm={f.EllipseRadiusMultiplier:F2}  {f.Hulls.Count} hulls  {vertices} vertices  {adj} adjacencies  {f.Frames.Count} frames");

    foreach (var h in f.Hulls)
        Console.WriteLine($"    hull  center=({h.Center.X:F2},{h.Center.Y:F2},{h.Center.Z:F2})  {h.VertexCount} verts  {h.AdjacencyCount} adj");
}

return bad == 0 ? 0 : 1;
