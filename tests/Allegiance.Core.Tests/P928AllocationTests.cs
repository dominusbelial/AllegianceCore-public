using Allegiance.TestInfra;
using Allegiance.Tools.P928;

namespace Allegiance.Core.Tests;

public sealed class P928AllocationTests
{
    // P951 — the golden is machine-independent (see docs/reports/P928_GOLDEN_PORTABLE_P951.md).
    // Hash columns (state/events/continuous), RNG draw, collision, kill and shot
    // counts must match exactly on every machine. The per-tick damage count is
    // the one deliberately tolerant column: a grazing projectile's hit/miss is a
    // knife-edge that ULP-scale position noise flips (measured ±1 event on 20 of
    // 500 ticks between the workstation and the runner image), so it is compared
    // with a tolerance instead of hashed.
    private const int PerTickDamageTolerance = 2;
    private const int TotalDamageTolerance = 32;

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Tier", "Live")]
    public void Crowded_sector_matches_pre_refactor_500_tick_golden(bool supplemental)
    {
        Require.Artwork();
        using var stream = typeof(P928AllocationTests).Assembly.GetManifestResourceStream(
            (supplemental ? "Allegiance.Core.Tests.Fixtures.P928SupplementalGolden.csv" : "Allegiance.Core.Tests.Fixtures.P928Golden.csv"))!;
        using var reader = new StreamReader(stream);
        var expected = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var actual = P928Trace.Run(supplemental);
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 2; i < expected.Length; i++)
        {
            var e = expected[i].Split(',');
            var a = actual[i].Split(',');
            Assert.Equal(e.Length, a.Length);
            Assert.Equal(e[0], a[0]);                                  // tick
            for (int c = 1; c <= 3; c++)                               // state/events/continuous hashes
                Assert.True(e[c] == a[c], $"Golden {Name(c)} mismatch at line {i + 1}:\nexpected {expected[i]}\nactual   {actual[i]}");
            Assert.True(e[4] == a[4], $"RNG draws differ at line {i + 1}:\nexpected {expected[i]}\nactual   {actual[i]}");
            Assert.True(e[5] == a[5], $"Collision count differs at line {i + 1}:\nexpected {expected[i]}\nactual   {actual[i]}");
            Assert.True(e[7] == a[7], $"Kill count differs at line {i + 1}:\nexpected {expected[i]}\nactual   {actual[i]}");
            Assert.True(e[8] == a[8], $"Shot count differs at line {i + 1}:\nexpected {expected[i]}\nactual   {actual[i]}");
            int expectedDamage = int.Parse(e[6]), actualDamage = int.Parse(a[6]);
            Assert.True(Math.Abs(expectedDamage - actualDamage) <= PerTickDamageTolerance,
                $"Damage count differs by more than {PerTickDamageTolerance} at line {i + 1}:\nexpected {expected[i]}\nactual   {actual[i]}");
        }
        var counts = actual.Skip(2).Select(line => line.Split(',')).ToArray();
        Assert.True(counts.Sum(r => int.Parse(r[5])) > 0, "No collisions exercised");
        var damageDelta = counts.Sum(r => int.Parse(r[6])) - expected.Skip(2).Sum(l => int.Parse(l.Split(',')[6]));
        Assert.True(Math.Abs(damageDelta) <= TotalDamageTolerance,
            $"Total damage drift {damageDelta} exceeds the machine-independence tolerance {TotalDamageTolerance}");
        Assert.True(counts.Sum(r => int.Parse(r[6])) > 0, "No damage exercised");
        Assert.True(counts.Sum(r => int.Parse(r[7])) > 0, "No kills exercised");
        Assert.True(counts.Sum(r => int.Parse(r[8])) > 1000, "No sustained weapon fire");
    }

    private static string Name(int column) => column switch
    {
        1 => "state-discrete", 2 => "events-discrete", _ => "continuous",
    };
}
