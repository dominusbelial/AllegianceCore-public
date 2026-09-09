namespace Allegiance.Core.Model;

using Allegiance.Core.Sim;

/// <summary>
/// The client-visible category of a team construction bucket
/// (<c>BucketItemType</c>, <c>src/Inc/Messages.h:500-503</c>):
/// <c>BIT_ShipType</c> builds a drone hull, <c>BIT_Factory</c> a factory,
/// <c>BIT_StoragePit</c> a storage pit.
/// </summary>
public enum BucketItemType
{
    /// <summary>A drone-hull ship-type bucket (<c>hullidDrone</c> set).</summary>
    BitShipType = 0,
    /// <summary>A factory bucket.</summary>
    BitFactory = 1,
    /// <summary>A storage-pit bucket.</summary>
    BitStoragePit = 2,
}

/// <summary>
/// A team construction <b>bucket</b> — the thing a side funds so it can build
/// something (a drone hull, factory, storage pit). This is the P119 port of
/// the native <c>CbucketIGC</c> (<c>src/Igc/bucketigc.h:22-324</c>, read in
/// full): it owns a buildable item's <see cref="Price"/> and
/// <see cref="TimeToBuild"/>, accumulates donated <see cref="InvestedMoney"/>
/// (capped at the price still owed) and invested build
/// <see cref="InvestedMilliseconds"/>, and completes once <b>both</b>
/// thresholds are met.
/// </summary>
/// <remarks>
/// <para><b>Completion gate</b> (the exact real formula, read from
/// <c>bucketigc.h</c>, not guessed): the time threshold is
/// <c>ttbms = m_timeToBuild * 1000</c> milliseconds (bucketigc.h:46); the
/// completion gate is <c>(m_investedMoney &gt;= m_price) &amp;&amp;
/// (m_investedMilliseconds &gt;= ttbms)</c> (bucketigc.h:51). Invested time
/// only accrues while there is invested money (<c>if (m_investedMoney &gt;
/// 0)</c>, bucketigc.h:44), and is <b>clamped</b> to the money-proportional
/// share <c>mim = 1000 * ((m_investedMoney * m_timeToBuild) / m_price)</c>
/// (bucketigc.h:58-61) — so time can never run ahead of the money put in, and
/// completion requires money-full <em>and</em> time-full together (once money
/// is full, <c>mim</c> equals <c>ttbms</c> and time freely accumulates to it).
/// </para>
/// <para><b>Leftover refund / cap</b>: <c>AddMoney</c> takes only what the
/// bucket still needs — <c>leftover = m_price - m_investedMoney - m; if
/// (leftover &lt; 0) m += leftover;</c> (bucketigc.h:284-287) — invests that
/// capped amount and <b>returns the actually-spent figure</b> (bucketigc.h:289-292),
/// so the caller can refund the excess (FedSrv.CPP:7151-7160).
/// </para>
/// <para><b>Seams</b>: on completing, the native bucket calls the site's
/// completion callbacks (bucketigc.h:188-261). Per the P119 boundary, this port
/// only <em>detects</em> completion — it raises the C#-only no-op seam
/// <see cref="IIgcSite.BucketCompleted"/> (exactly the port's established
/// no-op-default seam convention) and sets <see cref="Completed"/> — but
/// deliberately does <b>not</b> build what a completed bucket produces (that
/// is the explicit next task).</para>
/// <para><b>Catalog source (the task's open question, resolved)</b>: buckets
/// are <b>not</b> part of the mission <c>.igc</c> corpus or of this port's
/// static-data decode — native synthesizes them at runtime around a side's
/// civilizational buyables (<c>CsideIGC::CreateBuckets</c>,
/// <c>src/Igc/sideigc.cpp:113-310</c>, <c>DataBucketIGC db = {buyable, side}</c>),
/// and <c>BucketData</c> (Messages.h:505-525) is a client-side UI struct, not a
/// file record. So the <see cref="BucketCatalog"/> here is an explicitly
/// <b>synthetic</b> seed — one <see cref="BucketItemType.BitShipType"/> bucket
/// wrapping a <em>real</em> drone-hull record already decoded from the corpus
/// (PCore001.igc), taking that hull's real <c>Price</c>/<c>TimeToBuild</c>/
/// <c>Name</c>/<c>Description</c>. It is labeled synthetic, never presented as
/// decoded real data.</para>
/// </remarks>
public sealed class Bucket : IBucketIgc
{
    private readonly IIgcSite _site;
    private double _lastUpdate;
    private bool _completionRaised;

    /// <summary>The bucket id (<c>BucketID</c> = short, team-scoped).</summary>
    public short Id { get; }

    /// <summary>The kind of thing this bucket builds (<c>BucketItemType</c>).</summary>
    public BucketItemType ItemType { get; }

    /// <summary>The build's total money cost (<c>Money</c>, native 32-bit).</summary>
    public int Price { get; }

    /// <summary>The build's total time cost in <b>seconds</b>
    /// (<c>m_timeToBuild</c>, native DWORD seconds). The wire/reporting
    /// threshold is <c>Price * 1000</c> milliseconds.</summary>
    public uint TimeToBuild { get; }

    /// <summary>The bucket's buildable name (from the wrapped buyable's real
    /// hull record in the synthetic catalog).</summary>
    public string Name { get; }

    /// <summary>The bucket's buildable description.</summary>
    public string Description { get; }

    /// <summary>For <see cref="BucketItemType.BitShipType"/>, the drone hull id
    /// this bucket would produce (<c>HullID</c>); <c>-1</c> otherwise.</summary>
    public short HullIdDrone { get; }

    /// <summary>Money donated so far (native <c>m_investedMoney</c>). Widened to
    /// <c>long</c> to match this port's <see cref="Side.Money"/> widening
    /// convention; capped at <see cref="Price"/> by <see cref="AddMoney"/>.</summary>
    public long InvestedMoney { get; private set; }

    /// <summary>Build time invested so far, in <b>milliseconds</b> (native
    /// <c>m_investedMilliseconds</c>, <c>GetTime()</c> bucketigc.h:264-267).</summary>
    public long InvestedMilliseconds { get; private set; }

    /// <summary>True once the bucket has become fully funded and fully
    /// time-invested (the detection boundary; nothing is built on it yet).</summary>
    public bool Completed { get; private set; }

    /// <summary>The native percent-bought figure <c>100 * invested / price</c>
    /// (bucketigc.h:164-168).</summary>
    public int PercentBought => Price > 0 ? (int)((100 * InvestedMoney) / Price) : 0;

    /// <summary>Construct a bucket over a site seam, an id and a buyable
    /// definition.</summary>
    public Bucket(
        IIgcSite site,
        short id,
        BucketItemType itemType,
        int price,
        uint timeToBuild,
        string name,
        string description,
        short hullIdDrone = -1)
    {
        _site = site;
        Id = id;
        ItemType = itemType;
        Price = price;
        TimeToBuild = timeToBuild;
        Name = name;
        Description = description;
        HullIdDrone = hullIdDrone;
    }

    /// <summary>
    /// Donate money into the bucket — the port of <c>CbucketIGC::AddMoney</c>
    /// (<c>bucketigc.h:281-293</c>). Only what the bucket still needs is
    /// invested (an oversized donation is capped at the outstanding balance);
    /// the <b>amount actually spent</b> is returned so the caller can leave the
    /// excess in the donor's pool.
    /// </summary>
    public long AddMoney(long amount)
    {
        long leftover = Price - InvestedMoney - amount;   // bucketigc.h:284
        if (leftover < 0) amount += leftover;             // bucketigc.h:285-287: trim overshoot
        InvestedMoney += amount;                          // bucketigc.h:289
        return amount;                                    // bucketigc.h:292 — actually spent
    }

    /// <summary>
    /// Advance the bucket's invested-build-time accumulator for one sim step —
    /// the port of <c>CbucketIGC::Update</c> (<c>bucketigc.h:40-74</c>). Time
    /// only accrues while there is invested money, is clamped to the
    /// money-proportional share (<c>mim</c>, bucketigc.h:58-61), and completion
    /// is detected when money-full <em>and</em> time-full coincide
    /// (bucketigc.h:51), raising <see cref="IIgcSite.BucketCompleted"/> once.
    /// Called every tick (even when unfunded) so <c>_lastUpdate</c> stays
    /// current, matching the native's unconditional <c>m_lastUpdate = now</c>
    /// (bucketigc.h:73).
    /// </summary>
    public void Update(double now)
    {
        if (InvestedMoney > 0)                             // bucketigc.h:44
        {
            long ttbms = (long)TimeToBuild * 1000;         // bucketigc.h:46
            if (InvestedMilliseconds < ttbms)
            {
                double deltaMs = (now - _lastUpdate) * 1000.0;
                if (deltaMs < 0) deltaMs = 0;
                InvestedMilliseconds += (long)deltaMs;     // bucketigc.h:49
                if (InvestedMoney >= Price && InvestedMilliseconds >= ttbms)   // bucketigc.h:51
                    Complete(now);
                long mim = 1000 * ((InvestedMoney * TimeToBuild) / Price);    // bucketigc.h:58 (price > 0)
                if (InvestedMilliseconds > mim) InvestedMilliseconds = mim;   // bucketigc.h:59-60
            }
        }
        _lastUpdate = now;                                 // bucketigc.h:73
    }

    private void Complete(double now)
    {
        if (_completionRaised) return;                     // fire once
        _completionRaised = true;
        Completed = true;
        // Detection only — the no-op host seam (nothing is built yet).
        _site.BucketCompleted(this, now);
    }
}

/// <summary>
/// Seeds a side's <b>synthetic</b> bucket catalog. Bucket definitions are not
/// part of the mission <c>.igc</c> corpus nor of this port's static-data decode
/// (native builds buckets at runtime from civilizational buyables,
/// sideigc.cpp:113-310; <c>BucketData</c>, Messages.h:505-525, is client-only
/// UI struct, not a file record). So — pending a real catalog source — this
/// exposes one explicitly-synthetic <see cref="BucketItemType.BitShipType"/>
/// bucket.
/// </summary>
/// <remarks>
/// <para><b>Why the price/time-to-build are explicit constants, not read from
/// the hull</b> (measured, not assumed): this port's corpus decode yields
/// <c>TimeToBuild == 0</c> for <em>every</em> hull record in PCore001.igc — the
/// native build-time field is not meaningfully populated by this decode — so
/// deriving a bucket's build time from a real hull would give a degenerate
/// zero-time bucket that can never traverse the completion gate
/// (bucketigc.h:51). The bucket therefore carries its own synthetic
/// <see cref="Bucket.SyntheticPrice"/>/<see cref="Bucket.SyntheticTimeToBuild"/>,
/// and uses the real hull (id 504, 'Bomber', PCore001.igc) only for the
/// drone-hull reference and the human name/description — real corpus data for
/// the <c>hullIdDrone</c>/label fields, synthetic funding numbers for the
/// cost/build-time the corpus does not supply. Labeled synthetic throughout,
/// never presented as decoded real data.</para>
/// </remarks>
public static class BucketCatalog
{
    /// <summary>The hull id the synthetic ship-type bucket references: hull 504,
    /// a real hull in PCore001.igc (measured in P108, not guessed; name
    /// 'Bomber', own price 500). Used purely as the referenced drone hull + name
    /// source for the synthetic catalog entry.</summary>
    public const short SyntheticDroneHullId = 504;

    /// <summary>The synthetic bucket's money cost (<c>Money</c>). Not read from
    /// the hull — the corpus supplies no usable build price/cost curve for
    /// buckets, so this is an explicit, clearly-labeled synthetic value.</summary>
    public const int SyntheticPrice = 5800;

    /// <summary>The synthetic bucket's build time in seconds. Explicitly
    /// synthetic because the corpus decode yields <c>TimeToBuild == 0</c> for
    /// every hull record (measured), which would make a real-hull-derived bucket
    /// never complete.</summary>
    public const uint SyntheticTimeToBuild = 30;

    /// <summary>Build the synthetic per-side bucket catalog for a given core.
    /// Currently one <see cref="BucketItemType.BitShipType"/> bucket referencing
    /// <see cref="SyntheticDroneHullId"/>; falls back to an empty list if that
    /// hull is absent (a caller-supplied/empty core yields no buckets).</summary>
    public static IReadOnlyList<Bucket> ForSide(StaticCore core, IIgcSite site)
    {
        if (core is null || !core.HullTypes.TryGetValue(SyntheticDroneHullId, out var hull))
            return Array.Empty<Bucket>();
        return new[]
        {
            new Bucket(
                site,
                id: 1,
                BucketItemType.BitShipType,
                price: SyntheticPrice,
                timeToBuild: SyntheticTimeToBuild,
                name: hull.Name,
                description: hull.Description,
                hullIdDrone: hull.HullId),
        };
    }
}
