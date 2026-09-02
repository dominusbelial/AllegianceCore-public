namespace Allegiance.Core.Sim;

/// <summary>
/// The <b>runtime</b> asteroid — the minimal stateful counterpart to the
/// immutable static <see cref="DataAsteroidIGC"/> wire record. This is the
/// asteroid half of the same static-vs-runtime split P53 established for
/// stations (the static <c>DataStationIGC</c> in <c>Sector.Stations</c> plus
/// the docked state on the ship): it wraps the static data <see cref="Data"/>
/// and carries exactly the <b>two</b> pieces of runtime state mining needs —
/// the <b>depletable ore pool</b> (<see cref="OreRemaining"/>) and the
/// last-reported-ore watermark that throttles the <see cref="IIgcSite.MineAsteroidEvent"/>
/// emission. Nothing else (no hull/shield/name beyond what the record already
/// carries, matching the task's "keep it minimal: position + remaining ore").
/// It doubles as the <see cref="IAsteroidIgc"/> carrier for the seam, mirroring
/// <see cref="DockStation"/> : <c>IStationIgc</c> — the mining pass passes this
/// object as the <c>IAsteroidIgc</c> argument of <c>MineAsteroidEvent</c> and the
/// host casts it back out to reach the concrete record.
/// </summary>
/// <remarks>
/// Behaviour is ported from the reference engine and every simplification is
/// stated openly:
/// <list type="bullet">
/// <item><b>Depletion</b> (<see cref="MineOre"/>, port of
/// <c>CasteroidIGC::MineOre</c>, <c>asteroidigc.h:221-253</c>): the pool grants
/// only the requested <em>or</em> whatever remains when the rock is nearly
/// tapped — the <c>actualOre &lt; requestedOre</c> case the accrual loop reads
/// to detect tapping (<c>shipIGC.cpp:2240-2247</c>). The native
/// <c>DrainAsteroidEvent</c> call in the empty branch
/// (<c>asteroidigc.h:228-230</c>) is NYI-commented out in the reference itself,
/// so it is not emitted here either.</item>
/// <item><b>Regeneration</b> (<see cref="Regenerate"/>, port of
/// <c>CasteroidIGC::Update</c>, <c>asteroidigc.h:47-56</c>): the native engine
/// does <b>not</b> deplete-forever — a He3-capable asteroid continuously
/// regrows toward <c>oreMax</c> at a rate proportional to its deficit. This
/// server reproduces that formula with a flat <see cref="Sector.He3Regeneration"/>
/// constant in place of the per-mission <c>c_fcidHe3Regeneration</c> value
/// (the actual default lives in the core data, not in code this port decodes —
/// see <see cref="Sector.He3Regeneration"/>). The consequence, faithfully
/// inherited: a parked miner is <em>never</em> permanently starved, so "mining
/// stops when the pool hits zero" (the verification wording) manifests as the
/// pool limiting the per-tick yield — <see cref="MineOre"/> returns less than
/// requested — not as a hard stop. Picking a small regeneration constant (see
/// Sector) keeps the pool-limited behaviour observable within a short test.</item>
/// <item><b>He3 gate</b> (<see cref="CanMineHe3"/>): regeneration and mining
/// both require the <c>c_aabmMineHe3</c> (0x01) capability bit
/// (<c>igc.h:761</c>, <c>asteroidigc.h:49</c>, <c>shipIGC.cpp:1955</c>). An
/// asteroid without it is inert for this task.</item>
/// <item><b>Report throttling</b> (<see cref="TryReportIfChanged"/>, port of
/// <c>asteroidigc.h:246-251</c>): <c>MineAsteroidEvent</c> fires only when the
/// ore has moved by more than the native 3.0 threshold since the last report,
/// so the host is not spammed every tick.</item>
/// </list>
/// The asteroid is deliberately <b>not</b> a <see cref="Model"/> — it does not
/// join the sector's models loop; mining detection is a sector pass
/// (<see cref="Sector.ResolveMining"/>) like docking/warps, and regeneration
/// runs inside that same pass. Object identity is the record's <see cref="Data"/>
/// <c>AsteroidId</c>, which the mining pass uses to look up the pool.
/// </remarks>
public sealed class Asteroid : IAsteroidIgc
{
    /// <summary>The He3-minable capability bit <c>c_aabmMineHe3 = 0x01</c> (igc.h:761).</summary>
    public const short CapMineHe3 = 0x01;

    /// <summary>The underlying static wire record.</summary>
    public DataAsteroidIGC Data { get; }

    /// <summary>The asteroid's depletable ore pool, seeded from the record's
    /// <see cref="DataAsteroidIGC.Ore"/>. <see cref="MineOre"/> drains it;
    /// <see cref="Regenerate"/> regrows it toward <see cref="OreMax"/>.</summary>
    public float OreRemaining { get; private set; }

    /// <summary>The record's ore capacity (<see cref="DataAsteroidIGC.OreMax"/>).</summary>
    public float OreMax => Data.OreMax;

    /// <summary>The physical rock radius (the record's <see cref="DataAsteroidIGC.Radius"/>),
    /// used by the mining proximity gate (<see cref="Model.Sector.ResolveMining"/>)
    /// as <c>asteroid.Radius + ship.Radius + 30.0f</c> (<c>shipIGC.cpp:1960</c>).</summary>
    public float Radius => Data.Radius;

    /// <summary>Whether this asteroid holds minable He3 (<c>aabmCapabilities &amp; 0x01</c>,
    /// igc.h:761) — the gate native applies to both mining (shipIGC.cpp:1955)
    /// and regeneration (asteroidigc.h:49).</summary>
    public bool CanMineHe3 => (Data.AabmCapabilities & CapMineHe3) != 0;

    /// <summary>The ore fraction the seam reports — <c>ore/oreMax</c> clamped
    /// to [0,1]; 0 when the record carries no capacity. Native
    /// <c>GetOreFraction()</c> (<c>igc.h:4162</c>) is what
    /// <c>MineAsteroidEvent</c> transmits (<c>asteroidigc.h:249</c>).</summary>
    public float OreFraction => OreMax <= 0f ? 0f : Math.Clamp(OreRemaining / OreMax, 0f, 1f);

    private float _lastReportedOre;

    /// <summary>Wrap a static record as the runtime model, seeding the pool from
    /// its <see cref="DataAsteroidIGC.Ore"/>.</summary>
    public Asteroid(DataAsteroidIGC data)
    {
        Data = data;
        OreRemaining = data.Ore;
        _lastReportedOre = data.Ore;
    }

    /// <summary>
    /// Grant ore up to <paramref name="requested"/> from this asteroid's pool,
    /// draining it correspondingly — the port of <c>CasteroidIGC::MineOre</c>
    /// (<c>asteroidigc.h:221-253</c>). Returns the actual amount granted (less
    /// than requested once the rock is nearly tapped, per
    /// <c>shipIGC.cpp:2240-2241</c>).
    /// </summary>
    public float MineOre(float requested)
    {
        float granted;
        if (OreRemaining < requested)
        {
            granted = OreRemaining;      // tapped: grant everything left (asteroidigc.h:225)
            OreRemaining = 0.0f;         // the DrainAsteroidEvent call is NYI in native; not emitted
        }
        else
        {
            granted = requested;         // ordinary subtraction (asteroidigc.h:233)
            OreRemaining -= requested;
        }

        // Xynth #132 — MineAsteroidEvent only when the ore moved by > 3.0 since
        // the last report (asteroidigc.h:246-251), so a busy rock doesn't spam.
        if (MathF.Abs(OreRemaining - _lastReportedOre) > 3.0f)
        {
            _lastReportedOre = OreRemaining;
            SetChangedFlag();
        }
        return granted;
    }

    /// <summary>The mining pass sets this after emitting <see cref="IIgcSite.MineAsteroidEvent"/>.
    /// Returns the ore fraction to report.</summary>
    public float PendingReport { get; private set; }
    public bool HasPendingReport { get; private set; }
    private void SetChangedFlag()
    {
        PendingReport = OreFraction;
        HasPendingReport = true;
    }

    /// <summary>Consume the pending-report flag (called by the mining pass after
    /// routing it to the site).</summary>
    public void ClearPendingReport() => HasPendingReport = false;

    /// <summary>
    /// Regrow the ore pool toward <see cref="OreMax"/> — the port of the
    /// regeneration half of <c>CasteroidIGC::Update</c>
    /// (<c>asteroidigc.h:47-56</c>): a He3-capable asteroid below capacity gains
    /// <c>dT · regenConst · (oreMax − ore)</c>. <paramref name="dt"/> is the
    /// elapsed sim seconds for this tick. Clamped at <c>oreMax</c> so floating
    /// error never reports a fraction &gt; 1 (native's asymptotic approach makes
    /// overshoot impossible in exact arithmetic; the clamp is defensive).
    /// </summary>
    public void Regenerate(float dt, float regenConst)
    {
        if (!CanMineHe3) return;               // asteroidigc.h:49
        if (OreRemaining >= OreMax) return;    // already full (asteroidigc.h:50)
        float gained = dt * regenConst * (OreMax - OreRemaining);
        OreRemaining = Math.Min(OreMax, OreRemaining + gained);
    }
}
