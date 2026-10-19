using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    // Shared by one fleet manager. Natural deaths trigger purchases; tech notifications
    // never trigger a fleet-wide buy. The queue also bounds simultaneous dock bursts.
    private readonly LoadoutPurchasePacer _loadoutPurchasePacer = new();
    private readonly Dictionary<short, int> _fighterDispenserSlots = new();
    internal sealed class LoadoutPurchasePacer
    {
        private readonly object _sync = new();
        private double _next = double.NegativeInfinity;
        private double _lastSent = double.NegativeInfinity;
        internal bool TrySend(double now)
        {
            lock (_sync)
            {
                if (now - _lastSent < .05) return false;
                _lastSent = now;
                return true;
            }
        }
        internal double Reserve(double now)
        {
            lock (_sync)
            {
                double slot = Math.Max(now, _next);
                _next = slot + .05; // at most 20 requests/second, including retries
                return slot;
            }
        }
    }

    internal sealed record LoadoutShop(short Civ, short DockType, TechTreeBitMask Techs, long Budget, DataStationTypeIGC? Target = null);

    // Called under the purchasing client's gate, never from the probe's tech snapshot.
    internal static LoadoutShop? CurrentLoadoutShop(ConnectionSession cs, short side, short ship)
    {
        var core = Catalog.RepresentativeCore();
        var w = cs.World;
        if (w.DevelopmentTechsFor(side) is not { } techs || w.MyMoney is not { } money
            || !w.Ships.TryGetValue(ship, out var me) || me.StationId < 0
            || !w.Stations.TryGetValue(me.StationId, out var dock) || dock.SideId != side
            || !core.StationTypes.ContainsKey(dock.StationTypeId)) return null;
        foreach (var station in w.Stations.Values.Where(s => s.SideId == side))
            if (core.StationTypes.TryGetValue(station.StationTypeId, out var type))
                techs = techs.Or(TechTreeBitMask.FromBytes(type.TtbmEffects));
        short civ = w.SideRoster?.Sides.Where(s => s.SideId == side).Select(s => (short?)s.CivId).FirstOrDefault() ?? -1;
        if (!core.Civilizations.ContainsKey(civ)) return null;
        // Conservative: do not spend an estimated refund for the existing ship.
        var target = w.Stations.Values.Where(s => s.SideId >= 0 && s.SideId != side && core.StationTypes.ContainsKey(s.StationTypeId))
            .OrderByDescending(s => core.StationTypes[s.StationTypeId].HasCapability(DataStationTypeIGC.SabmRestart))
            .ThenBy(s => s.StationId).Select(s => (DataStationTypeIGC?)core.StationTypes[s.StationTypeId]).FirstOrDefault();
        return new(civ, dock.StationTypeId, techs, Math.Max(0, money), target);
    }

    internal static TechLoadoutSelector.Selection? PickTechLoadout(LoadoutShop shop, Showcase.HullRole role,
        DataStationTypeIGC? target = null, bool allowRoleFallback = true, bool support = false, bool defensiveMines = false)
    {
        var core = Catalog.RepresentativeCore();
        if (defensiveMines && allowRoleFallback && !support)
        {
            var layer = TechLoadoutSelector.Select(core, shop.Civ, Showcase.HullRole.Scout, shop.Techs,
                shop.DockType, budget: shop.Budget, preferMines: true);
            if (layer is not null && layer.Parts.Any(p => Showcase.IsMineLauncher(core, p.PartId)))
                return layer with { Reason = $"requested role={role}; defensive mine duty (one in three fighter slots); {layer.Reason}" };
        }
        var selected = support ? TechLoadoutSelector.SelectSupport(core, shop.Civ, shop.Techs, shop.DockType, shop.Budget)
            : TechLoadoutSelector.Select(core, shop.Civ, role, shop.Techs, shop.DockType, target ?? shop.Target, shop.Budget);
        if (support && selected is null) selected = TechLoadoutSelector.Select(core, shop.Civ, role, shop.Techs, shop.DockType, budget: shop.Budget);
        if (selected is not null || !allowRoleFallback) return selected;
        // Some roles do not exist until a branch station completes. Keep the
        // requested role on the bot and fly a legal small combat ship meanwhile.
        foreach (var fallback in new[] { Showcase.HullRole.Fighter, Showcase.HullRole.Scout, Showcase.HullRole.Interceptor })
        {
            if (fallback == role) continue;
            // Dedicated scouts retain scanner probes. Combat roles temporarily
            // flying scout hulls carry legal defensive mines when available.
            // The starting interceptor has no dispenser socket; selecting mines
            // only for non-scout hulls left the whole starting fleet without any.
            selected = TechLoadoutSelector.Select(core, shop.Civ, fallback, shop.Techs, shop.DockType,
                budget: shop.Budget, preferMines: role != Showcase.HullRole.Scout);
            if (selected is not null) return selected with { Reason = $"requested role={role} unavailable/over budget; temporary {fallback}; {selected.Reason}" };
        }
        return null;
    }

    private static void ApplyTechLoadout(ActiveBot bot, TechLoadoutSelector.Selection selected)
    {
        bot.HullId = selected.HullId;
        bot.HullName = selected.HullName;
        bot.Parts = selected.Parts;
        bot.WeaponPartId = Catalog.FirstWeaponPartId(selected.Parts);
        var core = Catalog.RepresentativeCore();
        bot.LauncherPartId = selected.Parts.Where(p => core.Launchers.TryGetValue(p.PartId, out var l)
                && core.MissileTypes.ContainsKey(l.ExpendabletypeId)).Select(p => (short?)p.PartId).FirstOrDefault();
        bot.MissileReadyTime = bot.LauncherPartId is { } id ? Catalog.LauncherReadyTime(id) : 0;
        bot.MineDispenserPartId = FindMineDispenser(selected.Parts);
        bot.PendingTechLoadout = selected;
    }

    private static bool RepickRelaunchLoadout(ActiveBot bot, ConnectionSession cs)
    {
        if (!bot.TechLoadouts) return true;
        lock (cs.Gate)
        {
            if (CurrentLoadoutShop(cs, bot.Side, bot.ShipId) is not { } shop) return false;
            TechLoadoutSelector.Selection? selected = null;
            if (bot.LoadoutRefused && bot.PreviousLegalLoadout is { } previous
                && previous.Price <= shop.Budget && TechLoadoutSelector.Legal(Catalog.RepresentativeCore(), shop.Civ,
                    shop.Techs, shop.DockType, previous.HullId, previous.Parts))
                selected = previous with { Reason = $"refusal fallback to previous legal loadout; {previous.Reason}" };
            if (selected is null)
            {
                if (bot.LoadoutRefused && bot.PendingTechLoadout is { } denied)
                    shop = shop with { Budget = Math.Min(shop.Budget, denied.Price - 1) };
                DataStationTypeIGC? target = null;
                if (bot.FinaleBomberActive && bot.Finale is { } run && run.Plans.TryGetValue(bot.Side, out var plan))
                    target = Catalog.RepresentativeCore().StationTypes[plan.TargetTypeId];
                if (!Enum.TryParse<Showcase.HullRole>(bot.Role, true, out var role)) role = Showcase.HullRole.Fighter;
                selected = PickTechLoadout(shop, role, target, allowRoleFallback: !bot.FinaleBomberActive, support: IsSupport(bot),
                    defensiveMines: bot.DefensiveMineDuty);
                if (selected is null && bot.FinaleBomberActive && target is { } station
                    && cs.World.ResearchBriefing?.TechMode == "research")
                {
                    selected = TechLoadoutSelector.SelectAvailableStrike(Catalog.RepresentativeCore(), shop.Civ,
                        shop.Techs, shop.DockType, station, shop.Budget);
                    selected ??= PickTechLoadout(shop, role, target, allowRoleFallback: true) is { } available
                        ? available with { Reason = "research finale: no legal station-damaging loadout; proceeding with best available combat loadout; bounded server finale armed; " + available.Reason }
                        : null;
                }
            }
            if (selected is null)
            {
                string warning = $"[loadout] {bot.Name}: no legal affordable {bot.Role} at dockType={shop.DockType}, techBits={shop.Techs.PopCount}, wallet={shop.Budget}; waiting";
                if (bot.GrantWarning != warning) Console.WriteLine(warning);
                bot.GrantWarning = warning;
                return false;
            }
            ApplyTechLoadout(bot, selected);
            Console.WriteLine($"[loadout] {bot.Name}: dockType={shop.DockType}; {selected.Reason}");
            return true;
        }
    }
}
