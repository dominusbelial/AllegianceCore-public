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
    /// <summary>
    /// A technology <b>development</b> bucket (P189) — wraps a real
    /// <see cref="DataDevelopmentIGC"/> from the core's <c>Developments</c>
    /// catalog. Completing it applies the development's <c>TtbmEffects</c> to
    /// the side's <see cref="Side.DevelopmentTechs"/> and (unless it is
    /// tech-only) its <c>Gas</c> to the side's
    /// <see cref="Side.GlobalAttributes"/>, then broadcasts
    /// <c>DEV_COMPLETED(8)</c> / <c>SIDE_TECH_CHANGE(75)</c> (side group) and
    /// <c>SIDE_ATTRIBUTE_CHANGE(76)</c> (whole mission). This enum value is
    /// this port's analogue of the native <c>OT_development</c> bucket
    /// buyable type (<c>AdjustBuckets</c>, <c>sideigc.h:624-638</c>).
    /// </summary>
    BitDevelopment = 3,

    /// <summary>
    /// A <b>drone-type</b> bucket (P458) — wraps a real
    /// <see cref="DataDroneTypeIGC"/> from the core's <c>DroneTypes</c>
    /// catalog. Created in <see cref="BucketCatalog.ForSide"/> alongside the
    /// station-type buckets (native <c>CreateBuckets</c> order: developments,
    /// then drone types, then station types, sideigc.cpp:204-255). Per the
    /// P458 scope, drone-type buckets are <b>created only</b> — their
    /// completion effect (spawning the real-hulled drone) is the explicit
    /// P459 follow-up, so <see cref="Server"/>'s completion handler still
    /// treats them as detection-only.
    /// </summary>
    BitDroneType = 4,

    /// <summary>
    /// A <b>station-type</b> bucket (P458) — wraps a real
    /// <see cref="DataStationTypeIGC"/> from the core's <c>StationTypes</c>
    /// catalog. Completing it is the port of native
    /// <c>FedSrvSiteBase::StationTypeCompleted</c> (<c>FedSrv.CPP:11343-11372</c>):
    /// it spawns the station type's construction drone at the side's home
    /// station (via <c>CreateStockDrone</c>/<c>SpawnNpcShip</c>), assigns it
    /// the station type as its base data, suppresses auto-build, and sends the
    /// team's "New constructor requesting asteroid." chat with the station
    /// type's <c>ConstructorNeedRockSound</c>.
    /// </summary>
    BitStationType = 5,

    // Native callback cases exist even though native CreateBuckets currently
    // comments out their catalog generation (Igc/sideigc.cpp:257-311).
    BitHullType = 6,
    BitPartType = 7,
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
    private int _lastPercentCompleteNotified;
    private bool _terminated;

    /// <summary>The owning side, bound when its catalog is installed.</summary>
    public Side? Side { get; internal set; }

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

    /// <summary>
    /// The real <see cref="DataDevelopmentIGC"/> this bucket wraps, when
    /// <see cref="ItemType"/> is <see cref="BucketItemType.BitDevelopment"/>;
    /// <c>null</c> for every other bucket type. The Server's completion handler
    /// reads <c>TtbmEffects</c>/<c>Gas</c>/<c>DevelopmentId</c> (and derives
    /// tech-only-ness via <see cref="DevelopmentLogic"/>) from it to run the
    /// native <c>DevelopmentCompleted</c> flow (FedSrv.CPP:11259-11276).</summary>
    public DataDevelopmentIGC? Development { get; }

    /// <summary>
    /// The real <see cref="DataDroneTypeIGC"/> this bucket wraps, when
    /// <see cref="ItemType"/> is <see cref="BucketItemType.BitDroneType"/>;
    /// <c>null</c> otherwise. Created (P458) but with no completion effect yet —
    /// P459 gives drone-type buckets theirs.
    /// </summary>
    public DataDroneTypeIGC? DroneType { get; }

    /// <summary>
    /// The real <see cref="DataStationTypeIGC"/> this bucket wraps, when
    /// <see cref="ItemType"/> is <see cref="BucketItemType.BitStationType"/>;
    /// <c>null</c> otherwise. The Server's completion handler reads
    /// <c>ConstructionDroneTypeId</c>/<c>ConstructorNeedRockSound</c> from it to
    /// run the native <c>StationTypeCompleted</c> flow (FedSrv.CPP:11343-11372).
    /// </summary>
    public DataStationTypeIGC? StationType { get; }
    public DataHullTypeIGC? HullType { get; }
    public DataPartTypeIGC? PartType { get; }

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

    /// <summary>Igc/bucketigc.h:169-172: integer build-time percentage.</summary>
    public int PercentComplete => TimeToBuild > 0
        ? (int)(InvestedMilliseconds / (10L * TimeToBuild)) : 0;

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
        short hullIdDrone = -1,
        DataDevelopmentIGC? development = null,
        DataDroneTypeIGC? droneType = null,
        DataStationTypeIGC? stationType = null,
        DataHullTypeIGC? hullType = null,
        DataPartTypeIGC? partType = null)
    {
        _site = site;
        Id = id;
        ItemType = itemType;
        Price = price;
        TimeToBuild = timeToBuild;
        Name = name;
        Description = description;
        HullIdDrone = hullIdDrone;
        Development = development;
        DroneType = droneType;
        StationType = stationType;
        HullType = hullType;
        PartType = partType;
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
        _site.BucketChangeEvent(BucketChange.MoneyChange, this); // :290, even for zero
        return amount;                                    // bucketigc.h:292 — actually spent
    }

    /// <summary>Igc/bucketigc.h:274-279: authoritative snapshot, then money notification.</summary>
    public void SetTimeAndMoney(long milliseconds, long money)
    {
        InvestedMilliseconds = milliseconds;
        InvestedMoney = money;
        _site.BucketChangeEvent(BucketChange.MoneyChange, this);
    }

    /// <summary>Igc/bucketigc.h:294-299: reset a repeatable purchase.</summary>
    public void SetEmpty()
    {
        InvestedMoney = InvestedMilliseconds = 0;
        Completed = _completionRaised = false;
        _site.BucketChangeEvent(BucketChange.Emptied, this);
    }

    /// <summary>Igc/bucketigc.cpp:87-101: notify while still in the side's catalog.</summary>
    public void Terminate()
    {
        if (_terminated) return;
        _terminated = true;
        _site.BucketChangeEvent(BucketChange.Terminated, this);
        Side?.RemoveBucket(this);
        Side = null;
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
        if (_terminated) return;
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
                int percent = PercentComplete;
                if (_lastPercentCompleteNotified != percent)
                {
                    _site.BucketChangeEvent(BucketChange.PercentCompleteChange, this);
                    _lastPercentCompleteNotified = percent;
                }
            }
        }
        _lastUpdate = now;                                 // bucketigc.h:73
    }

    private void Complete(double now)
    {
        if (_completionRaised) return;                     // fire once
        _completionRaised = true;
        // Igc/bucketigc.h:243-256: a stock purchase resets after its typed
        // callback and may be funded again. Catalog creation remains disabled.
        if (Side is { } side && (HullType.HasValue || PartType.HasValue))
        {
            if (HullType is { } hull) _site.HullTypeCompleted(side, hull);
            else if (PartType is { } part) _site.PartTypeCompleted(side, part);
            InvestedMoney = InvestedMilliseconds = 0;
            _completionRaised = false;
            return;
        }
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

    /// <summary>Build the synthetic per-side bucket catalog for a given core and
    /// the side's <b>initial</b> development techs. Always includes the existing
    /// <see cref="BucketItemType.BitShipType"/> bucket referencing
    /// <see cref="SyntheticDroneHullId"/> (falls back to an empty list if that
    /// hull is absent), then — P189 — one
    /// <see cref="BucketItemType.BitDevelopment"/> bucket per non-obsolete
    /// development in <paramref name="core"/>.<c>Developments</c>. The ship-type
    /// entry is retained untouched; development buckets are appended after it,
    /// in the core's deterministic development-id order, with sequentially
    /// increasing bucket ids starting at 2 (the ship-type bucket keeps id 1).
    /// <paramref name="civBaseAttributes"/> is the owning side's
    /// <see cref="GlobalAttributeSet"/> at creation (the civ's
    /// <c>GasBaseAttributes</c>); P478 uses it to scale each <b>development</b>
    /// bucket's price by <c>c_gaDevelopmentCost</c> and build time by
    /// <c>c_gaDevelopmentTime</c>, exactly as native bakes those into the bucket
    /// at <c>CbucketIGC::Initialize</c> (bucketigc.cpp:70-74).</summary>
    /// <remarks>
    /// <para><b>Which buckets native scales (the P478 fidelity cut).</b> The
    /// native <c>CbucketIGC::Initialize</c> applies the GA cost/time multipliers
    /// only <em>inside</em> <c>if (m_data.buyable->GetObjectType() ==
    /// OT_development)</c> (bucketigc.cpp:66-75) — so only development buckets
    /// are scaled, and drone-type / station-type / ship-type buckets are
    /// deliberately <b>not</b> (their cost is fixed by the core). Within
    /// development buckets, the <c>c_didTeamMoney</c> development (id 1) is also
    /// exempt (<c>if (pdevelopment->GetObjectID() != c_didTeamMoney)</c>,
    /// bucketigc.cpp:70) — completing it is the prosperity/economic-win
    /// shortcut, not a researchable GA.</para>
    /// <para><b>Which developments become buckets (the port of the native
    /// obsolete cut).</b> Native <c>CreateBuckets</c> adds a bucket for every
    /// buyable and then <c>AdjustBuckets</c> marks an <b>obsolete</b> one
    /// complete as a no-op rather than funding it (<c>sideigc.cpp:113-310</c>,
    /// <c>sideigc.h:624-633</c>). This port collapses that to the equivalent
    /// outcome by <b>not creating</b> a bucket for an obsolete development
    /// (tech-only <em>and</em> its effect techs already a subset of the side's
    /// dev techs — <see cref="DevelopmentLogic.IsObsolete"/>), so it can never
    /// be funded or broadcast. Every non-obsolete development — including a
    /// non-tech-only one whose effect techs are already known, which still
    /// grants an attribute delta — becomes a real bucket, mirroring native.</para>
    /// <para><b>Synthetic funding numbers.</b> Like the ship-type entry's price/
    /// build-time, a development bucket's <c>Price</c>/<c>TimeToBuild</c> come
    /// from the <em>decoded</em> <see cref="DataDevelopmentIGC"/> (the real
    /// corpus supplies those), not synthetic constants — only the ship-type
    /// entry retains its synthetic funding (per the P119 reasoning in
    /// <see cref="BucketCatalog"/>'s remarks).</para>
    /// </remarks>
    public static IReadOnlyList<Bucket> ForSide(
        StaticCore core,
        IIgcSite site,
        TechTreeBitMask initialDevelopmentTechs,
        GlobalAttributeSet civBaseAttributes)
    {
        if (core is null || !core.HullTypes.TryGetValue(SyntheticDroneHullId, out var hull))
            return Array.Empty<Bucket>();

        var buckets = new List<Bucket>
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

        // P189: one development bucket per non-obsolete development, in the
        // core's deterministic development-id order.
        short id = 2;
        foreach (var dev in core.Developments.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            if (DevelopmentLogic.IsObsolete(dev, initialDevelopmentTechs))
                continue;
            // P478 — native scales a development bucket's price/time-to-build by
            // the owning side's GA at creation (bucketigc.cpp:70-74), exempting
            // the c_didTeamMoney development (id 1).
            bool teamMoney = dev.DevelopmentId == Mission.TeamMoneyDevelopmentId;
            int price = teamMoney
                ? (int)core.Constants.FloatConstants[12]
                : (int)(dev.Price * civBaseAttributes.Get(GlobalAttributeSet.DevelopmentCost));
            uint timeToBuild = teamMoney
                ? dev.TimeToBuild
                : (uint)(dev.TimeToBuild * civBaseAttributes.Get(GlobalAttributeSet.DevelopmentTime));
            buckets.Add(new Bucket(
                site,
                id: id++,
                BucketItemType.BitDevelopment,
                price: price,
                timeToBuild: timeToBuild,
                name: dev.Name,
                description: dev.Description,
                development: dev));
        }

        // P458: append the drone-type and station-type buckets AFTER the
        // developments, in native CreateBuckets order (drone types then
        // station types, sideigc.cpp:204-255), each gated on the side's
        // reachable techs (see ReachableTechs). Existing bucket ids (the
        // synthetic id 1 and the developments, which start at 2) are
        // untouched — the new buckets continue id numbering after them.
        var reachable = ReachableTechs(core, initialDevelopmentTechs);
        foreach (var dt in core.DroneTypes.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            if (!TechTreeBitMask.FromBytes(dt.TtbmRequired).IsSubsetOf(reachable))
                continue;
            buckets.Add(new Bucket(
                site,
                id: id++,
                BucketItemType.BitDroneType,
                price: dt.Price,
                timeToBuild: dt.TimeToBuild,
                name: dt.Name,
                description: dt.Description,
                droneType: dt));
        }
        foreach (var st in core.StationTypes.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            if (!TechTreeBitMask.FromBytes(st.TtbmRequired).IsSubsetOf(reachable))
                continue;
            buckets.Add(new Bucket(
                site,
                id: id++,
                BucketItemType.BitStationType,
                price: st.Price,
                timeToBuild: st.TimeToBuild,
                name: st.Name,
                description: st.Description,
                stationType: st));
        }

        return buckets;
    }

    /// <summary>
    /// P487 — the bucket definition the <em>client</em> needs to render an
    /// investment pane, sharing the exact id numbering / skip rules of
    /// <see cref="ForSide"/> so a client-synthesized list lines up bucket-for-bucket
    /// with the ids the server reports on <c>BUCKET_STATUS(65)</c>. Carries the
    /// fields a display needs (name, kind, price, build time) plus the ids the
    /// client's <c>CanInvest</c> gate reads: the drone <see cref="HullIdDrone"/>
    /// (to count live drones of that type), the <see cref="DroneTypeId"/> and the
    /// <see cref="StationTypeId"/>.
    /// </summary>
    public sealed record BucketView(
        short Id,
        BucketItemType ItemType,
        string Name,
        string Description,
        int Price,
        uint TimeToBuildSec,
        short HullIdDrone,
        short DroneTypeId,
        short DevelopmentId,
        short StationTypeId,
        DataDevelopmentIGC? Development,
        DataDroneTypeIGC? DroneType,
        DataStationTypeIGC? StationType);

    /// <summary>
    /// P487 — synthesize a side's bucket list in the <em>same</em> order and with
    /// the <em>same</em> ids as the server's <see cref="ForSide"/>, so the client's
    /// investment pane can correlate each <c>BUCKET_STATUS(65)</c> (which carries
    /// only side+bucketId+money+time) back to its name/price/kind. Identical inputs
    /// yield identical ids: pass the side's <b>initial</b> development-tech mask
    /// (from <c>CREATE_BUCKETS</c> at join — equal to the civ's base techs for a
    /// mission-start join, which is what the server baked into its list at
    /// <c>Mission.Assemble</c>) and its creation-time GA (identity before any
    /// development completes). The synthetic ship-type bucket (id 1) is included,
    /// exactly as the server hosts it.
    /// </summary>
    public static IReadOnlyList<BucketView> Describe(
        StaticCore core,
        TechTreeBitMask initialDevelopmentTechs,
        GlobalAttributeSet civBaseAttributes)
    {
        var list = new List<BucketView>();
        foreach (var d in EnumerateDefs(core, initialDevelopmentTechs, civBaseAttributes))
            list.Add(new BucketView(d.Id, d.ItemType, d.Name, d.Description, d.Price,
                d.TimeToBuildSec, d.HullIdDrone, d.DroneTypeId, d.DevelopmentId,
                d.StationTypeId, d.Development, d.DroneType, d.StationType));
        return list;
    }

    /// <summary>
    /// P487 — the single source of truth for the bucket catalog's <em>order and
    /// id numbering</em>, consumed by both <see cref="ForSide"/> (which wraps each
    /// definition into a live <see cref="Bucket"/>) and <see cref="Describe"/>
    /// (which wraps it into a display <see cref="BucketView"/>). Keeping the two
    /// callers over one enumeration guarantees the client's synthesized ids can
    /// never drift from the server's.
    /// </summary>
    private readonly record struct Def(
        short Id,
        BucketItemType ItemType,
        string Name,
        string Description,
        int Price,
        uint TimeToBuildSec,
        short HullIdDrone,
        short DroneTypeId,
        short DevelopmentId,
        short StationTypeId,
        DataDevelopmentIGC? Development,
        DataDroneTypeIGC? DroneType,
        DataStationTypeIGC? StationType);

    /// <summary>
    /// P487 — enumerate the side's bucket definitions in native <c>CreateBuckets</c>
    /// order (ship-type id 1, then developments from id 2 skipping obsolete ones —
    /// P189 — then drone types then station types gated on the side's reachable
    /// techs — P458), applying the P478 development GA cost/time scaling and the
    /// <c>c_didTeamMoney</c> exemption exactly as <see cref="ForSide"/> did before
    /// it was factored. Yields nothing when the synthetic drone hull is absent from
    /// the core, mirroring the original <c>ForSide</c> early-out.
    /// </summary>
    private static IEnumerable<Def> EnumerateDefs(
        StaticCore core,
        TechTreeBitMask initialDevelopmentTechs,
        GlobalAttributeSet civBaseAttributes)
    {
        if (core is null || !core.HullTypes.TryGetValue(SyntheticDroneHullId, out var hull))
            yield break;

        yield return new Def(
            1, BucketItemType.BitShipType, hull.Name, hull.Description,
            SyntheticPrice, SyntheticTimeToBuild, hull.HullId, -1, -1, -1, null, null, null);

        short id = 2;
        foreach (var dev in core.Developments.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            if (DevelopmentLogic.IsObsolete(dev, initialDevelopmentTechs))
                continue;
            bool teamMoney = dev.DevelopmentId == Mission.TeamMoneyDevelopmentId;
            int price = teamMoney
                ? (int)core.Constants.FloatConstants[12]
                : (int)(dev.Price * civBaseAttributes.Get(GlobalAttributeSet.DevelopmentCost));
            uint timeToBuild = teamMoney
                ? dev.TimeToBuild
                : (uint)(dev.TimeToBuild * civBaseAttributes.Get(GlobalAttributeSet.DevelopmentTime));
            yield return new Def(
                id++, BucketItemType.BitDevelopment, dev.Name, dev.Description,
                price, timeToBuild, -1, -1, dev.DevelopmentId, -1, dev, null, null);
        }

        var reachable = ReachableTechs(core, initialDevelopmentTechs);
        foreach (var dt in core.DroneTypes.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            if (!TechTreeBitMask.FromBytes(dt.TtbmRequired).IsSubsetOf(reachable))
                continue;
            yield return new Def(
                id++, BucketItemType.BitDroneType, dt.Name, dt.Description,
                dt.Price, dt.TimeToBuild, dt.HullTypeId, dt.DroneTypeId, -1, -1, null, dt, null);
        }
        foreach (var st in core.StationTypes.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            if (!TechTreeBitMask.FromBytes(st.TtbmRequired).IsSubsetOf(reachable))
                continue;
            yield return new Def(
                id++, BucketItemType.BitStationType, st.Name, st.Description,
                st.Price, st.TimeToBuild, -1, -1, -1, st.StationTypeId, null, null, st);
        }
    }

    /// <summary>
    /// P458 — the side's <b>reachable techs</b>, the mask used to gate the
    /// drone-type and station-type buckets, approximating native
    /// <c>CreateBuckets</c>' <c>ttbmLocalUltimate</c> (<c>sideigc.cpp:119-175,
    /// 206</c>) as closely as this port's <see cref="DevelopmentLogic"/>
    /// allows. Native computes it as:
    /// <list type="number">
    /// <item><c>ttbmLocalUltimate</c> seeded to the OR of every station type's
    /// <c>GetLocalTechs()</c> (<c>sideigc.cpp:142</c>);</item>
    /// <item><c>m_ttbmUltimateTechs</c> seeded to the civ's base techs, OR-ed
    /// with every part type's and every station type's effect techs, then
    /// grown to a fixed point by repeatedly buying any development whose
    /// <c>RequiredTechs ≤ ultimate</c> and whose effect techs are not already
    /// present (<c>sideigc.cpp:123-174</c>);</item>
    /// <item>finally <c>ttbmLocalUltimate |= m_ttbmUltimateTechs</c>
    /// (<c>sideigc.cpp:206</c>) — the single gate both drone types
    /// (<c>:218</c>) and station types (<c>:242</c>) are tested against.</item>
    /// </list>
    /// <para>This port reproduces all three terms faithfully. Part types are
    /// read from <see cref="StaticCore.PartTypes"/>, which already contains the
    /// launcher-form parts bridged into full <see cref="DataPartTypeIGC"/>
    /// records with their <c>TtbmEffects</c> (the loader's
    /// <c>From&lt;Kind&gt;Launcher</c> bridges), so a single walk of
    /// <c>PartTypes</c> is the port's analogue of native's single
    /// <c>GetPartTypes()</c> list. No native term is omitted; this is the
    /// faithful port.</para>
    /// </summary>
    private static TechTreeBitMask ReachableTechs(StaticCore core, TechTreeBitMask initialDevelopmentTechs)
    {
        // Native ttbmLocalUltimate starts as the OR of every station type's
        // local techs (sideigc.cpp:142).
        var local = TechTreeBitMask.Empty;
        foreach (var st in core.StationTypes.Values)
            local = local.Or(TechTreeBitMask.FromBytes(st.TtbmLocal));

        // Native m_ttbmUltimateTechs (sideigc.cpp:123-133): the civ's dev
        // techs, OR-ed with every part type's AND every station type's effect
        // techs. This port's loader splits OT_partType into PartTypes
        // (non-launcher parts) and Launchers (the launcher form of
        // missile/mine/chaff/probe), so both dictionaries contribute, exactly
        // as native's single GetPartTypes() walk contributes every record.
        var ultimate = initialDevelopmentTechs;                 // m_data.ttbmDevelopmentTechs = civ base techs
        foreach (var p in core.PartTypes.Values)
            ultimate = ultimate.Or(TechTreeBitMask.FromBytes(p.TtbmEffects));
        foreach (var st in core.StationTypes.Values)
            ultimate = ultimate.Or(TechTreeBitMask.FromBytes(st.TtbmEffects));

        // Fixed-point buy closure over developments (sideigc.cpp:149-173):
        // repeatedly OR in the effect techs of any development whose required
        // techs are already reachable and whose effects add something new.
        bool added;
        do
        {
            added = false;
            foreach (var dev in core.Developments.Values)
            {
                var req = TechTreeBitMask.FromBytes(dev.TtbmRequired);
                var eff = TechTreeBitMask.FromBytes(dev.TtbmEffects);
                if (req.IsSubsetOf(ultimate) && !eff.IsSubsetOf(ultimate))
                {
                    ultimate = ultimate.Or(eff);
                    added = true;
                }
            }
        }
        while (added);

        // Final ttbmLocalUltimate |= m_ttbmUltimateTechs (sideigc.cpp:206).
        return local.Or(ultimate);
    }
}
