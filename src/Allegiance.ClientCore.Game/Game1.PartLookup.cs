using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private Rendering.ShopCatalog? _partCatalogCacheSource;
    private Dictionary<short, DataPartTypeIGC>? _partById;

    /// <summary>Resolve a part type id against the loaded shop catalogue. HUD-4b
    /// caches the Parts + launcher Parts into <see cref="_partById"/> once per
    /// catalogue (rebuilt only when <see cref="_shopCatalog"/> changes), so the
    /// radar treasure loop and the loadout scans do not rescan the whole
    /// catalogue per part per frame.</summary>
    private DataPartTypeIGC? ResolveFlightPart(short id)
    {
        if (_shopCatalog is not { } catalog) return null;
        _partById ??= new Dictionary<short, DataPartTypeIGC>();
        if (!ReferenceEquals(_partCatalogCacheSource, catalog))
        {
            _partById.Clear();
            foreach (var part in catalog.Parts) _partById[part.PartId] = part;
            if (catalog.Launchers is { } launchers)
                foreach (var launcher in launchers) _partById[launcher.Part.PartId] = launcher.Part;
            _partCatalogCacheSource = catalog;
        }
        return _partById.TryGetValue(id, out var partType) ? partType : null;
    }

    /// <summary>
    /// The number of distinct fixed-mount ids the pilot can cycle the weapon
    /// selection among. The wire never transmits the local hull's hardpoint
    /// list, and a stock auto-connect flight never populates the own ship's
    /// loadout (the own-ship EXPORT is the NA(-1) dummy, ClientWorld.cs:215;
    /// BUY_LOADOUT_ACK only reaches a buyer) — so there is no authoritative
    /// count without a server/wire change (out of scope for P225). When a
    /// mounted loadout IS known we use its distinct non-negative mount ids;
    /// otherwise we fall back to the full 2-bit selection range
    /// (SelectedWeaponMask+1 = 4) — the largest set of distinct mount ids the
    /// sim's selection mask can express. Selecting an id that has no weapon
    /// mounted simply fires nothing (Weapon.IsWeaponSelected), which is
    /// harmless; group-fire does not consult the selection at all.
    /// </summary>
    private int FixedMountCount()
    {
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is { } id && _session.World.Ships.TryGetValue(id, out var ship)
                && _shopCatalog is { } catalog)
                foreach (var hull in catalog.Hulls)
                    if (hull.HullId == ship.HullId) return hull.MaxFixedWeapons;
        }
        return 0;
    }

}
