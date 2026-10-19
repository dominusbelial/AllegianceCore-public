using Allegiance.Core.Wire;

namespace Allegiance.SinglePlayer;

public enum SkirmishCameraMode { HottestFight, Ship, Station, Free }

/// <summary>Stable subject ordering and a decaying damage/kill heat map, independent of rendering.</summary>
public sealed class SkirmishCamera
{
    public SkirmishCameraMode Mode { get; private set; }
    public short? ShipId { get; private set; }
    public short? StationId { get; private set; }
    public short SectorId { get; private set; } = -1;
    private readonly Dictionary<short, (float Health, short Sector)> _previous = new();
    private readonly Dictionary<short, double> _heat = new();
    private readonly Dictionary<short, double> _recentDamage = new();
    private string _epoch = "";
    private double _lastCut;
    public void CycleMode() { Mode = (SkirmishCameraMode)(((int)Mode + 1) % 4); _lastCut = double.NegativeInfinity; }
    public void SelectMode(SkirmishCameraMode mode) { Mode = mode; _lastCut = double.NegativeInfinity; }

    public void Update(SkirmishObservation frame, int direction = 0)
    {
        if (_epoch != frame.Epoch) { _previous.Clear(); _heat.Clear(); _recentDamage.Clear(); _epoch = frame.Epoch; _lastCut = double.NegativeInfinity; ShipId = StationId = null; SectorId = -1; }
        var poses = frame.Objects.OfType<SingleShipUpdate>().ToDictionary(s => s.OidTarget, s => s.Ship);
        var sectors = frame.Objects.OfType<ShipStatusMessage>().ToDictionary(s => s.ShipId, s => s.SectorId);
        foreach (var (sector, time) in frame.RecentKills) _heat[sector] = Math.Max(_heat.GetValueOrDefault(sector), time + 20);
        foreach (var (id, pose) in poses)
        {
            short sector = sectors[id];
            float health = pose.HullFraction + pose.ShieldFraction;
            if (_previous.TryGetValue(id, out var prior) && health < prior.Health - .001f)
            {
                _heat[sector] = Math.Max(_heat.GetValueOrDefault(sector), frame.Seconds + 10);
                _recentDamage[id] = frame.Seconds;
            }
        }
        _previous.Clear();
        foreach (var (id, pose) in poses) _previous[id] = (pose.HullFraction + pose.ShieldFraction, sectors[id]);
        var ships = poses.Keys.Order().ToArray();
        var stations = frame.Objects.OfType<ExportStation>().Select(s => s.Station).OrderBy(s => s.StationId).ToArray();
        if (Mode == SkirmishCameraMode.Station && stations.Length > 0)
        {
            StationId = Cycle(stations.Select(s => s.StationId).ToArray(), StationId, direction);
            SectorId = stations.First(s => s.StationId == StationId).ClusterId;
            ShipId = ships.Where(id => sectors[id] == SectorId).Select(id => (short?)id).FirstOrDefault()
                ?? ships.Select(id => (short?)id).FirstOrDefault();
            return;
        }
        if (Mode == SkirmishCameraMode.Free && SectorId >= 0) return;
        if (Mode == SkirmishCameraMode.HottestFight && (frame.Seconds - _lastCut >= 5 || ShipId is null || !poses.ContainsKey(ShipId.Value)))
        {
            SectorId = sectors.Values.GroupBy(s => s).OrderByDescending(g => _heat.GetValueOrDefault(g.Key) > frame.Seconds ? _heat[g.Key] : 0)
                .ThenByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault((short)-1);
            ShipId = ships.Where(id => sectors[id] == SectorId).OrderByDescending(id => _recentDamage.GetValueOrDefault(id)).Select(id => (short?)id).FirstOrDefault();
            _lastCut = frame.Seconds;
        }
        else if (Mode == SkirmishCameraMode.Ship || ShipId is null || !poses.ContainsKey(ShipId.Value))
        {
            ShipId = Cycle(ships, ShipId, direction);
            if (ShipId is { } id) SectorId = sectors[id];
        }
        if (SectorId < 0 && stations.Length > 0) SectorId = stations[0].ClusterId;
    }

    public static short? Cycle(short[] ids, short? current, int direction)
    {
        if (ids.Length == 0) return null;
        int index = current is { } id ? Array.IndexOf(ids, id) : -1;
        if (index < 0) return direction < 0 ? ids[^1] : ids[0];
        return ids[(index + direction % ids.Length + ids.Length) % ids.Length];
    }
}
