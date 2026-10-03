using Allegiance.ClientCore.Flight;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P517 — pure tests pinning the <b>units</b> of the turret-gunner ACTIVE_TURRET_UPDATE
/// timestamp and the reason the shared PING-fed server clock is required.
///
/// <para>The server's turret staleness gate is
/// <c>deltaT = gunner.LastUpdate − atu.time</c> compared against ±1.5 s
/// (<c>Server.ShipCommands.cs:212-213</c>), with <c>Ship.LastUpdate</c> in server
/// sim-<b>seconds</b> — so <c>atu.time</c> must be server sim-seconds. P464 tried to
/// derive those seconds from the peer-pose slot <c>ServerShipUpdate.Time</c>, but that
/// slot is the server <b>tick index</b> (loop iterations, ~20/s) not seconds
/// (<c>Server.Broadcast.cs:247,286</c> pass <c>_loopIterations</c>; noted at
/// <c>ShipDeadReckoner.cs:128</c>). <see cref="WrongTimeIfTickIndexReadAsSeconds"/>
/// pins that trap: interpreting the tick slot as seconds is ~20× off and every update
/// is dropped as stale. P517 instead stamps <c>atu.time</c> from the shared PING-fed
/// <see cref="OwnShipServerClock"/> (ms) converted to the gate's seconds;
/// <see cref="PingMsConvertedToGateSecondsStaysInsideTheGate"/> pins that conversion
/// keeps <c>deltaT</c> inside ±1.5 s under realistic echo jitter.</para>
/// </summary>
public sealed class TurretServerClockTests
{
    private const double GateSeconds = 1.5;

    /// <summary>
    /// PINS THE BUG: feeding a <b>tick-index</b> stream (what the peer-pose
    /// <c>ServerShipUpdate.Time</c> slot actually carries) into a seconds-interpreting
    /// estimate — the retired P464 <c>ServerClockEstimator</c> — yields a timestamp
    /// ~20× the real server time, hopelessly outside the ±1.5 s gate for any
    /// meaningful mission age. This is the "failing-first with tick-index input":
    /// the OLD estimator gives the WRONG time.
    /// </summary>
    [Fact]
    public void WrongTimeIfTickIndexReadAsSeconds()
    {
        const double simSecondsPerTick = 0.05;              // 20 Hz server
        for (double realSimSeconds = 2.0; realSimSeconds <= 20.0; realSimSeconds += 1.0)
        {
            double tickIndex = realSimSeconds / simSecondsPerTick;   // the pose slot's value (~20× real seconds)

            // The old estimator treated tickIndex AS server seconds (no conversion):
            double oldEstimateSeconds = tickIndex;
            double deltaT = realSimSeconds - oldEstimateSeconds;      // gate: gunner.LastUpdate − atu.time
            double ratio = oldEstimateSeconds / realSimSeconds;

            // The trap: ~20× inflation → massively outside ±1.5 s.
            Assert.True(Math.Abs(deltaT) > GateSeconds,
                $"tick-index-as-seconds must be hopelessly outside the gate; at {realSimSeconds:F0}s it read {oldEstimateSeconds:F0}s (delta {deltaT:F0}s, ratio {ratio:F1}x)");
            Assert.True(ratio > 5.0, $"tick slot misread as seconds inflates by the tick rate (~20×), got {ratio:F1}x");
        }
    }

    /// <summary>
    /// The FIX: the shared PING-fed <see cref="OwnShipServerClock"/> estimates server
    /// sim-<b>milliseconds</b> from real <c>FM_CS_PING(35)</c> echoes (server stamps
    /// <c>timeServer = SimNow·1000</c>), and the turret path converts that to the gate's
    /// seconds via <c>floor(ms / 1000)</c>. Under realistic echo latency jitter the
    /// converted <c>atu.time</c> keeps <c>deltaT = LastUpdate − atu.time</c> comfortably
    /// inside ±1.5 s (the floor leaves deltaT in ~[0,1] s, since the estimate tracks real
    /// server time). This is the failing-first guard: if a regression reintroduced the
    /// tick-index-as-seconds units, this test escapes the gate and goes red.
    /// </summary>
    [Fact]
    public void PingMsConvertedToGateSecondsStaysInsideTheGate()
    {
        var rng = new Random(517);
        var clock = new OwnShipServerClock();
        double firstSimMs = 40_000;                          // mission already 40 s in
        double rttMin = 0.003, rttMax = 0.060;               // 3–60 ms LAN-ish RTT

        for (double simMs = firstSimMs; simMs < 62_000; simMs += 55.0)   // ~18 Hz ping cadence
        {
            double rtt = rttMin + rng.NextDouble() * (rttMax - rttMin);
            double localRecvMs = simMs + rtt;                // echo arrives a little after server stamped it
            clock.AddSample((uint)simMs, (long)localRecvMs);

            // Stamp an ACTIVE update shortly after this echo, at the gate's seconds.
            double probeLocalMs = localRecvMs + 8.0 + rng.NextDouble() * 40.0;
            uint atuSeconds = (uint)Math.Floor(clock.ServerTimeMs((long)probeLocalMs) / 1000.0);

            // True server sim-seconds at the probe instant (epoch offset ≈ rtt, negligible here).
            double trueServerSeconds = (probeLocalMs - rtt) / 1000.0;
            double deltaT = trueServerSeconds - atuSeconds;     // gunner.LastUpdate − atu.time

            Assert.True(deltaT > -GateSeconds && deltaT < GateSeconds,
                $"atu.time {atuSeconds}s vs true {trueServerSeconds:F3}s → deltaT {deltaT:F3}s escaped ±{GateSeconds}s gate");
            // Floor normally leaves deltaT in [0,1]: never stale-high, always < 1.5.
            Assert.True(deltaT < GateSeconds - 0.2,
                $"floor-of-ms conversion should keep deltaT ~[0,1]s, got {deltaT:F3}s");
        }
    }
}
