using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using System.Text.Json;

var root = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
var hulls = new[] { "fig07", "faohshbmb", "ss24a", "quizsfv2" }
    .Select(n => CvhReader.Parse(File.ReadAllText(Path.Combine(root, n + ".cvh")))).ToArray();
var rng = new Random(694);
Vector3 RandomVector(float extent) => new((float)(rng.NextDouble() * 2 - 1) * extent,
    (float)(rng.NextDouble() * 2 - 1) * extent, (float)(rng.NextDouble() * 2 - 1) * extent);
int[] Bits(Vector3 v) => [BitConverter.SingleToInt32Bits(v.X), BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z)];
void Same(Vector3 expected, Vector3 actual)
{
    if (!Bits(expected).SequenceEqual(Bits(actual))) throw new Exception($"support mismatch: {expected} vs {actual}");
}
int supportChecks = 0, narrowChecks = 0, separationChecks = 0;
for (int i = 0; i < 12000; i++)
{
    var hullA = i % 5 == 0 ? null : hulls[i % hulls.Length];
    var hullB = i % 7 == 0 ? null : hulls[(i + 1) % hulls.Length];
    var positionA = RandomVector(i % 3 == 0 ? 1 : 40);
    var positionB = i % 11 == 0 ? positionA : RandomVector(40);
    var velocityA = RandomVector(60);
    var velocityB = i % 4 == 0 ? velocityA : RandomVector(60);
    var orientationA = new Orientation(); var orientationB = new Orientation();
    if (i % 2 == 0) orientationA.Set(RandomVector(1));
    if (i % 3 == 0) orientationB.Set(RandomVector(1));
    float radiusA = 10 + (float)rng.NextDouble() * 30, radiusB = 10 + (float)rng.NextDouble() * 30;
    var a = new Collider(i, positionA, velocityA, orientationA, hullA, radiusA, false);
    var b = new Collider(-i, positionB, velocityB, orientationB, hullB, radiusB, false);
    var oldA = new BaselineCollider(i, positionA, velocityA, orientationA, hullA, radiusA, false);
    var oldB = new BaselineCollider(-i, positionB, velocityB, orientationB, hullB, radiusB, false);
    if (i % 13 == 0) a.Shape = oldA.Shape = HitShape.Ellipse;
    if (i % 17 == 0) b.Shape = oldB.Shape = HitShape.Point;
    foreach (var direction in new[] { RandomVector(1).Normalize(), new Vector3(1, 0, -0f),
        new Vector3(-1, 1, 0).Normalize(), new Vector3(0, 0, 0), new Vector3(1, 1e-7f, -1).Normalize() })
    {
        if (hullA != null)
            for (int shape = 0; shape < hullA.Hulls.Count; shape++)
            {
                Same(oldA.GetHullExtreme(shape, direction), a.GetHullExtreme(shape, direction)); supportChecks++;
            }
        Same(oldA.GetMinExtreme(a.Shape < 0 ? a.Shape : 0, positionA, orientationA, direction),
            a.GetMinExtreme(a.Shape < 0 ? a.Shape : 0, positionA, orientationA, direction)); supportChecks++;
    }
    float dt = i % 2 == 0 ? .05f : .15f;
    var expected = BaselineNarrowPhase.TestPair(oldA, oldB, dt);
    var actual = NarrowPhase.TestPair(a, b, dt);
    if (expected.Found != actual.Found || BitConverter.SingleToInt32Bits(expected.TCollision) != BitConverter.SingleToInt32Bits(actual.TCollision)
        || expected.HtsA != actual.HtsA || expected.HtsB != actual.HtsB) throw new Exception($"pair mismatch at {i}");
    narrowChecks++;
    // Coincident centres deliberately invoke random perturbation; compare only
    // deterministic, non-coincident separation inputs between the two solvers.
    if (expected.Found && positionA != positionB)
    {
        var before = BaselineSeparate.SeparateColliders(oldA, oldB, expected.TCollision, true, expected.HtsA, expected.HtsB);
        var after = Separate.SeparateColliders(a, b, actual.TCollision, true, actual.HtsA, actual.HtsB);
        Same(before.Position1, after.Position1); Same(before.Position2, after.Position2); Same(before.Normal, after.Normal);separationChecks++;
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { supportChecks, narrowChecks, separationChecks, result = "bit-identical to HEAD collision solver" }));
