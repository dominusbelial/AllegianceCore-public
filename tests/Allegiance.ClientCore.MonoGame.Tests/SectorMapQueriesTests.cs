using System;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="SectorMapQueries"/> — the P460 owner-ring
/// and own-probe-count queries the starmap renders. No graphics device (same
/// discipline as <see cref="StarmapLayoutTests"/>). They pin native's
/// <c>GetClusterOwners</c> (sectormap.cpp:11-50) contract exactly: count each
/// side's non-pedestal stations and select the owner (most stations) and the
/// runner-up secondary owner, with ties broken in ASCENDING side-id order
/// (strict-greater comparisons), and native's own-probe capability filter
/// (<c>caps == 0 || caps &gt;= c_eabmRescue</c>, sectormap.cpp:328-345).
/// </summary>
public class SectorMapQueriesTests
{
    private static Func<short, bool> NoPedestals => _ => false;
    private static Func<short, bool> IsType(short pedestalType) => (short t) => t == pedestalType;

    private static (short, short) Station(short side, short type) => (side, type);

    // ---- GetClusterOwners ----------------------------------------------------

    [Fact]
    public void EmptySector_HasNoOwner()
    {
        var owners = SectorMapQueries.GetClusterOwners(Array.Empty<(short, short)>(), NoPedestals);
        Assert.Null(owners);
    }

    [Fact]
    public void AllPedestalStations_HasNoOwner()
    {
        // Every station is a pedestal type → none are countable → no owner
        // (native's nStationsOwner stays 0 and no side strictly exceeds it).
        var owners = SectorMapQueries.GetClusterOwners(
            [Station(0, 996), Station(1, 991), Station(0, 992)],
            t => t >= 990);   // all three types are pedestal
        Assert.Null(owners);
    }

    [Fact]
    public void SingleSide_OwnerOnly_NoSecondary()
    {
        var owners = SectorMapQueries.GetClusterOwners(
            [Station(2, 1), Station(2, 1), Station(2, 1)], NoPedestals);
        Assert.NotNull(owners);
        Assert.Equal((short)2, owners.Value.Owner);
        Assert.Null(owners.Value.Secondary);
    }

    [Fact]
    public void TwoVersusOne_OwnerAndSecondary()
    {
        // Side 3 has two stations, side 5 has one → owner 3, secondary 5.
        var owners = SectorMapQueries.GetClusterOwners(
            [Station(3, 1), Station(3, 1), Station(5, 1)], NoPedestals);
        Assert.NotNull(owners);
        Assert.Equal((short)3, owners.Value.Owner);
        Assert.Equal((short)5, owners.Value.Secondary);
    }

    [Fact]
    public void PedestalStations_AreIgnoredForOwnership()
    {
        // Side 0: one real + one pedestal; side 1: one real. Pedestal ignored →
        // sides tie 1-1 → lower side id (0) wins owner, 1 secondary.
        var owners = SectorMapQueries.GetClusterOwners(
            [Station(0, 1), Station(0, 888), Station(1, 2)], IsType(888));
        Assert.NotNull(owners);
        Assert.Equal((short)0, owners.Value.Owner);
        Assert.Equal((short)1, owners.Value.Secondary);
    }

    [Fact]
    public void Tie_ResolvesInAscendingSideIdOrder()
    {
        // Equal station counts 2-2: lower side id (1) becomes owner, higher (4)
        // becomes secondary (native's strict-greater walk in side-id order).
        var owners = SectorMapQueries.GetClusterOwners(
            [Station(4, 1), Station(4, 1), Station(1, 1), Station(1, 1)], NoPedestals);
        Assert.NotNull(owners);
        Assert.Equal((short)1, owners.Value.Owner);
        Assert.Equal((short)4, owners.Value.Secondary);
    }

    [Fact]
    public void ThreeWay_KeepsRunnerUpAsSecondary()
    {
        // Counts 3-2-1 (sides 0,2,5): owner 0, secondary 2, side 5 nowhere.
        var owners = SectorMapQueries.GetClusterOwners(
            [Station(0, 1), Station(0, 1), Station(0, 1),
             Station(2, 1), Station(2, 1), Station(5, 1)], NoPedestals);
        Assert.NotNull(owners);
        Assert.Equal((short)0, owners.Value.Owner);
        Assert.Equal((short)2, owners.Value.Secondary);
    }

    // ---- CountOwnProbes ------------------------------------------------------

    private static (short, short, short) Probe(short cluster, short side, short cap) => (cluster, side, cap);

    [Fact]
    public void ProbeCount_AppliesCapabilityFilterAndSectorSideGates()
    {
        // cluster 10 side 3: two countable probes (cap 0, cap 0x2000),
        // one excluded (cap 0x100 mine-style — nonzero, below rescue),
        // and one on the WRONG side (side 9) that must not count.
        var probes = new[]
        {
            Probe(10, 3, 0x0000),                              // count
            Probe(10, 3, SectorMapQueries.EabmRescue),         // count
            Probe(10, 3, 0x0100),                              // mine dispenser — not a probe count
            Probe(10, 9, 0x0000),                              // other side — not ours
            Probe(11, 3, 0x0000),                              // other cluster
        };
        Assert.Equal(2, SectorMapQueries.CountOwnProbes(probes, 10, 3));
    }

    [Fact]
    public void ProbeCount_AbsoluteRescueThreshold_InclusiveAtBoundary()
    {
        // Exactly >= c_eabmRescue counts; just below does not.
        var probes = new[]
        {
            Probe(5, 0, SectorMapQueries.EabmRescue),           // count
            Probe(5, 0, (short)(SectorMapQueries.EabmRescue - 1)), // no
            Probe(5, 0, 0x0000),                                 // count
        };
        Assert.Equal(2, SectorMapQueries.CountOwnProbes(probes, 5, 0));
    }

    [Fact]
    public void ProbeCount_NoMatchingProbes_IsZero()
    {
        Assert.Equal(0, SectorMapQueries.CountOwnProbes(Array.Empty<(short, short, short)>(), 1, 2));
    }

    // ---- SectorInfoPaneLayout probe-row integration ---------------------------

    [Fact]
    public void InfoPane_WithProbeCount_FlagsProbeRow_WithoutChangingAsteroidLines()
    {
        var samples = new[]
        {
            new SectorInfoPaneLayout.AsteroidSample(0x01, 120f),
            new SectorInfoPaneLayout.AsteroidSample(0x00, 0f),
        };
        var plain = SectorInfoPaneLayout.Build("A", samples);
        Assert.False(plain.ShowsOwnProbes);
        var lines = plain.DisplayLines;

        var withProbes = SectorInfoPaneLayout.Build("A", samples, 3);
        Assert.True(withProbes.ShowsOwnProbes);
        Assert.Equal(3, withProbes.OwnProbeCount);
        // The probe count rides as a separate glyph row, so the asteroid display
        // lines are identical to the no-probe build.
        Assert.Equal(lines, withProbes.DisplayLines);
    }

    // ---- KnownEnemySectors (P493) -------------------------------------------
    private static (short, short, bool, bool) ShipRow(short sector, short side, bool real, bool unknown) =>
        (sector, side, real, unknown);

    [Fact]
    public void KnownEnemySectors_Counts_CrossSideNotUnknown_RealIdentity_WithSector()
    {
        // Three known enemy ships in sector 1052 (side != my side 0, real
        // identity, not unknown, valid nonzero sector) → count 3 for that sector.
        var ships = new[]
        {
            ShipRow(1052, 1, true, false),
            ShipRow(1052, 1, true, false),
            ShipRow(1052, 3, true, false),
        };
        var r = SectorMapQueries.KnownEnemySectors(ships, mySideId: 0);
        Assert.Single(r);
        Assert.Equal(3, r[1052]);
    }

    [Fact]
    public void KnownEnemySectors_SameSide_IsExcluded()
    {
        var ships = new[] { ShipRow(1052, 0, true, false) };   // my own side
        Assert.Empty(SectorMapQueries.KnownEnemySectors(ships, mySideId: 0));
    }

    [Fact]
    public void KnownEnemySectors_Unknown_Hidden_Ship_IsExcluded()
    {
        // The hidden transition sets Unknown=true → the enemy is no longer a
        // known position (its marker disappears on hide).
        var ships = new[] { ShipRow(1052, 1, true, true) };
        Assert.Empty(SectorMapQueries.KnownEnemySectors(ships, mySideId: 0));
    }

    [Fact]
    public void KnownEnemySectors_NoRealIdentity_Or_NoSectorYet_IsExcluded()
    {
        // Default SideId 0 (no EXPORT yet) and default SectorId 0 (no SHIP_STATUS
        // yet) must never be misclassified as a known enemy position.
        var ships = new[]
        {
            ShipRow(1052, 0, false, false),   // no real identity → side is the C# default 0, unknowable
            ShipRow(0, 1, true, false),       // valid identity but no status yet → sector 0 sentinel
        };
        Assert.Empty(SectorMapQueries.KnownEnemySectors(ships, mySideId: 0));
    }

    [Fact]
    public void KnownEnemySectors_NoOwnSide_IsEmpty()
    {
        // Before MySideId is known nothing can be classified as enemy.
        var ships = new[] { ShipRow(1052, 1, true, false) };
        Assert.Empty(SectorMapQueries.KnownEnemySectors(ships, mySideId: null));
    }

    [Fact]
    public void KnownEnemySectors_GroupsAcrossSectors_AndAcrossSides()
    {
        var ships = new[]
        {
            ShipRow(1052, 1, true, false),
            ShipRow(1052, 2, true, false),
            ShipRow(1061, 1, true, false),
        };
        var r = SectorMapQueries.KnownEnemySectors(ships, mySideId: 0);
        Assert.Equal(2, r.Count);
        Assert.Equal(2, r[1052]);   // two different enemy sides in 1052
        Assert.Equal(1, r[1061]);
    }
}
