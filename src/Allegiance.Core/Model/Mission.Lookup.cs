using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

public sealed partial class Mission
{
    /// <summary>Resolve a command's live model by native type and ID.
    /// Igc/missionigc.cpp:3386-3416 deliberately excludes missiles, projectiles
    /// and building effects. Read the mission registries, including models
    /// temporarily outside a cluster, rather than the original map records.</summary>
    public IModelIgc? GetModel(IgcObjectType type, short id) => type switch
    {
        IgcObjectType.Ship => GetShip(id),
        IgcObjectType.Station => GetStation(id),
        IgcObjectType.Warp => GetWarp(id),
        IgcObjectType.Asteroid => GetAsteroid(id),
        IgcObjectType.Treasure => GetTreasure(id),
        IgcObjectType.Probe => GetProbe(id),
        IgcObjectType.Mine => GetMine(id),
        IgcObjectType.Buoy => GetBuoy(id),
        _ => null,
    };
}
