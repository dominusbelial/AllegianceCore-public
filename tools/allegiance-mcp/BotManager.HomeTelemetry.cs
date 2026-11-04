using Allegiance.ClientCore.Session;
using Allegiance.Core.Collision;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    // Event-only diagnostics: no orders, navigation state or sampling timer.
    // Actual selected target matters: server PodTripTelemetry.Home is a
    // diagnostic nearest-home pick and can differ from the bot's destination.
    internal static string HomeNavigationReceipt(ConnectionSession cs, short ship, short target, string edge)
    {
        lock (cs.Gate)
        {
            var world = cs.World;
            var pose = world.Ships.GetValueOrDefault(ship)?.Latest;
            bool known = world.Stations.TryGetValue(target, out var station);
            static string V(Vector3 v) => FormattableString.Invariant($"{v.X:R},{v.Y:R},{v.Z:R}");
            string position = pose is { } p ? V(p.Position) : "unknown";
            string velocity = pose is { } v ? V(v.Velocity) : "unknown";
            string center = station.Position is { Length: 3 } c ? V(new(c[0], c[1], c[2])) : "unknown";
            string forward = pose is { } f ? V(f.Orientation.GetForward()) : "unknown";
            string up = pose is { } u ? V(u.Orientation.GetUp()) : "unknown";
            string rates = pose is { } r ? string.Join(',', r.TurnRates.Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) : "unknown";
            return $"[pod-nav] utc={DateTime.UtcNow:O} cookie={world.Mission?.Cookie} generation={world.MissionGeneration} "
                + $"ship={ship} edge={edge} target={target} type={(known ? station.StationTypeId : -1)} "
                + $"targetSector={(known ? station.ClusterId : -1)} center={center} snapshotTime={pose?.Time} position={position} velocity={velocity}"
                + $" forward={forward} up={up} turnRates={rates}";
        }
    }
}
