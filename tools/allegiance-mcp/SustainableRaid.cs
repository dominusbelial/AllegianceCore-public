using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

/// <summary>The ordinary commander's first pre-finale sortie. All inputs belong to
/// one side and briefing epoch. Purchases and construction remain with that civ's
/// commander and dock. No preparation recall or finale reservation is needed.</summary>
internal sealed class SustainableRaid
{
    internal sealed record Fit(StrikeVolley.Loadout? Weapons, Dictionary<short, double> TravelSeconds,
        short? HomeStation, bool Protected);
    internal sealed record Observation(double At, HashSet<short> Visible, bool Closing,
        Func<short, short?> Destroyer, string Plan, Dictionary<short, MemberObservation>? Members = null,
        Func<ResearchTactics.Target, StrikeVolley.StationProfile?>? Profile = null);
    internal enum MemberState { Flying, Lost, Withdrawn }
    internal readonly record struct MemberObservation(MemberState State, int Deaths);
    private readonly Dictionary<short, int> _launchDeaths = [];
    private readonly Dictionary<short, MemberState> _departed = [];
    internal bool Available(short id) => !_departed.ContainsKey(id);
    internal sealed record Wing(ResearchTactics.Target Station, short[] Members, short Defender, short Home);
    internal sealed record Event(string Stage, short Target, double At, string Detail);

    internal Wing? Active { get; private set; }
    internal bool Completed { get; private set; }
    private readonly Dictionary<short, double> _sightings = [];
    private readonly List<Event> _events = [];
    private double _readyAt;
    private float _shield, _armor;
    private bool _arrived, _hit;
    private readonly HashSet<short> _dispatched = [];

    private readonly Dictionary<short, ResearchTactics.Order> _issued = [];

    internal ResearchTactics.Order[] TakeReleasedOrders()
    {
        if (_issued.Count == 0) return [];
        var released = _issued.Values.Where(o => Active is null || !Available(o.Recipient)).ToArray();
        foreach (var order in released) _issued.Remove(order.Recipient);
        return released;
    }

    internal Event[] TakeEvents()
    {
        var events = _events.ToArray();
        _events.Clear();
        return events;
    }

    // A complete candidate is one distinct set of owners at one station. Reserve
    // the last replay for a greedy wing in case the exact search exhausts its share.
    internal const int CandidateBudget = 12;
    internal const int RoundBudget = 768;
    internal int RoundsReplayed { get; private set; }
    private const int StationsPerTick = 4;
    internal int CandidatesExamined { get; private set; }
    private readonly HashSet<short> _searched = [];
    private double _searchedAt = double.NaN;
    private short? _resumeAfter;

    internal Wing? Ready(double now, Observation observation, ResearchTactics.Target[] enemies,
        ResearchTactics.Pilot[] pilots)
    {
        CandidatesExamined = RoundsReplayed = 0;
        if (observation.Closing || now - observation.At is < 0 or > 2) return null;
        if (_searchedAt != observation.At)
        {
            _searched.Clear();
            _searchedAt = observation.At;
        }
        var stations = enemies.Where(t => !t.Friendly && t.Type == IgcObjectType.Station
                && observation.Visible.Contains(t.Id) && !_searched.Contains(t.Id) && !observation.Destroyer(t.Id).HasValue)
            .OrderBy(t => t.Garrison).ThenByDescending(t => t.Damaged).ThenBy(t => t.Id).ToArray();
        if (stations.Length == 0) return null;
        var defenders = pilots.Where(p => p.Raid is { HomeStation: not null, Protected: false, Weapons: not null }).ToArray();
        if (defenders.Length == 0) return null;
        var guardIds = defenders.Select(p => p.Id).ToHashSet();
        int start = _resumeAfter.HasValue ? Array.FindIndex(stations, t => t.Id == _resumeAfter) + 1 : 0;
        Wing? best = null;
        int visited = 0;
        for (; visited < Math.Min(stations.Length, StationsPerTick) && CandidatesExamined < CandidateBudget && RoundsReplayed < RoundBudget; visited++)
        {
            var station = stations[(start + visited) % stations.Length];
            _searched.Add(station.Id);
            _resumeAfter = station.Id;
            var observedProfile = observation.Profile is null ? station.Profile : observation.Profile(station);
            if (observedProfile is not { MaxArmor: > 0, MaxShield: > 0 } profile) continue;
            var eligible = new List<Candidate>();
            var owners = new HashSet<short>();
            foreach (var pilot in pilots)
            {
                if (pilot.Raid is not { Protected: false, Weapons: { } fit } raid
                    || !raid.TravelSeconds.TryGetValue(station.Id, out double travel)
                    || !StrikeVolley.DamagesStation(profile, fit) || !owners.Add(pilot.Id)) continue;
                // A sole home defender cannot participate in any feasible set.
                if (guardIds.Count == 1 && guardIds.Contains(pilot.Id)) continue;
                double potential = 0;
                foreach (var mount in fit.Mounts)
                {
                    var damage = StrikeVolley.RoundDamage(profile, mount.DamageType, mount.Power);
                    int shots = mount.LoadTime > 0
                        ? (int)Math.Min(mount.Shots, 1 + Math.Floor(StrikeVolley.VolleyWindowSeconds / mount.LoadTime))
                        : mount.Shots;
                    potential += shots * (double)Math.Max(damage.Armor / profile.MaxArmor, damage.Shield / profile.MaxShield);
                }
                eligible.Add(new(pilot.Id, fit, travel, potential));
            }
            var ranked = eligible.OrderByDescending(p => p.Potential).ThenBy(p => p.Travel).ThenBy(p => p.Id).ToArray();
            int limit = Math.Min(best?.Members.Length - 1 ?? StrikeVolley.MaxPlannedBombers, ranked.Length);
            if (SmallestWing(station, profile, ranked, defenders, guardIds, limit) is { } wing) best = wing;
            if (best?.Members.Length == 1) break;
        }
        if (visited == stations.Length) _resumeAfter = null;
        return best;
    }

    private readonly record struct Candidate(short Id, StrikeVolley.Loadout Weapons, double Travel, double Potential);

    private Wing? SmallestWing(ResearchTactics.Target station, StrikeVolley.StationProfile profile,
        Candidate[] ranked, ResearchTactics.Pilot[] defenders, HashSet<short> guardIds, int limit)
    {
        if (limit <= 0) return null;
        // Greedy fallback keeps the strongest compatible arrivals and leaves a
        // home defender. Its sorted indices also identify duplicates in the exact search.
        var greedy = new List<int>(limit);
        int guardsLeft = guardIds.Count;
        double first = ranked[0].Travel, last = first;
        for (int i = 0; i < ranked.Length && greedy.Count < limit; i++)
        {
            var c = ranked[i];
            double lo = Math.Min(first, c.Travel), hi = Math.Max(last, c.Travel);
            if (hi - lo > StrikeVolley.VolleyWindowSeconds) continue;
            if (guardIds.Contains(c.Id))
            {
                if (guardsLeft == 1) continue;
                guardsLeft--;
            }
            greedy.Add(i);
            (first, last) = (lo, hi);
        }
        var fallback = greedy.ToArray();
        var indices = new int[limit];
        bool triedFallback = false;
        double upper = 0;
        for (int size = 1; size <= limit; size++)
        {
            upper += ranked[size - 1].Potential;
            if (upper < 1.99999) continue;
            for (int i = 0; i < size; i++) indices[i] = i;
            var set = indices.AsSpan(0, size);
            while (CandidatesExamined < CandidateBudget - 1 && RoundsReplayed < RoundBudget / 2)
            {
                bool greedySet = set.SequenceEqual(fallback);
                triedFallback |= greedySet;
                if (Evaluate(set, greedySet ? RoundBudget : RoundBudget / 2) is { } wing) return wing;
                int pos = size - 1;
                while (pos >= 0 && indices[pos] == ranked.Length - size + pos) pos--;
                if (pos < 0) break;
                indices[pos]++;
                for (int i = pos + 1; i < size; i++) indices[i] = indices[i - 1] + 1;
            }
            if (CandidatesExamined >= CandidateBudget - 1 || RoundsReplayed >= RoundBudget / 2) break;
        }
        return !triedFallback && fallback.Length > 0 ? Evaluate(fallback, RoundBudget) : null;

        Wing? Evaluate(ReadOnlySpan<int> set, int roundLimit)
        {
            CandidatesExamined++;
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity, potential = 0;
            foreach (int i in set)
            {
                lo = Math.Min(lo, ranked[i].Travel);
                hi = Math.Max(hi, ranked[i].Travel);
                potential += ranked[i].Potential;
            }
            if (potential < 1.99999 || hi - lo > StrikeVolley.VolleyWindowSeconds) return null;
            foreach (var guard in defenders)
            {
                bool claimed = false;
                foreach (int i in set) if (ranked[i].Id == guard.Id) claimed = true;
                if (claimed) continue;
                var shots = new PriorityQueue<Burst, (double At, float Power, int Mount)>();
                int sequence = 0;
                foreach (int i in set)
                {
                    var c = ranked[i];
                    foreach (var mount in c.Weapons.Mounts)
                    {
                        if (mount.Shots <= 0) continue;
                        var burst = new Burst(c.Weapons, mount, c.Travel - lo, 0);
                        shots.Enqueue(burst, (burst.Start, mount.Power, sequence++));
                    }
                }
                if (!StrikeVolley.ReplayInOrder(profile, OrderedShots(shots, roundLimit), StrikeVolley.VolleyWindowSeconds).Killed)
                    return null;
                var ids = new short[set.Length];
                for (int j = 0; j < ids.Length; j++) ids[j] = ranked[set[j]].Id;
                Array.Sort(ids);
                return new(station, ids, guard.Id, guard.Raid!.HomeStation!.Value);
            }
            return null;
        }
    }

    private readonly record struct Burst(StrikeVolley.Loadout Owner, StrikeVolley.Mount Mount, double Start, int Index);

    // Replay a chronological prefix. Dropping an individual mount to meet a
    // budget would change regeneration/overflow between the remaining shots.
    private IEnumerable<(double At, StrikeVolley.Loadout Owner, StrikeVolley.Shot Shot)> OrderedShots(
        PriorityQueue<Burst, (double At, float Power, int Mount)> shots, int limit)
    {
        while (RoundsReplayed < limit && shots.TryDequeue(out var burst, out var key))
        {
            var mount = burst.Mount;
            RoundsReplayed++;
            yield return (key.At, burst.Owner, new(burst.Index * mount.LoadTime, mount.Power, mount.DamageType));
            int next = burst.Index + 1;
            double at = burst.Start + next * mount.LoadTime;
            if (next < mount.Shots && at <= StrikeVolley.VolleyWindowSeconds)
                shots.Enqueue(burst with { Index = next }, (at, mount.Power, key.Mount));
        }
    }

    internal void Observe(double time, bool started, Observation observation, ResearchTactics.Target[] enemies,
        ResearchTactics.Target[] targets, ResearchTactics.Pilot[] pilots)
    {
        if (Completed) return;
        foreach (var station in enemies.Where(t => time - observation.At is >= 0 and <= 2 && observation.Visible.Contains(t.Id)))
            _sightings.TryAdd(station.Id, observation.At);
        if (Active is null)
        {
            if (!started || Ready(time, observation, enemies, pilots) is not { } wing) return;
            Active = wing;
            foreach (short id in wing.Members)
                if (observation.Members?.TryGetValue(id, out var member) == true) _launchDeaths[id] = member.Deaths;
            _readyAt = time;
            _armor = wing.Station.Hull;
            _shield = wing.Station.Shield;
            _events.Add(new("sighting", wing.Station.Id, _sightings[wing.Station.Id], ""));
            _events.Add(new("ready", wing.Station.Id, time,
                $"members={string.Join(',', wing.Members)} defender={wing.Defender} home={wing.Home} plan={observation.Plan}"));
        }
        var raid = Active;
        var target = targets.FirstOrDefault(t => t.Type == IgcObjectType.Station && t.Id == raid.Station.Id);
        foreach (short id in raid.Members)
            if (observation.Members?.TryGetValue(id, out var member) == true)
            {
                if (_launchDeaths.TryGetValue(id, out int deaths) && member.Deaths > deaths)
                    _departed.TryAdd(id, MemberState.Lost);
                else if (member.State != MemberState.Flying) _departed.TryAdd(id, member.State);
            }
        var survivors = pilots.Where(p => raid.Members.Contains(p.Id) && Available(p.Id)).ToArray();
        if (!_arrived && survivors.Any(p => _dispatched.Contains(p.Id) && p.Sector == raid.Station.Sector))
        {
            _arrived = true;
            _events.Add(new("arrival", raid.Station.Id, time, ""));
        }
        var killer = observation.Destroyer(raid.Station.Id);
        bool credited = killer is { } launcher && _dispatched.Contains(launcher);
        if (!_hit && (credited || _arrived && target is { Hull: >= 0, Shield: >= 0 }
                && observation.Visible.Contains(target.Id) && (target.Hull < _armor || target.Shield < _shield)))
        {
            _hit = true;
            _events.Add(new("hit", raid.Station.Id, time, credited ? "source=credited-launcher" : "source=observed-health"));
        }
        if (target is not null && observation.Visible.Contains(target.Id))
            (_armor, _shield) = (target.Hull, target.Shield);
        string outcome;
        if (credited) outcome = "credited-kill";
        else if (target is not null && target.Side != raid.Station.Side) outcome = "capture";
        else if (killer.HasValue) outcome = "withdrawn";
        else if (_departed.Count == raid.Members.Length)
            outcome = _departed.Values.All(s => s == MemberState.Lost) ? "repulse" : "withdrawn";
        else if (started && observation.Closing) outcome = "withdrawn";
        else if (!started || time - _readyAt >= 3 * ResearchTactics.RallyTimeoutSeconds) outcome = "timeout";
        else return;
        int lost = _departed.Values.Count(s => s == MemberState.Lost);
        _events.Add(new("outcome", raid.Station.Id, time,
            $"result={outcome} launcher={killer ?? -1} lost={lost} withdrawn={_departed.Count - lost}"));
        Completed = true;
        Active = null;
    }

    internal void SentOrder(double time, ResearchTactics.Order order)
    {
        _issued[order.Recipient] = order;
        if (Active is null || order.Command != OrderChange.CidAttack || !_dispatched.Add(order.Recipient) || _dispatched.Count != 1) return;
        _events.Add(new("launch", Active.Station.Id, time, "source=commander-order"));
    }

    internal static StrikeVolley.StationProfile? StationProfile(StaticCore core, short type, GlobalAttributeSet attributes)
        => StrikeVolley.Profile(core, type) is { } p ? p with
        {
            MaxArmor = p.MaxArmor * attributes.Get(GlobalAttributeSet.MaxArmorStation),
            ArmorRegen = p.ArmorRegen * attributes.Get(GlobalAttributeSet.ArmorRegenerationStation),
            MaxShield = p.MaxShield * attributes.Get(GlobalAttributeSet.MaxShieldStation),
            ShieldRegen = p.ShieldRegen * attributes.Get(GlobalAttributeSet.ShieldRegenerationStation),
        } : null;

    // Native mounted inventory (igc.h:1687-1690), current owner loadout and
    // launch acknowledgements. Cargo and incomplete mounts cannot fire.
    internal static StrikeVolley.Loadout? Mounted(StaticCore core, short hull, BotManager.LoadoutGrant grant,
        IReadOnlyList<ExpandedPartData>? current, long fired, float ammo, float energy, GlobalAttributeSet attributes)
    {
        if (grant.HullId != hull || current is null || grant.Parts is null || !core.HullTypes.TryGetValue(hull, out var type)) return null;
        var mounts = new List<StrikeVolley.Mount>();
        int rounds = (int)(Math.Clamp(ammo, 0, 1) * type.MaxAmmo);
        float energyBudget = Math.Clamp(energy, 0, 1) * type.MaxEnergy * attributes.Get(GlobalAttributeSet.MaxEnergy);
        foreach (var part in current.Where(p => p.MountId < 128 && p.Amount > 0 && p.FractionMounted >= ExpandedPartData.FullyMounted))
        {
            var bought = grant.Parts.FirstOrDefault(p => p.PartId == part.PartId && p.MountId == part.MountId);
            if (bought.Amount <= 0) continue;
            int amount = Math.Min(part.Amount, bought.Amount);
            if (core.Launchers.TryGetValue(part.PartId, out var launcher) && core.MissileTypes.ContainsKey(launcher.ExpendabletypeId))
                amount = (int)Math.Min(amount, Math.Max(0, bought.Amount - fired));
            var weapon = StrikeVolley.FromParts(core, hull, [part with { Amount = (short)amount }])?.Mounts.FirstOrDefault();
            if (weapon is null) continue;
            if (core.PartTypes.TryGetValue(part.PartId, out var pt) && pt.Type is DataPartTypeIGC.Weapon gun)
            {
                int shots = weapon.Shots;
                if (gun.CAmmoPerShot > 0) shots = Math.Min(shots, rounds / gun.CAmmoPerShot);
                if (gun.EnergyPerShot > 0) shots = Math.Min(shots, (int)(energyBudget / gun.EnergyPerShot));
                energyBudget -= shots * Math.Max(0, gun.EnergyPerShot);
                rounds -= shots * gun.CAmmoPerShot;
                weapon = weapon with { Shots = shots, Power = weapon.Power * attributes.Get(GlobalAttributeSet.DamageGuns) };
            }
            else weapon = weapon with { Power = weapon.Power * attributes.Get(GlobalAttributeSet.DamageMissiles) };
            if (weapon.Shots > 0) mounts.Add(weapon);
        }
        return mounts.Count > 0 ? new(hull, mounts.ToArray()) : null;
    }
}
