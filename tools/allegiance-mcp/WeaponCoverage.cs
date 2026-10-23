using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using V3 = Allegiance.Core.Collision.Vector3;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P796 — weapon coverage telemetry, built ONLY from events the bots already
/// receive (no new wire, no per-tick logging):
///   • gun triggers           — the bot fire law's rising AllWeapons edge
///                              (BotManager.cs EngageShipAt, shipIGC.cpp:2600)
///   • missile launches       — SendFireMissile (MaybeFireMissile)
///   • probe/mine/chaff drops — SendFireExpendable (Dispensers/Medevac laws)
///   • hull hits              — PROJECTILE_HULL_HIT(213) broadcasts, shooter-
///                              attributed via SourceId, deduped across the
///                              observers that share the victim's sector
///   • damage                 — VICTIM_DAMAGED(217) (the victim's own unicast;
///                              every showcase actor is a harness bot, so the
///                              union over bots covers the whole fight)
///   • kills                  — KILL_SHIP(105) via the world's EventLog
///   • hulls bought/launched  — the real BUY_LOADOUT launch acks the harness
///                              itself drives (the whole showcase fleet is
///                              harness-controlled)
/// The sweeps run ~1 Hz per bot from the manager loop and are bounded; the
/// per-minute `[gallery]` console line and the end-of-match table are the only
/// logging. One process-wide instance per the one-BotManager-per-process
/// harness invariant (static purchase paths must reach it).
/// </summary>
internal sealed class WeaponCoverage
{
    internal static readonly WeaponCoverage Global = new();

    internal sealed record PartStat(int Equipped, long Fired, long Hits, double Damage);
    internal sealed record ExpendableStat(long Fired, long Hits, double Damage);
    internal sealed record HullStat(long Bought, long Launched, long Killed);

    private readonly object _sync = new();
    private readonly Dictionary<short, PartStat> _parts = new();            // by real part id
    private readonly Dictionary<short, ExpendableStat> _expendables = new(); // by expendable type id (missile/mine/probe/chaff)
    private readonly Dictionary<short, HullStat> _hulls = new();            // by hull id
    private readonly Dictionary<short, ShipObs> _ships = new();             // observed ship id → hull/side
    private readonly Dictionary<short, (double AtMs, short PartId, short TypeId, string Kind)> _lastFire = new();
    private readonly Dictionary<ConnectionSession, (long KillSeq, long ChaffMs, object? LastStage)> _cursors = new();
    private readonly HashSet<long> _seenExpendables = new();                // missile/probe/mine object ids
    private readonly HashSet<long> _seenEvents = new();                     // broadcast hit/chaff dedupe
    private double _startedAtMs = double.NaN;
    private bool _endOfMatchPending;

    private sealed record ShipObs(short HullId, short Side);

    private const double HitAttributionWindowSeconds = 5.0;
    private const double ChaffBucketSeconds = 2.0;

    internal void BeginRun()
    {
        lock (_sync)
        {
            _parts.Clear(); _expendables.Clear(); _hulls.Clear(); _ships.Clear();
            _lastFire.Clear(); _cursors.Clear(); _seenExpendables.Clear(); _seenEvents.Clear();
            _startedAtMs = Environment.TickCount64;
        }
    }

    // ── harness-side exact events ──────────────────────────────────────────

    /// <summary>A real BUY_LOADOUT launch ack: hull bought + launched and every
    /// granted part marked equipped.</summary>
    internal void RecordPurchase(short side, short hullId, IReadOnlyList<ExpandedPartData> parts)
    {
        lock (_sync)
        {
            var h = _hulls.TryGetValue(hullId, out var s) ? s : new HullStat(0, 0, 0);
            _hulls[hullId] = h with { Bought = h.Bought + 1, Launched = h.Launched + 1 };
            foreach (var p in parts)
            {
                var stat = _parts.TryGetValue(p.PartId, out var ps) ? ps : new PartStat(0, 0, 0, 0);
                _parts[p.PartId] = stat with { Equipped = stat.Equipped + 1 };
            }
        }
    }

    /// <summary>Rising edge of the bot fire law's AllWeapons trigger: every
    /// mounted weapon part fires together (native allWeapons, shipIGC.cpp:2600).</summary>
    internal void RecordGunTrigger(short shipId, IReadOnlyList<short> weaponPartIds)
    {
        if (weaponPartIds.Count == 0) return;
        lock (_sync)
        {
            foreach (var id in weaponPartIds)
            {
                var stat = _parts.TryGetValue(id, out var ps) ? ps : new PartStat(0, 0, 0, 0);
                _parts[id] = stat with { Fired = stat.Fired + 1 };
            }
            _lastFire[shipId] = (Environment.TickCount64, weaponPartIds[0], 0, "gun");
        }
    }

    /// <summary>A real missile launch (SendFireMissile): the launcher part and
    /// its expendable missile type both fire.</summary>
    internal void RecordMissileLaunch(short shipId, short launcherPartId)
    {
        var core = Catalog.RepresentativeCore();
        short missileType = core.Launchers.TryGetValue(launcherPartId, out var l) ? l.ExpendabletypeId : (short)-1;
        lock (_sync)
        {
            var stat = _parts.TryGetValue(launcherPartId, out var ps) ? ps : new PartStat(0, 0, 0, 0);
            _parts[launcherPartId] = stat with { Fired = stat.Fired + 1 };
            BumpExpendable(missileType);
            _lastFire[shipId] = (Environment.TickCount64, launcherPartId, missileType, "missile");
        }
    }

    /// <summary>A real SendFireExpendable drop (probe / mine / chaff): the
    /// dispenser part and its expendable type both fire.</summary>
    internal void RecordDispenserDrop(short shipId, short launcherPartId)
    {
        var core = Catalog.RepresentativeCore();
        short type = core.Launchers.TryGetValue(launcherPartId, out var l) ? l.ExpendabletypeId : (short)-1;
        string kind = core.ProbeTypes.ContainsKey(type) ? "probe" : core.MineTypes.ContainsKey(type) ? "mine"
            : core.ChaffTypes.ContainsKey(type) ? "chaff" : "dispenser";
        lock (_sync)
        {
            var stat = _parts.TryGetValue(launcherPartId, out var ps) ? ps : new PartStat(0, 0, 0, 0);
            _parts[launcherPartId] = stat with { Fired = stat.Fired + 1 };
            BumpExpendable(type);
            _lastFire[shipId] = (Environment.TickCount64, launcherPartId, type, kind);
        }
    }

    private void BumpExpendable(short typeId)
    {
        var stat = _expendables.TryGetValue(typeId, out var es) ? es : new ExpendableStat(0, 0, 0);
        _expendables[typeId] = stat with { Fired = stat.Fired + 1 };
    }

    // ── per-bot event sweep (≈1 Hz, called from the manager loop) ──────────

    internal void SweepBot(ConnectionSession cs)
    {
        var core = Catalog.RepresentativeCore();
        lock (cs.Gate)
        {
            var w = cs.World;
            double nowMs = Environment.TickCount64;
            string? stage = w.Mission is { } mission ? mission.Stage.ToString() : null;
            // Ship registry: hull + side per ship id (kills attribute hulls).
            lock (_sync)
                foreach (var ship in w.Ships.Values)
                    if (!ship.IsMine && ship.HullId > 0)
                        _ships[ship.ShipId] = new ShipObs(ship.HullId, ship.SideId);
            // Kill log (KILL_SHIP with death credit → hull kill).
            long killSeq = w.Events.KillSequence;
            long cursor;
            lock (_sync) cursor = _cursors.TryGetValue(cs, out var cur) ? cur.KillSeq : killSeq;
            foreach (var k in w.Events.KillsAfter(cursor))
            {
                if (!k.BDeathCredit) continue;
                lock (_sync)
                {
                    if (!_seenEvents.Add(EventHash(k.ShipId, 0, 0, 0, 0, (long)(nowMs / 1000)))) continue;
                    if (_ships.TryGetValue(k.ShipId, out var victim))
                    {
                        var h = _hulls.TryGetValue(victim.HullId, out var hs) ? hs : new HullStat(0, 0, 0);
                        _hulls[victim.HullId] = h with { Killed = h.Killed + 1 };
                    }
                }
            }
            lock (_sync)
            {
                long chaffMs = _cursors.TryGetValue(cs, out var c2) ? c2.ChaffMs : 0;
                object? lastStage = _cursors.TryGetValue(cs, out var c3) ? c3.LastStage : null;
                // Match end: a stage that WAS 'Started' and no longer is (game over
                // / next-match rotation). One bounded end-of-match table per match.
                if (Equals(lastStage, "Started") && !Equals(stage, "Started")) _endOfMatchPending = true;
                _cursors[cs] = (killSeq, chaffMs, stage);
            }
            // Hull hits: shooter-attributed (P539b SourceId), deduped across the
            // observers that share the victim's sector.
            List<(short AttackerId, float Damage, long Ms)> damages;
            try { damages = w.TakeNewVictimDamages(); } catch { damages = []; }
            lock (_sync)
            {
                // Damage: the victim's own unicast — no dedupe possible or needed.
                foreach (var (attacker, damage, _) in damages)
                {
                    if (attacker < 0 || damage <= 0) continue;
                    if (_lastFire.TryGetValue(attacker, out var lf) && nowMs - lf.AtMs <= HitAttributionWindowSeconds * 1000)
                        CreditDamage(lf.PartId, lf.TypeId, damage);
                }
            }
            // Missiles / probes / mines in view: first sighting = a real launch
            // (the wire carries object ids; the global seen-set dedupes across
            // every bot that shares the sector).
            lock (_sync)
            {
                foreach (var m in w.Missiles.Values)
                    if (_seenExpendables.Add(m.MissileId)) CreditObservedLaunch(core, m.LauncherId, m.TypeId, "missile");
                foreach (var p in w.Probes.Values)
                    if (_seenExpendables.Add(p.ProbeId)) CreditObservedLaunch(core, p.LauncherShipId, p.ProbetypeId, "probe");
                foreach (var m in w.Mines.Values)
                    if (_seenExpendables.Add(m.MineId)) CreditObservedLaunch(core, m.LauncherShipId, m.MinetypeId, "mine");
                // Chaff: no object id on the wire, so dedupe by position bucket.
                foreach (var ch in w.Chaff)
                {
                    long bucket = ch.ReceivedLocalMs / (long)(ChaffBucketSeconds * 1000);
                    if (!_seenEvents.Add(EventHash(-2, ch.Etid, Q(ch.P0.X), Q(ch.P0.Y), Q(ch.P0.Z), bucket))) continue;
                    var stat = _expendables.TryGetValue(ch.Etid, out var es) ? es : new ExpendableStat(0, 0, 0);
                    _expendables[ch.Etid] = stat with { Fired = stat.Fired + 1 };
                }
            }
            // Bound the dedupe sets (a 200v200 march sees ~10⁵ ids/hour).
            lock (_sync)
            {
                if (_seenEvents.Count > 400_000) _seenEvents.Clear();
                if (_seenExpendables.Count > 400_000) _seenExpendables.Clear();
            }
        }
    }

    /// <summary>Hull hits the bot's combat tick already drained for its own threat
    /// tracking (BotManager.SampleCombatStats). Coverage must never drain the world's
    /// hit buffer itself: that would take hits away from RecentAttackers.</summary>
    internal void RecordHullHits(List<HullHitEntity> hits)
    {
        double nowMs = Environment.TickCount64;
        lock (_sync)
            foreach (var hit in hits)
            {
                if (hit.SourceId < 0) continue;
                if (!_seenEvents.Add(EventHash(hit.SourceId, hit.TargetId,
                    Q(hit.Position.X), Q(hit.Position.Y), Q(hit.Position.Z), (long)(nowMs / 1000)))) continue;
                if (_lastFire.TryGetValue(hit.SourceId, out var lf) && nowMs - lf.AtMs <= HitAttributionWindowSeconds * 1000)
                    CreditHit(lf.PartId, lf.TypeId);
            }
    }

    private void CreditObservedLaunch(StaticCore core, short launcherShipId, short expendableTypeId, string kind)
    {
        // The part attribution for observed launches comes from the harness's
        // own SendFireMissile/SendFireExpendable records (this sweep path sees
        // the expendable OBJECT, not the launcher part); shooters that are not
        // harness bots credit only the expendable type row.
        BumpExpendable(expendableTypeId);
    }

    private void CreditHit(short partId, short typeId)
    {
        if (partId > 0)
        {
            var stat = _parts.TryGetValue(partId, out var ps) ? ps : new PartStat(0, 0, 0, 0);
            _parts[partId] = stat with { Hits = stat.Hits + 1 };
        }
        if (typeId > 0 && _expendables.TryGetValue(typeId, out var es))
            _expendables[typeId] = es with { Hits = es.Hits + 1 };
    }

    /// <summary>Test seam for the sweep's hit/damage attribution (the sweep
    /// path itself is exercised by the live loopback showcase test).</summary>
    internal void CreditHitForTests(short partId, short typeId) { lock (_sync) CreditHit(partId, typeId); }
    internal void CreditDamageForTests(short partId, short typeId, double damage) { lock (_sync) CreditDamage(partId, typeId, damage); }

    private void CreditDamage(short partId, short typeId, double damage)
    {
        if (partId > 0)
        {
            var stat = _parts.TryGetValue(partId, out var ps) ? ps : new PartStat(0, 0, 0, 0);
            _parts[partId] = stat with { Damage = stat.Damage + damage };
        }
        if (typeId > 0 && _expendables.TryGetValue(typeId, out var es))
            _expendables[typeId] = es with { Damage = es.Damage + damage };
    }

    private static int Q(float v) => (int)MathF.Round(v / 25f);

    private static long EventHash(int a, int b, int x, int y, int z, long bucket)
    {
        ulong h = 1469598103934665603UL;
        foreach (int v in new[] { a, b, x, y, z })
        {
            h ^= (uint)v; h *= 1099511628211UL;
        }
        h ^= (ulong)bucket; h *= 1099511628211UL;
        return unchecked((long)h);
    }

    // ── reporting ──────────────────────────────────────────────────────────

    /// <summary>True exactly once per observed match end (a bot world's mission
    /// stage left 'Started'); the caller prints the end-of-match table.</summary>
    internal bool ConsumeEndOfMatchFlag()
    {
        lock (_sync)
        {
            bool pending = _endOfMatchPending;
            _endOfMatchPending = false;
            return pending;
        }
    }

    internal bool HasData
    {
        get { lock (_sync) return _parts.Any(p => p.Value.Equipped > 0); }
    }

    internal double ElapsedSeconds
    {
        get { lock (_sync) return double.IsNaN(_startedAtMs) ? 0 : (Environment.TickCount64 - _startedAtMs) / 1000.0; }
    }

    /// <summary>The bounded once-per-minute `[gallery]` summary: two lines at
    /// most, whatever the fleet size.</summary>
    internal string GallerySummary()
    {
        lock (_sync)
        {
            int equipped = _parts.Count(p => p.Value.Equipped > 0);
            int fired = _parts.Count(p => p.Value.Fired > 0);
            long partFires = _parts.Sum(p => p.Value.Fired);
            long hits = _parts.Sum(p => p.Value.Hits);
            double damage = _parts.Sum(p => p.Value.Damage);
            long kills = _hulls.Sum(h => h.Value.Killed);
            long expFires = _expendables.Sum(e => e.Value.Fired);
            return $"t={ElapsedSeconds:F0}s weapon-parts equipped={equipped} fired={fired} triggerEvents={partFires} hits={hits} damage={damage:F0}; "
                + $"expendable-fires={expFires} kills={kills} hulls bought={_hulls.Count(h => h.Value.Bought > 0)}";
        }
    }

    /// <summary>The end-of-match table: every part/expendable/hull row plus the
    /// never-fired equipped items with the reason when known.</summary>
    internal IReadOnlyList<string> Table()
    {
        var core = Catalog.RepresentativeCore();
        var lines = new List<string>();
        lock (_sync)
        {
            lines.Add($"P796 coverage table (t={ElapsedSeconds:F0}s): parts={_parts.Count} expendables={_expendables.Count} hulls={_hulls.Count}");
            lines.Add("part | kind | equipped | fired | hits | damage");
            foreach (var (id, s) in _parts.OrderBy(kv => kv.Key))
                lines.Add($"{id} | {PartName(core, id)} | {Kind(core, id)} | {s.Equipped} | {s.Fired} | {s.Hits} | {s.Damage:F0}");
            lines.Add("expendable-type | kind | fired | hits | damage");
            foreach (var (id, s) in _expendables.OrderBy(kv => kv.Key))
                lines.Add($"{id} | {ExpendableName(core, id)} | {ExpendableKind(core, id)} | {s.Fired} | {s.Hits} | {s.Damage:F0}");
            lines.Add("hull | bought | launched | killed");
            foreach (var (id, s) in _hulls.OrderBy(kv => kv.Key))
                lines.Add($"{id} | {HullName(core, id)} | {s.Bought} | {s.Launched} | {s.Killed}");
            var never = NeverFiredLocked();
            if (never.Count > 0)
            {
                lines.Add("never fired (equipped):");
                lines.AddRange(never);
            }
        }
        return lines;
    }

    /// <summary>Every equipped item with Fired == 0 and the reason when the
    /// item's nature explains it. Called with _sync held by <see cref="Table"/>.</summary>
    private List<string> NeverFiredLocked()
    {
        var core = Catalog.RepresentativeCore();
        var result = new List<string>();
        foreach (var (id, s) in _parts.OrderBy(kv => kv.Key))
        {
            if (s.Equipped == 0 || s.Fired > 0) continue;
            string reason;
            switch (Kind(core, id))
            {
                case "shield" or "afterburner" or "cloak":
                    reason = "passive equipment — never fires"; break;
                case "probe-launcher":
                    reason = "scanner/rescue probe law never qualified (no sweep/pod window)"; break;
                case "mine-launcher":
                    reason = "mine proximity law (different-side combat actor within 2500u, shipIGC.cpp:2651-2665) never qualified"; break;
                case "chaff-launcher":
                    reason = "chaff law (inbound missile lock, MissileTrack) never qualified"; break;
                case "missile-launcher":
                    reason = "missile gate (lock ≥ 1.0 and target evasion below max, shipIGC.cpp:2584-2596) never satisfied"; break;
                default:
                    reason = "fire gate (aim ≤ open-fire angle within lifespan, shipIGC.cpp:2600) never satisfied in observed fights"; break;
            }
            result.Add($"{id} | {PartName(core, id)} | {Kind(core, id)} | equipped={s.Equipped} | {reason}");
        }
        foreach (var (id, s) in _expendables.OrderBy(kv => kv.Key))
            if (s.Fired == 0)
                result.Add($"expendable {id} | {ExpendableName(core, id)} | {ExpendableKind(core, id)} | observed but never fired");
        return result;
    }

    internal object Json()
    {
        var core = Catalog.RepresentativeCore();
        lock (_sync)
        {
            return new
            {
                elapsedSeconds = ElapsedSeconds,
                parts = _parts.OrderBy(kv => kv.Key).Select(kv => new
                {
                    partId = kv.Key, name = PartName(core, kv.Key), kind = Kind(core, kv.Key),
                    equipped = kv.Value.Equipped, fired = kv.Value.Fired, hits = kv.Value.Hits,
                    damage = Math.Round(kv.Value.Damage, 1),
                }),
                expendables = _expendables.OrderBy(kv => kv.Key).Select(kv => new
                {
                    typeId = kv.Key, name = ExpendableName(core, kv.Key), kind = ExpendableKind(core, kv.Key),
                    fired = kv.Value.Fired, hits = kv.Value.Hits, damage = Math.Round(kv.Value.Damage, 1),
                }),
                hulls = _hulls.OrderBy(kv => kv.Key).Select(kv => new
                {
                    hullId = kv.Key, name = HullName(core, kv.Key),
                    bought = kv.Value.Bought, launched = kv.Value.Launched, killed = kv.Value.Killed,
                }),
                neverFired = NeverFiredLocked(),
            };
        }
    }

    internal static string PartName(StaticCore core, short id)
    {
        try { return TechLoadoutSelector.Part(core, id).Name; }
        catch { return id.ToString(); }
    }

    internal static string Kind(StaticCore core, short id)
    {
        try
        {
            var p = TechLoadoutSelector.Part(core, id);
            return p.Type switch
            {
                DataPartTypeIGC.Weapon => "gun",
                DataPartTypeIGC.Shield => "shield",
                DataPartTypeIGC.Afterburner => "afterburner",
                DataPartTypeIGC.Cloak => "cloak",
                DataPartTypeIGC.MissileLauncher => "missile-launcher",
                DataPartTypeIGC.ProbeLauncher => "probe-launcher",
                DataPartTypeIGC.MineLauncher => "mine-launcher",
                DataPartTypeIGC.ChaffLauncher => "chaff-launcher",
                _ => p.EquipmentType.ToString().ToLowerInvariant(),
            };
        }
        catch { return "unknown"; }
    }

    private static string ExpendableName(StaticCore core, short id)
        => core.MissileTypes.TryGetValue(id, out var m) ? m.ModelName
        : core.MineTypes.TryGetValue(id, out var mn) ? mn.ModelName
        : core.ProbeTypes.TryGetValue(id, out var p) ? p.ModelName
        : core.ChaffTypes.TryGetValue(id, out var c) ? c.ModelName : id.ToString();

    private static string ExpendableKind(StaticCore core, short id)
        => core.MissileTypes.ContainsKey(id) ? "missile"
        : core.MineTypes.ContainsKey(id) ? "mine"
        : core.ProbeTypes.ContainsKey(id) ? "probe"
        : core.ChaffTypes.ContainsKey(id) ? "chaff" : "unknown";

    private static string HullName(StaticCore core, short id)
        => core.HullTypes.TryGetValue(id, out var h) ? h.Name : id.ToString();
}
