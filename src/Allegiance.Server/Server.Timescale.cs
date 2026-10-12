using System.Globalization;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// P612 — the admin-only sim time scale (<c>sim.timeScale</c>) for cinematic
/// slow motion. Slow motion is achieved by advancing the server's sim clock by
/// <c>period × scale</c> per tick (instead of <c>period</c>) while the wall-clock
/// cadence stays fixed: the whole world — physics, bots, broadcasts — runs at
/// <c>scale</c>× speed, so a battle of any size plays in clean slow motion. Every
/// connected client learns the current scale via
/// <see cref="TimescaleUpdate"/> (server→client) and extrapolates its remote-ship
/// dead-reckoning in scaled sim time, so there is no rubber-banding.
/// <para>
/// Partial class sharing the <see cref="Server"/> type. Admin-only: the value is
/// clamped to [0.1, 1.0] (see <see cref="LiveSettingsConfig.ClampTimeScale"/>)
/// and is only settable by an admin <c>timescale</c> cheat line (LAN/demo-grade).
/// </para>
/// </summary>
public sealed partial class Server
{
    /// <summary>The active sim time scale (1.0 = normal). Read on the sim thread;
    /// only mutated by <see cref="SetTimeScale"/> (also on the sim thread, from the
    /// chat dispatch).</summary>
    private double _timeScale = 1.0;

    /// <summary>Initialize <see cref="_timeScale"/> from config, clamping and
    /// loudly logging. Called once at boot.</summary>
    private void InitTimeScale()
    {
        var cfg = _config.LiveSettings?.TimeScale;
        if (cfg is not { } v)
        {
            Console.WriteLine("[boot] sim.timeScale = 1.0 (default, normal speed)");
            return;
        }
        _timeScale = LiveSettingsConfig.ClampTimeScale(v);
        if (_timeScale != v)
            Console.WriteLine($"[boot] *** sim.timeScale config {v:R} OUT OF RANGE — clamped to {_timeScale:R} ([{LiveSettingsConfig.MinTimeScale:R},{LiveSettingsConfig.MaxTimeScale:R}]) ***");
        Console.WriteLine($"[boot] sim.timeScale = {_timeScale:R} (cinematic slow motion: world runs at {_timeScale:P0} speed)");
    }

    /// <summary>Set the sim time scale (clamped) and notify every connected client.
    /// Used by the admin <c>timescale</c> cheat. Runs on the sim thread.</summary>
    private void SetTimeScale(double value)
    {
        double clamped = LiveSettingsConfig.ClampTimeScale(value);
        _timeScale = clamped;
        BroadcastTimescale();
        Console.WriteLine($"[timescale] sim.timeScale -> {clamped:R}");
    }

    /// <summary>Send the current <see cref="TimescaleUpdate"/> to every connected
    /// peer (including pending side-select peers). Called at boot (during the join
    /// burst) and on every change so clients always converge on the current scale.</summary>
    private void BroadcastTimescale()
    {
        var packet = FedPacket.Pack(new[] { new TimescaleUpdate { Scale = (float)_timeScale }.Encode() });
        foreach (var kv in _peerShips)
            SendPeer(kv.Key, packet);
        foreach (var from in _pendingSideSelect.Keys)
            SendPeer(from, packet);
    }

    /// <summary>Send the current <see cref="TimescaleUpdate"/> to a single peer
    /// (called when a new peer is seated so it learns the current scale).</summary>
    private void SendTimescaleTo(ConnectionId from)
        => SendPeer(from, FedPacket.Pack(new[] { new TimescaleUpdate { Scale = (float)_timeScale }.Encode() }));

    /// <summary>Effective sim-period advance for this tick (period × scale), used by
    /// the sim loop to advance the clock by less than the wall period under slow motion.</summary>
    private double EffectiveSimPeriod => _period * _timeScale;

    /// <summary>
    /// The <c>timescale &lt;value&gt;</c> admin cheat token — a P612 port extension.
    /// Parses a float in [0.1, 1.0] and sets the whole-world sim time scale (clamped,
    /// notifying clients). Replies via <see cref="SendAdminReply"/>; consumed (never
    /// broadcast as chat) like every admin cheat.
    /// </summary>
    private void HandleTimescaleCheat(ConnectionId from, Ship ship, string rest)
    {
        if (!double.TryParse(rest.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            SendAdminReply(from, ship.Id, $"timescale: expected a number in [{LiveSettingsConfig.MinTimeScale:R},{LiveSettingsConfig.MaxTimeScale:R}] — got '{rest.Trim()}'");
            return;
        }
        double clamped = LiveSettingsConfig.ClampTimeScale(v);
        SetTimeScale(clamped);
        SendAdminReply(from, ship.Id, $"sim.timeScale = {clamped:R} (world runs at {clamped:P0} speed)");
    }
}
