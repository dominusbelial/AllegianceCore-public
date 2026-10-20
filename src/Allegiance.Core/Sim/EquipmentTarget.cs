using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>Model views of static command targets. Native magazines accept ImodelIGC,
/// including stations, rocks and warps (magazineigc.cpp:108,127-167).</summary>
public sealed class EquipmentTarget : Model
{
    private readonly Sector _sector;
    private readonly object _body;

    private EquipmentTarget(Sector sector, IgcObjectType type, short id, Vector3 position, object body)
        : base(id, position) { _sector = sector; ObjectType = type; _body = body; }

    public IgcObjectType ObjectType { get; }

    /// <summary>The wrapped body (a <see cref="Station"/>, <see cref="Asteroid"/> or
    /// <see cref="ResolvedWarp"/>) — used by the target-fraction broadcast to read
    /// the native OT_station/OT_asteroid fraction fields without a second lookup.</summary>
    public object Body => _body;

    /// <summary>The sector this equipment target was resolved in — the cluster
    /// that <see cref="IIgcSite.GetCluster"/> should return for a visible warp,
    /// station or asteroid target.</summary>
    public Sector OwnerSector => _sector;

    /// <summary>Collision/visibility radius of the wrapped body. Zero for a
    /// warp, which is handled as always-visible separately.</summary>
    public float Radius => _body switch
    {
        Station st => st.Radius,
        Asteroid a => a.Data.Radius,
        _ => 0f,
    };

    /// <summary>Scanner signature of the wrapped body. Zero for a warp.</summary>
    public float Signature => _body switch
    {
        Station st => st.Type.Signature,
        Asteroid a => a.Data.Signature,
        _ => 0f,
    };

    public static Model? Resolve(Sector? sector, IgcObjectType type, short id)
    {
        if (sector is null) return null;
        if (type == IgcObjectType.Ship) return sector.Models.OfType<Ship>().FirstOrDefault(s => s.Id == id);
        if (type == IgcObjectType.Station && sector.StationRuntimes.FirstOrDefault(s => s.StationId == id) is { } st)
            return new EquipmentTarget(sector, type, id, st.Center, st);
        if (type == IgcObjectType.Warp && sector.Warps.FirstOrDefault(w => w.Warp.WarpId == id) is { } w
            && w.Warp.Position is { Length: 3 } p)
            return new EquipmentTarget(sector, type, id, new(p[0], p[1], p[2]), w);
        if (type == IgcObjectType.Asteroid && sector.Mineable.FirstOrDefault(a => a.Data.AsteroidId == id) is { } a
            && a.Data.Position is { Length: 3 } ap)
            return new EquipmentTarget(sector, type, id, new(ap[0], ap[1], ap[2]), a);
        return sector.Models.FirstOrDefault(m => m.Id == id && TypeOf(m) == type);
    }

    public static IgcObjectType TypeOf(Model? target) => target switch
    {
        Ship => IgcObjectType.Ship, EquipmentTarget t => t.ObjectType,
        Probe => IgcObjectType.Probe, Mine => IgcObjectType.Mine, _ => (IgcObjectType)(-1)
    };

    public static short? SideOf(Model target) => target is EquipmentTarget { _body: Station st } ? st.SideId : BlastDamage.SideOf(target);

    public static bool IsPresent(Model target, Sector sector) => target is EquipmentTarget t
        ? t._sector == sector && (t._body switch
        {
            Station st => sector.StationRuntimes.Contains(st) && !st.Dead,
            ResolvedWarp w => sector.Warps.Contains(w),
            Asteroid a => sector.Mineable.Contains(a) && !a.Dead,
            _ => false
        }) : sector.Models.Contains(target);

    public static bool IsDamageable(Model target) => target switch
    {
        Ship s => !s.Dead, Probe p => !p.Dead, Mine m => m.Fraction > 0,
        EquipmentTarget { _body: Station st } => !st.Dead,
        EquipmentTarget { _body: Asteroid a } => !a.Dead, _ => false
    };

    public static bool CanSee(Model target, short side, Sector sector)
    {
        // Warps are static map objects, known to everyone. Other objects use
        // the same current side visibility as the server's targeting exports.
        if (target is EquipmentTarget { ObjectType: IgcObjectType.Warp }) return true;
        ISideVisible visible = target is EquipmentTarget t && t._body is ISideVisible body ? body : target;
        if (!visible.SideVisibility.HasState(side)) sector.ResolveCrossSideVisibility();
        return visible.SideVisibility.SeenBySide(side);
    }

    protected override void OnUpdate(double now, double dt) { }
}
