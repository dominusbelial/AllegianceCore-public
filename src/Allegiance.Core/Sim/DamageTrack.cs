using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// P549 — the port of native <c>DamageTrack</c> (<c>common.cpp:2570-2665</c>,
/// <c>igc.h:4930-5070</c>): the per-ship tally of how much damage each launcher
/// dealt, whose single purpose is <b>kill credit</b> — the death goes to the
/// launcher that dealt the <b>most total damage</b>, not the last hitter
/// (<c>shipIGC.cpp:1319-1331</c>, which reads the track's first, highest-sorted
/// bucket). Each <see cref="Ship"/> owns one (see <c>Ship.cs</c>); it records a
/// bucket per distinct damaging launcher and keeps the bucket list sorted
/// descending by total damage so <see cref="TopDealer"/> is always the leading
/// candidate.
/// </summary>
/// <remarks>
/// Faithful to native within this slice, with the port's standing simplifications
/// stated:
/// <list type="bullet">
/// <item><b>Buckets + sort.</b> <see cref="ApplyDamage"/> finds-or-creates a
/// <c>DamageBucket</c> per launcher, adds the damage, and re-sorts descending —
/// <c>DamageTrack::ApplyDamage</c>/<c>DamageTrack::sort</c>
/// (<c>common.cpp:2592-2665</c>), an insertion sort that keeps the list sorted
/// descending by <c>totalDamage</c>.</item>
/// <item><b>Decay.</b> Native retains damage for 8 slots &times; 5 s
/// (<c>c_iDamageSlotCount=8</c>, <c>c_dwDamageSlotInterval=5000 ms</c>,
/// <c>igc.h:4927-4928</c>): a slot is retired every 5 s, subtracting that slot's
/// accumulated damage from each bucket's total, and a bucket whose total drops
/// below 0.5 f is deleted (<c>common.cpp:2574-2581</c>). This port drives the
/// same 5 s slot retirement from the owning ship's per-tick update
/// (<see cref="Ship.OnUpdate"/>), giving the identical 40 s retention — the only
/// structural difference is that native shares one slot phase across all tracks
/// via the mission's <c>DamageTrackSet</c>, whereas each ported track keeps its
/// own self-timed slots (phase alignment is unobservable for kill credit).</item>
/// <item><b>Flash / <c>m_timeLastDamage</c>.</b> The bucket's per-slot
/// <c>m_damage[]</c> array (needed only for the shield <c>flash()</c> diagnostic)
/// is omitted; only the running <c>totalDamage</c> and the slot partitioning that
/// powers decay are kept. This is the one deliberate omission, documented: the
/// flash is a client-only red-flash hint, out of this server task's scope.</item>
/// </list>
/// </remarks>
public sealed class DamageTrack
{
    private const int SlotCount = 8;                  // c_iDamageSlotCount (igc.h:4928)
    private const double SlotIntervalSeconds = 5.0;   // c_dwDamageSlotInterval = 5000 ms (igc.h:4927)

    private readonly List<DamageBucket> _buckets = [];
    private int _idSlot;
    private double _timeSlotChange;

    /// <summary>Seed the first slot-retirement deadline at <paramref name="now"/>
    /// + 5 s, mirroring <c>DamageTrackSet::Initialize</c>
    /// (<c>igc.h:5075-5078</c>).</summary>
    public DamageTrack(double now) => _timeSlotChange = now + SlotIntervalSeconds;

    /// <summary>Record <paramref name="damage"/> from <paramref name="model"/> at
    /// sim time <paramref name="timeNow"/>, then re-sort the bucket list descending
    /// by total damage — <c>DamageTrack::ApplyDamage</c> (<c>common.cpp:2592-2608</c>)
    /// followed by <c>DamageTrack::sort</c> (<c>common.cpp:2624-2665</c>).</summary>
    public void ApplyDamage(double timeNow, Model model, float damage)
    {
        Update(timeNow);   // retire any stale slot before recording (native cycles slots on the mission tick)
        var bucket = _buckets.FirstOrDefault(b => ReferenceEquals(b.Model, model));
        if (bucket is null)
        {
            bucket = new DamageBucket(model);   // DamageBucket ctor (igc.h:5049-5061): new bucket per model
            _buckets.Add(bucket);
        }
        bucket.Apply(_idSlot, damage);
        Sort();
    }

    /// <summary>The launcher that has dealt the most total damage, or <c>null</c>
    /// when no damage has been recorded. Equals the <b>first</b> bucket of
    /// <c>GetDamageBuckets()</c> (<c>shipIGC.cpp:1320-1323</c>).</summary>
    public Model? TopDealer => _buckets.Count == 0 ? null : _buckets[0].Model;

    /// <summary>Advance slot retirement — the port of
    /// <c>DamageTrackSet::Update</c> + <c>DamageTrack::SwitchSlots</c>
    /// (<c>igc.h:5080-5095</c>, <c>common.cpp:2571-2581</c>): every 5 sim-seconds
    /// the current slot index advances and each bucket subtracts the damage that
    /// fell in the newly-entered slot (which, 8 slots later, retires the damage
    /// recorded 40 s ago); a bucket whose total drops below 0.5 f is deleted.
    /// Called each tick from the owning ship's <see cref="Ship.OnUpdate"/>.</summary>
    public void Update(double now)
    {
        while (now >= _timeSlotChange)
        {
            _idSlot = (_idSlot + 1) % SlotCount;
            _timeSlotChange += SlotIntervalSeconds;
            for (int i = _buckets.Count - 1; i >= 0; i--)
            {
                var b = _buckets[i];
                b.RetireSlot(_idSlot);                                   // db->SwitchSlots(m_pset->m_idSlot)
                if (b.TotalDamage < 0.5f) _buckets.RemoveAt(i);          // delete when total < 0.5 (common.cpp:2576-2578)
            }
            if (_buckets.Count == 0) break;
        }
    }

    private void Sort() => _buckets.Sort((a, b) => b.TotalDamage.CompareTo(a.TotalDamage));

    /// <summary>A single launcher's running total with its 8-slot decay
    /// partition — <c>DamageBucket</c> (<c>igc.h:4988-5066</c>), reduced to the
    /// fields that feed kill credit and decay (the client-only <c>flash()</c>
    /// timing is omitted, see the class remarks).</summary>
    private sealed class DamageBucket
    {
        public readonly Model Model;
        private readonly float[] _slotDamage = new float[SlotCount];

        public DamageBucket(Model model) => Model = model;

        public float TotalDamage { get; private set; }

        public void Apply(int slot, float d)
        {
            TotalDamage += d;
            _slotDamage[slot] += d;                                     // DamageBucket::ApplyDamage (igc.h:5027-5044)
        }

        public void RetireSlot(int slot)
        {
            TotalDamage -= _slotDamage[slot];
            _slotDamage[slot] = 0f;                                     // DamageBucket::SwitchSlots (igc.h:5017-5025)
        }
    }
}
