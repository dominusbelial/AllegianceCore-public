using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
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
    private readonly Dictionary<short, LoadoutV2Hardpoints> _loadoutV2Hardpoints = new();
    private LoadoutV2HardpointMarker? _loadoutV2Marker;

    private void CaptureLoadoutV2(ShopModel shop)
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
            if (_loadoutV2Catalog?.Core != shop.Catalog.Core) _loadoutV2Hardpoints.Clear();
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
    }

    private void DrawLoadoutV2(GameTime gameTime, ShopModel shop)
    {
        if (_loadoutV2Frame is null || _loadoutV2Model is null) return;
        var viewport = GraphicsDevice.Viewport;
        // P1015: the router's scroll offsets (wheel/focus), clamped by the layout.
        _loadoutV2Layout = _loadoutV2Frame.Layout(viewport.Width, viewport.Height,
            ModernHudScale.Core(_displayScale, _hudSettings.Size, viewport.Height), LoadoutV2InputRouter);

        // The same P519 preview, native orbit and native framing, in the layout's
        // dedicated geo pane (WinTrek/load.cpp:785-790,877-887). Restore the
        // viewport even if model resolution fails, for every later HUD pass.
        var preview = _loadoutV2Layout.ShipPreview;
        LoadoutV2PreviewCamera camera;
        try
        {
            GraphicsDevice.Viewport = new Viewport((int)preview.X, (int)preview.Y,
                (int)preview.Width, (int)preview.Height);
            camera = DrawDockedHullPreview(gameTime, shop);
        }
        finally { GraphicsDevice.Viewport = viewport; }
        _loadoutV2Renderer ??= new LoadoutV2Renderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);
        // P1015: hover comes from the gated router (none under a popup/chat).
        var hover = LoadoutV2InputRouter.HoverIn(_loadoutV2Layout);
        int selectedSlot = LoadoutV2Hardpoints.SelectedSlot(_loadoutV2Frame, hover);
        _loadoutV2Marker = null;
        var slots = _loadoutV2Frame.HardpointSlots;
        if (selectedSlot >= 0 && selectedSlot < slots.Count && _loadoutV2Model.EffectiveHull is { } previewHull)
        {
            if (!_loadoutV2Hardpoints.TryGetValue(previewHull.HullId, out var hardpoints))
            {
                Allegiance.Core.Collision.CvhFile? cvh = null;
                try
                {
                    string path = previewHull.ModelName + ".cvh";
                    if (_assets.Exists(path)) cvh = Allegiance.Core.Collision.CvhReader.Parse(Assets.AssetSources.ReadAllText(_assets, path));
                }
                catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
                {
                    Console.WriteLine($"[loadout-v2] hardpoint frames unavailable for hull {previewHull.HullId}: {e.Message}");
                }
                hardpoints = new LoadoutV2Hardpoints(previewHull, cvh);
                _loadoutV2Hardpoints[previewHull.HullId] = hardpoints;
            }
            _loadoutV2Marker = hardpoints.Project(slots[selectedSlot], camera, preview);
        }
        // P1042 — the loadout chrome is drawn by the active screen skin. Classic/Modern
        // forward the untouched P1014 drawing; Jet re-inks the same presentation through
        // its palette and frames the same layout rectangles (draw == hit).
        ActiveScreen.DrawLoadout(new LoadoutScreenFrame(GraphicsDevice.Viewport,
            NoShopRows, null, null, 0f, 0f, false, null, false,
            _loadoutV2Frame, _loadoutV2Layout, _hudSettings.Style, hover, _loadoutV2Marker,
            LoadoutScreenInks));
    }

    /// <summary>P1042 — the loadout frame's semantic inks for the legacy docked shop (the
    /// V2 composition carries its own per-row inks). Classic/Modern pass the values their
    /// renderer already holds; Jet maps its palette.</summary>
    private static readonly ScreenInks LoadoutScreenInks =
        new(new Color(1.00f, 0.85f, 0.45f), new Color(0.95f, 0.97f, 0.98f),
            new Color(0.45f, 0.48f, 0.52f), new Color(1.00f, 0.45f, 0.42f));

    /// <summary>No legacy rows in a V2 frame (a shared empty array: no per-frame alloc).</summary>
    private static readonly ShopLayout.Row[] NoShopRows = [];
}
