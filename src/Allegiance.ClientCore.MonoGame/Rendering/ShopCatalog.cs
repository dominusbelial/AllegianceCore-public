using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Content;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P268 — the minimal static shop catalogue for the docked/hangar loadout
/// screen. The MonoGame client is otherwise "a renderer with no static core"
/// (see <see cref="MissileModelSource"/>), so the shop cannot list *names and
/// prices* for buyable hulls/parts from the wire — the wire only ever carries
/// ids. This slice makes the catalogue reachable by parsing the same real
/// <c>PCore001.igc</c> the server boots (found under the artwork dir) and
/// exposing its hull + part tables, sorted by id for a stable row order.
///
/// <para>Scope honesty: this is a *display* catalogue only. Native gating of
/// which hulls/parts a particular station sells is per-station tech/bucket
/// availability (the "Invest"/tech-tree territory), which is a named follow-up
/// — every hull and part in the core is offered here. Loading fails cleanly
/// (null) so the rest of the client is never blocked on the artwork core.</para>
/// </summary>
public sealed record ShopCatalog(
    IReadOnlyList<DataHullTypeIGC> Hulls,
    IReadOnlyList<DataPartTypeIGC> Parts,
    IReadOnlyDictionary<short, DataStationTypeIGC>? StationTypes = null,
    IReadOnlyDictionary<short, DataMissileTypeIGC>? MissileTypes = null,
    IReadOnlyDictionary<short, DataMineTypeIGC>? MineTypes = null,
    IReadOnlyDictionary<short, DataChaffTypeIGC>? ChaffTypes = null,
    float LifepodEnduranceSeconds = 300f,
    IReadOnlyList<LauncherOffer>? Launchers = null)
{
    /// <summary>The static core file name the server boots (ServerConfig.StaticCore).</summary>
    public const string CoreFileName = "PCore001.igc";

    /// <summary>An empty catalogue (no hulls, no parts) — used when the real
    /// core cannot be loaded so the docked screen still renders the current
    /// ship and launch/undock remain usable.</summary>
    public static ShopCatalog Empty { get; } = new([], []);

    /// <summary>
    /// Parse the shop catalogue from the real core under <paramref name="artworkDir"/>.
    /// Returns null when the core cannot be located/parsed, so an absent artwork
    /// tree merely disables the shop rather than crashing the client.
    /// </summary>
    public static ShopCatalog? TryLoad(string artworkDir)
    {
        try
        {
            var path = Path.Combine(artworkDir, CoreFileName);
            if (!File.Exists(path)) return null;
            var file = IgcReader.ParseFile(path);
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) return null;
            var core = StaticCore.Load(file);
            var hulls = core.HullTypes.Values.OrderBy(h => h.HullId).ToList();
            var parts = core.PartTypes.Values.OrderBy(p => p.PartId).ToList();
            // P434/P435 — carry the REAL launchers from core.Launchers (the
            // 123-record compact launcher table, SEPARATE from the 117 core.Parts:
            // the launcher form of OT_partType lands here, not in PartTypes).
            // Each is resolved/shaped EXACTLY the way P433 resolves a missile
            // launcher (StaticCore.TryBuildExpendableLauncher ->
            // DataPartTypeIGC.From<Kind>Launcher), the shared bridge the server's
            // ApplyLoadout and both tools use — so a shop-bought launcher is the
            // selfsame mountable part a P433/P435-equipped bot carries. P435
            // extends the offer from the 56 genuine missile launchers to ALL real
            // kinds that share the Launchers table: the 67 real chaff/mine/probe
            // dispensers now also resolve here (their expendable resolves in
            // core.ChaffTypes/core.MineTypes/core.ProbeTypes). The real ammo
            // (DataLauncherTypeIGC.Amount) rides beside the part so the row shows
            // a real ammo count and the toggle grants the real reserve.
            var launchers = new List<LauncherOffer>();
            foreach (var lkv in core.Launchers.OrderBy(kv => kv.Key))
                if (core.TryBuildExpendableLauncher(lkv.Key, out var launcherPart))
                    launchers.Add(new LauncherOffer(launcherPart, lkv.Value.Amount, LauncherKindUtil.KindOf(launcherPart)));
            if (hulls.Count == 0 && parts.Count == 0) return null;
            // P377 — the Lifepod oxygen-endurance seconds from the SAME core
            // constant the server arms pods with (FloatConstants[31]); the
            // client uses it to turn the wire fraction into an mm:ss countdown.
            var fc = core.Constants.FloatConstants;
            float endurance = fc is { Length: > 31 } ? fc[31] : 300f;
            return new ShopCatalog(hulls, parts, core.StationTypes, core.MissileTypes, core.MineTypes, core.ChaffTypes, endurance, launchers);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// P284 — restrict this catalogue to the hulls and parts the connecting
    /// side may buy <em>at the docked station</em>: an item is offered iff its
    /// <c>TtbmRequired ⊆ (stationLocal | sideDev)</c>, the exact containment
    /// P283 wired into <c>Server.StationSellsHull</c> (see
    /// <see cref="TechTreeBitMask.RequiredTechsBuyable"/>) — reused here so the
    /// shop reflects researched tech the same way the server's buy-gate does
    /// (no second bitmask algorithm). <paramref name="stationLocal"/> is the
    /// docked station type's <c>TtbmLocal</c>; <paramref name="sideDev"/> is
    /// the local player's side's live <c>DevelopmentTechs</c> (from
    /// CREATE_BUCKETS / SIDE_TECH_CHANGE on the wire).
    /// <para><b>P468 — fails CLOSED, never open.</b> When either term is
    /// unavailable (unresolvable station, or the side's dev mask not yet
    /// observed — e.g. right after a side switch), this returns ONLY the items
    /// whose tech requirement is all-clear (<c>TtbmRequired</c> empty): the
    /// set that is correct regardless of the unknown mask. It deliberately does
    /// NOT return the full unfiltered catalogue, which made every
    /// undeveloped/unaffordable item look buyable and produced silent server
    /// refusals (P441). The docked screen renders a clear no-tech offer until
    /// the real mask arrives.</para>
    /// </summary>
    public ShopCatalog FilteredFor(byte[]? stationLocal, TechTreeBitMask? sideDev)
    {
        // P468 fail-closed: unknown tech terms → only all-clear items. The
        // union with an empty station mask + empty side mask reduces the P284
        // containment to "need ⊆ ∅", i.e. TtbmRequired all-clear.
        if (stationLocal is null || sideDev is null)
        {
            var none = Array.Empty<byte>();
            var clear = TechTreeBitMask.Empty;
            var noTechHulls = Hulls.Where(h => TechTreeBitMask.RequiredTechsBuyable(h.TtbmRequired, none, clear)).ToList();
            var noTechParts = Parts.Where(p => TechTreeBitMask.RequiredTechsBuyable(p.TtbmRequired, none, clear)).ToList();
            var noTechLaunchers = Launchers
                ?.Where(l => TechTreeBitMask.RequiredTechsBuyable(l.Part.TtbmRequired, none, clear))
                .ToList();
            return new ShopCatalog(noTechHulls, noTechParts, StationTypes, MissileTypes, MineTypes, ChaffTypes, LifepodEnduranceSeconds, noTechLaunchers);
        }
        var dev = sideDev.Value;
        var hulls = Hulls.Where(h => TechTreeBitMask.RequiredTechsBuyable(h.TtbmRequired, stationLocal, dev)).ToList();
        var parts = Parts.Where(p => TechTreeBitMask.RequiredTechsBuyable(p.TtbmRequired, stationLocal, dev)).ToList();
        // P434 — the same P284 containment gate applied to the real missile
        // launchers: a launcher with an unmet tech requirement (its TtbmRequired,
        // from the missile type's LauncherTtbmRequired via FromLauncher) is
        // filtered OUT exactly like any other gated part — never shown
        // unconditionally. Nil requirement (all-clear) stays offered.
        var launchers = Launchers
            ?.Where(l => TechTreeBitMask.RequiredTechsBuyable(l.Part.TtbmRequired, stationLocal, dev))
            .ToList();
        return new ShopCatalog(hulls, parts, StationTypes, MissileTypes, MineTypes, ChaffTypes, LifepodEnduranceSeconds, launchers);
    }

    /// <summary>P285 — whether a hull is a Builder/Constructor-class hull: the
    /// native <c>c_habmBuilder</c> hull ability bit (<c>c_habmBuilder =
    /// (short)0x8000</c>, <c>src/Igc/igc.h:741</c>), read via
    /// <see cref="DataHullTypeIGC.HasCapability"/>. This is the shop's signal
    /// for offering the purchase-time station-type chooser to a player
    /// highlighting/buying a Builder hull (P268/P284 never had one).</summary>
    public static bool IsBuilderHull(DataHullTypeIGC hull) =>
        hull.HasCapability(DataHullTypeIGC.HabmBuilder);

    /// <summary>
    /// P285 — the station types a Builder may be assigned to construct at this
    /// station, offered by the shop's builder-target chooser and tech-gated
    /// exactly like the hull/part offer: a techbase station type shows iff it is
    /// Builder-constructible (a techbase <see cref="DataStationTypeIGC.ClassId"/>
    /// — Garrison/Shipyard/Expansion/Supremacy/Tactical, excluding the utility
    /// Outpost/Teleport/Refinery/Platform) AND its <c>TtbmRequired ⊆
    /// (stationLocal | sideDev)</c>, the exact P283/P284
    /// <see cref="TechTreeBitMask.RequiredTechsBuyable"/> containment reused
    /// (no third bitmask algorithm). When the tech terms are unavailable it
    /// degrades to the techbase set without tech filtering, matching
    /// <see cref="FilteredFor"/>'s degrade-on-unknown behaviour.
    /// </summary>
    public IReadOnlyList<DataStationTypeIGC> BuilderTargetsFor(byte[]? stationLocal, TechTreeBitMask? sideDev)
    {
        var candidates = StationTypes?.Values
            .Where(IsBuilderConstructible)
            .OrderBy(s => s.Name)
            .ToList() ?? new List<DataStationTypeIGC>();
        if (stationLocal is null || sideDev is null) return candidates;
        var dev = sideDev.Value;
        return candidates
            .Where(s => TechTreeBitMask.RequiredTechsBuyable(s.TtbmRequired, stationLocal, dev))
            .ToList();
    }

    /// <summary>P285 — whether a station type is one a Builder actually raises:
    /// a techbase class. Empirically decoded from PCore001 the techbases are
    /// Garrison(<c>ClassId</c> 0), Shipyard(2), Expansion(5), Supremacy(6) and
    /// Tactical(7); the utility Outpost(1)/Teleport(3)/Refinery(4)/Platform(8)
    /// are excluded. This matches the native <c>StationClassID</c> grouping
    /// (<c>igc.h:626-633</c>) and the five techbases the AFS wiki names
    /// Garrison/Supremacy Center/Expansion Complex/Tactical Laboratory/Shipyard
    /// (the port's real decoded <see cref="DataStationTypeIGC.Name"/> values are
    /// used, not the wiki's English aliases).</summary>
    public static bool IsBuilderConstructible(DataStationTypeIGC s) =>
        s.ClassId is 0 or 2 or 5 or 6 or 7;
}

/// <summary>
/// P410 — the <see cref="HullModelResolver"/> overload that resolves a hull id
/// against the <see cref="ShopCatalog"/>'s real hull table (the in-client docked
/// loadout path). Kept here in the MonoGame project because it is the one
/// resolver method that depends on <c>ShopCatalog</c> (a client-only type), so
/// it cannot live in the now-headless-safe <c>Allegiance.Core.Content</c> where
/// <see cref="HullModelResolver"/> itself now resides. Reaches the moved resolver
/// via <c>using Allegiance.Core.Content</c>.
/// </summary>
public static class ShopCatalogHullResolutionExtensions
{
    /// <summary>Resolve a hull id against the real catalogue (the runtime path
    /// Game1 uses). Returns null when the hull isn't in the catalogue or has no
    /// art for the civ → caller falls back.</summary>
    public static string? ResolveAsset(this HullModelResolver resolver, string civKey, short hullId, ShopCatalog catalog)
    {
        if (resolver is null || catalog is null) return null;
        foreach (var h in catalog.Hulls)
            if (h.HullId == hullId)
                return resolver.ResolveAsset(civKey, h);
        return null;
    }
}

/// <summary>
/// P434/P435 — one real launcher shop offer: the mountable part (built from the
/// launcher + its expendable type via <c>DataPartTypeIGC.From&lt;Kind&gt;Launcher</c>,
/// the P433/P435 shared bridge) paired with the launcher's REAL carried ammo
/// (<see cref="DataLauncherTypeIGC.Amount"/>) and its REAL kind. The ammo is not
/// part of the built <see cref="DataPartTypeIGC"/> shape (its field layout has no
/// launcher-ammo slot), so it is carried beside the part so the shop row can show
/// a real ammo count and the toggle can grant the real reserve — the exact ammo a
/// P433/P435-equipped bot's launcher entry carries on the wire.
/// </summary>
public readonly record struct LauncherOffer(DataPartTypeIGC Part, short Ammo, LauncherKind Kind);

/// <summary>
/// P435 — which real expendable kind a launcher/dispenser offer fires. Derived
/// from the built part's construction-only subtype (the <c>From&lt;Kind&gt;Launcher</c>
/// bridge chose it by which expendable table the launcher's id resolves in). Used
/// to label the shop row so a player can tell a [MISSILE] from a [CHAFF]/[MINE]/
/// [PROBE] dispenser.
/// </summary>
public enum LauncherKind { Missile, Mine, Chaff, Probe }

/// <summary>Derive the <see cref="LauncherKind"/> of a built launcher part from
/// its construction-only subtype — the single place the subtype→kind mapping
/// lives, shared by <see cref="ShopCatalog"/> and tests.</summary>
public static class LauncherKindUtil
{
    public static LauncherKind KindOf(in DataPartTypeIGC part) => part.Type switch
    {
        DataPartTypeIGC.MissileLauncher => LauncherKind.Missile,
        DataPartTypeIGC.MineLauncher => LauncherKind.Mine,
        DataPartTypeIGC.ChaffLauncher => LauncherKind.Chaff,
        DataPartTypeIGC.ProbeLauncher => LauncherKind.Probe,
        _ => throw new InvalidOperationException($"part {part.PartId} is not a launcher/dispenser (subtype {part.Type.GetType().Name})"),
    };
}
