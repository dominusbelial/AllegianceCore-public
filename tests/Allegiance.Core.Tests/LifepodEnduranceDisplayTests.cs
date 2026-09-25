using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P377 — surfacing the lifepod's real oxygen countdown to the HUD. The server
/// already computes the drifting pod's deadline (P279, <c>Ship.LifepodTimeToDie</c>,
/// native <c>m_timeToDie</c>); P377 exposes it as the remaining fraction on the
/// OWNING peer's own-ship update only, mirroring native <c>IshipIGC::GetEndurance</c>
/// (<c>shipIGC.h:2473-2491</c>) and the self-only <c>ModelData::GetEndurance</c>
/// gate (<c>WinTrek/consoledata.cpp:533-546</c>).
/// <para>
/// These are pure unit tests (no network): the fraction formula/clamp/gates,
/// and the wire self-only gate (<c>carryEndurance</c>) — that a remote/teammate
/// observer's copy of a lifepod never carries the declining oxygen fraction.
/// The end-to-end live proof lives in <see cref="LiveLifepodOxygenTests"/>.
/// </para>
/// </summary>
public class LifepodEnduranceDisplayTests
{
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

    /// <summary>A real lifepod-capable hull from the corpus (falls back to a
    /// clone with only the lifepod bit if the corpus lacks one).</summary>
    private static DataHullTypeIGC LifepodHull()
    {
        var all = PayloadDecodeTests.Hulls().Select(h => h.V).ToList();
        var found = all.FirstOrDefault(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
        return found.HasCapability(DataHullTypeIGC.HabmLifepod)
            ? found
            : all[0] with { HabmCapabilities = DataHullTypeIGC.HabmLifepod };
    }

    private static DataHullTypeIGC NonLifepodHull()
    {
        var all = PayloadDecodeTests.Hulls().Select(h => h.V).ToList();
        return all.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod));
    }

    private static IReadOnlyList<SideSpec> SideSpecs()
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        return [new SideSpec(0, "Team 1", civ.CivilizationId)];
    }

    private static Mission NewStartedMission()
    {
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, SideSpecs(), new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    // ── the fraction formula (native IshipIGC::GetEndurance) ──────────────

    /// <summary>⭐ The remaining-oxygen fraction tracks the real formula
    /// (deadline − now)/endurance, clamps to [0,1], and reads a flat 1.0 for a
    /// non-lifepod hull (native returns 1.0f there too). Uses the P279
    /// <c>LifepodEnduranceOverrideSeconds</c> seam (10 s) so no five-minute
    /// wait is needed.</summary>
    [Fact]
    public void LifepodEnduranceFraction_tracks_formula_and_clamps()
    {
        var lp = LifepodHull();
        var ship = new Ship(42, lp, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        ship.LifepodEnduranceOverrideSeconds = 10.0;
        ship.ChangeHull(lp);   // arms LifepodTimeToDie = (no mission) 0 + 10 = 10

        Assert.Equal(1.0f, ship.LifepodEnduranceFraction(0.0), 3);   // fresh → full
        Assert.Equal(0.5f, ship.LifepodEnduranceFraction(5.0), 3);   // half elapsed
        Assert.Equal(0.25f, ship.LifepodEnduranceFraction(7.5), 3);
        Assert.Equal(0.0f, ship.LifepodEnduranceFraction(10.0), 3);  // at deadline → empty
        Assert.Equal(0.0f, ship.LifepodEnduranceFraction(15.0), 3);  // past → clamped 0
    }

    /// <summary>A docked (rescued / death-respawned berth) lifepod reads 1.0
    /// (inactive) — the same <c>StationId is null</c> gate that cancels the
    /// oxygen burn in <see cref="Ship.OnUpdate"/>. Native returns 1.0f when the
    /// pod is not in a cluster (docked).</summary>
    [Fact]
    public void LifepodEnduranceFraction_is_full_while_docked()
    {
        var lp = LifepodHull();
        var ship = new Ship(43, lp, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        ship.LifepodEnduranceOverrideSeconds = 10.0;
        ship.ChangeHull(lp);
        ship.StationId = 501;   // berthed (rescued) — no longer adrift

        Assert.Equal(1.0f, ship.LifepodEnduranceFraction(5.0), 3);   // would be 0.5 if adrift
    }

    /// <summary>A non-lifepod ship's fuel/endurance reads a flat 1.0 — the
    /// countdown is a lifepod-only value (native GetEndurance returns 1.0f for
    /// any non-lifepod hull).</summary>
    [Fact]
    public void LifepodEnduranceFraction_is_full_for_non_lifepod()
    {
        var ship = new Ship(44, NonLifepodHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity);
        Assert.Equal(1.0f, ship.LifepodEnduranceFraction(5.0), 3);
    }

    // ── the wire self-only gate (ServerShipUpdate.FromModel) ──────────────

    /// <summary>⭐ SELF-ONLY PROOF (unit). The owner's own payload
    /// (<c>carryEndurance:true</c>) puts the real declining oxygen fraction in
    /// the FUEL slot; the same lifepod encoded for a REMOTE observer
    /// (<c>carryEndurance:false</c> — the teammate relay path) keeps the flat
    /// 1.0 fuel value. A real mission advances the sim clock so the fraction is
    /// genuinely mid-depletion, not a fresh 1.0.</summary>
    [Fact]
    public void FromModel_endurance_rides_only_the_owner_payload()
    {
        var m = NewStartedMission();
        var lp = LifepodHull();
        var ship = new Ship(50, lp, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        ship.LifepodEnduranceOverrideSeconds = 1.0;
        m.PlaceShip(ship, m.Sectors[0]);

        // Arm the deadline at the current sim clock, then advance halfway.
        ship.ChangeHull(lp);                       // deadline = LastUpdate + 1.0
        double t0 = m.LastUpdate;
        m.Update(t0 + 0.5);                        // sim clock now t0 + 0.5

        Assert.Equal(0.5f, ship.LifepodEnduranceFraction(ship.Mission!.LastUpdate), 3); // precondition

        float owner = ServerShipUpdate.FromModel(ship.Id, ship, (uint)t0, carryEndurance: true).FuelFraction;
        float remote = ServerShipUpdate.FromModel(ship.Id, ship, (uint)t0, carryEndurance: false).FuelFraction;

        Assert.True(owner < 1.0f && owner >= 0.4f, $"owner should see a mid-depletion oxygen fraction, got {owner}");
        Assert.Equal(1.0f, remote, 3);             // an observer's copy stays flat fuel — self-only
    }
}
