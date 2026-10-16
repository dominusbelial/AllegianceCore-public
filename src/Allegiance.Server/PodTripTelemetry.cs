using System.Text.Json;
using System.Text.Json.Serialization;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Server;

/// <summary>Sim-thread diagnostics only. Tracks real ejections until a normal
/// dock, rescue, death or removal. A death restart never counts as arrival.</summary>
internal sealed class PodTripTelemetry(Action<string> write)
{
    private readonly Dictionary<Ship, Trip> _active = [];
    private readonly List<Result> _window = [];
    private double _nextWindow;
    internal int ActiveCount => _active.Count;
    internal event Action<Result>? Completed;
    internal sealed record Visit(short Sector, double At);
    internal sealed record Result(short Ship, short? Side, string Outcome, short EjectSector, double EjectAt,
        short? HomeSector, short? Station, Visit[] Route, double? HomeAt, double? RadiusAt, double? BayAt, double? ApproachAt,
        double EndAt, short? EndSector, float[] EndPosition, double Seconds, double OxygenAllowance,
        double WaitedAtBay)
    {
        // Capture the actual runtime geometry inputs, rather than inferring the
        // station model from the side's civ (file maps can retain authored types).
        public short PodHull { get; init; }
        public short PodCapabilities { get; init; }
        public short? StationType { get; init; }
        public string? StationModel { get; init; }
        public float? StationRadius { get; init; }
        public float[]? StationPosition { get; init; }
        public float[]? StationForward { get; init; }
        public float[]? StationUp { get; init; }
        public float[] EndVelocity { get; set; } = [];
        public float[] EndForward { get; set; } = [];
        public float[] EndUp { get; set; } = [];
        public float[] EndTurnRates { get; set; } = [];
    }
    private sealed class Trip(Ship ship, double now, Station? home)
    {
        public readonly double Start = now;
        public readonly short PodHull = ship.HullType.HullId;
        public readonly short PodCapabilities = ship.HullType.HabmCapabilities;
        public readonly double Allowance = ship.LifepodTimeToDie is { } deadline ? deadline - now : 0;
        public readonly short EjectSector = ship.Sector!.Id;
        public readonly Station? Home = home;
        public readonly List<Visit> Route = [new(ship.Sector!.Id, now)];
        public double? HomeAt, RadiusAt, BayAt, ApproachAt;
        public double LastSample = now, Wait;
        public bool NearBay;
    }

    internal void Begin(Ship ship, double now)
    {
        if (ship.Sector is not { } sector || ship.Mission is not { } mission) return;
        // Diagnostic destination only; this never issues an order or grants a dock.
        var distance = new Dictionary<Sector, int> { [sector] = 0 };
        var queue = new Queue<Sector>(); queue.Enqueue(sector);
        while (queue.TryDequeue(out var s))
            foreach (var w in s.Warps)
                if (w.Destination is { } d && distance.TryAdd(d, distance[s] + 1)) queue.Enqueue(d);
        var home = mission.Sectors.Where(distance.ContainsKey)
            .SelectMany(s => s.StationRuntimes.Where(st => !st.Dead && st.SideId == ship.SideId
                && st.Type.HasCapability(DataStationTypeIGC.SabmRestart)).Select(st => (s, st)))
            .OrderBy(x => distance[x.s]).ThenBy(x => (x.st.Center - ship.Position).LengthSquared())
            .Select(x => x.st).FirstOrDefault();
        _active[ship] = new Trip(ship, now, home);
        if (_nextWindow == 0) _nextWindow = now + 60;
        write($"[pod-trip] eject ship={ship.Id} side={ship.SideId} sector={sector.Id} at={now:F2} oxygen={_active[ship].Allowance:F2} home={home?.Data.ClusterId} station={home?.StationId}");
    }

    private static void Observe(Ship ship, Trip t, double now)
    {
        if (ship.Sector is { } sector && t.Route[^1].Sector != sector.Id) t.Route.Add(new(sector.Id, now));
        if (t.NearBay) t.Wait += Math.Max(0, now - t.LastSample);
        t.LastSample = now;
        t.NearBay = false;
        if (t.Home is not { } home || ship.Sector?.Id != home.Data.ClusterId) return;
        t.HomeAt ??= now;
        float radius = home.Radius + ship.HullType.Length / 2;
        float distance2 = (ship.Position - home.Center).LengthSquared();
        // Diagnostic envelope includes the native staging ring just OUTSIDE
        // collision range. This padding never changes a docking/physics gate.
        t.NearBay = distance2 <= (radius + 50) * (radius + 50);
        if (t.NearBay) t.ApproachAt ??= now;
        if (distance2 > radius * radius) return;
        t.RadiusAt ??= now;
        if (ship.Sector.StationLandingBays?.Bay(home.Type) is { } bay && home.InGarage(ship, ship.Position, bay))
            t.BayAt ??= now;
    }

    private static float[] Components(Vector3 v) => [v.X, v.Y, v.Z];

    internal Result? End(Ship ship, double now, string outcome)
    {
        if (!_active.Remove(ship, out var trip)) return null;
        Observe(ship, trip, now);
        var p = ship.Position;
        var result = new Result(ship.Id, ship.SideId, outcome, trip.EjectSector, trip.Start,
            trip.Home?.Data.ClusterId, trip.Home?.StationId, trip.Route.ToArray(), trip.HomeAt, trip.RadiusAt,
            trip.BayAt, trip.ApproachAt, now, ship.Sector?.Id, [p.X, p.Y, p.Z], now - trip.Start, trip.Allowance, trip.Wait)
        {
            PodHull = trip.PodHull, PodCapabilities = trip.PodCapabilities,
            StationType = trip.Home?.Type.StationTypeId, StationModel = trip.Home?.Type.ModelName,
            StationRadius = trip.Home?.Radius,
            StationPosition = trip.Home is { } home ? Components(home.Center) : null,
            StationForward = trip.Home is { } forwardHome ? Components(forwardHome.Orientation.GetForward()) : null,
            StationUp = trip.Home is { } upHome ? Components(upHome.Orientation.GetUp()) : null,
            EndVelocity = Components(ship.Velocity), EndForward = Components(ship.Orientation.GetForward()),
            EndUp = Components(ship.Orientation.GetUp()), EndTurnRates = ship.TurnRates.ToArray(),
        };
        _window.Add(result);
        write("[pod-trip] " + JsonSerializer.Serialize(result, PodTripJsonContext.Default.Result));
        Completed?.Invoke(result);
        return result;
    }

    internal void Sample(double now, IReadOnlyList<Mission>? missions = null)
    {
        foreach (var (ship, trip) in _active.ToArray())
        {
            if (ship.StationId is not null) End(ship, now, "docked");
            else if (ship.Dead || ship.Sector is null || !ship.Sector.Models.Contains(ship)
                || (missions is not null && (ship.Mission is null || !missions.Contains(ship.Mission)))) End(ship, now, "removed");
            else Observe(ship, trip, now);
        }
        if (_nextWindow == 0 || now < _nextWindow) return;
        Flush(now);
        _nextWindow = now + 60;
    }

    internal void Flush(double now)
    {
        static double Quantile(IEnumerable<double> source, double q)
        {
            var values = source.Order().ToArray();
            return values.Length == 0 ? 0 : values[Math.Clamp((int)Math.Ceiling(q * values.Length) - 1, 0, values.Length - 1)];
        }
        write(FormattableString.Invariant($"[pod-trip] window at={now:F2} docked={_window.Count(r => r.Outcome == "docked")} rescued={_window.Count(r => r.Outcome is "rescued" or "station-rescued")} oxygen={_window.Count(r => r.Outcome == "oxygen")} oxygen-home={_window.Count(r => r.Outcome == "oxygen" && r.HomeAt is not null)} other={_window.Count(r => r.Outcome != "oxygen" && r.Outcome != "docked" && r.Outcome != "rescued" && r.Outcome != "station-rescued")} active={_active.Count} median-trip-s={Quantile(_window.Select(r => r.Seconds), .5):F2} p95-trip-s={Quantile(_window.Select(r => r.Seconds), .95):F2} oxygen-allowance-s={Quantile(_window.Select(r => r.OxygenAllowance).Concat(_active.Values.Select(t => t.Allowance)), .5):F2} waited-at-bay-s={Quantile(_window.Select(r => r.WaitedAtBay), .95):F2}"));
        _window.Clear();
    }
}

[JsonSerializable(typeof(PodTripTelemetry.Result))]
internal partial class PodTripJsonContext : JsonSerializerContext;
