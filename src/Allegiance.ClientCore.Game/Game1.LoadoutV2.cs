using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public partial class Game1
{
    private LoadoutV2Renderer? _loadoutV2Renderer;
    private LoadoutV2Model? _loadoutV2Model;
    private TechTreeBitMask? _loadoutV2Techs;
    private byte[]? _loadoutV2Local;
    private short _loadoutV2Source = -1;
    private ShopCatalog? _loadoutV2Catalog;
    // Pure snapshot and geometry also let the real-window proof inspect exactly
    // the rows the shipping renderer has drawn, without replacing any UI.
    private LoadoutV2Presentation? _loadoutV2Frame;
    private LoadoutV2Layout? _loadoutV2Layout;

    private void DrawLoadoutV2(GameTime gameTime, ShopModel shop)
    {
        var world = _session.World;
        var catalog = _shopCatalog ?? shop.Catalog;
        var techs = world.DevelopmentTechsFor(world.MySideId ?? -1) ?? TechTreeBitMask.Empty;
        byte[]? local = null;
        short source = world.MyShipId ?? -1;
        if (world.Ships.TryGetValue(source, out var own))
        {
            if (own.ParentId >= 0) source = own.ParentId;
            if (world.Stations.TryGetValue(own.StationId, out var station)
                && catalog.StationTypes?.TryGetValue(station.StationTypeId, out var stationType) == true)
                local = stationType.TtbmLocal;
        }
        // Match the existing shop's live development + owned-building tech set.
        if (catalog.StationTypes is { } stationTypes)
            foreach (var station in world.Stations.Values.Where(s => s.SideId == world.MySideId))
                if (stationTypes.TryGetValue(station.StationTypeId, out var type))
                    techs = techs.Or(TechTreeBitMask.FromBytes(type.TtbmEffects));
        if (_loadoutV2Model?.Shop != shop || _loadoutV2Techs != techs
            || !ReferenceEquals(_loadoutV2Local, local) || _loadoutV2Source != source
            || _loadoutV2Catalog != shop.Catalog)
        {
            var reachable = catalog.Core is { } core ? BucketCatalog.ReachableTechs(core, techs) : (TechTreeBitMask?)null;
            _loadoutV2Model = new LoadoutV2Model(shop, catalog.Hulls, reachable, local, techs,
                mount =>
                {
                    int currentFixed = world.Ships.TryGetValue(source, out var ship)
                        ? catalog.Hulls.FirstOrDefault(h => h.HullId == ship.HullId).MaxFixedWeapons : 0;
                    int chosenFixed = catalog.Hulls.FirstOrDefault(h => h.HullId == shop.EffectiveHullId).MaxFixedWeapons;
                    return HudInventoryPaneBuilder.SourceShipOccupants(world.Ships, source).Occupant(
                        LoadoutV2Presentation.GunnerMount(mount, currentFixed, chosenFixed));
                });
            _loadoutV2Techs = techs;
            _loadoutV2Local = local;
            _loadoutV2Source = source;
            _loadoutV2Catalog = shop.Catalog;
        }
        var ga = world.GlobalAttributesFor(world.MySideId ?? -1);
        IShipStatLookup lookup = catalog.Core is { } loaded ? new StaticCoreShipStatLookup(loaded) : EmptyShipStatLookup.Instance;
        ShipStatSheet? Sheet(short hullId, IReadOnlyList<ExpandedPartData> parts)
        {
            DataHullTypeIGC? found = null;
            foreach (var candidate in catalog.Hulls)
                if (candidate.HullId == hullId) { found = candidate; break; }
            if (found is not { } hull) return null;
            var mounted = new List<MountedStatPart>();
            foreach (var part in parts)
                if (part.MountId < 0x80 && shop.PartById(part.PartId) is { } type)
                    mounted.Add(new(type, part.MountId));
            return ShipStatSheet.Build(hull, mounted, ga, lookup);
        }
        _loadoutV2Frame = LoadoutV2Presentation.Build(_loadoutV2Model,
            Sheet(shop.CurrentHullId, shop.CurrentParts), Sheet(shop.EffectiveHullId, shop.ChosenParts),
            LoadoutV2InputRouter, LoadoutV2Input.Hint(action => KeyOf(action).Label));
        var viewport = GraphicsDevice.Viewport;
        // P1015: the router's scroll offsets (wheel/focus), clamped by the layout.
        _loadoutV2Layout = _loadoutV2Frame.Layout(viewport.Width, viewport.Height,
            ModernHudScale.Core(_displayScale, _hudSettings.Size, viewport.Height), LoadoutV2InputRouter);

        // The same P519 preview, native orbit and native framing, in the layout's
        // dedicated geo pane (WinTrek/load.cpp:785-790,877-887). Restore the
        // viewport even if model resolution fails, for every later HUD pass.
        var preview = _loadoutV2Layout.ShipPreview;
        try
        {
            GraphicsDevice.Viewport = new Viewport((int)preview.X, (int)preview.Y,
                (int)preview.Width, (int)preview.Height);
            DrawDockedHullPreview(gameTime, shop);
        }
        finally { GraphicsDevice.Viewport = viewport; }
        _loadoutV2Renderer ??= new LoadoutV2Renderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);
        // P1015: hover comes from the gated router (none under a popup/chat).
        _loadoutV2Renderer.Draw(_loadoutV2Frame, _loadoutV2Layout, _hudSettings.Style,
            LoadoutV2InputRouter.HoverIn(_loadoutV2Layout));
    }
}
