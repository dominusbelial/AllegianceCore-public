using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P426 — real static-core catalog + name resolution. This is the same real
/// <c>PCore001.igc</c> static core <c>tools/vizfight</c> already decodes
/// (<see cref="IgcReader"/>/<see cref="StaticCore"/>), resolved by the same
/// substring rules the caller discovers against. It is deliberately standalone
/// (no reference back into vizfight) so the MCP server owns its own copy —
/// mirroring how vizfight itself copies these lookups rather than sharing.
/// </summary>
internal static class Catalog
{
    /// <summary>The real artwork corpus root (same env knob + default as vizfight
    /// and the live tests). Loads the SAME <c>PCore001.igc</c> the running server
    /// uses, so a name that resolves here is a hull/part id the server's own
    /// static core should also carry.</summary>
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static readonly Lazy<StaticCore> Core = new(() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"))));

    public static StaticCore RepresentativeCore() => Core.Value;

    // ── Hull resolution (mirror of vizfight's HullNameMap + substring match) ──

    /// <summary>A resolved hull: its real id + display name.</summary>
    public readonly record struct Hull(short Id, string Name);

    /// <summary>
    /// Resolve a hull by exact name or case-insensitive substring across the
    /// real corpus. Policies that match how a human names a hull:
    ///  • exact-name hits win over substring-only hits (so "Adv Miner" selects
    ///    the Adv Miner family even though "Adv Miner [SR]" also contains it);
    ///  • multiple records that share one NAME string (same-named variants —
    ///    the corpus has many, e.g. ten "Adv Miner" hull ids) are one hull: the
    ///    lowest-HullId representative is chosen;
    ///  • zero hits or several DISTINCT names throw a real, identifying error.
    /// Never a silent no-op.
    /// </summary>
    public static Hull ResolveHull(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("spawn_bot: `hull` must be a non-empty hull name or substring");

        var core = RepresentativeCore();
        var all = core.HullTypes.Values
            .Where(h => h.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
                     || h.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (all.Count == 0)
            throw new ArgumentException($"spawn_bot: no hull matches '{query}' (known hulls: {HullSummary(core)})");
        var exact = all.Where(h => h.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = exact.Count > 0 ? exact : all;
        var distinct = pool.Select(h => h.Name).Distinct().ToList();
        if (distinct.Count > 1)
            throw new ArgumentException(
                $"spawn_bot: hull '{query}' is ambiguous; it matches several distinct hulls: "
                + string.Join(", ", distinct.Select(n => $"'{n}'"))
                + ". Pass a more specific name.");
        var chosen = pool.OrderBy(h => h.HullId).First();
        return new Hull(chosen.HullId, chosen.Name);
    }

    private static string HullSummary(StaticCore core)
    {
        var names = core.HullTypes.Values.OrderBy(h => h.HullId).Select(h => h.Name).Distinct().ToList();
        if (names.Count > 40) names = names.Take(40).ToList();
        return string.Join(", ", names) + (names.Count >= 40 ? ", …" : "");
    }

    /// <summary>P430 — the real fighter hull spawn_scenario's default: the same
    /// fightable hull <see cref="DefaultParts"/>/<c>vizfight</c> equip (the
    /// first hull with energy+ammo and a fixed hardpoint, i.e. what a real
    /// fighter is). Lets <c>spawn_scenario</c> default its hull without a
    /// caller having to name one.</summary>
    public static Hull DefaultFighterHull()
    {
        var (hull, _) = Fight.Value;
        return new Hull(hull.HullId, hull.Name);
    }

    // ── Sector (cluster) resolution ──

    /// <summary>A resolved sector (cluster): its real id + display name.</summary>
    public readonly record struct Sector(short Id, string Name);

    /// <summary>
    /// Resolve a sector by its real numeric id (a decimal-string) or by exact /
    /// case-insensitive substring of its NAME, across the LIVE server map's
    /// clusters. Unlike hulls/parts the map is NOT part of the static
    /// PCore001.igc core — it is server-generated per mission and delivered to
    /// every peer as EXPORT OT_cluster / CLUSTERINFO — so the corpus is the
    /// connected session's real <paramref name="clusters"/> dictionary (the
    /// probe's, which is populated by the join-burst exports). The resolution
    /// policy is otherwise identical to <see cref="ResolveHull"/>: a numeric id
    /// hits its cluster directly, exact-name hits win over substring-only,
    /// same-named variants collapse to the lowest-id representative, and zero
    /// matches / several distinct names throw a real, identifying error. Never
    /// a silent no-op.
    /// </summary>
    public static Sector ResolveSector(string query, IReadOnlyDictionary<short, DataClusterIGC> clusters)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("spawn_bot: `target_sector` must be a non-empty sector id or name");
        if (clusters is null || clusters.Count == 0)
            throw new ArgumentException("spawn_bot: no sector map available yet (the server hasn't sent its clusters) — cannot resolve `target_sector`");

        // Numeric id form: a direct ClusterId hit wins outright.
        if (short.TryParse(query, out var id) && clusters.TryGetValue(id, out var byId))
            return new Sector(id, byId.Name);

        var all = clusters.Values
            .Where(c => c.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
                     || c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (all.Count == 0)
            throw new ArgumentException($"spawn_bot: no sector matches '{query}' (known sectors: {SectorSummary(clusters)})");
        var exact = all.Where(c => c.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = exact.Count > 0 ? exact : all;
        var distinct = pool.Select(c => c.Name).Distinct().ToList();
        if (distinct.Count > 1)
            throw new ArgumentException(
                $"spawn_bot: sector '{query}' is ambiguous; it matches several distinct sectors: "
                + string.Join(", ", distinct.Select(n => $"'{n}'"))
                + ". Pass a more specific name or the numeric sector id.");
        var chosen = pool.OrderBy(c => c.ClusterId).First();
        return new Sector(chosen.ClusterId, chosen.Name);
    }

    private static string SectorSummary(IReadOnlyDictionary<short, DataClusterIGC> clusters)
    {
        var names = clusters.Values.OrderBy(c => c.ClusterId).Select(c => c.Name).Distinct().ToList();
        if (names.Count > 40) names = names.Take(40).ToList();
        return string.Join(", ", names) + (names.Count >= 40 ? ", …" : "");
    }

    // ── Part resolution ──

    /// <summary>A resolved part: real id + display name + equipment category.</summary>
    public readonly record struct Part(short Id, string Name, string Category);

    public static IReadOnlyList<Part> AllParts()
    {
        var core = RepresentativeCore();
        return core.PartTypes.Values
            .OrderBy(p => p.PartId)
            .Select(p => new Part(p.PartId, p.Name, p.EquipmentType.ToString()))
            .ToList();
    }

    /// <summary>Resolve one part name (exact or case-insensitive substring) with
    /// the same policies as <see cref="ResolveHull"/>: exact wins, same-named
    /// variants collapse to the lowest-id representative, zero matches and
    /// multiple distinct names both throw real, identifying errors.</summary>
    private static Part ResolvePart(string query, StaticCore core)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("spawn_bot: a part name must be non-empty");
        var all = core.PartTypes.Values
            .Where(p => p.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (all.Count == 0)
            throw new ArgumentException($"spawn_bot: no part matches '{query}'");
        var exact = all.Where(p => p.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = exact.Count > 0 ? exact : all;
        var distinct = pool.Select(p => p.Name).Distinct().ToList();
        if (distinct.Count > 1)
            throw new ArgumentException(
                $"spawn_bot: part '{query}' is ambiguous; it matches several distinct parts: "
                + string.Join(", ", distinct.Select(n => $"'{n}'"))
                + ". Pass a more specific name.");
        var chosen = pool.OrderBy(p => p.PartId).First();
        return new Part(chosen.PartId, chosen.Name, chosen.EquipmentType.ToString());
    }

    /// <summary>
    /// P432 — resolve one weapon part name/substring (exact-over-substring, same
    /// policy as <see cref="ResolveHull"/>) and REQUIRE it to be a real
    /// <see cref="EquipmentType.Weapon"/>. A query that resolves to a
    /// non-weapon part (shield, afterburner, pack, …) is a real, specific error —
    /// the hull/weapon matrix only accepts real guns. Returns the resolved part
    /// (id, name, "Weapon" category).
    /// </summary>
    public static Part ResolveWeapon(string query)
    {
        var core = RepresentativeCore();
        var p = ResolvePart(query, core);
        if (core.PartTypes.TryGetValue(p.Id, out var dp) && dp.EquipmentType == EquipmentType.Weapon)
            return p;
        throw new ArgumentException(
            $"'{query}' resolved to part '{p.Name}' ({p.Category}) which is not a weapon — "
            + "run_hull_matrix `weapons` must name a real weapon part");
    }

    /// <summary>
    /// P432 — the real weapon part <see cref="DefaultParts"/> equips when the
    /// caller omits <c>weapons</c>: the same cheapest dispersion-0 real gun
    /// spawn_bot/spawn_scenario/vizfight default to. Lets the matrix default its
    /// weapon list to the standing default loadout's gun without a caller naming
    /// one (and still hits a real, specific compatibility error when that default
    /// gun does not fit a caller's hull — the honest behavior).
    /// </summary>
    public static Part DefaultWeaponPart()
    {
        var core = RepresentativeCore();
        var wid = OuterWeaponPartId()
            ?? throw new InvalidOperationException("no default weapon in the representative core");
        if (core.PartTypes.TryGetValue(wid, out var dp))
            return new Part(wid, dp.Name, EquipmentType.Weapon.ToString());
        throw new InvalidOperationException($"default weapon part id {wid} not found in the representative core");
    }

    /// <summary>
    /// P539 — the DOGFIGHT default weapon: when a combat bot's <c>weapons</c>
    /// is omitted, this is the sensible anti-ship gun the harness arms it
    /// with, instead of the dispersion-0 test gun (<see cref="DefaultWeaponPart"/>,
    /// "PW A-B Omega 2" — a determinism pick for tests, not a dogfight gun).
    /// Selection rule (cited in P539 report): among REAL weapon parts that
    /// (a) actually MOUNT on hull <paramref name="hullId"/> via the real
    /// <see cref="DataHullTypeIGC.CanMountPart"/> capability test (the server's
    /// ApplyLoadout gate, <c>shipIGC.h:1030</c>), (b) are energy-affordable on
    /// that hull (<c>EnergyPerShot ≤ hull.MaxEnergy</c>), and (c) fire a real
    /// damaging projectile (<c>ProjectileType.Power &gt; 0</c>), pick the one
    /// with the HIGHEST projectile <c>Power</c> (the strongest anti-ship round),
    /// tie-broken by longer reach (<c>Speed × Lifespan</c>) then lowest
    /// <c>PartId</c> (determinism). This yields a real, fitting, damaging gun
    /// for the hull — e.g. on the default fighter the high-power dispersed
    /// anti-ship gun rather than the dispersion-0 test round.
    /// </summary>
    public static Part DogfightWeaponPart(short hullId)
    {
        var core = RepresentativeCore();
        if (!core.HullTypes.TryGetValue(hullId, out var hull))
            throw new ArgumentException($"dogfight default weapon: hull id {hullId} not in the representative core");

        DataPartTypeIGC? best = null;
        float bestPower = -1f, bestReach = -1f;
        foreach (var p in core.PartTypes.Values)
        {
            if (p.EquipmentType != EquipmentType.Weapon) continue;
            if (!hull.CanMountPart(in p)) continue;
            if (p.Type is not DataPartTypeIGC.Weapon w) continue;
            if (w.EnergyPerShot > hull.MaxEnergy) continue;
            if (!core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt) || pt.Power <= 0f) continue;
            float reach = pt.Speed * pt.Lifespan;
            bool better = pt.Power > bestPower
                          || (Math.Abs(pt.Power - bestPower) < 1e-6f && (reach > bestReach || (Math.Abs(reach - bestReach) < 1e-6f && p.PartId < (best?.PartId ?? short.MaxValue))));
            if (better) { best = p; bestPower = pt.Power; bestReach = reach; }
        }
        if (best is null)
            throw new InvalidOperationException(
                $"no dogfight weapon mounts on hull id {hullId} ('{hull.Name}') in the representative core");
        var winner = best.Value;
        return new Part(winner.PartId, winner.Name, EquipmentType.Weapon.ToString());
    }

    /// <summary>
    /// P539 — the full real dogfight combat loadout for a hull: the
    /// <see cref="DogfightWeaponPart"/> anti-ship gun PLUS the standing
    /// mountable afterburner/cloak/shield defaults (the P483 policy — first
    /// real part of each type that mounts on THIS hull, honestly omitted when
    /// none does). This is the default combat loadout spawn_bot / spawn_scenario
    /// arm when the caller omits <c>weapons</c> — the live dogfight default,
    /// NOT the dispersion-0 test gun. Never fabricated; exactly what the
    /// server's ApplyLoadout can mount.
    /// </summary>
    public static List<ExpandedPartData> DogfightParts(short hullId, string hullName)
    {
        var core = RepresentativeCore();
        var weapon = DogfightWeaponPart(hullId);
        var parts = new List<ExpandedPartData> { new(weapon.Id, 1, 0, 255) };
        if (MountableDefaultPart(core, EquipmentType.Afterburner, hullId) is { } ab) parts.Add(new ExpandedPartData(ab, 1, 0, 255));
        if (MountableDefaultPart(core, EquipmentType.Cloak, hullId) is { } ck) parts.Add(new ExpandedPartData(ck, 1, 0, 255));
        if (MountableDefaultPart(core, EquipmentType.Shield, hullId) is { } sh) parts.Add(new ExpandedPartData(sh, 1, 0, 255));
        _ = hullName; // reserved: kept in the signature for symmetric logging call sites
        return parts;
    }

    /// <summary>
    /// P539 — the first real WEAPON part id in a loadout (or <c>null</c> when
    /// the loadout carries none). The combat engine reads this to know the
    /// projectile ballistics (speed / range / absolute) of the gun it leads
    /// with — the first mounted weapon, exactly native's
    /// <c>m_mountedWeapons[0]</c> lead choice (<c>shipIGC.cpp:2353,2466</c>).
    /// </summary>
    public static short? FirstWeaponPartId(IEnumerable<ExpandedPartData> parts)
    {
        var core = RepresentativeCore();
        foreach (var e in parts)
            if (core.PartTypes.TryGetValue(e.PartId, out var p) && p.EquipmentType == EquipmentType.Weapon)
                return e.PartId;
        return null;
    }

    /// <summary>P539 — whether a hull id is a LIFEPOD hull (carries the real
    /// <c>c_habmLifepod</c> capability, <c>DataHullTypeIGC.HabmLifepod</c> = 0x04)
    /// in the representative core. The MCP combat loop uses this on its OWN
    /// ship's current hull to detect that the server ejected it into a pod.</summary>
    public static bool IsLifepodHull(short hullId)
    {
        var core = RepresentativeCore();
        return core.HullTypes.TryGetValue(hullId, out var h)
            && h.HasCapability(DataHullTypeIGC.HabmLifepod);
    }

    // ── P539b — native preferred loadout + per-side tech level ─────────────
    //
    // The BOT'S DEFAULT combat loadout is the hull's own NATIVE PREFERRED
    // loadout (DataHullTypeIGC.PreferredPartsTypes, native preferredPartsTypes,
    // src/Igc/igc.h:1801, decoded into the port at DataHullTypeIGC.cs:50), NOT
    // "highest power" nor "lowest PartId that mounts". That is what fixed P539's
    // false "shield-regen equilibrium": P539's MountableDefaultPart shield pick
    // grabbed `Lrg Shield 2` (part 45, 3750 strength, 35/s regen), which mounts
    // but is effectively indestructible. P555 corrected the hull's shield socket
    // to native alignment (pmEquipment at offset 510, src/Igc/igc.h:1809), so the
    // hull's NATIVE preferred shield `Sm Shield 1` (part 21, 150/2-per-s) now
    // MOUNTS on the Adv Scout and the bot flies its real preferred loadout
    // (Counter 1 chaff, EWS Probe 1, PW Gat Gun, MRM Seeker, Sm Shield 1) — see
    // docs/reports/P555_HULL_MASK_DECODE.md. Native still never guarantees a
    // preferred part mounts: its loadout builders gate each preferred part by
    // CanMount and skip unmountable ones (FedSrv.CPP:11583,11590 UpgradeShip),
    // and this builder honours that same gate.

    /// <summary>
    /// P539b — resolve ONE preferred part of a hull after <paramref name="steps"/>
    /// upgrades along its native SUCCESSOR chain (<c>DataPartTypeIGC.SuccessorPartId</c>,
    /// native <c>successorPartID</c>, <c>src/Igc/igc.h:1827</c>), returning the LAST
    /// successor that still MOUNTS on the hull (native <c>CanMount</c>,
    /// <c>hullTypeIGC.h:199-213</c>, mirrored by <see cref="DataHullTypeIGC.CanMountPart"/>).
    /// Level 0 returns the part itself (when it mounts). Stops at the end of a
    /// chain (no successor) or after <paramref name="steps"/> upgrades. A part
    /// that never mounts on the hull at any reached step resolves to <c>null</c>
    /// (honestly omitted — native skips unmountable preferred parts). Launchers
    /// (not in <c>core.PartTypes</c>) have no successor chain and resolve to
    /// <c>null</c> here; <see cref="NativePreferredLoadout"/> handles them via
    /// <see cref="AppendLauncher"/>. Pure: takes the core explicitly so it is
    /// unit-provable against the real corpus.
    /// </summary>
    public static short? ResolvePreferredAtLevel(StaticCore core, short partId, short hullId, int steps)
    {
        if (!core.HullTypes.TryGetValue(hullId, out var hull))
            throw new ArgumentException($"resolve preferred part: hull id {hullId} not in the representative core");
        short? lastMounting = null;
        var cur = partId;
        for (int step = 0; step <= steps; step++)
        {
            if (step > 0)
            {
                // Advance along the successor chain (parts only; launchers have none).
                if (!core.PartTypes.TryGetValue(cur, out var cp) || cp.SuccessorPartId < 0) break;
                cur = cp.SuccessorPartId;
            }
            if (core.PartTypes.TryGetValue(cur, out var p) && hull.CanMountPart(in p))
                lastMounting = cur;
        }
        return lastMounting;
    }

    /// <summary>
    /// P556 — the hull a scenario side actually FLIES at tech level
    /// <paramref name="techLevel"/>: the scenario hull advanced
    /// <c>floor(techLevel/2)</c> steps along its native
    /// <see cref="DataHullTypeIGC.SuccessorHullId"/> chain, stopping at the
    /// chain end / <c>NA</c>. This is the native "advance to the successor hull"
    /// progression: <c>IstationIGC::GetSuccessor</c> walks a hull up its
    /// successor chain (<c>src/Igc/stationIGC.h:420</c>,
    /// <c>((IhullTypeIGC*)pNext)->GetSuccessorHullType()</c>), and the server's
    /// hull upgrade on a development completion swaps the obsolete hull for that
    /// successor (<c>src/Igc/shipIGC.h:1004</c>,
    /// <c>pht = (IhullTypeIGC*)(m_station->GetSuccessor(pht))</c>; the chain
    /// pointer itself resolves from <c>successorHullID</c> at
    /// <c>src/Igc/hullTypeIGC.cpp:217-219</c>). Native walks the WHOLE chain to
    /// the farthest buyable hull; this showcase uses a parametrized
    /// <c>floor(N/2)</c>-step walk so successive tech levels graduate the hull.
    /// Each step follows <c>SuccessorHullId</c> and stops early at a non-existent
    /// successor (chain end), so tech beyond the chain flies the final hull.
    /// </summary>
    public static DataHullTypeIGC EffectiveHullAtLevel(short hullId, int techLevel)
    {
        var core = RepresentativeCore();
        if (!core.HullTypes.TryGetValue(hullId, out var hull))
            throw new ArgumentException($"tech level: hull id {hullId} not in the representative core");
        int steps = techLevel / 2;                       // floor(N/2) hull upgrades
        for (int i = 0; i < steps; i++)
        {
            if (hull.SuccessorHullId < 0) break;         // NA = no further successor
            if (!core.HullTypes.TryGetValue(hull.SuccessorHullId, out var next)) break;
            hull = next;
        }
        return hull;
    }

    /// <summary>
    /// P556 — the fully-resolved TECH-N loadout a side flies: the effective hull
    /// (see <see cref="EffectiveHullAtLevel"/>) plus the parts resolved from
    /// THAT hull's native <see cref="DataHullTypeIGC.PreferredPartsTypes"/>,
    /// each advanced <paramref name="techLevel"/> steps along its successor
    /// chain (see <see cref="ResolvePreferredAtLevel"/>). Carries the effective
    /// hull id / name / hit-points alongside the parts so a showcase operator can
    /// report hull HP, shield and gun without a second (potentially divergent)
    /// core read.
    /// </summary>
    public readonly record struct TechLoadout(
        short HullId,
        string HullName,
        float HitPoints,
        IReadOnlyList<ExpandedPartData> Parts);

    /// <summary>
    /// P539b/P556 — the BOT'S default combat loadout at tech level
    /// <paramref name="techLevel"/> (0 = the scenario hull's native preferred
    /// loadout, now with its real <c>Sm Shield 1</c> after P555). The effective
    /// hull is computed first (see <see cref="EffectiveHullAtLevel"/>); then each
    /// entry of the RESULTING hull's native <see cref="DataHullTypeIGC.PreferredPartsTypes"/>
    /// is advanced <paramref name="techLevel"/> steps along its native successor
    /// chain (see <see cref="ResolvePreferredAtLevel"/>; the parts chain follows
    /// <c>successorPartID</c> — <c>src/Igc/partTypeIGC.cpp:35-37</c>, walked by
    /// <c>IstationIGC::GetSuccessor</c> at <c>src/Igc/stationIGC.h:415</c> and
    /// applied on upgrade at <c>src/Igc/shipIGC.h:1036</c>). Each non-launcher
    /// part is included only when the resolved part actually MOUNTS the
    /// effective hull (native CanMount gate — after P555's native-alignment fix,
    /// the scout's `Sm Shield 1` mounts and is included; unmountable preferred
    /// entries, e.g. the flagship's Hyper-Jump with no afterburner socket, are
    /// honestly skipped, exactly native's UpgradeShip). A preferred
    /// LAUNCHER/dispenser (which has no successor chain) is appended via
    /// <see cref="AppendLauncher"/>, so it is included only when it truly fits
    /// the effective hull. Tech 0 == the scenario hull's
    /// <c>PreferredPartsTypes</c>. Never fabricates a part; the result is exactly
    /// what the server's ApplyLoadout can mount on the effective hull.
    /// </summary>
    public static TechLoadout NativePreferredLoadout(short hullId, string hullName, int techLevel = 0)
    {
        var core = RepresentativeCore();
        if (!core.HullTypes.TryGetValue(hullId, out _))
            throw new ArgumentException($"native preferred loadout: hull id {hullId} not in the representative core");
        var eff = EffectiveHullAtLevel(hullId, techLevel);
        var parts = new List<ExpandedPartData>();
        var seen = new HashSet<short>();
        foreach (var pid in eff.PreferredPartsTypes)
        {
            if (pid < 0) break;                    // NA terminator
            if (core.Launchers.ContainsKey(pid))
            {
                // A preferred launcher/dispenser (compact record, no successor
                // chain): mount the base launcher only if it fits the effective
                // hull (AppendLauncher's real CanMountPart gate), else honestly skip.
                AppendLauncher(parts, pid, eff.HullId, eff.Name, "");
                continue;
            }
            var mount = ResolvePreferredAtLevel(core, pid, eff.HullId, techLevel);
            if (mount is null) continue;           // never mounts on this hull at any reached level
            if (seen.Add(mount.Value))
                parts.Add(new ExpandedPartData(mount.Value, 1, 0, 255));
        }
        return new TechLoadout(eff.HullId, eff.Name, eff.HitPoints, parts);
    }

    /// <summary>
    /// P556 — the MOUNTED SHIELD of a resolved tech loadout: part id, display
    /// name, and native <c>MaxStrength</c>/<c>RateRegen</c> (shield capacity &
    /// regen — <c>DataShieldTypeIGC</c>, <c>src/Igc/igc.h:1846</c>;
    /// <c>GetMaxStrength</c>/<c>GetRegeneration</c>, <c>shieldIGC.h:106-117</c>),
    /// or <c>null</c> when the loadout carries none. The first shield part,
    /// matching native's single shield mount.
    /// </summary>
    public static (short PartId, string Name, float Strength, float Regen)? ShieldStats(IReadOnlyList<ExpandedPartData> parts)
    {
        var core = RepresentativeCore();
        foreach (var e in parts)
            if (core.PartTypes.TryGetValue(e.PartId, out var p) && p.Type is DataPartTypeIGC.Shield sh)
                return (p.PartId, p.Name, sh.MaxStrength, sh.RateRegen);
        return null;
    }

    /// <summary>P539b — the display NAME of a real part id (a full part type or a
    /// launcher/dispenser), or <c>null</c> when it is neither in the core. Lets a
    /// showcase operator read the RESOLVED tech-level loadout by name, not id.</summary>
    public static string? PartName(short partId)
    {
        var core = RepresentativeCore();
        return core.PartTypes.TryGetValue(partId, out var p) ? p.Name
            : ResolveLauncherName(partId);
    }

    // ── Launcher/dispenser resolution (P433/P435) ─────────────────────────
    //
    // A real launcher is the compact 24-byte form of OT_partType that lives in
    // core.Launchers (keyed by PartId) — SEPARATE from the 117 core.Parts every
    // prior loadout builder read. Its display name / part mask live on the
    // expendable type it fires (Data<Kind>TypeIGC.LauncherName / LauncherPartMask);
    // its real ammo is the launcher record's Amount. P433 restricted resolution to
    // the 56 GENUINE missile launchers; P435 generalizes it to ALL real kinds that
    // share the Launchers table — the 67 real chaff/mine/probe dispensers resolve
    // too (their expendable resolves in core.ChaffTypes/core.MineTypes/core.ProbeTypes).

    /// <summary>P435 — which real expendable kind a launcher/dispenser fires.</summary>
    public enum LauncherKind { Missile, Mine, Chaff, Probe }

    /// <summary>A resolved launcher/dispenser: its real part id, display name
    /// (the expendable type's launcher name), the real ammo the loadout grants,
    /// and its real kind.</summary>
    public readonly record struct Launcher(short Id, string Name, short Amount, LauncherKind Kind);

    /// <summary>Derive the <see cref="LauncherKind"/> of a built launcher part
    /// from its construction-only subtype (the single mapping, mirrored by the
    /// shop's <c>LauncherKindUtil</c>).</summary>
    private static LauncherKind KindOf(in DataPartTypeIGC part) => part.Type switch
    {
        DataPartTypeIGC.MissileLauncher => LauncherKind.Missile,
        DataPartTypeIGC.MineLauncher => LauncherKind.Mine,
        DataPartTypeIGC.ChaffLauncher => LauncherKind.Chaff,
        DataPartTypeIGC.ProbeLauncher => LauncherKind.Probe,
        _ => throw new InvalidOperationException($"part {part.PartId} is not a launcher/dispenser"),
    };

    /// <summary>The real launcher candidates a launcher query resolves over: every
    /// compact launcher record whose expendable id resolves to a real expendable
    /// type in ONE of <c>core.MissileTypes</c> / <c>core.MineTypes</c> /
    /// <c>core.ChaffTypes</c> / <c>core.ProbeTypes</c>.</summary>
    private static IEnumerable<(short PartId, string Name, short Amount, LauncherKind Kind)> AllLaunchers(StaticCore core)
    {
        foreach (var (partId, l) in core.Launchers)
        {
            if (core.MissileTypes.TryGetValue(l.ExpendabletypeId, out var mt))
            {
                yield return (partId, mt.LauncherName, l.Amount, LauncherKind.Missile);
                continue;
            }
            if (core.MineTypes.TryGetValue(l.ExpendabletypeId, out var mn))
            {
                yield return (partId, mn.LauncherName, l.Amount, LauncherKind.Mine);
                continue;
            }
            if (core.ChaffTypes.TryGetValue(l.ExpendabletypeId, out var cf))
            {
                yield return (partId, cf.LauncherName, l.Amount, LauncherKind.Chaff);
                continue;
            }
            if (core.ProbeTypes.TryGetValue(l.ExpendabletypeId, out var pb))
            {
                yield return (partId, pb.LauncherName, l.Amount, LauncherKind.Probe);
            }
        }
    }

    /// <summary>
    /// P433/P435 — resolve one launcher/dispenser name/substring (exact-over-substring,
    /// the same policy as <see cref="ResolveHull"/>/<see cref="ResolveWeapon"/>)
    /// against the real <c>core.Launchers</c> corpus, across ALL FOUR real
    /// expendable kinds (missile + mine + chaff + probe). A query that resolves to
    /// none of them yields the specific "no launcher matches" error; zero hits or
    /// several distinct names are equally specific errors. Never a silent no-op.
    /// </summary>
    public static Launcher ResolveLauncher(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("spawn_bot: `launcher` must be a non-empty launcher/dispenser name or substring");
        var core = RepresentativeCore();
        var cands = AllLaunchers(core).ToList();
        var all = cands.Where(x => x.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
                                || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (all.Count == 0)
            throw new ArgumentException($"spawn_bot: no launcher matches '{query}' (known launchers: {LauncherSummary(cands)})");
        var exact = all.Where(x => x.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = exact.Count > 0 ? exact : all;
        var distinct = pool.Select(x => x.Name).Distinct().ToList();
        if (distinct.Count > 1)
            throw new ArgumentException(
                $"spawn_bot: launcher '{query}' is ambiguous; it matches several distinct launchers: "
                + string.Join(", ", distinct.Select(n => $"'{n}'"))
                + ". Pass a more specific name.");
        var chosen = pool.OrderBy(x => x.PartId).First();
        return new Launcher(chosen.PartId, chosen.Name, chosen.Amount, chosen.Kind);
    }

    private static string LauncherSummary(IReadOnlyList<(short PartId, string Name, short Amount, LauncherKind Kind)> cands)
    {
        var names = cands.OrderBy(c => c.PartId).Select(c => c.Name).Distinct().ToList();
        if (names.Count > 40) names = names.Take(40).ToList();
        return string.Join(", ", names) + (names.Count >= 40 ? ", …" : "");
    }

    /// <summary>
    /// P433 — the default missile launcher, mirroring <see cref="DefaultWeaponPart"/>:
    /// the real launcher the default combat loadout equips when a caller requests a
    /// launcher without naming one. Chosen deterministically as the lowest-<c>PartId</c>
    /// genuine MISSILE launcher that actually MOUNTS on the default fighter hull
    /// (<see cref="DefaultFighterHull"/>, the same hull <see cref="DefaultParts"/>/
    /// vizfight equip) via the real <c>CanMountPart</c> capability test — so the
    /// default is genuinely fightable with real missiles, never a fabricated or
    /// incompatible pick. (The standing combat default stays a missile launcher;
    /// callers who want a specific mine/chaff/probe dispenser name it explicitly.)
    /// If none mounts (a hull change), falls back to the lowest-<c>PartId</c> missile
    /// launcher, which <see cref="AppendLauncher"/>'s compatibility gate then reports
    /// honestly as gun-only.
    /// </summary>
    public static Launcher DefaultLauncherPart()
    {
        var core = RepresentativeCore();
        var fightHull = core.HullTypes[DefaultFighterHull().Id];
        var missiles = AllLaunchers(core).Where(x => x.Kind == LauncherKind.Missile).OrderBy(x => x.PartId).ToList();
        foreach (var c in missiles)
        {
            if (core.TryBuildMissileLauncher(c.PartId, out var part) && fightHull.CanMountPart(in part))
                return new Launcher(c.PartId, c.Name, c.Amount, LauncherKind.Missile);
        }
        var first = missiles.First();
        return new Launcher(first.PartId, first.Name, first.Amount, LauncherKind.Missile);
    }

    /// <summary>
    /// P432 — the real full playable loadout for a hull/weapon matrix combination:
    /// the given real weapon PLUS the standing afterburner/cloak/shield defaults
    /// (the same non-weapon trio vizfight's P420 full-inventory loadout equips),
    /// so a matrix combat bot carries a complete real fighting loadout around the
    /// weapon under test. Never a fabricated or partial loadout.
    /// </summary>
    public static List<ExpandedPartData> PartsForWeapon(short weaponPartId)
        => PartsForWeapon(weaponPartId, null, 0, "", "", out _);

    /// <summary>
    /// P433 — the real full playable loadout for a hull/weapon matrix combination,
    /// optionally carrying a real missile launcher: the given real weapon PLUS the
    /// standing afterburner/cloak/shield defaults PLUS (when a launcher is
    /// requested and actually mounts on <paramref name="hullId"/>) a real missile
    /// launcher. The launcher is checked against the hull's real capability mask
    /// via <see cref="AppendLauncher"/> (the exact P432 MatrixPlanner test,
    /// reused); when it does not fit the loadout is honestly gun-only (reported in
    /// <paramref name="launcherNote"/>). Never a fabricated or silent loadout.
    /// </summary>
    public static List<ExpandedPartData> PartsForWeapon(
        short weaponPartId,
        short? launcherPartId,
        short hullId,
        string hullName,
        string requestedLauncherName,
        out string? launcherNote)
    {
        var core = RepresentativeCore();
        var parts = new List<ExpandedPartData> { new(weaponPartId, 1, 0, 255) };
        // P483 — request ONLY the real default AB/cloak/shield that actually
        // MOUNT on the requested hull. The pre-fix code appended the first
        // (lowest-PartId) part of each type regardless of hull, so the server's
        // native CanMount gate (shipIGC.h:1030, mirrored at the P334 hull-swap
        // hardpoint policy, Server.ShipCommands.cs:2491) dropped them on any
        // hull lacking the slot — a class-1 "matrix tests loadouts that don't
        // exist" bug, surfacing as a false partial-grant warning in every
        // run_hull_matrix row. Each type is now resolved to the first REAL part
        // that mounts on THIS hull (the DefaultLauncherPart policy — lowest
        // PartId that fits), or honestly omitted when none mounts; the server
        // then mounts exactly what was requested (partialGrants=0).
        if (MountableDefaultPart(core, EquipmentType.Afterburner, hullId) is { } ab) parts.Add(new ExpandedPartData(ab, 1, 0, 255));
        if (MountableDefaultPart(core, EquipmentType.Cloak, hullId) is { } ck) parts.Add(new ExpandedPartData(ck, 1, 0, 255));
        if (MountableDefaultPart(core, EquipmentType.Shield, hullId) is { } sh) parts.Add(new ExpandedPartData(sh, 1, 0, 255));
        launcherNote = AppendLauncher(parts, launcherPartId, hullId, hullName, requestedLauncherName);
        return parts;
    }

    /// <summary>
    /// P433/P435 — append a real, hull-compatible launcher/dispenser to a loadout
    /// being assembled for <paramref name="hullId"/>. When <paramref name="launcherPartId"/>
    /// is null nothing is added and <c>null</c> is returned. Otherwise the launcher
    /// is resolved into its mountable form (ANY of the four real expendable kinds)
    /// and checked against the hull's REAL capability mask via
    /// <c>DataHullTypeIGC.CanMountPart</c> (the exact P432 MatrixPlanner
    /// compatibility test, reused — not reimplemented): if it fits it is appended
    /// (<c>ExpandedPartData</c> partId = launcher, amount = launcher's real ammo,
    /// mount 0, fully mounted) and a positive note returned; if it does NOT fit, the
    /// loadout is left GUN-ONLY and a plain explanatory note returned — never a
    /// crash, never a fabricated launcher. Throws only if the id is not a real
    /// launcher in the core (a resolution error, not a hull mismatch).
    /// </summary>
    public static string? AppendLauncher(
        List<ExpandedPartData> parts,
        short? launcherPartId,
        short hullId,
        string hullName,
        string requestedName)
    {
        if (launcherPartId is not { } lp) return null;
        var core = RepresentativeCore();
        if (!core.TryBuildExpendableLauncher(lp, out var part))
            throw new ArgumentException($"launcher part id {lp} is not a real launcher/dispenser in the representative core");
        if (!core.HullTypes.TryGetValue(hullId, out var hull))
            throw new ArgumentException($"hull id {hullId} ('{hullName}') not found in the representative core");
        if (!hull.CanMountPart(in part))
            return $"launcher '{requestedName}' (part id {lp}) does not mount on hull '{hullName}' (hull id {hullId}) — no compatible slot for a {KindWord(KindOf(part))} — falling back to gun-only loadout";
        var amount = core.Launchers[lp].Amount;
        parts.Add(new ExpandedPartData(lp, amount, 0, 255));
        return $"mounted real {KindWord(KindOf(part))} '{part.Name}' (part id {lp}, {amount} round(s))";
    }

    /// <summary>P435 — lowercase human word for a launcher kind, used in
    /// resolution/append log notes.</summary>
    private static string KindWord(LauncherKind kind) => kind switch
    {
        LauncherKind.Missile => "missile launcher",
        LauncherKind.Mine => "mine dispenser",
        LauncherKind.Chaff => "chaff dispenser",
        LauncherKind.Probe => "probe dispenser",
        _ => "launcher",
    };

    /// <summary>
    /// P433 — the real inter-shot readiness delay (seconds) of the missile a
    /// launcher <c>PartId</c> fires — the launcher's real cooldown from the data
    /// (<c>DataMissileTypeIGC.ReadyTime</c>), floored so even a dumbfire launcher
    /// (ReadyTime 0, instant-lock) is paced like the guns' refire rather than
    /// spammed. Used by the combat engines to decide when to send the real
    /// <c>SendFireMissile()</c>.
    /// </summary>
    public static float LauncherReadyTime(short launcherPartId)
    {
        var core = RepresentativeCore();
        if (core.Launchers.TryGetValue(launcherPartId, out var l)
            && core.MissileTypes.TryGetValue(l.ExpendabletypeId, out var mt))
            return Math.Max(mt.ReadyTime, 0.4f);
        return 0.8f;
    }

    /// <summary>P433/P435 — the display name of a real launcher/dispenser <c>PartId</c>
    /// (whichever expendable kind it fires), or <c>null</c> when it is not a real
    /// launcher in the representative core.</summary>
    public static string? ResolveLauncherName(short launcherPartId)
    {
        var core = RepresentativeCore();
        return AllLaunchers(core).FirstOrDefault(x => x.PartId == launcherPartId).Name is { } n && n.Length > 0 ? n : null;
    }

    /// <summary>Resolve a list of part names to the real <see cref="ExpandedPartData"/>
    /// wire loadout. Each name is validated; any failure throws a real identifying
    /// error before anything touches the wire.</summary>
    public static List<ExpandedPartData> ResolveParts(IEnumerable<string> names)
    {
        var core = RepresentativeCore();
        var seen = new HashSet<short>();
        var parts = new List<ExpandedPartData>();
        foreach (var n in names)
        {
            var p = ResolvePart(n, core);
            if (seen.Add(p.Id))
                parts.Add(new ExpandedPartData(p.Id, 1, 0, 255)); // amount 1, mount 0, fully mounted
        }
        return parts;
    }

    // ── Default loadout (mirror of vizfight's Fight + FullInventoryParts) ──

    /// <summary>The same fightable fighter + weapon pair vizfight selects: the
    /// first hull with energy/ammo + a fixed hardpoint, and the cheapest
    /// dispersion-0 weapon with real projectile power that hull can feed.</summary>
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    return (hull, p);
            }
        }
        throw new InvalidOperationException("no fightable hull+weapon in the representative core");
    });

    private static short? OuterWeaponPartId()
    {
        var core = RepresentativeCore();
        var (hull, weapon) = Fight.Value;
        foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon))
        {
            if (p.Type is DataPartTypeIGC.Weapon w && w.ProjectileTypeId == weapon.ProjectileTypeId
                && w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                return p.PartId;
        }
        return null;
    }

    /// <summary>The real playable loadout vizfight equips (P420 full inventory:
    /// weapon + afterburner + cloak + shield). Used when the caller omits
    /// <c>parts</c> — never a fabricated loadout.</summary>
    public static List<ExpandedPartData> DefaultParts()
    {
        var core = RepresentativeCore();
        var parts = new List<ExpandedPartData>();
        if (OuterWeaponPartId() is { } w) parts.Add(new ExpandedPartData(w, 1, 0, 255));
        if (FirstPart(core, EquipmentType.Afterburner) is { } ab) parts.Add(new ExpandedPartData(ab, 1, 0, 255));
        if (FirstPart(core, EquipmentType.Cloak) is { } ck) parts.Add(new ExpandedPartData(ck, 1, 0, 255));
        if (FirstPart(core, EquipmentType.Shield) is { } sh) parts.Add(new ExpandedPartData(sh, 1, 0, 255));
        return parts;
    }

    private static short? FirstPart(StaticCore core, EquipmentType et) =>
        core.PartTypes.Values.Where(p => p.EquipmentType == et)
            .OrderBy(p => p.PartId).Select(p => p.PartId).Cast<short?>().FirstOrDefault();

    /// <summary>
    /// P483 — the first REAL part of equipment type <paramref name="et"/> that
    /// actually MOUNTS on hull <paramref name="hullId"/> via the real
    /// <see cref="DataHullTypeIGC.CanMountPart"/> containment test — the exact
    /// capability check the server's ApplyLoadout hull-swap gate applies (native
    /// <c>CanMount</c>, <c>shipIGC.h:1030</c>, mirrored at the P334 hardpoint
    /// policy, <c>Server.ShipCommands.cs:2491</c>). Lowest-<c>PartId</c>-first,
    /// the same policy <see cref="DefaultLauncherPart"/> uses (first that fits).
    /// Returns <c>null</c> when no real part of that type mounts on the hull —
    /// the type is then honestly omitted from the loadout (never fabricated),
    /// matching what the server would actually mount. Contrast with
    /// <see cref="FirstPart"/>, which ignores hull capability (correct only for
    /// a no-hull-change/free-mount default, e.g. <see cref="DefaultParts"/>).
    /// </summary>
    private static short? MountableDefaultPart(StaticCore core, EquipmentType et, short hullId)
    {
        if (!core.HullTypes.TryGetValue(hullId, out var hull)) return null;
        foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == et).OrderBy(p => p.PartId))
            if (hull.CanMountPart(in p)) return p.PartId;
        return null;
    }
}
