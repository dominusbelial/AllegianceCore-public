namespace Allegiance.Core.Sim;

/// <summary>
/// P667 — the intra-sector parallel simulation switch (<c>sim.parallelSectorCells</c>,
/// <c>ALLEGIANCE_SIM_PARALLEL_CELLS</c>). A static holder so the Core sim
/// (<see cref="Allegiance.Core.Model.Sector.Update"/> →
/// <see cref="Allegiance.Core.Collision.CollisionDetector.Detect"/>) can consult
/// it without a <c>Allegiance.Server</c> dependency; it is set once at server boot
/// from <c>ServerConfig.Sim</c> (see <c>Server.Lifecycle.ResolveParallelSectorSim</c>),
/// and set directly by the determinism tests.
/// </summary>
/// <remarks>
/// <para><b>Default OFF.</b> When <see cref="Enabled"/> is false the detector's
/// serial path runs, byte-for-byte identical to before P667. When on, only the
/// collision <b>detection</b> phase of the detector-based sector passes is
/// parallelised (broad-phase pair generation + narrow-phase testing); the
/// ordered <see cref="Allegiance.Core.Collision.CollisionQueue"/> sort is unchanged
/// and the (mutation-heavy, order-sensitive) resolution passes stay serial on the
/// sim thread. See docs/reports/P667_PARALLEL_SECTOR_SIM.md for the determinism
/// argument: detection reads only per-collider state fixed before the phase and
/// <c>TestPair</c> is a pure function, so any thread schedule yields the same
/// candidate set, and the deterministic sort fixes the final event order
/// independently of insertion order — hence byte-identical output for any
/// <see cref="ThreadCount"/> and for flag off vs on.</para>
/// </remarks>
public static class ParallelSectorSim
{
    /// <summary>
    /// Master switch. <c>false</c> (the default) = the existing serial detector
    /// path, byte-for-byte. <c>true</c> = parallel detection, deterministic
    /// merge, identical output.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// Worker degree for the parallel detection phase (1, 2, 4, …). The result
    /// is independent of this value by construction (the deterministic sort fixes
    /// the event order), but it is kept explicit so the determinism gate can pin
    /// 1/2/4/8 and so an operator can bound the sim's core usage. Default: about
    /// half the logical processors (leaving room for the transport / OS), as the
    /// design's bounded-degree guidance suggests. <c>1</c> runs the parallel path
    /// with a single worker — still deterministic and equal to serial.
    /// </summary>
    public static int ThreadCount { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Diagnostic test seam — reset to the default (off) state.</summary>
    public static void Reset()
    {
        Enabled = false;
        ThreadCount = Math.Max(1, Environment.ProcessorCount / 2);
    }
}
