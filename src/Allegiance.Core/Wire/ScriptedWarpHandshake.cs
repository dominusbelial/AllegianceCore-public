using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// Protocol support for controls-only scripted pilots. After a sector transfer,
/// echo authoritative poses on fresh PING clocks through the ordinary accepted
/// SHIP_UPDATE path (FedSrv/fsship.cpp:1173-1190). No simulation/prediction or
/// server-side readiness shortcut: rejected poses receive SHIP_RESET and retry.
/// Call under the owning session's receive/control lock.
/// </summary>
public sealed class ScriptedWarpHandshake
{
    private short _sector = -1;
    private ServerShipUpdate? _pose;
    private byte _cookie;
    private bool _armed, _commanded, _nativeSender;
    private ControlData _controls;
    private int _state;
    private long _nextPing;
    private uint _lastTime, _anchorTick, _anchorMs, _latestServerTick;
    private double _millisecondsPerTick;
    private bool _clockReady;

    // Native client adopts the sector-assignment cookie (appmsg.cpp:1066).
    public void Assigned(SetCluster assignment)
    {
        bool sameShip = _pose?.ShipId == assignment.Ship.ShipId;
        _armed = sameShip && _sector >= 0
            && assignment.SectorId >= 0 && _sector != assignment.SectorId;
        _sector = assignment.SectorId;
        _pose = assignment.Ship;
        _latestServerTick = assignment.Ship.Time;
        _cookie = assignment.Cookie;
        if (!sameShip)
        {
            _controls = assignment.Ship.Controls;
            _state = assignment.Ship.StateM;
        }
        _commanded = !_nativeSender && assignment.SectorId >= 0;
        _nextPing = 0;
        _lastTime = 0;
    }

    public void ObservedTick(uint tick) => _latestServerTick = tick;

    public void Observed(ServerShipUpdate pose)
    {
        if (_pose?.ShipId == pose.ShipId) _pose = pose;
    }

    // Native own-ship EJECT rotates the cookie (appmsg.cpp:230-232).
    public void Ejected(Eject eject)
    {
        if (_pose?.ShipId == eject.ShipId) _cookie = eject.Cookie;
    }

    // Rejection resets reseed pose and cookie (appmsg.cpp:1179-1187).
    public void Reset(ShipReset reset)
    {
        if (_pose?.ShipId != reset.Ship.ShipId) return;
        _pose = reset.Ship;
        _cookie = reset.Cookie;
    }

    /// <summary>Request PING samples within the existing one-second server reply
    /// limit. Pose echoes use the native 0.15 s update interval
    /// (Inc/Messages.h:226; clintlib/clintlib.cpp:1832). Controls send immediately.</summary>
    public bool Command(ControlData controls, int state, long localMs)
    {
        _nativeSender = false;
        _controls = controls;
        _state = state;
        _commanded = true;
        return NeedsPing(localMs);
    }

    public void Disable() { _nativeSender = true; _commanded = false; }

    public bool NeedsPing(long localMs)
    {
        if (!_armed || !_commanded) return false;
        // Respect the server's existing one-second PING reply limiter (P645).
        // Pose-driven updates below advance in simulation time, so accelerated
        // scripted hosts need no PING flood or wall-clock warp fallback.
        if (localMs < _nextPing) return false;
        _nextPing = localMs + 1000;
        return true;
    }

    // The existing Time=0 controls form identifies a server-flown scripted
    // pilot. Pair it with the fully validated pose so the server retains its
    // existing order/autopilot ownership (no new wire fields or bot policy).
    public static byte[] Pack(ShipUpdate update) => FedPacket.Pack(new[]
    {
        new ShipUpdate { Cookie = update.Cookie,
            Payload = ClientShipUpdate.FromControls(0, update.Payload.StateM, update.Payload.Controls) }.Encode(),
        update.Encode(),
    });

    public ShipUpdate? Clock(uint serverMs)
    {
        if (_pose is null || serverMs == 0) return null;
        uint tick = _latestServerTick;
        if (tick > 0)
        {
            double period = _clockReady && tick > _anchorTick && serverMs > _anchorMs
                ? (serverMs - _anchorMs) / (double)(tick - _anchorTick)
                : serverMs / (double)tick;
            if (double.IsFinite(period) && period > 0 && period < 10000)
            {
                _millisecondsPerTick = period;
                _anchorTick = tick;
                _anchorMs = serverMs;
                _clockReady = true;
            }
        }
        return Update(serverMs);
    }

    /// <summary>Convert the shared HEAVY timeReference ticks to the server-ms epoch
    /// sampled by PING. This only timestamps echoed authoritative poses; it
    /// never predicts position or re-arms a ship. A later PING recalibrates the
    /// tick period, including after changes in the simulation clock rate.</summary>
    public ShipUpdate? PoseUpdate()
    {
        if (!_clockReady || _pose is null || _latestServerTick < _anchorTick) return null;
        double time = _anchorMs + (_latestServerTick - _anchorTick) * _millisecondsPerTick;
        if (time < _lastTime + 150d || time > uint.MaxValue) return null;
        return Update((uint)time);
    }

    private ShipUpdate? Update(uint serverMs)
    {
        if (!_armed || !_commanded || _pose is null || serverMs == 0 || serverMs <= _lastTime) return null;
        _lastTime = serverMs;
        return new ShipUpdate
        {
            Time = serverMs, Cookie = _cookie,
            Payload = ClientShipUpdate.FromPose(serverMs, _pose.Position, _pose.Velocity,
                _pose.Orientation, _state, _controls, _pose.TurnRates, _pose.Power),
        };
    }
}
