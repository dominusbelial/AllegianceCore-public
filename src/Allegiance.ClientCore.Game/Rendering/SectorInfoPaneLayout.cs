using System.Numerics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P272 — pure, headless-testable content/selection logic for the starmap's
/// per-cluster info pane (the port of native's <c>SectorInfoPane</c>,
/// <c>WinTrek/sectormap.cpp:139-538</c>). Like <see cref="StarmapLayout"/>
/// (P249) this class is deliberately PURE — <c>System.Numerics</c> only, no
/// MonoGame types on its surface — so the two things that are genuinely logic
/// (what to display from a cluster's asteroids, and how a keyboard cycle
/// advances the selected cluster) are unit-testable without a graphics device.
///
/// Native aggregates a cluster's asteroids into per-capability icon groups in
/// <c>DrawAsteroidIcons</c> (<c>sectormap.cpp:280-347</c>): an asteroid counts
/// toward a special-icon group only when <c>(aabm &amp; ~c_aabmBuildable) != 0</c>
/// (<c>sectormap.cpp:296</c>), and the helium figure in <c>GetHeliumInCluster</c>
/// (<c>sectormap.cpp:350-370</c>) sums the <c>c_aabmMineHe3 (0x01)</c> rocks'
/// ore. Both behaviours are reproduced here, operating on the client's OWN
/// decoded data (<c>DataAsteroidIGC.AabmCapabilities</c> /
/// <c>.Ore</c>, from the <c>OT_asteroid</c> export decode,
/// <c>Data/DataAsteroidIGC.cs:38,86</c>).
/// </summary>
public static class SectorInfoPaneLayout
{
    /// <summary><c>c_aabmMineHe3 = 0x01</c> (igc.h:761) — minable helium ore.</summary>
    public const short CapMineHe3 = 0x01;
    /// <summary><c>c_aabmMineLotsHe3 = 0x02</c> (igc.h:762) — richer helium.</summary>
    public const short CapMineLotsHe3 = 0x02;
    /// <summary><c>c_aabmMineGold = 0x04</c> (igc.h:763) — gold ore.</summary>
    public const short CapMineGold = 0x04;
    /// <summary><c>c_aabmBuildable = 0x08</c> (igc.h:765) — a station can be built here.</summary>
    public const short CapBuildable = 0x08;
    /// <summary><c>c_aabmSpecial = 0x10</c> (igc.h:766) — a special building can be built here.</summary>
    public const short CapSpecial = 0x10;

    /// <summary>The only capability data the pane reads off one asteroid: its
    /// ability bitmask and current ore — exactly the fields native's
    /// <c>DrawAsteroidIcons</c>/<c>GetHeliumInCluster</c> consume.</summary>
    public readonly record struct AsteroidSample(short Capabilities, float Ore);

    /// <summary>One special-capability group: a distinct full capability
    /// mask (the unit native's icon grouping keys on, sectormap.cpp:302-305) with its
    /// rock count and a human label derived from the set capability bits.</summary>
    public readonly record struct CapabilityGroup(string Label, short Mask, int Count);

    /// <summary>The info-pane content for one selected cluster, pre-rendered as
    /// ordered display lines so the renderer only paints text (logic stays here).</summary>
    public sealed class Data
    {
        /// <summary>The selected cluster's display name.</summary>
        public string Name { get; }
        /// <summary>Total asteroids in the cluster (all of them, incl. plain buildable rocks).</summary>
        public int TotalAsteroids { get; }
        /// <summary>Whether any asteroid in the cluster carries the He3-minable bit (0x01).</summary>
        public bool HasHelium { get; }
        /// <summary>Summed ore over the He3-capable rocks (native GetHeliumInCluster).</summary>
        public float HeliumOre { get; }
        /// <summary>The per-capability special groups (buildable-only rocks excluded, per native).</summary>
        public IReadOnlyList<CapabilityGroup> Groups { get; }
        /// <summary>The final ordered lines the renderer paints.</summary>
        public IReadOnlyList<string> DisplayLines { get; }
        /// <summary>
        /// P460 — whether the pane shows the own-probe row, and the probe count
        /// it carries (0 is a legitimate count). Kept OUT of <see cref="DisplayLines"/>
        /// because the renderer draws it as a distinct probe-glyph row in the own
        /// side's colour (native sectormap.cpp:338-344 draws the probe icon +
        /// count there), not as a plain text line.
        /// </summary>
        public bool ShowsOwnProbes { get; }
        /// <summary>The count of the player's own probes in this sector
        /// (<see cref="SectorMapQueries.CountOwnProbes"/>).</summary>
        public int OwnProbeCount { get; }

        public Data(string name, int totalAsteroids, bool hasHelium, float heliumOre,
            IReadOnlyList<CapabilityGroup> groups, IReadOnlyList<string> displayLines,
            bool showsOwnProbes = false, int ownProbeCount = 0)
        {
            Name = name;
            TotalAsteroids = totalAsteroids;
            HasHelium = hasHelium;
            HeliumOre = heliumOre;
            Groups = groups;
            DisplayLines = displayLines;
            ShowsOwnProbes = showsOwnProbes;
            OwnProbeCount = ownProbeCount;
        }
    }

    /// <summary>
    /// Aggregate a selected cluster's asteroids into the info-pane content.
    /// Special groups are keyed by the full capability mask after excluding
    /// buildable-only rocks (sectormap.cpp:296-305); the
    /// helium figure sums only the <c>c_aabmMineHe3</c> rocks' ore
    /// (sectormap.cpp:360-366). TotalAsteroids counts every rock in the cluster.
    /// </summary>
    public static Data Build(string clusterName, IReadOnlyList<AsteroidSample> asteroids)
        => Build(clusterName, asteroids, null);

    /// <summary>
    /// P460 — same as <see cref="Build(string,IReadOnlyList{AsteroidSample})"/> but
    /// additionally flags the pane to show the own-probe row carrying
    /// <paramref name="ownProbeCount"/>. The probe count is a separate concept
    /// from the asteroid aggregation (it comes from the client's probes, resolved
    /// via <see cref="SectorMapQueries.CountOwnProbes"/>), so it rides as a
    /// dedicated field the renderer paints as its glyph row — <c>null</c>
    /// <paramref name="ownProbeCount"/> (the default) suppresses the row entirely,
    /// keeping existing pane content byte-for-byte unchanged.
    /// </summary>
    public static Data Build(string clusterName, IReadOnlyList<AsteroidSample> asteroids, int? ownProbeCount)
    {
        var inner = BuildCore(clusterName, asteroids);
        return ownProbeCount is { } pc
            ? new Data(inner.Name, inner.TotalAsteroids, inner.HasHelium, inner.HeliumOre,
                inner.Groups, inner.DisplayLines, showsOwnProbes: true, ownProbeCount: pc)
            : inner;
    }

    private static Data BuildCore(string clusterName, IReadOnlyList<AsteroidSample> asteroids)
    {
        var byMask = new Dictionary<short, int>();
        bool hasHe3 = false;
        float he3Ore = 0f;

        foreach (var a in asteroids)
        {
            short special = (short)(a.Capabilities & ~CapBuildable);
            if (special != 0)
            {
                byMask.TryGetValue(a.Capabilities, out int cnt);
                byMask[a.Capabilities] = cnt + 1;
            }

            if ((a.Capabilities & CapMineHe3) != 0)
            {
                hasHe3 = true;
                he3Ore += a.Ore;
            }
        }

        // Most-populous special group first, native's DrawAsteroidIcons orders
        // by its icon-list insertion but ours is a text readout — sorted for
        // readability (deterministic for tests).
        var groups = byMask
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new CapabilityGroup(Label(kv.Key), kv.Key, kv.Value))
            .ToList();

        var lines = new List<string>
        {
            (string.IsNullOrEmpty(clusterName) ? "?" : clusterName) + " Sector",
            $"Asteroids: {asteroids.Count}",
        };
        foreach (var g in groups)
            lines.Add($"{g.Label}: {g.Count}");
        lines.Add($"He3 ore: {(int)he3Ore}");

        return new Data(clusterName ?? "", asteroids.Count, hasHe3, he3Ore, groups, lines);
    }

    /// <summary>A human label for a special-capability mask from its set bits.</summary>
    private static string Label(short mask)
    {
        var names = new List<string>();
        if ((mask & CapMineHe3) != 0) names.Add("He3");
        if ((mask & CapMineLotsHe3) != 0) names.Add("He3-rich");
        if ((mask & CapMineGold) != 0) names.Add("Gold");
        if ((mask & CapSpecial) != 0) names.Add("Special");
        return names.Count > 0 ? string.Join("+", names) : "Special";
    }

    /// <summary>
    /// P272 — advance the starmap's selected cluster one step forward
    /// (<paramref name="delta"/> +1) or back (-1) through the graph's cluster
    /// id order, wrapping at both ends. With no prior selection it starts at
    /// <paramref name="defaultId"/> (the player's current sector). Pure and
    /// headless-testable so the wrap-around contract is pinned.
    /// </summary>
    public static short NextSelection(short? currentId, IReadOnlyList<short> orderedIds, short defaultId, int delta)
    {
        static int IndexOf(IReadOnlyList<short> list, short value)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == value) return i;
            return -1;
        }

        if (orderedIds.Count == 0)
            return defaultId;

        // With a current selection, advance one step (wrapping at both ends).
        if (currentId is { } c && IndexOf(orderedIds, c) >= 0)
        {
            int idx = (IndexOf(orderedIds, c) + delta) % orderedIds.Count;
            if (idx < 0) idx += orderedIds.Count;
            return orderedIds[idx];
        }

        // No selection yet: show the player's own sector directly (don't move).
        // If that id isn't a real cluster, fall back to the first cluster.
        return IndexOf(orderedIds, defaultId) >= 0 ? defaultId : orderedIds[0];
    }

    /// <summary>Stack the display-line origins below a top-left point, each line
    /// <paramref name="lineHeight"/> apart — the pane's only 2-D layout math,
    /// kept in <c>System.Numerics</c> (headless-testable).</summary>
    public static IReadOnlyList<Vector2> LineOrigins(Vector2 topLeft, float lineHeight, int count)
    {
        var pts = new Vector2[count];
        for (int i = 0; i < count; i++)
            pts[i] = new Vector2(topLeft.X, topLeft.Y + i * lineHeight);
        return pts;
    }
}
