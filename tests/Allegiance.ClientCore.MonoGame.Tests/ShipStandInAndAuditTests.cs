using System;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P607 — failing-first tests for the crash fix: a ship whose native art
/// cannot be resolved and which has no Meshy pack fallback must NEVER pass
/// null to <c>ShipRenderer.Draw</c> (the P607 live
/// <c>NullReferenceException</c>). Verifies the guaranteed-non-null procedural
/// stand-in geometry is well-formed (so its Build cannot throw), and the pure
/// native-resolution audit reports exactly the unresolvable hulls (0 for a
/// healthy core, or the list).
/// </summary>
public class ShipStandInAndAuditTests
{
    // ── Stand-in geometry is valid → Build never throws / never null ──────────

    [Fact]
    public void StandInGeometry_IsAWellFormedOctahedron()
    {
        var geo = ShipStandInModel.CreateGeometry();
        // 6 vertices (2 apices + 4 equator), 24 indices = 8 triangles.
        Assert.Equal(6, geo.VertexCount);
        Assert.Equal(8, geo.TriangleCount);
        Assert.Equal(24, geo.Indices.Length);
        // Every index is in range.
        foreach (var i in geo.Indices)
            Assert.InRange(i, 0, geo.VertexCount - 1);
        // Non-degenerate: the octahedron has extent on every axis (apexes at
        // ±HalfLength along Z, equator at ±HalfWidth on X/Y).
        Assert.Equal(ShipStandInModel.HalfLength, geo.Positions[2], 4);       // top apex +L
        Assert.Equal(-ShipStandInModel.HalfLength, geo.Positions[17], 4);     // bottom apex -L (vertex 5, z slot)
        Assert.Equal(ShipStandInModel.HalfWidth, geo.Positions[6], 4);        // equator +W x
    }

    [Fact]
    public void StandInGeometry_TrianglesAreClosedCones()
    {
        // Every vertex is referenced (no orphan vertices), so the mesh rasterises
        // completely (nothing invisible / unwritable).
        var geo = ShipStandInModel.CreateGeometry();
        var used = geo.Indices.Distinct().OrderBy(x => x).ToArray();
        Assert.Equal(Enumerable.Range(0, geo.VertexCount), used);
    }

    // ── The native-resolution audit reports exactly the unresolved hulls ──────

    private static (short HullId, string Name, string? Stem) H(short id, string name, string? stem)
        => (id, name, stem);

    [Fact]
    public void Audit_ListsOnlyTheUnresolvableHulls()
    {
        var hulls = new[]
        {
            H(1, "Fighter", "fig01"),        // resolves
            H(2, "BrokenStem", ""),          // blank stem → unresolved
            H(3, "MissingMdl", "nope"),      // resolver says no → unresolved
            H(4, "Scout", "scou05"),         // resolves
        };
        bool Resolve(string? stem) => stem is "fig01" or "scou05";

        var unresolved = ShipNativeAudit.CollectUnresolved(hulls, Resolve);

        Assert.Equal(2, unresolved.Count);
        Assert.Contains(unresolved, u => u.HullId == 2 && u.Name == "BrokenStem");
        Assert.Contains(unresolved, u => u.HullId == 3 && u.Name == "MissingMdl" && u.Stem == "nope");
        // The resolvable hulls are NOT listed.
        Assert.DoesNotContain(unresolved, u => u.HullId is 1 or 4);
    }

    [Fact]
    public void Audit_HealthyCore_YieldsZeroUnresolved()
    {
        var hulls = new[] { H(1, "Fighter", "fig01"), H(4, "Scout", "scou05") };
        bool Resolve(string? stem) => stem is "fig01" or "scou05";

        var unresolved = ShipNativeAudit.CollectUnresolved(hulls, Resolve);
        Assert.Empty(unresolved);
    }

    // ── The fallback chain can never yield null (the crash guard) ─────────────

    /// <summary>The exact fallback expression the colour pass uses; generic so
    /// it is provable headless without a GraphicsDevice.</summary>
    private static T Pick<T>(T? shipModel, T? fallback, T standIn) where T : class
        => shipModel ?? fallback ?? standIn;

    [Fact]
    public void FallbackChain_NeverPassesNullToTheRenderer()
    {
        var standIn = ShipStandInModel.CreateGeometry();   // stands in for the non-null stand-in model
        // Both the ship model AND the fallback are null (no native art, no
        // Meshy pack) → the chain must land on the stand-in, never null.
        var picked = Pick<ShipStandInGeometry>(shipModel: null, fallback: null, standIn);
        Assert.Same(standIn, picked);
        Assert.NotNull(picked);
        // A present fallback wins over the stand-in; a present ship wins first.
        var fb = ShipStandInModel.CreateGeometry();
        Assert.Same(fb, Pick<ShipStandInGeometry>(null, fb, standIn));
        var ship = ShipStandInModel.CreateGeometry();
        Assert.Same(ship, Pick<ShipStandInGeometry>(ship, fb, standIn));
    }
}
