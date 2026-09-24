using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="WeaponReloadTracker"/> — the P362 per-mounted-
/// system status derived from two cheap client-observable signals: the live ammo
/// fraction (for the failure/"Ammo depleted." case) and the RELOAD(129) relay
/// (for the success/"Reloading ammo..." case — a successful P361 auto-reload
/// refills synchronously server-side, so the wire fraction never shows 0).
/// No graphics device, no MonoGame types on the surface.
///
/// <para>Covers: the &gt;0→~0 transition putting projectile mounts into
/// Reloading; sustained ~0 past the deplete threshold becoming Depleted (the
/// no-spare-in-cargo failure case); recovery from Depleted back to Ready; the
/// success path (RELOAD relay) showing Reloading for the brief window then Ready;
/// energy/non-ammo systems never entering Reloading/Depleted; multiple projectile
/// mounts sharing the one real magazine reloading together (faithful to the
/// port's shared-pool sim); and the Ammo column mapping each mount's live share.</para>
///
/// <para>Fraction vocabulary used throughout (EmptyEpsilon = 0.016): FULL = 1.0
/// (fully loaded), LOW = 0.0 (at/under epsilon — empty), SOME = 0.937 (above
/// epsilon — Ready/recovered). No in-between magnitudes are relied upon.</para>
/// </summary>
public class WeaponReloadTrackerTests
{
    private const float Full = 1.0f;    // fully loaded
    private const float Low = 0.0f;     // ≤ EmptyEpsilon — empty
    private const float Some = 0.937f;  // > EmptyEpsilon — has ammo

    // A gatling + a laser (energy) + a disruptor: two ammo-consuming projectile
    // mounts sharing the pool, one non-ammo system that must stay Ready.
    private static readonly IReadOnlyList<WeaponSlotDescriptor> Slots =
    [
        new(0, "Gatling", true, 200),
        new(1, "Laser", false, 989),
        new(2, "Disruptor", true, 150),
    ];

    private static WeaponStatus StatusOf(IReadOnlyList<WeaponStatusRow> rows, int mountId) =>
        rows.First(r => r.MountId == mountId).Status;

    [Fact]
    public void FullAmmo_AllSystemsReady()
    {
        var rows = new WeaponReloadTracker().Update(0.0f, Full, false, Slots);
        Assert.Equal(WeaponStatus.Ready, StatusOf(rows, 0));
        Assert.Equal(WeaponStatus.Ready, StatusOf(rows, 1));
        Assert.Equal(WeaponStatus.Ready, StatusOf(rows, 2));
    }

    [Fact]
    public void ZeroTransition_ProjectileMountsReload_EnergyStaysReady()
    {
        var t = new WeaponReloadTracker();
        t.Update(0.867f, Full, false, Slots);         // full → baseline
        var rows = t.Update(0.971f, Low, false, Slots); // magazine emptied (>0 → ~0)

        // Both projectile mounts (shared magazine) show RELOADING…
        Assert.Equal(WeaponStatus.Reloading, StatusOf(rows, 0));
        Assert.Equal(WeaponStatus.Reloading, StatusOf(rows, 2));
        // …while the energy weapon is unaffected — genuinely independent statuses.
        Assert.Equal(WeaponStatus.Ready, StatusOf(rows, 1));
    }

    [Fact]
    public void ReloadRelay_Success_ShowsReloadingWindow_ThenReady()
    {
        var t = new WeaponReloadTracker();
        t.Update(0.000f, Full, false, Slots);         // full, Ready
        // A successful P361 auto-reload is signalled by the RELOAD(129) relay;
        // the ammo fraction is already back (refilled synchronously).
        var r = t.Update(0.903f, Full, true, Slots);  // relay arrived
        Assert.Equal(WeaponStatus.Reloading, StatusOf(r, 0));
        Assert.Equal(WeaponStatus.Reloading, StatusOf(r, 2));
        Assert.Equal(WeaponStatus.Ready, StatusOf(r, 1));   // energy unaffected

        // Inside the brief display window (0.6s) it stays RELOADING…
        Assert.Equal(WeaponStatus.Reloading, StatusOf(t.Update(0.919f, Full, false, Slots), 0));
        // …then returns to Ready once the window closes.
        Assert.Equal(WeaponStatus.Ready, StatusOf(t.Update(1.612f, Full, false, Slots), 0));
        Assert.Equal(WeaponStatus.Ready, StatusOf(t.Update(1.619f, Full, false, Slots), 2));
    }

    [Fact]
    public void FailedReload_NoSpare_BecomesDepleted_AfterGrace()
    {
        var t = new WeaponReloadTracker();
        t.Update(0.830f, Full, false, Slots);
        t.Update(0.920f, Low, false, Slots);          // emptied → RELOADING (zeroSince=0.920)

        // Within the 0.35s grace, ammo still empty → stays RELOADING.
        Assert.Equal(WeaponStatus.Reloading, StatusOf(t.Update(0.967f, Low, false, Slots), 0));

        // Past the grace with ammo still empty → DEPLETED (native "Ammo depleted.").
        var depleted = t.Update(1.290f, Low, false, Slots);   // 1.290-0.920 = 0.370 ≥ 0.35
        Assert.Equal(WeaponStatus.Depleted, StatusOf(depleted, 0));
        Assert.Equal(WeaponStatus.Depleted, StatusOf(depleted, 2));   // both pool mounts
        Assert.Equal(WeaponStatus.Ready, StatusOf(depleted, 1));      // energy unaffected
    }

    [Fact]
    public void Depleted_ThenReplenished_ReturnsToReady()
    {
        var t = new WeaponReloadTracker();
        t.Update(0.000f, Full, false, Slots);
        t.Update(0.848f, Low, false, Slots);          // emptied (zeroSince=0.848)
        t.Update(0.899f, Low, false, Slots);          // still Reloading (0.051s)
        t.Update(0.958f, Low, false, Slots);          // still Reloading (0.110s)

        Assert.Equal(WeaponStatus.Depleted, StatusOf(t.Update(1.865f, Low, false, Slots), 0)); // 1.017s ≥ 0.35

        // Ammo comes back (restock) → Ready again.
        Assert.Equal(WeaponStatus.Ready, StatusOf(t.Update(1.919f, Some, false, Slots), 0));
    }

    [Fact]
    public void EnergySystem_NeverReloadsNorDepletes()
    {
        var t = new WeaponReloadTracker();
        t.Update(0.822f, Full, false, Slots);
        t.Update(0.969f, Low, false, Slots);          // pool empties
        for (int i = 0; i < 40; i++)                  // long exhausted stretch
            Assert.Equal(WeaponStatus.Ready, StatusOf(t.Update(0.969f + i * 0.059f, Low, false, Slots), 1));
    }

    [Fact]
    public void AmmoColumn_ScalesEachMountByItsShareOfLiveFraction()
    {
        var t = new WeaponReloadTracker();
        // Fractions stay well above EmptyEpsilon → never a transition, isolating
        // the Ammo = round(capacity × fraction) mapping.
        var r1 = t.Update(0.858f, 0.838f, false, Slots);
        Assert.Equal((short)168, r1.First(s => s.MountId == 0).Ammo);   // round(200×0.838)
        Assert.Equal((short)126, r1.First(s => s.MountId == 2).Ammo);   // round(150×0.838)

        var r2 = t.Update(0.839f, 0.829f, false, Slots);
        Assert.Equal(WeaponStatus.Ready, StatusOf(r2, 0));
        Assert.Equal((short)166, r2.First(s => s.MountId == 0).Ammo);   // round(200×0.829)
        Assert.Equal((short)124, r2.First(s => s.MountId == 2).Ammo);   // round(150×0.829)
    }
}
