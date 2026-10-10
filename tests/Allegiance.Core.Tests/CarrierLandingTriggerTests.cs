using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P116 — the real land-on-carrier trigger, detection-only. Proof that the
/// per-tick <see cref="Sector.ResolveCarrierLandings"/> pass fires the real
/// <see cref="Ship.InGarage"/> geometric test against <b>genuine corpus
/// geometry</b> (a real carrier hull record + its real <c>.cvh</c> landing-bay
/// frames, served through the production <see cref="HullLandingBayCache"/>),
/// changes no ship state, and re-fires each tick while a lander stays put —
/// confirming the native finding that <c>LandOnCarrierEvent</c>
/// (<c>FedSrv.CPP:12814-13031</c>) is atomic with no latched docked state.
/// Constructed on a bare <see cref="Sector"/> (no Mission, no sockets) exactly
/// like P114's <see cref="LandingBayTests"/>, using real corpus data throughout.
/// </summary>
/// <remarks>
/// Geometry reference (hand-derived in P114's LandingBayTests, same corpus
/// hull): real <c>faohshcar</c> (length 300, .cvh radius 16.29676056 → scale
/// 9.204283) at the origin, identity orientation, has one land slot of 5 planes
/// bounding x∈[-14.64,14.76], y∈[-31.65,-14.16], z∈[29.98,∞) for a radius-1
/// lander. Inside point (0,-23,31); outside points (0,-23,5) (below deck plane A),
/// (0,0,31) (outside the y-band), (100,-23,31) (far in x). The lander is a real
/// tiny hull (<c>dreghpod</c>, length 2 → radius 1) so it fits the bay; its
/// capability mask is cloned to pin the exact <c>c_habmLandOnCarrier</c> bit.
/// </remarks>
public class CarrierLandingTriggerTests
{
    private const short SideA = 0;
    private const short SideB = 1;

    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    /// <summary>A real hull record by model name (decoded from the corpus,
    /// exactly like <see cref="LandingBayTests"/>).</summary>
    private static DataHullTypeIGC Hull(string modelName) =>
        PayloadDecodeTests.Hulls().First(h => h.V.ModelName == modelName).V;

    /// <summary>The production resolver/cache, fed by the real corpus's .cvh
    /// files (the same source LandingBayTests loads) — so every InGarage test
    /// runs against genuine bay geometry, never invented numbers.</summary>
    private static HullLandingBayCache RealBayCache()
    {
        return new HullLandingBayCache(name =>
        {
            var path = CvhCorpus.Files.FirstOrDefault(f => Path.GetFileName(f) == name + ".cvh");
            return path is null ? null : CvhReader.Parse(File.ReadAllText(path));
        });
    }

    /// <summary>
    /// The <b>exact</b> production loader the host installs
    /// (src/Allegiance.Server/Server.cs <c>InstantiateGames</c>): a lookup of
    /// <c>&lt;artPath&gt;/&lt;modelName&gt;.cvh</c> — the server's configured asset
    /// root, where all corpus .cvh files sit flat. Kept verbatim so the chosen
    /// production lookup path is itself exercised, not just the CvhCorpus twin.
    /// </summary>
    private static HullLandingBayCache ProductionStyleArtPathCache(string? artPath = null)
    {
        artPath ??= TestArtRoot.Value;
        return new HullLandingBayCache(name =>
        {
            var p = Path.Combine(artPath, name + ".cvh");
            return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
        });
    }

    private static Vector3 Inside = new(0f, -23f, 31f);   // deep inside faohshcar's bay (LandingBayTests)
    private static Vector3 OutBelow = new(0f, -23f, 5f);  // below deck plane A
    private static Vector3 OutYBand = new(0f, 0f, 31f);   // y=0 outside the y-band
    private static Vector3 OutFarX = new(100f, -23f, 31f);// far outside in x

    /// <summary>Real carrier (faohshcar, carries c_habmCarrier=0x400 in its
    /// actual 0x1D02 mask) at the origin, plus a lander (dreghpod cloned with
    /// exactly c_habmLandOnCarrier=0x10) at <paramref name="landerPos"/> on the
    /// given side. Both placed in a real bare sector with the real bay cache
    /// attached, so <c>sector.Update</c> exercises the genuine pass.</summary>
    private (Sector sector, Ship carrier, Ship lander) Setup(
        Vector3 landerPos, short carrierSide, short landerSide, short landerCaps = DataHullTypeIGC.HabmLandOnCarrier)
    {
        var sector = new Sector(SomeMap.Value.Clusters[0]) { LandingBays = RealBayCache() };
        var carrier = new Ship(1, Hull("faohshcar"), Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { SideId = carrierSide };
        var lander = new Ship(2, Hull("dreghpod") with { HabmCapabilities = landerCaps },
            landerPos, Vector3.Zero, Orientation.Identity)
        { SideId = landerSide };
        sector.Add(carrier);
        sector.Add(lander);
        return (sector, carrier, lander);
    }

    private static double Next(ref double now)
    {
        now += 0.008;
        return now;
    }

    private static void AssertDetection((Ship Carrier, Ship Lander) p, Ship carrier, Ship lander)
    {
        Assert.Same(carrier, p.Carrier);
        Assert.Same(lander, p.Lander);
    }

    /// <summary>Snapshot the parts of a ship's state that a real
    /// <c>LandOnCarrierEvent</c> would alter (position/velocity/loadout) so the
    /// no-state-change guarantee can be asserted crisply.</summary>
    private static (Vector3 Pos, Vector3 Vel, float Energy, short Ammo, float Frac,
        int Parts, int Weapons, short? Station) Snapshot(Ship s) =>
        (s.Position, s.Velocity, s.Energy, s.Ammo, s.Fraction, s.Parts.Count, s.Weapons.Count, s.StationId);

    /// <summary>⭐ Scenario 1 — a real lander genuinely inside a real carrier's
    /// bay fires the detection, and <b>no ship state changes</b> (the pass is
    /// detection-only; position/velocity/loadout/parts identical before/after).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lander_inside_real_carrier_bay_is_detected_with_no_state_change()
    {
        Require.Artwork();
        var (sector, carrier, lander) = Setup(Inside, SideA, SideA);
        var beforeCarrier = Snapshot(carrier);
        var beforeLander = Snapshot(lander);
        double now = 510.0;

        sector.Update(Next(ref now));

        var hits = sector.DrainCarrierLandings();
        var hit = Assert.Single(hits);
        AssertDetection(hit, carrier, lander);

        // Detection-only: the pass mutates nothing. Neither ship's
        // position/velocity/energy/ammo/fraction/loadout/parts/dock-latch moved.
        Assert.Equal(beforeCarrier, Snapshot(carrier));
        Assert.Equal(beforeLander, Snapshot(lander));
        Assert.Null(lander.StationId);                              // not docked, not relocated

        Console.WriteLine($"CARRIER-INSIDE: ship {lander.Id} inside carrier {carrier.Id} bay -> detected, neither ship state changed");
    }

    /// <summary>⭐ Scenario 2 — opposite sides: a lander parked perfectly inside
    /// the bay is never detected (the same-side friendly gate rejects it).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Opposite_side_pair_is_not_detected()
    {
        Require.Artwork();
        var (sector, _, _) = Setup(Inside, SideA, SideB);
        double now = 520.0;

        for (int i = 0; i < 30; i++) sector.Update(Next(ref now));
        Assert.Empty(sector.DrainCarrierLandings());

        Console.WriteLine("CARRIER-NEG-SIDE: opposite-side lander inside the bay -> NO detection (same-side gate)");
    }

    /// <summary>⭐ Scenario 3 — missing capability bit: a lander lacking
    /// c_habmLandOnCarrier (resp. a carrier lacking c_habmCarrier) is never
    /// detected even parked dead-centre in the bay.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lander_without_land_on_carrier_capability_is_not_detected()
    {
        Require.Artwork();
        // Lander hull cloned with a mask that omits 0x10 entirely.
        var (sector, _, _) = Setup(Inside, SideA, SideA, landerCaps: 0);
        double now = 530.0;

        for (int i = 0; i < 30; i++) sector.Update(Next(ref now));
        Assert.Empty(sector.DrainCarrierLandings());

        Console.WriteLine("CARRIER-NEG-CAPS: lander without c_habmLandOnCarrier inside the bay -> NO detection (capability gate)");
    }

    /// <summary>⭐ Scenario 4 — outside the bay's half-spaces: the lander at any
    /// of P114's hand-derived outside points (below the deck plane, outside the
    /// y-band, far in x) is never detected — proving the real geometry, not a
    /// proximity shortcut, governs.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lander_outside_every_bay_half_space_is_not_detected()
    {
        Require.Artwork();
        foreach (var outside in new[] { OutBelow, OutYBand, OutFarX })
        {
            var (sector, _, _) = Setup(outside, SideA, SideA);
            double now = 550.0;
            for (int i = 0; i < 30; i++) sector.Update(Next(ref now));
            Assert.Empty(sector.DrainCarrierLandings());
        }

        Console.WriteLine("CARRIER-NEG-OUT: lander outside the bay's half-spaces -> NO detection (real InGarage geometry)");
    }

    /// <summary>⭐ Scenario 5 — no latch: a lander held stationary inside the bay
    /// across many consecutive ticks is detected <b>every</b> tick (the drained
    /// list is non-empty after each). This is the explicit, tested proof of the
    /// atomic-function / no-persistent-docked-state finding: nothing suppresses
    /// repeat detection, because the native has no such latch at this stage and
    /// the eventual relaunch (P117) ends the condition by moving the ship out.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Stationary_lander_is_detected_every_consecutive_tick_no_latch()
    {
        Require.Artwork();
        var (sector, carrier, lander) = Setup(Inside, SideA, SideA);
        double now = 570.0;
        int detections = 0;

        for (int i = 0; i < 25; i++)
        {
            sector.Update(Next(ref now));
            var hits = sector.DrainCarrierLandings();
            // Held stationary inside → the detection re-fires each and every tick.
            var hit = Assert.Single(hits);
            AssertDetection(hit, carrier, lander);
            detections++;
        }

        Assert.Equal(25, detections);   // every consecutive tick, never suppressed
        Assert.Equal(new Vector3(0, -23, 31), lander.Position);   // still exactly where it started
        Console.WriteLine($"CARRIER-NO-LATCH: stationary lander detected on all {detections}/25 consecutive ticks (no latch/cooldown) — atomic-function finding confirmed");
    }

    /// <summary>⭐ Production lookup path — the host's exact <c>artPath</c>-root
    /// loader (<c>&lt;artPath&gt;/&lt;modelName&gt;.cvh</c>, Server.cs
    /// InstantiateGames) resolves the real faohshcar bay and drives a real
    /// detection, proving the chosen production <c>.cvh</c> resolution path
    /// (not just the CvhCorpus twin) works against genuine on-disk geometry.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Production_artpath_loader_resolves_real_bay_and_detects()
    {
        Require.Artwork();
        var sector = new Sector(SomeMap.Value.Clusters[0]) { LandingBays = ProductionStyleArtPathCache() };
        var carrier = new Ship(1, Hull("faohshcar"), Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = SideA };
        var lander = new Ship(2, Hull("dreghpod") with { HabmCapabilities = DataHullTypeIGC.HabmLandOnCarrier },
            Inside, Vector3.Zero, Orientation.Identity) { SideId = SideA };
        sector.Add(carrier);
        sector.Add(lander);

        double now = 590.0;
        sector.Update(Next(ref now));

        var hit = Assert.Single(sector.DrainCarrierLandings());
        AssertDetection(hit, carrier, lander);
        Console.WriteLine("CARRIER-PROD-PATH: artPath-root loader resolved faohshcar.cvh -> real bay -> detection fired");
    }
}
