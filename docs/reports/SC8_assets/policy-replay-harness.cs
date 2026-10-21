using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

// Temporary SC8 measurement harness (evidence only, not part of the suite). Replays
// the same scripted pilot telemetry through the policy, so before/after differ only
// in the policy. Drop into tests/Allegiance.ClientCore.Tests, edit the absolute
// File.AppendAllText path to your worktree, run:
//   dotnet test tests/Allegiance.ClientCore.Tests --filter FullyQualifiedName~ZZSC8CountHarness
// and remove the file again; the permanent SC8 tests pin the rules.
public sealed class ZZSC8CountHarness
{
    private static readonly ResearchTactics.Target[] StrikeTargets =
    [
        new(50, IgcObjectType.Station, 1, new(0, 0, 0), true),
        new(300, IgcObjectType.Warp, 1, new(1000, 0, 0), true, DestinationSector: 2),
        new(100, IgcObjectType.Station, 2, new(5000, 0, 0), false),
    ];

    private static ResearchTactics.Pilot[] StrikeAt(double t)
    {
        var pilots = new List<ResearchTactics.Pilot>();
        for (short i = 0; i < 3; i++)
        {
            if (t >= 300) continue;                       // 1-3 dock at t=300
            if (t < 50) pilots.Add(new((short)(1 + i), 1, new Vector3(1000 + i * 60, i * 40, 0), false, true));
            else pilots.Add(new((short)(1 + i), 2, new Vector3(5000 + i * 100, 0, 0), false, true));
        }
        for (short i = 0; i < 3; i++)
        {
            float far = 9000 - (9000 - 1000) * (float)Math.Min(t / 30.0, 1);
            if (t < 30) pilots.Add(new((short)(4 + i), 1, new Vector3(far + i * 60, i * 40, 0), false, true));
            else if (t < 330) pilots.Add(new((short)(4 + i), 1, new Vector3(1000 + i * 60, i * 40, 0), false, true));
            else pilots.Add(new((short)(4 + i), 2, new Vector3(5000 + i * 100, 0, 0), false, true));
        }
        return pilots.ToArray();
    }

    private static readonly ResearchTactics.Target[] FighterTargets =
    [
        new(50, IgcObjectType.Station, 0, new(0, 0, 0), true),
        .. Enumerable.Range(0, 30).Select(i => new ResearchTactics.Target((short)(500 + i), IgcObjectType.Ship, 0,
            new Vector3(i * 120, 300, 0), false)),
    ];

    private static ResearchTactics.Pilot[] Fighters(int count)
        => Enumerable.Range(1, count).Select(i => new ResearchTactics.Pilot((short)i, 0, new Vector3(i * 100, 0, 0), false, false)).ToArray();

    [Fact]
    public void Count_orders_over_600_simulated_seconds()
    {
        var lines = new List<string>();
        var strike = new ResearchTactics();
        var strikeAll = new Dictionary<string, int>();
        for (int step = 0; step <= 1200; step++)
        {
            double t = step * 0.5;
            foreach (var o in strike.Tick(t, true, StrikeAt(t), StrikeTargets))
                strikeAll[$"{o.Command}"] = strikeAll.GetValueOrDefault($"{o.Command}") + 1;
        }
        lines.Add("strike_scenario " + string.Join(" ", strikeAll.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));

        // Pure non-strike load: 30 fighters, 30 enemy ships, no strike pilots.
        var fighters = new ResearchTactics();
        var fighterAll = new Dictionary<string, int>();
        for (int step = 0; step <= 1200; step++)
        {
            double t = step * 0.5;
            foreach (var o in fighters.Tick(t, true, Fighters(30), FighterTargets))
                fighterAll[$"{o.Command}"] = fighterAll.GetValueOrDefault($"{o.Command}") + 1;
        }
        lines.Add("fighter_scenario " + string.Join(" ", fighterAll.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));

        File.AppendAllText("/home/domingo/allegiance-pi-sc8/.scratch/sc8-count.txt", string.Join("\n", lines) + "\n");
    }
}
