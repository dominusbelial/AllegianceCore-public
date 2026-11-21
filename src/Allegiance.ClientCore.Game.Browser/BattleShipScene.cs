using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.ClientCore.Browser.Assets;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Both HUD styles consume this same visible roster. Geometry readiness
/// never affects membership. No lists or hull searches are created per frame.</summary>
public sealed class BattleShipScene(IReadOnlyDictionary<short, DataHullTypeIGC> hulls)
{
    private readonly Dictionary<short, string> _keys = hulls.Keys.ToDictionary(id => id, id => "hull-" + id);
    private readonly Dictionary<short, (long Revision, bool Ready)> _meshBytes = new();
    public bool MeshBytesReady(short hullId, StagedAssetSource assets)
    {
        if (_meshBytes.TryGetValue(hullId, out var cached) && cached.Revision == assets.ContentRevision) return cached.Ready;
        bool ready = HasMeshBytes(Hull(hullId), assets);
        _meshBytes[hullId] = (assets.ContentRevision, ready);
        return ready;
    }
    public List<ShipEntity> Ships { get; } = new(32);
    public DataHullTypeIGC? Hull(short id) => hulls.TryGetValue(id, out var hull) ? hull : null;
    public void Gather(ClientWorld world, short sector, Action<string>? request)
    {
        Ships.Clear();
        var members = world.ShipsInSector(sector);
        for (int i = 0; i < members.Count; i++)
        {
            var ship = members[i];
            // Native clintlib/appmsg.cpp:475-488; WinTrek/trekigc.cpp:2167-2169.
            if (!ship.CanDrawInSector(sector) || ship.StationId >= 0) continue;
            Ships.Add(ship);
            if (_keys.TryGetValue(ship.HullId, out var key)) request?.Invoke(key);
        }
    }
    public static bool HasMeshBytes(DataHullTypeIGC? hull, StagedAssetSource assets) => hull is { } h &&
        !string.IsNullOrEmpty(h.ModelName) && assets.Contains(h.ModelName + ".mdl");
    // A cube's corner lies at the hull radius (length/2), rather than scaling
    // each axis to a diameter and accidentally growing its bounding sphere.
    public static float StandInSide(float hullLength) => Math.Max(1, hullLength) / MathF.Sqrt(3);

    public static bool RockInView(Vector3 relativePosition, float radius, BoundingFrustum frustum, float farPlane = 10000) =>
        relativePosition.LengthSquared() <= (farPlane + radius) * (farPlane + radius) &&
        frustum.Contains(new BoundingSphere(relativePosition, radius)) != ContainmentType.Disjoint;
}
