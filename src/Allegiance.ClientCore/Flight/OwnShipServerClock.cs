namespace Allegiance.ClientCore.Flight;

/// <summary>
/// Estimates server simulation milliseconds for FM_C_SHIP_UPDATE. Native stamps
/// both clocks through ServerTimeFromClientTime (clintlib/clintlib.cpp:1861-1863,
/// Igc/shipIGC.h:1173-1178); FedSrv/FedSrv.CPP:4897-4903 returns server time in PING.
/// The port's server clock is simulation time, so extrapolation must follow the
/// advertised TimescaleUpdate rate, including between the six-second keep-alives.
/// Each echo reanchors the estimate. Fitting several minutes of old rates can put
/// even a newly received timestamp outside the native ±1.5 second gate
/// (Igc/shipIGC.cpp:3139-3142,3199-3200).
/// </summary>
public sealed class OwnShipServerClock : Allegiance.Core.Sim.IIgcSite
{
    private bool _ready;
    private double _serverMs;
    private double _clientMs;
    private long _localMs;
    private uint _lastSample;
    private double _timeScale = 1;

    public bool Ready => _ready;

    /// <summary>Clear the old server epoch on reconnect, never on mission rotation:
    /// the port preserves SimNow across missions on the same connection.</summary>
    public void Reset()
    {
        _ready = false;
        _serverMs = 0;
        _clientMs = 0;
        _localMs = 0;
        _lastSample = 0;
        _timeScale = 1;
    }

    /// <summary>Integrate the previous rate up to the change before adopting the
    /// new server simulation rate. Call on the main thread with a monotonic clock.</summary>
    public void SetTimeScale(double timeScale, long localMs)
    {
        if (!double.IsFinite(timeScale) || timeScale is < 0 or > 1 || (timeScale > 0 && timeScale < .1)) return;
        if (timeScale == _timeScale) return;
        if (_ready)
        {
            double elapsed = Math.Max(0, localMs - _localMs) * _timeScale;
            _serverMs += elapsed;
            _clientMs += elapsed;
            _localMs = localMs;
        }
        _timeScale = timeScale;
    }

    /// <summary>Observe server sim-ms and the local monotonic receive time of a
    /// PING echo. Duplicate or reordered echoes cannot move the anchor backwards.</summary>
    public void AddSample(uint serverSimMs, long localRecvMs)
    {
        if (_ready && (serverSimMs <= _lastSample || localRecvMs < _localMs)) return;
        _clientMs = _ready ? ClientTimeSeconds(localRecvMs) * 1000 : localRecvMs;
        _serverMs = serverSimMs;
        _localMs = localRecvMs;
        _lastSample = serverSimMs;
        _ready = true;
    }

    /// <summary>Server sim-ms at the given local monotonic instant; zero until a
    /// real PING echo establishes the server epoch. Subtract local epochs before
    /// converting to double so long machine uptimes cannot destabilize a fit.</summary>
    public uint ServerTimeMs(long localMs)
    {
        if (!_ready) return 0;
        double estimate = _serverMs + Math.Max(0, localMs - _localMs) * _timeScale;
        return estimate <= 0 ? 0u : (uint)estimate;
    }

    /// <summary>Local simulation time at a monotonic wall instant. Rate changes
    /// integrate before reanchoring, including pause; no inverse division by rate.</summary>
    public double ClientTimeSeconds(long localMs) => !_ready ? localMs / 1000.0
        : (_clientMs + Math.Max(0, localMs - _localMs) * _timeScale) / 1000.0;

    // Native clintlib/clintlib.h:860-869: add/subtract the same session offset.
    // Inputs are simulation seconds; wire factories perform the ms encoding.
    public double ServerTimeFromClientTime(double timeClient) =>
        !_ready ? timeClient : (timeClient * 1000 - _clientMs + _serverMs) / 1000.0;

    public double ClientTimeFromServerTime(double timeServer) =>
        !_ready ? timeServer : (timeServer * 1000 - _serverMs + _clientMs) / 1000.0;

}
