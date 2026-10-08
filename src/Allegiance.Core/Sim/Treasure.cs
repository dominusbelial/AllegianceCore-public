using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// A live treasure as a sector model — the port of <c>CtreasureIGC</c>
/// (<c>src/Igc/treasureIGC.cpp</c>, ~278 lines), kept to this slice's need: a
/// real <see cref="Model"/> that lives in a <see cref="Sector"/> (in the
/// sector's <c>_models</c> list, exactly where it was placed — a treasure has
/// no acceleration/velocity after its initial deceleration-to-stop and is
/// rendered static, <c>treasureIGC.cpp:95-116,215-262</c>), and is picked up
/// by a qualifying ship via the sector's proximity pass
/// (<see cref="Sector.ResolveTreasurePickups"/>).
/// </summary>
/// <remarks>
/// <para><b>Purpose in this slice (P123).</b> The treasure is the passive
/// pickup-able object the cash mechanic consumes: the port of
/// <c>CtreasureIGC</c> reduced to what cash pickup needs — a static body whose
/// <c>DataTreasureIGC</c> carries the <c>amount</c> (cash value) and
/// <c>treasureCode</c>, plus a pickup radius for the sector's proximity sweep.
/// The real randomized chance-weighted spawn-table algorithm is deliberately a
/// separate later task (see the P123 report's spawn-trigger finding), so
/// treasures are placed explicitly (e.g. by a test) exactly as mine/probe
/// tests already deploy their objects.</para>
/// <para><b>Honest-scope statement</b>, same style as <c>Mine.cs</c>'s remarks.
/// Deliberately <em>not</em> implemented this slice:
/// <list type="bullet">
/// <item><b>Movement / deceleration-to-stop.</b> Native integrates a treasure's
/// initial velocity <c>v0</c> down to a stop (<c>treasureIGC.cpp:95-116,215-262</c>).
/// This port places treasures already stationary — there is no launch/spawn
/// path that would impart a <c>v0</c> yet (that is part of the deferred spawn
/// system), so no motion integration is built.</item>
/// <item><b>Expiry.</b> Native destroys a treasure when
/// <c>now &gt;= time0 + lifespan</c> via <c>KillTreasureEvent</c>
/// (<c>treasureIGC.cpp:215-218</c>), broadcasting <c>DESTROY_TREASURE</c>. P586
/// implements this: <see cref="OnUpdate"/> tests <c>now &gt;= ExpireAt</c>,
/// removes the treasure from its sector and raises
/// <see cref="IIgcSite.TreasureDestroyedEvent"/> so the host broadcasts the
/// destroy wire message (the same destroy broadcast the pickup path uses).</item>
/// <item><b>Non-cash codes.</b> Only <c>c_tcCash</c> pickup is wired (see
/// <see cref="Sector.ResolveTreasurePickups"/>); <c>c_tcPart</c> /
/// <c>c_tcPowerup</c> / <c>c_tcDevelopment</c> / <c>c_tcFlag</c> consumption is
/// the separately-scoped follow-up.</item>
/// </list></para>
/// <para>The one behavioural surface beyond static existence is exposure of the
/// pickup fields the sector pass and the server's credit/destroy read:
/// <see cref="Code"/>, <see cref="Amount"/>, <see cref="PickupRadius"/>,
/// <see cref="OwnerSector"/>, and the full <see cref="Data"/>.</para>
/// </remarks>
public sealed class Treasure : Model
{
    /// <summary>The decoded <c>DataTreasureIGC</c> — the whole wire-export
    /// struct this treasure was created from (igc.h:2517-2529). Its
    /// <c>P0</c> is the resting position; <c>Amount</c>/<c>TreasureCode</c>/
    /// <c>TreasureId</c> drive the pickup credit and the destroy broadcast.
    /// Never hardcoded; the task rule.</summary>
    public DataTreasureIGC Data { get; }

    /// <summary>The sector this treasure lives in — the sector that owns it
    /// and whose id feeds the <c>DESTROY_TREASURE</c> broadcast
    /// (<c>sectorID</c>, FedSrv.CPP:12502) and the pickup pass.</summary>
    public Sector OwnerSector { get; }

    /// <summary>The mission this treasure belongs to (resolved at
    /// construction so the expiry path can reach the host seam's
    /// <see cref="IIgcSite.TreasureDestroyedEvent"/> via <c>Mission.Site</c>,
    /// exactly as <c>Mine</c> reaches its site through
    /// <c>launcherShip.Mission.Site</c>). Null in unit tests that construct a
    /// bare treasure, where the expiry event is a harmless no-op.</summary>
    public Mission? OwnerMission { get; }

    /// <summary>
    /// The sim-time (seconds) at which this treasure expires and is removed —
    /// the port of native <c>m_expire = time0 + lifespan</c>
    /// (<c>treasureIGC.cpp:197</c>). Native sums server <em>ms</em> clock;
    /// this port's <c>Data.Time0</c> is the tick index (not seconds) and its
    /// sim clock <c>now</c> is in seconds (Server.cs:793), so the expiry bound
    /// is stamped from the construction sim-time + <c>Lifespan</c> — the same
    /// instant native's expression denotes. Used by <see cref="OnUpdate"/>'s
    /// <c>now &gt;= ExpireAt</c> test (<c>treasureIGC.cpp:217</c>).
    /// </summary>
    public double ExpireAt { get; }

    /// <summary>The treasure's code (<c>GetTreasureCode</c>,
    /// treasureIGC.h:107-110 — <c>m_data.treasureCode</c>). The pickup pass
    /// acts only on <see cref="TreasureCode.Cash"/> this slice.</summary>
    public TreasureCode Code => Data.TreasureCode;

    /// <summary>The cash amount / part count (<c>GetAmount</c>,
    /// treasureIGC.h:124-127). For <c>c_tcCash</c> native sets
    /// <c>amount = treasureID</c> (<c>treasureIGC.cpp:91-92</c>); the pickup
    /// credits the picking ship's side by this many credits.</summary>
    public short Amount => Data.Amount;

    /// <summary>
    /// The treasure's collision/pickup radius — the port of the native
    /// <c>SetRadius(5.0f)</c> (<c>treasureIGC.cpp:193</c>:
    /// <c>//NYI -- needs to be gotten from the DB</c>). The sector's pickup
    /// pass measures proximity as <c>this + 0.5·ship.Length</c>, the same
    /// point-in-sphere convention the dock/mining/rescue passes use.
    /// </summary>
    public const float PickupRadius = 5.0f;

    /// <summary>Construct a treasure in <paramref name="sector"/> from its
    /// decoded <see cref="DataTreasureIGC"/> <paramref name="data"/>. Its
    /// position is the struct's <c>P0</c> (the resting spot); its id is the
    /// struct's <c>ObjectId</c>. Clock seeded to <paramref name="now"/> via
    /// <see cref="Model.InitClock"/> so a first tick is a genuine chunk
    /// (the same ship-clock fix <see cref="Projectile"/> documents).
    /// <paramref name="mission"/> (optional) lets the expiry path reach the
    /// host seam's <see cref="IIgcSite.TreasureDestroyedEvent"/>; production
    /// call sites pass it, unit tests may omit it (a null mission makes the
    /// expiry event a harmless no-op).</summary>
    public Treasure(short id, Sector sector, DataTreasureIGC data, double now, Mission? mission = null)
        : base(id, Pos(data))
    {
        OwnerSector = sector ?? throw new ArgumentNullException(nameof(sector));
        OwnerMission = mission;
        Data = data;
        ExpireAt = now + data.Lifespan;   // m_expire = time0 + lifespan (treasureIGC.cpp:197)
        InitClock(now);
    }

    private static Vector3 Pos(DataTreasureIGC d) =>
        d.P0 is { Length: 3 } p ? new Vector3(p[0], p[1], p[2]) : Vector3.Zero;

    /// <summary>
    /// One chunk's work: a treasure has no movement, so <c>OnUpdate</c> does
    /// only the expiry check — the literal shape of <c>CtreasureIGC::Update</c>
    /// (<c>treasureIGC.cpp:215-218</c>, <c>if (now &gt;= m_expire)
    /// KillTreasureEvent(this); else … drifts-to-stop …; base.Update</c>). On
    /// expiry (P586 — this fixes the "hundreds of loot pickups left after
    /// combat" pileup: native drops parts with 60 s / ammo &amp; fuel with 30 s
    /// lifespans and removes them, <c>FedSrv.CPP:11694,11709,11724</c>; the
    /// pre-P586 port never expired, so every drop lingered forever) remove
    /// <c>this</c> from <see cref="OwnerSector"/> and raise the host seam's
    /// <see cref="IIgcSite.TreasureDestroyedEvent"/> so the server can
    /// broadcast a real <c>FM_S_DESTROY_TREASURE(30)</c> whole-mission — the
    /// identical pair of calls <see cref="Mine.OnUpdate"/> makes on mine
    /// expiry. Removing <c>this</c> during this call is safe: the sector ticks
    /// over a snapshot (<c>Sector.Update</c>), and an expiry never repeats
    /// (latched by <see cref="ExpireAt"/>, monotone). The pickup path (which
    /// also destroys the treasure) is driven externally by
    /// <see cref="Sector.ResolveTreasurePickups"/>, not here. The drift-to-stop
    /// movement integration is the separately-scoped deferred spawn system
    /// (this port places treasures already at rest); gameplay expiry is this
    /// method's whole job.
    /// </summary>
    protected override void OnUpdate(double now, double dt)
    {
        if (now >= ExpireAt)
        {
            OwnerSector.Remove(this);
            OwnerMission?.Site.TreasureDestroyedEvent(this);
        }
    }
}
